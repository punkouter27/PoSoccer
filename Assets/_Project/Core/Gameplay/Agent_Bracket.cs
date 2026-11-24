using System.Collections.Generic;

namespace PoSoccer
{
    /// <summary>
    /// A single-elimination bracket of 4, 8 or 16 slots. Pure data and rules,
    /// serialisable with JsonUtility, no Unity objects - EditMode-testable.
    ///
    /// Slots hold entrant names (a character for the 1v1 cup, a team for the 4v4
    /// cup); an empty name is a BYE. Entrants are seeded strongest first into the
    /// standard seed layout, so with fewer entrants than slots the byes go to the
    /// top seeds (BYEs take the bottom seeds) and the best entrants meet last. A
    /// match against a BYE is decided without being played and never touches a
    /// rating; BYE vs BYE sends a BYE on. A drawn match goes to the higher seed
    /// (<see cref="RecordDraw"/>).
    ///
    /// Matches are numbered per round (round 0 = the first round; the last round is
    /// the final, 1 match) and stored flat: <see cref="Offset"/> maps a round to
    /// its first index.
    ///
    /// 2026-11-09: the size became per bracket (the 4v4 cup is four teams). A save
    /// written before then has no <see cref="size"/> and reads back as 16.
    /// </summary>
    [System.Serializable]
    public sealed class Agent_Bracket
    {
        /// <summary>Default and largest size: the 1v1 cup.</summary>
        public const int SIZE = 16;
        /// <summary>An empty slot (stored as ""). Shown to players as <see cref="BYE_LABEL"/>.</summary>
        public const string BYE = "";

        /// <summary>How an empty slot is displayed everywhere (menu grid and bracket): the tournament standard.</summary>
        public const string BYE_LABEL = "BYE";

        /// <summary>
        /// Bracket position of seeds 1..N (index = seed - 1) for each supported size.
        /// Tournament standard (2026-11-22): the first-round pairs, top to bottom, are
        /// 1v16, 8v9, 4v13, 5v12, 2v15, 7v10, 3v14, 6v11 (and 1v8, 4v5, 2v7, 3v6 for 8;
        /// 1v4, 2v3 for 4), so seeds 1 and 2 can only meet in the final.
        /// </summary>
        static readonly int[] Seed16 = { 0, 8, 12, 4, 6, 14, 10, 2, 3, 11, 15, 7, 5, 13, 9, 1 };
        static readonly int[] Seed8 = { 0, 4, 6, 2, 3, 7, 5, 1 };
        static readonly int[] Seed4 = { 0, 2, 3, 1 };

        static int[] LayoutFor(int bracketSize) => bracketSize switch
        {
            4 => Seed4,
            8 => Seed8,
            16 => Seed16,
            _ => throw new System.ArgumentException($"bracket size {bracketSize}; supported: 4, 8, 16"),
        };

        /// <summary>"BYE" for an empty name, the name otherwise.</summary>
        public static string Display(string name) => string.IsNullOrEmpty(name) ? BYE_LABEL : name;

        public int size = SIZE;
        public string[] slots = new string[SIZE];
        public string[] winners = new string[SIZE - 1];
        public bool[] decided = new bool[SIZE - 1];
        public string[] scores = new string[SIZE - 1];
        public bool[] played = new bool[SIZE - 1];

        /// <summary>Slot count (16 for a save that predates the field).</summary>
        public int Size => size > 0 ? size : SIZE;

        public int Rounds
        {
            get
            {
                int rounds = 0;
                for (int remaining = Size; remaining > 1; remaining >>= 1) rounds++;
                return rounds;
            }
        }

        public int Matches => Size - 1;

        public int MatchesIn(int round) => Size >> (round + 1);

        /// <summary>"FINAL", "SEMI-FINALS", ... named by how many matches the round holds.</summary>
        public string RoundName(int round)
        {
            int matches = MatchesIn(System.Math.Max(0, System.Math.Min(round, Rounds - 1)));
            return matches switch
            {
                1 => "FINAL",
                2 => "SEMI-FINALS",
                4 => "QUARTER-FINALS",
                _ => $"ROUND OF {matches * 2}",
            };
        }

        public int Offset(int round)
        {
            int offset = 0;
            for (int earlier = 0; earlier < round; earlier++) offset += MatchesIn(earlier);
            return offset;
        }

        /// <summary>New bracket from entrants ordered strongest first. Extra entrants beyond the size are dropped.</summary>
        public static Agent_Bracket Seeded(IList<string> strongestFirst, int bracketSize = SIZE)
        {
            int[] layout = LayoutFor(bracketSize);
            var bracket = new Agent_Bracket
            {
                size = bracketSize,
                slots = new string[bracketSize],
                winners = new string[bracketSize - 1],
                decided = new bool[bracketSize - 1],
                scores = new string[bracketSize - 1],
                played = new bool[bracketSize - 1],
            };
            for (int slot = 0; slot < bracketSize; slot++) bracket.slots[slot] = BYE;
            for (int match = 0; match < bracketSize - 1; match++)
            {
                bracket.winners[match] = BYE;
                bracket.scores[match] = "";
            }
            int count = strongestFirst == null ? 0 : System.Math.Min(strongestFirst.Count, bracketSize);
            for (int seed = 0; seed < count; seed++) bracket.slots[layout[seed]] = strongestFirst[seed] ?? BYE;
            bracket.AdvanceByes();
            return bracket;
        }

        /// <summary>True when the arrays match the size (a hand-edited or truncated save does not).</summary>
        public bool IsWellFormed =>
            slots != null && slots.Length == Size && winners != null && winners.Length == Matches &&
            decided != null && decided.Length == Matches && scores != null && scores.Length == Matches &&
            played != null && played.Length == Matches;

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

        public bool Finished => decided[Matches - 1];

        /// <summary>The champion's name, or null while the final is undecided (BYE if nobody entered).</summary>
        public string Champion => Finished ? winners[Matches - 1] : null;

        /// <summary>Decides every match that has a BYE on one side and a known entrant on both. Repeats to the final.</summary>
        public void AdvanceByes()
        {
            for (int round = 0; round < Rounds; round++)
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
            for (round = 0; round < Rounds; round++)
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

        /// <summary>Seed (1 = strongest) of the entrant who started in <paramref name="slot"/>.</summary>
        public int SeedOfSlot(int slot) => System.Array.IndexOf(LayoutFor(Size), slot) + 1;

        /// <summary>An entrant's seed (1 = strongest), or int.MaxValue for a BYE or a stranger.</summary>
        public int SeedOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return int.MaxValue;
            for (int slot = 0; slot < Size; slot++)
            {
                if (slots[slot] == name) return SeedOfSlot(slot);
            }
            return int.MaxValue;
        }

        /// <summary>The better-seeded (lower seed number) of the match's two entrants.</summary>
        public string HigherSeed(int round, int match)
        {
            string a = Entrant(round, match, 0), b = Entrant(round, match, 1);
            return SeedOf(b) < SeedOf(a) ? b : a;
        }

        /// <summary>
        /// Records a drawn match: the tournament standard sends the higher seed through.
        /// Ignored if already decided.
        /// </summary>
        public bool RecordDraw(int round, int match, string score)
            => Record(round, match, HigherSeed(round, match), score);

        /// <summary>Number of real (non-BYE) entrants.</summary>
        public int EntrantCount
        {
            get
            {
                int count = 0;
                for (int slot = 0; slot < Size; slot++) if (!string.IsNullOrEmpty(slots[slot])) count++;
                return count;
            }
        }
    }
}
