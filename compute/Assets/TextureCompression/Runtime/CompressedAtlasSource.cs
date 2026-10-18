using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityCompute.TextureCompression
{
    public enum CompressedRowOrder { BottomToTop, TopToBottom }

    /// <summary>One mip of compressed blocks. Keep Blocks unchanged until packing finishes.</summary>
    public sealed class CompressedAtlasSource
    {
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public TextureFormat Format { get; }
        public bool Srgb { get; }
        public CompressedRowOrder RowOrder { get; }
        public byte[] Blocks { get; }
        public int BlockSize => GetBlockSize(Format);

        public CompressedAtlasSource(string name, int width, int height, TextureFormat format,
            bool srgb, byte[] blocks, CompressedRowOrder rowOrder = CompressedRowOrder.BottomToTop)
        {
            int block = GetBlockSize(format);
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (rowOrder != CompressedRowOrder.BottomToTop && rowOrder != CompressedRowOrder.TopToBottom)
                throw new ArgumentOutOfRangeException(nameof(rowOrder));
            long expected = ((width + (long)block - 1) / block) * ((height + (long)block - 1) / block) * 16;
            if (blocks == null || blocks.LongLength != expected)
                throw new ArgumentException("Expected one tightly packed mip of 16-byte blocks, without a file header.", nameof(blocks));
            Name = name ?? ""; Width = width; Height = height; Format = format;
            Srgb = srgb; Blocks = blocks; RowOrder = rowOrder;
        }

        /// <summary>Call on the Unity main thread. Copies compressed CPU bytes, without decoding or GPU readback.</summary>
        public static CompressedAtlasSource FromTexture(Texture2D texture, string name = null, int mipLevel = 0)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            GetBlockSize(texture.format); // Reject an unsupported format or a platform RGBA fallback.
            if (!texture.isReadable)
                throw new InvalidOperationException($"'{texture.name}' needs Read/Write Enabled in Texture Importer before building its Addressables or AssetBundle.");
            if (mipLevel < 0 || mipLevel >= texture.mipmapCount) throw new ArgumentOutOfRangeException(nameof(mipLevel));
            return new CompressedAtlasSource(name ?? texture.name, Math.Max(1, texture.width >> mipLevel),
                Math.Max(1, texture.height >> mipLevel), texture.format,
                GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat), texture.GetPixelData<byte>(mipLevel).ToArray());
        }

        internal static int GetBlockSize(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.BC7: case TextureFormat.ASTC_4x4: return 4;
                case TextureFormat.ASTC_5x5: return 5;
                case TextureFormat.ASTC_6x6: return 6;
                case TextureFormat.ASTC_8x8: return 8;
                case TextureFormat.ASTC_10x10: return 10;
                case TextureFormat.ASTC_12x12: return 12;
                default: throw new NotSupportedException($"Expected BC7 or square LDR ASTC blocks, but the loaded texture is {format}. Check the build target, platform format override and device support.");
            }
        }
    }
}
