using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace reromanlee.Transvoxel.Editor
{
    /// <summary>
    /// UI Toolkit editor window for <see cref="TransvoxelMaterialPalette"/>: a reorderable
    /// layer list (index = voxel material id) next to a large drag-to-rotate preview sphere
    /// and the selected layer's fields. Open it from Window ▸ Transvoxel ▸ Material Palette,
    /// the palette inspector's button, or by double-clicking a palette asset.
    ///
    /// Everything binds through the palette's SerializedObject, so edits here support undo
    /// and fire the palette's OnValidate — a running terrain re-bakes and re-binds live.
    /// </summary>
    public sealed class TransvoxelPaletteWindow : EditorWindow
    {
        const int PreviewSize = 280;

        // Layer fields shown in the detail pane, in display order — keep in sync with
        // TransvoxelMaterialPalette.Layer.
        static readonly string[] LayerFieldPaths =
        {
            "name", "albedo", "tint", "smoothness", "normal", "normalStrength",
            "occlusion", "occlusionStrength", "height", "uvScaleMultiplier",
        };

        [SerializeField] TransvoxelMaterialPalette palette; // survives domain reloads

        SerializedObject serializedPalette;
        TransvoxelLayerPreview preview;

        ListView layerList;
        VisualElement detailPane;
        Label detailHeader;
        Image previewImage;
        PropertyField[] detailFields;

        // Per-layer preview rotation, and the pointer drag state of the preview image.
        readonly System.Collections.Generic.Dictionary<int, Vector2> previewAngles =
            new System.Collections.Generic.Dictionary<int, Vector2>();
        bool draggingPreview;

        [MenuItem("Window/Transvoxel/Material Palette")]
        static void OpenFromMenu() => Open(Selection.activeObject as TransvoxelMaterialPalette);

        public static void Open(TransvoxelMaterialPalette target)
        {
            var window = GetWindow<TransvoxelPaletteWindow>("Material Palette");
            if (target != null)
                window.SetPalette(target);
            window.Show();
        }

        // The EntityId signature is the Unity 6.2+ form of this callback (the int form
        // relies on APIs that are obsolete-as-error in 6.5).
        [UnityEditor.Callbacks.OnOpenAsset]
        static bool OnOpenAsset(EntityId entityId, int line)
        {
            if (EditorUtility.EntityIdToObject(entityId) is not TransvoxelMaterialPalette target)
                return false;
            Open(target);
            return true;
        }

        void OnDisable()
        {
            preview?.Dispose();
            preview = null;
        }

        void SetPalette(TransvoxelMaterialPalette target)
        {
            palette = target;
            if (rootVisualElement.childCount > 0)
                BuildContent();
        }

        // ------------------------------------------------------------------ layout

        public void CreateGUI()
        {
            preview ??= new TransvoxelLayerPreview();

            var toolbar = new Toolbar();
            var picker = new ObjectField
            {
                objectType = typeof(TransvoxelMaterialPalette),
                allowSceneObjects = false,
                value = palette,
                style = { flexGrow = 1f },
            };
            picker.RegisterValueChangedCallback(e => SetPalette(e.newValue as TransvoxelMaterialPalette));
            toolbar.Add(picker);
            rootVisualElement.Add(toolbar);

            rootVisualElement.Add(new VisualElement { name = "content", style = { flexGrow = 1f } });
            BuildContent();
        }

        void BuildContent()
        {
            VisualElement content = rootVisualElement.Q("content");
            content.Clear();
            var picker = rootVisualElement.Q<ObjectField>();
            if (picker != null)
                picker.SetValueWithoutNotify(palette);

            if (palette == null)
            {
                content.Add(new Label("Select a Material Palette asset, or create one via\n" +
                                      "Assets ▸ Create ▸ Transvoxel ▸ Material Palette.")
                {
                    style =
                    {
                        flexGrow = 1f, unityTextAlign = TextAnchor.MiddleCenter,
                        opacity = 0.6f, whiteSpace = WhiteSpace.Normal,
                    },
                });
                return;
            }

            serializedPalette = new SerializedObject(palette);
            var split = new TwoPaneSplitView(0, 240f, TwoPaneSplitViewOrientation.Horizontal);
            split.Add(BuildLayerListPane());
            split.Add(BuildDetailPane());
            content.Add(split);

            // Any palette change (fields, undo, add/remove/reorder): refresh the id badges,
            // the footer button states and the live preview.
            content.TrackSerializedObjectValue(serializedPalette, _ =>
            {
                layerList.RefreshItems();
                UpdateFooterButtons();
                RebindDetail();
            });

            // Select the default layer once the list has bound.
            rootVisualElement.schedule.Execute(() =>
            {
                if (layerList.selectedIndex < 0 && LayerCount > 0)
                    layerList.selectedIndex = 0;
                UpdateFooterButtons();
            });
        }

        VisualElement BuildLayerListPane()
        {
            var pane = new VisualElement { style = { minWidth = 180f } };

            layerList = new ListView
            {
                reorderable = true,
                reorderMode = ListViewReorderMode.Animated,
                showAddRemoveFooter = true,
                showBorder = true,
                showBoundCollectionSize = false,
                selectionType = SelectionType.Single,
                fixedItemHeight = 30f,
                makeItem = MakeLayerRow,
                bindItem = BindLayerRow,
                unbindItem = (row, _) => row.Unbind(),
                style = { flexGrow = 1f },
            };
            layerList.BindProperty(serializedPalette.FindProperty("layers"));
            layerList.selectionChanged += _ => RebindDetail();
            layerList.itemIndexChanged += (_, _) => layerList.RefreshItems();
            pane.Add(layerList);

            // Palette-wide (not per layer): how strongly the layers' height maps steer
            // the material transitions.
            var heightBlendField = new PropertyField
            {
                style = { marginLeft = 4f, marginRight = 4f, marginTop = 4f },
            };
            heightBlendField.BindProperty(serializedPalette.FindProperty("heightBlend"));
            pane.Add(heightBlendField);

            pane.Add(new Label("The list index is the material id stored in voxels —\n" +
                               "reordering re-labels already-painted terrain.")
            {
                style =
                {
                    opacity = 0.55f, fontSize = 10f, whiteSpace = WhiteSpace.Normal,
                    marginLeft = 4f, marginRight = 4f, marginTop = 4f, marginBottom = 6f,
                },
            });
            return pane;
        }

        static VisualElement MakeLayerRow()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 4f },
            };
            row.Add(new Label
            {
                name = "id",
                style = { width = 38f, unityFontStyleAndWeight = FontStyle.Bold, opacity = 0.7f },
            });
            var swatch = new VisualElement
            {
                name = "swatch",
                style =
                {
                    width = 14f, height = 14f, marginRight = 6f,
                    borderTopLeftRadius = 3f, borderTopRightRadius = 3f,
                    borderBottomLeftRadius = 3f, borderBottomRightRadius = 3f,
                },
            };
            row.Add(swatch);
            row.Add(new Label { name = "name", style = { flexGrow = 1f } });
            return row;
        }

        void BindLayerRow(VisualElement row, int index)
        {
            SerializedProperty layer = LayersProperty.GetArrayElementAtIndex(index);
            row.Q<Label>("id").text = $"ID {index}";

            var nameLabel = row.Q<Label>("name");
            SerializedProperty nameProperty = layer.FindPropertyRelative("name");
            nameLabel.text = nameProperty.stringValue;
            nameLabel.TrackPropertyValue(nameProperty, p => nameLabel.text = p.stringValue);

            VisualElement swatch = row.Q("swatch");
            SerializedProperty tintProperty = layer.FindPropertyRelative("tint");
            swatch.style.backgroundColor = tintProperty.colorValue;
            swatch.TrackPropertyValue(tintProperty, p => swatch.style.backgroundColor = p.colorValue);
        }

        VisualElement BuildDetailPane()
        {
            detailPane = new VisualElement
            {
                style = { paddingLeft = 10f, paddingRight = 10f, paddingTop = 8f, flexGrow = 1f },
            };

            detailHeader = new Label
            {
                style = { fontSize = 14f, unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6f },
            };
            detailPane.Add(detailHeader);

            previewImage = new Image
            {
                scaleMode = ScaleMode.ScaleToFit,
                style =
                {
                    width = PreviewSize, height = PreviewSize, alignSelf = Align.Center,
                    marginBottom = 2f,
                },
            };
            RegisterPreviewRotation(previewImage);
            detailPane.Add(previewImage);
            detailPane.Add(new Label("drag to rotate")
            {
                style =
                {
                    alignSelf = Align.Center, opacity = 0.45f, fontSize = 10f, marginBottom = 8f,
                },
            });

            // The field list outgrew small windows — scroll it under the pinned preview.
            var fieldScroll = new ScrollView { style = { flexGrow = 1f } };
            detailFields = new PropertyField[LayerFieldPaths.Length];
            for (int i = 0; i < detailFields.Length; i++)
            {
                detailFields[i] = new PropertyField();
                fieldScroll.Add(detailFields[i]);
            }
            detailPane.Add(fieldScroll);

            return detailPane;
        }

        // ------------------------------------------------------------------ selection & preview

        SerializedProperty LayersProperty => serializedPalette.FindProperty("layers");

        int LayerCount => palette != null ? LayersProperty.arraySize : 0;

        int SelectedLayer => layerList != null
            ? Mathf.Min(layerList.selectedIndex, LayerCount - 1)
            : -1;

        void RebindDetail()
        {
            int index = SelectedLayer;
            bool hasSelection = index >= 0;
            detailPane.visible = LayerCount > 0;
            if (!hasSelection)
                return;

            SerializedProperty layer = LayersProperty.GetArrayElementAtIndex(index);
            detailHeader.text = $"ID {index} — {layer.FindPropertyRelative("name").stringValue}";
            for (int i = 0; i < LayerFieldPaths.Length; i++)
                detailFields[i].BindProperty(layer.FindPropertyRelative(LayerFieldPaths[i]));
            RefreshPreview();
        }

        void RefreshPreview()
        {
            int index = SelectedLayer;
            // Bindings apply to the palette object before TrackSerializedObjectValue fires,
            // so reading the live layer here always sees the current values.
            if (index < 0 || preview == null || palette == null || index >= palette.Layers.Count)
                return;

            previewAngles.TryGetValue(index, out Vector2 angles);
            previewImage.image = preview.Render(palette.Layers[index], angles,
                PreviewSize, PreviewSize);
            previewImage.MarkDirtyRepaint();
        }

        void RegisterPreviewRotation(Image image)
        {
            image.RegisterCallback<PointerDownEvent>(e =>
            {
                draggingPreview = true;
                image.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            image.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!draggingPreview || SelectedLayer < 0)
                    return;
                previewAngles.TryGetValue(SelectedLayer, out Vector2 angles);
                angles += (Vector2)e.deltaPosition;
                angles.y = Mathf.Clamp(angles.y, -89f, 89f);
                previewAngles[SelectedLayer] = angles;
                RefreshPreview();
            });
            image.RegisterCallback<PointerUpEvent>(e =>
            {
                draggingPreview = false;
                image.ReleasePointer(e.pointerId);
            });
        }

        void UpdateFooterButtons()
        {
            // The bound footer edits the serialized list directly; gate it to the palette's
            // invariants (at least layer 0, at most MaxLayers).
            layerList.Q<Button>("unity-list-view__add-button")
                ?.SetEnabled(LayerCount < TransvoxelMaterialPalette.MaxLayers);
            layerList.Q<Button>("unity-list-view__remove-button")
                ?.SetEnabled(LayerCount > 1);
        }
    }
}
