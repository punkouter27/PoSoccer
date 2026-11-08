using UnityEditor;
using UnityEditor.SceneManagement;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Opens SCN_Menu and enters Play mode. The game always starts from the menu:
    /// only the menu sets the Agent_MatchSetup statics (CLAUDE.md). Launch it in a
    /// windowed editor (not batchmode):
    ///   Unity.exe -projectPath . -executeMethod PoSoccer.EditorTools.Editor_PlayMenu.Run
    /// </summary>
    public static class Editor_PlayMenu
    {
        private const string MENU_SCENE = "Assets/Scenes/SCN_Menu.unity";

        [MenuItem("PoSoccer/Play from Menu")]
        public static void Run()
        {
            if (EditorApplication.isPlaying)
            {
                return;
            }

            EditorSceneManager.OpenScene(MENU_SCENE, OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }
    }
}
