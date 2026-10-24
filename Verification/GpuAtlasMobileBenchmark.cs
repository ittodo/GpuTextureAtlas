using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace UnityCompute.TextureCompression.Verification
{
    // Measurement app only. Timing samples, device status and files do not enter the package runtime.
    public sealed class GpuAtlasMobileBenchmark : MonoBehaviour
    {
        public ComputeShader encoderShader, conversionShader;
        [Serializable] public sealed class Request
        {
            public int repetitions = 3, stressSeconds = 600, resolution, inputLimit, encodeBlocks = 128, highQualityEncodeBlocks = 16, maxImmediateResolution = 256;
            public bool measure = true, stress = true, skipHighQualityImmediate = true;
            public string onlyQuality = "", onlyScheduling = "", onlyGroup = "";
        }
        [Serializable] sealed class Input { public string texture, group, sha256; public int resolution; }
        [Serializable] sealed class Manifest { public Input[] items; }
        [Serializable] sealed class FrameSample { public ulong timestamp; public double cpuMs, gpuMs, mainMs, renderMs; }
        [Serializable] sealed class Result
        {
            public string texture, group, operation, quality, scheduling;
            public int resolution, repetition, activeFrames, timingSamples;
            public double submitCpuMs, completionMs, frameMedianMs, frameP95Ms, frameP99Ms, frameMaxMs;
            public double gpuFrameMedianMs, gpuFrameP95Ms, gpuFrameP99Ms, gpuFrameMaxMs;
            public long submitManagedBytes, managedStart, managedEnd, graphicsStart, graphicsPeak, totalStart, totalPeak;
            public double[] activeFrameMs;
            public FrameSample[] gpuWindow;
        }
        [Serializable] sealed class ThermalSample
        {
            public double elapsedSeconds;
            public int status = -1, batteryTemperatureTenthsC = -1;
            public long totalMemory, graphicsMemory, managedMemory;
            public int edits;
        }
        [Serializable] sealed class Skipped
        {
            public string texture, group, operation = "pack-convert", quality, scheduling = "immediate", reason;
            public int resolution, repetition;
        }
        [Serializable] sealed class StressCheckpoint
        {
            public int edits, pauses, resumes;
            public double elapsedSeconds;
            public List<ThermalSample> thermal;
        }
        [Serializable] sealed class Report
        {
            public bool passed, frameTimingEnabled, allocationCounterAvailable;
            public string unity, device, gpu, api, os, utc, phase, error;
            public int pauses, resumes, stressEdits;
            public double elapsedSeconds;
            public Request request;
            public List<Result> results = new List<Result>();
            public List<Skipped> skipped = new List<Skipped>();
            public List<ThermalSample> thermal = new List<ThermalSample>();
            public string timingScope = "submitCpuMs is initial main-thread call time. completionMs includes frame/fence waits. activeFrameMs is observed frame delta while the task is pending. gpuWindow is whole-app FrameTimingManager data, including 8 trailing frames; it is not kernel time. Zero GPU timings are unavailable, not zero cost. Graphics memory uses Unity's driver allocation estimate; all-zero values are unavailable. Battery temperature is not GPU temperature. The probe allocates timing records and report arrays outside the initial-call GC counter.";
        }
        readonly FrameTiming[] timings = new FrameTiming[16];
        readonly List<double> activeFrames = new List<double>(8192);
        readonly List<FrameSample> gpuWindow = new List<FrameSample>(8192);
        Report report; Request request; string output; bool collect, pending; ulong lastTimestamp;
        long totalPeak, graphicsPeak; readonly Stopwatch clock = new Stopwatch();
        void Progress(string phase)
        {
            report.phase = phase;
            File.WriteAllText(Path.Combine(output, "progress.json"), JsonUtility.ToJson(new ProgressInfo { phase = phase, results = report.results.Count, elapsedSeconds = clock.Elapsed.TotalSeconds }, true));
            Debug.Log("GPU_ATLAS_BENCHMARK " + phase);
        }
        [Serializable] sealed class ProgressInfo { public string phase; public int results; public double elapsedSeconds; }
        void Update()
        {
            FrameTimingManager.CaptureFrameTimings();
            if (pending) activeFrames.Add(Time.unscaledDeltaTime * 1000.0);
            if (!collect) return;
            totalPeak = Math.Max(totalPeak, Profiler.GetTotalAllocatedMemoryLong());
            graphicsPeak = Math.Max(graphicsPeak, Profiler.GetAllocatedMemoryForGraphicsDriver());
            uint count = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
            // Accept either array order. Compare all returned samples with the previous watermark.
            ulong previousTimestamp = lastTimestamp;
            for (int i = 0; i < count; i++)
            {
                var t = timings[i]; if (t.frameStartTimestamp <= previousTimestamp) continue;
                lastTimestamp = Math.Max(lastTimestamp, t.frameStartTimestamp);
                gpuWindow.Add(new FrameSample { timestamp = t.frameStartTimestamp, cpuMs = t.cpuFrameTime, gpuMs = t.gpuFrameTime,
                    mainMs = t.cpuMainThreadFrameTime, renderMs = t.cpuRenderThreadFrameTime });
            }
        }
        static async Task Frames(int count)
        {
            for (int i = 0; i < count; i++) { int frame = Time.frameCount; do { await Task.Yield(); } while (frame == Time.frameCount); }
        }
        static double Quantile(double[] sorted, double p) => sorted.Length == 0 ? -1 : sorted[(int)Math.Ceiling((sorted.Length - 1) * p)];
        async Task Measure(Input input, string operation, GpuAtlasQuality quality, bool sliced, int repetition, Func<Task> action)
        {
            await Frames(8); activeFrames.Clear(); gpuWindow.Clear();
            uint ready = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
            for (int i = 0; i < ready; i++) lastTimestamp = Math.Max(lastTimestamp, timings[i].frameStartTimestamp);
            var result = new Result { texture = input.texture, group = input.group, resolution = input.resolution,
                operation = operation, quality = quality.ToString(), scheduling = sliced ? "frame-budget" : "immediate", repetition = repetition,
                managedStart = GC.GetTotalMemory(false), graphicsStart = Profiler.GetAllocatedMemoryForGraphicsDriver(), totalStart = Profiler.GetTotalAllocatedMemoryLong() };
            totalPeak = result.totalStart; graphicsPeak = result.graphicsStart; collect = pending = true;
            long ticks = Stopwatch.GetTimestamp(), allocated = report.allocationCounterAvailable ? GC.GetAllocatedBytesForCurrentThread() : 0;
            Task task;
            try
            {
                task = action();
                result.submitManagedBytes = report.allocationCounterAvailable ? GC.GetAllocatedBytesForCurrentThread() - allocated : -1;
                result.submitCpuMs = (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
                await task;
                result.completionMs = (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
            }
            finally { pending = false; }
            await Frames(8); collect = false;
            result.activeFrameMs = activeFrames.ToArray(); result.gpuWindow = gpuWindow.OrderBy(t => t.timestamp).ToArray();
            result.activeFrames = activeFrames.Count; result.timingSamples = gpuWindow.Count;
            var frameValues = activeFrames.OrderBy(v => v).ToArray(); var gpuValues = gpuWindow.Where(t => t.gpuMs > 0).Select(t => t.gpuMs).OrderBy(v => v).ToArray();
            result.frameMedianMs = Quantile(frameValues, .5); result.frameP95Ms = Quantile(frameValues, .95);
            result.frameP99Ms = Quantile(frameValues, .99); result.frameMaxMs = Quantile(frameValues, 1);
            result.gpuFrameMedianMs = Quantile(gpuValues, .5); result.gpuFrameP95Ms = Quantile(gpuValues, .95);
            result.gpuFrameP99Ms = Quantile(gpuValues, .99); result.gpuFrameMaxMs = Quantile(gpuValues, 1);
            result.managedEnd = GC.GetTotalMemory(false); result.totalPeak = totalPeak; result.graphicsPeak = graphicsPeak;
            report.results.Add(result);
            File.WriteAllText(Path.Combine(output, "measurements.json"), JsonUtility.ToJson(report));
        }
        async Task<string> Extract(string name)
        {
            string path = Path.Combine(Application.persistentDataPath, name);
            using var request = UnityWebRequest.Get(Application.streamingAssetsPath + "/" + name);
            request.downloadHandler = new DownloadHandlerFile(path); var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.error);
            return path;
        }
        async Task Benchmark(Manifest manifest)
        {
            foreach (int size in new[] { 256, 512, 1024 })
            {
                if (request.resolution != 0 && request.resolution != size) continue;
                AssetBundle compressed = null, rgba = null;
                try
                {
                    Progress("Loading " + size);
                    compressed = AssetBundle.LoadFromFile(await Extract("astc-" + size));
                    rgba = AssetBundle.LoadFromFile(await Extract("rgba-" + size));
                    if (compressed == null || rgba == null) throw new IOException("Cannot load benchmark bundles.");
                    var compressedInputs = compressed.LoadAllAssets<Texture2D>();
                    var rgbaInputs = rgba.LoadAllAssets<Texture2D>();
                    var warmed = new HashSet<string>();
                    foreach (var input in manifest.items.Where(x => x.resolution == size && (string.IsNullOrEmpty(request.onlyGroup) || x.group == request.onlyGroup))
                        .Take(request.inputLimit > 0 ? request.inputLimit : int.MaxValue))
                    {
                        var copySource = compressedInputs.Single(t => t.name == input.texture);
                        var convertSource = rgbaInputs.Single(t => t.name == input.texture);
                        if (copySource.isReadable || convertSource.isReadable || copySource.format != TextureFormat.ASTC_4x4)
                            throw new InvalidOperationException("Benchmark inputs were not imported as unreadable ASTC/RGBA textures.");
                        foreach (bool sliced in new[] { false, true })
                        {
                            if (!string.IsNullOrEmpty(request.onlyScheduling) && request.onlyScheduling != (sliced ? "frame-budget" : "immediate")) continue;
                            var budget = sliced ? new GpuAtlasFrameBudget(encodeBlocksPerFrame: request.encodeBlocks) : null;
                            // Warm each shader path before collecting timings. Includes driver shader compilation.
                            foreach (var quality in new[] { GpuAtlasQuality.Fast, GpuAtlasQuality.Balanced, GpuAtlasQuality.HighQuality })
                            {
                                if (!string.IsNullOrEmpty(request.onlyQuality) && request.onlyQuality != quality.ToString()) continue;
                                if (!sliced && ((quality == GpuAtlasQuality.HighQuality && request.skipHighQualityImmediate) || size > request.maxImmediateResolution))
                                {
                                    for (int repeat = 0; repeat < request.repetitions; repeat++) report.skipped.Add(new Skipped
                                        { texture = input.texture, group = input.group, resolution = size, repetition = repeat, quality = quality.ToString(),
                                          reason = quality == GpuAtlasQuality.HighQuality && request.skipHighQualityImmediate
                                            ? "Excluded after an Adreno 619 GPU hang during full-image HighQuality encoding. Use a frame budget."
                                            : "Outside the requested full-image dispatch size limit. Budgeted encoding and compressed copies are measured at this size." });
                                    continue;
                                }
                                var qualityBudget = sliced && quality == GpuAtlasQuality.HighQuality
                                    ? new GpuAtlasFrameBudget(encodeBlocksPerFrame: request.highQualityEncodeBlocks) : budget;
                                if (warmed.Add(quality + "/" + sliced))
                                {
                                    Progress($"Warming {size}/{quality}/{(sliced ? "budget" : "immediate")}");
                                    using (var warm = await GpuTextureAtlas.PackTexturesAsync(new[] { convertSource }, TextureFormat.ASTC_4x4, true,
                                        encoderShader, conversionShader, quality, maxSize: 4096, frameBudget: qualityBudget)) { }
                                    await GpuAtlasCopyValidation.DrainRetirement();
                                }
                                for (int repeat = 0; repeat < request.repetitions; repeat++)
                                {
                                    Progress($"{size}/{input.group}/{input.texture}/{quality}/{(sliced ? "budget" : "immediate")}/{repeat}");
                                    GpuTextureAtlas atlas = null;
                                    try { await Measure(input, "pack-convert", quality, sliced, repeat, async () =>
                                        { atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { convertSource }, TextureFormat.ASTC_4x4, true,
                                            encoderShader, conversionShader, quality, maxSize: 4096, frameBudget: qualityBudget); }); }
                                    finally { atlas?.Dispose(); await GpuAtlasCopyValidation.DrainRetirement(); }
                                }
                            }
                            for (int repeat = 0; repeat < request.repetitions; repeat++)
                            {
                                GpuTextureAtlas atlas = null;
                                try
                                {
                                    await Measure(input, "pack-copy", GpuAtlasQuality.Fast, sliced, repeat, async () =>
                                        { atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { copySource }, TextureFormat.ASTC_4x4, true, maxSize: 4096, frameBudget: budget); });
                                    await Measure(input, "add-copy", GpuAtlasQuality.Fast, sliced, repeat, () => atlas.AddAsync(copySource, frameBudget: budget));
                                    atlas.Remove(0);
                                    await Measure(input, "repack", GpuAtlasQuality.Fast, sliced, repeat, () => atlas.RepackAsync(budget));
                                }
                                finally { atlas?.Dispose(); await GpuAtlasCopyValidation.DrainRetirement(); }
                            }
                        }
                        File.WriteAllText(Path.Combine(output, "measurements.json"), JsonUtility.ToJson(report));
                    }
                }
                finally { if (compressed != null) compressed.Unload(true); if (rgba != null) rgba.Unload(true); await Resources.UnloadUnusedAssets(); }
            }
        }
        void Thermal()
        {
            var sample = new ThermalSample { elapsedSeconds = clock.Elapsed.TotalSeconds, edits = report.stressEdits,
                totalMemory = Profiler.GetTotalAllocatedMemoryLong(), graphicsMemory = Profiler.GetAllocatedMemoryForGraphicsDriver(), managedMemory = GC.GetTotalMemory(false) };
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var power = activity.Call<AndroidJavaObject>("getSystemService", "power");
                sample.status = power.Call<int>("getCurrentThermalStatus");
                using var filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED");
                using var battery = activity.Call<AndroidJavaObject>("registerReceiver", null, filter);
                sample.batteryTemperatureTenthsC = battery.Call<int>("getIntExtra", "temperature", -1);
            }
            catch (Exception error) { Debug.Log("Device thermal data unavailable: " + error.GetType().Name); }
#endif
            report.thermal.Add(sample);
        }
        async Task Stress()
        {
            Progress("Stress / background-resume"); Thermal();
            var bundle = AssetBundle.LoadFromFile(await Extract("rgba-256")); GpuTextureAtlas atlas = null;
            try
            {
                if (bundle == null) throw new IOException("Cannot load stress bundle.");
                var inputs = bundle.LoadAllAssets<Texture2D>(); var budget = new GpuAtlasFrameBudget(encodeBlocksPerFrame: request.encodeBlocks);
                if (inputs.Length != 20) throw new InvalidOperationException("Expected 20 stress textures.");
                atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { inputs[0] }, TextureFormat.ASTC_4x4, true,
                    encoderShader, conversionShader, maxSize: 2048, frameBudget: budget);
                double deadline = Time.realtimeSinceStartupAsDouble + request.stressSeconds, nextSample = 0;
                while (Time.realtimeSinceStartupAsDouble < deadline)
                {
                    int id = atlas.NextSourceIndex;
                    await atlas.AddAsync(inputs[report.stressEdits % inputs.Length], encoderShader, conversionShader, frameBudget: budget);
                    foreach (int old in atlas.Entries.Where(e => e.SourceIndex != id).Select(e => e.SourceIndex).ToArray()) atlas.Remove(old);
                    await atlas.RepackAsync(budget); report.stressEdits++;
                    if (Time.realtimeSinceStartupAsDouble >= nextSample)
                    {
                        await GpuAtlasCopyValidation.DrainRetirement(); Thermal(); nextSample = Time.realtimeSinceStartupAsDouble + 10;
                        // Keep long-run checkpoints small. Do not serialize all timing frames every 10 seconds.
                        File.WriteAllText(Path.Combine(output, "stress.json"), JsonUtility.ToJson(new StressCheckpoint
                        { edits = report.stressEdits, pauses = report.pauses, resumes = report.resumes,
                            elapsedSeconds = clock.Elapsed.TotalSeconds, thermal = report.thermal }));
                    }
                }
            }
            finally { atlas?.Dispose(); if (bundle != null) bundle.Unload(true); await GpuAtlasCopyValidation.DrainRetirement(); await Frames(8); Thermal(); }
        }
        async void Start()
        {
            Application.targetFrameRate = 60; QualitySettings.vSyncCount = 0; Screen.sleepTimeout = SleepTimeout.NeverSleep;
            new GameObject("Benchmark camera").AddComponent<Camera>().backgroundColor = new Color(.04f, .05f, .07f);
            output = Path.Combine(Application.persistentDataPath, "mobile-benchmark"); Directory.CreateDirectory(output);
            string requestPath = Path.Combine(Application.persistentDataPath, "benchmark-request.json");
            request = File.Exists(requestPath) ? JsonUtility.FromJson<Request>(File.ReadAllText(requestPath)) : new Request();
            report = new Report { unity = Application.unityVersion, device = SystemInfo.deviceModel, gpu = SystemInfo.graphicsDeviceName,
                api = SystemInfo.graphicsDeviceVersion, os = SystemInfo.operatingSystem, utc = DateTime.UtcNow.ToString("O"), request = request,
                frameTimingEnabled = FrameTimingManager.IsFeatureEnabled(), allocationCounterAvailable = HasAllocationCounter() }; clock.Start();
            try
            {
                if (request.repetitions < 1 || request.repetitions > 10 || request.stressSeconds < 0 || request.stressSeconds > 3600 || request.encodeBlocks < 1 || request.highQualityEncodeBlocks < 1)
                    throw new ArgumentException("Invalid benchmark request.");
                var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(await Extract("benchmark-manifest.json")));
                Thermal(); if (request.measure) await Benchmark(manifest); if (request.stress) await Stress();
                await GpuAtlasCopyValidation.DrainRetirement(); report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally
            {
                collect = pending = false; report.elapsedSeconds = clock.Elapsed.TotalSeconds; Progress(report.passed ? "PASS" : "FAIL");
                File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(report));
                Screen.sleepTimeout = SleepTimeout.SystemSetting; await Frames(2); Application.Quit(report.passed ? 0 : 1);
            }
        }
        void OnApplicationPause(bool paused) { if (report == null) return; if (paused) report.pauses++; else report.resumes++; }
        static bool HasAllocationCounter()
        {
            // Unity's IL2CPP CoreLib implementation can return a constant zero. Verify the counter first.
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread(); var allocation = new byte[4096];
                long delta = GC.GetAllocatedBytesForCurrentThread() - before; GC.KeepAlive(allocation);
                return delta >= allocation.Length;
            }
            catch (NotImplementedException) { return false; }
        }
        void OnGUI() { GUI.Label(new Rect(20, 20, Screen.width - 40, 160), "GpuTextureAtlas mobile benchmark\n" + report?.phase + "\n" + report?.results.Count + " results"); }
    }
}
