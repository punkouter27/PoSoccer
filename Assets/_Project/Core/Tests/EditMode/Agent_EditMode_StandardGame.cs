using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// The Po Game Standard pieces that need no play mode: the TAB overlay's inference
    /// clock, the demo-mode settings asset, the C9 reward terms and the page template
    /// (chrome outside the canvas, one version stamp).
    /// </summary>
    public sealed class Agent_EditMode_StandardGame
    {
        [TearDown]
        public void TearDown() => Agent_InferenceClock.Reset();

        [Test]
        public void InferenceClock_AveragesTheLastWindowOfDecisions()
        {
            Agent_InferenceClock.Reset();
            Assert.Less(Agent_InferenceClock.AverageMs, 0d, "an empty window must say so, not report 0 ms");

            Agent_InferenceClock.Record(2d);
            Agent_InferenceClock.Record(4d);
            Assert.AreEqual(3d, Agent_InferenceClock.AverageMs, 1e-9);
            Assert.AreEqual(2, Agent_InferenceClock.SampleCount);

            for (int i = 0; i < Agent_InferenceClock.WINDOW; i++) Agent_InferenceClock.Record(1d);
            Assert.AreEqual(1d, Agent_InferenceClock.AverageMs, 1e-9, "old samples must roll out of the window");
            Assert.AreEqual(Agent_InferenceClock.WINDOW, Agent_InferenceClock.SampleCount);

            Agent_InferenceClock.Record(double.NaN);
            Agent_InferenceClock.Record(-5d);
            Assert.AreEqual(1d, Agent_InferenceClock.AverageMs, 1e-9, "NaN and negative samples are dropped");
        }

        [Test]
        public void InferenceClock_CountsOnlyStepsThatCollectedObservations()
        {
            Agent_InferenceClock.Reset();
            Agent_InferenceClock.ActionReceived();   // a repeat tick: no observation before it
            Assert.AreEqual(0, Agent_InferenceClock.SampleCount);

            Agent_InferenceClock.ObservationsCollected();
            Agent_InferenceClock.ActionReceived();
            Agent_InferenceClock.ActionReceived();   // the second agent's action, same step
            Assert.AreEqual(1, Agent_InferenceClock.SampleCount);
            Assert.GreaterOrEqual(Agent_InferenceClock.AverageMs, 0d);
        }

        [Test]
        public void DemoModeSettings_ShipWithTheStandardsNumbers()
        {
            var settings = Resources.Load<Agent_DemoModeSettings>(Agent_DemoModeSettings.RESOURCE);
            Assert.IsNotNull(settings, "Assets/_Project/Data/Resources/DemoModeSettings.asset is missing or does not load");
            Assert.AreEqual(30f, settings.idleSeconds, 0.001f, "the standard's idle threshold is 30 s");
            Assert.That(settings.cameraDwellSeconds, Is.InRange(6f, 8f), "showcase shots hold ~6-8 s each");
            Assert.That(settings.shots, Is.Not.Empty);
            Assert.AreEqual(settings.shots[0], settings.ShotAt(settings.shots.Length), "the shot list wraps");
            Assert.IsTrue(File.Exists(Path.Combine("Assets", "_Project", "Scenes", settings.matchScene + ".unity")),
                $"demo match scene {settings.matchScene} does not exist");
            Assert.IsTrue(File.Exists(Path.Combine("Assets", "_Project", "Scenes", settings.menuScene + ".unity")),
                $"demo menu scene {settings.menuScene} does not exist");
        }

        [Test]
        public void DemoMode_StartsAtTheThresholdNotBefore()
        {
            Assert.IsFalse(Agent_DemoMode.ShouldStart(29.9f, 30f));
            Assert.IsTrue(Agent_DemoMode.ShouldStart(30f, 30f));
        }

        [Test]
        public void DemoMode_ALongLoadingFrameIsNotIdleTime()
        {
            // The WebGL player's first frame lands after a ~70 s shader warm-up.
            Assert.AreEqual(0f, Agent_DemoMode.AccumulateIdle(0f, 70f), 1e-6f, "a 70 s frame adds nothing");
            Assert.AreEqual(1f, Agent_DemoMode.AccumulateIdle(1f, 0.26f), 1e-6f, "a frame just over 0.25 s adds nothing");
            Assert.AreEqual(1.25f, Agent_DemoMode.AccumulateIdle(1f, 0.25f), 1e-6f, "a 0.25 s frame still counts");
            float idle = 0f;
            for (int frame = 0; frame < 100; frame++) idle = Agent_DemoMode.AccumulateIdle(idle, 1f);
            Assert.IsFalse(Agent_DemoMode.ShouldStart(idle, 30f), "100 one-second stalls are not 30 s of idle");
            for (int frame = 0; frame < 1800; frame++) idle = Agent_DemoMode.AccumulateIdle(idle, 1f / 60f);
            Assert.IsTrue(Agent_DemoMode.ShouldStart(idle + 1e-3f, 30f), "30 s of ordinary frames still starts it");
        }

        [Test]
        public void DemoMode_CameraDwellSkipsALongFrame()
        {
            const float dwell = 7f;
            float shot = 6.9f;
            Assert.IsFalse(Agent_DemoMode.ShotDue(ref shot, 5f, dwell), "a 5 s stall must not cut the shot");
            Assert.AreEqual(6.9f, shot, 1e-6f);
            Assert.IsFalse(Agent_DemoMode.ShotDue(ref shot, 0.05f, dwell));
            Assert.IsTrue(Agent_DemoMode.ShotDue(ref shot, 0.05f, dwell), "ordinary frames still end the shot");
            Assert.AreEqual(0f, shot, 1e-6f, "the clock restarts for the next shot");
            int cuts = 0;
            for (int frame = 0; frame < 430; frame++)
                if (Agent_DemoMode.ShotDue(ref shot, 1f / 60f, dwell)) cuts++;
            Assert.AreEqual(1, cuts, "7.2 s of 60 fps frames is one shot");
        }

        [Test]
        public void RewardTerms_IncludeSquaredActionRateAndEnergy()
        {
            CollectionAssert.Contains(Agent_Soccer.TermNames, "action_rate_sq");
            CollectionAssert.Contains(Agent_Soccer.TermNames, "energy");
            Assert.AreEqual(0f, Agent_Soccer.EnergyCost(0f, 0f));
            Assert.AreEqual(1f, Agent_Soccer.EnergyCost(1f, 1f), 1e-6f);
            Assert.AreEqual(0.5f, Agent_Soccer.EnergyCost(2f, 0f), 1e-6f, "saturation is clamped to 1");
        }

        [Test]
        public void RewardTerms_IncludeTorqueSpinAndFootSliding_EachItsOwnTerm()
        {
            Assert.AreEqual(Agent_Soccer.TermCount, Agent_Soccer.TermNames.Length);
            foreach (string name in new[] { "jitter", "action_rate_sq", "energy", "torque_sq", "spin_sq", "foot_slide" })
                Assert.AreEqual(1, System.Array.FindAll(Agent_Soccer.TermNames, t => t == name).Length, name);
            Assert.AreEqual(0f, Agent_Soccer.TorqueCost(0f));
            Assert.AreEqual(0.25f, Agent_Soccer.TorqueCost(-0.5f), 1e-6f);
            Assert.AreEqual(1f, Agent_Soccer.TorqueCost(3f), 1e-6f, "turn is clamped to 1");
            Assert.AreEqual(0f, Agent_Soccer.SpinCost(0f, 500f));
            Assert.AreEqual(0.25f, Agent_Soccer.SpinCost(-250f, 500f), 1e-6f);
            Assert.AreEqual(1f, Agent_Soccer.SpinCost(900f, 500f), 1e-6f, "spin is clamped to the cap");
            Assert.AreEqual(0f, Agent_Soccer.SlideCost(0f));
            Assert.AreEqual(0.25f, Agent_Soccer.SlideCost(-Agent_Soccer.SLIDE_REFERENCE_SPEED / 2f), 1e-6f);
            Assert.AreEqual(1f, Agent_Soccer.SlideCost(10f), 1e-6f, "sliding is clamped to 1");
        }

        [Test]
        public void ShippedProfiles_KeepTheNewPenaltiesOff_SoTheirBrainsStillMatchTheirTraining()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Reward_Settings", new[] { "Assets/_Project/RL_Models" }))
            {
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.AreEqual(0f, profile.actionRateSquaredScale, profile.name);
                Assert.AreEqual(0f, profile.energyPenaltyScale, profile.name);
                Assert.AreEqual(0f, profile.torqueSquaredScale, profile.name);
                Assert.AreEqual(0f, profile.spinSquaredScale, profile.name);
                Assert.AreEqual(0f, profile.footSlideScale, profile.name);
            }
        }

        /// <summary>
        /// Every active trainer config sets both weights, and neither term's worst-case episode
        /// total outweighs conceding a goal (the ordering Agent_EditMode_RewardBudget keeps for
        /// the profile terms). Action rate pays once per decision (period 8), at most 4 per
        /// decision; energy pays every step, at most 1.
        /// </summary>
        [Test]
        public void TrainerConfigs_SetTheNewPenaltyWeights_WithinTheRewardBudget()
        {
            var standard = AssetDatabase.LoadAssetAtPath<Reward_Settings>("Assets/_Project/RL_Models/Standard_v01/Reward_STANDARD.asset");
            Assert.IsNotNull(standard, "Reward_STANDARD.asset not found.");
            float conceding = Mathf.Abs(standard.goalConceded);
            int cap = Mathf.Max(standard.maxEnvironmentSteps, 9000);

            foreach (string path in Directory.GetFiles("Training", "*.yaml"))
            {
                string text = File.ReadAllText(path);
                float rate = Weight(text, "action_rate_sq", path);
                float energy = Weight(text, "energy_penalty", path);
                Assert.Less(rate * 4f * cap / 8f, conceding, $"{path}: action_rate_sq ceiling outweighs conceding");
                Assert.Less(energy * cap, conceding, $"{path}: energy_penalty ceiling outweighs conceding");
                foreach (string key in new[] { "torque_sq", "spin_sq", "foot_slide" })
                    Assert.Less(Weight(text, key, path) * cap, conceding, $"{path}: {key} ceiling outweighs conceding");
            }
        }

        static float Weight(string yaml, string key, string path)
        {
            var match = System.Text.RegularExpressions.Regex.Match(yaml, @"^\s*" + key + @":\s*([0-9.eE+-]+)\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert.IsTrue(match.Success, $"{path} does not set {key}");
            float value = float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Greater(value, 0f, $"{path}: {key} must be a positive weight");
            return value;
        }

        [Test]
        public void PageTemplate_DrawsTheChromeOutsideTheCanvas()
        {
            string page = File.ReadAllText(Path.Combine("Assets", "WebGLTemplates", "Po", "index.html"));
            StringAssert.Contains("v{{{ PRODUCT_VERSION }}}", page, "the footer carries the build stamp");
            StringAssert.Contains("href=\"/diag\"", page, "the top bar links /diag");
            StringAssert.Contains("id=\"loading\"", page);
            StringAssert.DoesNotContain("{{{ WIDTH }}}", page, "the canvas fills the viewport; no fixed aspect");
            StringAssert.DoesNotContain("user-scalable=no", page, "pinch zoom stays allowed (axe meta-viewport)");
            StringAssert.Contains("<main id=\"frame\">", page, "the game sits in the page's <main> landmark (axe region)");
        }

        [Test]
        public void PanelSettings_ScaleForLandscapeAndPortrait()
        {
            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>("Assets/_Project/UI/PanelSettings.asset");
            Assert.IsNotNull(panel);
            Assert.AreEqual(UnityEngine.UIElements.PanelScaleMode.ScaleWithScreenSize, panel.scaleMode);
            Assert.AreEqual(UnityEngine.UIElements.PanelScreenMatchMode.Expand, panel.screenMatchMode,
                "Expand keeps the 1080x1920 layout whole on a 16:9 desktop and identical on a 9:16 phone");
        }
    }
}
