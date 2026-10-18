using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCompute.TextureCompression
{
    // Geometry only. CPU payload packing and GPU texture copies use the same placement.
    internal sealed class BlockAtlasLayout
    {
        internal int Width, Height, Columns;
        internal RectInt[] Regions;
        struct Item { internal int index, width, height; }

        internal static BlockAtlasLayout Build(IReadOnlyList<Vector2Int> sizes, int blockWidth, int blockHeight,
            int maxSize, int paddingBlocks, int bytesPerBlock)
        {
            if (sizes.Count == 0) throw new ArgumentException("At least one matching input is required.");
            if (maxSize < 1 || maxSize > 16384) throw new ArgumentOutOfRangeException(nameof(maxSize));
            if (paddingBlocks < 0 || paddingBlocks > maxSize) throw new ArgumentOutOfRangeException(nameof(paddingBlocks));
            int maxW = maxSize / blockWidth, maxH = maxSize / blockHeight, minW = 0, minH = 0;
            long area = 0; var items = new Item[sizes.Count];
            for (int i = 0; i < items.Length; i++)
            {
                if (sizes[i].x < 1 || sizes[i].y < 1) throw new ArgumentOutOfRangeException(nameof(sizes));
                int w = checked((int)((sizes[i].x + (long)blockWidth - 1) / blockWidth) + paddingBlocks * 2);
                int h = checked((int)((sizes[i].y + (long)blockHeight - 1) / blockHeight) + paddingBlocks * 2);
                if (w > maxW || h > maxH) throw new InvalidOperationException($"Input {i} exceeds maxSize with its block padding.");
                items[i] = new Item { index = i, width = w, height = h };
                minW = Math.Max(minW, w); minH = Math.Max(minH, h); area += (long)w * h;
            }
            if (area > (long)maxW * maxH) throw new InvalidOperationException("Inputs exceed the maximum atlas area.");
            Array.Sort(items, (a, b) => {
                int c = ((long)b.width * b.height).CompareTo((long)a.width * a.height);
                if (c == 0) c = Math.Max(b.width, b.height).CompareTo(Math.Max(a.width, a.height));
                return c != 0 ? c : a.index.CompareTo(b.index);
            });
            var candidates = new List<RectInt>();
            foreach (int w in Sizes(minW, maxW)) foreach (int h in Sizes(minH, maxH))
                if ((long)w * h >= area && (long)w * h * bytesPerBlock <= int.MaxValue)
                    candidates.Add(new RectInt(0, 0, w, h));
            candidates.Sort((a, b) => {
                int c = ((long)a.width * a.height).CompareTo((long)b.width * b.height);
                if (c == 0) c = Math.Max(a.width, a.height).CompareTo(Math.Max(b.width, b.height));
                return c != 0 ? c : a.width.CompareTo(b.width);
            });
            foreach (var candidate in candidates)
            {
                if (!Place(items, candidate, out var positions)) continue;
                for (int i = 0; i < positions.Length; i++)
                {
                    var r = positions[i]; positions[i] = new RectInt(r.x + paddingBlocks, r.y + paddingBlocks,
                        r.width - paddingBlocks * 2, r.height - paddingBlocks * 2);
                }
                return new BlockAtlasLayout { Width = candidate.width * blockWidth, Height = candidate.height * blockHeight,
                    Columns = candidate.width, Regions = positions };
            }
            throw new InvalidOperationException("The packing heuristic could not fit one atlas within maxSize. Increase maxSize or split the input list.");
        }

        static List<int> Sizes(int minimum, int maximum)
        {
            var values = new List<int> { minimum };
            for (int n = 1; n <= maximum; n *= 2) if (n > minimum && n < maximum) values.Add(n);
            if (maximum != minimum) values.Add(maximum);
            return values;
        }
        // Existing reservations stay fixed. Try holes first, then grow without moving an old image.
        internal static BlockAtlasLayout Append(BlockAtlasLayout current, IReadOnlyList<Vector2Int> sizes,
            int block, int maxSize, int paddingBlocks)
        {
            int maximum = maxSize / block, oldW = current.Width / block, oldH = current.Height / block;
            var occupied = new RectInt[current.Regions.Length]; long area = 0;
            for (int i = 0; i < occupied.Length; i++)
            {
                var r = current.Regions[i];
                occupied[i] = new RectInt(r.x - paddingBlocks, r.y - paddingBlocks,
                    r.width + paddingBlocks * 2, r.height + paddingBlocks * 2);
                area += (long)occupied[i].width * occupied[i].height;
            }
            var items = new Item[sizes.Count];
            for (int i = 0; i < items.Length; i++)
            {
                int w = checked((int)((sizes[i].x + (long)block - 1) / block) + paddingBlocks * 2);
                int h = checked((int)((sizes[i].y + (long)block - 1) / block) + paddingBlocks * 2);
                if (w > maximum || h > maximum) throw new InvalidOperationException($"Input {i} exceeds maxSize with its block padding.");
                items[i] = new Item { index = i, width = w, height = h }; area += (long)w * h;
            }
            Array.Sort(items, (a, b) => {
                int c = ((long)b.width * b.height).CompareTo((long)a.width * a.height);
                if (c == 0) c = Math.Max(b.width, b.height).CompareTo(Math.Max(a.width, a.height));
                return c != 0 ? c : a.index.CompareTo(b.index);
            });
            var candidates = new List<RectInt>();
            foreach (int w in Sizes(oldW, maximum)) foreach (int h in Sizes(oldH, maximum))
                if ((long)w * h >= area && (long)w * h * 16 <= int.MaxValue)
                    candidates.Add(new RectInt(0, 0, w, h));
            candidates.Sort((a, b) => {
                int c = ((long)a.width * a.height).CompareTo((long)b.width * b.height);
                if (c == 0) c = Math.Max(a.width, a.height).CompareTo(Math.Max(b.width, b.height));
                return c != 0 ? c : a.width.CompareTo(b.width);
            });
            foreach (var candidate in candidates)
            {
                if (!Place(items, candidate, out var positions, occupied)) continue;
                var all = new RectInt[current.Regions.Length + positions.Length];
                Array.Copy(current.Regions, all, current.Regions.Length);
                for (int i = 0; i < positions.Length; i++)
                {
                    var r = positions[i]; all[current.Regions.Length + i] = new RectInt(r.x + paddingBlocks, r.y + paddingBlocks,
                        r.width - paddingBlocks * 2, r.height - paddingBlocks * 2);
                }
                return new BlockAtlasLayout { Width = candidate.width * block, Height = candidate.height * block,
                    Columns = candidate.width, Regions = all };
            }
            throw new InvalidOperationException("New inputs do not fit without moving existing images. Increase maxSize when creating the atlas or use another atlas.");
        }
        static bool Contains(RectInt a, RectInt b) => a.x <= b.x && a.y <= b.y && a.xMax >= b.xMax && a.yMax >= b.yMax;
        static bool Place(Item[] items, RectInt bounds, out RectInt[] positions, IReadOnlyList<RectInt> occupied = null)
        {
            positions = new RectInt[items.Length]; var free = new List<RectInt> { bounds };
            if (occupied != null) foreach (var r in occupied) Subtract(free, r);
            foreach (var item in items)
            {
                int best = -1, shortSide = int.MaxValue, longSide = int.MaxValue;
                for (int i = 0; i < free.Count; i++)
                {
                    var r = free[i]; int dx = r.width - item.width, dy = r.height - item.height;
                    if (dx < 0 || dy < 0) continue;
                    int s = Math.Min(dx, dy), l = Math.Max(dx, dy);
                    if (s < shortSide || (s == shortSide && l < longSide)) { best = i; shortSide = s; longSide = l; }
                }
                if (best < 0) return false;
                var placed = new RectInt(free[best].x, free[best].y, item.width, item.height); positions[item.index] = placed;
                Subtract(free, placed);
            }
            return true;
        }
        static void Subtract(List<RectInt> free, RectInt placed)
        {
            int count = free.Count;
            for (int i = count - 1; i >= 0; i--)
            {
                var r = free[i]; if (!r.Overlaps(placed)) continue; free.RemoveAt(i);
                if (placed.x > r.x) free.Add(new RectInt(r.x, r.y, placed.x - r.x, r.height));
                if (placed.xMax < r.xMax) free.Add(new RectInt(placed.xMax, r.y, r.xMax - placed.xMax, r.height));
                if (placed.y > r.y) free.Add(new RectInt(r.x, r.y, r.width, placed.y - r.y));
                if (placed.yMax < r.yMax) free.Add(new RectInt(r.x, placed.yMax, r.width, r.yMax - placed.yMax));
            }
            for (int i = free.Count - 1; i >= 0; i--) for (int j = 0; j < free.Count; j++)
                if (i != j && Contains(free[j], free[i])) { free.RemoveAt(i); break; }
        }
    }
}
