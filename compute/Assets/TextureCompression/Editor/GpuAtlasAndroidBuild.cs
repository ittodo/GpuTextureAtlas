using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityCompute.TextureCompression.Editor
{
    public static class GpuAtlasAndroidBuild
    {
        public static void RunBatch()
        {
            try { Build(); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        static void Build()
        {
            const string inputs = "Assets/__GpuAtlasAndroidInputs", temp = "Assets/TextureCompression/AndroidVerificationTemp";
            if (AssetDatabase.IsValidFolder(inputs)) throw new InvalidOperationException("Temporary Android input folder already exists.");
            var type = typeof(GpuTextureAtlas).Assembly.GetType("UnityCompute.TextureCompression.Verification.GpuAtlasAndroidProbe");
            if (type == null) throw new InvalidOperationException("Install the temporary Android verification sources first.");
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/gpu-atlas/android")); Directory.CreateDirectory(output);
            var target = NamedBuildTarget.Android;
            string oldId = PlayerSettings.GetApplicationIdentifier(target), oldName = PlayerSettings.productName;
            var oldBackend = PlayerSettings.GetScriptingBackend(target); var oldArchitectures = PlayerSettings.Android.targetArchitectures;
            var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android); bool oldAuto = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
            var oldSdk = PlayerSettings.Android.targetSdkVersion; bool oldKeystore = PlayerSettings.Android.useCustomKeystore;
            bool oldBundle = EditorUserBuildSettings.buildAppBundle, oldExport = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            string bundlePath = "Assets/StreamingAssets/android-atlas-inputs";
            if (File.Exists(bundlePath)) throw new InvalidOperationException("Temporary Android test bundle already exists.");
            AssetDatabase.CreateFolder("Assets", "__GpuAtlasAndroidInputs");
            try
            {
                PlayerSettings.SetApplicationIdentifier(target, "com.ittodo.gputextureatlas.validation"); PlayerSettings.productName = "GpuTextureAtlas Verification";
                PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP); PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false); PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
                PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)36; PlayerSettings.Android.useCustomKeystore = false;
                EditorUserBuildSettings.buildAppBundle = false; EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
                var paths = new List<string>();
                foreach (bool srgb in new[] { false, true }) for (int i = 0; i < 5; i++)
                {
                    int width = new[] { 32, 64, 128, 37, 64 }[i], height = new[] { 16, 32, 64, 29, 32 }[i];
                    string path = $"{inputs}/{(srgb ? "srgb" : "linear")}-{i}.png"; var source = FrameSlicedValidation.Source(width, height);
                    try { File.WriteAllBytes(path, source.EncodeToPNG()); } finally { UnityEngine.Object.DestroyImmediate(source); }
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport); var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    importer.textureType = TextureImporterType.Default; importer.isReadable = false; importer.sRGBTexture = srgb;
                    importer.mipmapEnabled = true; importer.npotScale = TextureImporterNPOTScale.None;
                    importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 2048,
                        format = i < 3 ? TextureImporterFormat.ASTC_4x4 : i == 3 ? TextureImporterFormat.RGBA32 : TextureImporterFormat.ASTC_6x6,
                        compressionQuality = 50 }); importer.SaveAndReimport(); paths.Add(path);
                }
                string bundleDirectory = Path.Combine(output, "bundle"); Directory.CreateDirectory(bundleDirectory);
                var bundle = new AssetBundleBuild { assetBundleName = "android-atlas-inputs", assetNames = paths.ToArray() };
                if (BuildPipeline.BuildAssetBundles(bundleDirectory, new[] { bundle }, BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.Android) == null)
                    throw new InvalidOperationException("Android test bundle build failed.");
                Directory.CreateDirectory("Assets/StreamingAssets"); File.Copy(Path.Combine(bundleDirectory, bundle.assetBundleName), bundlePath); AssetDatabase.Refresh();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var component = new GameObject("Android atlas verification").AddComponent(type); var serialized = new SerializedObject(component);
                serialized.FindProperty("encoderShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Astc/Shaders/AstcEncoder.compute");
                serialized.FindProperty("conversionShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/TextureCompression/Shaders/GpuAtlasConvert.compute");
                serialized.FindProperty("readShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>(temp + "/GpuAtlasValidation.compute");
                serialized.ApplyModifiedPropertiesWithoutUndo(); string scenePath = temp + "/Probe.unity"; EditorSceneManager.SaveScene(scene, scenePath);
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = Path.Combine(output, "GpuAtlasVerification.apk"),
                    target = BuildTarget.Android, options = BuildOptions.Development,
                    // App UI is an Editor dependency in this project. Its touch startup is not part of the atlas probe.
                    extraScriptingDefines = new[] { "APP_UI_EDITOR_ONLY" } });
                if (result.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Android player build failed: " + result.summary.result);
            }
            finally
            {
                PlayerSettings.SetApplicationIdentifier(target, oldId); PlayerSettings.productName = oldName;
                PlayerSettings.SetScriptingBackend(target, oldBackend); PlayerSettings.Android.targetArchitectures = oldArchitectures;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, oldApis); PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, oldAuto);
                PlayerSettings.Android.targetSdkVersion = oldSdk; PlayerSettings.Android.useCustomKeystore = oldKeystore;
                EditorUserBuildSettings.buildAppBundle = oldBundle; EditorUserBuildSettings.exportAsGoogleAndroidProject = oldExport;
                AssetDatabase.DeleteAsset(inputs); AssetDatabase.DeleteAsset(bundlePath); AssetDatabase.SaveAssets();
            }
        }
    }
}
