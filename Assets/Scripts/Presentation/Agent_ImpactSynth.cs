using System.Threading;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Physics-driven impact sound: every contact is SYNTHESISED from its impulse
    /// rather than picked from a sample bank (2026-11-07).
    ///
    /// MODAL SYNTHESIS. A struck object rings at a handful of resonant frequencies
    /// (its modes), each decaying at its own rate. Each material below is four
    /// modes - frequency, T60 decay, amplitude - plus a short noise burst for the
    /// contact itself, and each mode is a two-pole resonator (two multiplies and
    /// two adds per sample). The material MATRIX is the pair that touched:
    ///
    ///   Leather  ball on boot/body   low thump, fast decay, a leather slap of noise
    ///   Boards   ball on the boards  hollow mid knock, medium decay
    ///   Post     ball on the woodwork  inharmonic metal ring, long decay
    ///   Body     player on player    dull low thud, cloth noise
    ///   Net      ball into the net   mostly filtered noise: the rustle
    ///
    /// WHAT THE IMPULSE CHANGES, which is the point of synthesising at all:
    ///  - level: Agent_ImpactScale.Loudness of the normalised impulse (as before);
    ///  - TIMBRE: the contact pulse gets SHORTER the harder the hit (a hard strike
    ///    deforms faster), and a shorter pulse excites the high modes more - so a
    ///    hard shot is brighter, not just louder. A sample bank cannot do this;
    ///  - pitch: the caller's per-pair pitch law (heavy kicks lower, a hard-struck
    ///    post higher) plus a small detune so no two hits are identical.
    ///
    /// Spatial picture, matching Agent_Audio.PlayAt: equal-power stereo pan from
    /// the pitch position, level and a one-pole air-absorption low-pass from the
    /// listener distance. It is a 2D source (spatialBlend 0) because the pan and
    /// distance are applied here, per voice; a 3D source would apply ONE position
    /// to every voice it mixes.
    ///
    /// THREADING. OnAudioFilterRead runs on the audio thread. The main thread only
    /// writes Strike structs into a single-producer / single-consumer ring and
    /// publishes the head index with Volatile.Write; the audio thread consumes up
    /// to that index. Every buffer is allocated in Awake, so the audio callback
    /// allocates nothing (.claude/rules/performance.md).
    ///
    /// Created and owned by Agent_Audio, which falls back to its sample banks when
    /// this component is missing or disabled. Never exists headless: Agent_Audio
    /// bails out before building anything there.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AudioSource))]
    public sealed class Agent_ImpactSynth : MonoBehaviour
    {
        /// <summary>The material pair that made contact. Index into the mode tables.</summary>
        public enum Surface { Leather = 0, Boards = 1, Post = 2, Body = 3, Net = 4 }

        const int MATERIAL_COUNT = 5;
        const int MODES = 4;
        const int VOICES = 8;
        const int QUEUE = 16;               // power of two
        const int QUEUE_MASK = QUEUE - 1;

        // -- Material table -----------------------------------------------------
        // Per material, MODES entries of (frequency Hz, T60 seconds, amplitude).
        // Set by ear against the physical shape of each sound, not measured from a
        // recording - see the class docstring.
        static readonly float[] ModeFreq =
        {
            95f, 180f, 420f, 1300f,         // Leather
            140f, 310f, 690f, 1550f,        // Boards
            520f, 1420f, 2680f, 4150f,      // Post (inharmonic ratios, like a tube)
            70f, 130f, 260f, 900f,          // Body
            230f, 480f, 1900f, 3600f,       // Net
        };

        static readonly float[] ModeT60 =
        {
            0.09f, 0.06f, 0.03f, 0.012f,
            0.18f, 0.12f, 0.07f, 0.04f,
            1.10f, 0.80f, 0.55f, 0.35f,
            0.08f, 0.06f, 0.04f, 0.015f,
            0.15f, 0.10f, 0.05f, 0.03f,
        };

        static readonly float[] ModeAmp =
        {
            1.0f, 0.6f, 0.25f, 0.12f,
            0.8f, 0.6f, 0.35f, 0.15f,
            0.6f, 0.45f, 0.3f, 0.2f,
            1.0f, 0.5f, 0.2f, 0.08f,
            0.2f, 0.15f, 0.08f, 0.05f,
        };

        /// <summary>Contact duration in ms at a medium hit; harder hits shorten it.</summary>
        static readonly float[] ContactMs = { 6f, 3f, 0.7f, 12f, 25f };

        /// <summary>Noise burst level and length (ms) - the slap, knock or rustle.</summary>
        static readonly float[] NoiseLevel = { 0.25f, 0.3f, 0.15f, 0.35f, 1.0f };
        static readonly float[] NoiseMs = { 10f, 8f, 4f, 18f, 180f };

        /// <summary>One-pole low-pass on the noise, Hz: what band the slap sits in.</summary>
        static readonly float[] NoiseCutoff = { 2500f, 3500f, 6000f, 1200f, 4500f };

        /// <summary>Output trim per material so the matrix sits level in the mix.</summary>
        static readonly float[] MaterialGain = { 0.55f, 0.45f, 0.30f, 0.50f, 0.35f };

        struct StrikeEvent
        {
            public int Material;
            public float Amplitude;
            public float Hardness;       // 0 soft .. 1 hard
            public float PitchScale;
            public float GainL, GainR;
            public float LowpassAlpha;   // one-pole coefficient for air absorption
            public float Detune;
        }

        [Tooltip("Air-absorption cutoff at maxDistance, Hz (matches Agent_Audio's _farCutoff).")]
        [SerializeField] private float _farCutoff = 900f;
        [Tooltip("Level at maxDistance relative to a contact at the listener.")]
        [Range(0f, 1f)] [SerializeField] private float _farLevel = 0.25f;

        // Ring buffer: main thread writes _queue[_head], audio thread reads _queue[_tail].
        readonly StrikeEvent[] _queue = new StrikeEvent[QUEUE];
        int _head;
        int _tail;

        // Voice state, flat arrays indexed [voice * MODES + mode]. Audio thread only.
        readonly float[] _y1 = new float[VOICES * MODES];
        readonly float[] _y2 = new float[VOICES * MODES];
        readonly float[] _a1 = new float[VOICES * MODES];
        readonly float[] _a2 = new float[VOICES * MODES];
        readonly float[] _inGain = new float[VOICES * MODES];
        readonly int[] _pulseLeft = new int[VOICES];
        readonly int[] _pulseLength = new int[VOICES];
        readonly int[] _noiseLeft = new int[VOICES];
        readonly int[] _noiseLength = new int[VOICES];
        readonly float[] _noiseLevel = new float[VOICES];
        readonly float[] _noiseAlpha = new float[VOICES];
        readonly float[] _noiseState = new float[VOICES];
        readonly float[] _gainL = new float[VOICES];
        readonly float[] _gainR = new float[VOICES];
        readonly float[] _airAlpha = new float[VOICES];
        readonly float[] _airState = new float[VOICES];
        readonly float[] _energy = new float[VOICES];
        readonly int[] _age = new int[VOICES];
        readonly bool[] _active = new bool[VOICES];

        int _sampleRate = 48000;
        uint _noiseSeed = 0x9E3779B9u;
        uint _detuneSeed = 0x2545F491u;
        AudioClip _carrier;

        /// <summary>Bus gain (mute, master, SFX). Written on the main thread, read per buffer.</summary>
        public float OutputGain { get; set; } = 1f;

        void Awake()
        {
            _sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;

            // A silent looping carrier keeps the source "playing", which is what
            // keeps OnAudioFilterRead being called. A null clip is not guaranteed
            // to: Play() on a clipless source is a no-op on some backends.
            var source = GetComponent<AudioSource>();
            _carrier = AudioClip.Create("impact_synth_carrier", _sampleRate, 1, _sampleRate, false);
            source.clip = _carrier;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.panStereo = 0f;
            source.volume = 1f;
            source.dopplerLevel = 0f;
            source.Play();
        }

        void OnDestroy()
        {
            if (_carrier != null) Destroy(_carrier);
        }

        /// <summary>
        /// Queue a contact. Main thread only. <paramref name="strength"/> is the
        /// normalised impulse (Agent_ImpactScale), <paramref name="level"/> the
        /// loudness the caller has already mixed for it, <paramref name="pan"/>
        /// -1..1 and <paramref name="distance01"/> 0 (at the listener) .. 1 (at
        /// maxDistance). Dropped silently if eight strikes are already waiting.
        /// </summary>
        public void Strike(Surface material, float strength, float level, float pan,
                           float distance01, float pitchScale)
        {
            int next = (_head + 1) & QUEUE_MASK;
            if (next == Volatile.Read(ref _tail)) return;

            // Equal-power pan law.
            float angle = (Mathf.Clamp(pan, -1f, 1f) + 1f) * 0.25f * Mathf.PI;
            float distanceGain = Mathf.Lerp(1f, _farLevel, Mathf.Clamp01(distance01));
            float cutoff = Mathf.Lerp(20000f, Mathf.Max(200f, _farCutoff), Mathf.Clamp01(distance01));

            _detuneSeed = _detuneSeed * 1664525u + 1013904223u;
            float detune = 1f + ((_detuneSeed >> 8) / 16777216f - 0.5f) * 0.08f;

            _queue[_head] = new StrikeEvent
            {
                Material = (int)material,
                Amplitude = Mathf.Clamp01(level) * distanceGain,
                Hardness = Mathf.Clamp01(strength),
                PitchScale = Mathf.Max(0.25f, pitchScale),
                GainL = Mathf.Cos(angle),
                GainR = Mathf.Sin(angle),
                LowpassAlpha = OnePole(cutoff, _sampleRate),
                Detune = detune,
            };
            Volatile.Write(ref _head, next);
        }

        static float OnePole(float cutoffHz, int sampleRate)
        {
            float dt = 1f / sampleRate;
            float rc = 1f / (2f * Mathf.PI * Mathf.Max(20f, cutoffHz));
            return dt / (rc + dt);
        }

        // -- Audio thread ---------------------------------------------------------

        void OnAudioFilterRead(float[] data, int channels)
        {
            DrainQueue();

            float gain = OutputGain;
            int frames = data.Length / Mathf.Max(1, channels);
            for (int frame = 0; frame < frames; frame++)
            {
                float left = 0f, right = 0f;
                for (int voice = 0; voice < VOICES; voice++)
                {
                    if (!_active[voice]) continue;
                    float sample = RenderVoice(voice);
                    left += sample * _gainL[voice];
                    right += sample * _gainR[voice];
                }

                left *= gain;
                right *= gain;
                // Soft clip: a scrum of eight contacts must saturate, not wrap.
                left = left / (1f + Mathf.Abs(left));
                right = right / (1f + Mathf.Abs(right));

                int index = frame * channels;
                if (channels == 1)
                {
                    data[index] += (left + right) * 0.5f;
                }
                else
                {
                    data[index] += left;
                    data[index + 1] += right;
                }
            }
        }

        void DrainQueue()
        {
            int head = Volatile.Read(ref _head);
            while (_tail != head)
            {
                BeginVoice(ref _queue[_tail]);
                Volatile.Write(ref _tail, (_tail + 1) & QUEUE_MASK);
            }
        }

        /// <summary>Allocate a voice (free, else the quietest) and set its resonators.</summary>
        void BeginVoice(ref StrikeEvent strike)
        {
            int voice = -1;
            float quietest = float.PositiveInfinity;
            for (int v = 0; v < VOICES; v++)
            {
                if (!_active[v]) { voice = v; break; }
                if (_energy[v] < quietest) { quietest = _energy[v]; voice = v; }
            }
            if (voice < 0) return;

            int material = Mathf.Clamp(strike.Material, 0, MATERIAL_COUNT - 1);
            float nyquistGuard = _sampleRate * 0.45f;
            float amplitude = strike.Amplitude * MaterialGain[material];

            for (int mode = 0; mode < MODES; mode++)
            {
                int table = material * MODES + mode;
                int slot = voice * MODES + mode;
                float frequency = Mathf.Min(ModeFreq[table] * strike.PitchScale * strike.Detune, nyquistGuard);
                float omega = 2f * Mathf.PI * frequency / _sampleRate;
                // r: per-sample decay so the mode falls 60 dB in T60 seconds.
                float r = Mathf.Pow(10f, -3f / (Mathf.Max(0.005f, ModeT60[table]) * _sampleRate));
                _a1[slot] = 2f * r * Mathf.Cos(omega);
                _a2[slot] = -r * r;
                // sin(omega) normalises the resonator's impulse response to unit peak.
                _inGain[slot] = amplitude * ModeAmp[table] * Mathf.Sin(omega);
                _y1[slot] = 0f;
                _y2[slot] = 0f;
            }

            // Harder hit -> shorter contact -> more high-mode energy (brighter).
            float contactMs = ContactMs[material] * Mathf.Lerp(1.4f, 0.6f, strike.Hardness);
            int pulse = Mathf.Max(1, Mathf.RoundToInt(contactMs * 0.001f * _sampleRate));
            _pulseLength[voice] = pulse;
            _pulseLeft[voice] = pulse;

            int noise = Mathf.Max(1, Mathf.RoundToInt(NoiseMs[material] * 0.001f * _sampleRate));
            _noiseLength[voice] = noise;
            _noiseLeft[voice] = noise;
            _noiseLevel[voice] = amplitude * NoiseLevel[material] * Mathf.Lerp(0.6f, 1.2f, strike.Hardness);
            _noiseAlpha[voice] = OnePole(NoiseCutoff[material] * Mathf.Lerp(0.7f, 1.3f, strike.Hardness), _sampleRate);
            _noiseState[voice] = 0f;

            _gainL[voice] = strike.GainL;
            _gainR[voice] = strike.GainR;
            _airAlpha[voice] = strike.LowpassAlpha;
            _airState[voice] = 0f;
            _energy[voice] = amplitude;
            _age[voice] = 0;
            _active[voice] = true;
        }

        float RenderVoice(int voice)
        {
            // Excitation: a half-sine contact pulse, normalised by its length so a
            // long soft contact delivers the same impulse as a short hard one.
            float drive = 0f;
            if (_pulseLeft[voice] > 0)
            {
                int length = _pulseLength[voice];
                int n = length - _pulseLeft[voice];
                drive = Mathf.Sin(Mathf.PI * (n + 0.5f) / length) * (2f / length);
                _pulseLeft[voice]--;
            }

            float sum = 0f;
            int baseSlot = voice * MODES;
            for (int mode = 0; mode < MODES; mode++)
            {
                int slot = baseSlot + mode;
                float y = _a1[slot] * _y1[slot] + _a2[slot] * _y2[slot] + drive * _inGain[slot];
                _y2[slot] = _y1[slot];
                _y1[slot] = y;
                sum += y;
            }

            if (_noiseLeft[voice] > 0)
            {
                _noiseSeed = _noiseSeed * 1664525u + 1013904223u;
                float white = (_noiseSeed >> 8) / 8388608f - 1f;
                float envelope = _noiseLeft[voice] / (float)_noiseLength[voice];
                _noiseState[voice] += _noiseAlpha[voice] * (white - _noiseState[voice]);
                sum += _noiseState[voice] * _noiseLevel[voice] * envelope * envelope;
                _noiseLeft[voice]--;
            }

            // Air absorption: one-pole low-pass set from the contact's distance.
            _airState[voice] += _airAlpha[voice] * (sum - _airState[voice]);
            float output = _airState[voice];

            // Retire a voice once its excitation is over and it has rung down.
            _age[voice]++;
            if ((_age[voice] & 255) == 0)
            {
                float level = 0f;
                for (int mode = 0; mode < MODES; mode++)
                {
                    level += Mathf.Abs(_y1[baseSlot + mode]) + Mathf.Abs(_y2[baseSlot + mode]);
                }
                _energy[voice] = level;
                if (level < 1e-5f && _pulseLeft[voice] == 0 && _noiseLeft[voice] == 0)
                {
                    _active[voice] = false;
                }
            }
            return output;
        }
    }
}
