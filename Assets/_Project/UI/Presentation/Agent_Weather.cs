using UnityEngine;
#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
using System.Collections;
using UnityEngine.Networking;
#endif

namespace PoSoccer
{
    /// <summary>
    /// Dynamic Weather & Pitch Turf Physics Mutator + Open-Meteo Live Weather Sync.
    ///
    /// Four turf states:
    ///  - Dry:   standard grip (1.00x traction, 1.00x ball damping)
    ///  - Wet:   slick grass (0.82x traction, 0.65x ball damping — ball skids fast)
    ///  - Muddy: heavy boggy turf (0.76x traction, 1.45x ball damping — passes hold up)
    ///  - Snowy: icy pitch (0.64x traction, 0.85x ball damping)
    ///
    /// SAFETY CONTRACT:
    ///  1. ActiveTractionMultiplier(env) returns 1.0f whenever env is null, in a
    ///     bounded training/eval episode, or when weather is Dry. Training and
    ///     evaluation checkpoints are never perturbed.
    ///  2. Open-Meteo network calls are compiled out on Android unless
    ///     POSOCCER_BRAIN_HUB is defined, preserving the zero-permission Android
    ///     manifest rule.
    /// </summary>
    [RequireComponent(typeof(Agent_EnvController))]
    [DisallowMultipleComponent]
    public sealed class Agent_Weather : MonoBehaviour
    {
        public enum Condition
        {
            Dry = 0,
            Wet = 1,
            Muddy = 2,
            Snowy = 3,
        }

        const string PREF_KEY = "posoccer_weather";

        [SerializeField] private float _defaultLatitude = 51.5074f;
        [SerializeField] private float _defaultLongitude = -0.1278f;
        [SerializeField] private bool _syncOpenMeteoOnEnable = false;

        static Condition _currentCondition = Condition.Dry;

        /// <summary>Current turf condition across the match scene.</summary>
        public static Condition CurrentCondition
        {
            get => _currentCondition;
            set
            {
                _currentCondition = value;
                if (_instance != null) _instance.ApplyCurrentCondition();
            }
        }

        /// <summary>
        /// Persisted setting in the MATCH tab of <see cref="Agent_SettingsSheet"/>.
        /// Toggling ON activates Wet/cycled weather; OFF restores Dry turf.
        /// </summary>
        public static bool Enabled
        {
            get => !Application.isBatchMode && PlayerPrefs.GetInt(PREF_KEY, 0) == 1;
            set
            {
                if (!Application.isBatchMode)
                {
                    PlayerPrefs.SetInt(PREF_KEY, value ? 1 : 0);
                    PlayerPrefs.Save();
                }
                if (!value)
                {
                    CurrentCondition = Condition.Dry;
                }
                else if (CurrentCondition == Condition.Dry)
                {
                    CurrentCondition = Condition.Wet;
                }
            }
        }

        static Agent_Weather _instance;
        Agent_EnvController _env;
        ParticleSystem _precipParticles;
        Material _precipMaterial;
        float _baseBallDamping = -1f;

        [System.Serializable]
        struct OpenMeteoEnvelope
        {
            public OpenMeteoCurrent current_weather;
        }

        [System.Serializable]
        struct OpenMeteoCurrent
        {
            public float temperature;
            public int weathercode;
            public int weather_code;
        }

        /// <summary>Pure multiplier for <c>_tractionMu</c> in <see cref="Agent_Soccer"/>.</summary>
        public static float TractionMultiplier(Condition condition)
        {
            switch (condition)
            {
                case Condition.Wet: return 0.82f;
                case Condition.Muddy: return 0.76f;
                case Condition.Snowy: return 0.64f;
                default: return 1.00f;
            }
        }

        /// <summary>Pure multiplier for the ball's <c>linearDamping</c>.</summary>
        public static float BallDampingMultiplier(Condition condition)
        {
            switch (condition)
            {
                case Condition.Wet: return 0.65f;
                case Condition.Muddy: return 1.45f;
                case Condition.Snowy: return 0.85f;
                default: return 1.00f;
            }
        }

        /// <summary>
        /// Returns the active traction multiplier for <paramref name="env"/>.
        /// Always 1.0f in training/eval (where <c>_instance</c> is disabled/null) so checkpoints are untouched.
        /// </summary>
        public static float ActiveTractionMultiplier(Agent_EnvController env)
        {
            if (_instance == null || env == null || _instance._env != env) return 1f;
            return TractionMultiplier(_currentCondition);
        }

        /// <summary>Maps a WMO weather interpretation code and temperature (°C) to a pitch <see cref="Condition"/>.</summary>
        public static Condition ParseOpenMeteoCode(int wmoCode, float tempC = 12f)
        {
            if ((wmoCode >= 71 && wmoCode <= 77) || wmoCode == 85 || wmoCode == 86
                || (tempC <= 0.5f && wmoCode >= 51))
            {
                return Condition.Snowy;
            }
            if ((wmoCode >= 63 && wmoCode <= 67) || wmoCode == 81 || wmoCode == 82 || wmoCode >= 95)
            {
                return Condition.Muddy;
            }
            if ((wmoCode >= 45 && wmoCode <= 62) || wmoCode == 80)
            {
                return Condition.Wet;
            }
            return Condition.Dry;
        }

        /// <summary>Parses an Open-Meteo <c>current_weather</c> JSON payload into WMO code and temperature.</summary>
        public static bool TryParseOpenMeteoJson(string json, out int wmoCode, out float tempC)
        {
            wmoCode = 0;
            tempC = 15f;
            if (string.IsNullOrEmpty(json) || !json.Contains("current_weather")) return false;
            try
            {
                var env = JsonUtility.FromJson<OpenMeteoEnvelope>(json);
                tempC = env.current_weather.temperature;
                wmoCode = env.current_weather.weathercode != 0
                    ? env.current_weather.weathercode
                    : env.current_weather.weather_code;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Cycles to the next weather condition (Dry -> Wet -> Muddy -> Snowy -> Dry).</summary>
        public static Condition CycleCondition()
        {
            CurrentCondition = (Condition)(((int)_currentCondition + 1) % 4);
            return _currentCondition;
        }

        void Awake()
        {
            Agent_SettingsSheet.RegisterSwitch(
                Agent_SettingsSheet.Tab.Match,
                "weather",
                "Wet weather turf",
                () => _currentCondition != Condition.Dry,
                on =>
                {
                    Enabled = on;
                    CurrentCondition = on ? Condition.Wet : Condition.Dry;
                });
        }

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            var hud = FindAnyObjectByType<Agent_HUD>();
            if (Agent_Presentation.IsHeadless || !Agent_Presentation.IsMatchScene(hud) || _env == null)
            {
                enabled = false;
                return;
            }

            _instance = this;
            if (_env.Ball != null) _baseBallDamping = _env.Ball.linearDamping;

            if (Enabled && _currentCondition == Condition.Dry)
            {
                _currentCondition = Condition.Wet;
            }

            BuildPrecipitationParticles();
            ApplyCurrentCondition();

#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
            if (_syncOpenMeteoOnEnable && Enabled)
            {
                StartCoroutine(FetchOpenMeteoCoroutine(_defaultLatitude, _defaultLongitude, cond => CurrentCondition = cond));
            }
#endif
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_env != null && _env.Ball != null && _baseBallDamping >= 0f)
            {
                _env.Ball.linearDamping = _baseBallDamping;
            }
            if (_precipMaterial != null) Destroy(_precipMaterial);
        }

        void ApplyCurrentCondition()
        {
            if (_env != null && _env.Ball != null)
            {
                if (_baseBallDamping < 0f) _baseBallDamping = _env.Ball.linearDamping;
                _env.Ball.linearDamping = _baseBallDamping * BallDampingMultiplier(_currentCondition);
            }

            if (_env != null)
            {
                var surfaces = _env.GetComponent<Agent_Surfaces>();
                if (surfaces != null) surfaces.ApplyWeather(_currentCondition);
                var audio = _env.GetComponent<Agent_Audio>();
                if (audio != null) audio.RefreshWeatherAudio(_currentCondition);
            }
            if (Agent_Stadium.Instance != null)
            {
                Agent_Stadium.Instance.SetWeatherCondition(_currentCondition);
            }

            if (_precipParticles == null) return;

            if (_currentCondition == Condition.Dry || _currentCondition == Condition.Muddy)
            {
                if (_precipParticles.isPlaying) _precipParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                return;
            }

            var main = _precipParticles.main;
            var emission = _precipParticles.emission;
            if (_currentCondition == Condition.Wet)
            {
                main.startSpeed = new ParticleSystem.MinMaxCurve(6.5f, 9.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.11f);
                main.startColor = new Color(0.75f, 0.88f, 1f, 0.36f);
                emission.rateOverTime = 48f;
            }
            else // Snowy
            {
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 2.6f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.18f);
                main.startColor = new Color(0.96f, 0.98f, 1f, 0.62f);
                emission.rateOverTime = 36f;
            }

            if (!_precipParticles.isPlaying) _precipParticles.Play();
        }

        void BuildPrecipitationParticles()
        {
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                ?? Shader.Find("Sprites/Default");
            if (shader == null) return;

            _precipMaterial = new Material(shader) { name = "PoSoccer_WeatherPrecip" };
            var sprite = Agent_Art.Disc(0.12f);
            if (sprite != null && sprite.texture != null) _precipMaterial.mainTexture = sprite.texture;

            var go = new GameObject("WeatherPrecipitation");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, _env.PitchHalfExtents.y + 1.5f, 0f);

            _precipParticles = go.AddComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = _precipMaterial;
            renderer.sortingOrder = 19;

            var main = _precipParticles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = 2.4f;
            main.maxParticles = 140;

            var shape = _precipParticles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(_env.PitchHalfExtents.x * 2.4f, 1f, 0.1f);

            var vel = _precipParticles.velocityOverLifetime;
            vel.enabled = true;
            vel.x = new ParticleSystem.MinMaxCurve(-0.8f, -0.3f);
            vel.y = new ParticleSystem.MinMaxCurve(-1f, -0.8f);
            vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            _precipParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
        public static IEnumerator FetchOpenMeteoCoroutine(
            float latitude, float longitude, System.Action<Condition> onComplete)
        {
            string url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:F4}&longitude={1:F4}&current_weather=true",
                latitude, longitude);

            using var req = UnityWebRequest.Get(url);
            req.timeout = 5;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success
                && TryParseOpenMeteoJson(req.downloadHandler.text, out int code, out float tempC))
            {
                onComplete?.Invoke(ParseOpenMeteoCode(code, tempC));
            }
        }
#endif
    }
}
