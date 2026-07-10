using System;
using reromanlee.Transvoxel.Density;
using UnityEngine;

namespace reromanlee.Transvoxel
{
    /// <summary>Where chunk volumes are sampled and meshed.</summary>
    public enum MeshingBackend
    {
        /// <summary>Worker threads on the CPU. Works everywhere, no GPU requirements.</summary>
        CpuThreads = 0,

        /// <summary>
        /// Compute shaders: density (noise + player edits) and the whole Transvoxel
        /// triangulation run on the GPU; the CPU only uploads finished meshes. Falls back
        /// to <see cref="CpuThreads"/> when the platform lacks compute or async readback.
        /// </summary>
        GpuCompute = 1,
    }

    /// <summary>
    /// All tuning knobs of the terrain in one asset (Concept.txt #4). Create via
    /// Assets ▸ Create ▸ Transvoxel ▸ Terrain Settings, or leave the field on
    /// <see cref="TransvoxelTerrain"/> empty to get sensible defaults at runtime.
    /// </summary>
    [CreateAssetMenu(fileName = "TransvoxelSettings", menuName = "Transvoxel/Terrain Settings")]
    public sealed class TransvoxelSettings : ScriptableObject
    {
        [Header("World")]
        [Tooltip("World size of one LOD0 voxel in meters. 1 gives bit-exact chunk borders.")]
        [Min(0.1f)] public float voxelSize = 1f;

        [Tooltip("Cells per chunk axis. 16 matches the paper and is a good default.")]
        [Range(8, 32)] public int chunkCells = 16;

        [Tooltip("LOD levels on top of LOD0. 4 means chunks exist at LOD0..LOD4, " +
                 "each level covering twice the size at half the resolution.")]
        [Range(1, 8)] public int maxLodLevels = 4;

        [Tooltip("No chunks are generated beyond this distance from the viewer, in meters.")]
        [Min(32f)] public float viewDistance = 500f;

        [Tooltip("A chunk splits into finer ones while the viewer is closer than this factor " +
                 "times the chunk's size. Higher = more detail further away, more chunks.")]
        [Range(1f, 4f)] public float lodSplitFactor = 1.4f;

        [Tooltip("Density value that counts as the terrain surface.")]
        [Range(0.05f, 0.95f)] public float isoLevel = 0.5f;

        [Header("Appearance")]
        [Tooltip("Smooth gradient normals with shared vertices, or flat low-poly triangles (Concept.txt #6).")]
        public bool smoothShading = true;

        [Tooltip("Material for the terrain. Leave empty for a default lit material. " +
                 "UV0 is a world-space XZ planar map; a triplanar shader is recommended " +
                 "for cliffs and multi-texture terrains (see paper Chapter 5).")]
        public Material material;

        [Tooltip("Texture repeats per meter in the world-space UVs.")]
        public float uvScale = 0.1f;

        [Tooltip("Tint chunks by LOD level to visualize the octree (debug).")]
        public bool colorizeLods;

        [Header("Landscape (density layer B)")]
        public NoiseSettings noise = new NoiseSettings();

        [Header("Performance")]
        [Tooltip("Where chunks are sampled and meshed. CPU = worker threads (runs everywhere). " +
                 "GPU = compute shaders do the noise, the player-edit overlay and the whole " +
                 "Transvoxel triangulation, leaving the CPU nearly idle; results stream back " +
                 "asynchronously. Falls back to CPU if the platform lacks compute shaders.")]
        public MeshingBackend meshingBackend = MeshingBackend.CpuThreads;

        [Tooltip("Optional replacement for the built-in TransvoxelCompute shader " +
                 "(loaded from the package's Resources when empty).")]
        public ComputeShader gpuComputeOverride;

        [Tooltip("GPU mode: how many chunk builds may be in flight on the GPU at once " +
                 "(dispatched but not yet read back). Higher = faster world fill, more VRAM.")]
        [Range(1, 32)] public int gpuJobsInFlight = 8;

        [Tooltip("Main-thread time budget per frame for uploading finished chunk meshes, in " +
                 "milliseconds. Uploads stop as soon as the budget is spent, so a burst of " +
                 "finished chunks (teleport, fast flight) never turns into one long frame.")]
        [Range(0.5f, 12f)] public float meshApplyBudgetMs = 3f;

        [Tooltip("Hard cap on mesh uploads per frame, on top of the millisecond budget.")]
        [Range(1, 64)] public int meshApplyBudgetPerFrame = 16;

        [Tooltip("Maximum parallel chunk builds (CPU mode). 0 = processor count - 2, " +
                 "leaving headroom for the main and render threads so building never " +
                 "competes with the frame itself.")]
        [Range(0, 32)] public int maxConcurrentBuilds = 0;

        [Tooltip("Chunks up to this LOD level receive a MeshCollider (baked off the main thread). " +
                 "-1 disables colliders entirely.")]
        [Range(-1, 8)] public int colliderMaxLod = 0;

        [Tooltip("The chunk set is recomputed after the viewer moves this fraction of a LOD0 chunk.")]
        [Range(0.05f, 2f)] public float viewerMoveThreshold = 0.5f;

        [Tooltip("Extra seconds a replaced chunk is kept on screen after its replacements are " +
                 "ready, so neighbours finishing their own transition rebuilds never expose a " +
                 "momentary crack during an LOD swap. 0 = retire as soon as replacements exist " +
                 "(may flicker while moving fast); larger = smoother swaps, slightly more overdraw.")]
        [Range(0f, 3f)] public float lodSwapLinger = 0.5f;

        [Tooltip("Density grids kept in memory for fast re-meshing (terraforming, LOD changes).")]
        [Range(0, 4096)] public int densityCacheChunks = 512;

        public int EffectiveMaxConcurrentBuilds =>
            maxConcurrentBuilds > 0 ? maxConcurrentBuilds : Mathf.Max(1, System.Environment.ProcessorCount - 2);

        /// <summary>
        /// Raised whenever a value changes, so a running <see cref="TransvoxelTerrain"/> can
        /// rebuild and apply the tweak live (Concept.txt #4). Unity fires this through
        /// <see cref="OnValidate"/> when you edit the asset in the Inspector — including
        /// during Play. If you change a field from code, call <see cref="NotifyChanged"/>
        /// yourself afterwards.
        /// </summary>
        public event Action Changed;

        public void NotifyChanged() => Changed?.Invoke();

        void OnValidate() => Changed?.Invoke();
    }
}
