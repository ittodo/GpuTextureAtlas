// Diagnostic plugin. Does not patch IL2CPP or rewrite game methods.
#include "il2cpp-api-types.h"
#include <dlfcn.h>
#include <android/log.h>
#include <atomic>
#include <mutex>
#include <string>
#include <vector>
#include <unordered_map>
#include <fstream>
#include <algorithm>

#define API(ret,name,args) static ret (*p_##name) args
API(void,profiler_install,(Il2CppProfiler*,Il2CppProfileFunc));
API(void,profiler_set_events,(Il2CppProfileFlags));
API(void,profiler_install_allocation,(Il2CppProfileAllocFunc));
API(void,profiler_install_gc,(Il2CppProfileGCFunc,Il2CppProfileGCResizeFunc));
API(Il2CppGCHandle,gchandle_new_weakref,(Il2CppObject*,bool));
API(Il2CppObject*,gchandle_get_target,(Il2CppGCHandle));
API(void,gchandle_free,(Il2CppGCHandle));
API(uint32_t,object_get_size,(Il2CppObject*));
API(const Il2CppType*,class_get_type,(Il2CppClass*));
API(char*,type_get_name,(const Il2CppType*));
API(void,free,(void*));
API(const MethodInfo*,class_get_method_from_name,(Il2CppClass*,const char*,int));
API(Il2CppClass*,method_get_declaring_type,(const MethodInfo*));
API(const char*,class_get_name,(Il2CppClass*));
API(const char*,class_get_namespace,(Il2CppClass*));
#undef API

struct Type {std::string name;bool hasFinalizer=false;uint64_t allocated=0,cleared=0,bytes=0,clearedBytes=0;};
struct Tracked {Il2CppGCHandle weak;size_t type;uint32_t size;};
static std::mutex gate;
static std::vector<Type> types;
static std::unordered_map<Il2CppClass*,size_t> typeIds;
static std::vector<Tracked> tracked;
static std::atomic<bool> active{false};
static std::atomic<uint64_t> gcReclaims{0},fullGcEnds{0},reentrantSkipped{0};
static uint64_t scannedGc=0,polls=0,handleFailures=0,capacityDropped=0;
static bool installed=false;
static thread_local bool inside=false;
static constexpr size_t MaxTracked=2000000;
static std::string Quote(const std::string& s){std::string r="\"";for(unsigned char c:s){if(c=='"'||c=='\\')r+='\\';if(c<32)r+=' ';else r+=c;}return r+'"';}
static void GcEvent(Il2CppProfiler*,Il2CppGCEvent event,int){
    // BDWGC's incremental completion does not always emit GC_EVENT_END.
    if(event==IL2CPP_GC_EVENT_RECLAIM_END)gcReclaims.fetch_add(1,std::memory_order_relaxed);
    if(event==IL2CPP_GC_EVENT_END)fullGcEnds.fetch_add(1,std::memory_order_relaxed);
}
// Unity 6000.7.0b5 checks gcEventCallback before invoking this callback too.
// It must be non-null when GC event monitoring is enabled.
static void HeapResize(Il2CppProfiler*,int64_t){}
static void Shutdown(Il2CppProfiler*){active=false;}
static void Allocation(Il2CppProfiler*,Il2CppObject* object,Il2CppClass* klass){
    if(!active.load(std::memory_order_relaxed))return;
    if(inside){reentrantSkipped++;return;}inside=true;
    std::lock_guard<std::mutex> lock(gate);
    if(!active){inside=false;return;}
    auto found=typeIds.find(klass);size_t index;
    if(found==typeIds.end()){
        index=types.size();Type value;
        char* name=p_type_get_name(p_class_get_type(klass));value.name=name?name:"<unknown>";if(name)p_free(name);
        auto method=p_class_get_method_from_name(klass,"Finalize",0);
        if(method){auto owner=p_method_get_declaring_type(method);value.hasFinalizer=std::string(p_class_get_name(owner))!="Object"||std::string(p_class_get_namespace(owner))!="System";}
        types.push_back(std::move(value));typeIds.emplace(klass,index);
    }else index=found->second;
    uint32_t size=p_object_get_size(object);types[index].allocated++;types[index].bytes+=size;
    if(tracked.size()>=MaxTracked)capacityDropped++;
    else{
        auto weak=p_gchandle_new_weakref(object,false);
        if(!weak)handleFailures++;else tracked.push_back({weak,index,size});
    }
    inside=false;
}
static void PollLocked(){
    // Never query handles inside a GC event callback: the collector holds locks there.
    const uint64_t observed=gcReclaims.load();
    size_t write=0;
    for(size_t i=0;i<tracked.size();i++){
        auto item=tracked[i];
        if(p_gchandle_get_target(item.weak)==nullptr){types[item.type].cleared++;types[item.type].clearedBytes+=item.size;p_gchandle_free(item.weak);}
        else tracked[write++]=item;
    }
    // A collection during this scan must cause another scan on the next frame.
    tracked.resize(write);scannedGc=observed;polls++;
}
#define EXPORTED extern "C" __attribute__((visibility("default")))
EXPORTED int AllocationProbe_Start(){
    std::lock_guard<std::mutex> lock(gate);if(installed)return 1;
    void* library=dlopen("libil2cpp.so",RTLD_NOW);if(!library)return -1;
#define LOAD(name) p_##name=reinterpret_cast<decltype(p_##name)>(dlsym(library,"il2cpp_" #name));if(!p_##name){__android_log_print(ANDROID_LOG_ERROR,"AllocationProbe","Missing il2cpp_%s",#name);return -2;}
    LOAD(profiler_install) LOAD(profiler_set_events) LOAD(profiler_install_allocation) LOAD(profiler_install_gc)
    LOAD(gchandle_new_weakref) LOAD(gchandle_get_target) LOAD(gchandle_free) LOAD(object_get_size)
    LOAD(class_get_type) LOAD(type_get_name) LOAD(free) LOAD(class_get_method_from_name)
    LOAD(method_get_declaring_type) LOAD(class_get_name) LOAD(class_get_namespace)
#undef LOAD
    tracked.reserve(32768);types.reserve(2048);
    p_profiler_install(nullptr,Shutdown);p_profiler_install_allocation(Allocation);p_profiler_install_gc(GcEvent,HeapResize);
    p_profiler_set_events((Il2CppProfileFlags)(IL2CPP_PROFILE_ALLOCATIONS|IL2CPP_PROFILE_GC));
    installed=true;active=true;__android_log_print(ANDROID_LOG_INFO,"AllocationProbe","IL2CPP callbacks installed");return 1;
}
EXPORTED void AllocationProbe_Poll(){if(!active||scannedGc==gcReclaims.load())return;std::lock_guard<std::mutex> lock(gate);PollLocked();}
EXPORTED int AllocationProbe_Save(const char* path,int stop){
    if(!installed)return -1;if(stop)active=false;
    std::lock_guard<std::mutex> lock(gate);PollLocked();
    std::ofstream f(path);if(!f)return -2;
    f<<"{\"method\":\"IL2CPP allocation callbacks and short weak handles\",\"physicalFreeCountAvailable\":false,\"stopped\":"<<(stop?"true":"false")
     <<",\"gcReclaimPhases\":"<<gcReclaims.load()<<",\"fullGcEndEvents\":"<<fullGcEnds.load()<<",\"polls\":"<<polls<<",\"trackedHandles\":"<<tracked.size()<<",\"handleFailures\":"<<handleFailures
     <<",\"capacityDropped\":"<<capacityDropped<<",\"reentrantSkipped\":"<<reentrantSkipped.load()<<",\"types\":[";
    bool first=true;for(auto& t:types){if(!first)f<<',';first=false;f<<"{\"type\":"<<Quote(t.name)<<",\"hasFinalizer\":"<<(t.hasFinalizer?"true":"false")<<",\"allocated\":"<<t.allocated<<",\"weakCleared\":"<<t.cleared<<",\"remainingTracked\":"<<t.allocated-t.cleared<<",\"allocatedBytes\":"<<t.bytes<<",\"weakClearedBytes\":"<<t.clearedBytes<<'}';}f<<"]}\n";
    bool success=f.good();f.close();
    if(stop){for(auto& item:tracked)p_gchandle_free(item.weak);tracked.clear();}
    return success?0:-3;
}
