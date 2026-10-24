// Standalone diagnostic DLL. Does not modify or inject IL into the game.
#define NOMINMAX
#include <windows.h>
#include <cor.h>
#include <corprof.h>
#include <atomic>
#include <mutex>
#include <unordered_map>
#include <vector>
#include <algorithm>
#include <string>
#include <fstream>
#include <sstream>
#include <chrono>
#include <cstring>

static const GUID ProfilerClsid={0xb52f6d9c,0x5eaf,0x4730,{0x9a,0xe5,0x1e,0x48,0x1b,0xa4,0x67,0x12}};
static const GUID MetadataImportIid={0x7dac8207,0xd3ae,0x4c75,{0x9b,0x67,0x92,0x80,0x1a,0x49,0x7d,0x44}};
static std::string Utf8(const std::wstring& value) {
    if(value.empty()) return {};
    int n=WideCharToMultiByte(CP_UTF8,0,value.data(),(int)value.size(),nullptr,0,nullptr,nullptr);
    std::string result(n,0); WideCharToMultiByte(CP_UTF8,0,value.data(),(int)value.size(),result.data(),n,nullptr,nullptr); return result;
}
static std::string Quote(const std::string& text) {
    std::string s="\""; for(unsigned char c:text) { if(c=='"'||c=='\\') {s+='\\';s+=c;} else if(c<32) s+=' '; else s+=c; } return s+'"';
}
struct TypeStat {std::string name; uint64_t allocated=0,collected=0,bytes=0,collectedBytes=0; size_t slot=0;};
struct ObjectRecord {ClassID type; ULONG size;};
struct Range {UINT_PTR oldStart,newStart,length;};
struct GcStat {uint64_t number=0,collected=0,collectedBytes=0,survived=0,unknownGeneration=0; bool generations[3]{};};
// Unity can exit without calling the CLR Shutdown callback. A file-backed view keeps
// the final counters outside the CLR lifecycle. Read it only after process exit.
struct SnapshotHeader {
    uint64_t magic,version,pid,sequence,typeCount,gcCount,inGc,collisions,boundsFailures,nestedGc,capacityErrors,nameErrors;
};
struct SnapshotType {uint64_t classId,allocated,collected,bytes,collectedBytes;char name[4096];};
static_assert(sizeof(SnapshotHeader)==96);
static_assert(sizeof(SnapshotType)==4136);
constexpr size_t SnapshotCapacity=4096;

class AllocationProfiler final: public ICorProfilerCallback2 {
    std::atomic<ULONG> references{1}; ICorProfilerInfo2* info=nullptr;
    std::mutex gate; std::unordered_map<ClassID,TypeStat> types;
    std::unordered_map<ObjectID,ObjectRecord> live,beforeGc;
    std::vector<COR_PRF_GC_GENERATION_RANGE> bounds;
    std::vector<Range> ranges; std::vector<GcStat> gcs; GcStat current;
    std::wstring output; uint64_t collisions=0,boundsFailures=0,nestedGc=0; bool inGc=false;
    HANDLE snapshotFile=INVALID_HANDLE_VALUE,snapshotMapping=nullptr;
    SnapshotHeader* snapshot=nullptr;
    SnapshotType* snapshotTypes=nullptr;
    bool OpenSnapshot() {
        std::wstring file=output+L".snapshot.bin";
        snapshotFile=CreateFileW(file.c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(snapshotFile==INVALID_HANDLE_VALUE)return false;
        size_t size=sizeof(SnapshotHeader)+sizeof(SnapshotType)*SnapshotCapacity;
        snapshotMapping=CreateFileMappingW(snapshotFile,nullptr,PAGE_READWRITE,0,(DWORD)size,nullptr);
        if(!snapshotMapping)return false;
        snapshot=(SnapshotHeader*)MapViewOfFile(snapshotMapping,FILE_MAP_WRITE,0,0,size);
        if(!snapshot)return false;
        snapshotTypes=(SnapshotType*)(snapshot+1);
        snapshot->magic=0x41544C4153434C52ULL;snapshot->version=1;snapshot->pid=GetCurrentProcessId();return true;
    }
    void BeginSnapshotUpdate(){snapshot->sequence++;MemoryBarrier();}
    void EndSnapshotUpdate(){
        snapshot->typeCount=types.size();snapshot->gcCount=gcs.size();snapshot->inGc=inGc;
        snapshot->collisions=collisions;snapshot->boundsFailures=boundsFailures;snapshot->nestedGc=nestedGc;
        MemoryBarrier();snapshot->sequence++;
    }
    void WriteSnapshotType(ClassID id,TypeStat& value,bool name=false){
        if(value.slot>=SnapshotCapacity){snapshot->capacityErrors++;return;}
        auto& target=snapshotTypes[value.slot];target.classId=id;target.allocated=value.allocated;target.collected=value.collected;target.bytes=value.bytes;target.collectedBytes=value.collectedBytes;
        if(name){if(value.name.size()>=sizeof(target.name))snapshot->nameErrors++;size_t length=std::min(value.name.size(),sizeof(target.name)-1);memcpy(target.name,value.name.data(),length);target.name[length]=0;}
    }
    std::string MetadataName(IMetaDataImport* metadata,mdTypeDef token,int depth=0) {
        if(depth>12)return "...";
        WCHAR name[2048]{};ULONG length=0;DWORD flags=0;mdToken extends=0;
        if(FAILED(metadata->GetTypeDefProps(token,name,2048,&length,&flags,&extends)))return "Token:"+std::to_string(token);
        mdTypeDef enclosing=0;std::string result=Utf8(name);
        if(SUCCEEDED(metadata->GetNestedClassProps(token,&enclosing))&&enclosing)result=MetadataName(metadata,enclosing,depth+1)+"+"+result;
        return result;
    }
    std::string TypeName(ClassID id,int depth=0) {
        if(depth>5) return "...";
        auto cached=types.find(id); if(cached!=types.end()) return cached->second.name;
        CorElementType element; ClassID base=0; ULONG rank=0;
        if(info->IsArrayClass(id,&element,&base,&rank)==S_OK) {
            std::string name;
            if(base) name=TypeName(base,depth+1);
            else switch(element) {
                case ELEMENT_TYPE_U1:name="System.Byte";break; case ELEMENT_TYPE_I1:name="System.SByte";break;
                case ELEMENT_TYPE_I4:name="System.Int32";break; case ELEMENT_TYPE_U4:name="System.UInt32";break;
                case ELEMENT_TYPE_I8:name="System.Int64";break; case ELEMENT_TYPE_U8:name="System.UInt64";break;
                case ELEMENT_TYPE_R4:name="System.Single";break; case ELEMENT_TYPE_R8:name="System.Double";break;
                case ELEMENT_TYPE_CHAR:name="System.Char";break;case ELEMENT_TYPE_BOOLEAN:name="System.Boolean";break;
                default:name="ElementType"+std::to_string(element);break;
            }
            return name+"["+std::string(rank>0?rank-1:0,',')+"]";
        }
        ModuleID module=0; mdTypeDef token=0; ClassID parent=0; ULONG32 count=0; ClassID args[32]{};
        if(SUCCEEDED(info->GetClassIDInfo2(id,&module,&token,&parent,32,&count,args))) {
            IMetaDataImport* metadata=nullptr;
            if(SUCCEEDED(info->GetModuleMetaData(module,ofRead,MetadataImportIid,(IUnknown**)&metadata))) {
                std::string text=MetadataName(metadata,token);metadata->Release();
                if(count&&count<=32){text+='<'; for(ULONG i=0;i<count;i++){if(i)text+=',';text+=TypeName(args[i],depth+1);}text+='>';} return text;
            }
        }
        return "ClassID:"+std::to_string(id);
    }
    void Save(bool shutdown) {
        std::ofstream f(output,std::ios::trunc);
        f<<"{\"source\":\"ICorProfilerCallback2 full allocation and GC survival callbacks\",\"shutdown\":"<<(shutdown?"true":"false")
         <<",\"pid\":"<<GetCurrentProcessId()<<",\"collisions\":"<<collisions<<",\"boundsFailures\":"<<boundsFailures<<",\"nestedGc\":"<<nestedGc<<",\"types\":[";
        bool first=true; std::vector<const TypeStat*> sorted; for(auto& pair:types) sorted.push_back(&pair.second);
        std::sort(sorted.begin(),sorted.end(),[](auto a,auto b){return a->allocated>b->allocated;});
        for(auto s:sorted) {if(!first)f<<',';first=false;f<<"{\"type\":"<<Quote(s->name)<<",\"allocated\":"<<s->allocated<<",\"collected\":"<<s->collected<<",\"remainingTracked\":"<<(s->allocated-s->collected)<<",\"allocatedBytes\":"<<s->bytes<<",\"collectedBytes\":"<<s->collectedBytes<<'}';}
        f<<"],\"gc\":[";first=true;for(auto& g:gcs){if(!first)f<<',';first=false;f<<"{\"number\":"<<g.number<<",\"generations\":["<<g.generations[0]<<','<<g.generations[1]<<','<<g.generations[2]<<"],\"collected\":"<<g.collected<<",\"collectedBytes\":"<<g.collectedBytes<<",\"survived\":"<<g.survived<<",\"unknownGeneration\":"<<g.unknownGeneration<<'}';}f<<"]}\n";
    }
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** target) override {
        if(!target)return E_POINTER;*target=nullptr;
        if(iid==__uuidof(IUnknown)||iid==__uuidof(ICorProfilerCallback)||iid==__uuidof(ICorProfilerCallback2)){*target=static_cast<ICorProfilerCallback2*>(this);AddRef();return S_OK;}return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++references;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG r=--references;if(!r)delete this;return r;}
    HRESULT STDMETHODCALLTYPE Initialize(IUnknown* runtime) override {
        WCHAR path[32768]{};if(!GetEnvironmentVariableW(L"ATLAS_CLR_PROFILE_OUTPUT",path,32768))return E_FAIL;output=path;
        if(!OpenSnapshot())return E_FAIL;
        HRESULT hr=runtime->QueryInterface(__uuidof(ICorProfilerInfo2),(void**)&info);if(FAILED(hr))return hr;
        hr=info->SetEventMask(COR_PRF_MONITOR_OBJECT_ALLOCATED|COR_PRF_ENABLE_OBJECT_ALLOCATED|COR_PRF_MONITOR_GC);
        if(FAILED(hr))return hr;Save(false);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Shutdown() override {
        std::lock_guard<std::mutex> lock(gate);Save(true);if(info){info->Release();info=nullptr;}return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ObjectAllocated(ObjectID objectId,ClassID classId) override {
        std::lock_guard<std::mutex> lock(gate);
        BeginSnapshotUpdate();
        if(!types.count(classId)){auto name=TypeName(classId);TypeStat value{name};value.slot=types.size();types.emplace(classId,value);WriteSnapshotType(classId,types[classId],true);}
        ULONG size=0;info->GetObjectSize(objectId,&size);auto& stat=types[classId];stat.allocated++;stat.bytes+=size;
        if(!live.emplace(objectId,ObjectRecord{classId,size}).second)collisions++;
        WriteSnapshotType(classId,stat);EndSnapshotUpdate();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GarbageCollectionStarted(int cGenerations,BOOL generationCollected[],COR_PRF_GC_REASON) override {
        std::lock_guard<std::mutex> lock(gate);BeginSnapshotUpdate();if(inGc){nestedGc++;EndSnapshotUpdate();return S_OK;}inGc=true;current=GcStat{};current.number=gcs.size()+1;
        for(int i=0;i<3&&i<cGenerations;i++)current.generations[i]=generationCollected[i]!=0;
        ULONG count=0;HRESULT hr=info->GetGenerationBounds(0,&count,nullptr);bounds.resize(count);
        if(FAILED(hr)||FAILED(info->GetGenerationBounds(count,&count,bounds.data()))){boundsFailures++;bounds.clear();}
        else bounds.resize(count);beforeGc.swap(live);live.clear();ranges.clear();EndSnapshotUpdate();return S_OK;
    }
    HRESULT STDMETHODCALLTYPE MovedReferences(ULONG count,ObjectID oldStart[],ObjectID newStart[],ULONG length[]) override {
        std::lock_guard<std::mutex> lock(gate);for(ULONG i=0;i<count;i++)ranges.push_back({oldStart[i],newStart[i],length[i]});return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SurvivingReferences(ULONG count,ObjectID start[],ULONG length[]) override {
        std::lock_guard<std::mutex> lock(gate);for(ULONG i=0;i<count;i++)ranges.push_back({start[i],start[i],length[i]});return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GarbageCollectionFinished() override {
        std::lock_guard<std::mutex> lock(gate);
        BeginSnapshotUpdate();
        std::sort(ranges.begin(),ranges.end(),[](const Range&a,const Range&b){return a.oldStart<b.oldStart;});
        for(auto& pair:beforeGc){ObjectID id=pair.first,newId=id;bool survived=false;
            auto r=std::upper_bound(ranges.begin(),ranges.end(),id,[](UINT_PTR a,const Range&b){return a<b.oldStart;});
            if(r!=ranges.begin()){--r;if(id-r->oldStart<r->length){survived=true;newId=r->newStart+(id-r->oldStart);}}
            int gen=-1;for(const auto& b:bounds)if(id>=b.rangeStart&&id-b.rangeStart<b.rangeLength){gen=(int)b.generation;break;}
            bool collectedGeneration=gen>=0&&(gen>=2?current.generations[2]:current.generations[gen]);
            if(gen<0)current.unknownGeneration++;
            if(survived||!collectedGeneration){if(!live.emplace(newId,pair.second).second)collisions++;current.survived++;}
            else{auto& stat=types[pair.second.type];stat.collected++;stat.collectedBytes+=pair.second.size;current.collected++;current.collectedBytes+=pair.second.size;}
        }
        beforeGc.clear();inGc=false;gcs.push_back(current);Save(false);
        for(auto& pair:types)WriteSnapshotType(pair.first,pair.second);
        EndSnapshotUpdate();return S_OK;
    }
#include "CallbackStubs.h"
};
class Factory final: public IClassFactory {
    std::atomic<ULONG> refs{1};
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** p) override{if(!p)return E_POINTER;*p=nullptr;if(iid==__uuidof(IUnknown)||iid==__uuidof(IClassFactory)){*p=this;AddRef();return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;} ULONG STDMETHODCALLTYPE Release() override{auto r=--refs;if(!r)delete this;return r;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** p) override{if(outer)return CLASS_E_NOAGGREGATION;auto instance=new AllocationProfiler();auto hr=instance->QueryInterface(iid,p);instance->Release();return hr;}
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override{return S_OK;}
};
STDAPI DllGetClassObject(REFCLSID clsid,REFIID iid,void** p){if(clsid!=ProfilerClsid)return CLASS_E_CLASSNOTAVAILABLE;auto f=new Factory();auto hr=f->QueryInterface(iid,p);f->Release();return hr;}
STDAPI DllCanUnloadNow(){return S_FALSE;}
