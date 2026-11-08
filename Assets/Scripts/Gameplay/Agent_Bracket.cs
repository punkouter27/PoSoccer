using System.Collections.Generic;

namespace PoSoccer
{
    /// <summary>
    /// A 16-slot single-elimination bracket. Pure data and rules, serialisable
    /// with JsonUtility, no Unity objects - EditMode-testable.
    ///
    /// Slots hold character names; an empty name is a BYE. Entrants are seeded
    /// strongest first into the standard 16-seed layout, so with fewer than 16
    /// players the byes go to the top seeds and the best players meet last.
    /// A match against a BYE is decided without being played and never touches
    /// a rating.
    ///
    /// Matches are numbered per round (round 0 = round of 16, 8 matches; round 3 =
    /// final, 1 match) and stored flat: <see cref="Offset"/> maps a round to its
    /// first index.
    /// </summary>
    [System.Serializable]
    public sealed class Agent_Bracket
    {
        public const int SIZE = 16;
        public const int ROUNDS = 4;
        public const int MATCHES = SIZE - 1;
        public const string BYE = "";

        /// <summary>Bracket position of seeds 1..16 (index = seed - 1).</summary>
        static readonly int[] SeedPosition = { 0, 14, 10, 6, 4, 8, 12, 2, 3, 13, 9, 5, 7, 11, 15, 1 };

        static readonly string[] RoundNames = { "ROUND OF 16", "QUARTER-FINALS", "SEMI-FINALS", "FINAL" };

        public string[] slots = new string[SIZE];
        public string[] winners = new string[MATCHES];
        public bool[] decided = new bool[MATCHES];
        public string[] scores = new string[MATCHES];
        public bool[] played = new bool[MATCHES];

        public static string RoundName(int round) => RoundNames[UnityEngine.Mathf.Clamp(round, 0, ROUNDS - 1)];

        public static int MatchesIn(int round) => SIZE >> (round + 1);

        public static int Offset(int round)
        {
            int offset = 0;
            for (int earlier = 0; earlier < round; earlier++) offset += MatchesIn(earlier);
            return offset;
        }

        /// <summary>New bracket from entrants ordered strongest first. Extra entrants beyond 16 are dropped.</summary>
        public static Agent_Bracket Seeded(IList<string> strongestFirst)
        {
            var bracket = new Agent_Bracket();
            for (int slot = 0; slot < SIZE; slot++) bracket.slots[slot] = BYE;
            for (int match = 0; match < MATCHES; match++)
            {
                bracket.winners[match] = BYE;
                bracket.scores[match] = "";
            }
            int count = strongestFirst == null ? 0 : System.Math.Min(strongestFirst.Count, SIZE);
            for (int seed = 0; seed < count; seed++) bracket.slots[SeedPosition[seed]] = strongestFirst[seed] ?? BYE;
            bracket.AdvanceByes();
            return bracket;
        }

        /// <summary>True once the entrant on this side is known (slot, or the earlier match decided).</summary>
        public bool EntrantKnown(int round, int match, int side)
        {
            if (round == 0) return true;
            return decided[Offset(round - 1) + 2 * match + side];
        }

        /// <summary>Entrant on one side of a match; BYE if empty or not yet known.</summary>
        public string Entrant(int round, int match, int side)
        {
            if (round == 0) return slots[2 * match + side] ?? BYE;
            int feeder = Offset(round - 1) + 2 * match + side;
            return decided[feeder] ? winners[feeder] ?? BYE : BYE;
        }

        public bool IsDecided(int round, int match) => decided[Offset(round) + match];

        public string WinnerOf(int round, int match) => winners[Offset(round) + match];

        public string ScoreOf(int round, int match) => scores[Offset(round) + match];

        public bool Finished => decided[MATCHES - 1];

        /// <summary>The champion's name, or null while the final is undecided (BYE if nobody entered).</summary>
        public string Champion => Finished ? winners[MATCHES - 1] : null;

        /// <summary>Decides every match that has a BYE on one side and a known entrant on both. Repeats to the final.</summary>
        public void AdvanceByes()
        {
            for (int round = 0; round < ROUNDS; round++)
            {
                for (int match = 0; match < MatchesIn(round); match++)
                {
                    int index = Offset(round) + match;
                    if (decided[index]) continue;
                    if (!EntrantKnown(round, match, 0) || !EntrantKnown(round, match, 1)) continue;
                    string a = Entrant(round, match, 0), b = Entrant(round, match, 1);
                    if (a == BYE || b == BYE)
                    {
                        winners[index] = a == BYE ? b : a;
                        decided[index] = true;
                        scores[index] = "BYE";
                    }
                }
            }
        }

        /// <summary>The next match that needs playing (both entrants real and known), earliest round first.</summary>
        public bool TryNextMatch(out int round, out int match)
        {
            for (round = 0; round < ROUNDS; round++)
            {
                for (match = 0; match < MatchesIn(round); match++)
                {
                    if (decided[Offset(round) + match]) continue;
                    if (!EntrantKnown(round, match, 0) || !EntrantKnown(round, match, 1)) continue;
                    if (Entrant(round, match, 0) != BYE && Entrant(round, match, 1) != BYE) return true;
                }
            }
            round = -1;
            match = -1;
            return false;
        }

        /// <summary>Records a played match and pushes byes forward. Ignored if already decided.</summary>
        public bool Record(int round, int match, string winner, string score)
        {
            int index = Offset(round) + match;
            if (decided[index]) return false;
            string a = Entrant(round, match, 0), b = Entrant(round, match, 1);
            if (winner != a && winner != b) return false;
            winners[index] = winner;
            scores[index] = score ?? "";
            played[index] = true;
            decided[index] = true;
            AdvanceByes();
            return true;
        }

        /// <summary>Number of real (non-BYE) entrants.</summary>
        public int EntrantCount
        {
            get
            {
                int count = 0;
                for (int slot = 0; slot < SIZE; slot++) if (!string.IsNullOrEmpty(slots[slot])) count++;
                return count;
            }
        }
    }
}
