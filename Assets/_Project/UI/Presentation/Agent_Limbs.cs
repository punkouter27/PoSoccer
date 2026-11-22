using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Gives every player a pair of legs that stride with the locomotion model,
    /// wind up and follow through on kicks, lunge into tackles, and (for the
    /// assigned Goalie in 3v3+) wear goalkeeper gloves that reach and dive
    /// laterally toward incoming shots.
    ///
    /// NOTHING HERE INVENTS ITS OWN MOTION.
    ///  - Stride phase is integrated from the body's FORWARD speed (velocity dot
    ///    transform.up).
    ///  - Kick wind-up reads the closing speed and proximity to the ball; follow-
    ///    through fires on actual BallTouched / WallKicked events.
    ///  - Goalkeeper gloves activate only when Agent_EnvController.RoleOf reports
    ///    Agent_TeamRoles.Role.Goalie, and reach/dive from real lateral slip and
    ///    shot velocity.
    ///
    /// One component on the pitch root, one shared material and one shared
    /// Agent_Art atlas page, so every leg and glove stays in the same batch.
    /// Presentation only. Self-disables in training and evaluation.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    [DefaultExecutionOrder(55)]
    public sealed class Agent_Limbs : MonoBehaviour
    {
        [Tooltip("Leg length as a fraction of body width.")]
        [Range(0.1f, 1.5f)] [SerializeField] private float _legLength = 0.62f;
        [Tooltip("Leg thickness as a fraction of body width.")]
        [Range(0.05f, 0.5f)] [SerializeField] private float _legWidth = 0.22f;
        [Tooltip("Sideways separation of the two legs, as a fraction of body width. " +
                 "Must be large enough that the legs clear the torso silhouette.")]
        [Range(0f, 1.5f)] [SerializeField] private float _hipWidth = 0.95f;
        [Tooltip("How far a leg swings fore and aft at full stride, as a fraction of body width.")]
        [Range(0f, 1f)] [SerializeField] private float _stride = 0.3f;
        [Tooltip("Forward speed (m/s) at which the stride reaches full amplitude.")]
        [SerializeField] private float _fullStrideSpeed = 6f;
        [Tooltip("Stride cycles per second per m/s of forward speed.")]
        [SerializeField] private float _cadence = 0.42f;
        [Tooltip("Extra stride amplitude while boosting.")]
        [Range(0f, 1f)] [SerializeField] private float _boostStretch = 0.35f;
        [Tooltip("How much darker the legs are than the personality colour.")]
        [Range(0f, 1f)] [SerializeField] private float _shade = 0.45f;
        [SerializeField] private bool _enableLimbs = true;

        /// <summary>Duration (seconds) of the post-contact kick follow-through swing.</summary>
        public const float KICK_FOLLOW_DURATION = 0.24f;
        /// <summary>Distance (metres) inside which an approaching player winds up its kicking leg.</summary>
        public const float KICK_WINDUP_RADIUS = 1.25f;

        sealed class Rig
        {
            public Agent_Soccer Agent;
            public Rigidbody2D Body;
            public Transform Left;
            public Transform Right;
            public Transform GloveLeft;
            public Transform GloveRight;
            public SpriteRenderer GloveLeftRenderer;
            public SpriteRenderer GloveRightRenderer;
            public float Phase;
            public float BodyWidth;
            public float KickTimer;
            public float DiveTimer;
            public float DiveSign;
            public Agent_Tween.SpringFloat BraceSpring;
            public System.Action<Vector2, Vector2> WallKickHandler;
        }

        readonly List<Rig> _rigs = new();
        Agent_EnvController _env;
        Material _material;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (!_enableLimbs || !Agent_Presentation.IsVisualScene(hud) || Agent_Presentation.IsHeadless)
            {
                enabled = false;
                return;
            }

            // Lit, unlike the shadows: a limb is part of the body and should sit
            // under the same floodlights the torso does, or it reads as a decal.
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default")
                ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                enabled = false;
                return;
            }
            _material = new Material(shader) { name = "PoSoccer_Limbs" };

            var agents = GetComponentsInChildren<Agent_Soccer>();
            for (int i = 0; i < agents.Length; i++) Build(agents[i]);
            if (_rigs.Count == 0)
            {
                enabled = false;
                return;
            }

            if (_env != null) _env.BallTouched += OnBallTouched;
        }

        void OnDestroy()
        {
            if (_env != null) _env.BallTouched -= OnBallTouched;
            for (int i = 0; i < _rigs.Count; i++)
            {
                var rig = _rigs[i];
                if (rig.Agent != null && rig.WallKickHandler != null)
                {
                    rig.Agent.WallKicked -= rig.WallKickHandler;
                }
            }
            if (_material != null) Destroy(_material);
        }

        void OnBallTouched(Agent_Soccer toucher)
        {
            if (toucher == null) return;
            for (int i = 0; i < _rigs.Count; i++)
            {
                if (_rigs[i].Agent == toucher)
                {
                    _rigs[i].KickTimer = KICK_FOLLOW_DURATION;
                    _rigs[i].BraceSpring.Impulse(6f);
                    break;
                }
            }
        }

        void Build(Agent_Soccer agent)
        {
            if (agent == null) return;
            if (!agent.TryGetComponent(out SpriteRenderer body) || body.sprite == null) return;
            if (agent.transform.Find("Leg_L") != null) return;

            float width = body.sprite.bounds.size.x;

            Color tint = agent.rewards != null ? agent.rewards.playerColor : Color.white;
            tint = Color.Lerp(tint, Color.black, _shade);
            tint.a = 1f;

            var rig = new Rig
            {
                Agent = agent,
                Body = agent.GetComponent<Rigidbody2D>(),
                BodyWidth = width,
                Phase = Random.Range(0f, Mathf.PI * 2f),
            };
            rig.Left = BuildLeg(agent.transform, body, "Leg_L", tint, width);
            rig.Right = BuildLeg(agent.transform, body, "Leg_R", tint, width);

            // Goalkeeper gloves: bright team-tinted pads on the front corners, active
            // only while the player holds the Goalie role.
            Color gloveTint = Color.Lerp(Agent_SoccerView.TeamColor(agent.team), Color.white, 0.35f);
            gloveTint.a = 1f;
            rig.GloveLeft = BuildGlove(agent.transform, body, "Glove_L", gloveTint, width, out rig.GloveLeftRenderer);
            rig.GloveRight = BuildGlove(agent.transform, body, "Glove_R", gloveTint, width, out rig.GloveRightRenderer);

            rig.WallKickHandler = (_, _) => rig.KickTimer = KICK_FOLLOW_DURATION;
            agent.WallKicked += rig.WallKickHandler;

            _rigs.Add(rig);
        }

        Transform BuildLeg(Transform parent, SpriteRenderer body, string label, Color tint, float width)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(width * _legWidth, width * _legLength, 1f);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Agent_Art.Cleat(1f);
            renderer.sharedMaterial = _material;
            renderer.color = tint;
            renderer.sortingLayerName = body.sortingLayerName;
            renderer.sortingOrder = body.sortingOrder + 1;
            return go.transform;
        }

        Transform BuildGlove(Transform parent, SpriteRenderer body, string label, Color tint, float width,
            out SpriteRenderer renderer)
        {
            var go = new GameObject(label);
            go.transform.SetParent(parent, false);
            float pad = width * 0.28f;
            go.transform.localScale = new Vector3(pad, pad * 1.15f, 1f);

            renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Agent_Art.Glove(1f);
            renderer.sharedMaterial = _material;
            renderer.color = tint;
            renderer.sortingLayerName = body.sortingLayerName;
            renderer.sortingOrder = body.sortingOrder + 2;
            renderer.enabled = false;
            return go.transform;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            bool haveBall = _env != null && _env.Ball != null;
            Vector2 ballPos = haveBall ? _env.Ball.position : Vector2.zero;
            Vector2 ballVel = haveBall ? _env.Ball.linearVelocity : Vector2.zero;

            for (int i = 0; i < _rigs.Count; i++)
            {
                var rig = _rigs[i];
                if (rig.Agent == null || rig.Left == null || rig.Right == null) continue;

                Vector2 vel = rig.Body != null ? rig.Body.linearVelocity : Vector2.zero;
                Vector2 up = rig.Agent.transform.up;
                Vector2 right = rig.Agent.transform.right;
                float forwardSpeed = Vector2.Dot(vel, up);

                rig.Phase += forwardSpeed * _cadence * Mathf.PI * 2f * dt;

                float effort = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(0.01f, _fullStrideSpeed));
                float amplitude = rig.BodyWidth * _stride * effort;
                if (rig.Agent.IsBoosting) amplitude *= 1f + _boostStretch;

                float swing = Mathf.Sin(rig.Phase) * amplitude;
                float hip = rig.BodyWidth * _hipWidth * 0.5f;

                if (rig.KickTimer > 0f) rig.KickTimer = Mathf.Max(0f, rig.KickTimer - dt);

                // Kick wind-up & follow-through + tackle lunge on the striking (right) leg.
                float kickOffset = 0f;
                float kickScaleY = 1f;
                if (haveBall)
                {
                    Vector2 toBall = ballPos - (Vector2)rig.Agent.transform.position;
                    float dist = toBall.magnitude;
                    float closingSpeed = dist > 0.01f ? Vector2.Dot(vel - ballVel, toBall / dist) : 0f;
                    kickOffset = ComputeKickSwingOffset(
                        rig.BodyWidth, dist, closingSpeed, rig.KickTimer, rig.Agent.IsBoosting, out kickScaleY);
                }

                float brace = Mathf.Clamp(_rigBrace(rig, dt), -0.18f, 0.25f);
                rig.Left.localPosition = new Vector3(-hip, swing - kickOffset * 0.25f, 0f);
                rig.Left.localScale = new Vector3(
                    rig.BodyWidth * _legWidth * (1f + brace * 0.35f),
                    rig.BodyWidth * _legLength * (1f - brace * 0.25f),
                    1f);
                rig.Right.localPosition = new Vector3(hip, -swing + kickOffset, 0f);
                rig.Right.localScale = new Vector3(
                    rig.BodyWidth * _legWidth * (1f - brace * 0.12f),
                    rig.BodyWidth * _legLength * kickScaleY,
                    1f);

                // Goalkeeper gloves & lateral dive stretch (#16).
                bool isGoalie = _env != null && _env.RoleOf(rig.Agent) == Agent_TeamRoles.Role.Goalie;
                if (rig.GloveLeftRenderer != null && rig.GloveLeftRenderer.enabled != isGoalie)
                {
                    rig.GloveLeftRenderer.enabled = isGoalie;
                    rig.GloveRightRenderer.enabled = isGoalie;
                }
                if (isGoalie && rig.GloveLeft != null && rig.GloveRight != null)
                {
                    Vector2 dive = haveBall
                        ? ComputeGoalieDiveOffset(
                            rig.Agent.transform.position, up, right, ballPos, ballVel,
                            rig.Agent.LateralSlipSpeed, rig.BodyWidth)
                        : Vector2.zero;
                    float gloveX = rig.BodyWidth * 0.54f;
                    float gloveY = rig.BodyWidth * 0.42f + Mathf.Abs(swing) * 0.25f;
                    rig.GloveLeft.localPosition = new Vector3(-gloveX + dive.x, gloveY + dive.y, 0f);
                    rig.GloveRight.localPosition = new Vector3(gloveX + dive.x, gloveY + dive.y, 0f);
                }
            }
        }

        /// <summary>
        /// Pure solver for the kicking leg's fore-aft offset and stretch.
        /// Winds back negatively when closing on the ball within <see cref="KICK_WINDUP_RADIUS"/>,
        /// snaps forward positively during <paramref name="kickFollowTimer"/>, and lunges
        /// forward when boosting into a challenge.
        /// </summary>
        public static float ComputeKickSwingOffset(float bodyWidth, float ballDist, float closingSpeed,
            float kickFollowTimer, bool isBoosting, out float legStretchMultiplier)
        {
            legStretchMultiplier = 1f;

            // Active post-contact follow-through: high forward snap with leg extension.
            if (kickFollowTimer > 0f)
            {
                float progress = 1f - Mathf.Clamp01(kickFollowTimer / KICK_FOLLOW_DURATION);
                float snap = Mathf.Sin(progress * Mathf.PI);
                legStretchMultiplier = 1f + snap * 0.32f;
                return bodyWidth * 0.48f * snap;
            }

            // Pre-contact wind-up when closing on the ball.
            if (ballDist < KICK_WINDUP_RADIUS && closingSpeed > 0.6f)
            {
                float proximity01 = 1f - Mathf.Clamp01(ballDist / KICK_WINDUP_RADIUS);
                float speed01 = Mathf.Clamp01((closingSpeed - 0.6f) / 5f);
                // If boosting hard right at the ball edge, transition into a tackle lunge.
                if (isBoosting && ballDist < 0.65f)
                {
                    legStretchMultiplier = 1.24f;
                    return bodyWidth * 0.36f * proximity01;
                }
                legStretchMultiplier = 1f + proximity01 * 0.1f;
                return -bodyWidth * 0.32f * proximity01 * speed01;
            }

            return 0f;
        }

        /// <summary>
        /// Pure solver for the goalkeeper's local glove reach/dive offset (x = lateral, y = forward)
        /// when tracking or diving across the goal mouth toward an incoming ball.
        /// </summary>
        public static Vector2 ComputeGoalieDiveOffset(Vector2 goaliePos, Vector2 forwardAxis, Vector2 rightAxis,
            Vector2 ballPos, Vector2 ballVel, float lateralSlipSpeed, float bodyWidth)
        {
            Vector2 toBall = ballPos - goaliePos;
            float dist = toBall.magnitude;
            if (dist < 0.001f) return Vector2.zero;

            // Lateral component of the ball relative to the keeper's body frame.
            float ballLat = Vector2.Dot(toBall / dist, rightAxis);
            float ballFwd = Vector2.Dot(toBall / dist, forwardAxis);

            // Incoming shot speed toward the keeper.
            float incomingSpeed = Mathf.Max(0f, -Vector2.Dot(ballVel, toBall / dist));
            float urgency = Mathf.Clamp01(incomingSpeed / 8f) * Mathf.Clamp01((6f - dist) / 5f);

            // Combine active lateral slip (diving across the line) with shot-tracking reach.
            float slipDive = Mathf.Clamp(lateralSlipSpeed / 4f, -1f, 1f) * 0.28f;
            float reachX = Mathf.Clamp(ballLat * (0.18f + urgency * 0.32f) + slipDive, -0.45f, 0.45f) * bodyWidth;
            float reachY = Mathf.Clamp(Mathf.Max(0f, ballFwd) * (0.12f + urgency * 0.26f), 0f, 0.38f) * bodyWidth;
            return new Vector2(reachX, reachY);
        }

        static float _rigBrace(Rig rig, float dt)
        {
            return rig.BraceSpring.Step(0f, dt, 26f, 0.5f);
        }
    }
}
