using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the opt-in realism options (Agent_Realism, 2026-11-07) to OFF by default.
    ///
    /// Each option changes what a brain learns against without changing a tensor shape -
    /// the dangerous kind of change in CLAUDE.md. Every deployed .onnx trained with both
    /// off, so the guarantee these tests hold is that nothing turns them on unless a run
    /// or a profile explicitly asks: no env var and no profile field means the shipped
    /// stamina values and zero action delay.
    /// </summary>
    public sealed class Agent_EditMode_Realism
    {
        string _savedStamina;
        string _savedReaction;

        [SetUp]
        public void SaveEnvironment()
        {
            _savedStamina = Environment.GetEnvironmentVariable(Agent_RunConfig.STAMINA_MODEL);
            _savedReaction = Environment.GetEnvironmentVariable(Agent_RunConfig.REACTION_MS);
            Environment.SetEnvironmentVariable(Agent_RunConfig.STAMINA_MODEL, null);
            Environment.SetEnvironmentVariable(Agent_RunConfig.REACTION_MS, null);
        }

        [TearDown]
        public void RestoreEnvironment()
        {
            Environment.SetEnvironmentVariable(Agent_RunConfig.STAMINA_MODEL, _savedStamina);
            Environment.SetEnvironmentVariable(Agent_RunConfig.REACTION_MS, _savedReaction);
        }

        static IEnumerable<(string path, Reward_Settings profile)> Profiles()
        {
            string[] guids = AssetDatabase.FindAssets("t:Reward_Settings");
            Assert.That(guids.Length, Is.GreaterThan(0), "No Reward_Settings assets found.");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(path);
                if (profile != null)
                {
                    yield return (path, profile);
                }
            }
        }

        [Test]
        public void NewProfile_DefaultsToShippedStaminaAndNoLatency()
        {
            var profile = ScriptableObject.CreateInstance<Reward_Settings>();
            try
            {
                Assert.That(profile.staminaModel, Is.EqualTo(Reward_Settings.StaminaModel.Shipped));
                Assert.That(profile.reactionLatencyMs, Is.EqualTo(0f));
                Assert.That(Agent_Realism.EffectiveStaminaModel(profile), Is.EqualTo(Reward_Settings.StaminaModel.Shipped));
                Assert.That(Agent_Realism.EffectiveReactionMs(profile), Is.EqualTo(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void NoProfileAndNoEnvironment_IsOff()
        {
            Assert.That(Agent_Realism.EffectiveStaminaModel(null), Is.EqualTo(Reward_Settings.StaminaModel.Shipped));
            Assert.That(Agent_Realism.EffectiveReactionMs(null), Is.EqualTo(0f));
            Assert.That(Agent_Realism.ReactionDelayTicks(0f, 0.01f), Is.EqualTo(0));
        }

        /// <summary>
        /// Every shipped profile carries both options off, which is how every deployed brain
        /// was trained. A profile that turns one on must also carry a brain trained with it
        /// (Agent_EditMode_CheckpointTags checks that pairing once it is stamped).
        /// </summary>
        [Test]
        public void EveryShippedProfile_HasRealismOptionsOff()
        {
            var on = new StringBuilder();
            foreach (var (path, profile) in Profiles())
            {
                if (profile.staminaModel != Reward_Settings.StaminaModel.Shipped || profile.reactionLatencyMs > 0f)
                {
                    bool stamped = !string.IsNullOrEmpty(profile.trainedPhysicsVersion)
                                   && profile.trainedPhysicsVersion == Agent_Realism.PhysicsVersionFor(profile.staminaModel)
                                   && profile.trainedInterfaceVersion == Agent_Realism.InterfaceVersionFor(profile.reactionLatencyMs);
                    if (!stamped)
                    {
                        on.AppendLine($"  {profile.playerName} ({path}): staminaModel={profile.staminaModel}, " +
                                      $"reactionLatencyMs={profile.reactionLatencyMs}, but its brain is not tagged as trained with them.");
                    }
                }
            }
            Assert.That(on.Length, Is.Zero,
                "A profile turns a realism option on without a brain trained under it - the brain " +
                "would read a different world:\n" + on);
        }

        [Test]
        public void EnvironmentOverridesProfile_BothWays()
        {
            var profile = ScriptableObject.CreateInstance<Reward_Settings>();
            try
            {
                Environment.SetEnvironmentVariable(Agent_RunConfig.STAMINA_MODEL, "human");
                Assert.That(Agent_Realism.EffectiveStaminaModel(profile), Is.EqualTo(Reward_Settings.StaminaModel.Human));

                profile.staminaModel = Reward_Settings.StaminaModel.Human;
                Environment.SetEnvironmentVariable(Agent_RunConfig.STAMINA_MODEL, "shipped");
                Assert.That(Agent_Realism.EffectiveStaminaModel(profile), Is.EqualTo(Reward_Settings.StaminaModel.Shipped));

                Environment.SetEnvironmentVariable(Agent_RunConfig.STAMINA_MODEL, null);
                Assert.That(Agent_Realism.EffectiveStaminaModel(profile), Is.EqualTo(Reward_Settings.StaminaModel.Human),
                    "Unset env var must fall back to the profile.");

                Environment.SetEnvironmentVariable(Agent_RunConfig.REACTION_MS, "200");
                Assert.That(Agent_Realism.EffectiveReactionMs(profile), Is.EqualTo(200f));
                Environment.SetEnvironmentVariable(Agent_RunConfig.REACTION_MS, "5000");
                Assert.That(Agent_Realism.EffectiveReactionMs(profile), Is.EqualTo(Agent_Realism.MAX_REACTION_MS));
                Environment.SetEnvironmentVariable(Agent_RunConfig.REACTION_MS, "-50");
                Assert.That(Agent_Realism.EffectiveReactionMs(profile), Is.EqualTo(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ReactionDelay_RoundsToWholeTicks()
        {
            Assert.That(Agent_Realism.ReactionDelayTicks(200f, 0.01f), Is.EqualTo(20));
            Assert.That(Agent_Realism.ReactionDelayTicks(80f, 0.01f), Is.EqualTo(8));
            Assert.That(Agent_Realism.ReactionDelayTicks(200f, 0f), Is.EqualTo(0));
        }

        [Test]
        public void VersionTags_AreTheBaseTagsWhenOff()
        {
            Assert.That(Agent_Realism.InterfaceVersionFor(0f), Is.EqualTo(Agent_Realism.INTERFACE_VERSION));
            Assert.That(Agent_Realism.PhysicsVersionFor(Reward_Settings.StaminaModel.Shipped),
                Is.EqualTo(Agent_Realism.PHYSICS_VERSION));
            Assert.That(Agent_Realism.InterfaceVersionFor(200f), Is.EqualTo(Agent_Realism.INTERFACE_VERSION + "+lat200"));
            Assert.That(Agent_Realism.PhysicsVersionFor(Reward_Settings.StaminaModel.Human),
                Is.EqualTo(Agent_Realism.PHYSICS_VERSION + "+stamina-human"));
        }

        /// <summary>The Shipped model must not touch a single serialized stamina field.</summary>
        [Test]
        public void ShippedStaminaModel_LeavesEveryFieldUnchanged()
        {
            var go = new GameObject("StaminaProbe");
            try
            {
                var stamina = go.AddComponent<Agent_Stamina>();
                float[] before = Snapshot(stamina);
                stamina.ApplyModel(Reward_Settings.StaminaModel.Shipped);
                Assert.That(Snapshot(stamina), Is.EqualTo(before));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// The Human model is the calibration it claims: about 6 s of sprint from a full tank,
        /// and a repeated-sprint decrement that never exceeds 20% of the ceiling.
        /// </summary>
        [Test]
        public void HumanStaminaModel_GivesAboutSixSecondsOfSprint()
        {
            var go = new GameObject("StaminaProbe");
            try
            {
                var stamina = go.AddComponent<Agent_Stamina>();
                stamina.ApplyModel(Reward_Settings.StaminaModel.Human);
                Assert.That(stamina.maxStamina / stamina.drainPerSecond, Is.InRange(5.5f, 6.5f));
                Assert.That(stamina.wearFloor, Is.GreaterThanOrEqualTo(0.8f));
                Assert.That(stamina.Current, Is.EqualTo(stamina.EffectiveMax));

                float t = 0f;
                while (stamina.HasStamina && t < 20f)
                {
                    stamina.Tick(true, 0.01f);
                    t += 0.01f;
                }
                Assert.That(t, Is.InRange(5.5f, 6.5f), "Seconds of continuous boost from a full tank.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// "Mass scaled to size" (AGENTS.md / CLAUDE.md): every shipped profile's mass is the
        /// 75 kg reference times bodyScale squared (the top-down body's area). Item 46 was
        /// already true when it was listed; this pins it.
        /// </summary>
        [Test]
        public void EveryProfile_MassScalesWithBodyArea()
        {
            var off = new StringBuilder();
            foreach (var (path, profile) in Profiles())
            {
                float expected = 75f * profile.bodyScale * profile.bodyScale;
                if (Mathf.Abs(profile.bodyMass - expected) > 1.5f)
                {
                    off.AppendLine($"  {profile.playerName} ({path}): bodyScale {profile.bodyScale} -> " +
                                   $"expected ~{expected:0.#} kg, has {profile.bodyMass} kg.");
                }
            }
            Assert.That(off.Length, Is.Zero, "Profile mass does not scale with body area:\n" + off);
        }

        static float[] Snapshot(Agent_Stamina s) => new[]
        {
            s.maxStamina, s.drainPerSecond, s.rechargePerSecond, s.wearPerBoostSecond,
            s.wearFloor, s.recoveryDelaySeconds, s.depletedRecoveryFactor, s.Current,
        };
    }
}
