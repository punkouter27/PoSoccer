using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Tournament mode's state across scene loads. There are two cups, each with
    /// its own bracket, saved to its own file in persistentDataPath:
    ///
    ///   1v1 CUP  (tournament.json)      16 slots, one character per slot, BYEs fill
    ///                                   the slots of untrained characters.
    ///   4v4 CUP  (tournament-4v4.json)  4 slots, one TEAM of four per slot. Teams are
    ///                                   drafted from the trained roster by Elo (snake
    ///                                   draft, so the four teams are about equal).
    ///
    /// <see cref="Mode"/> picks which cup every other member reads. The menu sets it
    /// when a cup is opened; it is a static, so it survives the load into the match
    /// scene and back.
    ///
    /// THE LOOP. The menu's bracket screen calls <see cref="BeginNextMatch"/>, which
    /// marks the match as in progress and returns the two entrant names; the menu
    /// turns them into squads (one character, or a team's four) and loads the match
    /// scene. There Agent_TournamentMatch waits for full time and calls
    /// <see cref="Report"/>, which records the winner, then returns to the menu.
    /// With <see cref="AutoPlay"/> on, the bracket screen opens itself and starts
    /// the next match, until a champion is crowned.
    ///
    /// Elo is a 1v1 rating: the 1v1 cup records it, the 4v4 cup does not (a team
    /// result says little about any one of its four players).
    ///
    /// A drawn match (possible only on the time limit) is not a result: the same
    /// pairing is played again.
    /// </summary>
    public static class Agent_Tournament
    {
        public enum Cup
        {
            Solo,
            Team,
        }

        public const int TEAM_SIZE = 4;
        public const int MAX_TEAMS = 4;
        public const string TEAM_PREFIX = "TEAM ";

        [System.Serializable]
        public sealed class Team
        {
            public string name;
            public string[] members;
        }

        [System.Serializable]
        sealed class SaveData
        {
            public Agent_Bracket bracket;
            // JsonUtility never writes null: a missing bracket comes back as a
            // default-constructed one, so "is there a tournament" is its own field.
            public bool hasBracket;
            public bool autoPlay;
            public int number;
            public List<Team> teams = new();
        }

        static readonly SaveData[] Data = new SaveData[2];

        /// <summary>Which cup the rest of this API reads and writes.</summary>
        public static Cup Mode { get; set; } = Cup.Solo;

        public static bool IsTeamCup => Mode == Cup.Team;

        static SaveData Current
        {
            get
            {
                Load(Mode);
                return Data[(int)Mode];
            }
        }

        /// <summary>The bracket, or null when no tournament has been started.</summary>
        public static Agent_Bracket Bracket => Current.hasBracket ? Current.bracket : null;

        /// <summary>Tournament number, counting up with every new bracket.</summary>
        public static int Number => Current.number;

        /// <summary>Play every remaining match without input.</summary>
        public static bool AutoPlay
        {
            get => Current.autoPlay;
            set
            {
                Current.autoPlay = value;
                Save(Mode);
            }
        }

        /// <summary>True while a tournament match is on the pitch (set by BeginNextMatch, cleared by Report).</summary>
        public static bool InMatch { get; private set; }
        public static int MatchRound { get; private set; } = -1;
        public static int MatchIndex { get; private set; } = -1;
        public static string BlueName { get; private set; }
        public static string RedName { get; private set; }

        /// <summary>Set when a result is recorded: the menu opens the bracket on arrival, then clears it.</summary>
        public static bool ShowBracketOnMenu;

        /// <summary>Last recorded result, for the bracket screen's banner.</summary>
        public static string LastResult { get; private set; } = "";

        public static bool Active => Bracket != null && !Bracket.Finished;

        /// <summary>A team's members in draft order (captain first), or null for an unknown name.</summary>
        public static string[] MembersOf(string teamName)
        {
            var teams = Current.teams;
            if (teams == null) return null;
            for (int teamIndex = 0; teamIndex < teams.Count; teamIndex++)
            {
                if (teams[teamIndex].name == teamName) return teams[teamIndex].members;
            }
            return null;
        }

        /// <summary>How many teams of four a roster of <paramref name="players"/> makes (at most four).</summary>
        public static int TeamCount(int players) => Mathf.Min(MAX_TEAMS, players / TEAM_SIZE);

        /// <summary>
        /// Snake draft, strongest first: team 1 picks first in odd rounds and last in
        /// even ones, so no team gets every early pick. Uses the strongest
        /// TeamCount x 4 players; the rest sit the cup out. Each team is named after
        /// its captain, its first pick. Pure, for tests.
        /// </summary>
        public static List<Team> DraftTeams(IList<string> strongestFirst)
        {
            int teamCount = TeamCount(strongestFirst == null ? 0 : strongestFirst.Count);
            var members = new List<string>[teamCount];
            for (int teamIndex = 0; teamIndex < teamCount; teamIndex++) members[teamIndex] = new List<string>(TEAM_SIZE);

            for (int pick = 0; pick < teamCount * TEAM_SIZE; pick++)
            {
                int draftRound = pick / teamCount;
                int position = pick % teamCount;
                int teamIndex = draftRound % 2 == 0 ? position : teamCount - 1 - position;
                members[teamIndex].Add(strongestFirst[pick]);
            }

            var teams = new List<Team>(teamCount);
            for (int teamIndex = 0; teamIndex < teamCount; teamIndex++)
            {
                teams.Add(new Team { name = TEAM_PREFIX + members[teamIndex][0], members = members[teamIndex].ToArray() });
            }
            return teams;
        }

        /// <summary>
        /// Starts a new bracket for the current cup from the trained roster's names.
        /// Seeds by Elo, strongest first. The 1v1 cup fills 16 slots with BYEs; the
        /// 4v4 cup drafts teams and seeds them by their total rating. False when the
        /// roster is too small for this cup (the 4v4 cup needs two full teams).
        /// </summary>
        public static bool StartNew(IList<string> entrants)
        {
            var ordered = new List<string>(entrants ?? new string[0]);
            ordered.Sort((x, y) => Agent_Elo.RatingOf(y).CompareTo(Agent_Elo.RatingOf(x)));
            var data = Current;

            if (IsTeamCup)
            {
                var teams = DraftTeams(ordered);
                if (teams.Count < 2) return false;
                teams.Sort((x, y) => TeamRating(y).CompareTo(TeamRating(x)));
                var names = new List<string>(teams.Count);
                for (int teamIndex = 0; teamIndex < teams.Count; teamIndex++) names.Add(teams[teamIndex].name);
                data.teams = teams;
                data.bracket = Agent_Bracket.Seeded(names, MAX_TEAMS);
            }
            else
            {
                data.teams = new List<Team>();
                data.bracket = Agent_Bracket.Seeded(ordered);
            }

            data.hasBracket = true;
            data.number++;
            LastResult = "";
            CancelMatch();
            Save(Mode);
            return true;
        }

        static float TeamRating(Team team)
        {
            float total = 0f;
            for (int memberIndex = 0; memberIndex < team.members.Length; memberIndex++) total += Agent_Elo.RatingOf(team.members[memberIndex]);
            return total;
        }

        /// <summary>Marks the next playable match as in progress. False when there is none.</summary>
        public static bool BeginNextMatch(out string blue, out string red)
        {
            blue = red = null;
            var bracket = Bracket;
            if (bracket == null || !bracket.TryNextMatch(out int round, out int match)) return false;
            blue = bracket.Entrant(round, match, 0);
            red = bracket.Entrant(round, match, 1);
            InMatch = true;
            MatchRound = round;
            MatchIndex = match;
            BlueName = blue;
            RedName = red;
            return true;
        }

        /// <summary>Abandons the in-progress match (the menu was reached without a result).</summary>
        public static void CancelMatch()
        {
            InMatch = false;
            MatchRound = MatchIndex = -1;
            BlueName = RedName = null;
        }

        /// <summary>
        /// Records the in-progress match from the final score and saves. In the 1v1
        /// cup both Elo ratings move. A draw leaves the match to be replayed.
        /// Returns a one-line description of the result.
        /// </summary>
        public static string Report(int blueScore, int redScore)
        {
            if (!InMatch || Bracket == null) return "";
            string blue = BlueName, red = RedName;
            int round = MatchRound, match = MatchIndex;
            CancelMatch();
            ShowBracketOnMenu = true;

            float blueResult = blueScore > redScore ? 1f : blueScore < redScore ? 0f : 0.5f;
            float delta = IsTeamCup ? 0f : Agent_Elo.Record(blue, red, blueResult);

            if (blueScore == redScore)
            {
                LastResult = $"{blue} {blueScore}-{redScore} {red}: DRAW, REPLAY";
                Save(Mode);
                return LastResult;
            }

            string winner = blueScore > redScore ? blue : red;
            string loser = winner == blue ? red : blue;
            Bracket.Record(round, match, winner, $"{blueScore}-{redScore}");
            float winnerDelta = winner == blue ? delta : -delta;
            string score = $"{Mathf.Max(blueScore, redScore)}-{Mathf.Min(blueScore, redScore)}";
            LastResult = Bracket.Finished
                ? $"{winner} WINS {CupName} #{Number}"
                : IsTeamCup
                    ? $"{winner} BEATS {loser} {score}"
                    : $"{winner} BEATS {loser} {score}  (ELO {winnerDelta:+0;-0})";
            if (Bracket.Finished) Current.autoPlay = false;
            Save(Mode);
            return LastResult;
        }

        /// <summary>"1v1 CUP" or "4v4 CUP".</summary>
        public static string CupName => IsTeamCup ? "4v4 CUP" : "1v1 CUP";

        /// <summary>Drops both saved tournaments and returns to the 1v1 cup (tests).</summary>
        public static void ResetForTests()
        {
            Data[0] = new SaveData();
            Data[1] = new SaveData();
            Mode = Cup.Solo;
            CancelMatch();
            LastResult = "";
        }

        /// <summary>
        /// In-memory only under batchmode: the headless test runner plays real
        /// matches, and must not write into the player's saved ratings and bracket.
        /// </summary>
        static bool Persist => !Application.isBatchMode;

        static string FilePath(Cup cup) => System.IO.Path.Combine(Application.persistentDataPath,
            cup == Cup.Team ? "tournament-4v4.json" : "tournament.json");

        static void Load(Cup cup)
        {
            int slot = (int)cup;
            if (Data[slot] != null) return;
            var data = new SaveData();
            Data[slot] = data;
            string path = FilePath(cup);
            try
            {
                if (Persist && System.IO.File.Exists(path))
                {
                    var loaded = JsonUtility.FromJson<SaveData>(System.IO.File.ReadAllText(path));
                    if (loaded != null) data = loaded;
                    data.teams ??= new List<Team>();
                    if (data.bracket == null || !data.bracket.IsWellFormed) data.hasBracket = false;
                    Data[slot] = data;
                }
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Agent_Tournament: could not read {path} ({exception.Message}); no tournament loaded.");
            }
        }

        static void Save(Cup cup)
        {
            if (!Persist) return;
            string path = FilePath(cup);
            try
            {
                System.IO.File.WriteAllText(path, JsonUtility.ToJson(Data[(int)cup], true));
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Agent_Tournament: could not write {path} ({exception.Message}).");
            }
        }
    }
}
