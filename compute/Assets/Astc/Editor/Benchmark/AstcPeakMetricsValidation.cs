using System;
using System.IO;
using UnityEngine;

namespace UnityCompute.Astc.Editor
{
    // Separate verification fixtures. Not called by the metric calculator or encoder.
    public static class AstcPeakMetricsValidation
    {
        static void Near(double actual, double expected) { if (Math.Abs(actual - expected) > 1e-9) throw new InvalidOperationException($"Expected {expected:R}, got {actual:R}"); }
        static void Require(bool value, string name) { if (!value) throw new InvalidOperationException(name); }
        static Color32[] Black(int count, byte alpha = 255)
        {
            var pixels = new Color32[count];
            for (int i = 0; i < count; i++) pixels[i] = new Color32(0, 0, 0, alpha);
            return pixels;
        }
        public static string Run()
        {
            var a = Black(35); var b = (Color32[])a.Clone();
            var exact = AstcPeakMetrics.Calculate(a, b, 7, 5, 4);
            foreach (var m in new[] { exact.rgb, exact.alpha, exact.composite })
            {
                Near(m.maxError, 0); Near(m.p99Error, 0); Near(m.worstBlockPsnr, 120);
                Require(m.maxX == 0 && m.maxY == 0, "Exact image origin");
            }
            b[34] = new Color32(255, 0, 0, 255); // top-right pixel in Unity row order
            var corner = AstcPeakMetrics.Calculate(a, b, 7, 5, 4);
            Near(corner.rgb.maxError, 255); Near(corner.rgb.p99Error, 255);
            Near(corner.rgb.worstBlockPsnr, 9.542425094393248);
            Near(corner.composite.worstBlockPsnr, 9.542425094393248);
            Require(corner.rgb.maxX == 6 && corner.rgb.maxY == 0 && corner.rgb.worstBlockX == 4 && corner.rgb.worstBlockY == 0 &&
                corner.rgb.worstBlockWidth == 3 && corner.rgb.worstBlockHeight == 1, "Partial top block coordinates");

            a = Black(100); b = (Color32[])a.Clone(); b[0] = new Color32(255, 0, 0, 255);
            Near(AstcPeakMetrics.Calculate(a, b, 10, 10, 4).rgb.p99Error, 0);
            b[0] = new Color32(40, 0, 0, 255); b[1] = new Color32(30, 0, 0, 255);
            var percentile = AstcPeakMetrics.Calculate(a, b, 10, 10, 4);
            Near(percentile.rgb.p99Error, 30); Near(percentile.rgb.maxError, 40);

            var hidden = AstcPeakMetrics.Calculate(Black(1, 0), new[] { new Color32(255, 0, 0, 0) }, 1, 1, 4);
            Near(hidden.rgb.maxError, 255); Near(hidden.composite.maxError, 0); Near(hidden.alpha.maxError, 0);

            var alpha = AstcPeakMetrics.Calculate(Black(1, 64), Black(1, 0), 1, 1, 4);
            Near(alpha.alpha.maxError, 64); Near(alpha.composite.maxError, 64);
            Near(alpha.composite.worstBlockPsnr, 15.017504085641171);

            var fractional = AstcPeakMetrics.Calculate(new[] { new Color32(255, 255, 255, 1) }, new[] { new Color32(254, 254, 254, 1) }, 1, 1, 4);
            Near(fractional.composite.maxError, 1.0 / 255); Near(fractional.composite.p99Error, 1.0 / 255);
            Near(fractional.composite.worstBlockPsnr, 96.2616072173582);

            a = Black(35); b = (Color32[])a.Clone(); b[0] = new Color32(60, 0, 0, 255);
            var bottom = AstcPeakMetrics.Calculate(a, b, 5, 7, 6);
            Require(bottom.rgb.maxX == 0 && bottom.rgb.maxY == 6 && bottom.rgb.worstBlockX == 0 && bottom.rgb.worstBlockY == 1 &&
                bottom.rgb.worstBlockWidth == 5 && bottom.rgb.worstBlockHeight == 6, "Bottom-aligned ASTC grid");
            Near(bottom.rgb.worstBlockPsnr, 32.110203695399484);
            string result = "PASS: exact pixels, partial block, nearest-rank P99, hidden RGB, alpha composite, fractional composite, bottom-aligned ASTC coordinates (7 fixtures).";
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation"));
            Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "peak-metrics-result.txt"), result);
            return result;
        }
    }
}
