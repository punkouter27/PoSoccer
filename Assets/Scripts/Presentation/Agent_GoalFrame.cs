using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Marks the goal mouth as a thick team-coloured segment laid over the grey
    /// boundary wall, and draws a spring-deformable goal net behind the goal line
    /// that ripples and billows when struck by the ball.
    ///
    /// Auto-attaches to the goal transforms from Agent_EnvController.Awake so the
    /// scene never has to carry a serialized reference, and tracks
    /// EnvController.CurrentGoalWidth (the curriculum and the squad-size pitch
    /// scaling both move it).
    ///
    /// Scale compensation matters: this component lives ON the goal transform,
    /// which is scaled non-uniformly (roughly 4.8 x 0.26) to stretch its sprite.
    /// Everything therefore hangs off a child whose scale is the inverse of the
    /// goal's, which puts the geometry back into true pitch metres.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class Agent_GoalFrame : MonoBehaviour
    {
        [Tooltip("Marker thickness as a multiple of the boundary wall's thickness. " +
                 "Slightly over 1 so it covers the wall band rather than sitting inside it.")]
        [SerializeField] private float _thicknessOfWall = 1.15f;
        [Tooltip("Fallback thickness when no boundary wall can be found.")]
        [SerializeField] private float _fallbackThickness = 1.1f;
        [Tooltip("Drawn above the wall sprites; the scene's highest sorting order is 4.")]
        [SerializeField] private int _sortingOrder = 6;

        public const int NET_NODES = 11;
        const float NET_DEPTH = 0.72f;

        Agent_EnvController _env;
        Transform _frameRoot;
        LineRenderer _bar;
        Color _color = Color.white;
        float _shownWidth = -1f;

        // Deformable spring-chain net (visual scenes only).
        Agent_Lines _netLines;
        readonly float[] _netDisp = new float[NET_NODES];
        readonly float[] _netVel = new float[NET_NODES];
        bool _netDirty = true;
        float _lastBallSpeed = 8f;

        void Awake()
        {
            _env = GetComponentInParent<Agent_EnvController>();
            BuildFrame();
        }

        void Start()
        {
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (!Agent_Presentation.IsHeadless && Agent_Presentation.IsVisualScene(hud))
            {
                _netLines = new Agent_Lines($"GoalNet_{name}", _sortingOrder - 1, vertexCapacity: 256);
                _netDirty = true;
                if (_env != null) _env.EpisodeEnded += OnEpisodeEnded;
            }
        }

        void OnEnable()
        {
            Agent_EnvController.PitchReconfigured += OnPitchReconfigured;
            if (_env != null) ApplyWidth(_env.CurrentGoalWidth);
        }

        void OnDisable()
        {
            Agent_EnvController.PitchReconfigured -= OnPitchReconfigured;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            _netLines?.Dispose();
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null || _env == null) return;
            // A goal scored by Blue lands in the Red goal (and vice versa).
            Transform concededGoal = _env.GetGoalTransform(Agent_Soccer.Opponent(winner.Value));
            if (concededGoal != transform) return;

            float halfW = Mathf.Max(0.5f, (_shownWidth > 0f ? _shownWidth : _env.CurrentGoalWidth) * 0.5f);
            float localX = _env.Ball != null
                ? (_env.Ball.position.x - transform.position.x)
                : 0f;
            float normX = Mathf.Clamp(localX / halfW, -1f, 1f);
            InjectNetImpact(_netVel, normX, Mathf.Max(6f, _lastBallSpeed));
            _netDirty = true;
        }

        void OnPitchReconfigured(Transform goal)
        {
            _cachedWall = null;
            if (goal != transform) return;
            float width = _env != null ? _env.CurrentGoalWidth : 0f;
            if (Mathf.Approximately(width, _shownWidth)) return;
            ApplyWidth(width);
        }

        void LateUpdate()
        {
            if (_env == null) return;
            CompensateScale();
            if (!Mathf.Approximately(_env.CurrentGoalWidth, _shownWidth))
            {
                ApplyWidth(_env.CurrentGoalWidth);
            }

            if (_netLines != null)
            {
                if (_env.Ball != null)
                {
                    float speed = _env.Ball.linearVelocity.magnitude;
                    if (speed > 1f) _lastBallSpeed = speed;
                    SampleLiveBallContact();
                }

                bool moving = StepNetSprings(_netDisp, _netVel, Time.unscaledDeltaTime);
                if (moving || _netDirty)
                {
                    DrawNet();
                    _netDirty = false;
                }
            }
        }

        void SampleLiveBallContact()
        {
            Vector2 ballPos = _env.Ball.position;
            Vector2 goalPos = transform.position;
            float halfW = Mathf.Max(0.5f, _shownWidth * 0.5f);
            float dx = ballPos.x - goalPos.x;
            float dy = Mathf.Abs(ballPos.y - goalPos.y);
            if (dy < 0.55f && Mathf.Abs(dx) <= halfW)
            {
                float outwardSign = Mathf.Sign(transform.localPosition.y);
                float outwardVel = Vector2.Dot(_env.Ball.linearVelocity, new Vector2(0f, outwardSign));
                if (outwardVel > 2f)
                {
                    InjectNetImpact(_netVel, Mathf.Clamp(dx / halfW, -1f, 1f), outwardVel * 0.45f);
                    _netDirty = true;
                }
            }
        }

        /// <summary>
        /// Pure 1D coupled harmonic spring solver for the goal net's back cord.
        /// Endpoints (0 and length-1) are pinned to the goalposts. Returns true while
        /// any node is still moving or displaced.
        /// </summary>
        public static bool StepNetSprings(float[] disp, float[] vel, float dt,
            float stiffness = 140f, float coupling = 95f, float damping = 6.5f)
        {
            if (disp == null || vel == null || disp.Length < 3 || vel.Length != disp.Length) return false;
            float clampedDt = Mathf.Min(Mathf.Max(0f, dt), 0.04f);
            if (clampedDt <= 0f) return false;

            int n = disp.Length;
            disp[0] = 0f; vel[0] = 0f;
            disp[n - 1] = 0f; vel[n - 1] = 0f;

            bool active = false;
            for (int i = 1; i < n - 1; i++)
            {
                float restore = -stiffness * disp[i];
                float neighborPull = coupling * (disp[i - 1] + disp[i + 1] - 2f * disp[i]);
                float drag = -damping * vel[i];
                vel[i] += (restore + neighborPull + drag) * clampedDt;
            }

            for (int i = 1; i < n - 1; i++)
            {
                disp[i] = Mathf.Clamp(disp[i] + vel[i] * clampedDt, -0.45f, 1.35f);
                if (Mathf.Abs(disp[i]) > 0.002f || Mathf.Abs(vel[i]) > 0.005f)
                {
                    active = true;
                }
                else
                {
                    disp[i] = 0f;
                    vel[i] = 0f;
                }
            }
            return active;
        }

        /// <summary>
        /// Pure impulse injection into the net velocity array around <paramref name="normalizedX"/> in [-1, 1].
        /// </summary>
        public static void InjectNetImpact(float[] vel, float normalizedX, float speed)
        {
            if (vel == null || vel.Length < 3) return;
            int n = vel.Length;
            float centerIdx = (Mathf.Clamp(normalizedX, -1f, 1f) * 0.5f + 0.5f) * (n - 1);
            float impulse = Mathf.Clamp(speed * 0.38f, 0.5f, 7.5f);

            for (int i = 1; i < n - 1; i++)
            {
                float dist = Mathf.Abs(i - centerIdx) / (n * 0.28f);
                float weight = Mathf.Exp(-dist * dist * 2.2f);
                vel[i] += impulse * weight;
            }
        }

        void DrawNet()
        {
            if (_netLines == null || _shownWidth <= 0f) return;

            float halfW = _shownWidth * 0.5f;
            Vector2 origin = transform.position;
            float outward = transform.localPosition.y >= 0f ? 1f : -1f;
            var wall = OutwardWall();
            float goalLineY = origin.y + (wall != null ? wall.localPosition.y - transform.localPosition.y : 0f);

            Color cordColor = new Color(_color.r, _color.g, _color.b, 0.72f);
            Color meshColor = new Color(0.92f, 0.95f, 1f, 0.28f);

            _netLines.Begin();

            Vector2 prevBack = Vector2.zero;
            Vector2 prevFront = Vector2.zero;
            int n = NET_NODES;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                float x = origin.x + Mathf.Lerp(-halfW, halfW, t);
                // Arch slightly deeper in the middle + dynamic spring billow.
                float arch = Mathf.Sin(t * Mathf.PI) * 0.14f;
                float depth = (NET_DEPTH + arch + _netDisp[i]) * outward;

                Vector2 front = new Vector2(x, goalLineY);
                Vector2 back = new Vector2(x, goalLineY + depth);

                // Side posts at i == 0 and i == n - 1, plus inner mesh ribs.
                _netLines.AddSegment(front, back, i == 0 || i == n - 1 ? 0.045f : 0.018f,
                    i == 0 || i == n - 1 ? cordColor : meshColor);

                if (i > 0)
                {
                    // Back net cord.
                    _netLines.AddSegment(prevBack, back, 0.038f, cordColor);
                    // Mid-depth cross-strand and diagonal diamond mesh.
                    Vector2 midA = Vector2.Lerp(prevFront, prevBack, 0.5f);
                    Vector2 midB = Vector2.Lerp(front, back, 0.5f);
                    _netLines.AddSegment(midA, midB, 0.016f, meshColor);
                    _netLines.AddSegment(prevFront, back, 0.014f, meshColor);
                    _netLines.AddSegment(front, prevBack, 0.014f, meshColor);
                }

                prevBack = back;
                prevFront = front;
            }

            _netLines.Commit();
        }

        void BuildFrame()
        {
            var rootGo = new GameObject("GoalFrame");
            rootGo.transform.SetParent(transform, false);
            rootGo.transform.localPosition = Vector3.zero;
            rootGo.transform.localRotation = Quaternion.identity;
            _frameRoot = rootGo.transform;
            CompensateScale();

            var go = new GameObject("GoalMouthBar");
            go.transform.SetParent(_frameRoot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            _bar = go.AddComponent<LineRenderer>();
            _bar.useWorldSpace = false;
            _bar.positionCount = 2;
            _bar.numCornerVertices = 0;
            _bar.numCapVertices = 0;
            _bar.alignment = LineAlignment.View;
            _bar.sortingOrder = _sortingOrder;
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default");
            _bar.sharedMaterial = new Material(shader) { color = Color.white };
        }

        /// <summary>Undo the goal transform's non-uniform scale so children use pitch metres.</summary>
        void CompensateScale()
        {
            if (_frameRoot == null) return;
            Vector3 s = transform.lossyScale;
            _frameRoot.localScale = new Vector3(
                Mathf.Abs(s.x) < 0.0001f ? 1f : 1f / s.x,
                Mathf.Abs(s.y) < 0.0001f ? 1f : 1f / s.y,
                1f);
        }

        void ApplyWidth(float width)
        {
            if (_bar == null || width <= 0f) return;
            _shownWidth = width;
            _netDirty = true;

            float halfW = width * 0.5f;
            var wall = OutwardWall();
            float y = wall != null ? wall.localPosition.y - transform.localPosition.y : 0f;
            float thickness = wall != null
                ? Mathf.Abs(wall.localScale.y) * _thicknessOfWall
                : _fallbackThickness;

            _bar.startWidth = thickness;
            _bar.endWidth = thickness;
            _bar.SetPosition(0, new Vector3(-halfW, y, 0f));
            _bar.SetPosition(1, new Vector3(halfW, y, 0f));
        }

        Transform OutwardWall()
        {
            if (_env == null) return null;
            if (_cachedWall != null) return _cachedWall;
            _cachedWall = FindOutwardWall();
            return _cachedWall;
        }

        Transform _cachedWall;

        Transform FindOutwardWall()
        {
            float goalY = transform.localPosition.y;
            Transform best = null;
            float bestDistance = float.MaxValue;
            foreach (Transform child in _env.transform)
            {
                if (!child.name.StartsWith("Wall_")) continue;
                if (Mathf.Sign(child.localPosition.y) != Mathf.Sign(goalY)) continue;
                if (Mathf.Abs(child.localScale.x) < Mathf.Abs(child.localScale.y)) continue;
                float distance = Mathf.Abs(child.localPosition.y - goalY);
                if (distance < bestDistance) { bestDistance = distance; best = child; }
            }
            return best;
        }

        /// <summary>Team-colored goal mouth (Blue = red goal, Red = blue goal).</summary>
        public void SetColor(Color color)
        {
            _color = color;
            _netDirty = true;
            if (_bar == null) return;
            _bar.startColor = _color;
            _bar.endColor = _color;
            if (_bar.sharedMaterial != null) _bar.sharedMaterial.color = _color;
        }
    }
}
