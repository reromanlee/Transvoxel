using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace reromanlee.Transvoxel.Editor
{
    /// <summary>
    /// Live debug readout of a <see cref="TransvoxelTerrain"/>'s own resource use —
    /// CPU/GPU time and RAM/GPU memory of just this package's work, right now. Open from
    /// Window ▸ Transvoxel ▸ Terrain Stats. Meaningful in Play mode (nothing builds in edit
    /// mode); polls <see cref="TransvoxelTerrain.CollectStats"/> a few times a second.
    /// </summary>
    public sealed class TransvoxelStatsWindow : EditorWindow
    {
        const long PollIntervalMs = 250;

        [SerializeField] TransvoxelTerrain terrain;

        // Each value label paired with how to fill it from a fresh stats snapshot.
        readonly List<(Label label, Func<TransvoxelResourceStats, string> format)> rows =
            new List<(Label, Func<TransvoxelResourceStats, string>)>();
        Label statusLabel;

        [MenuItem("Window/Transvoxel/Terrain Stats")]
        static void Open()
        {
            var window = GetWindow<TransvoxelStatsWindow>("Terrain Stats");
            window.minSize = new Vector2(300f, 420f);
            window.Show();
        }

        public void CreateGUI()
        {
            rows.Clear();
            VisualElement root = rootVisualElement;
            root.style.paddingLeft = 8f;
            root.style.paddingRight = 8f;
            root.style.paddingTop = 6f;

            var picker = new ObjectField("Terrain")
            {
                objectType = typeof(TransvoxelTerrain),
                allowSceneObjects = true,
                value = terrain,
            };
            picker.RegisterValueChangedCallback(e => terrain = e.newValue as TransvoxelTerrain);
            root.Add(picker);

            statusLabel = new Label
            {
                style = { opacity = 0.6f, marginTop = 2f, marginBottom = 4f, whiteSpace = WhiteSpace.Normal },
            };
            root.Add(statusLabel);

            BuildTimingSection(root);
            BuildMemorySection(root);
            BuildSceneSection(root);

            // Poll on a timer rather than every editor frame — a few reads a second is
            // plenty for a human-read display and keeps the window nearly free.
            root.schedule.Execute(Refresh).Every(PollIntervalMs);
        }

        // ------------------------------------------------------------------ sections

        void BuildTimingSection(VisualElement root)
        {
            VisualElement section = Section(root, "CPU / GPU time");
            Row(section, "Main thread", s => $"{s.MainThreadMsPerFrame:0.00} ms/frame");
            Row(section, "Main thread peak", s => $"{s.MainThreadPeakMs:0.00} ms");
            Row(section, "Worker CPU", s => $"{s.WorkerCpuMsPerSecond:0.0} ms/s");
            Row(section, "Chunk builds", s =>
                $"{s.BuildsPerSecond:0.0}/s  ·  {s.AverageBuildCpuMs:0.00} ms avg");
            Row(section, "GPU compute", s => s.GpuComputeMsPerSecond > 0f || s.GpuJobsInFlight > 0
                ? $"{s.GpuComputeMsPerSecond:0.0} ms/s  ·  {s.AverageGpuJobMs:0.00} ms/job"
                : "— (CPU backend)");
            Row(section, "GPU jobs in flight", s => s.GpuJobsInFlight.ToString());
        }

        void BuildMemorySection(VisualElement root)
        {
            VisualElement ram = Section(root, "RAM");
            Row(ram, "Density cache", s => FormatBytes(s.DensityCacheBytes));
            Row(ram, "Edit layer", s => FormatBytes(s.EditLayerBytes));
            Row(ram, "Material layer", s => FormatBytes(s.MaterialLayerBytes));
            Row(ram, "Mesh buffers", s => FormatBytes(s.MeshBufferBytes));
            Row(ram, "Chunk meshes", s => FormatBytes(s.ChunkMeshCpuBytes));
            Row(ram, "Total", s => FormatBytes(s.RamTotalBytes), bold: true);

            VisualElement gpu = Section(root, "GPU memory");
            Row(gpu, "Chunk meshes", s => FormatBytes(s.ChunkMeshGpuBytes));
            Row(gpu, "Compute buffers", s => FormatBytes(s.ComputeBufferBytes));
            Row(gpu, "Total", s => FormatBytes(s.GpuTotalBytes), bold: true);
        }

        void BuildSceneSection(VisualElement root)
        {
            VisualElement section = Section(root, "Scene");
            Row(section, "Live chunks", s => s.LiveChunks.ToString("n0"));
            Row(section, "Ghost chunks", s => s.GhostChunks.ToString("n0"));
            Row(section, "Pending builds", s => s.PendingBuilds.ToString("n0"));
            Row(section, "Cached grids", s => s.CachedGrids.ToString("n0"));
            Row(section, "Pooled views", s => s.PooledChunkViews.ToString("n0"));
            Row(section, "Pooled buffers", s => s.PooledMeshBuffers.ToString("n0"));
            Row(section, "Vertices", s => s.TotalVertices.ToString("n0"));
            Row(section, "Triangles", s => (s.TotalIndices / 3).ToString("n0"));
        }

        static VisualElement Section(VisualElement root, string title)
        {
            root.Add(new Label(title)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold, marginTop = 8f, marginBottom = 2f,
                    opacity = 0.8f,
                },
            });
            var box = new VisualElement
            {
                style =
                {
                    borderTopWidth = 1f, borderLeftWidth = 1f, borderRightWidth = 1f, borderBottomWidth = 1f,
                    borderTopColor = Border, borderLeftColor = Border,
                    borderRightColor = Border, borderBottomColor = Border,
                    borderTopLeftRadius = 4f, borderTopRightRadius = 4f,
                    borderBottomLeftRadius = 4f, borderBottomRightRadius = 4f,
                    paddingTop = 3f, paddingBottom = 3f, paddingLeft = 6f, paddingRight = 6f,
                },
            };
            root.Add(box);
            return box;
        }

        void Row(VisualElement section, string label, Func<TransvoxelResourceStats, string> format,
            bool bold = false)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween },
            };
            row.Add(new Label(label) { style = { opacity = 0.75f } });
            var value = new Label("—")
            {
                style = { unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal },
            };
            row.Add(value);
            section.Add(row);
            rows.Add((value, format));
        }

        // ------------------------------------------------------------------ polling

        void Refresh()
        {
            if (terrain == null)
                terrain = FindAnyObjectByType<TransvoxelTerrain>();

            if (terrain == null)
            {
                statusLabel.text = "No TransvoxelTerrain in the scene. Enter Play mode with one active.";
                foreach (var (label, _) in rows)
                    label.text = "—";
                return;
            }
            if (!Application.isPlaying)
            {
                statusLabel.text = "Enter Play mode for live numbers (nothing builds in edit mode).";
                return;
            }

            statusLabel.text = $"Backend: {terrain.ActiveBackend}  ·  live now";
            TransvoxelResourceStats stats = terrain.CollectStats();
            foreach (var (label, format) in rows)
                label.text = format(stats);
        }

        static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "0";
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:0.0} KB";
            return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        }

        static readonly Color Border = new Color(0.5f, 0.5f, 0.5f, 0.25f);
    }
}
