using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;

// Development convenience only: Editor-folder code is excluded from the world.
// Preserve an unsaved smoke scene while testing CircuitLab additively. SDK
// 3.10.5 clears Udon update queues on each sceneLoaded event; normal OnEnable
// restores registration after the final additive load.
[InitializeOnLoad]
internal static class CircuitLabPlayModeBootstrap
{
    private const string Lab = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private const string Smoke = "Assets/GpuNetlistSmoke/Scenes/GpuNetlistSmoke.unity";

    static CircuitLabPlayModeBootstrap()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                EditorApplication.delayCall += PrepareAdditiveLab;
        };
    }

    private static void PrepareAdditiveLab()
    {
        if (!EditorApplication.isPlaying || SceneManager.GetActiveScene().path != Lab) return;
        var lab = SceneManager.GetSceneByPath(Lab);
        var smoke = SceneManager.GetSceneByPath(Smoke);
        if (!lab.isLoaded || !smoke.isLoaded) return;
        foreach (var root in smoke.GetRootGameObjects()) root.SetActive(false);
        var simulator = lab.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<UdonBehaviour>(true))
            .FirstOrDefault(behaviour => behaviour.name == "GpuSimulator");
        if (simulator != null && !simulator.HasDoneStart)
        {
            foreach (var root in lab.GetRootGameObjects())
            {
                if (!root.activeSelf) continue;
                root.SetActive(false);
                root.SetActive(true);
            }
        }
        Debug.Log("CircuitLab Play Mode: isolated the additive smoke scene and restored Udon registration. Edit Mode changes are preserved.");
    }
}
