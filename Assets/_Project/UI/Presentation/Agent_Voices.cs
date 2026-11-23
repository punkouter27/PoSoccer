using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Gives every character a voice: HAPPY, ANGRY, HURT, INSULT and CELEBRATE
    /// (the tournament standard's 5 emotions x 5 levels), five lines each, five
    /// levels of expressiveness per line (1 = weakest, 5 = strongest).
    ///
    /// WHERE A SOUND COMES FROM. Resources/Voices/voices.json (<see cref="Agent_VoiceManifest"/>)
    /// lists every character x emotion x level, its files and whether each exists.
    /// First hit wins, and only files the manifest marks as existing are loaded:
    ///   1. the per-line take &lt;NAME&gt;_&lt;MOOD&gt;_&lt;line&gt;_&lt;level&gt;.wav
    ///      (e.g. MATT_INSULT_3_5.wav);
    ///   2. the cross-project standard file &lt;name&gt;/&lt;emotion&gt;-&lt;level&gt;.wav
    ///      (e.g. matt/celebrate-5.wav), one take for every line;
    ///   3. the advanced procedural imitation (<see cref="Agent_VoiceSynth"/>) driven by
    ///      the character's "voice" profile in the manifest (a character not in it, or
    ///      no manifest at all, gets a profile derived from its voicePitch).
    /// Every clip is cached after its first use, and each match warms its players'
    /// clips (take 1 of each cell) one per frame. docs/voices.md lists the names.
    ///
    /// WHEN A CHARACTER SPEAKS.
    ///   goal      the scorer is HAPPY (level from what the goal meant), a player
    ///             on the conceding side is ANGRY, then the scorer INSULTS them,
    ///             then a teammate (or the scorer) CELEBRATEs: level 1-3 for a
    ///             goal, 4 when it won the match.
    ///   full time the winning side CELEBRATEs: 4 for a match won on the clock,
    ///             5 for the whole team when it won the cup
    ///             (<see cref="CelebrateResult"/>, from Agent_TournamentMatch).
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

        static readonly string[] MoodNames = { "HAPPY", "ANGRY", "HURT", "INSULT", "CELEBRATE" };

        /// <summary>CELEBRATE levels: 1-3 a goal, 4 a match won, 5 a cup won.</summary>
        public const int CELEBRATE_MATCH_WON = 4;
        public const int CELEBRATE_CUP_WON = 5;

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
        bool _matchWinCelebrated;

        /// <summary>Last thing said, for tests and the health panel: "MATT HAPPY 2/5".</summary>
        public string LastSpoken { get; private set; } = "";

        /// <summary>Resource path (under Resources/) of one sound.</summary>
        public static string ResourcePath(string character, Agent_VoiceMood mood, int line, int level)
            => $"{RESOURCE_FOLDER}{character}_{MoodNames[(int)mood]}_{line}_{level}";

        /// <summary>
        /// Cross-project standard path (under Resources/) of one emotion at one level,
        /// shared by all five lines: Voices/&lt;name&gt;/&lt;emotion&gt;-&lt;level&gt;, lower case.
        /// </summary>
        public static string StandardResourcePath(string character, Agent_VoiceMood mood, int level)
            => $"{RESOURCE_FOLDER}{character.ToLowerInvariant()}/{MoodNames[(int)mood].ToLowerInvariant()}-{level}";

        /// <summary>CELEBRATE level for a goal that did not win the match: 1 still behind, 2 level, 3 ahead.</summary>
        public static int CelebrateLevelForGoal(int scored, int conceded)
            => scored < conceded ? 1 : scored == conceded ? 2 : 3;

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
            WarmUpAsync(_cts.Token).Forget();
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

        /// <summary>A recorded WAV the manifest says exists, otherwise the procedural imitation. Cached.</summary>
        public static AudioClip ClipFor(Reward_Settings character, Agent_VoiceMood mood, int line, int level)
        {
            string name = string.IsNullOrEmpty(character.playerName) ? "PLAYER" : character.playerName.ToUpperInvariant();
            string path = ResourcePath(name, mood, line, level);
            if (Clips.TryGetValue(path, out var cached) && cached != null) return cached;

            var entry = Agent_VoiceManifest.Loaded?.Find(name);
            var cell = entry?.ClipFor(mood, level);
            AudioClip clip = null;
            var take = cell?.TakeFor(line);
            if (take != null && take.exists) clip = LoadWav(Agent_VoiceManifest.ResourcePathOf(take.file));
            if (clip == null && cell != null && cell.exists) clip = LoadWav(Agent_VoiceManifest.ResourcePathOf(cell.file));
            if (clip == null)
            {
                var profile = entry?.voice != null && entry.voice.IsValid
                    ? entry.voice
                    : Agent_VoiceManifest.ProfileFromPitch(name, character.voicePitch);
                clip = Agent_VoiceSynth.ToClip(profile, Agent_VoiceManifest.CharacterId(name), mood, level, line);
            }
            Clips[path] = clip;
            return clip;
        }

        /// <summary>Renders (or loads) each player's 5 x 5 clips (take 1), one per frame, so a goal rarely waits on synthesis.</summary>
        async UniTaskVoid WarmUpAsync(CancellationToken token)
        {
            for (int playerIndex = 0; playerIndex < _players.Length; playerIndex++)
            {
                var profile = _players[playerIndex] != null ? _players[playerIndex].rewards : null;
                if (profile == null) continue;
                foreach (Agent_VoiceMood mood in System.Enum.GetValues(typeof(Agent_VoiceMood)))
                {
                    for (int level = 1; level <= Agent_VoiceSynth.LEVELS; level++)
                    {
                        // One take per cell: warming all five lines for eight players would hold
                        // ~1,000 clips in memory (WebGL); the other lines render on first use.
                        await UniTask.Yield(PlayerLoopTiming.Update, token);
                        ClipFor(profile, mood, 1, level);
                    }
                }
            }
        }

        static AudioClip LoadWav(string path)
        {
            if (NoWav.Contains(path)) return null;
            var clip = Resources.Load<AudioClip>(path);
            if (clip == null) NoWav.Add(path);
            return clip;
        }

        /// <summary>
        /// Full-time celebration, called once the result is final. A cup win has the
        /// whole winning side celebrate at level 5; a plain match win has one player
        /// celebrate at level 4, unless the winning goal already did.
        /// </summary>
        public void CelebrateResult(Agent_Soccer.Team winner, bool cupWon)
        {
            if (_players == null || _pool == null || _cts == null) return;
            if (cupWon)
            {
                CelebrateTeamAsync(winner, _cts.Token).Forget();
                return;
            }
            if (_matchWinCelebrated) return;
            _matchWinCelebrated = true;
            var player = FirstOf(winner);
            if (player != null) Say(player, Agent_VoiceMood.Celebrate, CELEBRATE_MATCH_WON);
        }

        async UniTaskVoid CelebrateTeamAsync(Agent_Soccer.Team winner, CancellationToken token)
        {
            for (int playerIndex = 0; playerIndex < _players.Length; playerIndex++)
            {
                if (_players[playerIndex] == null || _players[playerIndex].team != winner) continue;
                Say(_players[playerIndex], Agent_VoiceMood.Celebrate, CELEBRATE_CUP_WON);
                await UniTask.Delay(System.TimeSpan.FromSeconds(0.3f), DelayType.UnscaledDeltaTime, cancellationToken: token);
            }
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
            int celebrate = winningGoal ? CELEBRATE_MATCH_WON : CelebrateLevelForGoal(scored, conceded);
            if (winningGoal) _matchWinCelebrated = true;
            var celebrant = TeammateOf(scorer) ?? scorer;

            if (scorer != null) Say(scorer, Agent_VoiceMood.Happy, happy);
            SequenceAsync(scorer, victim, celebrant, angry, insult, celebrate, _cts.Token).Forget();
        }

        async UniTaskVoid SequenceAsync(Agent_Soccer scorer, Agent_Soccer victim, Agent_Soccer celebrant,
            int angry, int insult, int celebrate, CancellationToken token)
        {
            await UniTask.Delay(System.TimeSpan.FromSeconds(0.7f), DelayType.UnscaledDeltaTime, cancellationToken: token);
            if (victim != null) Say(victim, Agent_VoiceMood.Angry, angry);
            await UniTask.Delay(System.TimeSpan.FromSeconds(1.0f), DelayType.UnscaledDeltaTime, cancellationToken: token);
            if (scorer != null) Say(scorer, Agent_VoiceMood.Insult, insult);
            await UniTask.Delay(System.TimeSpan.FromSeconds(0.9f), DelayType.UnscaledDeltaTime, cancellationToken: token);
            if (celebrant != null) Say(celebrant, Agent_VoiceMood.Celebrate, celebrate);
        }

        /// <summary>Another player on the same side, or null (a 1 v 1).</summary>
        Agent_Soccer TeammateOf(Agent_Soccer player)
        {
            if (player == null) return null;
            for (int playerIndex = 0; playerIndex < _players.Length; playerIndex++)
            {
                var other = _players[playerIndex];
                if (other != null && other != player && other.team == player.team) return other;
            }
            return null;
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
