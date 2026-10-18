// Android arm64 Vulkan bridge. No codec or CPU readback runs in this plug-in.
#include <stddef.h>
#include "IUnityGraphics.h"
#include "IUnityGraphicsVulkan.h"

struct AstcUploadPacket
{
    void* buffer;
    void* texture;
    int width, height, blockSize, rowBlocks;
    int status, recordedEvents;
};
static_assert(sizeof(AstcUploadPacket) == 40, "Managed/native upload ABI differs");
static_assert(offsetof(AstcUploadPacket, status) == 32, "Upload status offset differs");
static IUnityGraphics* graphics;
static IUnityGraphicsVulkan* vulkan;
static VkDevice cachedDevice;
static PFN_vkCmdCopyBufferToImage copyBufferToImage;

// Freestanding Android build: provide compiler-emitted memory helpers without a C++ runtime.
extern "C" __attribute__((visibility("hidden"))) void* memset(void* destination, int value, size_t count)
{
    volatile unsigned char* bytes = static_cast<volatile unsigned char*>(destination);
    for (size_t i = 0; i < count; ++i) bytes[i] = static_cast<unsigned char>(value);
    return destination;
}
extern "C" __attribute__((visibility("hidden"))) void* memcpy(void* destination, const void* source, size_t count)
{
    volatile unsigned char* target = static_cast<volatile unsigned char*>(destination);
    const volatile unsigned char* origin = static_cast<const volatile unsigned char*>(source);
    for (size_t i = 0; i < count; ++i) target[i] = origin[i];
    return destination;
}
static void Status(AstcUploadPacket* packet, int value) { __atomic_store_n(&packet->status, value, __ATOMIC_RELEASE); }
static VkFormat Format(int block)
{
    switch (block)
    {
        case 4: return VK_FORMAT_ASTC_4x4_UNORM_BLOCK;
        case 5: return VK_FORMAT_ASTC_5x5_UNORM_BLOCK;
        case 6: return VK_FORMAT_ASTC_6x6_UNORM_BLOCK;
        case 8: return VK_FORMAT_ASTC_8x8_UNORM_BLOCK;
        case 10: return VK_FORMAT_ASTC_10x10_UNORM_BLOCK;
        case 12: return VK_FORMAT_ASTC_12x12_UNORM_BLOCK;
        default: return VK_FORMAT_UNDEFINED;
    }
}
static void UNITY_INTERFACE_API Upload(int eventId, void* data)
{
    (void)eventId;
    auto* packet = static_cast<AstcUploadPacket*>(data);
    if (!packet) return;
    if (!graphics || graphics->GetRenderer() != kUnityGfxRendererVulkan || !vulkan) { Status(packet, -1); return; }
    if (packet->width <= 0 || packet->height <= 0 || Format(packet->blockSize) == VK_FORMAT_UNDEFINED ||
        packet->rowBlocks < (packet->width + packet->blockSize - 1) / packet->blockSize) { Status(packet, -2); return; }
    UnityVulkanInstance instance = vulkan->Instance();
    if (cachedDevice != instance.device || !copyBufferToImage)
    {
        auto getDeviceProc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(instance.getInstanceProcAddr(instance.instance, "vkGetDeviceProcAddr"));
        if (!getDeviceProc) { Status(packet, -3); return; }
        copyBufferToImage = reinterpret_cast<PFN_vkCmdCopyBufferToImage>(getDeviceProc(instance.device, "vkCmdCopyBufferToImage"));
        cachedDevice = instance.device;
    }
    if (!copyBufferToImage) { Status(packet, -3); return; }
    vulkan->EnsureOutsideRenderPass();
    UnityVulkanBuffer buffer = {};
    UnityVulkanImage image = {};
    VkImageSubresource subresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0 };
    if (!vulkan->AccessBuffer(packet->buffer, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_ACCESS_TRANSFER_READ_BIT,
        kUnityVulkanResourceAccess_PipelineBarrier, &buffer)) { Status(packet, -4); return; }
    if (!vulkan->AccessTexture(packet->texture, &subresource, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
        VK_PIPELINE_STAGE_TRANSFER_BIT, VK_ACCESS_TRANSFER_WRITE_BIT, kUnityVulkanResourceAccess_PipelineBarrier, &image)) { Status(packet, -5); return; }
    VkFormat expected = Format(packet->blockSize);
    size_t rows = static_cast<size_t>((packet->height + packet->blockSize - 1) / packet->blockSize);
    size_t requiredBytes = static_cast<size_t>(packet->rowBlocks) * 16 * rows;
    if (!(buffer.usage & VK_BUFFER_USAGE_TRANSFER_SRC_BIT) || !(image.usage & VK_IMAGE_USAGE_TRANSFER_DST_BIT) ||
        buffer.sizeInBytes < requiredBytes || image.extent.width != static_cast<uint32_t>(packet->width) ||
        image.extent.height != static_cast<uint32_t>(packet->height) ||
        (image.format != expected && image.format != static_cast<VkFormat>(expected + 1))) { Status(packet, -6); return; }
    UnityVulkanRecordingState state = {};
    if (!vulkan->CommandRecordingState(&state, kUnityVulkanGraphicsQueueAccess_DontCare)) { Status(packet, -7); return; }
    VkBufferImageCopy region = {};
    region.bufferOffset = 0;
    region.bufferRowLength = static_cast<uint32_t>(packet->rowBlocks * packet->blockSize);
    region.bufferImageHeight = 0;
    region.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
    region.imageExtent = { static_cast<uint32_t>(packet->width), static_cast<uint32_t>(packet->height), 1 };
    copyBufferToImage(state.commandBuffer, buffer.buffer, image.image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    __atomic_add_fetch(&packet->recordedEvents, 1, __ATOMIC_RELEASE);
    Status(packet, 1); // Recorded, not a claim that GPU execution has completed.
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    graphics = interfaces->Get<IUnityGraphics>();
    vulkan = interfaces->Get<IUnityGraphicsVulkan>();
    cachedDevice = VK_NULL_HANDLE; copyBufferToImage = nullptr;
    if (vulkan)
    {
        UnityVulkanPluginEventConfig configuration = {};
        configuration.renderPassPrecondition = kUnityVulkanRenderPass_EnsureOutside;
        configuration.graphicsQueueAccess = kUnityVulkanGraphicsQueueAccess_DontCare;
        configuration.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission;
        vulkan->ConfigureEvent(0, &configuration);
    }
}
extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload() { graphics = nullptr; vulkan = nullptr; }
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuUploadInitialize() { return graphics && vulkan && graphics->GetRenderer() == kUnityGfxRendererVulkan ? 1 : 0; }
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuUploadAbiVersion() { return 1; }
extern "C" UNITY_INTERFACE_EXPORT UnityRenderingEventAndData AstcGpuUploadEvent() { return Upload; }
extern "C" UNITY_INTERFACE_EXPORT int AstcGpuUploadStatus(void* data) { return __atomic_load_n(&static_cast<AstcUploadPacket*>(data)->status, __ATOMIC_ACQUIRE); }
