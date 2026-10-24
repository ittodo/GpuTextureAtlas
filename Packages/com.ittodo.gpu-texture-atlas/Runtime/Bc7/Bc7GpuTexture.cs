using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityCompute.TextureCompression;

namespace UnityCompute.Bc7
{
    /// <summary>Persistent BC7 output on Unity's D3D12 graphics queue. Use from the main thread.</summary>
    public sealed class Bc7GpuTexture : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct UploadPacket
        {
            public IntPtr buffer, texture;
            public int width, height, rowBlocks, status, recordedEvents, generation;
        }
        const string Library="Bc7GpuUpload";
        [DllImport(Library)] static extern int Bc7GpuUploadInitialize();
        [DllImport(Library)] static extern int Bc7GpuUploadAbiVersion();
        [DllImport(Library)] static extern int Bc7GpuUploadGeneration();
        [DllImport(Library)] static extern int Bc7GpuUploadEventId();
        [DllImport(Library)] static extern IntPtr Bc7GpuUploadEvent();
        [DllImport(Library)] static extern int Bc7GpuUploadStatus(IntPtr packet);
        [DllImport(Library)] static extern int Bc7GpuUploadRecordedEvents(IntPtr packet);
        [DllImport(Library)] static extern void Bc7GpuUploadRetainResources(IntPtr packet);
        [DllImport(Library)] static extern void Bc7GpuUploadReleaseResources(IntPtr packet);

        Bc7Encoder encoder;
        ComputeBuffer blocks;
        CommandBuffer upload;
        IntPtr packet;
        readonly int rowBlocks, generation;
        readonly bool srgb;
        bool submitted, retained, disposed;
        FrameSlicedGpuJob sliced;
        Bc7Quality jobQuality;
        bool jobInputIsSrgb;
        public int TotalBlockRows => (Height + 3) / 4;
        public int CompletedBlockRows => sliced?.CompletedBlockRows ?? 0;
        public int SubmittedBlockRows => sliced?.SubmittedBlockRows ?? 0;
        public bool IsEncoding => !disposed && sliced != null && sliced.IsEncoding;
        public float EncodingProgress => (float)CompletedBlockRows / TotalBlockRows;
        public int Width { get; }
        public int Height { get; }
        public Texture2D Texture { get; private set; }
        public Vector2 UvScale=>new Vector2((float)Width/Texture.width,(float)Height/Texture.height);
        public bool IsReleased { get; private set; }
        /// <summary>1 means commands recorded. It does not mean GPU completion. Negative values indicate bridge errors.</summary>
        public int UploadStatus { get { CheckAlive();return Bc7GpuUploadStatus(packet); } }
        public int RecordedCopies { get { CheckAlive();return Bc7GpuUploadRecordedEvents(packet); } }

        public Bc7GpuTexture(ComputeShader shader,int width,int height,bool srgb=true)
        {
            if((Application.platform!=RuntimePlatform.WindowsEditor&&Application.platform!=RuntimePlatform.WindowsPlayer)||
                IntPtr.Size!=8||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12)
                throw new NotSupportedException("BC7 GPU texture upload requires Windows x64 D3D12.");
            if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsGraphicsFence||!SystemInfo.SupportsTextureFormat(TextureFormat.BC7))
                throw new NotSupportedException("BC7 sampling, compute shaders and CPU graphics fences are required.");
            if(width<1||height<1||width>SystemInfo.maxTextureSize||height>SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(width));
            if(Marshal.SizeOf<UploadPacket>()!=40||Bc7GpuUploadAbiVersion()!=1)
                throw new InvalidOperationException("BC7 native plug-in ABI differs.");
            if(Bc7GpuUploadInitialize()!=1)throw new NotSupportedException("Unity D3D12 v8 rendering interface is unavailable.");
            Width=width;Height=height;this.srgb=srgb;generation=Bc7GpuUploadGeneration();
            int columns=(width+3)/4,rows=(height+3)/4;
            rowBlocks=checked((columns+15)/16*16); // D3D12 texture footprints require 256-byte rows.
            try
            {
                encoder=new Bc7Encoder(shader);
                blocks=new ComputeBuffer(checked(rowBlocks*rows),16,ComputeBufferType.Structured);
                // One initialization upload. Mode 6 with zero endpoints is transparent black.
                var initial=new byte[checked(columns*rows*16)];
                for(int at=0;at<initial.Length;at+=16)initial[at]=0x40;
                Texture=new Bc7Image(width,height,srgb,initial).CreateTexture();
                Texture.name="BC7 GPU output";Texture.wrapMode=TextureWrapMode.Clamp;Texture.hideFlags=HideFlags.DontSave;
                packet=Marshal.AllocHGlobal(40);
                Marshal.StructureToPtr(new UploadPacket
                {
                    buffer=blocks.GetNativeBufferPtr(),texture=Texture.GetNativeTexturePtr(),
                    width=Texture.width,height=Texture.height,rowBlocks=rowBlocks,generation=generation
                },packet,false);
                Bc7GpuUploadRetainResources(packet);retained=true;
                IntPtr callback=Bc7GpuUploadEvent();
                if(callback==IntPtr.Zero)throw new InvalidOperationException("BC7 rendering callback is unavailable.");
                upload=new CommandBuffer{name="BC7 GPU buffer to texture"};
                upload.IssuePluginEventAndData(callback,Bc7GpuUploadEventId(),packet);
                sliced=new FrameSlicedGpuJob(width,height,rows,columns,EncodeRegion,Publish);
            }
            catch{Release();throw;}
        }
        void CheckAlive(){if(disposed)throw new ObjectDisposedException(nameof(Bc7GpuTexture));}
        /// <summary>Queue encoding and copy before consumers. No readback, allocation or Apply per frame.</summary>
        public void EncodeFrame(Texture source,bool sampledInputIsSrgb=false,Bc7Quality quality=Bc7Quality.Fast)
        {
            CheckSource(source);
            if(IsEncoding)throw new InvalidOperationException("Finish or dispose the frame-sliced job before EncodeFrame.");
            // Retire behind a fence even if dispatch or command submission throws.
            submitted=true;
            encoder.EncodeGpu(source,blocks,srgb,sampledInputIsSrgb,quality,rowBlocks);
            Graphics.ExecuteCommandBuffer(upload);
        }
        /// <summary>Capture one input. Call EncodeNextFrame from each Update until it returns true.</summary>
        public void BeginEncode(Texture source,bool sampledInputIsSrgb=false,
            Bc7Quality quality=Bc7Quality.Fast,bool snapshotInput=true)
        {
            CheckSource(source);
            if(quality!=Bc7Quality.Fast&&quality!=Bc7Quality.Balanced&&quality!=Bc7Quality.HighQuality)
                throw new ArgumentOutOfRangeException(nameof(quality));
            if(IsEncoding)throw new InvalidOperationException("An encoding job is already in progress.");
            submitted=true;
            sliced.Begin(source,snapshotInput);jobQuality=quality;jobInputIsSrgb=sampledInputIsSrgb;
        }
        /// <summary>Submit at most this many block rows and one batch per Player frame. Never waits on the CPU.</summary>
        public bool EncodeNextFrame(int maxBlockRows=4)
        {
            CheckAlive();CheckDevice();return sliced.Step(maxBlockRows);
        }
        /// <summary>Submit one region with at most maxBlocks, including a partial block row.</summary>
        public bool EncodeNextFrameBlocks(int maxBlocks)
        {
            CheckAlive();CheckDevice();return sliced.StepBlocks(maxBlocks);
        }
        void CheckDevice()
        {
            if(Bc7GpuUploadGeneration()!=generation)throw new InvalidOperationException("The graphics device changed. Recreate the BC7 output.");
            int status=Bc7GpuUploadStatus(packet);
            if(status<0)throw new InvalidOperationException("BC7 native copy failed: "+status);
        }
        void CheckSource(Texture source)
        {
            CheckAlive();CheckDevice();Bc7Encoder.RequiredBlockCount(source);
            if(source==Texture||source.width!=Width||source.height!=Height)
                throw new ArgumentException("Source must be a different 2D texture with the original output dimensions.",nameof(source));
        }
        void EncodeRegion(Texture source,int first,int count,int column,int columns) =>
            encoder.EncodeGpu(source,blocks,srgb,jobInputIsSrgb,jobQuality,rowBlocks,firstBlockRow:first,blockRowCount:count,firstBlockColumn:column,blockColumnCount:columns);
        void Publish() => Graphics.ExecuteCommandBuffer(upload);
        /// <summary>Call after the last consumer has queued its draw. Resources retire after a GPU fence.</summary>
        public void Dispose()
        {
            if(disposed)return;
            if(submitted)
            {
                var fence=Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation,SynchronisationStageFlags.AllGPUOperations);
                Bc7GpuRetirement.Retire(fence,Release);
            }
            else Release();
            disposed=true;
        }
        void Release()
        {
            sliced?.Dispose();sliced=null;
            if(retained){Bc7GpuUploadReleaseResources(packet);retained=false;}
            if(packet!=IntPtr.Zero){Marshal.FreeHGlobal(packet);packet=IntPtr.Zero;}
            blocks?.Dispose();blocks=null;upload?.Dispose();upload=null;encoder?.Dispose();encoder=null;
            if(Texture!=null)
            {
#if UNITY_EDITOR
                if(!Application.isPlaying)UnityEngine.Object.DestroyImmediate(Texture);else
#endif
                UnityEngine.Object.Destroy(Texture);
            }
            Texture=null;IsReleased=true;
        }
    }
}
