using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
using System.Collections;
using UnityEngine.Networking;
#endif

namespace PoSoccer
{
    /// <summary>
    /// "Bet on Brains" PoCoins Prediction Streak & Global/Local Elo Leaderboard.
    ///
    /// Spectators predict the winning team (Blue or Red) before or during the opening
    /// kickoff countdown using virtual PoCoins (starting bankroll 100, floor 25 so a
    /// player is never bankrupt). Payout odds are derived directly from the live
    /// <see cref="Agent_Elo.Expected"/> logistic win probability between the two squads.
    ///
    /// At full time, an end-panel card settles the prediction, updates the streak,
    /// displays the Elo standings table, and optionally syncs ratings to a REST
    /// leaderboard endpoint when <c>POSOCCER_LEADERBOARD_URL</c> is set (compiled out
    /// on Android unless <c>POSOCCER_BRAIN_HUB</c> is defined).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_Wager : MonoBehaviour
    {
        public const int START_COINS = 100;
        public const int MIN_COINS = 25;
        public const int DEFAULT_STAKE = 25;

        const string COINS_KEY = "posoccer_pocoins";
        const string STREAK_KEY = "posoccer_wager_streak";
        const string BEST_STREAK_KEY = "posoccer_wager_best";

        static int _memoryCoins = START_COINS;
        static int _memoryStreak;
        static int _memoryBestStreak;

        /// <summary>Current virtual PoCoins bankroll.</summary>
        public static int Coins
        {
            get => Application.isBatchMode ? _memoryCoins : PlayerPrefs.GetInt(COINS_KEY, START_COINS);
            set
            {
                int clamped = Mathf.Max(MIN_COINS, value);
                _memoryCoins = clamped;
                if (!Application.isBatchMode)
                {
                    PlayerPrefs.SetInt(COINS_KEY, clamped);
                    PlayerPrefs.Save();
                }
            }
        }

        /// <summary>Consecutive correct match predictions.</summary>
        public static int Streak
        {
            get => Application.isBatchMode ? _memoryStreak : PlayerPrefs.GetInt(STREAK_KEY, 0);
            set
            {
                int clamped = Mathf.Max(0, value);
                _memoryStreak = clamped;
                if (!Application.isBatchMode)
                {
                    PlayerPrefs.SetInt(STREAK_KEY, clamped);
                    PlayerPrefs.Save();
                }
                if (clamped > BestStreak) BestStreak = clamped;
            }
        }

        /// <summary>All-time best prediction streak.</summary>
        public static int BestStreak
        {
            get => Application.isBatchMode ? _memoryBestStreak : PlayerPrefs.GetInt(BEST_STREAK_KEY, 0);
            set
            {
                int clamped = Mathf.Max(0, value);
                _memoryBestStreak = clamped;
                if (!Application.isBatchMode)
                {
                    PlayerPrefs.SetInt(BEST_STREAK_KEY, clamped);
                    PlayerPrefs.Save();
                }
            }
        }

        /// <summary>Team picked to win the active match (null if no pick made).</summary>
        public static Agent_Soccer.Team? CurrentPick { get; set; }

        /// <summary>PoCoins staked on the active match.</summary>
        public static int CurrentStake { get; set; } = DEFAULT_STAKE;

        Agent_EnvController _env;
        Agent_HUD _hud;
        bool _settled;
        string _settlementLine = "";
        float _blueElo = Agent_Elo.START_RATING;
        float _redElo = Agent_Elo.START_RATING;

        /// <summary>
        /// Pure odds computation from Blue and Red Elo ratings.
        /// Returns decimal payout multipliers (e.g. 1.85x, 2.15x) clamped to [1.15, 4.50].
        /// </summary>
        public static (float blueOdds, float redOdds) ComputeOdds(float blueElo, float redElo)
        {
            float pBlue = Mathf.Clamp(Agent_Elo.Expected(blueElo, redElo), 0.12f, 0.88f);
            float pRed = 1f - pBlue;
            float blueOdds = Mathf.Clamp(Mathf.Round((0.96f / pBlue) * 100f) / 100f, 1.15f, 4.50f);
            float redOdds = Mathf.Clamp(Mathf.Round((0.96f / pRed) * 100f) / 100f, 1.15f, 4.50f);
            return (blueOdds, redOdds);
        }

        /// <summary>
        /// Pure settlement calculation for a completed prediction.
        /// Never reduces bankroll below <see cref="MIN_COINS"/>.
        /// </summary>
        public static (int newCoins, int newStreak, int delta) Settle(
            int currentCoins, int currentStreak, int stake, float oddsMultiplier, bool won)
        {
            stake = Mathf.Clamp(stake, 1, Mathf.Max(1, currentCoins));
            if (won)
            {
                int streakBonus = currentStreak * 5;
                int profit = Mathf.Max(5, Mathf.RoundToInt(stake * (oddsMultiplier - 1f))) + streakBonus;
                int nextCoins = currentCoins + profit;
                return (nextCoins, currentStreak + 1, profit);
            }
            else
            {
                int nextCoins = Mathf.Max(MIN_COINS, currentCoins - stake);
                int delta = nextCoins - currentCoins;
                return (nextCoins, 0, delta);
            }
        }

        void Start()
        {
            _env = GetComponent<Agent_EnvController>();
            _hud = FindAnyObjectByType<Agent_HUD>();
            if (!Agent_Presentation.IsMatchScene(_hud) || _env == null || Agent_MatchSetup.HumanControl)
            {
                enabled = false;
                return;
            }

            ComputeSquadElos(out _blueElo, out _redElo);
            _hud.AddEndPanelSection(BuildEndCard, Agent_HUD.EndTab.Summary);
        }

        void OnDestroy()
        {
            if (_hud != null) _hud.RemoveEndPanelSection(BuildEndCard);
        }

        void ComputeSquadElos(out float blueElo, out float redElo)
        {
            blueElo = Agent_Elo.START_RATING;
            redElo = Agent_Elo.START_RATING;
            if (_env == null) return;

            float bSum = 0f, rSum = 0f;
            int bCount = 0, rCount = 0;
            var agents = _env.GetComponentsInChildren<Agent_Soccer>();
            for (int i = 0; i < agents.Length; i++)
            {
                var a = agents[i];
                if (a == null || a.rewards == null) continue;
                float r = Agent_Elo.RatingOf(a.rewards.playerName);
                if (a.team == Agent_Soccer.Team.Blue) { bSum += r; bCount++; }
                else { rSum += r; rCount++; }
            }
            if (bCount > 0) blueElo = bSum / bCount;
            if (rCount > 0) redElo = rSum / rCount;
        }

        VisualElement BuildEndCard()
        {
            if (_hud == null) return null;

            if (!_settled)
            {
                _settled = true;
                int bScore = _hud.BlueScore;
                int rScore = _hud.RedScore;
                Agent_Soccer.Team? winner = bScore > rScore
                    ? Agent_Soccer.Team.Blue
                    : rScore > bScore ? Agent_Soccer.Team.Red : (Agent_Soccer.Team?)null;

                if (CurrentPick.HasValue && winner.HasValue)
                {
                    var (blueOdds, redOdds) = ComputeOdds(_blueElo, _redElo);
                    float odds = CurrentPick.Value == Agent_Soccer.Team.Blue ? blueOdds : redOdds;
                    bool won = CurrentPick.Value == winner.Value;
                    var (newCoins, newStreak, delta) = Settle(Coins, Streak, CurrentStake, odds, won);
                    Coins = newCoins;
                    Streak = newStreak;
                    _settlementLine = won
                        ? $"PICK WON ({CurrentPick.Value.ToString().ToUpperInvariant()} @ {odds:0.00}x)  ·  +{delta} PoCoins  ·  Bank {Coins}  ·  Streak {Streak}"
                        : $"PICK LOST ({CurrentPick.Value.ToString().ToUpperInvariant()})  ·  {delta} PoCoins  ·  Bank {Coins}  ·  Streak reset";
                }
                else
                {
                    _settlementLine = $"Bank {Coins} PoCoins  ·  Streak {Streak} (Best {BestStreak})";
                }

#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
                string endpoint = System.Environment.GetEnvironmentVariable("POSOCCER_LEADERBOARD_URL");
                if (!string.IsNullOrEmpty(endpoint))
                {
                    StartCoroutine(SyncLeaderboardCoroutine(endpoint));
                }
#endif
            }

            var card = new VisualElement { name = "wager-leaderboard-card" };
            card.AddToClassList("card");

            var heading = new Label("BET ON BRAINS & ELO STANDINGS");
            heading.AddToClassList("card__heading");
            card.Add(heading);

            var status = new Label(_settlementLine) { name = "wager-status-line" };
            status.style.fontSize = Agent_UIStyle.FontXS;
            status.style.color = Agent_UIStyle.TextPrimary;
            status.style.unityTextAlign = TextAnchor.MiddleCenter;
            status.style.marginBottom = 6;
            card.Add(status);

            List<Agent_Elo.Entry> table = Agent_Elo.Table();
            int count = Mathf.Min(table.Count, 4);
            for (int i = 0; i < count; i++)
            {
                var entry = table[i];
                var row = new Label($"#{i + 1}  {entry.name}   {entry.rating:0} ELO   ({entry.wins}W-{entry.losses}L-{entry.draws}D)");
                row.style.fontSize = Agent_UIStyle.FontXS;
                row.style.color = Agent_UIStyle.TextMuted;
                row.style.unityTextAlign = TextAnchor.MiddleCenter;
                card.Add(row);
            }

            return card;
        }

#if !UNITY_ANDROID || POSOCCER_BRAIN_HUB
        static IEnumerator SyncLeaderboardCoroutine(string url)
        {
            var table = Agent_Elo.Table();
            string payload = JsonUtility.ToJson(new LeaderboardPayload { entries = table, coins = Coins, bestStreak = BestStreak });
            using var req = new UnityWebRequest(url, "POST");
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(payload);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 5;
            yield return req.SendWebRequest();
        }

        [System.Serializable]
        struct LeaderboardPayload
        {
            public List<Agent_Elo.Entry> entries;
            public int coins;
            public int bestStreak;
        }
#endif
    }
}
