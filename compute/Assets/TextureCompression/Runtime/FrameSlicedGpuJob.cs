using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace UnityCompute.TextureCompression
{
    // Shared scheduling only. The format-specific owner supplies encoding and publication.
    internal sealed class FrameSlicedGpuJob : IDisposable
    {
        readonly int width, height, totalRows;
        readonly Action<Texture, int, int> encodeRows;
        readonly Action publish;
        Texture input;
        RenderTexture snapshot;
        GraphicsFence fence;
        bool hasFence;
        int submittedRows, lastSubmissionFrame = -1;
        public bool IsEncoding { get; private set; }
        public int CompletedBlockRows { get; private set; }
        public int SubmittedBlockRows => submittedRows;

        internal FrameSlicedGpuJob(int width, int height, int totalRows,
            Action<Texture, int, int> encodeRows, Action publish)
        {
            this.width = width; this.height = height; this.totalRows = totalRows;
            this.encodeRows = encodeRows; this.publish = publish;
        }

        internal void Begin(Texture source, bool snapshotInput)
        {
            if (IsEncoding) throw new InvalidOperationException("An encoding job is already in progress.");
            if (snapshotInput)
            {
                var required = CopyTextureSupport.Basic;
                if (!(source is RenderTexture)) required |= CopyTextureSupport.TextureToRT;
                if ((SystemInfo.copyTextureSupport & required) != required ||
                    !SystemInfo.IsFormatSupported(source.graphicsFormat, GraphicsFormatUsage.Render))
                    throw new NotSupportedException("GPU snapshots require a renderable source format and texture copy support. Use a stable input with snapshotInput:false instead.");
                if (snapshot == null || snapshot.graphicsFormat != source.graphicsFormat)
                {
                    DestroySnapshot();
                    var descriptor = new RenderTextureDescriptor(width, height)
                    {
                        graphicsFormat = source.graphicsFormat, depthStencilFormat = GraphicsFormat.None,
                        msaaSamples = 1, volumeDepth = 1, dimension = TextureDimension.Tex2D,
                        useMipMap = false, autoGenerateMips = false
                    };
                    snapshot = new RenderTexture(descriptor) { name = "Compression input snapshot" };
                    if (!snapshot.Create()) { DestroySnapshot(); throw new NotSupportedException("Cannot create the GPU input snapshot."); }
                }
                Graphics.CopyTexture(source, 0, 0, snapshot, 0, 0);
                input = snapshot;
                fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                hasFence = true;
            }
            else { input = source; hasFence = false; }
            submittedRows = 0; CompletedBlockRows = 0; IsEncoding = true;
        }

        // Call once per Update. A pending fence is polled, never waited on.
        internal bool Step(int maxBlockRows)
        {
            if (maxBlockRows < 1) throw new ArgumentOutOfRangeException(nameof(maxBlockRows));
            if (!IsEncoding) throw new InvalidOperationException("Call BeginEncode before EncodeNextFrame.");
            if (hasFence)
            {
                if (!fence.passed) return false;
                hasFence = false; CompletedBlockRows = submittedRows;
                if (submittedRows == totalRows) { IsEncoding = false; input = null; return true; }
            }
            // Prevent multiple submissions in one Player frame, including repeated calls by a caller.
            if (Application.isPlaying && lastSubmissionFrame == Time.frameCount) return false;
            if (input == null || input.width != width || input.height != height)
                throw new InvalidOperationException("Keep the input alive and unchanged until the job completes.");
            int rows = Math.Min(maxBlockRows, totalRows - submittedRows);
            encodeRows(input, submittedRows, rows);
            submittedRows += rows;
            if (submittedRows == totalRows) publish();
            fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            hasFence = true; lastSubmissionFrame = Time.frameCount;
            return false;
        }

        // The owner calls this only after its retirement fence has passed.
        public void Dispose() { IsEncoding = false; input = null; DestroySnapshot(); }
        void DestroySnapshot()
        {
            if (snapshot == null) return;
            snapshot.Release();
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(snapshot); else
#endif
                UnityEngine.Object.Destroy(snapshot);
            snapshot = null;
        }
    }
}
