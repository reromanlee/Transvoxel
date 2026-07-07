using System;
using System.Collections.Generic;
using UnityEngine;

namespace reromanlee.Transvoxel.Meshing
{
    /// <summary>
    /// Generates the primary mesh of one chunk with the modified Marching Cubes algorithm
    /// of Lengyel's paper, Sections 3.2–3.3: table-driven triangulation, shared vertices
    /// via the two-deck reuse scheme, gradient normals, and (for chunks that border finer
    /// neighbours) the secondary-position shift that makes room for transition cells.
    ///
    /// Not thread-safe across concurrent calls — use one instance per worker. All
    /// positions are computed in chunk-local voxel units and scaled to meters on output.
    /// </summary>
    public sealed class TransvoxelMesher
    {
        // Reusable vertex indices for the current and preceding deck of cells (paper §3.3).
        // Four slots per cell: slot 0 holds a vertex placed exactly on the cell's maximal
        // corner, slots 1..3 hold vertices on the three maximal edges.
        int[] currentDeck = Array.Empty<int>();
        int[] previousDeck = Array.Empty<int>();
        int deckCells;

        readonly int[] cellVertexIndices = new int[12];
        readonly float[] cornerDistance = new float[8];

        ChunkSamples samples;
        MeshBuffers output;
        float voxelSize;
        float uvScale;

        public void GenerateRegularMesh(ChunkSamples chunkSamples, float voxelSizeMeters, float uvScaleFactor,
            MeshBuffers meshOutput)
        {
            samples = chunkSamples;
            output = meshOutput;
            voxelSize = voxelSizeMeters;
            uvScale = uvScaleFactor;

            int n = samples.ChunkCells;
            if (deckCells != n)
            {
                currentDeck = new int[n * n * 4];
                previousDeck = new int[n * n * 4];
                deckCells = n;
            }

            for (int z = 0; z < n; z++)
            {
                (currentDeck, previousDeck) = (previousDeck, currentDeck);
                Array.Fill(currentDeck, -1);

                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    ProcessCell(x, y, z);
            }

            samples = null;
            output = null;
        }

        void ProcessCell(int x, int y, int z)
        {
            // Case index from the sign bits of the eight corners (paper Listing 3.1):
            // bit c set when corner c is inside solid space (negative distance).
            int caseCode = 0;
            for (int c = 0; c < 8; c++)
            {
                float d = samples.MainAt(x + 1 + (c & 1), y + 1 + ((c >> 1) & 1), z + 1 + ((c >> 2) & 1));
                cornerDistance[c] = d;
                if (d < 0f) caseCode |= 1 << c;
            }

            if (caseCode == 0 || caseCode == 255)
                return; // trivial: fully outside or fully inside

            var cellData = TransvoxelDataTables.regularCellData[TransvoxelDataTables.regularCellClass[caseCode]];
            int[] vertexData = TransvoxelDataTables.regularVertexData[caseCode];
            int vertexCount = cellData.GetVertexCount();
            int triangleCount = cellData.GetTriangleCount();

            for (int v = 0; v < vertexCount; v++)
                cellVertexIndices[v] = ResolveVertex(x, y, z, vertexData[v]);

            for (int t = 0; t < triangleCount; t++)
            {
                int i0 = cellVertexIndices[cellData.vertexIndex[t * 3]];
                int i1 = cellVertexIndices[cellData.vertexIndex[t * 3 + 1]];
                int i2 = cellVertexIndices[cellData.vertexIndex[t * 3 + 2]];
                if (i0 == i1 || i1 == i2 || i0 == i2)
                    continue;
                if (TransvoxelGeometry.IsDegenerate(output.Vertices[i0], output.Vertices[i1], output.Vertices[i2]))
                    continue;
                TransvoxelGeometry.EmitTriangle(output.Indices, i0, i1, i2, flip: false);
            }
        }

        /// <summary>
        /// Returns the output-buffer index for one vertex of the cell, reusing a vertex
        /// created by a preceding cell whenever the 16-bit table code allows it.
        /// </summary>
        int ResolveVertex(int x, int y, int z, int code)
        {
            int v0 = (code >> 4) & 0x0F; // lower-numbered edge endpoint
            int v1 = code & 0x0F;        // higher-numbered edge endpoint
            float d0 = cornerDistance[v0];
            float d1 = cornerDistance[v1];

            // A sample of exactly zero puts the vertex exactly on a corner; such vertices
            // are shared through corner slot 0 of the cell owning that corner (paper §3.3).
            if (d1 == 0f)
                return ResolveCornerVertex(x, y, z, v1, v0, v1, d0, d1);
            if (d0 == 0f)
                return ResolveCornerVertex(x, y, z, v0, v0, v1, d0, d1);

            // Vertex in the interior of the edge: the code's high byte names the cell and
            // slot the vertex can be reused from (Figure 3.9).
            int direction = (code >> 12) & 0x0F;
            int slot = (code >> 8) & 0x0F;

            if ((direction & 8) != 0)
            {
                // This cell owns the edge: create the vertex and publish it for reuse.
                int index = CreateVertex(x, y, z, v0, v1, d0, d1);
                currentDeck[(y * deckCells + x) * 4 + slot] = index;
                return index;
            }

            int reused = TryReuse(x, y, z, direction, slot);
            return reused >= 0 ? reused : CreateVertex(x, y, z, v0, v1, d0, d1);
        }

        int ResolveCornerVertex(int x, int y, int z, int corner, int v0, int v1, float d0, float d1)
        {
            int direction = corner ^ 7; // walk towards the cell having this corner as its maximal one

            if (direction == 0)
            {
                // This cell's own maximal corner: create once, then reuse.
                int deckIndex = (y * deckCells + x) * 4;
                if (currentDeck[deckIndex] >= 0)
                    return currentDeck[deckIndex];
                int index = CreateVertex(x, y, z, v0, v1, d0, d1);
                currentDeck[deckIndex] = index;
                return index;
            }

            int reused = TryReuse(x, y, z, direction, 0);
            return reused >= 0 ? reused : CreateVertex(x, y, z, v0, v1, d0, d1);
        }

        /// <summary>
        /// Looks up a previously created vertex in the deck the direction code points to.
        /// Returns -1 when the preceding cell is outside the chunk (first row/column/deck —
        /// the paper then permits creating a duplicate vertex, which lands on the exact
        /// same position, so the mesh stays watertight) or when the preceding cell placed
        /// no vertex there (its crossing degenerated to a corner).
        /// </summary>
        int TryReuse(int x, int y, int z, int direction, int slot)
        {
            int dx = direction & 1, dy = (direction >> 1) & 1, dz = (direction >> 2) & 1;
            x -= dx;
            y -= dy;
            if (x < 0 || y < 0 || z - dz < 0)
                return -1;
            int[] deck = dz != 0 ? previousDeck : currentDeck;
            return deck[(y * deckCells + x) * 4 + slot];
        }

        int CreateVertex(int x, int y, int z, int v0, int v1, float d0, float d1)
        {
            int step = samples.LodStep;

            var p0 = new Vector3((x + (v0 & 1)) * step, (y + ((v0 >> 1) & 1)) * step, (z + ((v0 >> 2) & 1)) * step);
            var p1 = new Vector3((x + (v1 & 1)) * step, (y + ((v1 >> 1) & 1)) * step, (z + ((v1 >> 2) & 1)) * step);

            // Fraction of the crossing along the edge, always measured from the
            // lower-numbered endpoint (paper Eq. 3.4) so that every cell — in this chunk
            // or a neighbouring one — computes bit-identical positions for a shared edge.
            float s = d0 / (d0 - d1);
            Vector3 position = p0 + s * (p1 - p0);

            Vector3 g0 = samples.GradientAt(x + 1 + (v0 & 1), y + 1 + ((v0 >> 1) & 1), z + 1 + ((v0 >> 2) & 1));
            Vector3 g1 = samples.GradientAt(x + 1 + (v1 & 1), y + 1 + ((v1 >> 1) & 1), z + 1 + ((v1 >> 2) & 1));
            Vector3 normal = (g0 + s * (g1 - g0)).normalized;
            if (normal == Vector3.zero)
                normal = Vector3.up;

            position = TransvoxelGeometry.ApplySecondaryShift(position, normal, samples.TransitionMask,
                samples.ChunkCells, step);

            return EmitVertex(position, normal);
        }

        int EmitVertex(Vector3 positionVoxels, Vector3 normal)
        {
            int index = output.Vertices.Count;
            output.Vertices.Add(positionVoxels * voxelSize);
            output.Normals.Add(normal);
            output.Uvs.Add(new Vector2(
                (samples.MinVoxel.x + positionVoxels.x) * voxelSize * uvScale,
                (samples.MinVoxel.z + positionVoxels.z) * voxelSize * uvScale));
            return index;
        }
    }
}
