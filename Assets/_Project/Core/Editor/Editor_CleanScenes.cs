using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoSoccer.EditorTools
{
    /// <summary>
    /// Removes missing MonoBehaviour references (e.g. removed Agent_Wear) from scenes
    /// via Unity's Scene API so no raw YAML text edits are needed.
    /// </summary>
    public static class Editor_CleanScenes
    {
        private static readonly string[] ScenePaths =
        {
            "Assets/_Project/Scenes/SCN_Menu.unity",
            "Assets/_Project/Scenes/SCN_Exhibition.unity",
            "Assets/_Project/Scenes/SCN_Training.unity",
        };

        public static void Run()
        {
            int removedTotal = 0;
            foreach (string path in ScenePaths)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                int removed = 0;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                    }
                }
                if (removed > 0)
                {
                    EditorSceneManager.SaveScene(scene);
                    removedTotal += removed;
                }
                Debug.Log($"[Editor_CleanScenes] {path}: removed {removed} missing script(s).");
            }
            Debug.Log($"[Editor_CleanScenes] Done. Total removed: {removedTotal}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
