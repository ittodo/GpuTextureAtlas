#include "Bc7Partitions.hlsl"

uint ReadBlock(uint4 block,uint bits,uint offset)
{
    uint word=offset>>5,shift=offset&31;
    uint low=word==0?block.x:word==1?block.y:word==2?block.z:block.w;
    uint high=word==0?block.y:word==1?block.z:word==2?block.w:0;
    uint value=low>>shift;
    if(shift+bits>32)value|=high<<(32-shift);
    return value&((1u<<bits)-1);
}
float BalancedError(uint2 origin,uint4 block)
{
    // The preceding Balanced dispatch writes a valid mode 4, 5 or 6 block.
    uint mode=(block.x&16u)!=0?4u:(block.x&32u)!=0?5u:6u;
    bool combined=mode==6;uint at=mode+1,rotation=0,selector=0;
    if(!combined){rotation=ReadBlock(block,2,at);at+=2;if(mode==4){selector=ReadBlock(block,1,at);at++;}}
    float4 lo=0,hi=0;
    [unroll]for(uint c=0;c<4;c++)
    {
        uint precision=combined?7:c==3?(mode==5?8:6):(mode==5?7:5);
        lo[c]=ReadBlock(block,precision,at);at+=precision;hi[c]=ReadBlock(block,precision,at);at+=precision;
    }
    if(combined){lo=lo*2+ReadBlock(block,1,at++);hi=hi*2+ReadBlock(block,1,at++);}
    else [unroll]for(uint c2=0;c2<4;c2++)
    {
        uint precision=c2==3?(mode==5?8:6):(mode==5?7:5);
        lo[c2]=Expand((uint)lo[c2],precision);hi[c2]=Expand((uint)hi[c2],precision);
    }
    uint colorBits=combined?4:mode==4&&selector==1?3:2;
    uint alphaBits=combined?4:mode==4&&selector==0?3:2;
    uint colorAt=mode==4&&selector==1?at+31:at;
    uint alphaAt=combined?colorAt:mode==5||selector==0?at+31:at;
    float error=0;
    [loop]for(uint i=0;i<16;i++)
    {
        uint ci=ReadBlock(block,colorBits-(i==0?1u:0u),colorAt+i*colorBits-(i>0?1u:0u));
        uint ai=combined?ci:ReadBlock(block,alphaBits-(i==0?1u:0u),alphaAt+i*alphaBits-(i>0?1u:0u));
        float4 decoded=Interpolate(lo,hi,ci,colorBits);if(!combined)decoded.a=Interpolate(lo,hi,ai,alphaBits).a;
        float4 d=Rotate(LoadColor(origin+uint2(i&3,i>>2)),rotation)-decoded;error+=dot(d,d);
    }
    return error;
}
uint ForcedP(float target,uint bits,uint p)
{
    uint maximum=(1u<<bits)-1;
    uint center=(uint)clamp(floor((target*((1u<<(bits+1))-1)/255.0-p)*.5+.5),0.0,(float)maximum);
    uint first=center>0?center-1:0,last=min(maximum,center+1),best=center;float error=1e30;
    for(uint q=first;q<=last;q++)
    {
        float difference=abs((float)Expand((q<<1)|p,bits+1)-target);
        if(difference<error){error=difference;best=q;}
    }
    return best;
}
void PartitionEndpoints(float4 targetLo,float4 targetHi,uint mode,bool opaque,
    out float4 lo,out float4 hi,out uint4 qlo,out uint4 qhi,out uint plo,out uint phi)
{
    uint bits=mode==1?6:mode==3?7:5,channels=mode==7?4:3;
    lo=255;hi=255;qlo=0;qhi=0;plo=0;phi=0;
    if(mode==1)
    {
        float best=1e30;
        [unroll]for(uint p=0;p<2;p++)
        {
            uint4 qa=0,qb=0;float4 a=255,b=255;float error=0;
            [unroll]for(uint c=0;c<3;c++)
            {
                qa[c]=ForcedP(targetLo[c],bits,p);qb[c]=ForcedP(targetHi[c],bits,p);
                a[c]=Expand((qa[c]<<1)|p,bits+1);b[c]=Expand((qb[c]<<1)|p,bits+1);
                error+=(a[c]-targetLo[c])*(a[c]-targetLo[c])+(b[c]-targetHi[c])*(b[c]-targetHi[c]);
            }
            if(error<best){best=error;lo=a;hi=b;qlo=qa;qhi=qb;plo=p;phi=p;}
        }
    }
    else
    {
        float bestLo=1e30,bestHi=1e30;
        [unroll]for(uint p=0;p<2;p++)
        {
            if(mode==7&&opaque&&p==0)continue;
            uint4 qa=0,qb=0;float4 a=255,b=255;float ea=0,eb=0;
            [unroll]for(uint c=0;c<4;c++)if(c<channels)
            {
                qa[c]=ForcedP(targetLo[c],bits,p);qb[c]=ForcedP(targetHi[c],bits,p);
                a[c]=Expand((qa[c]<<1)|p,bits+1);b[c]=Expand((qb[c]<<1)|p,bits+1);
                ea+=(a[c]-targetLo[c])*(a[c]-targetLo[c]);eb+=(b[c]-targetHi[c])*(b[c]-targetHi[c]);
            }
            if(ea<bestLo){bestLo=ea;lo=a;qlo=qa;plo=p;}
            if(eb<bestHi){bestHi=eb;hi=b;qhi=qb;phi=p;}
        }
    }
}
void PartitionInitial(float4 colors[16],uint mask,uint subset,bool rgba,
    out float4 lo,out float4 hi,out float4 sum,out float count)
{
    sum=0;count=0;float4 r=0,g=0,b=0,a=0;
    for(uint i=0;i<16;i++)if(((mask>>i)&1)==subset)
    {
        float4 c=colors[i];if(!rgba)c.a=0;
        sum+=c;count++;r+=c.r*c;g+=c.g*c;b+=c.b*c;a+=c.a*c;
    }
    float4 mean=sum/count;r-=mean.r*sum;g-=mean.g*sum;b-=mean.b*sum;a-=mean.a*sum;
    float4 variance=float4(r.r,g.g,b.b,rgba?a.a:-1);uint channel=0;
    for(uint c=1;c<4;c++)if(variance[c]>variance[channel])channel=c;
    float4 axis=channel==0?float4(1,0,0,0):channel==1?float4(0,1,0,0):channel==2?float4(0,0,1,0):float4(0,0,0,1);
    for(uint k=0;k<5;k++)
    {
        float4 next=float4(dot(r,axis),dot(g,axis),dot(b,axis),rgba?dot(a,axis):0);
        axis=next*rsqrt(max(dot(next,next),1e-20));
    }
    float low=1e30,high=-1e30;
    for(uint j=0;j<16;j++)if(((mask>>j)&1)==subset)
    {
        float4 c=colors[j];if(!rgba)c.a=0;float projection=dot(c-mean,axis);
        low=min(low,projection);high=max(high,projection);
    }
    lo=clamp(mean+axis*low,0,255);hi=clamp(mean+axis*high,0,255);
    if(!rgba){lo.a=255;hi.a=255;sum.a=255*count;}
}
void FixedPartitionEndpoints(float4 targetLo,float4 targetHi,float4 anchorColor,uint mode,bool opaque,uint bits,
    out float4 lo,out float4 hi,out uint4 qlo,out uint4 qhi,out uint plo,out uint phi)
{
    float4 a,b;uint4 qa,qb;uint pa,pb;
    PartitionEndpoints(targetLo,targetHi,mode,opaque,a,b,qa,qb,pa,pb);
    bool reverse=FindIndex(anchorColor,a,b,bits,2)>=(1u<<(bits-1));
    lo=reverse?b:a;hi=reverse?a:b;
    qlo=reverse?qb:qa;qhi=reverse?qa:qb;
    plo=reverse?pb:pa;phi=reverse?pa:pb;
}
void FitPartition(float4 sum,float count,float sw,float sww,float4 swc,float4 lo,float4 hi,
    out float4 targetLo,out float4 targetHi)
{
    float aa=count-2*sw+sww,ab=sw-sww,det=aa*sww-ab*ab;
    targetLo=lo;targetHi=hi;
    if(det>1e-6){targetLo=clamp(((sum-swc)*sww-swc*ab)/det,0,255);targetHi=clamp((swc*aa-(sum-swc)*ab)/det,0,255);}
}
void PartitionCandidate(float4 colors[16],uint partition,uint mode,bool opaque,inout float bestError,inout uint4 bestBlock)
{
    uint mask=Bc7Partitions[partition],anchor=Bc7Anchors[partition];
    uint indexBits=mode==1?3:2,precision=mode==1?6:mode==3?7:5,channels=mode==7?4:3;
    float4 targetLo0,targetHi0,sum0,targetLo1,targetHi1,sum1;float count0,count1;
    PartitionInitial(colors,mask,0,mode==7,targetLo0,targetHi0,sum0,count0);
    PartitionInitial(colors,mask,1,mode==7,targetLo1,targetHi1,sum1,count1);
    [loop]for(uint iteration=0;iteration<5;iteration++)
    {
        float4 lo0,hi0,lo1,hi1;uint4 qlo0,qhi0,qlo1,qhi1;uint plo0,phi0,plo1,phi1;
        FixedPartitionEndpoints(targetLo0,targetHi0,colors[0],mode,opaque,indexBits,lo0,hi0,qlo0,qhi0,plo0,phi0);
        FixedPartitionEndpoints(targetLo1,targetHi1,colors[anchor],mode,opaque,indexBits,lo1,hi1,qlo1,qhi1,plo1,phi1);
        uint4 block=0;Put(block,1u<<mode,mode+1,0);Put(block,partition,6,mode+1);uint at=mode+7;
        [loop]for(uint c=0;c<channels;c++)
        {
            Put(block,qlo0[c],precision,at);at+=precision;Put(block,qhi0[c],precision,at);at+=precision;
            Put(block,qlo1[c],precision,at);at+=precision;Put(block,qhi1[c],precision,at);at+=precision;
        }
        Put(block,plo0,1,at++);if(mode!=1)Put(block,phi0,1,at++);
        Put(block,plo1,1,at++);if(mode!=1)Put(block,phi1,1,at++);
        float error=0;float2 sw=0,sww=0;float4 swc0=0,swc1=0;
        [loop]for(uint i=0;i<16;i++)
        {
            uint subset=(mask>>i)&1;float4 lo=subset==0?lo0:lo1,hi=subset==0?hi0:hi1;
            uint index=FindIndex(colors[i],lo,hi,indexBits,2);
            float4 decoded=Interpolate(lo,hi,index,indexBits),d=colors[i]-decoded;error+=dot(d,d);
            uint length=indexBits-((i==0||i==anchor)?1u:0u);Put(block,index,length,at);at+=length;
            float w=Weight(index,indexBits)/64.0;
            if(subset==0){sw.x+=w;sww.x+=w*w;swc0+=colors[i]*w;}
            else{sw.y+=w;sww.y+=w*w;swc1+=colors[i]*w;}
        }
        if(error<bestError){bestError=error;bestBlock=block;}
        FitPartition(sum0,count0,sw.x,sww.x,swc0,lo0,hi0,targetLo0,targetHi0);
        FitPartition(sum1,count1,sw.y,sww.y,swc1,lo1,hi1,targetLo1,targetHi1);
    }
}
void SearchPartitions(uint2 origin,bool opaque,inout float bestError,inout uint4 bestBlock)
{
    if(bestError<.001)return;
    float4 colors[16];for(uint i=0;i<16;i++)colors[i]=LoadColor(origin+uint2(i&3,i>>2));
    float4 scores=1e30;uint4 ids=0;
    [loop]for(uint partition=0;partition<64;partition++)
    {
        uint mask=Bc7Partitions[partition];float4 sum0=0,sum1=0;float n0=0,n1=0,squared=0;
        for(uint j=0;j<16;j++)
        {
            float4 c=colors[j];if(opaque)c.a=0;squared+=dot(c,c);
            if(((mask>>j)&1)==0){sum0+=c;n0++;}else{sum1+=c;n1++;}
        }
        float error=max(0,squared-dot(sum0,sum0)/n0-dot(sum1,sum1)/n1);
        if(error<scores.x){scores=float4(error,scores.xyz);ids=uint4(partition,ids.xyz);}
        else if(error<scores.y){scores=float4(scores.x,error,scores.yz);ids=uint4(ids.x,partition,ids.yz);}
        else if(error<scores.z){scores=float4(scores.xy,error,scores.z);ids=uint4(ids.xy,partition,ids.z);}
        else if(error<scores.w){scores.w=error;ids.w=partition;}
    }
    [loop]for(uint candidate=0;candidate<4;candidate++)
    {
        if(opaque){PartitionCandidate(colors,ids[candidate],1,true,bestError,bestBlock);PartitionCandidate(colors,ids[candidate],3,true,bestError,bestBlock);}
        else PartitionCandidate(colors,ids[candidate],7,false,bestError,bestBlock);
    }
}




