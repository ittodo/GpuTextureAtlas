using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug=UnityEngine.Debug;
using Object=UnityEngine.Object;

namespace UnityCompute.Bc7.Editor
{
    // Readback and pixel comparisons belong to this verification tool, never to EncodeFrame.
    public static class Bc7GpuTextureValidation
    {
        [Serializable]sealed class Report
        {
            public string utc,unity,gpu,api;
            public bool passed;
            public int cases,frames,comparedChannels,maximumDifference,retiredOutputs;
            public long encodeFrameAllocatedBytes;
            public List<string> details=new List<string>();
        }
        static readonly string Root=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.validation/bc7"));
        public static string Status{get;private set;}="not run";
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        public static void RunAsync()
        {
            if(Status=="running")throw new InvalidOperationException("Validation is already running.");
            Status="running";
            // Drive simulated frames even when Unity 6.7 Edit Mode views are idle.
            EditorApplication.update+=PumpFrames;
            Run();
        }
        static void PumpFrames(){EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();}
        static async void Run()
        {
            var report=new Report{utc=DateTime.UtcNow.ToString("O"),unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString()};
            try
            {
                await Task.Yield();
                var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7Encoder.compute");
                using var reference=new Bc7Encoder(shader);
                foreach(var quality in new[]{Bc7Quality.Fast,Bc7Quality.Balanced,Bc7Quality.HighQuality})foreach(bool srgb in new[]{false,true})
                foreach(var size in new[]{new Vector2Int(1,1),new Vector2Int(17,13),new Vector2Int(64,4),new Vector2Int(65,33),new Vector2Int(256,256),new Vector2Int(512,512),new Vector2Int(1024,1024)})
                {
                    var source=Source(size.x,size.y);Bc7GpuTexture output=null;
                    try
                    {
                        Fill(source,report.cases+3);output=new Bc7GpuTexture(shader,size.x,size.y,srgb);
                        Require(output.Texture.format==TextureFormat.BC7&&!output.Texture.isReadable,"Output must remain GPU-only BC7.");
                        Require(output.UvScale==new Vector2((float)size.x/output.Texture.width,(float)size.y/output.Texture.height),"Invalid UV extent.");
                        var initial=Read(output.Texture,size.x,size.y);foreach(uint pixel in initial)Require(pixel==0,"Initial BC7 texture is not transparent black.");
                        output.EncodeFrame(source,quality:quality);report.frames++;
                        Compare(reference,output,source,srgb,quality,report);
                        Require(output.UploadStatus==1&&output.RecordedCopies==1,"Native upload was not recorded exactly once.");
                        report.cases++;report.details.Add($"{quality}/{size.x}x{size.y}/sRGB={srgb}: exact hardware sampling; one copy; no CPU texture shadow");
                    }
                    finally{output?.Dispose();Object.DestroyImmediate(source);}
                    if(output!=null){await Retired(output);report.retiredOutputs++;}
                    await Task.Yield();
                }
                foreach(var quality in new[]{Bc7Quality.Fast,Bc7Quality.Balanced,Bc7Quality.HighQuality})
                {
                    var source=Source(256,256);var output=new Bc7GpuTexture(shader,256,256,true);
                    var texture=output.Texture;IntPtr native=texture.GetNativeTexturePtr();
                    try
                    {
                        for(int frame=0;frame<90;frame++)
                        {
                            Fill(source,frame);output.EncodeFrame(source,quality:quality);report.frames++;
                            if(frame%29==0||frame==89)Compare(reference,output,source,true,quality,report);
                            Require(ReferenceEquals(texture,output.Texture),"Streaming replaced the texture.");
                            await Task.Yield();
                        }
                        Require(output.Texture.GetNativeTexturePtr()==native,"Streaming changed the native texture.");
                        // Isolate managed allocations in the hot API after its warmup. No readback in this loop.
                        long before=GC.GetAllocatedBytesForCurrentThread();
                        for(int i=0;i<32;i++)output.EncodeFrame(source,quality:quality);
                        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
                        report.encodeFrameAllocatedBytes+=allocated;report.frames+=32;
                        Require(allocated==0,"EncodeFrame allocated managed memory: "+allocated);
                        Compare(reference,output,source,true,quality,report);
                        Require(output.RecordedCopies==122&&output.UploadStatus==1,"Continuous-frame copy count differs.");
                        report.details.Add($"{quality}: 90 changing frames + 32 queued copies; stable native texture; hot-loop managed allocation {allocated} bytes");
                    }
                    finally{output.Dispose();Object.DestroyImmediate(source);}
                    await Retired(output);report.retiredOutputs++;
                }
                report.passed=true;Status=$"PASS: {report.cases} cases, {report.frames} copies, {report.comparedChannels} sampled channels, difference {report.maximumDifference}/255, hot-loop allocation {report.encodeFrameAllocatedBytes} bytes, {report.retiredOutputs} outputs retired.";
            }
            catch(Exception error){Status="FAIL: "+error;Debug.LogException(error);}
            finally{EditorApplication.update-=PumpFrames;Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"gpu-texture-validation.json"),JsonUtility.ToJson(report,true));File.WriteAllText(Path.Combine(Root,"gpu-texture-status.txt"),Status);}
        }
        static async Task Retired(Bc7GpuTexture output)
        {
            var watch=Stopwatch.StartNew();
            while(!output.IsReleased&&watch.Elapsed.TotalSeconds<15)await Task.Yield();
            Require(output.IsReleased,"GPU resources did not retire after their fence.");
        }
        static RenderTexture Source(int width,int height)
        {
            var rt=new RenderTexture(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear){enableRandomWrite=true};
            Require(rt.Create(),"Cannot create verification source.");return rt;
        }
        static void Fill(RenderTexture target,int frame)
        {
            var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7StreamValidation.compute");
            int kernel=shader.FindKernel("Fill");shader.SetInts("_Size",target.width,target.height);shader.SetInt("_Frame",frame);shader.SetTexture(kernel,"_Target",target);
            shader.Dispatch(kernel,(target.width+7)/8,(target.height+7)/8,1);
        }
        static void Compare(Bc7Encoder encoder,Bc7GpuTexture output,Texture source,bool srgb,Bc7Quality quality,Report report)
        {
            var expected=encoder.Encode(source,srgb,false,quality).CreateTexture();
            try
            {
                var a=Read(output.Texture,source.width,source.height);var b=Read(expected,source.width,source.height);
                for(int i=0;i<a.Length;i++)for(int c=0;c<4;c++)
                {
                    int difference=Math.Abs((int)((a[i]>>(c*8))&255)-(int)((b[i]>>(c*8))&255));
                    report.maximumDifference=Math.Max(report.maximumDifference,difference);
                    Require(difference==0,$"GPU texture differs at pixel {i}, channel {c}: {difference}");
                }
                report.comparedChannels+=a.Length*4;
                // Blit exercises pixel-shader sampling and Unity's transitions after the copy.
                var renderedA=new RenderTexture(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                var renderedB=new RenderTexture(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                var previousActive=RenderTexture.active;
                try
                {
                    renderedA.Create();renderedB.Create();
                    Graphics.Blit(output.Texture,renderedA,output.UvScale,Vector2.zero);Graphics.Blit(expected,renderedB,output.UvScale,Vector2.zero);
                    var ra=Read(renderedA,source.width,source.height);var rb=Read(renderedB,source.width,source.height);
                    for(int i=0;i<ra.Length;i++)Require(ra[i]==rb[i],"Pixel-shader sample differs at "+i);
                    report.comparedChannels+=ra.Length*4;
                }
                finally{RenderTexture.active=previousActive;Object.DestroyImmediate(renderedA);Object.DestroyImmediate(renderedB);}
            }
            finally{Object.DestroyImmediate(expected);}
        }
        static uint[] Read(Texture source,int width,int height)
        {
            var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7DecodeHardware.compute");
            int kernel=shader.FindKernel("DecodeHardware");using var pixels=new ComputeBuffer(width*height,4);
            shader.SetInts("_Size",width,height);shader.SetTexture(kernel,"_Source",source);shader.SetBuffer(kernel,"_Pixels",pixels);
            shader.Dispatch(kernel,(width+7)/8,(height+7)/8,1);var words=new uint[pixels.count];pixels.GetData(words);return words;
        }
    }
}
