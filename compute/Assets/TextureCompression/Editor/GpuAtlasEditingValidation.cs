using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityCompute.TextureCompression.Editor
{
    // Test-only pixel snapshots cover whole encoded edge blocks as well as valid image UVs.
    internal static class GpuAtlasEditingValidation
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public string error;
            public int cases, removed, repacks, reusedHoles, pixels, uvPixels, movedEntries, rejected, disposedInFlight;
        }
        sealed class Snapshot { internal CompressedAtlasEntry entry; internal Color32[] pixels; }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Reject<T>(Action action, Report report) where T : Exception
        {
            try { action(); } catch (T) { report.rejected++; return; }
            throw new InvalidOperationException("An invalid atlas edit was accepted.");
        }
        static async Task RejectAsync<T>(Func<Task> action, Report report) where T : Exception
        {
            try { await action(); } catch (T) { report.rejected++; return; }
            throw new InvalidOperationException("An invalid asynchronous edit was accepted.");
        }
        static Color32[] Read(GpuTextureAtlas atlas, ComputeShader decoder)
            => GpuAtlasValidation.Read(atlas.Texture, atlas.Width, atlas.Height, 0, decoder).Select(c => (Color32)c).ToArray();
        static Dictionary<int, Snapshot> Capture(GpuTextureAtlas atlas, ComputeShader decoder)
        {
            var full = Read(atlas, decoder); var result = new Dictionary<int, Snapshot>();
            foreach (var entry in atlas.Entries)
            {
                var r = entry.PackedRect; var pixels = new Color32[r.width * r.height];
                for (int y = 0; y < r.height; y++) Array.Copy(full, (r.y + y) * atlas.Width + r.x, pixels, y * r.width, r.width);
                result.Add(entry.SourceIndex, new Snapshot { entry = entry, pixels = pixels });
            }
            return result;
        }
        internal static async Task Run(string bundlePath, ComputeShader encoder, ComputeShader convert, ComputeShader decoder)
        {
            var report = new Report(); AssetBundle bundle = null; GpuTextureAtlas atlas = null, slots = null;
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/gpu-atlas/editing.json"));
            try
            {
                foreach (bool srgb in new[] { false, true }) foreach (GpuAtlasQuality quality in Enum.GetValues(typeof(GpuAtlasQuality)))
                {
                    bundle = AssetBundle.LoadFromFile(bundlePath); Require(bundle != null, "Cannot load edit inputs.");
                    var inputs = bundle.LoadAllAssets<Texture2D>().OrderBy(t => t.name, StringComparer.Ordinal).ToArray();
                    var seed = inputs.Single(t => t.name == (srgb ? "srgb-0" : "linear-0"));
                    atlas = await GpuTextureAtlas.PackTexturesAsync(inputs, TextureFormat.BC7, srgb, encoder, convert, quality, maxSize: 512, paddingBlocks: 1, filterMode: FilterMode.Bilinear);
                    await atlas.AddAsync(seed, mipLevel: 5); var expected = Capture(atlas, decoder);
                    bundle.Unload(true); bundle = null; Require(seed == null, "Edit source assets remain loaded.");
                    var texture = atlas.Texture; int version = atlas.Version, nextId = atlas.NextSourceIndex;
                    int[] remove = { 1, 3, 5, 7, 9, 3, 9999, -1 };
                    int removed = atlas.Remove(remove); Require(removed == 5, "Batch removal count differs."); report.removed += removed;
                    foreach (int id in remove) expected.Remove(id);
                    Require(atlas.Texture == texture && atlas.Version == version + 1 && atlas.NextSourceIndex == nextId, "Removal changed the texture or reused an ID.");
                    foreach (var e in atlas.Entries) Require(ReferenceEquals(e, expected[e.SourceIndex].entry), "Removal changed a surviving entry.");
                    Require(!atlas.TryGetEntry(3, out _) && !atlas.CopiedSourceIndices.Contains(3) && !atlas.ConvertedSourceIndices.Contains(3), "Removed ID is still active.");
                    Reject<KeyNotFoundException>(() => atlas.GetEntry(3), report);
                    Check(atlas, expected, decoder, report);
                    var before = atlas.Entries.ToArray(); var repack = atlas.RepackAsync();
                    if (!repack.IsCompleted)
                    {
                        Require(atlas.IsUpdating && atlas.Texture == texture && atlas.Entries.SequenceEqual(before), "Repack published before the fence.");
                        Reject<InvalidOperationException>(() => atlas.Remove(0), report);
                        await RejectAsync<InvalidOperationException>(() => atlas.AddAsync(Array.Empty<Texture2D>()), report);
                        await RejectAsync<InvalidOperationException>(() => atlas.RepackAsync(), report);
                    }
                    await repack; report.repacks++;
                    Require(atlas.Texture != texture && atlas.NextSourceIndex == nextId && atlas.Version == version + 2, "Repack did not publish atomically.");
                    report.movedEntries += before.Count(e => atlas.GetEntry(e.SourceIndex).PackedRect != e.PackedRect);
                    Check(atlas, expected, decoder, report);
                    // Repeated repacking is deterministic, including IDs and UVs.
                    before = atlas.Entries.ToArray(); int w = atlas.Width, h = atlas.Height;
                    await atlas.RepackAsync(); report.repacks++;
                    Require(w == atlas.Width && h == atlas.Height && before.Select(e => e.PackedRect).SequenceEqual(atlas.Entries.Select(e => e.PackedRect)), "Repack is not deterministic.");
                    Check(atlas, expected, decoder, report);

                    bundle = AssetBundle.LoadFromFile(bundlePath); inputs = bundle.LoadAllAssets<Texture2D>();
                    seed = inputs.Single(t => t.name == (srgb ? "srgb-0" : "linear-0"));
                    // Three equal reservations leave a known hole when the middle entry is removed.
                    slots = await GpuTextureAtlas.PackTexturesAsync(new[] { seed, seed, seed }, TextureFormat.BC7, srgb, maxSize: 512, paddingBlocks: 1);
                    var slotExpected = Capture(slots, decoder); var hole = slots.GetEntry(1).PackedRect; texture = slots.Texture;
                    Require(slots.Remove(1), "Cannot remove the middle slot."); report.removed++; slotExpected.Remove(1);
                    await slots.AddAsync(seed);
                    Require(slots.Texture == texture && slots.GetEntry(3).PackedRect == hole && !slots.TryGetEntry(1, out _), "Add did not reuse the freed reservation with a new ID.");
                    slotExpected.Add(3, Capture(slots, decoder)[3]); Check(slots, slotExpected, decoder, report); report.reusedHoles++;
                    slots.Dispose(); slots = null;

                    var newInputs = new[] { seed, inputs.Single(t => t.name == (srgb ? "linear-3" : "srgb-3")) };
                    await atlas.AddAsync(newInputs, encoder, convert, quality);
                    Require(atlas.GetEntry(nextId).Name == seed.name && atlas.GetEntry(nextId + 1).SourceIndex == nextId + 1 && atlas.NextSourceIndex == nextId + 2, "New IDs alias removed entries.");
                    var added = Capture(atlas, decoder); expected.Add(nextId, added[nextId]); expected.Add(nextId + 1, added[nextId + 1]);
                    Check(atlas, expected, decoder, report);
                    Require(atlas.Remove(0), "Single removal failed."); report.removed++; expected.Remove(0);
                    version = atlas.Version;
                    Require(!atlas.Remove(0) && !atlas.Remove(-1) && atlas.Remove(Array.Empty<int>()) == 0 && atlas.Version == version, "Missing IDs changed the version.");
                    Reject<ArgumentNullException>(() => atlas.Remove((IReadOnlyList<int>)null), report);
                    Check(atlas, expected, decoder, report);
                    int count = atlas.Entries.Count;
                    Require(atlas.Remove(atlas.Entries.Select(e => e.SourceIndex).ToArray()) == count, "Remove all failed."); report.removed += count; expected.Clear();
                    nextId = atlas.NextSourceIndex; await atlas.RepackAsync(); report.repacks++;
                    Require(atlas.Width == 4 && atlas.Height == 4 && atlas.Entries.Count == 0 && atlas.CopiedSourceIndices.Count == 0 && atlas.ConvertedSourceIndices.Count == 0 && atlas.NextSourceIndex == nextId, "Empty repack did not retain an empty atlas and stable ID counter.");
                    await atlas.AddAsync(seed, mipLevel: 5);
                    Require(atlas.Entries.Count == 1 && atlas.GetEntry(nextId).PixelRect.size == Vector2Int.one, "Refilling an empty atlas failed.");
                    expected = Capture(atlas, decoder); await atlas.RepackAsync(); report.repacks++; Check(atlas, expected, decoder, report);
                    bundle.Unload(true); bundle = null; await atlas.RepackAsync(); report.repacks++; Check(atlas, expected, decoder, report);
                    atlas.Dispose(); Reject<ObjectDisposedException>(() => atlas.Remove(nextId), report);
                    await RejectAsync<ObjectDisposedException>(() => atlas.RepackAsync(), report); atlas = null;
                    await GpuAtlasValidation.DrainRetirement(); report.cases++;
                }
                // Disposing during repack must not publish or destroy resources before their copies finish.
                bundle = AssetBundle.LoadFromFile(bundlePath); var finalInputs = bundle.LoadAllAssets<Texture2D>();
                atlas = await GpuTextureAtlas.PackTexturesAsync(finalInputs, TextureFormat.BC7, true, encoder, convert, maxSize: 512);
                bundle.Unload(true); bundle = null; var pending = atlas.RepackAsync(); bool inFlight = !pending.IsCompleted;
                atlas.Dispose(); if (inFlight) { await RejectAsync<ObjectDisposedException>(() => pending, report); report.disposedInFlight++; } else await pending;
                atlas = null; await GpuAtlasValidation.DrainRetirement();
                Require(report.movedEntries > 0 && report.disposedInFlight == 1, "The relocation or pending-lifetime case did not execute."); report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                atlas?.Dispose(); slots?.Dispose(); if (bundle != null) bundle.Unload(true);
                await GpuAtlasValidation.DrainRetirement(); File.WriteAllText(output, JsonUtility.ToJson(report, true));
            }
        }
        static void Check(GpuTextureAtlas atlas, Dictionary<int, Snapshot> expected, ComputeShader shader, Report report)
        {
            Require(!atlas.Texture.isReadable && !atlas.IsUpdating && atlas.Entries.Select(e => e.SourceIndex).SequenceEqual(expected.Keys.OrderBy(id => id)), "Active entries or readability differ.");
            var full = Read(atlas, shader);
            foreach (var e in atlas.Entries)
            {
                var old = expected[e.SourceIndex]; var r = e.PackedRect;
                Require(e.Name == old.entry.Name && e.PixelRect.size == old.entry.PixelRect.size && r.size == old.entry.PackedRect.size && ReferenceEquals(atlas.GetEntry(e.SourceIndex), e), "Metadata or stable ID lookup differs.");
                for (int y = 0; y < r.height; y++) for (int x = 0; x < r.width; x++)
                {
                    Require(old.pixels[y * r.width + x].Equals(full[(r.y + y) * atlas.Width + r.x + x]), "Repack changed a decoded compressed block."); report.pixels++;
                }
                int w = e.PixelRect.width, h = e.PixelRect.height, kernel = shader.FindKernel("SampleEntry");
                using var buffer = new ComputeBuffer((w + 2) * (h + 2), 4);
                shader.SetInts("_Size", w, h); shader.SetVector("_ScaleOffset", e.UvScaleOffset); shader.SetVector("_Clamp", e.UvClamp);
                shader.SetTexture(kernel, "_Source", atlas.Texture); shader.SetBuffer(kernel, "_Pixels", buffer); shader.Dispatch(kernel, (w + 9) / 8, (h + 9) / 8, 1);
                var words = new uint[buffer.count]; buffer.GetData(words);
                for (int y = 0; y < h + 2; y++) for (int x = 0; x < w + 2; x++)
                {
                    var color = old.pixels[Math.Max(0, Math.Min(h - 1, y - 1)) * r.width + Math.Max(0, Math.Min(w - 1, x - 1))];
                    for (int c = 0; c < 4; c++) Require(((words[y * (w + 2) + x] >> (c * 8)) & 255) == color[c], "Repacked UV or bilinear clamp points at a different image.");
                    report.uvPixels++;
                }
            }
            Require(atlas.CopiedSourceIndices.Concat(atlas.ConvertedSourceIndices).OrderBy(id => id).SequenceEqual(expected.Keys.OrderBy(id => id)), "Classification contains removed IDs or misses active IDs.");
        }
    }
}
