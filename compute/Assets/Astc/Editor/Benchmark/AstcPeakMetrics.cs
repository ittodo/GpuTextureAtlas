using System;
using UnityEngine;

namespace UnityCompute.Astc.Editor
{
    // Analysis only. Color32 arrays use Unity's bottom-to-top row order.
    public static class AstcPeakMetrics
    {
        [Serializable] public sealed class Result
        {
            public ErrorSummary rgb, alpha, composite;
        }
        [Serializable] public sealed class ErrorSummary
        {
            public double maxError, p99Error, worstBlockPsnr;
            // All reported coordinates use the top-left image origin, in source pixels.
            public int maxX, maxY, worstBlockX, worstBlockY, worstBlockWidth, worstBlockHeight;
        }

        sealed class Accumulator
        {
            readonly int[] histogram;
            readonly double scale;
            readonly double[] squared;
            int maximum = -1, maxX, maxY;
            public Accumulator(int levels, double scale, int blocks)
            {
                histogram = new int[levels]; this.scale = scale; squared = new double[blocks];
            }
            public void Add(int peak, int x, int topY, int block, double error)
            {
                histogram[peak]++;
                squared[block] += error;
                if (peak > maximum) { maximum = peak; maxX = x; maxY = topY; }
            }
            public ErrorSummary Finish(int width, int height, int size, int channels, int[] counts)
            {
                int rank = (int)Math.Ceiling((long)width * height * .99), seen = 0, percentile = 0;
                for (; percentile < histogram.Length; percentile++)
                {
                    seen += histogram[percentile];
                    if (seen >= rank) break;
                }
                int columns = (width + size - 1) / size, rows = (height + size - 1) / size;
                double worstMse = -1; int worstX = 0, worstY = 0;
                // Scan in displayed image order to resolve equal-error blocks consistently.
                for (int by = rows - 1; by >= 0; by--)
                for (int bx = 0; bx < columns; bx++)
                {
                    int block = by * columns + bx;
                    double mse = squared[block] / ((double)counts[block] * channels);
                    if (mse > worstMse) { worstMse = mse; worstX = bx; worstY = by; }
                }
                return new ErrorSummary
                {
                    maxError = maximum / scale, p99Error = percentile / scale,
                    maxX = maxX, maxY = maxY,
                    worstBlockPsnr = worstMse == 0 ? 120 : 10 * Math.Log10(255.0 * 255 / worstMse),
                    worstBlockX = worstX * size,
                    worstBlockY = Math.Max(0, height - (worstY + 1) * size),
                    worstBlockWidth = Math.Min(size, width - worstX * size),
                    worstBlockHeight = Math.Min(size, height - worstY * size)
                };
            }
        }

        public static Result Calculate(Color32[] original, Color32[] decoded, int width, int height, int size)
        {
            if (width < 1 || height < 1 || size < 1) throw new ArgumentOutOfRangeException(nameof(width));
            int length = checked(width * height);
            if (original == null || decoded == null || original.Length != length || decoded.Length != length)
                throw new ArgumentException("Image arrays must match the dimensions.");
            int columns = (width + size - 1) / size;
            int blocks = checked(columns * ((height + size - 1) / size));
            var counts = new int[blocks];
            var rgb = new Accumulator(256, 1, blocks);
            var alpha = new Accumulator(256, 1, blocks);
            // Composite errors are exact integer numerators / 255, so an integer
            // histogram preserves all possible values without rounding to 8 bits.
            var composite = new Accumulator(65026, 255, blocks);
            for (int topY = 0; topY < height; topY++)
            for (int x = 0; x < width; x++)
            {
                int y = height - 1 - topY, pixel = y * width + x;
                int block = (y / size) * columns + x / size;
                Color32 a = original[pixel], b = decoded[pixel];
                int rgbPeak = 0, compositePeak = 0, alphaError = a.a - b.a;
                double rgbSquared = 0, compositeSquared = 0;
                for (int c = 0; c < 3; c++)
                {
                    int delta = a[c] - b[c];
                    rgbPeak = Math.Max(rgbPeak, Math.Abs(delta)); rgbSquared += delta * delta;
                    int blackNumerator = a[c] * a.a - b[c] * b.a;
                    int whiteNumerator = blackNumerator + 255 * (b.a - a.a);
                    compositePeak = Math.Max(compositePeak, Math.Max(Math.Abs(blackNumerator), Math.Abs(whiteNumerator)));
                    double black = blackNumerator / 255.0, white = whiteNumerator / 255.0;
                    compositeSquared += black * black + white * white;
                }
                counts[block]++;
                rgb.Add(rgbPeak, x, topY, block, rgbSquared);
                alpha.Add(Math.Abs(alphaError), x, topY, block, alphaError * alphaError);
                composite.Add(compositePeak, x, topY, block, compositeSquared);
            }
            return new Result
            {
                rgb = rgb.Finish(width, height, size, 3, counts),
                alpha = alpha.Finish(width, height, size, 1, counts),
                composite = composite.Finish(width, height, size, 6, counts)
            };
        }
    }
}
