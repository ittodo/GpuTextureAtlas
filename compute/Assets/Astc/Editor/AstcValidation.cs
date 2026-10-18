using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityCompute.Astc;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace UnityCompute.Astc.Editor
{
    // Verification only. This code is excluded from player builds and from the encoding path.
    public static class AstcValidation
    {
        const string ShaderPath = "Assets/Astc/Shaders/AstcEncoder.compute";
        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation"));
        public static string AsyncStatus { get; private set; } = "not run";
        static AstcQuality activeQuality;

        public static string RunShaderCompilers()
        {
            // Compile a disposable asset. This internal API can leave the compiled asset
            // in a foreign-platform state in Unity 6000.6.6f1.
            const string probePath = "Assets/Astc/Shaders/AstcCompileProbe.compute";
            File.Copy(ShaderPath, probePath, true);
            AssetDatabase.ImportAsset(probePath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(probePath);
            try
            {
            // Unity exposes the compute variant compiler internally. Keep reflection in verification code.
            var method = typeof(ShaderUtil).GetMethod("CompileComputeShaderVariant",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (method == null) throw new NotSupportedException("This Unity version does not expose the compute variant compiler.");
            var results = new List<string>();
            var apis = new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan,
                UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3, UnityEngine.Rendering.GraphicsDeviceType.Metal };
            foreach (string kernelName in new[] { "EncodeAstcFast", "EncodeAstcBalanced", "EncodeAstcHighQuality" })
            foreach (var api in apis)
            {
                var target = api == UnityEngine.Rendering.GraphicsDeviceType.Metal ? BuildTarget.iOS : BuildTarget.Android;
                var result = (ShaderData.VariantCompileInfo)method.Invoke(null, new object[] { shader, shader.FindKernel(kernelName), new string[0], api, target });
                Require(result.Success && result.ShaderData != null && result.ShaderData.Length > 0 && result.Messages.Length == 0,
                    api + " compilation failed: " + string.Join("; ", result.Messages.Select(message => message.message)));
                results.Add($"PASS: {kernelName} {target}/{api}, {result.ShaderData.Length} bytes, {result.Messages.Length} diagnostics.");
            }
            Directory.CreateDirectory(Root);
            string summary = string.Join("\n", results);
            File.WriteAllText(Path.Combine(Root, "shader-compilers.txt"), summary);
            return summary;
            }
            finally { AssetDatabase.DeleteAsset(probePath); }
        }

        [Serializable] sealed class Report
        {
            public string unity, gpu, graphicsApi, decoder, utc;
            public bool passed;
            public int cases, comparedChannels, maxDecoderDifference;
            public List<CaseResult> results = new List<CaseResult>();
        }
        [Serializable] sealed class CaseResult
        {
            public string name;
            public int blockSize, width, height, bytes, maxDecoderDifference;
            public double rgbaPsnr;
        }

        [MenuItem("Tools/ASTC/Validate with Arm decoder")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run(AstcQuality quality = AstcQuality.Balanced)
        {
            activeQuality = quality;
            Directory.CreateDirectory(Root);
            string decoder = Path.Combine(Root, "astcenc/bin/astcenc-sse2.exe");
            if (!File.Exists(decoder)) throw new FileNotFoundException("Install the reference decoder with Tools/Verify-Astc.ps1.", decoder);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            var messages = ShaderUtil.GetComputeShaderMessages(shader);
            Require(messages.Length == 0, "Compute shader import has diagnostics.");
            using var encoder = new AstcEncoder(shader);
            var report = new Report { unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), decoder = "Arm astcenc 5.7.0 SSE2",
                utc = DateTime.UtcNow.ToString("O") };
            string directory = Path.Combine(Root, "cases", quality.ToString());
            Directory.CreateDirectory(directory);

            foreach (AstcBlockSize size in Enum.GetValues(typeof(AstcBlockSize)))
            {
                RunCase(encoder, decoder, directory, report, size, "solid", 13, 17, (x, y) => new Color32(37, 113, 219, 255), true);
                RunCase(encoder, decoder, directory, report, size, "transparent", 7, 5, (x, y) => new Color32(91, 27, 151, 0), true);
                RunCase(encoder, decoder, directory, report, size, "gradient", 65, 33,
                    (x, y) => new Color(x / 64f, y / 32f, (x / 64f + y / 32f) * .5f, 1), false, minimumPsnr: 22);
                RunCase(encoder, decoder, directory, report, size, "anticorrelated", 17, 13,
                    (x, y) => new Color(x / 16f, 1 - x / 16f, .25f, 1), false, minimumPsnr: 22);
                RunCase(encoder, decoder, directory, report, size, "alpha", 17, 13,
                    (x, y) => new Color(.2f, .4f, .7f, x / 16f), false, minimumPsnr: 22);
                RunCase(encoder, decoder, directory, report, size, "rgba_detail", 17, 13,
                    (x, y) => new Color((x & 1), (y & 1), ((x + y) % 3) * .5f, ((x * 7 + y * 3) % 11) / 10f), false);
                RunCase(encoder, decoder, directory, report, size, "one_pixel", 1, 1, (x, y) => new Color32(31, 79, 191, 83), true);
                RunCase(encoder, decoder, directory, report, size, "one_column", 1, 19,
                    (x, y) => new Color(y / 18f, .3f, .7f, 1), false, minimumPsnr: 22);
                RunCase(encoder, decoder, directory, report, size, "one_row", 19, 1,
                    (x, y) => new Color(.3f, x / 18f, .7f, 1), false, minimumPsnr: 22);
                RunCase(encoder, decoder, directory, report, size, "linear_to_srgb", 5, 3,
                    (x, y) => new Color32(128, 64, 192, 83), true, srgb: true);
                RunCase(encoder, decoder, directory, report, size, "srgb_to_linear", 5, 3,
                    (x, y) => new Color32(128, 64, 192, 83), true, inputSrgb: true);
            }
            RequireThrows<ArgumentNullException>(() => encoder.Encode(null));
            var input = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                RequireThrows<ArgumentOutOfRangeException>(() => encoder.Encode(input, (AstcBlockSize)7));
                var image = encoder.Encode(input);
                if (!SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_4x4))
                    RequireThrows<NotSupportedException>(() => image.CreateTexture());
            }
            finally { Object.DestroyImmediate(input); }
            report.passed = true;
            File.WriteAllText(Path.Combine(Root, quality == AstcQuality.Fast ? "report-fast.json" : quality == AstcQuality.HighQuality ? "report-high.json" : "report.json"), JsonUtility.ToJson(report, true));
            return $"PASS: {report.cases} GPU encodes and Arm decodes; {report.comparedChannels} channels; maximum decoder difference {report.maxDecoderDifference}/255.";
        }

        static void RunCase(AstcEncoder encoder, string decoder, string directory, Report report, AstcBlockSize size,
            string name, int width, int height, Func<int, int, Color32> pixel, bool exact,
            bool srgb = false, bool inputSrgb = false, double minimumPsnr = 0)
        {
            var source = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var colors = new Color32[width * height];
            var expectedSource = new Color32[colors.Length];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                colors[i] = pixel(x, y);
                Color value = colors[i];
                if (srgb != inputSrgb)
                {
                    value.r = srgb ? Mathf.LinearToGammaSpace(value.r) : Mathf.GammaToLinearSpace(value.r);
                    value.g = srgb ? Mathf.LinearToGammaSpace(value.g) : Mathf.GammaToLinearSpace(value.g);
                    value.b = srgb ? Mathf.LinearToGammaSpace(value.b) : Mathf.GammaToLinearSpace(value.b);
                }
                expectedSource[i] = value;
            }
            try
            {
                source.SetPixels32(colors);
                source.Apply(false, true); // Encoding must not depend on a CPU-readable source.
                var image = encoder.Encode(source, size, srgb, inputSrgb, activeQuality);
                int expectedBytes = ((width + (int)size - 1) / (int)size) * ((height + (int)size - 1) / (int)size) * 16;
                Require(image.Data.Length == expectedBytes, "Incorrect payload size.");
                string stem = Path.Combine(directory, $"{name}_{(int)size}");
                File.WriteAllBytes(stem + ".astc", image.ToAstcFile());
                File.WriteAllBytes(stem + ".source.rgba", Pack(expectedSource));
                var start = new ProcessStartInfo(decoder, $"{(srgb ? "-ds" : "-dl")} \"{stem}.astc\" \"{stem}.png\" -silent")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using (var process = Process.Start(start))
                {
                    if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Arm decoder timed out."); }
                    Require(process.ExitCode == 0, "Arm decoder failed: " + process.StandardError.ReadToEnd());
                }
                var png = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                Color32[] decoded;
                try
                {
                    Require(png.LoadImage(File.ReadAllBytes(stem + ".png")), "Cannot load decoded PNG.");
                    Require(png.width == width && png.height == height, "Decoded dimensions differ.");
                    decoded = png.GetPixels32();
                }
                finally { Object.DestroyImmediate(png); }

                double squaredError = 0;
                int maxDifference = 0;
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    // astcenc uses top-to-bottom file rows. Unity's PNG loader reverses those rows.
                    Color32 actual = decoded[(height - 1 - y) * width + x];
                    Color32 scalar = AstcScalarDecoder.DecodePixel(image, x, y);
                    Color32 original = expectedSource[y * width + x];
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int difference = Math.Abs(actual[channel] - scalar[channel]);
                        maxDifference = Math.Max(maxDifference, difference);
                        Require(difference <= 1, $"{name}/{size} decoder mismatch ({x},{y}) channel {channel}: Arm={actual[channel]}, scalar={scalar[channel]}.");
                        if (exact) Require(Math.Abs(actual[channel] - original[channel]) <= 1, $"{name}/{size} constant or color-space conversion differs.");
                        double error = actual[channel] - original[channel];
                        squaredError += error * error;
                    }
                }
                double psnr = squaredError == 0 ? 120 : 10 * Math.Log10(255.0 * 255 * colors.Length * 4 / squaredError);
                Require(psnr >= minimumPsnr, $"{name}/{size} PSNR {psnr:F2} is below {minimumPsnr} dB.");
                report.results.Add(new CaseResult { name = name, blockSize = (int)size, width = width, height = height,
                    bytes = image.Data.Length, maxDecoderDifference = maxDifference, rgbaPsnr = psnr });
                report.cases++;
                report.comparedChannels += colors.Length * 4;
                report.maxDecoderDifference = Math.Max(report.maxDecoderDifference, maxDifference);
            }
            finally { Object.DestroyImmediate(source); }
        }

        public static async void RunAsync()
        {
            AsyncStatus = "running";
            var texture = new Texture2D(17, 13, TextureFormat.RGBA32, false, true);
            var rt = new RenderTexture(17, 13, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previousActive = RenderTexture.active;
            try
            {
                var colors = new Color32[17 * 13];
                for (int i = 0; i < colors.Length; i++) colors[i] = new Color(i % 17 / 16f, i / 17 / 12f, .3f, .7f);
                texture.SetPixels32(colors); texture.Apply(false, true);
                rt.Create(); Graphics.Blit(texture, rt);
                using var encoder = new AstcEncoder(AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath));
                foreach (AstcQuality quality in Enum.GetValues(typeof(AstcQuality)))
                foreach (AstcBlockSize size in Enum.GetValues(typeof(AstcBlockSize)))
                {
                    byte[] sync = encoder.Encode(rt, size, quality: quality).Data;
                    Require(sync.SequenceEqual(encoder.Encode(texture, size, quality: quality).Data), "Texture2D and RenderTexture results differ.");
                    var asyncImage = await encoder.EncodeAsync(rt, size, quality: quality);
                    Require(sync.SequenceEqual(asyncImage.Data), "Sync and async RenderTexture results differ.");
                }
                AsyncStatus = "PASS: 18 async RenderTexture encodes match synchronous encodes (all three qualities).";
            }
            catch (Exception error) { AsyncStatus = "FAIL: " + error; Debug.LogException(error); }
            finally
            {
                RenderTexture.active = previousActive;
                rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(texture);
                Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root, "async-result.txt"), AsyncStatus);
            }
        }

        public static string RunGpuBuffers()
        {
            var source = new Texture2D(17, 13, TextureFormat.RGBA32, false, true);
            try
            {
                var pixels = new Color32[17 * 13];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32((byte)(i * 17), (byte)(i * 3), (byte)(i * 11), (byte)(i * 7));
                source.SetPixels32(pixels); source.Apply();
                using var encoder = new AstcEncoder(AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath));
                foreach (AstcQuality quality in Enum.GetValues(typeof(AstcQuality)))
                foreach (AstcBlockSize size in Enum.GetValues(typeof(AstcBlockSize)))
                {
                    int columns = (source.width + (int)size - 1) / (int)size;
                    int rows = (source.height + (int)size - 1) / (int)size, stride = 16;
                    using var output = new ComputeBuffer(rows * stride, 16);
                    var words = Enumerable.Repeat(0xDEADBEEFu, output.count * 4).ToArray(); output.SetData(words);
                    encoder.EncodeGpu(source, output, size, quality: quality, rowStrideBlocks: stride);
                    output.GetData(words);
                    var packed = new byte[columns * rows * 16];
                    for (int row = 0; row < rows; row++)
                    {
                        Buffer.BlockCopy(words, row * stride * 16, packed, row * columns * 16, columns * 16);
                        for (int word = columns * 4; word < stride * 4; word++)
                            Require(words[row * stride * 4 + word] == 0xDEADBEEFu, "GPU output overwrote row padding.");
                    }
                    Require(packed.SequenceEqual(encoder.Encode(source, size, quality: quality).Data), "Padded GPU buffer differs from tight output.");
                    RequireThrows<ArgumentOutOfRangeException>(() => encoder.EncodeGpu(source, output, size, rowStrideBlocks: -1));
                }
                encoder.Dispose();
                RequireThrows<ObjectDisposedException>(() => encoder.Encode(source));
                string result = "PASS: 18 padded GPU outputs match tight output; row padding and disposal checks pass.";
                File.WriteAllText(Path.Combine(Root, "gpu-buffer-result.txt"), result);
                return result;
            }
            finally { Object.DestroyImmediate(source); }
        }

        static byte[] Pack(Color32[] colors)
        {
            var bytes = new byte[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++)
            for (int channel = 0; channel < 4; channel++) bytes[i * 4 + channel] = colors[i][channel];
            return bytes;
        }

        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static void RequireThrows<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
    }
}
