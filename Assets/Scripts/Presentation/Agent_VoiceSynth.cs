using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Procedural placeholder voice: a small formant synthesiser that stands in for
    /// any character sound that has no recorded WAV yet (docs/voices.md).
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

        /// <summary>Renders one utterance as mono samples in [-1, 1].</summary>
        public static float[] Render(string character, Agent_VoiceMood mood, int line, int level, float voicePitch)
        {
            line = Mathf.Clamp(line, 1, LINES);
            level = Mathf.Clamp(level, 1, LEVELS);
            float intensity = (level - 1) / (float)(LEVELS - 1);          // 0..1
            voicePitch = Mathf.Clamp(voicePitch, 0.5f, 2f);

            uint state = NameSeed(character) ^ ((uint)mood * 0x9E3779B9u) ^ ((uint)line * 0x85EBCA6Bu);
            if (state == 0) state = 1;

            // ---- The utterance plan: syllables, each with a vowel, a length and a pitch contour.
            int syllables;
            float baseF0 = 135f * voicePitch;
            float syllableSeconds, gapSeconds, startBend, endBend, roughness, breath;
            switch (mood)
            {
                case Agent_VoiceMood.Happy:
                    // "yeah!" at level 1, a laugh at level 5.
                    syllables = 1 + (int)(Next(ref state) * 2f) + Mathf.RoundToInt(intensity * 3f);
                    syllableSeconds = Mathf.Lerp(0.16f, 0.12f, intensity);
                    gapSeconds = Mathf.Lerp(0.06f, 0.035f, intensity);
                    baseF0 *= Mathf.Lerp(1.05f, 1.6f, intensity);
                    startBend = 0.92f; endBend = Mathf.Lerp(1.1f, 1.35f, intensity);
                    roughness = 0.02f; breath = Mathf.Lerp(0.08f, 0.25f, intensity);
                    break;
                case Agent_VoiceMood.Angry:
                    syllables = 1 + (int)(Next(ref state) * 2f) + Mathf.RoundToInt(intensity * 1.5f);
                    syllableSeconds = Mathf.Lerp(0.22f, 0.3f, intensity);
                    gapSeconds = 0.05f;
                    baseF0 *= Mathf.Lerp(0.8f, 1.15f, intensity);
                    startBend = 1.08f; endBend = Mathf.Lerp(0.9f, 0.75f, intensity);
                    roughness = Mathf.Lerp(0.15f, 0.7f, intensity); breath = 0.1f;
                    break;
                case Agent_VoiceMood.Hurt:
                    // "oof" -> "ugh!" -> a yell.
                    syllables = 1 + (intensity > 0.7f && Next(ref state) > 0.5f ? 1 : 0);
                    syllableSeconds = Mathf.Lerp(0.14f, 0.45f, intensity);
                    gapSeconds = 0.04f;
                    baseF0 *= Mathf.Lerp(0.95f, 2.0f, intensity);
                    startBend = Mathf.Lerp(1.05f, 1.25f, intensity); endBend = Mathf.Lerp(0.8f, 0.6f, intensity);
                    roughness = Mathf.Lerp(0.05f, 0.4f, intensity); breath = Mathf.Lerp(0.35f, 0.2f, intensity);
                    break;
                default: // Insult: sing-song taunt, alternating a minor third.
                    syllables = 2 + (int)(Next(ref state) * 2f) + Mathf.RoundToInt(intensity * 2f);
                    syllableSeconds = Mathf.Lerp(0.17f, 0.2f, intensity);
                    gapSeconds = Mathf.Lerp(0.05f, 0.03f, intensity);
                    baseF0 *= Mathf.Lerp(1.0f, 1.3f, intensity);
                    startBend = 1f; endBend = 1f;
                    roughness = Mathf.Lerp(0.02f, 0.25f, intensity); breath = 0.06f;
                    break;
            }

            float totalSeconds = 0.04f + syllables * (syllableSeconds + gapSeconds);
            int length = Mathf.CeilToInt(totalSeconds * SAMPLE_RATE);
            var output = new float[length];

            float formantScale = Mathf.Pow(voicePitch, 0.35f);
            float dt = 1f / SAMPLE_RATE;
            float phase = 0f, glottalLow = 0f;
            float vibratoRate = 5f + Next(ref state) * 2f;
            float vibratoDepth = Mathf.Lerp(0.01f, 0.04f, intensity);

            int cursor = Mathf.RoundToInt(0.02f * SAMPLE_RATE);
            for (int syllable = 0; syllable < syllables; syllable++)
            {
                int vowel = (int)(Next(ref state) * 5f) % 5;
                if (mood == Agent_VoiceMood.Insult) vowel = syllable % 2 == 0 ? 0 : 1;          // "nyah-neh"
                if (mood == Agent_VoiceMood.Hurt) vowel = intensity > 0.6f ? 0 : 4;               // "ugh" / "ah!"
                float lengthJitter = 0.85f + Next(ref state) * 0.3f;
                int count = Mathf.RoundToInt(syllableSeconds * lengthJitter * SAMPLE_RATE);

                float syllableF0 = baseF0;
                if (mood == Agent_VoiceMood.Insult) syllableF0 *= syllable % 2 == 0 ? 1.19f : 1f;
                if (mood == Agent_VoiceMood.Happy) syllableF0 *= 1f + 0.06f * syllable * (1f - intensity * 0.5f);

                var r1 = new Resonator(Formants[vowel, 0] * formantScale, 9f);
                var r2 = new Resonator(Formants[vowel, 1] * formantScale, 12f);
                var r3 = new Resonator(Formants[vowel, 2] * formantScale, 15f);

                for (int sampleIndex = 0; sampleIndex < count && cursor + sampleIndex < length; sampleIndex++)
                {
                    float u = sampleIndex / (float)count;                       // 0..1 through the syllable
                    float time = (cursor + sampleIndex) * dt;
                    float bend = Mathf.Lerp(startBend, endBend, u);
                    float jitter = 1f + (Next(ref state) - 0.5f) * 0.02f * (1f + roughness * 4f);
                    float f0 = syllableF0 * bend * jitter * (1f + vibratoDepth * Mathf.Sin(2f * Mathf.PI * vibratoRate * time));

                    phase += f0 * dt;
                    if (phase >= 1f) phase -= 1f;
                    float saw = 2f * phase - 1f;
                    glottalLow += 0.35f * (saw - glottalLow);                   // soften the buzz
                    float noise = Next(ref state) * 2f - 1f;
                    float source = glottalLow * (1f - breath) + noise * breath;

                    float voiced = r1.Process(source) * 1.0f + r2.Process(source) * 0.6f + r3.Process(source) * 0.3f;

                    // Roughness: soft clip, harder as the level rises (anger, a yell).
                    if (roughness > 0f)
                    {
                        float drive = 1f + roughness * 8f;
                        voiced = Tanh(voiced * drive) / Tanh(drive);
                    }

                    // Envelope: fast attack, sustained body, tapered release.
                    float attack = Mathf.Clamp01(u / 0.08f);
                    float release = Mathf.Clamp01((1f - u) / Mathf.Lerp(0.35f, 0.2f, intensity));
                    output[cursor + sampleIndex] += voiced * attack * release;
                }

                cursor += count + Mathf.RoundToInt(gapSeconds * SAMPLE_RATE);
            }

            // Normalise to a level-dependent peak, so level is loudness as well as shape.
            float peak = 0f;
            for (int sampleIndex = 0; sampleIndex < length; sampleIndex++)
            {
                peak = Mathf.Max(peak, Mathf.Abs(output[sampleIndex]));
            }
            float target = Mathf.Lerp(0.3f, 0.95f, intensity);
            float gain = peak > 1e-6f ? target / peak : 0f;
            for (int sampleIndex = 0; sampleIndex < length; sampleIndex++)
            {
                output[sampleIndex] *= gain;
            }
            return output;
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

        /// <summary>RBJ constant-peak band-pass biquad.</summary>
        struct Resonator
        {
            readonly float _b0, _b2, _a1, _a2;
            float _x1, _x2, _y1, _y2;

            public Resonator(float frequency, float q)
            {
                frequency = Mathf.Min(frequency, SAMPLE_RATE * 0.45f);
                float w0 = 2f * Mathf.PI * frequency / SAMPLE_RATE;
                float alpha = Mathf.Sin(w0) / (2f * q);
                float a0 = 1f + alpha;
                _b0 = alpha / a0;
                _b2 = -alpha / a0;
                _a1 = -2f * Mathf.Cos(w0) / a0;
                _a2 = (1f - alpha) / a0;
                _x1 = _x2 = _y1 = _y2 = 0f;
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
