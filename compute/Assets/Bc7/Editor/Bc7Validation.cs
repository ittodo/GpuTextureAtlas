using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace UnityCompute.Bc7.Editor
{
    public static class Bc7Validation
    {
        const string ShaderPath="Packages/com.ittodo.gpu-texture-atlas/Shaders/Bc7/Bc7Encoder.compute";
        static string Root=>Path.GetFullPath(Path.Combine(Application.dataPath,"../../.validation/bc7"));
        public static string AsyncStatus{get;private set;}="not run";
        [Serializable]sealed class Report
        {
            public string utc,unity,gpu,api;public int cases,comparedChannels,maxDecoderDifference;
            public bool passed;public List<string> details=new List<string>();
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        public static string Run()
        {
            Directory.CreateDirectory(Root);
            var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
            Require(ShaderUtil.GetComputeShaderMessages(shader).Length==0,"BC7 encoder shader has diagnostics.");
            Require(SystemInfo.SupportsTextureFormat(TextureFormat.BC7),"BC7 hardware sampling is required for this validation.");
            var report=new Report{utc=DateTime.UtcNow.ToString("O"),unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString()};
            using var encoder=new Bc7Encoder(shader);
            foreach(Bc7Quality quality in Enum.GetValues(typeof(Bc7Quality)))
            {
                Check("black",8,8,(x,y)=>new Color32(0,0,0,255));
                Check("white",8,8,(x,y)=>new Color32(255,255,255,255));
                Check("solid_rgba",13,17,(x,y)=>new Color32(37,113,219,83));
                Check("transparent",7,5,(x,y)=>new Color32(91,27,151,0));
                Check("gradient",65,33,(x,y)=>new Color(x/64f,y/32f,(x/64f+y/32f)*.5f,1));
                Check("anticorrelated",17,13,(x,y)=>new Color(x/16f,1-x/16f,.25f,1));
                Check("alpha",17,13,(x,y)=>new Color(.2f,.4f,.7f,x/16f));
                Check("rgba_detail",17,13,(x,y)=>new Color(x&1,y&1,((x+y)%3)*.5f,((x*7+y*3)%11)/10f));
                Check("one_pixel",1,1,(x,y)=>new Color32(31,79,191,83));
                Check("one_row",19,1,(x,y)=>new Color(.3f,x/18f,.7f,1));
                Check("one_column",1,19,(x,y)=>new Color(y/18f,.3f,.7f,1));
                Check("linear_to_srgb",5,3,(x,y)=>new Color32(128,64,192,83),true);
                Check("srgb_to_linear",5,3,(x,y)=>new Color32(128,64,192,83),false,true);
                Check("flip_y",11,7,(x,y)=>new Color(x/10f,y/6f,.3f,1),flip:true);
                void Check(string name,int width,int height,Func<int,int,Color32> pixel,bool srgb=false,bool inputSrgb=false,bool flip=false)
                {
                    var source=new Texture2D(width,height,TextureFormat.RGBA32,false,true);
                    var original=new Color32[width*height];
                    for(int y=0;y<height;y++)for(int x=0;x<width;x++)original[y*width+x]=pixel(x,y);
                    source.SetPixels32(original);source.Apply();
                    try
                    {
                        var image=encoder.Encode(source,srgb,inputSrgb,quality,flip);
                        var scalar=Bc7ScalarDecoder.Decode(image);var hardware=Bc7HardwareDecoder.Decode(image);
                        int difference=Compare(scalar,hardware);
                        Require(difference==0,"BC7 hardware/scalar mismatch: "+name+" "+quality+" "+difference);
                        if(name=="linear_to_srgb"||name=="srgb_to_linear")
                        {
                            var expected=original[0];
                            for(int c=0;c<3;c++)
                            {
                                double value=expected[c]/255.0;
                                value=srgb?(value<=.0031308?value*12.92:1.055*Math.Pow(value,1.0/2.4)-.055):
                                    value<=.04045?value/12.92:Math.Pow((value+.055)/1.055,2.4);
                                expected[c]=(byte)Math.Floor(value*255+.5);
                            }
                            foreach(var actual in hardware)for(int c=0;c<4;c++)Require(Math.Abs(actual[c]-expected[c])<=2,"Color transfer differs: "+name);
                        }
                        if(flip)
                        {
                            double error=0;
                            for(int y=0;y<height;y++)for(int x=0;x<width;x++)for(int c=0;c<3;c++)
                            {double d=hardware[y*width+x][c]-original[(height-1-y)*width+x][c];error+=d*d;}
                            Require(10*Math.Log10(65025.0*width*height*3/Math.Max(error,1e-30))>24,"Flipped row direction differs.");
                        }
                        if(original.All(c=>c.a==255))Require(hardware.All(c=>c.a==255),"Opaque alpha changed: "+name);
                        if(original.All(c=>c.a==0))Require(hardware.All(c=>c.a==0),"Fully transparent alpha changed: "+name);
                        var dds=image.ToDdsFile();Require(dds.Length==image.Data.Length+148&&BitConverter.ToUInt32(dds,128)==(srgb?99u:98u),"DDS layout differs.");
                        int columns=(width+3)/4,rows=(height+3)/4,stride=columns+3;
                        using var padded=new ComputeBuffer(stride*rows,16);
                        var words=Enumerable.Repeat(0xDEADBEEFu,padded.count*4).ToArray();padded.SetData(words);
                        encoder.EncodeGpu(source,padded,srgb,inputSrgb,quality,stride,flip);padded.GetData(words);
                        var tight=new byte[columns*rows*16];
                        for(int row=0;row<rows;row++)
                        {
                            Buffer.BlockCopy(words,row*stride*16,tight,row*columns*16,columns*16);
                            for(int word=columns*4;word<stride*4;word++)Require(words[row*stride*4+word]==0xDEADBEEFu,"Row padding changed.");
                        }
                        Require(tight.SequenceEqual(image.Data),"Padded BC7 output differs.");
                        report.cases++;report.comparedChannels+=hardware.Length*4;report.maxDecoderDifference=Math.Max(report.maxDecoderDifference,difference);
                        report.details.Add($"{quality}/{name} {width}x{height}: hardware/scalar exact; padding preserved");
                    }
                    finally{Object.DestroyImmediate(source);}
                }
            }
            report.passed=true;File.WriteAllText(Path.Combine(Root,"report.json"),JsonUtility.ToJson(report,true));
            return $"PASS: {report.cases} BC7 GPU cases, {report.comparedChannels} channels, hardware/scalar difference {report.maxDecoderDifference}/255; padded output matches.";
        }
        public static int Compare(Color32[] a,Color32[] b)
        {
            Require(a.Length==b.Length,"Pixel counts differ.");int maximum=0;
            for(int i=0;i<a.Length;i++)for(int c=0;c<4;c++)maximum=Math.Max(maximum,Math.Abs(a[i][c]-b[i][c]));
            return maximum;
        }
        public static async void RunAsync()
        {
            AsyncStatus="running";Directory.CreateDirectory(Root);
            var texture=new Texture2D(17,13,TextureFormat.RGBA32,false,true);
            var rt=new RenderTexture(17,13,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var previousActive=RenderTexture.active;
            try
            {
                var pixels=new Color32[17*13];for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32((byte)(i*17),(byte)(i*3),(byte)(i*11),(byte)(i*7));
                texture.SetPixels32(pixels);texture.Apply();rt.Create();Graphics.Blit(texture,rt);
                using var encoder=new Bc7Encoder(AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath));
                foreach(Bc7Quality quality in Enum.GetValues(typeof(Bc7Quality)))foreach(bool flip in new[]{false,true})
                {
                    var expected=encoder.Encode(texture,true,true,quality,flip);
                    Require(expected.Data.SequenceEqual(encoder.Encode(rt,true,true,quality,flip).Data),"Texture2D/RenderTexture differ.");
                    var actual=await encoder.EncodeAsync(rt,true,true,quality,flip);
                    Require(expected.Data.SequenceEqual(actual.Data),"BC7 async result differs.");
                }
                AsyncStatus="PASS: 6 BC7 async results match sync and RenderTexture output (all three qualities and row directions).";
            }
            catch(Exception error){AsyncStatus="FAIL: "+error;Debug.LogException(error);}
            finally{RenderTexture.active=previousActive;rt.Release();Object.DestroyImmediate(rt);Object.DestroyImmediate(texture);File.WriteAllText(Path.Combine(Root,"async.txt"),AsyncStatus);}
        }
    }
}
