using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Energy system per PRD: 100 max, 60/s drain while boosting, 25/s recharge.
    /// Also implements UNITY_RULES exertion degradation: sustained boost accumulates
    /// wear that shrinks effective max stamina over continuous evaluation cycles.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_Stamina : MonoBehaviour
    {
        [Header("Stamina (PRD)")]
        public float maxStamina = 100f;
        public float drainPerSecond = 60f;
        public float rechargePerSecond = 25f;

        [Header("Exertion degradation (wear-and-tear)")]
        [Tooltip("Wear accumulated per second of boosting. Wear shrinks the effective stamina ceiling.")]
        public float wearPerBoostSecond = 0.002f;
        [Range(0.3f, 1f)]
        [Tooltip("Effective max stamina never degrades below this fraction of maxStamina.")]
        public float wearFloor = 0.6f;
        [Tooltip("If true, wear resets on every episode (training). Leave false for game-play fatigue across cycles.")]
        public bool resetWearOnEpisode = false;

        [Header("Recovery")]
        [Tooltip("Seconds after boosting stops before stamina begins to recover. A real " +
                 "athlete does not start refilling the instant they stop sprinting.")]
        public float recoveryDelaySeconds = 0.6f;
        [Tooltip("Recovery rate multiplier when completely drained. Recovery from full " +
                 "depletion is slower than topping up, so this stays below 1.")]
        [Range(0.2f, 1f)]
        public float depletedRecoveryFactor = 0.45f;

        float _recoveryCooldown;

        public float Current { get; private set; }
        public float Wear { get; private set; }

        public float EffectiveMax => maxStamina * Mathf.Max(wearFloor, 1f - Wear);
        public float Ratio => EffectiveMax > 0f ? Mathf.Clamp01(Current / EffectiveMax) : 0f;
        public bool HasStamina => Current > 0f;

        void Awake() => Current = maxStamina;

        /// <summary>
        /// True while a trainer or an evaluation run is driving the environment.
        ///
        /// Wear must ALWAYS reset per episode in those modes. Serialized false in
        /// both scenes, wear accrued at 0.002 per second of boosting toward a 0.4
        /// cap - reached after only ~200 seconds of cumulative boost, i.e. inside
        /// the first ~1% of a 3M-step run. Every run after that trained a body
        /// pinned at the wear floor, and because Ratio normalises by EffectiveMax
        /// the observation hid it. That is a silently non-stationary environment,
        /// which is the one thing an RL setup must not have.
        /// </summary>
        static bool HeadlessRun =>
            Agent_EvalStats.EvalMode || Unity.MLAgents.Academy.Instance.IsCommunicatorOn;

        /// <summary>Advance the stamina simulation one physics tick.</summary>
        public void Tick(bool boosting, float deltaTime)
        {
            if (boosting && Current > 0f)
            {
                Current = Mathf.Max(0f, Current - drainPerSecond * deltaTime);
                Wear = Mathf.Min(1f - wearFloor, Wear + wearPerBoostSecond * deltaTime);
                _recoveryCooldown = recoveryDelaySeconds;
                return;
            }

            // Recovery is delayed, then ramps with how full the tank already is:
            // fast when topping up, slow out of full depletion.
            _recoveryCooldown -= deltaTime;
            if (_recoveryCooldown > 0f) return;

            // Only the part of the timestep AFTER the delay expired counts. The
            // first version credited the whole tick the moment the delay lapsed,
            // which quietly handed back more stamina than elapsed time allowed.
            float active = Mathf.Min(deltaTime, -_recoveryCooldown);
            _recoveryCooldown = 0f;

            float fullness = EffectiveMax > 0f ? Current / EffectiveMax : 0f;
            float rate = rechargePerSecond *
                         Mathf.Lerp(depletedRecoveryFactor, 1f, Mathf.Sqrt(Mathf.Clamp01(fullness)));
            Current = Mathf.Min(EffectiveMax, Current + rate * active);
        }

        /// <summary>
        /// Opt-in repeated-sprint model (2026-11-07, item 43). <see cref="Reward_Settings.StaminaModel.Shipped"/>
        /// returns without touching a field, so every deployed brain keeps the stamina it
        /// trained under. See Agent_Realism for how the model is chosen.
        ///
        /// What the shipped model is, measured against people: 100 units drained at 60/s is
        /// 1.7 s of boost, refilled at 25/s in about 4 s. Top speed is NOT the problem - the
        /// measured sprint is 9.54 m/s (docs/rules-exemptions.md, fixed-timestep entry), a
        /// real sprint. The tank is: a footballer holds a
        /// maximal sprint for about 6 s (the phosphocreatine window), needs a work:rest of
        /// roughly 1:3 or more to repeat it, and loses some 10-20% of repeat-sprint output
        /// over a match. The values below are those three facts, approximately; they are a
        /// published-literature calibration, not a fit to tracking data.
        /// </summary>
        public void ApplyModel(Reward_Settings.StaminaModel model)
        {
            if (model != Reward_Settings.StaminaModel.Human) return;

            maxStamina = 100f;
            drainPerSecond = 100f / 6f;        // ~6 s of maximal sprint from full
            rechargePerSecond = 6f;            // ~17 s to refill at the full-tank rate
            recoveryDelaySeconds = 1.0f;
            depletedRecoveryFactor = 0.35f;    // slower still out of full depletion
            wearPerBoostSecond = 0.01f;        // ~10% of the ceiling per 10 s of cumulative sprint
            wearFloor = 0.8f;                  // repeated-sprint decrement caps at 20%
            // Called once from Agent_Soccer.Start, before the first episode: start full.
            Current = EffectiveMax;
        }

        public void ResetForEpisode()
        {
            // Wear FIRST: EffectiveMax is derived from it, so filling the tank
            // before clearing wear would start every episode short of full.
            if (resetWearOnEpisode || HeadlessRun) Wear = 0f;
            Current = EffectiveMax;
            _recoveryCooldown = 0f;
        }
    }
}
