using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// A match timeline in a fixed amount of memory: signed pressure averaged per
    /// bucket, cumulative xG per side at the end of each bucket, and the goals.
    ///
    /// A first-to-5 match has no fixed length - it can be over in two minutes or
    /// run for twenty - so the bucket width cannot be chosen up front. It starts at
    /// <c>initialBucketSeconds</c>, and whenever the buffer fills, neighbouring
    /// buckets are merged pairwise and the width doubles. Memory stays at the
    /// capacity, the whole match always fits, and the resolution is the best the
    /// buffer allows for the length the match turned out to be.
    ///
    /// Plain C#, no Unity objects, so the EditMode tests can drive it directly.
    /// Sampling allocates nothing.
    /// </summary>
    public sealed class Agent_MomentumSeries
    {
        readonly float[] _pressure;
        readonly float[] _blueXg;
        readonly float[] _redXg;
        readonly float[] _goalTimes;
        readonly int[] _goalTeams;

        float _bucketSeconds;
        float _currentSum;
        float _currentSeconds;
        float _lastBlueXg;
        float _lastRedXg;

        /// <summary>Completed buckets.</summary>
        public int Count { get; private set; }

        /// <summary>Width of one bucket in seconds; doubles each time the buffer compacts.</summary>
        public float BucketSeconds => _bucketSeconds;

        /// <summary>Match seconds recorded, including the bucket still filling.</summary>
        public float TotalSeconds { get; private set; }

        /// <summary>Goals recorded.</summary>
        public int GoalCount { get; private set; }

        public int Capacity => _pressure.Length;

        public Agent_MomentumSeries(int capacity, float initialBucketSeconds, int maxGoals = 32)
        {
            capacity = Mathf.Max(4, capacity);
            if (capacity % 2 != 0) capacity++;
            _pressure = new float[capacity];
            _blueXg = new float[capacity];
            _redXg = new float[capacity];
            _goalTimes = new float[Mathf.Max(1, maxGoals)];
            _goalTeams = new int[Mathf.Max(1, maxGoals)];
            _bucketSeconds = Mathf.Max(0.01f, initialBucketSeconds);
        }

        /// <summary>
        /// Add one tick. <paramref name="pressure"/> is signed: positive means blue is
        /// pressing, negative red, magnitude 0..1. The xG figures are the running
        /// match totals.
        /// </summary>
        public void Sample(float pressure, float blueXg, float redXg, float deltaSeconds)
        {
            if (deltaSeconds <= 0f) return;

            _currentSum += Mathf.Clamp(pressure, -1f, 1f) * deltaSeconds;
            _currentSeconds += deltaSeconds;
            _lastBlueXg = blueXg;
            _lastRedXg = redXg;
            TotalSeconds += deltaSeconds;

            if (_currentSeconds < _bucketSeconds) return;

            // Full buffer: halve the resolution first. The bucket still filling then
            // keeps accumulating until it covers the NEW width, so every stored bucket
            // spans the same time and the average of a merged pair stays honest.
            if (Count >= _pressure.Length) Compact();
            if (_currentSeconds >= _bucketSeconds) CloseBucket();
        }

        /// <summary>Record a goal at the current match time. 0 = blue, 1 = red.</summary>
        public void Goal(int team)
        {
            if (GoalCount >= _goalTimes.Length) return;
            _goalTimes[GoalCount] = TotalSeconds;
            _goalTeams[GoalCount] = team;
            GoalCount++;
        }

        /// <summary>Close the bucket still filling, if it holds anything. Call once at the end.</summary>
        public void Flush()
        {
            if (_currentSeconds > 0f) CloseBucket();
        }

        public float PressureAt(int index) => _pressure[index];
        public float BlueXgAt(int index) => _blueXg[index];
        public float RedXgAt(int index) => _redXg[index];
        public float GoalTimeAt(int index) => _goalTimes[index];
        public int GoalTeamAt(int index) => _goalTeams[index];

        /// <summary>Share of the recorded match that blue spent on top (mean positive pressure), 0..1.</summary>
        public float BlueShare()
        {
            float blue = 0f, red = 0f;
            for (int bucketIndex = 0; bucketIndex < Count; bucketIndex++)
            {
                float value = _pressure[bucketIndex];
                if (value > 0f) blue += value;
                else red -= value;
            }
            float total = blue + red;
            return total > 1e-5f ? blue / total : 0.5f;
        }

        void CloseBucket()
        {
            // Only Flush can arrive here with a full buffer; a short final bucket is
            // acceptable there because nothing is sampled after it.
            if (Count >= _pressure.Length) Compact();

            _pressure[Count] = _currentSeconds > 0f ? _currentSum / _currentSeconds : 0f;
            _blueXg[Count] = _lastBlueXg;
            _redXg[Count] = _lastRedXg;
            Count++;

            _currentSum = 0f;
            _currentSeconds = 0f;
        }

        /// <summary>
        /// Merge neighbours pairwise and double the width. Pressure is a time
        /// average, so the merged value is the mean of the pair (both were full
        /// buckets of equal width); xG is a running total, so the later value wins.
        /// </summary>
        void Compact()
        {
            int half = Count / 2;
            for (int pairIndex = 0; pairIndex < half; pairIndex++)
            {
                int left = pairIndex * 2;
                int right = left + 1;
                _pressure[pairIndex] = 0.5f * (_pressure[left] + _pressure[right]);
                _blueXg[pairIndex] = _blueXg[right];
                _redXg[pairIndex] = _redXg[right];
            }

            // An odd bucket out (impossible at even capacity, kept for safety) is carried over.
            if (Count % 2 != 0)
            {
                _pressure[half] = _pressure[Count - 1];
                _blueXg[half] = _blueXg[Count - 1];
                _redXg[half] = _redXg[Count - 1];
                half++;
            }

            Count = half;
            _bucketSeconds *= 2f;
        }
    }
}
