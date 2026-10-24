using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityCompute.Bc7;
using Object = UnityEngine.Object;

namespace UnityCompute.TextureCompression.Editor
{
    // All readback and CPU reference encoding in this file is verification only.
    public static class GpuAtlasValidation
    {
        static readonly string Root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/gpu-atlas"));
        public static string Status { get; private set; } = "not run";
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        [Serializable] sealed class Report
        {
            public bool passed;
            public string unity, backend, utc, error;
            public int sources, atlases, directImages, convertedImages, exactPixels, convertedPixels, maximumConversionDifference;
            public int edgeAtlases, edgePixels, releasedAtlases;
        }
        public static async void RunBatch()
        {
            Directory.CreateDirectory(Root);
            try { string blocks = CompressedAtlasValidation.RunBlocks(); await GpuAtlasCopyValidation.Run(); string gpu = await Run(); string compilers = RunCompilers() + "\n" + GpuAtlasRendererValidation.RunCompilers(Path.Combine(Root, "renderer"));
                File.WriteAllText(Path.Combine(Root, "batch-result.txt"), blocks + "\n" + gpu + "\n" + compilers); EditorApplication.Exit(0); }
            catch (Exception error) { File.WriteAllText(Path.Combine(Root, "batch-result.txt"), error.ToString()); Debug.LogException(error); EditorApplication.Exit(1); }
        }
        public static async void RunAsync() { Status = "running"; try { Status = await Run(); } catch (Exception e) { Status = e.ToString(); } }

        public static async Task<string> Run()
        {
            const string folder = "Assets/__GpuAtlasValidation";
            if (AssetDatabase.IsValidFolder(folder)) throw new InvalidOperationException("Temporary GPU atlas validation assets already exist.");
            var report = new Report { unity = Application.unityVersion, backend = SystemInfo.graphicsDeviceType.ToString(), utc = DateTime.UtcNow.ToString("O") };
            var atlases = new List<GpuTextureAtlas>(); AssetBundle bundle = null;
            Directory.CreateDirectory(Root); AssetDatabase.CreateFolder("Assets", "__GpuAtlasValidation");
            var decoder = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/TextureCompression/Editor/GpuAtlasValidation.compute");
            var convert = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Atlas/GpuAtlasConvert.compute");
            var encoder = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute");
            try
            {
                var paths = new List<string>();
                foreach (bool srgb in new[] { false, true }) for (int i = 0; i < 5; i++)
                {
                    int width = new[] { 32, 64, 128, 37, 64 }[i], height = new[] { 16, 32, 64, 29, 32 }[i];
                    var source = FrameSlicedValidation.Source(width, height); string path = $"{folder}/{(srgb ? "srgb" : "linear")}-{i}.png";
                    try { File.WriteAllBytes(path, source.EncodeToPNG()); } finally { Object.DestroyImmediate(source); }
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    importer.textureType = TextureImporterType.Default; importer.sRGBTexture = srgb;
                    importer.isReadable = false; importer.mipmapEnabled = true; importer.npotScale = TextureImporterNPOTScale.None;
                    importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true,
                        format = i < 3 ? TextureImporterFormat.BC7 : i == 3 ? TextureImporterFormat.RGBA32 : TextureImporterFormat.DXT5,
                        maxTextureSize = 2048, compressionQuality = 50 }); importer.SaveAndReimport(); paths.Add(path);
                }
                string bundleDir = Path.Combine(Root, "bundle"); Directory.CreateDirectory(bundleDir);
                var build = new AssetBundleBuild { assetBundleName = "gpu-atlas-inputs", assetNames = paths.ToArray() };
                Require(BuildPipeline.BuildAssetBundles(bundleDir, new[] { build }, BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.StandaloneWindows64) != null, "Bundle build failed.");
                bundle = AssetBundle.LoadFromFile(Path.Combine(bundleDir, build.assetBundleName)); Require(bundle != null, "Bundle load failed.");
                var textures = paths.Select(p => bundle.LoadAsset<Texture2D>(p)).ToArray();
                Require(textures.All(t => t != null && !t.isReadable), "A loaded input retained Read/Write."); report.sources = textures.Length;
                File.WriteAllLines(Path.Combine(Root, "loaded-textures.txt"), textures.Select(t => $"{t.name}: {t.width}x{t.height}, {t.graphicsFormat}, readable={t.isReadable}"));
                DiagnoseHalf(textures[3], convert, decoder);
                foreach (bool srgb in new[] { false, true }) foreach (GpuAtlasQuality quality in Enum.GetValues(typeof(GpuAtlasQuality)))
                {
                    var atlas = GpuTextureAtlas.PackTextures(textures, TextureFormat.BC7, srgb, encoder, convert, quality, maxSize: 512, paddingBlocks: 1, filterMode: FilterMode.Bilinear);
                    atlases.Add(atlas); await atlas.WaitForCompletionAsync();
                    Require(!atlas.Texture.isReadable && atlas.Texture.format == TextureFormat.BC7 && atlas.Srgb == srgb && atlas.Texture.mipmapCount == 1, "Output format differs.");
                    Require(atlas.CopiedSourceIndices.Count == 3 && atlas.ConvertedSourceIndices.Count == 7 && atlas.Entries.Count == textures.Length, "Direct/converted classification differs.");
                    foreach (var entry in atlas.Entries)
                    {
                        var source = textures[entry.SourceIndex]; var size = entry.PixelRect.size;
                        Color[] sourcePixels = Read(source, size.x, size.y, 0, decoder);
                        bool direct = atlas.CopiedSourceIndices.Contains(entry.SourceIndex);
                        Color[] expected = sourcePixels;
                        if (!direct)
                        {
                            int w = (size.x + 3) / 4 * 4, h = (size.y + 3) / 4 * 4;
                            var half = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true); var colors = new Color[w * h];
                            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) colors[y * w + x] = sourcePixels[Math.Min(y, size.y - 1) * size.x + Math.Min(x, size.x - 1)];
                            half.SetPixels(colors); half.Apply();
                            using var reference = new Bc7Encoder(encoder); Texture2D referenceTexture = null;
                            try { referenceTexture = reference.Encode(half, srgb, false, (Bc7Quality)quality).CreateTexture(); expected = Read(referenceTexture, w, h, 0, decoder); }
                            finally { Object.DestroyImmediate(half); if (referenceTexture != null) Object.DestroyImmediate(referenceTexture); }
                        }
                        CheckEntry(atlas, entry, expected, direct ? size.x : (size.x + 3) / 4 * 4, decoder, direct, report);
                    }
                    report.atlases++; report.directImages += atlas.CopiedSourceIndices.Count; report.convertedImages += atlas.ConvertedSourceIndices.Count;
                    File.WriteAllText(Path.Combine(Root, "progress.json"), JsonUtility.ToJson(report, true));
                }
                // A small matching mip must preserve its encoded edge block, without recompression.
                var small = textures.Where(t => t.format == TextureFormat.BC7 && !t.name.StartsWith("srgb", StringComparison.Ordinal)).ToArray();
                foreach (int mip in new[] { 1, 4, 5 })
                {
                    File.WriteAllText(Path.Combine(Root, "current-edge-mip.txt"), mip.ToString());
                    var atlas = GpuTextureAtlas.PackTextures(small, TextureFormat.BC7, false, maxSize: 256, mipLevel: mip, paddingBlocks: 1);
                    atlases.Add(atlas); await atlas.WaitForCompletionAsync(); Require(atlas.ConvertedSourceIndices.Count == 0, "Matching mip was recompressed.");
                    foreach (var entry in atlas.Entries)
                    {
                        var size = entry.PixelRect.size; var expected = Read(small[entry.SourceIndex], size.x, size.y, mip, decoder);
                        CheckEntry(atlas, entry, expected, size.x, decoder, true, report); report.edgePixels += size.x * size.y;
                    }
                    report.edgeAtlases++;
                }
                // Outputs remain valid after all source assets are explicitly destroyed by the loader.
                var retained = atlases.Select(a => Read(a.Texture, a.Width, a.Height, 0, decoder)).ToArray();
                bundle.Unload(true); bundle = null;
                for (int i = 0; i < atlases.Count; i++) Require(retained[i].SequenceEqual(Read(atlases[i].Texture, atlases[i].Width, atlases[i].Height, 0, decoder)), "Bundle unload changed an atlas.");
                await GpuAtlasAdditionValidation.Run(Path.Combine(bundleDir, build.assetBundleName), encoder, convert, decoder);
                await GpuAtlasEditingValidation.Run(Path.Combine(bundleDir, build.assetBundleName), encoder, convert, decoder);
                await GpuAtlasRendererValidation.Run(Path.Combine(bundleDir, build.assetBundleName), encoder, convert, decoder, Path.Combine(Root, "renderer"));
                foreach (var atlas in atlases) atlas.Dispose();
                // Let fence retirement run. A verification readback flushes submitted work, not the runtime API.
                await DrainRetirement();
                report.releasedAtlases = atlases.Count; report.passed = true;
                return $"PASS: {report.sources} unreadable mixed-format bundle textures; {report.atlases} mixed atlases; {report.directImages} unchanged + {report.convertedImages} converted images; {report.exactPixels} exact pixels; {report.convertedPixels} reference-converted pixels, max difference {report.maximumConversionDifference}/255; {report.edgeAtlases} edge-mip atlases; loader release checked.";
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                if (bundle != null) bundle.Unload(true); foreach (var atlas in atlases) atlas.Dispose();
                await DrainRetirement();
                AssetDatabase.DeleteAsset(folder); File.WriteAllText(Path.Combine(Root, "result.json"), JsonUtility.ToJson(report, true));
            }
        }
        internal static async Task DrainRetirement()
        {
            int Count(string name) => ((ICollection)typeof(GpuTextureAtlas).Assembly.GetType(name).GetField("pending", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count;
            int Pending() => Count("UnityCompute.TextureCompression.GpuAtlasRetirement") + Count("UnityCompute.Bc7.Bc7GpuRetirement");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Pending() != 0 && DateTime.UtcNow < deadline)
            { EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll(); await Task.Delay(10); }
            Require(Pending() == 0, "GPU atlas retirement did not finish.");
        }
        internal static Color[] Read(Texture texture, int width, int height, int mip, ComputeShader shader)
        {
            int kernel = shader.FindKernel("ReadLinear"); using var output = new ComputeBuffer(width * height, 16);
            shader.SetInts("_Size", width, height); shader.SetInt("_Mip", mip); shader.SetTexture(kernel, "_Source", texture); shader.SetBuffer(kernel, "_Linear", output);
            shader.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1); var pixels = new Color[width * height]; output.GetData(pixels); return pixels;
        }
        static void DiagnoseHalf(Texture2D source, ComputeShader convert, ComputeShader decoder)
        {
            int width = (source.width + 3) / 4 * 4, height = (source.height + 3) / 4 * 4;
            var original = Read(source, source.width, source.height, 0, decoder);
            var colors = new Color[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) colors[y * width + x] = original[Math.Min(y, source.height - 1) * source.width + Math.Min(x, source.width - 1)];
            var reference = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true); reference.SetPixels(colors); reference.Apply();
            var stage = new RenderTexture(new RenderTextureDescriptor(width, height) { graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true }); stage.Create();
            try
            {
                int kernel = convert.FindKernel("ConvertAndPad"); convert.SetInts("_InputSize", source.width, source.height); convert.SetInts("_OutputSize", width, height); convert.SetInt("_SourceMip", 0);
                convert.SetInt("_FirstRow", 0); convert.SetInt("_RowCount", height);
                convert.SetTexture(kernel, "_Source", source); convert.SetTexture(kernel, "_Output", stage); convert.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
                var a = Read(reference, width, height, 0, decoder); var b = Read(stage, width, height, 0, decoder); int differing = 0; float maximum = 0;
                var lines = new List<string>();
                for (int i = 0; i < a.Length; i++) for (int c = 0; c < 4; c++) if (a[i][c] != b[i][c])
                { differing++; maximum = Math.Max(maximum, Math.Abs(a[i][c] - b[i][c])); if (lines.Count < 8) lines.Add($"pixel={i}, channel={c}, source={colors[i][c]:R}, cpuHalf={a[i][c]:R}, gpuHalf={b[i][c]:R}"); }
                lines.Insert(0, $"differing={differing}, max={maximum:R}"); File.WriteAllLines(Path.Combine(Root, "half-diagnostic.txt"), lines);
                Require(differing == 0, "GPU half rounding differs from the independent CPU half reference.");
            }
            finally { Object.DestroyImmediate(reference); stage.Release(); Object.DestroyImmediate(stage); }
        }
        public static string RunCompilers()
        {
            const string source = "Packages/com.ittodo.gpu-texture-atlas/Shaders/Atlas/GpuAtlasConvert.compute", probe = "Assets/TextureCompression/Editor/GpuAtlasCompileProbe.compute";
            UnityCompute.TextureCompression.Editor.GpuAtlasProjectPaths.CopyShaderProbe(source, probe); AssetDatabase.ImportAsset(probe, ImportAssetOptions.ForceSynchronousImport);
            try
            {
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(probe);
                var compiler = typeof(ShaderUtil).GetMethod("CompileComputeShaderVariant", BindingFlags.Static | BindingFlags.NonPublic);
                var lines = new List<string>();
                foreach (var target in new[] { BuildTarget.StandaloneWindows64, BuildTarget.Android, BuildTarget.Android, BuildTarget.iOS })
                {
                    var api = target == BuildTarget.StandaloneWindows64 ? UnityEngine.Rendering.GraphicsDeviceType.Direct3D12
                        : target == BuildTarget.iOS ? UnityEngine.Rendering.GraphicsDeviceType.Metal
                        : lines.Count == 1 ? UnityEngine.Rendering.GraphicsDeviceType.Vulkan : UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3;
                    var result = (ShaderData.VariantCompileInfo)compiler.Invoke(null, new object[] { shader, shader.FindKernel("ConvertAndPad"), Array.Empty<string>(), api, target });
                    Require(result.Success && result.ShaderData != null && result.ShaderData.Length != 0 && result.Messages.Length == 0,
                        target + "/" + api + ": " + string.Join("; ", result.Messages.Select(m => m.message)));
                    lines.Add($"PASS: ConvertAndPad {target}/{api}, {result.ShaderData.Length} bytes, zero diagnostics.");
                }
                File.WriteAllLines(Path.Combine(Root, "shader-compilers.txt"), lines); return string.Join("\n", lines);
            }
            finally { AssetDatabase.DeleteAsset(probe); }
        }
        static void CheckEntry(GpuTextureAtlas atlas, CompressedAtlasEntry entry, Color[] expected, int stride, ComputeShader shader, bool exact, Report report)
        {
            int width = entry.PixelRect.width, height = entry.PixelRect.height, kernel = shader.FindKernel("SampleEntry");
            using var output = new ComputeBuffer((width + 2) * (height + 2), 4);
            shader.SetInts("_Size", width, height); shader.SetVector("_ScaleOffset", entry.UvScaleOffset); shader.SetVector("_Clamp", entry.UvClamp);
            shader.SetTexture(kernel, "_Source", atlas.Texture); shader.SetBuffer(kernel, "_Pixels", output); shader.Dispatch(kernel, (width + 9) / 8, (height + 9) / 8, 1);
            var words = new uint[output.count]; output.GetData(words);
            for (int y = 0; y < height + 2; y++) for (int x = 0; x < width + 2; x++)
            {
                Color32 reference = expected[Math.Max(0, Math.Min(height - 1, y - 1)) * stride + Math.Max(0, Math.Min(width - 1, x - 1))];
                uint word = words[y * (width + 2) + x];
                for (int channel = 0; channel < 4; channel++)
                {
                    int delta = Math.Abs((int)((word >> (channel * 8)) & 255) - reference[channel]);
                    if (!exact) report.maximumConversionDifference = Math.Max(report.maximumConversionDifference, delta);
                    Require(delta <= (exact ? 0 : 1), $"{entry.Name}, exact={exact}, ({x},{y}), channel={channel}, difference={delta}.");
                }
                if (exact) report.exactPixels++; else report.convertedPixels++;
            }
        }
    }
}
