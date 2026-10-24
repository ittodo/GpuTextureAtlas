using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityCompute.Astc;

namespace UnityCompute.TextureCompression.Verification
{
    // Device verification only. Readback, CPU references and progress files are not runtime atlas features.
    public sealed class GpuAtlasAndroidProbe : MonoBehaviour
    {
        public ComputeShader encoderShader, conversionShader, readShader;
        [Serializable] sealed class Report
        {
            public bool passed, copyPassed, conversionPassed, rendererPassed, schedulingPassed;
            public string unity, device, os, gpu, backend, graphicsVersion, utc, error, phase;
            public int frames, convertedCases, convertedPixels;
            public double elapsedSeconds;
        }
        Report report; string output;
        void Progress(string phase)
        {
            report.phase = phase; File.WriteAllText(Path.Combine(output, "progress.json"), JsonUtility.ToJson(report, true));
            Debug.Log("GPU_ATLAS_TEST " + phase);
        }
        async Task Phase(string name, Func<Task> run, Action success)
        {
            Progress(name);
            try { await run(); success(); }
            catch (Exception error) { report.error += name + ": " + error + "\n"; Debug.LogException(error); }
            await GpuAtlasCopyValidation.DrainRetirement();
        }
        async void Start()
        {
            Application.runInBackground = true; Application.targetFrameRate = 60; Screen.sleepTimeout = SleepTimeout.NeverSleep;
            output = Path.Combine(Application.persistentDataPath, "gpu-atlas"); Directory.CreateDirectory(output);
            report = new Report { unity = Application.unityVersion, device = SystemInfo.deviceModel, os = SystemInfo.operatingSystem,
                gpu = SystemInfo.graphicsDeviceName, backend = SystemInfo.graphicsDeviceType.ToString(), graphicsVersion = SystemInfo.graphicsDeviceVersion,
                utc = DateTime.UtcNow.ToString("O"), error = "" };
            var start = DateTime.UtcNow; int frame = Time.frameCount;
            try
            {
                Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan, "This test requires Vulkan.");
                await Phase("ASTC CopyTexture", () => GpuAtlasCopyValidation.Run(readShader, Path.Combine(output, "copytexture"), Progress), () => report.copyPassed = true);
                await Phase("ASTC GPU conversion", Conversion, () => report.conversionPassed = true);
                await Phase("ASTC frame budgets / cancellation", () => GpuAtlasSchedulingValidation.Run(TextureFormat.ASTC_4x4,
                    encoderShader, conversionShader, readShader, Path.Combine(output, "scheduling")), () => report.schedulingPassed = true);
                string bundlePath = Path.Combine(Application.persistentDataPath, "android-atlas-inputs");
                using (var request = UnityWebRequest.Get(Application.streamingAssetsPath + "/android-atlas-inputs"))
                {
                    request.downloadHandler = new DownloadHandlerFile(bundlePath); var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();
                    Require(request.result == UnityWebRequest.Result.Success, "Cannot extract test AssetBundle: " + request.error);
                }
                await Phase("ASTC bundle / 9-slice / SpriteMask", async () =>
                    { await GpuAtlasRendererValidation.Run(bundlePath, encoderShader, conversionShader, readShader, Path.Combine(output, "renderer"), TextureFormat.ASTC_4x4); },
                    () => report.rendererPassed = true);
                report.passed = report.copyPassed && report.conversionPassed && report.rendererPassed && report.schedulingPassed;
            }
            catch (Exception error) { report.error += error; Debug.LogException(error); }
            finally
            {
                report.frames = Time.frameCount - frame; report.elapsedSeconds = (DateTime.UtcNow - start).TotalSeconds;
                Progress(report.passed ? "PASS" : "FAIL"); File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(report, true));
                Debug.Log("GPU_ATLAS_TEST_COMPLETE " + JsonUtility.ToJson(report));
                Screen.sleepTimeout = SleepTimeout.SystemSetting;
                await Task.Delay(200); Application.Quit(report.passed ? 0 : 1);
            }
        }
        async Task Conversion()
        {
            var source = new Texture2D(37, 29, TextureFormat.RGBA32, false, true);
            var colors = new Color32[37 * 29];
            for (int y = 0; y < 29; y++) for (int x = 0; x < 37; x++) colors[y * 37 + x] = new Color32((byte)(x * 7), (byte)(y * 9), (byte)((x * 11 + y * 17) & 255), (byte)((x * 13 + y * 7) & 255));
            source.SetPixels32(colors); source.Apply(false, true);
            try
            {
                foreach (bool srgb in new[] { false, true }) foreach (var quality in new[] { GpuAtlasQuality.Fast, GpuAtlasQuality.Balanced, GpuAtlasQuality.HighQuality })
                {
                    Progress($"ASTC GPU conversion {srgb}, {quality}"); GpuTextureAtlas atlas = null; Texture2D reference = null, half = null;
                    try
                    {
                        atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { source }, TextureFormat.ASTC_4x4, srgb, encoderShader, conversionShader, quality, maxSize: 256);
                        Require(atlas.ConvertedSourceIndices.Count == 1 && !atlas.Texture.isReadable, "Conversion classification or readability differs.");
                        var pixels = GpuAtlasCopyValidation.Read(source, 37, 29, 0, readShader); var padded = new Color[40 * 32];
                        for (int y = 0; y < 32; y++) for (int x = 0; x < 40; x++) padded[y * 40 + x] = pixels[Math.Min(y, 28) * 37 + Math.Min(x, 36)];
                        half = new Texture2D(40, 32, TextureFormat.RGBAHalf, false, true); half.SetPixels(padded); half.Apply(false, true);
                        using (var encoder = new AstcEncoder(encoderShader)) reference = encoder.Encode(half, AstcBlockSize.Block4x4, srgb, false, (AstcQuality)quality).CreateTexture();
                        var expected = GpuAtlasCopyValidation.Read(reference, 40, 32, 0, readShader);
                        var actual = GpuAtlasCopyValidation.Read(atlas.Texture, atlas.Width, atlas.Height, 0, readShader); var rect = atlas.GetEntry(0).PackedRect;
                        for (int y = 0; y < 32; y++) for (int x = 0; x < 40; x++)
                        { Require(actual[(rect.y + y) * atlas.Width + rect.x + x].Equals(expected[y * 40 + x]), "Native GPU upload differs from the CPU block-upload reference."); report.convertedPixels++; }
                        report.convertedCases++;
                    }
                    finally { atlas?.Dispose(); if (half != null) Destroy(half); if (reference != null) Destroy(reference); await GpuAtlasCopyValidation.DrainRetirement(); }
                }
            }
            finally { Destroy(source); }
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void OnGUI()
        {
            GUI.Label(new Rect(24, 24, Screen.width - 48, 160), "GpuTextureAtlas device verification\n" + report?.gpu + "\n" + report?.phase);
        }
    }
}
