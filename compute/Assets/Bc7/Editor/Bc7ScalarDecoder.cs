using System;
using UnityEngine;

namespace UnityCompute.Bc7.Editor
{
    // Independent scalar reader for the emitted subset. Hardware is the conformance oracle.
    public static class Bc7ScalarDecoder
    {
        static readonly int[] W2={0,21,43,64},W3={0,9,18,27,37,46,55,64},W4={0,4,9,13,17,21,26,30,34,38,43,47,51,55,60,64};
        static int Read(byte[] data,int start,ref int bit,int count)
        {
            int value=0;for(int i=0;i<count;i++,bit++)value|=((data[start+(bit>>3)]>>(bit&7))&1)<<i;return value;
        }
        public static int Mode(byte[] data,int offset)
        {
            for(int mode=0;mode<8;mode++)if((data[offset]&(1<<mode))!=0)return mode;
            throw new InvalidOperationException("Reserved BC7 mode.");
        }
        public static Color32[] Decode(Bc7Image image)
        {
            var result=new Color32[image.Width*image.Height];int columns=(image.Width+3)/4;
            for(int by=0;by<(image.Height+3)/4;by++)for(int bx=0;bx<columns;bx++)
            {
                int offset=(by*columns+bx)*16,mode=Mode(image.Data,offset),bit=mode+1;
                if(mode==1||mode==3||mode==7)
                {
                    var pixels=DecodePartition(image.Data,offset,mode);
                    for(int i=0;i<16;i++){int x=bx*4+(i&3),y=by*4+(i>>2);if(x<image.Width&&y<image.Height)result[y*image.Width+x]=pixels[i];}
                    continue;
                }
                if(mode!=4&&mode!=5&&mode!=6)throw new NotSupportedException("Scalar reader handles BC7 modes 1, 3, 4, 5, 6, 7.");
                int rotation=mode==6?0:Read(image.Data,offset,ref bit,2);
                int selector=mode==4?Read(image.Data,offset,ref bit,1):0;
                var endpoints=new int[2,4];
                for(int c=0;c<4;c++)
                {
                    int precision=mode==6?7:c==3?(mode==5?8:6):(mode==5?7:5);
                    for(int e=0;e<2;e++)endpoints[e,c]=Read(image.Data,offset,ref bit,precision);
                }
                if(mode==6)
                {
                    for(int e=0;e<2;e++){int p=Read(image.Data,offset,ref bit,1);for(int c=0;c<4;c++)endpoints[e,c]=(endpoints[e,c]<<1)|p;}
                }
                else for(int c=0;c<4;c++)for(int e=0;e<2;e++)
                {
                    int precision=c==3?(mode==5?8:6):(mode==5?7:5);
                    int expanded=endpoints[e,c]<<(8-precision);endpoints[e,c]=expanded|(expanded>>precision);
                }
                var first=new int[16];var second=new int[16];
                int firstBits=mode==6?4:2,secondBits=mode==4?3:2;
                for(int i=0;i<16;i++)first[i]=Read(image.Data,offset,ref bit,firstBits-(i==0?1:0));
                if(mode!=6)for(int i=0;i<16;i++)second[i]=Read(image.Data,offset,ref bit,secondBits-(i==0?1:0));
                if(bit!=128)throw new InvalidOperationException("BC7 field length differs.");
                for(int i=0;i<16;i++)
                {
                    int x=bx*4+(i&3),y=by*4+(i>>2);if(x>=image.Width||y>=image.Height)continue;
                    int colorIndex=mode==4&&selector==1?second[i]:first[i];
                    int alphaIndex=mode==6?first[i]:mode==4&&selector==1?first[i]:second[i];
                    int colorBits=mode==6?4:mode==4&&selector==1?3:2,alphaBits=mode==6?4:mode==4&&selector==0?3:2;
                    int Weight(int q,int bits)=>bits==4?W4[q]:bits==3?W3[q]:W2[q];
                    var color=new Color32();
                    for(int c=0;c<4;c++){int w=c==3?Weight(alphaIndex,alphaBits):Weight(colorIndex,colorBits);color[c]=(byte)((endpoints[0,c]*(64-w)+endpoints[1,c]*w+32)>>6);}
                    if(rotation>0){byte t=color[rotation-1];color[rotation-1]=color.a;color.a=t;}
                    result[y*image.Width+x]=color;
                }
            }
            return result;
        }
        static Color32[] DecodePartition(byte[] data,int offset,int mode)
        {
            int bit=mode+1,partition=Read(data,offset,ref bit,6),precision=mode==1?6:mode==3?7:5,channels=mode==7?4:3;
            var endpoint=new int[4,4];
            for(int c=0;c<channels;c++)for(int e=0;e<4;e++)endpoint[e,c]=Read(data,offset,ref bit,precision);
            for(int subset=0;subset<2;subset++)
            {
                int first=Read(data,offset,ref bit,1),second=mode==1?first:Read(data,offset,ref bit,1);
                for(int c=0;c<channels;c++)
                {
                    endpoint[subset*2,c]=(endpoint[subset*2,c]<<1)|first;
                    endpoint[subset*2+1,c]=(endpoint[subset*2+1,c]<<1)|second;
                }
            }
            for(int c=0;c<4;c++)for(int e=0;e<4;e++)
            {
                if(c>=channels){endpoint[e,c]=255;continue;}
                int expanded=endpoint[e,c]<<(7-precision);endpoint[e,c]=expanded|(expanded>>(precision+1));
            }
            var result=new Color32[16];int bits=mode==1?3:2;
            for(int i=0;i<16;i++)
            {
                int index=Read(data,offset,ref bit,bits-((i==0||i==Bc7Partitions.Anchors[partition])?1:0));
                int weight=bits==3?W3[index]:W2[index],first=((Bc7Partitions.Masks[partition]>>i)&1)*2;
                var color=new Color32();for(int c=0;c<4;c++)color[c]=(byte)((endpoint[first,c]*(64-weight)+endpoint[first+1,c]*weight+32)>>6);
                result[i]=color;
            }
            if(bit!=128)throw new InvalidOperationException("BC7 partition field length differs.");
            return result;
        }
    }
}
