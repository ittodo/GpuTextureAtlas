// Windows x64, Unity 6 D3D12 v8. No pixel mapping or CPU readback.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi.h>
#include <stddef.h>
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"

struct Bc7UploadPacket
{
    ID3D12Resource* buffer;
    ID3D12Resource* texture;
    int width, height, rowBlocks;
    volatile LONG status, recordedEvents;
    LONG generation;
};
static_assert(sizeof(Bc7UploadPacket) == 40, "Managed/native ABI differs");
static_assert(offsetof(Bc7UploadPacket, status) == 28, "Status offset differs");
static IUnityGraphics* graphics;
static IUnityGraphicsD3D12v8* d3d;
static volatile LONG generation;
static int eventId;

static void UNITY_INTERFACE_API DeviceEvent(UnityGfxDeviceEventType event)
{
    if (event == kUnityGfxDeviceEventInitialize || event == kUnityGfxDeviceEventShutdown ||
        event == kUnityGfxDeviceEventBeforeReset || event == kUnityGfxDeviceEventAfterReset)
        InterlockedIncrement(&generation);
}
static void Status(Bc7UploadPacket* packet, LONG value) { InterlockedExchange(&packet->status, value); }
static void UNITY_INTERFACE_API Upload(int, void* data)
{
    auto* p = static_cast<Bc7UploadPacket*>(data);
    if (!p) return;
    if (!graphics || !d3d || graphics->GetRenderer() != kUnityGfxRendererD3D12) { Status(p, -1); return; }
    if (p->generation != InterlockedCompareExchange(&generation, 0, 0)) { Status(p, -6); return; }
    if (!p->buffer || !p->texture || p->width <= 0 || p->height <= 0 || (p->width & 3) ||
        (p->height & 3) || p->rowBlocks < p->width / 4 || (p->rowBlocks & 15)) { Status(p, -2); return; }
    const auto source = p->buffer->GetDesc();
    const auto target = p->texture->GetDesc();
    if (target.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D || target.Width != static_cast<UINT64>(p->width) ||
        target.Height != static_cast<UINT>(p->height) || target.DepthOrArraySize != 1 || target.MipLevels != 1 ||
        target.SampleDesc.Count != 1 || (target.Format != DXGI_FORMAT_BC7_TYPELESS &&
        target.Format != DXGI_FORMAT_BC7_UNORM && target.Format != DXGI_FORMAT_BC7_UNORM_SRGB)) { Status(p, -3); return; }
    const UINT64 bytes = static_cast<UINT64>(p->rowBlocks) * 16 * (p->height / 4);
    if (source.Dimension != D3D12_RESOURCE_DIMENSION_BUFFER || source.Width < bytes) { Status(p, -4); return; }

    // Unity tracks the transitions and inserts barriers after the compute write and before later sampling.
    d3d->RequestResourceState(p->buffer, D3D12_RESOURCE_STATE_COPY_SOURCE);
    d3d->RequestResourceState(p->texture, D3D12_RESOURCE_STATE_COPY_DEST);
    UnityGraphicsD3D12RecordingState recording = {};
    if (!d3d->CommandRecordingState(&recording) || !recording.commandList) { Status(p, -5); return; }
    D3D12_TEXTURE_COPY_LOCATION from = {};
    from.pResource = p->buffer;
    from.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    from.PlacedFootprint.Footprint.Format = DXGI_FORMAT_BC7_UNORM;
    from.PlacedFootprint.Footprint.Width = static_cast<UINT>(p->width);
    from.PlacedFootprint.Footprint.Height = static_cast<UINT>(p->height);
    from.PlacedFootprint.Footprint.Depth = 1;
    from.PlacedFootprint.Footprint.RowPitch = static_cast<UINT>(p->rowBlocks) * 16;
    D3D12_TEXTURE_COPY_LOCATION to = {};
    to.pResource = p->texture;
    to.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    recording.commandList->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
    d3d->RequestResourceState(p->buffer, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    d3d->RequestResourceState(p->texture, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    InterlockedIncrement(&p->recordedEvents);
    Status(p, 1); // Commands recorded, not GPU completion.
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    graphics = interfaces->Get<IUnityGraphics>();
    d3d = interfaces->Get<IUnityGraphicsD3D12v8>();
    if (graphics) graphics->RegisterDeviceEventCallback(DeviceEvent);
    if (graphics) eventId = graphics->ReserveEventIDRange(1);
    InterlockedIncrement(&generation);
    if (d3d)
    {
        UnityD3D12PluginEventConfig config = {};
        config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
        config.ensureActiveRenderTextureIsBound = false;
        d3d->ConfigureEvent(eventId, &config);
    }
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
{
    if (graphics) graphics->UnregisterDeviceEventCallback(DeviceEvent);
    graphics = nullptr; d3d = nullptr; InterlockedIncrement(&generation);
}
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuUploadInitialize()
{ return graphics && d3d && graphics->GetRenderer() == kUnityGfxRendererD3D12 ? 1 : 0; }
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuUploadAbiVersion() { return 1; }
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuUploadGeneration() { return InterlockedCompareExchange(&generation, 0, 0); }
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuUploadEventId() { return eventId; }
extern "C" UNITY_INTERFACE_EXPORT UnityRenderingEventAndData Bc7GpuUploadEvent() { return Upload; }
extern "C" UNITY_INTERFACE_EXPORT void Bc7GpuUploadRetainResources(void* data)
{
    auto* p = static_cast<Bc7UploadPacket*>(data);
    p->buffer->AddRef(); p->texture->AddRef();
}
extern "C" UNITY_INTERFACE_EXPORT void Bc7GpuUploadReleaseResources(void* data)
{
    auto* p = static_cast<Bc7UploadPacket*>(data);
    p->buffer->Release(); p->texture->Release();
}
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuUploadStatus(void* data)
{ return InterlockedCompareExchange(&static_cast<Bc7UploadPacket*>(data)->status, 0, 0); }
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuUploadRecordedEvents(void* data)
{ return InterlockedCompareExchange(&static_cast<Bc7UploadPacket*>(data)->recordedEvents, 0, 0); }
