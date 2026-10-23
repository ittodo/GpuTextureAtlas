using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;
#if !GPU_ATLAS_PLAYER_PROBE
using UnityEditor;
#endif

#if GPU_ATLAS_PLAYER_PROBE
namespace UnityCompute.TextureCompression.Verification
#else
namespace UnityCompute.TextureCompression.Editor
#endif
{
    // Verification only: upload known compressed blocks and compare every decoded block texel,
    // including texels outside the source's logical extent. The atlas never receives CPU bytes.
    public static class GpuAtlasCopyValidation
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public string unity, backend, utc, error, currentFormat;
            public string[] testedFormats, skippedFormats;
            public int cases, partialInputs, pixels, additions, repacks, rejections, currentMip;
        }
        sealed class Source
        {
            internal Texture2D texture;
            internal readonly List<byte[]> mips = new List<byte[]>();
        }
        sealed class Expected { internal Color[] pixels; internal int width, height; }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
#if !GPU_ATLAS_PLAYER_PROBE
        static string Output => Path.GetFullPath(Path.Combine(Application.dataPath,
            "../../.validation/gpu-atlas/copytexture", SystemInfo.graphicsDeviceType.ToString()));

        public static async void RunBatch()
        {
            int exit = 0;
            try { await Run(); }
            catch (Exception error) { Debug.LogException(error); exit = 1; }
            // Flush the Editor's GraphicsTexture finalizers while its main-thread queue still exists.
            GC.Collect(); GC.WaitForPendingFinalizers();
            EditorApplication.QueuePlayerLoopUpdate(); await Task.Delay(100);
            EditorApplication.Exit(exit);
        }

        public static Task Run() => Run(AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/TextureCompression/Editor/GpuAtlasValidation.compute"), Output);
#endif

        public static async Task Run(ComputeShader decoder, string output, Action<string> progress = null)
        {
            var report = new Report { unity = Application.unityVersion, backend = SystemInfo.graphicsDeviceType.ToString(), utc = DateTime.UtcNow.ToString("O") };
            var tested = new List<string>(); var skipped = new List<string>();
            var formats = new[] { TextureFormat.BC7, TextureFormat.ASTC_4x4, TextureFormat.ASTC_5x5,
                TextureFormat.ASTC_6x6, TextureFormat.ASTC_8x8, TextureFormat.ASTC_10x10, TextureFormat.ASTC_12x12 };
            try
            {
                foreach (var format in formats) foreach (bool srgb in new[] { false, true })
                {
                    var graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(format, srgb);
                    if (!SystemInfo.IsFormatSupported(graphicsFormat, GraphicsFormatUsage.Sample)) { skipped.Add(graphicsFormat.ToString()); continue; }
                    tested.Add(graphicsFormat.ToString());
                    report.currentFormat = graphicsFormat.ToString();
                    int block = format == TextureFormat.BC7 || format == TextureFormat.ASTC_4x4 ? 4 :
                        format == TextureFormat.ASTC_5x5 ? 5 : format == TextureFormat.ASTC_6x6 ? 6 :
                        format == TextureFormat.ASTC_8x8 ? 8 : format == TextureFormat.ASTC_10x10 ? 10 : 12;
                    var sources = new List<Source>();
                    try
                    {
                        sources.Add(CreateSource(block * 19, block * 15, format, srgb, 521));
                        sources.Add(CreateSource(block * 20, block * 16, format, srgb, 934));
                        sources.Add(CreateSource(block * 3, block * 5, format, srgb, 213));
                        int mipCount = sources.Max(s => s.mips.Count);
                        for (int mip = 0; mip < mipCount; mip++)
                        {
                            report.currentMip = mip; progress?.Invoke($"CopyTexture {graphicsFormat}, mip {mip}/{mipCount - 1}");
                            var inputs = sources.Where(s => s.mips.Count > mip).ToArray();
                            var expected = new Dictionary<int, Expected>();
                            GpuTextureAtlas atlas = null;
                            try
                            {
                                for (int i = 0; i < inputs.Length; i++)
                                {
                                    expected.Add(i, Reference(inputs[i], mip, block, decoder));
                                    int w = Math.Max(1, inputs[i].texture.width >> mip), h = Math.Max(1, inputs[i].texture.height >> mip);
                                    if (w % block != 0 || h % block != 0) report.partialInputs++;
                                }
                                atlas = await GpuTextureAtlas.PackTexturesAsync(inputs.Select(s => s.texture).ToArray(), format, srgb,
                                    maxSize: block * 128, paddingBlocks: 1, mipLevel: mip);
                                Check(atlas, expected, decoder, report);
                                int id = atlas.NextSourceIndex;
                                Require(atlas.Remove(0), "Remove failed."); expected.Remove(0);
                                await atlas.AddAsync(inputs[0].texture, mipLevel: mip); report.additions++;
                                expected.Add(id, Reference(inputs[0], mip, block, decoder)); Check(atlas, expected, decoder, report);
                                id = atlas.NextSourceIndex;
                                await atlas.AddAsync(sources[1].texture); report.additions++;
                                expected.Add(id, Reference(sources[1], 0, block, decoder)); Check(atlas, expected, decoder, report);
                                await atlas.RepackAsync(); report.repacks++; Check(atlas, expected, decoder, report);

                                // A missing conversion shader must not damage an already published atlas.
                                var other = new Texture2D(4, 4, TextureFormat.RGBA32, false, !srgb); other.Apply(false, true);
                                var published = atlas.Texture; int version = atlas.Version, nextId = atlas.NextSourceIndex;
                                try
                                {
                                    bool rejected = false;
                                    try { await atlas.AddAsync(new[] { inputs[0].texture, other }, mipLevel: 0); }
                                    catch (ArgumentException) { rejected = true; report.rejections++; }
                                    Require(rejected && atlas.Texture == published && atlas.Version == version && atlas.NextSourceIndex == nextId && !atlas.IsUpdating,
                                        "Rejected input changed the atlas.");
                                }
                                finally { Destroy(other); }
                                Check(atlas, expected, decoder, report);
                                atlas.Remove(atlas.Entries.Select(e => e.SourceIndex).ToArray()); expected.Clear();
                                await atlas.RepackAsync(); report.repacks++;
                                Require(atlas.Width == block && atlas.Height == block, "Empty repack size differs.");
                                id = atlas.NextSourceIndex;
                                await atlas.AddAsync(inputs[0].texture, mipLevel: mip); report.additions++;
                                expected.Add(id, Reference(inputs[0], mip, block, decoder)); Check(atlas, expected, decoder, report);
                                report.cases++;
                            }
                            finally { atlas?.Dispose(); await DrainRetirement(); }
                        }
                    }
                    finally { foreach (var source in sources) Destroy(source.texture); }
                }
                Require(report.cases != 0 && report.partialInputs != 0, "No supported partial-block cases ran.");
                report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                report.testedFormats = tested.ToArray(); report.skippedFormats = skipped.ToArray();
                Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(report, true));
            }
        }

        static Source CreateSource(int width, int height, TextureFormat format, bool srgb, int seed)
        {
            var source = new Source { texture = new Texture2D(width, height, format, true, !srgb) { name = $"copy-{format}-{width}x{height}-{srgb}" } };
            try
            {
                Require(source.texture.graphicsFormat == GraphicsFormatUtility.GetGraphicsFormat(format, srgb), "Source format was substituted.");
                var random = new System.Random(seed);
                for (int mip = 0; mip < source.texture.mipmapCount; mip++)
                {
                    var bytes = new byte[source.texture.GetPixelData<byte>(mip).Length]; random.NextBytes(bytes);
                    for (int offset = 0; offset < bytes.Length; offset += 16)
                    {
                        if (format == TextureFormat.BC7) bytes[offset] = (byte)((bytes[offset] & 128) | 64); // Valid mode 6.
                        else { bytes[offset] = 0xfc; bytes[offset + 1] = 0xfd; for (int i = 2; i < 8; i++) bytes[offset + i] = 0xff; } // LDR void extent.
                    }
                    source.mips.Add(bytes); source.texture.SetPixelData(bytes, mip);
                }
                source.texture.Apply(false, true); Require(!source.texture.isReadable, "Source retained CPU pixels."); return source;
            }
            catch { Destroy(source.texture); throw; }
        }

        static Expected Reference(Source source, int mip, int block, ComputeShader decoder)
        {
            int width = (Math.Max(1, source.texture.width >> mip) + block - 1) / block * block;
            int height = (Math.Max(1, source.texture.height >> mip) + block - 1) / block * block;
            var texture = new Texture2D(width, height, source.texture.graphicsFormat, TextureCreationFlags.None);
            try
            {
                texture.LoadRawTextureData(source.mips[mip]); texture.Apply(false, true);
                return new Expected { width = width, height = height, pixels = Read(texture, width, height, 0, decoder) };
            }
            finally { Destroy(texture); }
        }

        static void Check(GpuTextureAtlas atlas, Dictionary<int, Expected> expected, ComputeShader decoder, Report report)
        {
            Require(!atlas.Texture.isReadable && atlas.Entries.Count == expected.Count && atlas.ConvertedSourceIndices.Count == 0,
                "Copy-only atlas metadata differs.");
            var pixels = Read(atlas.Texture, atlas.Width, atlas.Height, 0, decoder);
            foreach (var entry in atlas.Entries)
            {
                var reference = expected[entry.SourceIndex]; var rect = entry.PackedRect;
                Require(rect.width == reference.width && rect.height == reference.height, "Packed block dimensions differ.");
                for (int y = 0; y < rect.height; y++) for (int x = 0; x < rect.width; x++)
                {
                    Require(pixels[(rect.y + y) * atlas.Width + rect.x + x].Equals(reference.pixels[y * rect.width + x]),
                        $"Compressed block changed at entry {entry.SourceIndex}, ({x}, {y}).");
                    report.pixels++;
                }
            }
        }

        internal static Color[] Read(Texture texture, int width, int height, int mip, ComputeShader shader)
        {
            int kernel = shader.FindKernel("ReadLinear"); using var output = new ComputeBuffer(width * height, 16);
            shader.SetInts("_Size", width, height); shader.SetInt("_Mip", mip); shader.SetTexture(kernel, "_Source", texture); shader.SetBuffer(kernel, "_Linear", output);
            shader.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1); var pixels = new Color[width * height]; output.GetData(pixels); return pixels;
        }
        internal static async Task DrainRetirement()
        {
            int Pending(string name)
            {
                var type = typeof(GpuTextureAtlas).Assembly.GetType(name);
                var field = type.GetField("pending", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var owner = field.IsStatic ? null : type.GetField("instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
                return !field.IsStatic && owner == null ? 0 : ((System.Collections.ICollection)field.GetValue(owner)).Count;
            }
            int Count() => Pending("UnityCompute.TextureCompression.GpuAtlasRetirement") + Pending("UnityCompute.Astc.AstcGpuRetirement") + Pending("UnityCompute.Bc7.Bc7GpuRetirement");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Count() != 0 && DateTime.UtcNow < deadline)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
                await Task.Delay(10);
            }
            Require(Count() == 0, "GPU resources did not retire.");
        }
        static void Destroy(Object value)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) { Object.DestroyImmediate(value); return; }
#endif
            Object.Destroy(value);
        }
    }
}
