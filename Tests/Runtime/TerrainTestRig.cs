using System.Collections;
using reromanlee.Transvoxel.Density;
using UnityEngine;
using UnityEngine.Rendering;

namespace reromanlee.Transvoxel.Tests
{
    /// <summary>
    /// Shared scaffolding for the PlayMode render tests: build a terrain, wait for it to
    /// settle, and read the rendered frame back as pixels so the tests can assert on what
    /// was actually drawn rather than on what the code intended to draw.
    ///
    /// Deliberately does not use <c>Camera.Render</c> (unsupported under SRPs) or
    /// <c>WaitForEndOfFrame</c> (unreliable in batchmode): the camera is given a target
    /// texture, the pipeline renders into it as part of its normal frame, and the texture is
    /// read back a few frames later.
    /// </summary>
    static class TerrainTestRig
    {
        public const int Width = 480;
        public const int Height = 270;

        /// <summary>Render tests need a real device; skip rather than fail under -nographics.</summary>
        public static bool GraphicsAvailable =>
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        public sealed class Rig
        {
            public GameObject TerrainObject;
            public GameObject CameraObject;
            public GameObject SunObject;
            public TransvoxelTerrain Terrain;
            public Camera Camera;

            public void Dispose()
            {
                if (TerrainObject != null) Object.Destroy(TerrainObject);
                if (CameraObject != null) Object.Destroy(CameraObject);
                if (SunObject != null) Object.Destroy(SunObject);
            }
        }

        /// <summary>Settings with every time-dependent effect off, so captures are deterministic.</summary>
        public static TransvoxelSettings Settings(TransvoxelMaterialPalette palette)
        {
            var settings = ScriptableObject.CreateInstance<TransvoxelSettings>();
            settings.name = "Transvoxel Test Settings";
            settings.viewDistance = 220f;
            settings.maxLodLevels = 3;
            settings.lodSplitFactor = 1.4f;
            settings.chunkFadeInSeconds = 0f;
            settings.edgeFadeFraction = 0f;
            settings.colliderMaxLod = -1;
            settings.colorizeLods = false;
            settings.uvScale = 0.2f;
            settings.materialPalette = palette;
            settings.noise = new NoiseSettings
            {
                seed = 1337,
                groundLevel = 0f,
                heightAmplitude = 30f,
                frequency = 0.01f,
                octaves = 4,
                surfaceBlend = 6f,
                caveStrength = 0f,
            };
            return settings;
        }

        public static Rig Build(TransvoxelSettings settings, Vector3 cameraPosition,
            Vector3 cameraEuler, IDensitySource densityOverride = null)
        {
            var rig = new Rig();

            rig.CameraObject = new GameObject("Test Camera");
            rig.Camera = rig.CameraObject.AddComponent<Camera>();
            rig.Camera.clearFlags = CameraClearFlags.SolidColor;
            rig.Camera.backgroundColor = new Color(0.35f, 0.45f, 0.6f);
            rig.Camera.farClipPlane = 800f;
            rig.Camera.nearClipPlane = 0.05f;
            rig.CameraObject.transform.SetPositionAndRotation(cameraPosition,
                Quaternion.Euler(cameraEuler));

            rig.SunObject = new GameObject("Test Sun");
            var sun = rig.SunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.4f;
            rig.SunObject.transform.rotation = Quaternion.Euler(30f, 40f, 0f);

            // Assign before OnEnable runs, as a scene-authored terrain would have them.
            rig.TerrainObject = new GameObject("Test Terrain");
            rig.TerrainObject.SetActive(false);
            rig.Terrain = rig.TerrainObject.AddComponent<TransvoxelTerrain>();
            rig.Terrain.settings = settings;
            rig.Terrain.viewer = rig.CameraObject.transform;
            if (densityOverride != null)
                rig.Terrain.DensityOverride = densityOverride;
            rig.TerrainObject.SetActive(true);
            return rig;
        }

        /// <summary>Pumps frames until the build queue drains and the chunk set stops changing.</summary>
        public static IEnumerator Settle(TransvoxelTerrain terrain, float timeoutSeconds = 60f)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            int stable = 0;
            int last = -1;
            while (Time.realtimeSinceStartup < deadline)
            {
                bool idle = terrain.PendingBuildCount == 0 && terrain.LiveChunkCount > 0
                            && terrain.LiveChunkCount == last;
                stable = idle ? stable + 1 : 0;
                last = terrain.LiveChunkCount;
                if (stable >= 12)
                    yield break;
                yield return null;
            }
            Debug.LogWarning($"[Transvoxel.Tests] terrain did not settle within {timeoutSeconds}s " +
                             $"(live={terrain.LiveChunkCount}, pending={terrain.PendingBuildCount})");
        }

        /// <summary>Renders the rig's camera and returns the frame. Must be iterated.</summary>
        public static IEnumerator Capture(Rig rig, Color32[] result)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "Transvoxel Test Target",
                antiAliasing = 1,
            };
            target.Create();
            rig.Camera.targetTexture = target;
            for (int i = 0; i < 4; i++)
                yield return null;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;

            Color32[] pixels = texture.GetPixels32();
            System.Array.Copy(pixels, result, result.Length);

            rig.Camera.targetTexture = null;
            target.Release();
            Object.Destroy(target);
            Object.Destroy(texture);
        }

        public static Color32[] NewFrame() => new Color32[Width * Height];

        /// <summary>The camera background, so coverage can be measured against it.</summary>
        public static readonly Color32 Background = new Color32(89, 115, 153, 255);

        /// <summary>
        /// Fraction of the frame that is not the clear colour — i.e. how much of it the
        /// terrain actually covers. Used to prove something was rendered at all: a test that
        /// asserts two frames are SIMILAR would otherwise be satisfied by two empty ones.
        ///
        /// Counting distinct colours would not do: a smoothly shaded single-colour terrain
        /// varies almost entirely in brightness, so its colours fall along one line in RGB
        /// and quantise down to very few buckets even when the render is perfect.
        /// </summary>
        public static float Coverage(Color32[] frame)
        {
            int covered = 0;
            foreach (Color32 pixel in frame)
            {
                int distance = Mathf.Abs(pixel.r - Background.r)
                               + Mathf.Abs(pixel.g - Background.g)
                               + Mathf.Abs(pixel.b - Background.b);
                if (distance > 24)
                    covered++;
            }
            return covered / (float)frame.Length;
        }

        /// <summary>Mean absolute per-channel difference between two frames, in 0..255.</summary>
        public static float MeanDifference(Color32[] a, Color32[] b)
        {
            long total = 0;
            for (int i = 0; i < a.Length; i++)
            {
                total += Mathf.Abs(a[i].r - b[i].r);
                total += Mathf.Abs(a[i].g - b[i].g);
                total += Mathf.Abs(a[i].b - b[i].b);
            }
            return total / (float)(a.Length * 3);
        }

        /// <summary>
        /// Mean absolute difference between vertically adjacent pixels, over a centred
        /// region. A surface whose texture has been smeared into vertical streaks is almost
        /// constant down each column, so this collapses towards zero; any real 2D pattern
        /// keeps it high. This is what distinguishes stretched planar UVs from triplanar.
        /// </summary>
        public static float VerticalVariation(Color32[] frame)
        {
            int x0 = Width / 5, x1 = Width - Width / 5;
            int y0 = Height / 5, y1 = Height - Height / 5;
            long total = 0;
            int count = 0;
            for (int y = y0; y < y1 - 1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * Width + x;
                int j = (y + 1) * Width + x;
                total += Mathf.Abs(frame[i].r - frame[j].r);
                total += Mathf.Abs(frame[i].g - frame[j].g);
                total += Mathf.Abs(frame[i].b - frame[j].b);
                count += 3;
            }
            return count == 0 ? 0f : total / (float)count;
        }

        // ------------------------------------------------------------------ textures

        /// <summary>A checkerboard, with a matching height map (light squares raised).</summary>
        public static void Checker(int size, int cells, out Texture2D albedo, out Texture2D height)
        {
            albedo = NewTexture(size, false);
            height = NewTexture(size, true);
            var albedoPixels = new Color[size * size];
            var heightPixels = new Color[size * size];
            int cell = Mathf.Max(1, size / cells);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool dark = ((x / cell) + (y / cell)) % 2 == 0;
                int i = y * size + x;
                albedoPixels[i] = dark
                    ? new Color(0.20f, 0.26f, 0.33f)
                    : new Color(0.80f, 0.76f, 0.68f);
                float h = dark ? 0.1f : 0.9f;
                heightPixels[i] = new Color(h, h, h, 1f);
            }
            Fill(albedo, albedoPixels);
            Fill(height, heightPixels);
        }

        /// <summary>Bevelled brickwork: a smooth heightfield, which is what a ray march wants.</summary>
        public static void Bricks(int size, int bricks, out Texture2D albedo, out Texture2D height)
        {
            albedo = NewTexture(size, false);
            height = NewTexture(size, true);
            var albedoPixels = new Color[size * size];
            var heightPixels = new Color[size * size];
            float cell = size / (float)bricks;
            float mortar = cell * 0.10f;
            float bevel = cell * 0.16f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int row = (int)(y / cell);
                float shifted = (x + (row % 2) * (cell * 0.5f)) % size;
                float dx = Mathf.Abs(shifted % cell - mortar * 0.5f);
                float dy = Mathf.Abs(y % cell - mortar * 0.5f);
                float h = Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01((Mathf.Min(dx, dy) - mortar * 0.5f) / bevel));

                int i = y * size + x;
                heightPixels[i] = new Color(h, h, h, 1f);
                albedoPixels[i] = Color.Lerp(new Color(0.32f, 0.31f, 0.29f),
                    new Color(0.66f, 0.36f, 0.28f), h);
            }
            Fill(albedo, albedoPixels);
            Fill(height, heightPixels);
        }

        // RGBA32 on purpose: RGB24 cannot be sampled from a Texture2DArray on some platforms,
        // which is a bug this suite has already had to fix once.
        static Texture2D NewTexture(int size, bool linear) =>
            new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true, linear: linear);

        static void Fill(Texture2D texture, Color[] pixels)
        {
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.SetPixels(pixels);
            texture.Apply(updateMipmaps: true);
        }
    }
}
