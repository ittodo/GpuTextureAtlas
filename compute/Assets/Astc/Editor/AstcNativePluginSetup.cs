using UnityEditor;

namespace UnityCompute.Astc.Editor
{
    public static class AstcNativePluginSetup
    {
        public static string Configure()
        {
            ConfigureOne("Assets/Astc/Plugins/Android/arm64-v8a/libAstcGpuUpload.so", BuildTarget.Android);
            ConfigureOne("Assets/Astc/Plugins/iOS/AstcGpuUploadMetal.mm", BuildTarget.iOS);
            return "Configured Android ARM64 and iOS native plug-in importers. Device execution remains untested.";
        }
        static void ConfigureOne(string path, BuildTarget target)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as PluginImporter;
            if (importer == null) throw new System.IO.FileNotFoundException("Install native plug-in files first.", path);
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(target, true);
            if (target == BuildTarget.Android) importer.SetPlatformData(target, "CPU", "ARM64");
            importer.SaveAndReimport();
        }
    }
}
