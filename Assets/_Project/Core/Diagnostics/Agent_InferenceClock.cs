using System.Diagnostics;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Inference latency for the TAB overlay (Po Game Standard, C12): a rolling mean
    /// of how long the policy step takes, in milliseconds.
    ///
    /// ML-Agents runs a step as AgentSendState (each deciding agent's
    /// CollectObservations) -> DecideAction (every brain's batched forward pass,
    /// input tensors included) -> AgentAct (each agent's OnActionReceived). Its own
    /// timers are internal to the package, so the interval is bracketed from the
    /// outside instead: the end of the last CollectObservations to the first
    /// OnActionReceived of the same step is exactly DecideAction. Only steps on which
    /// some agent asked for a decision count - between decisions ML-Agents replays the
    /// last action and OnActionReceived arrives with no observation before it.
    ///
    /// A heuristic (bot) brain is measured the same way; the overlay says how many
    /// samples the mean holds. Two Stopwatch reads per decision step, no allocation.
    /// </summary>
    public static class Agent_InferenceClock
    {
        /// <summary>Decision steps the rolling mean covers.</summary>
        public const int WINDOW = 30;

        static readonly double[] Samples = new double[WINDOW];
        static int _count;
        static int _head;
        static long _observedAt;
        static bool _pending;

        /// <summary>Rolling mean in ms, or -1 before the first decision step.</summary>
        public static double AverageMs
        {
            get
            {
                if (_count == 0) return -1d;
                double sum = 0d;
                for (int i = 0; i < _count; i++) sum += Samples[i];
                return sum / _count;
            }
        }

        /// <summary>Samples in the window (0..WINDOW).</summary>
        public static int SampleCount => _count;

        /// <summary>Called at the end of every Agent_Soccer.CollectObservations.</summary>
        public static void ObservationsCollected()
        {
            _observedAt = Stopwatch.GetTimestamp();
            _pending = true;
        }

        /// <summary>Called at the start of every Agent_Soccer.OnActionReceived; only the first after observations records.</summary>
        public static void ActionReceived()
        {
            if (!_pending) return;
            _pending = false;
            Record((Stopwatch.GetTimestamp() - _observedAt) * 1000d / Stopwatch.Frequency);
        }

        /// <summary>Adds one sample (ms) to the window. Public for tests.</summary>
        public static void Record(double ms)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0d) return;
            Samples[_head] = ms;
            _head = (_head + 1) % WINDOW;
            if (_count < WINDOW) _count++;
        }

        /// <summary>Empties the window.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            _count = 0;
            _head = 0;
            _pending = false;
        }
    }
}
