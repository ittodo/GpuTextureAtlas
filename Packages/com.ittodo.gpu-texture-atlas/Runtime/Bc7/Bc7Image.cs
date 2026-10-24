using System;
using UnityEngine;

namespace UnityCompute.Bc7
{
    public sealed class Bc7Image
    {
        public int Width{get;}
        public int Height{get;}
        public bool Srgb{get;}
        public int TextureWidth=>(Width+3)/4*4;
        public int TextureHeight=>(Height+3)/4*4;
        /// <summary>Valid source region when the texture dimensions require block padding.</summary>
        public Vector2 UvScale=>new Vector2((float)Width/TextureWidth,(float)Height/TextureHeight);
        /// <summary>Raw 16-byte BC7 blocks. Row order follows the encoded input.</summary>
        public byte[] Data{get;}
        public Bc7Image(int width,int height,bool srgb,byte[] data)
        {
            if(width<1||height<1)throw new ArgumentOutOfRangeException(nameof(width));
            int expected=checked(((width+3)/4)*((height+3)/4)*16);
            if(data==null||data.Length!=expected)throw new ArgumentException("Invalid BC7 payload.",nameof(data));
            Width=width;Height=height;Srgb=srgb;Data=data;
        }
        public Texture2D CreateTexture(bool makeNoLongerReadable=true)
        {
            if(!SystemInfo.SupportsTextureFormat(TextureFormat.BC7))throw new NotSupportedException("This GPU cannot sample BC7.");
            // D3D requires complete 4x4 blocks at the top mip level.
            var texture=new Texture2D(TextureWidth,TextureHeight,TextureFormat.BC7,false,!Srgb);
            try{texture.LoadRawTextureData(Data);texture.Apply(false,makeNoLongerReadable);return texture;}
            catch{UnityEngine.Object.Destroy(texture);throw;}
        }
        /// <summary>DX10 DDS. Payload row order is preserved. Encode with flipY:true for top-first DDS rows.</summary>
        public byte[] ToDdsFile()
        {
            var file=new byte[checked(148+Data.Length)];
            void Write(int at,uint value){file[at]=(byte)value;file[at+1]=(byte)(value>>8);file[at+2]=(byte)(value>>16);file[at+3]=(byte)(value>>24);}
            Write(0,0x20534444);Write(4,124);Write(8,0x81007);Write(12,(uint)Height);Write(16,(uint)Width);
            Write(20,(uint)Data.Length);Write(28,1);
            Write(76,32);Write(80,4);Write(84,0x30315844);Write(108,0x1000);
            Write(128,Srgb?99u:98u);Write(132,3);Write(140,1);Write(144,1);
            Buffer.BlockCopy(Data,0,file,148,Data.Length);return file;
        }
    }
}
