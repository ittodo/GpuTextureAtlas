using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.TextureCompression.Editor
{
    public static class GpuAtlasMobileBenchmarkBuild
    {
        [Serializable] sealed class Input { public string texture; public int resolution; }
        [Serializable] sealed class Manifest { public Input[] items; }
        [Serializable] sealed class SourceFile { public string path, sha256; }
        [Serializable] sealed class BuildInfo { public string unity, utc; public SourceFile[] files; }
        public static void RunBatch()
        {
            try { Build(); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        static void Build()
        {
            const string inputs = "Assets/__GpuAtlasMobileInputs", temp = "Assets/TextureCompression/MobileBenchmarkTemp";
            if (AssetDatabase.IsValidFolder(inputs)) throw new InvalidOperationException("Temporary benchmark inputs already exist.");
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityCompute.TextureCompression.Verification.GpuAtlasMobileBenchmark")).FirstOrDefault(t => t != null);
            if (type == null) throw new InvalidOperationException("Install the temporary benchmark sources first.");
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string output = Path.Combine(root, ".validation/gpu-atlas/mobile"); Directory.CreateDirectory(output);
            string manifestText = "{\"items\":" + File.ReadAllText(Path.Combine(root, "Benchmark/Input/manifest.json")) + "}";
            var manifest = JsonUtility.FromJson<Manifest>(manifestText);
            var target = NamedBuildTarget.Android;
            string oldId = PlayerSettings.GetApplicationIdentifier(target), oldName = PlayerSettings.productName;
            var oldBackend = PlayerSettings.GetScriptingBackend(target); var oldArchitectures = PlayerSettings.Android.targetArchitectures;
            var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android); bool oldAuto = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
            var oldSdk = PlayerSettings.Android.targetSdkVersion; bool oldKeystore = PlayerSettings.Android.useCustomKeystore;
            bool oldBundle = EditorUserBuildSettings.buildAppBundle, oldExport = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            bool oldTiming = PlayerSettings.enableFrameTimingStats;
            var streaming = new List<string>();
            foreach (int size in new[] { 256, 512, 1024 }) foreach (string kind in new[] { "astc", "rgba" }) streaming.Add("Assets/StreamingAssets/" + kind + "-" + size);
            streaming.Add("Assets/StreamingAssets/benchmark-manifest.json");
            if (streaming.Any(File.Exists)) throw new InvalidOperationException("Temporary benchmark streaming assets already exist.");
            AssetDatabase.CreateFolder("Assets", "__GpuAtlasMobileInputs");
            try
            {
                PlayerSettings.SetApplicationIdentifier(target, "com.ittodo.gputextureatlas.benchmark"); PlayerSettings.productName = "GpuTextureAtlas Benchmark";
                PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP); PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false); PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
                PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)36; PlayerSettings.Android.useCustomKeystore = false;
                EditorUserBuildSettings.buildAppBundle = false; EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
                PlayerSettings.enableFrameTimingStats = true;
                var bundles = new List<AssetBundleBuild>();
                foreach (int size in new[] { 256, 512, 1024 }) foreach (string kind in new[] { "astc", "rgba" })
                {
                    string folder = inputs + "/" + kind + "-" + size; Directory.CreateDirectory(folder);
                    var paths = new List<string>();
                    foreach (var input in manifest.items.Where(i => i.resolution == size))
                    {
                        string path = folder + "/" + input.texture + ".png";
                        File.Copy(Path.Combine(root, "Benchmark/Input", size.ToString(), input.texture + ".png"), path);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                        importer.textureType = TextureImporterType.Default; importer.isReadable = false; importer.sRGBTexture = true;
                        importer.mipmapEnabled = false; importer.npotScale = TextureImporterNPOTScale.None;
                        importer.alphaSource = TextureImporterAlphaSource.FromInput; importer.alphaIsTransparency = false;
                        importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 1024,
                            format = kind == "astc" ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.RGBA32, compressionQuality = 50 });
                        importer.SaveAndReimport(); paths.Add(path);
                    }
                    if (paths.Count != 20) throw new InvalidOperationException("Each resolution needs 20 textures.");
                    bundles.Add(new AssetBundleBuild { assetBundleName = kind + "-" + size, assetNames = paths.ToArray() });
                }
                string bundleDirectory = Path.Combine(output, "bundles"); Directory.CreateDirectory(bundleDirectory);
                if (BuildPipeline.BuildAssetBundles(bundleDirectory, bundles.ToArray(), BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.Android) == null)
                    throw new InvalidOperationException("Benchmark bundle build failed.");
                Directory.CreateDirectory("Assets/StreamingAssets");
                foreach (var bundle in bundles) File.Copy(Path.Combine(bundleDirectory, bundle.assetBundleName), "Assets/StreamingAssets/" + bundle.assetBundleName);
                File.WriteAllText("Assets/StreamingAssets/benchmark-manifest.json", manifestText); AssetDatabase.Refresh();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var component = new GameObject("Mobile atlas benchmark").AddComponent(type); var serialized = new SerializedObject(component);
                serialized.FindProperty("encoderShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute");
                serialized.FindProperty("conversionShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Atlas/GpuAtlasConvert.compute");
                serialized.ApplyModifiedPropertiesWithoutUndo(); string scenePath = temp + "/Benchmark.unity"; EditorSceneManager.SaveScene(scene, scenePath);
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = Path.Combine(output, "GpuAtlasBenchmark.apk"),
                    target = BuildTarget.Android, options = BuildOptions.None });
                if (result.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Benchmark player build failed: " + result.summary.result);
                string packagePath = Path.Combine(root, "Packages/com.ittodo.gpu-texture-atlas");
                var sourceFiles = Directory.GetFiles(packagePath, "*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".cs") || p.EndsWith(".compute") || p.EndsWith(".hlsl") || p.EndsWith(".so") || p.EndsWith(".asmdef"))
                    .Concat(new[] { Path.Combine(root, "Verification/GpuAtlasMobileBenchmark.cs") }).OrderBy(p => p).Select(p =>
                    {
                        using var hash = SHA256.Create();
                        return new SourceFile { path = p.Substring(root.Length + 1).Replace('\\', '/'), sha256 = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(p))).Replace("-", "").ToLowerInvariant() };
                    }).ToArray();
                File.WriteAllText(Path.Combine(output, "build-info.json"), JsonUtility.ToJson(new BuildInfo { unity = Application.unityVersion, utc = DateTime.UtcNow.ToString("O"), files = sourceFiles }, true));
            }
            finally
            {
                PlayerSettings.SetApplicationIdentifier(target, oldId); PlayerSettings.productName = oldName;
                PlayerSettings.SetScriptingBackend(target, oldBackend); PlayerSettings.Android.targetArchitectures = oldArchitectures;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, oldApis); PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, oldAuto);
                PlayerSettings.Android.targetSdkVersion = oldSdk; PlayerSettings.Android.useCustomKeystore = oldKeystore;
                EditorUserBuildSettings.buildAppBundle = oldBundle; EditorUserBuildSettings.exportAsGoogleAndroidProject = oldExport;
                PlayerSettings.enableFrameTimingStats = oldTiming;
                AssetDatabase.DeleteAsset(inputs); foreach (string path in streaming) AssetDatabase.DeleteAsset(path); AssetDatabase.SaveAssets();
            }
        }
    }
}
