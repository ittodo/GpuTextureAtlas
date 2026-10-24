using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCompute.Bc7.Editor
{
    public sealed class Bc7EncoderWindow:EditorWindow
    {
        Texture source;Bc7Quality quality=Bc7Quality.Fast;bool srgb=true,inputSrgb,busy;string status;
        [MenuItem("Tools/BC7/Encoder")]
        public static void Open()=>GetWindow<Bc7EncoderWindow>("BC7 Encoder");
        void OnGUI()
        {
            source=(Texture)EditorGUILayout.ObjectField("Source",source,typeof(Texture),false);
            quality=(Bc7Quality)EditorGUILayout.EnumPopup("Quality",quality);srgb=EditorGUILayout.Toggle("Output sRGB",srgb);
            inputSrgb=EditorGUILayout.Toggle(new GUIContent("Sampled input is sRGB","Enable when GPU loads return sRGB code values, such as a Gamma project."),inputSrgb);
            EditorGUILayout.HelpBox("LDR RGBA, BC7 4x4, one mip. Desktop format. DDS export flips source rows for top-first file order.",MessageType.Info);
            using(new EditorGUI.DisabledScope(source==null||busy))if(GUILayout.Button(busy?"Encoding...":"GPU encode and export DDS"))Export();
            if(!string.IsNullOrEmpty(status))EditorGUILayout.HelpBox(status,MessageType.None);
        }
        async void Export()
        {
            string path=EditorUtility.SaveFilePanel("Export BC7 DDS","",source.name+".dds","dds");if(string.IsNullOrEmpty(path))return;
            busy=true;
            try
            {
                using var encoder=new Bc7Encoder(AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute"));
                var image=SystemInfo.supportsAsyncGPUReadback?await encoder.EncodeAsync(source,srgb,inputSrgb,quality,true):encoder.Encode(source,srgb,inputSrgb,quality,true);
                File.WriteAllBytes(path,image.ToDdsFile());status=$"Saved {image.Width}x{image.Height}, {image.Data.Length:N0} BC7 bytes: {path}";
            }
            catch(Exception error){status=error.Message;Debug.LogException(error);}
            finally{busy=false;if(this!=null)Repaint();}
        }
    }
}
