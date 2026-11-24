using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// The control penalties (Po Game Standard C9) on a live pitch, and the Tab panel's
    /// training-speed benchmark.
    ///
    /// Each penalty term has its own weight on Reward_Settings and its own slot in the reward
    /// accounting, so this gives every weight a value, lets SCN_Training's agents play, charges
    /// the penalties from the real bodies each physics step, and asserts that EVERY term
    /// (jitter, action_rate_sq, energy, torque_sq, spin_sq, foot_slide) went negative. The
    /// shipped profiles keep 0 (their brains predate the terms); the trainer configs set them.
    /// </summary>
    // Explicit per-test cap (UTF default 180 s); the waits inside are condition-based with their own caps.
    [Timeout(300000)]
    public class Agent_PlayMode_ControlPenalties
    {
        static readonly string[] Terms = { "jitter", "action_rate_sq", "energy", "torque_sq", "spin_sq", "foot_slide" };

        readonly List<Object> _created = new();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Agent_TimeFreeze.ReleaseAll();
            for (int i = 0; i < _created.Count; i++) Object.Destroy(_created[i]);
            _created.Clear();
            yield return null;
        }

        static IEnumerator PlayRealtime(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        static Agent_EnvController LoadPitch()
        {
            return Object.FindAnyObjectByType<Agent_EnvController>();
        }

        [UnityTest]
        public IEnumerator EveryControlPenaltyTerm_IsChargedOnALivePitch()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            var env = LoadPitch();
            Assert.IsNotNull(env, "SCN_Training has no Agent_EnvController");
            Assert.Greater(env.agents.Count, 0, "no agents on the pitch");

            foreach (var agent in env.agents)
            {
                var weights = Object.Instantiate(agent.rewards);
                _created.Add(weights);
                weights.actionJitterScale = 0.01f;
                weights.actionRateSquaredScale = 0.01f;
                weights.energyPenaltyScale = 0.01f;
                weights.torqueSquaredScale = 0.01f;
                weights.spinSquaredScale = 0.01f;
                weights.footSlideScale = 0.01f;
                agent.rewards = weights;
            }

            // Let the bodies get moving (kick-off, chases, turns) before charging.
            yield return PlayRealtime(2f);

            var before = new float[Agent_Soccer.TermCount];
            for (int t = 0; t < Agent_Soccer.TermCount; t++)
                foreach (var agent in env.agents) before[t] += agent.RewardTerm(t);

            for (int step = 0; step < 150; step++)
            {
                yield return new WaitForFixedUpdate();
                float sign = (step & 1) == 0 ? 1f : -1f;
                foreach (var agent in env.agents)
                {
                    // Alternating actions so the action-rate terms see real changes; the
                    // body terms read the live Rigidbody2D and the feet's measured skid.
                    agent.ChargeControlPenalties(0.8f * sign, -0.5f * sign, 0.7f * sign, step % 3 == 0 ? 1f : 0f);
                }
            }

            var report = new System.Text.StringBuilder("penalties charged:");
            foreach (string name in Terms)
            {
                int t = System.Array.IndexOf(Agent_Soccer.TermNames, name);
                Assert.GreaterOrEqual(t, 0, name + " is not a reward-accounting term");
                float charged = -before[t];
                foreach (var agent in env.agents) charged += agent.RewardTerm(t);
                report.Append(' ').Append(name).Append(' ').Append(charged.ToString("0.0000"));
                Assert.Less(charged, 0f, name + " was never charged on a live pitch");
            }
            Debug.Log("[PoSoccer] " + report);
        }

        [UnityTest]
        public IEnumerator BenchmarkTrainingSpeed_TimesTheLivePitch_AndRestoresTheClock()
        {
            SceneManager.LoadScene("SCN_Training");
            yield return null;
            yield return null;
            Assert.IsNotNull(LoadPitch());

            var host = new GameObject("benchmark-test");
            _created.Add(host);
            var benchmark = host.AddComponent<Agent_SimBenchmark>();
            float scaleBefore = Time.timeScale;
            float maxDeltaBefore = Time.maximumDeltaTime;

            Assert.IsTrue(benchmark.Begin(null), benchmark.Result);
            Assert.IsTrue(benchmark.IsRunning);
            // Condition wait: the run ends itself after benchmarkSeconds of real time; the cap only
            // bounds a hang (a loaded machine's last frames can each carry a second of simulation).
            float limit = Agent_DemoModeSettings.Load().benchmarkSeconds + 60f;
            float deadline = Time.realtimeSinceStartup + limit;
            while (benchmark.IsRunning && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.IsFalse(benchmark.IsRunning, "the benchmark did not finish");
            Assert.Greater(benchmark.TicksPerSecond, 50f, benchmark.Result);
            StringAssert.Contains("not a training run", benchmark.Result);
            StringAssert.Contains(Agent_SimBenchmark.Hardware(), benchmark.Result, "the result names the CPU and GPU");
            StringAssert.Contains("Measured:", benchmark.Result);
            Assert.AreEqual(scaleBefore, Time.timeScale, 1e-4f, "time scale not restored");
            Assert.AreEqual(maxDeltaBefore, Time.maximumDeltaTime, 1e-4f, "maximumDeltaTime not restored");
            Debug.Log("[PoSoccer] Benchmark: " + benchmark.Result);
        }
    }
}
