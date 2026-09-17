using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace reromanlee.Transvoxel.Samples
{
    /// <summary>
    /// Drives the demo scene: terraforming input and the UI Toolkit overlay.
    ///
    /// The overlay is deliberately wide in scope. Almost every value on
    /// <see cref="TransvoxelSettings"/> applies while the game is running — including the
    /// ones that rebuild the entire world, like LOD count and voxel size — and that is the
    /// package's most useful and least obvious property. A panel that only exposed the
    /// cheap toggles would hide it.
    ///
    /// Nothing here is required to use the terrain: it is all scene wiring and UI.
    /// </summary>
    [AddComponentMenu("Transvoxel/Demo Controller")]
    [RequireComponent(typeof(UIDocument))]
    public sealed class TransvoxelDemoController : MonoBehaviour
    {
        [Header("Scene")]
        [Tooltip("The terrain this panel drives. Found in the scene when left empty.")]
        public TransvoxelTerrain terrain;

        [Tooltip("Camera used for the terraforming ray. Defaults to Camera.main.")]
        public Camera demoCamera;

        [Header("Brush")]
        [Range(1f, 24f)] public float brushRadius = 5f;
        [Range(0.1f, 2f)] public float brushStrength = 0.9f;

        [Tooltip("Seconds between brush applications while a sculpt button is held.")]
        [Range(0.02f, 0.5f)] public float brushInterval = 0.07f;

        static readonly string[] BackendNames = { "CPU", "GPU", "Hybrid" };

        TransvoxelFlyCamera flyCamera;
        UIDocument document;

        InputAction dig;
        InputAction build;
        InputAction point;
        InputAction nextMaterial;
        InputAction previousMaterial;

        // Widgets, looked up once by name.
        Label statFrame, statChunks, statCpu, statMemory, parallaxNote, controlsText;
        RadioButtonGroup materialPicker, backendPicker;
        VisualElement body;
        Button collapse;

        int buildMaterial;
        float fps;
        float nextBrushTime;
        float nextStatsTime;
        TransvoxelResourceStats stats;
        readonly StringBuilder line = new StringBuilder(128);

        // Set while a callback is pushing a value into the settings, so the change
        // notification does not bounce back and rewrite the widget the user is dragging.
        bool applying;

        TransvoxelSettings Settings => terrain != null ? terrain.settings : null;

        void OnEnable()
        {
            document = GetComponent<UIDocument>();
            if (terrain == null)
                terrain = FindAnyObjectByType<TransvoxelTerrain>();
            if (demoCamera == null)
                demoCamera = Camera.main;
            if (demoCamera != null)
                flyCamera = demoCamera.GetComponent<TransvoxelFlyCamera>();

            dig = new InputAction("Dig", InputActionType.Button);
            dig.AddBinding("<Mouse>/leftButton");
            dig.AddBinding("<Gamepad>/rightTrigger");

            build = new InputAction("Build", InputActionType.Button);
            build.AddBinding("<Gamepad>/leftTrigger");
            // Shift + left button builds instead of digging, matching the old demo.
            build.AddCompositeBinding("OneModifier")
                .With("Modifier", "<Keyboard>/leftShift")
                .With("Binding", "<Mouse>/leftButton");

            point = new InputAction("Point", InputActionType.Value);
            point.AddBinding("<Mouse>/position");

            nextMaterial = new InputAction("NextMaterial", InputActionType.Button);
            nextMaterial.AddBinding("<Gamepad>/rightShoulder");
            previousMaterial = new InputAction("PreviousMaterial", InputActionType.Button);
            previousMaterial.AddBinding("<Gamepad>/leftShoulder");

            dig.Enable();
            build.Enable();
            point.Enable();
            nextMaterial.Enable();
            previousMaterial.Enable();

            BuildUI();
            if (Settings != null)
                Settings.Changed += OnSettingsChangedExternally;
        }

        void OnDisable()
        {
            if (Settings != null)
                Settings.Changed -= OnSettingsChangedExternally;
            foreach (InputAction action in new[] { dig, build, point, nextMaterial, previousMaterial })
            {
                action?.Disable();
                action?.Dispose();
            }
        }

        // ------------------------------------------------------------------ UI

        void BuildUI()
        {
            VisualElement root = document.rootVisualElement;
            if (root == null)
                return;

            statFrame = root.Q<Label>("stat-frame");
            statChunks = root.Q<Label>("stat-chunks");
            statCpu = root.Q<Label>("stat-cpu");
            statMemory = root.Q<Label>("stat-memory");
            parallaxNote = root.Q<Label>("parallax-note");
            controlsText = root.Q<Label>("controls-text");
            materialPicker = root.Q<RadioButtonGroup>("material-picker");
            backendPicker = root.Q<RadioButtonGroup>("backend-picker");
            body = root.Q<VisualElement>("body");
            collapse = root.Q<Button>("collapse");

            MakeMouseOnly(root);

            if (collapse != null)
            {
                collapse.clicked += () =>
                {
                    bool hidden = body.style.display == DisplayStyle.None;
                    body.style.display = hidden ? DisplayStyle.Flex : DisplayStyle.None;
                    collapse.text = hidden ? "–" : "+";
                };
            }

            if (controlsText != null)
            {
                controlsText.text =
                    "Right mouse drag - look\n" +
                    "W A S D - fly, Q / E - down / up, Shift - faster\n" +
                    "Left mouse - dig, Shift + left mouse - build\n" +
                    "1-4 - pick the build material\n" +
                    "Gamepad: sticks fly and look, triggers sculpt, bumpers pick material\n" +
                    "\n" +
                    "The panel is mouse-only on purpose: it ignores the keyboard and " +
                    "gamepad entirely, so flying the camera cannot nudge its controls. " +
                    "Drag the sliders to change values.";
            }

            TransvoxelSettings settings = Settings;
            if (settings == null)
                return;

            Bind<Slider, float>(root, "brush-radius", brushRadius, v => brushRadius = v);
            Bind<Slider, float>(root, "brush-strength", brushStrength, v => brushStrength = v);
            Bind<Slider, float>(root, "blend-sharpness", settings.materialBlendSharpness,
                v => settings.materialBlendSharpness = v); // read live, no rebuild

            // World settings: assigning then notifying rebuilds the terrain the next frame.
            Bind<Slider, float>(root, "view-distance", settings.viewDistance,
                v => Structural(() => settings.viewDistance = v));
            Bind<SliderInt, int>(root, "lod-levels", settings.maxLodLevels,
                v => Structural(() => settings.maxLodLevels = v));
            Bind<Slider, float>(root, "lod-split", settings.lodSplitFactor,
                v => Structural(() => settings.lodSplitFactor = v));
            Bind<Slider, float>(root, "voxel-size", settings.voxelSize,
                v => Structural(() => settings.voxelSize = v));
            Bind<SliderInt, int>(root, "collider-lod", settings.colliderMaxLod,
                v => Structural(() => settings.colliderMaxLod = v));

            Bind<Toggle, bool>(root, "smooth-shading", settings.smoothShading, v =>
            {
                settings.smoothShading = v;
                terrain.RebuildAllChunks(); // geometry only; no pipeline teardown
            });
            Bind<Toggle, bool>(root, "colorize-lods", settings.colorizeLods, v =>
            {
                settings.colorizeLods = v;
                terrain.RefreshLodTint(); // swaps materials, keeps every chunk on screen
            });
            Bind<Slider, float>(root, "fade-seconds", settings.chunkFadeInSeconds,
                v => Live(() => settings.chunkFadeInSeconds = v));
            Bind<Slider, float>(root, "edge-fade", settings.edgeFadeFraction,
                v => Live(() => settings.edgeFadeFraction = v));

            TransvoxelMaterialPalette palette = settings.materialPalette;
            Bind<Toggle, bool>(root, "triplanar", palette != null && palette.triplanar, v =>
            {
                if (palette == null) return;
                palette.triplanar = v;
                palette.NotifyChanged(); // re-pushes bindings live, nothing rebuilds
            });
            Bind<Toggle, bool>(root, "parallax", palette != null && palette.parallaxOcclusion, v =>
            {
                if (palette == null) return;
                palette.parallaxOcclusion = v;
                palette.NotifyChanged();
                RefreshParallaxNote();
            });
            RefreshParallaxNote();

            if (backendPicker != null)
            {
                backendPicker.value = (int)settings.meshingBackend;
                backendPicker.RegisterValueChangedCallback(e =>
                {
                    if (applying) return;
                    Structural(() => settings.meshingBackend = (MeshingBackend)e.newValue);
                });
            }

            RefreshMaterialPicker();
        }

        /// <summary>
        /// Makes the panel respond to the mouse and nothing else.
        ///
        /// A runtime UI Toolkit panel has built-in NAVIGATION: the Input System's default UI
        /// map binds WASD and the arrow keys to Navigate and Space/Enter to Submit. While you
        /// are flying the camera that silently walks focus through the panel (the ScrollView
        /// scrolling to follow, so the panel appears to move on its own), changes whichever
        /// Slider holds focus, and opens and closes Foldouts.
        ///
        /// Two layers, because either alone leaves a gap:
        ///
        ///  1. Nothing in the panel is focusable. Navigation only travels between focusable
        ///     elements, so with none there is nowhere for it to go — no value changes, and
        ///     no focus ring flickering across the panel as you fly, which is the visible
        ///     half of the problem. Pointer events need no focus, so the mouse is unaffected.
        ///  2. Navigation and key events are swallowed at the root regardless, which covers
        ///     any control that reacts to a keystroke aimed straight at it.
        ///
        /// The sliders' numeric fields become read-outs: with no keyboard focus there is
        /// nothing to type into them. Drag the slider instead.
        ///
        /// Static and standalone so it can be applied to any panel — and tested without a
        /// scene. Call it again after adding controls at runtime (see RefreshMaterialPicker).
        /// </summary>
        public static void MakeMouseOnly(VisualElement root)
        {
            if (root == null)
                return;

            DenyFocus(root);

            root.RegisterCallback<NavigationMoveEvent>(Swallow, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(Swallow, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationCancelEvent>(Swallow, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(Swallow, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyUpEvent>(Swallow, TrickleDown.TrickleDown);
        }

        /// <summary>
        /// Clears focusability across the whole visual hierarchy, internals included — a
        /// Slider's dragger and a Foldout's toggle are children Unity creates itself, and
        /// they are exactly what navigation would otherwise land on.
        /// </summary>
        static void DenyFocus(VisualElement element)
        {
            element.focusable = false;
            if (element is TextField field)
                field.isReadOnly = true; // a read-out now; do not imply otherwise

            int count = element.hierarchy.childCount;
            for (int i = 0; i < count; i++)
                DenyFocus(element.hierarchy[i]);
        }

        static void Swallow(EventBase e)
        {
            e.StopImmediatePropagation();
            e.PreventDefault();
        }

        /// <summary>Wires one widget to an initial value and a setter, ignoring echo changes.</summary>
        void Bind<TField, TValue>(VisualElement root, string name, TValue initial,
            System.Action<TValue> apply)
            where TField : VisualElement, INotifyValueChanged<TValue>
        {
            var field = root.Q<TField>(name);
            if (field == null)
                return;
            field.SetValueWithoutNotify(initial);
            field.RegisterValueChangedCallback(e =>
            {
                if (applying)
                    return;
                apply(e.newValue);
            });
        }

        /// <summary>A change that tears down and rebuilds the world (the notification does it).</summary>
        void Structural(System.Action change)
        {
            applying = true;
            change();
            Settings.NotifyChanged();
            applying = false;
        }

        /// <summary>A change applied in place — no chunk is rebuilt.</summary>
        void Live(System.Action change) => Structural(change);

        void OnSettingsChangedExternally()
        {
            // Someone edited the asset in the Inspector while we are running; only the
            // material list can change shape, so that is all we refresh.
            if (!applying)
                RefreshMaterialPicker();
        }

        void RefreshMaterialPicker()
        {
            if (materialPicker == null)
                return;
            TransvoxelMaterialPalette palette = Settings != null ? Settings.materialPalette : null;
            if (palette == null || palette.LayerCount < 2)
            {
                materialPicker.style.display = DisplayStyle.None;
                return;
            }

            materialPicker.style.display = DisplayStyle.Flex;
            var names = new string[palette.LayerCount];
            for (int i = 0; i < names.Length; i++)
                names[i] = palette.Layers[i].name;
            materialPicker.choices = names;
            buildMaterial = Mathf.Clamp(buildMaterial, 0, names.Length - 1);
            materialPicker.SetValueWithoutNotify(buildMaterial);
            materialPicker.RegisterValueChangedCallback(e => buildMaterial = e.newValue);
            // Setting choices creates the radio buttons, so they missed the pass in BuildUI
            // and would still be focusable — the one gap navigation could otherwise use.
            DenyFocus(materialPicker);
        }

        void RefreshParallaxNote()
        {
            if (parallaxNote == null)
                return;
            TransvoxelMaterialPalette palette = Settings != null ? Settings.materialPalette : null;
            bool wantsParallax = palette != null && palette.parallaxOcclusion;
            if (wantsParallax && !palette.HasHeightMaps)
            {
                parallaxNote.text = "No layer has a height map, so there is nothing to march — "
                                    + "parallax stays off until one is assigned.";
                parallaxNote.AddToClassList("warn");
            }
            else
            {
                parallaxNote.text = "Triplanar fixes stretched texturing on cliffs; parallax "
                                    + "adds per-pixel depth. Both cost fill rate.";
                parallaxNote.RemoveFromClassList("warn");
            }
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f), 0.05f);

            HandleMaterialCycling();
            HandleSculpting();
            RefreshStats();
        }

        void HandleMaterialCycling()
        {
            TransvoxelMaterialPalette palette = Settings != null ? Settings.materialPalette : null;
            if (palette == null || palette.LayerCount < 2)
                return;

            int delta = 0;
            if (nextMaterial.WasPressedThisFrame()) delta = 1;
            else if (previousMaterial.WasPressedThisFrame()) delta = -1;

            // Number keys jump straight to a material.
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                for (int i = 0; i < Mathf.Min(9, palette.LayerCount); i++)
                {
                    if (keyboard[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame)
                    {
                        SetBuildMaterial(i);
                        return;
                    }
                }
            }
            if (delta != 0)
                SetBuildMaterial((buildMaterial + delta + palette.LayerCount) % palette.LayerCount);
        }

        void SetBuildMaterial(int index)
        {
            buildMaterial = index;
            materialPicker?.SetValueWithoutNotify(index);
        }

        void HandleSculpting()
        {
            if (terrain == null || demoCamera == null || Time.time < nextBrushTime)
                return;

            bool building = build.IsPressed();
            if (!building && !dig.IsPressed())
                return;

            Vector2 screenPosition;
            if (Mouse.current != null && (dig.activeControl?.device is Mouse
                                          || build.activeControl?.device is Mouse))
            {
                screenPosition = point.ReadValue<Vector2>();
                // A click that lands on the panel belongs to the panel. Asking UI Toolkit
                // what is under the pointer is exact, unlike the hard-coded screen rectangle
                // the old IMGUI overlay used.
                if (IsPointerOverUI(screenPosition))
                    return;
            }
            else
            {
                // Gamepad sculpting aims at the centre of the screen.
                screenPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            }

            Ray ray = demoCamera.ScreenPointToRay(screenPosition);
            if (!terrain.RaycastDensity(ray, 400f, out Vector3 hit))
                return;

            terrain.Terraform(hit, brushRadius, brushStrength, building, (byte)buildMaterial);
            nextBrushTime = Time.time + brushInterval;
        }

        bool IsPointerOverUI(Vector2 screenPosition)
        {
            if (flyCamera != null && flyCamera.IsLooking)
                return false; // cursor is captured; the panel cannot be under it
            IPanel panel = document != null && document.rootVisualElement != null
                ? document.rootVisualElement.panel
                : null;
            if (panel == null)
                return false;
            Vector2 panelPosition = RuntimePanelUtils.ScreenToPanel(panel, screenPosition);
            return panel.Pick(panelPosition) != null;
        }

        void RefreshStats()
        {
            if (terrain == null || Time.unscaledTime < nextStatsTime)
                return;
            nextStatsTime = Time.unscaledTime + 0.25f;
            stats = terrain.CollectStats();

            if (statFrame != null)
            {
                line.Clear();
                line.Append("FPS ").Append(Mathf.RoundToInt(fps))
                    .Append("   backend ").Append(BackendNames[(int)terrain.ActiveBackend]);
                statFrame.text = line.ToString();
            }
            if (statChunks != null)
            {
                line.Clear();
                line.Append("chunks ").Append(terrain.LiveChunkCount)
                    .Append("   building ").Append(terrain.PendingBuildCount)
                    .Append("   verts ").Append(terrain.TotalVertices.ToString("n0"));
                statChunks.text = line.ToString();
            }
            if (statCpu != null)
            {
                string gpu = stats.GpuComputeMsPerSecond > 0f || stats.GpuJobsInFlight > 0
                    ? stats.GpuComputeMsPerSecond.ToString("0.0") + " ms/s"
                    : "—";
                statCpu.text = $"main {stats.MainThreadMsPerFrame:0.00} ms/f   "
                               + $"workers {stats.WorkerCpuMsPerSecond:0.0} ms/s   gpu {gpu}";
            }
            if (statMemory != null)
            {
                statMemory.text = $"RAM {FormatBytes(stats.RamTotalBytes)}   "
                                  + $"VRAM {FormatBytes(stats.GpuTotalBytes)}";
            }
        }

        static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.00") + " MB";
        }
    }
}
