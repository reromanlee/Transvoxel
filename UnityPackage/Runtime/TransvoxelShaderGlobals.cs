using UnityEngine;

namespace reromanlee.Transvoxel
{
    /// <summary>
    /// Boots the fade shader globals to their neutral values at editor load and player
    /// startup. Unity's global floats default to 0, and a master fade of 0 means "fully
    /// dithered away" — without this, every fade-aware material (the bundled shader, or
    /// any Shader Graph using the TransvoxelDitherFade node) renders invisible in scene
    /// view, previews and player scenes until the first <see cref="TransvoxelTerrain"/>
    /// pushes its per-frame globals. A running terrain overwrites both values every frame;
    /// resetting the edge band here also clears stale edge-dissolve state left behind by a
    /// stopped Play session.
    /// </summary>
    static class TransvoxelShaderGlobals
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        static void SetNeutralDefaults()
        {
            Shader.SetGlobalFloat("_TransvoxelFade", 1f);
            Shader.SetGlobalFloat("_TransvoxelEdgeFadeBand", 0f); // 0 = edge dissolve off
        }
    }
}
