// EditMode correctness tests for the Transvoxel implementation.
//
// The central instrument is watertightness: for a closed density field (a sphere), the union
// of all generated chunk meshes must form a closed, consistently oriented 2-manifold — every
// directed edge appears exactly once and its reverse exactly once. That single property
// catches almost every possible bug: wrong tables, broken vertex reuse, mismatched
// interpolation across chunks, wrong transition-cell layout, bad winding, incorrect
// secondary-position shifts, and stale-mask cache reuse.
//
// Run them from Window ▸ General ▸ Test Runner ▸ EditMode.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using reromanlee.Transvoxel;
using reromanlee.Transvoxel.Density;
using reromanlee.Transvoxel.Meshing;
using reromanlee.Transvoxel.Octree;
using UnityEngine;

namespace reromanlee.Transvoxel.Editor.Tests
{
    public sealed class TransvoxelTests
    {
        // ------------------------------------------------------------------ density fields

        sealed class SphereDensity : IDensitySource
        {
            public Vector3 Center;
            public float Radius;
            public float Blend = 3f;

            public float SampleVoxel(int x, int y, int z)
            {
                float d = Vector3.Distance(new Vector3(x, y, z), Center);
                return Mathf.Clamp01(0.5f + (Radius - d) / (2f * Blend));
            }
        }

        sealed class FlatGround : IDensitySource
        {
            public float SampleVoxel(int x, int y, int z) => Mathf.Clamp01(0.5f - y / 8f);
        }

        // ------------------------------------------------------------------ mesh collection

        /// <summary>
        /// Welds many chunk meshes into one indexed mesh by quantized position, so shared
        /// vertices across chunk borders collapse to one and watertightness can be measured.
        /// </summary>
        sealed class MeshUnion
        {
            // Positions quantized to 2^-14 voxels: far above float noise (~1e-6), far below
            // any legitimate vertex separation.
            const float Quantum = 16384f;

            readonly Dictionary<(long, long, long), int> vertexIds = new Dictionary<(long, long, long), int>();
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<(int a, int b, int c)> Triangles = new List<(int, int, int)>();
            public int QuantizeCollisions;

            public void Add(MeshBuffers buffers, Vector3 worldOffset)
            {
                var map = new int[buffers.Vertices.Count];
                for (int i = 0; i < buffers.Vertices.Count; i++)
                {
                    Vector3 w = worldOffset + buffers.Vertices[i];
                    var key = ((long)Math.Round(w.x * Quantum), (long)Math.Round(w.y * Quantum), (long)Math.Round(w.z * Quantum));
                    if (!vertexIds.TryGetValue(key, out int id))
                    {
                        id = Positions.Count;
                        vertexIds.Add(key, id);
                        Positions.Add(w);
                    }
                    map[i] = id;
                }
                for (int t = 0; t < buffers.Indices.Count; t += 3)
                {
                    int a = map[buffers.Indices[t]], b = map[buffers.Indices[t + 1]], c = map[buffers.Indices[t + 2]];
                    if (a == b || b == c || a == c) { QuantizeCollisions++; continue; }
                    Triangles.Add((a, b, c));
                }
            }

            /// <summary>Directed edges that appear a number of times != 1 (holes / overlaps / flips).</summary>
            public int UnmatchedEdges(out int total)
            {
                var count = new Dictionary<(int, int), int>();
                foreach (var (a, b, c) in Triangles)
                {
                    Bump(count, a, b);
                    Bump(count, b, c);
                    Bump(count, c, a);
                }
                total = count.Count;
                int bad = 0;
                foreach (var entry in count)
                {
                    if (entry.Value != 1) bad++;
                    else if (!count.ContainsKey((entry.Key.Item2, entry.Key.Item1))) bad++;
                }
                return bad;
            }

            static void Bump(Dictionary<(int, int), int> map, int a, int b)
            {
                map.TryGetValue((a, b), out int n);
                map[(a, b)] = n + 1;
            }

            /// <summary>
            /// Six times the signed enclosed volume. Positive when triangles are wound for
            /// Unity front faces pointing outward (Cross(v1-v0, v2-v0) away from the interior).
            /// </summary>
            public double SignedVolume6()
            {
                double sum = 0;
                foreach (var (a, b, c) in Triangles)
                {
                    Vector3 v0 = Positions[a], v1 = Positions[b], v2 = Positions[c];
                    sum += (double)v0.x * ((double)v1.y * v2.z - (double)v1.z * v2.y)
                         - (double)v0.y * ((double)v1.x * v2.z - (double)v1.z * v2.x)
                         + (double)v0.z * ((double)v1.x * v2.y - (double)v1.y * v2.x);
                }
                return sum;
            }
        }

        static MeshBuffers BuildChunk(IDensitySource source, NodeKey key, byte mask)
        {
            const int cells = 16;
            var samples = ChunkSamples.Sample(source, key, cells, 0.5f, mask);
            return BuildFrom(samples, source, mask);
        }

        static MeshBuffers BuildChunkCached(IDensitySource source, NodeKey key, byte mask,
            SampleCache cache, int cells, float iso)
        {
            var samples = cache.GetOrSample(source, key, cells, iso, mask);
            return BuildFrom(samples, source, mask);
        }

        static MeshBuffers BuildFrom(ChunkSamples samples, IDensitySource source, byte mask)
        {
            var buffers = new MeshBuffers();
            new TransvoxelMesher().GenerateRegularMesh(samples, 1f, 0.1f, buffers);
            var transition = new TransvoxelTransitionMesher();
            for (int f = 0; f < 6; f++)
                if ((mask & (1 << f)) != 0)
                    transition.GenerateTransitionMesh(samples, (CubeFace)f, source, 1f, 0.1f, buffers);
            return buffers;
        }

        static Vector3 WorldOffset(NodeKey key)
        {
            var m = key.MinVoxel(16);
            return new Vector3(m.x, m.y, m.z);
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void SphereSingleChunk_IsWatertightAndOutward()
        {
            var sphere = new SphereDensity { Center = new Vector3(8, 8, 8), Radius = 5.5f };
            var buffers = BuildChunk(sphere, new NodeKey(0, Vector3Int.zero), 0);

            var union = new MeshUnion();
            union.Add(buffers, Vector3.zero);

            Assert.Greater(union.Triangles.Count, 50, "sphere produced too few triangles");
            int bad = union.UnmatchedEdges(out int total);
            Assert.AreEqual(0, bad, $"not watertight ({bad}/{total} bad edges, {union.QuantizeCollisions} collisions)");

            double volume = union.SignedVolume6() / 6.0;
            double expected = 4.0 / 3.0 * Math.PI * Math.Pow(5.5, 3);
            Assert.Greater(volume, 0, "winding is inside-out for Unity (negative signed volume)");
            Assert.Less(Math.Abs(volume - expected) / expected, 0.15,
                $"volume {volume:0.0} not within 15% of sphere {expected:0.0}");

            for (int i = 0; i < buffers.Vertices.Count; i++)
            {
                Vector3 dir = (buffers.Vertices[i] - sphere.Center).normalized;
                Assert.Greater(Vector3.Dot(buffers.Normals[i], dir), 0.7f, "a smooth normal points inward");
            }
        }

        [Test]
        public void SphereAcrossEightChunks_SameLodBordersAreWatertight()
        {
            var sphere = new SphereDensity { Center = new Vector3(16, 16, 16), Radius = 9f };
            var union = new MeshUnion();
            for (int z = 0; z < 2; z++)
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                var key = new NodeKey(0, new Vector3Int(x, y, z));
                union.Add(BuildChunk(sphere, key, 0), WorldOffset(key));
            }

            int bad = union.UnmatchedEdges(out int total);
            Assert.AreEqual(0, bad, $"same-LOD borders leak ({bad}/{total} bad edges)");
            double volume = union.SignedVolume6() / 6.0;
            double expected = 4.0 / 3.0 * Math.PI * Math.Pow(9, 3);
            Assert.Less(Math.Abs(volume - expected) / expected, 0.1, "volume off");
        }

        /// <summary>
        /// One coarse (LOD1) chunk borders four fine (LOD0) chunks across the given face; a
        /// sphere sits centred on the shared plane. The union of the four fine meshes, the
        /// coarse mesh (shifted boundary) and the coarse transition mesh must be perfectly closed.
        /// </summary>
        [TestCase(CubeFace.NegX)]
        [TestCase(CubeFace.PosX)]
        [TestCase(CubeFace.NegY)]
        [TestCase(CubeFace.PosY)]
        [TestCase(CubeFace.NegZ)]
        [TestCase(CubeFace.PosZ)]
        public void TransitionSeam_IsWatertight(CubeFace face)
        {
            int axis = face.Axis();
            var coarseKey = new NodeKey(1, Vector3Int.zero);
            byte coarseMask = (byte)face.Bit();

            float plane = face.IsPositive() ? 32f : 0f;
            int fineAxisCoord = face.IsPositive() ? 2 : -1;

            var center = new Vector3(16, 16, 16);
            center[axis] = plane;
            var sphere = new SphereDensity { Center = center, Radius = 10f };

            var union = new MeshUnion();
            union.Add(BuildChunk(sphere, coarseKey, coarseMask), WorldOffset(coarseKey));

            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                var coord = Vector3Int.zero;
                coord[axis] = fineAxisCoord;
                coord[(axis + 1) % 3] = i;
                coord[(axis + 2) % 3] = j;
                var key = new NodeKey(0, coord);
                union.Add(BuildChunk(sphere, key, 0), WorldOffset(key));
            }

            int bad = union.UnmatchedEdges(out int total);
            Assert.AreEqual(0, bad, $"LOD seam leaks ({bad}/{total} bad edges)");
            double volume = union.SignedVolume6() / 6.0;
            double expected = 4.0 / 3.0 * Math.PI * 1000.0;
            Assert.Greater(volume, 0);
            Assert.Less(Math.Abs(volume - expected) / expected, 0.15, "volume off");
        }

        /// <summary>
        /// Regression for "seams appear when flying": a chunk cached while it had a transition
        /// face, whose neighbour then becomes the same LOD (mask drops that face). Re-meshing
        /// must follow the new mask (no boundary shift), not the stale cached one.
        /// </summary>
        [Test]
        public void StaleMaskCache_DoesNotCrackSameLodSeam()
        {
            const int cells = 16;
            const float iso = 0.5f;
            var sphere = new SphereDensity { Center = new Vector3(32, 16, 16), Radius = 10f };
            var cache = new SampleCache(64);

            var leftKey = new NodeKey(1, Vector3Int.zero);
            var rightKey = new NodeKey(1, new Vector3Int(1, 0, 0));

            // Warm the cache with a stale +X transition mask for the left chunk.
            cache.GetOrSample(sphere, leftKey, cells, iso, (byte)CubeFace.PosX.Bit());

            var refetched = cache.GetOrSample(sphere, leftKey, cells, iso, 0);
            Assert.AreEqual(0, refetched.TransitionMask, "cache did not return the exact requested mask");

            var union = new MeshUnion();
            union.Add(BuildChunkCached(sphere, leftKey, 0, cache, cells, iso), WorldOffset(leftKey));
            union.Add(BuildChunkCached(sphere, rightKey, 0, cache, cells, iso), WorldOffset(rightKey));

            int bad = union.UnmatchedEdges(out int total);
            Assert.AreEqual(0, bad, $"same-LOD seam cracked after mask change ({bad}/{total} bad edges)");
        }

        [Test]
        public void FlatShading_PreservesGeometryAndWinding()
        {
            var sphere = new SphereDensity { Center = new Vector3(8, 8, 8), Radius = 5.5f };
            var buffers = BuildChunk(sphere, new NodeKey(0, Vector3Int.zero), 0);

            var before = new MeshUnion();
            before.Add(buffers, Vector3.zero);
            double volumeBefore = before.SignedVolume6();
            int triangles = buffers.Indices.Count / 3;

            buffers.ConvertToFlatShaded();
            Assert.AreEqual(triangles * 3, buffers.Vertices.Count, "flat shading vertex count wrong");
            Assert.AreEqual(triangles * 3, buffers.Indices.Count, "flat shading index count wrong");

            var after = new MeshUnion();
            after.Add(buffers, Vector3.zero);
            Assert.Less(Math.Abs(after.SignedVolume6() - volumeBefore), 1e-3, "flat shading changed geometry");

            for (int t = 0; t < buffers.Indices.Count; t += 3)
            {
                Vector3 p0 = buffers.Vertices[buffers.Indices[t]];
                Vector3 p1 = buffers.Vertices[buffers.Indices[t + 1]];
                Vector3 p2 = buffers.Vertices[buffers.Indices[t + 2]];
                Vector3 geometric = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                Assert.Greater(Vector3.Dot(geometric, buffers.Normals[buffers.Indices[t]]), 0.99f,
                    "face normal disagrees with winding");
            }
        }

        [Test]
        public void Octree_LeavesAreDisjointBalancedAndMaskedCorrectly()
        {
            const int cells = 16;
            const int maxLod = 4;
            var octree = new TerrainOctree(cells, 1f, maxLod, 220f, 1.3f);
            var commands = new List<ChunkDrawCommand>();
            octree.SelectChunks(new Vector3(7f, 3f, -11f), commands);

            var byKey = commands.ToDictionary(c => c.Key, c => c.TransitionMask);
            Assert.Greater(commands.Count, 100, "octree produced too few chunks");

            foreach (var command in commands)
            {
                var walk = command.Key;
                for (int lod = command.Key.Lod + 1; lod <= maxLod; lod++)
                {
                    walk = walk.Parent;
                    Assert.IsFalse(byKey.ContainsKey(walk), "a leaf is an ancestor of another (overlap)");
                }
            }

            var boxes = commands.Select(c =>
            {
                var min = c.Key.MinVoxel(cells);
                int size = cells << c.Key.Lod;
                return (c.Key, min, size, c.TransitionMask);
            }).ToList();

            foreach (var self in boxes)
            {
                for (int f = 0; f < 6; f++)
                {
                    var faceEnum = (CubeFace)f;
                    int axis = faceEnum.Axis();
                    bool positive = faceEnum.IsPositive();

                    bool sawFiner = false, sawAny = false;
                    foreach (var other in boxes)
                    {
                        if (other.Key.Equals(self.Key)) continue;
                        int planeSelf = positive ? Axis(self.min, axis) + self.size : Axis(self.min, axis);
                        int planeOtherNear = positive ? Axis(other.min, axis) : Axis(other.min, axis) + other.size;
                        if (planeSelf != planeOtherNear) continue;
                        bool touches = true;
                        for (int a = 0; a < 3; a++)
                        {
                            if (a == axis) continue;
                            touches &= Axis(other.min, a) < Axis(self.min, a) + self.size
                                    && Axis(other.min, a) + other.size > Axis(self.min, a);
                        }
                        if (!touches) continue;

                        sawAny = true;
                        int lodDiff = other.Key.Lod - self.Key.Lod;
                        Assert.LessOrEqual(Math.Abs(lodDiff), 1, "2:1 balance violated between neighbours");
                        if (lodDiff < 0) sawFiner = true;
                    }

                    bool maskBit = (self.TransitionMask & (1 << f)) != 0;
                    Assert.AreEqual(sawFiner && sawAny, maskBit, "transition mask disagrees with neighbour LODs");
                    if (self.Key.Lod == 0)
                        Assert.IsFalse(maskBit, "LOD0 chunk has a transition mask bit");
                }
            }
        }

        static int Axis(Vector3Int v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

        [Test]
        public void Terraform_EditsLayerAOverProceduralLayerB()
        {
            var edits = new VoxelEditLayer();
            var layered = new LayeredDensitySource(new FlatGround(), edits);

            float before = layered.SampleVoxel(0, -2, 0);
            Assert.Greater(before, 0.5f, "ground should be solid before digging");

            var bounds = layered.ApplySphereBrush(new Vector3(0, 0, 0), 4f, 1f, build: false);
            Assert.Less(layered.SampleVoxel(0, -2, 0), before, "digging should lower density");
            Assert.IsTrue(edits.TryGetVoxel(0, 0, 0, out _), "edit not stored");
            Assert.IsFalse(edits.TryGetVoxel(50, 0, 0, out _), "far voxel wrongly edited");
            Assert.Greater(layered.SampleVoxel(50, -2, 0), 0.5f, "far ground changed");
            Assert.IsTrue(bounds.xMin <= -4 && bounds.xMax >= 4, "reported bounds don't cover the brush");

            layered.ApplySphereBrush(new Vector3(0, 0, 0), 4f, 1f, build: true);
            Assert.Greater(layered.SampleVoxel(0, 1, 0), 0.5f, "building should raise density above air");
        }

        [Test]
        public void Noise_IsDeterministicAndThreadSafe()
        {
            var a = new ProceduralDensitySource(new NoiseSettings { seed = 42 }, 1f);
            var b = new ProceduralDensitySource(new NoiseSettings { seed = 42 }, 1f);
            var values = new float[64];
            System.Threading.Tasks.Parallel.For(0, 64, i => values[i] = a.SampleVoxel(i * 13, i * 7 - 100, -i * 3));
            for (int i = 0; i < 64; i++)
                Assert.AreEqual(b.SampleVoxel(i * 13, i * 7 - 100, -i * 3), values[i], "noise not deterministic");
        }
    }
}
