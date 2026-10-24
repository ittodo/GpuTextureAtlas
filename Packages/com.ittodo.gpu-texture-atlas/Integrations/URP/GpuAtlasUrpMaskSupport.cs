using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityCompute.TextureCompression
{
    internal static class GpuAtlasUrpMaskSupport
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register() { GpuAtlasMaskBridge.Apply = Apply; }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterEditor() { Register(); }
#endif
        static void Apply(MeshRenderer renderer, SpriteMaskInteraction interaction) { renderer.SetSpriteMaskInteraction(interaction); }
    }
}
