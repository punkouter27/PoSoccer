using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Voice tooling for <c>Assets/_Project/Data/Resources/Voices/</c>.
    ///
    /// <c>PoSoccer/Voices/Rebuild voices.json</c> (also run automatically whenever a WAV under
    /// the folder is imported, moved or deleted) scans the folder and rewrites
    /// <c>voices.json</c> (<see cref="Agent_VoiceManifest"/>): every face character x 5
    /// emotions x 5 levels, the standard file and the five per-line takes of each cell,
    /// their <c>exists</c> flags, and each character's voice profile (profiles already in
    /// the file are kept, so hand-tuned voices survive). It logs
    /// "N of M clips recorded; missing: ...".
    ///
    /// <c>PoSoccer/Voices/Bake sample WAVs</c> renders one take per mood (level 3) for the
    /// shipped four with the procedural engine, as a test of the WAV path.
    /// </summary>
    public static class Editor_VoicePack
    {
        public const string OUTPUT_DIR = Agent_VoiceManifest.ROOT;
        public const int SAMPLE_RATE = Agent_VoiceSynth.SAMPLE_RATE;

        static readonly string[] Characters = { "MATT", "KIM", "NICK", "STANDARD" };
        static readonly float[] Pitches = { 0.92f, 1.5f, 1.25f, 1.00f };

        static string ManifestPath => OUTPUT_DIR + "/" + Agent_VoiceManifest.FILE_NAME;

        [MenuItem("PoSoccer/Voices/Rebuild voices.json")]
        public static void RebuildManifestMenu() => Debug.Log("Editor_VoicePack: " + RebuildManifest());

        /// <summary>Rewrites voices.json from the roster and the folder; returns the summary line.</summary>
        public static string RebuildManifest()
        {
            Directory.CreateDirectory(OUTPUT_DIR);
            Agent_VoiceManifest.Manifest previous = null;
            string before = File.Exists(ManifestPath) ? File.ReadAllText(ManifestPath) : null;
            if (before != null)
            {
                try { previous = JsonUtility.FromJson<Agent_VoiceManifest.Manifest>(before); }
                catch (System.Exception exception) { Debug.LogWarning($"Editor_VoicePack: {ManifestPath} unreadable ({exception.Message}); rebuilding."); }
            }

            var roster = Roster();
            bool Exists(string file) => File.Exists(Path.Combine(OUTPUT_DIR, file));
            // Keep the old timestamp when nothing else changed, so a no-op rebuild is no diff.
            var manifest = Agent_VoiceManifest.Build(roster, Exists, previous, previous?.generated ?? "");
            string json = Agent_VoiceManifest.ToJson(manifest);
            if (json != before)
            {
                manifest.generated = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                File.WriteAllText(ManifestPath, Agent_VoiceManifest.ToJson(manifest));
                AssetDatabase.ImportAsset(ManifestPath);
                Agent_VoiceManifest.Reload();
            }
            return Agent_VoiceManifest.Summary(manifest);
        }

        /// <summary>Every character with a face (the 16 of the menu grid), by name.</summary>
        static List<Agent_VoiceManifest.RosterEntry> Roster()
        {
            var roster = new List<Agent_VoiceManifest.RosterEntry>();
            foreach (string guid in AssetDatabase.FindAssets("t:Reward_Settings"))
            {
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(AssetDatabase.GUIDToAssetPath(guid));
                if (profile == null || profile.faceTexture == null || string.IsNullOrEmpty(profile.playerName)) continue;
                roster.Add(new Agent_VoiceManifest.RosterEntry(profile.playerName, profile.voicePitch));
            }
            roster.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return roster;
        }

        /// <summary>Keeps voices.json's exists flags true: any WAV change under the folder rebuilds it.</summary>
        sealed class Watcher : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (Touches(imported) || Touches(deleted) || Touches(moved) || Touches(movedFrom))
                {
                    EditorApplication.delayCall -= RebuildManifestMenu;
                    EditorApplication.delayCall += RebuildManifestMenu;
                }
            }

            static bool Touches(string[] paths)
            {
                foreach (string path in paths)
                {
                    if (path.StartsWith(OUTPUT_DIR + "/", System.StringComparison.Ordinal)
                        && path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
        }

        [MenuItem("PoSoccer/Voices/Bake sample WAVs")]
        public static void GenerateSamplePack()
        {
            Directory.CreateDirectory(OUTPUT_DIR);
            int written = 0;
            for (int characterIndex = 0; characterIndex < Characters.Length; characterIndex++)
            {
                foreach (Agent_VoiceMood mood in System.Enum.GetValues(typeof(Agent_VoiceMood)))
                {
                    string path = $"{OUTPUT_DIR}/{Agent_VoiceManifest.TakeFile(Characters[characterIndex], mood, 1, 3)}";
                    File.WriteAllBytes(path, SynthesizeWav(Characters[characterIndex], (int)mood, 1, 3, Pitches[characterIndex]));
                    written++;
                }
            }
            AssetDatabase.Refresh();
            Debug.Log($"Editor_VoicePack: wrote {written} WAV files to {OUTPUT_DIR}");
        }

        /// <summary>One procedural take as a 16-bit PCM WAV.</summary>
        public static byte[] SynthesizeWav(string character, int mood, int line, int level, float pitch)
            => Agent_VoiceSynth.EncodeWav16(Agent_VoiceSynth.Render(character, (Agent_VoiceMood)mood, line, level, pitch), SAMPLE_RATE);
    }
}
