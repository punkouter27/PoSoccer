using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Builds the 16-character roster from the user-supplied faces (2026-11-08).
    ///
    /// - Imports Assets/Art/Faces/FACE_*.png (made by scripts/import-faces.py)
    ///   as clamped, 512 px, uncompressed-alpha-free textures.
    /// - Creates a Reward_Settings asset for each NEW character in
    ///   Assets/Agents/&lt;Name&gt;_v01/Reward_&lt;NAME&gt;.asset, cloned from STANDARD
    ///   with its own reward weights (its skills - play style only, the body is
    ///   STANDARD's) and with no brain and no training provenance.
    /// - Sets face, skills line and voice pitch on every character, including the
    ///   existing MATT, KIM and NICK (presentation fields only - their reward
    ///   weights and brains are untouched). STANDARD stays untextured (CLAUDE.md).
    /// - Wires all 16 into Agent_MainMenu.characters in SCN_Menu.
    ///
    /// Idempotent: an existing character asset keeps its reward weights and only
    /// has its presentation fields refreshed, so re-running never overwrites
    /// tuning or a trained brain.
    ///
    /// SKILLS STAY INSIDE THE REWARD GUARDS. stepPenalty, ballProximityScale,
    /// actionJitterScale and facingAlignmentScale are pinned to the code defaults
    /// for every profile (Agent_EditMode_SoccerAgent), and no dense term may
    /// out-pay a goal (Agent_EditMode_RewardBudget); each character's skills are
    /// spent on the terms those tests leave free, at KIM / NICK magnitudes.
    ///
    /// Written against SerializedObject by field name because this assembly
    /// (PoSoccer.Editor.Build) does not reference the runtime assembly.
    ///
    ///   scripts/unity-exec.ps1 -Method PoSoccer.EditorTools.Editor_CreateCharacters.Run
    /// </summary>
    public static class Editor_CreateCharacters
    {
        private const string FACES = "Assets/Art/Faces";
        private const string AGENTS = "Assets/Agents";
        private const string STANDARD = "Assets/Agents/Standard_v01/Reward_STANDARD.asset";
        private const string MENU_SCENE = "Assets/Scenes/SCN_Menu.unity";
        private const string TRAINING_SCENE = "Assets/Scenes/SCN_Training.unity";

        /// <summary>One character: identity, skills, and reward multipliers on STANDARD's table.</summary>
        private sealed class Spec
        {
            public string Name;
            public string Folder;
            public Color Color;
            public float Pitch;
            public string Skills;
            public string Notes;
            public bool Existing;
            public Dictionary<string, float> Scale = new();
            public Dictionary<string, float> Set = new();
        }

        private static List<Spec> Roster()
        {
            var roster = new List<Spec>
            {
                Existing("MATT", "Matt_v01", 0.92f, "Striker: shoots on sight, presses hard, weak at the back"),
                Existing("KIM", "Kim_v01", 1.5f, "Wall: screens the goal, hates conceding"),
                Existing("NICK", "Nick_v01", 1.25f, "Midfielder: keeps the ball, builds the play"),

                New("ALAN", "Alan_v01", new Color(0.2f, 0.35f, 0.75f), 0.95f,
                    "Captain: plays for the team, lives for the assist",
                    "THE CAPTAIN. The assist and the team win outweigh his own goals: sets up rather than shoots.",
                    scale: new() { ["assist"] = 1.4f, ["teamBaselineVictory"] = 1.2f, ["goalScorer"] = 1.15f }),
                New("BAILY", "Baily_v01", new Color(0.98f, 0.55f, 0.75f), 1.45f,
                    "Sprinter: always on the ball, shoots quickly",
                    "THE SPRINTER. Double the touch reward and a bigger shot-speed bonus: lives on the ball and hits it early.",
                    scale: new() { ["ballContact"] = 2f, ["goalSpeedBonus"] = 1.5f, ["ballToGoalVelocityScale"] = 1.3f }),
                New("BRENDAN", "Brendan_v01", new Color(0.55f, 0.55f, 0.6f), 0.8f,
                    "Veteran: patient, positional, never rushed",
                    "THE VETERAN. Paid to hold the screening position, stays central, and a draw barely stings: never rushed.",
                    scale: new() { ["wallProximityPenalty"] = 1.5f, ["stalemateTimeout"] = 0.5f },
                    set: new() { ["defensivePositionScale"] = 0.00004f }),
                New("DECOY", "Decoy_v01", new Color(0.35f, 0.8f, 0.55f), 1.0f,
                    "Trickster: lots of touches, hard to read",
                    "THE TRICKSTER. Triple the touch reward and paid to keep the ball close: dribbles, jinks, holds on.",
                    scale: new() { ["ballContact"] = 3f }, set: new() { ["possessionScale"] = 0.00004f }),
                New("DUPEE", "Dupee_v01", new Color(0.75f, 0.3f, 0.2f), 0.9f,
                    "Bulldozer: physical, goes through people",
                    "THE BULLDOZER. Four times the touch reward and half the wall and corner caution: goes through, not round.",
                    scale: new() { ["ballContact"] = 4f, ["wallProximityPenalty"] = 0.5f, ["cornerBallPenalty"] = 0.5f }),
                New("JH", "Jh_v01", new Color(0.95f, 0.75f, 0.1f), 0.85f,
                    "Sniper: shoots from distance, hits it hard",
                    "THE SNIPER. Four times the shot-speed bonus and double the ball-to-goal velocity term: long, hard shots.",
                    scale: new() { ["goalSpeedBonus"] = 4f, ["ballToGoalVelocityScale"] = 2f }),
                New("JOSH", "Josh_v01", new Color(0.4f, 0.25f, 0.6f), 1.0f,
                    "Grinder: never settles for a draw",
                    "THE GRINDER. The deepest stalemate penalty on the roster: chases a result to the final whistle.",
                    scale: new() { ["stalemateTimeout"] = 1.6f, ["goalScorer"] = 1.1f }),
                New("MANZI", "Manzi_v01", new Color(0.15f, 0.5f, 0.45f), 0.88f,
                    "Rock: goalkeeper's instincts, stays home",
                    "THE ROCK. Biggest fear of conceding and the strongest screening reward: a keeper in outfield clothes.",
                    scale: new() { ["goalConceded"] = 1.5f }, set: new() { ["defensivePositionScale"] = 0.00008f }),
                New("MAYA", "Maya_v01", new Color(1f, 0.45f, 0.55f), 1.9f,
                    "Prodigy: always moving the ball forward",
                    "THE PRODIGY. Strongest progress-to-goal shaping, matched by a bigger scoring reward: every touch goes forward.",
                    scale: new() { ["ballToGoalProgressScale"] = 1.2f, ["goalScorer"] = 1.2f }),
                New("PORTO", "Porto_v01", new Color(0.9f, 0.4f, 0.1f), 0.97f,
                    "Showman: flair, power shots, all touch",
                    "THE SHOWMAN. Double shot-speed and touch rewards: flashy, powerful, plays to the crowd.",
                    scale: new() { ["goalSpeedBonus"] = 2f, ["ballContact"] = 2f }),
                New("SOKOL", "Sokol_v01", new Color(0.6f, 0.1f, 0.15f), 0.85f,
                    "Enforcer: wins it back, defends hard",
                    "THE ENFORCER. A defender's fear of conceding, a screening reward and more for every touch: wins the ball back.",
                    scale: new() { ["goalConceded"] = 1.3f, ["ballContact"] = 1.5f }, set: new() { ["defensivePositionScale"] = 0.00004f }),
                New("TONG", "Tong_v01", new Color(0.5f, 0.85f, 0.95f), 2.0f,
                    "Wildcard: chaotic, fearless, unpredictable",
                    "THE WILDCARD. Double touch reward, half the wall caution, a draw barely matters: nobody knows what comes next.",
                    scale: new() { ["ballContact"] = 2f, ["wallProximityPenalty"] = 0.5f, ["stalemateTimeout"] = 0.5f }),
                New("VELEZ", "Velez_v01", new Color(0.25f, 0.6f, 0.2f), 0.9f,
                    "Poacher: lurks near goal, finishes chances",
                    "THE POACHER. Biggest scoring reward on the roster with matching progress shaping, half the assist: finishes.",
                    scale: new() { ["goalScorer"] = 1.4f, ["ballToGoalProgressScale"] = 1.3f, ["assist"] = 0.5f }),
            };
            return roster;
        }

        private static Spec Existing(string name, string folder, float pitch, string skills)
            => new Spec { Name = name, Folder = folder, Pitch = pitch, Skills = skills, Existing = true };

        private static Spec New(string name, string folder, Color color, float pitch, string skills, string notes,
            Dictionary<string, float> scale = null, Dictionary<string, float> set = null)
            => new Spec
            {
                Name = name, Folder = folder, Color = color, Pitch = pitch, Skills = skills, Notes = notes,
                Scale = scale ?? new Dictionary<string, float>(), Set = set ?? new Dictionary<string, float>(),
            };

        [MenuItem("PoSoccer/Create Characters From Faces")]
        public static void Run()
        {
            int code = 0;
            try
            {
                code = Create() ? 0 : 1;
            }
            catch (System.Exception exception)
            {
                Debug.LogError("CHARACTERS: " + exception);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        private static bool Create()
        {
            ImportFaces();

            var standard = AssetDatabase.LoadAssetAtPath<ScriptableObject>(STANDARD);
            if (standard == null)
            {
                Debug.LogError($"CHARACTERS: {STANDARD} not found.");
                return false;
            }

            var standardSo = new SerializedObject(standard);
            standardSo.FindProperty("skills").stringValue = "Baseline: balanced all-rounder, the reference brain";
            standardSo.FindProperty("voicePitch").floatValue = 1f;
            standardSo.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(standard);

            var all = new List<string>();
            foreach (var spec in Roster())
            {
                string folder = $"{AGENTS}/{spec.Folder}";
                string path = $"{folder}/Reward_{spec.Name}.asset";
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

                if (asset == null)
                {
                    if (spec.Existing)
                    {
                        Debug.LogError($"CHARACTERS: expected existing {path}.");
                        return false;
                    }
                    if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(AGENTS, spec.Folder);
                    asset = Object.Instantiate(standard);
                    asset.name = $"Reward_{spec.Name}";
                    AssetDatabase.CreateAsset(asset, path);
                    ApplyNewCharacter(asset, spec);
                    Debug.Log($"CHARACTERS: created {path}");
                }

                var so = new SerializedObject(asset);
                var face = AssetDatabase.LoadAssetAtPath<Texture2D>($"{FACES}/FACE_{spec.Name}.png");
                if (face == null) Debug.LogWarning($"CHARACTERS: no face for {spec.Name}");
                so.FindProperty("faceTexture").objectReferenceValue = face;
                so.FindProperty("skills").stringValue = spec.Skills;
                so.FindProperty("voicePitch").floatValue = spec.Pitch;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
                all.Add(path);
            }
            AssetDatabase.SaveAssets();

            if (!WireMenu(all)) return false;
            var trainable = new List<string> { STANDARD };
            trainable.AddRange(all);
            if (!WireTraining(trainable)) return false;
            AssignMissingBrains();
            Debug.Log($"CHARACTERS RESULT: OK, {all.Count} characters wired into {MENU_SCENE}, " +
                      $"{trainable.Count} profiles into {TRAINING_SCENE}");
            return true;
        }

        /// <summary>Identity, skills as reward weights, and a clean slate (no brain, no provenance).</summary>
        private static void ApplyNewCharacter(ScriptableObject asset, Spec spec)
        {
            var so = new SerializedObject(asset);
            so.FindProperty("playerName").stringValue = spec.Name;
            so.FindProperty("personalityNotes").stringValue = spec.Notes;
            so.FindProperty("playerColor").colorValue = spec.Color;
            so.FindProperty("brainModel").objectReferenceValue = null;
            so.FindProperty("trainingSteps").intValue = 0;
            so.FindProperty("trainingRunId").stringValue = "";
            so.FindProperty("trainedOn").stringValue = "";
            so.FindProperty("evalWinRate").floatValue = -1f;
            so.FindProperty("evalEpisodes").intValue = 0;
            so.FindProperty("trainedActionGain").floatValue = -1f;
            so.FindProperty("trainedInterfaceVersion").stringValue = "";
            so.FindProperty("trainedPhysicsVersion").stringValue = "";
            so.FindProperty("trainingConfigHash").stringValue = "";

            foreach (var pair in spec.Scale)
            {
                var property = so.FindProperty(pair.Key);
                if (property == null) throw new System.Exception($"{spec.Name}: no reward field '{pair.Key}'");
                property.floatValue *= pair.Value;
            }
            foreach (var pair in spec.Set)
            {
                var property = so.FindProperty(pair.Key);
                if (property == null) throw new System.Exception($"{spec.Name}: no reward field '{pair.Key}'");
                property.floatValue = pair.Value;
            }

            // Same terminal budget as STANDARD: skills REDISTRIBUTE the stakes
            // (goal, assist, team win, conceding), they never add to them, so no
            // character is simply more motivated than another (docs/players.md).
            const float TERMINAL_BUDGET = 4.0f;
            string[] terminal = { "goalScorer", "assist", "teamBaselineVictory", "goalConceded" };
            float total = 0f;
            foreach (string field in terminal) total += Mathf.Abs(so.FindProperty(field).floatValue);
            if (total > 1e-4f)
            {
                float rescale = TERMINAL_BUDGET / total;
                foreach (string field in terminal) so.FindProperty(field).floatValue *= rescale;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ImportFaces()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { FACES });
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;
                bool changed = importer.textureType != TextureImporterType.Default
                               || importer.maxTextureSize != 512
                               || importer.wrapMode != TextureWrapMode.Clamp
                               || importer.mipmapEnabled;
                importer.textureType = TextureImporterType.Default;
                importer.maxTextureSize = 512;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = false;
                importer.sRGBTexture = true;
                importer.alphaSource = TextureImporterAlphaSource.None;
                importer.textureCompression = TextureImporterCompression.Compressed;
                if (changed) importer.SaveAndReimport();
            }
        }

        /// <summary>
        /// Fills an empty brainModel from the &lt;NAME&gt;.onnx beside the profile: the
        /// one-time step update-model.ps1 used to leave to a hand in the Inspector
        /// when a character's first brain landed. Never replaces an assigned brain.
        ///
        ///   scripts/unity-exec.ps1 -Method PoSoccer.EditorTools.Editor_CreateCharacters.AssignBrains
        /// </summary>
        public static void AssignBrains()
        {
            int code = 0;
            try
            {
                Debug.Log($"BRAINS RESULT: OK, {AssignMissingBrains()} newly assigned");
            }
            catch (System.Exception exception)
            {
                Debug.LogError("BRAINS: " + exception);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        private static int AssignMissingBrains()
        {
            int assigned = 0;
            foreach (string guid in AssetDatabase.FindAssets("Reward_ t:ScriptableObject", new[] { AGENTS }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (profile == null) continue;
                var so = new SerializedObject(profile);
                var brain = so.FindProperty("brainModel");
                var name = so.FindProperty("playerName");
                if (brain == null || name == null || brain.objectReferenceValue != null) continue;

                string onnx = $"{System.IO.Path.GetDirectoryName(path).Replace('\\', '/')}/{name.stringValue}.onnx";
                var model = AssetDatabase.LoadMainAssetAtPath(onnx);
                if (model == null) continue;
                brain.objectReferenceValue = model;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(profile);
                assigned++;
                Debug.Log($"BRAINS: {name.stringValue} <- {onnx}");
            }
            AssetDatabase.SaveAssets();
            return assigned;
        }

        /// <summary>
        /// SCN_Training's profileRoster is what POSOCCER_PROFILE (training and
        /// evaluate.ps1 -Profile) resolves names against: every trainable profile
        /// has to be in it, or a run for that character trains STANDARD's table.
        /// </summary>
        private static bool WireTraining(List<string> profilePaths)
        {
            var scene = EditorSceneManager.OpenScene(TRAINING_SCENE, OpenSceneMode.Single);
            int wired = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour.GetType().Name != "Agent_EnvController") continue;
                    var so = new SerializedObject(behaviour);
                    var array = so.FindProperty("profileRoster");
                    array.arraySize = profilePaths.Count;
                    for (int profileIndex = 0; profileIndex < profilePaths.Count; profileIndex++)
                    {
                        var profile = AssetDatabase.LoadAssetAtPath<ScriptableObject>(profilePaths[profileIndex]);
                        if (profile == null)
                        {
                            Debug.LogError($"CHARACTERS: could not load {profilePaths[profileIndex]}");
                            return false;
                        }
                        array.GetArrayElementAtIndex(profileIndex).objectReferenceValue = profile;
                    }
                    so.ApplyModifiedPropertiesWithoutUndo();
                    wired++;
                }
            }
            if (wired == 0)
            {
                Debug.LogError("CHARACTERS: no Agent_EnvController in SCN_Training.");
                return false;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            return EditorSceneManager.SaveScene(scene);
        }

        private static bool WireMenu(List<string> characterPaths)
        {
            var scene = EditorSceneManager.OpenScene(MENU_SCENE, OpenSceneMode.Single);
            Component menu = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour != null && behaviour.GetType().Name == "Agent_MainMenu") menu = behaviour;
                }
            }
            if (menu == null)
            {
                Debug.LogError("CHARACTERS: no Agent_MainMenu in SCN_Menu.");
                return false;
            }

            // Reloaded by path AFTER the scene opens: opening a scene in Single mode
            // unloads objects created earlier in this call, and a stale reference
            // serialises as {fileID: 0} without any error.
            var so = new SerializedObject(menu);
            var array = so.FindProperty("characters");
            array.arraySize = characterPaths.Count;
            for (int characterIndex = 0; characterIndex < characterPaths.Count; characterIndex++)
            {
                var character = AssetDatabase.LoadAssetAtPath<ScriptableObject>(characterPaths[characterIndex]);
                if (character == null)
                {
                    Debug.LogError($"CHARACTERS: could not reload {characterPaths[characterIndex]}");
                    return false;
                }
                array.GetArrayElementAtIndex(characterIndex).objectReferenceValue = character;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            return EditorSceneManager.SaveScene(scene);
        }
    }
}
