using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Checkpoint tags (2026-11-07, AGENTS.md: "Tag each checkpoint with its interface and
    /// physics versions").
    ///
    /// Two halves:
    ///  1. The version strings in <see cref="Agent_Realism"/> are pinned to fingerprints of
    ///     the values they stand for. Change a contract number, a drive/traction/stamina
    ///     field in a scene, a physics material or the fixed timestep without bumping the
    ///     version, and this fails - which is the moment to decide whether every deployed
    ///     brain just became out of distribution. To accept a change: bump the version in
    ///     Agent_Realism and ADD a row below (keep the old rows; they document history).
    ///  2. Every deployed brain carries the tags it was trained under, and they match what
    ///     this runtime would apply to it.
    ///
    /// What a fingerprint cannot see: a change of MEANING inside code (a frame, a sign, a
    /// formula). Those still need a human to bump INTERFACE_VERSION or PHYSICS_VERSION.
    /// </summary>
    public sealed class Agent_EditMode_CheckpointTags
    {
        /// <summary>Interface fingerprint per INTERFACE_VERSION.</summary>
        static readonly Dictionary<string, string> InterfaceFingerprints = new()
        {
            ["iface-2026-10-10"] = "vec34x2|rays105|opp2|act4|gain1",
        };

        /// <summary>Physics fingerprint per PHYSICS_VERSION. Values exactly as serialized.</summary>
        static readonly Dictionary<string, string> PhysicsFingerprints = new()
        {
            ["phys-2026-10-10"] =
                "_moveForce=236;_linearDamping=0.7;_tractionMu=1.2;_lateralDrag=3;_sprintTurnFactor=0.25;" +
                "_turnScaleSpeed=10;_turnTorque=250;_boostMultiplier=2.2;_boostThreshold=0.1;" +
                "_maxAngularVelocityDeg=360;_forceSlewRate=1200;_tiredPowerFloor=0.6;_wallKickImpulse=6;" +
                "_wallKickBand=1;_wallKickCooldown=0.5;maxStamina=100;drainPerSecond=60;rechargePerSecond=25;" +
                "wearPerBoostSecond=0.002;wearFloor=0.6;recoveryDelaySeconds=0.6;depletedRecoveryFactor=0.45;" +
                "magnusScale=0.0005;ballDampingRange={x: 0.08, y: 0.15};m_Mass=0.43/75" +
                "|PM_Agent=0.3/0.05|PM_Ball=0.2/0.3|timestep=1411199/141120000" +
                "|contact=12/45/0.45/0.55/True",
        };

        /// <summary>Scene fields that set a player's or the ball's dynamics, in fingerprint order.</summary>
        static readonly string[] SceneFields =
        {
            "_moveForce", "_linearDamping", "_tractionMu", "_lateralDrag", "_sprintTurnFactor",
            "_turnScaleSpeed", "_turnTorque", "_boostMultiplier", "_boostThreshold",
            "_maxAngularVelocityDeg", "_forceSlewRate", "_tiredPowerFloor", "_wallKickImpulse",
            "_wallKickBand", "_wallKickCooldown", "maxStamina", "drainPerSecond", "rechargePerSecond",
            "wearPerBoostSecond", "wearFloor", "recoveryDelaySeconds", "depletedRecoveryFactor",
            "magnusScale", "ballDampingRange", "m_Mass",
        };

        static string ProjectPath(string relative) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, relative);

        static string CurrentInterfaceFingerprint() =>
            $"vec{Agent_Soccer.BaseObservationSize}x{Agent_Soccer.StackedObservations}" +
            $"|rays{Sensor_Vision.TotalRayObservationSize}" +
            $"|opp{Agent_Soccer.OpponentSlots}" +
            $"|act{Agent_Soccer.ContinuousActionCount}" +
            $"|gain{Agent_Soccer.ActionGain.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>Distinct serialized values of each field across one scene, ordinal-sorted.</summary>
        static string SceneFingerprint(string sceneRelativePath)
        {
            string[] lines = File.ReadAllLines(ProjectPath(sceneRelativePath));
            var parts = new List<string>(SceneFields.Length);
            foreach (string field in SceneFields)
            {
                var values = new SortedSet<string>(System.StringComparer.Ordinal);
                string prefix = "  " + field + ": ";
                foreach (string line in lines)
                {
                    if (line.StartsWith(prefix, System.StringComparison.Ordinal))
                    {
                        values.Add(line.Substring(prefix.Length).Trim());
                    }
                }
                parts.Add(field + "=" + string.Join("/", values));
            }
            return string.Join(";", parts);
        }

        static string MaterialFingerprint(string relative)
        {
            string text = File.ReadAllText(ProjectPath(relative));
            string friction = Regex.Match(text, @"^\s+m_Friction:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;
            string bounce = Regex.Match(text, @"^\s+m_Bounciness:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;
            return friction + "/" + bounce;
        }

        static string TimestepFingerprint()
        {
            string text = File.ReadAllText(ProjectPath("ProjectSettings/TimeManager.asset"));
            string count = Regex.Match(text, @"Fixed Timestep:\s*\n\s+m_Count:\s*(\d+)").Groups[1].Value;
            string numerator = Regex.Match(text, @"m_Numerator:\s*(\d+)").Groups[1].Value;
            return count + "/" + numerator;
        }

        /// <summary>Agent_Contact is added in code, so its C# defaults are the values in force.</summary>
        static string ContactFingerprint()
        {
            var go = new GameObject("ContactProbe");
            try
            {
                var contact = go.AddComponent<Agent_Contact>();
                var so = new SerializedObject(contact);
                string F(string name) =>
                    so.FindProperty(name).floatValue.ToString(CultureInfo.InvariantCulture);
                return $"{F("_staggerThreshold")}/{F("_staggerSaturation")}/{F("_staggerSeconds")}/" +
                       $"{F("_minAuthority")}/{so.FindProperty("_enableStagger").boolValue}";
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static string CurrentPhysicsFingerprint()
        {
            string training = SceneFingerprint("Assets/Scenes/SCN_Training.unity");
            string contact = ContactFingerprint();
            return training +
                   "|PM_Agent=" + MaterialFingerprint("Assets/Agents/Standard_v01/PM_Agent.physicsMaterial2D") +
                   "|PM_Ball=" + MaterialFingerprint("Assets/Agents/Standard_v01/PM_Ball.physicsMaterial2D") +
                   "|timestep=" + TimestepFingerprint() +
                   "|contact=" + contact;
        }

        [Test]
        public void InterfaceVersion_MatchesTheContractItNames()
        {
            Assert.That(InterfaceFingerprints.ContainsKey(Agent_Realism.INTERFACE_VERSION), Is.True,
                $"No fingerprint recorded for {Agent_Realism.INTERFACE_VERSION}. Add a row to InterfaceFingerprints.");
            Assert.That(CurrentInterfaceFingerprint(),
                Is.EqualTo(InterfaceFingerprints[Agent_Realism.INTERFACE_VERSION]),
                "The brain interface changed but Agent_Realism.INTERFACE_VERSION did not. Every deployed " +
                ".onnx was trained against the recorded interface: bump the version, add a row here, and " +
                "plan the retrain (or distillation, AGENTS.md 'Frozen interface').");
        }

        [Test]
        public void PhysicsVersion_MatchesTheDynamicsItNames()
        {
            Assert.That(PhysicsFingerprints.ContainsKey(Agent_Realism.PHYSICS_VERSION), Is.True,
                $"No fingerprint recorded for {Agent_Realism.PHYSICS_VERSION}. Add a row to PhysicsFingerprints.");
            Assert.That(CurrentPhysicsFingerprint(),
                Is.EqualTo(PhysicsFingerprints[Agent_Realism.PHYSICS_VERSION]),
                "The dynamics changed but Agent_Realism.PHYSICS_VERSION did not. A brain trained on the " +
                "recorded dynamics loads without a warning and plays a different body: bump the version, " +
                "add a row here, and treat it as a full retrain (CLAUDE.md, Brain contract).");
        }

        /// <summary>
        /// A brain is trained in SCN_Training and played in SCN_Exhibition. Any difference in
        /// the dynamics fields between the two is a train/deploy mismatch nothing else reports.
        /// </summary>
        [Test]
        public void ExhibitionScene_HasTheTrainingSceneDynamics()
        {
            Assert.That(SceneFingerprint("Assets/Scenes/SCN_Exhibition.unity"),
                Is.EqualTo(SceneFingerprint("Assets/Scenes/SCN_Training.unity")));
        }

        [Test]
        public void EveryDeployedBrain_IsTaggedWithTheVersionsThisRuntimeApplies()
        {
            string[] guids = AssetDatabase.FindAssets("t:Reward_Settings");
            Assert.That(guids.Length, Is.GreaterThan(0), "No Reward_Settings assets found.");
            var problems = new StringBuilder();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(path);
                if (profile == null || profile.brainModel == null)
                {
                    continue;
                }

                string iface = Agent_Realism.InterfaceVersionFor(profile.reactionLatencyMs);
                string physics = Agent_Realism.PhysicsVersionFor(profile.staminaModel);
                if (string.IsNullOrEmpty(profile.trainedInterfaceVersion) || string.IsNullOrEmpty(profile.trainedPhysicsVersion))
                {
                    problems.AppendLine($"  {profile.playerName} ({path}): unstamped. Deploy through " +
                                        "scripts/update-model.ps1 from a run started by train-phase1/train-parallel.");
                    continue;
                }
                if (profile.trainedInterfaceVersion != iface)
                {
                    problems.AppendLine($"  {profile.playerName}: trained on interface {profile.trainedInterfaceVersion}, runtime applies {iface}.");
                }
                if (profile.trainedPhysicsVersion != physics)
                {
                    problems.AppendLine($"  {profile.playerName}: trained on physics {profile.trainedPhysicsVersion}, runtime applies {physics}.");
                }
            }
            Assert.That(problems.Length, Is.Zero,
                "Deployed brains whose checkpoint tags do not match this runtime:\n" + problems);
        }
    }
}
