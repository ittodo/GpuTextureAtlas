using System;
using UnityEditor;

namespace UnityCompute.Bc7.Editor
{
    public static class Bc7NativePluginSetup
    {
        public static string Configure()
        {
            const string path="Packages/com.ittodo.gpu-texture-atlas/Plugins/Bc7/Windows/x86_64/Bc7GpuUpload.dll";
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var importer=AssetImporter.GetAtPath(path) as PluginImporter;
            if(importer==null)throw new InvalidOperationException("Build the BC7 Windows plug-in first.");
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);importer.SetEditorData("OS","Windows");importer.SetEditorData("CPU","x86_64");
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64,true);
            importer.SetPlatformData(BuildTarget.StandaloneWindows64,"CPU","x86_64");
            importer.SaveAndReimport();return "BC7 D3D12 Windows x64 plug-in configured for Editor and Player.";
        }
    }
}
