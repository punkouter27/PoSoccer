using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Shared per-pitch service locator populated once by <see cref="Agent_Presentation"/>
    /// so presentation components resolve <see cref="Env"/>, <see cref="Hud"/>, and
    /// peer services without running dozens of <c>Object.FindAnyObjectByType</c>
    /// sweeps on scene load.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_MatchContext : MonoBehaviour
    {
        public Agent_EnvController Env { get; internal set; }
        public Agent_HUD Hud { get; internal set; }
        public Agent_Audio Audio { get; internal set; }
        public Agent_ExpectedGoals ExpectedGoals { get; internal set; }

        public void Refresh()
        {
            if (Env == null) Env = GetComponent<Agent_EnvController>();
            if (Hud == null) Hud = FindAnyObjectByType<Agent_HUD>();
            if (Audio == null) Audio = GetComponent<Agent_Audio>() ?? FindAnyObjectByType<Agent_Audio>();
            if (ExpectedGoals == null) ExpectedGoals = GetComponent<Agent_ExpectedGoals>();
        }
    }
}
