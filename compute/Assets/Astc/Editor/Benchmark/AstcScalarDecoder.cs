using System;
using UnityEngine;

namespace UnityCompute.Astc.Editor
{
    // Verification-only reader. Compare each supported mode against the independent Arm decoder.
    internal static class AstcScalarDecoder
    {
        public static Color32 DecodePixel(AstcImage image, int x, int y)
        {
            int tile = (int)image.BlockSize;
            int offset = (y / tile * ((image.Width + tile - 1) / tile) + x / tile) * 16;
            byte[] data = image.Data;
            if (Read(data, offset, 0, 9) == 0x1FC)
                return new Color32((byte)((Read(data, offset, 64, 16) + 128) / 257), (byte)((Read(data, offset, 80, 16) + 128) / 257),
                    (byte)((Read(data, offset, 96, 16) + 128) / 257), (byte)((Read(data, offset, 112, 16) + 128) / 257));
            int modeCode = Read(data, offset, 0, 11);
            int[] mode = Array.Find(AstcTables.Modes, descriptor => descriptor[4] == modeCode);
            if (mode == null || Read(data, offset, 11, 2) != 0) throw new InvalidOperationException("Unsupported ASTC mode or partition count.");
            int gx = mode[0], gy = mode[1], weightBits = mode[2], nodes = gx * gy, planes = mode[3] != 0 ? 2 : 1;
            int format = Read(data, offset, 13, 4);
            if (format != 6 && format != 8 && format != 12) throw new InvalidOperationException("Unsupported endpoint format.");
            int valueCount = format == 6 ? 4 : format == 8 ? 6 : 8;
            int totalWeightBits = nodes * planes * weightBits;
            int budget = 128 - 17 - totalWeightBits - (planes == 2 ? 2 : 0), quant = -1;
            for (int i = 0; i < AstcTables.Levels.Length; i++)
            {
                int needed = AstcTables.Bits[i] * valueCount;
                needed += AstcTables.Radix[i] == 3 ? (valueCount * 8 + 4) / 5 : AstcTables.Radix[i] == 5 ? (valueCount * 7 + 2) / 3 : 0;
                if (needed <= budget) quant = i;
            }
            if (quant < 0) throw new InvalidOperationException("ASTC endpoint budget is too small.");
            int[] values = ReadSequence(data, offset, valueCount, quant);
            var lo = new int[] { 0, 0, 0, 255 }; var hi = new int[] { 0, 0, 0, 255 };
            for (int c = 0; c < (format == 6 ? 0 : valueCount / 2); c++)
            {
                lo[c] = AstcTables.Unpack[quant][values[c * 2]];
                hi[c] = AstcTables.Unpack[quant][values[c * 2 + 1]];
            }
            if (format == 6)
            {
                int scale = AstcTables.Unpack[quant][values[3]];
                for (int c = 0; c < 3; c++) { hi[c] = AstcTables.Unpack[quant][values[c]]; lo[c] = (hi[c] * scale) >> 8; }
            }
            if (lo[0] + lo[1] + lo[2] > hi[0] + hi[1] + hi[2])
            {
                lo[0] = (lo[0] + lo[2]) >> 1; lo[1] = (lo[1] + lo[2]) >> 1;
                hi[0] = (hi[0] + hi[2]) >> 1; hi[1] = (hi[1] + hi[2]) >> 1;
                var swap = lo; lo = hi; hi = swap;
            }
            int[] weights = new int[nodes * planes];
            for (int node = 0; node < nodes; node++)
            for (int plane = 0; plane < planes; plane++)
            {
                int code = 0;
                for (int bit = 0; bit < weightBits; bit++) code |= Read(data, offset, 127 - ((node * planes + plane) * weightBits + bit), 1) << bit;
                int v = weightBits == 1 ? code * 63 : weightBits == 2 ? code * 21 : weightBits == 3 ? (code << 3) | code : (code << 2) | (code >> 2);
                weights[node + plane * nodes] = v + (v > 32 ? 1 : 0);
            }
            int second = planes == 2 ? Read(data, offset, 128 - totalWeightBits - 2, 2) : -1;
            var output = new Color32();
            for (int channel = 0; channel < 4; channel++)
            {
                int weight = Interpolate(weights, channel == second ? nodes : 0, gx, gy, tile, x % tile, y % tile);
                int a = image.Srgb && channel < 3 ? lo[channel] * 256 + 128 : lo[channel] * 257;
                int b = image.Srgb && channel < 3 ? hi[channel] * 256 + 128 : hi[channel] * 257;
                int value16 = (a * (64 - weight) + b * weight + 32) / 64;
                output[channel] = (byte)((value16 * 255L + 32767) / 65535);
            }
            return output;
        }
        static int[] ReadSequence(byte[] data, int offset, int count, int quant)
        {
            int bits = AstcTables.Bits[quant], radix = AstcTables.Radix[quant], at = 17;
            int group = radix == 3 ? 5 : radix == 5 ? 3 : 1;
            var values = new int[count];
            for (int start = 0; start < count; start += group)
            {
                int packed = 0, consumed = 0, used = Math.Min(group, count - start);
                for (int i = 0; i < used; i++)
                {
                    values[start + i] = Read(data, offset, at, bits); at += bits;
                    int extra = radix == 3 ? (i == 2 || i == 4 ? 1 : 2) : radix == 5 ? (i == 0 ? 3 : 2) : 0;
                    packed |= Read(data, offset, at, extra) << consumed;
                    at += extra; consumed += extra;
                }
                if (radix == 0) continue;
                byte[] table = radix == 3 ? AstcTables.Trits : AstcTables.Quints;
                int limit = (int)Math.Pow(radix, used), digits = -1, mask = (1 << consumed) - 1;
                for (int value = 0; value < limit; value++) if ((table[value] & mask) == packed) { digits = value; break; }
                if (digits < 0) throw new InvalidOperationException("Invalid ASTC bounded integer sequence.");
                for (int i = 0; i < used; i++) { values[start + i] |= (digits % radix) << bits; digits /= radix; }
            }
            return values;
        }
        static int Interpolate(int[] weights, int plane, int gx, int gy, int tile, int x, int y)
        {
            int scale = (1024 + tile / 2) / (tile - 1);
            int px = (scale * x * (gx - 1) + 32) >> 6, py = (scale * y * (gy - 1) + 32) >> 6;
            int ix = px >> 4, iy = py >> 4, fx = px & 15, fy = py & 15;
            int d = (fx * fy + 8) >> 4;
            return (weights[plane + iy * gx + ix] * (16 - fx - fy + d) +
                weights[plane + iy * gx + Math.Min(ix + 1, gx - 1)] * (fx - d) +
                weights[plane + Math.Min(iy + 1, gy - 1) * gx + ix] * (fy - d) +
                weights[plane + Math.Min(iy + 1, gy - 1) * gx + Math.Min(ix + 1, gx - 1)] * d + 8) >> 4;
        }
        static int Read(byte[] data, int offset, int bit, int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++) value |= ((data[offset + (bit + i) / 8] >> ((bit + i) % 8)) & 1) << i;
            return value;
        }
    }
}
