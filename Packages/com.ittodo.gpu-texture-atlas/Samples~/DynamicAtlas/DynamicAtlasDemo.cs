using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityCompute.TextureCompression;

namespace GpuTextureAtlasSamples
{
    public sealed class DynamicAtlasDemo : MonoBehaviour
    {
        public ComputeShader astcEncoder, bc7Encoder, conversion;
        public Texture2D[] inputs;
        public GpuAtlasQuality quality = GpuAtlasQuality.Balanced;
        readonly GpuAtlasFrameBudget budget = new GpuAtlasFrameBudget();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly List<GpuAtlasRenderer> views = new List<GpuAtlasRenderer>();
        GpuTextureAtlas atlas; Task pending; TextureFormat format; ComputeShader encoder;
        bool ownsInputs, destroyed, sliced; int nextInput; string status = "Loading";

        void Start() { pending = Run(Initialize); }
        async Task Run(Func<Task> action)
        {
            try { await action(); if (!destroyed) { RefreshViews(); status = "Ready"; } }
            catch (OperationCanceledException) { status = "Canceled"; }
            catch (Exception error) { status = error.Message; Debug.LogException(error); }
        }
        async Task Initialize()
        {
            format = Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer
                ? TextureFormat.ASTC_4x4 : TextureFormat.BC7;
            encoder = format == TextureFormat.BC7 ? bc7Encoder : astcEncoder;
            if (inputs == null || inputs.Length == 0)
            {
                ownsInputs = true; inputs = new Texture2D[4];
                for (int i = 0; i < inputs.Length; i++)
                {
                    var texture = new Texture2D(64 + 16 * i, 64, TextureFormat.RGBA32, false, false) { name = "Sample " + i };
                    var colors = new Color[texture.width * texture.height];
                    for (int y = 0; y < texture.height; y++) for (int x = 0; x < texture.width; x++)
                    {
                        Color c = Color.HSVToRGB((i * .2f + x / (float)texture.width * .15f) % 1, .7f, .9f);
                        c.a = x < 4 || y < 4 || x >= texture.width - 4 || y >= texture.height - 4 ? 1 : .65f;
                        colors[y * texture.width + x] = c;
                    }
                    texture.SetPixels(colors); texture.Apply(false, true); inputs[i] = texture;
                }
            }
            atlas = await GpuTextureAtlas.PackTexturesAsync(inputs, format, true, encoder, conversion, quality,
                maxSize: 4096, paddingBlocks: 1, filterMode: FilterMode.Bilinear, frameBudget: budget, cancellationToken: lifetime.Token);
        }
        void RefreshViews()
        {
            if (atlas == null) return;
            for (int i = views.Count - 1; i >= 0; i--)
                if (!atlas.TryGetEntry(views[i].ImageId, out _)) { Destroy(views[i].gameObject); views.RemoveAt(i); }
            int index = 0;
            foreach (var entry in atlas.Entries)
            {
                var view = views.FirstOrDefault(v => v.ImageId == entry.SourceIndex);
                if (view == null)
                {
                    var created = new GameObject("Atlas ID " + entry.SourceIndex); created.transform.SetParent(transform);
                    view = created.AddComponent<GpuAtlasRenderer>(); view.Bind(atlas, entry.SourceIndex); views.Add(view);
                }
                var go = view.gameObject;
                go.transform.localPosition = new Vector3((index % 4 - 1.5f) * 1.7f, 1 - index / 4 * 1.7f, 0);
                view.Size = new Vector2(1.5f, 1.3f);
                view.PixelsPerUnit = 64; view.Border = new Vector4(8, 8, 8, 8); view.DrawMode = sliced ? GpuAtlasDrawMode.Sliced : GpuAtlasDrawMode.Simple;
                index++;
            }
        }
        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 430, 260), GUI.skin.box);
            GUILayout.Label("GpuTextureAtlas · " + status);
            GUILayout.Label(atlas == null ? "Preparing GPU resources" : $"{atlas.Entries.Count} images · {atlas.Width}×{atlas.Height} · version {atlas.Version}");
            GUI.enabled = atlas != null && (pending == null || pending.IsCompleted);
            if (GUILayout.Button("Add image")) pending = Run(async () => await atlas.AddAsync(inputs[nextInput++ % inputs.Length], encoder, conversion, quality, frameBudget: budget, cancellationToken: lifetime.Token));
            if (GUILayout.Button("Remove first image") && atlas.Entries.Count > 0) { atlas.Remove(atlas.Entries[0].SourceIndex); RefreshViews(); }
            if (GUILayout.Button("Repack")) pending = Run(() => atlas.RepackAsync(budget, lifetime.Token));
            if (GUILayout.Button("Toggle 9-slice")) { sliced = !sliced; foreach (var view in views) view.DrawMode = sliced ? GpuAtlasDrawMode.Sliced : GpuAtlasDrawMode.Simple; }
            GUI.enabled = true; GUILayout.Label("SpriteMask: use a URP 2D Renderer and set MaskInteraction on each view.");
            GUILayout.EndArea();
        }
        async void OnDestroy()
        {
            destroyed = true; lifetime.Cancel();
            try { if (pending != null) await pending; }
            finally
            {
                foreach (var view in views) if (view != null) view.Unbind();
                atlas?.Dispose();
                if (ownsInputs && inputs != null) foreach (var texture in inputs) if (texture != null) Destroy(texture);
                lifetime.Dispose();
            }
        }
    }
}
