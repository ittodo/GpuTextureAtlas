using System.Linq;
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCompute.Bc7.Editor
{
    public static class Bc7GpuPlayerBuild
    {
        public static string Build()
        {
            if(!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone,BuildTarget.StandaloneWindows64))
                throw new NotSupportedException("Windows x64 player build support is unavailable.");
            var type=AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityCompute.Bc7.Verification.Bc7GpuPlayerProbe")).FirstOrDefault(t => t != null);
            if(type==null)throw new InvalidOperationException("Install the temporary verification source and recompile first.");
            const string scenePath="Assets/Bc7/VerificationTemp/Probe.unity";
            var previous=SceneManager.GetActiveScene();var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);var component=new GameObject("BC7 standalone verification").AddComponent(type);
                var serialized=new SerializedObject(component);
                foreach(var item in new[]{new[]{"encoderShader","Bc7Encoder"},new[]{"fillShader","Bc7StreamValidation"},new[]{"decodeShader","Bc7DecodeHardware"}})
                    serialized.FindProperty(item[0]).objectReferenceValue=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/"+item[1]+".compute");
                serialized.ApplyModifiedPropertiesWithoutUndo();EditorSceneManager.SaveScene(scene,scenePath);
                string output=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.validation/bc7/player/Bc7GpuProbe.exe"));Directory.CreateDirectory(Path.GetDirectoryName(output));
                var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{scenePath},locationPathName=output,target=BuildTarget.StandaloneWindows64,options=BuildOptions.Development});
                if(result.summary.result!=BuildResult.Succeeded)throw new InvalidOperationException("BC7 verification player build failed: "+result.summary.result);
                return output;
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(previous.IsValid())SceneManager.SetActiveScene(previous);}
        }
    }
}
