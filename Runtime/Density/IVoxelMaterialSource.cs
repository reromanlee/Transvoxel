namespace reromanlee.Transvoxel.Density
{
    /// <summary>
    /// The material field the terrain surface is painted from: every voxel on the LOD0
    /// lattice carries one material id — an index into the terrain's
    /// <see cref="TransvoxelMaterialPalette"/>. Id 0 is the default material that fills
    /// the whole world unless something assigns a different id (terraforming with a
    /// selected material, custom painting code).
    ///
    /// Coordinates follow the same integer-lattice contract as <see cref="IDensitySource"/>,
    /// so every consumer — chunks of any LOD, transition cells — reads bit-identical ids at
    /// shared positions and material seams line up across chunk borders.
    ///
    /// Implementations must be thread-safe: chunk meshing calls this from worker threads.
    /// </summary>
    public interface IVoxelMaterialSource
    {
        byte SampleMaterial(int x, int y, int z);
    }
}
