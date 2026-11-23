using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Real-time diagnostic overlay: frame timing with percentiles, GC pressure,
    /// draw-call and geometry counts, memory, and PoSoccer-specific state.
    ///
    /// Built on ProfilerRecorder, which was installed in this project (Profiling
    /// Core, Memory Profiler, Profile Analyzer) and used exactly nowhere. Note
    /// that profiler stat NAMES drift between Unity versions, so every recorder
    /// is probed for Valid and silently dropped if this Unity does not publish it
    /// - the overlay degrades to fewer rows rather than throwing or, worse,
    /// printing zeroes that look like real measurements.
    ///
    /// Frame timing is measured directly from unscaledDeltaTime rather than from
    /// a recorder, because that is the number a player actually experiences and
    /// it is guaranteed to exist on every platform.
    ///
    /// COST WHEN HIDDEN IS ZERO: recorders are allocated on show and disposed on
    /// hide, and Update returns immediately while closed. Toggle with TAB (F3 is
    /// kept as an alias), Agent_Chrome's DEBUG button in the lower-left corner, or
    /// a three-finger tap on touch devices.
    ///
    /// 2026-10-10 - ONE HEALTH PANEL. The readout is a column of short rows -
    /// plain-language label, value, green/amber/red state, most useful first -
    /// followed by switches for the four overlays that used to be keyboard-only
    /// (I intent, V vision, T shot tracer, B brain cam) and the spectator
    /// playback speed. It fits one portrait screen and contains no ScrollView.
    ///
    /// 2026-09-05 - IT NOW ENFORCES BUDGETS AND WRITES A SESSION LOG. Reporting
    /// numbers is not the same as holding a line: every row that has a defensible
    /// budget carries one, breaches render in red and are counted, and a HOLD/OVER
    /// verdict sits at the top so the answer is legible without reading the table.
    ///
    /// The CSV matters more than the colours. This project's whole methodology is
    /// that a measurement nobody can re-read later becomes a claim nobody can
    /// falsify - the phase-10 retraction happened exactly that way. An overlay you
    /// can only read by squinting at a phone in your hand is that same trap in
    /// visual form, so every refresh is also a row, and the file is written to
    /// Application.persistentDataPath on hide and on quit. That is what makes an
    /// on-DEVICE session gradeable off-device, which is the only honest way to
    /// prove the sprite-atlas work actually paid off on the hardware that needed
    /// it rather than on a desktop that never had the problem.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class Agent_Telemetry : MonoBehaviour
    {
        [Tooltip("Show the overlay from the moment the scene loads.")]
        [SerializeField] private bool _visibleOnStart;
        [Tooltip("Frames retained for the percentile window.")]
        [SerializeField] private int _sampleWindow = 240;
        [Tooltip("Seconds between text refreshes. The samples are still collected every frame.")]
        [SerializeField] private float _refreshInterval = 0.25f;

        [Tooltip("Frame budget in milliseconds. 16.7 = 60 fps; raise to 33.3 to grade against 30 fps.")]
        [SerializeField] private float _frameBudgetMs = 16.7f;
        [Tooltip("Write a CSV of every refresh to persistentDataPath when the overlay is hidden or the app quits.")]
        [SerializeField] private bool _writeSessionCsv = true;
        [Tooltip("Maximum rows retained for the CSV. At 4 Hz this is about 40 minutes.")]
        [SerializeField] private int _maxCsvRows = 10000;

        readonly struct Stat
        {
            public readonly string Label;
            public readonly ProfilerCategory Category;
            public readonly string Name;
            public readonly float Scale;
            public readonly string Unit;
            /// <summary>Value above which this row is over budget. 0 = no budget.</summary>
            public readonly double Budget;

            public Stat(string label, ProfilerCategory category, string name, float scale,
                string unit, double budget = 0d)
            {
                Label = label;
                Category = category;
                Name = name;
                Scale = scale;
                Unit = unit;
                Budget = budget;
            }
        }

        // Budgets, and where each one comes from - a budget nobody can justify is
        // a number that gets raised the first time it goes red.
        //
        //   Draw calls / SetPass / Batches - .claude/rules/performance.md asks for
        //     the lowest count possible on a 2D mobile game. This scene draws a
        //     pitch, a backdrop, four players, a ball, two goals, the crowd
        //     tilemap, the ad boards and the runtime shapes. Post-atlas that is
        //     tens, not hundreds; 120 is deliberately loose enough that tripping
        //     it means something regressed rather than that the budget was tight.
        //   GC alloc/frame - the rule is ZERO allocation in Update/FixedUpdate.
        //     1 KB is the smallest threshold that does not fire on UI Toolkit's
        //     own per-frame churn, so anything above it is ours.
        //   System used - a 6-year-old midrange Android gives a game ~512 MB
        //     before the low-memory killer takes an interest.
        static readonly Stat[] Wanted =
        {
            new("Draw calls", ProfilerCategory.Render, "Draw Calls Count", 1f, "", 120),
            new("SetPass", ProfilerCategory.Render, "SetPass Calls Count", 1f, "", 60),
            new("Batches", ProfilerCategory.Render, "Batches Count", 1f, "", 120),
            new("Triangles", ProfilerCategory.Render, "Triangles Count", 1f, ""),
            new("Verts", ProfilerCategory.Render, "Vertices Count", 1f, ""),
            new("GC alloc/frame", ProfilerCategory.Memory, "GC Allocated In Frame", 1f / 1024f, " KB", 1.0),
            new("GC reserved", ProfilerCategory.Memory, "GC Reserved Memory", 1f / (1024f * 1024f), " MB"),
            new("System used", ProfilerCategory.Memory, "System Used Memory", 1f / (1024f * 1024f), " MB", 512),
            new("Audio voices", ProfilerCategory.Audio, "Playing Audio Sources", 1f, "", 24),
        };

        readonly List<ProfilerRecorder> _recorders = new();
        readonly List<Stat> _live = new();

        // -- Health panel ----------------------------------------------------
        //
        // 2026-10-10: THE OVERLAY IS NOW A PANEL OF ROWS, NOT ONE BLOCK OF TEXT.
        // The single Label read like a console dump: you had to know which of
        // fifteen lines mattered and what its budget was. Each row is now a
        // plain-language label, a value, and a traffic-light state, ordered most
        // useful first, with the overlay switches and the playback speed that
        // used to be keyboard-only underneath. It still costs nothing while
        // hidden, still honours POSOCCER_TELEMETRY=1, and still writes the same
        // CSV - the session log is unchanged, column for column.
        //
        // The frame-time sparkline is gone from the screen. It was the one part
        // that needed a wrapped monospace line to mean anything; its data is the
        // frame_ms / p95_ms / max_ms columns of the CSV, which is where a shape
        // over time is actually readable.

        const int STATE_NONE = 0;
        const int STATE_OK = 1;
        const int STATE_WARN = 2;
        const int STATE_BAD = 3;

        static readonly string[] StateClass = { null, "health--ok", "health--warn", "health--bad" };

        /// <summary>
        /// Player rows drawn before the list collapses into one "+N more" line.
        /// Sized to keep the panel fitting a 16:9 portrait screen without scrolling
        /// alongside the match stats section.
        /// </summary>
        const int MAX_PLAYER_ROWS = 4;

        static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };
        static readonly string[] SpeedText = { "0.25x", "0.5x", "1x", "2x", "4x" };

        static readonly string[] QualityText =
        {
            "tier 0 of 3 · all effects on",
            "tier 1 of 3 · pitch wear shed",
            "tier 2 of 3 · wear and bloom shed",
            "tier 3 of 3 · everything sheddable shed",
        };

        // Ultra (2026-11-07) only ever runs at tier 0; shown in place of tier 0's line.
        const string QUALITY_ULTRA_TEXT = "tier 0 of 3 · ULTRA (desktop extras on)";

        sealed class Row
        {
            public VisualElement Root;
            public Label Name;
            public Label Value;
            public string Shown;
            public int State = -1;
        }

        float[] _samples;
        float[] _sorted;
        int _sampleCount;
        int _sampleHead;
        float _nextRefresh;

        UIDocument _doc;
        VisualElement _panel;
        Label _title;
        bool _visible;
        System.Text.StringBuilder _builder;

        Row _frameRow, _qualityRow, _drawRow, _errorRow, _clockRow, _physicsRow, _ballRow;
        Row _modeSeedRow, _rlTelemetryRow;
        Row _fpsRow, _episodeRewardRow, _inferenceRow, _curriculumRow, _benchmarkRow;
        Button _benchmarkButton;
        Agent_SimBenchmark _benchmark;
        bool _tabPaused;
        Label _statsSection;
        Row _statsMatchRow, _statsModelRow, _statsFocusRow;
        readonly Row[] _playerRows = new Row[MAX_PLAYER_ROWS];
        Row _morePlayersRow;
        // One button per Agent_Overlays entry, rebuilt when the registry changes.
        VisualElement _overlayGrid;
        readonly List<Button> _overlayButtons = new();
        readonly List<string> _overlayShownText = new();
        int _overlayVersion = -1;
        readonly Button[] _speedButtons = new Button[Speeds.Length];
        Button _broadcastButton;
        bool _broadcastCached;

        Agent_HUD _hud;
        Agent_MatchStats _matchStats;
        Agent_WinProbability _winProb;
        readonly object _tabPauseToken = new Agent_TimeFreeze.Hold("Telemetry.Tab");

        int _drawIndex = -1, _batchIndex = -1;
        int _playerSignature;
        int _shownProblems = -1;
        int _shownErrors = -1, _shownDistinct = -1;
        float _shownTimeScale = -1f, _shownSpeed = -1f, _shownStepMs = -1f;
        int _shownQuality = -1;

        Agent_EnvController _env;
        int _threeFingerLatch;

        System.Text.StringBuilder _csv;
        int _csvRows;
        int _breachCount;
        float _worstFrameMs;

        /// <summary>Whether the panel is currently on screen. Read by
        /// Agent_Chrome's DEBUG button, which toggles rather than sets.</summary>
        public bool IsVisible => _visible;

        /// <summary>Shows or hides the panel. Profiler recorders exist only while shown.</summary>
        public void SetVisible(bool visible)
        {
            if (visible == _visible && (visible || _panel != null)) return;
            if (visible) Show();
            else Hide();
        }

        /// <summary>
        /// Set POSOCCER_TELEMETRY=1 to bring the overlay up from the first frame
        /// with no keypress.
        ///
        /// The overlay's whole value is the CSV, and the CSV is only written for a
        /// session that was VISIBLE - recorders are allocated on show, so an
        /// unattended run measures nothing. Every other unattended switch in this
        /// project is an environment variable read in Awake (POSOCCER_EVAL,
        /// POSOCCER_OPPONENT, POSOCCER_BOT_VISION), so this is the same door
        /// rather than a new one.
        /// </summary>
        public const string ENV_TELEMETRY = "POSOCCER_TELEMETRY";

        const string START_KEY = "posoccer_health_on_start";

        /// <summary>
        /// The player-facing twin of <see cref="ENV_TELEMETRY"/>: open the panel
        /// when a scene loads. Set from the settings sheet's Advanced tab, because
        /// an environment variable is not something you can set on a phone.
        /// Ignored in a headless process - a persisted preference from a desktop
        /// session must never put a UI panel and nine profiler recorders into a
        /// training run. The environment variable still can, on purpose.
        /// </summary>
        public static bool ShowOnStart
        {
            get => PlayerPrefs.GetInt(START_KEY, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(START_KEY, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        static bool TelemetryForcedOn()
        {
            string flag = System.Environment.GetEnvironmentVariable(ENV_TELEMETRY);
            return !string.IsNullOrEmpty(flag) && flag != "0";
        }

        void Start()
        {
            _env = FindAnyObjectByType<Agent_EnvController>();
            _samples = new float[Mathf.Max(30, _sampleWindow)];
            _builder = new System.Text.StringBuilder(256);
            bool preferred = ShowOnStart && !Agent_Presentation.IsHeadless;
            if (_visibleOnStart || TelemetryForcedOn() || preferred) Show();
        }

        void OnDestroy() => Hide();

        /// <summary>
        /// Android does not reliably call OnDestroy when the process is killed, so
        /// the session log is flushed here as well. Flush() is idempotent - it
        /// clears the buffer - so being called twice writes one file, not two.
        /// </summary>
        void OnApplicationQuit() => FlushCsv();

        void OnApplicationPause(bool paused)
        {
            if (paused) FlushCsv();
        }

        void Update()
        {
            if (ToggleRequested()) { if (_visible) Hide(); else Show(); }
            if (!_visible) return;

            // Sampled every frame; the percentile window is meaningless otherwise.
            _samples[_sampleHead] = Time.unscaledDeltaTime * 1000f;
            _sampleHead = (_sampleHead + 1) % _samples.Length;
            if (_sampleCount < _samples.Length) _sampleCount++;

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + _refreshInterval;
            Render();
        }

        bool ToggleRequested()
        {
            // TAB is the documented key; F3 is kept as an alias because it is what
            // this overlay answered to for its whole life before the panel.
            var keyboard = Keyboard.current;
            if (keyboard != null &&
                (keyboard.tabKey.wasPressedThisFrame || keyboard.f3Key.wasPressedThisFrame))
            {
                return true;
            }

            // Three fingers down is a gesture nothing in the game uses, so it
            // cannot be hit by accident during play. Agent_Chrome's DEBUG button
            // (lower-left) is the discoverable way in on a phone.
            var touch = Touchscreen.current;
            if (touch == null) return false;
            int down = 0;
            for (int i = 0; i < touch.touches.Count; i++)
                if (touch.touches[i].press.isPressed) down++;

            bool triggered = down >= 3 && _threeFingerLatch < 3;
            _threeFingerLatch = down;
            return triggered;
        }

        // -- Visibility ------------------------------------------------------

        void Show()
        {
            BuildOverlay();
            if (_panel == null) return;

            _drawIndex = -1;
            _batchIndex = -1;
            for (int i = 0; i < Wanted.Length; i++)
            {
                var recorder = ProfilerRecorder.StartNew(Wanted[i].Category, Wanted[i].Name);
                // Valid is only meaningful once started; an unpublished stat on
                // this Unity version simply drops out of the table.
                if (!recorder.Valid)
                {
                    recorder.Dispose();
                    continue;
                }
                // Indices 0 and 2 of Wanted are the two the panel shows. Looked up
                // by position in Wanted rather than by label so renaming a CSV
                // column cannot silently blank a row.
                if (i == WANTED_DRAW_CALLS) _drawIndex = _recorders.Count;
                if (i == WANTED_BATCHES) _batchIndex = _recorders.Count;
                _recorders.Add(recorder);
                _live.Add(Wanted[i]);
            }

            if (_env == null) _env = FindAnyObjectByType<Agent_EnvController>();

            _sampleCount = 0;
            _sampleHead = 0;
            _breachCount = 0;
            _worstFrameMs = 0f;
            _playerSignature = 0;
            _visible = true;
            _panel.style.display = DisplayStyle.Flex;
            _drawRow.Root.style.display = _drawIndex >= 0 ? DisplayStyle.Flex : DisplayStyle.None;

            if (_hud == null) _hud = FindAnyObjectByType<Agent_HUD>();
            if (_hud != null && _hud.enableMatchFlow && !Agent_Presentation.IsHeadless)
            {
                Agent_TimeFreeze.Acquire(_tabPauseToken);
                _tabPaused = true;
            }

            BeginCsv();
            // Render on the next Update, once there is a frame sample to grade.
            // Rendering here would open every session log with a row of zeroes.
            _nextRefresh = 0f;
        }

        /// <summary>Positions of the two displayed stats inside <see cref="Wanted"/>.</summary>
        const int WANTED_DRAW_CALLS = 0;
        const int WANTED_BATCHES = 2;

        void Hide()
        {
            // A benchmark puts the Tab pause back when it ends; end it first so the release sticks.
            if (_benchmark != null) _benchmark.Cancel();
            Agent_TimeFreeze.Release(_tabPauseToken);
            _tabPaused = false;
            FlushCsv();
            for (int i = 0; i < _recorders.Count; i++) _recorders[i].Dispose();
            _recorders.Clear();
            _live.Clear();
            _visible = false;
            if (_panel != null) _panel.style.display = DisplayStyle.None;
        }

        void BuildOverlay()
        {
            if (_panel != null) return;

            _doc = gameObject.GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();

            if (_doc.panelSettings == null)
            {
                // Share whatever panel the HUD already uses so scaling matches.
                //
                // Must scan ALL documents, not FindFirstObjectByType: that can
                // return the document this method just added to its own
                // GameObject, whereupon the `!= _doc` guard rejected the only
                // candidate it ever looked at, panelSettings stayed null, and the
                // overlay silently built nothing at all.
                var documents = FindObjectsByType<UIDocument>();
                for (int i = 0; i < documents.Length; i++)
                {
                    if (documents[i] == _doc || documents[i].panelSettings == null) continue;
                    _doc.panelSettings = documents[i].panelSettings;
                    break;
                }
            }
            if (_doc.panelSettings == null)
            {
                Debug.LogWarning("Agent_Telemetry: no PanelSettings available; overlay disabled.");
                return;
            }
            _doc.sortingOrder = 100;

            var root = _doc.rootVisualElement;
            if (root == null) return;

            Agent_UIStyle.ApplyTheme(root);
            root.pickingMode = PickingMode.Ignore;

            // Inside a safe-area container, in NORMAL FLOW. The old Label was
            // `position: absolute; top: 140`, and an absolute child resolves its
            // offsets against the containing block's PADDING box - so the safe-area
            // padding this container carries never reached it. Agent_Chrome records
            // the same defect on its corners.
            //
            // Both chrome bars are reserved, so the panel opens cleanly inside the
            // safe area without overlapping top controls or the bottom build stamp.
            var safe = new VisualElement();
            safe.style.flexGrow = 1;
            safe.style.justifyContent = Justify.FlexStart;
            safe.pickingMode = PickingMode.Ignore;
            Agent_UIStyle.BindSafeArea(safe, Agent_Chrome.BAR_HEIGHT, Agent_Chrome.BOTTOM_BAR_HEIGHT);
            root.Add(safe);

            _panel = new VisualElement { name = "health" };
            _panel.AddToClassList("telemetry");
            _panel.style.display = DisplayStyle.None;
            safe.Add(_panel);

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = Agent_UIStyle.Space1;

            _title = new Label("DEBUG & STATS   (TAB)");
            _title.AddToClassList("health__title");
            _title.style.flexShrink = 1;
            _title.style.marginBottom = 0;
            head.Add(_title);

            var close = new Button(Hide) { text = "CLOSE", name = "health-close" };
            close.AddToClassList("btn");
            close.style.minWidth = 150;
            close.style.paddingLeft = Agent_UIStyle.Space4;
            close.style.paddingRight = Agent_UIStyle.Space4;
            close.style.fontSize = Agent_UIStyle.FontXS;
            Agent_UIStyle.WireTapFeedback(close, 0.92f);
            head.Add(close);
            _panel.Add(head);

            // The Po Game Standard's overlay (C12) leads the panel: FPS, episode reward,
            // inference latency, curriculum stage.
            _fpsRow = AddRow("FPS");
            _episodeRewardRow = AddRow("Episode reward");
            _inferenceRow = AddRow("Inference latency");
            _curriculumRow = AddRow("Curriculum stage");
            _benchmarkRow = AddRow("Sim speed (benchmark)");
            var benchmarkButtons = ButtonRow();
            _benchmarkButton = PanelButton("BENCHMARK SIM SPEED", RunBenchmark);
            _benchmarkButton.name = "benchmark-training-speed";
            benchmarkButtons.Add(_benchmarkButton);
            _panel.Add(benchmarkButtons);
            _panel.Add(Section("HEALTH"));
            _frameRow = AddRow("Frame time (95th percentile)");
            _qualityRow = AddRow("Effects quality");
            _drawRow = AddRow("Draw calls · batches");
            _errorRow = AddRow("Errors logged");
            _clockRow = AddRow("Game clock");
            _physicsRow = AddRow("Physics step");
            _ballRow = AddRow("Ball · last touch");
            _modeSeedRow = AddRow("Mode · Seed · Camera");
            _rlTelemetryRow = AddRow("Reward · NaN/Inf");

            _statsSection = Section("MATCH STATS");
            _panel.Add(_statsSection);
            _statsMatchRow = AddRow("Possession · Top speed");
            _statsModelRow = AddRow("Win model · Ball peak");
            _statsFocusRow = AddRow("Focus / 1P telemetry");

            _panel.Add(Section("PLAYERS"));
            for (int i = 0; i < _playerRows.Length; i++) _playerRows[i] = AddRow(string.Empty);
            _morePlayersRow = AddRow(string.Empty);

            // The overlay switches come from Agent_Overlays (2026-11-07): every
            // overlay registered there gets a button here and in the settings
            // sheet's OVERLAYS tab, without editing this file.
            _panel.Add(Section("OVERLAYS"));
            _overlayGrid = new VisualElement { name = "health-overlays" };
            _panel.Add(_overlayGrid);
            RebuildOverlayButtons();

            _panel.Add(Section("PLAYBACK SPEED · HUD"));
            var speeds = ButtonRow();
            for (int i = 0; i < Speeds.Length; i++)
            {
                float speed = Speeds[i];
                _speedButtons[i] = PanelButton(SpeedText[i], () => SetSpeed(speed));
                speeds.Add(_speedButtons[i]);
            }
            _broadcastButton = PanelButton("STATS", ToggleBroadcastHud);
            _broadcastButton.name = "broadcast-toggle";
            Agent_UIStyle.WireTapFeedback(_broadcastButton, 1.04f);
            speeds.Add(_broadcastButton);
            _panel.Add(speeds);
        }

        Row AddRow(string name)
        {
            var row = new Row { Root = new VisualElement() };
            row.Root.AddToClassList("health__row");

            var dot = new VisualElement();
            dot.AddToClassList("health__dot");
            row.Root.Add(dot);

            row.Name = new Label(name);
            row.Name.AddToClassList("health__label");
            row.Root.Add(row.Name);

            row.Value = new Label();
            row.Value.AddToClassList("health__value");
            row.Root.Add(row.Value);

            _panel.Add(row.Root);
            return row;
        }

        static Label Section(string text)
        {
            var label = new Label(text);
            label.AddToClassList("health__section");
            return label;
        }

        static VisualElement ButtonRow()
        {
            var row = new VisualElement();
            row.AddToClassList("health__buttons");
            return row;
        }

        /// <summary>
        /// A .btn, so it inherits --touch-min like every other tappable control -
        /// Agent_PlayMode_Portrait asserts that on resolved layout, and a debug
        /// panel is not exempt from being used with a thumb.
        /// </summary>
        static Button PanelButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn");
            button.AddToClassList("health__btn");
            return button;
        }

        /// <summary>Writes a row's value and state, touching the element only on change.</summary>
        static void Set(Row row, string value, int state)
        {
            if (!string.Equals(row.Shown, value))
            {
                row.Shown = value;
                row.Value.text = value;
            }
            if (state == row.State) return;
            if (row.State > 0) row.Root.RemoveFromClassList(StateClass[row.State]);
            if (state > 0) row.Root.AddToClassList(StateClass[state]);
            row.State = state;
        }

        // -- Overlay switches --------------------------------------------------
        //
        // Each button calls Agent_Overlays.Entry.Advance, which writes the SAME
        // public member the overlay's own key handler writes (I / V / T / B / X /
        // G), so the keys keep working and the routes cannot disagree about the
        // state: there is one flag per overlay and the buttons re-read it on
        // every refresh.

        /// <summary>
        /// Buttons per row. Five, not four: the registry grows (other components
        /// add their overlays), and a third row would push the panel past a 16:9
        /// screen at the LARGER type scale. Labels are --font-xs, which fits
        /// "XG FIELD" in a fifth of the 1000-unit row.
        /// </summary>
        const int OVERLAYS_PER_ROW = 5;

        void RebuildOverlayButtons()
        {
            if (_overlayGrid == null) return;
            _overlayGrid.Clear();
            _overlayButtons.Clear();
            _overlayShownText.Clear();
            _overlayVersion = Agent_Overlays.Version;

            VisualElement row = null;
            for (int overlayIndex = 0; overlayIndex < Agent_Overlays.Count; overlayIndex++)
            {
                if (overlayIndex % OVERLAYS_PER_ROW == 0)
                {
                    row = ButtonRow();
                    _overlayGrid.Add(row);
                }
                var entry = Agent_Overlays.Get(overlayIndex);
                var button = PanelButton(entry.Text, entry.Advance);
                button.name = "health-overlay-" + entry.Id;
                row.Add(button);
                _overlayButtons.Add(button);
                _overlayShownText.Add(button.text);
            }
        }

        static void SetSpeed(float speed) => Agent_TimeFreeze.SpectatorSpeed = speed;

        /// <summary>BENCHMARK SIM SPEED: a few real seconds of this pitch at full speed (Agent_SimBenchmark).</summary>
        void RunBenchmark()
        {
            if (_benchmark == null) _benchmark = gameObject.AddComponent<Agent_SimBenchmark>();
            _benchmark.Begin(_tabPaused ? _tabPauseToken : null);
            _nextRefresh = 0f;
        }

        void ToggleBroadcastHud()
        {
            bool next = !Agent_HUD.BroadcastEnabled;
            Agent_HUD.BroadcastEnabled = next;
            if (_hud == null) _hud = FindAnyObjectByType<Agent_HUD>();
            if (_hud != null) _hud.SetBroadcastStripVisible(next);
            SyncSwitches();
        }

        void SyncSwitches()
        {
            if (_overlayVersion != Agent_Overlays.Version) RebuildOverlayButtons();
            for (int overlayIndex = 0; overlayIndex < _overlayButtons.Count; overlayIndex++)
            {
                var entry = Agent_Overlays.Get(overlayIndex);
                var button = _overlayButtons[overlayIndex];

                // Re-label only on change: the vision entry's text names its mode.
                string text = entry.Text;
                if (!string.Equals(text, _overlayShownText[overlayIndex]))
                {
                    _overlayShownText[overlayIndex] = text;
                    button.text = text;
                }

                // An overlay that cannot run here (the brain cam outside a match)
                // is shown disabled rather than hidden, so the rows keep their shape.
                bool available = entry.Available;
                button.SetEnabled(available);
                button.EnableInClassList(Agent_UIStyle.ON, available && entry.IsOn);
            }

            float current = Agent_TimeFreeze.SpectatorSpeed;
            for (int i = 0; i < Speeds.Length; i++)
            {
                _speedButtons[i].EnableInClassList(
                    Agent_UIStyle.ON, Mathf.Abs(current - Speeds[i]) < 0.001f);
            }

            if (_broadcastButton != null)
            {
                bool on = Agent_HUD.BroadcastEnabled;
                if (on != _broadcastCached)
                {
                    _broadcastCached = on;
                    _broadcastButton.text = on ? "STATS ON" : "STATS";
                }
                _broadcastButton.EnableInClassList(Agent_UIStyle.ON, on);
            }
        }

        // -- Readout ---------------------------------------------------------

        /// <summary>
        /// The frame budget, derived the way Agent_Quality derives its own: from
        /// what the app ASKED for. Grading a 30 fps target against a 60 fps budget
        /// would paint a device red for performing exactly as intended. The
        /// serialized field is the fallback for an uncapped target.
        /// </summary>
        float FrameBudgetMs => Application.targetFrameRate > 0
            ? 1000f / Application.targetFrameRate
            : _frameBudgetMs;

        /// <summary>
        /// Refreshes every row. Runs on the refresh timer (4 Hz by default), never
        /// per frame - which is what makes the string building below acceptable.
        /// Rows whose inputs have not changed are skipped before any string is
        /// built, so a healthy idle panel allocates for two rows, not for twenty.
        /// </summary>
        void Render()
        {
            float mean = 0f, worst = 0f;
            for (int i = 0; i < _sampleCount; i++)
            {
                float ms = _samples[i];
                mean += ms;
                if (ms > worst) worst = ms;
            }
            mean = _sampleCount > 0 ? mean / _sampleCount : 0f;
            float p95 = Percentile(0.95f);
            if (worst > _worstFrameMs) _worstFrameMs = worst;

            int problems = 0;
            bool breached = false;

            // p95, not mean, is graded against the frame budget. A mean inside
            // budget with a p95 outside it is a game that stutters, and the mean
            // is exactly the statistic that hides it. Amber up to the 1.25x margin
            // Agent_Quality itself tolerates before it sheds anything, red past it.
            float budget = FrameBudgetMs;
            int frameState = p95 <= budget ? STATE_OK : p95 <= budget * 1.25f ? STATE_WARN : STATE_BAD;
            if (frameState != STATE_OK) breached = true;
            if (frameState == STATE_BAD) problems++;
            _builder.Clear();
            _builder.Append(p95.ToString("0.0")).Append(" of ").Append(budget.ToString("0.0"))
                    .Append(" ms · ").Append((mean > 0.001f ? 1000f / mean : 0f).ToString("0"))
                    .Append(" fps");
            Set(_frameRow, _builder.ToString(), frameState);
            RenderStandard(mean);

            // What was DONE about a breach. Agent_Quality sheds effects when the
            // frame budget goes over, and a panel that reports the frame time
            // without the response would make a degraded frame look like a healthy
            // one - "inside budget" and "inside budget because the pitch wear is
            // switched off" are different facts.
            int tier = Mathf.Clamp(Agent_Quality.Tier, 0, QualityText.Length - 1);
            bool ultra = Agent_Quality.UltraActive && tier == 0;
            int qualityKey = ultra ? QualityText.Length : tier;   // cache key: tier, or one past for Ultra
            if (qualityKey != _shownQuality)
            {
                _shownQuality = qualityKey;
                Set(_qualityRow, ultra ? QUALITY_ULTRA_TEXT : QualityText[tier],
                    tier == 0 ? STATE_OK : tier < 3 ? STATE_WARN : STATE_BAD);
            }
            if (tier >= 3) problems++;

            if (_drawIndex >= 0)
            {
                double draws = _recorders[_drawIndex].LastValue * _live[_drawIndex].Scale;
                double drawBudget = _live[_drawIndex].Budget;
                int drawState = draws <= drawBudget ? STATE_OK
                              : draws <= drawBudget * 1.25d ? STATE_WARN : STATE_BAD;
                if (drawState != STATE_OK) breached = true;
                if (drawState == STATE_BAD) problems++;

                _builder.Clear();
                if (draws <= 0d)
                {
                    // A rendered frame always has at least one draw call. Zero means
                    // the counter did not report (seen in an unfocused editor), and a
                    // green "0" would read as a measurement.
                    Set(_drawRow, "not reported this frame", STATE_WARN);
                }
                else
                {
                _builder.Append(draws.ToString("0"));
                if (_batchIndex >= 0)
                {
                    double batches = _recorders[_batchIndex].LastValue * _live[_batchIndex].Scale;
                    _builder.Append(" · ").Append(batches.ToString("0"));
                }
                _builder.Append("  (budget ").Append(drawBudget.ToString("0")).Append(')');
                Set(_drawRow, _builder.ToString(), drawState);
                }
            }

            // The CSV's breach counter keeps its original meaning: a refresh in
            // which ANY budgeted stat was over, displayed or not.
            for (int i = 0; i < _live.Count && !breached; i++)
            {
                var stat = _live[i];
                if (stat.Budget > 0d && _recorders[i].LastValue * stat.Scale > stat.Budget) breached = true;
            }
            if (breached) _breachCount++;

            int errors = Agent_ErrorSink.TotalCount;
            int distinct = Agent_ErrorSink.Entries.Count;
            if (errors != _shownErrors || distinct != _shownDistinct)
            {
                _shownErrors = errors;
                _shownDistinct = distinct;
                if (errors == 0)
                {
                    Set(_errorRow, "none", STATE_OK);
                }
                else
                {
                    _builder.Clear();
                    _builder.Append(errors).Append(" logged · ").Append(distinct).Append(" distinct");
                    Set(_errorRow, _builder.ToString(), STATE_BAD);
                }
            }
            if (errors > 0) problems++;

            RenderClock();
            RenderPhysics();
            RenderMatchStats();
            RenderPlayers(ref problems);
            SyncSwitches();

            // The verdict, first thing a reader's eye lands on. A table of numbers
            // requires you to already know the budgets; this does not.
            if (problems != _shownProblems)
            {
                _shownProblems = problems;
                _title.text = problems == 0
                    ? "DEBUG & STATS · ALL OK   (TAB)"
                    : problems == 1 ? "DEBUG & STATS · 1 PROBLEM   (TAB)"
                    : "DEBUG & STATS · " + problems + " PROBLEMS   (TAB)";
            }

            AppendCsvRow(mean, p95, worst);
        }

        /// <summary>
        /// Distinguishes PAUSED (a full hold: replay, countdown, pause, end panel)
        /// from a changed playback rate. Both show a non-1 timeScale and only one
        /// of them is a bug when it persists, so the holders are NAMED - a leaked
        /// hold presents to the player as a hung game with nothing in the console.
        /// Amber rather than red: a hold is usually legitimate, and it is the name
        /// that tells you whether this one is.
        /// </summary>
        void RenderClock()
        {
            if (Agent_TimeFreeze.IsFrozen)
            {
                _shownTimeScale = -1f;
                _builder.Clear();
                _builder.Append("held by ").Append(Agent_TimeFreeze.DescribeHolders());
                Set(_clockRow, _builder.ToString(), STATE_WARN);
                return;
            }

            float scale = Time.timeScale;
            float speed = Agent_TimeFreeze.SpectatorSpeed;
            if (Mathf.Approximately(scale, _shownTimeScale) && Mathf.Approximately(speed, _shownSpeed)) return;
            _shownTimeScale = scale;
            _shownSpeed = speed;

            _builder.Clear();
            _builder.Append("running · time scale ").Append(scale.ToString("0.00"))
                    .Append(" · speed ").Append(speed.ToString("0.##")).Append('x');
            // Anything other than the spectator's own chosen rate is a hit-stop
            // dip in progress - expected for a moment, a leak if it stays.
            bool dipped = Mathf.Abs(scale - speed) > 0.001f;
            Set(_clockRow, _builder.ToString(), dipped ? STATE_WARN : STATE_OK);
        }

        void RenderPhysics()
        {
            // The project's contract is a 0.01 s fixed step; every trained brain
            // and every measured chassis number assumes it.
            float stepMs = Time.fixedDeltaTime * 1000f;
            if (!Mathf.Approximately(stepMs, _shownStepMs))
            {
                _shownStepMs = stepMs;
                _builder.Clear();
                _builder.Append(stepMs.ToString("0.#")).Append(" ms");
                bool expected = Mathf.Abs(stepMs - 10f) < 0.05f;
                if (!expected) _builder.Append("  (expected 10)");
                Set(_physicsRow, _builder.ToString(), expected ? STATE_OK : STATE_WARN);
            }

            if (_env == null || _env.Ball == null)
            {
                Set(_ballRow, "no pitch in this scene", STATE_NONE);
                return;
            }

            _builder.Clear();
            _builder.Append(_env.Ball.linearVelocity.magnitude.ToString("0.0")).Append(" m/s");
            var toucher = _env.LastToucher;
            if (toucher != null && toucher.Body != null)
            {
                _builder.Append(" · ")
                        .Append(toucher.rewards != null ? toucher.rewards.playerName : toucher.brainName)
                        .Append(' ').Append(toucher.Body.linearVelocity.magnitude.ToString("0.0"))
                        .Append(" m/s");
            }
            Set(_ballRow, _builder.ToString(), STATE_NONE);

            RenderModeAndReward();
        }

        void RenderMatchStats()
        {
            if (_hud == null) _hud = FindAnyObjectByType<Agent_HUD>();
            bool inMatch = _env != null && _hud != null && _hud.enableMatchFlow;
            var vis = inMatch ? DisplayStyle.Flex : DisplayStyle.None;
            if (_statsSection != null) _statsSection.style.display = vis;
            if (_statsMatchRow != null) _statsMatchRow.Root.style.display = vis;
            if (_statsModelRow != null) _statsModelRow.Root.style.display = vis;
            if (_statsFocusRow != null) _statsFocusRow.Root.style.display = vis;
            if (_broadcastButton != null) _broadcastButton.SetEnabled(inMatch);
            if (!inMatch) return;

            if (_matchStats == null) _matchStats = _env.GetComponent<Agent_MatchStats>();
            if (_winProb == null) _winProb = _env.GetComponent<Agent_WinProbability>();

            // Row 1: Possession & Top Speed leaders (measured)
            if (_matchStats != null)
            {
                Agent_Soccer possLeader = null, speedLeader = null;
                float bestPoss = -1f, bestSpeed = -1f;
                var roster = _env.agents;
                for (int i = 0; i < roster.Count; i++)
                {
                    var a = roster[i];
                    if (a == null || !_matchStats.TryGet(a, out var s)) continue;
                    if (s.PossessionShare > bestPoss) { bestPoss = s.PossessionShare; possLeader = a; }
                    if (s.TopSpeed > bestSpeed) { bestSpeed = s.TopSpeed; speedLeader = a; }
                }
                _builder.Clear();
                if (possLeader != null)
                {
                    _builder.Append(possLeader.rewards != null ? possLeader.rewards.playerName : possLeader.brainName)
                            .Append(' ').Append(Mathf.RoundToInt(bestPoss * 100f)).Append('%');
                }
                else _builder.Append("—");
                _builder.Append(" · top ");
                if (speedLeader != null)
                {
                    _builder.Append(speedLeader.rewards != null ? speedLeader.rewards.playerName : speedLeader.brainName)
                            .Append(' ').Append(bestSpeed.ToString("0.0")).Append(" m/s");
                }
                else _builder.Append("0.0 m/s");
                Set(_statsMatchRow, _builder.ToString(), STATE_NONE);
            }
            else
            {
                Set(_statsMatchRow, "no match stats", STATE_NONE);
            }

            // Row 2: Win chance (MODEL) + Ball peak speed
            _builder.Clear();
            int winPct = _winProb != null ? Mathf.RoundToInt(_winProb.BlueWinProbability * 100f) : 50;
            _builder.Append("BLUE ").Append(winPct).Append("% MODEL · ball ")
                    .Append(_matchStats != null ? _matchStats.BallTopSpeed.ToString("0.0") : "0.0").Append(" m/s");
            Set(_statsModelRow, _builder.ToString(), STATE_NONE);

            // Row 3: Focused / 1P / Last-touch telemetry
            Agent_Soccer focus = null;
            var spec = _env.GetComponent<Agent_SpectatorInput>();
            if (spec != null) focus = spec.FocusedAgent;
            if (focus == null && Agent_MatchSetup.HumanControl)
            {
                for (int i = 0; i < _env.agents.Count; i++)
                {
                    var a = _env.agents[i];
                    if (a != null && a.HumanControlled) { focus = a; break; }
                }
            }
            if (focus == null) focus = _env.LastToucher;
            if (focus == null && _env.agents.Count > 0) focus = _env.agents[0];
            if (focus != null && focus.Body != null)
            {
                _builder.Clear();
                _builder.Append(focus.rewards != null ? focus.rewards.playerName : focus.name)
                        .Append(" · ").Append(focus.Body.linearVelocity.magnitude.ToString("0.0")).Append(" m/s");
                if (focus.Stamina != null && focus.Stamina.maxStamina > 0f)
                {
                    int pct = Mathf.RoundToInt(focus.Stamina.Current / focus.Stamina.maxStamina * 100f);
                    _builder.Append(" · stam ").Append(pct).Append('%');
                }
                if (focus.IsBoosting) _builder.Append(" · BOOST");
                Set(_statsFocusRow, _builder.ToString(), STATE_NONE);
            }
            else
            {
                Set(_statsFocusRow, "—", STATE_NONE);
            }
        }

        /// <summary>FPS, episode reward, inference latency and curriculum stage (Po Game Standard C12).</summary>
        void RenderStandard(float meanFrameMs)
        {
            _builder.Clear();
            _builder.Append((meanFrameMs > 0.001f ? 1000f / meanFrameMs : 0f).ToString("0")).Append(" fps");
            Set(_fpsRow, _builder.ToString(), STATE_NONE);

            if (_env == null) _env = FindAnyObjectByType<Agent_EnvController>();
            Agent_Soccer agent = null;
            if (_env != null)
            {
                var spec = _env.GetComponent<Agent_SpectatorInput>();
                if (spec != null) agent = spec.FocusedAgent;
                for (int i = 0; agent == null && i < _env.agents.Count; i++)
                {
                    var a = _env.agents[i];
                    if (a != null && a.team == Agent_Soccer.Team.Blue) agent = a;
                }
            }
            _builder.Clear();
            if (agent != null)
            {
                _builder.Append(agent.GetCumulativeReward().ToString("+0.000;-0.000;0.000")).Append(" · ")
                        .Append(agent.rewards != null ? agent.rewards.playerName : agent.name);
            }
            else _builder.Append("no agent in this scene");
            Set(_episodeRewardRow, _builder.ToString(), STATE_NONE);

            double ms = Agent_InferenceClock.AverageMs;
            _builder.Clear();
            if (ms < 0d) _builder.Append("no decision yet");
            else _builder.Append(ms.ToString("0.00")).Append(" ms · mean of ").Append(Agent_InferenceClock.SampleCount)
                         .Append(" decisions");
            Set(_inferenceRow, _builder.ToString(), ms < 0d ? STATE_NONE : ms <= FrameBudgetMs ? STATE_OK : STATE_WARN);

            _builder.Clear();
            if (_env != null)
            {
                bool trainer = Unity.MLAgents.Academy.IsInitialized && Unity.MLAgents.Academy.Instance.IsCommunicatorOn;
                _builder.Append(trainer ? "trainer · " : "play (trainer defaults) · ")
                        .Append("goal ").Append(_env.CurrentGoalWidth.ToString("0.0")).Append(" m · bot ")
                        .Append(Mathf.RoundToInt(_env.CurrentBotStrength * 100f)).Append('%');
                if (_env.CurrentEpisodeSteps > 0) _builder.Append(" · ").Append(_env.CurrentEpisodeSteps).Append(" steps");
            }
            else _builder.Append("no pitch in this scene");
            Set(_curriculumRow, _builder.ToString(), STATE_NONE);

            string bench = _benchmark != null ? _benchmark.Result : string.Empty;
            if (string.IsNullOrEmpty(bench))
                bench = _env != null ? "press BENCHMARK SIM SPEED (a few seconds of this pitch at full speed)"
                                     : "start a match to benchmark";
            Set(_benchmarkRow, bench, STATE_NONE);
            if (_benchmarkButton != null) _benchmarkButton.SetEnabled(_benchmark == null || !_benchmark.IsRunning);
        }

        void RenderModeAndReward()
        {
            if (_modeSeedRow != null)
            {
                var cam = Camera.main;
                var follow = cam != null ? cam.GetComponent<Agent_CameraFollow>() : null;
                string camMode = follow != null ? follow.Mode.ToString() : "Broadcast";
                _builder.Clear();
                _builder.Append(Agent_MatchSetup.CurrentMode.ToString())
                        .Append(" · seed ")
                        .Append(Agent_EnvController.ActiveSeed.HasValue ? Agent_EnvController.ActiveSeed.Value.ToString() : "auto")
                        .Append(" · ")
                        .Append(camMode);
                Set(_modeSeedRow, _builder.ToString(), STATE_NONE);
            }

            if (_rlTelemetryRow != null)
            {
                float blueReward = 0f;
                if (_env != null)
                {
                    for (int i = 0; i < _env.agents.Count; i++)
                    {
                        var a = _env.agents[i];
                        if (a != null && a.team == Agent_Soccer.Team.Blue)
                        {
                            blueReward += a.GetCumulativeReward();
                            break;
                        }
                    }
                }
                int nanCount = Agent_Soccer.TotalNanInfCount;
                _builder.Clear();
                _builder.Append("rew ").Append(blueReward.ToString("0.00"))
                        .Append(" · NaN/Inf ").Append(nanCount);
                Set(_rlTelemetryRow, _builder.ToString(), nanCount == 0 ? STATE_OK : STATE_BAD);
            }
        }

        // -- Players -----------------------------------------------------------

        static readonly Dictionary<Unity.InferenceEngine.ModelAsset, int> ModelInputs = new();

        /// <summary>
        /// Inputs a model declares, or -1 if it cannot be read.
        ///
        /// The same arithmetic as Agent_Soccer.ModelFitsContract, which answers
        /// only yes/no and keeps its cache private. The panel needs the NUMBER:
        /// "178 of 188" says which contract the brain was trained on, where
        /// "incompatible" only says that something is wrong. Loaded once per model
        /// per process and only when the panel is opened - never in a hidden
        /// overlay, never in training.
        /// </summary>
        static int DeclaredInputs(Unity.InferenceEngine.ModelAsset model)
        {
            if (model == null) return -1;
            if (ModelInputs.TryGetValue(model, out int total)) return total;

            try
            {
                var loaded = Unity.InferenceEngine.ModelLoader.Load(model);
                total = 0;
                for (int inputIndex = 0; inputIndex < loaded.inputs.Count; inputIndex++)
                {
                    // Axis -1 is the feature count; axis 0 is the dynamic batch dim.
                    total += loaded.inputs[inputIndex].shape.Get(-1);
                }
            }
            catch (System.Exception)
            {
                // A stubbed LFS pointer or a corrupt asset. The row says so; a
                // diagnostic panel must never be what throws.
                total = -1;
            }

            ModelInputs[model] = total;
            return total;
        }

        /// <summary>
        /// Cheap fingerprint of "who is on the pitch and who drives them", so the
        /// player rows are rebuilt when the lineup changes and not four times a
        /// second. Object.GetHashCode is the instance id: no allocation.
        /// </summary>
        int PlayerSignature()
        {
            if (_env == null) return 1;
            var agents = _env.agents;
            int signature = 17 + agents.Count;
            for (int i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent == null) { signature = signature * 31 + 7; continue; }
                signature = signature * 31 + agent.GetHashCode();
                signature = signature * 31 + (agent.RuleBased ? 1 : 2);
                signature = signature * 31 + (agent.rewards != null ? agent.rewards.GetHashCode() : 3);
            }
            return signature;
        }

        int _playerProblems;

        void RenderPlayers(ref int problems)
        {
            int signature = PlayerSignature();
            if (signature == _playerSignature)
            {
                problems += _playerProblems;
                return;
            }
            _playerSignature = signature;
            _playerProblems = 0;

            int count = _env != null ? _env.agents.Count : 0;
            int runtimeInputs = Agent_Soccer.ExpectedModelInputSize;
            int shown = 0, hiddenBrains = 0, hiddenBots = 0, hiddenDropped = 0;

            for (int i = 0; i < count; i++)
            {
                var agent = _env.agents[i];
                if (agent == null) continue;

                var profile = agent.rewards;
                var model = profile != null ? profile.brainModel : null;
                int declared = DeclaredInputs(model);
                // "Dropped" is the case Agent_Soccer.DropIncompatibleModel handles:
                // the profile carries a brain, the runtime refused it, and the body
                // is being driven by the scripted bot instead.
                bool dropped = model != null && declared != runtimeInputs;
                if (dropped) _playerProblems++;

                if (shown >= MAX_PLAYER_ROWS)
                {
                    if (dropped) hiddenDropped++;
                    else if (agent.RuleBased) hiddenBots++;
                    else hiddenBrains++;
                    continue;
                }

                var row = _playerRows[shown++];
                row.Root.style.display = DisplayStyle.Flex;

                _builder.Clear();
                _builder.Append(agent.team == Agent_Soccer.Team.Blue ? "BLUE · " : "RED · ")
                        .Append(profile != null ? profile.playerName : agent.brainName)
                        .Append(agent.RuleBased ? " · BOT" : " · AI");
                row.Name.text = _builder.ToString();

                _builder.Clear();
                if (model == null)
                {
                    _builder.Append("scripted · no model");
                }
                else
                {
                    _builder.Append(string.IsNullOrEmpty(profile.trainingRunId)
                        ? "run unknown" : profile.trainingRunId);
                    _builder.Append(" · inputs ");
                    if (declared < 0) _builder.Append('?'); else _builder.Append(declared);
                    _builder.Append('/').Append(runtimeInputs);
                    if (dropped) _builder.Append(" · model dropped");
                }
                Set(row, _builder.ToString(),
                    dropped ? STATE_BAD : model == null ? STATE_NONE : STATE_OK);
            }

            for (int i = shown; i < _playerRows.Length; i++)
                _playerRows[i].Root.style.display = DisplayStyle.None;

            int hidden = hiddenBrains + hiddenBots + hiddenDropped;
            _morePlayersRow.Root.style.display = hidden > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (hidden > 0)
            {
                _builder.Clear();
                _builder.Append('+').Append(hidden).Append(" more");
                _morePlayersRow.Name.text = _builder.ToString();

                _builder.Clear();
                _builder.Append(hiddenBrains).Append(" AI · ").Append(hiddenBots).Append(" BOT");
                if (hiddenDropped > 0) _builder.Append(" · ").Append(hiddenDropped).Append(" dropped");
                Set(_morePlayersRow, _builder.ToString(), hiddenDropped > 0 ? STATE_BAD : STATE_NONE);
            }

            if (count == 0)
            {
                _playerRows[0].Root.style.display = DisplayStyle.Flex;
                _playerRows[0].Name.text = "no players in this scene";
                Set(_playerRows[0], string.Empty, STATE_NONE);
            }

            problems += _playerProblems;
        }

        // -- Session log -------------------------------------------------------

        void BeginCsv()
        {
            if (!_writeSessionCsv) return;

            _csv = new System.Text.StringBuilder(64 * 1024);
            _csvRows = 0;
            _csv.Append("t,frame_ms,p95_ms,max_ms");
            for (int i = 0; i < _live.Count; i++)
                _csv.Append(',').Append(_live[i].Label.Replace(' ', '_').Replace('/', '_'));
            _csv.Append(",time_scale,frozen,episode_step\n");
        }

        void AppendCsvRow(float mean, float p95, float worst)
        {
            if (_csv == null || _csvRows >= _maxCsvRows) return;

            _csv.Append(Time.unscaledTime.ToString("0.00")).Append(',')
                .Append(mean.ToString("0.000")).Append(',')
                .Append(p95.ToString("0.000")).Append(',')
                .Append(worst.ToString("0.000"));

            for (int i = 0; i < _live.Count; i++)
            {
                double value = _recorders[i].LastValue * _live[i].Scale;
                _csv.Append(',').Append(value.ToString("0.###"));
            }

            _csv.Append(',').Append(Time.timeScale.ToString("0.00"))
                .Append(',').Append(Agent_TimeFreeze.IsFrozen ? 1 : 0)
                .Append(',').Append(_env != null ? _env.StepCount : 0)
                .Append('\n');

            _csvRows++;
        }

        /// <summary>
        /// Write and clear. Clearing is what makes this safe to call from Hide,
        /// OnDestroy, OnApplicationQuit and OnApplicationPause - all four of which
        /// can fire for one session, and only the first should produce a file.
        /// </summary>
        void FlushCsv()
        {
            if (_csv == null || _csvRows == 0) { _csv = null; return; }

            string path = System.IO.Path.Combine(
                Application.persistentDataPath,
                $"posoccer-telemetry-{System.DateTime.Now:yyyyMMdd-HHmmss}.csv");

            try
            {
                System.IO.File.WriteAllText(path, _csv.ToString());
                Debug.Log($"Agent_Telemetry: wrote {_csvRows} rows to {path}");
            }
            catch (System.Exception exception)
            {
                // A telemetry overlay must never be the thing that crashes the
                // game it is measuring.
                Debug.LogWarning($"Agent_Telemetry: could not write the session log: {exception.Message}");
            }

            _csv = null;
            _csvRows = 0;
        }

        /// <summary>
        /// Percentile over the frame-time window. Copies and sorts only the
        /// populated part, and only on refresh (4x a second), never per frame -
        /// into a scratch buffer allocated once, where it used to allocate a fresh
        /// array on every refresh for as long as the panel stayed open.
        /// </summary>
        float Percentile(float fraction)
        {
            if (_sampleCount == 0) return 0f;
            if (_sorted == null || _sorted.Length != _samples.Length) _sorted = new float[_samples.Length];
            System.Array.Copy(_samples, _sorted, _sampleCount);
            System.Array.Sort(_sorted, 0, _sampleCount);
            int index = Mathf.Clamp(Mathf.RoundToInt(fraction * (_sampleCount - 1)), 0, _sampleCount - 1);
            return _sorted[index];
        }
    }
}
