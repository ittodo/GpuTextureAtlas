using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityCompute.Astc;
using UnityCompute.Bc7;
using GfxFormat = UnityEngine.Experimental.Rendering.GraphicsFormat;

namespace UnityCompute.TextureCompression
{
    public sealed partial class GpuTextureAtlas
    {
        sealed class InputBatch
        {
            internal Texture2D[] textures;
            internal Vector2Int[] sizes, paddedSizes;
            internal GraphicsTexture[] resources;
            internal bool[] needsConversion;
            internal readonly List<int> copied = new List<int>(), converted = new List<int>();
            internal int block, mip, kernel;
            internal ComputeShader encoder, convert;
            internal GpuAtlasQuality quality;
        }

        static InputBatch ReadInputs(IReadOnlyList<Texture2D> textures, GraphicsFormat format, int block, int mip,
            ComputeShader encoder, ComputeShader convert, GpuAtlasQuality quality)
        {
            if (textures == null) throw new ArgumentNullException(nameof(textures));
            if (textures.Count == 0) throw new ArgumentException("At least one input is required.", nameof(textures));
            if (mip < 0) throw new ArgumentOutOfRangeException(nameof(mip));
            if (quality < GpuAtlasQuality.Fast || quality > GpuAtlasQuality.HighQuality) throw new ArgumentOutOfRangeException(nameof(quality));
            int count = textures.Count;
            var batch = new InputBatch { textures = new Texture2D[count], sizes = new Vector2Int[count],
                paddedSizes = new Vector2Int[count], resources = new GraphicsTexture[count], needsConversion = new bool[count],
                block = block, mip = mip, encoder = encoder, convert = convert, quality = quality, kernel = -1 };
            for (int i = 0; i < count; i++)
            {
                var source = textures[i]; if (source == null) throw new ArgumentException($"Input {i} is null.", nameof(textures));
                var resource = source.graphicsTexture;
                if (resource == null) throw new InvalidOperationException($"'{source.name}' has no uploaded GPU resource.");
                var desc = resource.descriptor;
                if (desc.dimension != TextureDimension.Tex2D || desc.numSamples != 1 || mip >= desc.mipCount)
                    throw new ArgumentException($"'{source.name}' does not have the requested uploaded 2D mip.");
                if (!SystemInfo.IsFormatSupported(desc.format, GraphicsFormatUsage.Sample))
                    throw new NotSupportedException($"Cannot sample '{source.name}' as {desc.format}.");
                int w = Math.Max(1, desc.width >> mip), h = Math.Max(1, desc.height >> mip);
                batch.textures[i] = source; batch.resources[i] = resource;
                batch.sizes[i] = new Vector2Int(w, h);
                batch.paddedSizes[i] = new Vector2Int((w + block - 1) / block * block, (h + block - 1) / block * block);
                batch.needsConversion[i] = desc.format != format;
                if (batch.needsConversion[i]) batch.converted.Add(i); else batch.copied.Add(i);
            }
            if (batch.converted.Count != 0)
            {
                if (encoder == null || convert == null)
                    throw new ArgumentException("Mismatched formats or color spaces need the target encoder shader and GpuAtlasConvert.compute.");
                if (!SystemInfo.supportsComputeShaders || !SystemInfo.IsFormatSupported(GfxFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.LoadStore))
                    throw new NotSupportedException("GPU conversion needs compute and a writable RGBA half-float texture.");
                batch.kernel = convert.FindKernel("ConvertAndPad");
            }
            return batch;
        }

        GraphicsTexture Normalize(InputBatch batch, int index, out int readMip)
        {
            readMip = 0; var size = batch.paddedSizes[index];
            if (!batch.needsConversion[index])
            {
                if (size == batch.sizes[index]) { readMip = batch.mip; return batch.resources[index]; }
                if (!paddedCopies.TryGetValue(size, out var padded))
                { padded = NewTexture(size.x, size.y, GraphicsFormat, FilterMode.Point); paddedCopies.Add(size, padded); }
                // Region copies require aligned logical extents. A whole-mip copy preserves the
                // same physical block grid, including partial edge blocks, in the padded texture.
                // Keep this overload: using the region overload with rounded extents exceeds the source bounds.
                Graphics.CopyTexture(batch.resources[index], 0, batch.mip, padded.graphicsTexture, 0, 0);
                CopyCount++;
                return padded.graphicsTexture;
            }
            var item = GetConversion(batch, size);
            ConvertRows(batch, index, item, 0, size.y);
            if (item.bc7 != null) item.bc7.EncodeFrame(item.linear, false, (Bc7Quality)batch.quality);
            else item.astc.EncodeFrame(item.linear, false, (AstcQuality)batch.quality);
            return item.Texture.graphicsTexture;
        }
        Conversion GetConversion(InputBatch batch, Vector2Int size)
        {
            if (!conversions.TryGetValue(size, out var item))
            {
                item = new Conversion(); conversions.Add(size, item);
                item.linear = new RenderTexture(new RenderTextureDescriptor(size.x, size.y)
                { graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                    depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None,
                    enableRandomWrite = true, msaaSamples = 1, volumeDepth = 1, useMipMap = false, autoGenerateMips = false })
                { hideFlags = HideFlags.DontSave };
                if (!item.linear.Create()) throw new NotSupportedException("Cannot create the GPU conversion texture.");
                if (GraphicsFormatUtility.GetTextureFormat(GraphicsFormat) == TextureFormat.BC7)
                    item.bc7 = new Bc7GpuTexture(batch.encoder, size.x, size.y, Srgb);
                else item.astc = new AstcGpuTexture(batch.encoder, size.x, size.y, (AstcBlockSize)batch.block, Srgb);
            }
            return item;
        }
        static void ConvertRows(InputBatch batch, int index, Conversion item, int firstRow, int rowCount)
        {
            var size = batch.paddedSizes[index];
            batch.convert.SetInts("_InputSize", batch.sizes[index].x, batch.sizes[index].y);
            batch.convert.SetInts("_OutputSize", size.x, size.y); batch.convert.SetInt("_SourceMip", batch.mip);
            batch.convert.SetInt("_FirstRow", firstRow); batch.convert.SetInt("_RowCount", rowCount);
            batch.convert.SetTexture(batch.kernel, "_Source", batch.textures[index]); batch.convert.SetTexture(batch.kernel, "_Output", item.linear);
            batch.convert.Dispatch(batch.kernel, (size.x + 7) / 8, (rowCount + 7) / 8, 1);
        }

        /// <summary>Add images without moving existing pixel rectangles. Rebind Texture and refresh UVs after growth.</summary>
        public Task AddAsync(Texture2D texture, ComputeShader encoderShader = null, ComputeShader conversionShader = null,
            GpuAtlasQuality quality = GpuAtlasQuality.Fast, int mipLevel = 0,
            GpuAtlasFrameBudget frameBudget = null, CancellationToken cancellationToken = default)
            => AddAsync(new[] { texture }, encoderShader, conversionShader, quality, mipLevel, frameBudget, cancellationToken);

        /// <summary>Main thread only. One addition at a time. Keep new loader handles alive until completion, including failure.</summary>
        public async Task AddAsync(IReadOnlyList<Texture2D> textures, ComputeShader encoderShader = null, ComputeShader conversionShader = null,
            GpuAtlasQuality quality = GpuAtlasQuality.Fast, int mipLevel = 0,
            GpuAtlasFrameBudget frameBudget = null, CancellationToken cancellationToken = default)
        {
            CheckMutable();
            IsUpdating = true; GpuTextureAtlas work = null;
            try
            {
                var schedule = new FrameWork(this, frameBudget, cancellationToken);
                int block = CompressedAtlasSource.GetBlockSize(GraphicsFormatUtility.GetTextureFormat(GraphicsFormat));
                var batch = ReadInputs(textures, GraphicsFormat, block, mipLevel, encoderShader, conversionShader, quality);
                schedule.Validate(batch);
                var next = BlockAtlasLayout.Append(placement, batch.sizes, block, MaxSize, PaddingBlocks);
                int first = Entries.Count, firstId = NextSourceIndex;
                int nextId = checked(firstId + batch.textures.Length);
                bool grows = next.Width != Width || next.Height != Height;
                var entries = new CompressedAtlasEntry[first + batch.textures.Length];
                for (int i = 0; i < first; i++)
                {
                    var e = Entries[i];
                    entries[i] = grows ? new CompressedAtlasEntry(e.SourceIndex, e.Name, e.PixelRect.width, e.PixelRect.height,
                        CompressedRowOrder.BottomToTop, e.PackedRect, next.Width, next.Height) : e;
                }
                for (int i = 0; i < batch.textures.Length; i++)
                {
                    var r = next.Regions[first + i];
                    entries[first + i] = new CompressedAtlasEntry(firstId + i, batch.textures[i].name, batch.sizes[i].x, batch.sizes[i].y,
                        CompressedRowOrder.BottomToTop, new RectInt(r.x * block, r.y * block, r.width * block, r.height * block), next.Width, next.Height);
                }
                var copied = new List<int>(CopiedSourceIndices); foreach (int i in batch.copied) copied.Add(firstId + i);
                var converted = new List<int>(ConvertedSourceIndices); foreach (int i in batch.converted) converted.Add(firstId + i);
                var publishedEntries = Array.AsReadOnly(entries); var publishedCopied = copied.AsReadOnly(); var publishedConverted = converted.AsReadOnly();
                work = new GpuTextureAtlas { GraphicsFormat = GraphicsFormat, heldSources = new List<Texture2D>(batch.textures) };
                var destination = Texture.graphicsTexture;
                if (grows)
                {
                    work.Texture = NewTexture(next.Width, next.Height, GraphicsFormat, Texture.filterMode);
                    work.Texture.name = Texture.name; destination = work.Texture.graphicsTexture;
                    // Tile valid old blocks into new space; the first tile preserves every old block at its original position.
                    var oldGpu = Texture.graphicsTexture;
                    for (int y = 0; y < next.Height; y += Height) for (int x = 0; x < next.Width; x += Width)
                    {
                        await work.CopyRectAsync(oldGpu, 0, new RectInt(0, 0, Math.Min(Width, next.Width - x), Math.Min(Height, next.Height - y)),
                            destination, x, y, block, schedule);
                    }
                }
                for (int i = 0; i < batch.textures.Length; i++)
                {
                    var source = await work.NormalizeAsync(batch, i, schedule); var r = entries[first + i].PackedRect;
                    await work.CopyRectAsync(source.texture, source.mip, new RectInt(0, 0, r.width, r.height), destination, r.x, r.y, block, schedule);
                }
                work.FenceSubmittedWork(); await work.WaitForCompletionAsync(); schedule.Check();
                if (grows) PublishTexture(work);
                placement = next; Entries = publishedEntries; CopiedSourceIndices = publishedCopied; ConvertedSourceIndices = publishedConverted;
                CopyCount += work.CopyCount; NextSourceIndex = nextId; Version++; NotifyChanged();
            }
            catch
            {
                // Failed in-place copies only touched uncommitted space. The previous published entries remain valid.
                if (work != null) { work.Dispose(); await DrainSubmittedWorkAsync(); }
                throw;
            }
            finally { work?.Dispose(); IsUpdating = false; }
        }

        static async Task DrainSubmittedWorkAsync()
        {
            var drain = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            while (!drain.passed)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) { UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); UnityEditor.SceneView.RepaintAll(); }
#endif
                await Task.Yield();
            }
        }
    }
}
