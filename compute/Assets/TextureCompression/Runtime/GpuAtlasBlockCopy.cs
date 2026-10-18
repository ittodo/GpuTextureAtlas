using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.TextureCompression
{
    // Native image->buffer->image copies preserve partial edge blocks. No mapping or codec runs here.
    internal sealed class GpuAtlasBlockCopy : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Packet
        {
            internal IntPtr source, buffer, destination;
            internal int width, height, mip, blockSize, rowBlocks, x, y, status, events, generation;
        }
#if UNITY_IOS && !UNITY_EDITOR
        const string Library = "__Internal";
#else
        const string Library = "AstcGpuUpload";
#endif
        [DllImport(Library)] static extern int AstcGpuUploadInitialize();
        [DllImport(Library)] static extern int AstcGpuCopyTextureAbiVersion();
        [DllImport(Library)] static extern IntPtr AstcGpuCopyTextureEvent();
        [DllImport(Library)] static extern int AstcGpuCopyTextureStatus(IntPtr packet);
        [DllImport("Bc7GpuUpload")] static extern int Bc7GpuCopyTextureAbiVersion();
        [DllImport("Bc7GpuUpload")] static extern IntPtr Bc7GpuCopyTextureEvent();
        [DllImport("Bc7GpuUpload")] static extern int Bc7GpuCopyTextureEventId();
        [DllImport("Bc7GpuUpload")] static extern int Bc7GpuCopyTextureStatus(IntPtr packet);
        [DllImport("Bc7GpuUpload")] static extern int Bc7GpuUploadInitialize();
        [DllImport("Bc7GpuUpload")] static extern int Bc7GpuUploadGeneration();
        ComputeBuffer buffer;
        CommandBuffer command;
        IntPtr packet;
        readonly bool bc7;
        internal int Status => bc7 ? Bc7GpuCopyTextureStatus(packet) : AstcGpuCopyTextureStatus(packet);

        internal GpuAtlasBlockCopy(Texture2D source, Texture2D destination, int width, int height, int mip, int block, int x, int y)
        {
            bc7 = destination.format == TextureFormat.BC7;
            bool supported = bc7 ? ((Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer) &&
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12) :
                (Application.platform == RuntimePlatform.Android && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan) ||
                (Application.platform == RuntimePlatform.IPhonePlayer && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal);
            if (!supported || IntPtr.Size != 8) throw new NotSupportedException("Partial block copies require Windows x64 D3D12 BC7, Android ARM64 Vulkan ASTC or iOS ARM64 Metal ASTC.");
            if (Marshal.SizeOf<Packet>() != 64 || (bc7 ? Bc7GpuCopyTextureAbiVersion() : AstcGpuCopyTextureAbiVersion()) != 1 ||
                (bc7 ? Bc7GpuUploadInitialize() : AstcGpuUploadInitialize()) != 1)
                throw new NotSupportedException("Install the current native bridge for partial block copies.");
            try
            {
                int stride = ((width + block - 1) / block + 15) / 16 * 16;
                buffer = new ComputeBuffer(checked(stride * ((height + block - 1) / block)), 16);
                packet = Marshal.AllocHGlobal(64);
                Marshal.StructureToPtr(new Packet { source = source.GetNativeTexturePtr(), buffer = buffer.GetNativeBufferPtr(),
                    destination = destination.GetNativeTexturePtr(), width = width, height = height, mip = mip,
                    blockSize = block, rowBlocks = stride, x = x, y = y,
                    generation = bc7 ? Bc7GpuUploadGeneration() : 0 }, packet, false);
                command = new CommandBuffer { name = "Compressed edge blocks GPU copy" };
                command.IssuePluginEventAndData(bc7 ? Bc7GpuCopyTextureEvent() : AstcGpuCopyTextureEvent(), bc7 ? Bc7GpuCopyTextureEventId() : 1, packet);
                Graphics.ExecuteCommandBuffer(command);
            }
            catch
            {
                var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                GpuAtlasRetirement.Retire(fence, Dispose); throw;
            }
        }
        // The caller retires this packet only after the GPU atlas fence has passed.
        public void Dispose()
        {
            command?.Dispose(); command = null; buffer?.Dispose(); buffer = null;
            if (packet != IntPtr.Zero) { Marshal.FreeHGlobal(packet); packet = IntPtr.Zero; }
        }
    }
}
