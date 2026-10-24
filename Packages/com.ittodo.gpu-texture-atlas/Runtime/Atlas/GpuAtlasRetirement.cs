using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.TextureCompression
{
    internal sealed class GpuAtlasRetirement : MonoBehaviour
    {
        struct Pending { internal GraphicsFence fence; internal Action release; }
        static readonly List<Pending> pending = new List<Pending>();
#if !UNITY_EDITOR
        static GpuAtlasRetirement instance;
#endif
        internal static void Retire(GraphicsFence fence, Action release)
        {
            pending.Add(new Pending { fence = fence, release = release });
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= Poll;
            UnityEditor.EditorApplication.update += Poll;
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#else
            if (instance == null)
            {
                var host = new GameObject("GPU atlas resource retirement") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(host); instance = host.AddComponent<GpuAtlasRetirement>();
            }
#endif
        }
        void Update() => Poll();
        static void Poll()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (!pending[i].fence.passed) continue;
                var release = pending[i].release; pending.RemoveAt(i); release();
            }
#if UNITY_EDITOR
            if (pending.Count == 0) UnityEditor.EditorApplication.update -= Poll;
            else { UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); UnityEditor.SceneView.RepaintAll(); }
#endif
        }
    }
}
