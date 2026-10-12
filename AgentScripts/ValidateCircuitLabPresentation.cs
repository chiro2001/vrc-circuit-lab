using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VRC.Udon;
using Object = UnityEngine.Object;

public static class ValidateCircuitLabPresentation
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private static T[] Find<T>() where T : Component => Object.FindObjectsOfType<T>(true).Where(c => c.gameObject.scene.path == ScenePath).ToArray();
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static object Save(string name, object report)
    {
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/" + name + ".json", JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }
    private static JObject Pose(string key, Transform t) => new JObject
    {
        ["key"] = key, ["position"] = JArray.FromObject(new[]{t.position.x,t.position.y,t.position.z}),
        ["rotation"] = JArray.FromObject(new[]{t.rotation.x,t.rotation.y,t.rotation.z,t.rotation.w}),
        ["scale"] = JArray.FromObject(new[]{t.localScale.x,t.localScale.y,t.localScale.z})
    };
    public static object Baseline()
    {
        Check(!EditorApplication.isPlaying, "Record the existing layout in Edit Mode.");
        var poses = new JArray();
        foreach (var module in Find<CircuitLabModule>()) poses.Add(Pose("module:" + module.modulePath, module.transform));
        foreach (var tool in Find<CircuitLabMovableTool>()) poses.Add(Pose("tool:" + tool.name, tool.transform));
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        var floor = scene.GetRootGameObjects().Single(g => g.name == "CircuitLabWorld").transform.Find("Floor");
        poses.Add(Pose("floor", floor));
        return Save("presentation-baseline", new { poses, wasDirty = scene.isDirty });
    }
    public static object ReloadSavedScene()
    {
        Check(!EditorApplication.isPlaying, "Reload the authored scene after exiting Play Mode.");
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Check(scene.isLoaded, "Saved scene did not load.");
        return new { reloaded = true, scene = scene.path, dirty = scene.isDirty };
    }
    private static UdonBehaviour Vm(UdonSharpBehaviour proxy) => UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
    private static int Int(UdonBehaviour vm, string field) => Convert.ToInt32(vm.GetProgramVariable(field));
    private static bool Bool(UdonBehaviour vm, string field) => Convert.ToBoolean(vm.GetProgramVariable(field));
    private static string Str(UdonBehaviour vm, string field) => Convert.ToString(vm.GetProgramVariable(field));
    private static async Task Until(Func<bool> predicate, string message)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(20);
        while (!predicate()) { Check(DateTime.UtcNow < limit, message); await Task.Delay(30); }
    }
    private static void Click(CircuitLabButton handler)
    {
        Check(handler != null && handler.uiButton.IsActive() && handler.uiButton.IsInteractable(), "UI click target unavailable.");
        Check(handler.uiButton.onClick.GetPersistentTarget(0) == Vm(handler), "UI click bypasses Udon.");
        ExecuteEvents.Execute(handler.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
    }
    private static void BrowserClick(string eventName) => Click(Find<CircuitLabProgramBrowser>().Single().GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == eventName));
    private static void Select(int index) => Click(Find<CircuitLabProgramRow>().Single(r => r.programIndex == index).GetComponent<CircuitLabButton>());
    public static object Authoring()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        var world = scene.GetRootGameObjects().Single(g => g.name == "CircuitLabWorld").transform;
        var baseline = JObject.Parse(File.ReadAllText("Reports/CircuitLab/presentation-baseline.json"));
        int preserved = 0;
        foreach (JObject pose in (JArray)baseline["poses"])
        {
            string key = (string)pose["key"];
            Transform transform = key == "floor" ? world.Find("Floor") : key.StartsWith("module:")
                ? Find<CircuitLabModule>().Single(m => "module:" + m.modulePath == key).transform
                : Find<CircuitLabMovableTool>().Single(t => "tool:" + t.name == key).transform;
            var actual = Pose(key, transform);
            foreach (string field in new[]{"position", "rotation", "scale"})
            {
                float[] before = pose[field].ToObject<float[]>(), after = actual[field].ToObject<float[]>();
                Check(before.Zip(after, (a,b) => Mathf.Abs(a-b)).All(delta => delta < .00001f), "Existing layout changed: " + key + " / " + field);
            }
            preserved++;
        }
        var layout = Find<CircuitLabLayout>().Single();
        foreach (string name in new[]{"AboutPanel", "ProgramBrowserPanel"})
        {
            var panel = world.Find(name); Check(panel != null, "Missing panel: " + name);
            var tool = panel.GetComponent<CircuitLabMovableTool>();
            Check(tool != null && layout.tools.Contains(tool), "New panel not included in global layout reset: " + name);
            Check((tool.initialPosition - panel.position).sqrMagnitude < .00001f, "New panel initial position differs.");
        }
        var about = world.Find("AboutPanel").GetComponentsInChildren<Text>(true);
        Check(about.Any(t => t.text.Contains("https://github.com/chiro2001/vrc-circuit-lab")), "About panel has no complete GitHub URL.");
        var browser = Find<CircuitLabProgramBrowser>().Single();
        Check(Find<CircuitLabProgramRow>().Length == 13, "Preset list has the wrong length.");
        Check(browser.GetComponentsInChildren<ScrollRect>(true).Any(s => s.vertical), "Preset list cannot scroll.");
        Check(!Find<CircuitLabButton>().Any(b => b.eventName == "SelectProgram" && b.gameObject.activeInHierarchy), "Legacy single program switch is still active.");
        return Save("presentation-authoring", new { passed = true, preservedExistingPoses = preserved, aboutUrl = "https://github.com/chiro2001/vrc-circuit-lab", programRows = Find<CircuitLabProgramRow>().Length, scrollableList = true, newPanelsInLayoutReset = true, legacySwitchHidden = true });
    }
    public static async Task<object> BrowseAndLoad()
    {
        Check(EditorApplication.isPlaying, "Enter Play Mode first.");
        var simulator = Find<CircuitLabSimulator>().Single(); var sim = Vm(simulator);
        await Until(() => Bool(sim, "_initialized") && !Bool(sim, "readbackPending"), "CPU startup incomplete.");
        BrowserClick("ClaimControl");
        await Until(() => simulator.coordinator.HasControl() || Bool(Vm(simulator.coordinator), "ready"), "Browser cannot acquire control.");
        var browser = Find<CircuitLabProgramBrowser>().Single(); var vm = Vm(browser);
        var data = JObject.Parse(File.ReadAllText("Assets/CircuitLab/Data/gpu-netlist.json"));
        var programs = (JArray)data["programs"];
        // Establish the loaded PI preset explicitly so the suite is independent of previous GPU tests.
        int beforeLoad = Int(sim,"programRevision");
        vm.SetProgramVariable("selectedProgram",0); BrowserClick("LoadSelectedPreset");
        await Until(()=>Int(sim,"programRevision")>beforeLoad && Int(sim,"queuedCommandCount")==0,"PI baseline load failed.");
        await Task.Delay(150);
        int hello = Enumerable.Range(0, programs.Count).Single(i => (string)programs[i]["id"] == "hello");
        int revision = Int(sim, "programRevision"), cycle = Int(sim, "cycleCount"), samples = Int(sim, "sampleCount");
        var originalWords = (int[])((int[])sim.GetProgramVariable("programWords")).Clone();
        Select(hello);
        await Until(() => Int(vm, "selectedProgram") == hello && browser.browsingProgramText.text.Contains("Hello"), "List selection did not update the preview.");
        Check(browser.currentLoadedText.text.Contains("355/113"), "Loaded and browsing labels do not distinguish different programs.");
        Check(Int(sim, "programRevision") == revision && Int(sim, "cycleCount") == cycle && Int(sim, "sampleCount") == samples
            && originalWords.SequenceEqual((int[])sim.GetProgramVariable("programWords")), "Browsing altered executing CPU/ROM/history.");
        BrowserClick("ShowEncoding"); await Task.Delay(100);
        Check(browser.encodingText.text.Contains("048"), "HELLO encoding preview differs from its assembled ROM.");
        Select(0); BrowserClick("ShowSource"); await Task.Delay(100);
        Check(browser.sourceText.text.Contains(";") && browser.sourceText.text.Contains("355"), "Original PI source/comments missing.");
        string firstSourcePage = browser.sourceText.text;
        BrowserClick("NextPage"); await Task.Delay(100);
        Check(Int(vm, "browsePage") > 0 && browser.sourceText.text != firstSourcePage, "Source preview did not change the visible source page.");
        BrowserClick("ShowBinaryEncoding"); await Task.Delay(100);
        Check(browser.encodingText.text.Contains("000001110001"), "Binary preview did not show the actual first PI word.");
        var panel = Find<CircuitLabProgramPanel>().Single(); var draft = Vm(panel.sharedDraft);
        await Until(() => Int(draft, "selectedPreset") == 0 && Int(draft, "browseMode") == 2, "Browse state did not publish to the shared draft.");
        Select(hello);
        BrowserClick("LoadSelectedPreset");
        await Until(() => Int(sim, "programRevision") > revision && Int(sim, "programSelect") == hello && Int(sim, "queuedCommandCount") == 0, "Explicit ROM load did not finish.");
        Check(((int[])sim.GetProgramVariable("programWords")).SequenceEqual(programs[hello]["words"].ToObject<int[]>()), "Loaded ROM differs from the selected preset.");
        Check(Int(sim, "cycleCount") == 0 && !Bool(sim, "running"), "Loading did not initialize a paused CPU.");
        for (int i = 0; i < 26; i++) sim.SendCustomEvent("Step");
        await Until(() => Int(sim, "cycleCount") == 26 && Str(Vm(simulator.terminal), "output") == "HELLO\n", "Selected program did not produce real GPU OUT text.");
        return Save("presentation-browse-load", new { passed = true, selectionOnlyPreviews = true, actualRomUnchangedWhileBrowsing = true, originalAssemblyComments = true, sourcePaging = true, hexAndBinaryEncoding = true, sharedSelectionAndView = true, explicitLoadPreset = "hello", programWordsCompared = 512, actualGpuOutput = "HELLO\n", inputPath = "actual Unity Button.pointerClick -> backing Udon -> queued GPU load" });
    }
    public static async Task<object> SharedViewAndLayout()
    {
        var browser = Find<CircuitLabProgramBrowser>().Single(); var vm = Vm(browser);
        var simulator = Find<CircuitLabSimulator>().Single(); var sim = Vm(simulator); var net = Vm(simulator.coordinator);
        var draft = Vm(browser.sharedState);
        int cycle = Int(sim, "cycleCount"), revision = Int(sim, "programRevision");
        bool oldTest = Bool(net, "manualTestMode"), oldAuthority = Bool(net, "manualTestAuthority");
        try
        {
            net.SetProgramVariable("manualTestMode", true); net.SetProgramVariable("manualTestAuthority", false);
            await Task.Delay(100);
            var load = browser.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == "LoadSelectedPreset");
            Check(!load.uiButton.IsInteractable(), "Observer can load ROM.");
            Vm(load).SendCustomEvent("Press"); Check(Int(sim, "programRevision") == revision, "Observer bypassed ROM load guard.");
            draft.SetProgramVariable("selectedPreset", 0); draft.SetProgramVariable("browseMode", 1); draft.SetProgramVariable("browsePage", 42);
            draft.SendCustomEvent("_onDeserialization");
            await Until(() => Int(vm, "selectedProgram") == 0 && Int(vm, "browseMode") == 1 && Int(vm, "browsePage") == 42, "Shared browser selection/mode/page callback did not restore the last ROM page.");
            Check(browser.pageText.text.Contains("43"), "Restored preview page label differs.");
            Check(Int(sim, "cycleCount") == cycle && Int(sim, "programRevision") == revision, "Shared browser view altered the CPU.");
        }
        finally { net.SetProgramVariable("manualTestMode", oldTest); net.SetProgramVariable("manualTestAuthority", oldAuthority); }
        var tools = Find<CircuitLabMovableTool>().Where(t => t.name == "AboutPanel" || t.name == "ProgramBrowserPanel").ToArray();
        foreach (var tool in tools) { tool.transform.position += new Vector3(.5f, .3f, .2f); tool.transform.Rotate(0, 15, 0); }
        Vm(Find<CircuitLabLayout>().Single()).SendCustomEvent("ResetLayout"); await Task.Delay(150);
        foreach (var tool in tools) Check((tool.transform.position - tool.initialPosition).sqrMagnitude < .00001f && Quaternion.Angle(tool.transform.rotation, tool.initialRotation) < .001f, "Layout reset did not restore the new panel: " + tool.name);
        Check(Int(sim, "cycleCount") == cycle && Int(sim, "programRevision") == revision, "Layout reset altered the CPU.");
        return Save("presentation-shared-layout", new { passed = true, observerRomLoadDenied = true, actualUdonDeserializationCallback = true, lastRomPageRestored = 42, sharedViewPreservesCpu = true, newPanelsReset = tools.Length, layoutResetPreservesCpu = true, realMultiplayerTransportNotExercised = true });
    }
}
