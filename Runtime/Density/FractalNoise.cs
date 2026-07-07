using System;

namespace reromanlee.Transvoxel.Density
{
    /// <summary>
    /// Self-contained, deterministic Perlin gradient noise with fractal (fBm) helpers.
    ///
    /// Unity's Mathf.PerlinNoise is not seedable and only 2D, and UnityEngine.Random
    /// is main-thread only — chunk meshing runs on worker threads, so the noise here
    /// is plain math over a seeded permutation table: same seed, same terrain, on any
    /// thread. Output of Sample2/Sample3 is roughly in [-1, 1].
    /// </summary>
    public sealed class FractalNoise
    {
        readonly byte[] perm = new byte[512];

        public FractalNoise(int seed)
        {
            // Fisher-Yates shuffle of 0..255 with a small deterministic LCG,
            // duplicated to 512 entries so lookups never need wrapping.
            var p = new byte[256];
            for (int i = 0; i < 256; i++) p[i] = (byte)i;
            uint state = (uint)seed * 2654435761u + 1013904223u;
            for (int i = 255; i > 0; i--)
            {
                state = state * 1664525u + 1013904223u;
                int j = (int)(state % (uint)(i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }
            for (int i = 0; i < 512; i++) perm[i] = p[i & 255];
        }

        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        static float Grad2(int hash, float x, float y)
        {
            // 8 gradient directions.
            switch (hash & 7)
            {
                case 0: return x + y;
                case 1: return x - y;
                case 2: return -x + y;
                case 3: return -x - y;
                case 4: return x;
                case 5: return -x;
                case 6: return y;
                default: return -y;
            }
        }

        static float Grad3(int hash, float x, float y, float z)
        {
            // 12 gradient directions (edge midpoints of a cube).
            switch (hash & 15)
            {
                case 0: return x + y;
                case 1: return -x + y;
                case 2: return x - y;
                case 3: return -x - y;
                case 4: return x + z;
                case 5: return -x + z;
                case 6: return x - z;
                case 7: return -x - z;
                case 8: return y + z;
                case 9: return -y + z;
                case 10: return y - z;
                case 11: return -y - z;
                case 12: return x + y;
                case 13: return -y + z;
                case 14: return -x + y;
                default: return -y - z;
            }
        }

        public float Sample2(float x, float y)
        {
            int xi = FloorToInt(x), yi = FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            xi &= 255; yi &= 255;

            float u = Fade(xf), v = Fade(yf);
            int a = perm[xi] + yi, b = perm[xi + 1] + yi;

            return Lerp(
                Lerp(Grad2(perm[a], xf, yf), Grad2(perm[b], xf - 1f, yf), u),
                Lerp(Grad2(perm[a + 1], xf, yf - 1f), Grad2(perm[b + 1], xf - 1f, yf - 1f), u),
                v);
        }

        public float Sample3(float x, float y, float z)
        {
            int xi = FloorToInt(x), yi = FloorToInt(y), zi = FloorToInt(z);
            float xf = x - xi, yf = y - yi, zf = z - zi;
            xi &= 255; yi &= 255; zi &= 255;

            float u = Fade(xf), v = Fade(yf), w = Fade(zf);
            int a = perm[xi] + yi, aa = perm[a] + zi, ab = perm[a + 1] + zi;
            int b = perm[xi + 1] + yi, ba = perm[b] + zi, bb = perm[b + 1] + zi;

            return Lerp(
                Lerp(
                    Lerp(Grad3(perm[aa], xf, yf, zf), Grad3(perm[ba], xf - 1f, yf, zf), u),
                    Lerp(Grad3(perm[ab], xf, yf - 1f, zf), Grad3(perm[bb], xf - 1f, yf - 1f, zf), u),
                    v),
                Lerp(
                    Lerp(Grad3(perm[aa + 1], xf, yf, zf - 1f), Grad3(perm[ba + 1], xf - 1f, yf, zf - 1f), u),
                    Lerp(Grad3(perm[ab + 1], xf, yf - 1f, zf - 1f), Grad3(perm[bb + 1], xf - 1f, yf - 1f, zf - 1f), u),
                    v),
                w);
        }

        /// <summary>Fractal Brownian motion: octaves of Sample2 with increasing frequency.</summary>
        public float Fbm2(float x, float y, int octaves, float lacunarity, float persistence)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample2(x, y) * amp;
                norm += amp;
                amp *= persistence;
                x *= lacunarity;
                y *= lacunarity;
            }
            return sum / norm;
        }

        /// <summary>Fractal Brownian motion: octaves of Sample3 with increasing frequency.</summary>
        public float Fbm3(float x, float y, float z, int octaves, float lacunarity, float persistence)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample3(x, y, z) * amp;
                norm += amp;
                amp *= persistence;
                x *= lacunarity;
                y *= lacunarity;
                z *= lacunarity;
            }
            return sum / norm;
        }

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return v < i ? i - 1 : i;
        }
    }
}
