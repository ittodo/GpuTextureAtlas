using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityCompute.Astc.Editor;
using Object=UnityEngine.Object;

namespace UnityCompute.Bc7.Editor
{
    public static class Bc7GpuTextureBenchmark
    {
        [Serializable]public sealed class Row
        {
            public string texture,group,encoder;
            public int width,height,payloadBytes,recordedCopies;
            public double pipelineBatchMedianMs,submissionMedianMs,submissionP95Ms;
            public long allocatedBytes;
        }
        [Serializable]public sealed class Report
        {
            public string utc,completedUtc,unity,gpu,api,methodology,inputManifestSha256,shaderSha256,pluginSha256;
            public List<Row> rows=new List<Row>();
        }
        public static string Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../../Benchmark")),directory=Path.Combine(root,"Results/bc7");
            Directory.CreateDirectory(directory);
            string Hash(string path){using var h=SHA256.Create();return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
            var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7Encoder.compute");
            var report=new Report
            {
                utc=DateTime.UtcNow.ToString("O"),unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),
                inputManifestSha256=Hash(Path.Combine(root,"Input/manifest.json")),shaderSha256=Hash(AssetDatabase.GetAssetPath(shader)),
                pluginSha256=Hash("Assets/Bc7/Plugins/Windows/x86_64/Bc7GpuUpload.dll"),
                methodology="Same 60 canonical inputs, Fast/Balanced. Bc7GpuTexture on Windows x64 D3D12: compute encode, native GPU CopyTextureRegion, BC7 texture sampling. 16 warmups, then 3 batches of 16 EncodeFrame calls; one hardware sample/readback after each batch waits for completion. Batch elapsed divided by 16 includes CPU submission, encode and texture copy, plus amortized final sample/readback. It is throughput, not GPU timestamp or frame latency. EncodeFrame itself has no pixel readback or Apply. Submission median/p95 cover only EncodeFrame CPU calls, not GPU completion. Managed hot-loop allocation measured separately; 64 copies per input/profile. Initialization and input preparation excluded. Clocks and other desktop apps not controlled."
            };
            foreach(string file in Directory.GetFiles(Path.Combine(root,"Input"),"*.png",SearchOption.AllDirectories).OrderBy(p=>p))
            {
                var source=AstcBenchmark.Load(file);
                try
                {
                    foreach(Bc7Quality quality in new[]{Bc7Quality.Fast,Bc7Quality.Balanced})
                    {
                        using var output=new Bc7GpuTexture(shader,source.width,source.height,true);
                        using var sampler=new Sampler(output.Texture);
                        for(int i=0;i<16;i++)output.EncodeFrame(source,true,quality);
                        sampler.Wait();
                        var batches=new double[3];var submissions=new double[48];var clock=new Stopwatch();
                        long allocated=0;
                        for(int batch=0;batch<3;batch++)
                        {
                            var total=Stopwatch.StartNew();long before=GC.GetAllocatedBytesForCurrentThread();
                            for(int i=0;i<16;i++)
                            {
                                clock.Restart();output.EncodeFrame(source,true,quality);clock.Stop();submissions[batch*16+i]=clock.Elapsed.TotalMilliseconds;
                            }
                            allocated+=GC.GetAllocatedBytesForCurrentThread()-before;
                            sampler.Wait();batches[batch]=total.Elapsed.TotalMilliseconds/16;
                        }
                        if(output.UploadStatus!=1||output.RecordedCopies!=64)throw new InvalidOperationException("BC7 benchmark copy count differs.");
                        Array.Sort(batches);Array.Sort(submissions);
                        string name=Path.GetFileNameWithoutExtension(file);
                        report.rows.Add(new Row
                        {
                            texture=name,group=name.StartsWith("rgba_")||name=="ui_alpha_atlas"?"alpha":"opaque",
                            encoder=quality==Bc7Quality.Fast?"bc7-fast":"bc7-balanced",width=source.width,height=source.height,
                            payloadBytes=(source.width+3)/4*((source.height+3)/4)*16,recordedCopies=output.RecordedCopies,
                            pipelineBatchMedianMs=batches[1],submissionMedianMs=submissions[24],submissionP95Ms=submissions[(int)Math.Ceiling(.95*submissions.Length)-1],allocatedBytes=allocated
                        });
                    }
                    report.completedUtc=DateTime.UtcNow.ToString("O");File.WriteAllText(Path.Combine(directory,"gpu-texture-measurements.json"),JsonUtility.ToJson(report,true));
                }
                finally{Object.DestroyImmediate(source);}
            }
            return $"Saved {report.rows.Count} BC7 encode/copy/sample rows; {report.rows.Sum(r=>r.recordedCopies)} native copies; {report.rows.Sum(r=>r.allocatedBytes)} hot-loop managed bytes.";
        }
        sealed class Sampler:IDisposable
        {
            readonly ComputeShader shader;
            readonly ComputeBuffer pixels;
            readonly uint[] words;
            readonly Texture texture;
            readonly int kernel;
            public Sampler(Texture texture)
            {
                this.texture=texture;
                shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Bc7/Shaders/Bc7DecodeHardware.compute");kernel=shader.FindKernel("DecodeHardware");
                pixels=new ComputeBuffer(1,4);words=new uint[1];
            }
            public void Wait()
            {
                // One texel is enough to order a sample behind all prior copies on the graphics queue.
                shader.SetInts("_Size",1,1);shader.SetTexture(kernel,"_Source",texture);shader.SetBuffer(kernel,"_Pixels",pixels);
                shader.Dispatch(kernel,1,1,1);pixels.GetData(words);
            }
            public void Dispose()=>pixels.Dispose();
        }
    }
}
