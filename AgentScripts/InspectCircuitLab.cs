using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UdonSharp;
using UdonSharpEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Components;
using Object = UnityEngine.Object;

// Read-only scene/SDK audit. Reports live outside Assets and never save a scene.
public static class InspectCircuitLab
{
    private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
    private static string Path(Transform value) => value.parent == null ? value.name : Path(value.parent) + "/" + value.name;

    public static object CompilePrograms()
    {
        if (UnityEditor.EditorApplication.isPlaying) throw new InvalidOperationException("Compile Udon in Edit Mode.");
        var scripts = UnityEditor.AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/CircuitLab/Scripts" })
            .Select(guid => UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.MonoScript>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid)))
            .Where(script => script.GetClass() != null && typeof(UdonSharp.UdonSharpBehaviour).IsAssignableFrom(script.GetClass())).ToArray();
        foreach (var script in scripts)
        {
            string path = "Assets/CircuitLab/Programs/" + script.GetClass().Name + ".asset";
            var program = UnityEditor.AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(path);
            if (program == null)
            {
                program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                UnityEditor.AssetDatabase.CreateAsset(program,path);
            }
            program.sourceCsScript=script;
            UnityEditor.EditorUtility.SetDirty(program);
        }
        UnityEditor.AssetDatabase.SaveAssets();
        UdonSharpProgramAsset.CompileAllCsPrograms(true,true);
        if(UdonSharpProgramAsset.AnyUdonSharpScriptHasError()) throw new InvalidOperationException("UdonSharp compilation failed.");
        return new{compiled=true,programs=scripts.Select(s=>s.GetClass().Name).ToArray()};
    }

    public static object Baseline()
    {
        var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).ToArray();
        var transforms = Object.FindObjectsOfType<Transform>(true).Where(t => t.gameObject.scene.path.EndsWith("CircuitLab.unity")).ToArray();
        var result = new
        {
            capturedAt = DateTime.UtcNow,
            playing = UnityEditor.EditorApplication.isPlaying,
            scenes = scenes.Select(s => new { s.path, s.isDirty, s.isLoaded, roots = s.GetRootGameObjects().Select(g => new { g.name, position = V(g.transform.position), rotation = V(g.transform.eulerAngles), scale = V(g.transform.localScale) }).ToArray() }).ToArray(),
            environment = transforms.Where(t => t.name == "Floor" || Path(t).Contains("/Environment/") || t.name == "Environment").Select(t => new { path = Path(t), position = V(t.position), rotation = V(t.eulerAngles), scale = V(t.localScale), active = t.gameObject.activeSelf }).ToArray(),
            colliders = transforms.SelectMany(t => t.GetComponents<Collider>()).GroupBy(c => new { type = c.GetType().Name, layer = LayerMask.LayerToName(c.gameObject.layer), c.isTrigger }).Select(g => new { g.Key, count = g.Count() }).ToArray(),
            pickups = transforms.SelectMany(t => t.GetComponents<VRCPickup>()).Select(p => new { path = Path(p.transform), version = p.version.ToString(), p.proximity, p.DisallowTheft, autoHold = p.AutoHold.ToString(), grip = p.ExactGrip == null ? null : Path(p.ExactGrip), layer = LayerMask.LayerToName(p.gameObject.layer) }).ToArray(),
            playerCollisionLayers = new[] { "Default", "Pickup", "PickupNoEnvironment", "Walkthrough", "UI", "Player", "PlayerLocal" }.Select(name => new { name, layer = LayerMask.NameToLayer(name), playerCollision = LayerMask.NameToLayer(name) >= 0 && LayerMask.NameToLayer("Player") >= 0 && !Physics.GetIgnoreLayerCollision(LayerMask.NameToLayer(name), LayerMask.NameToLayer("Player")), localCollision = LayerMask.NameToLayer(name) >= 0 && LayerMask.NameToLayer("PlayerLocal") >= 0 && !Physics.GetIgnoreLayerCollision(LayerMask.NameToLayer(name), LayerMask.NameToLayer("PlayerLocal")) }).ToArray(),
            sdk = new { readback = typeof(VRC.SDK3.Rendering.VRCAsyncGPUReadbackRequest).GetMethods().Where(m => m.Name == "TryGetData").Select(m => m.ToString()).ToArray(), pickupVersions = Enum.GetNames(typeof(VRCPickup.Version)) }
        };
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/optimization-baseline.json", JsonConvert.SerializeObject(result, Formatting.Indented));
        return result;
    }
}
