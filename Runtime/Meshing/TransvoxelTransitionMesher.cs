using System;
using reromanlee.Transvoxel.Density;
using UnityEngine;

namespace reromanlee.Transvoxel.Meshing
{
    /// <summary>
    /// Generates the transition mesh for one face of a chunk that borders finer neighbours
    /// (Lengyel Sections 4.3–4.5). Transition cells are a one-cell-thick sheet owned by the
    /// coarser chunk: their full-resolution face lies exactly on the chunk boundary and
    /// matches the finer neighbour's lattice bit-for-bit, while their half-resolution face
    /// vertices run through the same secondary-position shift as the chunk's regular
    /// boundary vertices, landing exactly on the shrunk regular mesh. Together the three
    /// meshes close every crack without either neighbour knowing about the other's data.
    ///
    /// Not thread-safe across concurrent calls — use one instance per worker.
    /// </summary>
    public sealed class TransvoxelTransitionMesher
    {
        // Case-index contribution of each full-res sample location (paper Figure 4.17).
        static readonly int[] CaseWeight = { 0x01, 0x02, 0x04, 0x80, 0x100, 0x08, 0x40, 0x20, 0x10 };

        // (u, v) offsets of the 13 sample locations inside one transition cell, in
        // fine-lattice units (Figure 4.16): 0..8 row-major on the full-res face,
        // 9/A/B/C the corners of the half-res face (same sample values as 0/2/6/8).
        static readonly int[] LocU = { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 2, 0, 2 };
        static readonly int[] LocV = { 0, 0, 0, 1, 1, 1, 2, 2, 2, 0, 0, 2, 2 };

        const int ReuseSlots = 10;

        int[] currentRow = Array.Empty<int>();
        int[] previousRow = Array.Empty<int>();
        int rowCells;

        Vector3[] fineGradientCache = Array.Empty<Vector3>();
        bool[] fineGradientValid = Array.Empty<bool>();

        readonly int[] cellVertexIndices = new int[12];
        readonly float[] locDistance = new float[13];

        ChunkSamples samples;
        MeshBuffers output;
        IDensitySource source;
        float voxelSize;
        float uvScale;
        float[] sheet;
        int sheetPoints;   // fine lattice points per axis: 2·cells + 1
        int fineStep;

        // Face basis: world axes for the face-local u/v directions and the winding parity
        // of that basis (whether u × v points against the outward face normal).
        int axisN, axisU, axisV;
        int planeLocal;
        bool basisFlipsWinding;
        int cellsPerRow;

        public void GenerateTransitionMesh(ChunkSamples chunkSamples, CubeFace face, IDensitySource densitySource,
            float voxelSizeMeters, float uvScaleFactor, MeshBuffers meshOutput)
        {
            sheet = chunkSamples.FaceSheets[(int)face];
            if (sheet == null || chunkSamples.Key.Lod == 0)
                return;

            samples = chunkSamples;
            source = densitySource;
            output = meshOutput;
            voxelSize = voxelSizeMeters;
            uvScale = uvScaleFactor;

            int n = samples.ChunkCells;
            cellsPerRow = n;
            sheetPoints = 2 * n + 1;
            fineStep = samples.LodStep >> 1;

            axisN = face.Axis();
            axisU = axisN == 0 ? 1 : 0;
            axisV = axisN == 2 ? 1 : 2;
            planeLocal = face.IsPositive() ? n * samples.LodStep : 0;
            // Lengyel's transition tables list each full-res-face triangle so that, taken in
            // (u, v) order, its cross product points AGAINST the face's outward normal. So we
            // must flip on exactly the faces where the fixed ascending-axis basis
            // (axisU x axisV) already points ALONG the outward normal, leaving every face
            // wound outward and consistent with the regular mesh. These are the three faces:
            //   PosX: (Y x Z)=+X = +normal   NegY: (X x Z)=-Y = -normal   PosZ: (X x Y)=+Z = +normal
            // Verified face-by-face by the headless LOD-seam watertightness test.
            basisFlipsWinding = face == CubeFace.PosX || face == CubeFace.NegY || face == CubeFace.PosZ;

            if (rowCells != n)
            {
                currentRow = new int[n * ReuseSlots];
                previousRow = new int[n * ReuseSlots];
                rowCells = n;
            }
            if (fineGradientCache.Length < sheetPoints * sheetPoints)
            {
                fineGradientCache = new Vector3[sheetPoints * sheetPoints];
                fineGradientValid = new bool[sheetPoints * sheetPoints];
            }
            Array.Clear(fineGradientValid, 0, sheetPoints * sheetPoints);

            for (int cy = 0; cy < n; cy++)
            {
                (currentRow, previousRow) = (previousRow, currentRow);
                Array.Fill(currentRow, -1);

                for (int cx = 0; cx < n; cx++)
                    ProcessTransitionCell(cx, cy);
            }

            samples = null;
            output = null;
            source = null;
            sheet = null;
        }

        void ProcessTransitionCell(int cx, int cy)
        {
            int caseCode = 0;
            for (int loc = 0; loc < 9; loc++)
            {
                float d = sheet[(2 * cx + LocU[loc]) + (2 * cy + LocV[loc]) * sheetPoints];
                locDistance[loc] = d;
                if (d < 0f) caseCode |= CaseWeight[loc];
            }
            if (caseCode == 0 || caseCode == 511)
                return;

            // The half-res corners carry the same sample values as the full-res corners.
            locDistance[9] = locDistance[0];
            locDistance[10] = locDistance[2];
            locDistance[11] = locDistance[6];
            locDistance[12] = locDistance[8];

            int classIndex = TransvoxelDataTables.transitionCellClass[caseCode];
            bool inverted = (classIndex & 0x80) != 0;
            var cellData = TransvoxelDataTables.transitionCellData[classIndex & 0x7F];
            int[] vertexData = TransvoxelDataTables.transitionVertexData[caseCode];

            int vertexCount = cellData.GetVertexCount();
            int triangleCount = cellData.GetTriangleCount();

            for (int v = 0; v < vertexCount; v++)
                cellVertexIndices[v] = ResolveVertex(cx, cy, vertexData[v]);

            bool flip = inverted ^ basisFlipsWinding;
            for (int t = 0; t < triangleCount; t++)
            {
                int i0 = cellVertexIndices[cellData.vertexIndex[t * 3]];
                int i1 = cellVertexIndices[cellData.vertexIndex[t * 3 + 1]];
                int i2 = cellVertexIndices[cellData.vertexIndex[t * 3 + 2]];
                if (i0 == i1 || i1 == i2 || i0 == i2)
                    continue;
                if (TransvoxelGeometry.IsDegenerate(output.Vertices[i0], output.Vertices[i1], output.Vertices[i2]))
                    continue;
                TransvoxelGeometry.EmitTriangle(output.Indices, i0, i1, i2, flip);
            }
        }

        int ResolveVertex(int cx, int cy, int code)
        {
            int l0 = (code >> 4) & 0x0F;
            int l1 = code & 0x0F;
            float d0 = locDistance[l0];
            float d1 = locDistance[l1];

            // Samples exactly on the isosurface put the vertex exactly on that sample
            // location; reuse is then routed through the corner codes of Figure 4.19.
            if (d1 == 0f)
                return ResolveOnLocation(cx, cy, l1, l0, l1, d0, d1);
            if (d0 == 0f)
                return ResolveOnLocation(cx, cy, l0, l0, l1, d0, d1);

            int direction = (code >> 12) & 0x0F;
            int slot = (code >> 8) & 0x0F;

            if ((direction & 8) != 0)
            {
                // Maximal edge owned by this cell: create and publish for reuse.
                int index = CreateVertex(cx, cy, l0, l1, d0, d1);
                currentRow[cx * ReuseSlots + slot] = index;
                return index;
            }

            if ((direction & 4) != 0)
            {
                // Interior edge (the spokes of the full-res face): never shared.
                return CreateVertex(cx, cy, l0, l1, d0, d1);
            }

            int reused = TryReuse(cx, cy, direction, slot);
            return reused >= 0 ? reused : CreateVertex(cx, cy, l0, l1, d0, d1);
        }

        int ResolveOnLocation(int cx, int cy, int location, int l0, int l1, float d0, float d1)
        {
            int cornerCode = TransvoxelDataTables.transitionCornerData[location];
            int direction = (cornerCode >> 4) & 0x0F;
            int slot = cornerCode & 0x0F;

            if ((direction & 4) != 0)
                return CreateVertex(cx, cy, l0, l1, d0, d1); // center location, never shared

            if ((direction & 8) != 0)
            {
                // Owned by this cell; several edges of the cell may converge on the same
                // corner, so check the slot before creating.
                int deckIndex = cx * ReuseSlots + slot;
                if (currentRow[deckIndex] >= 0)
                    return currentRow[deckIndex];
                int index = CreateVertex(cx, cy, l0, l1, d0, d1);
                currentRow[deckIndex] = index;
                return index;
            }

            int reused = TryReuse(cx, cy, direction, slot);
            return reused >= 0 ? reused : CreateVertex(cx, cy, l0, l1, d0, d1);
        }

        int TryReuse(int cx, int cy, int direction, int slot)
        {
            int dx = direction & 1, dy = (direction >> 1) & 1;
            cx -= dx;
            if (cx < 0 || cy - dy < 0)
                return -1;
            int[] row = dy != 0 ? previousRow : currentRow;
            return row[cx * ReuseSlots + slot];
        }

        int CreateVertex(int cx, int cy, int l0, int l1, float d0, float d1)
        {
            // Both endpoints of an active edge always lie on the same face of the cell:
            // lateral edges never cross the surface because their two endpoints carry
            // identical sample values.
            bool halfRes = l0 >= 9;

            Vector3 p0 = LocationPosition(cx, cy, l0);
            Vector3 p1 = LocationPosition(cx, cy, l1);
            float s = d0 / (d0 - d1);
            Vector3 position = p0 + s * (p1 - p0);

            Vector3 g0 = LocationGradient(cx, cy, l0);
            Vector3 g1 = LocationGradient(cx, cy, l1);
            Vector3 normal = (g0 + s * (g1 - g0)).normalized;
            if (normal == Vector3.zero)
                normal = Vector3.up;

            // Half-res face vertices are computed at their primary position on the chunk
            // boundary and then shifted inward exactly like the regular boundary vertices
            // they must coincide with (paper §4.4). Full-res face vertices never move.
            if (halfRes)
                position = TransvoxelGeometry.ApplySecondaryShift(position, normal, samples.TransitionMask,
                    samples.ChunkCells, samples.LodStep);

            int index = output.Vertices.Count;
            output.Vertices.Add(position * voxelSize);
            output.Normals.Add(normal);
            output.Uvs.Add(new Vector2(
                (samples.MinVoxel.x + position.x) * voxelSize * uvScale,
                (samples.MinVoxel.z + position.z) * voxelSize * uvScale));
            return index;
        }

        /// <summary>Chunk-local position of a sample location, in voxel units.</summary>
        Vector3 LocationPosition(int cx, int cy, int location)
        {
            int fu = 2 * cx + LocU[location];
            int fv = 2 * cy + LocV[location];
            var p = Vector3.zero;
            p[axisU] = fu * fineStep;
            p[axisV] = fv * fineStep;
            p[axisN] = planeLocal;
            return p;
        }

        Vector3 LocationGradient(int cx, int cy, int location)
        {
            int fu = 2 * cx + LocU[location];
            int fv = 2 * cy + LocV[location];

            if (location >= 9)
            {
                // Half-res corners must use the exact gradient the regular mesher computes
                // from the coarse grid, or the shift projection (and shading) would diverge
                // from the coincident regular vertex.
                var grid = Vector3Int.zero;
                grid[axisU] = 1 + fu / 2;
                grid[axisV] = 1 + fv / 2;
                grid[axisN] = 1 + (planeLocal == 0 ? 0 : samples.ChunkCells);
                return samples.GradientAt(grid.x, grid.y, grid.z);
            }

            // Full-res locations use central differences on the neighbour's fine lattice —
            // the same values the finer chunk derives from its own grid, for continuous
            // shading across the seam. Computed on demand and cached per sheet point.
            int cacheIndex = fu + fv * sheetPoints;
            if (fineGradientValid[cacheIndex])
                return fineGradientCache[cacheIndex];

            var voxel = Vector3Int.zero;
            voxel[axisU] = fu * fineStep;
            voxel[axisV] = fv * fineStep;
            voxel[axisN] = planeLocal;
            voxel += samples.MinVoxel;

            float iso = samples.IsoLevel;
            var gradient = new Vector3(
                ChunkSamples.SampleSigned(source, iso, voxel.x + fineStep, voxel.y, voxel.z)
                - ChunkSamples.SampleSigned(source, iso, voxel.x - fineStep, voxel.y, voxel.z),
                ChunkSamples.SampleSigned(source, iso, voxel.x, voxel.y + fineStep, voxel.z)
                - ChunkSamples.SampleSigned(source, iso, voxel.x, voxel.y - fineStep, voxel.z),
                ChunkSamples.SampleSigned(source, iso, voxel.x, voxel.y, voxel.z + fineStep)
                - ChunkSamples.SampleSigned(source, iso, voxel.x, voxel.y, voxel.z - fineStep));

            fineGradientCache[cacheIndex] = gradient;
            fineGradientValid[cacheIndex] = true;
            return gradient;
        }
    }
}
