using System;
using UnityEngine;

namespace romanlee17.PerlinNoise {

    public class VolumeGenerator {

        /// <summary>
        /// Permuation table for perlin noise.
        /// </summary>
        private readonly int[] permutationTable = new int[512];

        public VolumeGenerator(int seed) {
            System.Random random = new(seed);
            for (int x = 0; x < 256; x++) {
                permutationTable[x] = x;
            }
            for (int x = 255; x > 0; x--) {
                int y = random.Next(0, 256);
                (permutationTable[y], permutationTable[x]) = (permutationTable[x], permutationTable[y]);
            }
            for (int x = 0; x < 256; x++) {
                permutationTable[256 + x] = permutationTable[x];
            }
        }

        public float FractalNoise(float x, float y, float z, int octaves, float frequency, float amplitude) {
            float scale = 1.0f;
            float value = 0.0f;
            for (int i = 0; i < octaves; i++) {
                value += Noise(x * scale / frequency, y * scale / frequency, z * scale / frequency) * amplitude / scale;
                scale *= 2.0f;
            }
            return Mathf.Clamp(value, -1.0f, 1.0f);
        }

        /// <summary>
        /// Generates Perlin noise value for a given 3D point (x, y, z).
        /// </summary>
        private float Noise(float x, float y, float z) {
            // Compute original floored integers
            int xFloorOriginal = Mathf.FloorToInt(x);
            int yFloorOriginal = Mathf.FloorToInt(y);
            int zFloorOriginal = Mathf.FloorToInt(z);

            // Compute masked integers for hash/permutation indexing
            int xFloor = xFloorOriginal & 255;
            int yFloor = yFloorOriginal & 255;
            int zFloor = zFloorOriginal & 255;
            int xCeil = (xFloorOriginal + 1) & 255;
            int yCeil = (yFloorOriginal + 1) & 255;
            int zCeil = (zFloorOriginal + 1) & 255;

            // Fractional offsets
            float xFrac = x - xFloorOriginal;
            float yFrac = y - yFloorOriginal;
            float zFrac = z - zFloorOriginal;
            float xFracMinus1 = xFrac - 1.0f;
            float yFracMinus1 = yFrac - 1.0f;
            float zFracMinus1 = zFrac - 1.0f;

            // Fade values
            float zFade = Fade(zFrac);
            float yFade = Fade(yFrac);
            float xFade = Fade(xFrac);

            // Gradient calculations (8 corners)
            float grad000 = Gradient(permutationTable[xFloor + permutationTable[yFloor + permutationTable[zFloor]]], xFrac, yFrac, zFrac);
            float grad001 = Gradient(permutationTable[xFloor + permutationTable[yFloor + permutationTable[zCeil]]], xFrac, yFrac, zFracMinus1);
            float grad010 = Gradient(permutationTable[xFloor + permutationTable[yCeil + permutationTable[zFloor]]], xFrac, yFracMinus1, zFrac);
            float grad011 = Gradient(permutationTable[xFloor + permutationTable[yCeil + permutationTable[zCeil]]], xFrac, yFracMinus1, zFracMinus1);
            float grad100 = Gradient(permutationTable[xCeil + permutationTable[yFloor + permutationTable[zFloor]]], xFracMinus1, yFrac, zFrac);
            float grad101 = Gradient(permutationTable[xCeil + permutationTable[yFloor + permutationTable[zCeil]]], xFracMinus1, yFrac, zFracMinus1);
            float grad110 = Gradient(permutationTable[xCeil + permutationTable[yCeil + permutationTable[zFloor]]], xFracMinus1, yFracMinus1, zFrac);
            float grad111 = Gradient(permutationTable[xCeil + permutationTable[yCeil + permutationTable[zCeil]]], xFracMinus1, yFracMinus1, zFracMinus1);

            // Trilinear interpolation
            float lerpZ0 = Lerp(zFade, grad000, grad001); // Interpolate along z (lower y, lower x)
            float lerpZ1 = Lerp(zFade, grad010, grad011); // Interpolate along z (upper y, lower x)
            float lerpZ2 = Lerp(zFade, grad100, grad101); // Interpolate along z (lower y, upper x)
            float lerpZ3 = Lerp(zFade, grad110, grad111); // Interpolate along z (upper y, upper x)

            float lerpY0 = Lerp(yFade, lerpZ0, lerpZ1); // Interpolate along y (lower x)
            float lerpY1 = Lerp(yFade, lerpZ2, lerpZ3); // Interpolate along y (upper x)

            return Lerp(xFade, lerpY0, lerpY1); // Interpolate along x
        }

        /// <summary>
        /// Gradient vectors as [gx, gy, gz] for hash % 16 (0 to 15).
        /// Each triplet represents coefficients for x, y, z.
        /// </summary>
        private static readonly float[] gradientTable = new float[] {
             1,  1,  0,   // 0: ( 1,  1,  0)
            -1,  1,  0,   // 1: (-1,  1,  0)
             1, -1,  0,   // 2: ( 1, -1,  0)
            -1, -1,  0,   // 3: (-1, -1,  0)
             1,  0,  1,   // 4: ( 1,  0,  1)
            -1,  0,  1,   // 5: (-1,  0,  1)
             1,  0, -1,   // 6: ( 1,  0, -1)
            -1,  0, -1,   // 7: (-1,  0, -1)
             0,  1,  1,   // 8: ( 0,  1,  1)
             0, -1,  1,   // 9: ( 0, -1,  1)
             0,  1, -1,   // 10:( 0,  1, -1)
             0, -1, -1,   // 11:( 0, -1, -1)
             1,  1,  0,   // 12:( 1,  1,  0)  *Matches original behavior for 12
            -1,  1,  0,   // 13:( -1, 1,  0)
             1, -1,  0,   // 14:( 1, -1,  0)  *Matches original behavior for 14
            -1, -1,  0    // 15:(-1, -1,  0)
        };

        /// <summary>
        /// Computes the dot product of a pseudorandom gradient vector and the distance vector.
        /// This is used in the Perlin noise algorithm to calculate the influence of a gradient
        /// at a given point in space (x, y, z).
        /// </summary>
        private static float Gradient(int hash, float x, float y, float z) {
            int h = hash & 15;  // Bitwise AND instead of % 16 (faster, same result for 0-255)
            int index = h * 3;  // Each gradient has 3 components (gx, gy, gz)
            return gradientTable[index] * x + gradientTable[index + 1] * y + gradientTable[index + 2] * z;
        }

        /// <summary>
        /// Linearly interpolates between two values based on a given weight.
        /// </summary>
        private static float Lerp(float t, float a, float b) {
            return a + t * (b - a);
        }

        /// <summary>
        /// This is a 5th-degree polynomial (quintic) that takes an input t (typically between 0 and 1) 
        /// and produces a smoothed output between 0 and 1. It’s often written in expanded form as:
        /// f(t) = 6t^5 - 15t^4 + 10t^3
        /// </summary>
        private static float Fade(float t) {
            return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
        }

    }

}