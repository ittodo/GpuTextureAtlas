using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityCompute.Bc7;
using UnityCompute.Bc7.Editor;
using Object=UnityEngine.Object;

namespace UnityCompute.Astc.Editor
{
    // Fresh matched measurements. Historical 6.6 runs remain separate.
    public static class HighQualityBenchmark
    {
        [Serializable]public sealed class Entry
        {
            public string format,quality;
            public AstcBenchmark.Row measurement;
            public double rgbaPsnr,pipelineMs;
            public long allocatedBytes;
            public int copies,improvedBlocks,regressedBlocks;
            public int[] modes;
        }
        [Serializable]public sealed class Report
        {
            public string unity,utc,completedUtc,gpu,cpu,api,manifestSha256,astcSha256,bc7Sha256,bc7HighQualitySha256,bc7PartitionsSha256,astcRuntimeSha256,bc7RuntimeSha256,pluginSha256,methodology;
            public int resolution;
            public List<Entry> rows=new List<Entry>();
        }
        static readonly string Root=Path.GetFullPath(Path.Combine(Application.dataPath,"../../Benchmark"));
        public static string Run(int resolution,int limit=0)
        {
            if(resolution!=256&&resolution!=512&&resolution!=1024)throw new ArgumentOutOfRangeException(nameof(resolution));
            string directory=Path.Combine(Root,"Results/high-quality");Directory.CreateDirectory(directory);
            var astcShader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Astc/AstcEncoder.compute");
            var bc7Shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute");
            using var astc=new AstcEncoder(astcShader);using var bc7=new Bc7Encoder(bc7Shader);
            string Hash(string p){using var h=SHA256.Create();return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(UnityCompute.TextureCompression.Editor.GpuAtlasProjectPaths.FilePath(p)))).Replace("-","");}
            var report=new Report{unity=Application.unityVersion,utc=DateTime.UtcNow.ToString("O"),gpu=SystemInfo.graphicsDeviceName,
                cpu=SystemInfo.processorType,api=SystemInfo.graphicsDeviceType.ToString(),resolution=resolution,
                manifestSha256=Hash(Path.Combine(Root,"Input/manifest.json")),astcSha256=Hash(AssetDatabase.GetAssetPath(astcShader)),bc7Sha256=Hash(AssetDatabase.GetAssetPath(bc7Shader)),
                bc7HighQualitySha256=Hash("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7HighQuality.hlsl"),bc7PartitionsSha256=Hash("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Partitions.hlsl"),pluginSha256=Hash("Packages/com.ittodo.gpu-texture-atlas/Plugins/Bc7/Windows/x86_64/Bc7GpuUpload.dll"),
                astcRuntimeSha256=Hash("Packages/com.ittodo.gpu-texture-atlas/Runtime/Astc/AstcEncoder.cs"),bc7RuntimeSha256=Hash("Packages/com.ittodo.gpu-texture-atlas/Runtime/Bc7/Bc7Encoder.cs"),
                methodology="Matched Unity 6.7 run on the same canonical alpha 10 + opaque 10 inputs at each size. ASTC 4x4/6x6, BC7 4x4; all three GPU qualities. Raw sRGB codes via linear input textures. 2 sync warmups, 5 timed sync API calls. Reused buffer: 3 batches of 8 dispatches with one final readback, median divided by 8. BC7 texture pipeline: 2 warmups + 3 batches of 8 EncodeFrame calls, final 1-pixel GPU sample/readback per batch, median divided by 8. Throughput includes amortized completion readback; not GPU timestamp/frame latency. EncodeFrame itself has no readback. Quality and peak metrics share the original definitions. ASTC decoded with official astcenc 5.7.0. All BC7 pixels checked against scalar and hardware decoders. Integer RGBA block errors compare HQ to Balanced; each metric can trade against the other channels. Desktop D3D12 timings, no mobile timing claims."};
            foreach(string path in Directory.GetFiles(Path.Combine(Root,"Input",resolution.ToString()),"*.png").OrderBy(p=>p).Take(limit>0?limit:int.MaxValue))
            {
                var source=AstcBenchmark.Load(path);var original=source.GetPixels32();string name=Path.GetFileNameWithoutExtension(path);
                try
                {
                    foreach(var size in new[]{AstcBlockSize.Block4x4,AstcBlockSize.Block6x6})
                    {
                        long[] balanced=null;
                        foreach(AstcQuality quality in Enum.GetValues(typeof(AstcQuality)))
                        {
                            string profile=quality.ToString(),stem=Path.Combine(directory,$"{name}_{resolution}_{(int)size}_astc-{profile}");
                            AstcImage image=null;for(int i=0;i<2;i++)image=astc.Encode(source,size,true,true,quality);
                            var times=new double[5];for(int i=0;i<5;i++){var clock=Stopwatch.StartNew();image=astc.Encode(source,size,true,true,quality);times[i]=clock.Elapsed.TotalMilliseconds;}
                            File.WriteAllBytes(stem+".astc",image.ToAstcFile());var decoded=AstcBenchmark.Decode(stem,true,resolution);
                            var row=AstcBenchmark.Summarize(name,"astc-"+profile,(int)size,source,original,decoded,times,null,image.Data.Length);
                            using(var buffer=new ComputeBuffer(AstcEncoder.RequiredBlockCount(source,size),16))
                            {MeasureReuse(()=>astc.EncodeGpu(source,buffer,size,true,true,quality),buffer,row);}
                            var errors=BlockErrors(original,decoded,resolution,(int)size);if(quality==AstcQuality.Balanced)balanced=errors;
                            report.rows.Add(EntryFor("ASTC",profile,row,errors,quality==AstcQuality.HighQuality?balanced:null));
                        }
                    }
                    long[] balancedBc7=null;
                    foreach(Bc7Quality quality in Enum.GetValues(typeof(Bc7Quality)))
                    {
                        string profile=quality.ToString(),stem=Path.Combine(directory,$"{name}_{resolution}_4_bc7-{profile}");
                        Bc7Image image=null;for(int i=0;i<2;i++)image=bc7.Encode(source,true,true,quality);
                        var times=new double[5];for(int i=0;i<5;i++){var clock=Stopwatch.StartNew();image=bc7.Encode(source,true,true,quality);times[i]=clock.Elapsed.TotalMilliseconds;}
                        var decoded=Bc7HardwareDecoder.Decode(image);
                        if(Bc7Validation.Compare(decoded,Bc7ScalarDecoder.Decode(image))!=0)throw new InvalidOperationException("BC7 decoder mismatch: "+stem);
                        File.WriteAllBytes(stem+".bin",image.Data);AstcBenchmark.SaveRows(stem+".png",resolution,resolution,decoded);
                        var row=AstcBenchmark.Summarize(name,"bc7-"+profile,4,source,original,decoded,times,null,image.Data.Length);
                        using(var buffer=new ComputeBuffer(Bc7Encoder.RequiredBlockCount(source),16)){MeasureReuse(()=>bc7.EncodeGpu(source,buffer,true,true,quality),buffer,row);}
                        var errors=BlockErrors(original,decoded,resolution,4);if(quality==Bc7Quality.Balanced)balancedBc7=errors;
                        var entry=EntryFor("BC7",profile,row,errors,quality==Bc7Quality.HighQuality?balancedBc7:null);entry.modes=new int[8];
                        for(int at=0;at<image.Data.Length;at+=16)entry.modes[Bc7ScalarDecoder.Mode(image.Data,at)]++;
                        if(quality==Bc7Quality.HighQuality&&entry.regressedBlocks!=0)throw new InvalidOperationException("BC7 HQ increased a block's RGBA error.");
                        MeasureTexture(source,bc7Shader,quality,entry);report.rows.Add(entry);
                    }
                    report.completedUtc=DateTime.UtcNow.ToString("O");
                    File.WriteAllText(Path.Combine(directory,$"measurements-{resolution}{(limit>0?"-probe":"")}.json"),JsonUtility.ToJson(report,true));
                }
                finally{Object.DestroyImmediate(source);}
            }
            return $"Saved {report.rows.Count} matched HQ comparison rows at {resolution}: {directory}";
        }
        static Entry EntryFor(string format,string quality,AstcBenchmark.Row row,long[] errors,long[] balanced)
        {
            long total=0;int improved=0,regressed=0;
            for(int i=0;i<errors.Length;i++){total+=errors[i];if(balanced!=null){if(errors[i]<balanced[i])improved++;else if(errors[i]>balanced[i])regressed++;}}
            double mse=(double)total/(row.width*row.height*4);
            return new Entry{format=format,quality=quality,measurement=row,rgbaPsnr=mse==0?120:10*Math.Log10(255.0*255/mse),improvedBlocks=improved,regressedBlocks=regressed};
        }
        static long[] BlockErrors(Color32[] original,Color32[] decoded,int width,int size)
        {
            int columns=(width+size-1)/size;var errors=new long[columns*columns];
            for(int y=0;y<width;y++)for(int x=0;x<width;x++)
            {int at=y*width+x;long e=0;for(int c=0;c<4;c++){int d=original[at][c]-decoded[at][c];e+=d*d;}errors[y/size*columns+x/size]+=e;}
            return errors;
        }
        static void MeasureReuse(Action dispatch,ComputeBuffer buffer,AstcBenchmark.Row row)
        {
            var words=new uint[buffer.count*4];dispatch();buffer.GetData(words);var batches=new double[3];
            for(int b=0;b<3;b++){var clock=Stopwatch.StartNew();for(int i=0;i<8;i++)dispatch();buffer.GetData(words);batches[b]=clock.Elapsed.TotalMilliseconds/8;}
            Array.Sort(batches);row.bufferReuseBatchMedianMs=batches[1];
        }
        static void MeasureTexture(Texture source,ComputeShader encoderShader,Bc7Quality quality,Entry entry)
        {
            using var output=new Bc7GpuTexture(encoderShader,source.width,source.height,true);
            using var pixel=new ComputeBuffer(1,4);var words=new uint[1];
            var decoder=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7DecodeHardware.compute");int kernel=decoder.FindKernel("DecodeHardware");
            void Wait(){decoder.SetInts("_Size",1,1);decoder.SetTexture(kernel,"_Source",output.Texture);decoder.SetBuffer(kernel,"_Pixels",pixel);decoder.Dispatch(kernel,1,1,1);pixel.GetData(words);}
            for(int i=0;i<2;i++)output.EncodeFrame(source,true,quality);Wait();var batches=new double[3];
            for(int b=0;b<3;b++)
            {
                var clock=Stopwatch.StartNew();long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<8;i++)output.EncodeFrame(source,true,quality);
                entry.allocatedBytes+=GC.GetAllocatedBytesForCurrentThread()-before;Wait();batches[b]=clock.Elapsed.TotalMilliseconds/8;
            }
            if(entry.allocatedBytes!=0||output.UploadStatus!=1||output.RecordedCopies!=26)throw new InvalidOperationException("BC7 texture streaming invariant failed.");
            Array.Sort(batches);entry.pipelineMs=batches[1];entry.copies=output.RecordedCopies;
        }
    }
}
