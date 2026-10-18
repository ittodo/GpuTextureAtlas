using System;
using UnityEngine;

namespace UnityCompute.Bc7
{
    /// <summary>Encode a changing source before rendering. The output texture is reused.</summary>
    public sealed class Bc7GpuStream:MonoBehaviour
    {
        public ComputeShader encoderShader;
        public Texture source;
        public Renderer targetRenderer;
        public Bc7Quality quality=Bc7Quality.Fast;
        public bool srgb=true,sampledInputIsSrgb;
        public bool spreadAcrossFrames;
        [Min(1)] public int blockRowsPerFrame=4;
        public bool snapshotInput=true;
        Bc7GpuTexture output;
        Material previous,display;
        void OnEnable()
        {
            try
            {
                if(source==null||targetRenderer==null||targetRenderer.sharedMaterial==null)
                    throw new InvalidOperationException("Assign Source and Target Renderer with a material.");
                output=new Bc7GpuTexture(encoderShader,source.width,source.height,srgb);
                previous=targetRenderer.sharedMaterial;display=new Material(previous);
                bool bound=false;
                foreach(string property in new[]{"_BaseMap","_MainTex"})if(display.HasProperty(property))
                {display.SetTexture(property,output.Texture);display.SetTextureScale(property,output.UvScale);display.SetTextureOffset(property,Vector2.zero);bound=true;}
                if(!bound)throw new InvalidOperationException("The material needs _BaseMap or _MainTex.");
                targetRenderer.sharedMaterial=display;
            }
            catch(Exception error){Debug.LogException(error,this);enabled=false;}
        }
        void Update()
        {
            try
            {
                if(spreadAcrossFrames||output.IsEncoding)
                {
                    if(!output.IsEncoding)output.BeginEncode(source,sampledInputIsSrgb,quality,snapshotInput);
                    output.EncodeNextFrame(blockRowsPerFrame);
                }
                else output.EncodeFrame(source,sampledInputIsSrgb,quality);
            }
            catch(Exception error){Debug.LogException(error,this);enabled=false;}
        }
        void OnDisable()
        {
            if(targetRenderer!=null&&display!=null&&targetRenderer.sharedMaterial==display)targetRenderer.sharedMaterial=previous;
            output?.Dispose();output=null;
            if(display!=null)Destroy(display);display=null;
        }
    }
}
