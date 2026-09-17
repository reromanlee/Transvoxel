using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace reromanlee.Transvoxel.Editor
{
    /// <summary>
    /// Compact inspector for <see cref="TransvoxelMaterialPalette"/>: the plain layer list
    /// for quick edits, plus a button into <see cref="TransvoxelPaletteWindow"/> — the full
    /// editing experience with the rotatable preview spheres (also opened by double-clicking
    /// the asset).
    /// </summary>
    [CustomEditor(typeof(TransvoxelMaterialPalette))]
    public sealed class TransvoxelMaterialPaletteEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            root.Add(new HelpBox(
                "The list index is the material id stored in voxels: layer 0 fills the " +
                "whole world by default, terraforming assigns the other ids. Reordering " +
                "re-labels already-painted terrain.", HelpBoxMessageType.Info));

            var openButton = new Button(() =>
                TransvoxelPaletteWindow.Open((TransvoxelMaterialPalette)target))
            {
                text = "Open Palette Editor",
                style = { height = 28f, marginTop = 4f, marginBottom = 6f },
            };
            root.Add(openButton);

            // PropertyFields bind themselves to the inspector's SerializedObject, so edits
            // support undo and fire the palette's OnValidate — a running terrain re-bakes
            // its texture arrays and re-binds the shader uniforms live.
            root.Add(new PropertyField(serializedObject.FindProperty("heightBlend")));
            root.Add(new PropertyField(serializedObject.FindProperty("layers")));

            return root;
        }
    }
}
