using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// 2.5D Ball Height ("Lob / Bounce / Header") visual & event layer.
    ///
    /// WHY 2.5D WITHOUT 3D PHYSICS. Every trained brain in this project observes a
    /// 2D ground-plane ball (Agent_EditMode_ObsContract). Changing the ball's 2D
    /// Rigidbody or scaling its collider transform would invalidate every checkpoint.
    /// Instead, this component integrates a vertical height z(t) and vertical velocity
    /// vz(t) alongside the live 2D ball, triggered by corner-escape wall kicks,
    /// high-impulse deflections, and hard strikes.
    ///
    /// When the ball is airborne (Height > 0.02 m):
    ///  - a child visual sprite scales up toward the camera (1 + Height * 0.35) and
    ///    lifts along +y while the physics collider on the parent stays untouched;
    ///  - a faint ground-target ring marks the true 2D footprint on the turf;
    ///  - Agent_Shadows pushes the ball's contact shadow further outward and softens it;
    ///  - if a player touches the ball while Height >= HeaderMinHeight, HeaderStruck
    ///    fires so Agent_Commentary and Agent_Faces can call and animate the header.
    ///
    /// Presentation only: self-disables in training, evaluation, and headless runs.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    [DisallowMultipleComponent]
    public sealed class Agent_BallHeight : MonoBehaviour
    {
        public const float HeaderMinHeight = 0.38f;
        public const float MaxHeight = 3.2f;

        [SerializeField] private float _gravity = 9.81f;
        [SerializeField] private float _restitution = 0.58f;
        [SerializeField] private float _visualScalePerMetre = 0.34f;
        [SerializeField] private float _visualLiftPerMetre = 0.24f;
        [SerializeField] private bool _enableBallHeight = true;

        /// <summary>Fired when an agent touches the ball while it is at or above <see cref="HeaderMinHeight"/>.</summary>
        public static event System.Action<Agent_Soccer, float> HeaderStruck;

        /// <summary>Fired when an airborne ball bounces off the turf (worldPos, verticalImpactSpeed).</summary>
        public static event System.Action<Vector2, float> BallBounced;

        /// <summary>Current ball height above the turf in metres (0 when rolling on the grass).</summary>
        public float Height => _height;

        /// <summary>Current vertical velocity in m/s.</summary>
        public float VerticalVelocity => _verticalVel;

        Agent_EnvController _env;
        Agent_Soccer[] _agents;
        System.Action<Vector2, Vector2>[] _wallKickHandlers;
        SpriteRenderer _ballRenderer;
        Transform _loftTransform;
        SpriteRenderer _loftRenderer;
        Agent_Lines _groundRing;
        float _ballRadius = 0.11f;
        float _height;
        float _verticalVel;
        Vector2 _ballVelLastStep;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (!_enableBallHeight || Agent_Presentation.IsHeadless || !Agent_Presentation.IsVisualScene(hud)
                || _env == null || _env.Ball == null)
            {
                enabled = false;
                return;
            }

            if (_env.Ball.TryGetComponent(out CircleCollider2D circle))
            {
                _ballRadius = circle.radius * Mathf.Abs(_env.Ball.transform.lossyScale.x);
            }

            _ballRenderer = _env.Ball.GetComponentInChildren<SpriteRenderer>();
            if (_ballRenderer != null)
            {
                var go = new GameObject("BallLoftVisual");
                go.transform.SetParent(_env.Ball.transform, false);
                _loftTransform = go.transform;
                _loftRenderer = go.AddComponent<SpriteRenderer>();
                _loftRenderer.sprite = _ballRenderer.sprite;
                _loftRenderer.sharedMaterial = _ballRenderer.sharedMaterial;
                _loftRenderer.color = _ballRenderer.color;
                _loftRenderer.sortingLayerID = _ballRenderer.sortingLayerID;
                _loftRenderer.sortingOrder = _ballRenderer.sortingOrder + 1;
                _loftRenderer.enabled = false;
            }

            _groundRing = new Agent_Lines("BallGroundRing", sortingOrder: 1, vertexCapacity: 12 * 4);
            _groundRing.Visible = false;

            _agents = _env.GetComponentsInChildren<Agent_Soccer>(true);
            _wallKickHandlers = new System.Action<Vector2, Vector2>[_agents.Length];
            for (int i = 0; i < _agents.Length; i++)
            {
                var agent = _agents[i];
                if (agent == null) continue;
                _wallKickHandlers[i] = (_, _) => OnWallKicked(agent, Vector2.zero);
                agent.WallKicked += _wallKickHandlers[i];
            }

            _env.BallTouched += OnBallTouched;
            _env.EpisodeEnded += OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit += OnBallHit;
        }

        void OnDestroy()
        {
            if (_env != null)
            {
                _env.BallTouched -= OnBallTouched;
                _env.EpisodeEnded -= OnEpisodeEnded;
            }
            if (_agents != null && _wallKickHandlers != null)
            {
                for (int i = 0; i < _agents.Length; i++)
                {
                    if (_agents[i] != null && _wallKickHandlers[i] != null)
                        _agents[i].WallKicked -= _wallKickHandlers[i];
                }
            }
            Agent_MatchFX.BallContact.Hit -= OnBallHit;
            if (_groundRing != null) _groundRing.Dispose();
        }

        /// <summary>
        /// Pure vertical ball integrator: applies gravity and bounces off the turf (height = 0)
        /// with coefficient of restitution until vertical speed falls below the settle threshold.
        /// </summary>
        public static void StepBallVertical(
            ref float height, ref float verticalVel, float dt,
            float gravity = 9.81f, float restitution = 0.58f)
        {
            if (dt <= 0f) return;
            if (height <= 0f && Mathf.Abs(verticalVel) < 0.001f)
            {
                height = 0f;
                verticalVel = 0f;
                return;
            }

            verticalVel -= gravity * dt;
            height += verticalVel * dt;

            if (height <= 0f)
            {
                height = 0f;
                if (verticalVel < -0.65f)
                {
                    verticalVel = -verticalVel * Mathf.Clamp01(restitution);
                }
                else
                {
                    verticalVel = 0f;
                }
            }
            else if (height > MaxHeight)
            {
                height = MaxHeight;
                if (verticalVel > 0f) verticalVel = 0f;
            }
        }

        /// <summary>
        /// Pure helper mapping horizontal ball speed and contact type to an upward loft impulse (m/s).
        /// </summary>
        public static float ImpulseToLoft(float speed, bool isWallKick = false, bool isDeflection = false)
        {
            if (isWallKick)
            {
                return Mathf.Clamp(3.6f + speed * 0.22f, 3.6f, 6.8f);
            }
            if (isDeflection)
            {
                return speed > 5.5f ? Mathf.Clamp((speed - 5.5f) * 0.48f, 0f, 6.2f) : 0f;
            }
            return speed > 9.5f ? Mathf.Clamp((speed - 9.5f) * 0.38f, 0f, 5.2f) : 0f;
        }

        void FixedUpdate()
        {
            if (_env == null || _env.Ball == null || Agent_TimeFreeze.IsFrozen) return;
            _ballVelLastStep = _env.Ball.linearVelocity;
            float prevHeight = _height;
            float prevVel = _verticalVel;
            StepBallVertical(ref _height, ref _verticalVel, Time.fixedDeltaTime, _gravity, _restitution);
            if (prevHeight > 0.05f && _height <= 0f && prevVel < -0.75f)
            {
                BallBounced?.Invoke(_env.Ball.position, -prevVel);
            }
        }

        void LateUpdate()
        {
            if (_env == null || _env.Ball == null) return;

            bool airborne = _height > 0.02f;
            if (_loftRenderer != null)
            {
                if (_loftRenderer.enabled != airborne) _loftRenderer.enabled = airborne;
                if (airborne)
                {
                    float scale = 1f + _height * _visualScalePerMetre;
                    _loftTransform.localScale = new Vector3(scale, scale, 1f);
                    // World-up lift independent of ball spin rotation.
                    _loftTransform.position = _env.Ball.transform.position + new Vector3(0f, _height * _visualLiftPerMetre, 0f);
                    _loftTransform.rotation = _env.Ball.transform.rotation;
                }
            }

            if (_groundRing != null)
            {
                if (!airborne)
                {
                    if (_groundRing.Visible) _groundRing.Visible = false;
                }
                else
                {
                    Vector2 pos = _env.Ball.position;
                    float alpha = Mathf.Clamp01(_height / 1.6f) * 0.42f;
                    var col = new Color(1f, 1f, 1f, alpha);
                    float radius = _ballRadius * (0.9f + Mathf.Min(_height, 1.5f) * 0.25f);
                    const int Segs = 12;
                    _groundRing.Begin();
                    for (int i = 0; i < Segs; i++)
                    {
                        float a0 = (i / (float)Segs) * Mathf.PI * 2f;
                        float a1 = ((i + 1) / (float)Segs) * Mathf.PI * 2f;
                        Vector2 p0 = pos + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
                        Vector2 p1 = pos + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
                        _groundRing.AddSegment(p0, p1, 0.016f, col);
                    }
                    _groundRing.Commit();
                    if (!_groundRing.Visible) _groundRing.Visible = true;
                }
            }
        }

        void OnWallKicked(Agent_Soccer agent, Vector2 position)
        {
            float speed = _env != null && _env.Ball != null ? _env.Ball.linearVelocity.magnitude : 8f;
            float loft = ImpulseToLoft(speed, isWallKick: true);
            if (loft > _verticalVel) _verticalVel = loft;
        }

        void OnBallTouched(Agent_Soccer toucher)
        {
            if (toucher == null || _env == null || _env.Ball == null) return;

            if (_height >= HeaderMinHeight)
            {
                float struckHeight = _height;
                // A header redirects the vertical trajectory into a crisp downward bounce.
                _verticalVel = Mathf.Max(1.8f, Mathf.Abs(_verticalVel) * 0.55f);
                HeaderStruck?.Invoke(toucher, struckHeight);
                return;
            }

            float speed = Mathf.Max(_ballVelLastStep.magnitude, _env.Ball.linearVelocity.magnitude);
            bool deflection = _env.PreviousToucher != null && _env.PreviousToucher.team != toucher.team && speed > 6.5f;
            float loft = ImpulseToLoft(speed, isWallKick: false, isDeflection: deflection);
            if (loft > _verticalVel) _verticalVel = loft;
        }

        void OnBallHit(Collision2D collision)
        {
            if (_env == null || _env.Ball == null || collision == null || collision.collider == null) return;
            if (collision.collider.CompareTag("Wall") && _ballVelLastStep.magnitude > 8.5f)
            {
                float loft = ImpulseToLoft(_ballVelLastStep.magnitude, isWallKick: false, isDeflection: true) * 0.65f;
                if (loft > _verticalVel) _verticalVel = loft;
            }
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            _height = 0f;
            _verticalVel = 0f;
        }
    }
}
