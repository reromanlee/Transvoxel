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
    /// which could carry shaders that cannot run on the same blended pixel. All layer
    /// albedos are baked into one <see cref="Texture2DArray"/>, so the whole landscape still
    /// renders with a single material and SRP batching stays intact no matter how many
    /// materials it shows.
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

            [Tooltip("Albedo texture of this material. Empty uses plain white, so the tint " +
                     "alone defines the color.")]
            public Texture2D albedo;

            [Tooltip("Multiplied into the albedo.")]
            public Color tint = Color.white;

            [Range(0f, 1f)] public float smoothness = 0.1f;

            [Tooltip("Texture repeats of this layer relative to the terrain's UV scale. " +
                     "2 tiles this material twice as densely as the others.")]
            [Min(0.01f)] public float uvScaleMultiplier = 1f;
        }

        [SerializeField]
        List<Layer> layers = new List<Layer>
        {
            new Layer { name = "Default", tint = new Color(0.42f, 0.55f, 0.3f) },
        };

        public IReadOnlyList<Layer> Layers => layers;

        public int LayerCount => Mathf.Min(layers.Count, MaxLayers);

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
        /// array and refresh the shader uniforms live — no chunk is ever rebuilt for it.
        /// Fired by <see cref="OnValidate"/> on Inspector edits; call
        /// <see cref="NotifyChanged"/> after changing layers from code.
        /// </summary>
        public event Action Changed;

        public void NotifyChanged()
        {
            bakedAlbedoDirty = true;
            Changed?.Invoke();
        }

        void OnValidate() => NotifyChanged();

        // ------------------------------------------------------------------ shader uniforms

        /// <summary>
        /// Writes the per-layer shader parameters into caller-provided arrays of
        /// <see cref="MaxLayers"/> entries (uniform arrays must always be uploaded at full
        /// declared size — see the terrain). colors = (tint.rgb, smoothness),
        /// scales = (uvScaleMultiplier, 0, 0, 0).
        /// </summary>
        public void FillLayerUniforms(Vector4[] colors, Vector4[] scales)
        {
            int count = LayerCount;
            for (int i = 0; i < MaxLayers; i++)
            {
                Layer layer = i < count ? layers[i] : null;
                Color tint = layer?.tint ?? Color.white;
                colors[i] = new Vector4(tint.r, tint.g, tint.b, layer?.smoothness ?? 0f);
                scales[i] = new Vector4(layer?.uvScaleMultiplier ?? 1f, 0f, 0f, 0f);
            }
        }

        // ------------------------------------------------------------------ albedo array

        Texture2DArray bakedAlbedo;
        bool bakedAlbedoDirty = true;

        /// <summary>
        /// The layer albedos baked into one texture array (layer index = material id),
        /// rebaked lazily after any palette change. When every albedo shares size, format
        /// and mip count the bake is a plain GPU copy that keeps compressed formats; mixed
        /// inputs are resized onto the largest layer and stored uncompressed instead.
        /// Returns null for an empty palette.
        /// </summary>
        public Texture2DArray GetAlbedoArray()
        {
            if (!bakedAlbedoDirty && bakedAlbedo != null)
                return bakedAlbedo;

            DestroyBakedAlbedo();
            if (LayerCount > 0)
                bakedAlbedo = BakeAlbedoArray();
            bakedAlbedoDirty = false;
            return bakedAlbedo;
        }

        void OnDisable() => DestroyBakedAlbedo();

        void DestroyBakedAlbedo()
        {
            if (bakedAlbedo == null)
                return;
            if (Application.isPlaying)
                Destroy(bakedAlbedo);
            else
                DestroyImmediate(bakedAlbedo);
            bakedAlbedo = null;
        }

        Texture2DArray BakeAlbedoArray()
        {
            int count = LayerCount;
            var sources = new Texture2D[count];
            for (int i = 0; i < count; i++)
                sources[i] = layers[i].albedo != null ? layers[i].albedo : Texture2D.whiteTexture;

            Texture2DArray array = CanCopyDirectly(sources)
                ? BakeByCopy(sources)
                : BakeByBlit(sources);
            array.name = $"{name} Albedo Array";
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
        /// the fast path.
        /// </summary>
        static Texture2DArray BakeByBlit(Texture2D[] sources)
        {
            int width = 4, height = 4;
            foreach (Texture2D source in sources)
            {
                width = Mathf.Max(width, source.width);
                height = Mathf.Max(height, source.height);
            }

            var array = new Texture2DArray(width, height, sources.Length, TextureFormat.RGBA32,
                mipChain: true, linear: false);
            var scratch = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true, linear: false);
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
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
