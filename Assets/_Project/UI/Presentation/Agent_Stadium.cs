using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PoSoccer
{
    /// <summary>
    /// Runtime stadium dressing: URP 2D lighting (global fill, corner floodlights,
    /// goal glows), lit sprite materials, and a post-processing volume
    /// (bloom + goal-moment chromatic pulse). Everything is built in code; the
    /// scene only carries this component.
    ///
    /// 2026-08-05: 2D shadows and the vignette were removed. Shadows were the
    /// single largest GPU cost here — six shadow-casting point lights plus a
    /// ShadowCaster2D on every wall and agent — and on a flat top-down pitch they
    /// added almost nothing readable. The vignette darkened the edges of an
    /// already-small portrait view, working against readability rather than for
    /// it. Bloom, the lights and the goal pulse carry the look on their own.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class Agent_Stadium : MonoBehaviour
    {
        public static Agent_Stadium Instance { get; private set; }

        [Tooltip("Sprite-Lit material applied to every SpriteRenderer so 2D lights affect them.")]
        public Material litMaterial;
        [Range(0f, 1f)] public float globalFill = 0.5f;

        [Tooltip("Seconds the floodlights strobe in the scoring team's colour after a goal.")]
        [SerializeField] private float _celebrationSeconds = 2.2f;
        [Tooltip("Strobe cycles per second during a goal celebration.")]
        [SerializeField] private float _celebrationHz = 6f;

        [Tooltip("Neutral is the safe broadcast look; ACES is punchier but crushes saturated team colours.")]
        [SerializeField] private TonemappingMode _tonemapping = TonemappingMode.Neutral;

        [Header("Follow spot")]
        [Tooltip("One spot that eases toward whoever touched the ball last. Match scene only.")]
        [SerializeField] private bool _followSpot = true;
        [Range(0f, 2f)] [SerializeField] private float _spotIntensity = 0.45f;
        [SerializeField] private float _spotRadius = 3.2f;
        [SerializeField] private float _spotFollowRate = 6f;
        [Range(0f, 4f)] [SerializeField] private float _spotGoalBoost = 1.5f;
        [SerializeField] private float _spotGoalSeconds = 0.7f;

        Bloom _bloom;
        ChromaticAberration _chroma;
        ColorAdjustments _grading;
        SplitToning _splitToning;
        float _pulse;
        Transform _shadowRimRoot;
        Material _shadowRimMaterial;

        // Floodlights are captured so a goal can recolour them; each keeps its
        // authored colour/intensity so the celebration can hand them back exactly.
        readonly List<Light2D> _floodlights = new();
        readonly List<Color> _floodBaseColor = new();
        readonly List<float> _floodBaseIntensity = new();
        float _celebrate;
        Color _celebrateColor = Color.white;

        // Follow spot. Null everywhere except a live match scene - see Start.
        static readonly Color SpotRestColor = new(1f, 0.97f, 0.88f);
        Agent_EnvController _env;
        Light2D _spot;
        float _spotPresence;     // 0 = nobody to follow, 1 = locked on
        float _spotPulse;        // 1 at the instant of a goal, decays to 0
        Color _spotPulseColor = Color.white;

        /// <summary>
        /// Off = the follow spot is switched off and costs nothing. Defaults on.
        ///
        /// It is a CONTINUOUS cost - one more point light in the 2D light pass,
        /// every frame - so by Agent_Quality's own ordering (continuous cost is
        /// spent before momentary meaning) it belongs with the things shed early.
        /// Exposed as a property rather than wired here because the tier ladder
        /// lives in Agent_Quality.Apply.
        /// </summary>
        public bool FollowSpotEnabled { get; set; } = true;

        void Awake()
        {
            // Headless training/eval builds no stadium. Every light, shadow caster
            // and post-process volume below exists to be photographed, and the
            // FindObjectsByType sweep underneath runs across sixteen cloned pitches
            // to set a material on renderers that are never rendered. Instance is
            // deliberately left null so anything that reaches for the stadium in a
            // headless run gets the same "not present" answer it already handles.
            if (Agent_Presentation.IsHeadless)
            {
                enabled = false;
                return;
            }

            Instance = this;

            if (litMaterial != null)
                foreach (var sr in FindObjectsByType<SpriteRenderer>())
                    sr.sharedMaterial = litMaterial;

            // Lights: GlobalFill + 2 opposing Floodlights (reduced from 7 permanent lights for mobile/WebGL fill-rate).
            MakeLight("GlobalFill", Light2D.LightType.Global, Vector2.zero,
                new Color(0.8f, 0.85f, 0.95f), globalFill, 0f);
            foreach (var c in new[] { new Vector2(0f, -8.5f), new Vector2(0f, 8.5f) })
                MakeLight("Floodlight", Light2D.LightType.Point, c,
                    new Color(1f, 0.96f, 0.82f), 1.05f, 15f);

            BuildPostVolume();
        }

        /// <summary>
        /// The follow spot is built in Start, not with the other lights in Awake,
        /// and behind a harder gate than they are.
        ///
        /// Awake's IsHeadless check only asks whether this process has a screen.
        /// The spot is a GLOBAL - one light, one target - so it needs the
        /// IsMatchScene answer instead: no trainer attached, not an evaluation,
        /// and not the gallery, where there are six pitches and one stadium and
        /// "the last toucher" would mean whichever env FindAnyObjectByType
        /// happened to return. That predicate reads the Academy, which is why it
        /// waits for Start. A disabled component gets no Start, so the headless
        /// exit in Awake still covers this and nothing here can exist across
        /// Agent_TrainingGrid's sixteen cloned pitches.
        /// </summary>
        void Start()
        {
            if (!_followSpot) return;

            var hud = FindAnyObjectByType<Agent_HUD>();
            if (!Agent_Presentation.IsMatchScene(hud)) return;

            _env = FindAnyObjectByType<Agent_EnvController>();
            if (_env == null) return;

            _spot = MakeLight("FollowSpot", Light2D.LightType.Point, Vector2.zero,
                SpotRestColor, 0f, Mathf.Max(0.5f, _spotRadius));
            // Dark until there is somebody to follow: a disabled Light2D is not
            // drawn at all, where one at intensity 0 still takes its pass.
            _spot.enabled = false;

            _env.EpisodeEnded += OnEpisodeEnded;
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            if (_shadowRimMaterial != null) Destroy(_shadowRimMaterial);
        }

        /// <summary>
        /// Goal: the spot flares in the SCORING team's colour, on the scorer.
        /// EpisodeEnded fires before ResetPitch, so LastToucher is still the
        /// player who put it in and the spot is already sitting on them. The
        /// colour comes from Agent_Palette so the colour-vision modes move it
        /// with everything else.
        /// </summary>
        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (winner == null) return;
            _spotPulseColor = Agent_Palette.Team(winner.Value);
            _spotPulse = 1f;
        }

        Light2D MakeLight(string label, Light2D.LightType type, Vector2 pos,
            Color color, float intensity, float radius)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            var light = go.AddComponent<Light2D>();
            light.lightType = type;
            light.color = color;
            light.intensity = intensity;
            if (radius > 0f)
            {
                light.pointLightInnerRadius = radius * 0.15f;
                light.pointLightOuterRadius = radius;
            }

            if (label == "Floodlight")
            {
                _floodlights.Add(light);
                _floodBaseColor.Add(color);
                _floodBaseIntensity.Add(intensity);
            }

            return light;
        }

        void BuildPostVolume()
        {
            var go = new GameObject("PostFX");
            go.transform.SetParent(transform, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            // Beat the project's default volume profile, which sits at priority 0.
            volume.priority = 10f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();

            // TONEMAPPING IS THE WHOLE POINT OF THIS BLOCK. The project renders in
            // HDR, and PulseGoal drives bloom intensity to 2.5 on every goal, but
            // Assets/_Project/Data/Settings/DefaultVolumeProfile.asset explicitly overrides
            // Tonemapping to None. With no tonemapper, everything above 1.0 clamps,
            // so the celebration - the single most important frame in the game -
            // resolved to a flat white blob. Neutral maps the highlight rolloff
            // properly and costs one full-screen curve.
            var tonemapping = profile.Add<Tonemapping>();
            tonemapping.mode.Override(_tonemapping);

            _grading = profile.Add<ColorAdjustments>();
            _grading.postExposure.Override(0.15f);
            _grading.contrast.Override(14f);
            _grading.saturation.Override(12f);

            // Floodlit-stadium look: cool shadows against warm key light. Cheap,
            // and it stops the pitch green from reading as flat poster paint.
            _splitToning = profile.Add<SplitToning>();
            _splitToning.shadows.Override(new Color(0.35f, 0.45f, 0.75f));
            _splitToning.highlights.Override(new Color(1f, 0.93f, 0.72f));
            _splitToning.balance.Override(-15f);

            _bloom = profile.Add<Bloom>();
            _bloom.intensity.Override(0.9f);
            _bloom.threshold.Override(0.9f);
            _bloom.scatter.Override(0.72f);

            _chroma = profile.Add<ChromaticAberration>();
            _chroma.intensity.Override(0f);

            volume.profile = profile;
            SetWeatherCondition(Agent_Weather.CurrentCondition);
        }

        /// <summary>Subtle weather-responsive color grading and split toning.</summary>
        public void SetWeatherCondition(Agent_Weather.Condition condition)
        {
            if (_grading != null)
            {
                switch (condition)
                {
                    case Agent_Weather.Condition.Wet:
                        _grading.saturation.Override(16f);
                        _grading.contrast.Override(18f);
                        break;
                    case Agent_Weather.Condition.Muddy:
                        _grading.saturation.Override(4f);
                        _grading.contrast.Override(20f);
                        break;
                    case Agent_Weather.Condition.Snowy:
                        _grading.saturation.Override(-4f);
                        _grading.contrast.Override(16f);
                        break;
                    default:
                        _grading.saturation.Override(12f);
                        _grading.contrast.Override(14f);
                        break;
                }
            }

            if (_splitToning != null)
            {
                switch (condition)
                {
                    case Agent_Weather.Condition.Wet:
                        _splitToning.shadows.Override(new Color(0.28f, 0.48f, 0.82f));
                        _splitToning.highlights.Override(new Color(0.92f, 0.97f, 1f));
                        break;
                    case Agent_Weather.Condition.Muddy:
                        _splitToning.shadows.Override(new Color(0.38f, 0.34f, 0.30f));
                        _splitToning.highlights.Override(new Color(1f, 0.88f, 0.68f));
                        break;
                    case Agent_Weather.Condition.Snowy:
                        _splitToning.shadows.Override(new Color(0.45f, 0.62f, 0.88f));
                        _splitToning.highlights.Override(new Color(0.96f, 0.99f, 1f));
                        break;
                    default:
                        _splitToning.shadows.Override(new Color(0.35f, 0.45f, 0.75f));
                        _splitToning.highlights.Override(new Color(1f, 0.93f, 0.72f));
                        break;
                }
            }
        }

        /// <summary>
        /// Momentary exposure lift, used by the goal celebration so the whole
        /// image blooms rather than only the emissive pixels.
        /// </summary>
        public void SetExposureBoost(float stops)
        {
            if (_grading == null) return;
            _grading.postExposure.Override(0.15f + stops);
        }

        /// <summary>Goal-moment kick: bloom flash + chromatic aberration pulse.</summary>
        public void PulseGoal() => _pulse = 1f;

        /// <summary>High-xG shot micro-pulse: brief bloom and exposure lift.</summary>
        public void PulseChance(float xg)
        {
            if (xg < 0.28f) return;
            _pulse = Mathf.Max(_pulse, Mathf.Lerp(0.18f, 0.45f, Mathf.Clamp01((xg - 0.28f) / 0.45f)));
        }

        /// <summary>
        /// Full goal light show: the post pulse plus a strobe that throws the
        /// floodlights into the scoring team's colour and hands them back. Driven
        /// on unscaled time so it still plays while the goal replay has the
        /// game clock frozen (see Agent_TimeFreeze).
        /// </summary>
        public void CelebrateGoal(Color teamColor)
        {
            PulseGoal();
            _celebrateColor = teamColor;
            _celebrate = _celebrationSeconds;
        }

        /// <summary>
        /// Switches bloom off, and on again, without rebuilding the volume.
        ///
        /// Called by Agent_Quality as its last resort when the frame budget is
        /// breached. It toggles the OVERRIDE's active flag rather than the whole
        /// volume: the goal pulse also drives chromatic aberration and exposure
        /// through this same profile, and those are cheap - shedding them with
        /// bloom would remove the goal's punch to buy nothing.
        /// </summary>
        public void SetBloomEnabled(bool enabled)
        {
            if (_bloom == null) return;
            _bloom.active = enabled;
        }

        /// <summary>
        /// Desktop Ultra perimeter stadium shadow rim: soft unlit wall-edge shadow
        /// strips along the pitch perimeter without heavy ShadowCaster2D passes.
        /// </summary>
        public void SetCastShadows(bool enabled)
        {
            if (!enabled)
            {
                if (_shadowRimRoot != null) _shadowRimRoot.gameObject.SetActive(false);
                return;
            }

            if (_shadowRimRoot == null) BuildShadowRim();
            if (_shadowRimRoot != null) _shadowRimRoot.gameObject.SetActive(true);
        }

        void BuildShadowRim()
        {
            var env = _env != null ? _env : FindAnyObjectByType<Agent_EnvController>();
            if (env == null) return;

            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default");
            if (shader == null) return;

            _shadowRimMaterial = new Material(shader) { name = "PoSoccer_StadiumShadowRim" };
            var rootGo = new GameObject("StadiumShadowRim");
            rootGo.transform.SetParent(env.transform, false);
            _shadowRimRoot = rootGo.transform;

            Vector2 half = env.PitchHalfExtents;
            Sprite strip = Agent_Art.Square(1f);
            Color shade = new(0.02f, 0.04f, 0.06f, 0.26f);
            const float THICKNESS = 0.65f;

            AddShadowStrip("Rim_N", new Vector2(0f, half.y - THICKNESS * 0.5f), new Vector2(half.x * 2f, THICKNESS), strip, shade);
            AddShadowStrip("Rim_S", new Vector2(0f, -half.y + THICKNESS * 0.5f), new Vector2(half.x * 2f, THICKNESS), strip, shade);
            AddShadowStrip("Rim_E", new Vector2(half.x - THICKNESS * 0.5f, 0f), new Vector2(THICKNESS, half.y * 2f), strip, shade);
            AddShadowStrip("Rim_W", new Vector2(-half.x + THICKNESS * 0.5f, 0f), new Vector2(THICKNESS, half.y * 2f), strip, shade);
        }

        void AddShadowStrip(string label, Vector2 pos, Vector2 size, Sprite sprite, Color color)
        {
            var go = new GameObject(label);
            go.transform.SetParent(_shadowRimRoot, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = _shadowRimMaterial;
            sr.color = color;
            sr.sortingOrder = -5;
        }

        void Update()
        {
            if (_spot != null) TickFollowSpot();
            if (_celebrate > 0f) TickCelebration();

            if (_pulse <= 0f || _bloom == null) return;
            _pulse = Mathf.Max(0f, _pulse - Time.unscaledDeltaTime * 1.6f);
            _bloom.intensity.Override(0.9f + _pulse * 1.6f);
            _chroma.intensity.Override(_pulse * 0.45f);
            SetExposureBoost(_pulse * 0.5f);
        }

        /// <summary>
        /// Eases the spot onto the last toucher and plays out any goal pulse.
        ///
        /// Position moves on SCALED time, so the spot holds still with everything
        /// else through a replay freeze; the pulse decays on UNSCALED time for
        /// the same reason the floodlight strobe does - it has to play while the
        /// goal has the clock stopped. No toucher (kickoff, after a reset) fades
        /// the spot out rather than parking it on the centre circle.
        /// </summary>
        void TickFollowSpot()
        {
            if (!FollowSpotEnabled)
            {
                if (_spot.enabled) _spot.enabled = false;
                _spotPresence = 0f;
                _spotPulse = 0f;
                return;
            }

            float unscaled = Time.unscaledDeltaTime;
            _spotPulse = Mathf.Max(0f, _spotPulse - unscaled / Mathf.Max(0.05f, _spotGoalSeconds));

            Agent_Soccer target = _env != null ? _env.LastToucher : null;
            bool hasTarget = target != null;
            _spotPresence = Mathf.MoveTowards(_spotPresence, hasTarget ? 1f : 0f, unscaled * 2.5f);

            if (hasTarget)
            {
                Transform spot = _spot.transform;
                Vector3 from = spot.position;
                Vector3 to = target.transform.position;
                to.z = from.z;
                // Arriving from nothing: start ON the player rather than sweeping
                // in from wherever the previous possession ended.
                float blend = _spot.enabled
                    ? 1f - Mathf.Exp(-Mathf.Max(0.1f, _spotFollowRate) * Time.deltaTime)
                    : 1f;
                spot.position = Vector3.Lerp(from, to, blend);
            }

            // Squared so the pulse snaps up and tails off, like a lamp striking.
            float pulse = _spotPulse * _spotPulse;
            float intensity = _spotIntensity * _spotPresence + _spotGoalBoost * pulse;

            bool lit = intensity > 0.005f;
            if (_spot.enabled != lit) _spot.enabled = lit;
            if (!lit) return;

            _spot.intensity = intensity;
            _spot.color = Color.Lerp(SpotRestColor, _spotPulseColor, Mathf.Clamp01(pulse * 1.5f));
        }

        void TickCelebration()
        {
            _celebrate = Mathf.Max(0f, _celebrate - Time.unscaledDeltaTime);
            // Square-ish strobe that fades out, so the last cycles ease back to
            // the authored lighting instead of snapping.
            float envelope = Mathf.Clamp01(_celebrate / Mathf.Max(0.01f, _celebrationSeconds));
            float phase = Mathf.PingPong(_celebrate * _celebrationHz, 1f);
            float mix = phase * envelope;

            for (int i = 0; i < _floodlights.Count; i++)
            {
                var light = _floodlights[i];
                if (light == null) continue;
                light.color = Color.Lerp(_floodBaseColor[i], _celebrateColor, mix);
                light.intensity = _floodBaseIntensity[i] * (1f + mix * 0.9f);
            }

            if (_celebrate > 0f) return;

            // Exact restore - a drifting floodlight colour would accumulate over a match.
            for (int i = 0; i < _floodlights.Count; i++)
            {
                var light = _floodlights[i];
                if (light == null) continue;
                light.color = _floodBaseColor[i];
                light.intensity = _floodBaseIntensity[i];
            }
        }
    }
}
