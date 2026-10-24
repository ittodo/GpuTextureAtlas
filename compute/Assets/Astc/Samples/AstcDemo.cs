using System;
using UnityEngine;

namespace UnityCompute.Astc
{
    public sealed class AstcDemo : MonoBehaviour
    {
        public ComputeShader encoderShader;
        public AstcBlockSize blockSize = AstcBlockSize.Block6x6;
        Texture2D source, compressed;
        string status = "Encoding...";
        bool destroyed, pending;

        async void Start()
        {
            pending = true;
            try
            {
                source = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
                var pixels = new Color32[256 * 256];
                for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                    pixels[y * 256 + x] = new Color(x / 255f, y / 255f, (Mathf.Sin(x * .09f) + 1) * .5f, 1);
                source.SetPixels32(pixels);
                source.Apply(false, true);
                using var encoder = new AstcEncoder(encoderShader);
                var image = SystemInfo.supportsAsyncGPUReadback ? await encoder.EncodeAsync(source, blockSize) : encoder.Encode(source, blockSize);
                if (destroyed) return;
                if (!SystemInfo.SupportsTextureFormat(AstcImage.GetTextureFormat(blockSize)))
                {
                    status = $"Encoded {image.Data.Length:N0} bytes. This GPU cannot display ASTC. Run this scene on a compatible Android or iOS device.";
                    return;
                }
                compressed = image.CreateTexture();
                status = $"ASTC {(int)blockSize}x{(int)blockSize}: {image.Data.Length:N0} bytes / RGBA: {256 * 256 * 4:N0} bytes";
            }
            catch (Exception error) { if (!destroyed) { status = error.Message; Debug.LogException(error); } }
            finally
            {
                pending = false;
                if (destroyed && source != null) Destroy(source);
            }
        }

        void OnGUI()
        {
            float side = Mathf.Min((Screen.width - 48) / 2f, Screen.height - 140);
            GUI.Label(new Rect(16, 12, Screen.width - 32, 60), status);
            GUI.Label(new Rect(16, 74, side, 22), "Original");
            GUI.Label(new Rect(32 + side, 74, side, 22), "ASTC");
            if (source != null) GUI.DrawTexture(new Rect(16, 100, side, side), source, ScaleMode.ScaleToFit, false);
            if (compressed != null) GUI.DrawTexture(new Rect(32 + side, 100, side, side), compressed, ScaleMode.ScaleToFit, false);
        }

        void OnDestroy()
        {
            destroyed = true;
            if (!pending && source != null) Destroy(source);
            if (compressed != null) Destroy(compressed);
        }
    }
}
