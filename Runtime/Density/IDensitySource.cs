namespace reromanlee.Transvoxel.Density
{
    /// <summary>
    /// A density field the terrain is extracted from.
    ///
    /// Coordinates are integer positions on the LOD0 voxel lattice (world position =
    /// origin + coord * voxelSize). Using the integer lattice instead of free world-space
    /// floats guarantees that every consumer — chunks of any LOD, transition cells,
    /// terraforming — samples bit-identical values at shared positions, which is what
    /// keeps chunk borders seamless. Coarser LODs simply sample every 2^lod-th lattice
    /// point; nothing in between is ever read (see Concept.txt #5).
    ///
    /// The returned density is in [0, 1]: values above the iso level (0.5 by default)
    /// are solid ground, values below are air.
    ///
    /// Implementations must be thread-safe: chunk meshing calls this from worker threads.
    /// </summary>
    public interface IDensitySource
    {
        float SampleVoxel(int x, int y, int z);
    }
}
