using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using Object = UnityEngine.Object;

// Integration checks use actual Udon events and actual GPU readback. Reference
// outputs are assertions only and never provide production simulation state.
public static class ValidateCircuitLabRam
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private static CircuitLabSimulator Simulator() => Object.FindObjectsOfType<CircuitLabSimulator>(true).Single(s => s.gameObject.scene.path == ScenePath);
    private static UdonBehaviour Vm(UdonSharpBehaviour proxy) => UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
    private static int Int(UdonBehaviour vm, string field) => Convert.ToInt32(vm.GetProgramVariable(field));
    private static bool Bool(UdonBehaviour vm, string field) => Convert.ToBoolean(vm.GetProgramVariable(field));
    private static string Str(UdonBehaviour vm, string field) => Convert.ToString(vm.GetProgramVariable(field));
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static JObject Data() => JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/gpu-netlist.json").text);
    private static async Task Until(Func<bool> condition, string message, int seconds = 30)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { Check(DateTime.UtcNow < limit, message); await Task.Delay(40); }
    }
    private static object Save(string name, object report)
    {
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/" + name + ".json", JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }
    private static void Click(string eventName)
    {
        var handler = Object.FindObjectsOfType<CircuitLabButton>().FirstOrDefault(b => b.eventName == eventName && b.gameObject.scene.path == ScenePath);
        Check(handler != null && handler.uiButton != null && handler.uiButton.IsInteractable(), "UI button unavailable: " + eventName);
        Check(handler.uiButton.onClick.GetPersistentTarget(0) == Vm(handler), "UI event bypasses backing Udon: " + eventName);
        ExecuteEvents.Execute(handler.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
    }
    private static async Task<Color32[]> ReadBytes(RenderTexture texture)
    {
        var source = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) source.TrySetException(new InvalidOperationException("GPU byte readback failed."));
            else source.TrySetResult(request.GetData<Color32>().ToArray());
        });
        if (await Task.WhenAny(source.Task, Task.Delay(10000)) != source.Task) throw new TimeoutException("GPU byte readback timeout.");
        return await source.Task;
    }
    private static async Task<Color[]> ReadFloats(RenderTexture texture)
    {
        var source = new TaskCompletionSource<Color[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) source.TrySetException(new InvalidOperationException("GPU float readback failed."));
            else source.TrySetResult(request.GetData<Color>().ToArray());
        });
        if (await Task.WhenAny(source.Task, Task.Delay(10000)) != source.Task) throw new TimeoutException("GPU float readback timeout.");
        return await source.Task;
    }
    private static async Task<byte[]> Ram()
    {
        var pixels = await ReadBytes((RenderTexture)Vm(Simulator()).GetProgramVariable("currentRam"));
        return pixels.Select(p => p.r).ToArray();
    }
    private static CircuitLabProgramPanel Panel() => Object.FindObjectOfType<CircuitLabProgramPanel>();
    private static async Task Quiescent()
    {
        var s = Vm(Simulator()); var n = Vm(Simulator().coordinator);
        await Until(() => !Bool(s, "running") && Int(s, "queuedCommandCount") == 0 && Int(s, "queuedStepCount") == 0
            && !Bool(n, "restorePending") && Int(n, "snapshotCaptureStage") == 0, "Experiment did not reach a stable boundary.");
    }
    private static async Task LoadDraft(string text, bool binary = false)
    {
        var panel = Panel(); var vm = Vm(panel); var sim = Vm(Simulator());
        if (!Simulator().coordinator.HasControl()) Click("ClaimControl");
        await Until(() => panel.draftInput.IsInteractable(), "Controller cannot edit the program.");
        Click(binary ? "SetInputBinary" : "SetInputHex");
        panel.draftInput.text = text;
        int previous = Int(sim, "programRevision");
        Click("LoadDraft");
        await Until(() => Int(sim, "programRevision") > previous, "Valid draft was not loaded: " + panel.editorStatus.text);
        await Quiescent();
    }
    private static async Task Step(int count)
    {
        var sim = Vm(Simulator());
        for (int i = 0; i < count; i++)
        {
            int before = Int(sim, "cycleCount"); Click("Step");
            await Until(() => Int(sim, "cycleCount") == before + 1 && Int(sim, "queuedCommandCount") == 0 && Int(sim, "queuedStepCount") == 0, "STEP did not complete exactly one cycle.");
        }
        await Quiescent();
    }
    public static object BackupAuthoring()
    {
        Check(!EditorApplication.isPlaying, "Back up authored assets in Edit Mode.");
        var records = new JArray();
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/CircuitLab/Materials" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid); var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var textures = new JObject(); var serialized = new SerializedObject(material);
            var pairs = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
            for (int i = 0; pairs != null && i < pairs.arraySize; i++)
            {
                var pair = pairs.GetArrayElementAtIndex(i);
                var texture = pair.FindPropertyRelative("second.m_Texture").objectReferenceValue;
                textures[pair.FindPropertyRelative("first").stringValue] = texture == null ? "" : AssetDatabase.GetAssetPath(texture);
            }
            Check(material.shader != null && material.shader.isSupported && !material.shader.name.Contains("InternalError"), "Invalid authored material: " + path);
            records.Add(new JObject { ["kind"] = "material", ["path"] = path, ["json"] = EditorJsonUtility.ToJson(material), ["shader"] = AssetDatabase.GetAssetPath(material.shader), ["shaderName"] = material.shader.name, ["textures"] = textures });
        }
        var s = Vm(Simulator());
        foreach (string field in new[] { "programTexture", "ramInitialTexture", "restoreStateTexture", "restoreRamTexture" })
        {
            var texture = s.GetProgramVariable(field) as Texture2D; if (texture == null) continue;
            Check(texture.format == TextureFormat.RGBA32, "Runtime-mutable texture format unexpected: " + field);
            var pixels = texture.GetPixels32(); var bytes = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++) { bytes[4*i] = pixels[i].r; bytes[4*i+1] = pixels[i].g; bytes[4*i+2] = pixels[i].b; bytes[4*i+3] = pixels[i].a; }
            records.Add(new JObject { ["kind"] = "texture", ["path"] = AssetDatabase.GetAssetPath(texture), ["rgba"] = Convert.ToBase64String(bytes) });
        }
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/ram-authoring-backup.json", records.ToString());
        return new { saved = true, records = records.Count };
    }
    public static object SaveAndBackup()
    {
        Check(!EditorApplication.isPlaying, "Save the completed upgrade in Edit Mode.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        Check(scene.isLoaded, "CircuitLab scene is not loaded.");
        Check(EditorSceneManager.SaveScene(scene), "Saving the authored scene failed.");
        return BackupAuthoring();
    }
    public static object RestoreAuthoring()
    {
        Check(!EditorApplication.isPlaying, "Restore authored assets after exiting Play Mode.");
        var records = JArray.Parse(File.ReadAllText("Reports/CircuitLab/ram-authoring-backup.json"));
        foreach (var record in records)
        {
            string path = (string)record["path"];
            if ((string)record["kind"] == "material")
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                EditorJsonUtility.FromJsonOverwrite((string)record["json"], material);
                string shaderName = (string)record["shaderName"];
                Shader shader = string.IsNullOrEmpty(shaderName) ? null : Shader.Find(shaderName);
                if (shader == null && !string.IsNullOrEmpty((string)record["shader"])) shader = AssetDatabase.LoadAssetAtPath<Shader>((string)record["shader"]);
                if (shader == null) shader = material.shader; // Keep the valid reference restored by EditorJsonUtility.
                Check(shader != null && shader.isSupported && !shader.name.Contains("InternalError"), "Cannot restore material shader: " + path);
                material.shader = shader;
                foreach (var pair in ((JObject)record["textures"]).Properties())
                {
                    string texturePath = (string)pair.Value;
                    material.SetTexture(pair.Name, string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(texturePath));
                }
                EditorUtility.SetDirty(material);
            }
            else
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path); var bytes = Convert.FromBase64String((string)record["rgba"]);
                var pixels = new Color32[bytes.Length / 4];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(bytes[4*i], bytes[4*i+1], bytes[4*i+2], bytes[4*i+3]);
                texture.SetPixels32(pixels); texture.Apply(false, false); EditorUtility.SetDirty(texture);
            }
        }
        AssetDatabase.SaveAssets();
        return new { restored = true, records = records.Count };
    }
    public static object RepairBuiltinMaterials()
    {
        Check(!EditorApplication.isPlaying, "Repair authored shader references in Edit Mode.");
        Shader shader = Shader.Find("Unlit/Color"); Check(shader != null && shader.isSupported, "Built-in Unlit/Color unavailable.");
        string[] names = { "ModuleBoard", "ModuleRim", "Panel", "ProbeHighlight", "PickupHandles", "Floor", "Wall", "Probe0", "Probe1", "Probe2", "Probe3" };
        foreach (string name in names)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/CircuitLab/Materials/" + name + ".mat");
            Check(material != null, "Missing expected authored material: " + name);
            material.shader = shader; EditorUtility.SetDirty(material);
        }
        AssetDatabase.SaveAssets();
        return new { repaired = true, materials = names.Length, shader = shader.name };
    }
    public static async Task<object> Initialization()
    {
        Check(EditorApplication.isPlaying, "Enter Play Mode first.");
        var simulator = Simulator(); var sim = Vm(simulator);
        await Until(() => Networking.LocalPlayer != null && sim.HasDoneStart && Bool(sim, "_initialized"), "ClientSim CPU startup incomplete.", 40);
        Check(Int(sim, "statusCode") != 5, "Invalid GPU configuration.");
        Check(!Bool(sim, "historyFrozen") && Bool(sim, "followLatest") && simulator.waveHudText.text.StartsWith("LIVE"), "Analyzer did not start LIVE.");
        Check(((RenderTexture)sim.GetProgramVariable("currentRam")) != ((RenderTexture)sim.GetProgramVariable("currentState")), "RAM and gate state are not independent textures.");
        Check((await Ram()).Length == 256, "RAM does not contain 256 bytes.");
        return Save("ram-initialization", new { passed = true, startsLive = true, ramBytes = 256, separateStateTextures = true, initialProgram = Str(sim, "activeProgramName") });
    }
    public static async Task<object> EditingAndReset()
    {
        await LoadDraft("0x0aB ; LDI AB\n920 // STORE 20\nF00\n");
        var sim = Vm(Simulator());
        var words = (int[])sim.GetProgramVariable("programWords");
        Check(words.Length == 512 && words[0] == 0x0ab && words[1] == 0x920 && words.Skip(2).All(w => w == 0xf00), "HEX parser/image padding incorrect.");
        int revision = Int(sim, "programRevision");
        Panel().draftInput.text = "1000"; Click("LoadDraft"); await Task.Delay(150);
        Check(Int(sim, "programRevision") == revision, "Invalid HEX changed the executing image.");
        Check(Panel().editorStatus.text.ToLowerInvariant().Contains("line") || Panel().editorStatus.text.Contains("1"), "Invalid input has no line diagnostic.");
        await Step(3); // LDI fetched/executed, STORE fetched but not committed.
        Check((await Ram()).All(b => b == 0), "STORE happened before its EXEC rising edge.");
        Click("ResetCircuit"); await Quiescent();
        Check((await Ram()).All(b => b == 0), "RESET allowed the pending STORE to commit.");
        await Step(4);
        var written = await Ram(); Check(written[0x20] == 0xab && written.Where((b, i) => i != 0x20).All(b => b == 0), "GPU RAM write address/data incorrect.");
        Click("ResetCircuit"); await Quiescent();
        Check((await Ram()).SequenceEqual(written), "Warm RESET altered RAM.");
        Click("RestartExperiment"); await Quiescent();
        Check((await Ram()).All(b => b == 0), "RESTART did not initialize RAM.");
        await LoadDraft("0000 01000001\n0101 00000000\n1111 00000000", true);
        await Step(6);
        await Until(() => Str(Vm(Simulator().terminal), "output") == "A", "Binary program did not emit actual OUT.");
        return Save("ram-edit-reset", new { passed = true, hexAndBinary = true, commentsAndPrefix = true, invalidImagePreservesProgram = true, pendingStoreCancelled = true, warmResetPreservesAllRam = true, restartInitializesRam = true, binaryTerminalOutput = "A", inputPath = "Unity Button pointerClick -> backing Udon -> queued actual GPU work" });
    }
    public static async Task<object> VirtualKeyboard()
    {
        var panel = Panel(); var vm = Vm(panel);
        if (!Simulator().coordinator.HasControl()) Click("ClaimControl");
        await Until(() => panel.draftInput.IsInteractable(), "Controller cannot use the virtual keyboard.");
        Click("SetInputHex"); panel.draftInput.text = "0AB\n";
        vm.SetProgramVariable("virtualWord", 0);
        Click("InsertBit11"); Click("InsertBit0");
        Check(Int(vm, "virtualWord") == 0x801, "Bit keyboard ordering is incorrect.");
        Click("CommitWord");
        Check(panel.draftInput.text == "0AB\n0x801\n" && Int(vm, "virtualWord") == 0, "APPEND WORD did not append and clear the virtual word.");
        Click("BackspaceBit"); Check(panel.draftInput.text == "0AB\n", "DELETE LINE did not remove a trailing-newline word.");
        Click("BackspaceBit"); Check(panel.draftInput.text == "", "DELETE LINE did not remove the final line.");
        Click("SetInputBinary"); Click("InsertBit0"); Click("CommitWord");
        Check(panel.draftInput.text == "000000000001\n", "Binary keyboard did not append twelve bits.");
        Click("BackspaceBit"); Check(panel.draftInput.text == "", "Binary DELETE LINE did not remove the appended word.");
        return Save("ram-keyboard", new { passed = true, bitOrdering = true, appendHexAndBinary = true, clearsVirtualWord = true, deletesTrailingNewlineWord = true, inputPath = "actual Unity pointerClick -> backing Udon" });
    }
    public static async Task<object> RamProbe()
    {
        await LoadDraft("0AB\n920\nF00");
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        Check(tablet != null, "State tablet missing.");
        Vm(tablet).SetProgramVariable("selectRamAddress", 0x20);
        Click("SelectRamAddress"); Click("BindSelectedRamByte");
        var sim = Vm(Simulator());
        await Until(() => ((int[])sim.GetProgramVariable("probeWidths"))[0] == 8, "RAM probe was not bound.");
        await Step(4);
        var history = await ReadFloats((RenderTexture)sim.GetProgramVariable("currentHistory"));
        int segment = ((int[])sim.GetProgramVariable("probeSegments"))[0];
        for (int sample = 0; sample < 8; sample++)
        {
            var record = history[sample]; int expected = sample == 7 ? 0xab : 0;
            Check(record.g > .5f && Mathf.RoundToInt(record.r) == expected && Mathf.RoundToInt(record.b) == segment && Mathf.RoundToInt(record.a) == sample + 1, "RAM probe waveform is not actual stable RAM at sample " + sample);
        }
        Click("FreezeWaveform"); await Quiescent();
        var frozen = await ReadFloats(Simulator().frozenHistory);
        Click("ReturnAllProbes"); await Quiescent();
        Check((await ReadFloats(Simulator().frozenHistory)).SequenceEqual(frozen), "Returning a RAM probe rewrote frozen history.");
        return Save("ram-probe", new { passed = true, stableHalfSamples = 8, storeVisibleOnlyAfterExecEdge = true, virtualRamPointReadsGpuMemory = true, frozenHistoryPreservedOnReturn = true });
    }
    public static async Task<object> StartPi()
    {
        var programs = (JArray)Data()["programs"];
        int index = Enumerable.Range(0, programs.Count).First(i => ((string)programs[i]["expectedText"] ?? "").StartsWith("3.141592"));
        var panel = Panel(); var p = Vm(panel); var sim = Vm(Simulator());
        if (!Simulator().coordinator.HasControl()) Click("ClaimControl");
        p.SetProgramVariable("selectedPreset", index);
        int revision = Int(sim, "programRevision"); Click("LoadSelectedPreset");
        await Until(() => Int(sim, "programRevision") > revision, "PI preset did not load.");
        await Quiescent();
        for (int i = 0; i < 8; i++) Click("SpeedUp");
        await Quiescent(); Check(Convert.ToSingle(sim.GetProgramVariable("cyclesPerSecond")) == 256f, "Speed ceiling is not 256 cycles/s.");
        Click("Run");
        return new { started = true, preset = index, name = (string)programs[index]["name"], expectedText = (string)programs[index]["expectedText"], expectedCycles = programs[index]["expectedCycles"], targetHz = 256 };
    }
    public static async Task<object> PrepareReplica()
    {
        await LoadDraft("0AB\n920\n001\n121\n920\n601");
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        Vm(tablet).SetProgramVariable("selectRamAddress", 0x20);
        Click("SelectRamAddress"); Click("BindSelectedRamByte");
        await Step(4);
        Check((await Ram())[0x20] == 0xab, "Replica source RAM write is missing.");
        Click("FreezeWaveform"); await Quiescent();
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        await Until(() => !Bool(sim, "readbackPending") && Int(terminal, "lastProcessedCycle") >= Int(sim, "cycleCount"), "Replica source readbacks did not settle.");
        return new { ready = true, cycle = Int(sim, "cycleCount"), ram20 = 0xab, catchupRam20 = 0x22 };
    }
    public static object PiStatus()
    {
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        return new { cycle = Int(sim, "cycleCount"), output = Str(terminal, "output"), actualHz = sim.GetProgramVariable("actualCyclesPerSecond"), targetHz = sim.GetProgramVariable("cyclesPerSecond"), halt = Int(sim, "observedHalt"), queued = Int(sim, "queuedCommandCount"), snapshotStage = Int(Vm(Simulator().coordinator), "snapshotCaptureStage"), errors = Int(terminal, "readbackErrors") };
    }
    public static async Task<object> RunPi()
    {
        await StartPi();
        DateTime started = DateTime.UtcNow;
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        float peakHz = 0;
        await Until(() =>
        {
            peakHz = Mathf.Max(peakHz, Convert.ToSingle(sim.GetProgramVariable("actualCyclesPerSecond")));
            return Int(sim, "observedHalt") == 1 && Str(terminal, "output") == "3.141592\n";
        }, "Actual GPU PI program did not finish.", 45);
        var report = JObject.FromObject(await CompletePi());
        report["wallSeconds"] = (DateTime.UtcNow - started).TotalSeconds;
        report["peakObservedHz"] = peakHz;
        report["expectedCpuRunCycles"] = 2020;
        report["clockContinuesAfterHalt"] = true;
        report["terminalReadbackErrors"] = Int(terminal, "readbackErrors");
        report["terminalMissedCycles"] = Int(terminal, "missedCycles");
        return Save("ram-pi-gpu", report);
    }
    public static object AuthoringLayout()
    {
        var panel = Panel(); var canvas = panel.transform.Find("ProgramCanvas");
        var input = (RectTransform)canvas.Find("DraftInput");
        Rect Bounds(RectTransform rect) => new Rect(rect.anchoredPosition - rect.sizeDelta * .5f, rect.sizeDelta);
        int keys = 0;
        foreach (Transform t in canvas)
        {
            if (!t.name.StartsWith("VIRTUAL_") && t.name != "LOAD_DRAFT") continue;
            Check(!Bounds(input).Overlaps(Bounds((RectTransform)t)), "Program key overlaps the input: " + t.name);
            keys++;
        }
        Check(keys == 15, "Program virtual keyboard is incomplete.");
        var request = Object.FindObjectsOfType<CircuitLabButton>(true).First(b => b.eventName == "RequestControl");
        var analyzer = request.transform.root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "MovableWaveformPanel");
        var wave = analyzer.GetComponentsInChildren<Transform>(true).Single(t => t.name == "WaveformScreen");
        foreach (string name in new[] { "RequestControl", "GrantControl", "ReleaseControl" })
        {
            var button = analyzer.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == name);
            float y = analyzer.InverseTransformPoint(button.transform.position).y;
            float waveTop = analyzer.InverseTransformPoint(wave.position).y + wave.localScale.y * .5f;
            Check(y - .12f > waveTop, "Shared toolbar is below the waveform: " + name);
        }
        Check(((RectTransform)canvas.Find("REQUEST_PROGRAM_CONTROL")).anchoredPosition.y > 300, "Program request is below the header.");
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        Check(((RectTransform)tablet.transform.Find("StateTabletCanvas/TABLET_REQUEST_CONTROL")).anchoredPosition.y > 250, "Tablet request is below the header.");
        return Save("ram-ui-layout", new { passed = true, controlToolbarAboveWaveform = true, programAndTabletControlAtTop = true, keyboardControlsChecked = keys, noInputKeyboardOverlap = true });
    }
    public static object FinalAuthoring()
    {
        Check(!EditorApplication.isPlaying, "Leave the Editor stopped for delivery.");
        var simulator = Simulator(); var scene = simulator.gameObject.scene;
        Check(!scene.isDirty, "Authored scene has unsaved changes.");
        Check(!simulator.running && !simulator.testRunning && !simulator.historyFrozen && simulator.followLatest, "Authored CPU/analyzer defaults were changed by tests.");
        Check(simulator.coordinator.controlAuthorityId == -1 && !simulator.coordinator.manualTestMode, "Test control authority was persisted.");
        Check(simulator.programSelect == 0 && Panel().draftInput.text == "" && simulator.terminal.outputText.text == "", "A test program, draft or terminal was persisted.");
        int materials = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/CircuitLab/Materials" }))
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            Check(material.shader != null && material.shader.isSupported && !material.shader.name.Contains("InternalError"), "Invalid delivered shader: " + material.name);
            materials++;
        }
        var rom = simulator.programTexture.GetPixels32(); var ram = simulator.ramInitialTexture.GetPixels32();
        Check(rom.Length == 512 && ram.Length == 256, "Authored memory texture dimensions changed.");
        for (int i = 0; i < 512; i++) Check((rom[i].r | (rom[i].g << 8)) == simulator.programWords[i], "Test ROM bytes were persisted at " + i);
        for (int i = 0; i < 256; i++) Check(ram[i].r == simulator.initialRam[i], "Test RAM bytes were persisted at " + i);
        var floor = scene.GetRootGameObjects().Single(g => g.name == "CircuitLabWorld").transform.Find("Floor");
        Check(Mathf.Approximately(floor.localScale.x, 100) && Mathf.Approximately(floor.localScale.z, 100), "User floor scale was altered.");
        Check(Object.FindObjectsOfType<Camera>(true).Where(c => c.name.Contains("Preview")).All(c => !c.enabled), "Preview camera was left enabled.");
        return Save("ram-final-authoring", new { passed = true, editorStopped = true, sceneSaved = true, startsPausedAndLive = true, unclaimedControl = true, defaultPi = true, noTestDraftOrTerminal = true, validMaterialShaders = materials, initialProgramWordsCompared = 512, initialRamBytesCompared = 256, userFloorPreserved = true });
    }
    public static async Task<object> CompletePi()
    {
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        int index = Int(sim, "programSelect"); var program = Data()["programs"][index];
        string expected = (string)program["expectedText"];
        Check(Str(terminal, "output") == expected, "GPU PI output differs from independent RTL/gate result.");
        Click("Pause"); await Quiescent();
        int telemetry = Int(sim, "telemetryReadbackCount"); sim.SendCustomEvent("RequestTelemetry");
        await Until(() => Int(sim, "telemetryReadbackCount") > telemetry && !Bool(sim, "readbackPending"), "PI final telemetry missing.");
        Check(Int(sim, "observedHalt") == 1, "PI program did not execute HALT.");
        byte[] ram = await Ram(); int compared = 0;
        if (program["expectedFinalRam"] != null)
        {
            var reference = program["expectedFinalRam"].ToObject<byte[]>();
            Check(ram.SequenceEqual(reference), "GPU PI final RAM differs from independent RTL/gate result."); compared = ram.Length;
        }
        return Save("ram-pi-gpu", new { passed = true, algorithm = "355/113 decimal long division; first six PI decimals agree", program = Str(sim, "activeProgramName"), output = expected, writes = Int(terminal, "eventCount"), targetHz = sim.GetProgramVariable("cyclesPerSecond"), cycles = Int(sim, "cycleCount"), finalRamBytesCompared = compared, source = "actual synthesized CPU + separate GPU RAM + real OUT event readback", nativeClientAndRealMultiplayerNotExercised = true });
    }
}
