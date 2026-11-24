using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Zero-allocation procedural spring and easing primitives for presentation
    /// polish (UI cards, net ripples, face micro-expressions, kick follow-through,
    /// sideline reactions).
    ///
    /// Every state holder is a plain value struct with no delegates, no coroutines
    /// and no heap allocations, so callers can step springs in Update/LateUpdate
    /// while clearing the zero-alloc budget in .claude/rules/performance.md.
    /// </summary>
    public static class Agent_Tween
    {
        /// <summary>
        /// Critically / under-damped 1D harmonic spring value.
        /// </summary>
        /// <summary>Sub-steps that keep omega * h at or below 0.5 (well inside the stable range).</summary>
        public static int SubSteps(float dt, float omega) => Mathf.Clamp(Mathf.CeilToInt(dt * omega / 0.5f), 1, 16);

        public struct SpringFloat
        {
            public float Value;
            public float Velocity;

            public SpringFloat(float initial)
            {
                Value = initial;
                Velocity = 0f;
            }

            /// <summary>
            /// Steps the spring toward <paramref name="target"/> using semi-implicit Euler.
            /// <paramref name="angularFrequency"/> controls speed (rad/s, e.g. 18..35);
            /// <paramref name="dampingRatio"/> controls bounce (1 = critical, 0.45 = bouncy).
            /// </summary>
            public float Step(float target, float dt, float angularFrequency = 22f, float dampingRatio = 0.55f)
            {
                if (dt <= 0f) return Value;
                // Clamp the frame so a hitch never explodes a stiff spring, then integrate in
                // sub-steps short enough to stay stable: semi-implicit Euler diverges once
                // omega * h nears 2, and a slow frame (0.05 s) at omega 38 - the goal-post
                // shake - is 1.9, which blew GoalNet's mesh up to 1e20 on a loaded machine.
                float clampedDt = Mathf.Min(dt, 0.05f);
                float omega = Mathf.Max(0.1f, angularFrequency);
                float zeta = Mathf.Max(0.01f, dampingRatio);
                int steps = SubSteps(clampedDt, omega);
                float h = clampedDt / steps;
                for (int i = 0; i < steps; i++)
                {
                    float accel = omega * omega * (target - Value) - 2f * zeta * omega * Velocity;
                    Velocity += accel * h;
                    Value += Velocity * h;
                }
                return Value;
            }

            /// <summary>Injects an instantaneous velocity impulse (e.g. on impact or goal).</summary>
            public void Impulse(float velocityDelta) => Velocity += velocityDelta;
        }

        /// <summary>
        /// 2D harmonic spring for world/local offsets (limbs, head bob, camera/UI punch).
        /// </summary>
        public struct SpringVector2
        {
            public Vector2 Value;
            public Vector2 Velocity;

            public Vector2 Step(Vector2 target, float dt, float angularFrequency = 22f, float dampingRatio = 0.55f)
            {
                if (dt <= 0f) return Value;
                float clampedDt = Mathf.Min(dt, 0.05f);
                float omega = Mathf.Max(0.1f, angularFrequency);
                float zeta = Mathf.Max(0.01f, dampingRatio);
                int steps = SubSteps(clampedDt, omega);
                float h = clampedDt / steps;
                for (int i = 0; i < steps; i++)
                {
                    Vector2 accel = omega * omega * (target - Value) - 2f * zeta * omega * Velocity;
                    Velocity += accel * h;
                    Value += Velocity * h;
                }
                return Value;
            }

            public void Impulse(Vector2 velocityDelta) => Velocity += velocityDelta;
        }

        /// <summary>Overshooting back-ease for snappy card and badge entrances, t in [0, 1].</summary>
        public static float EaseOutBack(float t, float overshoot = 1.70158f)
        {
            t = Mathf.Clamp01(t) - 1f;
            return t * t * ((overshoot + 1f) * t + overshoot) + 1f;
        }

        /// <summary>Elastic bell pulse that starts at 0, peaks at 1 near t = 0.25, and rings back to 0 at t = 1.</summary>
        public static float Punch(float t)
        {
            t = Mathf.Clamp01(t);
            if (t <= 0f || t >= 1f) return 0f;
            return Mathf.Exp(-4.5f * t) * Mathf.Sin(t * Mathf.PI * 2.5f);
        }
    }
}
