using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.TextureCompression
{
    public enum GpuAtlasDrawMode { Simple, Sliced }

    /// <summary>Unlit atlas rendering by stable image ID. Atlas edits update the shared GPU lookup table automatically.</summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("Rendering/GPU Atlas Renderer")]
    [SelectionBase]
    public sealed class GpuAtlasRenderer : MonoBehaviour
    {
        [SerializeField] Shader shader;
        [SerializeField] int imageId = -1;
        [SerializeField] bool useNativeSize = true;
        [SerializeField, Min(.0001f)] float pixelsPerUnit = 100;
        [SerializeField] Vector2 size = Vector2.one;
        [SerializeField] Vector2 pivot = new Vector2(.5f, .5f);
        [SerializeField] GpuAtlasDrawMode drawMode;
        [SerializeField, Tooltip("Source pixel borders: left, bottom, right, top. Sliced mode only.")] Vector4 border;
        [SerializeField, Tooltip("Requires a URP 2D Renderer with a depth/stencil buffer.")] SpriteMaskInteraction maskInteraction;
        [SerializeField] Color color = Color.white;
        [SerializeField] bool flipX, flipY;
        [SerializeField] int sortingLayerId, sortingOrder;
        GpuTextureAtlas atlas;
        GpuAtlasRenderState state;
        GameObject drawObject;
        MeshRenderer drawRenderer;
        MeshFilter filter;
        MaterialPropertyBlock properties;
        bool propertiesConfigured;
        Vector4 configuredSourceBorder, configuredDestinationBorder;

        public GpuTextureAtlas Atlas => atlas;
        public int ImageId => imageId;
        public MeshRenderer Renderer => drawRenderer;
        public int LookupCapacity => state != null ? state.Capacity : 0;
        public int LookupUploadCount => state != null ? state.UploadCount : 0;
        public bool UseNativeSize { get => useNativeSize; set { useNativeSize = value; RefreshFromState(); } }
        public float PixelsPerUnit { get => pixelsPerUnit; set { if (!Finite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); pixelsPerUnit = value; RefreshFromState(); } }
        public Vector2 Size { get => size; set { if (!Finite(value.x) || !Finite(value.y) || value.x < 0 || value.y < 0) throw new ArgumentOutOfRangeException(nameof(value)); size = value; useNativeSize = false; RefreshFromState(); } }
        public Vector2 Pivot { get => pivot; set { if (!Finite(value.x) || !Finite(value.y)) throw new ArgumentOutOfRangeException(nameof(value)); pivot = value; RefreshFromState(); } }
        public GpuAtlasDrawMode DrawMode { get => drawMode; set { if (value != GpuAtlasDrawMode.Simple && value != GpuAtlasDrawMode.Sliced) throw new ArgumentOutOfRangeException(nameof(value)); drawMode = value; RefreshFromState(); } }
        /// <summary>Left, bottom, right, top in the packed image's valid pixels. Borders use PixelsPerUnit in local space.</summary>
        public Vector4 Border { get => border; set { for (int i = 0; i < 4; i++) if (!Finite(value[i]) || value[i] < 0) throw new ArgumentOutOfRangeException(nameof(value)); border = value; RefreshFromState(); } }
        public SpriteMaskInteraction MaskInteraction { get => maskInteraction; set { if (!ValidMask(value)) throw new ArgumentOutOfRangeException(nameof(value)); maskInteraction = value; state?.RefreshInstancing(); RefreshFromState(); } }
        public Color Color { get => color; set { color = value; RefreshFromState(); } }
        public bool FlipX { get => flipX; set { flipX = value; RefreshFromState(); } }
        public bool FlipY { get => flipY; set { flipY = value; RefreshFromState(); } }
        public int SortingLayerId { get => sortingLayerId; set { sortingLayerId = value; RefreshFromState(); } }
        public int SortingOrder { get => sortingOrder; set { sortingOrder = value; RefreshFromState(); } }

        /// <summary>Call on the main thread after initial packing completes. An absent ID stays invisible.</summary>
        public void Bind(GpuTextureAtlas atlas, int imageId, Shader shader = null)
        {
            if (atlas == null) throw new ArgumentNullException(nameof(atlas));
            if (atlas.IsDisposed) throw new ObjectDisposedException(nameof(atlas));
            if (!atlas.IsReady) throw new InvalidOperationException("Await initial atlas packing before binding a renderer.");
            if (imageId < 0) throw new ArgumentOutOfRangeException(nameof(imageId));
            var selected = shader != null ? shader : this.shader != null ? this.shader : Resources.Load<Shader>("GpuAtlasUnlit");
            if (selected == null || !selected.isSupported || SystemInfo.graphicsShaderLevel < 45)
                throw new NotSupportedException("GPU atlas rendering requires the GpuAtlasUnlit shader and structured buffer support (shader model 4.5).");
            if (state != null && !state.Matches(atlas, selected)) Detach();
            this.atlas = atlas; this.imageId = imageId; this.shader = selected; propertiesConfigured = false;
            if (isActiveAndEnabled) { try { Attach(); } catch { Detach(); throw; } }
        }
        public void Unbind() { Detach(); atlas = null; imageId = -1; }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool ValidMask(SpriteMaskInteraction value) => value == SpriteMaskInteraction.None || value == SpriteMaskInteraction.VisibleInsideMask || value == SpriteMaskInteraction.VisibleOutsideMask;
        void OnEnable() { if (atlas != null && !atlas.IsDisposed) Attach(); }
        void OnDisable() => Detach();
        void OnDestroy() { Detach(); GpuAtlasRenderState.DestroyObject(drawObject); }
        void OnValidate()
        {
            if (!Finite(pixelsPerUnit) || pixelsPerUnit <= 0) pixelsPerUnit = 100;
            if (!Finite(size.x) || !Finite(size.y)) size = Vector2.one; size = Vector2.Max(size, Vector2.zero);
            if (!Finite(pivot.x) || !Finite(pivot.y)) pivot = new Vector2(.5f, .5f);
            if (drawMode != GpuAtlasDrawMode.Simple && drawMode != GpuAtlasDrawMode.Sliced) drawMode = GpuAtlasDrawMode.Simple;
            for (int i = 0; i < 4; i++) if (!Finite(border[i]) || border[i] < 0) border[i] = 0;
            if (!ValidMask(maskInteraction)) maskInteraction = SpriteMaskInteraction.None;
            state?.RefreshInstancing();
            if (state != null) RefreshFromState();
        }
        void LateUpdate() { if (drawObject != null && drawObject.layer != gameObject.layer) drawObject.layer = gameObject.layer; }
        void Attach()
        {
            if (state == null) state = GpuAtlasRenderState.Acquire(atlas, shader, this);
            if (drawObject == null)
            {
                drawObject = new GameObject("GPU atlas draw") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
                drawObject.transform.SetParent(transform, false); drawObject.layer = gameObject.layer;
                filter = drawObject.AddComponent<MeshFilter>(); drawRenderer = drawObject.AddComponent<MeshRenderer>();
                drawRenderer.shadowCastingMode = ShadowCastingMode.Off; drawRenderer.receiveShadows = false;
                drawRenderer.lightProbeUsage = LightProbeUsage.Off; drawRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                properties = new MaterialPropertyBlock();
            }
            filter.sharedMesh = state.Grid; drawRenderer.sharedMaterial = state.Material; RefreshFromState();
        }
        void Detach()
        {
            propertiesConfigured = false;
            if (drawRenderer != null) { drawRenderer.enabled = false; drawRenderer.sharedMaterial = null; drawRenderer.SetPropertyBlock(null); }
            if (filter != null) filter.sharedMesh = null;
            var previous = state; state = null; previous?.Release(this);
        }
        internal void StateReleased(GpuAtlasRenderState released)
        {
            if (!ReferenceEquals(state, released)) return;
            state = null;
            propertiesConfigured = false;
            if (drawRenderer != null) { drawRenderer.enabled = false; drawRenderer.sharedMaterial = null; drawRenderer.SetPropertyBlock(null); }
            if (filter != null) filter.sharedMesh = null;
        }
        internal void RefreshFromState(bool parametersChanged = true)
        {
            if (state == null || drawRenderer == null) return;
            bool exists = state.TryGetEntry(imageId, out var entry);
            Vector2 dimensions = exists ? useNativeSize ? (Vector2)entry.PixelRect.size / pixelsPerUnit : size : Vector2.zero;
            Vector4 sourceBorder = Vector4.one / 3, destinationBorder = sourceBorder;
            if (exists && drawMode == GpuAtlasDrawMode.Sliced)
            {
                var pixels = border;
                FitBorders(ref pixels.x, ref pixels.z, entry.PixelRect.width);
                FitBorders(ref pixels.y, ref pixels.w, entry.PixelRect.height);
                sourceBorder = new Vector4(pixels.x / entry.PixelRect.width, pixels.y / entry.PixelRect.height, pixels.z / entry.PixelRect.width, pixels.w / entry.PixelRect.height);
                // Work in double precision to keep finite input borders safe even at extreme scales.
                FitDestination(pixels.x, pixels.z, pixelsPerUnit, dimensions.x, out destinationBorder.x, out destinationBorder.z);
                FitDestination(pixels.y, pixels.w, pixelsPerUnit, dimensions.y, out destinationBorder.y, out destinationBorder.w);
            }
            if (parametersChanged || !propertiesConfigured || sourceBorder != configuredSourceBorder || destinationBorder != configuredDestinationBorder)
            {
                properties.SetInteger("_AtlasImageId", imageId);
                properties.SetVector("_AtlasTint", new Vector4(color.r, color.g, color.b, color.a));
                properties.SetVector("_AtlasSourceBorder", sourceBorder);
                properties.SetVector("_AtlasDestinationBorder", destinationBorder);
                drawRenderer.SetPropertyBlock(properties); drawRenderer.sortingLayerID = sortingLayerId; drawRenderer.sortingOrder = sortingOrder;
                GpuAtlasMaskBridge.Set(drawRenderer, maskInteraction);
                configuredSourceBorder = sourceBorder; configuredDestinationBorder = destinationBorder;
                propertiesConfigured = true;
            }
            drawRenderer.enabled = exists && isActiveAndEnabled;
            if (!exists) return;
            var scale = new Vector3(dimensions.x * (flipX ? -1 : 1), dimensions.y * (flipY ? -1 : 1), 1);
            var position = new Vector3((.5f - pivot.x) * scale.x, (.5f - pivot.y) * scale.y, 0);
            if (drawObject.transform.localScale != scale) drawObject.transform.localScale = scale;
            if (drawObject.transform.localPosition != position) drawObject.transform.localPosition = position;
        }
        static void FitBorders(ref float first, ref float last, float limit)
        {
            double total = (double)first + last;
            if (total > limit) { double scale = limit / total; first = (float)(first * scale); last = (float)(last * scale); }
        }
        static void FitDestination(float first, float last, float ppu, float size, out float firstFraction, out float lastFraction)
        {
            double pixels = (double)ppu * size, total = (double)first + last;
            double divisor = Math.Max(pixels, total);
            firstFraction = divisor > 0 ? (float)(first / divisor) : 0;
            lastFraction = divisor > 0 ? (float)(last / divisor) : 0;
        }
    }
}
