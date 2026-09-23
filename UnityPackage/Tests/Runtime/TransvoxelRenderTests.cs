using System.Collections;
using NUnit.Framework;
using reromanlee.Transvoxel.Density;
using UnityEngine;
using UnityEngine.TestTools;

namespace reromanlee.Transvoxel.Tests
{
    /// <summary>
    /// PlayMode tests that assert on rendered pixels. The EditMode suite proves the geometry
    /// is correct; these prove it is *shaded* correctly, which is where this package has had
    /// its most costly bugs — each one here stands for a defect that shipped, looked fine in
    /// code review, and was only visible on screen.
    ///
    /// They need a real graphics device and are skipped without one (batchmode -nographics).
    /// </summary>
    public sealed class TransvoxelRenderTests
    {
        /// <summary>
        /// LOD colorization was silently dead whenever a material palette was assigned: the
        /// tint went through a MaterialPropertyBlock as _BaseColor, and the palette shader
        /// variants build albedo entirely from the palette without ever reading _BaseColor.
        /// The frames with the debug view on and off were pixel-identical.
        /// </summary>
        [UnityTest]
        public IEnumerator LodColorization_ChangesTheImage_WithAPaletteAssigned()
        {
            if (!TerrainTestRig.GraphicsAvailable)
                Assert.Ignore("needs a graphics device");

            var palette = ScriptableObject.CreateInstance<TransvoxelMaterialPalette>();
            palette.Layers[0].tint = new Color(0.42f, 0.55f, 0.3f);

            Color32[] plain = TerrainTestRig.NewFrame();
            Color32[] tinted = TerrainTestRig.NewFrame();

            TransvoxelSettings settings = TerrainTestRig.Settings(palette);
            settings.colorizeLods = false;
            var rig = TerrainTestRig.Build(settings, new Vector3(0f, 120f, -120f),
                new Vector3(35f, 0f, 0f));
            try
            {
                yield return TerrainTestRig.Settle(rig.Terrain);
                Assert.Greater(rig.Terrain.LiveChunkCount, 0, "no chunks were built");
                yield return TerrainTestRig.Capture(rig, plain);

                // Flip the debug view on the live terrain — this is also the cheap in-place
                // refresh path, so it must not need a rebuild to take effect.
                settings.colorizeLods = true;
                rig.Terrain.RefreshLodTint();
                yield return TerrainTestRig.Capture(rig, tinted);
            }
            finally
            {
                rig.Dispose();
                Object.Destroy(palette);
                Object.Destroy(settings);
            }

            float difference = TerrainTestRig.MeanDifference(plain, tinted);
            Assert.Greater(difference, 4f,
                $"colorizeLods changed nothing on screen (mean difference {difference:0.00}/255). " +
                "The LOD tint is not reaching the palette shader variants.");
        }

        /// <summary>
        /// The shader declares the UV2 fade input in every variant, but the mesh only carried
        /// that channel when fading was switched on — so chunkFadeInSeconds = 0, a documented
        /// setting, left the attribute unbound and the fragment shader dither-clipped the
        /// surface against undefined data, riddling the terrain with holes.
        /// </summary>
        [UnityTest]
        public IEnumerator FadingDisabled_RendersTheSameSolidSurfaceAsFadingEnabled()
        {
            if (!TerrainTestRig.GraphicsAvailable)
                Assert.Ignore("needs a graphics device");

            Color32[] withFade = TerrainTestRig.NewFrame();
            Color32[] withoutFade = TerrainTestRig.NewFrame();

            var position = new Vector3(0f, 120f, -120f);
            var euler = new Vector3(35f, 0f, 0f);

            TransvoxelSettings fading = TerrainTestRig.Settings(null);
            fading.chunkFadeInSeconds = 0.3f;
            var rig = TerrainTestRig.Build(fading, position, euler);
            try
            {
                yield return TerrainTestRig.Settle(rig.Terrain);
                // Let every chunk finish dithering in before looking.
                float until = Time.realtimeSinceStartup + 1.5f;
                while (Time.realtimeSinceStartup < until)
                    yield return null;
                yield return TerrainTestRig.Capture(rig, withFade);
            }
            finally
            {
                rig.Dispose();
                Object.Destroy(fading);
            }
            yield return null;

            TransvoxelSettings instant = TerrainTestRig.Settings(null);
            instant.chunkFadeInSeconds = 0f;
            var rig2 = TerrainTestRig.Build(instant, position, euler);
            try
            {
                yield return TerrainTestRig.Settle(rig2.Terrain);
                yield return TerrainTestRig.Capture(rig2, withoutFade);
            }
            finally
            {
                rig2.Dispose();
                Object.Destroy(instant);
            }

            // Without this, two empty frames would satisfy the similarity assertion below.
            float coverage = TerrainTestRig.Coverage(withoutFade);
            Assert.Greater(coverage, 0.3f,
                $"terrain covers only {coverage:P0} of the frame - too little was rendered for " +
                "the comparison below to mean anything");

            float difference = TerrainTestRig.MeanDifference(withFade, withoutFade);
            Assert.Less(difference, 3f,
                $"the same terrain rendered differently with fading off (mean difference " +
                $"{difference:0.00}/255) — the fade vertex channel is missing and the shader " +
                "is clipping against unbound data.");
        }

        /// <summary>Solid for x &lt; 0 and below y = 0: one vertical face, one flat top.</summary>
        sealed class WallDensity : IDensitySource
        {
            public float SampleVoxel(int x, int y, int z) =>
                Mathf.Clamp01(0.5f + Mathf.Min(-x, -y) / 3f);
        }

        /// <summary>
        /// UV0 is a world-XZ planar map, so on a face whose normal is +X the U coordinate
        /// barely changes while V does: the texture smears into vertical streaks, and each
        /// column of pixels becomes nearly constant. Triplanar restores a real 2D pattern.
        /// Measured rather than eyeballed — vertical variation collapses when streaking.
        /// </summary>
        [UnityTest]
        public IEnumerator Triplanar_RemovesVerticalStreakingOnAVerticalFace()
        {
            if (!TerrainTestRig.GraphicsAvailable)
                Assert.Ignore("needs a graphics device");

            TerrainTestRig.Checker(128, 6, out Texture2D albedo, out Texture2D height);
            Color32[] planar = TerrainTestRig.NewFrame();
            Color32[] triplanar = TerrainTestRig.NewFrame();

            // Out in the open air in front of the wall, looking straight at its face.
            var position = new Vector3(22f, -6f, 0f);
            var euler = new Vector3(6f, -90f, 0f);

            for (int pass = 0; pass < 2; pass++)
            {
                var palette = ScriptableObject.CreateInstance<TransvoxelMaterialPalette>();
                palette.Layers[0].albedo = albedo;
                palette.Layers[0].tint = Color.white;
                palette.heightBlend = 0f;
                palette.triplanar = pass == 1;

                TransvoxelSettings settings = TerrainTestRig.Settings(palette);
                settings.viewDistance = 160f;
                settings.maxLodLevels = 2;
                settings.uvScale = 0.25f;
                var rig = TerrainTestRig.Build(settings, position, euler, new WallDensity());
                try
                {
                    yield return TerrainTestRig.Settle(rig.Terrain);
                    Assert.Greater(rig.Terrain.LiveChunkCount, 0, "no chunks were built");
                    yield return TerrainTestRig.Capture(rig, pass == 0 ? planar : triplanar);
                }
                finally
                {
                    rig.Dispose();
                    Object.Destroy(palette);
                    Object.Destroy(settings);
                }
                yield return null;
            }
            Object.Destroy(albedo);
            Object.Destroy(height);

            float planarVariation = TerrainTestRig.VerticalVariation(planar);
            float triplanarVariation = TerrainTestRig.VerticalVariation(triplanar);
            Assert.Greater(triplanarVariation, planarVariation * 3f,
                $"triplanar did not restore vertical detail on a vertical face " +
                $"(planar {planarVariation:0.00}, triplanar {triplanarVariation:0.00})");
        }

        /// <summary>
        /// Parallax occlusion has to visibly change the surface — height maps used to only
        /// steer blend weights between two different materials, which on a single material is
        /// an identity, so a height map appeared to do nothing whatsoever.
        /// </summary>
        [UnityTest]
        public IEnumerator Parallax_ChangesTheSurface_WhenAHeightMapIsPresent()
        {
            if (!TerrainTestRig.GraphicsAvailable)
                Assert.Ignore("needs a graphics device");

            TerrainTestRig.Bricks(256, 4, out Texture2D albedo, out Texture2D height);
            Color32[] flat = TerrainTestRig.NewFrame();
            Color32[] deep = TerrainTestRig.NewFrame();

            var position = new Vector3(0f, 2.6f, -4.5f);
            var euler = new Vector3(27f, 0f, 0f);

            for (int pass = 0; pass < 2; pass++)
            {
                var palette = ScriptableObject.CreateInstance<TransvoxelMaterialPalette>();
                palette.Layers[0].albedo = albedo;
                palette.Layers[0].height = height;
                palette.Layers[0].tint = Color.white;
                palette.Layers[0].heightScale = 0.03f;
                palette.heightBlend = 0f;
                palette.parallaxOcclusion = pass == 1;
                palette.parallaxMinSteps = 8;
                palette.parallaxMaxSteps = 32;
                palette.parallaxDistance = 120f;
                Assert.AreEqual(pass == 1, palette.ParallaxActive,
                    "parallax should be active exactly when it is switched on and a height map exists");

                TransvoxelSettings settings = TerrainTestRig.Settings(palette);
                settings.uvScale = 0.2f;
                var rig = TerrainTestRig.Build(settings, position, euler);
                try
                {
                    yield return TerrainTestRig.Settle(rig.Terrain);
                    yield return TerrainTestRig.Capture(rig, pass == 0 ? flat : deep);
                }
                finally
                {
                    rig.Dispose();
                    Object.Destroy(palette);
                    Object.Destroy(settings);
                }
                yield return null;
            }
            Object.Destroy(albedo);
            Object.Destroy(height);

            float difference = TerrainTestRig.MeanDifference(flat, deep);
            Assert.Greater(difference, 2f,
                $"parallax occlusion changed nothing on screen (mean difference " +
                $"{difference:0.00}/255) — the ray march is not running.");
        }
    }
}
