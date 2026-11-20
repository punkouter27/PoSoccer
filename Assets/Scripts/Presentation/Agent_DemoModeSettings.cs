using UnityEngine;

namespace PoSoccer
{
    /// <summary>
    /// Editor-tunable demo (attract) mode: how long the game waits with no input, which
    /// camera shots it cycles and how long each holds (Po Game Standard, C13). The shipped
    /// asset is Assets/Resources/DemoModeSettings.asset; without it the defaults below apply.
    /// </summary>
    [CreateAssetMenu(menuName = "PoSoccer/Demo Mode Settings", fileName = "DemoModeSettings")]
    public sealed class Agent_DemoModeSettings : ScriptableObject
    {
        /// <summary>Resources path of the shipped asset, without extension.</summary>
        public const string RESOURCE = "DemoModeSettings";

        /// <summary>One showcase camera set-up.</summary>
        public enum Shot
        {
            /// <summary>The director's broadcast camera (wide shot, replays, impact cuts).</summary>
            Broadcast,
            /// <summary>Tight follow on the ball.</summary>
            FollowBall,
            /// <summary>Slow orbit around the ball.</summary>
            Orbit,
            /// <summary>Focus lock on a random player.</summary>
            FocusPlayer,
        }

        [Tooltip("Seconds with no keyboard, mouse, touch or gamepad input before demo mode starts.")]
        [Min(5f)] public float idleSeconds = 30f;

        [Tooltip("Seconds each showcase camera shot holds before the next.")]
        [Min(2f)] public float cameraDwellSeconds = 7f;

        [Tooltip("Showcase camera shots, cycled in order.")]
        public Shot[] shots = { Shot.Broadcast, Shot.FocusPlayer, Shot.FollowBall, Shot.Orbit };

        [Tooltip("From the menu (or a 1-player match), start an all-AI exhibition. Off = demo only inside a watch match.")]
        public bool startExhibitionFromMenu = true;

        [Tooltip("Match scene the demo exhibition loads.")]
        public string matchScene = "SCN_Exhibition";

        [Tooltip("Menu scene: where a demo exhibition returns to on input.")]
        public string menuScene = "SCN_Menu";

        [Tooltip("Seconds the final score stays up before the next demo exhibition starts.")]
        [Min(1f)] public float nextMatchAfterSeconds = 6f;

        /// <summary>The shipped asset, or a default instance when it is missing.</summary>
        public static Agent_DemoModeSettings Load()
        {
            var asset = Resources.Load<Agent_DemoModeSettings>(RESOURCE);
            return asset != null ? asset : CreateInstance<Agent_DemoModeSettings>();
        }

        /// <summary>The shot to use at position <paramref name="index"/> of the cycle (wraps; Broadcast when empty).</summary>
        public Shot ShotAt(int index)
        {
            if (shots == null || shots.Length == 0) return Shot.Broadcast;
            int i = index % shots.Length;
            return shots[i < 0 ? i + shots.Length : i];
        }
    }
}
