using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GpuTextureAtlasSamples.Editor
{
    public static class CreateDemoScene
    {
        [MenuItem("Tools/GpuTextureAtlas/Create sample scene")]
        public static void Create()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
            camera.orthographic = true; camera.orthographicSize = 3; camera.transform.position = new Vector3(0, 0, -10);
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.04f, .05f, .08f);
            var sample = new GameObject("Dynamic atlas").AddComponent<DynamicAtlasDemo>();
            const string root = "Packages/com.ittodo.gpu-texture-atlas/";
            sample.astcEncoder = AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "Shaders/Astc/AstcEncoder.compute");
            sample.bc7Encoder = AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "Shaders/Bc7/Bc7Encoder.compute");
            sample.conversion = AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "Shaders/Atlas/GpuAtlasConvert.compute");
            Selection.activeGameObject = sample.gameObject;
        }
    }
}
