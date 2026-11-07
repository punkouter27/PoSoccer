using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the pure logic behind the 2026-11-07 spectator features: the passing-lane
    /// race (Agent_PassLaneMath), the fixed-memory momentum timeline
    /// (Agent_MomentumSeries), the saliency input grouping (Agent_Saliency) and the
    /// stamina ring's thresholds (Agent_StaminaRing).
    ///
    /// Shapes, not tuned numbers, wherever a number is a serialized knob.
    /// </summary>
    public sealed class Agent_EditMode_SpectatorFeatures
    {
        // -- Passing lanes ------------------------------------------------------

        const float BallSpeed = 9f;
        const float RunnerSpeed = 6.5f;
        const float Reach = 0.75f;
        const float Reaction = 0.2f;
        const float Window = 0.45f;

        static float Openness(Vector2 from, Vector2 to, params Vector2[] opponents) =>
            Agent_PassLaneMath.Openness(from, to, opponents, opponents.Length,
                BallSpeed, RunnerSpeed, Reach, Reaction, Window);

        [Test]
        public void Lane_WithNoOpponents_IsFullyOpen()
        {
            Assert.That(Agent_PassLaneMath.Openness(Vector2.zero, new Vector2(0f, 10f), new Vector2[1], 0,
                BallSpeed, RunnerSpeed, Reach, Reaction, Window), Is.EqualTo(1f));
        }

        [Test]
        public void Lane_WithAnOpponentStandingOnIt_IsShut()
        {
            Assert.That(Openness(Vector2.zero, new Vector2(0f, 10f), new Vector2(0f, 5f)), Is.EqualTo(0f));
        }

        [Test]
        public void Lane_WithAnOpponentFarAway_IsOpen()
        {
            Assert.That(Openness(Vector2.zero, new Vector2(0f, 10f), new Vector2(12f, 5f)), Is.EqualTo(1f));
        }

        [Test]
        public void SameOffset_IsWorseNearTheReceiverThanNearThePasser()
        {
            // Three metres off the line: next to the passer the ball is already past
            // him; next to the receiver of a long pass he has all the flight time.
            var from = Vector2.zero;
            var to = new Vector2(0f, 20f);
            float nearPasser = Openness(from, to, new Vector2(3f, 0.5f));
            float nearReceiver = Openness(from, to, new Vector2(3f, 19f));
            Assert.That(nearPasser, Is.GreaterThan(nearReceiver));
            Assert.That(nearReceiver, Is.LessThan(0.2f));
        }

        [Test]
        public void Lane_IsOnlyAsOpenAsItsTightestOpponent()
        {
            var from = Vector2.zero;
            var to = new Vector2(0f, 10f);
            float alone = Openness(from, to, new Vector2(12f, 5f));
            float withBlocker = Openness(from, to, new Vector2(12f, 5f), new Vector2(0.2f, 6f));
            Assert.That(withBlocker, Is.LessThan(alone));
        }

        [Test]
        public void Tint_RunsRedToGreen()
        {
            Color shut = Agent_PassLaneMath.Tint(0f, 1f);
            Color open = Agent_PassLaneMath.Tint(1f, 1f);
            Assert.That(shut.r, Is.GreaterThan(shut.g));
            Assert.That(open.g, Is.GreaterThan(open.r));
        }

        // -- Momentum -----------------------------------------------------------

        [Test]
        public void Momentum_AveragesPressureWithinABucket()
        {
            var series = new Agent_MomentumSeries(capacity: 8, initialBucketSeconds: 1f);
            series.Sample(1f, 0f, 0f, 0.5f);
            series.Sample(0f, 0f, 0f, 0.5f);
            Assert.That(series.Count, Is.EqualTo(1));
            Assert.That(series.PressureAt(0), Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void Momentum_FitsAnyMatchLengthInFixedMemory()
        {
            var series = new Agent_MomentumSeries(capacity: 4, initialBucketSeconds: 1f);
            for (int tick = 0; tick < 200; tick++) series.Sample(0.5f, 0f, 0f, 0.1f);
            series.Flush();

            Assert.That(series.Count, Is.LessThanOrEqualTo(series.Capacity));
            Assert.That(series.BucketSeconds, Is.GreaterThan(1f));
            Assert.That(series.TotalSeconds, Is.EqualTo(20f).Within(1e-3f));

            // Compaction must not bend a constant signal.
            for (int bucketIndex = 0; bucketIndex < series.Count; bucketIndex++)
            {
                Assert.That(series.PressureAt(bucketIndex), Is.EqualTo(0.5f).Within(1e-4f));
            }
        }

        [Test]
        public void Momentum_KeepsTheLatestRunningXg()
        {
            var series = new Agent_MomentumSeries(capacity: 4, initialBucketSeconds: 1f);
            for (int tick = 0; tick < 100; tick++)
            {
                series.Sample(0f, tick * 0.01f, tick * 0.02f, 0.1f);
            }
            series.Flush();
            int last = series.Count - 1;
            Assert.That(series.BlueXgAt(last), Is.EqualTo(0.99f).Within(1e-4f));
            Assert.That(series.RedXgAt(last), Is.EqualTo(1.98f).Within(1e-4f));
        }

        [Test]
        public void Momentum_RecordsGoalsAtMatchTime()
        {
            var series = new Agent_MomentumSeries(capacity: 8, initialBucketSeconds: 1f);
            series.Sample(0f, 0f, 0f, 3f);
            series.Goal(1);
            Assert.That(series.GoalCount, Is.EqualTo(1));
            Assert.That(series.GoalTimeAt(0), Is.EqualTo(3f).Within(1e-5f));
            Assert.That(series.GoalTeamAt(0), Is.EqualTo(1));
        }

        [Test]
        public void Momentum_BlueShareSplitsPressure()
        {
            var series = new Agent_MomentumSeries(capacity: 8, initialBucketSeconds: 1f);
            series.Sample(0.6f, 0f, 0f, 1f);
            series.Sample(-0.2f, 0f, 0f, 1f);
            Assert.That(series.BlueShare(), Is.EqualTo(0.75f).Within(1e-4f));
            Assert.That(new Agent_MomentumSeries(4, 1f).BlueShare(), Is.EqualTo(0.5f));
        }

        // -- Saliency grouping --------------------------------------------------

        [Test]
        public void Saliency_VectorGroups_CoverTheBaseObservationExactly()
        {
            // The grouping mirrors Agent_Soccer.CollectObservations line by line. If the
            // contract changes size, this fails first, which is the point.
            Assert.That(Agent_Soccer.BaseObservationSize, Is.EqualTo(34));

            var counts = new int[Agent_Saliency.GROUP_COUNT];
            for (int index = 0; index < Agent_Soccer.BaseObservationSize; index++)
            {
                counts[(int)Agent_Saliency.VectorGroupOf(index)]++;
            }

            Assert.That(counts[(int)Agent_Saliency.Group.Clock], Is.EqualTo(3));
            Assert.That(counts[(int)Agent_Saliency.Group.Self], Is.EqualTo(5));
            Assert.That(counts[(int)Agent_Saliency.Group.Ball], Is.EqualTo(4));
            Assert.That(counts[(int)Agent_Saliency.Group.Goals], Is.EqualTo(5));
            Assert.That(counts[(int)Agent_Saliency.Group.Teammate], Is.EqualTo(4));
            Assert.That(counts[(int)Agent_Saliency.Group.Opponents], Is.EqualTo(4 * Agent_Soccer.OpponentSlots));
            Assert.That(counts[(int)Agent_Saliency.Group.Role], Is.EqualTo(5));
        }

        [Test]
        public void Saliency_KnowsEveryRaySensorInTheBattery()
        {
            var seen = new bool[Agent_Saliency.GROUP_COUNT];
            for (int specIndex = 0; specIndex < Sensor_Vision.Battery.Length; specIndex++)
            {
                string name = Sensor_Vision.Battery[specIndex].Name;
                Assert.That(Agent_Saliency.TryRayGroupOf(name, out var group), Is.True, name);
                Assert.That(seen[(int)group], Is.False, "two sensors share a group: " + name);
                seen[(int)group] = true;
            }
            Assert.That(Agent_Saliency.TryRayGroupOf("Sensor_Unknown", out _), Is.False);
        }

        [Test]
        public void Saliency_BlankRayReadsAsACleanMiss()
        {
            // Two tags: [tag0, tag1, missFlag, distance] per ray.
            Assert.That(Agent_Saliency.NeutralRayValue(0, 2), Is.EqualTo(0f));
            Assert.That(Agent_Saliency.NeutralRayValue(1, 2), Is.EqualTo(0f));
            Assert.That(Agent_Saliency.NeutralRayValue(2, 2), Is.EqualTo(1f));
            Assert.That(Agent_Saliency.NeutralRayValue(3, 2), Is.EqualTo(1f));
            Assert.That(Agent_Saliency.NeutralRayValue(4, 2), Is.EqualTo(0f));
        }

        [Test]
        public void Saliency_SharesSumToOne_AndZeroStaysZero()
        {
            var raw = new float[Agent_Saliency.GROUP_COUNT];
            var shares = new float[Agent_Saliency.GROUP_COUNT];
            raw[0] = 0.3f;
            raw[4] = 0.1f;
            Agent_Saliency.Normalise(raw, shares);
            float total = 0f;
            for (int groupIndex = 0; groupIndex < shares.Length; groupIndex++) total += shares[groupIndex];
            Assert.That(total, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(shares[0], Is.EqualTo(0.75f).Within(1e-5f));

            System.Array.Clear(raw, 0, raw.Length);
            Agent_Saliency.Normalise(raw, shares);
            for (int groupIndex = 0; groupIndex < shares.Length; groupIndex++)
            {
                Assert.That(shares[groupIndex], Is.EqualTo(0f));
            }
        }

        [Test]
        public void Saliency_HasANameForEveryGroup()
        {
            Assert.That(Agent_Saliency.GroupNames.Length, Is.EqualTo(Agent_Saliency.GROUP_COUNT));
            Assert.That(System.Enum.GetValues(typeof(Agent_Saliency.Group)).Length,
                Is.EqualTo(Agent_Saliency.GROUP_COUNT));
        }

        // -- Stamina ring ---------------------------------------------------------

        [Test]
        public void StaminaRing_AFreshTankDrawsNothing()
        {
            Assert.That(Agent_StaminaRing.ShouldDraw(1f, 1f), Is.False);
            Assert.That(Agent_StaminaRing.ShouldDraw(0.8f, 1f), Is.True);
            Assert.That(Agent_StaminaRing.ShouldDraw(1f, 0.9f), Is.True, "wear alone is worth showing");
        }

        [Test]
        public void StaminaRing_LimitsWhenEmptyOrLow()
        {
            Assert.That(Agent_StaminaRing.IsLimiting(0.9f, true, 0.3f), Is.False);
            Assert.That(Agent_StaminaRing.IsLimiting(0.2f, true, 0.3f), Is.True);
            Assert.That(Agent_StaminaRing.IsLimiting(0.9f, false, 0.3f), Is.True, "no stamina means no boost");
        }

        [Test]
        public void StaminaRing_TankShareIsClamped()
        {
            Assert.That(Agent_StaminaRing.TankShare(50f, 100f), Is.EqualTo(0.5f));
            Assert.That(Agent_StaminaRing.TankShare(150f, 100f), Is.EqualTo(1f));
            Assert.That(Agent_StaminaRing.TankShare(10f, 0f), Is.EqualTo(0f));
        }
    }
}
