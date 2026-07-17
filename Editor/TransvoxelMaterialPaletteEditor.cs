using UnityEditor;
using UnityEngine;

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
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "The list index is the material id stored in voxels: layer 0 fills the " +
                "whole world by default, terraforming assigns the other ids. Reordering " +
                "re-labels already-painted terrain.", MessageType.Info);

            if (GUILayout.Button("Open Palette Editor", GUILayout.Height(28f)))
                TransvoxelPaletteWindow.Open((TransvoxelMaterialPalette)target);
            EditorGUILayout.Space(4f);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("heightBlend"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("layers"), true);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
