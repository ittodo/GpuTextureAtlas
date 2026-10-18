using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityCompute.Bc7;
using Object = UnityEngine.Object;

namespace UnityCompute.TextureCompression.Editor
{
    // Test-only readback and independent conversion references. Never called by the runtime API.
    internal static class GpuAtlasAdditionValidation
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public string error;
            public int geometryCases, atlases, additions, growths, inPlace, exactPixels, convertedPixels,
                maximumDifference, rejected, concurrentRejected, disposedInFlight;
        }
        sealed class Expected { internal Color[] pixels; internal int stride; internal bool converted; }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task Reject(Func<Task> action, Report report)
        {
            try { await action(); }
            catch (ArgumentException) { report.rejected++; return; }
            catch (InvalidOperationException) { report.rejected++; return; }
            throw new InvalidOperationException("Invalid addition was accepted.");
        }
        static Color[] Read(Texture texture, int w, int h, int mip, ComputeShader decoder)
            => GpuAtlasValidation.Read(texture, w, h, mip, decoder);
        static Texture2D Find(Texture2D[] textures, bool srgb, int index)
            => textures.Single(t => t.name == (srgb ? "srgb-" : "linear-") + index);

        internal static async Task Run(string bundlePath, ComputeShader encoder, ComputeShader convert, ComputeShader decoder)
        {
            var report = new Report(); AssetBundle bundle = null; GpuTextureAtlas atlas = null;
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/gpu-atlas/addition.json"));
            try
            {
                report.geometryCases = CheckGeometry();
                foreach (bool srgb in new[] { false, true }) foreach (GpuAtlasQuality quality in Enum.GetValues(typeof(GpuAtlasQuality)))
                {
                    bundle = AssetBundle.LoadFromFile(bundlePath); Require(bundle != null, "Cannot reload addition inputs.");
                    var inputs = bundle.LoadAllAssets<Texture2D>(); var seed = Find(inputs, srgb, 0);
                    var expected = new List<Expected> { Reference(seed, 0, srgb, quality, encoder, decoder) };
                    atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { seed }, TextureFormat.BC7, srgb, maxSize: 512, paddingBlocks: 1, filterMode: FilterMode.Bilinear);
                    bundle.Unload(true); bundle = null; Require(seed == null, "Initial source was not released.");
                    bundle = AssetBundle.LoadFromFile(bundlePath); inputs = bundle.LoadAllAssets<Texture2D>();
                    var oldTexture = atlas.Texture; var oldEntry = atlas.Entries[0]; var large = Find(inputs, srgb, 1);
                    expected.Add(Reference(large, 0, srgb, quality, encoder, decoder));
                    var adding = atlas.AddAsync(large);
                    if (!adding.IsCompleted)
                    {
                        Require(atlas.IsUpdating && atlas.Entries.Count == 1 && atlas.Texture == oldTexture, "Addition published before completion.");
                        await Reject(() => atlas.AddAsync(large), report); report.concurrentRejected++;
                    }
                    await adding; report.additions++; report.growths++;
                    Require(atlas.Texture != oldTexture && atlas.Version == 2 && !atlas.IsUpdating, "Required growth did not publish.");
                    Require(atlas.Entries[0].PackedRect == oldEntry.PackedRect && atlas.Entries[0].UvScaleOffset != oldEntry.UvScaleOffset, "Growth moved old pixels or failed to refresh UVs.");
                    CheckEntries(atlas, expected, decoder, report);
                    var sameTexture = atlas.Texture; var stableEntry = atlas.Entries[0];
                    var tiny = Find(inputs, srgb, 0); expected.Add(Reference(tiny, 5, srgb, quality, encoder, decoder));
                    await atlas.AddAsync(tiny, mipLevel: 5); report.additions++; report.inPlace++;
                    Require(atlas.Texture == sameTexture && ReferenceEquals(atlas.Entries[0], stableEntry), "An available hole did not preserve texture and UVs.");
                    Require(atlas.Entries[2].PixelRect.size == Vector2Int.one, "Small source mip was lost.");
                    CheckEntries(atlas, expected, decoder, report);
                    var mixed = new[] { Find(inputs, srgb, 3), Find(inputs, !srgb, 0), Find(inputs, srgb, 4) };
                    foreach (var texture in mixed) expected.Add(Reference(texture, 0, srgb, quality, encoder, decoder));
                    var beforeRects = atlas.Entries.Select(e => e.PackedRect).ToArray(); oldTexture = atlas.Texture;
                    await atlas.AddAsync(mixed, encoder, convert, quality); report.additions++;
                    if (atlas.Texture == oldTexture) report.inPlace++; else report.growths++;
                    Require(atlas.Entries.Count == 6 && atlas.Version == 4 && atlas.MaxSize == 512 && atlas.PaddingBlocks == 1, "Addition metadata differs.");
                    Require(atlas.CopiedSourceIndices.SequenceEqual(new[] { 0, 1, 2 }) && atlas.ConvertedSourceIndices.SequenceEqual(new[] { 3, 4, 5 }), "Global source classification differs.");
                    for (int i = 0; i < beforeRects.Length; i++) Require(atlas.Entries[i].PackedRect == beforeRects[i], "Mixed addition moved an old image.");
                    CheckEntries(atlas, expected, decoder, report);
                    var before = Read(atlas.Texture, atlas.Width, atlas.Height, 0, decoder); int version = atlas.Version;
                    await Reject(() => atlas.AddAsync(Array.Empty<Texture2D>()), report);
                    await Reject(() => atlas.AddAsync((IReadOnlyList<Texture2D>)null), report);
                    await Reject(() => atlas.AddAsync(new Texture2D[] { null }), report);
                    await Reject(() => atlas.AddAsync(mixed), report); // Conversion shaders are required.
                    await Reject(() => atlas.AddAsync(large, mipLevel: 99), report);
                    await Reject(() => atlas.AddAsync(large, quality: (GpuAtlasQuality)99), report);
                    await Reject(() => atlas.AddAsync(Enumerable.Repeat(large, 256).ToArray()), report);
                    Require(atlas.Version == version && !atlas.IsUpdating && before.SequenceEqual(Read(atlas.Texture, atlas.Width, atlas.Height, 0, decoder)), "Rejected input changed the published atlas.");
                    // A valid addition still works after rejection; duplicate names retain distinct global indices.
                    expected.Add(Reference(large, 0, srgb, quality, encoder, decoder)); oldTexture = atlas.Texture;
                    await atlas.AddAsync(large); report.additions++;
                    if (atlas.Texture == oldTexture) report.inPlace++; else report.growths++;
                    CheckEntries(atlas, expected, decoder, report);
                    before = Read(atlas.Texture, atlas.Width, atlas.Height, 0, decoder);
                    bundle.Unload(true); bundle = null;
                    Require(before.SequenceEqual(Read(atlas.Texture, atlas.Width, atlas.Height, 0, decoder)), "Source release changed an extended atlas.");
                    atlas.Dispose(); await Reject(() => atlas.AddAsync(Array.Empty<Texture2D>()), report); atlas = null; report.atlases++;
                    await GpuAtlasValidation.DrainRetirement();
                }
                // Dispose while a converted addition is pending. The source bundle remains alive until the task finishes.
                bundle = AssetBundle.LoadFromFile(bundlePath); var last = bundle.LoadAllAssets<Texture2D>();
                atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { Find(last, false, 0) }, TextureFormat.BC7, false, maxSize: 512);
                var pending = atlas.AddAsync(new[] { Find(last, true, 3), Find(last, false, 2) }, encoder, convert, GpuAtlasQuality.HighQuality);
                bool inFlight = !pending.IsCompleted; atlas.Dispose();
                if (inFlight) { await Reject(() => pending, report); report.disposedInFlight++; } else await pending;
                atlas = null; bundle.Unload(true); bundle = null; await GpuAtlasValidation.DrainRetirement();
                Require(report.concurrentRejected != 0 && report.disposedInFlight != 0, "The asynchronous lifetime cases did not execute.");
                report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                atlas?.Dispose(); if (bundle != null) bundle.Unload(true);
                await GpuAtlasValidation.DrainRetirement(); File.WriteAllText(output, JsonUtility.ToJson(report, true));
            }
        }

        static Expected Reference(Texture2D source, int mip, bool srgb, GpuAtlasQuality quality, ComputeShader encoder, ComputeShader decoder)
        {
            var desc = source.graphicsTexture.descriptor; int w = Math.Max(1, desc.width >> mip), h = Math.Max(1, desc.height >> mip);
            var pixels = Read(source, w, h, mip, decoder);
            if (desc.format == UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.BC7, srgb))
                return new Expected { pixels = pixels, stride = w };
            int pw = (w + 3) / 4 * 4, ph = (h + 3) / 4 * 4; var colors = new Color[pw * ph];
            for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) colors[y * pw + x] = pixels[Math.Min(y, h - 1) * w + Math.Min(x, w - 1)];
            var half = new Texture2D(pw, ph, TextureFormat.RGBAHalf, false, true); Texture2D texture = null;
            try
            {
                half.SetPixels(colors); half.Apply(); using var codec = new Bc7Encoder(encoder);
                texture = codec.Encode(half, srgb, false, (Bc7Quality)quality).CreateTexture();
                return new Expected { pixels = Read(texture, pw, ph, 0, decoder), stride = pw, converted = true };
            }
            finally { Object.DestroyImmediate(half); if (texture != null) Object.DestroyImmediate(texture); }
        }
        static void CheckEntries(GpuTextureAtlas atlas, List<Expected> expected, ComputeShader shader, Report report)
        {
            Require(atlas.Entries.Count == expected.Count && !atlas.Texture.isReadable, "Published entries or readability differ.");
            for (int i = 0; i < expected.Count; i++)
            {
                var e = atlas.Entries[i]; var reference = expected[i]; int w = e.PixelRect.width, h = e.PixelRect.height;
                Require(e.SourceIndex == i, "A stable source index changed.");
                int kernel = shader.FindKernel("SampleEntry"); using var buffer = new ComputeBuffer((w + 2) * (h + 2), 4);
                shader.SetInts("_Size", w, h); shader.SetVector("_ScaleOffset", e.UvScaleOffset); shader.SetVector("_Clamp", e.UvClamp);
                shader.SetTexture(kernel, "_Source", atlas.Texture); shader.SetBuffer(kernel, "_Pixels", buffer); shader.Dispatch(kernel, (w + 9) / 8, (h + 9) / 8, 1);
                var words = new uint[buffer.count]; buffer.GetData(words);
                for (int y = 0; y < h + 2; y++) for (int x = 0; x < w + 2; x++)
                {
                    Color32 color = reference.pixels[Math.Max(0, Math.Min(h - 1, y - 1)) * reference.stride + Math.Max(0, Math.Min(w - 1, x - 1))];
                    for (int c = 0; c < 4; c++)
                    {
                        int delta = Math.Abs((int)((words[y * (w + 2) + x] >> (8 * c)) & 255) - color[c]);
                        report.maximumDifference = Math.Max(report.maximumDifference, delta);
                        Require(delta <= (reference.converted ? 1 : 0), $"Extended atlas entry {i}, channel {c}, delta={delta}.");
                    }
                    if (reference.converted) report.convertedPixels++; else report.exactPixels++;
                }
            }
        }
        static int CheckGeometry()
        {
            int cases = 0;
            foreach (int block in new[] { 4, 5, 6, 8, 10, 12 }) foreach (int padding in new[] { 0, 1 })
            {
                int limit = block * 64;
                var layout = LayoutProbe.Build(new[] { new Vector2Int(block * 2 - 1, block - 1) }, block, limit, padding);
                for (int step = 0; step < 16; step++)
                {
                    var inputs = new[] { new Vector2Int(step == 0 ? block * 5 : block + step, step == 0 ? block * 3 : block - 1) };
                    var next = layout.Append(inputs, block, limit, padding);
                    var again = layout.Append(inputs, block, limit, padding);
                    Require(next.Width == again.Width && next.Height == again.Height && next.Regions.SequenceEqual(again.Regions), "Append placement is not deterministic.");
                    Require(next.Width >= layout.Width && next.Height >= layout.Height, "Append shrank the atlas.");
                    for (int i = 0; i < layout.Regions.Length; i++) Require(layout.Regions[i] == next.Regions[i], "Append moved a reserved rectangle.");
                    var occupied = new bool[next.Width / block * (next.Height / block)];
                    foreach (var r in next.Regions)
                        for (int y = r.y - padding; y < r.yMax + padding; y++) for (int x = r.x - padding; x < r.xMax + padding; x++)
                        {
                            Require(x >= 0 && y >= 0 && x < next.Width / block && y < next.Height / block, "Append reservation escaped bounds.");
                            int at = y * (next.Width / block) + x; Require(!occupied[at], "Append reservations overlap."); occupied[at] = true;
                        }
                    layout = next;
                }
                cases++;
            }
            return cases;
        }
        // Geometry remains an internal runtime detail. This test assembly accesses it only for verification.
        sealed class LayoutProbe
        {
            readonly object value;
            static Type LayoutType => typeof(GpuTextureAtlas).Assembly.GetType("UnityCompute.TextureCompression.BlockAtlasLayout");
            object Field(string name) => LayoutType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
            internal int Width => (int)Field("Width");
            internal int Height => (int)Field("Height");
            internal RectInt[] Regions => (RectInt[])Field("Regions");
            LayoutProbe(object value) { this.value = value; }
            internal static LayoutProbe Build(Vector2Int[] sizes, int block, int limit, int padding)
                => new LayoutProbe(LayoutType.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { sizes, block, block, limit, padding, 16 }));
            internal LayoutProbe Append(Vector2Int[] sizes, int block, int limit, int padding)
                => new LayoutProbe(LayoutType.GetMethod("Append", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { value, sizes, block, limit, padding }));
        }
    }
}
