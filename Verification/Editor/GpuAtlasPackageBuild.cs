using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityCompute.TextureCompression.Verification;

public static class GpuAtlasPackageBuild
{
    public static void RunBatch()
    {
        try
        {
            Directory.CreateDirectory("Assets/Inputs");
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) settings = AddressableAssetSettings.Create("Assets/AddressableAssetsData", "AddressableAssetSettings", true, true);
            AddressableAssetSettingsDefaultObject.Settings = settings;
            var group = settings.FindGroup("Atlas inputs") ?? settings.CreateGroup("Atlas inputs", false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            foreach (bool compressed in new[] { true, false })
            {
                string key = compressed ? "atlas-compressed" : "atlas-rgba", file = "Assets/Inputs/" + key + ".png";
                var texture = new Texture2D(128, 84); var pixels = new Color[128*84];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color((i%128)/127f, (i/128)/83f, (i%7)/6f, (i%13)/12f);
                texture.SetPixels(pixels); texture.Apply(); File.WriteAllBytes(file, texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(file, ImportAssetOptions.ForceSynchronousImport); var importer = (TextureImporter)AssetImporter.GetAtPath(file);
                importer.isReadable = false; importer.sRGBTexture = true; importer.mipmapEnabled = false; importer.npotScale = TextureImporterNPOTScale.None;
                importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 1024,
                    format = compressed ? TextureImporterFormat.BC7 : TextureImporterFormat.RGBA32 }); importer.SaveAndReimport();
                settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(file), group).address = key;
            }
            AssetDatabase.SaveAssets(); AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult content);
            if (!string.IsNullOrEmpty(content.Error)) throw new InvalidOperationException(content.Error);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Camera").AddComponent<Camera>(); camera.orthographic = true; camera.orthographicSize = 3; camera.transform.position = new Vector3(0,0,-10);
            var probe = new GameObject("Package consumer verification").AddComponent<GpuAtlasPackageProbe>();
            const string root = "Packages/com.ittodo.gpu-texture-atlas/";
            probe.encoder = AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "Shaders/Bc7/Bc7Encoder.compute");
            probe.conversion = AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "Shaders/Atlas/GpuAtlasConvert.compute");
            probe.decoder = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Verification/GpuAtlasValidation.compute");
            EditorSceneManager.SaveScene(scene,"Assets/Probe.unity");
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
            var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Probe.unity" }, locationPathName = "Build/PackageProbe.exe", target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
            if (result.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Package consumer build failed.");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
