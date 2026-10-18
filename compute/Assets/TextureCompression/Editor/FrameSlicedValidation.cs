using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityCompute.Astc;
using UnityCompute.Bc7;
using Object = UnityEngine.Object;

namespace UnityCompute.TextureCompression.Editor
{
    // Verification and readback stay outside the runtime scheduling path.
    public static class FrameSlicedValidation
    {
        static readonly string Root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/frame-sliced"));
        public static string Status { get; private set; } = "not run";
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        internal static Texture2D Source(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var colors = new Color32[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                colors[y * width + x] = new Color32((byte)(x * 29 + y * 7), (byte)(y * 31 + x * 3),
                    (byte)((x ^ y) * 17), (byte)((x * 11 + y * 23) & 255));
            texture.SetPixels32(colors); texture.Apply(false, false); return texture;
        }
        public static string RunGpuRows()
        {
            var source = Source(37, 29); int cases = 0, bands = 0;
            using var astc = new AstcEncoder(AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Astc/Shaders/AstcEncoder.compute"));
            using var bc7 = new Bc7Encoder(AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7Encoder.compute"));
            try
            {
                foreach (bool srgb in new[] { false, true }) foreach (AstcQuality quality in Enum.GetValues(typeof(AstcQuality)))
                foreach (AstcBlockSize size in Enum.GetValues(typeof(AstcBlockSize)))
                {
                    int block = (int)size, columns = (source.width + block - 1) / block, rows = (source.height + block - 1) / block;
                    CheckRows(columns, rows, (buffer, first, count) => astc.EncodeGpu(source, buffer, size, srgb, true,
                        quality, columns + 3, first, count), ref bands); cases++;
                }
                foreach (Bc7Quality quality in Enum.GetValues(typeof(Bc7Quality))) foreach (bool flip in new[] { false, true })
                {
                    int columns = (source.width + 3) / 4, rows = (source.height + 3) / 4;
                    CheckRows(columns, rows, (buffer, first, count) => bc7.EncodeGpu(source, buffer, true, true,
                        quality, columns + 3, flip, first, count), ref bands); cases++;
                }
                string result = $"PASS: {cases} row-range cases, {bands} bands; full and sliced bytes match; untouched rows and padding preserved.";
                Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "rows-result.txt"), result); return result;
            }
            finally { Object.DestroyImmediate(source); }
        }
        static void CheckRows(int columns, int rows, Action<ComputeBuffer, int, int> encode, ref int bands)
        {
            int stride = columns + 3, count = stride * rows;
            using var full = new ComputeBuffer(count, 16); using var sliced = new ComputeBuffer(count, 16);
            var expected = new uint[count * 4]; var actual = new uint[count * 4];
            for (int i = 0; i < actual.Length; i++) actual[i] = 0xA5A5A5A5;
            full.SetData(actual); sliced.SetData(actual); encode(full, 0, rows); full.GetData(expected);
            int done = 0;
            while (done < rows)
            {
                int n = Math.Min(done % 2 == 0 ? 1 : 3, rows - done);
                encode(sliced, done, n); done += n; sliced.GetData(actual); bands++;
                for (int y = 0; y < rows; y++) for (int x = 0; x < stride; x++) for (int word = 0; word < 4; word++)
                {
                    int at = (y * stride + x) * 4 + word;
                    uint value = y < done && x < columns ? expected[at] : 0xA5A5A5A5;
                    Require(actual[at] == value, $"Range writes differ at ({x},{y}), word {word}.");
                }
            }
        }

        [Serializable] sealed class StreamReport
        {
            public string unity, utc;
            public bool passed;
            public int cases, steps, copies, retired;
            public long beginAllocatedBytes, stepAllocatedBytes;
            public string error;
        }
        public static void RunStreamingAsync()
        {
            if (Status == "running") throw new InvalidOperationException("Verification is already running.");
            Status = "running"; EditorApplication.update += Pump; RunStreaming();
        }
        static void Pump() { EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll(); }
        static async void RunStreaming()
        {
            var report = new StreamReport { unity = Application.unityVersion, utc = DateTime.UtcNow.ToString("O") };
            try
            {
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7Encoder.compute");
                using var reference = new Bc7Encoder(shader);
                foreach (Bc7Quality quality in Enum.GetValues(typeof(Bc7Quality))) foreach (bool snapshot in new[] { false, true })
                {
                    var source = Source(65, 33); var expected = reference.Encode(source, true, true, quality).CreateTexture();
                    var output = new Bc7GpuTexture(shader, source.width, source.height, true);
                    try
                    {
                        var previous = Read(output.Texture, source.width, source.height);
                        var target = Read(expected, source.width, source.height);
                        // First pass allocates the optional snapshot. Second pass measures reuse.
                        for (int pass = 0; pass < 2; pass++)
                        {
                            if (snapshot && pass == 1) { source.SetPixels32(SourcePixels(source.width, source.height)); source.Apply(); }
                            int beforeCopies = output.RecordedCopies;
                            long before = GC.GetAllocatedBytesForCurrentThread();
                            output.BeginEncode(source, true, quality, snapshot);
                            if (pass == 1) report.beginAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                            Require(output.CompletedBlockRows == 0 && output.SubmittedBlockRows == 0, "Begin must not encode all rows.");
                            if (snapshot) { var changed = new Color32[source.width * source.height]; source.SetPixels32(changed); source.Apply(); }
                            int steps = 0;
                            while (output.IsEncoding)
                            {
                                before = GC.GetAllocatedBytesForCurrentThread(); output.EncodeNextFrame(2);
                                if (pass == 1) report.stepAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                                steps++; report.steps++;
                                var pixels = Read(output.Texture, source.width, source.height);
                                int copies = output.RecordedCopies - beforeCopies;
                                Require(copies == 0 || copies == 1, "A job must publish only once.");
                                Equal(pixels, copies == 0 ? previous : target);
                                Require(steps < 200, "Sliced job did not finish.");
                                await Task.Yield();
                            }
                            Require(output.CompletedBlockRows == output.TotalBlockRows && output.EncodingProgress == 1, "Progress did not complete.");
                            Require(output.RecordedCopies == beforeCopies + 1 && steps >= 5, "Job did not span multiple steps.");
                            previous = target; report.copies++; report.cases++;
                        }
                    }
                    finally
                    {
                        output.Dispose(); for (int i = 0; i < 200 && !output.IsReleased; i++) await Task.Yield();
                        Require(output.IsReleased, "Output snapshot or native resources did not retire."); report.retired++;
                        Object.DestroyImmediate(expected); Object.DestroyImmediate(source);
                    }
                }
                // Dispose with a partially encoded snapshot. No incomplete texture is published.
                var pendingSource = Source(65, 33); var pending = new Bc7GpuTexture(shader, 65, 33);
                pending.BeginEncode(pendingSource); pending.EncodeNextFrame(1); pending.Dispose();
                for (int i = 0; i < 200 && !pending.IsReleased; i++) await Task.Yield();
                Require(pending.IsReleased, "In-flight disposal did not retire."); report.retired++; Object.DestroyImmediate(pendingSource);
                Require(report.beginAllocatedBytes == 0 && report.stepAllocatedBytes == 0, "Reused slicing allocates managed memory.");
                report.passed = true; Status = $"PASS: {report.cases} sliced jobs, {report.copies} final copies, snapshots remain stable, 0 reused Begin/Step bytes, {report.retired} outputs retired.";
            }
            catch (Exception error) { report.error = error.ToString(); Status = "FAIL: " + error; Debug.LogException(error); }
            finally { EditorApplication.update -= Pump; Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "stream-validation.json"), JsonUtility.ToJson(report, true)); }
        }
        static Color32[] SourcePixels(int width, int height)
        {
            var source = Source(width, height); try { return source.GetPixels32(); } finally { Object.DestroyImmediate(source); }
        }
        internal static uint[] Read(Texture texture, int width, int height)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7DecodeHardware.compute"); int kernel = shader.FindKernel("DecodeHardware");
            using var buffer = new ComputeBuffer(width * height, 4);
            shader.SetInts("_Size", width, height); shader.SetTexture(kernel, "_Source", texture); shader.SetBuffer(kernel, "_Pixels", buffer);
            shader.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1); var pixels = new uint[width * height]; buffer.GetData(pixels); return pixels;
        }
        static void Equal(uint[] a, uint[] b) { for (int i = 0; i < a.Length; i++) Require(a[i] == b[i], "Published texture differs at pixel " + i); }
    }
}
