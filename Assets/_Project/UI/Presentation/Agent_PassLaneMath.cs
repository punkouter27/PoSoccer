using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Is a pass along a straight line still on? A race, not a geometry test.
    ///
    /// The obvious "is anybody within a metre of the line" test is wrong in both
    /// directions. A defender two metres off the line next to the receiver has all
    /// the time the ball spends travelling to get there, so a long pass past him is
    /// shut; a defender one metre off the line right next to the passer has no time
    /// at all, but he also has nothing to reach if the ball is already past. What
    /// decides a lane is whether any opponent can get a foot to SOME point on it
    /// before the ball does.
    ///
    /// For each opponent the closest point on the lane is taken as his best
    /// interception point. The ball needs along / ballSpeed to arrive there; he needs
    /// reaction + max(0, lateral - reach) / runnerSpeed. The margin between the two
    /// is how open the lane is against him, and the lane is only as open as its
    /// tightest opponent.
    ///
    /// The closest point is not always the best interception point (a fast runner
    /// can prefer a point further down the lane), so this slightly flatters long
    /// lanes. That bias is deliberate for an overlay: it errs toward "open" in
    /// exactly the cases where the race is close enough that the colour is amber
    /// anyway.
    ///
    /// Pure and allocation-free so the overlay can call it per lane per frame and
    /// the EditMode tests can pin its shape without a scene.
    /// </summary>
    public static class Agent_PassLaneMath
    {
        /// <summary>
        /// Openness of the lane from <paramref name="from"/> to <paramref name="to"/>,
        /// 0 (an opponent wins the race comfortably) .. 1 (nobody gets close).
        /// </summary>
        /// <param name="opponents">Opponent positions; only the first <paramref name="count"/> are read.</param>
        /// <param name="ballSpeed">Pass speed, m/s.</param>
        /// <param name="runnerSpeed">Speed an opponent closes on the lane at, m/s.</param>
        /// <param name="reach">Distance at which an opponent can touch the ball without moving, m.</param>
        /// <param name="reaction">Seconds before an opponent starts to move.</param>
        /// <param name="window">Margin, in seconds, that counts as fully open.</param>
        public static float Openness(Vector2 from, Vector2 to, Vector2[] opponents, int count,
                                     float ballSpeed, float runnerSpeed, float reach,
                                     float reaction, float window)
        {
            if (opponents == null || count <= 0) return 1f;

            Vector2 lane = to - from;
            float lengthSquared = lane.sqrMagnitude;
            if (lengthSquared < 1e-6f) return 1f;

            float length = Mathf.Sqrt(lengthSquared);
            float safeBall = Mathf.Max(0.1f, ballSpeed);
            float safeRunner = Mathf.Max(0.1f, runnerSpeed);
            float safeWindow = Mathf.Max(0.01f, window);

            float openness = 1f;
            int limit = Mathf.Min(count, opponents.Length);
            for (int opponentIndex = 0; opponentIndex < limit; opponentIndex++)
            {
                float margin = Margin(from, lane, length, lengthSquared, opponents[opponentIndex],
                    safeBall, safeRunner, reach, reaction);
                float againstThisOne = Mathf.Clamp01(margin / safeWindow);
                if (againstThisOne < openness) openness = againstThisOne;
            }
            return openness;
        }

        /// <summary>
        /// Seconds by which the ball beats one opponent to his best point on the lane.
        /// Negative means he gets there first.
        /// </summary>
        public static float Margin(Vector2 from, Vector2 lane, float length, float lengthSquared,
                                   Vector2 opponent, float ballSpeed, float runnerSpeed,
                                   float reach, float reaction)
        {
            float t = Mathf.Clamp01(Vector2.Dot(opponent - from, lane) / lengthSquared);
            Vector2 closest = from + lane * t;

            float along = t * length;
            float lateral = (opponent - closest).magnitude;

            float ballTime = along / ballSpeed;
            float runnerTime = reaction + Mathf.Max(0f, lateral - reach) / runnerSpeed;
            return runnerTime - ballTime;
        }

        /// <summary>Red (shut) through amber to green (open). Alpha is the caller's.</summary>
        public static Color Tint(float openness, float alpha)
        {
            openness = Mathf.Clamp01(openness);
            Color shut = new(1f, 0.28f, 0.22f, alpha);
            Color tight = new(1f, 0.78f, 0.2f, alpha);
            Color open = new(0.3f, 0.95f, 0.45f, alpha);
            return openness < 0.5f
                ? Color.Lerp(shut, tight, openness * 2f)
                : Color.Lerp(tight, open, (openness - 0.5f) * 2f);
        }
    }
}
