using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.Astc
{
    public enum AstcBlockSize { Block4x4 = 4, Block5x5 = 5, Block6x6 = 6, Block8x8 = 8, Block10x10 = 10, Block12x12 = 12 }
    public enum AstcQuality { Fast = 0, Balanced = 1, HighQuality = 2 }

    /// <summary>Encodes one LDR mip level. Call from the Unity main thread.</summary>
    public sealed class AstcEncoder : IDisposable
    {
        readonly ComputeShader shader;
        readonly int kernel;
        readonly int fastKernel;
        readonly int highQualityKernel;
        readonly int highThreadsX, highThreadsY;
        readonly int threadsX, threadsY, fastThreadsX, fastThreadsY;
        readonly bool adaptive;
        bool disposed;
        readonly int[] dimensions = new int[2];
        ComputeBuffer lookup;
        static readonly int SourceId = Shader.PropertyToID("_Source");
        static readonly int BlocksId = Shader.PropertyToID("_Blocks");
        static readonly int SizeId = Shader.PropertyToID("_Size");
        static readonly int BlockSizeId = Shader.PropertyToID("_BlockSize");
        static readonly int BlocksXId = Shader.PropertyToID("_BlocksX");
        static readonly int TransformId = Shader.PropertyToID("_ColorTransform");
        static readonly int QualityId = Shader.PropertyToID("_Quality");
        static readonly int LookupId = Shader.PropertyToID("_AstcLookup");
        static readonly int RowStartId = Shader.PropertyToID("_BlockRowStart");
        static readonly int RowCountId = Shader.PropertyToID("_BlockRowCount");

        public AstcEncoder(ComputeShader shader)
        {
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            this.shader = shader;
            adaptive = shader.HasKernel("EncodeAstcFast");
            kernel = shader.FindKernel(adaptive ? "EncodeAstcBalanced" : "EncodeAstc");
            fastKernel = shader.HasKernel("EncodeAstcFast") ? shader.FindKernel("EncodeAstcFast") : kernel;
            shader.GetKernelThreadGroupSizes(kernel, out uint x, out uint y, out _);
            threadsX = (int)x; threadsY = (int)y;
            shader.GetKernelThreadGroupSizes(fastKernel, out x, out y, out _);
            fastThreadsX = (int)x; fastThreadsY = (int)y;
            highQualityKernel = shader.HasKernel("EncodeAstcHighQuality") ? shader.FindKernel("EncodeAstcHighQuality") : kernel;
            shader.GetKernelThreadGroupSizes(highQualityKernel, out x, out y, out _);
            highThreadsX = (int)x; highThreadsY = (int)y;
            if (adaptive)
            {
                lookup = new ComputeBuffer(AstcLookup.Data.Length, 4, ComputeBufferType.Structured);
                lookup.SetData(AstcLookup.Data);
            }
        }

        public void Dispose() { lookup?.Dispose(); lookup = null; disposed = true; }

        /// <param name="sampledInputIsSrgb">True when GPU loads return sRGB encoded RGB values. Alpha is always linear.</param>
        public AstcImage Encode(Texture source, AstcBlockSize blockSize = AstcBlockSize.Block4x4,
            bool srgb = false, bool sampledInputIsSrgb = false, AstcQuality quality = AstcQuality.Balanced)
        {
            using (var buffer = Dispatch(source, blockSize, srgb, sampledInputIsSrgb, quality))
            {
                var words = new uint[checked(buffer.count * 4)];
                buffer.GetData(words);
                var bytes = new byte[checked(words.Length * 4)];
                Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
                return new AstcImage(source.width, source.height, blockSize, srgb, bytes);
            }
        }

        /// <summary>Keeps the output buffer alive until readback completes. Keep the source alive until the task completes.</summary>
        public Task<AstcImage> EncodeAsync(Texture source, AstcBlockSize blockSize = AstcBlockSize.Block4x4,
            bool srgb = false, bool sampledInputIsSrgb = false, AstcQuality quality = AstcQuality.Balanced)
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("Async GPU readback is unavailable. Use Encode for a blocking readback.");

            var buffer = Dispatch(source, blockSize, srgb, sampledInputIsSrgb, quality);
            int width = source.width, height = source.height;
            var completion = new TaskCompletionSource<AstcImage>();
            void Complete(AsyncGPUReadbackRequest request)
            {
                AstcImage image = null;
                Exception failure = null;
                try
                {
                    if (request.hasError) throw new InvalidOperationException("ASTC GPU readback failed.");
                    image = new AstcImage(width, height, blockSize, srgb, request.GetData<byte>().ToArray());
                }
                catch (Exception error) { failure = error; }
                finally { buffer.Dispose(); }
                if (failure != null) completion.TrySetException(failure);
                else completion.TrySetResult(image);
            }
            try
            {
#if UNITY_EDITOR
                var readback = AsyncGPUReadback.Request(buffer);
                readback.forcePlayerLoopUpdate = true;
                // In Edit mode, do not depend on the player-loop callback. Poll without waiting on the GPU.
                UnityEditor.EditorApplication.update += UpdateReadback;
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
                void UpdateReadback()
                {
                    readback.Update();
                    if (!readback.done && !readback.hasError)
                    {
                        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                        // Unity 6.7 can defer GPU submission while Edit Mode views are idle.
                        UnityEditor.SceneView.RepaintAll();
                        return;
                    }
                    UnityEditor.EditorApplication.update -= UpdateReadback;
                    Complete(readback);
                }
#else
                AsyncGPUReadback.Request(buffer, Complete);
#endif
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
            return completion.Task;
        }

        ComputeBuffer Dispatch(Texture source, AstcBlockSize blockSize, bool srgb, bool sampledInputIsSrgb, AstcQuality quality)
        {
            int count = RequiredBlockCount(source, blockSize);
            if (disposed) throw new ObjectDisposedException(nameof(AstcEncoder));
            var buffer = new ComputeBuffer(count, 16, ComputeBufferType.Structured);
            try { EncodeGpu(source, buffer, blockSize, srgb, sampledInputIsSrgb, quality); return buffer; }
            catch { buffer.Dispose(); throw; }
        }

        public static int RequiredBlockCount(Texture source, AstcBlockSize blockSize)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            AstcImage.GetTextureFormat(blockSize); // Reject unsupported enum values before allocation.
            if (source.dimension != TextureDimension.Tex2D || source.width < 1 || source.height < 1)
                throw new ArgumentException("Source must be a nonempty 2D texture.", nameof(source));
            if (source is RenderTexture rt && (!rt.IsCreated() || rt.antiAliasing != 1))
                throw new ArgumentException("Source RenderTexture must be created and single sampled.", nameof(source));

            int size = (int)blockSize;
            int blocksX = (source.width + size - 1) / size;
            int blocksY = (source.height + size - 1) / size;
            int count = checked(blocksX * blocksY);
            checked { _ = count * 16; }
            return count;
        }

        /// <summary>Writes ASTC blocks to a caller-owned reusable GPU buffer. Does not allocate output arrays or read back data.</summary>
        public void EncodeGpu(Texture source, ComputeBuffer destination, AstcBlockSize blockSize = AstcBlockSize.Block4x4,
            bool srgb = false, bool sampledInputIsSrgb = false, AstcQuality quality = AstcQuality.Fast, int rowStrideBlocks = 0,
            int firstBlockRow = 0, int blockRowCount = 0)
        {
            if (disposed) throw new ObjectDisposedException(nameof(AstcEncoder));
            RequiredBlockCount(source, blockSize);
            if (quality != AstcQuality.Fast && quality != AstcQuality.Balanced && quality != AstcQuality.HighQuality) throw new ArgumentOutOfRangeException(nameof(quality));
            if (quality == AstcQuality.HighQuality && !shader.HasKernel("EncodeAstcHighQuality"))
                throw new NotSupportedException("This shader does not provide the HighQuality kernel.");
            int size = (int)blockSize;
            int blocksX = (source.width + size - 1) / size;
            int blocksY = (source.height + size - 1) / size;
            if (firstBlockRow < 0 || firstBlockRow >= blocksY) throw new ArgumentOutOfRangeException(nameof(firstBlockRow));
            if (blockRowCount < 0 || blockRowCount > blocksY - firstBlockRow) throw new ArgumentOutOfRangeException(nameof(blockRowCount));
            int rows = blockRowCount == 0 ? blocksY - firstBlockRow : blockRowCount;
            if (!adaptive && (firstBlockRow != 0 || rows != blocksY))
                throw new NotSupportedException("Row ranges require the current ASTC encoder shader.");
            int stride = rowStrideBlocks == 0 ? blocksX : rowStrideBlocks;
            if (stride < blocksX) throw new ArgumentOutOfRangeException(nameof(rowStrideBlocks));
            int count = checked(stride * blocksY);
            if (destination == null || destination.count < count || destination.stride != 16)
                throw new ArgumentException("Destination must contain enough structured elements with stride 16.", nameof(destination));
            int activeKernel = quality == AstcQuality.Fast ? fastKernel : quality == AstcQuality.Balanced ? kernel : highQualityKernel;
            if (!SystemInfo.supportsComputeShaders || !shader.IsSupported(activeKernel))
                throw new NotSupportedException("This device cannot run the ASTC compute kernel.");
            dimensions[0] = source.width; dimensions[1] = source.height;
            shader.SetInts(SizeId, dimensions);
            shader.SetInt(BlockSizeId, size);
            shader.SetInt(BlocksXId, stride);
            shader.SetInt(TransformId, srgb == sampledInputIsSrgb ? 0 : srgb ? 1 : 2);
            shader.SetInt(QualityId, (int)quality);
            if (adaptive) { shader.SetInt(RowStartId, firstBlockRow); shader.SetInt(RowCountId, rows); }
            shader.SetTexture(activeKernel, SourceId, source);
            shader.SetBuffer(activeKernel, BlocksId, destination);
            if (adaptive) shader.SetBuffer(activeKernel, LookupId, lookup);
            int tx = quality == AstcQuality.HighQuality ? highThreadsX : activeKernel == fastKernel ? fastThreadsX : threadsX;
            int ty = quality == AstcQuality.HighQuality ? highThreadsY : activeKernel == fastKernel ? fastThreadsY : threadsY;
            shader.Dispatch(activeKernel, (blocksX + tx - 1) / tx, (rows + ty - 1) / ty, 1);
        }
    }
}
