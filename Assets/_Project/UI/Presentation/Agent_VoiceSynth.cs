using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Procedural voice: the ADVANCED PROCEDURAL IMITATION (2026-11-22) that stands in
    /// for any character sound whose WAV voices.json marks missing (docs/voices.md).
    /// The engine is <see cref="Render(Agent_VoiceProfile, string, Agent_VoiceMood, int, int)"/>;
    /// the notes below describe the older address scheme it still serves.
    ///
    /// Every sound is addressed as (character, mood, line 1..5, level 1..5).
    /// LINE picks one of five distinct utterances - different vowels, syllable
    /// counts and rhythm, from a seeded generator so the same address always
    /// renders the same sound. LEVEL is expressiveness: 1 is a mutter, 5 is a
    /// shout. It raises loudness, length, pitch range and roughness together,
    /// so the five levels of one line read as the same remark said harder.
    ///
    /// The source is a glottal pulse train (sawtooth through a one-pole low
    /// pass) with jitter and vibrato, plus breath noise, shaped by three formant
    /// band-passes per vowel. Character comes from voicePitch, which moves both
    /// the fundamental and (more gently) the formants - a smaller vocal tract.
    ///
    /// Pure: no Unity object is touched except in <see cref="ToClip"/>, so the
    /// renderer is EditMode-testable and costs nothing until a sound is needed.
    /// </summary>
    public static class Agent_VoiceSynth
    {
        public const int SAMPLE_RATE = 22050;
        public const int LINES = 5;
        public const int LEVELS = 5;

        // F1, F2, F3 (Hz) for a, e, i, o, u - adult averages.
        static readonly float[,] Formants =
        {
            { 800f, 1200f, 2500f },
            { 450f, 1900f, 2600f },
            { 300f, 2300f, 3000f },
            { 500f, 900f, 2400f },
            { 350f, 800f, 2300f },
        };

        /// <summary>Stable seed for a character name (string.GetHashCode is not stable across runtimes).</summary>
        public static uint NameSeed(string name)
        {
            uint hash = 2166136261u;
            if (string.IsNullOrEmpty(name)) return hash;
            for (int charIndex = 0; charIndex < name.Length; charIndex++)
            {
                hash = (hash ^ char.ToUpperInvariant(name[charIndex])) * 16777619u;
            }
            return hash;
        }

        /// <summary>Unity clip for one address. Allocates; callers cache it.</summary>
        public static AudioClip ToClip(string character, Agent_VoiceMood mood, int line, int level, float voicePitch)
        {
            float[] samples = Render(character, mood, line, level, voicePitch);
            var clip = AudioClip.Create($"voice_{character}_{mood}_{line}_{level}", samples.Length, 1, SAMPLE_RATE, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Renders a spoken broadcast commentary cadence from the words/vowels in <paramref name="text"/>.
        /// Exclamation marks and ALL-CAPS lines raise announcer pitch, projection, and excitement.
        /// </summary>
        public static float[] RenderCommentary(string text, float basePitch = 0.92f)
        {
            if (string.IsNullOrEmpty(text)) return new float[SAMPLE_RATE / 10];
            uint state = NameSeed(text);
            if (state == 0) state = 1u;

            bool excited = text.IndexOf('!') >= 0 || string.Equals(text, text.ToUpperInvariant(), System.StringComparison.Ordinal);
            float intensity = excited ? 0.82f : 0.45f;
            int wordCount = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == ' ') wordCount++;
            }
            int syllables = Mathf.Clamp(wordCount + 1, 2, 8);
            float syllableSec = excited ? 0.11f : 0.13f;
            float gapSec = 0.032f;
            float totalSec = 0.03f + syllables * (syllableSec + gapSec);
            int length = Mathf.CeilToInt(totalSec * SAMPLE_RATE);
            int delaySamples = Mathf.RoundToInt(0.065f * SAMPLE_RATE);
            int outLength = length + delaySamples;
            var output = new float[outLength];

            float f0Base = 122f * Mathf.Clamp(basePitch, 0.6f, 1.6f) * (excited ? 1.24f : 1.0f);
            float formantScale = Mathf.Pow(Mathf.Clamp(basePitch, 0.6f, 1.6f), 0.35f);
            float dt = 1f / SAMPLE_RATE;
            float phase = 0f, glottalLow = 0f;
            int cursor = Mathf.RoundToInt(0.015f * SAMPLE_RATE);

            for (int s = 0; s < syllables; s++)
            {
                char seedChar = text[(s * text.Length) / syllables];
                int vowel = VowelForChar(seedChar, ref state);
                bool fricative = "sSzZfFvVhHxX".IndexOf(seedChar) >= 0;
                int count = Mathf.RoundToInt(syllableSec * (0.9f + Next(ref state) * 0.2f) * SAMPLE_RATE);
                float contour = excited
                    ? 1f + 0.18f * Mathf.Sin((s / (float)Mathf.Max(1, syllables - 1)) * Mathf.PI)
                    : 1f - 0.04f * s;
                float syllableF0 = f0Base * contour;

                var r1 = new Resonator(Formants[vowel, 0] * formantScale, 9f);
                var r2 = new Resonator(Formants[vowel, 1] * formantScale, 12f);
                var r3 = new Resonator(Formants[vowel, 2] * formantScale, 15f);

                for (int i = 0; i < count && cursor + i < length; i++)
                {
                    float u = i / (float)count;
                    float bend = Mathf.Lerp(1.03f, excited ? 1.12f : 0.94f, u);
                    float f0 = syllableF0 * bend * (1f + (Next(ref state) - 0.5f) * 0.018f);
                    phase += f0 * dt;
                    if (phase >= 1f) phase -= 1f;
                    float saw = 2f * phase - 1f;
                    glottalLow += 0.35f * (saw - glottalLow);
                    float noise = (Next(ref state) * 2f - 1f) * (u < 0.14f ? (fricative ? 0.55f : 0.32f) : 0.1f);
                    float src = glottalLow * (u < 0.10f && fricative ? 0.35f : 0.9f) + noise;
                    float voiced = r1.Process(src) + r2.Process(src) * 0.6f + r3.Process(src) * 0.3f;
                    float env = Mathf.Clamp01(u / 0.06f) * Mathf.Clamp01((1f - u) / 0.25f);
                    output[cursor + i] += voiced * env;
                }
                cursor += count + Mathf.RoundToInt(gapSec * SAMPLE_RATE);
            }

            // Stadium PA slapback echo (65 ms, damped).
            float paLp = 0f;
            for (int i = outLength - 1; i >= delaySamples; i--)
            {
                paLp += 0.35f * (output[i - delaySamples] - paLp);
                output[i] += paLp * 0.18f;
            }

            float peak = 0f;
            for (int i = 0; i < outLength; i++) peak = Mathf.Max(peak, Mathf.Abs(output[i]));
            float gain = peak > 1e-6f ? Mathf.Lerp(0.42f, 0.85f, intensity) / peak : 0f;
            for (int i = 0; i < outLength; i++) output[i] *= gain;
            return output;
        }

        static int VowelForChar(char c, ref uint state)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'a': return 0;
                case 'e': return 1;
                case 'i': return 2;
                case 'o': return 3;
                case 'u': return 4;
                default: return (int)(Next(ref state) * 5f) % 5;
            }
        }

        /// <summary>Builds a Unity <see cref="AudioClip"/> for a broadcast commentary line.</summary>
        public static AudioClip ToCommentaryClip(string text, float basePitch = 0.92f)
        {
            float[] samples = RenderCommentary(text, basePitch);
            var clip = AudioClip.Create($"commentary_{NameSeed(text)}", samples.Length, 1, SAMPLE_RATE, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Encodes mono [-1, 1] samples into a standard 44-byte header 16-bit PCM RIFF WAVE byte array.
        /// Used by <c>Editor_VoicePack</c> to bake offline WAV files into <c>Assets/_Project/Data/Resources/Voices/</c>.
        /// </summary>
        public static byte[] EncodeWav16(float[] samples, int sampleRate = SAMPLE_RATE)
        {
            int count = samples != null ? samples.Length : 0;
            int dataBytes = count * 2;
            byte[] wav = new byte[44 + dataBytes];

            WriteAscii(wav, 0, "RIFF");
            WriteInt32LE(wav, 4, 36 + dataBytes);
            WriteAscii(wav, 8, "WAVE");
            WriteAscii(wav, 12, "fmt ");
            WriteInt32LE(wav, 16, 16);              // PCM chunk size
            WriteInt16LE(wav, 20, 1);               // AudioFormat = 1 (PCM)
            WriteInt16LE(wav, 22, 1);               // NumChannels = 1 (Mono)
            WriteInt32LE(wav, 24, sampleRate);      // SampleRate
            WriteInt32LE(wav, 28, sampleRate * 2);  // ByteRate
            WriteInt16LE(wav, 32, 2);               // BlockAlign
            WriteInt16LE(wav, 34, 16);              // BitsPerSample
            WriteAscii(wav, 36, "data");
            WriteInt32LE(wav, 40, dataBytes);

            int offset = 44;
            for (int i = 0; i < count; i++)
            {
                short pcm = (short)Mathf.RoundToInt(Mathf.Clamp(samples[i], -1f, 1f) * 32767f);
                wav[offset++] = (byte)(pcm & 0xFF);
                wav[offset++] = (byte)((pcm >> 8) & 0xFF);
            }
            return wav;
        }

        static void WriteAscii(byte[] buf, int offset, string s)
        {
            for (int i = 0; i < s.Length; i++) buf[offset + i] = (byte)s[i];
        }

        static void WriteInt16LE(byte[] buf, int offset, short value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        static void WriteInt32LE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        /// <summary>Renders one utterance from a voicePitch alone (the profile is derived, see Agent_VoiceManifest.ProfileFromPitch).</summary>
        public static float[] Render(string character, Agent_VoiceMood mood, int line, int level, float voicePitch)
            => Render(Agent_VoiceManifest.ProfileFromPitch(character, voicePitch), character, mood, level, line);

        // ---- Advanced procedural imitation (2026-11-22, shared Po voice spec) -----------------

        // F1..F4 (Hz) for a, e, i, o, u, schwa.
        static readonly float[,] Vowels =
        {
            { 800f, 1200f, 2500f, 3500f },
            { 450f, 1900f, 2600f, 3500f },
            { 300f, 2300f, 3000f, 3700f },
            { 500f, 900f, 2400f, 3400f },
            { 350f, 800f, 2300f, 3300f },
            { 500f, 1500f, 2500f, 3500f },
        };
        static readonly float[] Nasal = { 250f, 2000f, 2700f, 3500f };
        static readonly float[] Bandwidths = { 90f, 110f, 160f, 220f };
        static readonly float[] FormantGains = { 1f, 0.7f, 0.4f, 0.25f };
        const int VA = 0, VE = 1, VI = 2, VO = 3, VU = 4, VSCHWA = 5;

        /// <summary>One planned syllable: an optional consonant onset, then a vowel with its own pitch contour.</summary>
        struct Syllable
        {
            public char Onset;        // '\0' none, h, s, S (sh), f, p, t, k (plosives), n (nasal), w, y (glides)
            public int Vowel;
            public float Seconds;     // vowel length
            public float F0Start, F0End, Hump;   // multiples of pitchHz; hump = rise-fall bump
            public float Amp, Gap;
            public bool Unvoiced;     // whispered / gasp / "pfft"
            public bool PitchBreak;   // drops a quarter part-way (hurt)
            public bool Held;         // held final vowel: wide, growing vibrato

            public static Syllable Make(char onset, int vowel, float seconds, float f0Start, float f0End, float gap = 0.04f)
                => new() { Onset = onset, Vowel = vowel, Seconds = seconds, F0Start = f0Start, F0End = f0End, Amp = 1f, Gap = gap };
        }

        static float OnsetSeconds(char onset) => onset switch
        {
            'h' => 0.045f,
            's' => 0.07f,
            'S' => 0.07f,
            'f' => 0.06f,
            'p' => 0.035f,
            't' => 0.035f,
            'k' => 0.04f,
            'n' => 0.05f,
            'w' => 0.04f,
            'y' => 0.035f,
            _ => 0f,
        };

        static float FricativeHz(char onset) => onset switch
        {
            's' => 5000f,
            'S' => 2800f,
            'f' => 1500f,
            'p' => 800f,
            't' => 4000f,
            'k' => 2000f,
            _ => 0f,
        };

        /// <summary>Deterministic seed for one request: (character, emotion, level, take).</summary>
        public static uint Seed(string characterId, Agent_VoiceMood mood, int level, int take)
        {
            uint state = NameSeed(characterId) ^ ((uint)(mood + 1) * 0x9E3779B9u) ^ ((uint)level * 0xC2B2AE35u) ^ ((uint)take * 0x85EBCA6Bu);
            return state == 0 ? 1u : state;
        }

        /// <summary>
        /// The advanced procedural imitation: a short human vocalisation by THIS character.
        /// Source: Rosenberg glottal pulses at the profile's pitch with jitter, shimmer and
        /// aspiration (breath), subharmonic rasp. Filter: four formant resonators gliding
        /// between vowel targets (a, e, i, o, u, schwa) scaled by formantScale. Syllables
        /// with consonant onsets (h/s/sh/f noise, p/t/k bursts, n murmur, w/y glides) and
        /// per-emotion prosody:
        ///   angry     tense, falling, raspy, hard onsets ("hah!", "kah-rrah")
        ///   happy     bright e/i, rise-fall, laugh pulses from level 3 ("heh", "ha-ha-ha")
        ///   hurt      gasp then groan with a downward pitch break; level 5 a long cry
        ///   insult    sing-song minor third, raised F2 (sneer), "pfft" from level 4 ("nyah-nyah")
        ///   celebrate rising whoop with a held vibrato vowel, laugh tail from level 4 ("woo-hoo!")
        /// Level 1..5 scales syllable count, length, pitch range, rasp, breath, vibrato and
        /// loudness. Deterministic per (character, emotion, level, take); callers cache.
        /// </summary>
        public static float[] Render(Agent_VoiceProfile profile, string characterId, Agent_VoiceMood mood, int level, int take)
        {
            if (profile == null || !profile.IsValid) profile = new Agent_VoiceProfile();
            level = Mathf.Clamp(level, 1, LEVELS);
            take = Mathf.Clamp(take, 1, LINES);
            float x = (level - 1) / (float)(LEVELS - 1);
            uint state = Seed(characterId, mood, level, take);

            float tempo = Mathf.Clamp(profile.tempo, 0.6f, 1.6f);
            float rasp = Mathf.Clamp01(profile.rasp);
            float breath = Mathf.Clamp01(profile.breath);
            float vibratoDepth = Mathf.Clamp(profile.vibratoDepth, 0f, 0.08f) * (0.6f + 0.8f * x);
            float f2Scale = 1f;
            bool tense = false, nasal = false;
            float attackSeconds = 0.02f;
            float takePitch = 1f + (Next(ref state) - 0.5f) * 0.08f;     // small take variation
            float takeLength = 0.92f + Next(ref state) * 0.16f;

            var plan = new System.Collections.Generic.List<Syllable>(6);
            switch (mood)
            {
                case Agent_VoiceMood.Angry:
                {
                    int count = 1 + (level >= 3 ? 1 : 0) + (level >= 5 ? 1 : 0);
                    char first = (take % 3) switch { 0 => 'k', 1 => 'h', _ => '\0' };
                    for (int index = 0; index < count; index++)
                    {
                        bool last = index == count - 1;
                        float start = (1.05f + 0.3f * x) * (1f - 0.05f * index);
                        plan.Add(Syllable.Make(index == 0 ? first : 'h', last || take % 2 == 1 ? VA : VSCHWA,
                            (0.14f + 0.2f * x) * (last ? 1.3f : 0.7f), start, start * 0.78f));
                    }
                    rasp = Mathf.Clamp01(rasp + 0.25f + 0.45f * x);
                    breath *= 0.7f;
                    vibratoDepth *= 0.5f;
                    tense = true;
                    attackSeconds = 0.006f;
                    break;
                }
                case Agent_VoiceMood.Happy:
                {
                    if (level <= 2)
                    {
                        var heh = Syllable.Make(take % 2 == 1 ? 'h' : 'y', take % 3 == 0 ? VI : VE, 0.16f + 0.08f * x, 1.08f, 1.12f);
                        heh.Hump = 0.12f;
                        plan.Add(heh);
                    }
                    else
                    {
                        int laughs = level - 1;
                        for (int index = 0; index < laughs; index++)
                        {
                            float start = (1.25f + 0.35f * x) * (1f - 0.04f * index);
                            var ha = Syllable.Make('h', take % 2 == 0 ? VA : VE, 0.085f + 0.02f * x, start, start * 0.94f, 0.045f);
                            ha.Hump = 0.05f;
                            plan.Add(ha);
                        }
                    }
                    vibratoDepth *= 1.2f;
                    breath = Mathf.Clamp01(breath + 0.1f * x);
                    rasp *= 0.3f;
                    break;
                }
                case Agent_VoiceMood.Hurt:
                {
                    var gasp = Syllable.Make('\0', VA, 0.05f + 0.08f * x, 1f, 1f, 0.03f);
                    gasp.Unvoiced = true;
                    gasp.Amp = 0.5f;
                    plan.Add(gasp);
                    if (level <= 2)
                    {
                        plan.Add(Syllable.Make('\0', VU, 0.12f + 0.1f * x, 1f, 0.8f));               // "oof"
                        var coda = Syllable.Make('f', VU, 0.02f, 1f, 1f, 0f);
                        coda.Unvoiced = true;
                        coda.Amp = 0.3f;
                        plan.Add(coda);
                    }
                    else if (level < 5)
                    {
                        var ugh = Syllable.Make('\0', VSCHWA, 0.22f + 0.15f * x, 1.1f + 0.4f * x, 0.65f);    // "ugh"
                        ugh.PitchBreak = true;
                        plan.Add(ugh);
                    }
                    else
                    {
                        var cry = Syllable.Make('\0', VA, 0.75f, 1.7f, 0.75f);                         // a long cry
                        cry.PitchBreak = true;
                        cry.Held = true;
                        plan.Add(cry);
                    }
                    breath = Mathf.Clamp01(breath + 0.25f);
                    rasp = Mathf.Clamp01(rasp + 0.15f * x);
                    break;
                }
                case Agent_VoiceMood.Insult:
                {
                    if (level >= 4)
                    {
                        var pfft = Syllable.Make('p', VU, 0.1f, 1f, 1f, 0.05f);                            // "pfft"
                        pfft.Unvoiced = true;
                        pfft.Amp = 0.45f;
                        plan.Add(pfft);
                    }
                    int count = 2 + (level >= 3 ? 1 : 0);
                    for (int index = 0; index < count; index++)
                    {
                        float start = (1f + 0.2f * x) * (index % 2 == 0 ? 1.19f : 1f);                  // minor third
                        plan.Add(Syllable.Make('n', index % 2 == 0 ? VA : VE, 0.13f + 0.05f * x, start,
                            start * (index == count - 1 ? 0.92f : 1f), 0.035f));
                    }
                    f2Scale = 1.15f;
                    nasal = true;
                    rasp *= 0.6f + 0.4f * x;
                    break;
                }
                default: // Celebrate
                {
                    if (level == 1)
                    {
                        var yeah = Syllable.Make('y', VA, 0.26f, 1.15f, 1.35f);                          // "yeah!"
                        yeah.Hump = 0.05f;
                        plan.Add(yeah);
                    }
                    else
                    {
                        plan.Add(Syllable.Make('w', VO, 0.12f + 0.05f * x, 1.15f + 0.2f * x, 1.25f + 0.25f * x, 0.03f));   // "woo"
                        var hoo = Syllable.Make('h', VU, 0.22f + 0.35f * x, 1.3f + 0.3f * x, 1.55f + 0.45f * x);           // "hoo!"
                        hoo.Held = true;
                        plan.Add(hoo);
                        for (int index = 0; index < level - 3; index++)
                        {
                            plan.Add(Syllable.Make('h', VA, 0.09f, 1.4f, 1.3f, 0.05f));                  // "ha-ha"
                        }
                    }
                    vibratoDepth = Mathf.Max(vibratoDepth, 0.02f) * (1.5f + 1.5f * x);
                    breath = Mathf.Clamp01(breath + 0.05f);
                    break;
                }
            }

            // ---- Synthesis ---------------------------------------------------------------
            float lengthScale = takeLength / tempo;
            float totalSeconds = 0.03f + 0.08f;
            for (int index = 0; index < plan.Count; index++)
            {
                totalSeconds += OnsetSeconds(plan[index].Onset) + (plan[index].Seconds + plan[index].Gap) * lengthScale;
            }
            var output = new float[Mathf.CeilToInt(totalSeconds * SAMPLE_RATE)];

            float pitchHz = Mathf.Clamp(profile.pitchHz, 60f, 420f) * takePitch;
            float formantScale = Mathf.Clamp(profile.formantScale, 0.75f, 1.35f);
            float vibratoHz = Mathf.Clamp(profile.vibratoHz, 3f, 8f);
            float jitter = 0.005f + 0.01f * rasp;
            float shimmer = 0.03f + 0.03f * x;
            float subharmonic = rasp * 0.5f;
            float drive = 1f + rasp * 6f;
            float driveNorm = Tanh(drive);
            float glide = 1f - Mathf.Exp(-1f / (0.025f * SAMPLE_RATE));
            float dt = 1f / SAMPLE_RATE;

            var resonators = new Resonator[4];
            var current = new float[4];
            var target = new float[4];
            for (int formant = 0; formant < 4; formant++)
            {
                current[formant] = Vowels[VSCHWA, formant] * formantScale;
                resonators[formant] = new Resonator(current[formant], current[formant] / Bandwidths[formant]);
            }

            float phase = 0f, flowPrev = 0f, jitterMul = 1f, shimmerMul = 1f, subMul = 1f;
            int period = 0;
            int cursor = Mathf.RoundToInt(0.02f * SAMPLE_RATE);

            for (int index = 0; index < plan.Count; index++)
            {
                var syllable = plan[index];
                int onsetCount = Mathf.RoundToInt(OnsetSeconds(syllable.Onset) * SAMPLE_RATE);
                int vowelCount = Mathf.Max(1, Mathf.RoundToInt(syllable.Seconds * lengthScale * SAMPLE_RATE));
                int count = onsetCount + vowelCount;
                float fricHz = FricativeHz(syllable.Onset);
                var fricative = fricHz > 0f ? new Resonator(fricHz, syllable.Onset == 'f' ? 0.7f : 3f) : default;

                // Glides start from their own vowel; everything else carries on from the last syllable.
                if (syllable.Onset == 'w' || syllable.Onset == 'y')
                {
                    int from = syllable.Onset == 'w' ? VU : VI;
                    for (int formant = 0; formant < 4; formant++) current[formant] = Vowels[from, formant] * formantScale;
                }
                if (syllable.Onset == 'h')
                {
                    for (int formant = 0; formant < 4; formant++) current[formant] = Vowels[syllable.Vowel, formant] * formantScale;
                }

                for (int sampleIndex = 0; sampleIndex < count && cursor + sampleIndex < output.Length; sampleIndex++)
                {
                    bool inOnset = sampleIndex < onsetCount;
                    float onsetU = inOnset ? sampleIndex / (float)Mathf.Max(1, onsetCount) : 1f;
                    float u = inOnset ? 0f : (sampleIndex - onsetCount) / (float)vowelCount;
                    float time = (cursor + sampleIndex) * dt;

                    // Formant targets: the nasal murmur during an 'n', else the vowel (F2 raised for a sneer).
                    for (int formant = 0; formant < 4; formant++)
                    {
                        float hz = inOnset && syllable.Onset == 'n' ? Nasal[formant] : Vowels[syllable.Vowel, formant];
                        if (formant == 1) hz *= f2Scale;
                        if (formant == 0 && nasal) hz *= 0.9f;
                        target[formant] = hz * formantScale;
                        current[formant] += (target[formant] - current[formant]) * glide;
                    }
                    if ((sampleIndex & 15) == 0)
                    {
                        for (int formant = 0; formant < 4; formant++) resonators[formant].Tune(current[formant], current[formant] / Bandwidths[formant]);
                    }

                    // Pitch: contour + rise-fall hump + pitch break + vibrato + per-period jitter.
                    float contour = Mathf.Lerp(syllable.F0Start, syllable.F0End, u) + syllable.Hump * Mathf.Sin(Mathf.PI * u);
                    if (syllable.PitchBreak && u > 0.4f) contour *= 0.75f;
                    float depth = vibratoDepth * (syllable.Held ? 0.3f + 1.2f * u : 1f);
                    float f0 = pitchHz * contour * (1f + depth * Mathf.Sin(2f * Mathf.PI * vibratoHz * time)) * jitterMul;

                    phase += f0 * dt;
                    if (phase >= 1f)
                    {
                        phase -= 1f;
                        period++;
                        jitterMul = 1f + (Next(ref state) - 0.5f) * 2f * jitter;
                        shimmerMul = 1f + (Next(ref state) - 0.5f) * 2f * shimmer;
                        subMul = (period & 1) == 1 ? 1f - subharmonic : 1f;
                    }
                    float flow = Rosenberg(phase, tense);
                    float pulse = (flow - flowPrev) * SAMPLE_RATE / Mathf.Max(f0, 1f) * 0.1f;   // radiated flow derivative
                    flowPrev = flow;
                    float noise = Next(ref state) * 2f - 1f;

                    // Source mix and per-part envelope.
                    float voiced = 0f, aspirated = 0f, frication = 0f;
                    if (inOnset)
                    {
                        switch (syllable.Onset)
                        {
                            case 'h':
                                aspirated = noise * 0.6f * onsetU;
                                break;
                            case 's':
                            case 'S':
                            case 'f':
                                frication = noise * 0.35f * Mathf.Sin(Mathf.PI * onsetU);
                                break;
                            case 'p':
                            case 't':
                            case 'k':
                                // closure silence, then a decaying burst
                                frication = onsetU < 0.4f ? 0f : noise * 0.8f * Mathf.Exp(-(onsetU - 0.4f) * 8f);
                                break;
                            case 'n':
                                voiced = 0.4f * Mathf.Clamp01(onsetU * 4f);
                                break;
                            default: // w, y glides
                                voiced = 0.5f + 0.5f * onsetU;
                                break;
                        }
                    }
                    else
                    {
                        float attack = Mathf.Clamp01(u * vowelCount * dt / attackSeconds);
                        float release = Mathf.Clamp01((1f - u) / (syllable.Held ? 0.35f : 0.25f));
                        float envelope = attack * release * syllable.Amp;
                        if (syllable.Unvoiced)
                        {
                            aspirated = noise * 0.7f * envelope * (0.5f + 0.5f * u);
                            if (fricHz > 0f) frication = noise * 0.4f * envelope;
                        }
                        else
                        {
                            voiced = envelope;
                            aspirated = noise * breath * (0.35f + 0.65f * flow) * envelope;
                        }
                    }

                    float source = voiced * pulse * shimmerMul * subMul + aspirated;
                    float sample = 0f;
                    for (int formant = 0; formant < 4; formant++) sample += resonators[formant].Process(source) * FormantGains[formant];
                    if (fricHz > 0f) sample += fricative.Process(frication);
                    else sample += frication * 0.2f;

                    // Rasp: a growl of amplitude modulation and a soft clip, harder with the level.
                    if (rasp > 0f)
                    {
                        sample *= 1f - 0.3f * rasp * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 31f * time));
                        sample = Tanh(sample * drive) / driveNorm;
                    }
                    output[cursor + sampleIndex] += sample;
                }

                cursor += count + Mathf.RoundToInt(syllable.Gap * lengthScale * SAMPLE_RATE);
            }

            // Normalise to a level-dependent peak, so level is loudness as well as shape and
            // procedural clips sit near recorded (normalised) ones.
            float peak = 0f;
            for (int sampleIndex = 0; sampleIndex < output.Length; sampleIndex++) peak = Mathf.Max(peak, Mathf.Abs(output[sampleIndex]));
            float gain = peak > 1e-6f ? Mathf.Lerp(0.3f, 0.95f, x) / peak : 0f;
            for (int sampleIndex = 0; sampleIndex < output.Length; sampleIndex++) output[sampleIndex] *= gain;
            return output;
        }

        /// <summary>Unity clip for one request with an explicit profile. Allocates; callers cache it.</summary>
        public static AudioClip ToClip(Agent_VoiceProfile profile, string characterId, Agent_VoiceMood mood, int level, int take)
        {
            float[] samples = Render(profile, characterId, mood, level, take);
            var clip = AudioClip.Create($"voice_{characterId}_{mood}_{take}_{level}", samples.Length, 1, SAMPLE_RATE, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>Rosenberg glottal flow over one period (0..1): opening, closing, closed.</summary>
        static float Rosenberg(float phase, bool tense)
        {
            const float open = 0.4f;
            float closing = tense ? 0.1f : 0.16f;
            if (phase < open) return 0.5f * (1f - Mathf.Cos(Mathf.PI * phase / open));
            if (phase < open + closing) return Mathf.Cos(0.5f * Mathf.PI * (phase - open) / closing);
            return 0f;
        }

        /// <summary>xorshift32 in [0, 1).</summary>
        static float Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFF) / 16777216f;
        }

        static float Tanh(float x)
        {
            float e = Mathf.Exp(2f * Mathf.Clamp(x, -10f, 10f));
            return (e - 1f) / (e + 1f);
        }

        /// <summary>RBJ constant-peak band-pass biquad; <see cref="Tune"/> moves it without losing its state (formant glides).</summary>
        struct Resonator
        {
            float _b0, _b2, _a1, _a2;
            float _x1, _x2, _y1, _y2;

            public Resonator(float frequency, float q)
            {
                _b0 = _b2 = _a1 = _a2 = 0f;
                _x1 = _x2 = _y1 = _y2 = 0f;
                Tune(frequency, q);
            }

            public void Tune(float frequency, float q)
            {
                frequency = Mathf.Clamp(frequency, 40f, SAMPLE_RATE * 0.45f);
                q = Mathf.Max(0.3f, q);
                float w0 = 2f * Mathf.PI * frequency / SAMPLE_RATE;
                float alpha = Mathf.Sin(w0) / (2f * q);
                float a0 = 1f + alpha;
                _b0 = alpha / a0;
                _b2 = -alpha / a0;
                _a1 = -2f * Mathf.Cos(w0) / a0;
                _a2 = (1f - alpha) / a0;
            }

            public float Process(float x)
            {
                float y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x;
                _y2 = _y1; _y1 = y;
                return y;
            }
        }
    }
}
