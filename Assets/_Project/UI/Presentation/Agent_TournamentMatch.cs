using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// The match-scene half of tournament mode, and the Elo recorder for every
    /// completed AI match: tournament matches, exhibitions and demo (attract) matches,
    /// 1 v 1 and 4 v 4 (2026-11-22, demo Elo spec).
    ///
    /// At full time the end panel builds its SUMMARY tab, which calls the card
    /// builder registered here exactly once: that is the moment the result is
    /// final (the replay and whistle are over), so it is where the result is
    /// recorded - into the bracket for a tournament match (Agent_Tournament.Report)
    /// or straight into Agent_Elo for an exhibition or demo match
    /// (<see cref="Agent_Elo.PendingMatch"/>: recorded once; a match left early, e.g. input
    /// ending a demo, never reaches full time and records nothing). The card says who won,
    /// the Elo change and, in a tournament, what happens next.
    ///
    /// With AutoPlay on, the scene returns to the menu by itself after
    /// <see cref="AUTO_RETURN_SECONDS"/> of real time (the clock is frozen under
    /// the end panel); the menu then starts the next match. Without it the card's
    /// BRACKET button goes back.
    ///
    /// Match-only and code-installed; off in training and evaluation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_TournamentMatch : MonoBehaviour
    {
        public const float AUTO_RETURN_SECONDS = 5f;
        public const string BRACKET_BUTTON_TEXT = "BRACKET";

        [SerializeField] private string _menuScene = "SCN_Menu";

        Agent_HUD _hud;
        bool _tournament;
        bool _recorded;
        float _returnAt = -1f;
        Label _countdown;
        Agent_Elo.PendingMatch _exhibition;

        /// <summary>True when this match is a tournament match.</summary>
        public bool IsTournamentMatch => _tournament;

        void Start()
        {
            _hud = FindAnyObjectByType<Agent_HUD>();
            if (!Agent_Presentation.IsMatchScene(_hud))
            {
                enabled = false;
                return;
            }

            _tournament = Agent_Tournament.InMatch;
            bool exhibition = !_tournament && Agent_MatchSetup.Applied && !Agent_MatchSetup.HumanControl
                              && !Agent_MatchSetup.TestbedMode && !Agent_MatchSetup.GalleryMode
                              && Agent_MatchSetup.BlueSize > 0 && Agent_MatchSetup.RedSize > 0;
            if (!_tournament && !exhibition)
            {
                enabled = false;
                return;
            }
            if (exhibition) _exhibition = new Agent_Elo.PendingMatch(SquadNames(Agent_Soccer.Team.Blue), SquadNames(Agent_Soccer.Team.Red));

            _hud.AddEndPanelSection(BuildResultCard, Agent_HUD.EndTab.Summary);
            if (_tournament)
            {
                var bracket = Agent_Tournament.Bracket;
                if (bracket != null)
                {
                    _hud.Toast(bracket.RoundName(Agent_Tournament.MatchRound), Agent_UIStyle.TextPrimary, 2.2f);
                }
            }
        }

        static List<string> SquadNames(Agent_Soccer.Team team)
        {
            int size = team == Agent_Soccer.Team.Blue ? Agent_MatchSetup.BlueSize : Agent_MatchSetup.RedSize;
            var names = new List<string>(size);
            for (int slot = 0; slot < size; slot++)
            {
                var profile = Agent_MatchSetup.Get(team, slot);
                if (profile != null && !string.IsNullOrEmpty(profile.playerName)) names.Add(profile.playerName);
            }
            return names;
        }

        void OnDestroy()
        {
            _exhibition?.Abort();   // left before full time: nothing is recorded
            if (_hud != null) _hud.RemoveEndPanelSection(BuildResultCard);
        }

        void Update()
        {
            if (_returnAt < 0f) return;
            float left = _returnAt - Time.unscaledTime;
            if (_countdown != null) _countdown.text = $"next match in {Mathf.Max(0, Mathf.CeilToInt(left))}";
            if (left <= 0f)
            {
                _returnAt = -1f;
                ReturnToBracket();
            }
        }

        VisualElement BuildResultCard()
        {
            if (_hud == null) return null;
            int blue = _hud.BlueScore, red = _hud.RedScore;
            string line;
            bool champion = false;

            if (!_recorded)
            {
                _recorded = true;
                Agent_Soccer.Team? winner = blue > red ? Agent_Soccer.Team.Blue : blue < red ? Agent_Soccer.Team.Red : null;
                if (_tournament)
                {
                    // A drawn cup match goes to the higher seed (tournament standard).
                    var bracket = Agent_Tournament.Bracket;
                    if (winner == null && bracket != null && Agent_Tournament.InMatch)
                    {
                        string through = bracket.HigherSeed(Agent_Tournament.MatchRound, Agent_Tournament.MatchIndex);
                        winner = through == Agent_Tournament.BlueName ? Agent_Soccer.Team.Blue : Agent_Soccer.Team.Red;
                    }
                    line = Agent_Tournament.Report(blue, red);
                    champion = Agent_Tournament.Bracket != null && Agent_Tournament.Bracket.Finished;
                    if (Agent_Tournament.AutoPlay && !champion) _returnAt = Time.unscaledTime + AUTO_RETURN_SECONDS;
                }
                else
                {
                    line = RecordExhibition(blue, red);
                }

                // Match won / cup won: the winners CELEBRATE (Agent_Voices, level 4 / 5).
                if (winner != null)
                {
                    var voices = FindAnyObjectByType<Agent_Voices>();
                    if (voices != null && voices.isActiveAndEnabled) voices.CelebrateResult(winner.Value, champion);
                }
            }
            else
            {
                line = _tournament ? Agent_Tournament.LastResult : "";
            }

            var card = new VisualElement { name = "tournament-result" };
            card.style.alignItems = Align.Center;
            card.style.backgroundColor = Agent_UIStyle.PanelBg;
            card.style.marginBottom = Agent_UIStyle.Pad;
            Agent_UIStyle.Round(card, Agent_UIStyle.RadiusM);
            Agent_UIStyle.PadAll(card, Agent_UIStyle.Pad);

            var heading = new Label(_tournament ? $"{Agent_Tournament.CupName} #{Agent_Tournament.Number}" : "ELO");
            heading.style.fontSize = Agent_UIStyle.FontS;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.color = Agent_UIStyle.TextMuted;
            card.Add(heading);

            var result = new Label(line) { name = "tournament-result-line" };
            result.style.fontSize = champion ? Agent_UIStyle.FontL : Agent_UIStyle.FontM;
            result.style.unityFontStyleAndWeight = FontStyle.Bold;
            result.style.color = champion ? new Color(1f, 0.84f, 0.2f) : Agent_UIStyle.TextPrimary;
            result.style.whiteSpace = WhiteSpace.Normal;
            result.style.unityTextAlign = TextAnchor.MiddleCenter;
            card.Add(result);

            if (_tournament)
            {
                if (_returnAt > 0f)
                {
                    _countdown = new Label { name = "tournament-countdown" };
                    _countdown.style.fontSize = Agent_UIStyle.FontS;
                    _countdown.style.color = Agent_UIStyle.TextMuted;
                    card.Add(_countdown);
                }
                var back = new Button(ReturnToBracket) { text = BRACKET_BUTTON_TEXT, name = "tournament-bracket" };
                back.AddToClassList("btn");
                back.style.marginTop = Agent_UIStyle.Space3;
                card.Add(back);
            }
            return card;
        }

        /// <summary>Records a completed exhibition or demo match (once) and describes every rating change.</summary>
        string RecordExhibition(int blue, int red)
        {
            var deltas = _exhibition?.Complete(blue, red);
            if (deltas == null || deltas.Count == 0) return "";
            var parts = new List<string>(deltas.Count);
            foreach (var side in new[] { _exhibition.Blue, _exhibition.Red })
            {
                var named = new List<string>();
                foreach (string name in side)
                {
                    if (!deltas.TryGetValue(name, out float delta) || named.Contains(name)) continue;
                    named.Add(name);
                    parts.Add($"{name} {Agent_Elo.RatingOf(name):0} ({delta:+0;-0})");
                }
            }
            return string.Join("   ", parts);
        }

        void ReturnToBracket()
        {
            Agent_TimeFreeze.ReleaseAll();
            SceneManager.LoadScene(_menuScene);
        }
    }
}
