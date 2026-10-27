using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// The game in a browser: a WebGL player in Builds/WebGL, served from the Azure Static Web App
    /// "posoccer" (resource group PoSoccer) by Tools/deploy_webgl.ps1. Menu PoSoccer/Build WebGL (browser), or headless:
    ///   Unity.exe -batchmode -quit -projectPath . -buildTarget WebGL -executeMethod PoSoccer.EditorTools.Editor_BuildWebGL.Build
    /// The outcome is the "WEBGL BUILD RESULT:" log line (and the exit code in batch mode).
    ///
    /// Gzip with the decompression fallback: Static Web Apps does not send the Content-Encoding
    /// header a compressed Unity build otherwise needs, so the loader unpacks the files itself.
    /// The canvas is 540 x 960, the 9:16 portrait the game is laid out for on the phone.
    /// The MCP bridge's UNITY_MCP_* defines are stripped for the build, as for Android, so its
    /// runtime (and the NuGet closure behind it) stays out of the player.
    /// </summary>
    public static class Editor_BuildWebGL
    {
        const string OUTPUT = "Builds/WebGL";

        [MenuItem("PoSoccer/Build WebGL (browser)")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { Finish("Aborted — exit Play mode first", false); return; }

            string[] scenes = Editor_BuildAndroidAAB.SHIP_SCENES;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.defaultWebScreenWidth = 540;
            PlayerSettings.defaultWebScreenHeight = 960;
            PlayerSettings.WebGL.template = "PROJECT:Po";

            string defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL);
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL,
                string.Join(";", defines.Split(';').Where(d => !d.Trim().StartsWith("UNITY_MCP_"))));
            BuildSummary summary;
            try
            {
                summary = BuildPipeline.BuildPlayer(scenes, OUTPUT, BuildTarget.WebGL, BuildOptions.None).summary;
            }
            finally
            {
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL, defines);
            }
            Finish($"{summary.result} | v{PlayerSettings.bundleVersion} | {scenes.Length} scenes | {summary.totalErrors} errors | " +
                   $"{summary.totalSize / (1024 * 1024)} MB | {summary.totalTime.TotalMinutes:0.0} min | {OUTPUT}",
                   summary.result == BuildResult.Succeeded);
        }

        static void Finish(string message, bool ok)
        {
            if (ok) Debug.Log("WEBGL BUILD RESULT: " + message);
            else Debug.LogError("WEBGL BUILD RESULT: " + message);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
