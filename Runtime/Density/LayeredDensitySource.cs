using UnityEngine;

namespace reromanlee.Transvoxel.Density
{
    /// <summary>
    /// The two-layer density lookup from Concept.txt #2: player edits (layer A,
    /// <see cref="VoxelEditLayer"/>) take priority; anything untouched falls through
    /// to the original procedural landscape (layer B).
    /// </summary>
    public sealed class LayeredDensitySource : IDensitySource
    {
        public IDensitySource BaseSource { get; }
        public VoxelEditLayer Edits { get; }

        public LayeredDensitySource(IDensitySource baseSource, VoxelEditLayer edits)
        {
            BaseSource = baseSource;
            Edits = edits;
        }

        public float SampleVoxel(int x, int y, int z)
        {
            return Edits.TryGetVoxel(x, y, z, out float edited)
                ? edited
                : BaseSource.SampleVoxel(x, y, z);
        }

        /// <summary>
        /// Terraforming brush: pushes density towards air (dig) or solid (build) inside a
        /// sphere, blending softly at the rim. Coordinates are in LOD0 voxel units.
        /// Returns the axis-aligned voxel region that changed so callers can rebuild
        /// exactly the chunks that overlap it.
        /// </summary>
        public BoundsInt ApplySphereBrush(Vector3 centerVoxel, float radiusVoxels, float strength, bool build)
        {
            int min = Mathf.FloorToInt(-radiusVoxels) - 1;
            int max = Mathf.CeilToInt(radiusVoxels) + 1;
            var minVoxel = new Vector3Int(
                Mathf.FloorToInt(centerVoxel.x) + min,
                Mathf.FloorToInt(centerVoxel.y) + min,
                Mathf.FloorToInt(centerVoxel.z) + min);
            var size = Vector3Int.one * (max - min + 1);

            // Soft rim: full effect inside (radius - blend), fading to zero at the surface.
            float blend = Mathf.Max(1.5f, radiusVoxels * 0.4f);

            Edits.WriteBatch(set =>
            {
                for (int z = 0; z < size.z; z++)
                for (int y = 0; y < size.y; y++)
                for (int x = 0; x < size.x; x++)
                {
                    int vx = minVoxel.x + x, vy = minVoxel.y + y, vz = minVoxel.z + z;
                    float dist = Vector3.Distance(new Vector3(vx, vy, vz), centerVoxel);
                    float influence = Mathf.Clamp01((radiusVoxels - dist) / blend);
                    if (influence <= 0f) continue;

                    float current = SampleVoxel(vx, vy, vz);
                    float target = build
                        ? current + strength * influence
                        : current - strength * influence;
                    set(vx, vy, vz, Mathf.Clamp01(target));
                }
            });

            return new BoundsInt(minVoxel, size);
        }
    }
}
