using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// The passing-lane overlay: from whoever has the ball, a line to every
    /// teammate and an arrow at the goal, each coloured by whether the ball would
    /// get there before an opponent does.
    ///
    /// WHY. A spectator watching two brains can see where the ball goes but not what
    /// was on offer when it went there. A carrier who dribbles into a crowd while a
    /// green lane sat open to a teammate is a policy that cannot see the pass; one
    /// that shoots down a red lane is a policy that cannot read a block. Both look
    /// identical without this overlay. It reads only positions the simulation
    /// already has - see <see cref="Agent_PassLaneMath"/> for the race it runs.
    ///
    /// The carrier is the player nearest the ball inside <see cref="_controlRadius"/>,
    /// the same 1.2 m the possession reward pays inside, so "has the ball" here
    /// means what it means to the brains. Nobody inside it, nothing drawn.
    ///
    /// In a 1v1 there is no teammate, so only the shot lane is drawn.
    ///
    /// One draw call (<see cref="Agent_Lines"/>), zero allocation per frame.
    /// Hidden on start; toggle with P or <see cref="Visible"/>. Presentation only:
    /// self-disables in training, evaluation and headless players.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_PassLanes : MonoBehaviour
    {
        [Header("Who has the ball")]
        [Tooltip("Distance from the ball (m) inside which the nearest player counts as the " +
                 "carrier. 1.2 m matches the possession reward in Agent_Soccer.")]
        [SerializeField] private float _controlRadius = 1.2f;

        [Header("The race")]
        [Tooltip("Pass speed, m/s.")]
        [SerializeField] private float _ballSpeed = 9f;
        [Tooltip("Speed an opponent closes on the lane at, m/s. Below the 9.54 m/s top " +
                 "speed because nobody intercepts from a standing start at top speed.")]
        [SerializeField] private float _runnerSpeed = 6.5f;
        [Tooltip("Distance (m) at which an opponent can touch the ball without moving: " +
                 "body radius plus ball radius plus a stretched leg.")]
        [SerializeField] private float _reach = 0.75f;
        [Tooltip("Seconds before an opponent starts to react.")]
        [SerializeField] private float _reaction = 0.2f;
        [Tooltip("Margin (s) by which the ball must win the race to read fully open.")]
        [SerializeField] private float _openWindow = 0.45f;

        [Header("Appearance")]
        [SerializeField] private float _thickness = 0.06f;
        [Range(0f, 1f)] [SerializeField] private float _alpha = 0.75f;
        [Tooltip("Above the pitch and players, below the intent overlay (15).")]
        [SerializeField] private int _sortingOrder = 13;
        [SerializeField] private bool _visibleOnStart = false;
        [SerializeField] private bool _enablePassLanes = true;

        /// <summary>
        /// Overlay visibility, static so a settings screen or chip row can drive it
        /// without a reference. Re-seeded from the serialized default in Start.
        /// </summary>
        public static bool Visible { get; set; }

        const int MAX_OPPONENTS = 8;

        Agent_EnvController _env;
        Agent_Lines _lines;
        readonly Vector2[] _opponents = new Vector2[MAX_OPPONENTS];

        /// <summary>The carrier found on the last drawn frame, or null. Read by the tests.</summary>
        public Agent_Soccer Carrier { get; private set; }

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enablePassLanes || Agent_Presentation.IsHeadless || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            Visible = _visibleOnStart;
            _lines = new Agent_Lines("PassLaneOverlay", _sortingOrder, vertexCapacity: 512);
        }

        void OnDestroy()
        {
            if (_lines != null) _lines.Dispose();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.pKey.wasPressedThisFrame) Visible = !Visible;

            if (_lines == null || _env == null) return;

            _lines.Visible = Visible;
            if (!Visible) return;

            _lines.Begin();
            Carrier = FindCarrier();
            if (Carrier != null) DrawLanes(Carrier);
            _lines.Commit();
        }

        Agent_Soccer FindCarrier()
        {
            if (_env.Ball == null) return null;
            Vector2 ball = _env.Ball.position;

            Agent_Soccer best = null;
            float bestDistance = _controlRadius;
            var agents = _env.agents;
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var agent = agents[agentIndex];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;
                float distance = (agent.Body.position - ball).magnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = agent;
                }
            }
            return best;
        }

        void DrawLanes(Agent_Soccer carrier)
        {
            // The ball, not the carrier's centre: that is where the pass starts.
            Vector2 from = _env.Ball.position;

            int opponentCount = 0;
            var agents = _env.agents;
            for (int agentIndex = 0; agentIndex < agents.Count && opponentCount < MAX_OPPONENTS; agentIndex++)
            {
                var agent = agents[agentIndex];
                if (agent == null || agent.Body == null || !agent.isActiveAndEnabled) continue;
                if (agent.team == carrier.team) continue;
                _opponents[opponentCount++] = agent.Body.position;
            }

            // Teammates.
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var mate = agents[agentIndex];
                if (mate == null || mate == carrier || mate.Body == null || !mate.isActiveAndEnabled) continue;
                if (mate.team != carrier.team) continue;

                Vector2 to = mate.Body.position;
                float openness = Agent_PassLaneMath.Openness(from, to, _opponents, opponentCount,
                    _ballSpeed, _runnerSpeed, _reach, _reaction, _openWindow);
                Color tint = Agent_PassLaneMath.Tint(openness, _alpha);
                Color faded = new(tint.r, tint.g, tint.b, _alpha * 0.25f);

                // Thicker when open, so the eye lands on the pass that is actually on.
                float width = _thickness * (0.6f + 0.8f * openness);
                _lines.AddFadedSegment(from, to, width, faded, tint);
                _lines.AddDiamond(to, 0.22f, tint);
            }

            // The shot lane, to the centre of the goal being attacked.
            Vector2 goal = _env.GetGoalPosition(Agent_Soccer.Opponent(carrier.team));
            Vector2 toGoal = goal - from;
            float distance = toGoal.magnitude;
            if (distance > 0.5f)
            {
                float shotOpenness = Agent_PassLaneMath.Openness(from, goal, _opponents, opponentCount,
                    _ballSpeed * 1.4f, _runnerSpeed, _reach, _reaction, _openWindow);
                Color shotTint = Agent_PassLaneMath.Tint(shotOpenness, _alpha * 0.8f);
                _lines.AddArrow(from, toGoal / distance, distance, _thickness * 0.7f, shotTint);
            }
        }
    }
}
