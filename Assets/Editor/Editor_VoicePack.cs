using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Offline Voice & Commentary WAV generator for <c>Assets/Resources/Voices/</c>.
    ///
    /// Menu item: <c>PoSoccer/Generate Voice Pack WAVs</c>.
    /// Writes 16-bit PCM RIFF <c>.wav</c> files for each character (MATT, KIM, NICK,
    /// STANDARD) across moods (HAPPY, ANGRY, HURT, INSULT) so <c>Agent_Voices.ClipFor</c>
    /// loads pre-baked assets from <c>Resources/Voices/&lt;NAME&gt;_&lt;MOOD&gt;_&lt;line&gt;_&lt;level&gt;</c>.
    /// </summary>
    public static class Editor_VoicePack
    {
        public const string OUTPUT_DIR = "Assets/Resources/Voices";
        public const int SAMPLE_RATE = 22050;

        static readonly string[] Characters = { "MATT", "KIM", "NICK", "STANDARD" };
        static readonly float[] Pitches = { 0.92f, 1.18f, 1.02f, 1.00f };
        static readonly string[] Moods = { "HAPPY", "ANGRY", "HURT", "INSULT" };

        [MenuItem("PoSoccer/Generate Voice Pack WAVs")]
        public static void GenerateSamplePack()
        {
            Directory.CreateDirectory(OUTPUT_DIR);
            int written = 0;

            for (int c = 0; c < Characters.Length; c++)
            {
                string character = Characters[c];
                float pitch = Pitches[c];
                for (int m = 0; m < Moods.Length; m++)
                {
                    string mood = Moods[m];
                    string path = $"{OUTPUT_DIR}/{character}_{mood}_1_3.wav";
                    byte[] wav = SynthesizeWav(character, m, 1, 3, pitch);
                    File.WriteAllBytes(path, wav);
                    written++;
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"Editor_VoicePack: wrote {written} WAV files to {OUTPUT_DIR}");
        }

        public static byte[] SynthesizeWav(string character, int mood, int line, int level, float pitch)
        {
            int count = SAMPLE_RATE / 3;
            var samples = new float[count];
            float f0 = 135f * Mathf.Clamp(pitch, 0.5f, 2f) * (1f + mood * 0.08f + level * 0.04f);
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)SAMPLE_RATE;
                float u = i / (float)count;
                float env = Mathf.Clamp01(u / 0.08f) * Mathf.Clamp01((1f - u) / 0.25f);
                samples[i] = Mathf.Sin(2f * Mathf.PI * f0 * t) * 0.55f * env;
            }
            return EncodeWav16(samples, SAMPLE_RATE);
        }

        public static byte[] EncodeWav16(float[] samples, int sampleRate)
        {
            int count = samples != null ? samples.Length : 0;
            int dataBytes = count * 2;
            byte[] wav = new byte[44 + dataBytes];

            wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
            WriteInt32LE(wav, 4, 36 + dataBytes);
            wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
            wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
            WriteInt32LE(wav, 16, 16);
            WriteInt16LE(wav, 20, 1);
            WriteInt16LE(wav, 22, 1);
            WriteInt32LE(wav, 24, sampleRate);
            WriteInt32LE(wav, 28, sampleRate * 2);
            WriteInt16LE(wav, 32, 2);
            WriteInt16LE(wav, 34, 16);
            wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
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
    }
}
