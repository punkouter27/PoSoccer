using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Version tags and the opt-in realism options (2026-11-07).
    ///
    /// VERSION TAGS. A trained brain is only valid against the interface and the physics it
    /// learned under. Shape changes are caught by the runtime (the model is refused); the
    /// dangerous changes keep the shape and change the meaning (see CLAUDE.md). These two
    /// strings name the current interface and physics so every deployed brain can carry
    /// the pair it was trained against (<see cref="Reward_Settings.trainedInterfaceVersion"/>,
    /// <see cref="Reward_Settings.trainedPhysicsVersion"/>), and so a provenance.json written
    /// by the training scripts can be compared with the runtime that will play it.
    ///
    /// BUMP <see cref="INTERFACE_VERSION"/> when anything a brain reads or writes changes
    /// shape OR meaning: observation count, order, frame or scale, ray arc/range/layers,
    /// action count or ActionGain. BUMP <see cref="PHYSICS_VERSION"/> when the dynamics
    /// change: Agent_Soccer drive/traction/turn fields, Agent_Stamina fields, masses,
    /// physics materials, the fixed timestep. Agent_EditMode_CheckpointTags pins both against
    /// fingerprints of the values they cover, so changing a value without bumping fails.
    ///
    /// REALISM OPTIONS. Each changes what a brain learns against, so each defaults OFF and
    /// the off path is the shipped code path, bit for bit. Resolution order: the
    /// environment variable (training scripts) wins; otherwise the profile's own field.
    ///  - Stamina model (<see cref="Reward_Settings.StaminaModel"/>): POSOCCER_STAMINA_MODEL.
    ///  - Reaction latency (<see cref="Reward_Settings.reactionLatencyMs"/>): POSOCCER_REACTION_MS.
    /// The scripted bot never gets latency: it is the benchmark opponent and must not change.
    /// </summary>
    public static class Agent_Realism
    {
        /// <summary>Interface as of the 173-input contract (2026-10-10).</summary>
        public const string INTERFACE_VERSION = "iface-2026-10-10";

        /// <summary>
        /// Dynamics as of 2026-10-10. No dynamics change landed between the 173-input contract
        /// and 2026-11-07 (checked in git: the 2026-10-13 Agent_Soccer change only exposed
        /// GroundForce and LateralSlipSpeed taps).
        /// </summary>
        public const string PHYSICS_VERSION = "phys-2026-10-10";

        /// <summary>Upper bound on reaction latency. Human simple reaction time is about 200 ms.</summary>
        public const float MAX_REACTION_MS = 400f;

        const string STAMINA_HUMAN = "human";
        const string STAMINA_SHIPPED = "shipped";

        /// <summary>The stamina model this agent plays under: env var, else the profile, else Shipped.</summary>
        public static Reward_Settings.StaminaModel EffectiveStaminaModel(Reward_Settings profile)
        {
            if (Agent_RunConfig.Is(Agent_RunConfig.STAMINA_MODEL, STAMINA_HUMAN))
            {
                return Reward_Settings.StaminaModel.Human;
            }
            if (Agent_RunConfig.Is(Agent_RunConfig.STAMINA_MODEL, STAMINA_SHIPPED)
                || Agent_RunConfig.Is(Agent_RunConfig.STAMINA_MODEL, "0"))
            {
                return Reward_Settings.StaminaModel.Shipped;
            }
            return profile != null ? profile.staminaModel : Reward_Settings.StaminaModel.Shipped;
        }

        /// <summary>Reaction latency in ms: env var, else the profile, else 0. Clamped to [0, MAX].</summary>
        public static float EffectiveReactionMs(Reward_Settings profile)
        {
            float ms = Agent_RunConfig.TryGetFloat(Agent_RunConfig.REACTION_MS, out float fromEnv)
                ? fromEnv
                : profile != null ? profile.reactionLatencyMs : 0f;
            return Mathf.Clamp(ms, 0f, MAX_REACTION_MS);
        }

        /// <summary>Latency in whole physics ticks. 0 for any non-positive latency or timestep.</summary>
        public static int ReactionDelayTicks(float latencyMs, float fixedDeltaTime)
        {
            if (latencyMs <= 0f || fixedDeltaTime <= 0f) return 0;
            return Mathf.Max(0, Mathf.RoundToInt(latencyMs * 0.001f / fixedDeltaTime));
        }

        /// <summary>Interface tag for a brain trained with this latency (latency changes what an action means).</summary>
        public static string InterfaceVersionFor(float reactionLatencyMs)
        {
            int ms = Mathf.RoundToInt(Mathf.Clamp(reactionLatencyMs, 0f, MAX_REACTION_MS));
            return ms <= 0 ? INTERFACE_VERSION : INTERFACE_VERSION + "+lat" + ms;
        }

        /// <summary>Physics tag for a brain trained under this stamina model.</summary>
        public static string PhysicsVersionFor(Reward_Settings.StaminaModel model) =>
            model == Reward_Settings.StaminaModel.Human
                ? PHYSICS_VERSION + "+stamina-human"
                : PHYSICS_VERSION;
    }
}
