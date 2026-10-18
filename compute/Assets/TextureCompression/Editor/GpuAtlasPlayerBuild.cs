using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCompute.TextureCompression.Editor
{
    public static class GpuAtlasPlayerBuild
    {
        public static void RunBatch()
        {
            try { Build(); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        public static void Build()
        {
            var type = typeof(GpuTextureAtlas).Assembly.GetType("UnityCompute.TextureCompression.Verification.GpuAtlasPlayerProbe");
            if (type == null) throw new InvalidOperationException("Install the temporary verification source first.");
            const string scenePath = "Assets/TextureCompression/VerificationTemp/Probe.unity";
            var previous = SceneManager.GetActiveScene();
            // A fresh batch Editor starts with an untitled scene. No user scene is open there.
            bool batchUntitled = Application.isBatchMode && string.IsNullOrEmpty(previous.path);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, batchUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                var component = new GameObject("GPU atlas verification").AddComponent(type); var serialized = new SerializedObject(component);
                serialized.FindProperty("encoderShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7Encoder.compute");
                serialized.FindProperty("conversionShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/TextureCompression/Shaders/GpuAtlasConvert.compute");
                serialized.FindProperty("readShader").objectReferenceValue = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/TextureCompression/VerificationTemp/GpuAtlasValidation.compute");
                serialized.ApplyModifiedPropertiesWithoutUndo(); EditorSceneManager.SaveScene(scene, scenePath);
                string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/gpu-atlas/player/GpuAtlasProbe.exe"));
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = output,
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
                if (result.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("GPU atlas player build failed: " + result.summary.result);
            }
            finally { if (!batchUntitled) { EditorSceneManager.CloseScene(scene, true); if (previous.IsValid()) SceneManager.SetActiveScene(previous); } }
        }
    }
}
