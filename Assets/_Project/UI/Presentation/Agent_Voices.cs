using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Gives every character a voice: HAPPY, ANGRY, HURT and INSULT, five lines
    /// each, five levels of expressiveness per line (1 = weakest, 5 = strongest).
    ///
    /// WHERE A SOUND COMES FROM. A recorded WAV at
    /// Resources/Voices/&lt;NAME&gt;_&lt;MOOD&gt;_&lt;line&gt;_&lt;level&gt; (for example
    /// Voices/MATT_INSULT_3_5) always wins. Any address without one is rendered by
    /// <see cref="Agent_VoiceSynth"/> from the character's voicePitch, so every
    /// character has the full 4 x 5 x 5 set from day one and each WAV replaces its
    /// placeholder the moment it is added. docs/voices.md lists the names.
    ///
    /// WHEN A CHARACTER SPEAKS.
    ///   goal      the scorer is HAPPY (level from what the goal meant), a player
    ///             on the conceding side is ANGRY, then the scorer INSULTS them.
    ///   contact   a player hit hard enough says HURT, level from the impulse.
    ///   stalemate one player is mildly ANGRY.
    /// Each player has a cooldown so a scramble is not a choir.
    ///
    /// Match-only and code-installed (one audio pool for the match). Off in
    /// training, evaluation and anything headless.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_Voices : MonoBehaviour
    {
        const int POOL_SIZE = 4;
        const float PLAYER_COOLDOWN = 1.2f;
        const float HURT_MIN_STRENGTH = 0.35f;
        const string RESOURCE_FOLDER = "Voices/";

        static readonly string[] MoodNames = { "HAPPY", "ANGRY", "HURT", "INSULT" };

        /// <summary>WAV lookups and rendered placeholders, keyed by address. Shared across matches.</summary>
        static readonly Dictionary<string, AudioClip> Clips = new();
        static readonly HashSet<string> NoWav = new();

        Agent_EnvController _env;
        Agent_HUD _hud;
        Agent_Soccer[] _players;
        Agent_Contact[] _contacts;
        System.Action<Vector2, Vector2, float>[] _contactHandlers;
        float[] _nextAllowed;
        AudioSource[] _pool;
        int _poolCursor;
        Agent_Soccer _lastBlueToucher, _lastRedToucher;
        CancellationTokenSource _cts;

        /// <summary>Last thing said, for tests and the health panel: "MATT HAPPY 2/5".</summary>
        public string LastSpoken { get; private set; } = "";

        /// <summary>Resource path (under Resources/) of one sound.</summary>
        public static string ResourcePath(string character, Agent_VoiceMood mood, int line, int level)
            => $"{RESOURCE_FOLDER}{character}_{MoodNames[(int)mood]}_{line}_{level}";

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            if (_env == null) _env = FindAnyObjectByType<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();
            if (_env == null || !Agent_Presentation.IsMatchScene(_hud) || Agent_Presentation.IsHeadless)
            {
                enabled = false;
                return;
            }

            _cts = new CancellationTokenSource();
            _pool = new AudioSource[POOL_SIZE];
            for (int sourceIndex = 0; sourceIndex < POOL_SIZE; sourceIndex++)
            {
                var source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
                _pool[sourceIndex] = source;
            }

            _players = _env.GetComponentsInChildren<Agent_Soccer>();
            _nextAllowed = new float[_players.Length];
            _contacts = new Agent_Contact[_players.Length];
            _contactHandlers = new System.Action<Vector2, Vector2, float>[_players.Length];
            for (int playerIndex = 0; playerIndex < _players.Length; playerIndex++)
            {
                var contact = _players[playerIndex].GetComponent<Agent_Contact>();
                if (contact == null) continue;
                int captured = playerIndex;
                _contactHandlers[playerIndex] = (point, normal, impulse) => OnContact(captured, impulse);
                contact.PlayerContact += _contactHandlers[playerIndex];
                _contacts[playerIndex] = contact;
            }

            _env.EpisodeEnded += OnEpisodeEnded;
            _env.BallTouched += OnBallTouched;
        }

        void OnDestroy()
        {
            if (_env != null)
            {
                _env.EpisodeEnded -= OnEpisodeEnded;
                _env.BallTouched -= OnBallTouched;
            }
            if (_contacts != null)
            {
                for (int playerIndex = 0; playerIndex < _contacts.Length; playerIndex++)
                {
                    if (_contacts[playerIndex] != null) _contacts[playerIndex].PlayerContact -= _contactHandlers[playerIndex];
                }
            }
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        /// <summary>Plays one sound for one player now. Level 1..5; line 0 = pick one.</summary>
        public void Say(Agent_Soccer speaker, Agent_VoiceMood mood, int level, int line = 0)
        {
            if (speaker == null || speaker.rewards == null || _pool == null) return;
            level = Mathf.Clamp(level, 1, Agent_VoiceSynth.LEVELS);
            if (line <= 0) line = Random.Range(1, Agent_VoiceSynth.LINES + 1);

            var clip = ClipFor(speaker.rewards, mood, line, level);
            if (clip == null) return;

            var source = _pool[_poolCursor];
            _poolCursor = (_poolCursor + 1) % _pool.Length;
            source.Stop();
            source.clip = clip;
            source.volume = Agent_Audio.Muted ? 0f : Agent_Audio.MasterVolume * Agent_Audio.SfxVolume;
            source.panStereo = Mathf.Clamp(speaker.transform.position.x / Mathf.Max(1f, _env.PitchHalfExtents.x), -1f, 1f) * 0.6f;
            source.Play();

            LastSpoken = $"{speaker.rewards.playerName} {MoodNames[(int)mood]} {line}/{level}";
            int index = IndexOf(speaker);
            if (index >= 0) _nextAllowed[index] = Time.unscaledTime + PLAYER_COOLDOWN;
        }

        /// <summary>A recorded WAV if one exists, otherwise the procedural placeholder.</summary>
        public static AudioClip ClipFor(Reward_Settings character, Agent_VoiceMood mood, int line, int level)
        {
            string name = string.IsNullOrEmpty(character.playerName) ? "PLAYER" : character.playerName.ToUpperInvariant();
            string path = ResourcePath(name, mood, line, level);
            if (Clips.TryGetValue(path, out var cached) && cached != null) return cached;

            AudioClip clip = null;
            if (!NoWav.Contains(path))
            {
                clip = Resources.Load<AudioClip>(path);
                if (clip == null) NoWav.Add(path);
            }
            if (clip == null) clip = Agent_VoiceSynth.ToClip(name, mood, line, level, character.voicePitch);
            Clips[path] = clip;
            return clip;
        }

        void OnBallTouched(Agent_Soccer toucher)
        {
            if (toucher == null) return;
            if (toucher.team == Agent_Soccer.Team.Blue) _lastBlueToucher = toucher;
            else _lastRedToucher = toucher;
        }

        void OnContact(int playerIndex, float impulse)
        {
            float strength = Agent_ImpactScale.Player01(impulse);
            if (strength < HURT_MIN_STRENGTH || !CanSpeak(playerIndex)) return;
            int level = 1 + Mathf.Clamp(Mathf.FloorToInt((strength - HURT_MIN_STRENGTH) / (1f - HURT_MIN_STRENGTH) * 5f), 0, 4);
            Say(_players[playerIndex], Agent_VoiceMood.Hurt, level);
        }

        void OnEpisodeEnded(Agent_Soccer.Team? winner)
        {
            if (_players == null || _players.Length == 0) return;

            if (winner == null)
            {
                var grumbler = _players[Random.Range(0, _players.Length)];
                Say(grumbler, Agent_VoiceMood.Angry, Random.Range(1, 3));
                return;
            }

            var scoringTeam = winner.Value;
            var scorer = scoringTeam == Agent_Soccer.Team.Blue ? _lastBlueToucher : _lastRedToucher;
            if (scorer == null || scorer.team != scoringTeam) scorer = FirstOf(scoringTeam);
            var victim = scoringTeam == Agent_Soccer.Team.Blue ? _lastRedToucher : _lastBlueToucher;
            if (victim == null || victim.team == scoringTeam) victim = FirstOf(Other(scoringTeam));

            // What the goal meant, from the scoreboard after it landed.
            int scored = _hud != null ? (scoringTeam == Agent_Soccer.Team.Blue ? _hud.BlueScore : _hud.RedScore) : 1;
            int conceded = _hud != null ? (scoringTeam == Agent_Soccer.Team.Blue ? _hud.RedScore : _hud.BlueScore) : 0;
            bool winningGoal = _hud != null && _hud.MatchOver;
            int happy = winningGoal ? 5 : scored == conceded ? 4 : scored == conceded + 1 ? 3 : 2;
            int angry = winningGoal ? 5 : Mathf.Clamp(2 + (scored - conceded), 1, 4);
            int insult = winningGoal ? 5 : Mathf.Clamp(1 + (scored - conceded), 1, 4);

            if (scorer != null) Say(scorer, Agent_VoiceMood.Happy, happy);
            SequenceAsync(scorer, victim, angry, insult, _cts.Token).Forget();
        }

        async UniTaskVoid SequenceAsync(Agent_Soccer scorer, Agent_Soccer victim, int angry, int insult, CancellationToken token)
        {
            await UniTask.Delay(System.TimeSpan.FromSeconds(0.7f), DelayType.UnscaledDeltaTime, cancellationToken: token);
            if (victim != null) Say(victim, Agent_VoiceMood.Angry, angry);
            await UniTask.Delay(System.TimeSpan.FromSeconds(1.0f), DelayType.UnscaledDeltaTime, cancellationToken: token);
            if (scorer != null) Say(scorer, Agent_VoiceMood.Insult, insult);
        }

        bool CanSpeak(int playerIndex) => Time.unscaledTime >= _nextAllowed[playerIndex];

        int IndexOf(Agent_Soccer player)
        {
            for (int playerIndex = 0; playerIndex < _players.Length; playerIndex++)
            {
                if (_players[playerIndex] == player) return playerIndex;
            }
            return -1;
        }

        Agent_Soccer FirstOf(Agent_Soccer.Team team)
        {
            for (int playerIndex = 0; playerIndex < _players.Length; playerIndex++)
            {
                if (_players[playerIndex] != null && _players[playerIndex].team == team) return _players[playerIndex];
            }
            return null;
        }

        static Agent_Soccer.Team Other(Agent_Soccer.Team team)
            => team == Agent_Soccer.Team.Blue ? Agent_Soccer.Team.Red : Agent_Soccer.Team.Blue;
    }
}
