using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Sideline Life: Touchline Assistant Referees (Linesmen with <see cref="Agent_Art.Flag"/>),
    /// Pacing Technical-Area Bench Coaches, and Front-Row Waving Crowd Scarves.
    ///
    /// Positioned in the 0.35 m touchline apron between the pitch boundary and
    /// <see cref="Agent_Crowd"/>'s advertising hoardings so they are visible inside
    /// the portrait camera's framing without occluding the field of play.
    ///
    /// Presentation only: self-disables in training, evaluation, and headless runs.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    [DisallowMultipleComponent]
    public sealed class Agent_Sidelines : MonoBehaviour
    {
        const int SCARF_COUNT = 16;

        [SerializeField] private int _sortingOrder = 4;
        [SerializeField] private bool _enableSidelines = true;

        Agent_EnvController _env;
        Agent_Soccer[] _agents;
        System.Action<Vector2, Vector2>[] _wallKickHandlers;
        Transform _root;

        Transform _linesmanNorth;
        Transform _linesmanSouth;
        Transform _flagNorth;
        Transform _flagSouth;
        float _northY, _northVelY;
        float _southY, _southVelY;
        float _flagWaveTimer;

        Transform _coachBlue;
        Transform _coachRed;
        SpriteRenderer _coachBlueRenderer;
        SpriteRenderer _coachRedRenderer;
        float _blueExcitement;
        float _redExcitement;

        Agent_Lines _scarfLines;
        float _scarfTimer;

        /// <summary>
        /// Pure spring follower keeping an assistant referee aligned with the ball's Y coordinate
        /// within their assigned half of the pitch.
        /// </summary>
        public static float ComputeLinesmanY(
            float currentY, ref float velY, float ballY, float halfPitchY, bool isNorthHalf, float dt)
        {
            float limit = Mathf.Max(0.5f, halfPitchY * 0.92f);
            float targetY = isNorthHalf
                ? Mathf.Clamp(ballY, 0f, limit)
                : Mathf.Clamp(ballY, -limit, 0f);
            var spring = new Agent_Tween.SpringFloat { Value = currentY, Velocity = velY };
            float next = spring.Step(targetY, dt, angularFrequency: 12f, dampingRatio: 0.85f);
            velY = spring.Velocity;
            return next;
        }

        /// <summary>
        /// Pure position solver for a bench coach pacing their technical area along the touchline.
        /// </summary>
        public static Vector2 ComputeCoachPosition(
            Vector2 pitchCentre, Vector2 halfExtents, bool isBlueTeam, float timeSeconds, float excitement01)
        {
            float x = pitchCentre.x - halfExtents.x - 0.24f;
            float baseHalfY = isBlueTeam ? -halfExtents.y * 0.42f : halfExtents.y * 0.42f;
            float paceRate = 1.4f + excitement01 * 3.2f;
            float paceSpan = Mathf.Min(1.2f, halfExtents.y * 0.16f);
            float y = pitchCentre.y + baseHalfY + Mathf.Sin(timeSeconds * paceRate + (isBlueTeam ? 0f : 1.7f)) * paceSpan;
            float hop = excitement01 > 0.01f
                ? Mathf.Abs(Mathf.Sin(timeSeconds * 14f)) * 0.08f * excitement01
                : 0f;
            return new Vector2(x - hop, y);
        }

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (!_enableSidelines || Agent_Presentation.IsHeadless || !Agent_Presentation.IsMatchScene(hud) || _env == null)
            {
                enabled = false;
                return;
            }

            BuildActors();
            _scarfLines = new Agent_Lines("CrowdScarves", _sortingOrder + 1, vertexCapacity: SCARF_COUNT * 4);
            _scarfLines.Visible = false;

            _agents = _env.GetComponentsInChildren<Agent_Soccer>(true);
            _wallKickHandlers = new System.Action<Vector2, Vector2>[_agents.Length];
            for (int i = 0; i < _agents.Length; i++)
            {
                var agent = _agents[i];
                if (agent == null) continue;
                _wallKickHandlers[i] = (_, _) => OnWallKicked(agent, Vector2.zero);
                agent.WallKicked += _wallKickHandlers[i];
            }

            _env.EpisodeEnded += OnEpisodeEnded;
            Agent_MatchFX.BallContact.Hit += OnBallHit;
        }

        void OnDestroy()
        {
            if (_env != null)
            {
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
            if (_scarfLines != null) _scarfLines.Dispose();
        }

        void BuildActors()
        {
            var rootGo = new GameObject("Sidelines");
            rootGo.transform.SetParent(transform, false);
            _root = rootGo.transform;

            Material lit = Agent_Stadium.Instance != null ? Agent_Stadium.Instance.litMaterial : null;
            Sprite bodySprite = Agent_Art.Disc(0.34f);
            Sprite flagSprite = Agent_Art.Flag(0.30f);

            _linesmanNorth = CreateOfficial("Linesman_North", bodySprite, flagSprite, lit, out _flagNorth);
            _linesmanSouth = CreateOfficial("Linesman_South", bodySprite, flagSprite, lit, out _flagSouth);

            _coachBlue = CreateCoach("Coach_Blue", bodySprite, lit, Agent_Soccer.Team.Blue, out _coachBlueRenderer);
            _coachRed = CreateCoach("Coach_Red", bodySprite, lit, Agent_Soccer.Team.Red, out _coachRedRenderer);

            Vector2 half = _env.PitchHalfExtents;
            _northY = half.y * 0.4f;
            _southY = -half.y * 0.4f;
        }

        Transform CreateOfficial(string label, Sprite bodySprite, Sprite flagSprite, Material lit, out Transform flagOut)
        {
            var go = new GameObject(label);
            go.transform.SetParent(_root, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = bodySprite;
            if (lit != null) sr.sharedMaterial = lit;
            sr.color = new Color(0.14f, 0.16f, 0.20f, 1f);
            sr.sortingOrder = _sortingOrder;

            var flagGo = new GameObject("Flag");
            flagGo.transform.SetParent(go.transform, false);
            flagGo.transform.localPosition = new Vector3(0f, 0.22f, 0f);
            var flagSr = flagGo.AddComponent<SpriteRenderer>();
            flagSr.sprite = flagSprite;
            if (lit != null) flagSr.sharedMaterial = lit;
            flagSr.color = Color.white;
            flagSr.sortingOrder = _sortingOrder + 1;

            flagOut = flagGo.transform;
            return go.transform;
        }

        Transform CreateCoach(string label, Sprite bodySprite, Material lit, Agent_Soccer.Team team, out SpriteRenderer srOut)
        {
            var go = new GameObject(label);
            go.transform.SetParent(_root, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = bodySprite;
            if (lit != null) sr.sharedMaterial = lit;
            sr.color = Color.Lerp(Agent_SoccerView.TeamColor(team), Color.black, 0.25f);
            sr.sortingOrder = _sortingOrder;
            srOut = sr;
            return go.transform;
        }

        void LateUpdate()
        {
            if (_env == null || _root == null) return;

            float dt = Time.unscaledDeltaTime;
            float t = Time.unscaledTime;
            Vector2 centre = transform.position;
            Vector2 half = _env.PitchHalfExtents;
            float ballLocalY = _env.Ball != null ? _env.Ball.position.y - centre.y : 0f;

            _northY = ComputeLinesmanY(_northY, ref _northVelY, ballLocalY, half.y, isNorthHalf: true, dt);
            _southY = ComputeLinesmanY(_southY, ref _southVelY, ballLocalY, half.y, isNorthHalf: false, dt);

            float eastX = centre.x + half.x + 0.22f;
            if (_linesmanNorth != null)
            {
                _linesmanNorth.position = new Vector3(eastX, centre.y + _northY, 0f);
                _linesmanNorth.rotation = Quaternion.Euler(0f, 0f, 90f);
            }
            if (_linesmanSouth != null)
            {
                _linesmanSouth.position = new Vector3(eastX, centre.y + _southY, 0f);
                _linesmanSouth.rotation = Quaternion.Euler(0f, 0f, 90f);
            }

            if (_flagWaveTimer > 0f)
            {
                _flagWaveTimer = Mathf.Max(0f, _flagWaveTimer - dt);
                float wave = Mathf.Sin(t * 24f) * 38f;
                if (_flagNorth != null) _flagNorth.localRotation = Quaternion.Euler(0f, 0f, wave);
                if (_flagSouth != null) _flagSouth.localRotation = Quaternion.Euler(0f, 0f, -wave);
            }
            else
            {
                if (_flagNorth != null) _flagNorth.localRotation = Quaternion.identity;
                if (_flagSouth != null) _flagSouth.localRotation = Quaternion.identity;
            }

            _blueExcitement = Mathf.Max(0f, _blueExcitement - dt * 0.45f);
            _redExcitement = Mathf.Max(0f, _redExcitement - dt * 0.45f);

            if (_coachBlue != null)
            {
                Vector2 pos = ComputeCoachPosition(centre, half, isBlueTeam: true, t, _blueExcitement);
                _coachBlue.position = new Vector3(pos.x, pos.y, 0f);
                if (_coachBlueRenderer != null)
                    _coachBlueRenderer.color = Color.Lerp(Agent_SoccerView.TeamColor(Agent_Soccer.Team.Blue), Color.black, 0.25f);
            }
            if (_coachRed != null)
            {
                Vector2 pos = ComputeCoachPosition(centre, half, isBlueTeam: false, t, _redExcitement);
                _coachRed.position = new Vector3(pos.x, pos.y, 0f);
                if (_coachRedRenderer != null)
                    _coachRedRenderer.color = Color.Lerp(Agent_SoccerView.TeamColor(Agent_Soccer.Team.Red), Color.black, 0.25f);
            }

            UpdateScarves(centre, half, dt, t);
        }

        void UpdateScarves(Vector2 centre, Vector2 half, float dt, float t)
        {
            if (_scarfLines == null) return;
            if (_scarfTimer <= 0f)
            {
                if (_scarfLines.Visible) _scarfLines.Visible = false;
                return;
            }

            _scarfTimer = Mathf.Max(0f, _scarfTimer - dt);
            float envAlpha = Mathf.Clamp01(_scarfTimer / 0.5f);
            Color blueCol = Agent_SoccerView.TeamColor(Agent_Soccer.Team.Blue);
            Color redCol = Agent_SoccerView.TeamColor(Agent_Soccer.Team.Red);
            blueCol.a = envAlpha * 0.9f;
            redCol.a = envAlpha * 0.9f;

            _scarfLines.Begin();
            int perSide = SCARF_COUNT / 2;
            for (int i = 0; i < SCARF_COUNT; i++)
            {
                bool east = i < perSide;
                int idx = east ? i : i - perSide;
                float u = (idx + 0.5f) / perSide;
                float y = Mathf.Lerp(-half.y * 0.85f, half.y * 0.85f, u);
                float x = east ? half.x + 0.95f : -half.x - 0.95f;
                float wave = Mathf.Sin(t * 14f + i * 0.9f) * 0.12f;
                Vector2 center = centre + new Vector2(x + wave * 0.4f, y);
                Vector2 span = new Vector2(wave, 0.22f);
                Color col = y < 0f ? blueCol : redCol;
                _scarfLines.AddSegment(center - span, center + span, 0.045f, col);
            }
            _scarfLines.Commit();
            if (!_scarfLines.Visible) _scarfLines.Visible = true;
        }

        void OnWallKicked(Agent_Soccer agent, Vector2 position)
        {
            _flagWaveTimer = Mathf.Max(_flagWaveTimer, 1.1f);
            if (agent != null)
            {
                if (agent.team == Agent_Soccer.Team.Blue) _blueExcitement = Mathf.Max(_blueExcitement, 0.5f);
                else _redExcitement = Mathf.Max(_redExcitement, 0.5f);
            }
        }

        void OnBallHit(Collision2D collision)
        {
            if (collision != null && collision.collider != null && collision.collider.CompareTag("Wall"))
            {
                _flagWaveTimer = Mathf.Max(_flagWaveTimer, 0.65f);
            }
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            _flagWaveTimer = 2.4f;
            _scarfTimer = 2.8f;
            if (winner == Agent_Soccer.Team.Blue) _blueExcitement = 1f;
            else if (winner == Agent_Soccer.Team.Red) _redExcitement = 1f;
        }
    }
}
