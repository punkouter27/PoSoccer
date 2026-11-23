using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the voice manifest (Resources/Voices/voices.json, po-voice-manifest/1) and the
    /// advanced procedural imitation that plays whenever a WAV is missing (2026-11-22).
    /// </summary>
    public sealed class Agent_EditMode_VoiceManifest
    {
        static readonly string ManifestPath = Agent_VoiceManifest.ROOT + "/" + Agent_VoiceManifest.FILE_NAME;

        // -- The shipped file ----------------------------------------------------

        [Test]
        public void ShippedManifest_CoversEveryFaceCharacter_FiveEmotionsByFiveLevels()
        {
            Assert.IsTrue(File.Exists(ManifestPath), $"{ManifestPath} is missing: run PoSoccer/Voices/Rebuild voices.json.");
            var manifest = JsonUtility.FromJson<Agent_VoiceManifest.Manifest>(File.ReadAllText(ManifestPath));
            Assert.AreEqual(Agent_VoiceManifest.SCHEMA, manifest.schema);
            CollectionAssert.AreEqual(new[] { "angry", "happy", "hurt", "insult", "celebrate" }, manifest.emotions);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, manifest.levels);

            var roster = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Reward_Settings"))
            {
                var profile = AssetDatabase.LoadAssetAtPath<Reward_Settings>(AssetDatabase.GUIDToAssetPath(guid));
                if (profile != null && profile.faceTexture != null) roster.Add(profile.playerName.ToUpperInvariant());
            }
            Assert.GreaterOrEqual(roster.Count, 16, "the 16 face characters");
            Assert.AreEqual(roster.Count, manifest.characters.Count, "one manifest entry per face character, no BYEs");

            foreach (string name in roster)
            {
                var character = manifest.Find(name);
                Assert.IsNotNull(character, $"{name} is not in voices.json");
                AssertCharacter(character, name, file => File.Exists(Path.Combine(Agent_VoiceManifest.ROOT, file)));
            }
        }

        static void AssertCharacter(Agent_VoiceManifest.Character character, string name, System.Func<string, bool> exists)
        {
            Assert.AreEqual(name.ToLowerInvariant(), character.id);
            Assert.IsNotNull(character.voice);
            Assert.IsTrue(character.voice.IsValid, $"{name} has no voice profile");
            Assert.IsFalse(string.IsNullOrEmpty(character.voice.timbre));
            foreach (var mood in Agent_VoiceManifest.Emotions)
            {
                for (int level = 1; level <= 5; level++)
                {
                    var clip = character.ClipFor(mood, level);
                    Assert.IsNotNull(clip, $"{name} {mood} {level}");
                    Assert.AreEqual($"{name.ToLowerInvariant()}/{mood.ToString().ToLowerInvariant()}-{level}.wav", clip.file);
                    Assert.AreEqual(exists(clip.file), clip.exists, $"{clip.file}: exists flag is stale");
                    Assert.AreEqual(5, clip.takes.Count, $"{clip.file}: five per-line takes");
                    for (int line = 1; line <= 5; line++)
                    {
                        var take = clip.TakeFor(line);
                        Assert.AreEqual($"{name}_{mood.ToString().ToUpperInvariant()}_{line}_{level}.wav", take.file);
                        Assert.AreEqual(exists(take.file), take.exists, $"{take.file}: exists flag is stale");
                    }
                }
            }
        }

        // -- The generator (pure) ----------------------------------------------------

        [Test]
        public void Build_ListsEveryCell_FlagsExistingFiles_AndKeepsVoiceProfiles()
        {
            var roster = new List<Agent_VoiceManifest.RosterEntry>
            {
                new("MATT", 0.92f), new("KIM", 1.5f), new("MATT", 0.92f),
            };
            var recorded = new HashSet<string> { "MATT_CELEBRATE_2_4.wav", "kim/hurt-1.wav" };
            var manifest = Agent_VoiceManifest.Build(roster, recorded.Contains, null, "2026-11-22T00:00:00Z");

            Assert.AreEqual(2, manifest.characters.Count, "duplicates are listed once");
            foreach (var character in manifest.characters) AssertCharacter(character, character.name, recorded.Contains);
            Assert.IsTrue(manifest.Find("MATT").ClipFor(Agent_VoiceMood.Celebrate, 4).TakeFor(2).exists);
            Assert.IsTrue(manifest.Find("KIM").ClipFor(Agent_VoiceMood.Hurt, 1).exists);
            StringAssert.StartsWith("2 of 50 clips recorded (1 of 250 per-line takes); missing: ", Agent_VoiceManifest.Summary(manifest));

            var tuned = manifest.Find("KIM").voice;
            tuned.pitchHz = 333f;
            var rebuilt = Agent_VoiceManifest.Build(roster, recorded.Contains, manifest, "");
            Assert.AreEqual(333f, rebuilt.Find("KIM").voice.pitchHz, "a profile already in the file is kept");

            string json = Agent_VoiceManifest.ToJson(rebuilt);
            StringAssert.Contains("\"schema\": \"po-voice-manifest/1\"", json);
            StringAssert.Contains("\"file\": \"MATT_CELEBRATE_2_4.wav\", \"exists\": true", json);
            StringAssert.Contains("\"file\": \"matt/angry-1.wav\", \"exists\": false", json);
        }

        [Test]
        public void ProfileFromPitch_GivesEveryRosterCharacterADifferentVoice()
        {
            string[] names = { "MATT", "KIM", "NICK", "ALAN", "BAILY", "BRENDAN", "DECOY", "DUPEE", "JH", "JOSH", "MANZI", "MAYA", "PORTO", "SOKOL", "TONG", "VELEZ" };
            var seen = new HashSet<string>();
            foreach (string name in names)
            {
                var voice = Agent_VoiceManifest.ProfileFromPitch(name, 1f);   // same pitch on purpose
                Assert.IsTrue(voice.IsValid);
                Assert.IsTrue(seen.Add($"{voice.breath}|{voice.rasp}|{voice.vibratoHz}|{voice.tempo}"), $"{name} shares a voice");
            }
            Assert.AreEqual("deep", Agent_VoiceManifest.ProfileFromPitch("X", 0.8f).timbre);
            Assert.AreEqual("soprano", Agent_VoiceManifest.ProfileFromPitch("X", 2f).timbre);
        }

        // -- The procedural imitation ------------------------------------------------

        [Test]
        public void Procedural_EveryEmotion_IsAudible_AndLevelFiveIsLouderAndLonger()
        {
            var voice = Agent_VoiceManifest.ProfileFromPitch("SOKOL", 0.85f);
            foreach (var mood in Agent_VoiceManifest.Emotions)
            {
                float[] weak = Agent_VoiceSynth.Render(voice, "sokol", mood, 1, 1);
                float[] strong = Agent_VoiceSynth.Render(voice, "sokol", mood, 5, 1);
                Assert.Greater(Peak(weak), 0.1f, $"{mood} level 1 is silent");
                Assert.Greater(Rms(weak), 0.01f, $"{mood} level 1 has no body");
                Assert.Greater(Peak(strong), Peak(weak) * 2f, $"{mood}: level 5 should be far louder");
                Assert.Greater(strong.Length, weak.Length, $"{mood}: level 5 should be longer");
                Assert.LessOrEqual(Peak(strong), 1f, $"{mood} clips");
                foreach (float sample in strong) Assert.IsFalse(float.IsNaN(sample), $"{mood} produced NaN");
            }
        }

        [Test]
        public void Procedural_IsDeterministic_AndCharactersAndTakesDiffer()
        {
            var matt = Agent_VoiceManifest.ProfileFromPitch("MATT", 0.92f);
            var maya = Agent_VoiceManifest.ProfileFromPitch("MAYA", 1.9f);
            foreach (var mood in Agent_VoiceManifest.Emotions)
            {
                float[] once = Agent_VoiceSynth.Render(matt, "matt", mood, 3, 2);
                CollectionAssert.AreEqual(once, Agent_VoiceSynth.Render(matt, "matt", mood, 3, 2), $"{mood} is not deterministic");
                Assert.IsFalse(Same(once, Agent_VoiceSynth.Render(maya, "maya", mood, 3, 2)), $"{mood}: MATT and MAYA sound the same");
                Assert.IsFalse(Same(once, Agent_VoiceSynth.Render(matt, "matt", mood, 3, 3)), $"{mood}: two takes are identical");
            }
        }

        static float Peak(float[] samples)
        {
            float peak = 0f;
            foreach (float sample in samples) peak = Mathf.Max(peak, Mathf.Abs(sample));
            return peak;
        }

        static float Rms(float[] samples)
        {
            double sum = 0;
            foreach (float sample in samples) sum += sample * sample;
            return (float)System.Math.Sqrt(sum / Mathf.Max(1, samples.Length));
        }

        static bool Same(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int index = 0; index < a.Length; index++) if (Mathf.Abs(a[index] - b[index]) > 1e-6f) return false;
            return true;
        }
    }
}
