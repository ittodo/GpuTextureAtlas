using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityCompute.TextureCompression;

namespace UnityCompute.TextureCompression.Verification
{
    // Copied into a temporary asset folder only while building the verification player.
    public sealed class GpuAtlasPlayerProbe : MonoBehaviour
    {
        public ComputeShader encoderShader, conversionShader, readShader;
        [Serializable] sealed class Report
        {
            public bool passed;
            public bool rendererPassed;
            public string unity, backend, error;
            public int sources, copied, converted, exactPixels, retainedPixels, frames, pendingReleases;
            public int additions, growths, inPlace, addedPixels;
            public int removed, repacks, editedPixels;
        }
        async void Start()
        {
            Application.runInBackground = true;
            var report = new Report { unity = Application.unityVersion, backend = SystemInfo.graphicsDeviceType.ToString() };
            AssetBundle bundle = null; GpuTextureAtlas atlas = null, repacked = null, incremental = null; int start = Time.frameCount;
            string Argument(string key) { var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, key); return at < 0 ? null : args[at + 1]; }
            string reportPath = Argument("-gpu-atlas-report");
            try
            {
                await GpuAtlasSchedulingValidation.Run(TextureFormat.BC7, encoderShader, conversionShader, readShader,
                    Path.Combine(Path.GetDirectoryName(reportPath), "scheduling"));
                await GpuAtlasRendererValidation.Run(Argument("-gpu-atlas-bundle"), encoderShader, conversionShader, readShader,
                    Path.Combine(Path.GetDirectoryName(reportPath), "renderer")); report.rendererPassed = true;
                bundle = AssetBundle.LoadFromFile(Argument("-gpu-atlas-bundle")); Require(bundle != null, "Cannot load verification bundle.");
                var inputs = bundle.LoadAllAssets<Texture2D>().OrderBy(t => t.name, StringComparer.Ordinal).ToArray();
                Require(inputs.Length == 10 && inputs.All(t => !t.isReadable), "Bundle inputs must have Read/Write disabled."); report.sources = inputs.Length;
                atlas = await GpuTextureAtlas.PackTexturesAsync(inputs, TextureFormat.BC7, true, encoderShader, conversionShader, GpuAtlasQuality.Balanced, maxSize: 512, paddingBlocks: 1);
                Require(!atlas.Texture.isReadable && atlas.Texture.format == TextureFormat.BC7, "Atlas must remain compressed and unreadable.");
                report.copied = atlas.CopiedSourceIndices.Count; report.converted = atlas.ConvertedSourceIndices.Count;
                Require(report.copied == 3 && report.converted == 7, "Matching inputs were recompressed or mismatches were omitted.");
                var pixels = Read(atlas.Texture, atlas.Width, atlas.Height);
                foreach (int i in atlas.CopiedSourceIndices)
                {
                    var entry = atlas.Entries[i]; var source = Read(inputs[i], entry.PixelRect.width, entry.PixelRect.height);
                    for (int y = 0; y < entry.PixelRect.height; y++) for (int x = 0; x < entry.PixelRect.width; x++)
                    {
                        Require(source[y * entry.PixelRect.width + x].Equals(pixels[(entry.PixelRect.y + y) * atlas.Width + entry.PixelRect.x + x]), "Direct-copy pixel changed.");
                        report.exactPixels++;
                    }
                }
                // Grow, fill a hole, and add mixed formats while keeping existing images intact.
                var seed = inputs.Single(t => t.name == "srgb-0");
                var large = inputs.Single(t => t.name == "srgb-1");
                // Two 256x256 atlas inputs each reserve an 8-pixel gutter, so a later add needs a side larger than 512.
                incremental = await GpuTextureAtlas.PackTexturesAsync(new[] { seed }, TextureFormat.BC7, true, maxSize: 1024, paddingBlocks: 1);
                var references = new System.Collections.Generic.List<Color32[]> { Read(seed, 32, 16) };
                var previous = incremental.Texture;
                await incremental.AddAsync(large); report.additions++; report.growths++;
                Require(previous != incremental.Texture, "Addition did not grow the player atlas."); references.Add(Read(large, 64, 32));
                previous = incremental.Texture;
                await incremental.AddAsync(seed, mipLevel: 5); report.additions++; report.inPlace++;
                Require(previous == incremental.Texture, "Tiny addition did not reuse a hole."); references.Add(Read(seed, 1, 1, 5));
                var mixed = new[] { inputs.Single(t => t.name == "srgb-3"), inputs.Single(t => t.name == "linear-0"), inputs.Single(t => t.name == "srgb-4") };
                previous = incremental.Texture;
                await incremental.AddAsync(mixed, encoderShader, conversionShader, GpuAtlasQuality.Balanced); report.additions++;
                if (previous == incremental.Texture) report.inPlace++; else report.growths++;
                foreach (var source in mixed)
                {
                    var e = atlas.Entries.Single(entry => entry.Name == source.name); var reference = new Color32[e.PixelRect.width * e.PixelRect.height];
                    for (int y = 0; y < e.PixelRect.height; y++) Array.Copy(pixels, (e.PixelRect.y + y) * atlas.Width + e.PixelRect.x, reference, y * e.PixelRect.width, e.PixelRect.width);
                    references.Add(reference);
                }
                CheckAdded(incremental, references, report);
                bundle.Unload(true); bundle = null; await NextFrame();
                Require(pixels.SequenceEqual(Read(atlas.Texture, atlas.Width, atlas.Height)), "Releasing the source bundle changed the atlas.");
                CheckAdded(incremental, references, report);
                // Add an existing GPU-only atlas after every original bundle texture has been destroyed.
                previous = incremental.Texture;
                await incremental.AddAsync(atlas.Texture); report.additions++;
                if (previous == incremental.Texture) report.inPlace++; else report.growths++;
                references.Add(pixels); CheckAdded(incremental, references, report);
                // Remove fragmented entries, then relocate survivors after all original assets are gone.
                var byId = references.Select((p, i) => new { id = i, pixels = p }).ToDictionary(item => item.id, item => item.pixels);
                report.removed = incremental.Remove(new[] { 0, 2, 4, 4, 999 });
                Require(report.removed == 3 && !incremental.TryGetEntry(0, out _) && !incremental.TryGetEntry(2, out _), "Player removal failed.");
                byId.Remove(0); byId.Remove(2); byId.Remove(4);
                int nextId = incremental.NextSourceIndex; previous = incremental.Texture;
                await incremental.RepackAsync(); report.repacks++;
                Require(incremental.Texture != previous && incremental.NextSourceIndex == nextId, "Player repack reused IDs or failed to replace the texture.");
                CheckEdited(incremental, byId, report);
                previous = incremental.Texture; await incremental.AddAsync(atlas.Texture); report.additions++; byId.Add(nextId, pixels);
                if (previous == incremental.Texture) report.inPlace++; else report.growths++;
                Require(!incremental.TryGetEntry(0, out _) && incremental.GetEntry(nextId).SourceIndex == nextId, "Player addition reused a removed ID.");
                CheckEdited(incremental, byId, report);
                report.removed += incremental.Remove(incremental.Entries.Select(e => e.SourceIndex).ToArray());
                nextId = incremental.NextSourceIndex; await incremental.RepackAsync(); report.repacks++;
                Require(incremental.Entries.Count == 0 && incremental.Width == 4 && incremental.Height == 4, "Player empty repack failed.");
                previous = incremental.Texture; await incremental.AddAsync(atlas.Texture); report.additions++;
                if (previous == incremental.Texture) report.inPlace++; else report.growths++;
                byId.Clear(); byId.Add(nextId, pixels); await incremental.RepackAsync(); report.repacks++; CheckEdited(incremental, byId, report);
                incremental.Dispose(); incremental = null;
                // Repack a GPU-only result with no conversion shaders, then release the first owner.
                repacked = await GpuTextureAtlas.PackTexturesAsync(new[] { atlas.Texture }, TextureFormat.BC7, true, maxSize: 512);
                int width = atlas.Width, height = atlas.Height; var firstTexture = atlas.Texture; atlas.Dispose(); atlas = null;
                for (int i = 0; i < 6; i++) await NextFrame();
                Require(firstTexture == null, "Disposed atlas texture was not destroyed.");
                Require(pixels.SequenceEqual(Read(repacked.Texture, width, height)), "Repacking a GPU-only atlas changed pixels.");
                report.retainedPixels = pixels.Length; repacked.Dispose(); repacked = null;
                int Pending(string type) => ((ICollection)typeof(GpuTextureAtlas).Assembly.GetType(type).GetField("pending", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count;
                float deadline = Time.realtimeSinceStartup + 15;
                do
                {
                    await NextFrame(); report.pendingReleases = Pending("UnityCompute.TextureCompression.GpuAtlasRetirement") + Pending("UnityCompute.Bc7.Bc7GpuRetirement");
                } while (report.pendingReleases != 0 && Time.realtimeSinceStartup < deadline);
                Require(report.pendingReleases == 0, "GPU atlas resources did not retire.");
                report.frames = Time.frameCount - start; report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); }
            finally
            {
                if (bundle != null) bundle.Unload(true); atlas?.Dispose(); repacked?.Dispose(); incremental?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)); File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
                Application.Quit(report.passed ? 0 : 1);
            }
        }
        async Task NextFrame() { int frame = Time.frameCount; while (Time.frameCount == frame) await Task.Yield(); }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void CheckAdded(GpuTextureAtlas atlas, System.Collections.Generic.List<Color32[]> references, Report report)
        {
            var actual = Read(atlas.Texture, atlas.Width, atlas.Height);
            Require(atlas.Entries.Count == references.Count && !atlas.Texture.isReadable && !atlas.IsUpdating, "Extended atlas metadata differs.");
            for (int i = 0; i < references.Count; i++)
            {
                var e = atlas.Entries[i]; Require(e.SourceIndex == i, "Extended player source index changed.");
                for (int y = 0; y < e.PixelRect.height; y++) for (int x = 0; x < e.PixelRect.width; x++)
                {
                    Require(references[i][y * e.PixelRect.width + x].Equals(actual[(e.PixelRect.y + y) * atlas.Width + e.PixelRect.x + x]), "Extended player atlas pixel changed.");
                    report.addedPixels++;
                }
            }
        }
        Color32[] Read(Texture texture, int width, int height, int mip = 0)
        {
            int kernel = readShader.FindKernel("ReadLinear"); using var buffer = new ComputeBuffer(width * height, 16);
            readShader.SetInts("_Size", width, height); readShader.SetInt("_Mip", mip); readShader.SetTexture(kernel, "_Source", texture); readShader.SetBuffer(kernel, "_Linear", buffer);
            readShader.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1); var linear = new Color[width * height]; buffer.GetData(linear);
            return linear.Select(c => (Color32)c).ToArray();
        }
        void CheckEdited(GpuTextureAtlas atlas, System.Collections.Generic.Dictionary<int, Color32[]> expected, Report report)
        {
            Require(atlas.Entries.Select(e => e.SourceIndex).SequenceEqual(expected.Keys.OrderBy(id => id)), "Player active IDs differ.");
            var pixels = Read(atlas.Texture, atlas.Width, atlas.Height);
            foreach (var entry in atlas.Entries)
            {
                Require(ReferenceEquals(atlas.GetEntry(entry.SourceIndex), entry), "Player ID lookup differs.");
                var reference = expected[entry.SourceIndex];
                for (int y = 0; y < entry.PixelRect.height; y++) for (int x = 0; x < entry.PixelRect.width; x++)
                {
                    Require(reference[y * entry.PixelRect.width + x].Equals(pixels[(entry.PixelRect.y + y) * atlas.Width + entry.PixelRect.x + x]), "Player repacking changed image pixels."); report.editedPixels++;
                    var uv = entry.MapUv(new Vector2((x + .5f) / entry.PixelRect.width, (y + .5f) / entry.PixelRect.height));
                    Require(Mathf.FloorToInt(uv.x * atlas.Width) == entry.PixelRect.x + x && Mathf.FloorToInt(uv.y * atlas.Height) == entry.PixelRect.y + y, "Player repacked UV differs.");
                }
            }
        }
    }
}
