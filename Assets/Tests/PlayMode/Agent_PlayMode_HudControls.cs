using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// The 2026-11-07 HUD controls, on the live match scene:
    ///
    ///   - VIEW: one button that shows the overlays one at a time;
    ///   - the lower-left agent line, which rides the STATS switch;
    ///   - driver tags on the chips only when the pitch mixes bot and brain;
    ///   - the upper-right SETTINGS opening the sheet over the HUD (so the HUD's
    ///     own refresh runs after a change);
    ///   - the result screen's two ways on: REMATCH and CHANGE LINEUP.
    /// </summary>
    public class Agent_PlayMode_HudControls
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_Overlays.AllOff();
            Agent_SettingsSheet.Close();
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator Real(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        static IEnumerator LoadMatch()
        {
            SceneManager.LoadScene("SCN_Exhibition");
            for (int frame = 0; frame < 6; frame++) yield return null;
            yield return Real(1f);
        }

        static VisualElement HudRoot()
        {
            var hud = Object.FindAnyObjectByType<Agent_HUD>();
            Assert.IsNotNull(hud, "No HUD in the match scene");
            return hud.GetComponent<UIDocument>().rootVisualElement;
        }

        static void Click(Button button)
        {
            Assert.IsNotNull(button, "Cannot click a button that is not there");
            Assert.IsTrue(button.enabledInHierarchy, $"'{button.text}' is disabled");
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        [UnityTest]
        public IEnumerator TheViewButton_ShowsOneOverlayAtATime()
        {
            yield return LoadMatch();
            Agent_Overlays.AllOff();

            Assert.IsNull(HudRoot().Q<Button>("overlay-cycle"),
                "overlay-cycle should not clutter the default gameplay HUD (overlays live behind TAB / Settings).");

            int steps = 0;
            bool sawOne = false;
            do
            {
                Agent_Overlays.CycleSolo();
                yield return Real(0.1f);
                steps++;
                int on = Agent_Overlays.OnCount();
                Assert.LessOrEqual(on, 1, "CycleSolo left more than one overlay on.");
                if (on == 1) sawOne = true;
                Assert.Less(steps, Agent_Overlays.Count + 3, "CycleSolo never came back round to none.");
            }
            while (Agent_Overlays.OnCount() > 0);

            Assert.IsTrue(sawOne, "CycleSolo never showed an overlay.");
            Assert.AreEqual("VIEW", Agent_Overlays.SoloText(), "With nothing on, SoloText should read VIEW.");
        }

        [UnityTest]
        public IEnumerator TheAgentLine_RidesTheStatsSwitch()
        {
            bool previous = Agent_HUD.BroadcastEnabled;
            var telemetry = Object.FindAnyObjectByType<Agent_Telemetry>();
            try
            {
                Agent_HUD.BroadcastEnabled = false;
                yield return LoadMatch();
                telemetry = Object.FindAnyObjectByType<Agent_Telemetry>();
                Assert.IsNotNull(telemetry, "No Agent_Telemetry in the match scene");

                Assert.IsNull(HudRoot().Q<Button>("broadcast-toggle"),
                    "STATS toggle must not clutter the gameplay HUD; it belongs in the TAB panel.");

                var stream = HudRoot().Q<Label>("agent-stream");
                Assert.IsNotNull(stream, "HUD.uxml has no agent-stream lane");
                Assert.AreEqual(DisplayStyle.None, stream.resolvedStyle.display,
                    "The agent line is on the default HUD; it belongs behind STATS / TAB.");

                telemetry.SetVisible(true);
                yield return Real(0.4f);
                var statsToggle = telemetry.GetComponent<UIDocument>().rootVisualElement.Q<Button>("broadcast-toggle");
                Assert.IsNotNull(statsToggle, "TAB telemetry panel has no broadcast-toggle");
                Click(statsToggle);
                yield return Real(0.6f);
                Assert.AreNotEqual(DisplayStyle.None, stream.resolvedStyle.display,
                    "TAB STATS toggle did not bring the agent line up.");
                StringAssert.Contains("m/s", stream.text, "The agent line carries no speed.");
            }
            finally
            {
                if (telemetry != null) telemetry.SetVisible(false);
                Agent_HUD.BroadcastEnabled = previous;
            }
        }

        [UnityTest]
        public IEnumerator DriverTags_AppearOnlyWhenThePitchMixesBotAndBrain()
        {
            yield return LoadMatch();
            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env);

            bool anyBot = false;
            int fielded = 0;
            foreach (var agent in env.agents)
            {
                if (agent == null || agent.rewards == null) continue;
                fielded++;
                if (agent.RuleBased) anyBot = true;
            }

            int tags = HudRoot().Query<Label>(className: "chip__driver").ToList().Count;
            Assert.AreEqual(anyBot ? fielded : 0, tags,
                anyBot
                    ? "A bot is on the pitch, so every chip should say BOT or AI."
                    : "Every player is a trained brain; an AI tag under every chip says nothing.");
        }

        [UnityTest]
        public IEnumerator ChromeSettings_OpensTheSheetOverTheHud()
        {
            yield return LoadMatch();

            var chrome = Object.FindAnyObjectByType<Agent_Chrome>();
            Assert.IsNotNull(chrome, "No chrome in the match");
            Click(chrome.GetComponent<UIDocument>().rootVisualElement.Q<Button>("open-settings"));
            yield return Real(0.4f);

            Assert.IsTrue(Agent_SettingsSheet.IsOpen, "SETTINGS did not open the sheet");
            Assert.IsNotNull(HudRoot().Q<VisualElement>("settings-sheet"),
                "The sheet opened somewhere other than the HUD's panel, so the HUD's " +
                "refresh (broadcast switch, chips) will not run after a change.");

            // A second tap is a toggle.
            Click(chrome.GetComponent<UIDocument>().rootVisualElement.Q<Button>("open-settings"));
            yield return null;
            Assert.IsFalse(Agent_SettingsSheet.IsOpen, "A second tap on SETTINGS did not close the sheet");
        }

        [UnityTest]
        public IEnumerator TheResultScreen_OffersRematchAndChangeLineup()
        {
            yield return LoadMatch();
            var hud = Object.FindAnyObjectByType<Agent_HUD>();
            hud.ShowEndPanelNow();
            yield return Real(0.4f);

            var actions = HudRoot().Q<VisualElement>("end-actions");
            Assert.IsNotNull(actions, "No result screen");
            bool rematch = false, lineup = false;
            actions.Query<Button>().ForEach(button =>
            {
                if (button.text == "REMATCH") rematch = true;
                if (button.text == Agent_HUD.LINEUP_TEXT) lineup = true;
            });
            Assert.IsTrue(rematch, "The result screen has no REMATCH.");
            Assert.IsTrue(lineup, "The result screen has no CHANGE LINEUP.");
        }

        [UnityTest]
        public IEnumerator OnePlayerMode_TogglesFromMenu_AndDrivesGenericBlueBoxIn1v1And4v4()
        {
            Agent_MainMenu.ForgetLineup();
            Agent_MatchSetup.Clear();
            try
            {
                SceneManager.LoadScene("SCN_Menu");
                for (int i = 0; i < 4; i++) yield return null;

                var menu = Object.FindAnyObjectByType<Agent_MainMenu>();
                Assert.IsNotNull(menu, "No Agent_MainMenu in SCN_Menu");
                var menuRoot = menu.GetComponent<UIDocument>().rootVisualElement;

                var toggle1P = menuRoot.Q<Button>("toggle-1p");
                Assert.IsNotNull(toggle1P, "SCN_Menu has no toggle-1p button");
                Assert.IsFalse(menu.HumanMode, "Menu should default to AI vs AI after ForgetLineup");

                // Toggle 1P ON in 4v4 mode and launch match.
                Click(menuRoot.Q<Button>("mode-4v4"));
                yield return null;
                Click(toggle1P);
                yield return null;
                Assert.IsTrue(menu.HumanMode, "Clicking toggle-1p did not enable HumanMode");

                Click(menuRoot.Q<Button>("play"));
                for (int i = 0; i < 6; i++) yield return null;

                Assert.IsTrue(Agent_MatchSetup.HumanControl, "Match setup did not carry HumanControl=true");
                var env = Object.FindAnyObjectByType<Agent_EnvController>();
                Assert.IsNotNull(env);

                int humanCount = 0;
                int blueCount = 0;
                Agent_Soccer humanAgent = null;
                foreach (var agent in env.agents)
                {
                    if (agent == null) continue;
                    if (agent.team == Agent_Soccer.Team.Blue) blueCount++;
                    if (agent.HumanControlled)
                    {
                        humanCount++;
                        humanAgent = agent;
                    }
                }

                Assert.AreEqual(4, blueCount, "4v4 mode should spawn 4 Blue players");
                Assert.AreEqual(1, humanCount, "Exactly 1 Blue player should be human-controlled in 4v4");
                Assert.IsNotNull(humanAgent);
                Assert.IsFalse(humanAgent.RuleBased, "Human player should not be flagged as RuleBased bot");
                Assert.AreEqual(Agent_HumanController.HUMAN_PLAYER_NAME, humanAgent.rewards.playerName);
                var face = humanAgent.transform.Find("Face");
                Assert.IsTrue(face == null || !face.gameObject.activeSelf,
                    "Human player must be a generic Blue box with no active Face sprite");

                // Release opening countdown freeze and test live screen-relative movement + kick.
                Agent_TimeFreeze.ReleaseAll();
                var humanCtrl = humanAgent.GetComponent<Agent_HumanController>();
                Assert.IsNotNull(humanCtrl);

                Vector2 startPos = humanAgent.Body.position;
                humanCtrl.InjectedMoveInput = Vector2.up;
                humanCtrl.InjectedBoost = true;
                for (int step = 0; step < 60; step++) yield return new WaitForFixedUpdate();

                Assert.Greater(humanAgent.Body.position.y, startPos.y + 0.15f,
                    "Screen-relative Up input did not drive the generic Blue box upward on the pitch");

                // Verify 4v4 1P gameplay stays uncluttered (agent-stream hidden by default),
                // and enabling broadcast stats surfaces YOU's telemetry.
                var hud = Object.FindAnyObjectByType<Agent_HUD>();
                Assert.IsNotNull(hud);
                var hudRoot = hud.GetComponent<UIDocument>().rootVisualElement;
                var streamLabel = hudRoot.Q<Label>("agent-stream");
                Assert.IsNotNull(streamLabel);
                Assert.AreEqual(DisplayStyle.None, streamLabel.resolvedStyle.display,
                    "1P gameplay HUD should stay uncluttered with agent-stream hidden by default");
                hud.SetBroadcastStripVisible(true);
                yield return Real(0.35f);
                StringAssert.StartsWith("YOU  ·  ", streamLabel.text,
                    "Enabling broadcast stats should display YOU's speed and stamina telemetry");
                hud.SetBroadcastStripVisible(false);

                // Place ball just ahead of the human box and trigger kick lunge.
                env.Ball.position = humanAgent.Body.position + new Vector2(0f, 0.8f);
                env.Ball.linearVelocity = Vector2.zero;
                humanCtrl.InjectedKickPressed = true;
                yield return null;
                yield return new WaitForFixedUpdate();
                yield return null;

                Assert.Greater(env.Ball.linearVelocity.y, 2f,
                    "Active Kick/Lunge button did not propel the ball forward");
                Assert.Greater(humanCtrl.CurrentRingScale, 1.22f,
                    "Kick lunge should visually pulse the HumanRing indicator");

                // Verify on-screen 1P virtual thumbstick + boost + kick controls are visible and kick works.
                var stick = hudRoot.Q<VisualElement>("thumbstick-pad");
                var boostBtn = hudRoot.Q<Button>("human-boost");
                var kickBtn = hudRoot.Q<Button>("human-kick");
                Assert.IsNotNull(stick);
                Assert.IsNotNull(boostBtn);
                Assert.IsNotNull(kickBtn);
                Assert.AreNotEqual(DisplayStyle.None, stick.resolvedStyle.display, "1P thumbstick should be visible");
                Assert.AreNotEqual(DisplayStyle.None, boostBtn.resolvedStyle.display, "1P BOOST button should be visible");
                Assert.AreNotEqual(DisplayStyle.None, kickBtn.resolvedStyle.display, "1P KICK button should be visible");

                // Wait out kick cooldown and test virtual KICK button click.
                yield return Real(Agent_HumanController.KICK_COOLDOWN + 0.05f);
                env.Ball.position = humanAgent.Body.position + new Vector2(0f, 0.8f);
                env.Ball.linearVelocity = Vector2.zero;
                Click(kickBtn);
                yield return null;
                yield return new WaitForFixedUpdate();
                yield return null;
                Assert.Greater(env.Ball.linearVelocity.y, 2f,
                    "On-screen 1P KICK button did not propel the ball forward");
            }
            finally
            {
                Agent_MainMenu.ForgetLineup();
                Agent_MatchSetup.Clear();
            }
        }
    }
}
