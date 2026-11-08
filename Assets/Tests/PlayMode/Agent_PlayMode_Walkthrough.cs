using System.Collections;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Visual walkthrough of the player's loop: menu -> match -> full time -> end
    /// panel (every tab) -> back to the menu, twice. At each stop it saves a
    /// screenshot and a dump of every visible label and button, so the screens can
    /// be reviewed without an editor window (CLAUDE.md: Unity is headless-only).
    ///
    /// A capture tool, not a gate: it runs only when POSOCCER_CAPTURE_DIR names an
    /// output folder and is skipped otherwise. Agent_PlayMode_GameFlow and
    /// Agent_PlayMode_FlowLoop own the assertions about the loop itself.
    /// </summary>
    public class Agent_PlayMode_Walkthrough
    {
        const string CAPTURE_DIR_VAR = "POSOCCER_CAPTURE_DIR";

        string _dir;
        int _shot;
        readonly StringBuilder _log = new StringBuilder();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++) yield return null;
        }

        static IEnumerator Realtime(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        static IEnumerator WaitUntilRealtime(System.Func<bool> condition, float timeout)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < until) yield return null;
        }

        static void Click(Button button, string what)
        {
            Assert.IsNotNull(button, what + ": no such button");
            Assert.IsTrue(button.enabledInHierarchy, what + ": button is disabled");
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        static Button FindButton(string text)
        {
            Button found = null;
            var documents = Object.FindObjectsByType<UIDocument>();
            for (int docIndex = 0; docIndex < documents.Length; docIndex++)
            {
                var root = documents[docIndex].rootVisualElement;
                if (root == null) continue;
                root.Query<Button>().ForEach(candidate =>
                {
                    if (found == null && candidate.text == text && candidate.resolvedStyle.display != DisplayStyle.None)
                        found = candidate;
                });
            }
            return found;
        }

        static bool EndPanelVisible(Agent_HUD hud)
        {
            if (hud == null) return false;
            var root = hud.GetComponent<UIDocument>().rootVisualElement;
            return root != null && root.Q("end-panel") != null;
        }

        static bool IsShown(VisualElement element)
        {
            for (var current = element; current != null; current = current.parent)
            {
                if (current.resolvedStyle.display == DisplayStyle.None) return false;
                if (current.resolvedStyle.visibility == Visibility.Hidden) return false;
                if (current.resolvedStyle.opacity <= 0.01f) return false;
            }
            return element.worldBound.width > 0f && element.worldBound.height > 0f;
        }

        /// <summary>Screenshot plus a text dump of everything visible on every UIDocument.</summary>
        IEnumerator Capture(string label)
        {
            // NOT WaitForEndOfFrame: it never resumes under the headless batchmode
            // runner (scripts/unity-test.ps1), which hung the first version of this
            // test forever. ScreenCapture.CaptureScreenshot queues the grab for the
            // end of the frame itself and simply writes nothing if no frame is
            // presented, so the text dump below is the record that always exists.
            yield return null;
            _shot++;
            string file = $"{_shot:00}_{label}.png";
            ScreenCapture.CaptureScreenshot(Path.Combine(_dir, file));
            yield return null;

            _log.AppendLine($"## {_shot:00} {label}  ({SceneManager.GetActiveScene().name}, {Screen.width}x{Screen.height}, file {file})");
            var documents = Object.FindObjectsByType<UIDocument>();
            for (int docIndex = 0; docIndex < documents.Length; docIndex++)
            {
                var root = documents[docIndex].rootVisualElement;
                if (root == null) continue;
                var texts = new StringBuilder();
                root.Query<TextElement>().ForEach(element =>
                {
                    if (string.IsNullOrWhiteSpace(element.text) || !IsShown(element)) return;
                    string kind = element is Button ? "button" : "text";
                    texts.AppendLine($"    [{kind}] {element.text.Replace('\n', ' ')}");
                });
                if (texts.Length == 0) continue;
                _log.AppendLine($"  {documents[docIndex].gameObject.name}:");
                _log.Append(texts);
            }
            _log.AppendLine();
        }

        static IEnumerator ScoreInto(Agent_EnvController env, bool blueNet)
        {
            bool ended = false;
            System.Action<Agent_Soccer.Team?> handler = _ => ended = true;
            env.EpisodeEnded += handler;
            float sign = blueNet ? -1f : 1f;
            var ball = env.Ball;
            ball.position = new Vector2(0f, sign * (env.PitchHalfExtents.y - 1.2f));
            ball.linearVelocity = new Vector2(0f, sign * 30f);
            ball.WakeUp();
            for (int tick = 0; tick < 400 && !ended; tick++) yield return new WaitForFixedUpdate();
            env.EpisodeEnded -= handler;
        }

        [UnityTest]
        public IEnumerator Walkthrough_MenuMatchEndMenu_TwoLaps()
        {
            _dir = System.Environment.GetEnvironmentVariable(CAPTURE_DIR_VAR);
            if (string.IsNullOrEmpty(_dir)) Assert.Ignore($"Capture tool: set {CAPTURE_DIR_VAR} to run it.");
            Directory.CreateDirectory(_dir);

            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(10);
            yield return Realtime(1.5f);
            yield return Capture("menu");

            for (int lap = 1; lap <= 2; lap++)
            {
                Click(FindButton("PLAY"), $"lap {lap} PLAY");
                yield return Frames(2);
                yield return Realtime(0.3f);
                yield return Capture($"lap{lap}_match_open");

                var hud = Object.FindAnyObjectByType<Agent_HUD>();
                var env = Object.FindAnyObjectByType<Agent_EnvController>();
                Assert.IsNotNull(hud, "no HUD");
                Assert.IsNotNull(env, "no env");
                hud.matchGoals = 2;

                yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 20f);
                yield return Realtime(8f);   // let the agents actually play
                yield return Capture($"lap{lap}_match_play");

                // Lap 1: blue wins 2-0. Lap 2: red scores first, blue then scores twice.
                if (lap == 2)
                {
                    yield return ScoreInto(env, blueNet: true);
                    yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 30f);
                }
                yield return ScoreInto(env, blueNet: false);
                yield return Realtime(0.6f);
                yield return Capture($"lap{lap}_goal");
                yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 30f);
                yield return Realtime(1f);
                yield return ScoreInto(env, blueNet: false);

                yield return WaitUntilRealtime(() => EndPanelVisible(hud), 40f);
                Assert.IsTrue(EndPanelVisible(hud), $"lap {lap}: end panel never appeared");
                yield return Realtime(1.2f);
                yield return Capture($"lap{lap}_end_summary");

                foreach (string tab in new[] { "STATS", "PLAYERS" })
                {
                    Click(FindButton(tab), $"lap {lap} tab {tab}");
                    yield return Frames(3);
                    yield return Realtime(0.4f);
                    yield return Capture($"lap{lap}_end_{tab.ToLowerInvariant()}");
                }

                Click(FindButton(Agent_HUD.LINEUP_TEXT), $"lap {lap} back to menu");
                yield return Frames(10);
                yield return Realtime(1f);
                Assert.AreEqual("SCN_Menu", SceneManager.GetActiveScene().name, $"lap {lap}: not back on the menu");
                yield return Capture($"lap{lap}_back_at_menu");
            }

            File.WriteAllText(Path.Combine(_dir, "walkthrough.txt"), _log.ToString());
        }
    }
}
