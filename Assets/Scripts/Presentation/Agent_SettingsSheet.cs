using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The one settings surface in the game: a bottom sheet with a tab strip,
    /// opened from the SETTINGS button in Agent_Chrome's upper-right corner (every
    /// screen) and from the pause panel.
    ///
    /// WHY ONE. Settings had accreted wherever there was room: a three-button
    /// accessibility shelf on the menu, four volume sliders on the pause panel, a
    /// mute toggle in both, and a broadcast-strip switch that was a serialized
    /// field nobody could reach on a phone. Each was reasonable where it landed
    /// and together they meant no screen had all of them - you could change the
    /// palette only from the menu and the mix only mid-match.
    ///
    /// SHAPE. A dim scrim over the host panel with the sheet docked to the bottom
    /// edge, inside thumb reach in portrait. Four tabs - MATCH, VIEW, OVERLAYS,
    /// AUDIO - switch the body IN PLACE; the body has a fixed height so the tab
    /// strip never moves under the thumb. No nested popups, no ScrollView: a tab
    /// that outgrows the body is a tab that needs splitting, and the fixed height
    /// is what makes that visible.
    ///
    /// EVERY CHANGE APPLIES LIVE AND PERSISTS IMMEDIATELY, as the volume sliders
    /// always have. There is no OK button, so there is no half-applied state and
    /// closing is always safe - by the CLOSE button, a tap on the scrim, or back.
    ///
    /// Static, because it holds no scene state: the settings themselves live in
    /// PlayerPrefs behind Agent_Audio / Agent_Palette / Agent_Haptics, and the
    /// sheet is rebuilt from them on every open. The only thing it keeps is which
    /// tab was last looked at.
    ///
    /// EXTENDING IT (2026-11-07). A component that owns a new preference adds it
    /// with one line, and it appears on the next open:
    ///
    ///   Agent_SettingsSheet.RegisterSwitch(Agent_SettingsSheet.Tab.View, "ultra",
    ///       "Ultra effects", () => Agent_Quality.Ultra, on => Agent_Quality.Ultra = on);
    ///
    /// Overlays do NOT register here - they register with Agent_Overlays, and the
    /// OVERLAYS tab lists that registry. Capacity is the fixed body: MATCH and
    /// VIEW have room for two extra rows each; AUDIO extras become toggles in the
    /// Sound row (two fit beside SOUND); anything past capacity is not drawn and
    /// is logged once.
    /// </summary>
    public static class Agent_SettingsSheet
    {
        /// <summary>The four tabs, in strip order.</summary>
        public enum Tab { Match = 0, View = 1, Overlays = 2, Audio = 3 }

        static readonly string[] TabNames = { "MATCH", "VIEW", "OVERLAYS", "AUDIO" };

        /// <summary>Rows a tab body holds: fixed height 640 / --touch-min 124.</summary>
        const int ROWS_PER_TAB = 5;

        /// <summary>Overlay toggles per row on the OVERLAYS tab.</summary>
        const int OVERLAYS_PER_ROW = 3;

        /// <summary>Toggles in the AUDIO tab's Sound row, SOUND included.</summary>
        const int AUDIO_ROW_TOGGLES = 3;

        static VisualElement _host, _scrim, _body;
        static readonly Button[] Tabs = new Button[4];
        static System.Action _onChanged;
        static Tab _tab = Tab.Match;

        // -- Registered extra switches -------------------------------------------

        sealed class Extra
        {
            public string Id;
            public Tab Tab;
            public string Label;
            public System.Func<bool> Get;
            public System.Action<bool> Set;
        }

        static readonly List<Extra> Extras = new();
        static bool _warnedOverflow;

        /// <summary>
        /// Adds an ON/OFF setting to a tab, or replaces the one registered under the
        /// same id. Shown from the next open. See the class note for capacity.
        /// </summary>
        public static void RegisterSwitch(Tab tab, string id, string label,
            System.Func<bool> get, System.Action<bool> set)
        {
            if (string.IsNullOrEmpty(id) || get == null || set == null) return;
            var extra = new Extra { Id = id, Tab = tab, Label = label, Get = get, Set = set };
            for (int extraIndex = 0; extraIndex < Extras.Count; extraIndex++)
            {
                if (!string.Equals(Extras[extraIndex].Id, id, System.StringComparison.Ordinal)) continue;
                Extras[extraIndex] = extra;
                return;
            }
            Extras.Add(extra);
        }

        /// <summary>Removes a registered setting. Safe when it was never registered.</summary>
        public static void Unregister(string id)
        {
            for (int extraIndex = Extras.Count - 1; extraIndex >= 0; extraIndex--)
            {
                if (string.Equals(Extras[extraIndex].Id, id, System.StringComparison.Ordinal))
                    Extras.RemoveAt(extraIndex);
            }
        }

        // -- Host ------------------------------------------------------------------
        //
        // The sheet has to open on the panel of whatever screen owns the scene - the
        // HUD in a match, the menu in SCN_Menu - so that screen's own refresh runs
        // after a change (the HUD re-reads the broadcast switch, the menu its mute
        // label). Agent_Chrome's SETTINGS button does not know which screen that is;
        // it asks for the sheet, and the registered host opens it.

        static System.Action _hostOpener;

        /// <summary>The screen that owns this scene registers how to open the sheet over itself.</summary>
        public static void SetHost(System.Action opener) => _hostOpener = opener;

        /// <summary>Unregisters, only if <paramref name="opener"/> is still the host.</summary>
        public static void ClearHost(System.Action opener)
        {
            if (_hostOpener == opener) _hostOpener = null;
        }

        /// <summary>
        /// Opens the sheet over the registered host's panel, or over
        /// <paramref name="fallback"/> when no screen registered (the gallery).
        /// Closes it instead when it is already open, so the button is a toggle.
        /// </summary>
        public static void RequestOpen(VisualElement fallback)
        {
            if (IsOpen)
            {
                Close();
                return;
            }
            if (_hostOpener != null) _hostOpener();
            else Open(fallback);
        }

        /// <summary>
        /// True while the sheet is on a live panel. The panel check matters: these
        /// are statics, and a scene load destroys the host document without
        /// calling <see cref="Close"/>, which would otherwise leave this reporting
        /// an open sheet that no longer exists - and every back press after that
        /// would be swallowed closing it.
        /// </summary>
        public static bool IsOpen => _scrim != null && _scrim.panel != null;

        /// <summary>The tab currently shown (or shown last).</summary>
        public static Tab CurrentTab => _tab;

        /// <summary>
        /// Raises the sheet over <paramref name="host"/> (a panel root).
        /// <paramref name="onChanged"/> runs after every setting change.
        /// </summary>
        public static void Open(VisualElement host, System.Action onChanged = null)
        {
            if (host == null) return;
            Close();

            _host = host;
            _onChanged = onChanged;

            _scrim = new VisualElement { name = "settings-sheet" };
            _scrim.AddToClassList("sheet-scrim");
            // A tap on the scrim itself dismisses. The target check matters:
            // without it every tap inside the sheet bubbles up here and closes it
            // the moment a slider is touched.
            _scrim.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, _scrim)) Close();
            });
            // The scrim is absolute on the panel root, so it carries its own
            // safe-area inset - and reserves Agent_Chrome's bottom bar, because
            // the chrome draws ABOVE this panel and its DEBUG button would
            // otherwise sit on top of the sheet's tab strip.
            Agent_UIStyle.BindSafeArea(_scrim, 0f, Agent_Chrome.BAR_HEIGHT);

            var sheet = new VisualElement { name = "settings-panel" };
            sheet.AddToClassList("sheet");
            _scrim.Add(sheet);

            var head = new VisualElement();
            head.AddToClassList("sheet__head");
            var title = new Label("SETTINGS");
            title.AddToClassList("sheet__title");
            head.Add(title);
            var close = new Button(Close) { text = "CLOSE", name = "settings-close" };
            close.AddToClassList("btn");
            head.Add(close);
            sheet.Add(head);

            _body = new VisualElement { name = "settings-body" };
            _body.AddToClassList("sheet__body");
            sheet.Add(_body);

            // Tabs BELOW the body: the sheet is docked to the bottom edge, so the
            // strip a thumb switches with is the part nearest the thumb.
            var tabs = new VisualElement { name = "settings-tabs" };
            tabs.AddToClassList("tabs");
            for (int tabIndex = 0; tabIndex < TabNames.Length; tabIndex++)
            {
                var captured = (Tab)tabIndex;
                var tab = new Button(() => ShowTab(captured))
                {
                    text = TabNames[tabIndex],
                    name = "settings-tab-" + TabNames[tabIndex].ToLowerInvariant(),
                };
                tab.AddToClassList("btn");
                tab.AddToClassList("tab");
                Tabs[tabIndex] = tab;
                tabs.Add(tab);
            }
            sheet.Add(tabs);

            host.Add(_scrim);
            Agent_UIStyle.PlayEntrance(_scrim);
            ShowTab(_tab);
        }

        /// <summary>Dismisses the sheet. Safe when it is not open.</summary>
        public static void Close()
        {
            if (_scrim != null) _scrim.RemoveFromHierarchy();
            _scrim = null;
            _body = null;
            _host = null;
            _onChanged = null;
            for (int tabIndex = 0; tabIndex < Tabs.Length; tabIndex++) Tabs[tabIndex] = null;
        }

        /// <summary>Switches the body to <paramref name="tab"/>. No-op while closed (but remembered).</summary>
        public static void ShowTab(Tab tab)
        {
            _tab = (Tab)Mathf.Clamp((int)tab, 0, TabNames.Length - 1);
            if (_body == null) return;

            for (int tabIndex = 0; tabIndex < Tabs.Length; tabIndex++)
            {
                if (Tabs[tabIndex] != null)
                    Tabs[tabIndex].EnableInClassList(Agent_UIStyle.ON, tabIndex == (int)_tab);
            }

            _body.Clear();
            switch (_tab)
            {
                case Tab.Match: BuildMatch(); break;
                case Tab.View: BuildView(); break;
                case Tab.Overlays: BuildOverlays(); break;
                default: BuildAudio(); break;
            }
        }

        // -- Tabs --------------------------------------------------------------
        //
        // Every control shows what it is currently SET TO, never what it would
        // switch to. A control labelled with its next state reads as an
        // instruction and leaves the player with no way to know where they are -
        // which matters most for the people the colour and text-size rows exist for.

        static void BuildMatch()
        {
            _body.Add(Row("Broadcast stats in a match", Switch(
                () => Agent_HUD.BroadcastEnabled,
                on => Agent_HUD.BroadcastEnabled = on)));
            _body.Add(Row("Vibration", Switch(
                () => Agent_Haptics.Enabled,
                on => Agent_Haptics.Enabled = on)));
            _body.Add(Row("Open health panel on start", Switch(
                () => Agent_Telemetry.ShowOnStart,
                on => Agent_Telemetry.ShowOnStart = on)));
            AddExtraRows(Tab.Match, 3);
        }

        static void BuildView()
        {
            _body.Add(Row("Text size", Cycle(
                () => TypeLabel(Agent_Palette.TypeScale),
                () => Agent_Palette.TypeScale != 0,
                Agent_Palette.CycleTypeScale)));
            _body.Add(Row("Team colours", Cycle(
                () => Agent_Palette.Label(Agent_Palette.Current),
                () => Agent_Palette.Current != Agent_Palette.Mode.Standard,
                Agent_Palette.CycleMode)));
            // The readout only ever appears with the health panel open (TAB /
            // DEBUG) - the default screen carries no diagnostics.
            _body.Add(Row("FPS with health panel", Switch(
                () => Agent_Chrome.ShowFps,
                on => Agent_Chrome.ShowFps = on)));

            // Desktop Ultra effects. Only on a machine Agent_Quality would run them
            // on, and only in a match (the component lives on the match pitch).
            // Not persisted: Agent_Quality re-decides it every match from the
            // measured frame time and may switch it back off under load.
            int used = 3;
            var quality = Object.FindAnyObjectByType<Agent_Quality>();
            if (quality != null && quality.enabled && Agent_Quality.UltraEligible(ULTRA_MIN_VRAM_MB))
            {
                _body.Add(Row("Ultra effects", Switch(
                    () => Agent_Quality.UltraActive,
                    on =>
                    {
                        var live = Object.FindAnyObjectByType<Agent_Quality>();
                        if (live != null) live.SetUltra(on);
                    })));
                used++;
            }
            AddExtraRows(Tab.View, used);
        }

        /// <summary>
        /// The VRAM floor for showing the Ultra row. Agent_Quality's own floor is a
        /// serialized field (default 2048) that SetUltra re-checks, so this only
        /// decides whether the row is worth showing.
        /// </summary>
        const int ULTRA_MIN_VRAM_MB = 2048;

        /// <summary>
        /// Every registered overlay as a toggle, three to a row. One tap does what
        /// the overlay's own key does (vision steps through its three states).
        /// Overlays that cannot run on this screen - the menu has no pitch - are
        /// shown disabled rather than hidden, so the grid keeps its shape.
        /// </summary>
        static void BuildOverlays()
        {
            int count = Agent_Overlays.Count;
            int capacity = ROWS_PER_TAB * OVERLAYS_PER_ROW;
            if (count > capacity) WarnOverflow("OVERLAYS", count - capacity);

            VisualElement row = null;
            for (int overlayIndex = 0; overlayIndex < count && overlayIndex < capacity; overlayIndex++)
            {
                if (overlayIndex % OVERLAYS_PER_ROW == 0)
                {
                    row = new VisualElement();
                    row.AddToClassList("sheet__toggles");
                    _body.Add(row);
                }

                var entry = Agent_Overlays.Get(overlayIndex);
                Button button = null;
                button = new Button(() =>
                {
                    entry.Advance();
                    button.text = entry.Text;
                    button.EnableInClassList(Agent_UIStyle.ON, entry.IsOn);
                    NotifyChanged();
                })
                {
                    text = entry.Text,
                    name = "overlay-" + entry.Id,
                };
                button.AddToClassList("btn");
                button.AddToClassList("sheet__toggle");
                button.EnableInClassList(Agent_UIStyle.ON, entry.IsOn);
                button.SetEnabled(entry.Available);
                row.Add(button);
            }

            if (count == 0) _body.Add(Note("No overlays on this screen."));
        }

        static void BuildAudio()
        {
            // The Sound row holds SOUND plus any registered audio switches as
            // toggles, because the four sliders below fill the rest of the body.
            int extras = CountExtras(Tab.Audio);
            if (extras == 0)
            {
                _body.Add(Row("Sound", Switch(
                    () => !Agent_Audio.Muted,
                    on => Agent_Audio.Muted = !on)));
            }
            else
            {
                var row = new VisualElement { name = "settings-audio-toggles" };
                row.AddToClassList("sheet__toggles");
                row.Add(Toggle("SOUND", () => !Agent_Audio.Muted, on => Agent_Audio.Muted = !on));

                int placed = 0;
                for (int extraIndex = 0; extraIndex < Extras.Count; extraIndex++)
                {
                    var extra = Extras[extraIndex];
                    if (extra.Tab != Tab.Audio) continue;
                    if (placed >= AUDIO_ROW_TOGGLES - 1)
                    {
                        WarnOverflow("AUDIO", extras - placed);
                        break;
                    }
                    row.Add(Toggle(extra.Label.ToUpperInvariant(), extra.Get, extra.Set));
                    placed++;
                }
                _body.Add(row);
            }
            _body.Add(Agent_UIStyle.VolumeSliders());
        }

        static void AddExtraRows(Tab tab, int usedRows)
        {
            int free = ROWS_PER_TAB - usedRows;
            int placed = 0, total = 0;
            for (int extraIndex = 0; extraIndex < Extras.Count; extraIndex++)
            {
                var extra = Extras[extraIndex];
                if (extra.Tab != tab) continue;
                total++;
                if (placed >= free) continue;
                _body.Add(Row(extra.Label, Switch(extra.Get, extra.Set)));
                placed++;
            }
            if (total > placed) WarnOverflow(TabNames[(int)tab], total - placed);
        }

        static int CountExtras(Tab tab)
        {
            int count = 0;
            for (int extraIndex = 0; extraIndex < Extras.Count; extraIndex++)
            {
                if (Extras[extraIndex].Tab == tab) count++;
            }
            return count;
        }

        static void WarnOverflow(string tab, int dropped)
        {
            if (_warnedOverflow) return;
            _warnedOverflow = true;
            Debug.LogWarning($"Agent_SettingsSheet: the {tab} tab is full; {dropped} registered " +
                             "control(s) are not drawn. Split the tab rather than adding a scroll view.");
        }

        static string TypeLabel(int step)
        {
            switch (step)
            {
                case 1: return "LARGE";
                case 2: return "LARGER";
                default: return "NORMAL";
            }
        }

        // -- Controls ----------------------------------------------------------

        static VisualElement Row(string label, VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("sheet__row");

            var name = new Label(label);
            name.AddToClassList("sheet__label");
            row.Add(name);

            row.Add(control);
            return row;
        }

        static Label Note(string text)
        {
            var note = new Label(text);
            note.AddToClassList("sheet__label");
            return note;
        }

        /// <summary>An ON / OFF control.</summary>
        static Button Switch(System.Func<bool> get, System.Action<bool> set)
        {
            return Cycle(
                () => get() ? "ON" : "OFF",
                get,
                () => set(!get()));
        }

        /// <summary>A named toggle for a toggle row: the name is the label, the fill is the state.</summary>
        static Button Toggle(string text, System.Func<bool> get, System.Action<bool> set)
        {
            Button button = null;
            button = new Button(() =>
            {
                set(!get());
                button.EnableInClassList(Agent_UIStyle.ON, get());
                NotifyChanged();
            })
            { text = text };
            button.AddToClassList("btn");
            button.AddToClassList("sheet__toggle");
            button.EnableInClassList(Agent_UIStyle.ON, get());
            return button;
        }

        /// <summary>
        /// A control that steps through its values on each tap and always shows
        /// the current one. A .btn, so it is --touch-min tall like every other
        /// tappable control in the game.
        /// </summary>
        static Button Cycle(System.Func<string> text, System.Func<bool> on, System.Action advance)
        {
            Button button = null;
            button = new Button(() =>
            {
                advance();
                button.text = text();
                button.EnableInClassList(Agent_UIStyle.ON, on());
                NotifyChanged();
            })
            { text = text() };
            button.AddToClassList("btn");
            button.AddToClassList("sheet__value");
            button.EnableInClassList(Agent_UIStyle.ON, on());
            return button;
        }

        /// <summary>
        /// Re-stamps the palette and type-scale classes on the host root - they are
        /// USS variable overrides, so that one call restyles every element on the
        /// panel, this sheet included - then lets the host fix up anything it has
        /// baked in that a variable cannot reach.
        /// </summary>
        static void NotifyChanged()
        {
            if (_host != null) Agent_UIStyle.ApplyAccessibility(_host);
            if (_onChanged != null) _onChanged();
        }
    }
}
