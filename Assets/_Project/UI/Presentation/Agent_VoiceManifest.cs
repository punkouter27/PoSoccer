using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// One character's procedural voice (po-voice-manifest/1 "voice"). Drives
    /// <see cref="Agent_VoiceSynth"/> whenever a clip has no recorded WAV.
    /// </summary>
    [System.Serializable]
    public sealed class Agent_VoiceProfile
    {
        public float pitchHz = 120f;
        public float formantScale = 1f;
        public float breath = 0.2f;
        public float rasp = 0.2f;
        public float vibratoHz = 5.5f;
        public float vibratoDepth = 0.02f;
        public float tempo = 1f;
        public string timbre = "baritone";

        /// <summary>True when the profile carries real values (JsonUtility builds an empty one for a missing field).</summary>
        public bool IsValid => pitchHz > 0f && formantScale > 0f;
    }

    /// <summary>
    /// voices.json (2026-11-22, shared Po spec "po-voice-manifest/1"): the single place
    /// that says which WAV every character uses for every emotion and level, and
    /// whether it exists. Lives next to the WAVs in Resources/Voices.
    ///
    /// Each cell (character x emotion x level) names the cross-project standard file
    /// <c>&lt;id&gt;/&lt;emotion&gt;-&lt;level&gt;.wav</c> and, under "takes", this game's
    /// five per-line takes <c>&lt;NAME&gt;_&lt;MOOD&gt;_&lt;line&gt;_&lt;level&gt;.wav</c>.
    /// "exists" is written by the generator (PoSoccer/Voices/Rebuild voices.json, and
    /// on every WAV import under the folder), never by hand.
    ///
    /// Runtime (<see cref="Agent_Voices.ClipFor"/>): a take that exists plays; else the
    /// standard file if it exists; else the character's "voice" profile drives the
    /// procedural imitation. Files the manifest calls missing are never loaded. A
    /// missing or broken manifest means procedural for everyone (one warning).
    ///
    /// Building and writing are pure C# (no Unity calls), so the generator logic is
    /// testable outside the editor; only <see cref="Loaded"/> touches Unity.
    /// </summary>
    public static class Agent_VoiceManifest
    {
        public const string SCHEMA = "po-voice-manifest/1";
        public const string GAME = "PoSoccer";
        public const string ROOT = "Assets/_Project/Data/Resources/Voices";
        public const string FILE_NAME = "voices.json";
        public const string RESOURCE_PATH = "Voices/voices";

        /// <summary>The standard's emotion order (manifest order, not the enum's).</summary>
        public static readonly Agent_VoiceMood[] Emotions =
        {
            Agent_VoiceMood.Angry, Agent_VoiceMood.Happy, Agent_VoiceMood.Hurt, Agent_VoiceMood.Insult, Agent_VoiceMood.Celebrate,
        };

        [System.Serializable]
        public sealed class Take
        {
            public int line;
            public string file;
            public bool exists;
        }

        [System.Serializable]
        public sealed class Clip
        {
            public int level;
            public string file;
            public bool exists;
            public List<Take> takes = new();

            public Take TakeFor(int line)
            {
                if (takes == null) return null;
                for (int takeIndex = 0; takeIndex < takes.Count; takeIndex++)
                {
                    if (takes[takeIndex] != null && takes[takeIndex].line == line) return takes[takeIndex];
                }
                return null;
            }
        }

        [System.Serializable]
        public sealed class Clips
        {
            public List<Clip> angry = new();
            public List<Clip> happy = new();
            public List<Clip> hurt = new();
            public List<Clip> insult = new();
            public List<Clip> celebrate = new();

            public List<Clip> For(Agent_VoiceMood mood) => mood switch
            {
                Agent_VoiceMood.Angry => angry,
                Agent_VoiceMood.Happy => happy,
                Agent_VoiceMood.Hurt => hurt,
                Agent_VoiceMood.Insult => insult,
                _ => celebrate,
            };
        }

        [System.Serializable]
        public sealed class Character
        {
            public string id;
            public string name;
            public Agent_VoiceProfile voice = new();
            public Clips clips = new();

            public Clip ClipFor(Agent_VoiceMood mood, int level)
            {
                var list = clips?.For(mood);
                if (list == null) return null;
                for (int clipIndex = 0; clipIndex < list.Count; clipIndex++)
                {
                    if (list[clipIndex] != null && list[clipIndex].level == level) return list[clipIndex];
                }
                return null;
            }
        }

        [System.Serializable]
        public sealed class Manifest
        {
            public string schema = SCHEMA;
            public string game = GAME;
            public string generated = "";
            public string root = ROOT;
            public List<string> emotions = new();
            public List<int> levels = new();
            public string fallback = "procedural";
            public List<Character> characters = new();

            public Character Find(string name)
            {
                if (characters == null || string.IsNullOrEmpty(name)) return null;
                for (int characterIndex = 0; characterIndex < characters.Count; characterIndex++)
                {
                    var character = characters[characterIndex];
                    if (character != null && string.Equals(character.name, name, System.StringComparison.OrdinalIgnoreCase)) return character;
                }
                return null;
            }
        }

        /// <summary>One roster entry for the generator.</summary>
        public readonly struct RosterEntry
        {
            public readonly string Name;
            public readonly float VoicePitch;

            public RosterEntry(string name, float voicePitch)
            {
                Name = name;
                VoicePitch = voicePitch;
            }
        }

        public static string EmotionName(Agent_VoiceMood mood) => mood.ToString().ToLowerInvariant();

        public static string CharacterId(string name) => (name ?? "").ToLowerInvariant();

        /// <summary>Standard file (relative to <see cref="ROOT"/>): matt/celebrate-4.wav.</summary>
        public static string StandardFile(string name, Agent_VoiceMood mood, int level)
            => $"{CharacterId(name)}/{EmotionName(mood)}-{level}.wav";

        /// <summary>This game's per-line take (relative to <see cref="ROOT"/>): MATT_CELEBRATE_2_4.wav.</summary>
        public static string TakeFile(string name, Agent_VoiceMood mood, int line, int level)
            => $"{(name ?? "").ToUpperInvariant()}_{mood.ToString().ToUpperInvariant()}_{line}_{level}.wav";

        /// <summary>Resources.Load path for a manifest file: "Voices/" + the file without ".wav".</summary>
        public static string ResourcePathOf(string file)
        {
            string path = file ?? "";
            if (path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
            return "Voices/" + path;
        }

        /// <summary>
        /// A distinct, deterministic voice for a character with no profile yet. Pitch and
        /// vocal-tract size come from the character's voicePitch (as the old synth did);
        /// breath, rasp, vibrato and tempo from a hash of the name, so no two characters
        /// share a voice even at the same pitch.
        /// </summary>
        public static Agent_VoiceProfile ProfileFromPitch(string name, float voicePitch)
        {
            voicePitch = Mathf.Clamp(voicePitch, 0.5f, 2f);
            uint seed = Agent_VoiceSynth.NameSeed(name);
            float Hash(uint salt)
            {
                uint h = (seed ^ salt) * 2654435761u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return (h & 0xFFFF) / 65535f;
            }
            return new Agent_VoiceProfile
            {
                pitchHz = Round(120f * voicePitch, 1),
                formantScale = Round(Mathf.Pow(voicePitch, 0.3f), 3),
                breath = Round(0.12f + 0.2f * Hash(0xA1u), 3),
                rasp = Round(voicePitch > 1.4f ? 0.05f + 0.15f * Hash(0xB2u) : 0.1f + 0.35f * Hash(0xB2u), 3),
                vibratoHz = Round(4.5f + 2f * Hash(0xC3u), 2),
                vibratoDepth = Round(0.012f + 0.02f * Hash(0xD4u), 3),
                tempo = Round(0.9f + 0.25f * Hash(0xE5u), 3),
                timbre = voicePitch < 0.88f ? "deep" : voicePitch < 1f ? "baritone" : voicePitch < 1.3f ? "tenor"
                    : voicePitch < 1.7f ? "alto" : "soprano",
            };
        }

        static float Round(float value, int digits) => (float)System.Math.Round(value, digits);

        /// <summary>
        /// Builds the manifest for <paramref name="roster"/> (every character x 5 emotions x
        /// 5 levels x 5 takes). <paramref name="exists"/> answers for a path relative to
        /// <see cref="ROOT"/>. Voice profiles already in <paramref name="previous"/> are kept.
        /// </summary>
        public static Manifest Build(IList<RosterEntry> roster, System.Func<string, bool> exists, Manifest previous, string generated)
        {
            var manifest = new Manifest { generated = generated ?? "" };
            for (int emotionIndex = 0; emotionIndex < Emotions.Length; emotionIndex++) manifest.emotions.Add(EmotionName(Emotions[emotionIndex]));
            for (int level = 1; level <= Agent_VoiceSynth.LEVELS; level++) manifest.levels.Add(level);

            var seen = new HashSet<string>();
            for (int rosterIndex = 0; rosterIndex < (roster?.Count ?? 0); rosterIndex++)
            {
                string name = (roster[rosterIndex].Name ?? "").ToUpperInvariant();
                if (name.Length == 0 || !seen.Add(name)) continue;
                var kept = previous?.Find(name)?.voice;
                var character = new Character
                {
                    id = CharacterId(name),
                    name = name,
                    voice = kept != null && kept.IsValid ? kept : ProfileFromPitch(name, roster[rosterIndex].VoicePitch),
                };
                for (int emotionIndex = 0; emotionIndex < Emotions.Length; emotionIndex++)
                {
                    var mood = Emotions[emotionIndex];
                    var list = character.clips.For(mood);
                    for (int level = 1; level <= Agent_VoiceSynth.LEVELS; level++)
                    {
                        string file = StandardFile(name, mood, level);
                        var clip = new Clip { level = level, file = file, exists = exists != null && exists(file) };
                        for (int line = 1; line <= Agent_VoiceSynth.LINES; line++)
                        {
                            string take = TakeFile(name, mood, line, level);
                            clip.takes.Add(new Take { line = line, file = take, exists = exists != null && exists(take) });
                        }
                        list.Add(clip);
                    }
                }
                manifest.characters.Add(character);
            }
            return manifest;
        }

        /// <summary>"N of M clips recorded; missing: ..." (a cell counts as recorded when its standard file or any take exists).</summary>
        public static string Summary(Manifest manifest, int maxListed = 10)
        {
            int total = 0, recorded = 0, takes = 0, takesRecorded = 0;
            var missing = new List<string>();
            foreach (var character in manifest.characters)
            {
                foreach (var mood in Emotions)
                {
                    foreach (var clip in character.clips.For(mood))
                    {
                        total++;
                        bool any = clip.exists;
                        foreach (var take in clip.takes)
                        {
                            takes++;
                            if (take.exists) { takesRecorded++; any = true; }
                        }
                        if (any) recorded++;
                        else if (missing.Count < maxListed) missing.Add(clip.file);
                    }
                }
            }
            string list = missing.Count == 0 ? "none" : string.Join(", ", missing) + (total - recorded > missing.Count ? ", ..." : "");
            return $"{recorded} of {total} clips recorded ({takesRecorded} of {takes} per-line takes); missing: {list}";
        }

        /// <summary>Stable, readable JSON: one clip per line. Pure (no JsonUtility), so it runs anywhere.</summary>
        public static string ToJson(Manifest manifest)
        {
            var json = new StringBuilder(256 * 1024);
            json.Append("{\n");
            json.Append("  \"schema\": ").Append(Quote(manifest.schema)).Append(",\n");
            json.Append("  \"game\": ").Append(Quote(manifest.game)).Append(",\n");
            json.Append("  \"generated\": ").Append(Quote(manifest.generated)).Append(",\n");
            json.Append("  \"root\": ").Append(Quote(manifest.root)).Append(",\n");
            json.Append("  \"emotions\": [");
            for (int index = 0; index < manifest.emotions.Count; index++) json.Append(index > 0 ? ", " : "").Append(Quote(manifest.emotions[index]));
            json.Append("],\n");
            json.Append("  \"levels\": [");
            for (int index = 0; index < manifest.levels.Count; index++) json.Append(index > 0 ? ", " : "").Append(manifest.levels[index].ToString(CultureInfo.InvariantCulture));
            json.Append("],\n");
            json.Append("  \"fallback\": ").Append(Quote(manifest.fallback)).Append(",\n");
            json.Append("  \"characters\": [\n");
            for (int characterIndex = 0; characterIndex < manifest.characters.Count; characterIndex++)
            {
                var character = manifest.characters[characterIndex];
                var voice = character.voice;
                json.Append("    {\n");
                json.Append("      \"id\": ").Append(Quote(character.id)).Append(",\n");
                json.Append("      \"name\": ").Append(Quote(character.name)).Append(",\n");
                json.Append("      \"voice\": {")
                    .Append("\"pitchHz\": ").Append(Number(voice.pitchHz))
                    .Append(", \"formantScale\": ").Append(Number(voice.formantScale))
                    .Append(", \"breath\": ").Append(Number(voice.breath))
                    .Append(", \"rasp\": ").Append(Number(voice.rasp))
                    .Append(", \"vibratoHz\": ").Append(Number(voice.vibratoHz))
                    .Append(", \"vibratoDepth\": ").Append(Number(voice.vibratoDepth))
                    .Append(", \"tempo\": ").Append(Number(voice.tempo))
                    .Append(", \"timbre\": ").Append(Quote(voice.timbre))
                    .Append("},\n");
                json.Append("      \"clips\": {\n");
                for (int emotionIndex = 0; emotionIndex < Emotions.Length; emotionIndex++)
                {
                    var list = character.clips.For(Emotions[emotionIndex]);
                    json.Append("        ").Append(Quote(EmotionName(Emotions[emotionIndex]))).Append(": [\n");
                    for (int clipIndex = 0; clipIndex < list.Count; clipIndex++)
                    {
                        var clip = list[clipIndex];
                        json.Append("          {\"level\": ").Append(clip.level)
                            .Append(", \"file\": ").Append(Quote(clip.file))
                            .Append(", \"exists\": ").Append(clip.exists ? "true" : "false")
                            .Append(", \"takes\": [");
                        for (int takeIndex = 0; takeIndex < clip.takes.Count; takeIndex++)
                        {
                            var take = clip.takes[takeIndex];
                            json.Append(takeIndex > 0 ? ", " : "")
                                .Append("{\"line\": ").Append(take.line)
                                .Append(", \"file\": ").Append(Quote(take.file))
                                .Append(", \"exists\": ").Append(take.exists ? "true" : "false").Append('}');
                        }
                        json.Append("]}").Append(clipIndex < list.Count - 1 ? ",\n" : "\n");
                    }
                    json.Append("        ]").Append(emotionIndex < Emotions.Length - 1 ? ",\n" : "\n");
                }
                json.Append("      }\n");
                json.Append("    }").Append(characterIndex < manifest.characters.Count - 1 ? ",\n" : "\n");
            }
            json.Append("  ]\n");
            json.Append("}\n");
            return json.ToString();
        }

        static string Number(float value) => ((double)value).ToString("0.###", CultureInfo.InvariantCulture);

        static string Quote(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // -- Runtime ---------------------------------------------------------------

        static Manifest _loaded;
        static bool _tried;

        /// <summary>The shipped manifest, loaded once from Resources; null (procedural for all) when missing or broken.</summary>
        public static Manifest Loaded
        {
            get
            {
                if (_tried) return _loaded;
                _tried = true;
                var asset = Resources.Load<TextAsset>(RESOURCE_PATH);
                if (asset != null)
                {
                    try
                    {
                        _loaded = JsonUtility.FromJson<Manifest>(asset.text);
                    }
                    catch (System.Exception exception)
                    {
                        Debug.LogWarning($"Agent_VoiceManifest: {RESOURCE_PATH}.json is invalid ({exception.Message}); every voice is procedural.");
                        _loaded = null;
                        return null;
                    }
                }
                if (_loaded == null || _loaded.characters == null || _loaded.schema != SCHEMA)
                {
                    Debug.LogWarning($"Agent_VoiceManifest: no valid Resources/{RESOURCE_PATH}.json; every voice is procedural.");
                    _loaded = null;
                }
                return _loaded;
            }
        }

        /// <summary>Forgets the loaded manifest (the generator calls this after rewriting the file).</summary>
        public static void Reload()
        {
            _loaded = null;
            _tried = false;
        }
    }
}
