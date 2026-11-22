using UnityEngine;
using UnityEngine.InputSystem;

namespace PoSoccer
{
    /// <summary>
    /// A ring at each player's feet showing the stamina tank: how much is left,
    /// how much the match has worn away for good, and when fatigue has started to
    /// cost speed.
    ///
    /// WHY. Agent_Stamina is one of the few things in this simulation that changes
    /// what a body CAN do: boost needs stamina, and an empty tank scales drive power
    /// down toward Agent_Soccer's tired floor. A brain that sprints early and then
    /// cannot chase late looks, without this, exactly like one that stopped trying.
    /// It is also the closest thing this top-down body has to a strain gauge.
    ///
    /// The ring's full circle is the FRESH tank (maxStamina). Read clockwise from the
    /// top:
    ///  - the bright arc is what is in the tank now;
    ///  - the dim arc after it is room the player can still recover into;
    ///  - the dark red arc at the end is capacity lost to wear (Agent_Stamina.Wear),
    ///    which never comes back within a match.
    /// The ring pulses red once the tank is below <see cref="_limitingBelow"/> or
    /// empty, where boost is gone and drive power is visibly down.
    ///
    /// A full, unworn tank draws nothing, so a fresh pitch stays clean.
    ///
    /// Drawn under the bodies, one draw call (<see cref="Agent_Lines"/>), zero
    /// allocation per frame. Toggle with H or <see cref="Visible"/>.
    /// Presentation only: self-disables in training, evaluation and headless players.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_StaminaRing : MonoBehaviour
    {
        [Tooltip("Ring radius as a multiple of the body radius.")]
        [SerializeField] private float _ringScale = 1.3f;
        [Tooltip("Nominal body radius before per-profile scaling.")]
        [SerializeField] private float _bodyRadius = 0.4f;
        [SerializeField] private float _thickness = 0.05f;
        [Tooltip("Tank fraction below which fatigue is treated as limiting and the ring pulses.")]
        [Range(0f, 1f)] [SerializeField] private float _limitingBelow = 0.3f;
        [Tooltip("Pulses per second while limiting.")]
        [SerializeField] private float _pulseRate = 2.5f;
        [Tooltip("Under the bodies (0) and above the pitch (-10) and shadows (-2).")]
        [SerializeField] private int _sortingOrder = -1;
        [SerializeField] private bool _visibleOnStart = true;
        [SerializeField] private bool _enableStaminaRing = true;

        /// <summary>Overlay visibility, static so a settings screen can drive it.</summary>
        public static bool Visible { get; set; } = true;

        const int SEGMENTS_PER_TURN = 32;

        static readonly Color FillTint = new(1f, 1f, 1f, 0.85f);
        static readonly Color RoomTint = new(1f, 1f, 1f, 0.18f);
        static readonly Color WearTint = new(0.55f, 0.08f, 0.08f, 0.6f);
        static readonly Color LimitTint = new(1f, 0.25f, 0.2f, 0.95f);

        Agent_EnvController _env;
        Agent_Lines _lines;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enableStaminaRing || Agent_Presentation.IsHeadless || !Agent_Presentation.IsVisualScene(hud))
            {
                enabled = false;
                return;
            }

            Visible = _visibleOnStart;
            _lines = new Agent_Lines("StaminaRingOverlay", _sortingOrder, vertexCapacity: 1024);
        }

        void OnDestroy()
        {
            if (_lines != null) _lines.Dispose();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.hKey.wasPressedThisFrame) Visible = !Visible;

            if (_lines == null || _env == null) return;

            _lines.Visible = Visible;
            if (!Visible) return;

            _lines.Begin();
            var agents = _env.agents;
            for (int agentIndex = 0; agentIndex < agents.Count; agentIndex++)
            {
                var agent = agents[agentIndex];
                if (agent == null || agent.Body == null || agent.Stamina == null || !agent.isActiveAndEnabled) continue;
                DrawRing(agent);
            }
            _lines.Commit();
        }

        void DrawRing(Agent_Soccer agent)
        {
            var stamina = agent.Stamina;
            float tank = TankShare(stamina.Current, stamina.maxStamina);
            float capacity = TankShare(stamina.EffectiveMax, stamina.maxStamina);
            if (!ShouldDraw(tank, capacity)) return;

            Vector2 centre = agent.Body.position;
            float radius = _bodyRadius * Mathf.Max(0.1f, agent.transform.localScale.x) * _ringScale;

            bool limiting = IsLimiting(stamina.Ratio, stamina.HasStamina, _limitingBelow);
            Color fill = FillTint;
            if (limiting)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * _pulseRate * Mathf.PI * 2f);
                fill = Color.Lerp(FillTint, LimitTint, 0.5f + 0.5f * pulse);
            }

            AddArcRange(centre, radius, 0f, tank, fill);
            AddArcRange(centre, radius, tank, capacity, RoomTint);
            AddArcRange(centre, radius, capacity, 1f, WearTint);
        }

        /// <summary>Fraction of the fresh tank, 0..1.</summary>
        public static float TankShare(float amount, float maxStamina) =>
            maxStamina > 0f ? Mathf.Clamp01(amount / maxStamina) : 0f;

        /// <summary>A full, unworn tank is not worth a mark on the pitch.</summary>
        public static bool ShouldDraw(float tankShare, float capacityShare) =>
            tankShare < 0.995f || capacityShare < 0.995f;

        /// <summary>
        /// Fatigue is limiting once boost is gone (an empty tank) or the tank, measured
        /// against what the player can currently hold, is below the threshold.
        /// </summary>
        public static bool IsLimiting(float ratio, bool hasStamina, float threshold) =>
            !hasStamina || ratio < threshold;

        /// <summary>Arc from fraction <paramref name="from"/> to <paramref name="to"/> of a turn, clockwise from the top.</summary>
        void AddArcRange(Vector2 centre, float radius, float from, float to, Color tint)
        {
            if (to - from <= 0.002f) return;

            int segments = Mathf.Max(1, Mathf.CeilToInt(SEGMENTS_PER_TURN * (to - from)));
            float start = Mathf.PI * 0.5f - from * Mathf.PI * 2f;
            float step = (to - from) * Mathf.PI * 2f / segments;
            for (int segmentIndex = 0; segmentIndex < segments; segmentIndex++)
            {
                float a0 = start - segmentIndex * step;
                float a1 = start - (segmentIndex + 1) * step;
                Vector2 p0 = centre + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
                Vector2 p1 = centre + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
                _lines.AddSegment(p0, p1, _thickness, tint);
            }
        }
    }
}
