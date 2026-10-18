using System;
using UnityEditor;
using UnityEngine;

namespace UnityCompute.Bc7.Editor
{
    // Analysis only: BC7 UNORM hardware sampling, no sRGB transfer conversion.
    public static class Bc7HardwareDecoder
    {
        public static Color32[] Decode(Bc7Image image)
        {
            var linear=new Bc7Image(image.Width,image.Height,false,image.Data);
            var texture=linear.CreateTexture();
            try
            {
                if(texture.format!=TextureFormat.BC7)throw new InvalidOperationException("BC7 was substituted by an uncompressed format.");
                var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7DecodeHardware.compute");
                int kernel=shader.FindKernel("DecodeHardware");
                using var buffer=new ComputeBuffer(checked(image.Width*image.Height),4);
                shader.SetInts("_Size",image.Width,image.Height);shader.SetTexture(kernel,"_Source",texture);shader.SetBuffer(kernel,"_Pixels",buffer);
                shader.Dispatch(kernel,(image.Width+7)/8,(image.Height+7)/8,1);
                var words=new uint[buffer.count];buffer.GetData(words);var pixels=new Color32[words.Length];
                for(int i=0;i<words.Length;i++){uint word=words[i];pixels[i]=new Color32((byte)word,(byte)(word>>8),(byte)(word>>16),(byte)(word>>24));}
                return pixels;
            }
            finally{UnityEngine.Object.DestroyImmediate(texture);}
        }
    }
}
