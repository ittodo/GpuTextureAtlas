using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using UnityCompute.TextureCompression;

namespace UnityCompute.TextureCompression.Verification
{
    public sealed class GpuAtlasPackageOwner : MonoBehaviour
    {
        public GpuTextureAtlas atlas;
        void OnDestroy() { atlas?.Dispose(); }
    }
    public sealed class GpuAtlasPackageProbe : MonoBehaviour
    {
        public ComputeShader encoder, conversion, decoder;
        [Serializable] sealed class Report { public bool passed; public string error, unity, gpu; public int cycles, exactPixels, releasedInputs, sceneCancellations, sampleViews; }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task Frames(int count) { for (int i = 0; i < count; i++) { int f = Time.frameCount; do { await Task.Yield(); } while (f == Time.frameCount); } }
        Color[] Read(Texture2D texture)
        {
            int k = decoder.FindKernel("ReadLinear"); using var b = new ComputeBuffer(texture.width * texture.height, 16);
            decoder.SetInts("_Size", texture.width, texture.height); decoder.SetInt("_Mip", 0); decoder.SetTexture(k, "_Source", texture); decoder.SetBuffer(k, "_Linear", b);
            decoder.Dispatch(k, (texture.width + 7) / 8, (texture.height + 7) / 8, 1); var pixels = new Color[texture.width * texture.height]; b.GetData(pixels); return pixels;
        }
        void Compare(GpuTextureAtlas atlas, int id, Color[] expected, Report report, string stage = "after release")
        {
            var actual = Read(atlas.Texture); var r = atlas.GetEntry(id).PixelRect;
            for (int y = 0; y < r.height; y++) for (int x = 0; x < r.width; x++)
                Require(actual[(r.y+y)*atlas.Width+r.x+x].Equals(expected[y*r.width+x]), $"Addressable atlas differs {stage}, id={id}, pixel={x},{y}, actual={actual[(r.y+y)*atlas.Width+r.x+x]}, expected={expected[y*r.width+x]}.");
            report.exactPixels += r.width*r.height;
        }
        async void Start()
        {
            Application.targetFrameRate = 120; var report = new Report { unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName };
            string output = Environment.GetCommandLineArgs().SkipWhile(a => a != "-atlas-report").Skip(1).FirstOrDefault() ?? Path.Combine(Application.persistentDataPath, "package-result.json");
            try
            {
                var budget = new GpuAtlasFrameBudget(64, 64, 256);
                for (int cycle = 0; cycle < 8; cycle++)
                {
                    AsyncOperationHandle<Texture2D> a = default, b = default; GpuTextureAtlas atlas = null;
                    try
                    {
                        a = Addressables.LoadAssetAsync<Texture2D>("atlas-compressed"); b = Addressables.LoadAssetAsync<Texture2D>("atlas-rgba");
                        var compressed = await a.Task; var rgba = await b.Task;
                        Require(a.Status == AsyncOperationStatus.Succeeded && b.Status == AsyncOperationStatus.Succeeded, "Addressables load failed.");
                        Require(!compressed.isReadable && !rgba.isReadable && compressed.format == TextureFormat.BC7, "Wrong Addressable import settings.");
                        var expectedCopy = Read(compressed);
                        Color[] expectedConverted;
                        using (var reference = await GpuTextureAtlas.PackTexturesAsync(new[] { rgba }, TextureFormat.BC7, true, encoder, conversion)) expectedConverted = Read(reference.Texture);
                        atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { compressed }, TextureFormat.BC7, true, frameBudget: budget);
                        await atlas.AddAsync(rgba, encoder, conversion, frameBudget: budget);
                        Compare(atlas, 0, expectedCopy, report, "before release"); Compare(atlas, 1, expectedConverted, report, "before release");
                        Addressables.Release(a); Addressables.Release(b);
                        await Resources.UnloadUnusedAssets(); await Frames(2);
                        Require(compressed == null && rgba == null, "Released Addressables remain loaded."); report.releasedInputs += 2;
                        Compare(atlas, 0, expectedCopy, report); Compare(atlas, 1, expectedConverted, report);
                        atlas.Remove(0); await atlas.RepackAsync(budget); Compare(atlas, 1, expectedConverted, report); report.cycles++;
                    }
                    finally { atlas?.Dispose(); if (a.IsValid()) Addressables.Release(a); if (b.IsValid()) Addressables.Release(b); await GpuAtlasCopyValidation.DrainRetirement(); }
                }
                // Scene unload disposes an owner while its Add is spread across frames.
                var handle = Addressables.LoadAssetAsync<Texture2D>("atlas-rgba");
                try
                {
                    var texture = await handle.Task;
                    var owned = await GpuTextureAtlas.PackTexturesAsync(new[] { texture }, TextureFormat.BC7, true, encoder, conversion);
                    var scene = SceneManager.CreateScene("Atlas lifetime test"); var go = new GameObject("Atlas owner"); SceneManager.MoveGameObjectToScene(go, scene); go.AddComponent<GpuAtlasPackageOwner>().atlas = owned;
                    Task addition = owned.AddAsync(texture, encoder, conversion, frameBudget: new GpuAtlasFrameBudget(1, 32, 128));
                    await SceneManager.UnloadSceneAsync(scene);
                    try { await addition; throw new InvalidOperationException("Disposed scene owner published an Add."); }
                    catch (ObjectDisposedException) { report.sceneCancellations++; }
                }
                finally { if (handle.IsValid()) Addressables.Release(handle); await GpuAtlasCopyValidation.DrainRetirement(); }
                var demoObject = new GameObject("Imported package sample"); var demo = demoObject.AddComponent<GpuTextureAtlasSamples.DynamicAtlasDemo>();
                demo.bc7Encoder = encoder; demo.conversion = conversion;
                var atlasField = typeof(GpuTextureAtlasSamples.DynamicAtlasDemo).GetField("atlas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                for (int i = 0; i < 1200 && atlasField.GetValue(demo) == null; i++) await Frames(1);
                Require(atlasField.GetValue(demo) != null, "Imported sample did not create an atlas."); await Frames(2);
                report.sampleViews = demoObject.GetComponentsInChildren<GpuAtlasRenderer>().Length; Require(report.sampleViews == 4, "Sample did not bind four renderers.");
                Destroy(demoObject); await Frames(3); await GpuAtlasCopyValidation.DrainRetirement(); report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
            finally { Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output, JsonUtility.ToJson(report, true)); Application.Quit(report.passed ? 0 : 1); }
        }
    }
}
