using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Tournament mode end to end (2026-11-08), through the real buttons:
    /// menu -> 1v1 CUP -> BRACKET -> NEW -> PLAY NEXT -> match (intro card, faces on the
    /// pitch) -> full time (named winner, result card) -> BRACKET -> ... until a
    /// champion is crowned, with every played match in the Elo book.
    ///
    /// Goals are driven into the net (as in Agent_PlayMode_FlowLoop) with
    /// matchGoals dropped to 1, so the subject is the tournament state machine,
    /// not the football. Elo and the bracket are in-memory under batchmode
    /// (Agent_Elo / Agent_Tournament.Persist), so this never touches saved data.
    /// </summary>
    public class Agent_PlayMode_TournamentFlow
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            Agent_Tournament.ResetForTests();
            // The cup test leaves the menu on 1v1 CUP; later tests click PLAY expecting a match.
            Agent_MainMenu.ForgetLineup();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++) yield return null;
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

        static VisualElement MenuRoot() => Object.FindAnyObjectByType<Agent_MainMenu>().GetComponent<UIDocument>().rootVisualElement;

        static Button ButtonIn(VisualElement root, string text)
        {
            Button found = null;
            root.Query<Button>().ForEach(candidate => { if (found == null && candidate.text == text) found = candidate; });
            return found;
        }

        static IEnumerator ScoreIntoRedNet(Agent_EnvController env)
        {
            bool ended = false;
            System.Action<Agent_Soccer.Team?> handler = _ => ended = true;
            env.EpisodeEnded += handler;
            var ball = env.Ball;
            ball.position = new Vector2(0f, env.PitchHalfExtents.y - 1.2f);
            ball.linearVelocity = new Vector2(0f, 30f);
            ball.WakeUp();
            for (int tick = 0; tick < 400 && !ended; tick++) yield return new WaitForFixedUpdate();
            env.EpisodeEnded -= handler;
        }

        [UnityTest]
        public IEnumerator Tournament_PlaysToAChampion_ThroughTheRealButtons()
        {
            Agent_Tournament.ResetForTests();
            SceneManager.LoadScene("SCN_Menu");
            yield return Frames(4);

            Click(MenuRoot().Q<Button>("mode-cup1"), "menu 1v1 CUP");
            yield return Frames(2);
            Click(MenuRoot().Q<Button>("play"), "menu BRACKET");
            yield return Frames(2);
            Assert.IsNotNull(MenuRoot().Q(Agent_BracketView.OVERLAY_NAME), "1v1 CUP -> BRACKET did not open the bracket");
            Click(ButtonIn(MenuRoot(), Agent_BracketView.NEW_TEXT), "bracket NEW");
            yield return Frames(2);

            var bracket = Agent_Tournament.Bracket;
            Assert.IsNotNull(bracket, "no bracket after NEW");
            int entrants = bracket.EntrantCount;
            Assert.GreaterOrEqual(entrants, 2, "the trained roster should give at least two entrants");
            Assert.LessOrEqual(entrants, Agent_Bracket.SIZE);
            int expectedMatches = entrants - 1;

            int played = 0;
            while (!Agent_Tournament.Bracket.Finished)
            {
                Assert.Less(played, expectedMatches, "more matches than a knockout of this size needs");
                string where = $"match {played + 1}/{expectedMatches}";

                Click(ButtonIn(MenuRoot(), Agent_BracketView.PLAY_TEXT), where + " PLAY NEXT");
                yield return Frames(4);
                Assert.AreEqual("SCN_Exhibition", SceneManager.GetActiveScene().name, where + ": not in the match");
                Assert.IsTrue(Agent_Tournament.InMatch, where + ": the match is not marked in progress");
                string blue = Agent_Tournament.BlueName;

                var hud = Object.FindAnyObjectByType<Agent_HUD>();
                var env = Object.FindAnyObjectByType<Agent_EnvController>();
                var hudRoot = hud.GetComponent<UIDocument>().rootVisualElement;
                Assert.IsNotNull(hudRoot.Q(Agent_MatchIntro.CARD_NAME), where + ": no player intro card at kickoff");

                // Every player whose character has a face shows it on the pitch.
                foreach (var agent in env.GetComponentsInChildren<Agent_Soccer>())
                {
                    if (agent.rewards == null || agent.rewards.faceTexture == null) continue;
                    var face = agent.transform.Find("Face");
                    Assert.IsNotNull(face, $"{where}: {agent.rewards.playerName} has a face texture but no face on the pitch");
                    Assert.IsTrue(face.gameObject.activeInHierarchy, $"{where}: {agent.rewards.playerName}'s face is hidden");
                    Assert.AreEqual(180f, face.localEulerAngles.z, 0.5f, "the chin must point where the player faces");
                }

                hud.matchGoals = 1;
                yield return WaitUntilRealtime(() => !Agent_TimeFreeze.IsFrozen, 20f);
                yield return ScoreIntoRedNet(env);   // blue scores, blue wins
                yield return WaitUntilRealtime(() => hudRoot.Q("end-panel") != null, 40f);
                Assert.IsNotNull(hudRoot.Q("end-panel"), where + ": no end panel");

                var headline = hudRoot.Q<Label>("end-headline");
                Assert.AreEqual($"{blue} WINS", headline.text, where + ": the end panel should name the winner");
                Assert.IsNotNull(hudRoot.Q("tournament-result"), where + ": no tournament result card");
                Assert.IsFalse(Agent_Tournament.InMatch, where + ": the result was not recorded");
                Assert.IsNotNull(Agent_Elo.Get(blue), where + ": the winner has no Elo record");
                Assert.Greater(Agent_Elo.RatingOf(blue), Agent_Elo.START_RATING - 64f);
                played++;

                Click(ButtonIn(hudRoot, Agent_TournamentMatch.BRACKET_BUTTON_TEXT), where + " BRACKET");
                yield return Frames(6);
                Assert.AreEqual("SCN_Menu", SceneManager.GetActiveScene().name, where + ": BRACKET did not return to the menu");
                Assert.IsNotNull(MenuRoot().Q(Agent_BracketView.OVERLAY_NAME), where + ": the bracket did not reopen");
                Assert.AreEqual(0, Agent_TimeFreeze.HolderCount, where + ": the clock is still held at the menu");
            }

            Assert.AreEqual(expectedMatches, played, "a knockout of N players is N - 1 matches");
            Assert.IsFalse(string.IsNullOrEmpty(Agent_Tournament.Bracket.Champion), "no champion");
            StringAssert.Contains("WINS 1v1 CUP", Agent_Tournament.LastResult);
            Assert.IsFalse(ButtonIn(MenuRoot(), Agent_BracketView.PLAY_TEXT).enabledInHierarchy,
                "PLAY NEXT should be disabled once there is a champion");
        }
    }
}
