using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins tournament mode's pure rules (2026-11-08): the 16-slot bracket with
    /// byes (Agent_Bracket), the Elo arithmetic (Agent_Elo.Rate / Expected) and
    /// the procedural placeholder voice (Agent_VoiceSynth).
    /// </summary>
    public sealed class Agent_EditMode_Tournament
    {
        // -- Bracket -----------------------------------------------------------

        [Test]
        public void FourEntrants_GetByesToTheSemis_TopSeedsMeetLast()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B", "C", "D" });

            Assert.AreEqual(4, bracket.EntrantCount);
            Assert.IsTrue(bracket.TryNextMatch(out int round, out int match));
            Assert.AreEqual(2, round, "with four players every earlier round is byes");

            var pairings = new HashSet<string>();
            for (int semi = 0; semi < 2; semi++)
            {
                string a = bracket.Entrant(2, semi, 0), b = bracket.Entrant(2, semi, 1);
                pairings.Add(string.CompareOrdinal(a, b) < 0 ? a + b : b + a);
            }
            CollectionAssert.AreEquivalent(new[] { "AD", "BC" }, pairings, "seed 1 meets seed 4, seed 2 meets seed 3");
        }

        [Test]
        public void PlayingEveryMatch_CrownsOneChampion()
        {
            var names = new List<string>();
            for (int seed = 0; seed < 16; seed++) names.Add("P" + seed);
            var bracket = Agent_Bracket.Seeded(names);

            int played = 0;
            while (bracket.TryNextMatch(out int round, out int match))
            {
                string winner = bracket.Entrant(round, match, 0);   // left side always wins
                Assert.IsTrue(bracket.Record(round, match, winner, "1-0"));
                played++;
                Assert.Less(played, 20, "the bracket never finished");
            }
            Assert.AreEqual(15, played, "a full 16 bracket is 15 matches");
            Assert.IsTrue(bracket.Finished);
            Assert.AreEqual("P0", bracket.Champion, "seed 1 sits top-left and won every match");
        }

        [Test]
        public void ThreeEntrants_PlayTwoMatches()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B", "C" });
            int played = 0;
            while (bracket.TryNextMatch(out int round, out int match))
            {
                bracket.Record(round, match, bracket.Entrant(round, match, 1), "2-1");
                played++;
            }
            Assert.AreEqual(2, played);
            Assert.IsTrue(bracket.Finished);
        }

        [Test]
        public void OneEntrant_IsChampionWithoutPlaying_NoEntrantsIsNobody()
        {
            var solo = Agent_Bracket.Seeded(new List<string> { "SOLO" });
            Assert.IsTrue(solo.Finished);
            Assert.AreEqual("SOLO", solo.Champion);
            Assert.IsFalse(solo.TryNextMatch(out _, out _));

            var empty = Agent_Bracket.Seeded(new List<string>());
            Assert.IsTrue(empty.Finished);
            Assert.AreEqual(Agent_Bracket.BYE, empty.Champion);
        }

        [Test]
        public void Record_RejectsAStrangerAndASecondResult()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B" });
            Assert.IsTrue(bracket.TryNextMatch(out int round, out int match));
            Assert.IsFalse(bracket.Record(round, match, "Z", "1-0"), "a name not in the match");
            Assert.IsTrue(bracket.Record(round, match, "B", "1-0"));
            Assert.IsFalse(bracket.Record(round, match, "A", "1-0"), "a decided match stays decided");
            Assert.AreEqual("B", bracket.Champion);
        }

        [Test]
        public void Bracket_SurvivesJsonRoundTrip()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B", "C", "D", "E" });
            bracket.TryNextMatch(out int round, out int match);
            bracket.Record(round, match, bracket.Entrant(round, match, 0), "3-2");

            var copy = JsonUtility.FromJson<Agent_Bracket>(JsonUtility.ToJson(bracket));
            CollectionAssert.AreEqual(bracket.slots, copy.slots);
            CollectionAssert.AreEqual(bracket.decided, copy.decided);
            CollectionAssert.AreEqual(bracket.winners, copy.winners);
        }

        // -- Elo ---------------------------------------------------------------

        [Test]
        public void Elo_EqualPlayers_ExpectHalf_AndTheWinnerGainsHalfOfK()
        {
            Assert.AreEqual(0.5f, Agent_Elo.Expected(1500f, 1500f), 1e-5f);
            var (a, b) = Agent_Elo.Rate(1500f, 1500f, 1f);
            Assert.AreEqual(1500f + Agent_Elo.K_FACTOR / 2f, a, 1e-3f);
            Assert.AreEqual(1500f - Agent_Elo.K_FACTOR / 2f, b, 1e-3f);
        }

        [Test]
        public void Elo_ConservesTheTotal_AndAnUpsetMovesMore()
        {
            var (favourite, underdog) = Agent_Elo.Rate(1700f, 1400f, 0f);   // the favourite loses
            Assert.AreEqual(3100f, favourite + underdog, 1e-3f);
            float upsetSwing = 1700f - favourite;

            var (favourite2, _) = Agent_Elo.Rate(1700f, 1400f, 1f);         // the favourite wins
            float expectedSwing = favourite2 - 1700f;
            Assert.Greater(upsetSwing, expectedSwing * 3f);
            Assert.Greater(Agent_Elo.Expected(1700f, 1400f), 0.8f);
        }

        [Test]
        public void Elo_DrawBetweenEquals_ChangesNothing()
        {
            var (a, b) = Agent_Elo.Rate(1520f, 1520f, 0.5f);
            Assert.AreEqual(1520f, a, 1e-4f);
            Assert.AreEqual(1520f, b, 1e-4f);
        }

        // -- Voices ------------------------------------------------------------

        [Test]
        public void EveryVoiceAddress_RendersAudibleBoundedSound()
        {
            foreach (Agent_VoiceMood mood in System.Enum.GetValues(typeof(Agent_VoiceMood)))
            {
                for (int line = 1; line <= Agent_VoiceSynth.LINES; line++)
                {
                    for (int level = 1; level <= Agent_VoiceSynth.LEVELS; level++)
                    {
                        float[] samples = Agent_VoiceSynth.Render("MATT", mood, line, level, 0.92f);
                        Assert.Greater(samples.Length, Agent_VoiceSynth.SAMPLE_RATE / 20, $"{mood} {line}/{level} is too short");
                        float peak = Peak(samples);
                        Assert.Greater(peak, 0.1f, $"{mood} {line}/{level} is silent");
                        Assert.LessOrEqual(peak, 1f, $"{mood} {line}/{level} clips");
                    }
                }
            }
        }

        [Test]
        public void VoiceLevel_IsLouder_AndRenderIsDeterministic()
        {
            foreach (Agent_VoiceMood mood in System.Enum.GetValues(typeof(Agent_VoiceMood)))
            {
                float weak = Peak(Agent_VoiceSynth.Render("KIM", mood, 2, 1, 1.5f));
                float strong = Peak(Agent_VoiceSynth.Render("KIM", mood, 2, 5, 1.5f));
                Assert.Greater(strong, weak * 2f, $"{mood}: level 5 should be far louder than level 1");
            }
            CollectionAssert.AreEqual(
                Agent_VoiceSynth.Render("NICK", Agent_VoiceMood.Insult, 3, 4, 1.25f),
                Agent_VoiceSynth.Render("NICK", Agent_VoiceMood.Insult, 3, 4, 1.25f));
        }

        [Test]
        public void VoiceLines_AndCharacters_SoundDifferent()
        {
            float[] lineOne = Agent_VoiceSynth.Render("ALAN", Agent_VoiceMood.Happy, 1, 3, 0.95f);
            float[] lineTwo = Agent_VoiceSynth.Render("ALAN", Agent_VoiceMood.Happy, 2, 3, 0.95f);
            float[] otherCharacter = Agent_VoiceSynth.Render("MAYA", Agent_VoiceMood.Happy, 1, 3, 1.9f);
            Assert.IsFalse(Same(lineOne, lineTwo), "two lines of one character rendered identically");
            Assert.IsFalse(Same(lineOne, otherCharacter), "two characters rendered identically");
        }

        [Test]
        public void VoiceResourcePath_FollowsTheDocumentedName()
        {
            Assert.AreEqual("Voices/MATT_INSULT_3_5", Agent_Voices.ResourcePath("MATT", Agent_VoiceMood.Insult, 3, 5));
            Assert.AreEqual("Voices/KIM_HAPPY_1_1", Agent_Voices.ResourcePath("KIM", Agent_VoiceMood.Happy, 1, 1));
        }

        static float Peak(float[] samples)
        {
            float peak = 0f;
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++) peak = Mathf.Max(peak, Mathf.Abs(samples[sampleIndex]));
            return peak;
        }

        static bool Same(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int sampleIndex = 0; sampleIndex < a.Length; sampleIndex++) if (Mathf.Abs(a[sampleIndex] - b[sampleIndex]) > 1e-6f) return false;
            return true;
        }
    }
}
