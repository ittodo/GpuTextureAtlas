using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.Bc7
{
    public enum Bc7Quality { Fast=0, Balanced=1, HighQuality=2 }

    /// <summary>LDR BC7 compute encoder. Call and dispose on the Unity main thread.</summary>
    public sealed class Bc7Encoder : IDisposable
    {
        readonly ComputeShader shader;
        readonly int fast, balanced, highQuality;
        readonly int highThreadsX,highThreadsY;
        readonly int[] dimensions = new int[2];
        bool disposed;
        static readonly int SourceId=Shader.PropertyToID("_Source"), BlocksId=Shader.PropertyToID("_Blocks"),
            SizeId=Shader.PropertyToID("_Size"), StrideId=Shader.PropertyToID("_BlocksX"),
            TransformId=Shader.PropertyToID("_ColorTransform"), FlipId=Shader.PropertyToID("_FlipY"),
            RowStartId=Shader.PropertyToID("_BlockRowStart"),RowCountId=Shader.PropertyToID("_BlockRowCount");

        public Bc7Encoder(ComputeShader shader)
        {
            if(shader==null)throw new ArgumentNullException(nameof(shader));
            this.shader=shader;
            fast=shader.FindKernel("EncodeBc7Fast");balanced=shader.FindKernel("EncodeBc7Balanced");
            highQuality=shader.FindKernel("EncodeBc7HighQuality");
            shader.GetKernelThreadGroupSizes(highQuality,out uint x,out uint y,out _);highThreadsX=(int)x;highThreadsY=(int)y;
        }
        public void Dispose(){disposed=true;}

        public static int RequiredBlockCount(Texture source)
        {
            if(source==null)throw new ArgumentNullException(nameof(source));
            if(source.dimension!=TextureDimension.Tex2D||source.width<1||source.height<1)
                throw new ArgumentException("Source must be a nonempty 2D texture.",nameof(source));
            if(source is RenderTexture rt&&(!rt.IsCreated()||rt.antiAliasing!=1))
                throw new ArgumentException("Source RenderTexture must be created and single sampled.",nameof(source));
            int count=checked(((source.width+3)/4)*((source.height+3)/4));
            checked{_=count*16;}return count;
        }
        /// <summary>Writes caller-owned GPU blocks without output allocation or CPU readback. Padding is not written.</summary>
        public void EncodeGpu(Texture source,ComputeBuffer destination,bool srgb=false,bool sampledInputIsSrgb=false,
            Bc7Quality quality=Bc7Quality.Fast,int rowStrideBlocks=0,bool flipY=false,int firstBlockRow=0,int blockRowCount=0)
        {
            if(disposed)throw new ObjectDisposedException(nameof(Bc7Encoder));
            RequiredBlockCount(source);
            if(quality!=Bc7Quality.Fast&&quality!=Bc7Quality.Balanced&&quality!=Bc7Quality.HighQuality)throw new ArgumentOutOfRangeException(nameof(quality));
            int columns=(source.width+3)/4,rows=(source.height+3)/4,stride=rowStrideBlocks==0?columns:rowStrideBlocks;
            if(firstBlockRow<0||firstBlockRow>=rows)throw new ArgumentOutOfRangeException(nameof(firstBlockRow));
            if(blockRowCount<0||blockRowCount>rows-firstBlockRow)throw new ArgumentOutOfRangeException(nameof(blockRowCount));
            int dispatchRows=blockRowCount==0?rows-firstBlockRow:blockRowCount;
            if(stride<columns)throw new ArgumentOutOfRangeException(nameof(rowStrideBlocks));
            int count=checked(stride*rows);
            if(destination==null||destination.stride!=16||destination.count<count)
                throw new ArgumentException("Destination needs enough structured elements with stride 16.",nameof(destination));
            int kernel=quality==Bc7Quality.Fast?fast:quality==Bc7Quality.Balanced?balanced:highQuality;
            if(!SystemInfo.supportsComputeShaders||!shader.IsSupported(kernel))throw new NotSupportedException("BC7 compute kernel is unavailable.");
            dimensions[0]=source.width;dimensions[1]=source.height;
            shader.SetInts(SizeId,dimensions);shader.SetInt(StrideId,stride);
            shader.SetInt(TransformId,srgb==sampledInputIsSrgb?0:srgb?1:2);shader.SetInt(FlipId,flipY?1:0);
            shader.SetInt(RowStartId,firstBlockRow);shader.SetInt(RowCountId,dispatchRows);
            if(quality==Bc7Quality.HighQuality)
            {
                // Seed with the actual Balanced bytes. This also survives compiler/FMA differences between kernels.
                if(!shader.IsSupported(balanced))throw new NotSupportedException("BC7 Balanced seed kernel is unavailable.");
                shader.SetTexture(balanced,SourceId,source);shader.SetBuffer(balanced,BlocksId,destination);
                shader.Dispatch(balanced,(columns+7)/8,(dispatchRows+3)/4,1);
            }
            shader.SetTexture(kernel,SourceId,source);shader.SetBuffer(kernel,BlocksId,destination);
            int tx=quality==Bc7Quality.HighQuality?highThreadsX:8,ty=quality==Bc7Quality.HighQuality?highThreadsY:4;
            shader.Dispatch(kernel,(columns+tx-1)/tx,(dispatchRows+ty-1)/ty,1);
        }
        ComputeBuffer Dispatch(Texture source,bool srgb,bool inputSrgb,Bc7Quality quality,bool flipY)
        {
            var buffer=new ComputeBuffer(RequiredBlockCount(source),16,ComputeBufferType.Structured);
            try{EncodeGpu(source,buffer,srgb,inputSrgb,quality,flipY:flipY);return buffer;}
            catch{buffer.Dispose();throw;}
        }
        public Bc7Image Encode(Texture source,bool srgb=false,bool sampledInputIsSrgb=false,
            Bc7Quality quality=Bc7Quality.Fast,bool flipY=false)
        {
            using var buffer=Dispatch(source,srgb,sampledInputIsSrgb,quality,flipY);
            var words=new uint[buffer.count*4];buffer.GetData(words);
            var bytes=new byte[words.Length*4];Buffer.BlockCopy(words,0,bytes,0,bytes.Length);
            return new Bc7Image(source.width,source.height,srgb,bytes);
        }
        /// <summary>Keep the source and encoder alive until completion. Alpha is always linear.</summary>
        public Task<Bc7Image> EncodeAsync(Texture source,bool srgb=false,bool sampledInputIsSrgb=false,
            Bc7Quality quality=Bc7Quality.Fast,bool flipY=false)
        {
            if(!SystemInfo.supportsAsyncGPUReadback)throw new NotSupportedException("Async GPU readback is unavailable.");
            var buffer=Dispatch(source,srgb,sampledInputIsSrgb,quality,flipY);
            int width=source.width,height=source.height;
            var completion=new TaskCompletionSource<Bc7Image>();
            void Complete(AsyncGPUReadbackRequest request)
            {
                Bc7Image image=null;Exception failure=null;
                try
                {
                    if(request.hasError)throw new InvalidOperationException("BC7 GPU readback failed.");
                    image=new Bc7Image(width,height,srgb,request.GetData<byte>().ToArray());
                }
                catch(Exception error){failure=error;}
                finally{buffer.Dispose();}
                if(failure!=null)completion.TrySetException(failure);else completion.TrySetResult(image);
            }
            try
            {
#if UNITY_EDITOR
                var readback=AsyncGPUReadback.Request(buffer);readback.forcePlayerLoopUpdate=true;
                UnityEditor.EditorApplication.update+=Poll;
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
                void Poll()
                {
                    readback.Update();
                    if(!readback.done&&!readback.hasError)
                    {
                        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                        UnityEditor.SceneView.RepaintAll();
                        return;
                    }
                    UnityEditor.EditorApplication.update-=Poll;Complete(readback);
                }
#else
                AsyncGPUReadback.Request(buffer,Complete);
#endif
            }
            catch{buffer.Dispose();throw;}
            return completion.Task;
        }
    }
}
