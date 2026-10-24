using System;
using UnityEngine;

namespace UnityCompute.TextureCompression
{
    /// <summary>Share one instance across atlas jobs to limit GPU submissions per Player frame.
    /// These are work quotas, not a GPU time limit. Texture creation and native uploads are atomic.</summary>
    public sealed class GpuAtlasFrameBudget
    {
        public int CopyBlocksPerFrame { get; }
        public int EncodeBlocksPerFrame { get; }
        public int ConversionPixelsPerFrame { get; }
        int frame = -1, copies, encoded, converted;
        internal int PeakCopyBlocks { get; private set; }
        internal int PeakEncodeBlocks { get; private set; }
        internal int PeakConversionPixels { get; private set; }
        internal int AtomicCopyOverruns { get; private set; }

        public GpuAtlasFrameBudget(int copyBlocksPerFrame = 16384, int encodeBlocksPerFrame = 1024,
            int conversionPixelsPerFrame = 65536)
        {
            if (copyBlocksPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(copyBlocksPerFrame));
            if (encodeBlocksPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(encodeBlocksPerFrame));
            if (conversionPixelsPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(conversionPixelsPerFrame));
            CopyBlocksPerFrame = copyBlocksPerFrame; EncodeBlocksPerFrame = encodeBlocksPerFrame;
            ConversionPixelsPerFrame = conversionPixelsPerFrame;
        }
        internal void CheckPlayer()
        {
            if (!Application.isPlaying) throw new NotSupportedException("Frame budgets require Play Mode or a Player frame loop.");
        }
        void Refresh()
        {
            CheckPlayer();
            if (frame == Time.frameCount) return;
            frame = Time.frameCount; copies = encoded = converted = 0;
        }
        internal int TakeCopies(int wanted, int rowBlocks)
        {
            Refresh(); int count = Math.Min(wanted, Math.Max(0, CopyBlocksPerFrame - copies));
            if (count >= rowBlocks) count = count / rowBlocks * rowBlocks;
            copies += count; PeakCopyBlocks = Math.Max(PeakCopyBlocks, copies); return count;
        }
        // Unity requires a whole-mip copy to preserve non-aligned edge blocks. It cannot be tiled.
        internal bool TakeAtomicCopy(int blocks)
        {
            Refresh();
            if (copies != 0 && blocks > CopyBlocksPerFrame - copies) return false;
            copies += blocks; PeakCopyBlocks = Math.Max(PeakCopyBlocks, copies);
            if (blocks > CopyBlocksPerFrame) AtomicCopyOverruns++;
            return true;
        }
        internal int TakeEncoding(int wantedBlocks)
        {
            Refresh(); int count = Math.Min(wantedBlocks, Math.Max(0, EncodeBlocksPerFrame - encoded));
            encoded += count; PeakEncodeBlocks = Math.Max(PeakEncodeBlocks, encoded); return count;
        }
        internal int TakeConversionRows(int width, int wantedRows)
        {
            Refresh(); int rows = Math.Min(wantedRows, Math.Max(0, ConversionPixelsPerFrame - converted) / width);
            converted += rows * width; PeakConversionPixels = Math.Max(PeakConversionPixels, converted); return rows;
        }
    }
}
