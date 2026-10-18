using System;
using System.IO;
using UnityEngine;

namespace UnityCompute.Bc7.Editor
{
    // Independent bit fixtures validate all partition anchors against the hardware decoder.
    public static class Bc7PartitionValidation
    {
        public static string Run()
        {
            var data=new byte[64*3*16];
            for(int row=0;row<3;row++)for(int partition=0;partition<64;partition++)
            {
                int mode=row==0?1:row==1?3:7,precision=mode==1?6:mode==3?7:5,channels=mode==7?4:3;
                int offset=(row*64+partition)*16,bit=0;
                void Put(int value,int count){for(int b=0;b<count;b++,bit++)data[offset+(bit>>3)]|=(byte)(((value>>b)&1)<<(bit&7));}
                Put(1<<mode,mode+1);Put(partition,6);
                for(int c=0;c<channels;c++)for(int e=0;e<4;e++)Put((partition*13+c*19+e*31+7)&((1<<precision)-1),precision);
                for(int p=0;p<(mode==1?2:4);p++)Put((partition+p)&1,1);
                int bits=mode==1?3:2;
                for(int i=0;i<16;i++){int length=bits-((i==0||i==Bc7Partitions.Anchors[partition])?1:0);Put((i*5+partition)&((1<<length)-1),length);}
                if(bit!=128)throw new InvalidOperationException("Partition fixture bit count differs.");
            }
            var image=new Bc7Image(256,12,false,data);
            int difference=Bc7Validation.Compare(Bc7HardwareDecoder.Decode(image),Bc7ScalarDecoder.Decode(image));
            if(difference!=0)throw new InvalidOperationException("BC7 partition decoder differs from hardware: "+difference);
            string result="PASS: 192 BC7 mode 1/3/7 fixtures cover all 64 partitions and subset anchors; 12288 hardware/scalar channels match exactly.";
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.validation/bc7"));Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"partition-result.txt"),result);return result;
        }
    }
}
