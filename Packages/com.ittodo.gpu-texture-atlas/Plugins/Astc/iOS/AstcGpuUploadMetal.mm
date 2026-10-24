// iOS Metal bridge. Unity and its command buffer retain ownership of all GPU resources.
#import <Metal/Metal.h>
#include <stddef.h>
#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"

struct AstcUploadPacket
{
    void* buffer; void* texture;
    int width, height, blockSize, rowBlocks;
    int status, recordedEvents;
};
static_assert(sizeof(AstcUploadPacket) == 40, "Managed/native upload ABI differs");
struct AstcCopyPacket
{
    void* source; void* buffer; void* destination;
    int width, height, mip, blockSize, rowBlocks, x, y, status, events;
};
static_assert(sizeof(AstcCopyPacket) == 64, "ASTC copy ABI differs");
static_assert(offsetof(AstcCopyPacket, status) == 52, "ASTC copy status differs");
static IUnityGraphicsMetalV2* metal;
static bool registered;
static MTLPixelFormat Format(int block)
{
    switch (block)
    {
        case 4: return MTLPixelFormatASTC_4x4_LDR;
        case 5: return MTLPixelFormatASTC_5x5_LDR;
        case 6: return MTLPixelFormatASTC_6x6_LDR;
        case 8: return MTLPixelFormatASTC_8x8_LDR;
        case 10: return MTLPixelFormatASTC_10x10_LDR;
        case 12: return MTLPixelFormatASTC_12x12_LDR;
        default: return MTLPixelFormatInvalid;
    }
}
static void Status(AstcUploadPacket* packet, int value) { __atomic_store_n(&packet->status, value, __ATOMIC_RELEASE); }
static void UNITY_INTERFACE_API Upload(int eventId, void* data)
{
    (void)eventId;
    auto* packet = static_cast<AstcUploadPacket*>(data);
    if (!packet) return;
    if (!metal) { Status(packet, -1); return; }
    MTLPixelFormat expected = Format(packet->blockSize);
    if (packet->width <= 0 || packet->height <= 0 || expected == MTLPixelFormatInvalid ||
        packet->rowBlocks < (packet->width + packet->blockSize - 1) / packet->blockSize || packet->rowBlocks % 16 != 0)
    { Status(packet, -2); return; }
    id<MTLBuffer> source = (__bridge id<MTLBuffer>)packet->buffer;
    id<MTLTexture> destination = (__bridge id<MTLTexture>)packet->texture;
    NSUInteger rowBytes = static_cast<NSUInteger>(packet->rowBlocks) * 16;
    NSUInteger rows = (packet->height + packet->blockSize - 1) / packet->blockSize;
    // Apple's ASTC sRGB values are exactly 18 lower than the matching LDR values.
    if (!source || !destination || source.length < rowBytes * rows || destination.width != static_cast<NSUInteger>(packet->width) ||
        destination.height != static_cast<NSUInteger>(packet->height) ||
        (destination.pixelFormat != expected && destination.pixelFormat != static_cast<MTLPixelFormat>(expected - 18)))
    { Status(packet, -2); return; }
    metal->EndCurrentCommandEncoder();
    id<MTLCommandBuffer> commands = metal->CurrentCommandBuffer();
    if (!commands) { Status(packet, -3); return; }
    id<MTLBlitCommandEncoder> blit = [commands blitCommandEncoder];
    if (!blit) { Status(packet, -3); return; }
    [blit copyFromBuffer:source sourceOffset:0 sourceBytesPerRow:rowBytes sourceBytesPerImage:rowBytes * rows
        sourceSize:MTLSizeMake(packet->width, packet->height, 1) toTexture:destination destinationSlice:0 destinationLevel:0
        destinationOrigin:MTLOriginMake(0, 0, 0)];
    [blit endEncoding];
    __atomic_add_fetch(&packet->recordedEvents, 1, __ATOMIC_RELEASE);
    Status(packet, 1);
}
static void UNITY_INTERFACE_API Load(IUnityInterfaces* interfaces) { metal = interfaces->Get<IUnityGraphicsMetalV2>(); }
static void UNITY_INTERFACE_API CopyTextureBlocks(int, void* data)
{
    auto* p = static_cast<AstcCopyPacket*>(data); if (!p) return;
    auto status = [p](int value) { __atomic_store_n(&p->status, value, __ATOMIC_RELEASE); };
    const int block = p->blockSize;
    if (!metal || p->width < 1 || p->height < 1 || p->mip < 0 || p->mip > 30 || Format(block) == MTLPixelFormatInvalid ||
        p->x < 0 || p->y < 0 || p->x % block || p->y % block || p->rowBlocks % 16 ||
        p->rowBlocks < (p->width + block - 1) / block) { status(-1); return; }
    id<MTLTexture> source = (__bridge id<MTLTexture>)p->source;
    id<MTLTexture> destination = (__bridge id<MTLTexture>)p->destination;
    id<MTLBuffer> buffer = (__bridge id<MTLBuffer>)p->buffer;
    NSUInteger rowBytes = static_cast<NSUInteger>(p->rowBlocks) * 16;
    NSUInteger rows = (p->height + block - 1) / block, columns = (p->width + block - 1) / block;
    MTLPixelFormat expected = Format(block);
    if (!source || !destination || !buffer || source.pixelFormat != destination.pixelFormat ||
        (source.pixelFormat != expected && source.pixelFormat != static_cast<MTLPixelFormat>(expected - 18)) ||
        p->mip >= source.mipmapLevelCount || buffer.length < rowBytes * rows ||
        p->x + columns * block > destination.width || p->y + rows * block > destination.height) { status(-2); return; }
    metal->EndCurrentCommandEncoder();
    id<MTLCommandBuffer> commands = metal->CurrentCommandBuffer();
    if (!commands) { status(-3); return; }
    id<MTLBlitCommandEncoder> blit = [commands blitCommandEncoder];
    if (!blit) { status(-3); return; }
    [blit copyFromTexture:source sourceSlice:0 sourceLevel:p->mip sourceOrigin:MTLOriginMake(0, 0, 0)
        sourceSize:MTLSizeMake(p->width, p->height, 1) toBuffer:buffer destinationOffset:0
        destinationBytesPerRow:rowBytes destinationBytesPerImage:rowBytes * rows];
    [blit copyFromBuffer:buffer sourceOffset:0 sourceBytesPerRow:rowBytes sourceBytesPerImage:rowBytes * rows
        sourceSize:MTLSizeMake(columns * block, rows * block, 1) toTexture:destination destinationSlice:0 destinationLevel:0
        destinationOrigin:MTLOriginMake(p->x, p->y, 0)];
    [blit endEncoding]; __atomic_add_fetch(&p->events, 1, __ATOMIC_RELEASE); status(1);
}
static void UNITY_INTERFACE_API Unload() { metal = nullptr; }
extern "C" void UnityRegisterRenderingPluginV5(void (UNITY_INTERFACE_API*)(IUnityInterfaces*), void (UNITY_INTERFACE_API*)());
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuUploadInitialize()
{
    if (!registered) { UnityRegisterRenderingPluginV5(Load, Unload); registered = true; }
    return metal ? 1 : 0;
}
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuUploadAbiVersion() { return 1; }
extern "C" UNITY_INTERFACE_EXPORT UnityRenderingEventAndData AstcGpuUploadEvent() { return Upload; }
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuUploadStatus(void* data) { return __atomic_load_n(&static_cast<AstcUploadPacket*>(data)->status, __ATOMIC_ACQUIRE); }
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuCopyTextureAbiVersion() { return 1; }
extern "C" UNITY_INTERFACE_EXPORT UnityRenderingEventAndData AstcGpuCopyTextureEvent() { return CopyTextureBlocks; }
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuCopyTextureStatus(void* data) { return __atomic_load_n(&static_cast<AstcCopyPacket*>(data)->status, __ATOMIC_ACQUIRE); }
