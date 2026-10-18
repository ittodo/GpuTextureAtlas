using System;
using UnityEngine;

namespace UnityCompute.Astc
{
    public sealed class AstcImage
    {
        public int Width { get; }
        public int Height { get; }
        public AstcBlockSize BlockSize { get; }
        public bool Srgb { get; }
        /// <summary>Raw 16-byte ASTC blocks, without a file header, in Unity's bottom-to-top texture row order.</summary>
        public byte[] Data { get; }

        internal AstcImage(int width, int height, AstcBlockSize blockSize, bool srgb, byte[] data)
        {
            int size = (int)blockSize;
            GetTextureFormat(blockSize);
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            int expected = checked(((width + size - 1) / size) * ((height + size - 1) / size) * 16);
            if (data == null || data.Length != expected) throw new ArgumentException("Invalid ASTC payload length.", nameof(data));
            Width = width;
            Height = height;
            BlockSize = blockSize;
            Srgb = srgb;
            Data = data;
        }

        public static TextureFormat GetTextureFormat(AstcBlockSize blockSize)
        {
            switch (blockSize)
            {
                case AstcBlockSize.Block4x4: return TextureFormat.ASTC_4x4;
                case AstcBlockSize.Block5x5: return TextureFormat.ASTC_5x5;
                case AstcBlockSize.Block6x6: return TextureFormat.ASTC_6x6;
                case AstcBlockSize.Block8x8: return TextureFormat.ASTC_8x8;
                case AstcBlockSize.Block10x10: return TextureFormat.ASTC_10x10;
                case AstcBlockSize.Block12x12: return TextureFormat.ASTC_12x12;
                default: throw new ArgumentOutOfRangeException(nameof(blockSize));
            }
        }

        public Texture2D CreateTexture(bool makeNoLongerReadable = true)
        {
            var format = GetTextureFormat(BlockSize);
            if (!SystemInfo.SupportsTextureFormat(format))
                throw new NotSupportedException($"This GPU cannot sample {format}. Encoding and file export are still available.");
            var texture = new Texture2D(Width, Height, format, false, !Srgb);
            try
            {
                texture.LoadRawTextureData(Data);
                texture.Apply(false, makeNoLongerReadable);
                return texture;
            }
            catch
            {
                UnityEngine.Object.Destroy(texture);
                throw;
            }
        }

        /// <summary>File rows follow the raw payload order. The ASTC header stores neither color space nor image orientation.</summary>
        public byte[] ToAstcFile()
        {
            var file = new byte[checked(Data.Length + 16)];
            file[0] = 0x13; file[1] = 0xAB; file[2] = 0xA1; file[3] = 0x5C;
            file[4] = file[5] = (byte)BlockSize;
            file[6] = 1;
            Write24(file, 7, Width);
            Write24(file, 10, Height);
            Write24(file, 13, 1);
            Buffer.BlockCopy(Data, 0, file, 16, Data.Length);
            return file;
        }

        static void Write24(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
        }
    }
}
