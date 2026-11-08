using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoSoccer
{
    /// <summary>
    /// Introduces the players before kickoff: a versus card with each side's
    /// faces, names, skills and Elo, shown over the opening 3-2-1 countdown and
    /// then faded out.
    ///
    /// It does NOT hold the clock. Agent_MatchFlow's opening countdown already
    /// freezes the pitch for the length of this card, and a second freeze holder
    /// would be one more thing every navigation path has to release. The card
    /// ignores pointer input, so nothing under it is blocked.
    ///
    /// Match-only and code-installed; off in training and evaluation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_MatchIntro : MonoBehaviour
    {
        public const string CARD_NAME = "match-intro";
        const float SHOW_SECONDS = 2.6f;
        const float FADE_SECONDS = 0.5f;
        const int FACE_SIZE = 150;

        VisualElement _card;
        float _hideAt = -1f;

        void Start()
        {
            var hud = FindAnyObjectByType<Agent_HUD>();
            var env = GetComponent<Agent_EnvController>();
            if (env == null) env = FindAnyObjectByType<Agent_EnvController>();
            if (env == null || !Agent_Presentation.IsMatchScene(hud) || !hud.showHud)
            {
                enabled = false;
                return;
            }
            var root = hud.GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                enabled = false;
                return;
            }

            var blue = new List<Reward_Settings>();
            var red = new List<Reward_Settings>();
            var agents = env.GetComponentsInChildren<Agent_Soccer>();
            for (int agentIndex = 0; agentIndex < agents.Length; agentIndex++)
            {
                var agent = agents[agentIndex];
                if (agent.rewards == null) continue;
                (agent.team == Agent_Soccer.Team.Blue ? blue : red).Add(agent.rewards);
            }
            if (blue.Count == 0 || red.Count == 0)
            {
                enabled = false;
                return;
            }

            _card = Build(blue, red);
            root.Add(_card);
            _hideAt = Time.unscaledTime + SHOW_SECONDS;
        }

        void Update()
        {
            if (_card == null) return;
            float left = _hideAt - Time.unscaledTime;
            if (left > 0f) return;
            float alpha = 1f + left / FADE_SECONDS;
            if (alpha <= 0f)
            {
                _card.RemoveFromHierarchy();
                _card = null;
                enabled = false;
                return;
            }
            _card.style.opacity = alpha;
        }

        static VisualElement Build(List<Reward_Settings> blue, List<Reward_Settings> red)
        {
            var card = new VisualElement { name = CARD_NAME, pickingMode = PickingMode.Ignore };
            card.style.position = Position.Absolute;
            card.style.left = 0; card.style.right = 0;
            card.style.top = Length.Percent(22);
            card.style.alignItems = Align.Center;

            var panel = new VisualElement { pickingMode = PickingMode.Ignore };
            panel.style.flexDirection = FlexDirection.Row;
            panel.style.alignItems = Align.Center;
            panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.72f);
            Agent_UIStyle.Round(panel, Agent_UIStyle.RadiusL);
            Agent_UIStyle.PadAll(panel, Agent_UIStyle.Pad);
            card.Add(panel);

            panel.Add(Side(blue, Agent_UIStyle.BlueTeam));
            var versus = new Label("VS") { pickingMode = PickingMode.Ignore };
            versus.style.fontSize = Agent_UIStyle.FontL;
            versus.style.unityFontStyleAndWeight = FontStyle.Bold;
            versus.style.color = Agent_UIStyle.TextPrimary;
            versus.style.marginLeft = Agent_UIStyle.Pad;
            versus.style.marginRight = Agent_UIStyle.Pad;
            panel.Add(versus);
            panel.Add(Side(red, Agent_UIStyle.RedTeam));
            return card;
        }

        static VisualElement Side(List<Reward_Settings> squad, Color team)
        {
            var column = new VisualElement { pickingMode = PickingMode.Ignore };
            column.style.alignItems = Align.Center;
            column.style.width = 380;

            // A squad bigger than two shows its first two and a count.
            int shown = Mathf.Min(squad.Count, 2);
            for (int playerIndex = 0; playerIndex < shown; playerIndex++)
            {
                var profile = squad[playerIndex];
                column.Add(Face(profile, team, shown == 1 ? FACE_SIZE : FACE_SIZE * 2 / 3));

                var name = new Label(profile.playerName) { pickingMode = PickingMode.Ignore };
                name.style.fontSize = Agent_UIStyle.FontM;
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.color = team;
                column.Add(name);

                if (shown == 1)
                {
                    var skills = new Label(string.IsNullOrEmpty(profile.skills) ? "" : profile.skills) { pickingMode = PickingMode.Ignore };
                    skills.style.fontSize = Agent_UIStyle.FontXS;
                    skills.style.color = Agent_UIStyle.TextMuted;
                    skills.style.whiteSpace = WhiteSpace.Normal;
                    skills.style.unityTextAlign = TextAnchor.MiddleCenter;
                    column.Add(skills);

                    var elo = new Label($"ELO {Agent_Elo.RatingOf(profile.playerName):0}") { pickingMode = PickingMode.Ignore };
                    elo.style.fontSize = Agent_UIStyle.FontXS;
                    elo.style.color = Agent_UIStyle.TextPrimary;
                    column.Add(elo);
                }
            }
            if (squad.Count > shown)
            {
                var more = new Label($"+{squad.Count - shown} more") { pickingMode = PickingMode.Ignore };
                more.style.fontSize = Agent_UIStyle.FontXS;
                more.style.color = Agent_UIStyle.TextMuted;
                column.Add(more);
            }
            return column;
        }

        /// <summary>Face photo, or the coloured initial badge for a player without one.</summary>
        public static VisualElement Face(Reward_Settings profile, Color team, int size)
        {
            var face = new Label { pickingMode = PickingMode.Ignore };
            face.style.width = size;
            face.style.height = size;
            face.style.marginBottom = Agent_UIStyle.Space2;
            face.style.borderBottomWidth = face.style.borderTopWidth =
                face.style.borderLeftWidth = face.style.borderRightWidth = 4;
            face.style.borderBottomColor = face.style.borderTopColor =
                face.style.borderLeftColor = face.style.borderRightColor = team;
            Agent_UIStyle.Round(face, Agent_UIStyle.RadiusM);
            if (profile.faceTexture != null)
            {
                face.style.backgroundImage = new StyleBackground(profile.faceTexture);
            }
            else
            {
                face.text = profile.Initial.ToString();
                face.style.backgroundColor = profile.playerColor;
                face.style.color = Color.black;
                face.style.fontSize = size / 2;
                face.style.unityFontStyleAndWeight = FontStyle.Bold;
                face.style.unityTextAlign = TextAnchor.MiddleCenter;
            }
            return face;
        }
    }
}
