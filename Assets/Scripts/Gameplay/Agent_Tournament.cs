using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Tournament mode's state across scene loads: the current
    /// <see cref="Agent_Bracket"/>, whether it plays itself, and which match is
    /// on the pitch right now. Saved to persistentDataPath/tournament.json after
    /// every change, so a tournament survives quitting the game.
    ///
    /// THE LOOP. The menu's bracket screen calls <see cref="BeginNextMatch"/>,
    /// which marks the match as in progress and returns the two names; the menu
    /// sets Agent_MatchSetup (1 v 1) and loads the match scene. There
    /// Agent_TournamentMatch waits for full time and calls <see cref="Report"/>,
    /// which records the winner and both Elo ratings, then returns to the menu.
    /// With <see cref="AutoPlay"/> on, the bracket screen opens itself and starts
    /// the next match, until a champion is crowned.
    ///
    /// A drawn match (possible only on the time limit) is not a result: the same
    /// pairing is played again.
    /// </summary>
    public static class Agent_Tournament
    {
        const string FILE_NAME = "tournament.json";

        [System.Serializable]
        sealed class SaveData
        {
            public Agent_Bracket bracket;
            // JsonUtility never writes null: a missing bracket comes back as a
            // default-constructed one, so "is there a tournament" is its own field.
            public bool hasBracket;
            public bool autoPlay;
            public int number;
        }

        static SaveData _data;

        /// <summary>The bracket, or null when no tournament has been started.</summary>
        public static Agent_Bracket Bracket
        {
            get { Load(); return _data.hasBracket ? _data.bracket : null; }
        }

        /// <summary>Tournament number, counting up with every new bracket.</summary>
        public static int Number
        {
            get { Load(); return _data.number; }
        }

        /// <summary>Play every remaining match without input.</summary>
        public static bool AutoPlay
        {
            get { Load(); return _data.autoPlay; }
            set { Load(); _data.autoPlay = value; Save(); }
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

        /// <summary>Starts a new bracket from entrant names. Seeds by Elo, strongest first; BYE fills to 16.</summary>
        public static void StartNew(IList<string> entrants)
        {
            Load();
            var ordered = new List<string>(entrants);
            ordered.Sort((x, y) => Agent_Elo.RatingOf(y).CompareTo(Agent_Elo.RatingOf(x)));
            _data.bracket = Agent_Bracket.Seeded(ordered);
            _data.hasBracket = true;
            _data.number++;
            LastResult = "";
            CancelMatch();
            Save();
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
        /// Records the in-progress match from the final score, updates both Elo
        /// ratings and saves. A draw updates Elo but leaves the match to be replayed.
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
            float delta = Agent_Elo.Record(blue, red, blueResult);

            if (blueScore == redScore)
            {
                LastResult = $"{blue} {blueScore}-{redScore} {red}: DRAW, REPLAY";
                Save();
                return LastResult;
            }

            string winner = blueScore > redScore ? blue : red;
            string loser = winner == blue ? red : blue;
            Bracket.Record(round, match, winner, $"{blueScore}-{redScore}");
            float winnerDelta = winner == blue ? delta : -delta;
            LastResult = Bracket.Finished
                ? $"{winner} WINS TOURNAMENT #{Number}"
                : $"{winner} BEATS {loser} {Mathf.Max(blueScore, redScore)}-{Mathf.Min(blueScore, redScore)}  (ELO {winnerDelta:+0;-0})";
            if (Bracket.Finished) _data.autoPlay = false;
            Save();
            return LastResult;
        }

        /// <summary>Drops the saved tournament (tests).</summary>
        public static void ResetForTests()
        {
            _data = new SaveData();
            CancelMatch();
            LastResult = "";
        }

        /// <summary>
        /// In-memory only under batchmode: the headless test runner plays real 1 v 1
        /// matches, and must not write into the player's saved ratings and bracket.
        /// </summary>
        static bool Persist => !Application.isBatchMode;

        static string FilePath => System.IO.Path.Combine(Application.persistentDataPath, FILE_NAME);

        static void Load()
        {
            if (_data != null) return;
            _data = new SaveData();
            try
            {
                if (Persist && System.IO.File.Exists(FilePath))
                {
                    var loaded = JsonUtility.FromJson<SaveData>(System.IO.File.ReadAllText(FilePath));
                    if (loaded != null) _data = loaded;
                    if (_data.bracket != null && (_data.bracket.slots == null || _data.bracket.slots.Length != Agent_Bracket.SIZE))
                        _data.hasBracket = false;
                }
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Agent_Tournament: could not read {FilePath} ({exception.Message}); no tournament loaded.");
            }
        }

        static void Save()
        {
            if (!Persist) return;
            try
            {
                System.IO.File.WriteAllText(FilePath, JsonUtility.ToJson(_data, true));
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Agent_Tournament: could not write {FilePath} ({exception.Message}).");
            }
        }
    }
}
