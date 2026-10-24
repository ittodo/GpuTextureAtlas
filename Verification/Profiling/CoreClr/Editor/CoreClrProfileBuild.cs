using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
public static class CoreClrProfileBuild {
    public static void Run() {
        try {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.CoreCLR);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone,ManagedStrippingLevel.Minimal);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D12});
            Debug.Log("CORECLR_PROFILE_BUILD backend="+PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone));
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                scenes=new[]{"Assets/Probe.unity"},target=BuildTarget.StandaloneWindows64,
                locationPathName="Build/CoreClrPackageProbe.exe",options=BuildOptions.Development
            });
            if(report.summary.result!=BuildResult.Succeeded)throw new Exception("CoreCLR build failed: "+report.summary.result);
            EditorApplication.Exit(0);
        } catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}
    }
}
