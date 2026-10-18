using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCompute.TextureCompression
{
    // One material, immutable 4x4 grid and bounded ID lookup table per atlas/shader pair.
    internal sealed class GpuAtlasRenderState
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Record
        {
            internal Vector4 scaleOffset, clamp;
            internal uint id, occupied, reserved0, reserved1;
        }
        static readonly List<GpuAtlasRenderState> states = new List<GpuAtlasRenderState>();
        readonly List<GpuAtlasRenderer> clients = new List<GpuAtlasRenderer>();
        Dictionary<int, CompressedAtlasEntry> entries;
        GraphicsBuffer buffer;
        readonly GpuTextureAtlas atlas;
        readonly Shader shader;
        bool disposed;
        internal Material Material { get; private set; }
        internal Mesh Grid { get; private set; }
        internal int Capacity { get; private set; }
        internal int UploadCount { get; private set; }

        internal static GpuAtlasRenderState Acquire(GpuTextureAtlas atlas, Shader shader, GpuAtlasRenderer client)
        {
            foreach (var state in states)
                if (ReferenceEquals(state.atlas, atlas) && state.shader == shader)
                { state.clients.Add(client); state.RefreshInstancing(); return state; }
            var created = new GpuAtlasRenderState(atlas, shader);
            created.clients.Add(client); states.Add(created); created.RefreshInstancing(); return created;
        }
        GpuAtlasRenderState(GpuTextureAtlas atlas, Shader shader)
        {
            this.atlas = atlas; this.shader = shader;
            try
            {
                Material = new Material(shader) { name = "GPU atlas shared material", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                Grid = new Mesh { name = "GPU atlas shared slice grid", hideFlags = HideFlags.HideAndDontSave };
                var vertices = new Vector3[16]; var uv = new Vector2[16]; var triangles = new int[54];
                for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
                { int i = y * 4 + x; uv[i] = new Vector2(x / 3f, y / 3f); vertices[i] = new Vector3(uv[i].x - .5f, uv[i].y - .5f); }
                for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
                {
                    int vertex = y * 4 + x, index = (y * 3 + x) * 6;
                    triangles[index] = vertex; triangles[index + 1] = vertex + 5; triangles[index + 2] = vertex + 1;
                    triangles[index + 3] = vertex; triangles[index + 4] = vertex + 4; triangles[index + 5] = vertex + 5;
                }
                Grid.vertices = vertices; Grid.uv = uv; Grid.triangles = triangles; Grid.RecalculateBounds(); Grid.UploadMeshData(true);
                Synchronize(); atlas.Changed += OnChanged;
            }
            catch { Dispose(); throw; }
        }
        internal bool Matches(GpuTextureAtlas candidate, Shader candidateShader) => !disposed && ReferenceEquals(atlas, candidate) && shader == candidateShader;
        internal bool TryGetEntry(int id, out CompressedAtlasEntry entry) => entries.TryGetValue(id, out entry);
        internal void Release(GpuAtlasRenderer client)
        {
            clients.Remove(client); if (clients.Count == 0) Dispose(); else RefreshInstancing();
        }
        internal void RefreshInstancing()
        {
            // In Unity 6000.7.0b5, automatic MeshRenderer instancing bypasses SpriteMask stencil overrides.
            // Keep per-renderer mask tests by using ordinary GPU draws while this group has masked views.
            bool enabled = !clients.Exists(client => client != null && client.MaskInteraction != SpriteMaskInteraction.None);
            if (Material.enableInstancing != enabled) Material.enableInstancing = enabled;
        }
        static uint Hash(uint value)
        {
            unchecked { value ^= value >> 16; value *= 0x7feb352d; value ^= value >> 15; value *= 0x846ca68b; return value ^ (value >> 16); }
        }
        void Synchronize()
        {
            int capacity = 4;
            while (capacity < (long)atlas.Entries.Count * 2)
            { if (capacity > int.MaxValue / 2) throw new InvalidOperationException("Too many atlas entries."); capacity *= 2; }
            var records = new Record[capacity]; var nextEntries = new Dictionary<int, CompressedAtlasEntry>(atlas.Entries.Count);
            foreach (var entry in atlas.Entries)
            {
                nextEntries.Add(entry.SourceIndex, entry);
                int slot = (int)(Hash((uint)entry.SourceIndex) & (uint)(capacity - 1));
                while (records[slot].occupied != 0) slot = (slot + 1) & (capacity - 1);
                records[slot] = new Record { scaleOffset = entry.UvScaleOffset, clamp = entry.UvClamp, id = (uint)entry.SourceIndex, occupied = 1 };
            }
            GraphicsBuffer next = null;
            try
            {
                next = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 48); next.SetData(records);
                Material.SetBuffer("_AtlasEntries", next); Material.SetInteger("_AtlasHashMask", capacity - 1); Material.SetTexture("_AtlasTex", atlas.Texture);
            }
            catch
            {
                var failed = next; if (failed != null) Retire(() => failed.Dispose()); throw;
            }
            var old = buffer; buffer = next; entries = nextEntries; Capacity = capacity; UploadCount++;
            if (old != null) Retire(() => old.Dispose());
        }
        void OnChanged(GpuTextureAtlas changed)
        {
            if (atlas.IsDisposed) { Dispose(); return; }
            try
            {
                Synchronize(); foreach (var client in clients) if (client != null) client.RefreshFromState(false);
            }
            catch
            {
                // Stop future draws if resource allocation fails. Never render with a retired texture or stale UVs.
                Dispose(); throw;
            }
        }
        void Dispose()
        {
            if (disposed) return; disposed = true; atlas.Changed -= OnChanged; states.Remove(this);
            foreach (var client in clients) if (client != null) client.StateReleased(this); clients.Clear();
            var oldBuffer = buffer; var oldMaterial = Material; var oldMesh = Grid;
            buffer = null; Material = null; Grid = null;
            Retire(() => { oldBuffer?.Dispose(); DestroyObject(oldMaterial); DestroyObject(oldMesh); });
        }
        static void Retire(Action release)
        {
            var fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            GpuAtlasRetirement.Retire(fence, release);
        }
        internal static void DestroyObject(UnityEngine.Object value)
        {
            if (value == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(value); else
#endif
                UnityEngine.Object.Destroy(value);
        }
    }
}
