using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityCompute.Bc7;
using Object = UnityEngine.Object;

namespace UnityCompute.TextureCompression.Editor
{
    // Verification only. Bundle building, readback and full byte comparisons never run in the packing API.
    public static class CompressedAtlasValidation
    {
        static readonly string Root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/compressed-atlas"));
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } catch (NotSupportedException) { return; } throw new Exception("Invalid input was accepted."); }
        static readonly TextureFormat[] Formats = { TextureFormat.BC7, TextureFormat.ASTC_4x4, TextureFormat.ASTC_5x5,
            TextureFormat.ASTC_6x6, TextureFormat.ASTC_8x8, TextureFormat.ASTC_10x10, TextureFormat.ASTC_12x12 };
        static readonly int[] BlockSizes = { 4, 4, 5, 6, 8, 10, 12 };

        public static void RunBatch()
        {
            Directory.CreateDirectory(Root);
            try
            {
                File.WriteAllText(Path.Combine(Root, "batch-result.txt"), RunBlocks() + "\n" + RunAssetBundle());
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Root, "batch-result.txt"), error.ToString());
                Debug.LogException(error); EditorApplication.Exit(1);
            }
        }

        public static string RunBlocks()
        {
            int cases = 0, images = 0; long bytes = 0;
            var random = new System.Random(4371);
            foreach (var format in Formats) foreach (bool srgb in new[] { false, true }) foreach (int padding in new[] { 0, 1 })
            {
                int block = BlockSizes[Array.IndexOf(Formats, format)];
                var sources = new List<CompressedAtlasSource>();
                var sizes = new List<Vector2Int> { new Vector2Int(1, 1), new Vector2Int(101, 57), new Vector2Int(250, 130), new Vector2Int(512, 256) };
                for (int i = 0; i < 16; i++) sizes.Add(new Vector2Int(random.Next(1, 140), random.Next(1, 140)));
                for (int i = 0; i < sizes.Count; i++)
                {
                    int w = sizes[i].x, h = sizes[i].y;
                    var data = new byte[((w + block - 1) / block) * ((h + block - 1) / block) * 16]; random.NextBytes(data);
                    sources.Add(new CompressedAtlasSource("image-" + i, w, h, format, srgb, data,
                        i % 2 == 0 ? CompressedRowOrder.BottomToTop : CompressedRowOrder.TopToBottom));
                }
                var originals = sources.Select(s => (byte[])s.Blocks.Clone()).ToArray();
                var atlas = CompressedAtlas.Pack(sources, 1024, padding);
                Check(atlas, sources, padding, ref bytes);
                var again = CompressedAtlas.Pack(sources, 1024, padding);
                Require(atlas.Width == again.Width && atlas.Height == again.Height && atlas.Data.SequenceEqual(again.Data), "Placement is not deterministic.");
                for (int i = 0; i < sources.Count; i++) Require(originals[i].SequenceEqual(sources[i].Blocks), "Packing modified an input.");
                byte[] file = atlas.ToFile(); int header = format == TextureFormat.BC7 ? 148 : 16;
                Require(file.Length == header + atlas.Data.Length && file.Skip(header).SequenceEqual(atlas.Data), "File export modified blocks.");
                cases++; images += sources.Count;
            }
            var one = new CompressedAtlasSource("one", 1, 1, TextureFormat.BC7, false, new byte[16]);
            Reject(() => CompressedAtlas.Pack(null)); Reject(() => CompressedAtlas.Pack(Array.Empty<CompressedAtlasSource>()));
            Reject(() => CompressedAtlas.Pack(new CompressedAtlasSource[] { null }));
            Reject(() => CompressedAtlas.Pack(new[] { one }, 3)); Reject(() => CompressedAtlas.Pack(new[] { one }, 16, -1));
            Reject(() => CompressedAtlas.Pack(new[] { one }, 4, 1)); Reject(() => CompressedAtlas.Pack(new[] { one, one }, 4));
            Reject(() => new CompressedAtlasSource("bad", 0, 1, TextureFormat.BC7, false, new byte[16]));
            Reject(() => new CompressedAtlasSource("bad", 5, 1, TextureFormat.BC7, false, new byte[16]));
            Reject(() => new CompressedAtlasSource("bad", 1, 1, TextureFormat.RGBA32, false, new byte[4]));
            Reject(() => CompressedAtlas.Pack(new[] { one, new CompressedAtlasSource("sRGB", 1, 1, TextureFormat.BC7, true, new byte[16]) }));
            Reject(() => CompressedAtlas.Pack(new[] { one, new CompressedAtlasSource("ASTC", 1, 1, TextureFormat.ASTC_4x4, false, new byte[16]) }));
            string result = $"PASS: {cases} atlases, {images} variable-size inputs, {bytes} copied bytes; exact blocks, non-overlap, UVs, gutters, determinism and 12 invalid inputs.";
            Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "blocks.txt"), result); return result;
        }

        static void Check(CompressedAtlas atlas, IReadOnlyList<CompressedAtlasSource> sources, int padding, ref long bytes)
        {
            int block = atlas.BlockSize, columns = atlas.Width / block, rows = atlas.Height / block;
            Require(atlas.Width % block == 0 && atlas.Height % block == 0 && atlas.Data.Length == columns * rows * 16, "Atlas dimensions are not block aligned.");
            var occupied = new bool[columns * rows]; var reserved = new bool[occupied.Length];
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i]; var entry = atlas.Entries[i]; var rect = entry.PackedRect;
                Require(entry.SourceIndex == i && entry.Name == source.Name && entry.PixelRect.size == new Vector2Int(source.Width, source.Height), "Original metadata was lost.");
                int x0 = rect.x / block, y0 = rect.y / block, w = rect.width / block, h = rect.height / block;
                Require(rect.x % block == 0 && rect.y % block == 0 && x0 >= padding && y0 >= padding && x0 + w + padding <= columns && y0 + h + padding <= rows, "Placement is out of bounds.");
                for (int y = y0 - padding; y < y0 + h + padding; y++) for (int x = x0 - padding; x < x0 + w + padding; x++)
                { int at = y * columns + x; Require(!reserved[at], "Reserved rectangles overlap."); reserved[at] = true; }
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int at = (y0 + y) * columns + x0 + x; occupied[at] = true;
                    for (int b = 0; b < 16; b++) { Require(atlas.Data[at * 16 + b] == source.Blocks[(y * w + x) * 16 + b], "A compressed byte changed."); bytes++; }
                }
                foreach (int y in new[] { 0, source.Height / 2, source.Height - 1 }) foreach (int x in new[] { 0, source.Width / 2, source.Width - 1 })
                {
                    var uv = entry.MapUv(new Vector2((x + .5f) / source.Width, (y + .5f) / source.Height));
                    int physicalY = source.RowOrder == CompressedRowOrder.TopToBottom ? source.Height - 1 - y : y;
                    Require(Mathf.FloorToInt(uv.x * atlas.Width) == rect.x + x && Mathf.FloorToInt(uv.y * atlas.Height) == rect.y + physicalY, "UV points at a different source texel.");
                }
                foreach (var uv in new[] { entry.MapUv(new Vector2(-1, -1)), entry.MapUv(new Vector2(2, 2)) })
                    Require(uv.x >= entry.UvClamp.x && uv.y >= entry.UvClamp.y && uv.x <= entry.UvClamp.z && uv.y <= entry.UvClamp.w, "UV clamp escaped valid pixels.");
            }
            for (int i = 0; i < occupied.Length; i++) if (!occupied[i])
                for (int b = 0; b < 16; b++)
                {
                    byte expected = atlas.Format == TextureFormat.BC7 ? (b == 0 ? (byte)0x40 : (byte)0)
                        : b == 0 ? (byte)0xFC : b == 1 ? (byte)0xFD : b < 8 ? (byte)0xFF : (byte)0;
                    Require(atlas.Data[i * 16 + b] == expected, "An empty block is not valid transparent black.");
                }
        }

        [Serializable] sealed class BundleReport
        {
            public bool passed;
            public string unity, backend, utc;
            public int textures, atlases, pixels, sourceMipLevels;
            public string[] formats;
        }

        public static string RunAssetBundle()
        {
            const string folder = "Assets/__CompressedAtlasValidation";
            if (AssetDatabase.IsValidFolder(folder)) throw new InvalidOperationException("The temporary validation folder already exists.");
            Directory.CreateDirectory(Root); AssetDatabase.CreateFolder("Assets", "__CompressedAtlasValidation");
            AssetBundle bundle = null;
            var outputs = new List<Texture2D>();
            var report = new BundleReport { unity = Application.unityVersion, backend = SystemInfo.graphicsDeviceType.ToString(), utc = DateTime.UtcNow.ToString("O") };
            try
            {
                var paths = new List<string>();
                foreach (bool srgb in new[] { false, true }) for (int i = 0; i < 3; i++)
                {
                    int width = new[] { 32, 64, 128 }[i], height = new[] { 16, 32, 64 }[i];
                    var source = FrameSlicedValidation.Source(width, height);
                    string path = $"{folder}/{(srgb ? "srgb" : "linear")}-{i}.png";
                    try { File.WriteAllBytes(path, source.EncodeToPNG()); } finally { Object.DestroyImmediate(source); }
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    importer.textureType = TextureImporterType.Default; importer.sRGBTexture = srgb;
                    importer.isReadable = true; importer.mipmapEnabled = true; importer.npotScale = TextureImporterNPOTScale.None;
                    importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true,
                        format = TextureImporterFormat.BC7, maxTextureSize = 2048, compressionQuality = 50 });
                    importer.SaveAndReimport(); paths.Add(path);
                }
                string bundleDir = Path.Combine(Root, "bundle"); Directory.CreateDirectory(bundleDir);
                var build = new AssetBundleBuild { assetBundleName = "compressed-atlas-test", assetNames = paths.ToArray() };
                var manifest = BuildPipeline.BuildAssetBundles(bundleDir, new[] { build }, BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.StandaloneWindows64);
                Require(manifest != null, "AssetBundle build failed.");
                bundle = AssetBundle.LoadFromFile(Path.Combine(bundleDir, build.assetBundleName)); Require(bundle != null, "Cannot load test bundle.");
                var all = paths.Select(p => bundle.LoadAsset<Texture2D>(p)).ToArray();
                report.formats = all.Select(t => t.format.ToString()).ToArray();
                File.WriteAllLines(Path.Combine(Root, "loaded-textures.txt"), all.Select(t => t == null ? "null"
                    : $"{t.name}: {t.width}x{t.height}, {t.format}, readable={t.isReadable}, mips={t.mipmapCount}"));
                Require(all.All(t => t != null && t.format == TextureFormat.BC7 && t.isReadable && t.mipmapCount > 1), "Bundle did not preserve BC7 CPU blocks and mipmaps. See loaded-textures.txt.");
                report.sourceMipLevels = all.Sum(t => t.mipmapCount);
                foreach (bool srgb in new[] { false, true })
                {
                    var textures = all.Where(t => t.name.StartsWith(srgb ? "srgb" : "linear", StringComparison.Ordinal)).ToArray();
                    var sources = textures.Select(t => CompressedAtlasSource.FromTexture(t)).ToArray();
                    Require(sources.Length == 3 && sources.All(s => s.Srgb == srgb), "Loaded color space differs.");
                    Require(textures.All(t => t.GetRawTextureData<byte>().Length > t.GetPixelData<byte>(0).Length), "The fixture needs multiple mip levels.");
                    var atlas = CompressedAtlas.PackTextures(textures, 512, 1); long bytes = 0; Check(atlas, sources, 1, ref bytes);
                    var texture = atlas.CreateTexture(); outputs.Add(texture);
                    Require(texture.format == TextureFormat.BC7 && !texture.isReadable && texture.mipmapCount == 1, "Output changed compression/readability/mip count.");
                    var decoded = UnityCompute.Bc7.Editor.Bc7HardwareDecoder.Decode(new Bc7Image(atlas.Width, atlas.Height, false, atlas.Data));
                    for (int i = 0; i < sources.Length; i++)
                    {
                        var source = sources[i]; var expected = UnityCompute.Bc7.Editor.Bc7HardwareDecoder.Decode(new Bc7Image(source.Width, source.Height, false, source.Blocks));
                        var entry = atlas.Entries[i];
                        for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
                        {
                            var uv = entry.MapUv(new Vector2((x + .5f) / source.Width, (y + .5f) / source.Height));
                            var actual = decoded[Mathf.FloorToInt(uv.y * atlas.Height) * atlas.Width + Mathf.FloorToInt(uv.x * atlas.Width)];
                            Require(actual.Equals(expected[y * source.Width + x]), "GPU decoding differs after packing."); report.pixels++;
                        }
                    }
                    // The selected mip is packed alone; the lower mip chain must not leak into its block payload.
                    var mipSources = textures.Select(t => CompressedAtlasSource.FromTexture(t, mipLevel: 1)).ToArray();
                    var mipAtlas = CompressedAtlas.PackTextures(textures, 512, mipLevel: 1); Check(mipAtlas, mipSources, 0, ref bytes);
                    report.atlases += 2;
                }
                Reject(() => CompressedAtlasSource.FromTexture(outputs[0]));
                bundle.Unload(true); bundle = null;
                Require(outputs.All(t => t != null && t.format == TextureFormat.BC7), "Unloading the source bundle invalidated an output.");
                // Actual compressed data remains independently available after releasing the loader's assets.
                report.textures = all.Length; report.passed = true;
                File.WriteAllText(Path.Combine(Root, "assetbundle.json"), JsonUtility.ToJson(report, true));
                return $"PASS: {report.textures} TextureImporter BC7 textures loaded from AssetBundle; {report.atlases} atlases; {report.pixels} GPU-decoded pixels match; mip selection, sRGB, read/write and bundle release checked.";
            }
            finally
            {
                if (bundle != null) bundle.Unload(true);
                foreach (var texture in outputs) if (texture != null) Object.DestroyImmediate(texture);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
