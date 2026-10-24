using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityCompute.Astc;
using UnityCompute.Astc.Editor;
using UnityCompute.Bc7;
using Object = UnityEngine.Object;

namespace UnityCompute.TextureCompression.Editor
{
    public static class FrameSlicedBenchmark
    {
        [Serializable] public sealed class Row
        {
            public string texture, format, quality;
            public int width, height, blockSize, rowsPerBatch, batches;
            public double fullMs, meanBatchMs, p95BatchMs, maxBatchMs;
        }
        [Serializable] sealed class Report
        {
            public string unity, utc, gpu, api, methodology, astcSha256, bc7Sha256;
            public List<Row> rows = new List<Row>();
        }
        public static string Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../"));
            var astcShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute");
            var bc7Shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute");
            using var astc = new AstcEncoder(astcShader); using var bc7 = new Bc7Encoder(bc7Shader);
            string Hash(string p) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(UnityCompute.TextureCompression.Editor.GpuAtlasProjectPaths.FilePath(p)))).Replace("-", ""); }
            var report = new Report { unity = Application.unityVersion, utc = DateTime.UtcNow.ToString("O"), gpu = SystemInfo.graphicsDeviceName,
                api = SystemInfo.graphicsDeviceType.ToString(), astcSha256 = Hash(AssetDatabase.GetAssetPath(astcShader)), bc7Sha256 = Hash(AssetDatabase.GetAssetPath(bc7Shader)),
                methodology = "Two representative canonical 1024 inputs, not the full 60-input quality corpus. At least 200 ms full-image GPU warmup per profile and two untimed dispatches per measured range. Reused buffer throughput; median of 3 batches of 4 dispatches, final GetData amortized per dispatch. Every row band is measured, including the shorter final band. Snapshot, final native texture copy and rendering are excluded. These are not GPU timestamps, per-frame latency, or a strict time budget. Clocks are not pinned. No mobile timing claims." };
            foreach (string name in new[] { "ui_alpha_atlas", "wood_floor_worn" })
            {
                var source = AstcBenchmark.Load(Path.Combine(root, "Benchmark/Input/1024", name + ".png"));
                try
                {
                    foreach (AstcQuality quality in Enum.GetValues(typeof(AstcQuality))) foreach (var size in new[] { AstcBlockSize.Block4x4, AstcBlockSize.Block6x6 })
                    {
                        using var buffer = new ComputeBuffer(AstcEncoder.RequiredBlockCount(source, size), 16);
                        Measure(report, name, "ASTC", quality.ToString(), source, (int)size, buffer,
                            (first, count) => astc.EncodeGpu(source, buffer, size, true, true, quality, firstBlockRow: first, blockRowCount: count));
                    }
                    foreach (Bc7Quality quality in Enum.GetValues(typeof(Bc7Quality)))
                    {
                        using var buffer = new ComputeBuffer(Bc7Encoder.RequiredBlockCount(source), 16);
                        Measure(report, name, "BC7", quality.ToString(), source, 4, buffer,
                            (first, count) => bc7.EncodeGpu(source, buffer, true, true, quality, firstBlockRow: first, blockRowCount: count));
                    }
                }
                finally { Object.DestroyImmediate(source); }
            }
            string directory = Path.Combine(root, "Benchmark/Results/frame-sliced"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "measurements.json"), JsonUtility.ToJson(report, true));
            return $"PASS: saved {report.rows.Count} full-vs-band timing rows: {directory}";
        }
        static void Measure(Report report, string texture, string format, string quality, Texture source, int blockSize,
            ComputeBuffer buffer, Action<int, int> encode)
        {
            int rows = (source.height + blockSize - 1) / blockSize; var words = new uint[buffer.count * 4];
            var warmup = Stopwatch.StartNew();
            do { encode(0, rows); buffer.GetData(words); } while (warmup.ElapsedMilliseconds < 200);
            double Time(int first, int count)
            {
                for (int i = 0; i < 2; i++) encode(first, count);
                buffer.GetData(words);
                var samples = new double[3];
                for (int repeat = 0; repeat < samples.Length; repeat++)
                {
                    var watch = Stopwatch.StartNew(); for (int i = 0; i < 4; i++) encode(first, count);
                    buffer.GetData(words); samples[repeat] = watch.Elapsed.TotalMilliseconds / 4;
                }
                Array.Sort(samples); return samples[1];
            }
            double full = Time(0, rows);
            foreach (int budget in new[] { 4, 32 })
            {
                var bands = new List<double>();
                for (int first = 0; first < rows; first += budget) bands.Add(Time(first, Math.Min(budget, rows - first)));
                double total = 0; foreach (double value in bands) total += value; bands.Sort();
                report.rows.Add(new Row { texture = texture, format = format, quality = quality, width = source.width, height = source.height,
                    blockSize = blockSize, rowsPerBatch = budget, batches = bands.Count, fullMs = full,
                    meanBatchMs = total / bands.Count, p95BatchMs = bands[(int)Math.Ceiling(bands.Count * .95) - 1], maxBatchMs = bands[bands.Count - 1] });
            }
        }
    }
}
