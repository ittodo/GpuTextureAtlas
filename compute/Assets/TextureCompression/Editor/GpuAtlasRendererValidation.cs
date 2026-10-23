using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

#if GPU_ATLAS_PLAYER_PROBE
namespace UnityCompute.TextureCompression.Verification
#else
namespace UnityCompute.TextureCompression.Editor
#endif
{
    // Also copied into the verification Player. All readback, CPU sampling and reflection stay in this test file.
    public static class GpuAtlasRendererValidation
    {
        [Serializable] sealed class Report
        {
            public bool passed;
            public string error;
            public int cases, frames, pixels, tableUploads, maximumCapacity, slicedFrames, maskFrames;
            public float maximumDifference;
        }
        sealed class Image { internal int width, height; internal Color[] pixels; }
        const int Width = 96, Height = 48, Layer = 29;
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Destroy(Object value)
        {
            if (value == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) Object.DestroyImmediate(value); else
#endif
                Object.Destroy(value);
        }
        static Color[] Read(Texture texture, int w, int h, ComputeShader decoder)
        {
            int kernel = decoder.FindKernel("ReadLinear"); using var buffer = new ComputeBuffer(w * h, 16);
            decoder.SetInts("_Size", w, h); decoder.SetInt("_Mip", 0); decoder.SetTexture(kernel, "_Source", texture); decoder.SetBuffer(kernel, "_Linear", buffer);
            decoder.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1); var pixels = new Color[w * h]; buffer.GetData(pixels); return pixels;
        }
        static Image Capture(GpuTextureAtlas atlas, int id, ComputeShader decoder)
        {
            var entry = atlas.GetEntry(id); var full = Read(atlas.Texture, atlas.Width, atlas.Height, decoder); var r = entry.PixelRect;
            var result = new Image { width = r.width, height = r.height, pixels = new Color[r.width * r.height] };
            for (int y = 0; y < r.height; y++) Array.Copy(full, (r.y + y) * atlas.Width + r.x, result.pixels, y * r.width, r.width);
            return result;
        }
        public static async Task<string> Run(string bundlePath, ComputeShader encoder, ComputeShader convert, ComputeShader decoder, string output,
            TextureFormat targetFormat = TextureFormat.BC7)
        {
            Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "environment.txt"), "");
            var report = new Report(); AssetBundle bundle = null; GpuTextureAtlas atlas = null;
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            float previousRenderScale = pipeline != null ? pipeline.renderScale : 1;
            GameObject cameraObject = null, mainObject = null, otherObject = null, maskObject = null; RenderTexture target = null;
            Texture2D maskTexture = null; Sprite maskSprite = null;
            try
            {
                // The CPU reference uses the target pixel grid. Upscaling adds a second filter.
                // Keep this setting local to verification and restore it even when a check fails.
                if (pipeline != null) pipeline.renderScale = 1;
                File.AppendAllText(Path.Combine(output, "environment.txt"), $"Render scale: {previousRenderScale} -> 1 for pixel comparison\n");
                cameraObject = new GameObject("Atlas renderer verification camera"); var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false; camera.orthographic = true; camera.orthographicSize = .5f; camera.aspect = 2;
                camera.transform.position = new Vector3(0, 0, -10); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << Layer; camera.allowMSAA = false; camera.allowHDR = true;
                var data = cameraObject.AddComponent<UniversalAdditionalCameraData>(); data.renderPostProcessing = false; data.renderShadows = false; data.antialiasing = AntialiasingMode.None;
                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "Atlas renderer verification target" };
                Require(target.Create(), "Cannot create the renderer verification target."); camera.targetTexture = target;
                mainObject = new GameObject("Atlas renderer verification main") { layer = Layer };
                otherObject = new GameObject("Atlas renderer verification other") { layer = Layer };
                var main = mainObject.AddComponent<GpuAtlasRenderer>(); var other = otherObject.AddComponent<GpuAtlasRenderer>();
                maskTexture = new Texture2D(16, 16, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                maskTexture.SetPixels(Enumerable.Range(0, 256).Select(i => new Color(1, 1, 1, MaskAlpha(i % 16, i / 16))).ToArray()); maskTexture.Apply(false, true);
                maskSprite = Sprite.Create(maskTexture, new Rect(0, 0, 16, 16), Vector2.one * .5f, 16, 0, SpriteMeshType.FullRect);
                maskObject = new GameObject("Atlas renderer verification mask") { layer = Layer }; var mask = maskObject.AddComponent<SpriteMask>();
                File.AppendAllText(Path.Combine(output, "environment.txt"), $"Default mask shader: {mask.sharedMaterial?.shader?.name}\n");
                mask.sprite = maskSprite; mask.alphaCutoff = .5f; mask.isCustomRangeActive = true; mask.backSortingOrder = -20; mask.frontSortingOrder = 20; mask.enabled = false;
                main.Size = other.Size = new Vector2(2, 1); otherObject.transform.position = Vector3.right * 100;
                foreach (int rendererIndex in new[] { 0, 1 })
                {
                data.SetRenderer(rendererIndex);
                Require(rendererIndex == 0 || data.scriptableRenderer.GetType().Name == "Renderer2D", "Configure the GPU atlas 2D Renderer at index 1 before verification.");
                var backDepth = typeof(Graphics).GetProperty("backBufferHasDepthStencil", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                File.AppendAllText(Path.Combine(output, "environment.txt"), $"{data.scriptableRenderer.GetType().Name}, backBufferHasDepthStencil={backDepth}, targetDepth={target.depthStencilFormat}\n");
                foreach (bool srgb in new[] { false, true })
                {
                    main.DrawMode = other.DrawMode = GpuAtlasDrawMode.Simple; main.MaskInteraction = other.MaskInteraction = SpriteMaskInteraction.None;
                    main.Size = other.Size = new Vector2(2, 1); main.Color = other.Color = Color.white; main.PixelsPerUnit = 16;
                    bundle = AssetBundle.LoadFromFile(bundlePath); Require(bundle != null, "Cannot load renderer inputs.");
                    var inputs = bundle.LoadAllAssets<Texture2D>(); string sourcePrefix = srgb ? "srgb-" : "linear-";
                    string prefix = (rendererIndex == 0 ? "forward-" : "2d-") + sourcePrefix;
                    var seed = inputs.Single(t => t.name == sourcePrefix + "0");
                    atlas = await GpuTextureAtlas.PackTexturesAsync(new[] { seed }, targetFormat, srgb, maxSize: 1024, paddingBlocks: 1, filterMode: FilterMode.Bilinear);
                    var images = new Dictionary<int, Image> { { 0, Capture(atlas, 0, decoder) } };
                    main.Bind(atlas, 0); other.Bind(atlas, 0);
                    Require(ReferenceEquals(main.Renderer.sharedMaterial, other.Renderer.sharedMaterial) && ReferenceEquals(main.Renderer.GetComponent<MeshFilter>().sharedMesh, other.Renderer.GetComponent<MeshFilter>().sharedMesh), "Renderers did not share their material and quad.");
                    var shared = main.Renderer.sharedMaterial;
                    other.enabled = false;
                    Require(other.LookupCapacity == 0 && main.Renderer.sharedMaterial == shared && !other.Renderer.enabled, "Disabling one renderer released a shared state that is still used.");
                    other.enabled = true; Require(other.Renderer.sharedMaterial == shared && other.Renderer.enabled, "A renderer did not rejoin the shared state.");
                    main.enabled = other.enabled = false;
                    Require(main.LookupCapacity == 0 && other.LookupCapacity == 0, "The last disabled renderer retained the shared state.");
                    main.enabled = other.enabled = true;
                    Require(main.Renderer.sharedMaterial != shared && main.Renderer.sharedMaterial == other.Renderer.sharedMaterial, "Re-enabling did not create and share a fresh state.");
                    int uploads = main.LookupUploadCount;
                    await Check(camera, target, decoder, new[] { main, other }, images, report, Path.Combine(output, prefix + "initial.png"));
                    main.Unbind(); Require(main.Atlas == null && main.ImageId == -1 && !main.Renderer.enabled, "Unbind retained the image binding.");
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.Bind(atlas, 0); await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.FlipX = true; await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.FlipY = true; await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.FlipX = main.FlipY = false;
                    main.Pivot = Vector2.zero; mainObject.transform.position = new Vector3(-1, -.5f, 0);
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.Pivot = new Vector2(.5f, .5f); mainObject.transform.position = Vector3.zero;
                    mainObject.transform.rotation = Quaternion.Euler(0, 0, 17); main.Color = new Color(.7f, .4f, 1, .65f);
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    mainObject.transform.rotation = Quaternion.identity; main.Color = Color.white;
                    otherObject.transform.position = Vector3.zero; other.Color = new Color(1, .2f, .1f, .6f); other.SortingOrder = 5;
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    other.SortingOrder = -5; await Check(camera, target, decoder, new[] { main, other }, images, report);
                    otherObject.transform.position = Vector3.right * 100;
                    main.DrawMode = GpuAtlasDrawMode.Sliced; main.Border = new Vector4(4, 3, 7, 2);
                    await Check(camera, target, decoder, new[] { main, other }, images, report, Path.Combine(output, prefix + "sliced.png"));
                    main.Size = new Vector2(.3f, .2f); await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.Size = Vector2.zero; await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.UseNativeSize = true; await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.Size = new Vector2(2, 1); main.Border = new Vector4(80, 40, 48, 24);
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.Border = Vector4.zero; await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.Border = new Vector4(4, 3, 7, 2); main.FlipX = main.FlipY = true;
                    main.Pivot = new Vector2(.2f, .3f); mainObject.transform.position = new Vector3(-.6f, -.2f, 0);
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.FlipX = main.FlipY = false; main.Pivot = Vector2.one * .5f; mainObject.transform.position = Vector3.zero;
                    if (rendererIndex == 1)
                    {
                        mask.enabled = true; main.MaskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                        Require(main.Renderer.GetSpriteMaskInteraction() == main.MaskInteraction, "SpriteMask interaction was not applied to the MeshRenderer.");
                        Require(!main.Renderer.sharedMaterial.enableInstancing, "A masked group kept the Unity instancing path that bypasses SpriteMask.");
                        await Check(camera, target, decoder, new[] { main, other }, images, report, Path.Combine(output, prefix + "mask-inside.png"), mask);
                        otherObject.transform.position = Vector3.zero; other.MaskInteraction = SpriteMaskInteraction.VisibleOutsideMask; other.SortingOrder = 5;
                        await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        other.SortingOrder = -5; await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        otherObject.transform.position = Vector3.right * 100; other.MaskInteraction = SpriteMaskInteraction.None;
                        main.enabled = false; Require(other.Renderer.sharedMaterial.enableInstancing, "The last masked view did not restore instancing when disabled.");
                        main.enabled = true; Require(!main.Renderer.sharedMaterial.enableInstancing, "A masked view rejoined an instanced group.");
                        main.MaskInteraction = SpriteMaskInteraction.VisibleOutsideMask;
                        await Check(camera, target, decoder, new[] { main, other }, images, report, Path.Combine(output, prefix + "mask-outside.png"), mask);
                        mask.alphaCutoff = .8f; await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        mask.transform.position = new Vector3(.25f, 0, 0); await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        mask.enabled = false; main.MaskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                        await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        main.MaskInteraction = SpriteMaskInteraction.VisibleOutsideMask;
                        await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        mask.enabled = true; mask.backSortingOrder = 10;
                        await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        main.SortingOrder = 15; await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        main.SortingOrder = 0; mask.backSortingOrder = -20; mask.transform.position = Vector3.zero; mask.alphaCutoff = .5f;
                        main.MaskInteraction = SpriteMaskInteraction.None;
                        Require(main.Renderer.sharedMaterial.enableInstancing, "Removing the last mask interaction did not restore instancing.");
                        await Check(camera, target, decoder, new[] { main, other }, images, report, mask: mask);
                        mask.enabled = false;
                    }
                    Require(main.LookupUploadCount == uploads, "Transforms and tint changes rebuilt the shared UV table.");
                    bundle.Unload(true); bundle = null; Require(seed == null, "Renderer's source bundle is still loaded.");
                    bundle = AssetBundle.LoadFromFile(bundlePath); inputs = bundle.LoadAllAssets<Texture2D>(); seed = inputs.Single(t => t.name == sourcePrefix + "0");
                    var material = main.Renderer.sharedMaterial; var mesh = main.Renderer.GetComponent<MeshFilter>().sharedMesh;
                    var oldTexture = atlas.Texture;
                    await atlas.AddAsync(inputs.Single(t => t.name == sourcePrefix + "2"));
                    Require(atlas.Texture != oldTexture && main.LookupUploadCount == uploads + 1 && other.LookupUploadCount == main.LookupUploadCount, "Growth did not update one shared table.");
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    if (rendererIndex == 1) { mask.enabled = true; main.MaskInteraction = SpriteMaskInteraction.VisibleInsideMask; }
                    atlas.Remove(1); await atlas.RepackAsync();
                    Require(ReferenceEquals(material, main.Renderer.sharedMaterial) && ReferenceEquals(mesh, main.Renderer.GetComponent<MeshFilter>().sharedMesh) && main.Renderer.sharedMaterial.GetTexture("_AtlasTex") == atlas.Texture, "Repacking replaced the quad/material or kept the retired texture.");
                    await Check(camera, target, decoder, new[] { main, other }, images, report, Path.Combine(output, prefix + "repacked.png"), rendererIndex == 1 ? mask : null);
                    main.MaskInteraction = SpriteMaskInteraction.None; mask.enabled = false;
                    int convertedId = atlas.NextSourceIndex;
                    await atlas.AddAsync(inputs.Single(t => t.name == sourcePrefix + "3"), encoder, convert, GpuAtlasQuality.Balanced);
                    images.Add(convertedId, Capture(atlas, convertedId, decoder)); main.Bind(atlas, convertedId);
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    // Sparse IDs above float's exact integer range must not become huge tables or lose ID bits.
                    const int sparse = 16777217;
                    typeof(GpuTextureAtlas).GetProperty("NextSourceIndex").SetValue(atlas, sparse);
                    main.Bind(atlas, sparse); Require(!main.Renderer.enabled, "An absent future ID was visible.");
                    main.Renderer.enabled = true; // The shader must reject an absent new ID as well as a removed one.
                    await Check(camera, target, decoder, new[] { main, other }, images, report); main.Renderer.enabled = false;
                    await atlas.AddAsync(seed); images.Add(sparse, Capture(atlas, sparse, decoder));
                    Require(main.Renderer.enabled && main.LookupCapacity <= 16, "A future/sparse ID failed to activate or grew a dense table.");
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    atlas.Remove(sparse); Require(!main.Renderer.enabled, "Removed image renderer was not hidden.");
                    main.Renderer.enabled = true; // Prove that GPU lookup also rejects a removed ID.
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    int fresh = atlas.NextSourceIndex; await atlas.AddAsync(seed); images.Add(fresh, Capture(atlas, fresh, decoder));
                    Require(!main.Renderer.enabled && !atlas.TryGetEntry(sparse, out _), "A removed ID aliased its replacement.");
                    main.Bind(atlas, fresh); main.UseNativeSize = true; main.PixelsPerUnit = 16;
                    Require(main.Renderer.transform.localScale == new Vector3(2, 1, 1), "Native image size did not use pixels per unit.");
                    main.Size = new Vector2(2, 1);
                    bundle.Unload(true); bundle = null; await atlas.RepackAsync();
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    int count = atlas.Entries.Count; atlas.Remove(atlas.Entries.Select(e => e.SourceIndex).ToArray()); await atlas.RepackAsync();
                    Require(count > 0 && main.LookupCapacity == 4 && !main.Renderer.enabled && !other.Renderer.enabled, "Empty atlas did not hide renderers and shrink the ID table.");
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    report.tableUploads += main.LookupUploadCount;
                    atlas.Dispose(); atlas = null;
                    Require(main.LookupCapacity == 0 && !main.Renderer.enabled && main.Renderer.sharedMaterial == null, "Atlas disposal left rendering resources bound.");
                    await Check(camera, target, decoder, new[] { main, other }, images, report);
                    main.Unbind(); other.Unbind(); report.cases++;
                }
                }
                report.passed = true;
            }
            catch (Exception error) { report.error = error.ToString(); throw; }
            finally
            {
                if (pipeline != null) pipeline.renderScale = previousRenderScale;
                if (bundle != null) bundle.Unload(true); atlas?.Dispose(); Destroy(mainObject); Destroy(otherObject); Destroy(maskObject); Destroy(maskSprite); Destroy(maskTexture); Destroy(cameraObject);
                if (target != null) { target.Release(); Destroy(target); }
                if (Application.isPlaying) { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
                File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(report, true));
            }
            return $"PASS: Renderer {report.cases} pipeline/color cases, {report.frames} camera frames ({report.slicedFrames} sliced, {report.maskFrames} mask), {report.pixels} checked pixels, max difference {report.maximumDifference:R}, sparse IDs and automatic atlas updates.";
        }
        static Color Sample(Image image, float u, float v)
        {
            float x = Mathf.Clamp(u * image.width - .5f, 0, image.width - 1), y = Mathf.Clamp(v * image.height - .5f, 0, image.height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, image.width - 1), y1 = Math.Min(y0 + 1, image.height - 1);
            return Color.LerpUnclamped(Color.LerpUnclamped(image.pixels[y0 * image.width + x0], image.pixels[y0 * image.width + x1], x - x0),
                Color.LerpUnclamped(image.pixels[y1 * image.width + x0], image.pixels[y1 * image.width + x1], x - x0), y - y0);
        }
#if UNITY_EDITOR && !GPU_ATLAS_PLAYER_PROBE
        public static async void RunBatch()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.validation/gpu-atlas"));
            try
            {
                string result = await Run(Path.Combine(root, "bundle/gpu-atlas-inputs"),
                    UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7Encoder.compute"),
                    UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/TextureCompression/Shaders/GpuAtlasConvert.compute"),
                    UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/TextureCompression/Editor/GpuAtlasValidation.compute"), Path.Combine(root, "renderer"));
                await GpuAtlasValidation.DrainRetirement();
                File.WriteAllText(Path.Combine(root, "renderer/batch-result.txt"), result + "\n" + RunCompilers(Path.Combine(root, "renderer"))); UnityEditor.EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); UnityEditor.EditorApplication.Exit(1); }
        }
        internal static string RunCompilers(string output)
        {
            var shader = Resources.Load<Shader>("GpuAtlasUnlit"); var data = UnityEditor.ShaderUtil.GetShaderData(shader); var lines = new List<string>();
            var platforms = new[] { UnityEditor.Rendering.ShaderCompilerPlatform.D3D12, UnityEditor.Rendering.ShaderCompilerPlatform.Vulkan,
                UnityEditor.Rendering.ShaderCompilerPlatform.GLES3x, UnityEditor.Rendering.ShaderCompilerPlatform.Metal };
            foreach (var platform in platforms)
            {
                var target = platform == UnityEditor.Rendering.ShaderCompilerPlatform.D3D12 ? UnityEditor.BuildTarget.StandaloneWindows64
                    : platform == UnityEditor.Rendering.ShaderCompilerPlatform.Metal ? UnityEditor.BuildTarget.iOS : UnityEditor.BuildTarget.Android;
                int variants = 0;
                for (int sub = 0; sub < data.SubshaderCount; sub++) for (int pass = 0; pass < data.GetSubshader(sub).PassCount; pass++)
                    foreach (bool instanced in new[] { false, true })
                        foreach (var stage in new[] { UnityEditor.Rendering.ShaderType.Vertex, UnityEditor.Rendering.ShaderType.Fragment })
                        {
                            // Vulkan/GLES bytecode includes both stages in the vertex program.
                            if (stage == UnityEditor.Rendering.ShaderType.Fragment && (platform == UnityEditor.Rendering.ShaderCompilerPlatform.Vulkan || platform == UnityEditor.Rendering.ShaderCompilerPlatform.GLES3x)) continue;
                            var result = data.GetSubshader(sub).GetPass(pass).CompileVariant(stage, instanced ? new[] { "INSTANCING_ON" } : Array.Empty<string>(), platform, target);
                            Require(result.Success && result.ShaderData.Length > 0, $"Renderer compiler {platform}, pass {pass}, {stage}: " + string.Join("; ", result.Messages.Select(m => m.message)));
                            Require(result.Messages.All(m => m.severity != UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), "Renderer shader contains compiler errors."); variants++;
                        }
                lines.Add($"PASS: GpuAtlasUnlit {platform}/{target}, {variants} stage variants.");
            }
            Directory.CreateDirectory(output); File.WriteAllLines(Path.Combine(output, "shader-compilers.txt"), lines); return string.Join("\n", lines);
        }
#endif
        static async Task Check(Camera camera, RenderTexture target, ComputeShader decoder, GpuAtlasRenderer[] renderers,
            Dictionary<int, Image> images, Report report, string png = null, SpriteMask mask = null)
        {
            // SpriteMask registration and stencil ranges are updated by Unity's frame loop.
            if (Application.isPlaying) { int frame = Time.frameCount; while (frame == Time.frameCount) await Task.Yield(); }
            else await Task.Yield();
            if (GraphicsSettings.currentRenderPipeline != null)
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            else camera.Render();
            var actual = Read(target, Width, Height, decoder); var ordered = renderers.OrderBy(r => r.SortingOrder).ToArray();
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            {
                var expected = Color.clear;
                var world = new Vector3(((x + .5f) / Width - .5f) * 2, (y + .5f) / Height - .5f, 0);
                foreach (var r in ordered)
                {
                    if (!r.isActiveAndEnabled || r.Atlas == null || r.Atlas.IsDisposed || !r.Atlas.TryGetEntry(r.ImageId, out var entry)) continue;
                    var size = r.UseNativeSize ? (Vector2)entry.PixelRect.size / r.PixelsPerUnit : r.Size;
                    if (size.x <= 0 || size.y <= 0) continue;
                    var local = r.transform.InverseTransformPoint(world);
                    float u = local.x / (size.x * (r.FlipX ? -1 : 1)) + r.Pivot.x, v = local.y / (size.y * (r.FlipY ? -1 : 1)) + r.Pivot.y;
                    if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                    if (r.MaskInteraction != SpriteMaskInteraction.None)
                    {
                        var m = mask != null ? mask.transform.InverseTransformPoint(world) : Vector3.zero;
                        int mx = Mathf.FloorToInt(m.x * 16 + 8), my = Mathf.FloorToInt(m.y * 16 + 8);
                        bool inside = mask != null && mask.enabled && mask.gameObject.activeInHierarchy && r.SortingOrder > mask.backSortingOrder && r.SortingOrder <= mask.frontSortingOrder
                            && mx >= 0 && my >= 0 && mx < 16 && my < 16 && MaskAlpha(mx, my) >= mask.alphaCutoff;
                        if ((r.MaskInteraction == SpriteMaskInteraction.VisibleInsideMask) != inside) continue;
                    }
                    if (r.DrawMode == GpuAtlasDrawMode.Sliced)
                    {
                        u = SliceCoordinate(u, images[r.ImageId].width, r.Border.x, r.Border.z, size.x, r.PixelsPerUnit);
                        v = SliceCoordinate(v, images[r.ImageId].height, r.Border.y, r.Border.w, size.y, r.PixelsPerUnit);
                    }
                    var color = Sample(images[r.ImageId], u, v) * r.Color;
                    expected = new Color(color.r * color.a + expected.r * (1 - color.a), color.g * color.a + expected.g * (1 - color.a),
                        color.b * color.a + expected.b * (1 - color.a), color.a + expected.a * (1 - color.a));
                }
                for (int c = 0; c < 4; c++)
                {
                    float delta = Mathf.Abs(actual[y * Width + x][c] - expected[c]); report.maximumDifference = Math.Max(report.maximumDifference, delta);
                    Require(delta <= 2f / 255, $"Renderer frame={report.frames}, pixel=({x},{y}), channel={c}, actual={actual[y * Width + x][c]:R}, expected={expected[c]:R}, delta={delta:R}.");
                }
                report.pixels++;
            }
            report.frames++; if (renderers.Any(r => r.DrawMode == GpuAtlasDrawMode.Sliced)) report.slicedFrames++; if (mask != null) report.maskFrames++;
            foreach (var r in renderers) report.maximumCapacity = Math.Max(report.maximumCapacity, r.LookupCapacity);
            if (png != null)
            {
                var preview = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                try { preview.SetPixels(actual.Select(c => c.gamma).ToArray()); preview.Apply(); File.WriteAllBytes(png, preview.EncodeToPNG()); }
                finally { Destroy(preview); }
            }
        }
        static float MaskAlpha(int x, int y) => x < 2 || y < 2 || x >= 14 || y >= 14 || (x >= 6 && x < 10 && y >= 6 && y < 10) ? 0 : x < 8 ? .7f : 1;
        // Independent piecewise sampling in source/destination pixel distances, not shader grid coordinates.
        static float SliceCoordinate(float normalized, int sourcePixels, float first, float last, float size, float ppu)
        {
            double sum = (double)first + last;
            double a = first, b = last;
            if (sum > sourcePixels) { a *= sourcePixels / sum; b *= sourcePixels / sum; }
            double distance = normalized * size * ppu, target = size * ppu;
            double shrink = a + b > target ? target / (a + b) : 1;
            double left = a * shrink, right = b * shrink;
            if (distance < left) return (float)(distance / shrink / sourcePixels);
            if (distance > target - right) return (float)(1 - (target - distance) / shrink / sourcePixels);
            return target > left + right ? (float)((a + (distance - left) / (target - left - right) * (sourcePixels - a - b)) / sourcePixels) : (float)(a / sourcePixels);
        }
    }
}
