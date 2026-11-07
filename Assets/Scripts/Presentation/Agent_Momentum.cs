using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The momentum timeline on the end panel: who was pressing when, how each
    /// side's expected goals built up, and where the goals fell.
    ///
    /// WHY. The scoreboard says 5-3; it does not say whether the loser was outplayed
    /// throughout or pinned the winner back for a minute and conceded twice on the
    /// break. For two brains that is the difference between "weaker" and "cannot
    /// defend a transition", and the timeline makes it visible at a glance.
    ///
    /// Two strips, one time axis:
    ///  - PRESSURE: <see cref="Agent_WinProbability.Threat"/>, signed by the side doing
    ///    the threatening, averaged per bucket. Blue bars rise, red bars hang.
    ///  - xG RACE: each side's running <see cref="Agent_ExpectedGoals"/> total.
    /// Goals are vertical ticks across both, in the scorer's colour.
    ///
    /// BOTH SIGNALS ARE HAND-SET MODELS and the card says MODEL, as every surface that
    /// shows either of them does. Nothing here is measured the way Agent_Dossier's
    /// occupancy is.
    ///
    /// No half-time: a match is first to 5. The card is built once, at full time.
    /// Sampling runs on the physics clock and stops while the clock is frozen, for
    /// the reason Agent_MatchStats gives. Memory is fixed whatever the match length -
    /// see <see cref="Agent_MomentumSeries"/>.
    ///
    /// Presentation only. Self-disables outside a match scene.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    public sealed class Agent_Momentum : MonoBehaviour
    {
        [Tooltip("Buckets kept. The width doubles whenever they fill, so any match length fits.")]
        [SerializeField] private int _capacity = 120;
        [Tooltip("Starting bucket width in seconds of match time.")]
        [SerializeField] private float _initialBucketSeconds = 1f;
        [Tooltip("Seconds of play below which no card is shown.")]
        [SerializeField] private float _minimumSeconds = 10f;
        [Header("Chart (pixels; the card scales it to the panel width)")]
        [SerializeField] private int _chartWidth = 480;
        [SerializeField] private int _pressureHeight = 120;
        [SerializeField] private int _xgHeight = 56;
        [SerializeField] private bool _enableMomentum = true;

        Agent_EnvController _env;
        Agent_HUD _hud;
        Agent_WinProbability _probability;
        Agent_ExpectedGoals _xg;
        Agent_MomentumSeries _series;
        Texture2D _texture;

        /// <summary>The recorded series. Read by the tests.</summary>
        public Agent_MomentumSeries Series => _series;

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();

            if (!_enableMomentum || !Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _probability = GetComponent<Agent_WinProbability>();
            _xg = GetComponent<Agent_ExpectedGoals>();
            _series = new Agent_MomentumSeries(_capacity, _initialBucketSeconds);

            _env.EpisodeEnded += OnEpisodeEnded;
            _hud.AddEndPanelSection(BuildCard, Agent_HUD.EndTab.Summary);
        }

        void OnDestroy()
        {
            if (_env != null) _env.EpisodeEnded -= OnEpisodeEnded;
            if (_hud != null) _hud.RemoveEndPanelSection(BuildCard);
            if (_texture != null)
            {
                if (Application.isPlaying) Destroy(_texture);
                else DestroyImmediate(_texture);
            }
        }

        void FixedUpdate()
        {
            if (_series == null || Agent_TimeFreeze.IsFrozen) return;
            if (_hud != null && _hud.MatchOver) return;

            float pressure = 0f;
            if (_probability != null && _probability.isActiveAndEnabled)
            {
                pressure = _probability.Threat *
                           (_probability.ThreatTeam == Agent_Soccer.Team.Blue ? 1f : -1f);
            }

            float blueXg = 0f, redXg = 0f;
            if (_xg != null && _xg.isActiveAndEnabled)
            {
                blueXg = _xg.TotalFor(Agent_Soccer.Team.Blue);
                redXg = _xg.TotalFor(Agent_Soccer.Team.Red);
            }

            _series.Sample(pressure, blueXg, redXg, Time.fixedDeltaTime);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            // A stalemate or a reset with no scorer is not a goal.
            if (_series == null || !winner.HasValue) return;
            _series.Goal((int)winner.Value);
        }

        // -- End-of-match card -----------------------------------------------

        VisualElement BuildCard()
        {
            if (_series == null || _series.TotalSeconds < _minimumSeconds) return null;
            _series.Flush();
            if (_series.Count < 2) return null;

            var card = new VisualElement();
            card.AddToClassList("card");

            var heading = new Label("MOMENTUM");
            heading.AddToClassList("card__heading");
            card.Add(heading);

            int width = Mathf.Max(64, _chartWidth);
            int height = Mathf.Max(16, _pressureHeight) + Mathf.Max(8, _xgHeight);

            var chart = new VisualElement();
            chart.style.width = Length.Percent(100f);
            // Keep the texture's aspect so bars are not stretched into a different story.
            chart.style.height = 900f * height / width;
            chart.style.marginTop = 8;
            chart.style.backgroundColor = new Color(1f, 1f, 1f, 0.04f);
            chart.style.backgroundImage = new StyleBackground(BuildTexture(width, height));
            Agent_UIStyle.Round(chart, 10);
            card.Add(chart);

            float blueShare = _series.BlueShare();
            float blueXg = _series.BlueXgAt(_series.Count - 1);
            float redXg = _series.RedXgAt(_series.Count - 1);
            var figures = new Label(
                $"on top: blue {blueShare * 100f:0}%  ·  red {(1f - blueShare) * 100f:0}%" +
                $"  ·  xG {blueXg:0.0} - {redXg:0.0}");
            figures.style.fontSize = Agent_UIStyle.FontXS;
            figures.style.color = Agent_UIStyle.TextMuted;
            figures.style.unityTextAlign = TextAnchor.MiddleCenter;
            figures.style.marginTop = 6;
            card.Add(figures);

            var caption = new Label(
                "top: pressure (MODEL threat) · bottom: xG race (MODEL) · ticks: goals");
            caption.AddToClassList("card__tile-caption");
            card.Add(caption);

            return card;
        }

        /// <summary>
        /// The chart, drawn into a texture once at full time. Row 0 is the bottom, so
        /// the xG strip occupies the low rows and the pressure strip the high ones.
        /// </summary>
        Texture2D BuildTexture(int width, int height)
        {
            if (_texture != null) Destroy(_texture);
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = "Momentum",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[width * height];
            Color blue = Agent_SoccerView.TeamColor(Agent_Soccer.Team.Blue);
            Color red = Agent_SoccerView.TeamColor(Agent_Soccer.Team.Red);
            Color32 axis = new Color(1f, 1f, 1f, 0.25f);

            int xgHeight = Mathf.Max(8, _xgHeight);
            int pressureBase = xgHeight;
            int pressureHeight = height - xgHeight;
            int mid = pressureBase + pressureHeight / 2;
            int halfBar = pressureHeight / 2 - 2;

            int count = _series.Count;
            float maxXg = 0.25f;
            for (int bucketIndex = 0; bucketIndex < count; bucketIndex++)
            {
                maxXg = Mathf.Max(maxXg, _series.BlueXgAt(bucketIndex), _series.RedXgAt(bucketIndex));
            }

            for (int x = 0; x < width; x++)
            {
                int bucket = Mathf.Clamp(x * count / width, 0, count - 1);

                // Pressure bar from the mid line.
                float pressure = _series.PressureAt(bucket);
                int bar = Mathf.RoundToInt(Mathf.Abs(pressure) * halfBar);
                Color tint = pressure >= 0f ? blue : red;
                tint.a = 0.85f;
                for (int step = 1; step <= bar; step++)
                {
                    int y = pressure >= 0f ? mid + step : mid - step;
                    if (y >= pressureBase && y < height) pixels[y * width + x] = tint;
                }
                pixels[mid * width + x] = axis;

                // xG race: two running totals on a shared scale.
                Plot(pixels, width, x, 2 + Mathf.RoundToInt(_series.BlueXgAt(bucket) / maxXg * (xgHeight - 5)), blue);
                Plot(pixels, width, x, 2 + Mathf.RoundToInt(_series.RedXgAt(bucket) / maxXg * (xgHeight - 5)), red);
                pixels[(xgHeight - 1) * width + x] = axis;
            }

            // Goals.
            float total = Mathf.Max(0.01f, _series.TotalSeconds);
            for (int goalIndex = 0; goalIndex < _series.GoalCount; goalIndex++)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt(_series.GoalTimeAt(goalIndex) / total * (width - 1)), 0, width - 1);
                Color tint = _series.GoalTeamAt(goalIndex) == (int)Agent_Soccer.Team.Blue ? blue : red;
                tint.a = 1f;
                for (int y = 0; y < height; y++)
                {
                    pixels[y * width + x] = tint;
                    if (x + 1 < width) pixels[y * width + x + 1] = tint;
                }
            }

            _texture.SetPixels32(pixels);
            _texture.Apply(updateMipmaps: false);
            return _texture;
        }

        /// <summary>A two-pixel-tall dot, so a flat line still reads at panel scale.</summary>
        static void Plot(Color32[] pixels, int width, int x, int y, Color tint)
        {
            int height = pixels.Length / width;
            tint.a = 1f;
            if (y >= 0 && y < height) pixels[y * width + x] = tint;
            if (y + 1 >= 0 && y + 1 < height) pixels[(y + 1) * width + x] = tint;
        }
    }
}
