using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Writes Assets/Resources/BuildStamp.txt (YYMMDD) before every player build,
    /// so the lower-right corner of the built game shows the day it was built.
    /// Read at runtime by PoSoccer.Agent_BuildStamp.
    ///
    /// Runs for every build path - the MCP manage_build call, the
    /// -buildWindows64Player CLI build in scripts/build-headless.ps1, the WebGL
    /// and Android menu builds - because IPreprocessBuildWithReport is invoked by
    /// BuildPipeline itself, not by any one menu item.
    ///
    /// The file is gitignored (a build output). This assembly does not reference
    /// PoSoccer.Runtime, so the format string is repeated here; it must match
    /// Agent_BuildStamp.FORMAT, which Agent_EditMode_BuildStamp pins.
    /// </summary>
    public sealed class Editor_BuildStamp : IPreprocessBuildWithReport
    {
        public const string PATH = "Assets/Resources/BuildStamp.txt";
        public const string FORMAT = "yyMMdd";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            string stamp = System.DateTime.Now.ToString(FORMAT, CultureInfo.InvariantCulture);
            string existing = File.Exists(PATH) ? File.ReadAllText(PATH).Trim() : null;
            if (existing == stamp) return;

            File.WriteAllText(PATH, stamp);
            AssetDatabase.ImportAsset(PATH, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"Editor_BuildStamp: build stamp {stamp} written to {PATH}");
        }
    }
}
