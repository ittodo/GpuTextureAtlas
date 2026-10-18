using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.Astc
{
    // Only disposal allocates an entry. No per-frame retirement work is added by EncodeFrame.
    internal sealed class AstcGpuRetirement : MonoBehaviour
    {
        struct Pending { public GraphicsFence fence; public Action release; }
        static AstcGpuRetirement instance;
        readonly List<Pending> pending = new List<Pending>();

        internal static void Retire(GraphicsFence fence, Action release)
        {
            if (instance == null)
            {
                var host = new GameObject("ASTC GPU resource retirement") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(host);
                instance = host.AddComponent<AstcGpuRetirement>();
            }
            instance.pending.Add(new Pending { fence = fence, release = release });
        }

        void Update()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (!pending[i].fence.passed) continue;
                pending[i].release();
                pending.RemoveAt(i);
            }
        }
        // At process exit, leave outstanding native packets allocated. The OS reclaims them.
        // Freeing them from OnDestroy could race a queued render-thread event.
    }
}
