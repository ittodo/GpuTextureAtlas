using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityCompute.TextureCompression.Verification
{
    // Player-only verification. CPU references and readback are not part of the runtime scheduler.
    public static class GpuAtlasSchedulingValidation
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public string error, backend;
            public int cases, pixels, cancellations, disposals, frames, peakCopies, peakEncoding, peakConversion, atomicCopies;
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static int Counter(GpuAtlasFrameBudget budget, string name) => (int)typeof(GpuAtlasFrameBudget)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(budget);
        static Texture2D Source(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(Enumerable.Range(0, width * height).Select(i =>
                new Color32((byte)(i * 7), (byte)(i * 13), (byte)(i * 3), (byte)(i * 11))).ToArray());
            texture.Apply(false, true); return texture;
        }
        static Color[] Read(Texture2D texture, ComputeShader decoder) => GpuAtlasCopyValidation.Read(texture, texture.width, texture.height, 0, decoder);
        static void CheckEntry(GpuTextureAtlas atlas, int id, Color[] expected, int width, int height, ComputeShader decoder, Report report)
        {
            var pixels = Read(atlas.Texture, decoder); var rect = atlas.GetEntry(id).PixelRect;
            Require(rect.width == width && rect.height == height, "Scheduled entry size changed.");
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Require(pixels[(rect.y + y) * atlas.Width + rect.x + x].Equals(expected[y * width + x]), "Scheduled pixels differ from immediate encoding.");
                report.pixels++;
            }
        }
        static async Task Cancel(Task task, CancellationTokenSource cancellation, Report report)
        {
            Require(!task.IsCompleted, "The cancellation test did not span frames."); cancellation.Cancel();
            try { await task; throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { report.cancellations++; }
        }
        public static async Task Run(TextureFormat format, ComputeShader encoder, ComputeShader convert, ComputeShader decoder, string output)
        {
            Directory.CreateDirectory(output); int frame = Time.frameCount;
            var report = new Report { backend = SystemInfo.graphicsDeviceType.ToString() };
            var source = Source(68, 52);
            try
            {
                foreach (bool srgb in new[] { false, true }) foreach (var quality in new[] { GpuAtlasQuality.Fast, GpuAtlasQuality.Balanced, GpuAtlasQuality.HighQuality })
                {
                    GpuTextureAtlas reference = null, atlas = null, sharedAtlas = null;
                    try
                    {
                        reference = await GpuTextureAtlas.PackTexturesAsync(new[] { source }, format, srgb, encoder, convert, quality, maxSize: 512);
                        var expected = Read(reference.Texture, decoder);
                        var budget = new GpuAtlasFrameBudget(32, 5, 136);
                        int start = Time.frameCount;
                        var first = GpuTextureAtlas.PackTexturesAsync(new[] { source }, format, srgb, encoder, convert, quality,
                            maxSize: 512, frameBudget: budget);
                        var second = GpuTextureAtlas.PackTexturesAsync(new[] { reference.Texture }, format, srgb,
                            maxSize: 512, frameBudget: budget);
                        try { await Task.WhenAll(first, second); }
                        finally { if (first.Status == TaskStatus.RanToCompletion) atlas = first.Result; if (second.Status == TaskStatus.RanToCompletion) sharedAtlas = second.Result; }
                        Require(Time.frameCount > start + 1, "Scheduled pack did not span frames.");
                        CheckEntry(atlas, 0, expected, 68, 52, decoder, report);
                        CheckEntry(sharedAtlas, 0, expected, 68, 52, decoder, report);
                        var old = atlas.Texture; int version = atlas.Version;
                        var add = atlas.AddAsync(source, encoder, convert, quality, frameBudget: budget);
                        Require(atlas.IsUpdating && atlas.Texture == old && atlas.Version == version && atlas.Entries.Count == 1,
                            "Scheduled add published an unfinished atlas.");
                        CheckEntry(atlas, 0, expected, 68, 52, decoder, report);
                        await add; CheckEntry(atlas, 0, expected, 68, 52, decoder, report); CheckEntry(atlas, 1, expected, 68, 52, decoder, report);
                        atlas.Remove(0); old = atlas.Texture; version = atlas.Version;
                        var repack = atlas.RepackAsync(budget);
                        Require(atlas.Texture == old && atlas.Version == version, "Scheduled repack published early.");
                        await repack; CheckEntry(atlas, 1, expected, 68, 52, decoder, report);
                        using (var cancellation = new CancellationTokenSource())
                        {
                            old = atlas.Texture; version = atlas.Version; int id = atlas.NextSourceIndex;
                            await Cancel(atlas.AddAsync(source, encoder, convert, quality, frameBudget: budget, cancellationToken: cancellation.Token), cancellation, report);
                            Require(!atlas.IsUpdating && atlas.Texture == old && atlas.Version == version && atlas.NextSourceIndex == id,
                                "Cancelled add changed published state.");
                            CheckEntry(atlas, 1, expected, 68, 52, decoder, report);
                        }
                        using (var cancellation = new CancellationTokenSource())
                        {
                            await Cancel(atlas.RepackAsync(budget, cancellation.Token), cancellation, report);
                            Require(!atlas.IsUpdating && atlas.Texture == old && atlas.Version == version, "Cancelled repack changed published state.");
                        }
                        using (var cancellation = new CancellationTokenSource())
                            await Cancel(GpuTextureAtlas.PackTexturesAsync(new[] { source }, format, srgb, encoder, convert, quality,
                                maxSize: 512, frameBudget: budget, cancellationToken: cancellation.Token), cancellation, report);
                        using (var cancellation = new CancellationTokenSource())
                        {
                            var encodingBudget = new GpuAtlasFrameBudget(32, 17, 65536);
                            var encoding = GpuTextureAtlas.PackTexturesAsync(new[] { source }, format, srgb, encoder, convert, quality,
                                maxSize: 512, frameBudget: encodingBudget, cancellationToken: cancellation.Token);
                            int stopFrame = Time.frameCount + 3;
                            while (Time.frameCount < stopFrame) await Task.Yield();
                            Require(Counter(encodingBudget, "PeakEncodeBlocks") > 0, "Encoding cancellation did not reach the encode stage.");
                            await Cancel(encoding, cancellation, report);
                        }
                        var pending = atlas.AddAsync(source, encoder, convert, quality, frameBudget: budget);
                        Require(!pending.IsCompleted, "Dispose test did not span frames."); atlas.Dispose();
                        try { await pending; throw new InvalidOperationException("Disposed owner accepted an add."); }
                        catch (ObjectDisposedException) { report.disposals++; }
                        report.peakCopies = Math.Max(report.peakCopies, Counter(budget, "PeakCopyBlocks"));
                        report.peakEncoding = Math.Max(report.peakEncoding, Counter(budget, "PeakEncodeBlocks"));
                        report.peakConversion = Math.Max(report.peakConversion, Counter(budget, "PeakConversionPixels"));
                        Require(report.peakCopies <= 32 && report.peakEncoding <= 5 && report.peakConversion <= 136,
                            "Concurrent jobs exceeded the shared frame quota.");
                        report.cases++;
                    }
                    finally { atlas?.Dispose(); reference?.Dispose(); sharedAtlas?.Dispose(); await GpuAtlasCopyValidation.DrainRetirement(); }
                }
                // A partial edge requires one whole-mip copy. This is the documented atomic exception.
                // D3D12 requires an aligned BC7 base level. Its mip 2 has the desired 13x9 partial edge.
                var partial = new Texture2D(52, 36, format, true, true);
                try
                {
                    int blockCount = partial.GetPixelData<byte>(2).Length / 16;
                    var random = new System.Random(19);
                    for (int mip = 0; mip < partial.mipmapCount; mip++)
                    {
                        var bytes = new byte[partial.GetPixelData<byte>(mip).Length]; random.NextBytes(bytes);
                        for (int offset = 0; offset < bytes.Length; offset += 16)
                            if (format == TextureFormat.BC7) bytes[offset] = (byte)((bytes[offset] & 128) | 64);
                            else { bytes[offset] = 0xfc; bytes[offset+1] = 0xfd; for (int i = 2; i < 8; i++) bytes[offset+i] = 0xff; }
                        partial.SetPixelData(bytes, mip);
                    }
                    partial.Apply(false, true);
                    var expected = GpuAtlasCopyValidation.Read(partial, 13, 9, 2, decoder);
                    var atomicBudget = new GpuAtlasFrameBudget(1, 1, 1);
                    using (var packed = await GpuTextureAtlas.PackTexturesAsync(new[] { partial }, format, false, mipLevel: 2, frameBudget: atomicBudget))
                        CheckEntry(packed, 0, expected, 13, 9, decoder, report);
                    report.atomicCopies = Counter(atomicBudget, "AtomicCopyOverruns");
                    Require(report.atomicCopies == 1 && Counter(atomicBudget, "PeakCopyBlocks") == blockCount, "Atomic copy accounting differs.");
                }
                finally { UnityEngine.Object.Destroy(partial); await GpuAtlasCopyValidation.DrainRetirement(); }
                report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                UnityEngine.Object.Destroy(source); report.frames = Time.frameCount - frame;
                File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(report, true));
            }
        }
    }
}
