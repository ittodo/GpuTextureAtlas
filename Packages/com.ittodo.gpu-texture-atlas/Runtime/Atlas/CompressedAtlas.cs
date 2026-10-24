using System;
using System.Collections.Generic;
using UnityEngine;
using UnityCompute.Astc;
using UnityCompute.Bc7;

namespace UnityCompute.TextureCompression
{
    public sealed class CompressedAtlasEntry
    {
        /// <summary>CPU input index, or stable GPU atlas entry ID. GPU IDs can have gaps after removal.</summary>
        public int SourceIndex { get; }
        public string Name { get; }
        /// <summary>Valid pixels in the atlas. Coordinates use Unity's bottom-left texture origin.</summary>
        public RectInt PixelRect { get; }
        /// <summary>Whole blocks, including unused pixels in the source's last block. Excludes the optional gutter.</summary>
        public RectInt PackedRect { get; }
        /// <summary>atlasUv = localUv * xy + zw. Negative y handles top-first raw input without changing blocks.</summary>
        public Vector4 UvScaleOffset { get; }
        /// <summary>Minimum xy and maximum zw at valid texel centers. Clamp transformed UVs for bilinear sampling.</summary>
        public Vector4 UvClamp { get; }

        internal CompressedAtlasEntry(int index, CompressedAtlasSource source, RectInt blocks, int width, int height)
            : this(index, source.Name, source.Width, source.Height, source.RowOrder,
                new RectInt(blocks.x * source.BlockSize, blocks.y * source.BlockSize,
                    blocks.width * source.BlockSize, blocks.height * source.BlockSize), width, height) { }

        internal CompressedAtlasEntry(int index, string name, int imageWidth, int imageHeight,
            CompressedRowOrder rowOrder, RectInt packed, int width, int height)
        {
            SourceIndex = index; Name = name; PackedRect = packed;
            PixelRect = new RectInt(packed.x, packed.y, imageWidth, imageHeight);
            bool topFirst = rowOrder == CompressedRowOrder.TopToBottom;
            UvScaleOffset = new Vector4((float)imageWidth / width, (topFirst ? -1f : 1f) * imageHeight / height,
                (float)PixelRect.x / width, (float)(topFirst ? PixelRect.yMax : PixelRect.y) / height);
            UvClamp = new Vector4((PixelRect.x + .5f) / width, (PixelRect.y + .5f) / height,
                (PixelRect.xMax - .5f) / width, (PixelRect.yMax - .5f) / height);
        }

        public Vector2 MapUv(Vector2 localUv) => new Vector2(
            Mathf.Clamp(localUv.x * UvScaleOffset.x + UvScaleOffset.z, UvClamp.x, UvClamp.z),
            Mathf.Clamp(localUv.y * UvScaleOffset.y + UvScaleOffset.w, UvClamp.y, UvClamp.w));
    }

    /// <summary>CPU block packing for loaded compressed textures. No pixel decoding or GPU readback.</summary>
    public sealed class CompressedAtlas
    {
        public int Width { get; }
        public int Height { get; }
        public TextureFormat Format { get; }
        public bool Srgb { get; }
        public int BlockSize => CompressedAtlasSource.GetBlockSize(Format);
        public byte[] Data { get; }
        /// <summary>Entries retain input order, even when placement sorts images by size.</summary>
        public IReadOnlyList<CompressedAtlasEntry> Entries { get; }

        CompressedAtlas(int width, int height, TextureFormat format, bool srgb, byte[] data, CompressedAtlasEntry[] entries)
        {
            Width = width; Height = height; Format = format; Srgb = srgb; Data = data;
            Entries = Array.AsReadOnly(entries);
        }

        /// <summary>Use after Addressables/AssetBundle loading. All textures must be readable and use the same format and color space.</summary>
        public static CompressedAtlas PackTextures(IReadOnlyList<Texture2D> textures, int maxSize = 4096, int paddingBlocks = 0, int mipLevel = 0)
        {
            if (textures == null) throw new ArgumentNullException(nameof(textures));
            var sources = new CompressedAtlasSource[textures.Count];
            for (int i = 0; i < sources.Length; i++) sources[i] = CompressedAtlasSource.FromTexture(textures[i], mipLevel: mipLevel);
            return Pack(sources, maxSize, paddingBlocks);
        }

        /// <summary>Pure block copies. Can run on a worker thread after FromTexture has captured the CPU bytes on the main thread.</summary>
        public static CompressedAtlas Pack(IReadOnlyList<CompressedAtlasSource> sources, int maxSize = 4096, int paddingBlocks = 0)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (sources.Count == 0) throw new ArgumentException("At least one input is required.", nameof(sources));
            var first = sources[0] ?? throw new ArgumentException("An input is null.", nameof(sources));
            int size = first.BlockSize; var sizes = new Vector2Int[sources.Count];
            for (int i = 0; i < sizes.Length; i++)
            {
                var source = sources[i] ?? throw new ArgumentException("An input is null.", nameof(sources));
                if (source.Format != first.Format || source.Srgb != first.Srgb)
                    throw new ArgumentException("All inputs must use the same ASTC block size or BC7 format, and the same sRGB setting.", nameof(sources));
                sizes[i] = new Vector2Int(source.Width, source.Height);
            }
            var layout = BlockAtlasLayout.Build(sizes, size, size, maxSize, paddingBlocks, 16);
            int width = layout.Width, height = layout.Height;
            var data = new byte[checked(layout.Columns * (height / size) * 16)];
            for (int at = 0; at < data.Length; at += 16)
            {
                if (first.Format == TextureFormat.BC7) data[at] = 0x40;
                else { data[at] = 0xFC; data[at + 1] = 0xFD; for (int b = 2; b < 8; b++) data[at + b] = 0xFF; }
            }
            var entries = new CompressedAtlasEntry[sources.Count];
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i]; var rect = layout.Regions[i];
                for (int y = 0; y < rect.height; y++)
                    Buffer.BlockCopy(source.Blocks, y * rect.width * 16, data,
                        ((rect.y + y) * layout.Columns + rect.x) * 16, rect.width * 16);
                entries[i] = new CompressedAtlasEntry(i, source, rect, width, height);
            }
            return new CompressedAtlas(width, height, first.Format, first.Srgb, data, entries);
        }

        /// <summary>Main thread only. Uploads compressed bytes once. The caller owns the returned texture.</summary>
        public Texture2D CreateTexture(bool makeNoLongerReadable = true, FilterMode filterMode = FilterMode.Point)
        {
            if (Width > SystemInfo.maxTextureSize || Height > SystemInfo.maxTextureSize)
                throw new NotSupportedException("The atlas exceeds this device's texture size limit.");
            Texture2D texture = Format == TextureFormat.BC7
                ? new Bc7Image(Width, Height, Srgb, Data).CreateTexture(makeNoLongerReadable)
                : new AstcImage(Width, Height, (AstcBlockSize)BlockSize, Srgb, Data).CreateTexture(makeNoLongerReadable);
            texture.name = $"{Format} atlas"; texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = filterMode;
            return texture;
        }

        /// <summary>Exports one mip as ASTC or DX10 BC7 DDS. Save Entries separately to retain image placement.</summary>
        public byte[] ToFile() => Format == TextureFormat.BC7
            ? new Bc7Image(Width, Height, Srgb, Data).ToDdsFile()
            : new AstcImage(Width, Height, (AstcBlockSize)BlockSize, Srgb, Data).ToAstcFile();

    }
}
