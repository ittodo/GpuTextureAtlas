using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCompute.Astc.Editor
{
    public sealed class AstcEncoderWindow : EditorWindow
    {
        Texture source;
        AstcBlockSize size = AstcBlockSize.Block6x6;
        AstcQuality quality = AstcQuality.Balanced;
        bool srgb = true, sampledInputIsSrgb;
        bool busy;
        string status;

        [MenuItem("Tools/ASTC/Encoder")]
        public static void Open() => GetWindow<AstcEncoderWindow>("ASTC Encoder");

        void OnGUI()
        {
            source = (Texture)EditorGUILayout.ObjectField("Source", source, typeof(Texture), false);
            size = (AstcBlockSize)EditorGUILayout.EnumPopup("Block size", size);
            quality = (AstcQuality)EditorGUILayout.EnumPopup("Quality", quality);
            srgb = EditorGUILayout.Toggle("Output sRGB", srgb);
            sampledInputIsSrgb = EditorGUILayout.Toggle(new GUIContent("Sampled input is sRGB", "Enable when GPU loads return sRGB values, for example an sRGB image in a Gamma color space project."), sampledInputIsSrgb);
            EditorGUILayout.HelpBox("LDR RGBA, one mip level. Export works on desktop GPUs with compute support. Display requires ASTC texture support.", MessageType.Info);
            using (new EditorGUI.DisabledScope(source == null || busy))
                if (GUILayout.Button(busy ? "Encoding..." : "Encode and export .astc")) Export();
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
        }

        async void Export()
        {
            string path = EditorUtility.SaveFilePanel("Export ASTC", "", source.name + ".astc", "astc");
            if (string.IsNullOrEmpty(path)) return;
            busy = true;
            try
            {
                using var encoder = new AstcEncoder(AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute"));
                var image = SystemInfo.supportsAsyncGPUReadback ? await encoder.EncodeAsync(source, size, srgb, sampledInputIsSrgb, quality) : encoder.Encode(source, size, srgb, sampledInputIsSrgb, quality);
                File.WriteAllBytes(path, image.ToAstcFile());
                status = $"Saved {image.Width}x{image.Height}, {image.Data.Length:N0} payload bytes: {path}";
            }
            catch (Exception error) { status = error.Message; Debug.LogException(error); }
            finally { busy = false; if (this != null) Repaint(); }
        }

        [MenuItem("Tools/ASTC/Create demo scene")]
        public static void CreateDemoScene()
        {
            const string path = "Assets/Astc/Samples/AstcDemo.unity";
            if (File.Exists(path)) { EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(path)); return; }
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject("ASTC Demo");
            SceneManager.MoveGameObjectToScene(root, scene);
            var demo = root.AddComponent<AstcDemo>();
            demo.encoderShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute");
            var camera = new GameObject("Camera");
            SceneManager.MoveGameObjectToScene(camera, scene);
            camera.AddComponent<Camera>().backgroundColor = new Color(.08f, .09f, .12f);
            Directory.CreateDirectory("Assets/Astc/Samples");
            EditorSceneManager.SaveScene(scene, path);
            EditorSceneManager.CloseScene(scene, true);
            SceneManager.SetActiveScene(previous);
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(path));
        }
    }
}
