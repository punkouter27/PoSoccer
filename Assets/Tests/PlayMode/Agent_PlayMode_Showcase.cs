using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    public class Agent_PlayMode_Showcase
    {
        const string SHOWCASE_DIR_VAR = "POSOCCER_SHOWCASE_DIR";

        string _dir;
        readonly List<string> _metaEntries = new();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_Overlays.AllOff();
            Agent_Weather.CurrentCondition = Agent_Weather.Condition.Dry;
            Agent_SettingsSheet.Close();
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
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

        static Button FindButton(string text, VisualElement scope = null)
        {
            Button found = null;
            if (scope != null)
            {
                scope.Query<Button>().ForEach(b =>
                {
                    if (found == null && b.text == text && b.resolvedStyle.display != DisplayStyle.None)
                        found = b;
                });
                return found;
            }

            var docs = Object.FindObjectsByType<UIDocument>();
            for (int i = 0; i < docs.Length; i++)
            {
                var root = docs[i].rootVisualElement;
                if (root == null) continue;
                root.Query<Button>().ForEach(b =>
                {
                    if (found == null && b.text == text && b.resolvedStyle.display != DisplayStyle.None)
                        found = b;
                });
            }
            return found;
        }

        static void Click(Button button, string label)
        {
            Assert.IsNotNull(button, "Missing button: " + label);
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        static string Escape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        void RecordBounds(string shotName)
        {
            var boxes = new List<string>();
            var docs = Object.FindObjectsByType<UIDocument>();
            for (int i = 0; i < docs.Length; i++)
            {
                var root = docs[i].rootVisualElement;
                if (root == null) continue;
                float pw = root.worldBound.width;
                float ph = root.worldBound.height;
                if (float.IsNaN(pw) || pw <= 1f || float.IsNaN(ph) || ph <= 1f) continue;

                root.Query<VisualElement>().ForEach(el =>
                {
                    if (string.IsNullOrEmpty(el.name)) return;
                    if (el.resolvedStyle.display == DisplayStyle.None) return;
                    var wb = el.worldBound;
                    if (float.IsNaN(wb.width) || wb.width < 4f || wb.height < 4f) return;
                    float nx = wb.x / pw;
                    float ny = wb.y / ph;
                    float nw = wb.width / pw;
                    float nh = wb.height / ph;
                    boxes.Add($"\"{Escape(el.name)}\":[{nx:F4},{ny:F4},{nw:F4},{nh:F4}]");
                });
            }
            _metaEntries.Add($"\"{Escape(shotName)}\":{{{string.Join(",", boxes)}}}");
        }

        IEnumerator CapturePng(string relPath, string metaName = null)
        {
            yield return null;
            string full = Path.Combine(_dir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            ScreenCapture.CaptureScreenshot(full);
            yield return Frames(2);
            if (!string.IsNullOrEmpty(metaName)) RecordBounds(metaName);
        }

        static IEnumerator ScoreGoal(Agent_EnvController env, bool blueNet)
        {
            bool ended = false;
            System.Action<Agent_Soccer.Team?> h = _ => ended = true;
            env.EpisodeEnded += h;
            float sign = blueNet ? -1f : 1f;
            var ball = env.Ball;
            ball.position = new Vector2(0f, sign * (env.PitchHalfExtents.y - 1.0f));
            ball.linearVelocity = new Vector2(0f, sign * 32f);
            ball.WakeUp();
            for (int t = 0; t < 360 && !ended; t++) yield return new WaitForFixedUpdate();
            env.EpisodeEnded -= h;
        }

        [UnityTest]
        public IEnumerator Showcase_NewFeaturesAndGameplayFrames()
        {
            _dir = System.Environment.GetEnvironmentVariable(SHOWCASE_DIR_VAR);
            if (string.IsNullOrEmpty(_dir)) Assert.Ignore($"Set {SHOWCASE_DIR_VAR} to capture showcase.");
            Directory.CreateDirectory(_dir);

            Agent_MainMenu.ForgetLineup();
            Agent_MatchSetup.Clear();
            Agent_HUD.BroadcastEnabled = false;
            Agent_Overlays.AllOff();
            Agent_Weather.CurrentCondition = Agent_Weather.Condition.Dry;

            // 1. Consolidated 8-Trained-Character Menu
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(12);
            yield return Realtime(0.8f);
            yield return CapturePng("01_menu.png", "01_menu");

            // 2. 1v1 AI Match — Streamlined View + 1v1 GIF burst
            Click(FindButton("PLAY"), "1v1 PLAY");
            yield return Frames(8);
            var hud = Object.FindAnyObjectByType<Agent_HUD>();
            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(hud);
            Assert.IsNotNull(env);
            hud.matchGoals = 2;

            yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 18f);
            yield return Realtime(2.5f);
            yield return CapturePng("02_match_streamlined.png", "02_match_streamlined");

            for (int f = 0; f < 30; f++)
            {
                yield return Realtime(0.06f);
                yield return CapturePng($"gif_1v1/frame_{f:000}.png");
            }

            // 3. Overlays + STATS Broadcast Strip (Intent + Vision + ShotTracer, clean pitch)
            var hudRoot = hud.GetComponent<UIDocument>().rootVisualElement;
            var statsBtn = hudRoot.Q<Button>("broadcast-toggle");
            if (statsBtn != null) Click(statsBtn, "STATS toggle");
            Agent_Intent.Visible = true;
            Agent_VisionView.CurrentMode = Agent_VisionView.Mode.Focus;
            Agent_ShotTracer.Visible = true;
            yield return Realtime(1.2f);
            yield return CapturePng("03_overlays_stats.png", "03_overlays_stats");

            // 3b. BrainCam + Saliency
            Agent_Overlays.AllOff();
            var brainCam = Object.FindAnyObjectByType<Agent_BrainCam>();
            if (brainCam != null) brainCam.SetVisible(true);
            Agent_BrainCam.SaliencyEnabled = true;
            yield return Realtime(0.9f);
            yield return CapturePng("03b_braincam.png", "03b_braincam");

            Agent_Overlays.AllOff();
            if (statsBtn != null) Click(statsBtn, "STATS off");
            Agent_HUD.BroadcastEnabled = false;

            // 4. Dynamic Weather (Snowy)
            Agent_Weather.CurrentCondition = Agent_Weather.Condition.Snowy;
            yield return Realtime(2.8f);
            yield return CapturePng("04_weather_snowy.png", "04_weather_snowy");
            Agent_Weather.CurrentCondition = Agent_Weather.Condition.Dry;

            // 5. Settings Sheet open over live match
            var settingsBtn = FindButton("SETTINGS");
            if (settingsBtn != null) Click(settingsBtn, "SETTINGS");
            yield return Frames(6);
            yield return CapturePng("05_settings_sheet.png", "05_settings_sheet");
            Agent_SettingsSheet.Close();
            yield return Frames(4);

            // 6. Goal celebration + Replay GIF burst
            bool firstEnded = false;
            System.Action<Agent_Soccer.Team?> onFirst = _ => firstEnded = true;
            env.EpisodeEnded += onFirst;
            env.Ball.position = new Vector2(0.3f, env.PitchHalfExtents.y - 2.4f);
            env.Ball.linearVelocity = new Vector2(-0.4f, 28f);
            env.Ball.WakeUp();

            for (int f = 0; f < 32; f++)
            {
                yield return Realtime(0.07f);
                if (f == 14) RecordBounds("06_goal_replay");
                yield return CapturePng($"gif_goal_replay/frame_{f:000}.png");
            }
            env.EpisodeEnded -= onFirst;
            string replayFrame = Path.Combine(_dir, "gif_goal_replay/frame_014.png");
            if (File.Exists(replayFrame))
                File.Copy(replayFrame, Path.Combine(_dir, "06_goal_replay.png"), true);

            yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 25f);
            yield return Realtime(1.2f);

            // Ensure 2nd goal finishes the match
            if (!firstEnded)
            {
                yield return ScoreGoal(env, blueNet: false);
                yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 25f);
                yield return Realtime(1.2f);
            }
            yield return ScoreGoal(env, blueNet: false);

            yield return WaitUntilRealtime(() => hudRoot.Q("end-panel") != null, 35f);
            var endPanel = hudRoot.Q("end-panel");
            Assert.IsNotNull(endPanel, "End panel never appeared");
            yield return Realtime(0.8f);
            Click(FindButton("STATS", endPanel), "EndPanel STATS tab");
            yield return Frames(5);
            yield return CapturePng("07_end_panel_stats.png", "07_end_panel_stats");

            // 7. Return to Menu -> 4v4 + 1P Human Control Mode + 4v4 GIF burst
            var lineupBtn = endPanel.Q<Button>("end-lineup") ?? FindButton(Agent_HUD.LINEUP_TEXT, endPanel);
            Click(lineupBtn, "EndPanel CHANGE LINEUP");
            yield return Frames(10);
            yield return Realtime(0.5f);
            Assert.AreEqual("SCN_Menu", SceneManager.GetActiveScene().name);

            Click(FindButton("4v4"), "Mode 4v4");
            Click(FindButton("1P: OFF"), "Enable 1P");
            yield return Frames(4);
            Click(FindButton("PLAY"), "Play 4v4 1P");
            yield return Frames(8);

            hud = Object.FindAnyObjectByType<Agent_HUD>();
            env = Object.FindAnyObjectByType<Agent_EnvController>();
            Agent_Soccer human = null;
            foreach (var a in env.agents)
            {
                if (a != null && a.HumanControlled) { human = a; break; }
            }
            Assert.IsNotNull(human);
            var ctrl = human.GetComponent<Agent_HumanController>();
            yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 20f);

            for (int f = 0; f < 30; f++)
            {
                Vector2 toBall = env.Ball.position - human.Body.position;
                ctrl.InjectedMoveInput = toBall.sqrMagnitude > 0.01f ? toBall.normalized : Vector2.up;
                ctrl.InjectedBoost = f % 2 == 0;
                if (toBall.magnitude < Agent_HumanController.KICK_RADIUS)
                    ctrl.InjectedKickPressed = true;
                yield return Realtime(0.06f);
                if (f == 18) RecordBounds("08_4v4_1p");
                yield return CapturePng($"gif_4v4_1p/frame_{f:000}.png");
            }
            ctrl.InjectedMoveInput = Vector2.zero;
            ctrl.InjectedBoost = false;

            string f4v4 = Path.Combine(_dir, "gif_4v4_1p/frame_018.png");
            if (File.Exists(f4v4))
                File.Copy(f4v4, Path.Combine(_dir, "08_4v4_1p.png"), true);

            File.WriteAllText(Path.Combine(_dir, "meta.json"), "{" + string.Join(",", _metaEntries) + "}");
        }
    }
}
