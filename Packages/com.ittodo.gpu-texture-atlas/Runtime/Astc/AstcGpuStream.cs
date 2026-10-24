using System;
using UnityEngine;

namespace UnityCompute.Astc
{
    /// <summary>Optional mobile demonstration. Assign a source and Renderer, then profile the player.</summary>
    public sealed class AstcGpuStream : MonoBehaviour
    {
        public ComputeShader encoderShader;
        public Texture source;
        public Renderer targetRenderer;
        public AstcBlockSize blockSize = AstcBlockSize.Block4x4;
        public AstcQuality quality = AstcQuality.Fast;
        public bool srgb = true, sampledInputIsSrgb;
        public bool spreadAcrossFrames;
        [Min(1)] public int blockRowsPerFrame = 4;
        public bool snapshotInput = true;
        AstcGpuTexture output;
        Material previous, display;

        void OnEnable()
        {
            try
            {
                if (source == null || targetRenderer == null) throw new InvalidOperationException("Assign Source and Target Renderer.");
                output = new AstcGpuTexture(encoderShader, source.width, source.height, blockSize, srgb);
                previous = targetRenderer.sharedMaterial;
                display = new Material(previous);
                if (display.HasProperty("_BaseMap")) display.SetTexture("_BaseMap", output.Texture);
                if (display.HasProperty("_MainTex")) display.SetTexture("_MainTex", output.Texture);
                targetRenderer.sharedMaterial = display;
            }
            catch (Exception error) { Debug.LogException(error, this); enabled = false; }
        }
        void Update()
        {
            try
            {
                int status = output.UploadStatus;
                if (status < 0) throw new InvalidOperationException($"ASTC native copy failed: {status}. Inspect the player graphics API and bridge resource usage.");
                if (spreadAcrossFrames || output.IsEncoding)
                {
                    if (!output.IsEncoding) output.BeginEncode(source, sampledInputIsSrgb, quality, snapshotInput);
                    output.EncodeNextFrame(blockRowsPerFrame);
                }
                else output.EncodeFrame(source, sampledInputIsSrgb, quality);
            }
            catch (Exception error) { Debug.LogException(error, this); enabled = false; }
        }
        void OnDisable()
        {
            if (targetRenderer != null && display != null && targetRenderer.sharedMaterial == display)
                targetRenderer.sharedMaterial = previous;
            output?.Dispose(); output = null;
            if (display != null) Destroy(display);
            display = null;
        }
    }
}
