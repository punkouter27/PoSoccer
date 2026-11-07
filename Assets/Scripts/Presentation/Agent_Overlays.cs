using System.Collections.Generic;
using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// The one list of match overlays, and the one switch that cycles them.
    ///
    /// WHY A REGISTRY (2026-11-07). Six overlays had grown six letter keys (I, V, T,
    /// B, X, G) and two hard-coded rows of buttons in the health panel, and three
    /// more arrived the same day (stamina ring H, passing lanes P, saliency J). A phone has
    /// no keys, and every new overlay meant editing the panel. Each overlay now
    /// registers itself here once and appears everywhere at the same time:
    ///
    ///   - the health panel's OVERLAYS rows (Agent_Telemetry),
    ///   - the settings sheet's OVERLAYS tab (Agent_SettingsSheet),
    ///   - the HUD's VIEW button and the O key, which cycle them one at a time
    ///     (<see cref="CycleSolo"/>).
    ///
    /// The letter keys still work; they write the same flags this reads, so the
    /// two routes cannot disagree.
    ///
    /// ADDING AN OVERLAY IS ONE LINE, anywhere that runs before the panel is
    /// opened (a static constructor or Awake is fine; a repeat with the same id
    /// replaces the entry rather than adding a second one):
    ///
    ///   Agent_Overlays.Register("lanes", "LANES", () => Visible, on => Visible = on);
    ///
    /// The flag should be a static, like Agent_Intent.Visible, because nothing
    /// here holds a reference to a component.
    /// </summary>
    public static class Agent_Overlays
    {
        /// <summary>One overlay's switch.</summary>
        public sealed class Entry
        {
            /// <summary>Stable key; registering the same id again replaces the entry.</summary>
            public readonly string Id;

            /// <summary>Short upper-case name for buttons ("INTENT").</summary>
            public readonly string Name;

            readonly System.Func<bool> _isOn;
            readonly System.Action<bool> _set;
            readonly System.Action _advance;
            readonly System.Func<string> _text;
            readonly System.Func<bool> _available;

            internal Entry(string id, string name, System.Func<bool> isOn, System.Action<bool> set,
                System.Action advance, System.Func<string> text, System.Func<bool> available)
            {
                Id = id;
                Name = name;
                _isOn = isOn;
                _set = set;
                _advance = advance;
                _text = text;
                _available = available;
            }

            public bool IsOn => _isOn != null && _isOn();

            /// <summary>False while the overlay cannot run in this scene (e.g. the brain cam outside a match).</summary>
            public bool Available => _available == null || _available();

            public void Set(bool on)
            {
                if (_set != null) _set(on);
            }

            /// <summary>
            /// What one tap on this overlay's button does: toggle, unless the overlay
            /// has more than two states (vision steps OFF / LAST / ALL).
            /// </summary>
            public void Advance()
            {
                if (_advance != null) _advance();
                else Set(!IsOn);
            }

            /// <summary>Button text for the current state ("VISION ALL"); the name when none is given.</summary>
            public string Text => _text != null ? _text() : Name;
        }

        static readonly List<Entry> Entries = new();

        /// <summary>Bumped on every registration change, so a panel can rebuild only when needed.</summary>
        public static int Version { get; private set; }

        public static int Count => Entries.Count;

        public static Entry Get(int index) => Entries[index];

        static Agent_Overlays()
        {
            RegisterBuiltIns();
        }

        /// <summary>
        /// Adds an on/off overlay, or replaces the one registered under the same id.
        /// <paramref name="available"/> may be null (always available).
        /// </summary>
        public static void Register(string id, string name, System.Func<bool> isOn, System.Action<bool> set,
            System.Func<bool> available = null)
        {
            Register(id, name, isOn, set, null, null, available);
        }

        /// <summary>
        /// Full form: <paramref name="advance"/> replaces the default toggle for
        /// multi-state overlays, and <paramref name="text"/> labels the current state.
        /// </summary>
        public static void Register(string id, string name, System.Func<bool> isOn, System.Action<bool> set,
            System.Action advance, System.Func<string> text, System.Func<bool> available)
        {
            if (string.IsNullOrEmpty(id) || isOn == null || set == null) return;

            var entry = new Entry(id, name, isOn, set, advance, text, available);
            int existing = IndexOf(id);
            if (existing >= 0) Entries[existing] = entry;
            else Entries.Add(entry);
            Version++;
        }

        /// <summary>Removes an overlay. Safe when it was never registered.</summary>
        public static void Unregister(string id)
        {
            int existing = IndexOf(id);
            if (existing < 0) return;
            Entries.RemoveAt(existing);
            Version++;
        }

        public static int IndexOf(string id)
        {
            for (int entryIndex = 0; entryIndex < Entries.Count; entryIndex++)
            {
                if (string.Equals(Entries[entryIndex].Id, id, System.StringComparison.Ordinal)) return entryIndex;
            }
            return -1;
        }

        /// <summary>How many overlays are currently on.</summary>
        public static int OnCount()
        {
            int on = 0;
            for (int entryIndex = 0; entryIndex < Entries.Count; entryIndex++)
            {
                if (Entries[entryIndex].IsOn) on++;
            }
            return on;
        }

        /// <summary>
        /// A bitmask of which overlays are on (first 31), so a caller polling on a
        /// timer can tell "nothing changed" with one int compare and no allocation.
        /// </summary>
        public static int Signature()
        {
            int mask = 0;
            int limit = Mathf.Min(Entries.Count, 31);
            for (int entryIndex = 0; entryIndex < limit; entryIndex++)
            {
                if (Entries[entryIndex].IsOn) mask |= 1 << entryIndex;
            }
            return mask;
        }

        /// <summary>
        /// THE ONE SWITCH. Shows the overlays one at a time:
        /// none -> first -> second -> ... -> last -> none. If several are on (set
        /// from the panel), the first press clears them all. Unavailable overlays
        /// are skipped. Returns the index now on, or -1 for none.
        /// </summary>
        public static int CycleSolo()
        {
            int on = OnCount();
            int current = -1;
            if (on == 1)
            {
                for (int entryIndex = 0; entryIndex < Entries.Count; entryIndex++)
                {
                    if (Entries[entryIndex].IsOn) { current = entryIndex; break; }
                }
            }

            AllOff();
            if (on > 1) return -1;

            for (int entryIndex = current + 1; entryIndex < Entries.Count; entryIndex++)
            {
                if (!Entries[entryIndex].Available) continue;
                Entries[entryIndex].Set(true);
                return entryIndex;
            }
            return -1;
        }

        /// <summary>Switches every overlay off.</summary>
        public static void AllOff()
        {
            for (int entryIndex = 0; entryIndex < Entries.Count; entryIndex++)
            {
                if (Entries[entryIndex].IsOn) Entries[entryIndex].Set(false);
            }
        }

        /// <summary>
        /// Short text for the cycler button: "VIEW" when nothing is on, the
        /// overlay's name when exactly one is, "VIEW n" when several are.
        /// Returns cached strings for the first case and entry names for the
        /// second, so the common path does not allocate.
        /// </summary>
        public static string SoloText()
        {
            int on = 0;
            Entry single = null;
            for (int entryIndex = 0; entryIndex < Entries.Count; entryIndex++)
            {
                if (!Entries[entryIndex].IsOn) continue;
                on++;
                single = Entries[entryIndex];
            }
            if (on == 0) return "VIEW";
            if (on == 1) return single.Name;
            return on < MixedText.Length ? MixedText[on] : "VIEW +";
        }

        static readonly string[] MixedText = BuildMixedText();

        static string[] BuildMixedText()
        {
            var text = new string[10];
            for (int count = 0; count < text.Length; count++) text[count] = "VIEW " + count;
            return text;
        }

        // -- Built-ins ----------------------------------------------------------

        static readonly string[] VisionText = { "VISION", "VISION LAST", "VISION ALL" };

        static Agent_BrainCam _brainCam;
        static float _nextBrainSearch;

        /// <summary>
        /// The brain cam is the one overlay that is a component instance rather
        /// than a static flag. Looked up lazily, at most once a second while it is
        /// missing, because this is read on panel refreshes.
        /// </summary>
        static Agent_BrainCam BrainCam()
        {
            if (_brainCam != null) return _brainCam;
            if (Time.unscaledTime < _nextBrainSearch) return null;
            _nextBrainSearch = Time.unscaledTime + 1f;
            _brainCam = Object.FindAnyObjectByType<Agent_BrainCam>();
            return _brainCam;
        }

        static void RegisterBuiltIns()
        {
            // FIRST because it is the one overlay that starts ON: with it first, the
            // first VIEW press moves straight on to INTENT instead of to nothing.
            Register("stamina", "STAMINA",
                () => Agent_StaminaRing.Visible, on => Agent_StaminaRing.Visible = on);

            Register("intent", "INTENT", () => Agent_Intent.Visible, on => Agent_Intent.Visible = on);

            Register("lanes", "LANES", () => Agent_PassLanes.Visible, on => Agent_PassLanes.Visible = on);

            // Vision has three states; a tap steps through them, and the solo
            // cycler shows the focused player's fan (the readable one).
            Register("vision", "VISION",
                () => Agent_VisionView.CurrentMode != Agent_VisionView.Mode.Off,
                on => Agent_VisionView.CurrentMode = on ? Agent_VisionView.Mode.Focus : Agent_VisionView.Mode.Off,
                () =>
                {
                    Agent_VisionView.CurrentMode = Agent_VisionView.CurrentMode == Agent_VisionView.Mode.All
                        ? Agent_VisionView.Mode.Off
                        : Agent_VisionView.CurrentMode + 1;
                },
                () => VisionText[Mathf.Clamp((int)Agent_VisionView.CurrentMode, 0, VisionText.Length - 1)],
                null);

            Register("tracer", "TRACER", () => Agent_ShotTracer.Visible, on => Agent_ShotTracer.Visible = on);

            Register("brain", "BRAIN",
                () =>
                {
                    var cam = BrainCam();
                    return cam != null && cam.enabled && cam.IsVisible;
                },
                on =>
                {
                    var cam = BrainCam();
                    if (cam != null && cam.enabled) cam.SetVisible(on);
                },
                null, null,
                () =>
                {
                    var cam = BrainCam();
                    return cam != null && cam.enabled;
                });

            // The brain cam's input-saliency block (J). Only meaningful with the
            // brain cam present, so it shares the brain cam's availability.
            Register("saliency", "SALIENCY",
                () => Agent_BrainCam.SaliencyEnabled, on => Agent_BrainCam.SaliencyEnabled = on,
                null, null,
                () =>
                {
                    var cam = BrainCam();
                    return cam != null && cam.enabled;
                });

            Register("xg", "XG FIELD",
                () => Agent_ExpectedGoals.FieldVisible, on => Agent_ExpectedGoals.FieldVisible = on);

            Register("skids", "SKIDS", () => Agent_Skids.Visible, on => Agent_Skids.Visible = on);
        }
    }
}
