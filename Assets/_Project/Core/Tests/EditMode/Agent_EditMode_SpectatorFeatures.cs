using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the pure math, physics, and atlas invariants of the 11 spectator &
    /// realism upgrades:
    ///  - #2  Dynamic Head/Gaze Tracking & Facial Micro-Expressions (Agent_Faces)
    ///  - #3  Procedural Kick Wind-Up, Follow-Through & Tackle Lunge (Agent_Limbs)
    ///  - #4  Dynamic Weather & Turf Physics Mutator + Open-Meteo parser (Agent_Weather)
    ///  - #6  Spoken Commentary Synth & 16-bit PCM WAV Encoder (Agent_VoiceSynth)
    ///  - #7  Spring-Verlet Deformable Goal Nets (Agent_GoalFrame)
    ///  - #8  2.5D Ball Height / Bounce / Header Solver (Agent_BallHeight)
    ///  - #12 "Bet on Brains" Elo Odds & PoCoins Settlement (Agent_Wager)
    ///  - #13 Zero-Alloc Spring/Tween Engine & Sports Prop Atlas (Agent_Tween, Agent_Art)
    ///  - #15 Sideline Linesmen & Technical-Area Bench Coaches (Agent_Sidelines)
    ///  - #16 Goalkeeper Gloves & Lateral Dive Reach (Agent_Limbs)
    ///  - #17 Tactical Pass Network Detection (Agent_Dossier)
    /// </summary>
    public sealed class Agent_EditMode_SpectatorFeatures
    {
        [TearDown]
        public void TearDown()
        {
            Agent_Weather.CurrentCondition = Agent_Weather.Condition.Dry;
        }

        [Test]
        public void Tween_SpringFloat_ConvergesToTarget()
        {
            var spring = new Agent_Tween.SpringFloat(0f);
            for (int i = 0; i < 120; i++)
            {
                spring.Step(10f, 1f / 60f, angularFrequency: 22f, dampingRatio: 0.75f);
            }
            Assert.AreEqual(10f, spring.Value, 0.05f);
            Assert.AreEqual(0f, Agent_Tween.Punch(0f));
            Assert.AreEqual(0f, Agent_Tween.Punch(1f), 0.001f);
            Assert.Greater(Agent_Tween.Punch(0.2f), 0.4f);
            Assert.Greater(Agent_Tween.EaseOutBack(0.5f), 0.5f);
        }

        [Test]
        public void Art_SportsProps_ShareSingleAtlasPage()
        {
            Sprite cleat = Agent_Art.Cleat(0.28f);
            Sprite flag = Agent_Art.Flag(0.30f);
            Sprite glove = Agent_Art.Glove(0.26f);

            Assert.IsNotNull(cleat);
            Assert.IsNotNull(flag);
            Assert.IsNotNull(glove);
            Assert.AreSame(Agent_Art.Page, cleat.texture);
            Assert.AreSame(Agent_Art.Page, flag.texture);
            Assert.AreSame(Agent_Art.Page, glove.texture);
        }

        [Test]
        public void Faces_GazeAndMicroExpressions_ClampAndScaleCleanly()
        {
            // Looking 90 degrees to the left (-rightAxis) should clamp at +MAX_GAZE_DEG (22 deg).
            float gazeLeft = Agent_Faces.ComputeGazeAngleDeg(
                Vector2.zero, Vector2.up, Vector2.right, Vector2.left * 3f, Agent_Faces.MAX_GAZE_DEG);
            Assert.AreEqual(Agent_Faces.MAX_GAZE_DEG, gazeLeft, 0.01f);

            // Micro-expression flinch widens X and squashes Y.
            Vector2 flinchScale = Agent_Faces.ComputeExpressionScale(1f, 0.5f, 0f, 0f, 0f);
            Assert.Greater(flinchScale.x, 1f);
            Assert.Less(flinchScale.y, 1f);
        }

        [Test]
        public void Limbs_KickSwingAndGoalieDive_FollowBiomechanics()
        {
            // Pre-contact wind-up pulls the kicking foot back (< 0).
            float windUp = Agent_Limbs.ComputeKickSwingOffset(
                bodyWidth: 0.8f, ballDist: 0.5f, closingSpeed: 4f,
                kickFollowTimer: 0f, isBoosting: false, out float windStretch);
            Assert.Less(windUp, 0f);
            Assert.Greater(windStretch, 1f);

            // Follow-through drives the kicking foot forward (> 0).
            float followThrough = Agent_Limbs.ComputeKickSwingOffset(
                bodyWidth: 0.8f, ballDist: 0.5f, closingSpeed: 4f,
                kickFollowTimer: 0.11f, isBoosting: true, out float followStretch);
            Assert.Greater(followThrough, 0.1f);
            Assert.Greater(followStretch, 1.1f);

            // Goalie dive reaches laterally toward a fast shot heading at goal.
            Vector2 goaliePos = new Vector2(0f, -7.5f);
            Vector2 ballPos = new Vector2(1.4f, -4.5f);
            Vector2 ballVel = new Vector2(-1.2f, -12f);
            Vector2 dive = Agent_Limbs.ComputeGoalieDiveOffset(
                goaliePos, Vector2.up, Vector2.right, ballPos, ballVel, lateralSlipSpeed: 2f, bodyWidth: 0.8f);
            Assert.Greater(dive.x, 0.05f);
            Assert.Greater(dive.y, 0.05f);
        }

        [Test]
        public void GoalFrame_SpringVerletNet_DeformsAndPinsPosts()
        {
            float[] disp = new float[Agent_GoalFrame.NET_NODES];
            float[] vel = new float[Agent_GoalFrame.NET_NODES];

            Agent_GoalFrame.InjectNetImpact(vel, normalizedX: 0f, speed: 10f);
            Agent_GoalFrame.StepNetSprings(disp, vel, 1f / 60f);

            Assert.AreEqual(0f, disp[0], 1e-5f, "Left goalpost node must remain pinned.");
            Assert.AreEqual(0f, disp[disp.Length - 1], 1e-5f, "Right goalpost node must remain pinned.");
            Assert.Greater(disp[disp.Length / 2], 0.02f, "Centre net node should bulge outward on goal impact.");

            for (int i = 0; i < 240; i++)
            {
                Agent_GoalFrame.StepNetSprings(disp, vel, 1f / 60f);
            }
            Assert.AreEqual(0f, disp[disp.Length / 2], 0.02f, "Net should settle back to rest after damping.");
        }

        [Test]
        public void BallHeight_VerticalSolver_LoftsBouncesAndSettles()
        {
            float height = 0f;
            float vz = Agent_BallHeight.ImpulseToLoft(12f, isWallKick: true);
            Assert.Greater(vz, 3.5f);

            float peak = 0f;
            for (int i = 0; i < 300; i++)
            {
                Agent_BallHeight.StepBallVertical(ref height, ref vz, 1f / 60f);
                if (height > peak) peak = height;
            }
            Assert.Greater(peak, 0.6f, "Lofted ball should arc above header height.");
            Assert.AreEqual(0f, height, 0.01f, "Ball should settle back onto the turf.");
        }

        [Test]
        public void Weather_MultipliersAndOpenMeteoParser_MapCorrectly()
        {
            Assert.AreEqual(1.00f, Agent_Weather.TractionMultiplier(Agent_Weather.Condition.Dry), 1e-4f);
            Assert.Less(Agent_Weather.TractionMultiplier(Agent_Weather.Condition.Wet), 1.0f);
            Assert.Less(Agent_Weather.BallDampingMultiplier(Agent_Weather.Condition.Wet), 1.0f);
            Assert.Greater(Agent_Weather.BallDampingMultiplier(Agent_Weather.Condition.Muddy), 1.0f);

            Assert.AreEqual(Agent_Weather.Condition.Snowy, Agent_Weather.ParseOpenMeteoCode(73, -1f));
            Assert.AreEqual(Agent_Weather.Condition.Muddy, Agent_Weather.ParseOpenMeteoCode(95, 18f));
            Assert.AreEqual(Agent_Weather.Condition.Wet, Agent_Weather.ParseOpenMeteoCode(61, 14f));
            Assert.AreEqual(Agent_Weather.Condition.Dry, Agent_Weather.ParseOpenMeteoCode(1, 20f));

            const string sampleJson = "{\"current_weather\":{\"temperature\":11.5,\"weathercode\":61}}";
            Assert.IsTrue(Agent_Weather.TryParseOpenMeteoJson(sampleJson, out int code, out float temp));
            Assert.AreEqual(61, code);
            Assert.AreEqual(11.5f, temp, 0.01f);
        }

        [Test]
        public void VoiceSynth_RenderCommentaryAndWavEncoder_ProduceValidData()
        {
            float[] samples = Agent_VoiceSynth.RenderCommentary("WHAT A STRIKE!", 0.95f);
            Assert.IsNotNull(samples);
            Assert.Greater(samples.Length, Agent_VoiceSynth.SAMPLE_RATE / 4);

            byte[] wav = Agent_VoiceSynth.EncodeWav16(samples);
            Assert.AreEqual(44 + samples.Length * 2, wav.Length);
            Assert.AreEqual((byte)'R', wav[0]);
            Assert.AreEqual((byte)'I', wav[1]);
            Assert.AreEqual((byte)'F', wav[2]);
            Assert.AreEqual((byte)'F', wav[3]);
            Assert.AreEqual((byte)'W', wav[8]);
            Assert.AreEqual((byte)'A', wav[9]);
            Assert.AreEqual((byte)'V', wav[10]);
            Assert.AreEqual((byte)'E', wav[11]);
        }

        [Test]
        public void Sidelines_LinesmanAndCoach_StayInAssignedZones()
        {
            float northY = 1f, velN = 0f;
            float southY = -1f, velS = 0f;
            for (int i = 0; i < 60; i++)
            {
                northY = Agent_Sidelines.ComputeLinesmanY(northY, ref velN, -5f, 8f, isNorthHalf: true, 1f / 60f);
                southY = Agent_Sidelines.ComputeLinesmanY(southY, ref velS, 5f, 8f, isNorthHalf: false, 1f / 60f);
            }
            Assert.GreaterOrEqual(northY, -0.01f, "North linesman must not cross into South half.");
            Assert.LessOrEqual(southY, 0.01f, "South linesman must not cross into North half.");

            Vector2 blueCoach = Agent_Sidelines.ComputeCoachPosition(Vector2.zero, new Vector2(4.5f, 9f), isBlueTeam: true, 1.5f, 0.5f);
            Vector2 redCoach = Agent_Sidelines.ComputeCoachPosition(Vector2.zero, new Vector2(4.5f, 9f), isBlueTeam: false, 1.5f, 0.5f);
            Assert.Less(blueCoach.x, -4.5f, "Coach must remain outside the West touchline.");
            Assert.Less(blueCoach.y, 0f, "Blue coach stays in Blue's half.");
            Assert.Greater(redCoach.y, 0f, "Red coach stays in Red's half.");
        }

        [Test]
        public void HumanController_ScreenRelativeSteering_AlignsDriveAndTurn()
        {
            // Idle input -> zero actions.
            Vector4 idle = Agent_HumanController.ComputeSteerActions(
                Vector2.zero, Vector2.up, Vector2.right, 0f, boostWanted: false);
            Assert.AreEqual(Vector4.zero, idle);

            // Pressing Up (+Y) while facing Up (+Y): full forward drive, zero turn, boost engaged.
            Vector4 upWhileUp = Agent_HumanController.ComputeSteerActions(
                Vector2.up, Vector2.up, Vector2.right, 0f, boostWanted: true);
            Assert.Greater(upWhileUp.x, 0.95f, "Forward channel should be full when aligned with screen input.");
            Assert.AreEqual(0f, upWhileUp.y, 0.01f);
            Assert.AreEqual(0f, upWhileUp.z, 0.01f);
            Assert.AreEqual(1f, upWhileUp.w, 0.01f);

            // Pressing Left (-X) while facing Up (+Y): left strafe (-lateral) and positive CCW turn.
            Vector4 leftWhileUp = Agent_HumanController.ComputeSteerActions(
                Vector2.left, Vector2.up, Vector2.right, 0f, boostWanted: false);
            Assert.Less(leftWhileUp.y, -0.95f, "Lateral channel should strafe left immediately.");
            Assert.Greater(leftWhileUp.z, 0.5f, "Turn channel should steer counter-clockwise toward +X=left.");
        }

        [Test]
        public void HumanController_KickImpulse_StrikesInAimedDirectionWithinRadius()
        {
            Vector2 playerPos = Vector2.zero;
            Vector2 forward = Vector2.up;

            // Ball outside kick radius -> zero impulse.
            Vector2 farImpulse = Agent_HumanController.ComputeKickImpulse(
                playerPos, forward, Vector2.up, new Vector2(0f, 3.0f));
            Assert.AreEqual(Vector2.zero, farImpulse);

            // Ball within radius, aimed diagonally up-right -> positive X and Y impulse.
            Vector2 nearImpulse = Agent_HumanController.ComputeKickImpulse(
                playerPos, forward, new Vector2(1f, 1f), new Vector2(0f, 0.9f));
            Assert.Greater(nearImpulse.magnitude, 2.0f);
            Assert.Greater(nearImpulse.x, 0.5f, "Kick should angle toward held movement direction.");
            Assert.Greater(nearImpulse.y, 0.5f, "Kick should drive the ball forward.");
        }

        [Test]
        public void HumanController_ProfileAndMatchSetup_LocksToBlueSlotZero()
        {
            var profile = Agent_HumanController.GetOrCreateProfile();
            Assert.IsNotNull(profile);
            Assert.AreEqual(Agent_HumanController.HUMAN_PLAYER_NAME, profile.playerName);
            Assert.IsNull(profile.faceTexture, "Generic Blue box must have no face photo.");
            Assert.AreEqual(1f, profile.bodyScale, 0.001f);
            Assert.AreEqual(75f, profile.bodyMass, 0.001f);

            try
            {
                Agent_MatchSetup.Clear();
                Assert.IsFalse(Agent_MatchSetup.IsHumanSlot(Agent_Soccer.Team.Blue, 0));

                Agent_MatchSetup.HumanControl = true;
                Assert.IsTrue(Agent_MatchSetup.IsHumanSlot(Agent_Soccer.Team.Blue, 0));
                Assert.IsFalse(Agent_MatchSetup.IsHumanSlot(Agent_Soccer.Team.Blue, 1),
                    "4v4 teammates in slots 1..3 must remain AI-controlled.");
                Assert.IsFalse(Agent_MatchSetup.IsHumanSlot(Agent_Soccer.Team.Red, 0),
                    "Opponents must remain AI-controlled.");

                // Human "YOU" matches must never pollute the AI Elo leaderboard.
                Agent_Elo.ResetAll();
                float delta = Agent_Elo.Record(Agent_HumanController.HUMAN_PLAYER_NAME, "KIM", 1f);
                Assert.AreEqual(0f, delta, 1e-5f, "Human 1P games must not mutate AI Elo ratings.");
                Assert.AreEqual(0, Agent_Elo.Table().Count, "YOU must not appear in the AI Elo table.");
            }
            finally
            {
                Agent_MatchSetup.Clear();
            }
        }
    }
}
