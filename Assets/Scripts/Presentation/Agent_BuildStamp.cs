using System.Globalization;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// The build date as YYMMDD, shown in the lower-right corner next to the
    /// version (Agent_Chrome's "chrome-version" label).
    ///
    /// A PLAYER reads it from Resources/BuildStamp.txt, which
    /// Editor_BuildStamp writes at the start of every build, so the date is the
    /// day the binary was made rather than the day it is run. The file is
    /// gitignored: it is a build output, and committing it would put a dirty
    /// file in the tree after every build.
    ///
    /// THE EDITOR ALWAYS SHOWS TODAY. A stamp file left behind by the last build
    /// would otherwise label every play session with that build's date, which is
    /// exactly the stale-build confusion this number exists to remove.
    /// </summary>
    public static class Agent_BuildStamp
    {
        /// <summary>Resources path of the stamp file, without extension.</summary>
        public const string RESOURCE = "BuildStamp";

        /// <summary>The format string. Editor_BuildStamp writes the same one.</summary>
        public const string FORMAT = "yyMMdd";

        static string _stamp;

        /// <summary>YYMMDD of this build (today, in the editor). Resolved once.</summary>
        public static string Stamp
        {
            get
            {
                if (_stamp == null) _stamp = Resolve();
                return _stamp;
            }
        }

        /// <summary>Formats a date the way the stamp is written.</summary>
        public static string Format(System.DateTime when) =>
            when.ToString(FORMAT, CultureInfo.InvariantCulture);

        /// <summary>True for exactly six ASCII digits.</summary>
        public static bool IsValid(string text)
        {
            if (text == null || text.Length != FORMAT.Length) return false;
            for (int charIndex = 0; charIndex < text.Length; charIndex++)
            {
                if (text[charIndex] < '0' || text[charIndex] > '9') return false;
            }
            return true;
        }

        static string Resolve()
        {
#if UNITY_EDITOR
            return Format(System.DateTime.Now);
#else
            var asset = Resources.Load<TextAsset>(RESOURCE);
            if (asset != null)
            {
                string text = asset.text.Trim();
                if (IsValid(text)) return text;
            }
            // A player built without the stamp (the preprocess step did not run)
            // says so rather than inventing a date.
            return "nodate";
#endif
        }
    }
}
