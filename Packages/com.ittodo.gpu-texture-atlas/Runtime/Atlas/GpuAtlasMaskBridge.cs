using System;
using UnityEngine;

namespace UnityCompute.TextureCompression
{
    internal static class GpuAtlasMaskBridge
    {
        internal static Action<MeshRenderer, SpriteMaskInteraction> Apply;
        internal static void Set(MeshRenderer renderer, SpriteMaskInteraction interaction)
        {
            if (Apply != null) Apply(renderer, interaction);
            else if (interaction != SpriteMaskInteraction.None)
                throw new NotSupportedException("SpriteMask requires URP 17.7 or later and a 2D Renderer.");
        }
    }
}
