using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCompute.Astc.Editor
{
    public static class AstcBenchmark
    {
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "../../Benchmark"));
        static string Codec => Path.GetFullPath(Path.Combine(Root, "../.validation/astcenc/bin/astcenc-avx2.exe"));
        [Serializable] public sealed class Measurements
        {
            public string utc, unity, gpu, cpu, api, methodology, shaderSha256, inputManifestSha256;
            public string peakMetricsUtc;
            public List<Row> rows = new List<Row>();
        }
        [Serializable] public sealed class Row
        {
            public string texture, encoder, group;
            public int size, width, height, payloadBytes, repetitions;
            public double medianMs, p95Ms, codecMedianMs, rgbPsnr, alphaPsnr, compositePsnr, ssim;
            public double bufferReuseBatchMedianMs, submissionMedianMs;
            public AstcPeakMetrics.Result peak;
        }

        /// <summary>Reuses canonical decoded PNGs. Does not rerun compression or change timing measurements.</summary>
        public static string RefreshPeakMetrics()
        {
            string Hash(string path) { using var h = SHA256.Create(); return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
            string manifestHash = Hash(Path.Combine(Root, "Input/manifest.json"));
            var originals = new Dictionary<string, Color32[]>();
            int updated = 0;
            foreach (string version in new[] { "baseline", "fast", "balanced" })
            {
                string directory = Path.Combine(Root, "Results", version), reportPath = Path.Combine(directory, "measurements.json");
                string saved = File.ReadAllText(reportPath);
                var report = JsonUtility.FromJson<Measurements>(saved);
                // Preserve the original decimal tokens. Unity JsonUtility can change the
                // last bit of an existing double during a deserialize/serialize round trip.
                JObject document;
                using (var reader = new JsonTextReader(new StringReader(saved))
                { FloatParseHandling = FloatParseHandling.Decimal, DateParseHandling = DateParseHandling.None })
                    document = JObject.Load(reader);
                if (report.inputManifestSha256 != manifestHash) throw new InvalidOperationException("Input manifest changed: " + version);
                int rowIndex = 0;
                foreach (var row in report.rows)
                {
                    string key = row.width + "/" + row.texture;
                    if (!originals.TryGetValue(key, out var original))
                    {
                        var input = Load(Path.Combine(Root, "Input", row.width.ToString(), row.texture + ".png"));
                        try
                        {
                            if (input.width != row.width || input.height != row.height) throw new InvalidDataException(key);
                            original = input.GetPixels32(); originals.Add(key, original);
                        }
                        finally { Object.DestroyImmediate(input); }
                    }
                    string suffix = row.encoder.StartsWith("astcenc-", StringComparison.Ordinal) ? row.encoder.Replace('-', '_') : "gpu";
                    string path = Path.Combine(directory, row.texture + "_" + row.width + "_" + row.size + "_" + suffix + ".png");
                    var decoded = Load(path);
                    try
                    {
                        if (decoded.width != row.width || decoded.height != row.height) throw new InvalidDataException(path);
                        row.peak = AstcPeakMetrics.Calculate(original, decoded.GetPixels32(), row.width, row.height, row.size);
                        document["rows"][rowIndex]["peak"] = JObject.Parse(JsonUtility.ToJson(row.peak));
                        updated++;
                    }
                    finally { Object.DestroyImmediate(decoded); }
                    rowIndex++;
                }
                document["peakMetricsUtc"] = DateTime.UtcNow.ToString("O");
                File.WriteAllText(reportPath, document.ToString(Formatting.Indented));
            }
            return $"Updated peak metrics for {updated} stored comparisons. Encoding and timing were not rerun.";
        }

        public static string Run(string version = "baseline", bool includeReference = true, int limit = 0)
        {
            string output = Path.Combine(Root, "Results", version);
            Directory.CreateDirectory(output);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(version == "baseline" ?
                "Assets/Astc/Editor/Benchmark/AstcBaseline.compute" : "Assets/Astc/Shaders/AstcEncoder.compute");
            if (shader == null) throw new InvalidOperationException("Benchmark shader is missing.");
            using var encoder = new AstcEncoder(shader);
            AstcQuality quality = version.StartsWith("fast", StringComparison.Ordinal) ? AstcQuality.Fast : AstcQuality.Balanced;
            string shaderPath = AssetDatabase.GetAssetPath(shader);
            string Hash(string path) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
            var report = new Measurements { utc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion,
                shaderSha256 = Hash(shaderPath), inputManifestSha256 = Hash(Path.Combine(Root, "Input/manifest.json")),
                gpu = SystemInfo.graphicsDeviceName, cpu = SystemInfo.processorType, api = SystemInfo.graphicsDeviceType.ToString(),
                methodology = "10 alpha + 10 opaque textures at each of 256, 512 and 1024 square pixels, center-cropped and bicubic resized. Alpha: 5 official Arm/Khronos textures, 4 photographic RGB textures with controlled independent alpha masks, 1 UI atlas. Three Arm originals are 256 pixels and are upscaled at higher test resolutions. RGB code-value PSNR, alpha PSNR, black/white straight-alpha composite PSNR, 8x8 luminance SSIM. GPU: 3 warmups, 9 sync API wall-clock repetitions, allocation/dispatch/readback/copy included. Buffer reuse: median of 3 batches of 16 dispatches with one final completion/readback per batch; this is not a GPU timestamp and excludes native ASTC texture upload. CPU: astcenc 5.7.0 AVX2, -cs -j 16; 1 warmup, 3 process wall-clock repetitions; codecMedianMs is the separately reported coding time. No mobile timing claims." };
            foreach (string sourcePath in Directory.GetFiles(Path.Combine(Root, "Input"), "*.png", SearchOption.AllDirectories).OrderBy(path => path).Take(limit > 0 ? limit : int.MaxValue))
            {
                string name = Path.GetFileNameWithoutExtension(sourcePath);
                var source = Load(sourcePath);
                Color32[] original = source.GetPixels32();
                try
                {
                    // Raw sRGB code values are loaded through a linear texture. No GPU color transform is needed.
                    foreach (var size in new[] { AstcBlockSize.Block4x4, AstcBlockSize.Block6x6 })
                    {
                        string stem = Path.Combine(output, name + "_" + source.width + "_" + (int)size);
                        AstcImage image = null;
                        for (int warm = 0; warm < 3; warm++) image = encoder.Encode(source, size, true, true, quality);
                        var times = new double[9];
                        for (int repeat = 0; repeat < times.Length; repeat++)
                        {
                            var clock = Stopwatch.StartNew();
                            image = encoder.Encode(source, size, true, true, quality);
                            times[repeat] = clock.Elapsed.TotalMilliseconds;
                        }
                        string gpuStem = stem + "_gpu";
                        File.WriteAllBytes(gpuStem + ".astc", image.ToAstcFile());
                        var gpuPixels = Decode(gpuStem, image.Srgb, source.height);
                        var row = Summarize(name, version, (int)size, source, original, gpuPixels, times, null, image.Data.Length);
                        MeasureBufferReuse(encoder, source, size, quality, row);
                        report.rows.Add(row);
                        if (includeReference)
                        foreach (string preset in new[] { "fastest", "fast", "medium" })
                        {
                            string referenceStem = stem + "_astcenc_" + preset;
                            // ASTC file row order follows the input PNG. Flip it to match the GPU export's row order.
                            string input = Path.Combine(output, name + "_" + source.width + "_codec_input.png");
                            if (!File.Exists(input)) SaveRows(input, source.width, source.height, Flip(original, source.width, source.height));
                            string command = $"-cs \"{input}\" \"{referenceStem}.astc\" {(int)size}x{(int)size} -{preset} -j 16";
                            Execute(command);
                            var cpuTimes = new double[3];
                            var codingTimes = new double[3];
                            for (int repeat = 0; repeat < 3; repeat++)
                            {
                                var clock = Stopwatch.StartNew();
                                string log = Execute(command);
                                cpuTimes[repeat] = clock.Elapsed.TotalMilliseconds;
                                var match = Regex.Match(log, @"Coding time\s*:\s*([0-9.]+)\s*s");
                                if (!match.Success) throw new InvalidOperationException("Cannot parse astcenc coding time: " + log);
                                codingTimes[repeat] = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1000;
                            }
                            var decoded = Decode(referenceStem, true, source.height);
                            report.rows.Add(Summarize(name, "astcenc-" + preset, (int)size, source, original, decoded,
                                cpuTimes, codingTimes, checked((int)new FileInfo(referenceStem + ".astc").Length - 16)));
                        }
                        File.WriteAllText(Path.Combine(output, "measurements.json"), JsonUtility.ToJson(report, true));
                    }
                }
                finally { Object.DestroyImmediate(source); }
            }
            return $"Saved {report.rows.Count} benchmark rows to {output}";
        }

        static void MeasureBufferReuse(AstcEncoder encoder, Texture source, AstcBlockSize size, AstcQuality quality, Row row)
        {
            int count = AstcEncoder.RequiredBlockCount(source, size);
            using (var buffer = new ComputeBuffer(count, 16))
            {
                var words = new uint[count * 4];
                encoder.EncodeGpu(source, buffer, size, true, true, quality); buffer.GetData(words);
                var batches = new double[3]; var submissions = new List<double>();
                for (int repeat = 0; repeat < batches.Length; repeat++)
                {
                    var clock = Stopwatch.StartNew();
                    for (int i = 0; i < 16; i++)
                    {
                        var submit = Stopwatch.StartNew();
                        encoder.EncodeGpu(source, buffer, size, true, true, quality);
                        submissions.Add(submit.Elapsed.TotalMilliseconds);
                    }
                    buffer.GetData(words); // One completion/readback per batch, amortized across 16 dispatches.
                    batches[repeat] = clock.Elapsed.TotalMilliseconds / 16;
                }
                Array.Sort(batches); submissions.Sort();
                row.bufferReuseBatchMedianMs = batches[1]; row.submissionMedianMs = submissions[submissions.Count / 2];
            }
        }

        internal static Row Summarize(string name, string encoder, int size, Texture2D source, Color32[] original,
            Color32[] decoded, double[] wall, double[] coding, int bytes)
        {
            double rgb = 0, alpha = 0, composite = 0;
            for (int i = 0; i < original.Length; i++)
            {
                double a = original[i].a / 255.0, b = decoded[i].a / 255.0;
                for (int c = 0; c < 3; c++)
                {
                    double difference = original[i][c] - decoded[i][c]; rgb += difference * difference;
                    double black = original[i][c] * a - decoded[i][c] * b;
                    double white = black + 255 * (b - a);
                    composite += black * black + white * white;
                }
                double ad = original[i].a - decoded[i].a; alpha += ad * ad;
            }
            Array.Sort(wall);
            if (coding != null) Array.Sort(coding);
            return new Row { texture = name, encoder = encoder, group = original.Any(pixel => pixel.a != 255) ? "alpha" : "opaque", size = size, width = source.width, height = source.height,
                payloadBytes = bytes, repetitions = wall.Length, medianMs = wall[wall.Length / 2],
                p95Ms = wall[(int)Math.Ceiling(wall.Length * .95) - 1], codecMedianMs = coding == null ? 0 : coding[coding.Length / 2],
                rgbPsnr = Psnr(rgb, original.Length * 3), alphaPsnr = Psnr(alpha, original.Length),
                compositePsnr = Psnr(composite, original.Length * 6), ssim = Ssim(original, decoded, source.width, source.height),
                peak = AstcPeakMetrics.Calculate(original, decoded, source.width, source.height, size) };
        }

        static double Psnr(double error, int count) => error == 0 ? 120 : 10 * Math.Log10(255.0 * 255 * count / error);
        static double Ssim(Color32[] a, Color32[] b, int width, int height)
        {
            double total = 0; int windows = 0;
            for (int y = 0; y < height; y += 8)
            for (int x = 0; x < width; x += 8)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; int count = 0;
                for (int j = y; j < Math.Min(y + 8, height); j++)
                for (int i = x; i < Math.Min(x + 8, width); i++)
                {
                    Color32 ca = a[j * width + i], cb = b[j * width + i];
                    double va = ca.r * .2126 + ca.g * .7152 + ca.b * .0722;
                    double vb = cb.r * .2126 + cb.g * .7152 + cb.b * .0722;
                    sa += va; sb += vb; saa += va * va; sbb += vb * vb; sab += va * vb; count++;
                }
                double ma = sa / count, mb = sb / count;
                total += ((2 * ma * mb + 6.5025) * (2 * (sab / count - ma * mb) + 58.5225)) /
                    ((ma * ma + mb * mb + 6.5025) * (saa / count - ma * ma + sbb / count - mb * mb + 58.5225));
                windows++;
            }
            return total / windows;
        }

        internal static Color32[] Decode(string stem, bool srgb, int height)
        {
            Execute($"{(srgb ? "-ds" : "-dl")} \"{stem}.astc\" \"{stem}.png\" -silent");
            var image = Load(stem + ".png");
            try
            {
                var canonical = Flip(image.GetPixels32(), image.width, height);
                SaveRows(stem + ".png", image.width, height, canonical);
                return canonical;
            }
            finally { Object.DestroyImmediate(image); }
        }
        static Color32[] Flip(Color32[] pixels, int width, int height)
        {
            var result = new Color32[pixels.Length];
            for (int y = 0; y < height; y++) Array.Copy(pixels, y * width, result, (height - 1 - y) * width, width);
            return result;
        }
        internal static Texture2D Load(string path)
        {
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!image.LoadImage(File.ReadAllBytes(path))) { Object.DestroyImmediate(image); throw new IOException(path); }
            return image;
        }
        internal static void SaveRows(string path, int width, int height, Color32[] colors)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try { texture.SetPixels32(colors); texture.Apply(false); File.WriteAllBytes(path, texture.EncodeToPNG()); }
            finally { Object.DestroyImmediate(texture); }
        }
        static string Execute(string arguments)
        {
            var start = new ProcessStartInfo(Codec, arguments) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(120000)) { process.Kill(); throw new TimeoutException("astcenc timed out."); }
                if (process.ExitCode != 0) throw new InvalidOperationException(output + process.StandardError.ReadToEnd());
                return output;
            }
        }
    }
}
