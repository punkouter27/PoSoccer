using NUnit.Framework;
using UnityEngine;

namespace PoSoccer.Tests
{
    /// <summary>
    /// Runs once before every PlayMode test in this assembly: asks the editor for
    /// the game's real screen, 1080 x 1920 portrait.
    ///
    /// WHY (2026-11-08). The suite used to run through an editor bridge inside a
    /// windowed editor whose Game view was portrait. Under the headless runner
    /// (scripts/unity-test.ps1, batchmode with graphics) the default is 640 x 480
    /// LANDSCAPE, where the portrait HUD's two bands cover 88 % of the height and
    /// the menu's bottom row sits under the simulated gesture bar - layout tests
    /// failing on a screen no player has. The resulting size is logged, so a run
    /// that could not get portrait says so instead of failing mysteriously.
    /// </summary>
    [SetUpFixture]
    public sealed class Agent_PlayMode_PortraitSetup
    {
        public const int WIDTH = 1080;
        public const int HEIGHT = 1920;

        [OneTimeSetUp]
        public void UsePortraitScreen()
        {
#if UNITY_EDITOR
            try
            {
                UnityEditor.PlayModeWindow.SetViewType(UnityEditor.PlayModeWindow.PlayModeViewTypes.GameView);
                UnityEditor.PlayModeWindow.SetCustomRenderingResolution(WIDTH, HEIGHT, "PoSoccer portrait");
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning("Agent_PlayMode_PortraitSetup: could not set the Game view to portrait: " + exception.Message);
            }
#endif
            // Every flow test that clicks PLAY expects a match, not a cup bracket: start
            // from the menu's default (a filled 1v1), not whatever was last played here.
            Agent_MainMenu.ForgetLineup();
            Debug.Log($"Agent_PlayMode_PortraitSetup: Screen {Screen.width}x{Screen.height}");
        }
    }
}
