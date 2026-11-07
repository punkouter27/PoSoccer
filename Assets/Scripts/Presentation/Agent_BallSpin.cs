using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Makes the ball's SPIN visible: two swirl arcs around the ball that turn
    /// with Rigidbody2D.angularVelocity and fade in with it.
    ///
    /// WHY IT MATTERS FOR READING THE GAME. Agent_EnvController already applies a
    /// Magnus-lite curve (magnusScale x spin x perpendicular velocity), so a
    /// spinning ball bends - but a 0.22 m disc seen from above shows no spin at
    /// all, and a bending ball with nothing to explain it reads as a physics bug.
    /// The swirl turns the cause into something a spectator can see before the
    /// curve happens: arcs turning clockwise, ball bending one way.
    ///
    /// VISUAL ONLY, by rule. It reads angularVelocity and never writes to the body:
    /// any change to the ball's physics changes what every trained brain's
    /// observations mean (CLAUDE.md, "the dangerous kind" of contract change).
    ///
    /// One Agent_Lines mesh, so one draw call, rebuilt only while the ball is
    /// actually spinning; at rest the batch is hidden and Update returns early.
    /// Per pitch (InstallVisuals), so each gallery clone swirls its own ball.
    /// Presentation only: self-disables in training, evaluation and headless runs.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_BallSpin : MonoBehaviour
    {
        [Tooltip("Spin (deg/s) below which nothing is drawn.")]
        [SerializeField] private float _minSpin = 90f;
        [Tooltip("Spin (deg/s) at which the swirl is fully opaque.")]
        [SerializeField] private float _fullSpin = 720f;
        [Tooltip("Fastest the swirl is drawn turning, deg/s. Real spin above this " +
                 "strobes into a stationary pattern at 60 fps, which reads as NO spin.")]
        [SerializeField] private float _maxDrawnSpin = 540f;
        [Tooltip("Swirl radius as a multiple of the ball radius.")]
        [SerializeField] private float _radiusScale = 1.7f;
        [Tooltip("Fraction of a full turn each of the two arcs covers.")]
        [Range(0.05f, 0.45f)] [SerializeField] private float _arcFraction = 0.22f;
        [Tooltip("Above the ball (2) and the shot tracer (16), below confetti (20).")]
        [SerializeField] private int _sortingOrder = 17;
        [Range(0f, 1f)] [SerializeField] private float _opacity = 0.8f;
        [SerializeField] private bool _enableSpin = true;

        const int SEGMENTS_PER_ARC = 6;

        Agent_EnvController _env;
        Agent_Lines _lines;
        float _phaseRad;
        float _ballRadius = 0.11f;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (!_enableSpin || Agent_Presentation.IsHeadless || !Agent_Presentation.IsVisualScene(hud)
                || _env == null || _env.Ball == null)
            {
                enabled = false;
                return;
            }

            if (_env.Ball.TryGetComponent(out CircleCollider2D circle))
            {
                _ballRadius = circle.radius * Mathf.Abs(_env.Ball.transform.lossyScale.x);
            }

            _lines = new Agent_Lines("BallSpin", _sortingOrder, vertexCapacity: SEGMENTS_PER_ARC * 2 * 4);
            _lines.Visible = false;
        }

        void OnDestroy()
        {
            _lines?.Dispose();
        }

        void LateUpdate()
        {
            var ball = _env.Ball;
            if (ball == null) return;

            float spin = ball.angularVelocity;          // deg/s, + = counter-clockwise
            float magnitude = Mathf.Abs(spin);
            if (magnitude < _minSpin || Agent_TimeFreeze.IsFrozen)
            {
                if (_lines.Visible) _lines.Visible = false;
                return;
            }

            float drawn = Mathf.Sign(spin) * Mathf.Min(magnitude, _maxDrawnSpin);
            _phaseRad += drawn * Mathf.Deg2Rad * Time.deltaTime;
            if (_phaseRad > Mathf.PI * 2f) _phaseRad -= Mathf.PI * 2f;
            else if (_phaseRad < 0f) _phaseRad += Mathf.PI * 2f;

            float alpha = _opacity * Mathf.Clamp01((magnitude - _minSpin) / Mathf.Max(1f, _fullSpin - _minSpin));
            var head = new Color(1f, 1f, 1f, alpha);
            var tail = new Color(1f, 1f, 1f, 0f);

            Vector2 centre = ball.position;
            float radius = _ballRadius * _radiusScale;
            float halfWidth = Mathf.Max(0.012f, _ballRadius * 0.16f);
            float sweep = _arcFraction * Mathf.PI * 2f;
            // Tails trail BEHIND the direction of turn, so the fade itself says
            // which way the ball spins.
            float trail = -Mathf.Sign(spin);

            _lines.Begin();
            for (int arc = 0; arc < 2; arc++)
            {
                float start = _phaseRad + arc * Mathf.PI;
                for (int segment = 0; segment < SEGMENTS_PER_ARC; segment++)
                {
                    float t0 = segment / (float)SEGMENTS_PER_ARC;
                    float t1 = (segment + 1) / (float)SEGMENTS_PER_ARC;
                    float a0 = start + trail * sweep * t0;
                    float a1 = start + trail * sweep * t1;
                    Vector2 p0 = centre + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
                    Vector2 p1 = centre + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
                    _lines.AddFadedSegment(p0, p1, halfWidth,
                        Color.Lerp(head, tail, t0), Color.Lerp(head, tail, t1));
                }
            }
            _lines.Commit();
            if (!_lines.Visible) _lines.Visible = true;
        }
    }
}
