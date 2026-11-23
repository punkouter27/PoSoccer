using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.SceneManagement;

namespace PoSoccer
{
    /// <summary>
    /// Demo (attract) mode, Po Game Standard C13. After <see cref="Agent_DemoModeSettings.idleSeconds"/>
    /// with no keyboard, mouse, touch or gamepad input:
    ///
    ///   - in a WATCH match (every slot already on its brain) it hides the HUD's control
    ///     band and the corner chrome and cycles the showcase cameras in place;
    ///   - on the menu, or in a 1-player / testbed match, it starts an all-AI exhibition
    ///     (two random characters from the pickable roster, every slot on its trained policy,
    ///     inference only) and runs it the same way, back to back.
    ///
    /// Any input leaves: an in-place demo restores the HUD, chrome and camera; a demo
    /// exhibition returns to the menu. A demo match that reaches full time updates Elo like
    /// any exhibition (Agent_TournamentMatch, 2026-11-22); one left early records nothing.
    ///
    /// Installed once per process from code (DontDestroyOnLoad), never in a scene, and not at
    /// all in batch mode, on a Null graphics device (training, eval, the headless test runners)
    /// or while a trainer is connected - an unattended run must never wander off to a demo.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class Agent_DemoMode : MonoBehaviour
    {
        /// <summary>Master switch (tests and tools may turn it off).</summary>
        public static bool Enabled = true;

        /// <summary>True while demo mode is showing.</summary>
        public static bool Active { get; private set; }

        /// <summary>True for a match that demo mode itself started (input returns to the menu).</summary>
        public static bool DemoMatch { get; private set; }

        /// <summary>Characters a demo exhibition may field. Agent_MainMenu fills it from its roster.</summary>
        public static Reward_Settings[] Pool;

        Agent_DemoModeSettings _settings;
        System.IDisposable _anyButton;
        bool _buttonPressed;
        float _idleSeconds;

        /// <summary>
        /// Longest single frame that counts toward the idle time. The WebGL player's first frame
        /// arrives after a ~70 s shader warm-up; counting that as idle started the demo the moment
        /// the page became playable, so a click meant for the menu only ended the demo.
        /// </summary>
        public const float MAX_IDLE_STEP = 0.25f;

        /// <summary>Idle time after one frame of <paramref name="frameSeconds"/> (pure, for tests).</summary>
        public static float AccumulateIdle(float idleSeconds, float frameSeconds) =>
            idleSeconds + Mathf.Clamp(frameSeconds, 0f, MAX_IDLE_STEP);
        float _nextShotAt;
        int _shotIndex;
        float _matchOverSince = -1f;
        Agent_HUD _hud;
        Agent_CameraFollow _camera;
        Agent_CameraFollow.CameraMode _savedMode;

        /// <summary>The settings in use (the shipped asset, or defaults).</summary>
        public Agent_DemoModeSettings Settings => _settings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Enabled = true;
            Active = false;
            DemoMatch = false;
            Pool = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (Application.isBatchMode || Agent_Presentation.IsHeadless) return;
            if (FindAnyObjectByType<Agent_DemoMode>() != null) return;
            var host = new GameObject("DemoMode");
            DontDestroyOnLoad(host);
            host.AddComponent<Agent_DemoMode>();
        }

        /// <summary>True when an idle time has reached the threshold (pure, for tests).</summary>
        public static bool ShouldStart(float idleSeconds, float threshold) => idleSeconds >= threshold;

        void Awake()
        {
            _settings = Agent_DemoModeSettings.Load();
            _idleSeconds = 0f;
        }

        void OnEnable()
        {
            _anyButton = InputSystem.onAnyButtonPress.Call(_ => _buttonPressed = true);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDisable()
        {
            _anyButton?.Dispose();
            _anyButton = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        bool InputThisFrame()
        {
            if (_buttonPressed)
            {
                _buttonPressed = false;
                return true;
            }
            var mouse = Mouse.current;
            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 4f || mouse.scroll.ReadValue().sqrMagnitude > 0f))
                return true;
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.isPressed) return true;
            return false;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (InputThisFrame())
            {
                _idleSeconds = 0f;
                if (Active) Exit();
                return;
            }
            if (!Active)
            {
                _idleSeconds = AccumulateIdle(_idleSeconds, Time.unscaledDeltaTime);
                if (Enabled && !TrainerConnected() && ShouldStart(_idleSeconds, _settings.idleSeconds)) Enter();
                return;
            }
            Tick(now);
        }

        static bool TrainerConnected() =>
            Unity.MLAgents.Academy.IsInitialized && Unity.MLAgents.Academy.Instance.IsCommunicatorOn;

        void Enter()
        {
            _idleSeconds = 0f;   // a failed start waits a full period before retrying
            var hud = FindAnyObjectByType<Agent_HUD>();
            bool match = hud != null && Agent_Presentation.IsMatchScene(hud);
            if (match && !Agent_MatchSetup.HumanControl && !Agent_MatchSetup.TestbedMode)
            {
                Begin(hud);
                return;
            }
            bool menu = SceneManager.GetActiveScene().name == _settings.menuScene;
            if ((menu || match) && _settings.startExhibitionFromMenu) StartExhibition();
        }

        /// <summary>Loads an all-AI exhibition of two random pool characters. False when the pool is too small.</summary>
        bool StartExhibition()
        {
            var pool = Pool;
            if (pool == null || pool.Length < 2) return false;
            int blue = Random.Range(0, pool.Length);
            int red = (blue + Random.Range(1, pool.Length)) % pool.Length;
            Agent_TimeFreeze.ReleaseAll();
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = new[] { pool[blue] };
            Agent_MatchSetup.RedSquad = new[] { pool[red] };
            DemoMatch = true;
            Active = true;
            _hud = null;
            SceneManager.LoadScene(_settings.matchScene);
            return true;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Active && DemoMatch) StartCoroutine(BeginWhenHudReady());
        }

        IEnumerator BeginWhenHudReady()
        {
            for (int frame = 0; frame < 120 && Active; frame++)
            {
                yield return null;
                var hud = FindAnyObjectByType<Agent_HUD>();
                if (hud != null && hud.OverlayRoot != null)
                {
                    Begin(hud);
                    yield break;
                }
            }
        }

        void Begin(Agent_HUD hud)
        {
            Active = true;
            _hud = hud;
            _hud.SetDemoMode(true);
            Agent_Chrome.SetDemoHidden(true);
            var cam = Camera.main;
            _camera = cam != null ? cam.GetComponent<Agent_CameraFollow>() : null;
            if (_camera != null) _savedMode = _camera.Mode;
            _shotIndex = 0;
            _matchOverSince = -1f;
            NextShot(Time.unscaledTime);
        }

        void Tick(float now)
        {
            if (_hud == null) return;   // waiting for a demo exhibition's scene
            if (now >= _nextShotAt) NextShot(now);

            if (!_hud.MatchOver)
            {
                _matchOverSince = -1f;
                return;
            }
            if (_matchOverSince < 0f) _matchOverSince = now;
            else if (now - _matchOverSince >= _settings.nextMatchAfterSeconds && !StartExhibition())
            {
                // No roster to draw from (the match scene was opened directly): stay on the result.
                _matchOverSince = float.PositiveInfinity;
            }
        }

        void NextShot(float now)
        {
            _nextShotAt = now + _settings.cameraDwellSeconds;
            if (_camera == null) return;
            var shot = _settings.ShotAt(_shotIndex++);
            _camera.ClearFocus();
            switch (shot)
            {
                case Agent_DemoModeSettings.Shot.FollowBall:
                    _camera.SetCameraMode(Agent_CameraFollow.CameraMode.Follow);
                    break;
                case Agent_DemoModeSettings.Shot.Orbit:
                    _camera.SetCameraMode(Agent_CameraFollow.CameraMode.Orbit);
                    break;
                case Agent_DemoModeSettings.Shot.FocusPlayer:
                    _camera.SetCameraMode(Agent_CameraFollow.CameraMode.Broadcast);
                    var env = FindAnyObjectByType<Agent_EnvController>();
                    if (env != null && env.agents.Count > 0)
                    {
                        var agent = env.agents[Random.Range(0, env.agents.Count)];
                        if (agent != null) _camera.SetFocus(agent.transform);
                    }
                    break;
                default:
                    _camera.SetCameraMode(Agent_CameraFollow.CameraMode.Broadcast);
                    break;
            }
        }

        void Exit()
        {
            Active = false;
            if (DemoMatch)
            {
                DemoMatch = false;
                _hud = null;
                Agent_TimeFreeze.ReleaseAll();
                Agent_MatchSetup.Clear();
                SceneManager.LoadScene(_settings.menuScene);
                return;
            }
            if (_hud != null) _hud.SetDemoMode(false);
            Agent_Chrome.SetDemoHidden(false);
            if (_camera != null)
            {
                _camera.ClearFocus();
                _camera.SetCameraMode(_savedMode);
            }
            _hud = null;
        }
    }
}
