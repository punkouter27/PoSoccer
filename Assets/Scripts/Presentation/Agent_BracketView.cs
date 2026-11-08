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
        const int SLOT_HEIGHT = 66;
        const int FACE = 52;

        static readonly Color Gold = new(1f, 0.84f, 0.2f);
        static readonly Color SlotBg = new(1f, 1f, 1f, 0.08f);
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
            _overlay.AddToClassList("panel--scrim");
            _overlay.style.justifyContent = Justify.FlexStart;
            Agent_UIStyle.BindSafeArea(_overlay, Agent_Chrome.BAR_HEIGHT, Agent_Chrome.BAR_HEIGHT);

            var title = new Label("TOURNAMENT") { name = "bracket-title" };
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
            _next.style.color = Agent_UIStyle.TextPrimary;
            _next.style.unityTextAlign = TextAnchor.MiddleCenter;
            _next.style.marginBottom = Agent_UIStyle.Space3;
            _overlay.Add(_next);

            _columns = new VisualElement { name = "bracket-columns" };
            _columns.style.flexDirection = FlexDirection.Row;
            _columns.style.flexGrow = 1;
            _columns.style.width = Length.Percent(100);
            _overlay.Add(_columns);

            var actions = new VisualElement { name = "bracket-actions" };
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.justifyContent = Justify.Center;
            actions.style.marginTop = Agent_UIStyle.Space3;
            _play = ActionButton(PLAY_TEXT, PlayNext);
            _play.name = "bracket-play";
            actions.Add(_play);
            _auto = ActionButton(AUTO_OFF_TEXT, ToggleAuto);
            _auto.name = "bracket-auto";
            actions.Add(_auto);
            actions.Add(ActionButton(NEW_TEXT, NewTournament));
            actions.Add(ActionButton(ELO_TEXT, ToggleElo));
            actions.Add(ActionButton(CLOSE_TEXT, Close));
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
            _eloPanel = new VisualElement { name = "bracket-elo" };
            _eloPanel.style.position = Position.Absolute;
            _eloPanel.style.left = Length.Percent(10);
            _eloPanel.style.right = Length.Percent(10);
            _eloPanel.style.top = Length.Percent(20);
            _eloPanel.style.backgroundColor = new Color(0.05f, 0.07f, 0.06f, 0.97f);
            Agent_UIStyle.Round(_eloPanel, Agent_UIStyle.RadiusM);
            Agent_UIStyle.PadAll(_eloPanel, Agent_UIStyle.Pad);
            _eloPanel.RegisterCallback<PointerDownEvent>(_ => ToggleElo());

            var heading = new Label("ELO RATINGS  (tap to close)");
            heading.style.fontSize = Agent_UIStyle.FontS;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.color = Agent_UIStyle.TextPrimary;
            heading.style.marginBottom = Agent_UIStyle.Space3;
            _eloPanel.Add(heading);

            var table = Agent_Elo.Table();
            if (table.Count == 0) _eloPanel.Add(Row("No matches recorded yet.", Agent_UIStyle.TextMuted));
            for (int rank = 0; rank < table.Count && rank < 16; rank++)
            {
                var entry = table[rank];
                _eloPanel.Add(Row($"{rank + 1,2}. {entry.name,-10} {entry.rating,5:0}   {entry.wins}W {entry.losses}L {entry.draws}D",
                    Agent_UIStyle.TextPrimary));
            }
            _overlay.Add(_eloPanel);
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

            for (int round = 0; round < Agent_Bracket.ROUNDS; round++)
            {
                _columns.Add(RoundColumn(bracket, round));
            }
            _columns.Add(ChampionColumn(bracket));

            _status.text = string.IsNullOrEmpty(Agent_Tournament.LastResult)
                ? $"TOURNAMENT #{Agent_Tournament.Number}  ·  {bracket.EntrantCount} players, {Agent_Bracket.SIZE - bracket.EntrantCount} byes"
                : Agent_Tournament.LastResult;

            bool hasNext = bracket.TryNextMatch(out int nextRound, out int nextMatch);
            _next.text = bracket.Finished
                ? $"CHAMPION: {DisplayName(bracket.Champion)}"
                : hasNext ? NextLine(nextRound, nextMatch) : "No playable match";
            _play.SetEnabled(hasNext);
            _auto.text = Agent_Tournament.AutoPlay ? AUTO_ON_TEXT : AUTO_OFF_TEXT;
        }

        static string NextLine(int round, int match)
        {
            var bracket = Agent_Tournament.Bracket;
            return $"NEXT: {Agent_Bracket.RoundName(round)}  ·  " +
                   $"{bracket.Entrant(round, match, 0)} vs {bracket.Entrant(round, match, 1)}";
        }

        VisualElement RoundColumn(Agent_Bracket bracket, int round)
        {
            var column = Column(Agent_Bracket.RoundName(round).Replace("-FINALS", "").Replace("ROUND OF ", "R"));
            for (int match = 0; match < Agent_Bracket.MatchesIn(round); match++)
            {
                var box = new VisualElement();
                box.style.flexGrow = 1;
                box.style.justifyContent = Justify.Center;
                box.style.marginBottom = 2;

                bool decided = bracket.IsDecided(round, match);
                string winner = decided ? bracket.WinnerOf(round, match) : null;
                for (int side = 0; side < 2; side++)
                {
                    bool known = bracket.EntrantKnown(round, match, side);
                    string name = known ? bracket.Entrant(round, match, side) : null;
                    bool lost = decided && name != winner;
                    box.Add(Slot(name, known, lost));
                }
                if (decided && bracket.played[Agent_Bracket.Offset(round) + match])
                {
                    var score = new Label(bracket.ScoreOf(round, match));
                    score.style.fontSize = 24;
                    score.style.color = Agent_UIStyle.TextMuted;
                    score.style.unityTextAlign = TextAnchor.MiddleCenter;
                    box.Add(score);
                }
                column.Add(box);
            }
            return column;
        }

        VisualElement ChampionColumn(Agent_Bracket bracket)
        {
            var column = Column("CHAMP");
            var box = new VisualElement();
            box.style.flexGrow = 1;
            box.style.justifyContent = Justify.Center;
            box.Add(Slot(bracket.Finished ? bracket.Champion : null, bracket.Finished, false, champion: true));
            column.Add(box);
            return column;
        }

        static VisualElement Column(string heading)
        {
            var column = new VisualElement();
            column.style.flexGrow = 1;
            column.style.flexBasis = 0;
            column.style.marginLeft = 2;
            column.style.marginRight = 2;
            var label = new Label(heading);
            label.style.fontSize = 24;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = Agent_UIStyle.TextMuted;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            column.Add(label);
            return column;
        }

        VisualElement Slot(string name, bool known, bool lost, bool champion = false)
        {
            var slot = new VisualElement();
            slot.style.flexDirection = FlexDirection.Row;
            slot.style.alignItems = Align.Center;
            slot.style.height = SLOT_HEIGHT;
            slot.style.marginBottom = 2;
            slot.style.backgroundColor = champion && known ? new Color(Gold.r, Gold.g, Gold.b, 0.25f) : SlotBg;
            Agent_UIStyle.Round(slot, Agent_UIStyle.RadiusS);
            slot.style.paddingLeft = 4;
            slot.style.overflow = Overflow.Hidden;

            bool bye = known && string.IsNullOrEmpty(name);
            var profile = known && !bye ? _lookup(name) : null;

            if (profile != null)
            {
                var face = Agent_MatchIntro.Face(profile, profile.playerColor, FACE);
                face.style.marginBottom = 0;
                face.style.marginRight = 4;
                face.style.flexShrink = 0;
                face.style.borderBottomWidth = face.style.borderTopWidth =
                    face.style.borderLeftWidth = face.style.borderRightWidth = 2;
                slot.Add(face);
            }

            var text = new VisualElement();
            text.style.flexShrink = 1;
            var label = new Label(!known ? "—" : bye ? "BYE" : name);
            label.style.fontSize = 26;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = !known || bye ? ByeText : champion ? Gold : Agent_UIStyle.TextPrimary;
            text.Add(label);
            if (known && !bye)
            {
                var elo = new Label($"{Agent_Elo.RatingOf(name):0}");
                elo.style.fontSize = 20;
                elo.style.color = Agent_UIStyle.TextMuted;
                text.Add(elo);
            }
            slot.Add(text);
            if (lost) slot.style.opacity = 0.4f;
            return slot;
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
