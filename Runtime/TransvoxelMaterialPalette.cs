using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// The terrain's material set: an ordered list of layers where the LIST INDEX IS THE
    /// MATERIAL ID stored in the voxels (<see cref="Density.VoxelMaterialLayer"/>). Layer 0
    /// is the default material the whole world is made of; terraforming (or custom painting
    /// code) assigns other ids, and the terrain shader blends between them per pixel.
    ///
    /// Because one pixel can show a mix of two materials, layers are texture/parameter sets
    /// blended by the terrain's own shader — not arbitrary <see cref="Material"/> assets,
    /// which could carry shaders that cannot run on the same blended pixel. Each map kind
    /// (albedo, normal, occlusion, height) is baked into one <see cref="Texture2DArray"/>,
    /// so the whole landscape still renders with a single material and SRP batching stays
    /// intact no matter how many materials it shows. The detail maps are opt-in by content:
    /// a palette without any normal/occlusion/height map renders on the exact albedo-only
    /// shader variant (and cost) it always had — see <see cref="HasDetailMaps"/>.
    ///
    /// Reordering layers re-labels every already-painted voxel (ids stay, meanings move) —
    /// the custom editor warns about this.
    /// </summary>
    [CreateAssetMenu(fileName = "TransvoxelMaterialPalette", menuName = "Transvoxel/Material Palette")]
    public sealed class TransvoxelMaterialPalette : ScriptableObject
    {
        /// <summary>
        /// Uniform-array capacity in the shader (a fixed declaration size, not a per-slot
        /// cost). Voxel ids are bytes, but 64 layers is far beyond any practical palette.
        /// </summary>
        public const int MaxLayers = 64;

        [Serializable]
        public sealed class Layer
        {
            public string name = "Material";

            [Tooltip("Albedo (color) texture of this material. Empty uses plain white, so " +
                     "the tint alone defines the color.")]
            public Texture2D albedo;

            [Tooltip("Multiplied into the albedo.")]
            public Color tint = Color.white;

            [Range(0f, 1f)] public float smoothness = 0.1f;

            [Tooltip("Tangent-space normal map (texture type 'Normal map'). Empty = flat.")]
            public Texture2D normal;

            [Tooltip("Strength of the normal map: 0 flattens it, above 1 exaggerates it.")]
            [Range(0f, 2f)] public float normalStrength = 1f;

            [Tooltip("Ambient occlusion map (white = fully lit). Attenuates ambient/indirect " +
                     "light only, like Unity's standard materials. Empty = no occlusion.")]
            public Texture2D occlusion;

            [Tooltip("How much of the occlusion map is applied.")]
            [Range(0f, 1f)] public float occlusionStrength = 1f;

            [Tooltip("Height map steering material transitions: at a boundary the higher " +
                     "surface (rock, cobbles) pushes through the lower one (sand, dirt) " +
                     "instead of a plain crossfade — see the palette's Height Blend slider. " +
                     "Not a displacement/parallax input. Empty reads as uniform mid height.")]
            public Texture2D height;

            [Tooltip("Texture repeats of this layer relative to the terrain's UV scale. " +
                     "2 tiles this material twice as densely as the others.")]
            [Min(0.01f)] public float uvScaleMultiplier = 1f;
        }

        [Tooltip("How strongly the layers' height maps steer material transitions: 0 = plain " +
                 "crossfade, 1 = the higher material cuts hard through the lower one. Only " +
                 "relative height differences matter — layers without a height map read as " +
                 "uniform mid height, so they keep the plain crossfade against each other. " +
                 "Live-tunable — no rebuild.")]
        [Range(0f, 1f)] public float heightBlend = 0.5f;

        [SerializeField]
        List<Layer> layers = new List<Layer>
        {
            new Layer { name = "Default", tint = new Color(0.42f, 0.55f, 0.3f) },
        };

        public IReadOnlyList<Layer> Layers => layers;

        public int LayerCount => Mathf.Min(layers.Count, MaxLayers);

        /// <summary>
        /// True when any layer carries a normal/occlusion/height map. The terrain picks the
        /// shader variant with this: map-free palettes keep rendering on the exact
        /// albedo-only path (and cost) they had before detail maps existed.
        /// </summary>
        public bool HasDetailMaps
        {
            get
            {
                int count = LayerCount;
                for (int i = 0; i < count; i++)
                {
                    Layer layer = layers[i];
                    if (layer.normal != null || layer.occlusion != null || layer.height != null)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Appends a layer from code (runtime-built palettes, tools); its material id is
        /// the returned index. Editing existing <see cref="Layers"/> entries is fine too —
        /// call <see cref="NotifyChanged"/> once after a batch of changes.
        /// </summary>
        public int AddLayer(Layer layer)
        {
            if (layers.Count >= MaxLayers)
                throw new InvalidOperationException($"A palette holds at most {MaxLayers} layers.");
            layers.Add(layer);
            NotifyChanged();
            return layers.Count - 1;
        }

        /// <summary>
        /// Raised whenever the palette changes so a running terrain can rebake the texture
        /// arrays and refresh the shader uniforms live — no chunk is ever rebuilt for it.
        /// Fired by <see cref="OnValidate"/> on Inspector edits; call
        /// <see cref="NotifyChanged"/> after changing layers from code.
        /// </summary>
        public event Action Changed;

        public void NotifyChanged()
        {
            albedoDirty = normalDirty = occlusionDirty = heightDirty = true;
            Changed?.Invoke();
        }

        void OnValidate() => NotifyChanged();

        // ------------------------------------------------------------------ shader uniforms

        /// <summary>
        /// Writes the per-layer shader parameters into caller-provided arrays of
        /// <see cref="MaxLayers"/> entries (uniform arrays must always be uploaded at full
        /// declared size — see the terrain). colors = (tint.rgb, smoothness),
        /// scales = (uvScaleMultiplier, normalStrength, occlusionStrength, 0).
        /// </summary>
        public void FillLayerUniforms(Vector4[] colors, Vector4[] scales)
        {
            int count = LayerCount;
            for (int i = 0; i < MaxLayers; i++)
            {
                Layer layer = i < count ? layers[i] : null;
                Color tint = layer?.tint ?? Color.white;
                colors[i] = new Vector4(tint.r, tint.g, tint.b, layer?.smoothness ?? 0f);
                scales[i] = new Vector4(layer?.uvScaleMultiplier ?? 1f,
                    layer?.normalStrength ?? 1f, layer?.occlusionStrength ?? 1f, 0f);
            }
        }

        // ------------------------------------------------------------------ baked map arrays

        Texture2DArray bakedAlbedo, bakedNormal, bakedOcclusion, bakedHeight;
        bool albedoDirty = true, normalDirty = true, occlusionDirty = true, heightDirty = true;

        // 4x4 flat normal (0.5, 0.5, 1, alpha 1). Not Texture2D.normalTexture: that one's
        // 0.5 alpha breaks the shader's RG/AG unpack (x = r·a), which must read x = 0.5 for
        // both uncompressed fallbacks (a = 1) and compressed maps (r = 1 or a = 1).
        static Texture2D flatNormal;

        static Texture2D FlatNormalFallback
        {
            get
            {
                if (flatNormal != null)
                    return flatNormal;
                flatNormal = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false, linear: true)
                {
                    name = "Transvoxel Flat Normal",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var pixels = new Color32[16];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(128, 128, 255, 255);
                flatNormal.SetPixels32(pixels);
                flatNormal.Apply(updateMipmaps: false);
                return flatNormal;
            }
        }

        /// <summary>
        /// The layer albedos baked into one texture array (layer index = material id),
        /// rebaked lazily after any palette change. When every source shares size, format
        /// and mip count the bake is a plain GPU copy that keeps compressed formats; mixed
        /// inputs are resized onto the largest layer and stored uncompressed instead.
        /// Returns null for an empty palette. Empty slots read plain white.
        /// </summary>
        public Texture2DArray GetAlbedoArray()
            => GetArray(ref bakedAlbedo, ref albedoDirty, l => l.albedo,
                Texture2D.whiteTexture, linear: false, "Albedo");

        /// <summary>
        /// The layer normal maps as one texture array (same bake rules as
        /// <see cref="GetAlbedoArray"/>). Baked linear — normal data never goes through
        /// sRGB. Empty slots read a flat normal.
        /// </summary>
        public Texture2DArray GetNormalArray()
            => GetArray(ref bakedNormal, ref normalDirty, l => l.normal,
                FlatNormalFallback, linear: true, "Normal");

        /// <summary>
        /// The layer occlusion maps as one texture array (same bake rules as
        /// <see cref="GetAlbedoArray"/>; the shader reads the R channel). Baked linear —
        /// import grayscale masks with sRGB off. Empty slots read white (unoccluded).
        /// </summary>
        public Texture2DArray GetOcclusionArray()
            => GetArray(ref bakedOcclusion, ref occlusionDirty, l => l.occlusion,
                Texture2D.whiteTexture, linear: true, "Occlusion");

        /// <summary>
        /// The layer height maps as one texture array (same bake rules as
        /// <see cref="GetAlbedoArray"/>; the shader reads the R channel). Baked linear —
        /// import grayscale masks with sRGB off. Empty slots read uniform mid height, the
        /// identity for the height-steered blend.
        /// </summary>
        public Texture2DArray GetHeightArray()
            => GetArray(ref bakedHeight, ref heightDirty, l => l.height,
                Texture2D.linearGrayTexture, linear: true, "Height");

        Texture2DArray GetArray(ref Texture2DArray baked, ref bool dirty,
            Func<Layer, Texture2D> map, Texture2D fallback, bool linear, string label)
        {
            if (!dirty && baked != null)
                return baked;

            DestroyBaked(ref baked);
            if (LayerCount > 0)
                baked = BakeArray(map, fallback, linear, label);
            dirty = false;
            return baked;
        }

        void OnDisable()
        {
            DestroyBaked(ref bakedAlbedo);
            DestroyBaked(ref bakedNormal);
            DestroyBaked(ref bakedOcclusion);
            DestroyBaked(ref bakedHeight);
        }

        static void DestroyBaked(ref Texture2DArray baked)
        {
            if (baked == null)
                return;
            if (Application.isPlaying)
                Destroy(baked);
            else
                DestroyImmediate(baked);
            baked = null;
        }

        Texture2DArray BakeArray(Func<Layer, Texture2D> map, Texture2D fallback, bool linear,
            string label)
        {
            int count = LayerCount;
            var sources = new Texture2D[count];
            for (int i = 0; i < count; i++)
            {
                Texture2D source = map(layers[i]);
                sources[i] = source != null ? source : fallback;
            }

            Texture2DArray array = CanCopyDirectly(sources)
                ? BakeByCopy(sources)
                : BakeByBlit(sources, linear);
            array.name = $"{name} {label} Array";
            array.hideFlags = HideFlags.HideAndDontSave;
            array.wrapMode = TextureWrapMode.Repeat;
            array.filterMode = FilterMode.Trilinear;
            array.anisoLevel = 4;
            return array;
        }

        static bool CanCopyDirectly(Texture2D[] sources)
        {
            Texture2D first = sources[0];
            foreach (Texture2D source in sources)
            {
                // Crunched formats are CPU-side containers Graphics.CopyTexture cannot
                // read — those palettes go through the blit path instead.
                if (GraphicsFormatUtility.IsCrunchFormat(source.format))
                    return false;
                if (source.width != first.width || source.height != first.height
                    || source.graphicsFormat != first.graphicsFormat
                    || source.mipmapCount != first.mipmapCount)
                    return false;
            }
            return true;
        }

        /// <summary>All sources match: element-to-element GPU copies, compression preserved.</summary>
        static Texture2DArray BakeByCopy(Texture2D[] sources)
        {
            Texture2D first = sources[0];
            // The MipChain flag is what allocates the mip levels — without it the mipCount
            // argument is ignored, the array gets a single level, and every CopyTexture of
            // a mipmapped source is rejected ("mismatching mip counts").
            TextureCreationFlags flags = first.mipmapCount > 1
                ? TextureCreationFlags.MipChain
                : TextureCreationFlags.None;
            var array = new Texture2DArray(first.width, first.height, sources.Length,
                first.graphicsFormat, flags, first.mipmapCount);
            for (int i = 0; i < sources.Length; i++)
                Graphics.CopyTexture(sources[i], 0, array, i);
            return array;
        }

        /// <summary>
        /// Mixed sizes/formats: every source is resized onto the largest layer through a
        /// temporary render target and stored as uncompressed RGBA32. Costs more video
        /// memory than the direct copy — give all layers the same import settings to get
        /// the fast path. Color data goes through sRGB, map data (normal/occlusion/height)
        /// stays linear so the raw values survive.
        /// </summary>
        static Texture2DArray BakeByBlit(Texture2D[] sources, bool linear)
        {
            int width = 4, height = 4;
            foreach (Texture2D source in sources)
            {
                width = Mathf.Max(width, source.width);
                height = Mathf.Max(height, source.height);
            }

            var array = new Texture2DArray(width, height, sources.Length, TextureFormat.RGBA32,
                mipChain: true, linear: linear);
            var scratch = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true,
                linear: linear);
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0,
                RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;

            for (int i = 0; i < sources.Length; i++)
            {
                Graphics.Blit(sources[i], target);
                RenderTexture.active = target;
                scratch.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                scratch.Apply(updateMipmaps: true, makeNoLongerReadable: false);
                Graphics.CopyTexture(scratch, 0, array, i);
            }

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            if (Application.isPlaying)
                Destroy(scratch);
            else
                DestroyImmediate(scratch);
            return array;
        }
    }
}
