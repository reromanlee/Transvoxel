using reromanlee.Transvoxel.Density;
using UnityEngine;

namespace reromanlee.Transvoxel
{
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
        [Tooltip("How many finished chunk meshes may be uploaded per frame on the main thread.")]
        [Range(1, 64)] public int meshApplyBudgetPerFrame = 8;

        [Tooltip("Maximum parallel chunk builds. 0 = processor count - 1.")]
        [Range(0, 32)] public int maxConcurrentBuilds = 0;

        [Tooltip("Chunks up to this LOD level receive a MeshCollider (baked off the main thread). " +
                 "-1 disables colliders entirely.")]
        [Range(-1, 8)] public int colliderMaxLod = 0;

        [Tooltip("The chunk set is recomputed after the viewer moves this fraction of a LOD0 chunk.")]
        [Range(0.05f, 2f)] public float viewerMoveThreshold = 0.5f;

        [Tooltip("Density grids kept in memory for fast re-meshing (terraforming, LOD changes).")]
        [Range(0, 4096)] public int densityCacheChunks = 512;

        public int EffectiveMaxConcurrentBuilds =>
            maxConcurrentBuilds > 0 ? maxConcurrentBuilds : Mathf.Max(1, System.Environment.ProcessorCount - 1);
    }
}
