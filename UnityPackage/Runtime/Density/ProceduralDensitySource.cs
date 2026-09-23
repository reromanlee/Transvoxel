using System;
using UnityEngine;

namespace reromanlee.Transvoxel.Density
{
    /// <summary>
    /// Inspector-friendly parameters for <see cref="ProceduralDensitySource"/>.
    /// </summary>
    [Serializable]
    public class NoiseSettings
    {
        public int seed = 1337;

        [Header("Height field")]
        [Tooltip("World y of the average ground surface, in meters.")]
        public float groundLevel = 0f;

        [Tooltip("Maximum height variation above/below ground level, in meters.")]
        public float heightAmplitude = 48f;

        [Tooltip("Feature frequency of the terrain, in 1/meters. Lower = wider hills.")]
        public float frequency = 0.004f;

        [Range(1, 8)] public int octaves = 5;
        public float lacunarity = 2f;
        [Range(0f, 1f)] public float persistence = 0.5f;

        [Tooltip("Vertical distance in meters over which density fades from solid to air. " +
                 "Larger values give smoother normals; must stay above one voxel.")]
        public float surfaceBlend = 8f;

        [Header("Caves / overhangs")]
        [Tooltip("0 disables 3D carving. Higher values cut more aggressively.")]
        [Range(0f, 4f)] public float caveStrength = 0f;
        public float caveFrequency = 0.02f;
        [Tooltip("3D noise above this threshold gets carved out; range roughly [-1, 1].")]
        public float caveThreshold = 0.45f;
    }

    /// <summary>
    /// Layer B of the terrain: the original, untouched landscape (Concept.txt #2).
    ///
    /// Density is a signed-distance-like ramp around a fractal height field, optionally
    /// carved by 3D noise for caves and overhangs. The ramp (rather than a hard 0/1 step)
    /// is what makes Marching Cubes vertices land between lattice points and gives the
    /// surface smooth gradients for normal estimation.
    /// </summary>
    public sealed class ProceduralDensitySource : IDensitySource
    {
        readonly NoiseSettings s;
        readonly FractalNoise noise;
        readonly float voxelSize;

        public ProceduralDensitySource(NoiseSettings settings, float voxelSize)
        {
            s = settings;
            noise = new FractalNoise(settings.seed);
            this.voxelSize = voxelSize;
        }

        public float SampleVoxel(int x, int y, int z)
        {
            float wx = x * voxelSize;
            float wy = y * voxelSize;
            float wz = z * voxelSize;

            float height = s.groundLevel
                           + noise.Fbm2(wx * s.frequency, wz * s.frequency, s.octaves, s.lacunarity, s.persistence)
                           * s.heightAmplitude;

            // Signed distance to the height surface, mapped to a density ramp:
            // 1 deep underground, 0.5 exactly at the surface, 0 high in the air.
            float density = Mathf.Clamp01(0.5f + (height - wy) / (2f * s.surfaceBlend));

            if (s.caveStrength > 0f && density > 0f)
            {
                float carve = noise.Fbm3(wx * s.caveFrequency, wy * s.caveFrequency, wz * s.caveFrequency, 3,
                    s.lacunarity, s.persistence);
                density -= Mathf.Max(0f, carve - s.caveThreshold) * s.caveStrength;
                if (density < 0f) density = 0f;
            }

            return density;
        }
    }
}
