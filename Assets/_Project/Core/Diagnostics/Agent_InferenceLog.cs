using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// One console line per brain the first time its ONNX policy answers a decision in this session:
    /// "[PoSoccer] &lt;brain&gt;.onnx inference running (...)". It is the proof, in a player's browser
    /// console (the local WebGL Release build at 16:9 and 9:16, AGENTS.md section 3), that the trained
    /// policy really runs rather than the rule-based bot. Agent_Soccer.OnActionReceived calls
    /// <see cref="Note"/> for every InferenceOnly agent; the set makes the second and later calls free.
    /// </summary>
    public static class Agent_InferenceLog
    {
        public const string Marker = "inference running";
        static readonly HashSet<string> Seen = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Seen.Clear();

        /// <summary>The line for one brain (pure, for tests).</summary>
        public static string Line(string brain, string detail) => $"[PoSoccer] {brain}.onnx {Marker} ({detail})";

        /// <summary>Logs the line for this brain once; true when it logged now.</summary>
        public static bool Note(string brain, string detail)
        {
            if (string.IsNullOrEmpty(brain) || !Seen.Add(brain)) return false;
            Debug.Log(Line(brain, detail));
            return true;
        }

        /// <summary>Tests: forget which brains were logged.</summary>
        public static void Clear() => Seen.Clear();
    }
}
