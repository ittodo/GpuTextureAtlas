using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityCompute.TextureCompression
{
    public sealed partial class GpuTextureAtlas
    {
        /// <summary>Look up the current rectangle by stable ID, including after removal, growth or repacking.</summary>
        public bool TryGetEntry(int sourceIndex, out CompressedAtlasEntry entry)
        {
            CheckAlive();
            foreach (var candidate in Entries)
                if (candidate.SourceIndex == sourceIndex) { entry = candidate; return true; }
            entry = null; return false;
        }

        public CompressedAtlasEntry GetEntry(int sourceIndex)
            => TryGetEntry(sourceIndex, out var entry) ? entry : throw new KeyNotFoundException($"Atlas entry {sourceIndex} does not exist.");

        /// <summary>Release an entry's reservation. Remaining rectangles and texture data do not change.</summary>
        public bool Remove(int sourceIndex) => Remove(new[] { sourceIndex }) != 0;

        /// <summary>Remove active IDs once each. Unknown IDs are ignored. No GPU commands or pixel clearing.</summary>
        public int Remove(IReadOnlyList<int> sourceIndices)
        {
            CheckMutable();
            if (sourceIndices == null) throw new ArgumentNullException(nameof(sourceIndices));
            var removed = new HashSet<int>(); foreach (int id in sourceIndices) removed.Add(id);
            var entries = new List<CompressedAtlasEntry>(Entries.Count); var regions = new List<RectInt>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                if (removed.Contains(Entries[i].SourceIndex)) continue;
                entries.Add(Entries[i]); regions.Add(placement.Regions[i]);
            }
            int count = Entries.Count - entries.Count; if (count == 0) return 0;
            var copied = new List<int>(); foreach (int id in CopiedSourceIndices) if (!removed.Contains(id)) copied.Add(id);
            var converted = new List<int>(); foreach (int id in ConvertedSourceIndices) if (!removed.Contains(id)) converted.Add(id);
            var next = new BlockAtlasLayout { Width = placement.Width, Height = placement.Height,
                Columns = placement.Columns, Regions = regions.ToArray() };
            var publishedEntries = entries.AsReadOnly(); var publishedCopied = copied.AsReadOnly(); var publishedConverted = converted.AsReadOnly();
            placement = next; Entries = publishedEntries; CopiedSourceIndices = publishedCopied; ConvertedSourceIndices = publishedConverted;
            Version++; NotifyChanged(); return count;
        }

        /// <summary>Rebuild placement from active entries and copy their compressed blocks to a new texture. No re-encoding.</summary>
        public async Task RepackAsync()
        {
            CheckMutable(); IsUpdating = true; GpuTextureAtlas work = null;
            try
            {
                int block = CompressedAtlasSource.GetBlockSize(GraphicsFormatUtility.GetTextureFormat(GraphicsFormat));
                var sizes = new Vector2Int[Entries.Count];
                for (int i = 0; i < sizes.Length; i++) sizes[i] = Entries[i].PixelRect.size;
                var next = sizes.Length == 0
                    ? new BlockAtlasLayout { Width = block, Height = block, Columns = 1, Regions = Array.Empty<RectInt>() }
                    : BlockAtlasLayout.Build(sizes, block, block, MaxSize, PaddingBlocks, 16);
                var entries = new CompressedAtlasEntry[Entries.Count];
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = Entries[i]; var r = next.Regions[i];
                    entries[i] = new CompressedAtlasEntry(e.SourceIndex, e.Name, e.PixelRect.width, e.PixelRect.height,
                        CompressedRowOrder.BottomToTop, new RectInt(r.x * block, r.y * block, r.width * block, r.height * block), next.Width, next.Height);
                }
                var publishedEntries = Array.AsReadOnly(entries);
                work = new GpuTextureAtlas { GraphicsFormat = GraphicsFormat };
                work.Texture = NewTexture(next.Width, next.Height, GraphicsFormat, Texture.filterMode); work.Texture.name = Texture.name;
                var source = Texture.graphicsTexture; var destination = work.Texture.graphicsTexture;
                // Give every gap a valid encoded block. An empty atlas retains one old block until the next add.
                var seed = new RectInt(0, 0, block, block); long best = long.MaxValue;
                foreach (var e in Entries)
                {
                    var r = e.PackedRect;
                    long copies = ((next.Width + (long)r.width - 1) / r.width) * ((next.Height + (long)r.height - 1) / r.height);
                    if (copies < best) { best = copies; seed = r; }
                }
                for (int y = 0; y < next.Height; y += seed.height) for (int x = 0; x < next.Width; x += seed.width)
                {
                    Graphics.CopyTexture(source, 0, 0, seed.x, seed.y, Math.Min(seed.width, next.Width - x), Math.Min(seed.height, next.Height - y), destination, 0, 0, x, y);
                    work.CopyCount++;
                }
                for (int i = 0; i < entries.Length; i++)
                {
                    var from = Entries[i].PackedRect; var to = entries[i].PackedRect;
                    Graphics.CopyTexture(source, 0, 0, from.x, from.y, from.width, from.height, destination, 0, 0, to.x, to.y); work.CopyCount++;
                }
                work.fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                work.hasFence = true; await work.WaitForCompletionAsync(); CheckAlive();
                PublishTexture(work); placement = next; Entries = publishedEntries; CopyCount += work.CopyCount; Version++; NotifyChanged();
            }
            catch
            {
                if (work != null) { work.Dispose(); await DrainSubmittedWorkAsync(); }
                throw;
            }
            finally { work?.Dispose(); IsUpdating = false; }
        }

        void CheckMutable()
        {
            CheckAlive();
            if (IsUpdating) throw new InvalidOperationException("An atlas update is already in progress. Await it before adding, removing or repacking.");
            if (!IsReady) throw new InvalidOperationException("Wait for the initial atlas to finish before editing it.");
        }
        void PublishTexture(GpuTextureAtlas work)
        {
            var old = Texture;
            // Include drawing commands submitted while the update was pending.
            var lastConsumer = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            GpuAtlasRetirement.Retire(lastConsumer, () => DestroyTexture(old));
            Texture = work.Texture; work.Texture = null;
        }
    }
}
