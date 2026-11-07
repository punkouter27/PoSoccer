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

            var view = HudRoot().Q<Button>("overlay-cycle");
            Assert.IsNotNull(view, "The HUD has no VIEW button - on a phone the overlays are unreachable.");

            int steps = 0;
            bool sawOne = false;
            do
            {
                Click(view);
                yield return Real(0.35f);   // the label refreshes four times a second
                steps++;
                int on = Agent_Overlays.OnCount();
                Assert.LessOrEqual(on, 1, "VIEW left more than one overlay on.");
                if (on == 1)
                {
                    sawOne = true;
                    Assert.AreEqual(Agent_Overlays.SoloText(), view.text,
                        "The VIEW button does not name the overlay it is showing.");
                }
                Assert.Less(steps, Agent_Overlays.Count + 3, "VIEW never came back round to none.");
            }
            while (Agent_Overlays.OnCount() > 0);

            Assert.IsTrue(sawOne, "VIEW never showed an overlay.");
            Assert.AreEqual("VIEW", view.text, "With nothing on, the button should read VIEW.");
        }

        [UnityTest]
        public IEnumerator TheAgentLine_RidesTheStatsSwitch()
        {
            bool previous = Agent_HUD.BroadcastEnabled;
            try
            {
                Agent_HUD.BroadcastEnabled = false;
                yield return LoadMatch();

                var stream = HudRoot().Q<Label>("agent-stream");
                Assert.IsNotNull(stream, "HUD.uxml has no agent-stream lane");
                Assert.AreEqual(DisplayStyle.None, stream.resolvedStyle.display,
                    "The agent line is on the default HUD; it belongs behind STATS.");

                Click(HudRoot().Q<Button>("broadcast-toggle"));
                yield return Real(0.6f);
                Assert.AreNotEqual(DisplayStyle.None, stream.resolvedStyle.display,
                    "STATS did not bring the agent line up.");
                StringAssert.Contains("m/s", stream.text, "The agent line carries no speed.");
            }
            finally
            {
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
    }
}
