using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using VRC.Udon;
using Object = UnityEngine.Object;

public static class ValidateCircuitLabSource
{
    private static CircuitLabSimulator Sim() => Object.FindObjectOfType<CircuitLabSimulator>();
    private static CircuitLabSourceViewer Viewer() => Object.FindObjectOfType<CircuitLabSourceViewer>();
    private static UdonBehaviour Vm(UdonSharpBehaviour proxy) => UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
    private static int Int(UdonBehaviour vm, string field) => Convert.ToInt32(vm.GetProgramVariable(field));
    private static bool Bool(UdonBehaviour vm, string field) => Convert.ToBoolean(vm.GetProgramVariable(field));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> test, string message, int seconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!test()) { Check(DateTime.UtcNow < deadline, message); await Task.Delay(40); }
    }
    private static JObject Data() => JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/gpu-netlist.json").text);
    private static JObject SourceData() => JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/verilog-source.json").text);
    private static object Save(string name, object report)
    {
        Directory.CreateDirectory("Reports/CircuitLab"); File.WriteAllText("Reports/CircuitLab/" + name + ".json", JsonConvert.SerializeObject(report, Formatting.Indented)); return report;
    }
    private static void Click(CircuitLabButton button)
    {
        Check(button != null && button.uiButton != null && button.uiButton.IsInteractable(), "Button is unavailable.");
        Check(button.uiButton.onClick.GetPersistentTarget(0) == Vm(button), "Button does not target its Udon VM.");
        ExecuteEvents.Execute(button.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
    }
    public static object Authoring()
    {
        Check(!EditorApplication.isPlaying, "Use Edit Mode.");
        var sim = Sim(); var viewer = Viewer(); var data = SourceData(); var cpu = Data();
        Check(sim.programCount == 25 && sim.presetProgramWords.Length == 25 * 512, "Donut catalog dimensions differ.");
        var donut = cpu["programs"].Single(p => (string)p["id"] == "player-donut");
        int index = cpu["programs"].ToList().IndexOf(donut);
        Check(sim.presetProgramWords.Skip(index * 512).Take(512).SequenceEqual(donut["words"].ToObject<int[]>()), "Donut ROM differs.");
        var browser = Object.FindObjectOfType<CircuitLabProgramBrowser>();
        Check(browser.programSources[index] == (string)donut["source"] && Object.FindObjectsOfType<CircuitLabProgramRow>(true).Any(r => r.programIndex == index), "Donut browser row/source is missing.");
        var modules = Object.FindObjectsOfType<CircuitLabModule>(true);
        foreach (JObject instance in data["modules"])
        {
            int id = (int)instance["id"], entry = Array.IndexOf(viewer.moduleIds, id), definition = (int)instance["definitionIndex"];
            Check(entry >= 0 && viewer.modulePaths[entry] == (string)instance["path"] && viewer.moduleDefinitions[entry] == definition, "Instance source mapping differs.");
            Check(viewer.definitionTexts[definition] == (string)data["definitions"][definition]["text"], "Verilog body differs.");
            var module = modules.Single(m => m.moduleId == id); Check(module.sourceViewer == viewer, "Module source viewer is missing.");
            var button = module.footer.Find("VIEW_VERILOG_Canvas/VIEW_VERILOG");
            Check(module.footer.GetComponentsInChildren<CircuitLabButton>(true).Any(b => b.eventName == "ShowSource" && b.target == module && !b.requireControl), "Module source action requires control or is missing.");
        }
        Check(Object.FindObjectsOfType<CircuitLabSourceRow>(true).Length == modules.Length, "Source module rows differ.");
        Check(Object.FindObjectsOfType<CircuitLabHierarchyRow>(true).All(r => r.GetComponentsInChildren<CircuitLabButton>(true).Any(b => b.eventName == "ShowSource" && !b.requireControl)), "Hierarchy source buttons are missing.");
        var locale = viewer.localization; int code = Array.IndexOf(locale.sourceTexts, viewer.sourceText);
        Check(code >= 0 && locale.keepText[code] && viewer.sourceDisplays.Length == 3, "Source code localization protection is missing.");
        Check(viewer.sourceDisplays.All(t => t != null && !t.richText && t.font.name.StartsWith("Code-IBM-Plex-Mono")), "Verilog does not use plain monospace SDF text.");
        Check(viewer.fileTexts[0] == (string)data["files"][0]["text"], "Full source file differs.");
        Check(viewer.selectedIndex == 0 && !viewer.fullFile, "A test selection was persisted.");
        Check(!Object.FindObjectsOfType<VRC.SDK3.Components.VRCObjectSync>(true).Any(o => o.GetComponents<UdonBehaviour>().Any(v => v.SyncIsManual)), "Manual Udon sync conflicts with object sync.");
        return Save("source-authoring", new { passed = true, programs = sim.programCount, donutWords = (int)donut["romWordCount"], modules = modules.Length,
            definitions = viewer.definitionNames.Length, linkedSourceButtons = modules.Length * 2, localSourceBrowsing = true, fullFileExact = true });
    }
    public static async Task<object> Browsing()
    {
        Check(EditorApplication.isPlaying, "Enter Play Mode.");
        var viewer = Viewer(); var vm = Vm(viewer); var sim = Vm(Sim()); var network = Vm(Sim().coordinator);
        await Until(() => vm.HasDoneStart && Vm(Sim()).HasDoneStart, "Udon source viewer did not start.");
        int authority = Int(network, "controlAuthorityId"), revision = Int(sim, "programRevision"), selectedProgram = Int(sim, "programSelect");
        var modules = Object.FindObjectsOfType<CircuitLabModule>(true);
        foreach (var module in modules)
        {
            Vm(module).SendCustomEvent("ShowSource");
            int expected = Array.IndexOf(viewer.moduleIds, module.moduleId);
            await Until(() => Int(vm, "selectedIndex") == expected && viewer.instanceText.text == module.modulePath, "Module source event selected the wrong instance.");
            string name = viewer.definitionNames[viewer.moduleDefinitions[expected]];
            Check(viewer.sourceText.text.Contains("module " + name), "Wrong module definition displayed: " + name);
        }
        var registers = modules.Single(m => m.modulePath == "teaching_cpu/registers");
        var row = Object.FindObjectsOfType<CircuitLabHierarchyRow>(true).Single(r => r.module == registers);
        Click(row.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == "ShowSource"));
        await Until(() => viewer.instanceText.text == registers.modulePath, "Hierarchy source click failed.");
        Check(viewer.moduleScrollRect.verticalNormalizedPosition < .2f, "Selected module row was not brought into view.");
        Check(viewer.sourceText.text.Contains("zero <= x_count_result == 8'd0;"), "Current INX repair is absent from the displayed source.");
        string moduleText = viewer.sourceText.text;
        Click(viewer.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == "ShowFullFile"));
        await Until(() => Bool(vm, "fullFile") && viewer.sourceText.text.Contains("`endif"), "Full file was truncated.");
        await Task.Delay(300);
        Check(viewer.sourceContentRect.rect.height >= viewer.sourceDisplays[0].preferredHeight, "Full-file scroll rect clips source.");
        Click(viewer.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == "ScrollSourceDown"));
        Check(viewer.sourceScrollRect.verticalNormalizedPosition < .99f, "Source scroll button did not scroll.");
        Vm(viewer.localization).SendCustomEvent("SetChinese"); await Until(() => Int(Vm(viewer.localization), "languageIndex") == 1, "Chinese switch failed.");
        await Task.Delay(300); Check(viewer.sourceText.text.Contains("module teaching_cpu"), "Language switch modified Verilog.");
        Vm(viewer.localization).SendCustomEvent("SetJapanese"); await Task.Delay(300);
        Check(viewer.sourceText.text.Contains("module teaching_cpu"), "Japanese switch modified Verilog.");
        Vm(viewer.localization).SendCustomEvent("SetEnglish"); await Task.Delay(250);
        Click(viewer.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == "ShowModuleSource"));
        Check(viewer.sourceText.text == moduleText, "Module-only view did not recover its complete body.");
        Check(Int(network, "controlAuthorityId") == authority && Int(sim, "programRevision") == revision && Int(sim, "programSelect") == selectedProgram,
            "Source browsing changed shared CPU/control state.");
        return Save("source-browsing", new { passed = true, moduleEvents = modules.Length, hierarchyClick = true, fullFileUntruncated = true,
            scrolling = true, languages = 3, sharedCpuAndAuthorityPreserved = true });
    }
    public static async Task<object> StartDonut()
    {
        Check(EditorApplication.isPlaying, "Enter Play Mode.");
        var sim = Vm(Sim()); var network = Vm(Sim().coordinator);
        await Until(() => VRC.SDKBase.Networking.LocalPlayer != null, "ClientSim player missing.");
        network.SendCustomEvent("RequestControl");
        await Until(() => Bool(network, "ready") && Int(network, "controlAuthorityId") == VRC.SDKBase.Networking.LocalPlayer.playerId, "Cannot acquire control.");
        sim.SendCustomEvent("Pause"); await Until(() => !Bool(sim, "running") && Int(sim, "queuedCommandCount") == 0 && Int(network, "snapshotCaptureStage") == 0, "Pause failed.");
        int index = Data()["programs"].ToList().FindIndex(p => (string)p["id"] == "player-donut");
        var browser = Object.FindObjectOfType<CircuitLabProgramBrowser>(); var bv = Vm(browser);
        var row = Object.FindObjectsOfType<CircuitLabProgramRow>(true).Single(r => r.programIndex == index);
        Click(row.GetComponent<CircuitLabButton>());
        await Until(() => Int(bv, "selectedProgram") == index, "Donut selection failed.");
        var load = browser.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == "LoadSelectedPreset");
        int revision = Int(sim, "programRevision"); Click(load);
        await Until(() => Int(sim, "programRevision") > revision && Int(sim, "programSelect") == index && Int(sim, "queuedCommandCount") == 0, "Donut ROM was not loaded.");
        sim.SetProgramVariable("cyclesPerSecond", 8192f); sim.SendCustomEvent("Run");
        return new { started = true, preset = index, expectedFirstFrameCycles = 171771, actualCpuComputesFrame = true };
    }
    public static object DonutStatus()
    {
        var sim = Vm(Sim()); var terminal = Vm(Sim().terminal);
        var expected = Data()["programs"].Single(p => (string)p["id"] == "player-donut")["expectedBytes"].ToObject<int[]>();
        string frame = new string(expected.Skip(1).Select(b => (char)b).ToArray());
        return new { cycles = Int(sim, "cycleCount"), actualHz = sim.GetProgramVariable("actualCyclesPerSecond"), outputBytes = Int(terminal, "eventCount"),
            frameComplete = Convert.ToString(terminal.GetProgramVariable("output")) == frame, running = Bool(sim, "running"), missedCycles = Int(terminal, "missedCycles") };
    }
    private static async Task<Color32[]> Read(RenderTexture texture)
    {
        var completion = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture, 0, r => { if (r.hasError) completion.SetException(new InvalidOperationException("GPU readback failed.")); else completion.SetResult(r.GetData<Color32>().ToArray()); });
        if (await Task.WhenAny(completion.Task, Task.Delay(10000)) != completion.Task) throw new TimeoutException("GPU readback timed out.");
        return await completion.Task;
    }
    public static async Task<object> CompleteDonut()
    {
        var sim = Vm(Sim()); var terminal = Vm(Sim().terminal); sim.SendCustomEvent("Pause");
        await Until(() => !Bool(sim, "running") && Int(sim, "queuedCommandCount") == 0 && Int(Vm(Sim().coordinator), "snapshotCaptureStage") == 0, "Donut pause failed.");
        await Task.Delay(150);
        var program = Data()["programs"].Single(p => (string)p["id"] == "player-donut");
        string expected = new string(program["expectedBytes"].ToObject<int[]>().Skip(1).Select(b => (char)b).ToArray());
        string actual = Convert.ToString(terminal.GetProgramVariable("output"));
        Check(actual == expected, "GPU donut frame differs from the RTL/gate golden frame.");
        Check(Int(terminal, "readbackErrors") == 0 && Int(terminal, "missedCycles") == 0, "Terminal capture lost data.");
        var state = await Read((RenderTexture)sim.GetProgramVariable("currentState")); var ram = await Read((RenderTexture)sim.GetProgramVariable("currentRam"));
        var registers = new JObject(); var ports = Data()["ports"];
        foreach (string name in new[] { "pc", "instruction", "phase", "acc", "x", "sp", "out", "zero", "carry", "halt", "bank", "terminal_sequence" })
        {
            int value = 0; var ids = ports[name].ToObject<int[]>(); for (int bit = 0; bit < ids.Length; bit++) if (state[ids[bit]].r > 127) value |= 1 << bit; registers[name] = value;
        }
        return Save("donut-gpu", new { passed = true, id = "player-donut", cycles = Int(sim, "cycleCount"), output = actual,
            writes = Int(terminal, "eventCount"), width = 30, height = 11, registers, ram = ram.Select(p => (int)p.r).ToArray(),
            noTerminalLoss = true, source = "shared preset loader, real Udon VM, synthesized GPU CPU and terminal OUT events" });
    }
}
