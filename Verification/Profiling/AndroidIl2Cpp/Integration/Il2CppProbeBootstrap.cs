using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

// Diagnostic integration only. No edits to game or package classes.
public sealed class Il2CppProbeBootstrap : MonoBehaviour
{
    [DllImport("Il2CppAllocationProbe")] static extern int AllocationProbe_Start();
    [DllImport("Il2CppAllocationProbe")] static extern void AllocationProbe_Poll();
    [DllImport("Il2CppAllocationProbe")] static extern int AllocationProbe_Save(string path,int stop);
    public static string Output { get; private set; }
    public static int InstallResult { get; private set; }
    public static bool Stopped { get; private set; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Install()
    {
        Output=Path.Combine(Application.persistentDataPath,"il2cpp-profile");Directory.CreateDirectory(Output);
        InstallResult=AllocationProbe_Start();Debug.Log("IL2CPP_PROFILE_START "+InstallResult);
        var go=new GameObject("External allocation collector");DontDestroyOnLoad(go);go.AddComponent<Il2CppProbeBootstrap>();
    }
    void LateUpdate(){if(InstallResult==1&&!Stopped)AllocationProbe_Poll();}
    public static void Save(string name,bool stop=false)
    {
        if(InstallResult!=1)throw new InvalidOperationException("Native collector install failed: "+InstallResult);
        int result=AllocationProbe_Save(Path.Combine(Output,name+".json"),stop?1:0);
        if(result!=0)throw new InvalidOperationException("Native collector save failed: "+result);
        if(stop)Stopped=true;
    }
    void OnApplicationQuit(){if(InstallResult==1&&!Stopped)Save("profile",true);}
}
