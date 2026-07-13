using reromanlee.Transvoxel.Density;
using UnityEngine;

namespace reromanlee.Transvoxel.Demo
{
    /// <summary>
    /// Self-contained playground: drop this single component on an empty GameObject in an
    /// empty scene and press Play. It creates the camera, light and terrain, then lets you
    /// fly around (RMB + WASD) and terraform (left click digs, Shift + left click builds).
    /// The overlay shows live stats and toggles for smooth shading and LOD visualization.
    /// </summary>
    public sealed class TransvoxelDemo : MonoBehaviour
    {
        [Tooltip("Optional pre-made settings; leave empty for demo defaults.")]
        public TransvoxelSettings settings;

        [Header("Brush")]
        [Range(1f, 24f)] public float brushRadius = 5f;
        [Range(0.1f, 2f)] public float brushStrength = 0.9f;

        static readonly string[] BackendNames = { "CPU", "GPU", "Hybrid" };

        TransvoxelTerrain terrain;
        Camera demoCamera;
        string[] materialNames;
        int buildMaterial;
        float fps;

        // Resource stats are recomputed on an interval, not every OnGUI event: CollectStats
        // walks the live chunks, and OnGUI runs at least twice a frame (Layout + Repaint).
        TransvoxelResourceStats stats;
        float nextStatsTime;
#if ENABLE_LEGACY_INPUT_MANAGER
        float nextBrushTime;
#endif

        void OnEnable()
        {
            demoCamera = Camera.main;
            if (demoCamera == null)
            {
                var cameraObject = new GameObject("Demo Camera") { tag = "MainCamera" };
                demoCamera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
            }
            demoCamera.farClipPlane = Mathf.Max(demoCamera.farClipPlane, 2000f);
            if (demoCamera.GetComponent<FlyCamera>() == null)
                demoCamera.gameObject.AddComponent<FlyCamera>();

            if (FindAnyObjectByType<Light>() == null)
            {
                var lightObject = new GameObject("Demo Sun");
                var sun = lightObject.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.2f;
                sun.shadows = LightShadows.Soft;
                lightObject.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            }

            if (settings == null)
            {
                settings = CreateDemoSettings();
                Debug.Log("[TransvoxelDemo] No settings asset assigned — using built-in demo " +
                          "defaults. To tweak LOD count, view distance, noise, etc. (and see it " +
                          "update live), create Assets ▸ Create ▸ Transvoxel ▸ Terrain Settings and " +
                          "drop it on this component's 'Settings' field.");
            }

            // Assign settings/viewer while the object is inactive so they are in place before
            // TransvoxelTerrain.OnEnable runs (AddComponent would otherwise run it immediately,
            // before these fields are set).
            var terrainObject = new GameObject("Transvoxel Terrain");
            terrainObject.SetActive(false);
            terrain = terrainObject.AddComponent<TransvoxelTerrain>();
            terrain.settings = settings;
            terrain.viewer = demoCamera.transform;
            terrainObject.SetActive(true);

            // Start well above the ground looking out over the landscape.
            demoCamera.transform.position = new Vector3(0f, settings.noise.groundLevel + settings.noise.heightAmplitude + 30f, 0f);
            demoCamera.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
        }

        void OnDisable()
        {
            if (terrain != null)
                Destroy(terrain.gameObject);
        }

        static TransvoxelSettings CreateDemoSettings()
        {
            var demoSettings = ScriptableObject.CreateInstance<TransvoxelSettings>();
            demoSettings.name = "Demo Settings";
            demoSettings.viewDistance = 500f;
            demoSettings.maxLodLevels = 4;
            demoSettings.lodSplitFactor = 1.4f;
            demoSettings.materialPalette = CreateDemoPalette();
            demoSettings.noise = new NoiseSettings
            {
                seed = 1337,
                groundLevel = 0f,
                heightAmplitude = 42f,
                frequency = 0.005f,
                octaves = 5,
                surfaceBlend = 8f,
                caveStrength = 1.6f,
                caveFrequency = 0.028f,
                caveThreshold = 0.5f,
            };
            return demoSettings;
        }

        /// <summary>
        /// A small tint-only palette (no textures needed) so the demo can show building
        /// with different materials and the per-pixel blending between them out of the box.
        /// </summary>
        static TransvoxelMaterialPalette CreateDemoPalette()
        {
            var palette = ScriptableObject.CreateInstance<TransvoxelMaterialPalette>();
            palette.name = "Demo Palette";
            palette.Layers[0].name = "Grass";
            palette.Layers[0].tint = new Color(0.42f, 0.55f, 0.3f);
            palette.AddLayer(new TransvoxelMaterialPalette.Layer
            {
                name = "Rock", tint = new Color(0.45f, 0.44f, 0.42f), smoothness = 0.05f,
            });
            palette.AddLayer(new TransvoxelMaterialPalette.Layer
            {
                name = "Sand", tint = new Color(0.76f, 0.68f, 0.45f), smoothness = 0.15f,
            });
            palette.AddLayer(new TransvoxelMaterialPalette.Layer
            {
                name = "Snow", tint = new Color(0.92f, 0.94f, 0.97f), smoothness = 0.35f,
            });
            return palette;
        }

        void Update()
        {
            fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f), 0.05f);
            if (terrain != null && Time.unscaledTime >= nextStatsTime)
            {
                stats = terrain.CollectStats();
                nextStatsTime = Time.unscaledTime + 0.25f;
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            HandleTerraforming();
#endif
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        void HandleTerraforming()
        {
            if (terrain == null || !Input.GetMouseButton(0) || Time.time < nextBrushTime)
                return;
            // Ignore clicks on the GUI overlay.
            if (Input.mousePosition.x < 300f && Input.mousePosition.y > Screen.height - 600f)
                return;

            var ray = demoCamera.ScreenPointToRay(Input.mousePosition);
            if (terrain.RaycastDensity(ray, 400f, out Vector3 hit))
            {
                bool build = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                terrain.Terraform(hit, brushRadius, brushStrength, build, (byte)buildMaterial);
                nextBrushTime = Time.time + 0.07f;
            }
        }
#endif

        void OnGUI()
        {
            const int width = 290;
            GUILayout.BeginArea(new Rect(10, 10, width, 590), GUI.skin.box);
            GUILayout.Label("<b>Transvoxel Demo</b>", RichLabel());

#if ENABLE_LEGACY_INPUT_MANAGER
            GUILayout.Label("RMB drag – look around\nWASD + Q/E – fly, Shift – fast\nLMB – dig   |   Shift+LMB – build");
#else
            GUILayout.Label("Set Project Settings ▸ Player ▸ Active Input Handling\nto 'Both' (or 'Input Manager') for camera and\nterraforming controls.");
#endif
            GUILayout.Space(6);

            if (terrain != null)
            {
                GUILayout.Label(
                    $"FPS: {fps:0}   Backend: {BackendNames[(int)terrain.ActiveBackend]}\n" +
                    $"Chunks: {terrain.LiveChunkCount}   Building: {terrain.PendingBuildCount}\n" +
                    $"Vertices: {terrain.TotalVertices:n0}");

                // Package-only resource approximation (see TransvoxelTerrain.CollectStats).
                string gpuTime = stats.GpuComputeMsPerSecond > 0f || stats.GpuJobsInFlight > 0
                    ? $"{stats.GpuComputeMsPerSecond:0.0} ms/s"
                    : "—";
                GUILayout.Label(
                    $"<b>Resources (this package)</b>\n" +
                    $"CPU main: {stats.MainThreadMsPerFrame:0.00} ms/frame\n" +
                    $"CPU workers: {stats.WorkerCpuMsPerSecond:0.0} ms/s   GPU: {gpuTime}\n" +
                    $"RAM: {FormatBytes(stats.RamTotalBytes)}   VRAM: {FormatBytes(stats.GpuTotalBytes)}",
                    RichLabel());
            }
            GUILayout.Space(6);

            GUILayout.Label($"Brush radius: {brushRadius:0.0} m");
            brushRadius = GUILayout.HorizontalSlider(brushRadius, 1f, 24f);
            GUILayout.Space(6);

            var palette = settings.materialPalette;
            if (palette != null && palette.LayerCount > 1)
            {
                if (materialNames == null || materialNames.Length != palette.LayerCount)
                {
                    materialNames = new string[palette.LayerCount];
                    for (int i = 0; i < materialNames.Length; i++)
                        materialNames[i] = palette.Layers[i].name;
                }
                GUILayout.Label("Build material");
                buildMaterial = GUILayout.Toolbar(Mathf.Min(buildMaterial, palette.LayerCount - 1),
                    materialNames);

                GUILayout.Label($"Blend sharpness: {settings.materialBlendSharpness:0.0}");
                // Read live by the terrain every frame — no rebuild, tune while brushing.
                settings.materialBlendSharpness =
                    GUILayout.HorizontalSlider(settings.materialBlendSharpness, 1f, 16f);
                GUILayout.Space(6);
            }

            bool smooth = GUILayout.Toggle(settings.smoothShading, " Smooth shading");
            if (smooth != settings.smoothShading)
            {
                settings.smoothShading = smooth;
                terrain.RebuildAllChunks();
            }

            bool tint = GUILayout.Toggle(settings.colorizeLods, " Colorize LOD levels");
            if (tint != settings.colorizeLods)
            {
                settings.colorizeLods = tint;
                terrain.RefreshLodTint();
            }

            GUILayout.Label("Meshing backend");
            int backendIndex = (int)settings.meshingBackend;
            int chosenBackend = GUILayout.Toolbar(backendIndex, BackendNames);
            if (chosenBackend != backendIndex)
            {
                settings.meshingBackend = (MeshingBackend)chosenBackend;
                settings.NotifyChanged(); // rebuilds the pipeline live, edits preserved
            }

            GUILayout.EndArea();
        }

        static GUIStyle RichLabel()
        {
            var style = new GUIStyle(GUI.skin.label) { richText = true };
            return style;
        }

        static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
            return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        }
    }
}
