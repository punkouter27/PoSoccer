using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Turns the frame budget from a REPORT into a CONTROL LOOP.
    ///
    /// Agent_Telemetry already knows when the game is over budget: it grades p95
    /// frame time against 16.7 ms, colours the breach red and counts it into a
    /// CSV. Nothing acted on any of that. On the device that actually needs it -
    /// a phone, where the overlay is a three-finger tap away and nobody is
    /// looking - the measurement existed and the response did not.
    ///
    /// WHY IT MEASURES ITS OWN FRAME TIME instead of reading the overlay's. The
    /// overlay allocates its ProfilerRecorders on show and disposes them on hide,
    /// and it is hidden by default; a controller that only works while a debug
    /// panel is open is a controller that never works. Frame time from
    /// unscaledDeltaTime needs no recorder, exists on every platform, and is the
    /// number the player actually feels - the same argument the overlay itself
    /// makes for grading on it.
    ///
    /// WHAT IT SHEDS, IN ORDER, AND WHY THAT ORDER. Cheapest thing to lose
    /// first, measured by cost-per-pixel rather than by taste:
    ///
    ///   1. PITCH WEAR. One full-pitch transparent quad of CONTINUOUS overdraw,
    ///      plus a texture upload a few times a second. Paid every frame for
    ///      something nobody watches directly.
    ///   2. BLOOM. A multi-pass post effect, also paid every frame.
    ///   3. IMPACT OVERLAYS. The goal flash and shockwave - LAST, because they
    ///      cost half a second at the one moment the whole match builds towards.
    ///      They were first until a probe caught a real goal being celebrated
    ///      with nothing at all on a machine that had quietly walked to the
    ///      bottom tier. Continuous cost is spent before momentary meaning.
    ///
    /// It is deliberately asymmetric and slow: three consecutive bad windows to
    /// step down, twelve consecutive good ones to step back up. A controller that
    /// recovers as eagerly as it degrades oscillates, and an oscillating quality
    /// setting is more distracting than the dropped frames it is chasing.
    ///
    /// ULTRA (2026-11-07) sits ABOVE tier 0, on desktop only. It buys back what the
    /// mobile budget gave up - floodlight cast shadows (Agent_Stadium), turf micro-
    /// detail (Agent_Surfaces), divot clods and double turf spray (Agent_ParticleFX)
    /// - and it is graded against a different bar: a sustained 30 fps FLOOR
    /// (<see cref="_ultraFloorFps"/>), not the app's target frame rate. A desktop
    /// that holds 40 fps with everything on keeps everything on; one that sinks
    /// under 30 sheds Ultra FIRST, before the wear, because Ultra is pure
    /// continuous cost. It is a flag rather than a tier -1 so <see cref="Tier"/>
    /// keeps its 0..3 meaning for the telemetry overlay and its CSV. Two shed
    /// events in one scene latch it off: re-arming forever would oscillate
    /// between 25 and 60 fps every half minute.
    ///
    /// Never on Android or WebGL: WebGL has no compute and a phone has no budget
    /// for six shadow-casting lights, so Ultra is unreachable there whatever the
    /// frame time says.
    /// </summary>
    [DefaultExecutionOrder(80)]
    public sealed class Agent_Quality : MonoBehaviour
    {
        [Tooltip("Fallback frame budget (ms) when the app sets no target frame rate. " +
                 "16.7 = 60 fps.")]
        [SerializeField] private float _budgetMs = 16.7f;
        [Tooltip("How far past the budget a window must sit before it counts as a breach. " +
                 "1.25 = 20.9 ms against a 60 fps target.")]
        [Range(1f, 2f)] [SerializeField] private float _breachMargin = 1.25f;
        [Tooltip("Frames per judged window. At 60 fps, 90 frames is ~1.5 s.")]
        [SerializeField] private int _window = 90;
        [Tooltip("Consecutive over-budget windows before dropping a tier.")]
        [SerializeField] private int _windowsToDrop = 3;
        [Tooltip("Consecutive comfortable windows before recovering a tier.")]
        [SerializeField] private int _windowsToRecover = 12;
        [Tooltip("Headroom required to recover: p95 must be under budget * this.")]
        [Range(0.5f, 1f)] [SerializeField] private float _recoverMargin = 0.8f;
        [Tooltip("Off = the tier never moves. The measurement still runs, so the " +
                 "telemetry line stays honest about what the frame time is doing.")]
        [SerializeField] private bool _adaptive = true;

        [Header("Ultra (desktop only)")]
        [Tooltip("Allow the desktop-only Ultra tier above tier 0.")]
        [SerializeField] private bool _allowUltra = true;
        [Tooltip("Ultra is kept while p95 frame time holds this frame rate or better.")]
        [SerializeField] private float _ultraFloorFps = 30f;
        [Tooltip("Shed events after which Ultra stays off for the rest of the scene.")]
        [SerializeField] private int _ultraMaxStrikes = 2;
        [Tooltip("Minimum graphics memory (MB) for Ultra. Integrated GPUs below this " +
                 "never reach it.")]
        [SerializeField] private int _ultraMinVramMb = 2048;

        float[] _samples;
        int _ultraStrikes;
        Agent_ParticleFX _particles;
        Agent_Surfaces _surfaces;
        float _effectiveBudgetMs = 16.7f;
        int _count;
        int _badWindows, _goodWindows;

        Agent_ScreenFX _screenFX;
        Agent_Wear _wear;
        Agent_Skids _skids;
        Agent_Stadium _stadium;

        /// <summary>
        /// 0 = everything on, 3 = everything sheddable shed. Static so the
        /// telemetry overlay can print it without holding a reference - there is
        /// exactly one of these, on the match pitch.
        /// </summary>
        public static int Tier { get; private set; }

        /// <summary>Windows judged over budget since load. Printed by the overlay.</summary>
        public static int Drops { get; private set; }

        /// <summary>Most recent judged p95, in ms. -1 before the first window closes.</summary>
        public static float LastP95 { get; private set; } = -1f;

        /// <summary>
        /// True while the desktop Ultra extras are on. Only ever true at tier 0.
        /// Static for the same reason as <see cref="Tier"/>.
        /// </summary>
        public static bool UltraActive { get; private set; }

        /// <summary>
        /// Whether this machine may run Ultra at all: a desktop player or the editor,
        /// not WebGL, with a discrete-class GPU. Pure, so a test can read it.
        /// </summary>
        public static bool UltraEligible(int minVramMb)
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer) return false;
            if (Application.isMobilePlatform) return false;
            if (SystemInfo.deviceType != DeviceType.Desktop) return false;
            return SystemInfo.graphicsMemorySize >= minVramMb;
        }

        void Start()
        {
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (!Agent_Presentation.IsMatchScene(hud))
            {
                enabled = false;
                return;
            }

            _samples = new float[Mathf.Max(30, _window)];

            // The budget is whatever the app ASKED FOR, not a constant. Both the
            // menu and Agent_Bootstrap set Application.targetFrameRate, and a
            // controller grading a 30 fps target against a 60 fps budget would
            // shed every effect on a device that is performing exactly as
            // intended.
            _effectiveBudgetMs = Application.targetFrameRate > 0
                ? 1000f / Application.targetFrameRate
                : _budgetMs;
            _screenFX = FindAnyObjectByType<Agent_ScreenFX>();
            _wear = FindAnyObjectByType<Agent_Wear>();
            _skids = FindAnyObjectByType<Agent_Skids>();
            _stadium = Agent_Stadium.Instance;
            _particles = FindAnyObjectByType<Agent_ParticleFX>();
            _surfaces = FindAnyObjectByType<Agent_Surfaces>();

            // A scene entered at a degraded tier from a previous match would be
            // dishonest about why it looks the way it does.
            Tier = 0;
            Drops = 0;
            LastP95 = -1f;
            _ultraStrikes = 0;
            UltraActive = _allowUltra && UltraEligible(_ultraMinVramMb);
            Apply();
        }

        /// <summary>
        /// The static flag would otherwise outlive the match it was measured in and
        /// report "+ULTRA" over the menu or the gallery.
        /// </summary>
        void OnDestroy()
        {
            UltraActive = false;
        }

        void Update()
        {
            // unscaledDeltaTime: the goal replay and the hit-stop both scale the
            // clock, and a controller reading scaled time would see a hit-stop as
            // a stutter and degrade the game for a deliberate effect.
            float ms = Time.unscaledDeltaTime * 1000f;
            _samples[_count++] = ms;
            if (_count < _samples.Length) return;

            Judge();
            _count = 0;
        }

        void Judge()
        {
            float p95 = Percentile95();
            LastP95 = p95;

            if (UltraActive)
            {
                JudgeUltra(p95);
                return;
            }

            // A breach has to CLEAR the margin. Ordinary jitter around the budget
            // is not trouble, and treating it as trouble is how the editor - which
            // never holds 16.7 ms - reached the bottom tier inside a minute and
            // celebrated a real goal with nothing at all. Measured, then fixed.
            if (p95 > _effectiveBudgetMs * _breachMargin)
            {
                Drops++;
                _goodWindows = 0;
                _badWindows++;
            }
            else if (p95 < _effectiveBudgetMs * _recoverMargin)
            {
                _badWindows = 0;
                _goodWindows++;
            }
            else
            {
                // In the dead band: neither a breach nor headroom. Hold, and let
                // neither counter run - this is the state a well-tuned tier sits
                // in, and treating it as "good" would ratchet the game back up
                // into the breach it just escaped.
                _badWindows = 0;
                _goodWindows = 0;
            }

            if (!_adaptive) return;

            if (_badWindows >= _windowsToDrop && Tier < 3)
            {
                _badWindows = 0;
                Tier++;
                Apply();
            }
            else if (_goodWindows >= _windowsToRecover && Tier > 0)
            {
                _goodWindows = 0;
                Tier--;
                Apply();
            }
            else if (_goodWindows >= _windowsToRecover && Tier == 0 && CanRearmUltra())
            {
                // Comfortable at tier 0 with headroom against the APP's budget: try
                // the extras again. JudgeUltra takes over from the next window.
                _goodWindows = 0;
                UltraActive = true;
                Apply();
            }
        }

        /// <summary>
        /// While Ultra is on, the only question is the 30 fps floor. The app's own
        /// target (usually 60) is deliberately NOT the bar here - Ultra exists to
        /// spend the headroom between the target and the floor - so the normal
        /// ladder is paused and its counters reused.
        /// </summary>
        void JudgeUltra(float p95)
        {
            float floorMs = 1000f / Mathf.Max(1f, _ultraFloorFps);
            if (p95 > floorMs)
            {
                Drops++;
                _goodWindows = 0;
                _badWindows++;
            }
            else
            {
                _badWindows = 0;
            }

            if (!_adaptive || _badWindows < _windowsToDrop) return;

            _badWindows = 0;
            _ultraStrikes++;
            UltraActive = false;
            Apply();
        }

        bool CanRearmUltra() =>
            _adaptive && _allowUltra && _ultraStrikes < _ultraMaxStrikes
            && UltraEligible(_ultraMinVramMb);

        /// <summary>
        /// p95 by selection over a copy-free partial sort.
        ///
        /// Insertion sort on a 90-element array of floats, once every 90 frames.
        /// That is ~2000 comparisons a second in the worst case against a budget
        /// of 60 frames each costing 16 ms - immaterial, and it avoids the
        /// allocation that System.Array.Sort on a subrange would still need for
        /// a comparer. Sorting in place is fine: the window is finished.
        /// </summary>
        float Percentile95()
        {
            int n = _samples.Length;
            for (int i = 1; i < n; i++)
            {
                float value = _samples[i];
                int j = i - 1;
                while (j >= 0 && _samples[j] > value)
                {
                    _samples[j + 1] = _samples[j];
                    j--;
                }
                _samples[j + 1] = value;
            }
            int index = Mathf.Clamp(Mathf.CeilToInt(0.95f * n) - 1, 0, n - 1);
            return _samples[index];
        }

        void Apply()
        {
            // ORDER CORRECTED 2026-09-06 AFTER WATCHING IT RUN, and the correction
            // is the whole reason to have watched. The goal flash and shockwave
            // used to be shed FIRST. A probe logged during a live match read
            // `winner=Red allow=False tier=3` - a real goal, celebrated with
            // nothing, because the editor never holds 16.7 ms and the controller
            // had walked to the bottom tier inside a minute.
            //
            // Continuous cost is spent before momentary meaning: the wear quad is
            // paid every frame, bloom is paid every frame, and the goal overlays
            // are paid for half a second at the one moment the whole match is
            // building towards. So they go last.
            if (_wear != null) _wear.Visible = Tier < 1;
            // The skid streaks are the same kind of cost as the wear: ground detail,
            // redrawn continuously. They go together.
            if (_skids != null) _skids.Allowed = Tier < 1;
            if (_stadium != null) _stadium.SetBloomEnabled(Tier < 2);
            // The follow spot is a continuous light, so it goes with the bloom.
            if (_stadium != null) _stadium.FollowSpotEnabled = Tier < 2;
            if (_screenFX != null) _screenFX.AllowContinuous = Tier < 3;

            // Divots are ground detail, like the wear and the skids: same tier.
            if (_particles != null) _particles.DivotsAllowed = Tier < 1;

            // Ultra extras. Ultra is only ever true at tier 0, but the AND keeps a
            // forced SetTier from leaving shadows on at a degraded tier.
            bool ultra = UltraActive && Tier == 0;
            if (_stadium != null) _stadium.SetCastShadows(ultra);
            if (_surfaces != null) _surfaces.TurfDetailEnabled = ultra;
            if (_particles != null) _particles.UltraBursts = ultra;
        }

        /// <summary>
        /// Forces a tier. For tests and for a settings screen; the adaptive loop
        /// keeps running afterwards and may move it again. Any tier above 0 also
        /// sheds Ultra, which is what a degraded tier means.
        /// </summary>
        public void SetTier(int tier)
        {
            Tier = Mathf.Clamp(tier, 0, 3);
            if (Tier > 0) UltraActive = false;
            Apply();
        }

        /// <summary>
        /// Forces Ultra on or off. For tests and a settings screen. Refused (stays
        /// off) on a machine that is not eligible, and only meaningful at tier 0.
        /// </summary>
        public void SetUltra(bool on)
        {
            UltraActive = on && Tier == 0 && UltraEligible(_ultraMinVramMb);
            Apply();
        }
    }
}
