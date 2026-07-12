using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace reromanlee.Transvoxel.Editor
{
    /// <summary>
    /// Inspector for <see cref="TransvoxelMaterialPalette"/>: a reorderable layer list
    /// where each entry shows its voxel material id (= list index) next to a live,
    /// drag-to-rotate preview sphere shaded with the layer's albedo, tint and smoothness —
    /// so the palette can be judged at a glance without entering Play mode.
    /// </summary>
    [CustomEditor(typeof(TransvoxelMaterialPalette))]
    public sealed class TransvoxelMaterialPaletteEditor : UnityEditor.Editor
    {
        const float PreviewSize = 96f;
        const float Padding = 6f;

        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");

        ReorderableList list;
        PreviewRenderUtility previewUtility;
        Mesh sphereMesh;

        // Per-layer preview state, keyed by list index.
        readonly Dictionary<int, Material> previewMaterials = new Dictionary<int, Material>();
        readonly Dictionary<int, Vector2> previewAngles = new Dictionary<int, Vector2>();

        void OnEnable()
        {
            list = new ReorderableList(serializedObject, serializedObject.FindProperty("layers"),
                draggable: true, displayHeader: true, displayAddButton: true, displayRemoveButton: true)
            {
                drawHeaderCallback = rect =>
                    EditorGUI.LabelField(rect, "Layers (list index = voxel material id)"),
                elementHeightCallback = _ => PreviewSize + 2f * Padding,
                drawElementCallback = DrawLayer,
                onCanRemoveCallback = l => l.count > 1, // layer 0 is the world default
                onCanAddCallback = l => l.count < TransvoxelMaterialPalette.MaxLayers,
            };
        }

        void OnDisable()
        {
            previewUtility?.Cleanup();
            previewUtility = null;
            foreach (Material material in previewMaterials.Values)
                DestroyImmediate(material);
            previewMaterials.Clear();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "Layer 0 fills the whole world by default; terraforming assigns the other " +
                "ids. Drag a preview sphere to rotate it.\n" +
                "Reordering layers re-labels every already-painted voxel: the stored ids " +
                "keep their values but now point at different layers.", MessageType.Info);
            EditorGUILayout.Space(2f);

            list.DoLayoutList();

            serializedObject.ApplyModifiedProperties();
        }

        void DrawLayer(Rect rect, int index, bool isActive, bool isFocused)
        {
            SerializedProperty layer = list.serializedProperty.GetArrayElementAtIndex(index);
            rect.y += Padding;
            rect.height -= 2f * Padding;

            var previewRect = new Rect(rect.xMax - PreviewSize, rect.y, PreviewSize, PreviewSize);
            var fields = new Rect(rect.x, rect.y, rect.width - PreviewSize - 2f * Padding, rect.height);

            float line = EditorGUIUtility.singleLineHeight;
            float step = line + EditorGUIUtility.standardVerticalSpacing;
            var row = new Rect(fields.x, fields.y, fields.width, line);

            EditorGUI.LabelField(row, $"ID {index}", EditorStyles.boldLabel);
            row.y += step;
            EditorGUI.PropertyField(row, layer.FindPropertyRelative("name"));
            row.y += step;
            EditorGUI.PropertyField(row, layer.FindPropertyRelative("albedo"));
            row.y += step;
            EditorGUI.PropertyField(row, layer.FindPropertyRelative("tint"));
            row.y += step;
            EditorGUI.PropertyField(row, layer.FindPropertyRelative("smoothness"));
            row.y += step;
            EditorGUI.PropertyField(row, layer.FindPropertyRelative("uvScaleMultiplier"),
                new GUIContent("UV Scale"));

            HandlePreview(previewRect, index, layer);
        }

        // ------------------------------------------------------------------ sphere preview

        void HandlePreview(Rect rect, int index, SerializedProperty layer)
        {
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            Event current = Event.current;
            switch (current.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when rect.Contains(current.mousePosition):
                    GUIUtility.hotControl = controlId;
                    current.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    Vector2 angles = previewAngles.TryGetValue(index, out Vector2 stored)
                        ? stored
                        : Vector2.zero;
                    angles += current.delta;
                    angles.y = Mathf.Clamp(angles.y, -89f, 89f);
                    previewAngles[index] = angles;
                    current.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    GUIUtility.hotControl = 0;
                    current.Use();
                    break;
                case EventType.Repaint:
                    RenderPreview(rect, index, layer);
                    break;
            }
        }

        void RenderPreview(Rect rect, int index, SerializedProperty layer)
        {
            EnsurePreviewResources();
            if (sphereMesh == null)
                return;

            Material material = GetPreviewMaterial(index, layer);
            Vector2 angles = previewAngles.TryGetValue(index, out Vector2 stored) ? stored : Vector2.zero;
            Quaternion rotation = Quaternion.Euler(angles.y, -angles.x, 0f);

            previewUtility.BeginPreview(rect, "PreBackground");
            previewUtility.camera.transform.SetPositionAndRotation(new Vector3(0f, 0f, -3f),
                Quaternion.identity);
            previewUtility.camera.nearClipPlane = 0.1f;
            previewUtility.camera.farClipPlane = 10f;
            previewUtility.lights[0].intensity = 1.2f;
            previewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            previewUtility.lights[1].intensity = 0.6f;
            previewUtility.ambientColor = new Color(0.2f, 0.2f, 0.2f);

            previewUtility.DrawMesh(sphereMesh,
                Matrix4x4.TRS(Vector3.zero, rotation, Vector3.one * 2.2f), material, 0);
            previewUtility.camera.Render();
            GUI.DrawTexture(rect, previewUtility.EndPreview(), ScaleMode.StretchToFill, false);
        }

        void EnsurePreviewResources()
        {
            previewUtility ??= new PreviewRenderUtility();
            if (sphereMesh == null)
            {
                // The editor-only way to a clean unit sphere mesh without leaking a scene object.
                GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(temp);
            }
        }

        Material GetPreviewMaterial(int index, SerializedProperty layer)
        {
            if (!previewMaterials.TryGetValue(index, out Material material) || material == null)
            {
                material = new Material(PreviewShader()) { hideFlags = HideFlags.HideAndDontSave };
                previewMaterials[index] = material;
            }

            // Cheap enough to refresh every repaint — the preview always matches the fields.
            var albedo = layer.FindPropertyRelative("albedo").objectReferenceValue as Texture2D;
            Color tint = layer.FindPropertyRelative("tint").colorValue;
            float smoothness = layer.FindPropertyRelative("smoothness").floatValue;
            if (material.HasProperty(BaseMapId)) material.SetTexture(BaseMapId, albedo);
            if (material.HasProperty(MainTexId)) material.SetTexture(MainTexId, albedo);
            if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, tint);
            if (material.HasProperty(ColorId)) material.SetColor(ColorId, tint);
            if (material.HasProperty(SmoothnessId)) material.SetFloat(SmoothnessId, smoothness);
            if (material.HasProperty(GlossinessId)) material.SetFloat(GlossinessId, smoothness);
            return material;
        }

        /// <summary>The active pipeline's default lit shader, so previews match the project.</summary>
        static Shader PreviewShader()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Shader shader = pipeline != null ? pipeline.defaultShader : null;
            return shader != null ? shader : Shader.Find("Standard");
        }
    }
}
