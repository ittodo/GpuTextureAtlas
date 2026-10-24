using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.PackageManager;

namespace UnityCompute.TextureCompression.Editor
{
    // Verification tools use this to resolve virtual UPM asset paths to local files.
    public static class GpuAtlasProjectPaths
    {
        public static string FilePath(string assetPath)
        {
            if (!assetPath.StartsWith("Packages/")) return assetPath;
            var info = PackageInfo.FindForAssetPath(assetPath);
            if (info == null) throw new FileNotFoundException("Package asset is not registered.", assetPath);
            return Path.Combine(info.resolvedPath, assetPath.Substring(info.assetPath.Length + 1));
        }
        public static void CopyShaderProbe(string source, string destination)
        {
            string text = File.ReadAllText(FilePath(source));
            string directory = Path.GetDirectoryName(source).Replace('\\', '/');
            text = Regex.Replace(text, "#include \\\"([^\\\"]+)\\\"", match =>
                match.Groups[1].Value.StartsWith("Packages/") ? match.Value : "#include \"" + directory + "/" + match.Groups[1].Value + "\"");
            File.WriteAllText(destination, text);
        }
    }
}
