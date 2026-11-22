using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Draws each player's face photo (Reward_Settings.faceTexture) on top of its
    /// square body, and brings it to life with dynamic gaze tracking and micro-expressions.
    ///
    /// GAZE TRACKING. The photo is stored upright; on the pitch its neutral angle is
    /// 180 degrees so the CHIN points along transform.up. In play, the head turns up
    /// to +/-MAX_GAZE_DEG toward the ball (or the player's active role post when
    /// rotating off the ball), smoothed by a critically damped spring so players
    /// visibly scan the field rather than staring straight ahead like statues.
    ///
    /// MICRO-EXPRESSIONS.
    ///  - Contact flinch: a heavy collision (Agent_Contact.PlayerContact) squashes
    ///    the face briefly and flashes a warm impact flush.
    ///  - Fatigue pant: when stamina falls below 30%, subtle rhythmic breathing
    ///    oscillates the face scale.
    ///  - Goal celebration: the scoring side gets an elastic bounce pulse.
    ///
    /// Per-pitch visual: installed by Agent_Presentation.InstallVisuals and gated on
    /// IsVisualScene, so training and evaluation never build or tick it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_Faces : MonoBehaviour
    {
        /// <summary>Share of the body's side the face covers.</summary>
        const float FACE_FRACTION = 0.84f;
        const string CHILD_NAME = "Face";

        /// <summary>Maximum head yaw (degrees) away from the body's forward axis.</summary>
        public const float MAX_GAZE_DEG = 22f;

        static readonly Dictionary<Texture2D, Sprite> Sprites = new();

        sealed class FaceRig
        {
            public Agent_Soccer Agent;
            public Transform FaceTransform;
            public SpriteRenderer Renderer;
            public Agent_Contact Contact;
            public System.Action<Vector2, Vector2, float> ContactHandler;
            public float BaseScale;
            public Agent_Tween.SpringFloat GazeSpring;
            public float FlinchTimer;
            public float CelebrationTimer;
        }

        readonly List<FaceRig> _rigs = new();
        Agent_EnvController _env;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            if (_env == null) _env = FindAnyObjectByType<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (_env == null || !Agent_Presentation.IsVisualScene(hud) || Agent_Presentation.IsHeadless)
            {
                enabled = false;
                return;
            }

            var agents = _env.GetComponentsInChildren<Agent_Soccer>(true);
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                var agent = agents[agentIndex];
                Attach(agent);
                TryBindRig(agent);
            }

            if (_rigs.Count == 0)
            {
                enabled = false;
                return;
            }

            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            for (int i = 0; i < _rigs.Count; i++)
            {
                var rig = _rigs[i];
                if (rig.Contact != null && rig.ContactHandler != null)
                {
                    rig.Contact.PlayerContact -= rig.ContactHandler;
                }
            }
            _rigs.Clear();
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;
            for (int i = 0; i < _rigs.Count; i++)
            {
                var rig = _rigs[i];
                if (rig.Agent != null && rig.Agent.team == winner.Value)
                {
                    rig.CelebrationTimer = 1.4f;
                }
            }
        }

        void TryBindRig(Agent_Soccer agent)
        {
            if (agent == null) return;
            var face = agent.transform.Find(CHILD_NAME);
            if (face == null || !face.gameObject.activeSelf) return;
            var renderer = face.GetComponent<SpriteRenderer>();
            if (renderer == null || renderer.sprite == null) return;

            var rig = new FaceRig
            {
                Agent = agent,
                FaceTransform = face,
                Renderer = renderer,
                BaseScale = face.localScale.x,
                GazeSpring = new Agent_Tween.SpringFloat(0f),
            };

            var contact = agent.GetComponent<Agent_Contact>();
            if (contact != null)
            {
                rig.Contact = contact;
                rig.ContactHandler = (_, _, impulse) =>
                {
                    float strength = Agent_ImpactScale.Player01(impulse);
                    if (strength > 0.2f)
                    {
                        rig.FlinchTimer = Mathf.Max(rig.FlinchTimer, Mathf.Lerp(0.15f, 0.38f, strength));
                        rig.GazeSpring.Impulse((Random.value > 0.5f ? 1f : -1f) * strength * 180f);
                    }
                };
                contact.PlayerContact += rig.ContactHandler;
            }

            _rigs.Add(rig);
        }

        void LateUpdate()
        {
            if (_env == null) return;
            float dt = Time.deltaTime;
            float unscaledDt = Time.unscaledDeltaTime;
            float now = Time.time;
            bool haveBall = _env.Ball != null;
            Vector2 ballPos = haveBall ? _env.Ball.position : Vector2.zero;

            for (int i = 0; i < _rigs.Count; i++)
            {
                var rig = _rigs[i];
                if (rig.Agent == null || rig.FaceTransform == null) continue;

                Vector2 agentPos = rig.Agent.transform.position;
                Vector2 forward = rig.Agent.transform.up;
                Vector2 right = rig.Agent.transform.right;

                // Look at the ball when nearby or on ball duty; blend toward role target when off-ball.
                Vector2 lookTarget = ballPos;
                if (haveBall && !_env.HasBallDuty(rig.Agent))
                {
                    Vector2 roleTarget = _env.RoleTarget(rig.Agent);
                    lookTarget = Vector2.Lerp(ballPos, roleTarget, 0.35f);
                }

                float gaze = 0f;
                if (Agent_TimeFreeze.IsFrozen)
                {
                    rig.GazeSpring.Value = 0f;
                    rig.GazeSpring.Velocity = 0f;
                }
                else
                {
                    float targetGaze = haveBall
                        ? ComputeGazeAngleDeg(agentPos, forward, right, lookTarget, MAX_GAZE_DEG)
                        : 0f;
                    gaze = rig.GazeSpring.Step(targetGaze, dt, angularFrequency: 20f, dampingRatio: 0.75f);
                }
                rig.FaceTransform.localRotation = Quaternion.Euler(0f, 0f, 180f + gaze);

                if (rig.FlinchTimer > 0f) rig.FlinchTimer = Mathf.Max(0f, rig.FlinchTimer - dt);
                if (rig.CelebrationTimer > 0f) rig.CelebrationTimer = Mathf.Max(0f, rig.CelebrationTimer - unscaledDt);

                float flinch01 = Mathf.Clamp01(rig.FlinchTimer / 0.35f);
                float staminaRatio = rig.Agent.Stamina != null ? rig.Agent.Stamina.Ratio : 1f;
                float fatigue01 = Mathf.Clamp01((0.35f - staminaRatio) / 0.35f);
                float celeb01 = Mathf.Clamp01(rig.CelebrationTimer / 1.4f);

                Vector2 scale = ComputeExpressionScale(rig.BaseScale, flinch01, fatigue01, celeb01, now + i * 0.7f);
                rig.FaceTransform.localScale = new Vector3(scale.x, scale.y, 1f);

                // Subtle impact/fatigue flush on the face sprite.
                if (rig.Renderer != null)
                {
                    float flush = Mathf.Max(flinch01 * 0.45f, fatigue01 * 0.22f);
                    rig.Renderer.color = flush > 0.005f
                        ? Color.Lerp(Color.white, new Color(1f, 0.72f, 0.68f, 1f), flush)
                        : Color.white;
                }
            }
        }

        /// <summary>
        /// Pure gaze solver: returns the signed head yaw in [-maxAngleDeg, +maxAngleDeg]
        /// around the body's forward axis toward <paramref name="targetPos"/>.
        /// </summary>
        public static float ComputeGazeAngleDeg(Vector2 agentPos, Vector2 forwardAxis, Vector2 rightAxis,
            Vector2 targetPos, float maxAngleDeg = MAX_GAZE_DEG)
        {
            Vector2 toTarget = targetPos - agentPos;
            if (toTarget.sqrMagnitude < 0.0004f) return 0f;
            Vector2 dir = toTarget.normalized;
            float forwardDot = Vector2.Dot(dir, forwardAxis);
            float rightDot = Vector2.Dot(dir, rightAxis);
            // In Unity 2D, positive Z Euler rotation turns counter-clockwise (left, -rightAxis).
            float signedAngleDeg = Mathf.Atan2(-rightDot, forwardDot) * Mathf.Rad2Deg;
            return Mathf.Clamp(signedAngleDeg * 0.55f, -maxAngleDeg, maxAngleDeg);
        }

        /// <summary>
        /// Pure micro-expression scale solver combining contact flinch squash,
        /// low-stamina breathing heave, and goal celebration bounce.
        /// </summary>
        public static Vector2 ComputeExpressionScale(float baseScale, float flinch01,
            float fatigue01, float celebration01, float timeSeconds)
        {
            float sx = baseScale;
            float sy = baseScale;

            // Contact flinch: wide lateral squash, compressed fore-aft.
            if (flinch01 > 0f)
            {
                float squash = Mathf.Sin(flinch01 * Mathf.PI) * 0.16f;
                sx *= 1f + squash;
                sy *= 1f - squash * 0.85f;
            }

            // Exhaustion pant: rhythmic breathing heave.
            if (fatigue01 > 0f)
            {
                float breath = Mathf.Sin(timeSeconds * 9.5f) * 0.045f * fatigue01;
                sx *= 1f + breath;
                sy *= 1f - breath * 0.5f;
            }

            // Goal celebration: elastic joy pulse.
            if (celebration01 > 0f)
            {
                float bounce = Agent_Tween.Punch(1f - celebration01) * 0.14f;
                sx *= 1f + bounce;
                sy *= 1f + bounce;
            }

            return new Vector2(sx, sy);
        }

        /// <summary>Adds (or refreshes) the face child on one player. Safe to call twice.</summary>
        public static void Attach(Agent_Soccer agent)
        {
            if (agent == null) return;
            var face = agent.rewards != null ? agent.rewards.faceTexture : null;
            var existing = agent.transform.Find(CHILD_NAME);

            if (face == null)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }
            if (!agent.TryGetComponent(out SpriteRenderer body) || body.sprite == null) return;

            SpriteRenderer renderer;
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                renderer = existing.GetComponent<SpriteRenderer>();
            }
            else
            {
                var child = new GameObject(CHILD_NAME);
                child.transform.SetParent(agent.transform, false);
                renderer = child.AddComponent<SpriteRenderer>();
            }

            // Upright photo -> chin toward local +Y (forward) is a half turn.
            renderer.transform.localPosition = new Vector3(0f, 0f, -0.001f);
            renderer.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);

            renderer.sprite = SpriteFor(face);
            renderer.sortingLayerID = body.sortingLayerID;
            renderer.sortingOrder = body.sortingOrder + 1;

            // Fit the body's LOCAL square, so bodyScale (applied to the parent)
            // scales the face with the body.
            Vector2 bodySize = body.sprite.bounds.size;
            float side = Mathf.Min(bodySize.x, bodySize.y) * FACE_FRACTION;
            float spriteSide = Mathf.Max(0.0001f, renderer.sprite.bounds.size.x);
            float scale = side / spriteSide;
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
        }

        /// <summary>One sprite per face texture, 1 world unit across before scaling.</summary>
        static Sprite SpriteFor(Texture2D texture)
        {
            if (Sprites.TryGetValue(texture, out var cached) && cached != null) return cached;
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), texture.width, 0, SpriteMeshType.FullRect);
            sprite.name = "face_" + texture.name;
            Sprites[texture] = sprite;
            return sprite;
        }
    }
}
