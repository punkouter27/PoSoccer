using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Elo ratings for every character, kept across sessions in
    /// persistentDataPath/elo.json.
    ///
    /// Standard Elo: everyone starts at <see cref="START_RATING"/>, the expected
    /// score is the logistic 1 / (1 + 10^((Rb - Ra) / 400)), and a result moves
    /// both ratings by K x (actual - expected) - so the pair's total is conserved.
    /// A draw scores 0.5. Byes are not games and never touch a rating.
    ///
    /// Recorded for every finished 1 v 1 tournament match (<see cref="Record"/>) and,
    /// since 2026-11-22, every completed exhibition or demo match, 1 v 1 or 4 v 4
    /// (<see cref="RecordMatch"/>, pairwise team rule in <see cref="TeamDeltas"/>).
    /// The 4v4 cup still leaves Elo alone.
    /// </summary>
    public static class Agent_Elo
    {
        /// <summary>
        /// Tournament standard (2026-11-22): 1000 (was 1500). Only characters with no saved
        /// entry start here; ratings already in elo.json keep their values.
        /// </summary>
        public const float START_RATING = 1000f;
        public const float K_FACTOR = 32f;
        const string FILE_NAME = "elo.json";

        [System.Serializable]
        public sealed class Entry
        {
            public string name;
            public float rating = START_RATING;
            public int games;
            public int wins;
            public int losses;
            public int draws;
        }

        [System.Serializable]
        sealed class Book
        {
            public List<Entry> entries = new();
        }

        static Book _book;

        /// <summary>Probability that a player rated <paramref name="rating"/> beats <paramref name="opponent"/>.</summary>
        public static float Expected(float rating, float opponent)
            => 1f / (1f + Mathf.Pow(10f, (opponent - rating) / 400f));

        /// <summary>New ratings after one game. <paramref name="scoreA"/>: 1 win, 0.5 draw, 0 loss for A.</summary>
        public static (float a, float b) Rate(float ratingA, float ratingB, float scoreA, float k = K_FACTOR)
        {
            float expectedA = Expected(ratingA, ratingB);
            float delta = k * (scoreA - expectedA);
            return (ratingA + delta, ratingB - delta);
        }

        /// <summary>A character's current rating (START_RATING if never rated).</summary>
        public static float RatingOf(string name) => Find(name, create: false)?.rating ?? START_RATING;

        /// <summary>A character's record, or null if they have never played.</summary>
        public static Entry Get(string name) => Find(name, create: false);

        /// <summary>Every rated character, highest first.</summary>
        public static List<Entry> Table()
        {
            Load();
            var table = new List<Entry>(_book.entries.Count);
            for (int i = 0; i < _book.entries.Count; i++)
            {
                var entry = _book.entries[i];
                if (entry == null || string.Equals(entry.name, Agent_HumanController.HUMAN_PLAYER_NAME, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                table.Add(entry);
            }
            table.Sort((x, y) => y.rating.CompareTo(x.rating));
            return table;
        }

        /// <summary>Records one game and saves. Returns the rating change for A.</summary>
        public static float Record(string nameA, string nameB, float scoreA)
        {
            if (string.IsNullOrEmpty(nameA) || string.IsNullOrEmpty(nameB) || nameA == nameB) return 0f;
            if (string.Equals(nameA, Agent_HumanController.HUMAN_PLAYER_NAME, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(nameB, Agent_HumanController.HUMAN_PLAYER_NAME, System.StringComparison.OrdinalIgnoreCase))
                return 0f;
            var a = Find(nameA, create: true);
            var b = Find(nameB, create: true);
            float before = a.rating;
            (a.rating, b.rating) = Rate(a.rating, b.rating, scoreA);
            a.games++; b.games++;
            if (scoreA > 0.75f) { a.wins++; b.losses++; }
            else if (scoreA < 0.25f) { a.losses++; b.wins++; }
            else { a.draws++; b.draws++; }
            Save();
            return a.rating - before;
        }

        // -- Exhibition and demo matches, 1 v 1 and team (2026-11-22, demo Elo spec) -----------

        static bool IsHuman(string name)
            => string.Equals(name, Agent_HumanController.HUMAN_PLAYER_NAME, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>A side's rated characters: named, not the human, each once (copies aggregate into one entry).</summary>
        public static List<string> RatedSide(IList<string> side, System.Func<string, bool> isHuman = null)
        {
            var names = new List<string>();
            if (side == null) return names;
            for (int index = 0; index < side.Count; index++)
            {
                string name = side[index];
                if (string.IsNullOrEmpty(name) || (isHuman ?? IsHuman)(name)) continue;
                bool seen = false;
                for (int other = 0; other < names.Count; other++)
                {
                    if (string.Equals(names[other], name, System.StringComparison.OrdinalIgnoreCase)) seen = true;
                }
                if (!seen) names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// Rating change of every player in one match (pure). Team rule (no older one
        /// existed): each player on one side plays each on the other, pairwise, with K
        /// divided by the number of opponents that player faced, so a 4 v 4 moves a
        /// rating about as much as a 1 v 1 (and a 1 v 1 is plain Elo). A character on
        /// both sides never plays itself: those pairs are skipped. All pairs use the
        /// ratings from before the match. <paramref name="blueScore"/>: 1 win, 0.5 draw, 0 loss.
        /// </summary>
        public static Dictionary<string, float> TeamDeltas(IList<string> blue, IList<string> red, float blueScore,
            System.Func<string, float> ratingOf, float k = K_FACTOR, System.Func<string, bool> isHuman = null)
        {
            var blueSide = RatedSide(blue, isHuman);
            var redSide = RatedSide(red, isHuman);
            var deltas = new Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
            var opponents = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string name in blueSide) { deltas[name] = 0f; opponents[name] = 0; }
            foreach (string name in redSide) { deltas[name] = 0f; opponents[name] = 0; }
            foreach (string a in blueSide)
            {
                foreach (string b in redSide)
                {
                    if (string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase)) continue;
                    opponents[a]++;
                    opponents[b]++;
                }
            }
            foreach (string a in blueSide)
            {
                foreach (string b in redSide)
                {
                    if (string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase)) continue;
                    float surprise = blueScore - Expected(ratingOf(a), ratingOf(b));
                    deltas[a] += k / opponents[a] * surprise;
                    deltas[b] -= k / opponents[b] * surprise;
                }
            }
            return deltas;
        }

        /// <summary>
        /// Records one finished exhibition or demo match (1 v 1 or team) and saves at once.
        /// A character fielded on both sides is left alone (it would partly play itself). Returns each rated
        /// player's change.
        /// </summary>
        public static Dictionary<string, float> RecordMatch(IList<string> blue, IList<string> red, float blueScore)
        {
            var deltas = TeamDeltas(blue, red, blueScore, RatingOf);
            var blueSide = RatedSide(blue);
            var redSide = RatedSide(red);
            bool any = false;
            foreach (var pair in deltas)
            {
                bool onBlue = blueSide.Exists(name => string.Equals(name, pair.Key, System.StringComparison.OrdinalIgnoreCase));
                bool onRed = redSide.Exists(name => string.Equals(name, pair.Key, System.StringComparison.OrdinalIgnoreCase));
                if (onBlue && onRed) continue;   // fielded on both sides: unrated for this match
                float score = onBlue ? blueScore : 1f - blueScore;
                var entry = Find(pair.Key, create: true);
                entry.rating += pair.Value;
                entry.games++;
                if (score > 0.75f) entry.wins++;
                else if (score < 0.25f) entry.losses++;
                else entry.draws++;
                any = true;
            }
            if (any) Save();
            return deltas;
        }

        /// <summary>
        /// One exhibition or demo match from kickoff to result. <see cref="Complete"/> records
        /// it exactly once; a match that is aborted (input left the demo, the scene was
        /// reset or unloaded) never completes and records nothing.
        /// </summary>
        public sealed class PendingMatch
        {
            readonly string[] _blue, _red;
            readonly System.Func<IList<string>, IList<string>, float, Dictionary<string, float>> _record;
            bool _closed;

            public PendingMatch(IList<string> blue, IList<string> red,
                System.Func<IList<string>, IList<string>, float, Dictionary<string, float>> record = null)
            {
                _blue = blue == null ? new string[0] : new List<string>(blue).ToArray();
                _red = red == null ? new string[0] : new List<string>(red).ToArray();
                _record = record ?? RecordMatch;
            }

            public IReadOnlyList<string> Blue => _blue;
            public IReadOnlyList<string> Red => _red;

            /// <summary>True once completed or aborted.</summary>
            public bool Closed => _closed;

            /// <summary>Records the final score once; null when already completed or aborted.</summary>
            public Dictionary<string, float> Complete(int blueGoals, int redGoals)
            {
                if (_closed) return null;
                _closed = true;
                float blueScore = blueGoals > redGoals ? 1f : blueGoals < redGoals ? 0f : 0.5f;   // no seeds in a demo
                return _record(_blue, _red, blueScore);
            }

            /// <summary>The match ended without a result: nothing is recorded, now or later.</summary>
            public void Abort() => _closed = true;
        }

        /// <summary>Forgets every rating (tests, or a fresh league).</summary>
        public static void ResetAll()
        {
            _book = new Book();
            Save();
        }

        static Entry Find(string name, bool create)
        {
            Load();
            if (string.IsNullOrEmpty(name)) return null;
            for (int entryIndex = 0; entryIndex < _book.entries.Count; entryIndex++)
            {
                if (string.Equals(_book.entries[entryIndex].name, name, System.StringComparison.OrdinalIgnoreCase))
                    return _book.entries[entryIndex];
            }
            if (!create) return null;
            var entry = new Entry { name = name };
            _book.entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// In-memory only under batchmode: the headless test runner plays real 1 v 1
        /// matches, and must not write into the player's saved ratings and bracket.
        /// </summary>
        static bool Persist => !Application.isBatchMode;

        static string FilePath => System.IO.Path.Combine(Application.persistentDataPath, FILE_NAME);

        static void Load()
        {
            if (_book != null) return;
            _book = new Book();
            try
            {
                if (Persist && System.IO.File.Exists(FilePath))
                {
                    var loaded = JsonUtility.FromJson<Book>(System.IO.File.ReadAllText(FilePath));
                    if (loaded != null && loaded.entries != null) _book = loaded;
                }
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Agent_Elo: could not read {FilePath} ({exception.Message}); starting fresh.");
            }
        }

        static void Save()
        {
            if (!Persist) return;
            try
            {
                System.IO.File.WriteAllText(FilePath, JsonUtility.ToJson(_book, true));
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Agent_Elo: could not write {FilePath} ({exception.Message}).");
            }
        }
    }
}
