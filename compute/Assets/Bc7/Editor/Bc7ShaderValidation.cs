using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.Bc7.Editor
{
    public static class Bc7ShaderValidation
    {
        public static string Run()
        {
            const string source="Assets/Bc7/Shaders/Bc7Encoder.compute",probe="Assets/Bc7/Shaders/Bc7CompileProbe.compute";
            File.Copy(source,probe,true);AssetDatabase.ImportAsset(probe,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
            try
            {
                var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>(probe);
                var compiler=typeof(ShaderUtil).GetMethod("CompileComputeShaderVariant",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
                if(compiler==null)throw new NotSupportedException("Unity compute variant compiler API changed.");
                var results=new List<string>();
                foreach(string name in new[]{"EncodeBc7Fast","EncodeBc7Balanced","EncodeBc7HighQuality"})
                foreach(var api in new[]{GraphicsDeviceType.Direct3D11,GraphicsDeviceType.Direct3D12,GraphicsDeviceType.Vulkan,GraphicsDeviceType.Metal})
                {
                    var target=api==GraphicsDeviceType.Metal?BuildTarget.StandaloneOSX:BuildTarget.StandaloneWindows64;
                    var result=(ShaderData.VariantCompileInfo)compiler.Invoke(null,new object[]{shader,shader.FindKernel(name),new string[0],api,target});
                    if(!result.Success||result.ShaderData==null||result.ShaderData.Length==0||result.Messages.Length!=0)
                        throw new InvalidOperationException(name+"/"+api+": "+string.Join("; ",result.Messages.Select(m=>m.message)));
                    results.Add($"PASS: {name} {target}/{api}, {result.ShaderData.Length} bytes, zero diagnostics.");
                }
                string summary=string.Join("\n",results),directory=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.validation/bc7"));
                Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"shader-compilers.txt"),summary);return summary;
            }
            finally{AssetDatabase.DeleteAsset(probe);}
        }
    }
}
