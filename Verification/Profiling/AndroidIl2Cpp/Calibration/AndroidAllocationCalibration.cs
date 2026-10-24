using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Scripting;
using GpuTextureAtlasSamples;
using UnityCompute.TextureCompression;

public sealed class CalTransient { public int Value; }
public sealed class CalRetained { public int Value; }
public sealed class CalFinalizable { public static int Finalized; ~CalFinalizable(){Interlocked.Increment(ref Finalized);} }
public sealed class CalResurrecting { public static CalResurrecting Resurrected; public static int Finalized; ~CalResurrecting(){Resurrected=this;Interlocked.Increment(ref Finalized);} }

// Known-count workload. This is a separate verification fixture, not game instrumentation.
public sealed class AndroidAllocationCalibration : MonoBehaviour
{
    public ComputeShader encoder,conversion;
    static CalTransient[] temporary;static CalRetained[] retained;
    static CalFinalizable[] finalizable;static CalResurrecting resurrection;
    Exception workerError;DynamicAtlasDemo sample;
    [Serializable] sealed class Result {public bool passed,incremental,resurrectionConfirmed;public int transientExpected=10000,retainedExpected=2000,finalizableExpected=100,finalized,resurrectedFinalized,sampleViews,gcBefore,gcAfter;public string unity,device,gpu,error;}
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AllocateKnown()
    {
        temporary=new CalTransient[10000];for(int i=0;i<temporary.Length;i++)temporary[i]=new CalTransient{Value=i};
        retained=new CalRetained[2000];for(int i=0;i<retained.Length;i++)retained[i]=new CalRetained{Value=i};
        finalizable=new CalFinalizable[100];for(int i=0;i<finalizable.Length;i++)finalizable[i]=new CalFinalizable();
        resurrection=new CalResurrecting();
    }
    IEnumerator Start()
    {
        Application.targetFrameRate=60;Application.runInBackground=true;Screen.sleepTimeout=SleepTimeout.NeverSleep;
        var report=new Result{unity=Application.unityVersion,device=SystemInfo.deviceModel,gpu=SystemInfo.graphicsDeviceName,incremental=GarbageCollector.isIncremental,gcBefore=GC.CollectionCount(0)};
        var execution=Run(report);
        while(true){bool next=false;object item=null;try{next=execution.MoveNext();if(next)item=execution.Current;}catch(Exception error){report.error=error.ToString();Debug.LogException(error);break;}if(!next){report.passed=true;break;}yield return item;}
        report.gcAfter=GC.CollectionCount(0);report.finalized=CalFinalizable.Finalized;report.resurrectedFinalized=CalResurrecting.Finalized;report.resurrectionConfirmed=CalResurrecting.Resurrected!=null;
        File.WriteAllText(Path.Combine(Il2CppProbeBootstrap.Output,"result.json"),JsonUtility.ToJson(report,true));
        Il2CppProbeBootstrap.Save("profile",true);Debug.Log("IL2CPP_PROFILE_COMPLETE "+JsonUtility.ToJson(report));
        Screen.sleepTimeout=SleepTimeout.SystemSetting;yield return new WaitForSeconds(.3f);Application.Quit(report.passed?0:1);
    }
    IEnumerator Run(Result report)
    {
        if(Il2CppProbeBootstrap.InstallResult!=1)throw new Exception("Collector installation failed.");
        var worker=new Thread(()=>{try{AllocateKnown();}catch(Exception e){workerError=e;}});worker.Start();while(worker.IsAlive)yield return null;worker.Join();if(workerError!=null)throw workerError;
        Il2CppProbeBootstrap.Save("allocated");temporary=null;finalizable=null;resurrection=null;
        for(int i=0;i<6;i++){yield return null;GC.Collect();GC.WaitForPendingFinalizers();yield return null;}
        if(retained.Length!=2000||retained[1999].Value!=1999)throw new Exception("Retained fixture corrupted.");
        if(CalFinalizable.Finalized!=100||CalResurrecting.Finalized!=1||CalResurrecting.Resurrected==null)throw new Exception("Finalizer fixture did not complete.");
        Il2CppProbeBootstrap.Save("calibration");
        // Run the copied, unchanged package sample and let its ordinary destruction run.
        var go=new GameObject("Unmodified package sample");sample=go.AddComponent<DynamicAtlasDemo>();sample.astcEncoder=encoder;sample.conversion=conversion;sample.quality=GpuAtlasQuality.Fast;
        var field=typeof(DynamicAtlasDemo).GetField("atlas",BindingFlags.NonPublic|BindingFlags.Instance);
        for(int i=0;i<1800&&field.GetValue(sample)==null;i++)yield return null;
        if(field.GetValue(sample)==null)throw new Exception("Package sample initialization timed out.");
        yield return null;report.sampleViews=go.GetComponentsInChildren<GpuAtlasRenderer>().Length;if(report.sampleViews!=4)throw new Exception("Expected four sample renderers.");
        Il2CppProbeBootstrap.Save("sample-live");Destroy(go);sample=null;go=null;
        for(int i=0;i<12;i++)yield return null;
        yield return Resources.UnloadUnusedAssets();
        for(int i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();yield return null;}
        GC.KeepAlive(retained);GC.KeepAlive(CalResurrecting.Resurrected);
    }
}
