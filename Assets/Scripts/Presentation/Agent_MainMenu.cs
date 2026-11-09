using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Opening menu (UI Toolkit, mobile portrait, safe-area).
    ///
    /// 2026-11-09 REDESIGN (user request). The screen is the 16 face characters in
    /// a 4 x 4 grid plus four modes:
    ///
    ///   1v1       tap two faces: the first fills Blue, the second Red. PLAY.
    ///   4v4       the same with four a side.
    ///   1v1 CUP   the 16-slot single-elimination bracket (Agent_BracketView).
    ///   4v4 CUP   four teams of four, drafted by Elo, in a 4-slot bracket.
    ///
    /// A character without a trained brain is shown greyed with IN TRAINING and
    /// cannot be picked; it unlocks by itself the first time the menu loads after
    /// its brain lands, and its cup slot is a BYE until then. Tapping a picked face
    /// (in the grid or in its seat) removes it. RANDOM fills both sides.
    ///
    /// Removed in the same pass: the 1v1/2v2/5v5 presets, the per-side roster
    /// pickers and steppers (sides are now exactly 1 or 4), the squad cards, and
    /// the community-brain HUB screen. Kept: ELO, CARDS, GALLERY, sound, and
    /// SETTINGS on the chrome's top-right button.
    ///
    /// Structure lives in Resources/Menu.uxml and appearance in
    /// Resources/PoSoccerTheme.uss; this class binds them and builds only what is
    /// data-shaped (a tile per character, a seat per squad place).
    ///
    /// The menu remembers the mode and the last lineup that was PLAYED, validated
    /// against the current roster.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class Agent_MainMenu : MonoBehaviour
    {
        public enum Mode
        {
            OneVsOne = 0,
            FourVsFour = 1,
            SoloCup = 2,
            TeamCup = 3,
        }

        [Header("Roster (profile assets)")]
        [Tooltip("The reference brain. Benched from play (2026-11-08); the no-brain fallback.")]
        public Reward_Settings standard;
        public Reward_Settings matt;
        public Reward_Settings kim;
        public Reward_Settings nick;
        [Tooltip("The rule-based benchmark opponent (Reward_BOT). Never selectable; the gallery's opponent.")]
        public Reward_Settings ruleBot;
        [Tooltip("Every character with a face (2026-11-08), trained or not, in grid order.")]
        public Reward_Settings[] characters;

        [Header("Flow")]
        public string matchScene = "SCN_Exhibition";

        const string TEMPLATE_RESOURCE = "Menu";
        const string LINEUP_BLUE_KEY = "posoccer_lineup_blue";
        const string LINEUP_RED_KEY = "posoccer_lineup_red";
        const string MODE_KEY = "posoccer_menu_mode";
        const char LINEUP_SEPARATOR = '|';
        const int TEAM_SIZE = Agent_Tournament.TEAM_SIZE;

        /// <summary>Every character shown in the grid, trained or not.</summary>
        Reward_Settings[] _characters;

        /// <summary>The characters whose brain this runtime can run: the pickable ones.</summary>
        Reward_Settings[] _trained;

        readonly List<Reward_Settings> _blue = new();
        readonly List<Reward_Settings> _red = new();
        Mode _mode = Mode.OneVsOne;

        VisualElement _root, _grid, _seatsBlue, _seatsRed, _lineup;
        Label _pitchNote, _cupInfo, _brainNote, _backHint;
        Button _play, _random, _soundButton;
        readonly Button[] _modeButtons = new Button[4];
        Agent_BracketView _bracket;
        VisualElement _eloPanel;

        /// <summary>Squad size of the current mode (cups: the size they field).</summary>
        int Capacity => _mode == Mode.FourVsFour || _mode == Mode.TeamCup ? TEAM_SIZE : 1;

        bool IsCupMode => _mode == Mode.SoloCup || _mode == Mode.TeamCup;

        void OnEnable()
        {
            // Android defaults to 30 fps unless something asks for more; this is the
            // first scene loaded and has no Agent_Bootstrap (2026-08-29).
            Application.targetFrameRate = 60;

            // Reaching the menu means no lineup is chosen: statics outlive a scene
            // load in a player build, so every flag set by the last launch is
            // cleared here and owned by the launcher that sets it.
            Agent_MatchSetup.Clear();

            // Scene entries own the clock (Agent_TimeFreeze.ReleaseAll).
            Agent_TimeFreeze.ReleaseAll();

            // The chrome (MENU / SETTINGS / DEBUG) is guaranteed here; this scene has
            // no Agent_Bootstrap to do it.
            Agent_Presentation.EnsureChrome();

            // The health panel answers on the menu too, on its OWN GameObject:
            // Agent_Telemetry takes the UIDocument of whatever it sits on.
            if (FindAnyObjectByType<Agent_Telemetry>() == null)
            {
                new GameObject("Telemetry").AddComponent<Agent_Telemetry>();
            }

            var doc = GetComponent<UIDocument>();
            var root = doc.rootVisualElement;
            if (doc.panelSettings == null || root == null)
            {
                Debug.LogWarning("Agent_MainMenu: UIDocument needs a PanelSettings asset.");
                return;
            }

            LoadRoster();
            if (_characters.Length == 0)
            {
                Debug.LogWarning("Agent_MainMenu: no characters wired; menu disabled.");
                return;
            }

            // Tournament mode: arriving from a result reopens that cup's bracket, and
            // with AUTO on the bracket starts the next match itself. Arriving
            // mid-match (the chrome's MENU button) abandons that match.
            bool fromResult = Agent_Tournament.ShowBracketOnMenu;
            Agent_Tournament.ShowBracketOnMenu = false;
            if (Agent_Tournament.InMatch) Agent_Tournament.CancelMatch();

            _mode = SavedMode();
            if (fromResult) _mode = Agent_Tournament.IsTeamCup ? Mode.TeamCup : Mode.SoloCup;
            if (!RestoreLineup()) FillDefault();

            _root = root;
            Build();

            if (IsCupMode)
            {
                Agent_Tournament.Mode = _mode == Mode.TeamCup ? Agent_Tournament.Cup.Team : Agent_Tournament.Cup.Solo;
                if (fromResult || (Agent_Tournament.Active && Agent_Tournament.AutoPlay)) OpenBracket();
            }
        }

        // -- Roster --------------------------------------------------------------

        /// <summary>
        /// The grid is every wired face character; the pickable subset is the ones
        /// with a usable brain. STANDARD stays benched (2026-11-08). With no face
        /// characters wired the shipped three stand in, and with no usable brain at
        /// all every character is offered (fielding the bot) under the brain note,
        /// so the game can still be started.
        /// </summary>
        void LoadRoster()
        {
            _characters = Compact(characters);
            if (_characters.Length == 0) _characters = Compact(matt, kim, nick);
            _trained = TrainedOnly(_characters);
        }

        bool AnyUsableBrain => _trained.Length > 0;

        /// <summary>Who can be picked: the trained characters, or everyone when none is trained.</summary>
        Reward_Settings[] Pickable => AnyUsableBrain ? _trained : _characters;

        bool IsPickable(Reward_Settings profile) => System.Array.IndexOf(Pickable, profile) >= 0;

        /// <summary>A character by name, trained or not.</summary>
        Reward_Settings FindCharacter(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return null;
            for (int characterIndex = 0; characterIndex < _characters.Length; characterIndex++)
            {
                if (string.Equals(_characters[characterIndex].playerName, playerName, System.StringComparison.Ordinal))
                {
                    return _characters[characterIndex];
                }
            }
            return null;
        }

        /// <summary>Cup entrants: the trained characters' names (BYEs fill the rest).</summary>
        string[] TournamentEntrants()
        {
            var names = new List<string>(_trained.Length);
            for (int trainedIndex = 0; trainedIndex < _trained.Length; trainedIndex++)
            {
                if (!names.Contains(_trained[trainedIndex].playerName)) names.Add(_trained[trainedIndex].playerName);
            }
            return names.ToArray();
        }

        /// <summary>
        /// A brain is USABLE when the profile carries one and it fits this runtime's
        /// observation contract: a model trained on an older contract is refused at
        /// Awake and the body is driven by the scripted bot, so `brainModel != null`
        /// alone would promise a brain the match will not field.
        /// </summary>
        static bool IsUsable(Reward_Settings profile)
        {
            if (profile == null || profile.brainModel == null) return false;
            try
            {
                return Agent_Soccer.ModelFitsContract(profile.brainModel);
            }
            catch (System.Exception)
            {
                // An LFS pointer stub or a corrupt asset throws on load; the menu must
                // not be what dies on a bad binary.
                return false;
            }
        }

        static Reward_Settings[] Compact(params Reward_Settings[] entries)
        {
            var list = new List<Reward_Settings>(entries == null ? 0 : entries.Length);
            if (entries == null) return list.ToArray();
            for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                if (entries[entryIndex] != null && !list.Contains(entries[entryIndex])) list.Add(entries[entryIndex]);
            }
            return list.ToArray();
        }

        static Reward_Settings[] TrainedOnly(Reward_Settings[] entries)
        {
            var list = new List<Reward_Settings>(entries.Length);
            for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
            {
                if (IsUsable(entries[entryIndex])) list.Add(entries[entryIndex]);
            }
            return list.ToArray();
        }

        // -- Remembered mode and lineup ------------------------------------------

        Mode SavedMode()
        {
            int saved = PlayerPrefs.GetInt(MODE_KEY, (int)Mode.OneVsOne);
            return saved >= 0 && saved <= (int)Mode.TeamCup ? (Mode)saved : Mode.OneVsOne;
        }

        void SaveMode()
        {
            PlayerPrefs.SetInt(MODE_KEY, (int)_mode);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Writes both squads as profile NAMES. Called on PLAY only: a lineup someone
        /// was only poking at is not a lineup they chose.
        /// </summary>
        void SaveLineup()
        {
            PlayerPrefs.SetString(LINEUP_BLUE_KEY, JoinNames(_blue));
            PlayerPrefs.SetString(LINEUP_RED_KEY, JoinNames(_red));
            PlayerPrefs.Save();
        }

        static string JoinNames(List<Reward_Settings> squad)
        {
            var builder = new System.Text.StringBuilder();
            for (int slot = 0; slot < squad.Count; slot++)
            {
                if (squad[slot] == null) continue;
                if (builder.Length > 0) builder.Append(LINEUP_SEPARATOR);
                builder.Append(squad[slot].playerName);
            }
            return builder.ToString();
        }

        /// <summary>
        /// Rebuilds both squads from the saved names. False - leaving the caller to
        /// fill them - when nothing was saved, a name no longer resolves to a pickable
        /// character, or the saved squads do not fit the current mode's size.
        /// </summary>
        bool RestoreLineup()
        {
            _blue.Clear();
            _red.Clear();
            if (!PlayerPrefs.HasKey(LINEUP_BLUE_KEY) || !PlayerPrefs.HasKey(LINEUP_RED_KEY)) return false;

            ParseNames(PlayerPrefs.GetString(LINEUP_BLUE_KEY, string.Empty), _blue);
            ParseNames(PlayerPrefs.GetString(LINEUP_RED_KEY, string.Empty), _red);
            if (_blue.Count == Capacity && _red.Count == Capacity) return true;

            _blue.Clear();
            _red.Clear();
            return false;
        }

        void ParseNames(string saved, List<Reward_Settings> squad)
        {
            if (string.IsNullOrEmpty(saved)) return;
            string[] names = saved.Split(LINEUP_SEPARATOR);
            for (int nameIndex = 0; nameIndex < names.Length; nameIndex++)
            {
                var profile = FindCharacter(names[nameIndex]);
                if (profile != null && IsPickable(profile) && !_blue.Contains(profile) && !_red.Contains(profile))
                {
                    squad.Add(profile);
                }
            }
        }

        /// <summary>
        /// Forgets the remembered lineup and mode, so the next menu load opens on a
        /// filled 1v1. For tests: PlayerPrefs are shared with every play session on
        /// the machine.
        /// </summary>
        public static void ForgetLineup()
        {
            PlayerPrefs.DeleteKey(LINEUP_BLUE_KEY);
            PlayerPrefs.DeleteKey(LINEUP_RED_KEY);
            PlayerPrefs.DeleteKey(MODE_KEY);
        }

        /// <summary>Both sides filled in grid order, so PLAY works on arrival.</summary>
        void FillDefault()
        {
            _blue.Clear();
            _red.Clear();
            var pool = Pickable;
            for (int pick = 0; pick < pool.Length && pick < Capacity * 2; pick++)
            {
                (pick < Capacity ? _blue : _red).Add(pool[pick]);
            }
        }

        // -- Build ---------------------------------------------------------------

        /// <summary>
        /// Instantiates Resources/Menu.uxml and binds it. Separate from OnEnable
        /// because a settings change can rebuild the screen.
        /// </summary>
        void Build()
        {
            _root.Clear();
            Agent_UIStyle.ApplyTheme(_root);

            var template = Resources.Load<VisualTreeAsset>(TEMPLATE_RESOURCE);
            if (template == null)
            {
                // The menu IS the screen - this is an error, not a warning.
                Debug.LogError($"Agent_MainMenu: '{TEMPLATE_RESOURCE}' not found in Resources; the menu cannot be built.");
                return;
            }
            template.CloneTree(_root);

            var safe = _root.Q<VisualElement>("safe");
            var header = _root.Q<VisualElement>("header");
            var footer = _root.Q<VisualElement>("footer");
            var options = _root.Q<VisualElement>("options");
            _grid = _root.Q<VisualElement>("grid");
            _lineup = _root.Q<VisualElement>("lineup");
            _seatsBlue = _root.Q<VisualElement>("seats-blue");
            _seatsRed = _root.Q<VisualElement>("seats-red");
            _cupInfo = _root.Q<Label>("cup-info");
            _pitchNote = _root.Q<Label>("pitch-note");
            _brainNote = _root.Q<Label>("brain-note");
            _backHint = _root.Q<Label>("back-hint");
            _play = _root.Q<Button>("play");
            _random = _root.Q<Button>("random");
            _modeButtons[(int)Mode.OneVsOne] = _root.Q<Button>("mode-1v1");
            _modeButtons[(int)Mode.FourVsFour] = _root.Q<Button>("mode-4v4");
            _modeButtons[(int)Mode.SoloCup] = _root.Q<Button>("mode-cup1");
            _modeButtons[(int)Mode.TeamCup] = _root.Q<Button>("mode-cup4");

            if (safe == null || _grid == null || _lineup == null || _seatsBlue == null || _seatsRed == null ||
                _cupInfo == null || _play == null || _random == null || options == null ||
                System.Array.IndexOf(_modeButtons, null) >= 0)
            {
                Debug.LogError($"Agent_MainMenu: '{TEMPLATE_RESOURCE}' is missing a bound element. " +
                               "Agent_EditMode_Theme pins the full name list.");
                return;
            }

            // Safe area stays in code: a device measurement, not a design token. Both
            // chrome bars are reserved so nothing sits under MENU/SETTINGS or DEBUG.
            Agent_UIStyle.BindSafeArea(safe, Agent_Chrome.BAR_HEIGHT, Agent_Chrome.BAR_HEIGHT);

            for (int modeIndex = 0; modeIndex < _modeButtons.Length; modeIndex++)
            {
                var mode = (Mode)modeIndex;
                _modeButtons[modeIndex].clicked += () => SetMode(mode);
            }
            _play.clicked += OnPlay;
            _random.clicked += RandomLineup;

            _bracket = new Agent_BracketView(_root, FindCharacter, TournamentEntrants, LaunchTournamentMatch);

            options.Add(OptionButton("ELO", "open-elo", ToggleElo));
            options.Add(OptionButton("CARDS", "open-cards", () => OpenCards(0)));
            var gallery = OptionButton("GALLERY", "open-gallery", OpenGallery);
            gallery.SetEnabled(_trained.Length > 0);
            options.Add(gallery);
            _soundButton = Agent_UIStyle.SoundToggleButton();
            options.Add(_soundButton);

            // SETTINGS is the chrome's top-right button (2026-11-07); the menu
            // registers as the sheet's host so it opens over this panel.
            _openSettingsAction = OpenSettings;
            Agent_SettingsSheet.SetHost(_openSettingsAction);

            if (_brainNote != null) Agent_UIStyle.SetShown(_brainNote, !AnyUsableBrain);

            BuildGrid();
            RefreshAll();

            StageEntrance(header, 0);
            StageEntrance(footer, 90);
        }

        static Button OptionButton(string text, string name, System.Action onClick)
        {
            var button = new Button(onClick) { text = text, name = name };
            button.AddToClassList("btn");
            return button;
        }

        /// <summary>One tile per character: face, name, and a tag line (IN TRAINING / BLUE / RED).</summary>
        void BuildGrid()
        {
            _grid.Clear();
            for (int characterIndex = 0; characterIndex < _characters.Length; characterIndex++)
            {
                var profile = _characters[characterIndex];
                var tile = new Button(() => TapCharacter(profile)) { name = "tile-" + profile.playerName };
                tile.AddToClassList("btn");
                tile.AddToClassList("menu__tile");
                tile.userData = profile;

                tile.Add(FaceElement(profile, "menu__tile-face"));

                var name = new Label(profile.playerName) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("menu__tile-name");
                tile.Add(name);

                var tag = new Label { name = "tag", pickingMode = PickingMode.Ignore };
                tag.AddToClassList("menu__tile-tag");
                tile.Add(tag);

                if (!IsPickable(profile))
                {
                    tile.AddToClassList("menu__tile--locked");
                    tile.SetEnabled(false);
                    tag.text = "IN TRAINING";
                }
                _grid.Add(tile);
            }
        }

        /// <summary>The face photo, or the initial on the profile colour when there is none.</summary>
        static VisualElement FaceElement(Reward_Settings profile, string className)
        {
            var face = new Label { pickingMode = PickingMode.Ignore };
            face.AddToClassList(className);
            if (profile.faceTexture != null)
            {
                face.style.backgroundImage = new StyleBackground(profile.faceTexture);
            }
            else
            {
                face.text = profile.Initial.ToString();
                face.style.backgroundColor = profile.playerColor;
            }
            return face;
        }

        // -- Interaction ----------------------------------------------------------

        void SetMode(Mode mode)
        {
            if (mode == _mode) return;
            _mode = mode;
            SaveMode();
            if (!RestoreLineup()) FillDefault();
            RefreshAll();
        }

        /// <summary>
        /// A tap on a face: removes it if it is already playing, otherwise fills the
        /// first empty place, Blue before Red. Cups ignore taps (the bracket fields
        /// everyone).
        /// </summary>
        void TapCharacter(Reward_Settings profile)
        {
            if (IsCupMode || !IsPickable(profile)) return;
            if (_blue.Remove(profile) || _red.Remove(profile))
            {
                RefreshAll();
                return;
            }
            if (_blue.Count < Capacity) _blue.Add(profile);
            else if (_red.Count < Capacity) _red.Add(profile);
            RefreshAll();
        }

        void RandomLineup()
        {
            if (IsCupMode) return;
            var pool = new List<Reward_Settings>(Pickable);
            _blue.Clear();
            _red.Clear();
            for (int pick = 0; pick < Capacity * 2 && pool.Count > 0; pick++)
            {
                int index = Random.Range(0, pool.Count);
                (_blue.Count < Capacity ? _blue : _red).Add(pool[index]);
                pool.RemoveAt(index);
            }
            RefreshAll();
        }

        void OnPlay()
        {
            if (IsCupMode)
            {
                OpenBracket();
                return;
            }
            StartMatch();
        }

        void StartMatch()
        {
            if (_blue.Count != Capacity || _red.Count != Capacity) return;
            // Statics outlive a scene load in a player build: clear before setting.
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = _blue.ToArray();
            Agent_MatchSetup.RedSquad = _red.ToArray();
            SaveLineup();
            SaveMode();
            SceneManager.LoadScene(matchScene);
        }

        // -- Cups -------------------------------------------------------------------

        bool CupPlayable => _mode == Mode.SoloCup
            ? _trained.Length >= 2
            : Agent_Tournament.TeamCount(_trained.Length) >= 2;

        void OpenBracket()
        {
            if (!IsCupMode || !CupPlayable) return;
            Agent_Tournament.Mode = _mode == Mode.TeamCup ? Agent_Tournament.Cup.Team : Agent_Tournament.Cup.Solo;
            SaveMode();
            _bracket.Open();
        }

        /// <summary>Starts one cup match through the same path as PLAY: one character a side, or a team's four.</summary>
        void LaunchTournamentMatch(string blueName, string redName)
        {
            var blue = SquadFor(blueName);
            var red = SquadFor(redName);
            if (blue == null || red == null)
            {
                Debug.LogWarning($"Agent_MainMenu: cup match {blueName} vs {redName} names someone this menu " +
                                 "does not know; abandoning it.");
                Agent_Tournament.CancelMatch();
                return;
            }
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = blue;
            Agent_MatchSetup.RedSquad = red;
            SceneManager.LoadScene(matchScene);
        }

        /// <summary>A cup entrant's squad: its team's members, or the one character. Null if anyone is unknown.</summary>
        Reward_Settings[] SquadFor(string entrant)
        {
            string[] members = Agent_Tournament.IsTeamCup ? Agent_Tournament.MembersOf(entrant) : new[] { entrant };
            if (members == null || members.Length == 0) return null;
            var squad = new Reward_Settings[members.Length];
            for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
            {
                squad[memberIndex] = FindCharacter(members[memberIndex]);
                if (squad[memberIndex] == null) return null;
            }
            return squad;
        }

        void ToggleElo()
        {
            if (_eloPanel != null)
            {
                _eloPanel.RemoveFromHierarchy();
                _eloPanel = null;
                return;
            }
            _eloPanel = Agent_BracketView.EloPanel(ToggleElo);
            _root.Add(_eloPanel);
        }

        // -- Rendering --------------------------------------------------------------

        void RefreshAll()
        {
            if (_grid == null) return;

            for (int modeIndex = 0; modeIndex < _modeButtons.Length; modeIndex++)
            {
                _modeButtons[modeIndex].EnableInClassList("is-on", modeIndex == (int)_mode);
            }

            bool cup = IsCupMode;
            _lineup.style.display = cup ? DisplayStyle.None : DisplayStyle.Flex;
            _cupInfo.style.display = cup ? DisplayStyle.Flex : DisplayStyle.None;
            _random.style.display = cup ? DisplayStyle.None : DisplayStyle.Flex;

            RenderSeats(_seatsBlue, _blue, "menu__seat--blue");
            RenderSeats(_seatsRed, _red, "menu__seat--red");

            for (int tileIndex = 0; tileIndex < _grid.childCount; tileIndex++)
            {
                var tile = _grid[tileIndex];
                if (!(tile.userData is Reward_Settings profile)) continue;
                bool blue = !cup && _blue.Contains(profile);
                bool red = !cup && _red.Contains(profile);
                tile.EnableInClassList("menu__tile--blue", blue);
                tile.EnableInClassList("menu__tile--red", red);
                if (IsPickable(profile))
                {
                    var tag = tile.Q<Label>("tag");
                    if (tag != null) tag.text = blue ? "BLUE" : red ? "RED" : cup ? $"ELO {Agent_Elo.RatingOf(profile.playerName):0}" : "";
                }
            }

            if (cup)
            {
                _play.text = "BRACKET";
                _play.SetEnabled(CupPlayable);
                _cupInfo.text = CupLine();
                _pitchNote.text = _mode == Mode.SoloCup
                    ? "1v1 knockout · plays itself on AUTO"
                    : "4v4 knockout · plays itself on AUTO";
                return;
            }

            _play.text = "PLAY";
            bool full = _blue.Count == Capacity && _red.Count == Capacity;
            _play.SetEnabled(full);
            _random.SetEnabled(Pickable.Length >= Capacity * 2);

            if (Pickable.Length < Capacity * 2)
            {
                _pitchNote.text = $"{Capacity}v{Capacity} needs {Capacity * 2} trained players · {Pickable.Length} ready";
            }
            else if (!full)
            {
                _pitchNote.text = _blue.Count < Capacity ? "tap a face to add it to BLUE" : "tap a face to add it to RED";
            }
            else
            {
                Vector2 half = Agent_PitchSizing.HalfExtentsFor(Capacity, Capacity);
                _pitchNote.text = $"{Capacity}v{Capacity}  ·  pitch {half.x * 2f:0}m x {half.y * 2f:0}m  ·  tap a face to swap";
            }
        }

        string CupLine()
        {
            int ready = _trained.Length;
            if (_mode == Mode.SoloCup)
            {
                return ready >= 2
                    ? $"16-slot bracket: {ready} players + {Agent_Bracket.SIZE - ready} BYEs (players still in training)"
                    : "The 1v1 cup needs 2 trained players.";
            }
            int teams = Agent_Tournament.TeamCount(ready);
            if (teams < 2)
            {
                return $"The 4v4 cup needs 8 trained players for two teams · {ready} ready.";
            }
            int sitting = ready - teams * TEAM_SIZE;
            return $"{teams} teams of 4, drafted by Elo so they are even" +
                   (sitting > 0 ? $" · {sitting} sit out until the next full team" : "") +
                   (teams < Agent_Tournament.MAX_TEAMS ? $" · {Agent_Tournament.MAX_TEAMS - teams} BYE" : "");
        }

        void RenderSeats(VisualElement host, List<Reward_Settings> squad, string teamClass)
        {
            host.Clear();
            for (int seat = 0; seat < Capacity; seat++)
            {
                var profile = seat < squad.Count ? squad[seat] : null;
                var button = new Button(() =>
                {
                    if (profile == null) return;
                    squad.Remove(profile);
                    RefreshAll();
                }) { name = "seat" };
                button.AddToClassList("menu__seat");
                button.AddToClassList(teamClass);
                if (profile == null)
                {
                    button.AddToClassList("menu__seat--empty");
                    button.text = "?";
                }
                else
                {
                    button.tooltip = profile.playerName;
                    if (profile.faceTexture != null) button.style.backgroundImage = new StyleBackground(profile.faceTexture);
                    else
                    {
                        button.text = profile.Initial.ToString();
                        button.style.backgroundColor = profile.playerColor;
                    }
                }
                host.Add(button);
            }
        }

        static void StageEntrance(VisualElement element, long delayMs)
        {
            if (element == null) return;
            element.AddToClassList("enter-from");
            element.schedule.Execute(() => element.RemoveFromClassList("enter-from")).ExecuteLater(delayMs + 16);
        }

        // -- Settings ---------------------------------------------------------------

        System.Action _openSettingsAction;

        void OnDisable()
        {
            if (_openSettingsAction != null) Agent_SettingsSheet.ClearHost(_openSettingsAction);
        }

        void OpenSettings()
        {
            if (_root == null) return;
            Agent_SettingsSheet.Open(_root, OnSettingsChanged);
        }

        /// <summary>Team colours come from USS classes, so only the mirrored sound label needs updating.</summary>
        void OnSettingsChanged()
        {
            if (_soundButton != null) _soundButton.text = Agent_Audio.Muted ? "SND OFF" : "SND ON";
        }

        // -- Gallery ----------------------------------------------------------------

        /// <summary>Every trained brain on its own pitch against the same bot.</summary>
        void OpenGallery()
        {
            if (_trained.Length == 0) return;
            Agent_MatchSetup.Clear();
            Agent_MatchSetup.GalleryMode = true;
            Agent_MatchSetup.GalleryProfiles = _trained;
            Agent_MatchSetup.GalleryOpponent = ruleBot;

            // Agent_MatchLoader runs before Agent_Gallery and needs a lineup for the
            // pitch the grid starts from; Agent_Gallery reassigns per pitch after that.
            Agent_MatchSetup.Applied = true;
            Agent_MatchSetup.BlueSquad = new[] { _trained[0] };
            Agent_MatchSetup.RedSquad = new[] { ruleBot != null ? ruleBot : _trained[0] };
            SceneManager.LoadScene(matchScene);
        }

        // -- Player cards -----------------------------------------------------------

        VisualElement _cardOverlay, _cardHost;
        Label _cardCount;
        int _cardIndex;

        /// <summary>
        /// The player card over the whole menu, browsing every character. The ADD
        /// buttons fill a side the same way a tap on the grid does.
        /// </summary>
        void OpenCards(int index)
        {
            if (_root == null || _characters.Length == 0) return;
            if (_cardOverlay != null) CloseCards();

            _cardOverlay = new VisualElement();
            _cardOverlay.AddToClassList("panel--scrim");
            _cardOverlay.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (ReferenceEquals(evt.target, _cardOverlay)) CloseCards();
            });

            // The card scrolls: personalityNotes is free text and can run long.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.width = 1000;
            scroll.style.maxHeight = 1250;
            _cardOverlay.Add(scroll);
            _cardHost = scroll.contentContainer;
            _cardHost.style.alignItems = Align.Center;

            var nav = new VisualElement();
            nav.AddToClassList("card-nav");
            nav.Add(NavArrow("<", () => ShowCard(_cardIndex - 1)));
            _cardCount = new Label();
            _cardCount.AddToClassList("card-nav__count");
            nav.Add(_cardCount);
            nav.Add(NavArrow(">", () => ShowCard(_cardIndex + 1)));
            _cardOverlay.Add(nav);

            var add = new VisualElement();
            add.AddToClassList("card-add");
            var pick = new Button(() =>
            {
                TapCharacter(_characters[_cardIndex]);
                UpdateCardCounts();
            }) { text = "PICK / DROP", name = "card-pick" };
            pick.AddToClassList("btn");
            pick.AddToClassList("card-add__btn");
            add.Add(pick);
            _cardOverlay.Add(add);

            var close = new Button(CloseCards) { text = "CLOSE" };
            close.AddToClassList("btn");
            close.style.marginTop = Agent_UIStyle.Space4;
            _cardOverlay.Add(close);

            _root.Add(_cardOverlay);
            Agent_UIStyle.PlayEntrance(_cardOverlay);
            ShowCard(index);
        }

        Button NavArrow(string glyph, System.Action onClick)
        {
            var button = new Button(onClick) { text = glyph };
            button.AddToClassList("btn");
            button.AddToClassList("card-nav__arrow");
            return button;
        }

        void ShowCard(int index)
        {
            if (_cardHost == null || _characters.Length == 0) return;
            int count = _characters.Length;
            _cardIndex = ((index % count) + count) % count;
            _cardHost.Clear();
            _cardHost.Add(Agent_PlayerCard.Build(_characters[_cardIndex], _characters, ruleBot));
            UpdateCardCounts();
        }

        void UpdateCardCounts()
        {
            if (_cardCount == null) return;
            var profile = _characters[_cardIndex];
            string state = !IsPickable(profile) ? "IN TRAINING"
                : _blue.Contains(profile) ? "BLUE" : _red.Contains(profile) ? "RED" : "not picked";
            _cardCount.text = $"{_cardIndex + 1}/{_characters.Length}\n{state}";
        }

        void CloseCards()
        {
            if (_cardOverlay == null) return;
            if (_root != null) _root.Remove(_cardOverlay);
            _cardOverlay = null;
            _cardHost = null;
            _cardCount = null;
            RefreshAll();
        }

        // -- Back button --------------------------------------------------------------

        float _backArmedUntil;

        /// <summary>
        /// Android's back button arrives as Escape. Modal layers close first,
        /// topmost first; on the bare menu it needs a second press to quit.
        /// </summary>
        void Update()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;

            if (Agent_SettingsSheet.IsOpen)
            {
                Agent_SettingsSheet.Close();
                return;
            }
            if (_eloPanel != null)
            {
                ToggleElo();
                return;
            }
            if (_cardOverlay != null)
            {
                CloseCards();
                return;
            }
            if (_bracket != null && _bracket.IsOpen)
            {
                _bracket.Close();
                return;
            }

            if (Time.unscaledTime < _backArmedUntil)
            {
                Application.Quit();
                return;
            }

            _backArmedUntil = Time.unscaledTime + 2f;
            if (_backHint == null) return;
            Agent_UIStyle.SetShown(_backHint, true);
            _backHint.schedule.Execute(() => Agent_UIStyle.SetShown(_backHint, false)).ExecuteLater(2000);
        }
    }
}
