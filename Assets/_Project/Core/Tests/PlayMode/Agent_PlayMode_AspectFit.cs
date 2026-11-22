using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Every screen fits a 16:9, 19.5:9 and 21:9 portrait phone with nothing
    /// outside the safe area and no vertical scrolling - menu, every settings tab,
    /// the match HUD with every optional readout on, pause, the health panel and
    /// the result screen.
    ///
    /// HOW AN ASPECT IS SIMULATED. The Game View cannot be resized from a test
    /// without perturbing every later measurement (see Agent_PlayMode_Portrait),
    /// but every screen in this game lays out inside Agent_UIStyle.BindSafeArea.
    /// So the test injects a safe rectangle with the Game View's full width and
    /// the HEIGHT of the target aspect (width x 16/9, x 19.5/9, x 21/9), centred.
    /// The panel is match-width at 1080, so width is the same at every aspect and
    /// height is the only thing that differs - the injected rectangle is exactly
    /// the room a phone of that shape gives the UI.
    ///
    /// The limit is the Game View itself: an aspect TALLER than the Game View
    /// cannot be produced by shrinking. Those aspects are reported, not faked; the
    /// test is inconclusive only if none could run. 16:9 - the shortest, and the
    /// one every overflow shows up on first - runs on any portrait Game View.
    /// </summary>
    public class Agent_PlayMode_AspectFit
    {
        static readonly (string Label, float HeightOverWidth)[] Aspects =
        {
            ("16:9", 16f / 9f),
            ("19.5:9", 19.5f / 9f),
            ("21:9", 21f / 9f),
        };

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_UIStyle.SafeAreaOverride = null;
            Agent_SettingsSheet.Close();
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++) yield return null;
        }

        static IEnumerator Real(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        [UnityTest]
        public IEnumerator EveryScreen_FitsEveryShippedAspect_WithoutScrolling()
        {
            bool previousBroadcast = Agent_HUD.BroadcastEnabled;
            var ran = new List<string>();
            var skipped = new List<string>();

            try
            {
                foreach (var (label, heightOverWidth) in Aspects)
                {
                    float height = Mathf.Round(Screen.width * heightOverWidth);
                    if (height > Screen.height + 1f)
                    {
                        skipped.Add($"{label} needs {height:0} px of a {Screen.height} px Game View");
                        continue;
                    }

                    float bottom = Mathf.Floor((Screen.height - height) * 0.5f);
                    float top = Screen.height - height - bottom;
                    Agent_UIStyle.SafeAreaOverride = new Rect(0f, bottom, Screen.width, height);
                    ran.Add(label);

                    // ---------------- menu + every settings tab ----------------
                    SceneManager.LoadScene("SCN_Menu");
                    yield return Frames(4);
                    yield return Real(0.6f);
                    AssertFits($"{label} menu", top, bottom);

                    var chrome = Object.FindAnyObjectByType<Agent_Chrome>();
                    Assert.IsNotNull(chrome, $"{label}: no chrome on the menu");
                    Click(chrome.GetComponent<UIDocument>().rootVisualElement.Q<Button>("open-settings"));
                    yield return Frames(3);
                    yield return Real(0.4f);
                    Assert.IsTrue(Agent_SettingsSheet.IsOpen, $"{label}: SETTINGS did not open the sheet");
                    foreach (Agent_SettingsSheet.Tab tab in System.Enum.GetValues(typeof(Agent_SettingsSheet.Tab)))
                    {
                        Agent_SettingsSheet.ShowTab(tab);
                        yield return Frames(3);
                        AssertFits($"{label} settings / {tab}", top, bottom);
                    }
                    Agent_SettingsSheet.Close();

                    // ---------------- match, every readout on ----------------
                    Agent_HUD.BroadcastEnabled = true;
                    SceneManager.LoadScene("SCN_Exhibition");
                    yield return Frames(6);
                    yield return Real(1.5f);

                    var hud = Object.FindAnyObjectByType<Agent_HUD>();
                    Assert.IsNotNull(hud, $"{label}: no HUD in the match");
                    AssertFits($"{label} match HUD (STATS on)", top, bottom);

                    hud.TogglePause();
                    yield return Frames(3);
                    yield return Real(0.4f);
                    AssertFits($"{label} pause", top, bottom);
                    hud.TogglePause();
                    yield return Frames(2);

                    var telemetry = Object.FindAnyObjectByType<Agent_Telemetry>();
                    Assert.IsNotNull(telemetry, $"{label}: no health panel in the match");
                    telemetry.SetVisible(true);
                    yield return Frames(4);
                    AssertFits($"{label} health panel", top, bottom);
                    telemetry.SetVisible(false);
                    yield return null;

                    hud.ShowEndPanelNow();
                    yield return Frames(3);
                    yield return Real(0.4f);
                    foreach (Agent_HUD.EndTab endTab in new[]
                             { Agent_HUD.EndTab.Summary, Agent_HUD.EndTab.Stats, Agent_HUD.EndTab.Players })
                    {
                        hud.ShowEndTab(endTab);
                        yield return Frames(3);
                        AssertFits($"{label} result / {endTab}", top, bottom);
                    }

                    Agent_TimeFreeze.ReleaseAll();
                    Agent_UIStyle.SafeAreaOverride = null;
                }
            }
            finally
            {
                Agent_HUD.BroadcastEnabled = previousBroadcast;
                Agent_UIStyle.SafeAreaOverride = null;
                Agent_SettingsSheet.Close();
                Agent_TimeFreeze.ReleaseAll();
            }

            if (skipped.Count > 0)
            {
                Debug.Log("Agent_PlayMode_AspectFit: not simulated (taller than the Game View): " +
                          string.Join("; ", skipped));
            }
            if (ran.Count == 0)
            {
                Assert.Inconclusive("The Game View is shorter than 16:9 portrait, so no aspect could be " +
                                    "simulated. Set the Game View to a portrait resolution.");
            }
        }

        // ------------------------------------------------------------------

        static void Click(Button button)
        {
            Assert.IsNotNull(button, "Cannot click a button that is not there");
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        /// <summary>
        /// Every drawn label and button on every panel lies inside the injected
        /// safe rectangle (converted into that panel's units), and no panel holds a
        /// ScrollView. Content inside the result screen's pane is clipped by design,
        /// so the pane is checked rather than what it holds.
        /// </summary>
        static void AssertFits(string screen, float topInsetPx, float bottomInsetPx)
        {
            const float SLACK = 2f;
            int checkedCount = 0;
            var offenders = new StringBuilder();

            foreach (var doc in Object.FindObjectsByType<UIDocument>())
            {
                VisualElement root = doc.rootVisualElement;
                if (root == null) continue;

                root.Query<ScrollView>().ForEach(scroll =>
                    offenders.AppendLine($"  ScrollView '{scroll.name}' on {doc.gameObject.name}"));

                Rect panel = root.worldBound;
                if (float.IsNaN(panel.width) || panel.width <= 1f) continue;

                float scale = Screen.width / panel.width;
                float safeTop = panel.yMin + topInsetPx / scale;
                float safeBottom = panel.yMax - bottomInsetPx / scale;

                root.Query<VisualElement>().ForEach(element =>
                {
                    if (element is not (Label or Button)) return;
                    if (!IsDrawn(element)) return;
                    if (HasAncestorNamed(element, "end-body")) return;

                    Rect r = element.worldBound;
                    if (float.IsNaN(r.x) || float.IsNaN(r.y)) return;
                    if (r.width <= 0.5f || r.height <= 0.5f) return;
                    checkedCount++;

                    if (r.yMin < safeTop - SLACK || r.yMax > safeBottom + SLACK ||
                        r.xMin < panel.xMin - SLACK || r.xMax > panel.xMax + SLACK)
                    {
                        string text = element is Button b && !string.IsNullOrEmpty(b.text) ? b.text
                            : element is Label l && !string.IsNullOrEmpty(l.text) ? l.text : element.name;
                        offenders.AppendLine(
                            $"  '{text}' on {doc.gameObject.name}: y {r.yMin:F0}..{r.yMax:F0} " +
                            $"vs safe y {safeTop:F0}..{safeBottom:F0}");
                    }
                });
            }

            Assert.Greater(checkedCount, 0, $"{screen}: nothing was laid out, so this asserted nothing");
            Assert.AreEqual(string.Empty, offenders.ToString(),
                $"{screen}: does not fit - outside the safe area or scrolling:\n{offenders}");
        }

        static bool IsDrawn(VisualElement element)
        {
            for (VisualElement current = element; current != null; current = current.parent)
            {
                if (current.resolvedStyle.display == DisplayStyle.None) return false;
                if (current.resolvedStyle.visibility == Visibility.Hidden) return false;
                if (current.resolvedStyle.opacity < 0.05f) return false;
            }
            return true;
        }

        static bool HasAncestorNamed(VisualElement element, string name)
        {
            for (VisualElement current = element.parent; current != null; current = current.parent)
            {
                if (current.name == name) return true;
            }
            return false;
        }
    }
}
