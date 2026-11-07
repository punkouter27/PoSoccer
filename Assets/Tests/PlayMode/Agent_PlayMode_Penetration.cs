using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoSoccer.Tests
{
    /// <summary>
    /// MEASURES how deep bodies sink into each other on a hard contact, in metres,
    /// for the three pairs the game is made of: player on player, player and ball
    /// on the boards, ball on the post (the wall end beside a goal mouth - there is
    /// no separate post collider).
    ///
    /// Written 2026-11-07 without a running editor (no licence on the machine), so
    /// the tolerance is a first bar, not a measured one: Box2D resolves a resting
    /// contact to within about two contact offsets (Physics2D.defaultContactOffset,
    /// 0.01 m by default), and every body here uses Continuous collision detection,
    /// so a fast body should never tunnel. The measured worst case is logged on
    /// every run; tighten PENETRATION_TOLERANCE to it once a run exists.
    ///
    /// Method: each scenario switches every body it does not involve out of the
    /// simulation (Rigidbody2D.simulated = false, restored afterwards), switches
    /// the agents' own control off so no policy or bot steers into the measurement,
    /// launches the pair at a realistic worst-case speed, and samples
    /// Physics2D.Distance after every physics step. A negative distance is overlap.
    /// </summary>
    public class Agent_PlayMode_Penetration
    {
        /// <summary>Metres of overlap a hard contact may show. See the class docstring.</summary>
        const float PENETRATION_TOLERANCE = 0.03f;

        /// <summary>Fastest sprint the shipped bodies reach, m/s, closing from both sides.</summary>
        const float SPRINT_SPEED = 9.5f;

        /// <summary>A hard shot, m/s.</summary>
        const float SHOT_SPEED = 30f;

        const int STEPS = 120;   // 1.2 s at the 0.01 s fixed step

        readonly List<Rigidbody2D> _parked = new();
        readonly List<Behaviour> _paused = new();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Restore();
            Agent_TimeFreeze.ReleaseAll();
            yield return null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        IEnumerator Stage(System.Action<Agent_EnvController> done)
        {
            SceneManager.LoadScene("SCN_Training");
            yield return Frames(3);
            var env = Object.FindAnyObjectByType<Agent_EnvController>();
            Assert.IsNotNull(env, "SCN_Training has no Agent_EnvController");
            Assert.IsNotNull(env.Ball, "SCN_Training has no ball");

            // Nothing may reset the pitch or steer a body while we measure.
            Pause(env);
            foreach (var agent in env.GetComponentsInChildren<Agent_Soccer>())
            {
                foreach (var behaviour in agent.GetComponents<Behaviour>())
                {
                    if (behaviour is Collider2D) continue;
                    Pause(behaviour);
                }
            }
            done(env);
        }

        void Pause(Behaviour behaviour)
        {
            if (behaviour == null || !behaviour.enabled) return;
            behaviour.enabled = false;
            _paused.Add(behaviour);
        }

        void ParkAllExcept(Agent_EnvController env, params Rigidbody2D[] keep)
        {
            var bodies = env.GetComponentsInChildren<Rigidbody2D>();
            foreach (var body in bodies)
            {
                if (System.Array.IndexOf(keep, body) >= 0) continue;
                if (!body.simulated) continue;
                body.simulated = false;
                _parked.Add(body);
            }
        }

        void Restore()
        {
            foreach (var body in _parked) if (body != null) body.simulated = true;
            _parked.Clear();
            foreach (var behaviour in _paused) if (behaviour != null) behaviour.enabled = true;
            _paused.Clear();
        }

        static void Launch(Rigidbody2D body, Vector2 position, Vector2 velocity)
        {
            body.position = position;
            body.rotation = 0f;
            body.linearVelocity = velocity;
            body.angularVelocity = 0f;
        }

        /// <summary>Deepest overlap, in metres (0 when they never overlapped), over the run.</summary>
        static IEnumerator Measure(Collider2D a, Collider2D b, float[] worst)
        {
            Physics2D.SyncTransforms();
            worst[0] = 0f;
            for (int step = 0; step < STEPS; step++)
            {
                yield return new WaitForFixedUpdate();
                if (!a.enabled || !b.enabled) continue;
                ColliderDistance2D distance = Physics2D.Distance(a, b);
                if (distance.isValid && distance.distance < 0f)
                {
                    worst[0] = Mathf.Max(worst[0], -distance.distance);
                }
            }
        }

        static Collider2D SolidCollider(Component owner)
        {
            foreach (var collider in owner.GetComponentsInChildren<Collider2D>())
            {
                if (!collider.isTrigger) return collider;
            }
            return null;
        }

        static void Report(string label, float worst)
        {
            Debug.Log($"[Penetration] {label}: deepest overlap {worst * 1000f:0.0} mm " +
                      $"(tolerance {PENETRATION_TOLERANCE * 1000f:0} mm)");
            Assert.LessOrEqual(worst, PENETRATION_TOLERANCE,
                $"{label}: bodies overlapped by {worst:0.000} m");
        }

        [UnityTest]
        public IEnumerator PlayerOnPlayer_HeadOnSprint_DoesNotSinkIn()
        {
            Agent_EnvController env = null;
            yield return Stage(found => env = found);

            Agent_Soccer blue = null, red = null;
            foreach (var agent in env.GetComponentsInChildren<Agent_Soccer>())
            {
                if (agent.team == Agent_Soccer.Team.Blue && blue == null) blue = agent;
                if (agent.team == Agent_Soccer.Team.Red && red == null) red = agent;
            }
            Assert.IsNotNull(blue, "no blue agent");
            Assert.IsNotNull(red, "no red agent");

            var blueBody = blue.GetComponent<Rigidbody2D>();
            var redBody = red.GetComponent<Rigidbody2D>();
            ParkAllExcept(env, blueBody, redBody);

            Vector2 centre = env.transform.position;
            Launch(blueBody, centre + new Vector2(-1.5f, 0f), new Vector2(SPRINT_SPEED, 0f));
            Launch(redBody, centre + new Vector2(1.5f, 0f), new Vector2(-SPRINT_SPEED, 0f));

            var worst = new float[1];
            yield return Measure(SolidCollider(blue), SolidCollider(red), worst);
            Report("player-player head-on", worst[0]);
        }

        [UnityTest]
        public IEnumerator PlayerIntoBoards_AtSprint_DoesNotSinkIn()
        {
            Agent_EnvController env = null;
            yield return Stage(found => env = found);

            var agent = env.GetComponentInChildren<Agent_Soccer>();
            var body = agent.GetComponent<Rigidbody2D>();
            ParkAllExcept(env, body);

            Vector2 centre = env.transform.position;
            float halfX = env.PitchHalfExtents.x;
            Launch(body, centre + new Vector2(halfX - 2f, 0f), new Vector2(SPRINT_SPEED, 0f));

            Collider2D wall = NearestSolidPitchCollider(env, centre + new Vector2(halfX, 0f));
            Assert.IsNotNull(wall, "no side wall found");

            var worst = new float[1];
            yield return Measure(SolidCollider(agent), wall, worst);
            Report("player-boards", worst[0]);
        }

        [UnityTest]
        public IEnumerator BallIntoBoards_AtShotSpeed_DoesNotTunnelOrSinkIn()
        {
            Agent_EnvController env = null;
            yield return Stage(found => env = found);

            var ball = env.Ball;
            ParkAllExcept(env, ball);

            Vector2 centre = env.transform.position;
            float halfX = env.PitchHalfExtents.x;
            Launch(ball, centre, new Vector2(SHOT_SPEED, 0f));

            Collider2D wall = NearestSolidPitchCollider(env, centre + new Vector2(halfX, 0f));
            Assert.IsNotNull(wall, "no side wall found");

            var worst = new float[1];
            yield return Measure(SolidCollider(ball), wall, worst);
            Report("ball-boards", worst[0]);

            // Tunnelling shows up as a ball OUTSIDE the pitch, not as overlap.
            Vector2 local = ball.position - centre;
            Assert.LessOrEqual(Mathf.Abs(local.x), halfX + 0.5f,
                $"ball tunnelled through the boards: ended at x={local.x:0.00} m, wall at {halfX:0.00} m");
        }

        [UnityTest]
        public IEnumerator BallOntoPost_AtShotSpeed_DoesNotSinkIn()
        {
            Agent_EnvController env = null;
            yield return Stage(found => env = found);

            var ball = env.Ball;
            ParkAllExcept(env, ball);

            Assert.IsNotNull(env.redGoal, "no red goal");
            Vector2 goal = env.redGoal.position;
            Vector2 centre = env.transform.position;
            Vector2 towardGoal = (goal - centre).normalized;
            Vector2 goalLine = new(-towardGoal.y, towardGoal.x);

            // Just outside the mouth: the end of the wall that frames the goal, i.e.
            // the post. Aim at it from the field at 30 degrees off the goal axis.
            float halfMouth = Mathf.Max(0.1f, env.CurrentGoalWidth * 0.5f);
            Vector2 post = goal + goalLine * (halfMouth + 0.15f);
            Collider2D postCollider = NearestSolidPitchCollider(env, post);
            Assert.IsNotNull(postCollider, "no collider beside the goal mouth");

            Vector2 approach = (towardGoal * Mathf.Cos(30f * Mathf.Deg2Rad)
                                + goalLine * Mathf.Sin(30f * Mathf.Deg2Rad)).normalized;
            Launch(ball, post - approach * 3f, approach * SHOT_SPEED);

            var worst = new float[1];
            yield return Measure(SolidCollider(ball), postCollider, worst);
            Report("ball-post", worst[0]);
        }

        /// <summary>
        /// The solid (non-trigger) pitch collider closest to a point: walls, boards
        /// and the goal frame. Excludes the ball and the agents.
        /// </summary>
        static Collider2D NearestSolidPitchCollider(Agent_EnvController env, Vector2 point)
        {
            Collider2D best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (var collider in env.GetComponentsInChildren<Collider2D>())
            {
                if (collider.isTrigger) continue;
                if (env.Ball != null && collider.transform.IsChildOf(env.Ball.transform)) continue;
                if (collider.GetComponentInParent<Agent_Soccer>() != null) continue;

                float distance = (collider.ClosestPoint(point) - point).magnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = collider;
                }
            }
            return best;
        }
    }
}
