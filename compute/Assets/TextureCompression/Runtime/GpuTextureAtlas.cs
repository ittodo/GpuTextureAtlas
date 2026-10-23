using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityCompute.Astc;
using UnityCompute.Bc7;
using GfxFormat = UnityEngine.Experimental.Rendering.GraphicsFormat;

namespace UnityCompute.TextureCompression
{
    public enum GpuAtlasQuality { Fast, Balanced, HighQuality }

    /// <summary>Copy matching compressed textures; convert only mismatches on the GPU. Main thread only.</summary>
    public sealed partial class GpuTextureAtlas : IDisposable
    {
        public Texture2D Texture { get; private set; }
        public GraphicsFormat GraphicsFormat { get; private set; }
        public bool Srgb => GraphicsFormatUtility.IsSRGBFormat(GraphicsFormat);
        public int Width => Texture != null ? Texture.width : 0;
        public int Height => Texture != null ? Texture.height : 0;
        public int SourceMipLevel { get; private set; }
        public int CopyCount { get; private set; }
        public int MaxSize { get; private set; }
        public int PaddingBlocks { get; private set; }
        public int Version { get; private set; }
        /// <summary>The next stable entry ID. Removed IDs are never reused.</summary>
        public int NextSourceIndex { get; private set; }
        public bool IsUpdating { get; private set; }
        public bool IsDisposed => disposed;
        internal event Action<GpuTextureAtlas> Changed;
        public IReadOnlyList<CompressedAtlasEntry> Entries { get; private set; }
        public IReadOnlyList<int> CopiedSourceIndices { get; private set; }
        public IReadOnlyList<int> ConvertedSourceIndices { get; private set; }
        GraphicsFence fence;
        bool hasFence, disposed, completed;
        Exception failure;
        List<Texture2D> heldSources;
        BlockAtlasLayout placement;
        readonly Dictionary<Vector2Int, Conversion> conversions = new Dictionary<Vector2Int, Conversion>();
        readonly Dictionary<Vector2Int, Texture2D> paddedCopies = new Dictionary<Vector2Int, Texture2D>();

        sealed class Conversion : IDisposable
        {
            internal RenderTexture linear;
            internal Bc7GpuTexture bc7;
            internal AstcGpuTexture astc;
            internal Texture2D Texture => bc7 != null ? bc7.Texture : astc.Texture;
            internal int Status => bc7 != null ? bc7.UploadStatus : astc.UploadStatus;
            public void Dispose()
            {
                bc7?.Dispose(); astc?.Dispose();
                if (linear != null) { linear.Release(); DestroyTexture(linear); linear = null; }
            }
        }

        /// <summary>Poll without waiting or readback. Keep loader handles alive until true.</summary>
        public bool IsReady
        {
            get
            {
                CheckAlive();
                if (!completed && hasFence && !fence.passed) return false;
                if (!completed)
                {
                    try
                    {
                        foreach (var item in conversions.Values) if (item.Status != 1) throw new InvalidOperationException($"GPU encoding upload failed: {item.Status}.");
                    }
                    catch (Exception error) { failure = error; }
                    finally { ReleaseTemporaryResources(); completed = true; heldSources = null; }
                }
                if (failure != null) throw new InvalidOperationException("GPU atlas operation failed.", failure);
                return true;
            }
        }

        public static GpuTextureAtlas PackTextures(IReadOnlyList<Texture2D> textures, TextureFormat format, bool srgb,
            ComputeShader encoderShader = null, ComputeShader conversionShader = null, GpuAtlasQuality quality = GpuAtlasQuality.Fast,
            int maxSize = 4096, int paddingBlocks = 0, int mipLevel = 0, FilterMode filterMode = FilterMode.Point)
        {
            CompressedAtlasSource.GetBlockSize(format);
            return PackTextures(textures, GraphicsFormatUtility.GetGraphicsFormat(format, srgb), encoderShader,
                conversionShader, quality, maxSize, paddingBlocks, mipLevel, filterMode);
        }

        /// <summary>All inputs enter one target atlas. mipLevel is relative to each currently uploaded GPU resource.</summary>
        public static GpuTextureAtlas PackTextures(IReadOnlyList<Texture2D> textures, GraphicsFormat format,
            ComputeShader encoderShader = null, ComputeShader conversionShader = null, GpuAtlasQuality quality = GpuAtlasQuality.Fast,
            int maxSize = 4096, int paddingBlocks = 0, int mipLevel = 0, FilterMode filterMode = FilterMode.Point)
        {
            if (textures == null) throw new ArgumentNullException(nameof(textures));
            if (textures.Count == 0) throw new ArgumentException("At least one input is required.", nameof(textures));
            if (mipLevel < 0) throw new ArgumentOutOfRangeException(nameof(mipLevel));
            if (maxSize < 1 || maxSize > 16384) throw new ArgumentOutOfRangeException(nameof(maxSize));
            if (quality < GpuAtlasQuality.Fast || quality > GpuAtlasQuality.HighQuality) throw new ArgumentOutOfRangeException(nameof(quality));
            var target = GraphicsFormatUtility.GetTextureFormat(format); int block = CompressedAtlasSource.GetBlockSize(target);
            if (GraphicsFormatUtility.GetGraphicsFormat(target, GraphicsFormatUtility.IsSRGBFormat(format)) != format)
                throw new NotSupportedException("Choose an exact LDR ASTC or BC7 target graphics format.");
            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0 || !SystemInfo.supportsGraphicsFence ||
                !SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample))
                throw new NotSupportedException($"GPU copies, CPU-pollable fences and {format} sampling are required.");
            var batch = ReadInputs(textures, format, block, mipLevel, encoderShader, conversionShader, quality);
            var sizes = batch.sizes; var paddedSizes = batch.paddedSizes;
            int limit = Math.Min(maxSize, SystemInfo.maxTextureSize);
            var layout = BlockAtlasLayout.Build(sizes, block, block, limit, paddingBlocks, 16);
            var result = new GpuTextureAtlas { GraphicsFormat = format, SourceMipLevel = mipLevel,
                CopiedSourceIndices = batch.copied.AsReadOnly(), ConvertedSourceIndices = batch.converted.AsReadOnly(),
                heldSources = new List<Texture2D>(batch.textures), placement = layout, MaxSize = limit, PaddingBlocks = paddingBlocks,
                Version = 1, NextSourceIndex = batch.textures.Length };
            try
            {
                result.Texture = NewTexture(layout.Width, layout.Height, format, filterMode);
                result.Texture.name = $"{format} GPU atlas";
                var destination = result.Texture.graphicsTexture;
                var entries = new CompressedAtlasEntry[batch.textures.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    var r = layout.Regions[i];
                    entries[i] = new CompressedAtlasEntry(i, batch.textures[i].name, sizes[i].x, sizes[i].y,
                        CompressedRowOrder.BottomToTop, new RectInt(r.x * block, r.y * block, r.width * block, r.height * block), layout.Width, layout.Height);
                }
                result.Entries = Array.AsReadOnly(entries);
                // Fill gaps with valid source blocks on the GPU. This does not create transparent borders.
                int seed = 0; long best = long.MaxValue;
                for (int i = 0; i < sizes.Length; i++)
                {
                    long tiles = ((layout.Width + paddedSizes[i].x - 1L) / paddedSizes[i].x) * ((layout.Height + paddedSizes[i].y - 1L) / paddedSizes[i].y);
                    if (tiles < best) { best = tiles; seed = i; }
                }
                var seedGpu = result.Normalize(batch, seed, out int seedMip);
                for (int y = 0; y < layout.Height; y += paddedSizes[seed].y) for (int x = 0; x < layout.Width; x += paddedSizes[seed].x)
                {
                    Graphics.CopyTexture(seedGpu, 0, seedMip, 0, 0, Math.Min(paddedSizes[seed].x, layout.Width - x),
                        Math.Min(paddedSizes[seed].y, layout.Height - y), destination, 0, 0, x, y); result.CopyCount++;
                }
                void CopyRegion(int index, GraphicsTexture normalized, int readMip)
                {
                    var r = entries[index].PackedRect;
                    Graphics.CopyTexture(normalized, 0, readMip, 0, 0, r.width, r.height, destination, 0, 0, r.x, r.y); result.CopyCount++;
                }
                CopyRegion(seed, seedGpu, seedMip);
                for (int i = 0; i < batch.textures.Length; i++) if (i != seed)
                { var normalized = result.Normalize(batch, i, out int readMip); CopyRegion(i, normalized, readMip); }
                result.fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                result.hasFence = true; return result;
            }
            catch { result.Dispose(); throw; }
        }

        static Texture2D NewTexture(int width, int height, GraphicsFormat format, FilterMode filter)
        {
            var texture = new Texture2D(width, height, format, 1, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
            try
            {
                // Discard initial CPU storage before copies. Never Apply after GPU data has been written.
                texture.Apply(false, true); texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = filter;
                if (texture.graphicsFormat != format || texture.isReadable) throw new NotSupportedException("Unity substituted the requested atlas format.");
                return texture;
            }
            catch { DestroyTexture(texture); throw; }
        }
        public async Task WaitForCompletionAsync()
        {
            while (!IsReady)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) { UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); UnityEditor.SceneView.RepaintAll(); }
#endif
                await Task.Yield();
            }
        }
        /// <summary>Ready result. On failure, drain queued GPU work before the caller can release loader handles.</summary>
        public static async Task<GpuTextureAtlas> PackTexturesAsync(IReadOnlyList<Texture2D> textures, TextureFormat format, bool srgb,
            ComputeShader encoderShader = null, ComputeShader conversionShader = null, GpuAtlasQuality quality = GpuAtlasQuality.Fast,
            int maxSize = 4096, int paddingBlocks = 0, int mipLevel = 0, FilterMode filterMode = FilterMode.Point)
        {
            GpuTextureAtlas atlas = null;
            try
            {
                atlas = PackTextures(textures, format, srgb, encoderShader, conversionShader, quality, maxSize, paddingBlocks, mipLevel, filterMode);
                await atlas.WaitForCompletionAsync(); return atlas;
            }
            catch
            {
                atlas?.Dispose();
                if (SystemInfo.supportsGraphicsFence) await DrainSubmittedWorkAsync();
                throw;
            }
        }
        void CheckAlive() { if (disposed) throw new ObjectDisposedException(nameof(GpuTextureAtlas)); }
        void NotifyChanged()
        {
            if (Changed == null) return;
            // Rendering listeners cannot turn an already committed atlas update into a failed task.
            foreach (Action<GpuTextureAtlas> listener in Changed.GetInvocationList())
                try { listener(this); } catch (Exception error) { Debug.LogException(error); }
        }
        void ReleaseTemporaryResources()
        {
            foreach (var item in conversions.Values) item.Dispose(); conversions.Clear();
            foreach (var item in paddedCopies.Values) DestroyTexture(item); paddedCopies.Clear();
        }
        static void DestroyTexture(UnityEngine.Object texture)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(texture); else
#endif
                UnityEngine.Object.Destroy(texture);
        }
        /// <summary>Submit the last consumer's drawing first. Native packets and scratch resources retire behind a fence.</summary>
        public void Dispose()
        {
            if (disposed) return; disposed = true; var texture = Texture; Texture = null;
            NotifyChanged(); Changed = null;
            var retirementFence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            GpuAtlasRetirement.Retire(retirementFence, () => { ReleaseTemporaryResources(); heldSources = null; if (texture != null) DestroyTexture(texture); });
        }
    }
}
