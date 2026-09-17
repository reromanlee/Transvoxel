using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace reromanlee.Transvoxel.Samples.Tests
{
    /// <summary>
    /// Guards the demo overlay against driving itself.
    ///
    /// A runtime UI Toolkit panel has built-in navigation, and the Input System's default UI
    /// map binds WASD and the arrow keys to Navigate and Space/Enter to Submit. Flying the
    /// camera therefore walked focus through the panel, changed whichever slider held focus,
    /// and opened and closed the Foldout sections — with the ScrollView scrolling to follow
    /// the focus, so the panel appeared to move on its own.
    /// </summary>
    public sealed class MouseOnlyPanelTests
    {
        /// <summary>A stand-in for the real panel: the control types that were misbehaving.</summary>
        static VisualElement BuildPanel(out Toggle toggle, out Slider slider, out Foldout foldout)
        {
            var root = new VisualElement();
            var scroll = new ScrollView();
            root.Add(scroll);

            foldout = new Foldout { text = "Section", value = true };
            scroll.Add(foldout);

            toggle = new Toggle("Colorize LODs");
            slider = new Slider("Radius", 1f, 24f) { value = 5f, showInputField = true };
            foldout.Add(toggle);
            foldout.Add(slider);
            return root;
        }

        [Test]
        public void MakeMouseOnly_LeavesNothingFocusable()
        {
            VisualElement root = BuildPanel(out Toggle toggle, out Slider slider, out Foldout foldout);
            TransvoxelDemoController.MakeMouseOnly(root);

            // Every element, internals included — a Slider's dragger and a Foldout's toggle
            // are children Unity creates itself, and they are what navigation lands on.
            AssertNotFocusable(root);
            Assert.IsFalse(toggle.focusable);
            Assert.IsFalse(slider.focusable);
            Assert.IsFalse(foldout.focusable);
        }

        static void AssertNotFocusable(VisualElement element)
        {
            Assert.IsFalse(element.focusable,
                $"'{element.name}' ({element.GetType().Name}) can still take keyboard focus");
            if (element is TextField field)
                Assert.IsTrue(field.isReadOnly,
                    "a slider's numeric field must read as a read-out, not an editable field");

            int count = element.hierarchy.childCount;
            for (int i = 0; i < count; i++)
                AssertNotFocusable(element.hierarchy[i]);
        }

        /// <summary>
        /// The behavioural half: even aimed straight at a control, navigation must do nothing.
        /// Needs a real panel, because events are dispatched by one.
        /// </summary>
        [UnityTest]
        public IEnumerator NavigationEvents_DoNotDriveTheControls()
        {
            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            var documentObject = new GameObject("Test UI");
            var document = documentObject.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            yield return null;

            VisualElement root = document.rootVisualElement;
            if (root == null || root.panel == null)
            {
                Object.Destroy(documentObject);
                Object.Destroy(panelSettings);
                Assert.Ignore("no runtime panel available in this environment");
            }

            VisualElement panel = BuildPanel(out Toggle toggle, out Slider slider, out Foldout foldout);
            root.Add(panel);
            TransvoxelDemoController.MakeMouseOnly(root);
            yield return null;

            bool toggleBefore = toggle.value;
            bool foldoutBefore = foldout.value;
            float sliderBefore = slider.value;

            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = toggle;
                toggle.SendEvent(submit);
            }
            using (var submitFoldout = NavigationSubmitEvent.GetPooled())
            {
                submitFoldout.target = foldout;
                foldout.SendEvent(submitFoldout);
            }
            foreach (NavigationMoveEvent.Direction direction in new[]
                     {
                         NavigationMoveEvent.Direction.Left, NavigationMoveEvent.Direction.Right,
                         NavigationMoveEvent.Direction.Up, NavigationMoveEvent.Direction.Down,
                     })
            {
                using var move = NavigationMoveEvent.GetPooled(direction);
                move.target = slider;
                slider.SendEvent(move);
            }
            yield return null;

            Assert.AreEqual(toggleBefore, toggle.value,
                "Submit (Space/Enter) flipped a toggle");
            Assert.AreEqual(foldoutBefore, foldout.value,
                "Submit (Space/Enter) opened or closed a foldout section");
            Assert.AreEqual(sliderBefore, slider.value, 1e-5f,
                "Navigate (WASD/arrows) moved a slider");

            // ...while the mouse path still works: a value set as a drag would set it still
            // raises the change notification the controller listens to.
            float notified = float.NaN;
            slider.RegisterValueChangedCallback(e => notified = e.newValue);
            slider.value = 12f;
            yield return null;
            Assert.AreEqual(12f, notified, 1e-5f, "the panel no longer reports value changes");

            Object.Destroy(documentObject);
            Object.Destroy(panelSettings);
        }
    }
}
