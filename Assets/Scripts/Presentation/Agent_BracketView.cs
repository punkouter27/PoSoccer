using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The tournament screen raised over the main menu: a 16-slot bracket in five
    /// columns (round of 16, quarter-finals, semi-finals, final, champion), the
    /// next match, and the controls.
    ///
    ///   PLAY NEXT   plays the next match (1 v 1, first to the HUD's matchGoals).
    ///   AUTO        plays every remaining match unattended until a champion.
    ///   NEW         reseeds a fresh bracket from the current trained roster by Elo.
    ///   ELO         the rating table.
    ///
    /// Slots are filled with the trained roster; empty slots are BYE (greyed). A
    /// slot shows the character's face (or initial badge), name and Elo; a
    /// finished match shows its score and dims the loser.
    ///
    /// Plain C#: Agent_MainMenu owns the instance and supplies the roster lookup
    /// and the match launcher, so this class never touches scenes or statics other
    /// than Agent_Tournament and Agent_Elo.
    /// </summary>
    public sealed class Agent_BracketView
    {
        public const string OVERLAY_NAME = "bracket-overlay";
        public const string PLAY_TEXT = "PLAY NEXT";
        public const string AUTO_ON_TEXT = "AUTO: ON";
        public const string AUTO_OFF_TEXT = "AUTO: OFF";
        public const string NEW_TEXT = "NEW";
        public const string ELO_TEXT = "ELO";
        public const string CLOSE_TEXT = "CLOSE";
        const float AUTO_START_SECONDS = 3f;
        const int SLOT_HEIGHT = 60;
        const int BYE_HEIGHT = 30;
        const int FACE = 48;
        const int TEAM_FACE = 40;

        static readonly Color Gold = new(1f, 0.84f, 0.2f);
        static readonly Color BoxBg = new(1f, 1f, 1f, 0.07f);
        static readonly Color ScreenBg = new(13f / 255f, 23f / 255f, 18f / 255f, 1f);
        static readonly Color Accent = new(41f / 255f, 140f / 255f, 71f / 255f, 1f);
        static readonly Color ByeText = new(1f, 1f, 1f, 0.3f);

        readonly VisualElement _host;
        readonly System.Func<string, Reward_Settings> _lookup;
        readonly System.Func<string[]> _entrants;
        readonly System.Action<string, string> _launch;

        VisualElement _overlay, _columns, _eloPanel;
        Label _status, _next;
        Button _play, _auto;
        IVisualElementScheduledItem _autoStart;

        public bool IsOpen => _overlay != null;

        /// <param name="host">The menu's panel root.</param>
        /// <param name="lookup">Character by name (null if unknown).</param>
        /// <param name="entrants">Names for a new bracket (the trained roster).</param>
        /// <param name="launch">Starts a 1 v 1 match: (blue name, red name).</param>
        public Agent_BracketView(VisualElement host, System.Func<string, Reward_Settings> lookup,
            System.Func<string[]> entrants, System.Action<string, string> launch)
        {
            _host = host;
            _lookup = lookup;
            _entrants = entrants;
            _launch = launch;
        }

        public void Open()
        {
            if (_host == null) return;
            if (_overlay != null) Close();
            if (Agent_Tournament.Bracket == null) Agent_Tournament.StartNew(_entrants());

            _overlay = new VisualElement { name = OVERLAY_NAME };
            // A full screen of its own, opaque (2026-11-09): the shared scrim let the
            // menu grid and title show through and read as part of the bracket.
            _overlay.style.position = Position.Absolute;
            _overlay.style.left = 0;
            _overlay.style.right = 0;
            _overlay.style.top = 0;
            _overlay.style.bottom = 0;
            _overlay.style.backgroundColor = ScreenBg;
            _overlay.style.alignItems = Align.Stretch;
            _overlay.style.justifyContent = Justify.FlexStart;
            Agent_UIStyle.BindSafeArea(_overlay, Agent_Chrome.BAR_HEIGHT, Agent_Chrome.BAR_HEIGHT);
            _overlay.style.paddingLeft = Agent_UIStyle.Space3;
            _overlay.style.paddingRight = Agent_UIStyle.Space3;

            var title = new Label(Agent_Tournament.CupName) { name = "bracket-title" };
            title.style.fontSize = Agent_UIStyle.FontL;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Agent_UIStyle.TextPrimary;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            _overlay.Add(title);

            _status = new Label { name = "bracket-status" };
            _status.style.fontSize = Agent_UIStyle.FontXS;
            _status.style.color = Gold;
            _status.style.unityTextAlign = TextAnchor.MiddleCenter;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _overlay.Add(_status);

            _next = new Label { name = "bracket-next" };
            _next.style.fontSize = Agent_UIStyle.FontS;
            _next.style.unityFontStyleAndWeight = FontStyle.Bold;
            _next.style.color = Agent_UIStyle.TextPrimary;
            _next.style.unityTextAlign = TextAnchor.MiddleCenter;
            _next.style.whiteSpace = WhiteSpace.Normal;
            _next.style.backgroundColor = BoxBg;
            Agent_UIStyle.Round(_next, Agent_UIStyle.RadiusM);
            Agent_UIStyle.PadAll(_next, Agent_UIStyle.Space2);
            _next.style.marginTop = Agent_UIStyle.Space2;
            _next.style.marginBottom = Agent_UIStyle.Space3;
            _overlay.Add(_next);

            _columns = new VisualElement { name = "bracket-columns" };
            _columns.style.flexDirection = FlexDirection.Row;
            _columns.style.flexGrow = 1;
            _columns.style.flexShrink = 1;
            _overlay.Add(_columns);

            var actions = new VisualElement { name = "bracket-actions" };
            actions.style.marginTop = Agent_UIStyle.Space3;
            var primary = new VisualElement();
            primary.style.flexDirection = FlexDirection.Row;
            _play = ActionButton(PLAY_TEXT, PlayNext);
            _play.name = "bracket-play";
            _play.style.flexGrow = 2;
            _play.style.backgroundColor = Accent;
            _play.style.unityFontStyleAndWeight = FontStyle.Bold;
            primary.Add(_play);
            _auto = ActionButton(AUTO_OFF_TEXT, ToggleAuto);
            _auto.name = "bracket-auto";
            _auto.style.flexGrow = 1;
            primary.Add(_auto);
            actions.Add(primary);
            var secondary = new VisualElement();
            secondary.style.flexDirection = FlexDirection.Row;
            foreach (var button in new[] { ActionButton(NEW_TEXT, NewTournament), ActionButton(ELO_TEXT, ToggleElo), ActionButton(CLOSE_TEXT, Close) })
            {
                button.style.flexGrow = 1;
                secondary.Add(button);
            }
            actions.Add(secondary);
            _overlay.Add(actions);

            _host.Add(_overlay);
            Refresh();

            if (Agent_Tournament.AutoPlay && Agent_Tournament.Active) ScheduleAutoStart();
        }

        public void Close()
        {
            _autoStart?.Pause();
            _autoStart = null;
            if (_overlay != null) _overlay.RemoveFromHierarchy();
            _overlay = null;
            _eloPanel = null;
        }

        void PlayNext()
        {
            _autoStart?.Pause();
            _autoStart = null;
            if (!Agent_Tournament.BeginNextMatch(out string blue, out string red))
            {
                Refresh();
                return;
            }
            _launch(blue, red);
        }

        void ToggleAuto()
        {
            Agent_Tournament.AutoPlay = !Agent_Tournament.AutoPlay;
            Refresh();
            if (Agent_Tournament.AutoPlay && Agent_Tournament.Active) ScheduleAutoStart();
            else
            {
                _autoStart?.Pause();
                _autoStart = null;
            }
        }

        void NewTournament()
        {
            _autoStart?.Pause();
            _autoStart = null;
            Agent_Tournament.StartNew(_entrants());
            Refresh();
            if (Agent_Tournament.AutoPlay) ScheduleAutoStart();
        }

        void ScheduleAutoStart()
        {
            if (_overlay == null) return;
            _autoStart?.Pause();
            float startAt = Time.unscaledTime + AUTO_START_SECONDS;
            _autoStart = _overlay.schedule.Execute(() =>
            {
                float left = startAt - Time.unscaledTime;
                if (left > 0f)
                {
                    if (_next != null && Agent_Tournament.Bracket != null
                        && Agent_Tournament.Bracket.TryNextMatch(out int round, out int match))
                    {
                        _next.text = $"{NextLine(round, match)}  ·  starts in {Mathf.CeilToInt(left)}";
                    }
                    return;
                }
                _autoStart?.Pause();
                _autoStart = null;
                if (Agent_Tournament.AutoPlay) PlayNext();
            }).Every(200);
        }

        void ToggleElo()
        {
            if (_eloPanel != null)
            {
                _eloPanel.RemoveFromHierarchy();
                _eloPanel = null;
                return;
            }
            _eloPanel = EloPanel(ToggleElo);
            _overlay.Add(_eloPanel);
        }

        /// <summary>
        /// The rating table (1v1 Elo, highest first) as a floating panel that calls
        /// <paramref name="onTap"/> when tapped. Shared by the bracket screen and the
        /// main menu's ELO button.
        /// </summary>
        public static VisualElement EloPanel(System.Action onTap)
        {
            var panel = new VisualElement { name = "elo-panel" };
            panel.style.position = Position.Absolute;
            panel.style.left = Length.Percent(10);
            panel.style.right = Length.Percent(10);
            panel.style.top = Length.Percent(20);
            panel.style.backgroundColor = new Color(0.05f, 0.07f, 0.06f, 0.97f);
            Agent_UIStyle.Round(panel, Agent_UIStyle.RadiusM);
            Agent_UIStyle.PadAll(panel, Agent_UIStyle.Pad);
            panel.RegisterCallback<PointerDownEvent>(_ => onTap());

            var heading = new Label("ELO RATINGS, 1v1  (tap to close)");
            heading.style.fontSize = Agent_UIStyle.FontS;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.color = Agent_UIStyle.TextPrimary;
            heading.style.marginBottom = Agent_UIStyle.Space3;
            panel.Add(heading);

            var table = Agent_Elo.Table();
            if (table.Count == 0) panel.Add(Row("No matches recorded yet.", Agent_UIStyle.TextMuted));
            for (int rank = 0; rank < table.Count && rank < 16; rank++)
            {
                var entry = table[rank];
                panel.Add(Row($"{rank + 1,2}. {entry.name,-10} {entry.rating,5:0}   {entry.wins}W {entry.losses}L {entry.draws}D",
                    Agent_UIStyle.TextPrimary));
            }
            return panel;
        }

        static Label Row(string text, Color color)
        {
            var row = new Label(text);
            row.style.fontSize = Agent_UIStyle.FontXS;
            row.style.color = color;
            return row;
        }

        /// <summary>Rebuilds the columns and labels from Agent_Tournament.</summary>
        public void Refresh()
        {
            if (_overlay == null) return;
            var bracket = Agent_Tournament.Bracket;
            _columns.Clear();
            if (bracket == null) return;

            bool hasNext = bracket.TryNextMatch(out int nextRound, out int nextMatch);
            for (int round = 0; round < bracket.Rounds; round++)
            {
                _columns.Add(RoundColumn(bracket, round, hasNext && round == nextRound ? nextMatch : -1));
            }
            _columns.Add(ChampionColumn(bracket));

            _status.text = string.IsNullOrEmpty(Agent_Tournament.LastResult)
                ? $"#{Agent_Tournament.Number}  ·  {bracket.EntrantCount} {(Agent_Tournament.IsTeamCup ? "teams" : "players")} · {bracket.Size - bracket.EntrantCount} byes"
                : Agent_Tournament.LastResult;

            _next.text = bracket.Finished
                ? $"CHAMPION: {DisplayName(bracket.Champion)}"
                : hasNext ? NextLine(nextRound, nextMatch) : "No playable match";
            _play.SetEnabled(hasNext);
            _auto.text = Agent_Tournament.AutoPlay ? AUTO_ON_TEXT : AUTO_OFF_TEXT;
        }

        static string NextLine(int round, int match)
        {
            var bracket = Agent_Tournament.Bracket;
            return $"NEXT · {bracket.RoundName(round)}\n" +
                   $"{bracket.Entrant(round, match, 0)}  vs  {bracket.Entrant(round, match, 1)}";
        }

        static string ShortRound(Agent_Bracket bracket, int round)
        {
            int matches = bracket.MatchesIn(round);
            return matches switch
            {
                1 => "FINAL",
                2 => "SEMIS",
                4 => "QUARTERS",
                _ => $"LAST {matches * 2}",
            };
        }

        VisualElement RoundColumn(Agent_Bracket bracket, int round, int highlight)
        {
            // The first round gets the most room: it holds every name.
            var column = Column(ShortRound(bracket, round), round == 0 ? 1.35f : 1f);
            for (int match = 0; match < bracket.MatchesIn(round); match++)
            {
                // Each match sits centred in its share of the column, so a later
                // round's match lines up between the two that feed it.
                var cell = new VisualElement();
                cell.style.flexGrow = 1;
                cell.style.flexBasis = 0;
                cell.style.justifyContent = Justify.Center;

                var box = new VisualElement();
                box.style.backgroundColor = BoxBg;
                Agent_UIStyle.Round(box, Agent_UIStyle.RadiusS);
                box.style.paddingTop = box.style.paddingBottom = 2;
                box.style.marginTop = box.style.marginBottom = 2;
                if (match == highlight)
                {
                    box.style.borderTopWidth = box.style.borderBottomWidth =
                        box.style.borderLeftWidth = box.style.borderRightWidth = 3;
                    box.style.borderTopColor = box.style.borderBottomColor =
                        box.style.borderLeftColor = box.style.borderRightColor = Gold;
                }

                bool decided = bracket.IsDecided(round, match);
                string winner = decided ? bracket.WinnerOf(round, match) : null;
                for (int side = 0; side < 2; side++)
                {
                    bool known = bracket.EntrantKnown(round, match, side);
                    string name = known ? bracket.Entrant(round, match, side) : null;
                    bool lost = decided && name != winner;
                    box.Add(Slot(name, known, lost));
                }
                if (decided && bracket.played[bracket.Offset(round) + match])
                {
                    var score = new Label(bracket.ScoreOf(round, match));
                    // In the corner, not a row: a full first round of eight played
                    // matches would otherwise outgrow a phone screen.
                    score.style.position = Position.Absolute;
                    score.style.right = 6;
                    score.style.bottom = 2;
                    score.style.fontSize = 20;
                    score.style.color = Gold;
                    box.Add(score);
                }
                cell.Add(box);
                column.Add(cell);
            }
            return column;
        }

        VisualElement ChampionColumn(Agent_Bracket bracket)
        {
            var column = Column("CHAMP", 1f);
            var cell = new VisualElement();
            cell.style.flexGrow = 1;
            cell.style.justifyContent = Justify.Center;
            var box = new VisualElement();
            box.style.backgroundColor = bracket.Finished ? new Color(Gold.r, Gold.g, Gold.b, 0.25f) : BoxBg;
            Agent_UIStyle.Round(box, Agent_UIStyle.RadiusS);
            box.Add(Slot(bracket.Finished ? bracket.Champion : null, bracket.Finished, false, champion: true));
            cell.Add(box);
            column.Add(cell);
            return column;
        }

        static VisualElement Column(string heading, float grow)
        {
            var column = new VisualElement();
            column.style.flexGrow = grow;
            column.style.flexBasis = 0;
            column.style.marginLeft = 3;
            column.style.marginRight = 3;
            var label = new Label(heading);
            label.style.fontSize = 22;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = Agent_UIStyle.TextMuted;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.marginBottom = 4;
            column.Add(label);
            return column;
        }

        VisualElement Slot(string name, bool known, bool lost, bool champion = false)
        {
            var slot = new VisualElement();
            slot.style.flexDirection = FlexDirection.Row;
            slot.style.alignItems = Align.Center;
            slot.style.paddingLeft = 4;
            slot.style.paddingRight = 2;
            slot.style.overflow = Overflow.Hidden;

            bool bye = known && string.IsNullOrEmpty(name);
            // A BYE or a not-yet-known entrant is a thin, dim row, so the real
            // players carry the column.
            if (!known || bye)
            {
                slot.style.height = BYE_HEIGHT;
                var empty = new Label(bye ? "bye" : "·  ·  ·");
                empty.style.fontSize = 20;
                empty.style.color = ByeText;
                slot.Add(empty);
                return slot;
            }

            slot.style.height = SLOT_HEIGHT;
            string[] members = Agent_Tournament.MembersOf(name);
            if (members != null)
            {
                // A team: its four faces, small, captain first, over the team name.
                slot.style.height = SLOT_HEIGHT + TEAM_FACE;
                slot.style.flexDirection = FlexDirection.Column;
                slot.style.alignItems = Align.FlexStart;
                slot.style.justifyContent = Justify.Center;
                var faces = new VisualElement();
                faces.style.flexDirection = FlexDirection.Row;
                for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    var member = _lookup(members[memberIndex]);
                    if (member != null) faces.Add(SmallFace(member, TEAM_FACE));
                }
                slot.Add(faces);
            }
            else
            {
                var profile = _lookup(name);
                if (profile != null) slot.Add(SmallFace(profile, FACE));
            }

            var text = new VisualElement();
            text.style.flexShrink = 1;
            var label = new Label(name);
            label.style.fontSize = 24;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = champion ? Gold : Agent_UIStyle.TextPrimary;
            text.Add(label);
            if (members == null)
            {
                var elo = new Label($"{Agent_Elo.RatingOf(name):0}");
                elo.style.fontSize = 18;
                elo.style.color = Agent_UIStyle.TextMuted;
                text.Add(elo);
            }
            slot.Add(text);
            if (lost) slot.style.opacity = 0.35f;
            return slot;
        }

        static VisualElement SmallFace(Reward_Settings profile, int size)
        {
            var face = Agent_MatchIntro.Face(profile, profile.playerColor, size);
            face.style.marginBottom = 0;
            face.style.marginRight = 4;
            face.style.flexShrink = 0;
            face.style.borderBottomWidth = face.style.borderTopWidth =
                face.style.borderLeftWidth = face.style.borderRightWidth = 2;
            return face;
        }

        static string DisplayName(string name) => string.IsNullOrEmpty(name) ? "nobody" : name;

        static Button ActionButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn");
            button.style.marginLeft = Agent_UIStyle.Space1;
            button.style.marginRight = Agent_UIStyle.Space1;
            button.style.marginTop = Agent_UIStyle.Space1;
            return button;
        }
    }
}
