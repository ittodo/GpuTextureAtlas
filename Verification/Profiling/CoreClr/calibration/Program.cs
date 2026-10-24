using System.Runtime.CompilerServices;
sealed class ProfilerCalibrationTransient { public int Value; }
sealed class ProfilerCalibrationRetained { public int Value; }
sealed class ProfilerCalibrationYoung { public int Value; }
static class Program {
    static object? temporary;
    static ProfilerCalibrationRetained[]? retained;
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    static void Allocate() {
        var items = new ProfilerCalibrationTransient[10000];
        for(int i=0;i<items.Length;i++) items[i]=new ProfilerCalibrationTransient { Value=i };
        temporary=items;
        retained=new ProfilerCalibrationRetained[2000];
        for(int i=0;i<retained.Length;i++) retained[i]=new ProfilerCalibrationRetained { Value=i };
    }
    static void Main() {
        Allocate(); GC.Collect(2,GCCollectionMode.Forced,true,true);
        temporary=null; GC.Collect(2,GCCollectionMode.Forced,true,false);
        GC.WaitForPendingFinalizers(); GC.Collect(2,GCCollectionMode.Forced,true,true);
        AllocateYoung(); temporary=null; GC.Collect(0,GCCollectionMode.Forced,true,false);
        Console.WriteLine($"Expected transient allocated=10000 collected=10000; retained allocated=2000 collected=0. Retained value={retained![1999].Value}");
        GC.KeepAlive(retained);
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    static void AllocateYoung() {
        var items=new ProfilerCalibrationYoung[500];
        for(int i=0;i<items.Length;i++)items[i]=new ProfilerCalibrationYoung { Value=i };
        temporary=items;
    }
}
