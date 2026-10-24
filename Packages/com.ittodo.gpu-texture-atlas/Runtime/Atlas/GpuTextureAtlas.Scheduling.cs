using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityCompute.Astc;
using UnityCompute.Bc7;

namespace UnityCompute.TextureCompression
{
    public sealed partial class GpuTextureAtlas
    {
        sealed class FrameWork
        {
            internal readonly GpuAtlasFrameBudget budget;
            readonly GpuTextureAtlas owner;
            readonly CancellationToken cancellation;
            internal FrameWork(GpuTextureAtlas owner, GpuAtlasFrameBudget budget, CancellationToken cancellation)
            { this.owner = owner; this.budget = budget; this.cancellation = cancellation; Check(); budget?.CheckPlayer(); }
            internal void Check() { owner.CheckAlive(); cancellation.ThrowIfCancellationRequested(); }
            internal void Validate(InputBatch batch)
            {
                if (budget == null) return;
                foreach (int i in batch.converted)
                {
                    var size = batch.paddedSizes[i];
                    if (size.x > budget.ConversionPixelsPerFrame)
                        throw new ArgumentException("The conversion budget must fit at least one pixel row.");
                }
            }
            internal async Task NextFrame()
            {
                int frame = Time.frameCount;
                do { await Task.Yield(); Check(); } while (frame == Time.frameCount);
            }
        }
        readonly struct Normalized
        {
            internal readonly GraphicsTexture texture;
            internal readonly int mip;
            internal Normalized(GraphicsTexture texture, int mip) { this.texture = texture; this.mip = mip; }
        }
        async Task<Normalized> NormalizeAsync(InputBatch batch, int index, FrameWork work)
        {
            work.Check();
            if (work.budget == null) return new Normalized(Normalize(batch, index, out int mip), mip);
            var size = batch.paddedSizes[index];
            if (!batch.needsConversion[index])
            {
                if (size == batch.sizes[index]) return new Normalized(batch.resources[index], batch.mip);
                int blocks = (size.x / batch.block) * (size.y / batch.block);
                while (!work.budget.TakeAtomicCopy(blocks)) await work.NextFrame();
                return new Normalized(Normalize(batch, index, out int mip), mip);
            }
            var item = GetConversion(batch, size);
            for (int row = 0; row < size.y;)
            {
                work.Check(); int rows = work.budget.TakeConversionRows(size.x, size.y - row);
                if (rows == 0) { await work.NextFrame(); continue; }
                ConvertRows(batch, index, item, row, rows); row += rows;
            }
            // The private conversion target stays unchanged until this encoding and its atlas copy finish.
            if (item.bc7 != null) item.bc7.BeginEncode(item.linear, false, (Bc7Quality)batch.quality, snapshotInput: false);
            else item.astc.BeginEncode(item.linear, false, (AstcQuality)batch.quality, snapshotInput: false);
            int totalBlocks = (size.x / batch.block) * (size.y / batch.block);
            while (true)
            {
                work.Check(); int blocks = work.budget.TakeEncoding(totalBlocks);
                if (blocks == 0) { await work.NextFrame(); continue; }
                bool done = item.bc7 != null ? item.bc7.EncodeNextFrameBlocks(blocks) : item.astc.EncodeNextFrameBlocks(blocks);
                if (done) break;
                await work.NextFrame();
            }
            if (item.Status != 1) throw new InvalidOperationException("GPU encoding upload failed: " + item.Status);
            return new Normalized(item.Texture.graphicsTexture, 0);
        }
        async Task CopyRectAsync(GraphicsTexture source, int mip, RectInt rect, GraphicsTexture destination,
            int x, int y, int block, FrameWork work)
        {
            work.Check();
            if (work.budget == null)
            {
                Graphics.CopyTexture(source, 0, mip, rect.x, rect.y, rect.width, rect.height, destination, 0, 0, x, y);
                CopyCount++; return;
            }
            int columns = rect.width / block, rows = rect.height / block, column = 0, row = 0;
            while (row < rows)
            {
                work.Check();
                int available = work.budget.TakeCopies(column == 0 ? (rows - row) * columns : columns - column,
                    column == 0 ? columns : 1);
                if (available == 0) { await work.NextFrame(); continue; }
                int width = column == 0 && available >= columns ? columns : available;
                int height = available / width;
                Graphics.CopyTexture(source, 0, mip, rect.x + column * block, rect.y + row * block,
                    width * block, height * block, destination, 0, 0, x + column * block, y + row * block);
                CopyCount++; column += width;
                if (column == columns) { column = 0; row += height; }
            }
        }
        void FenceSubmittedWork()
        {
            fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            hasFence = true;
        }
    }
}
