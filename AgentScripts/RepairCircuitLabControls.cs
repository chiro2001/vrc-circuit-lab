using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using Object = UnityEngine.Object;

public static class RepairCircuitLabControls
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private static CircuitLabButton Find(string name) => Object.FindObjectsOfType<CircuitLabButton>(true).First(b => b.eventName == name && b.gameObject.activeInHierarchy);
    private static UdonBehaviour Vm(UdonSharp.UdonSharpBehaviour b) => UdonSharpEditorUtility.GetBackingUdonBehaviour(b);
    private static int Int(UdonBehaviour vm, string key) => Convert.ToInt32(vm.GetProgramVariable(key));
    private static bool Bool(UdonBehaviour vm, string key) => Convert.ToBoolean(vm.GetProgramVariable(key));
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition()) { Check(DateTime.UtcNow < deadline, message); await Task.Delay(50); }
    }
    private static object Save(object report)
    {
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/control-clicks.json", JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }
    public static object ApplyLiveDefault()
    {
        Check(!EditorApplication.isPlaying, "Set the startup view in Edit Mode.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        Check(scene.isLoaded && !scene.isDirty, "Preserve existing unsaved scene changes.");
        var simulator = Object.FindObjectOfType<CircuitLabSimulator>();
        Undo.RecordObject(simulator, "Start analyzer in LIVE view");
        simulator.historyFrozen = false; simulator.followLatest = true; simulator.waveOffset = 0;
        UdonSharpEditorUtility.CopyProxyToUdon(simulator);
        EditorUtility.SetDirty(simulator); EditorUtility.SetDirty(Vm(simulator));
        Undo.RecordObject(simulator.waveHudText, "Show LIVE before Udon startup");
        simulator.waveHudText.text = "LIVE  /  FOLLOWING LATEST  /  FREEZE LOCKS THE VIEW";
        EditorUtility.SetDirty(simulator.waveHudText);
        EditorSceneManager.MarkSceneDirty(scene);
        Check(EditorSceneManager.SaveScene(scene), "Scene save failed.");
        return new { saved = true, startsLive = true, followLatest = true, offset = 0 };
    }
    public static object Apply()
    {
        Check(!EditorApplication.isPlaying, "Repair the toolbar in Edit Mode.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        Check(scene.isLoaded && !scene.isDirty, "Preserve existing unsaved scene changes.");
        var panel = Find("RequestControl").transform.parent.parent;
        foreach (var name in new[] { "RequestControl", "GrantControl", "ReleaseControl" })
        {
            var t = Find(name).transform.parent; Undo.RecordObject(t, "Move shared controls to header");
            var p = t.localPosition; p.y = 2.27f; t.localPosition = p;
        }
        foreach (var entry in new[] { new { name = "WaveTitle", y = 2.78f }, new { name = "ControlStatus", y = 2.53f } })
        {
            var t = panel.Find(entry.name); Undo.RecordObject(t, "Reposition analyzer header");
            var p = t.localPosition; p.y = entry.y; t.localPosition = p;
        }
        var status = panel.Find("ControlStatus").GetComponent<Text>(); Undo.RecordObject(status, "Explain initial control acquisition");
        status.text = "CONTROL: UNCLAIMED | CLICK A CONTROL TO BEGIN";
        foreach (var name in new[] { "Frame", "Surface" })
        {
            var t = panel.Find(name); Undo.RecordObject(t, "Resize analyzer backing");
            var scale = t.localScale; scale.y = name == "Frame" ? 6.07f : 6f; t.localScale = scale;
        }
        EditorSceneManager.MarkSceneDirty(scene); Check(EditorSceneManager.SaveScene(scene), "Scene save failed.");
        return new { saved = true, toolbarAtTop = true, preservedModuleTransformsAndFloor = true };
    }
    private static void Click(CircuitLabButton handler)
    {
        Check(handler.uiButton.IsActive() && handler.uiButton.IsInteractable(), "Button is disabled: " + handler.name);
        Check(handler.uiButton.onClick.GetPersistentTarget(0) == Vm(handler), "Click target is not backing Udon: " + handler.name);
        // Exercise Unity Button's pointer handler and its persistent Udon event.
        ExecuteEvents.Execute(handler.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
    }
    private static void CheckRay(CircuitLabButton handler)
    {
        var canvas = handler.GetComponentInParent<Canvas>();
        RaycastHit hit;
        Check(Physics.Raycast(handler.transform.position - canvas.transform.forward * .25f, canvas.transform.forward, out hit, .3f, 1 << handler.gameObject.layer, QueryTriggerInteraction.Ignore), "UI shape is unreachable: " + handler.name);
        Check(hit.collider.gameObject == canvas.gameObject, "Another collider intercepts the UI ray: " + handler.name + " / " + hit.collider.name);
    }
    public static async Task<object> Verify()
    {
        Check(EditorApplication.isPlaying, "Enter Play Mode first.");
        await Until(() => Networking.LocalPlayer != null, "ClientSim player missing.");
        var coordinator = Object.FindObjectOfType<CircuitLabNetworkCoordinator>(); var net = Vm(coordinator);
        var sim = Vm(coordinator.simulator);
        Check(Int(net, "controlAuthorityId") < 0, "Run this test from a fresh unclaimed Play Mode.");
        var initialButtons = Object.FindObjectsOfType<CircuitLabButton>().Where(b => b.requireControl).ToArray();
        Check(initialButtons.All(b => b.uiButton.IsInteractable()), "Unclaimed controls are still disabled.");
        CheckRay(Find("Step")); Click(Find("Step"));
        await Until(() => Int(sim, "cycleCount") == 1, "First STEP click did not acquire control and execute a cycle.");
        Check(Int(net, "controlAuthorityId") == Networking.LocalPlayer.playerId, "First click did not claim global control.");
        CheckRay(Find("FreezeWaveform")); Click(Find("FreezeWaveform"));
        await Until(() => Bool(sim, "historyFrozen"), "FREEZE click was not delivered.");
        var tree = Object.FindObjectOfType<CircuitLabHierarchyTree>(); var treeVm = Vm(tree);
        var directory = Object.FindObjectsOfType<CircuitLabButton>().Single(b => b.eventName == "ToggleDirectory" && ((CircuitLabHierarchyRow)b.target).module.logicalParent == null);
        var row = (CircuitLabHierarchyRow)directory.target;
        bool beforeDirectory = ((bool[])treeVm.GetProgramVariable("directoryExpanded"))[row.moduleIndex];
        CheckRay(directory); Click(directory);
        Check(((bool[])treeVm.GetProgramVariable("directoryExpanded"))[row.moduleIndex] != beforeDirectory, "Hierarchy directory click failed.");
        var select = Object.FindObjectsOfType<CircuitLabButton>().Single(b => b.name == "SELECT_1");
        CheckRay(select); Click(select);
        Check(Bool(Vm(select.target), "isSelected"), "Probe selection click failed.");
        // A simulated non-controller must remain locked, while Request stays usable.
        net.SetProgramVariable("manualTestMode", true); net.SetProgramVariable("manualTestAuthority", false);
        await Task.Delay(100);
        Check(!Find("Step").uiButton.IsInteractable(), "Non-controller STEP is enabled.");
        int queued = Int(sim, "queuedCommandCount"); Vm(Find("Step")).SendCustomEvent("Press");
        Check(Int(sim, "queuedCommandCount") == queued, "Non-controller bypassed the command guard.");
        Check(Find("RequestControl").uiButton.IsInteractable(), "Request button is disabled for an observer.");
        Click(Find("RequestControl")); await Task.Delay(100);
        Check(Bool(net, "manualTestAuthority") && Find("Step").uiButton.IsInteractable(), "Request click did not enable observer controls.");
        net.SetProgramVariable("manualTestMode", false);
        return Save(new { passed = true, initialEnabledControls = initialButtons.Length, firstClickClaimsAndSteps = true, hierarchyClick = true, probeSelectionClick = true, freezeClick = true, observerLock = true, requestClick = true, physicsShapeRays = 4, inputPath = "Unity pointerClick -> Button.onClick -> backing Udon Press -> actual target", nativeMouseInputAndMultiplayerNotExercised = true });
    }
}
