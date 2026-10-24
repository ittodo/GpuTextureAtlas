using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityCompute.TextureCompression;

namespace UnityCompute.Astc
{
    /// <summary>Experimental mobile GPU buffer-to-ASTC texture path. Call from the main thread in Play mode.</summary>
    public sealed class AstcGpuTexture : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct UploadPacket
        {
            public IntPtr buffer, texture;
            public int width, height, blockSize, rowBlocks, status, recordedEvents;
        }
#if UNITY_IOS && !UNITY_EDITOR
        const string Library = "__Internal";
#else
        const string Library = "AstcGpuUpload";
#endif
        [DllImport(Library)] static extern int AstcGpuUploadInitialize();
        [DllImport(Library)] static extern int AstcGpuUploadAbiVersion();
        [DllImport(Library)] static extern IntPtr AstcGpuUploadEvent();
        [DllImport(Library)] static extern int AstcGpuUploadStatus(IntPtr packet);

        AstcEncoder encoder;
        ComputeBuffer blocks;
        CommandBuffer upload;
        IntPtr packet;
        IntPtr callback;
        readonly int rowBlocks;
        readonly bool srgb;
        bool submitted, disposed;
        FrameSlicedGpuJob sliced;
        AstcQuality jobQuality;
        bool jobInputIsSrgb;
        public int Width { get; }
        public int Height { get; }
        public int TotalBlockRows => (Height + (int)BlockSize - 1) / (int)BlockSize;
        public int CompletedBlockRows => sliced?.CompletedBlockRows ?? 0;
        public int SubmittedBlockRows => sliced?.SubmittedBlockRows ?? 0;
        public bool IsEncoding => !disposed && sliced != null && sliced.IsEncoding;
        public float EncodingProgress => (float)CompletedBlockRows / TotalBlockRows;
        public bool IsReleased { get; private set; }
        public Texture2D Texture { get; private set; }
        public AstcBlockSize BlockSize { get; }

        /// <summary>1 means the native copy was recorded. It does not mean GPU completion. Negative values indicate bridge errors.</summary>
        public int UploadStatus
        {
            get
            {
                if (disposed) throw new ObjectDisposedException(nameof(AstcGpuTexture));
                return AstcGpuUploadStatus(packet);
            }
        }

        public AstcGpuTexture(ComputeShader shader, int width, int height,
            AstcBlockSize blockSize = AstcBlockSize.Block4x4, bool srgb = true)
        {
            if (!Application.isPlaying) throw new NotSupportedException("Use this mobile upload path in a player or Play mode.");
            bool supported = (Application.platform == RuntimePlatform.Android && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan) ||
                             (Application.platform == RuntimePlatform.IPhonePlayer && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal);
            if (!supported || IntPtr.Size != 8)
                throw new NotSupportedException("GPU texture upload requires an Android ARM64 Vulkan player or an iOS ARM64 Metal player.");
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsGraphicsFence)
                throw new NotSupportedException("Compute shaders and CPU graphics fences are required.");
            if (width < 1 || height < 1 || width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(width));
            var format = AstcImage.GetTextureFormat(blockSize);
            if (!SystemInfo.SupportsTextureFormat(format)) throw new NotSupportedException($"ASTC sampling is unavailable: {format}");
            if (Marshal.SizeOf<UploadPacket>() != 40 || AstcGpuUploadAbiVersion() != 1)
                throw new InvalidOperationException("ASTC native plug-in ABI differs.");
            if (AstcGpuUploadInitialize() != 1) throw new NotSupportedException("ASTC native rendering interface is unavailable.");
            Width = width; Height = height; BlockSize = blockSize; this.srgb = srgb;
            int size = (int)blockSize;
            int columns = (width + size - 1) / size, rows = (height + size - 1) / size;
            // Metal buffer copies use 256-byte row alignment. Vulkan accepts the same padding.
            rowBlocks = checked((columns + 15) / 16 * 16);
            try
            {
                encoder = new AstcEncoder(shader);
                blocks = new ComputeBuffer(checked(rowBlocks * rows), 16, ComputeBufferType.Structured);
                // Initialize once with valid transparent void-extent blocks before the first render event.
                var initial = new byte[checked(columns * rows * 16)];
                for (int at = 0; at < initial.Length; at += 16)
                {
                    initial[at] = 0xFC; initial[at + 1] = 0xFD;
                    for (int b = 2; b < 8; b++) initial[at + b] = 0xFF;
                }
                Texture = new AstcImage(width, height, blockSize, srgb, initial).CreateTexture();
                Texture.name = "ASTC GPU output"; Texture.hideFlags = HideFlags.DontSave;
                Texture.wrapMode = TextureWrapMode.Clamp;
                packet = Marshal.AllocHGlobal(40);
                Marshal.StructureToPtr(new UploadPacket
                {
                    buffer = blocks.GetNativeBufferPtr(), texture = Texture.GetNativeTexturePtr(),
                    width = width, height = height, blockSize = size, rowBlocks = rowBlocks
                }, packet, false);
                callback = AstcGpuUploadEvent();
                if (callback == IntPtr.Zero) throw new InvalidOperationException("ASTC rendering callback is unavailable.");
                upload = new CommandBuffer { name = "ASTC buffer to texture" };
                upload.IssuePluginEventAndData(callback, 0, packet);
                sliced = new FrameSlicedGpuJob(width, height, rows, columns, EncodeRegion, Publish);
            }
            catch { Release(); throw; }
        }

        /// <summary>Reuses the output texture and buffers. Queues work on Unity's graphics queue with no CPU readback.</summary>
        public void EncodeFrame(Texture source, bool sampledInputIsSrgb = false, AstcQuality quality = AstcQuality.Fast)
        {
            CheckSource(source);
            if (IsEncoding) throw new InvalidOperationException("Finish or dispose the frame-sliced job before EncodeFrame.");
            // Mark before dispatch so even an exception retires resources behind a GPU fence.
            submitted = true;
            encoder.EncodeGpu(source, blocks, BlockSize, srgb, sampledInputIsSrgb, quality, rowBlocks);
            Graphics.ExecuteCommandBuffer(upload);
        }

        /// <summary>Capture one input. Call EncodeNextFrame from each Update until it returns true.</summary>
        public void BeginEncode(Texture source, bool sampledInputIsSrgb = false,
            AstcQuality quality = AstcQuality.Fast, bool snapshotInput = true)
        {
            CheckSource(source);
            if (quality != AstcQuality.Fast && quality != AstcQuality.Balanced && quality != AstcQuality.HighQuality)
                throw new ArgumentOutOfRangeException(nameof(quality));
            if (IsEncoding) throw new InvalidOperationException("An encoding job is already in progress.");
            submitted = true; // The snapshot copy also needs retirement if a later operation throws.
            sliced.Begin(source, snapshotInput); jobQuality = quality; jobInputIsSrgb = sampledInputIsSrgb;
        }

        /// <summary>Submit at most this many block rows and one batch per Player frame. Never waits on the CPU.</summary>
        public bool EncodeNextFrame(int maxBlockRows = 4)
        {
            if (disposed) throw new ObjectDisposedException(nameof(AstcGpuTexture));
            int status = UploadStatus;
            if (status < 0) throw new InvalidOperationException("ASTC native copy failed: " + status);
            return sliced.Step(maxBlockRows);
        }
        /// <summary>Submit one region with at most maxBlocks. Supports budgets smaller than a block row.</summary>
        public bool EncodeNextFrameBlocks(int maxBlocks)
        {
            if (disposed) throw new ObjectDisposedException(nameof(AstcGpuTexture));
            int status = UploadStatus;
            if (status < 0) throw new InvalidOperationException("ASTC native copy failed: " + status);
            return sliced.StepBlocks(maxBlocks);
        }
        void CheckSource(Texture source)
        {
            if (disposed) throw new ObjectDisposedException(nameof(AstcGpuTexture));
            AstcEncoder.RequiredBlockCount(source, BlockSize);
            if (source == Texture || source.width != Width || source.height != Height)
                throw new ArgumentException("Source must be a different 2D texture with the output dimensions.", nameof(source));
        }
        void EncodeRegion(Texture source, int first, int count, int column, int columns) =>
            encoder.EncodeGpu(source, blocks, BlockSize, srgb, jobInputIsSrgb, jobQuality, rowBlocks, first, count, column, columns);
        void Publish() => Graphics.ExecuteCommandBuffer(upload);

        /// <summary>Keep the object alive until the last consumer has queued its draw. Resource release waits for the GPU.</summary>
        public void Dispose()
        {
            if (disposed) return;
            if (submitted)
            {
                var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                AstcGpuRetirement.Retire(fence, Release);
            }
            else Release();
            disposed = true;
        }

        void Release()
        {
            sliced?.Dispose(); sliced = null;
            if (packet != IntPtr.Zero) { Marshal.FreeHGlobal(packet); packet = IntPtr.Zero; }
            blocks?.Dispose(); blocks = null;
            upload?.Dispose(); upload = null;
            encoder?.Dispose(); encoder = null;
            if (Texture != null) UnityEngine.Object.Destroy(Texture);
            Texture = null;
            IsReleased = true;
        }
    }
}
