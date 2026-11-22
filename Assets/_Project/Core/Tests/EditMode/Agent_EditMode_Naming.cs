using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Pins the project's naming and layout rules (CLAUDE.md "Hard rules", UU_PROMPTS
    /// CLEAN item 2) so a new file that breaks them fails here instead of in an audit.
    ///
    /// The prompt's own prefixes (BP_, SM_) are Unreal asset types and have no Unity
    /// counterpart in this project; the Unity equivalents that DO exist are pinned:
    /// script prefixes, SCN_ scenes, PM_ physics materials, M_ materials (none are
    /// serialized today - every material is built in code - so that rule only bites
    /// when someone adds one), PascalCase folders, and the Po Unity layout (2026-11-22):
    /// project assets live in Assets/_Project/{Agents,Core,Data,Environments,RL_Models,
    /// Scenes,UI}, at most three folders below Assets/_Project/; everything else under
    /// Assets/ (Plugins, WebGLTemplates) at most two deep, as before.
    ///
    /// Reads the file system, not the AssetDatabase, so it needs no import and runs
    /// the same on any machine (relative to Application.dataPath).
    /// </summary>
    public sealed class Agent_EditMode_Naming
    {
        // Folders Unity itself creates and names. Renaming them breaks the editor
        // feature that owns them, so they are exempt rather than "fixed".
        static readonly HashSet<string> UnityOwnedFolders = new HashSet<string>
        {
            "Build Profiles",
            // Written by the ML-Agents package at runtime (timers, demos); gitignored.
            "ML-Agents",
        };

        static readonly Regex ScriptPrefix = new Regex(@"^(Agent_|Sensor_|Reward_)[A-Z]\w*\.cs$");
        static readonly Regex EditorScriptPrefix = new Regex(@"^Editor_[A-Z]\w*\.cs$");
        static readonly Regex PascalFolder = new Regex(@"^[A-Z][A-Za-z0-9]*$");
        static readonly Regex AgentFolder = new Regex(@"^[A-Z][a-z]+_v\d{2}$");

        static string AssetsRoot => Application.dataPath;
        static string ProjectRoot => Path.Combine(AssetsRoot, "_Project");

        /// <summary>The Po Unity layout: the only folders directly under Assets/_Project/.</summary>
        static readonly HashSet<string> ProjectFolders = new HashSet<string>
        {
            "Agents", "Core", "Data", "Environments", "RL_Models", "Scenes", "UI",
        };

        static bool IsEditorOrTest(string file)
        {
            string rel = file.Replace('\\', '/');
            return rel.Contains("/_Project/Core/Editor/") || rel.Contains("/_Project/Core/Tests/");
        }

        static string Relative(string path) =>
            path.Substring(AssetsRoot.Length - "Assets".Length).Replace('\\', '/');

        [Test]
        public void RuntimeScripts_CarryAgentSensorOrRewardPrefix()
        {
            var bad = new List<string>();
            foreach (string file in Directory.GetFiles(ProjectRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsEditorOrTest(file)) continue;
                string name = Path.GetFileName(file);
                if (name == "AssemblyInfo.cs") continue;
                if (!ScriptPrefix.IsMatch(name)) bad.Add(Relative(file));
            }
            Assert.That(bad, Is.Empty, "Runtime scripts must be Agent_/Sensor_/Reward_ + PascalCase:\n" + string.Join("\n", bad));
        }

        [Test]
        public void EditorScripts_CarryEditorOrAgentPrefix()
        {
            var bad = new List<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(ProjectRoot, "Core", "Editor"), "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (!EditorScriptPrefix.IsMatch(name)) bad.Add(Relative(file));
            }
            Assert.That(bad, Is.Empty, "Editor scripts must be Editor_ + PascalCase:\n" + string.Join("\n", bad));
        }

        [Test]
        public void Scenes_CarryScnPrefix()
        {
            var bad = new List<string>();
            foreach (string file in Directory.GetFiles(AssetsRoot, "*.unity", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(file).StartsWith("SCN_")) bad.Add(Relative(file));
            }
            Assert.That(bad, Is.Empty, "Scenes must start with SCN_:\n" + string.Join("\n", bad));
        }

        [Test]
        public void Materials_CarryTypePrefix()
        {
            var bad = new List<string>();
            foreach (string file in Directory.GetFiles(AssetsRoot, "*.mat", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(file).StartsWith("M_")) bad.Add(Relative(file));
            }
            foreach (string file in Directory.GetFiles(AssetsRoot, "*.physicsMaterial2D", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(file).StartsWith("PM_")) bad.Add(Relative(file));
            }
            Assert.That(bad, Is.Empty, "Materials must be M_*, physics materials PM_*:\n" + string.Join("\n", bad));
        }

        [Test]
        public void Folders_FollowThePoUnityLayout()
        {
            var bad = new List<string>();
            foreach (string dir in Directory.GetDirectories(AssetsRoot, "*", SearchOption.AllDirectories))
            {
                string rel = Relative(dir);
                string name = Path.GetFileName(dir);
                if (UnityOwnedFolders.Contains(name)) continue;

                int depth = rel.Split('/').Length - 1;   // "Assets/A" = 1, "Assets/A/B" = 2
                if (rel == "Assets/_Project") continue;
                bool inProject = rel.StartsWith("Assets/_Project/");
                if (inProject && depth == 2 && !ProjectFolders.Contains(name))
                {
                    bad.Add(rel + "  (Assets/_Project/ holds only " + string.Join(", ", ProjectFolders) + ")");
                    continue;
                }
                if (depth > (inProject ? 4 : 2))
                {
                    bad.Add(rel + (inProject ? "  (deeper than three levels under Assets/_Project/)"
                                             : "  (deeper than two levels under Assets/)"));
                    continue;
                }
                if (inProject && depth == 2) continue;   // the layout's own names (RL_Models)

                bool agentSlot = rel.StartsWith("Assets/_Project/RL_Models/") && depth == 3;
                if (agentSlot ? !AgentFolder.IsMatch(name) : !PascalFolder.IsMatch(name))
                {
                    bad.Add(rel + (agentSlot ? "  (agent folders are <Name>_v<NN>)" : "  (not PascalCase)"));
                }
            }
            Assert.That(bad, Is.Empty, "Folder layout violations:\n" + string.Join("\n", bad));
        }
    }
}
