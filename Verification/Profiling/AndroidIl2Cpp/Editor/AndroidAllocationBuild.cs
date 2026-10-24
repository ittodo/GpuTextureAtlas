using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
public static class AndroidAllocationBuild
{
    public static void Run()
    {
        try{
            var target=NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(target,"com.ittodo.il2cppallocationprobe");PlayerSettings.productName="IL2CPP Allocation Probe";
            PlayerSettings.SetScriptingBackend(target,ScriptingImplementation.IL2CPP);PlayerSettings.Android.targetArchitectures=AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion=(AndroidSdkVersions)26;PlayerSettings.Android.targetSdkVersion=(AndroidSdkVersions)36;PlayerSettings.Android.useCustomKeystore=false;
            PlayerSettings.SetManagedStrippingLevel(target,ManagedStrippingLevel.Minimal);PlayerSettings.gcIncremental=true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android,false);PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,new[]{GraphicsDeviceType.Vulkan});
            EditorUserBuildSettings.buildAppBundle=false;EditorUserBuildSettings.exportAsGoogleAndroidProject=false;
            var plugin=(PluginImporter)AssetImporter.GetAtPath("Assets/Plugins/Android/arm64-v8a/libIl2CppAllocationProbe.so");plugin.SetCompatibleWithAnyPlatform(false);plugin.SetCompatibleWithPlatform(BuildTarget.Android,true);plugin.SetPlatformData(BuildTarget.Android,"CPU","ARM64");plugin.SaveAndReimport();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=3;camera.transform.position=new Vector3(0,0,-10);
            var probe=new GameObject("Allocation verification").AddComponent<AndroidAllocationCalibration>();
            probe.encoder=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute");
            probe.conversion=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Atlas/GpuAtlasConvert.compute");
            EditorSceneManager.SaveScene(scene,"Assets/Probe.unity");
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{"Assets/Probe.unity"},locationPathName="Build/AllocationProbe.apk",target=BuildTarget.Android,options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded)throw new Exception("Build failed: "+report.summary.result);EditorApplication.Exit(0);
        }catch(Exception error){Debug.LogException(error);EditorApplication.Exit(1);}
    }
}
