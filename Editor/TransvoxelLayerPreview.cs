using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace reromanlee.Transvoxel.Editor
{
    /// <summary>
    /// Renders one palette layer as a lit sphere into an offscreen texture, for the
    /// palette window's rotatable preview. Owns a single <see cref="PreviewRenderUtility"/>
    /// and preview material; dispose with the view that uses it.
    /// </summary>
    sealed class TransvoxelLayerPreview : IDisposable
    {
        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");

        PreviewRenderUtility utility;
        Mesh sphereMesh;
        Material material;

        /// <summary>
        /// Renders the layer and returns the preview texture. The texture is owned by the
        /// utility and stays valid until the next Render call — assign it to the target
        /// image, don't cache it.
        /// </summary>
        public Texture Render(Texture2D albedo, Color tint, float smoothness, Vector2 angles,
            int width, int height)
        {
            EnsureResources();

            if (material.HasProperty(BaseMapId)) material.SetTexture(BaseMapId, albedo);
            if (material.HasProperty(MainTexId)) material.SetTexture(MainTexId, albedo);
            if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, tint);
            if (material.HasProperty(ColorId)) material.SetColor(ColorId, tint);
            if (material.HasProperty(SmoothnessId)) material.SetFloat(SmoothnessId, smoothness);
            if (material.HasProperty(GlossinessId)) material.SetFloat(GlossinessId, smoothness);

            utility.BeginPreview(new Rect(0f, 0f, width, height), GUIStyle.none);

            // Frame the unit sphere explicitly — the utility's default camera (narrow FOV,
            // arbitrary position) does not, which left the old inspector preview blank.
            Camera camera = utility.camera;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 0f, -2.6f), Quaternion.identity);
            camera.fieldOfView = 30f;
            camera.nearClipPlane = 0.5f;
            camera.farClipPlane = 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 1f);

            utility.lights[0].intensity = 1.2f;
            utility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            utility.lights[1].intensity = 0.6f;
            utility.ambientColor = new Color(0.2f, 0.2f, 0.2f);

            var rotation = Quaternion.Euler(angles.y, -angles.x, 0f);
            utility.DrawMesh(sphereMesh, Matrix4x4.TRS(Vector3.zero, rotation, Vector3.one), material, 0);
            camera.Render();
            return utility.EndPreview();
        }

        void EnsureResources()
        {
            utility ??= new PreviewRenderUtility();
            if (sphereMesh == null)
            {
                // The editor-only way to a clean unit sphere mesh without leaking a scene object.
                GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(temp);
            }
            if (material == null)
                material = new Material(PreviewShader()) { hideFlags = HideFlags.HideAndDontSave };
        }

        /// <summary>The active pipeline's default lit shader, so previews match the project.</summary>
        static Shader PreviewShader()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Shader shader = pipeline != null ? pipeline.defaultShader : null;
            return shader != null ? shader : Shader.Find("Standard");
        }

        public void Dispose()
        {
            utility?.Cleanup();
            utility = null;
            if (material != null)
            {
                UnityEngine.Object.DestroyImmediate(material);
                material = null;
            }
        }
    }
}
