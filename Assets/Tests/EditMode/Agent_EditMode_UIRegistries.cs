using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the three pieces of shared UI plumbing added on 2026-11-07:
    ///
    ///   - Agent_Overlays: one registry behind the health panel's switches, the
    ///     settings sheet's OVERLAYS tab and the HUD's VIEW cycler / O key;
    ///   - Agent_SettingsSheet: four tabs that each fit the fixed body, plus the
    ///     one-line registration other components use;
    ///   - Agent_BuildStamp: the YYMMDD label in the lower-right corner.
    /// </summary>
    public class Agent_EditMode_UIRegistries
    {
        static bool _a, _b;

        [TearDown]
        public void TearDown()
        {
            Agent_Overlays.Unregister("test-a");
            Agent_Overlays.Unregister("test-b");
            Agent_SettingsSheet.Unregister("test-switch");
            Agent_SettingsSheet.Close();
        }

        // ------------------------------------------------------------------
        // Overlays
        // ------------------------------------------------------------------

        [Test]
        public void Register_SameIdTwice_ReplacesRatherThanDuplicates()
        {
            int before = Agent_Overlays.Count;
            Agent_Overlays.Register("test-a", "A", () => _a, on => _a = on);
            Agent_Overlays.Register("test-a", "A2", () => _a, on => _a = on);

            Assert.AreEqual(before + 1, Agent_Overlays.Count,
                "Registering the same id twice added two entries; a component's Awake " +
                "running on every scene load would grow the panel forever.");
            Assert.AreEqual("A2", Agent_Overlays.Get(Agent_Overlays.IndexOf("test-a")).Name);
        }

        [Test]
        public void CycleSolo_ShowsOneOverlayAtATime_ThenNone()
        {
            var saved = SaveBuiltIns();
            try
            {
                _a = false;
                _b = false;
                Agent_Overlays.Register("test-a", "A", () => _a, on => _a = on);
                Agent_Overlays.Register("test-b", "B", () => _b, on => _b = on);
                Agent_Overlays.AllOff();

                bool sawA = false, sawB = false, aBeforeB = false;
                int steps = 0;
                int shown;
                do
                {
                    shown = Agent_Overlays.CycleSolo();
                    steps++;
                    Assert.LessOrEqual(Agent_Overlays.OnCount(), 1,
                        "The cycler left more than one overlay on - it is supposed to show them one at a time.");
                    if (_a) { sawA = true; if (!sawB) aBeforeB = true; }
                    if (_b) sawB = true;
                    Assert.Less(steps, Agent_Overlays.Count + 3, "The cycler never returned to none.");
                }
                while (shown >= 0);

                Assert.IsTrue(sawA && sawB, "The cycler skipped a registered overlay.");
                Assert.IsTrue(aBeforeB, "The cycler did not follow registration order.");
                Assert.AreEqual(0, Agent_Overlays.OnCount(), "The cycle did not end with every overlay off.");
            }
            finally
            {
                RestoreBuiltIns(saved);
            }
        }

        [Test]
        public void CycleSolo_FromSeveralOn_ClearsThemFirst()
        {
            var saved = SaveBuiltIns();
            try
            {
                Agent_Overlays.Register("test-a", "A", () => _a, on => _a = on);
                Agent_Overlays.Register("test-b", "B", () => _b, on => _b = on);
                Agent_Overlays.AllOff();
                _a = true;
                _b = true;

                Assert.AreEqual("VIEW 2", Agent_Overlays.SoloText());
                Assert.AreEqual(-1, Agent_Overlays.CycleSolo());
                Assert.AreEqual(0, Agent_Overlays.OnCount());
                Assert.AreEqual("VIEW", Agent_Overlays.SoloText());

                _b = true;
                Assert.AreEqual("B", Agent_Overlays.SoloText(), "One overlay on should name it.");
            }
            finally
            {
                RestoreBuiltIns(saved);
            }
        }

        [Test]
        public void UnavailableOverlays_AreSkippedByTheCycler()
        {
            var saved = SaveBuiltIns();
            try
            {
                Agent_Overlays.Register("test-a", "A", () => _a, on => _a = on, () => false);
                Agent_Overlays.AllOff();
                for (int step = 0; step < Agent_Overlays.Count + 2; step++)
                {
                    Agent_Overlays.CycleSolo();
                    Assert.IsFalse(_a, "The cycler switched on an overlay that reported itself unavailable.");
                }
            }
            finally
            {
                RestoreBuiltIns(saved);
            }
        }

        [Test]
        public void TheBuiltInOverlays_AreRegistered()
        {
            foreach (string id in new[]
                     { "stamina", "intent", "lanes", "vision", "tracer", "brain", "saliency", "xg", "skids" })
            {
                Assert.GreaterOrEqual(Agent_Overlays.IndexOf(id), 0,
                    $"Built-in overlay '{id}' is missing from Agent_Overlays.");
            }
        }

        // ------------------------------------------------------------------
        // Settings sheet
        // ------------------------------------------------------------------

        /// <summary>
        /// Every tab fits the fixed body: at most five rows (640 / --touch-min),
        /// no ScrollView, and the OVERLAYS tab has a button per registered overlay
        /// (up to its fifteen-slot capacity).
        /// </summary>
        [Test]
        public void EveryTab_FitsTheFixedBody_WithoutScrolling()
        {
            var host = new VisualElement();
            Agent_SettingsSheet.Open(host);
            var body = host.Q<VisualElement>("settings-body");
            Assert.IsNotNull(body, "The sheet built no body.");

            foreach (Agent_SettingsSheet.Tab tab in System.Enum.GetValues(typeof(Agent_SettingsSheet.Tab)))
            {
                Agent_SettingsSheet.ShowTab(tab);
                Assert.Greater(body.childCount, 0, $"{tab}: empty tab.");
                Assert.LessOrEqual(body.childCount, 5,
                    $"{tab}: {body.childCount} rows in a body that holds five - the tab strip would move.");
                Assert.AreEqual(0, body.Query<ScrollView>().ToList().Count,
                    $"{tab}: a ScrollView in the settings sheet. Split the tab instead.");
            }

            Agent_SettingsSheet.ShowTab(Agent_SettingsSheet.Tab.Overlays);
            int expected = Mathf.Min(Agent_Overlays.Count, 15);
            int buttons = 0;
            for (int overlayIndex = 0; overlayIndex < expected; overlayIndex++)
            {
                if (body.Q<Button>("overlay-" + Agent_Overlays.Get(overlayIndex).Id) != null) buttons++;
            }
            Assert.AreEqual(expected, buttons, "The OVERLAYS tab does not list every registered overlay.");
        }

        [Test]
        public void RegisterSwitch_AppearsOnItsTab()
        {
            bool value = false;
            Agent_SettingsSheet.RegisterSwitch(Agent_SettingsSheet.Tab.View, "test-switch",
                "Test switch", () => value, on => value = on);

            var host = new VisualElement();
            Agent_SettingsSheet.Open(host);
            Agent_SettingsSheet.ShowTab(Agent_SettingsSheet.Tab.View);

            bool found = false;
            host.Query<Label>().ForEach(label => { if (label.text == "Test switch") found = true; });
            Assert.IsTrue(found, "A registered switch did not appear on its tab.");
        }

        [Test]
        public void TheTabs_AreMatchViewOverlaysAudio()
        {
            var host = new VisualElement();
            Agent_SettingsSheet.Open(host);
            foreach (string name in new[] { "match", "view", "overlays", "audio" })
            {
                Assert.IsNotNull(host.Q<Button>("settings-tab-" + name), $"No {name.ToUpperInvariant()} tab.");
            }
        }

        // ------------------------------------------------------------------
        // Build stamp
        // ------------------------------------------------------------------

        [Test]
        public void BuildStamp_IsYYMMDD()
        {
            Assert.AreEqual("261107", Agent_BuildStamp.Format(new System.DateTime(2026, 11, 7)));
            Assert.AreEqual("990102", Agent_BuildStamp.Format(new System.DateTime(1999, 1, 2)));
            Assert.IsTrue(Agent_BuildStamp.IsValid(Agent_BuildStamp.Stamp),
                $"The editor's stamp '{Agent_BuildStamp.Stamp}' is not six digits.");
            Assert.IsFalse(Agent_BuildStamp.IsValid("26117"));
            Assert.IsFalse(Agent_BuildStamp.IsValid("2611O7"));
            Assert.IsFalse(Agent_BuildStamp.IsValid(null));
        }

        /// <summary>
        /// The build-time writer lives in an editor assembly this test assembly
        /// does not reference, so its format string is checked in the source.
        /// The two must agree or a player shows "nodate".
        /// </summary>
        [Test]
        public void TheBuildStepWriter_UsesTheSameFormat()
        {
            string path = Path.Combine(Application.dataPath, "Editor", "Editor_BuildStamp.cs");
            Assert.IsTrue(File.Exists(path), $"Missing {path}");
            string source = File.ReadAllText(path);
            StringAssert.Contains($"FORMAT = \"{Agent_BuildStamp.FORMAT}\"", source,
                "Editor_BuildStamp writes a different format from the one Agent_BuildStamp reads.");
            StringAssert.Contains("Resources/" + Agent_BuildStamp.RESOURCE + ".txt", source,
                "Editor_BuildStamp writes to a different resource from the one Agent_BuildStamp loads.");
        }

        // ------------------------------------------------------------------

        static bool[] SaveBuiltIns()
        {
            return new[]
            {
                Agent_Intent.Visible, Agent_ShotTracer.Visible, Agent_Skids.Visible,
                Agent_ExpectedGoals.FieldVisible,
                Agent_VisionView.CurrentMode != Agent_VisionView.Mode.Off,
                Agent_StaminaRing.Visible, Agent_PassLanes.Visible, Agent_BrainCam.SaliencyEnabled,
            };
        }

        static void RestoreBuiltIns(bool[] saved)
        {
            Agent_Intent.Visible = saved[0];
            Agent_ShotTracer.Visible = saved[1];
            Agent_Skids.Visible = saved[2];
            Agent_ExpectedGoals.FieldVisible = saved[3];
            if (!saved[4]) Agent_VisionView.CurrentMode = Agent_VisionView.Mode.Off;
            Agent_StaminaRing.Visible = saved[5];
            Agent_PassLanes.Visible = saved[6];
            Agent_BrainCam.SaliencyEnabled = saved[7];
        }
    }
}
