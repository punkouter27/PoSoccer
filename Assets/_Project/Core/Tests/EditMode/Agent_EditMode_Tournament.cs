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

        [Test]
        public void FourSlotBracket_IsSemisThenFinal()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "T1", "T2", "T3", "T4" }, 4);
            Assert.AreEqual(2, bracket.Rounds);
            Assert.AreEqual("SEMI-FINALS", bracket.RoundName(0));
            Assert.AreEqual("FINAL", bracket.RoundName(1));

            int played = 0;
            while (bracket.TryNextMatch(out int round, out int match))
            {
                bracket.Record(round, match, bracket.Entrant(round, match, 0), "1-0");
                played++;
            }
            Assert.AreEqual(3, played, "four teams play two semis and a final");
            Assert.AreEqual("T1", bracket.Champion);

            var three = Agent_Bracket.Seeded(new List<string> { "T1", "T2", "T3" }, 4);
            Assert.IsTrue(three.TryNextMatch(out int firstRound, out _));
            Assert.AreEqual(0, firstRound, "seeds 2 and 3 meet in a semi; seed 1 has the bye");
        }

        [Test]
        public void SixteenSlotNames_MatchTheOldFixedBracket()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B" });
            Assert.AreEqual(16, bracket.Size);
            Assert.AreEqual("ROUND OF 16", bracket.RoundName(0));
            Assert.AreEqual("QUARTER-FINALS", bracket.RoundName(1));
            Assert.AreEqual("FINAL", bracket.RoundName(3));
        }

        [Test]
        public void OldSave_WithoutASize_ReadsAsSixteen()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B", "C" });
            string json = JsonUtility.ToJson(bracket).Replace("\"size\":16,", "");
            var copy = JsonUtility.FromJson<Agent_Bracket>(json);
            Assert.AreEqual(16, copy.Size);
            Assert.IsTrue(copy.IsWellFormed);
        }

        // -- Tournament standard (2026-11-22) ------------------------------------

        [Test]
        public void Standard_SixteenSeeds_PairInTheStandardOrder()
        {
            var names = new List<string>();
            for (int seed = 1; seed <= 16; seed++) names.Add("S" + seed);
            var bracket = Agent_Bracket.Seeded(names);

            int[,] expected = { { 1, 16 }, { 8, 9 }, { 4, 13 }, { 5, 12 }, { 2, 15 }, { 7, 10 }, { 3, 14 }, { 6, 11 } };
            for (int match = 0; match < 8; match++)
            {
                Assert.AreEqual("S" + expected[match, 0], bracket.Entrant(0, match, 0), $"match {match + 1}, top");
                Assert.AreEqual("S" + expected[match, 1], bracket.Entrant(0, match, 1), $"match {match + 1}, bottom");
                Assert.AreEqual(expected[match, 0], bracket.SeedOfSlot(2 * match));
                Assert.AreEqual(expected[match, 1], bracket.SeedOfSlot(2 * match + 1));
            }
        }

        [Test]
        public void Standard_ByesTakeTheBottomSeeds_AndTheirOpponentsAdvanceUnfought()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B", "C", "D", "E" });
            for (int slot = 0; slot < bracket.Size; slot++)
            {
                bool bye = string.IsNullOrEmpty(bracket.slots[slot]);
                Assert.AreEqual(bracket.SeedOfSlot(slot) > 5, bye, $"slot {slot} (seed {bracket.SeedOfSlot(slot)})");
            }
            // Seed 1 (A) meets seed 16, a BYE: through without playing.
            Assert.IsTrue(bracket.IsDecided(0, 0));
            Assert.AreEqual("A", bracket.WinnerOf(0, 0));
            Assert.AreEqual("BYE", bracket.ScoreOf(0, 0));
        }

        [Test]
        public void Standard_ByeVersusBye_SendsAByeOn()
        {
            var bracket = Agent_Bracket.Seeded(new List<string> { "A", "B" });
            // Match 2 is seeds 8 v 9: both BYEs.
            Assert.IsTrue(bracket.IsDecided(0, 1));
            Assert.AreEqual(Agent_Bracket.BYE, bracket.WinnerOf(0, 1));
            Assert.IsTrue(bracket.TryNextMatch(out int round, out _));
            Assert.AreEqual(3, round, "seeds 1 and 2 are walked through to the final");
        }

        [Test]
        public void Standard_ADraw_GoesToTheHigherSeed()
        {
            var names = new List<string>();
            for (int seed = 1; seed <= 16; seed++) names.Add("S" + seed);
            var bracket = Agent_Bracket.Seeded(names);

            Assert.AreEqual("S8", bracket.HigherSeed(0, 1));
            Assert.IsTrue(bracket.RecordDraw(0, 1, "1-1"));
            Assert.AreEqual("S8", bracket.WinnerOf(0, 1));

            // Bottom side the better seed: still the better seed.
            Assert.IsTrue(bracket.Record(0, 0, "S16", "2-1"));
            Assert.AreEqual("S8", bracket.HigherSeed(1, 0), "seed 8 outranks seed 16");
        }

        [Test]
        public void Standard_EmptySlotsDisplayAsBYE()
        {
            Assert.AreEqual("BYE", Agent_Bracket.BYE_LABEL);
            Assert.AreEqual("BYE", Agent_Bracket.Display(Agent_Bracket.BYE));
            Assert.AreEqual("MATT", Agent_Bracket.Display("MATT"));
        }

        [Test]
        public void Standard_EloStartsAt1000_WithK32()
        {
            Assert.AreEqual(1000f, Agent_Elo.START_RATING);
            Assert.AreEqual(32f, Agent_Elo.K_FACTOR);
            Assert.AreEqual(Agent_Elo.START_RATING, Agent_Elo.RatingOf("NOBODY-EVER-RATED-" + System.Guid.NewGuid()));
        }

        [Test]
        public void SnakeDraft_BalancesTeams_AndNamesThemAfterTheCaptain()
        {
            var names = new List<string>();
            for (int seed = 1; seed <= 16; seed++) names.Add("P" + seed);
            var teams = Agent_Tournament.DraftTeams(names);

            Assert.AreEqual(4, teams.Count);
            CollectionAssert.AreEqual(new[] { "P1", "P8", "P9", "P16" }, teams[0].members);
            CollectionAssert.AreEqual(new[] { "P4", "P5", "P12", "P13" }, teams[3].members);
            Assert.AreEqual("TEAM P1", teams[0].name);

            // Sum of seeds is equal for every team: 34.
            foreach (var team in teams)
            {
                int total = 0;
                foreach (string member in team.members) total += int.Parse(member.Substring(1));
                Assert.AreEqual(34, total, team.name + " is not balanced");
            }
        }

        [Test]
        public void Draft_UsesOnlyFullTeams()
        {
            var nine = new List<string> { "A", "B", "C", "D", "E", "F", "G", "H", "I" };
            var teams = Agent_Tournament.DraftTeams(nine);
            Assert.AreEqual(2, teams.Count, "nine players make two teams of four");
            Assert.AreEqual(Agent_Tournament.TEAM_SIZE, teams[1].members.Length);
            Assert.AreEqual(0, Agent_Tournament.DraftTeams(new List<string> { "A", "B", "C" }).Count);
            Assert.AreEqual(Agent_Tournament.MAX_TEAMS, Agent_Tournament.TeamCount(20));
        }

        [Test]
        public void TeamCup_StartsOnlyWithTwoTeams_AndKeepsItsOwnBracket()
        {
            Agent_Tournament.ResetForTests();
            try
            {
                Agent_Tournament.Mode = Agent_Tournament.Cup.Team;
                Assert.IsFalse(Agent_Tournament.StartNew(new[] { "A", "B", "C", "D", "E", "F" }), "six players are one team");
                Assert.IsNull(Agent_Tournament.Bracket);

                var eight = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
                Assert.IsTrue(Agent_Tournament.StartNew(eight));
                Assert.AreEqual(4, Agent_Tournament.Bracket.Size);
                Assert.AreEqual(2, Agent_Tournament.Bracket.EntrantCount);
                Assert.IsTrue(Agent_Tournament.BeginNextMatch(out string blue, out string red));
                Assert.AreEqual(4, Agent_Tournament.MembersOf(blue).Length);
                Assert.AreEqual(4, Agent_Tournament.MembersOf(red).Length);

                Agent_Tournament.Mode = Agent_Tournament.Cup.Solo;
                Assert.IsNull(Agent_Tournament.Bracket, "the 1v1 cup has its own (empty) bracket");
            }
            finally
            {
                Agent_Tournament.ResetForTests();
            }
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
            Assert.AreEqual("Voices/MATT_CELEBRATE_2_4", Agent_Voices.ResourcePath("MATT", Agent_VoiceMood.Celebrate, 2, 4));
            Assert.AreEqual("Voices/matt/celebrate-5", Agent_Voices.StandardResourcePath("MATT", Agent_VoiceMood.Celebrate, 5));
            Assert.AreEqual("Voices/kim/hurt-1", Agent_Voices.StandardResourcePath("KIM", Agent_VoiceMood.Hurt, 1));
        }

        [Test]
        public void VoiceMatrix_IsFiveEmotionsByFiveLevels_AndCelebrateHasItsOwnProfile()
        {
            Assert.AreEqual(5, System.Enum.GetValues(typeof(Agent_VoiceMood)).Length);
            Assert.AreEqual(5, Agent_VoiceSynth.LEVELS);
            float[] celebrate = Agent_VoiceSynth.Render("MATT", Agent_VoiceMood.Celebrate, 1, 3, 0.92f);
            float[] happy = Agent_VoiceSynth.Render("MATT", Agent_VoiceMood.Happy, 1, 3, 0.92f);
            Assert.IsFalse(Same(celebrate, happy), "CELEBRATE renders the same as HAPPY");

            Assert.AreEqual(1, Agent_Voices.CelebrateLevelForGoal(1, 2));
            Assert.AreEqual(2, Agent_Voices.CelebrateLevelForGoal(2, 2));
            Assert.AreEqual(3, Agent_Voices.CelebrateLevelForGoal(3, 1));
            Assert.Less(3, Agent_Voices.CELEBRATE_MATCH_WON);
            Assert.Less(Agent_Voices.CELEBRATE_MATCH_WON, Agent_Voices.CELEBRATE_CUP_WON);
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
