// Standalone verification source. The build tool copies this into a temporary Unity asset folder.
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityCompute.Bc7;

namespace UnityCompute.Bc7.Verification
{
    public sealed class Bc7GpuPlayerProbe:MonoBehaviour
    {
        public ComputeShader encoderShader,fillShader,decodeShader;
        [Serializable]sealed class Report
        {
            public bool passed,resourcesReleased;
            public string utc,unity,gpu,api,error;
            public int frames,comparedPixels,copies,slicedJobs,slicedFrames;
            public long hotLoopAllocatedBytes,slicedBeginAllocatedBytes,slicedStepAllocatedBytes;
        }
        Report report;
        Bc7GpuTexture output;
        Bc7Encoder reference;
        RenderTexture source;
        string reportPath;
        IEnumerator Start()
        {
            reportPath=Path.GetFullPath("bc7-player-probe.json");
            string[] args=Environment.GetCommandLineArgs();for(int i=0;i<args.Length-1;i++)if(args[i]=="-bc7-probe-report")reportPath=args[i+1];
            report=new Report{utc=DateTime.UtcNow.ToString("O"),unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString()};
            Application.runInBackground=true;Application.targetFrameRate=60;QualitySettings.vSyncCount=0;
            try
            {
                Require(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D12,"Player did not use D3D12.");
                source=new RenderTexture(256,256,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear){enableRandomWrite=true};source.Create();
                output=new Bc7GpuTexture(encoderShader,256,256,true);reference=new Bc7Encoder(encoderShader);
            }
            catch(Exception error){Finish(error);yield break;}
            for(int frame=0;frame<180;frame++)
            {
                try
                {
                    var quality=frame<60?Bc7Quality.Fast:frame<120?Bc7Quality.Balanced:Bc7Quality.HighQuality;
                    int fill=fillShader.FindKernel("Fill");fillShader.SetInts("_Size",256,256);fillShader.SetInt("_Frame",frame);fillShader.SetTexture(fill,"_Target",source);fillShader.Dispatch(fill,32,32,1);
                    output.EncodeFrame(source,quality:quality);report.frames++;
                    if(frame%15==0||frame==179)Compare(quality);
                }
                catch(Exception error){Finish(error);yield break;}
                yield return null;
            }
            for(int job=0;job<4;job++)
            {
                var quality=job<2?Bc7Quality.Fast:job==2?Bc7Quality.Balanced:Bc7Quality.HighQuality;
                int rowBudget=quality==Bc7Quality.Fast?2:quality==Bc7Quality.Balanced?8:3;
                Texture2D expected=null;uint[] previous=null,target=null;int beforeCopies=0,steps=0;
                try
                {
                    Fill(700+job);expected=reference.Encode(source,true,false,quality).CreateTexture();
                    previous=Read(output.Texture);target=Read(expected);beforeCopies=output.RecordedCopies;
                    long before=GC.GetAllocatedBytesForCurrentThread();output.BeginEncode(source,quality:quality);
                    if(job>0)report.slicedBeginAllocatedBytes+=GC.GetAllocatedBytesForCurrentThread()-before;
                }
                catch(Exception error){if(expected!=null)Destroy(expected);Finish(error);yield break;}
                while(output.IsEncoding)
                {
                    try
                    {
                        Fill(900+report.slicedFrames);int frameStartRows=output.SubmittedBlockRows;
                        for(int repeat=0;repeat<3&&output.IsEncoding;repeat++)
                        {
                            long before=GC.GetAllocatedBytesForCurrentThread();output.EncodeNextFrame(rowBudget);
                            if(job>0)report.slicedStepAllocatedBytes+=GC.GetAllocatedBytesForCurrentThread()-before;
                        }
                        Require(output.SubmittedBlockRows-frameStartRows<=rowBudget,"More than one row budget was queued in one Player frame.");
                        var pixels=Read(output.Texture);int copies=output.RecordedCopies-beforeCopies;
                        Require(copies==0||copies==1,"Sliced job published more than once.");
                        var wanted=copies==0?previous:target;
                        for(int i=0;i<pixels.Length;i++)Require(pixels[i]==wanted[i],"Partial or mixed-frame texture published.");
                        report.comparedPixels+=pixels.Length;
                        // Read forces completion. Repeating Step must still obey the same-frame limit.
                        if(output.IsEncoding)
                        {
                            long before=GC.GetAllocatedBytesForCurrentThread();output.EncodeNextFrame(rowBudget);
                            if(job>0)report.slicedStepAllocatedBytes+=GC.GetAllocatedBytesForCurrentThread()-before;
                            Require(output.SubmittedBlockRows-frameStartRows<=rowBudget,"A completed fence bypassed the per-frame budget.");
                        }
                        Require(++steps<200,"Sliced job timed out.");report.slicedFrames++;
                    }
                    catch(Exception error){Destroy(expected);Finish(error);yield break;}
                    yield return null;
                }
                try
                {
                    Require(output.EncodingProgress==1&&output.CompletedBlockRows==output.TotalBlockRows,"Sliced progress did not complete.");
                    Require(output.RecordedCopies==beforeCopies+1,"Sliced job did not publish exactly once.");
                    report.slicedJobs++;Destroy(expected);
                }
                catch(Exception error){Destroy(expected);Finish(error);yield break;}
            }
            try
            {
                long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<32;i++)output.EncodeFrame(source);
                report.hotLoopAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-before;
                Compare(Bc7Quality.Fast);
                report.copies=output.RecordedCopies;Require(report.copies==216&&output.UploadStatus==1,"Player copy count differs.");
                Require(report.hotLoopAllocatedBytes==0,"Player hot loop allocates managed memory.");
                Require(report.slicedBeginAllocatedBytes==0&&report.slicedStepAllocatedBytes==0,"Reused sliced jobs allocate managed memory.");
                output.Dispose();
            }
            catch(Exception error){Finish(error);yield break;}
            for(int i=0;i<180&&!output.IsReleased;i++)yield return null;
            try{Require(output.IsReleased,"Player resource fence did not pass.");report.resourcesReleased=true;report.passed=true;Finish(null);}
            catch(Exception error){Finish(error);}
        }
        void Fill(int frame)
        {
            int kernel=fillShader.FindKernel("Fill");fillShader.SetInts("_Size",256,256);fillShader.SetInt("_Frame",frame);
            fillShader.SetTexture(kernel,"_Target",source);fillShader.Dispatch(kernel,32,32,1);
        }
        void Compare(Bc7Quality quality)
        {
            var texture=reference.Encode(source,true,false,quality).CreateTexture();
            try
            {
                uint[] a=Read(output.Texture),b=Read(texture);for(int i=0;i<a.Length;i++)Require(a[i]==b[i],"Player BC7 sample differs at "+i);
                report.comparedPixels+=a.Length;
                var targetA=new RenderTexture(256,256,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                var targetB=new RenderTexture(256,256,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                var previousActive=RenderTexture.active;
                try
                {
                    targetA.Create();targetB.Create();Graphics.Blit(output.Texture,targetA);Graphics.Blit(texture,targetB);
                    a=Read(targetA);b=Read(targetB);for(int i=0;i<a.Length;i++)Require(a[i]==b[i],"Player pixel-shader sample differs at "+i);
                    report.comparedPixels+=a.Length;
                }
                finally{RenderTexture.active=previousActive;Destroy(targetA);Destroy(targetB);}
            }
            finally{Destroy(texture);}
        }
        uint[] Read(Texture texture)
        {
            int kernel=decodeShader.FindKernel("DecodeHardware");using var pixels=new ComputeBuffer(256*256,4);
            decodeShader.SetInts("_Size",256,256);decodeShader.SetTexture(kernel,"_Source",texture);decodeShader.SetBuffer(kernel,"_Pixels",pixels);decodeShader.Dispatch(kernel,32,32,1);
            var words=new uint[pixels.count];pixels.GetData(words);return words;
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        void Finish(Exception error)
        {
            if(error!=null){report.error=error.ToString();report.passed=false;}
            File.WriteAllText(reportPath,JsonUtility.ToJson(report,true));
            if(output!=null&&!output.IsReleased)output.Dispose();reference?.Dispose();if(source!=null)Destroy(source);
            Application.Quit(report.passed?0:1);
        }
    }
}
