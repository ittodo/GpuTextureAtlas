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
struct Bc7CopyPacket
{
    ID3D12Resource* source; ID3D12Resource* buffer; ID3D12Resource* destination;
    int width, height, mip, blockSize, rowBlocks, x, y;
    volatile LONG status, events; LONG generation;
};
static_assert(sizeof(Bc7CopyPacket) == 64, "BC7 copy ABI differs");
static_assert(offsetof(Bc7CopyPacket, status) == 52, "BC7 copy status differs");
static bool IsBc7(DXGI_FORMAT format)
{ return format == DXGI_FORMAT_BC7_TYPELESS || format == DXGI_FORMAT_BC7_UNORM || format == DXGI_FORMAT_BC7_UNORM_SRGB; }
static void UNITY_INTERFACE_API CopyTextureBlocks(int, void* data)
{
    auto* p = static_cast<Bc7CopyPacket*>(data); if (!p) return;
    auto status = [p](LONG value) { InterlockedExchange(&p->status, value); };
    if (!graphics || !d3d || graphics->GetRenderer() != kUnityGfxRendererD3D12 ||
        p->generation != InterlockedCompareExchange(&generation, 0, 0)) { status(-1); return; }
    if (!p->source || !p->buffer || !p->destination || p->width < 1 || p->height < 1 || p->mip < 0 || p->mip > 30 ||
        p->blockSize != 4 || p->x < 0 || p->y < 0 || (p->x & 3) || (p->y & 3) || (p->rowBlocks & 15) ||
        p->rowBlocks < (p->width + 3) / 4) { status(-2); return; }
    const auto source = p->source->GetDesc(), target = p->destination->GetDesc(), temporary = p->buffer->GetDesc();
    UINT64 sourceWidth = source.Width >> p->mip; if (!sourceWidth) sourceWidth = 1;
    UINT sourceHeight = source.Height >> p->mip; if (!sourceHeight) sourceHeight = 1;
    const UINT width = (p->width + 3) / 4 * 4, height = (p->height + 3) / 4 * 4;
    if (source.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D || target.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
        !IsBc7(source.Format) || !IsBc7(target.Format) || p->mip >= source.MipLevels || sourceWidth != static_cast<UINT64>(p->width) ||
        sourceHeight != static_cast<UINT>(p->height) || p->x + width > target.Width || p->y + height > target.Height ||
        source.SampleDesc.Count != 1 || target.SampleDesc.Count != 1 || source.DepthOrArraySize != 1 || target.DepthOrArraySize != 1 ||
        temporary.Dimension != D3D12_RESOURCE_DIMENSION_BUFFER || temporary.Width < static_cast<UINT64>(p->rowBlocks) * 16 * (height / 4))
    { status(-3); return; }
    d3d->RequestResourceState(p->source, D3D12_RESOURCE_STATE_COPY_SOURCE);
    d3d->RequestResourceState(p->buffer, D3D12_RESOURCE_STATE_COPY_DEST);
    d3d->RequestResourceState(p->destination, D3D12_RESOURCE_STATE_COPY_DEST);
    UnityGraphicsD3D12RecordingState recording = {};
    if (!d3d->CommandRecordingState(&recording) || !recording.commandList) { status(-4); return; }
    D3D12_TEXTURE_COPY_LOCATION texture = {}, buffer = {};
    texture.pResource = p->source; texture.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; texture.SubresourceIndex = p->mip;
    buffer.pResource = p->buffer; buffer.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    buffer.PlacedFootprint.Footprint.Format = DXGI_FORMAT_BC7_UNORM;
    // Placed BC footprints describe complete blocks, including a mip smaller than one block.
    buffer.PlacedFootprint.Footprint.Width = width; buffer.PlacedFootprint.Footprint.Height = height;
    buffer.PlacedFootprint.Footprint.Depth = 1; buffer.PlacedFootprint.Footprint.RowPitch = p->rowBlocks * 16;
    recording.commandList->CopyTextureRegion(&buffer, 0, 0, 0, &texture, nullptr);
    d3d->RequestResourceState(p->buffer, D3D12_RESOURCE_STATE_COPY_SOURCE);
    if (!d3d->CommandRecordingState(&recording) || !recording.commandList) { status(-4); return; }
    buffer.PlacedFootprint.Footprint.Width = width; buffer.PlacedFootprint.Footprint.Height = height;
    texture.pResource = p->destination; texture.SubresourceIndex = 0;
    recording.commandList->CopyTextureRegion(&texture, p->x, p->y, 0, &buffer, nullptr);
    d3d->RequestResourceState(p->source, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    d3d->RequestResourceState(p->destination, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
    d3d->RequestResourceState(p->buffer, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
    InterlockedIncrement(&p->events); status(1);
}
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuCopyTextureAbiVersion() { return 1; }
extern "C" UNITY_INTERFACE_EXPORT UnityRenderingEventAndData Bc7GpuCopyTextureEvent() { return CopyTextureBlocks; }
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuCopyTextureEventId() { return eventId + 1; }
extern "C" UNITY_INTERFACE_EXPORT int Bc7GpuCopyTextureStatus(void* data)
{ return InterlockedCompareExchange(&static_cast<Bc7CopyPacket*>(data)->status, 0, 0); }

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
    if (graphics) eventId = graphics->ReserveEventIDRange(2);
    InterlockedIncrement(&generation);
    if (d3d)
    {
        UnityD3D12PluginEventConfig config = {};
        config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
        config.ensureActiveRenderTextureIsBound = false;
        d3d->ConfigureEvent(eventId, &config);
        d3d->ConfigureEvent(eventId + 1, &config);
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
