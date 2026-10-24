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
    public static class Bc7Benchmark
    {
        static string Root=>Path.GetFullPath(Path.Combine(Application.dataPath,"../../Benchmark"));
        [Serializable]sealed class ModeUsage
        {
            public string texture,encoder;public int width,mode4,mode5,mode6;
            public int[] rotations=new int[4],selectors=new int[2];
        }
        [Serializable]sealed class Modes { public List<ModeUsage> rows=new List<ModeUsage>(); }
        public static string Run(string folder="bc7",int limit=0)
        {
            string directory=Path.Combine(Root,"Results",folder);Directory.CreateDirectory(directory);
            var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute");
            using var encoder=new Bc7Encoder(shader);
            string Hash(string path){using var h=SHA256.Create();return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(UnityCompute.TextureCompression.Editor.GpuAtlasProjectPaths.FilePath(path)))).Replace("-","");}
            var report=new AstcBenchmark.Measurements
            {
                utc=DateTime.UtcNow.ToString("O"),unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,
                cpu=SystemInfo.processorType,api=SystemInfo.graphicsDeviceType.ToString(),
                shaderSha256=Hash(AssetDatabase.GetAssetPath(shader)),inputManifestSha256=Hash(Path.Combine(Root,"Input/manifest.json")),
                methodology="Same 60 canonical 8-bit RGBA inputs as ASTC: 10 alpha + 10 opaque at 256/512/1024. BC7 4x4 = 8bpp. GPU Fast modes 6/5; Balanced modes 4/5/6 and rotations. 16 warmups and 9 synchronous API wall measurements (allocation, dispatch, readback, copy included). Reuse: 16 warmup dispatches with one readback, then 3 batches of 16 dispatches plus one final readback, divided by 16; not a GPU timestamp and excludes compressed texture upload. Unity reference: EditorUtility.CompressTexture BC7 Normal, 1 warmup and 3 timed API calls; input preparation is outside these timings, backend/thread count are not asserted. All results decoded by GPU BC7 UNORM hardware. Every GPU result compared exactly against the separate scalar decoder. Existing ASTC runs were measured at a different time; clocks were not controlled."
            };
            var modes=new Modes();int compared=0;
            foreach(string file in Directory.GetFiles(Path.Combine(Root,"Input"),"*.png",SearchOption.AllDirectories).OrderBy(p=>p).Take(limit>0?limit:int.MaxValue))
            {
                var source=AstcBenchmark.Load(file);var original=source.GetPixels32();string name=Path.GetFileNameWithoutExtension(file);
                try
                {
                    foreach(Bc7Quality quality in new[]{Bc7Quality.Fast,Bc7Quality.Balanced})
                    {
                        string profile=quality==Bc7Quality.Fast?"bc7-fast":"bc7-balanced";
                        Bc7Image image=null;
                        for(int i=0;i<16;i++)image=encoder.Encode(source,true,true,quality);
                        var times=new double[9];
                        for(int i=0;i<times.Length;i++){var clock=Stopwatch.StartNew();image=encoder.Encode(source,true,true,quality);times[i]=clock.Elapsed.TotalMilliseconds;}
                        var decoded=Bc7HardwareDecoder.Decode(image);
                        int difference=Bc7Validation.Compare(decoded,Bc7ScalarDecoder.Decode(image));
                        if(difference!=0)throw new InvalidOperationException($"BC7 decode mismatch {name}/{quality}: {difference}");
                        compared+=decoded.Length*4;
                        string stem=Path.Combine(directory,name+"_"+source.width+"_4_"+profile);
                        File.WriteAllBytes(stem+".bin",image.Data);AstcBenchmark.SaveRows(stem+".png",source.width,source.height,decoded);
                        var row=AstcBenchmark.Summarize(name,profile,4,source,original,decoded,times,null,image.Data.Length);
                        MeasureReuse(encoder,source,quality,row);report.rows.Add(row);
                        var usage=new ModeUsage{texture=name,encoder=profile,width=source.width};
                        for(int at=0;at<image.Data.Length;at+=16)
                        {
                            int mode=Bc7ScalarDecoder.Mode(image.Data,at);
                            if(mode==4){usage.mode4++;int bits=(image.Data[at]|(image.Data[at+1]<<8))>>5;usage.rotations[bits&3]++;usage.selectors[(bits>>2)&1]++;}
                            else if(mode==5){usage.mode5++;usage.rotations[(image.Data[at]>>6)&3]++;}
                            else if(mode==6)usage.mode6++;
                        }
                        modes.rows.Add(usage);
                    }
                    Reference(original,source.width,source.height,out _);
                    var referenceTimes=new double[3];Bc7Image reference=null;
                    for(int i=0;i<3;i++){reference=Reference(original,source.width,source.height,out double elapsed);referenceTimes[i]=elapsed;}
                    var referencePixels=Bc7HardwareDecoder.Decode(reference);
                    string referenceStem=Path.Combine(directory,name+"_"+source.width+"_4_unity-bc7");
                    File.WriteAllBytes(referenceStem+".bin",reference.Data);AstcBenchmark.SaveRows(referenceStem+".png",source.width,source.height,referencePixels);
                    report.rows.Add(AstcBenchmark.Summarize(name,"unity-bc7",4,source,original,referencePixels,referenceTimes,referenceTimes,reference.Data.Length));
                    report.peakMetricsUtc=DateTime.UtcNow.ToString("O");
                    File.WriteAllText(Path.Combine(directory,"measurements.json"),JsonUtility.ToJson(report,true));
                    File.WriteAllText(Path.Combine(directory,"modes.json"),JsonUtility.ToJson(modes,true));
                }
                finally{Object.DestroyImmediate(source);}
            }
            return $"Saved {report.rows.Count} BC7 rows; {compared} GPU/scalar channels agree exactly: {directory}";
        }
        static Bc7Image Reference(Color32[] pixels,int width,int height,out double elapsed)
        {
            var copy=new Texture2D(width,height,TextureFormat.RGBA32,false,true);
            try
            {
                copy.SetPixels32(pixels);copy.Apply(false,false);
                var clock=Stopwatch.StartNew();EditorUtility.CompressTexture(copy,TextureFormat.BC7,TextureCompressionQuality.Normal);elapsed=clock.Elapsed.TotalMilliseconds;
                if(copy.format!=TextureFormat.BC7)throw new InvalidOperationException("Editor reference did not produce BC7.");
                return new Bc7Image(width,height,true,copy.GetRawTextureData<byte>().ToArray());
            }
            finally{Object.DestroyImmediate(copy);}
        }
        static void MeasureReuse(Bc7Encoder encoder,Texture source,Bc7Quality quality,AstcBenchmark.Row row)
        {
            using var buffer=new ComputeBuffer(Bc7Encoder.RequiredBlockCount(source),16);
            var words=new uint[buffer.count*4];
            for(int i=0;i<16;i++)encoder.EncodeGpu(source,buffer,true,true,quality);
            buffer.GetData(words);
            var batches=new double[3];var submissions=new List<double>();
            for(int repeat=0;repeat<3;repeat++)
            {
                var clock=Stopwatch.StartNew();
                for(int i=0;i<16;i++){var submit=Stopwatch.StartNew();encoder.EncodeGpu(source,buffer,true,true,quality);submissions.Add(submit.Elapsed.TotalMilliseconds);}
                buffer.GetData(words);batches[repeat]=clock.Elapsed.TotalMilliseconds/16;
            }
            Array.Sort(batches);submissions.Sort();row.bufferReuseBatchMedianMs=batches[1];row.submissionMedianMs=submissions[submissions.Count/2];
        }
    }
}
