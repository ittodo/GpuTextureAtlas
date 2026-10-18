using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.Bc7
{
    // Retirement only runs after Dispose. EncodeFrame adds no queue entries.
    internal sealed class Bc7GpuRetirement:MonoBehaviour
    {
        struct Pending { public GraphicsFence fence;public Action release; }
        static readonly List<Pending> pending=new List<Pending>();
#if !UNITY_EDITOR
        static Bc7GpuRetirement instance;
#endif
        internal static void Retire(GraphicsFence fence,Action release)
        {
            pending.Add(new Pending{fence=fence,release=release});
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update-=Poll;
            UnityEditor.EditorApplication.update+=Poll;
            RequestEditorFrame();
#else
            if(instance==null)
            {
                var host=new GameObject("BC7 GPU resource retirement"){hideFlags=HideFlags.HideInHierarchy};
                DontDestroyOnLoad(host);instance=host.AddComponent<Bc7GpuRetirement>();
            }
#endif
        }
        void Update()=>Poll();
        static void Poll()
        {
            for(int i=pending.Count-1;i>=0;i--)
            {
                if(!pending[i].fence.passed)continue;
                pending[i].release();pending.RemoveAt(i);
            }
#if UNITY_EDITOR
            if(pending.Count==0)UnityEditor.EditorApplication.update-=Poll;
            else RequestEditorFrame();
#endif
        }
#if UNITY_EDITOR
        static void RequestEditorFrame()
        {
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
        }
#endif
        // At process exit, pending packets and their retained native resources are left for the OS.
        // Never free a packet while a render-thread callback can still use it.
    }
}
