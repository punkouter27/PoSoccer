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
    /// when someone adds one), PascalCase folders, and a folder depth of at most two
    /// under Assets/.
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
        };

        static readonly Regex ScriptPrefix = new Regex(@"^(Agent_|Sensor_|Reward_)[A-Z]\w*\.cs$");
        static readonly Regex EditorScriptPrefix = new Regex(@"^(Editor_|Agent_)[A-Z]\w*\.cs$");
        static readonly Regex PascalFolder = new Regex(@"^[A-Z][A-Za-z0-9]*$");
        static readonly Regex AgentFolder = new Regex(@"^[A-Z][a-z]+_v\d{2}$");

        static string AssetsRoot => Application.dataPath;

        static string Relative(string path) =>
            path.Substring(AssetsRoot.Length - "Assets".Length).Replace('\\', '/');

        [Test]
        public void RuntimeScripts_CarryAgentSensorOrRewardPrefix()
        {
            var bad = new List<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(AssetsRoot, "Scripts"), "*.cs", SearchOption.AllDirectories))
            {
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
            foreach (string file in Directory.GetFiles(Path.Combine(AssetsRoot, "Editor"), "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (!EditorScriptPrefix.IsMatch(name)) bad.Add(Relative(file));
            }
            Assert.That(bad, Is.Empty, "Editor scripts must be Editor_ (or Agent_) + PascalCase:\n" + string.Join("\n", bad));
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
        public void Folders_ArePascalCase_AndAtMostTwoDeep()
        {
            var bad = new List<string>();
            foreach (string dir in Directory.GetDirectories(AssetsRoot, "*", SearchOption.AllDirectories))
            {
                string rel = Relative(dir);
                string name = Path.GetFileName(dir);
                if (UnityOwnedFolders.Contains(name)) continue;

                int depth = rel.Split('/').Length - 1;   // "Assets/A" = 1, "Assets/A/B" = 2
                if (depth > 2)
                {
                    bad.Add(rel + "  (deeper than two levels under Assets/)");
                    continue;
                }

                bool agentSlot = rel.StartsWith("Assets/Agents/") && depth == 2;
                if (agentSlot ? !AgentFolder.IsMatch(name) : !PascalFolder.IsMatch(name))
                {
                    bad.Add(rel + (agentSlot ? "  (agent folders are <Name>_v<NN>)" : "  (not PascalCase)"));
                }
            }
            Assert.That(bad, Is.Empty, "Folder layout violations:\n" + string.Join("\n", bad));
        }
    }
}
