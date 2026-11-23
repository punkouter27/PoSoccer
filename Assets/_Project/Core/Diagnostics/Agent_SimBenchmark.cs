using System.Globalization;
using Unity.MLAgents;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// The Tab panel's BENCHMARK SIM SPEED button: runs this pitch's own simulation
    /// (every agent observing, deciding and moving, Physics2D on its fixed 0.02 s step) as
    /// fast as this device manages for a few real seconds, and counts the physics ticks and
    /// the ML-Agents environment steps it got through.
    ///
    /// WHAT IT IS AND IS NOT. It is the environment side of training: the same fixed step a
    /// headless env runs, sped up through <see cref="Agent_TimeFreeze.BenchmarkScale"/> (the
    /// time scale only; fixedDeltaTime is untouched). It is NOT a training run (no trainer,
    /// no gradient step), so the result is labelled local simulation speed. The match visibly
    /// fast-forwards while it runs, then returns to the viewer's speed. Duration and time
    /// scale come from <see cref="Agent_DemoModeSettings"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_SimBenchmark : MonoBehaviour
    {
        Agent_DemoModeSettings _settings;
        Agent_EnvController _env;
        object _pauseToken;
        bool _running;
        int _ticks;
        int _startSteps;
        float _realElapsed;
        float _savedMaximumDeltaTime;

        /// <summary>The last result in words; empty before the first run.</summary>
        public string Result { get; private set; } = string.Empty;

        /// <summary>Physics ticks per real second of the last run; 0 before the first.</summary>
        public float TicksPerSecond { get; private set; }

        /// <summary>ML-Agents environment steps per real second of the last run.</summary>
        public float EnvStepsPerSecond { get; private set; }

        public bool IsRunning => _running;

        /// <summary>
        /// Starts a run. <paramref name="pauseToken"/> is a freeze holder (the Tab panel's) to
        /// lift for the run and put back after it; null when the caller holds none.
        /// False, with <see cref="Result"/> saying why, where there is nothing to time.
        /// </summary>
        public bool Begin(object pauseToken)
        {
            if (_running) return false;
            if (_settings == null) _settings = Agent_DemoModeSettings.Load();
            _env = FindAnyObjectByType<Agent_EnvController>();
            if (_env == null || _env.agents.Count == 0)
            {
                Result = "Benchmark: start a match first - the menu has no simulation to time.";
                return false;
            }

            _running = true;
            _ticks = 0;
            _realElapsed = 0f;
            _startSteps = Academy.IsInitialized ? Academy.Instance.TotalStepCount : 0;
            _pauseToken = pauseToken;
            if (_pauseToken != null) Agent_TimeFreeze.Release(_pauseToken);
            // One frame may carry up to a second of simulation, so the count is bounded by
            // this device's CPU rather than by Unity's catch-up cap.
            _savedMaximumDeltaTime = Time.maximumDeltaTime;
            Time.maximumDeltaTime = 1f;
            Agent_TimeFreeze.BenchmarkScale = _settings.benchmarkTimeScale;
            Result = "Benchmark running...";
            return true;
        }

        void FixedUpdate()
        {
            if (_running) _ticks++;
        }

        void Update()
        {
            if (!_running) return;
            _realElapsed += Time.unscaledDeltaTime;
            if (_realElapsed >= _settings.benchmarkSeconds) Stop(true);
        }

        void OnDisable() => Stop(false);

        /// <summary>Ends a run without a result (the Tab panel closing mid-run).</summary>
        public void Cancel() => Stop(false);

        void Stop(bool report)
        {
            if (!_running) return;
            _running = false;
            Agent_TimeFreeze.BenchmarkScale = 1f;
            Time.maximumDeltaTime = _savedMaximumDeltaTime;
            if (_pauseToken != null) Agent_TimeFreeze.Acquire(_pauseToken);
            _pauseToken = null;
            if (!report) return;

            int steps = Academy.IsInitialized ? Academy.Instance.TotalStepCount - _startSteps : 0;
            TicksPerSecond = _realElapsed > 0f ? _ticks / _realElapsed : 0f;
            EnvStepsPerSecond = _realElapsed > 0f ? steps / _realElapsed : 0f;
            int agents = _env != null ? _env.agents.Count : 0;
            int period = 1;
            if (agents > 0 && _env.agents[0] != null)
            {
                var requester = _env.agents[0].GetComponent<Unity.MLAgents.DecisionRequester>();
                if (requester != null) period = Mathf.Max(1, requester.DecisionPeriod);
            }
            float decisions = EnvStepsPerSecond / period * agents;
            Result = string.Format(CultureInfo.InvariantCulture,
                "{0:N0} physics ticks/s · {1:N0} env steps/s · {2:N0} agent decisions/s ({3} agents, 1 pitch, {4:0.0} s, this device). " +
                "Local simulation speed, not a training run.",
                TicksPerSecond, EnvStepsPerSecond, decisions, agents, _realElapsed);
            // The requested time scale caps the tick rate; reaching it means the device has headroom.
            float cap = _settings.benchmarkTimeScale / Mathf.Max(1e-4f, Time.fixedDeltaTime);
            if (TicksPerSecond >= cap * 0.97f)
                Result += string.Format(CultureInfo.InvariantCulture,
                    " Reached the {0:0}x time-scale cap ({1:N0} ticks/s): this device can go faster.",
                    _settings.benchmarkTimeScale, cap);
            Debug.Log("[PoSoccer] Benchmark: " + Result);
        }
    }
}
