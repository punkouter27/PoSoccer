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
    /// Recorded for every finished 1 v 1 match between two named characters:
    /// tournament matches and exhibition 1 v 1s alike.
    /// </summary>
    public static class Agent_Elo
    {
        public const float START_RATING = 1500f;
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
            var table = new List<Entry>(_book.entries);
            table.Sort((x, y) => y.rating.CompareTo(x.rating));
            return table;
        }

        /// <summary>Records one game and saves. Returns the rating change for A.</summary>
        public static float Record(string nameA, string nameB, float scoreA)
        {
            if (string.IsNullOrEmpty(nameA) || string.IsNullOrEmpty(nameB) || nameA == nameB) return 0f;
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
