using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.Udon;
using Object = UnityEngine.Object;

// Editor-only integration checks drive the actual backing Udon VM. Nothing here
// provides production simulation values or runs inside the world.
public static class ValidateCircuitLab
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private const string ReportPath = "Reports/CircuitLab";

    public static object IsolatePlayMode()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        var hidden = new List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded || scene.path == ScenePath) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!root.activeSelf) continue;
                hidden.Add(scene.path + "/" + root.name);
                root.SetActive(false);
            }
        }
        // SDK 3.10.5 clears all Udon update registrations on each additive
        // sceneLoaded event. Re-enable through the ordinary Unity lifecycle;
        // do not call private VM methods or replace production Update.
        bool reRegistered = false;
        if (!VM().HasDoneStart)
        {
            foreach (var root in SceneManager.GetSceneByPath(ScenePath).GetRootGameObjects())
            {
                if (!root.activeSelf) continue;
                root.SetActive(false);
                root.SetActive(true);
            }
            reRegistered = true;
        }
        var camera = Object.FindObjectsOfType<Camera>(true).FirstOrDefault(c => c.gameObject.scene.path == ScenePath && c.name.Contains("Preview"));
        if (camera != null) camera.enabled = true;
        return new { isolated = true, temporarilyHidden = hidden, reRegisteredAfterAdditiveLoad = reRegistered, preview = camera != null };
    }

    private static CircuitLabSimulator Proxy()
    {
        var proxy = Object.FindObjectsOfType<CircuitLabSimulator>(true).FirstOrDefault(p => p.gameObject.scene.path == ScenePath);
        if (proxy == null) throw new InvalidOperationException("CircuitLab simulator is missing.");
        return proxy;
    }

    private static UdonBehaviour VM() => UdonSharpEditorUtility.GetBackingUdonBehaviour(Proxy());
    private static int Int(UdonBehaviour vm, string name) => Convert.ToInt32(vm.GetProgramVariable(name));
    private static bool Bool(UdonBehaviour vm, string name) => Convert.ToBoolean(vm.GetProgramVariable(name));

    private static void Event(string name)
    {
        var button = Object.FindObjectsOfType<CircuitLabButton>(true).FirstOrDefault(p => p.gameObject.scene.path == ScenePath && p.eventName == name && p.gameObject.activeInHierarchy);
        if (button != null) UdonSharpEditorUtility.GetBackingUdonBehaviour(button).Interact();
        else VM().SendCustomEvent(name);
    }

    private static async Task Until(Func<bool> predicate, string message, int seconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!predicate()) throw new InvalidOperationException(message);
    }

    private static object Save(string name, object result)
    {
        Directory.CreateDirectory(ReportPath);
        File.WriteAllText(ReportPath + "/" + name + ".json", JsonConvert.SerializeObject(result, Formatting.Indented));
        return result;
    }

    private static async Task<Color[]> ReadFloatTexture(RenderTexture texture)
    {
        if (texture == null) throw new InvalidOperationException("History texture is missing.");
        // Completed requests are disposed on the next frame. Copy in the
        // completion callback instead of polling past the data's lifetime.
        var completion = new TaskCompletionSource<Color[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) completion.TrySetException(new InvalidOperationException("Editor history GPU readback failed."));
            else completion.TrySetResult(request.GetData<Color>().ToArray());
        });
        if (await Task.WhenAny(completion.Task, Task.Delay(8000)) != completion.Task)
            throw new InvalidOperationException("Editor history GPU readback timed out.");
        return await completion.Task;
    }

    private static async Task<Color32[]> ReadState()
    {
        var texture = (RenderTexture)VM().GetProgramVariable("currentState");
        var completion = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) completion.TrySetException(new InvalidOperationException("State readback failed."));
            else completion.TrySetResult(request.GetData<Color32>().ToArray());
        });
        if (await Task.WhenAny(completion.Task, Task.Delay(8000)) != completion.Task) throw new InvalidOperationException("State readback timeout.");
        return await completion.Task;
    }

    private static async Task Telemetry()
    {
        var vm = VM();
        int target = Int(vm, "cycleCount");
        int previous = Int(vm, "telemetryReadbackCount");
        Event("RequestTelemetry");
        await Until(() => Int(vm, "telemetryReadbackCount") > previous && Int(vm, "lastReadbackCycle") >= target, "Latest telemetry did not arrive.");
    }

    public static object ShaderStatus()
    {
        var names = new[] { "CircuitLabSim", "CircuitLabHistory", "CircuitLabWaveform", "CircuitLabDisplay" };
        var diagnostics = names.Select(name =>
        {
            var shader = Shader.Find("VrcCpuWorld/" + name);
            return new { name, supported = shader != null && shader.isSupported, diagnostics = shader == null ? null : ShaderUtil.GetShaderMessages(shader).Select(m => new { m.message, severity = m.severity.ToString(), m.line }).ToArray() };
        }).ToArray();
        if (diagnostics.Any(d => !d.supported || d.diagnostics.Any(m => m.severity == "Error")))
            throw new InvalidOperationException("CircuitLab shader support or compilation failed.");
        return Save("shaders", new { passed = true, graphicsDevice = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(), diagnostics });
    }

    public static async Task<object> Controls()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        var vm = VM();
        if (Bool(vm, "running") || Bool(vm, "testRunning")) throw new InvalidOperationException("Default state must be paused.");
        int initial = Int(vm, "cycleCount");
        await Task.Delay(300);
        if (Int(vm, "cycleCount") != initial) throw new InvalidOperationException("Paused simulation advanced.");
        Event("Step");
        await Until(() => Int(vm, "cycleCount") == initial + 1, "Step did not advance exactly one cycle.");
        await Task.Delay(200);
        if (Int(vm, "cycleCount") != initial + 1) throw new InvalidOperationException("Step advanced more than once.");
        Event("Run");
        await Until(() => Int(vm, "cycleCount") >= initial + 3, "Run did not continuously advance.");
        Event("Pause");
        await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        int paused = Int(vm, "cycleCount");
        await Task.Delay(350);
        if (Int(vm, "cycleCount") != paused) throw new InvalidOperationException("Cycles advanced after pause.");
        int telemetry = Int(vm, "telemetryReadbackCount");
        Event("RequestTelemetry");
        await Until(() => Int(vm, "telemetryReadbackCount") > telemetry, "Telemetry readback did not complete.");
        return Save("controls", new { passed = true, initialCycle = initial, pausedCycle = paused, sampleCount = Int(vm, "sampleCount"), observedAcc = Int(vm, "observedAcc"), observedPc = Int(vm, "observedPc"), eventPath = "UdonBehaviour.Interact -> button Udon -> simulator Udon -> GPU" });
    }

    public static object StartSelfTest()
    {
        var vm = VM();
        int previousRun = Int(vm, "selfTestRunId");
        Event("RunSelfTest");
        return new { submitted = true, previousRun };
    }

    public static object SimulationStatus()
    {
        var vm = VM();
        return new { status = Int(vm, "statusCode"), running = Bool(vm, "running"), testRunning = Bool(vm, "testRunning"),
            completedVectors = Int(vm, "completedVectors"), mismatchCount = Int(vm, "mismatchCount"),
            cycle = Int(vm, "cycleCount"), samples = Int(vm, "sampleCount"), runId = Int(vm, "selfTestRunId"),
            readbackPending = Bool(vm, "readbackPending"), queuedCommands = Int(vm, "queuedCommandCount"),
            observedAcc = Int(vm, "observedAcc"), observedPc = Int(vm, "observedPc"), observedOut = Int(vm, "observedOut") };
    }

    public static async Task<object> WaitForInitialization()
    {
        var vm = VM();
        await Until(() => vm.HasDoneStart && Bool(vm, "_initialized"), "ClientSim did not start the CircuitLab Udon program.", 35);
        if (Int(vm, "statusCode") == 5) throw new InvalidOperationException("CircuitLab GPU configuration failed.");
        await Until(() => !Bool(vm, "readbackPending"), "Initial GPU telemetry did not complete.");
        return new { ready = true, paused = !Bool(vm, "running"), cycle = Int(vm, "cycleCount"), observedZero = Int(vm, "observedZero") };
    }

    public static async Task<object> CompletedSelfTestAndWaveform()
    {
        var proxy = Proxy();
        var vm = VM();
        if (Bool(vm, "testRunning") || Int(vm, "statusCode") != 3 || Int(vm, "mismatchCount") != 0
            || Int(vm, "completedVectors") != proxy.vectorCount)
            throw new InvalidOperationException("The complete GPU self-test must pass first.");
        var gpu = Save("gpu", new { passed = true, vectors = Int(vm, "completedVectors"), nodes = proxy.nodeCount,
            comparedBits = Int(vm, "lastSelfTestComparedBits"), mismatches = Int(vm, "mismatchCount"),
            milliseconds = vm.GetProgramVariable("elapsedMilliseconds"), runId = Int(vm, "selfTestRunId"),
            graphicsDevice = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString() });
        var data = JObject.Parse(File.ReadAllText("Assets/CircuitLab/Data/gpu-netlist.json"));
        var reference = JObject.Parse(File.ReadAllText("Assets/CircuitLab/Data/waveform-reference.json"));
        var samples = (JArray)reference["samples"];
        int count = Int(vm, "sampleCount");
        int capacity = proxy.historyCapacity;
        if (count != samples.Count) throw new InvalidOperationException("GPU history does not contain two samples per fixture cycle.");
        var widths = (int[])vm.GetProgramVariable("probeWidths");
        var segments = (int[])vm.GetProgramVariable("probeSegments");
        var ids = (int[])vm.GetProgramVariable("_probeIds");
        var names = new string[4];
        for (int channel = 0; channel < 4; channel++)
        {
            var bits = ids.Skip(channel * 16).Take(widths[channel]).ToArray();
            var match = ((JObject)data["ports"]).Properties().FirstOrDefault(p => ((JArray)p.Value).Values<int>().SequenceEqual(bits));
            if (match == null) throw new InvalidOperationException("Default probe channel does not map to a top port.");
            names[channel] = match.Name;
        }
        var history = (RenderTexture)vm.GetProgramVariable("currentHistory");
        var pixels = await ReadFloatTexture(history);
        int checkedValues = 0;
        // GPU readback order is interpreted in the texture's row coordinates.
        for (int sample = Math.Max(0, count - capacity); sample < count; sample++)
        {
            if (!(bool)samples[sample]["valid"]) continue;
            for (int channel = 0; channel < 4; channel++)
            {
                Color record = pixels[channel * capacity + sample % capacity];
                int expected = (int)samples[sample][names[channel]];
                if (record.g < .5f || Math.Abs(record.r - expected) > .1f || Math.Abs(record.b - segments[channel]) > .1f
                    || Math.Abs(record.a - (sample + 1)) > .1f)
                    throw new InvalidOperationException("GPU waveform differs from RTL: sample=" + sample + " port=" + names[channel]
                        + " expected=" + expected + " record=" + record);
                checkedValues++;
            }
        }
        var wave = Save("waveform", new { passed = true, source = "independent RTL/gate low/high trace", samples = count,
            comparedValues = checkedValues, channels = names, capacity, invalidInitialLowSkipped = true });
        return new { passed = true, gpu, waveform = wave };
    }

    public static async Task<object> FreezeAndBrowse()
    {
        var proxy = Proxy();
        var vm = VM();
        Event("Pause");
        await Until(() => !Bool(vm, "running"), "Pause did not apply before freeze.");
        Event("FreezeWaveform");
        await Until(() => Bool(vm, "historyFrozen"), "Waveform did not freeze.");
        int frozenCount = Int(vm, "frozenSampleCount");
        var before = await ReadFloatTexture(proxy.frozenHistory);
        int cycles = Int(vm, "cycleCount");
        Event("Run");
        await Until(() => Int(vm, "cycleCount") >= cycles + 3, "Simulation did not continue behind frozen view.");
        Event("Pause");
        await Until(() => !Bool(vm, "running"), "Pause did not apply after background capture.");
        var after = await ReadFloatTexture(proxy.frozenHistory);
        if (before.Length != after.Length || before.Where((pixel, index) => pixel != after[index]).Any())
            throw new InvalidOperationException("Frozen waveform texture changed while recording.");
        if (Int(vm, "frozenSampleCount") != frozenCount || Int(vm, "sampleCount") <= frozenCount)
            throw new InvalidOperationException("Frozen timestamp or ongoing recording is incorrect.");
        int frozenWindow = Int(vm, "waveWindowSamples");
        Event("ZoomIn");
        await Until(() => Int(vm, "waveWindowSamples") < frozenWindow, "Waveform zoom did not change.");
        int cursor = Int(vm, "cursorA");
        Event("MoveCursorARight");
        await Task.Delay(100);
        if (Int(vm, "cursorA") == cursor) throw new InvalidOperationException("Time cursor did not move.");
        Event("PanLeft");
        await Until(() => !Bool(vm, "followLatest"), "Historical browsing did not start.");
        Event("ResumeWaveform");
        await Until(() => !Bool(vm, "historyFrozen") && Bool(vm, "followLatest"), "Waveform did not return to live capture.");
        return Save("freeze-browse", new { passed = true, frozenCount, liveCount = Int(vm, "sampleCount"),
            frozenTextureUnchanged = true, continuedCycles = Int(vm, "cycleCount") - cycles,
            zoomWindow = Int(vm, "waveWindowSamples"), cursorA = Int(vm, "cursorA"), resumedLatest = true });
    }

    public static async Task<object> ProgramsAndBurst()
    {
        var vm = VM();
        Event("Pause");
        await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        int before = Int(vm, "cycleCount");
        for (int i = 0; i < 16; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == before + 16 && Int(vm, "queuedStepCount") == 0, "Sixteen queued steps were lost or duplicated.");
        await Task.Delay(150);
        if (Int(vm, "cycleCount") != before + 16) throw new InvalidOperationException("Extra burst step executed.");
        if (Int(vm, "programSelect") != 1) Event("SelectProgram");
        else Event("RestartExperiment");
        await Until(() => Int(vm, "cycleCount") == 0 && !Bool(vm, "running"), "Program restart did not pause and reset cycle count.");
        await Telemetry();
        if (Int(vm, "observedPc") != 0 || Int(vm, "observedAcc") != 0 || Int(vm, "observedZero") != 1 || Int(vm, "observedHalt") != 0)
            throw new InvalidOperationException("Program initialization did not establish synchronous reset values.");
        for (int i = 0; i < 30; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == 30 && Int(vm, "queuedStepCount") == 0, "Logic program did not finish thirty clocks.");
        await Telemetry();
        if (Int(vm, "observedAcc") != 170 || Int(vm, "observedOut") != 170 || Int(vm, "observedPc") != 15 || Int(vm, "observedHalt") != 1)
            throw new InvalidOperationException("Logic, conditional jumps and HALT did not produce ACC=OUT=AA PC=15 HALT=1.");
        int haltedAcc = Int(vm, "observedAcc"), haltedOut = Int(vm, "observedOut");
        int haltedSamples = Int(vm, "sampleCount");
        Event("ResetCircuit");
        await Until(() => Int(vm, "cycleCount") == 31, "Synchronous reset did not execute one clock.");
        await Telemetry();
        if (Int(vm, "observedPc") != 0 || Int(vm, "observedAcc") != 0 || Int(vm, "observedOut") != 0 || Int(vm, "observedZero") != 1
            || Int(vm, "observedHalt") != 0 || Int(vm, "sampleCount") != haltedSamples + 2)
            throw new InvalidOperationException("Circuit reset state or retained trace is incorrect.");
        Event("SelectProgram");
        await Until(() => Int(vm, "programSelect") == 0 && Int(vm, "cycleCount") == 0 && Int(vm, "sampleCount") == 0, "Program selection did not restart the experiment.");
        for (int i = 0; i < 32; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == 32 && Int(vm, "queuedStepCount") == 0, "Counting program did not advance.");
        await Telemetry();
        if (Int(vm, "observedAcc") < 4 || Int(vm, "observedOut") < 4 || Int(vm, "observedHalt") != 0)
            throw new InvalidOperationException("Counting program did not continue producing output.");
        float rate = Convert.ToSingle(vm.GetProgramVariable("cyclesPerSecond"));
        Event("SpeedUp"); await Task.Delay(100);
        if (Convert.ToSingle(vm.GetProgramVariable("cyclesPerSecond")) <= rate) throw new InvalidOperationException("SpeedUp did not apply.");
        Event("SpeedDown"); await Task.Delay(100);
        if (Math.Abs(Convert.ToSingle(vm.GetProgramVariable("cyclesPerSecond")) - rate) > .01f) throw new InvalidOperationException("SpeedDown did not restore rate.");
        return Save("programs-burst", new { passed = true, queuedSteps = 16, haltedAcc, haltedOut, haltPc = 15,
            resetRetainedHistory = true, countingAcc = Int(vm, "observedAcc"), countingOut = Int(vm, "observedOut"), cyclesPerSecond = rate });
    }

    public static async Task<object> DeepProbeAndWires()
    {
        var vm = VM();
        Event("Pause"); await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        var modules = Object.FindObjectsOfType<CircuitLabModule>(true).Where(m => m.gameObject.scene.path == ScenePath).ToArray();
        var alu = modules.Single(m => m.modulePath == "teaching_cpu/alu");
        var adder = modules.Single(m => m.modulePath == "teaching_cpu/alu/adder");
        var leaf = modules.Single(m => m.modulePath == "teaching_cpu/alu/adder/fa0");
        var control = modules.Single(m => m.modulePath == "teaching_cpu/control");
        foreach (var module in new[] { alu, adder, leaf, control })
        {
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(module);
            if (Bool(backing, "expanded")) continue;
            var hierarchy = Object.FindObjectsOfType<CircuitLabButton>().Single(b => b.gameObject.scene.path == ScenePath && b.name == "HIERARCHY_" + module.moduleId);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(hierarchy).Interact();
        }
        await Until(() => leaf.gameObject.activeInHierarchy, "Recursive expansion did not expose a full adder.");
        await Task.Delay(150);
        var target = leaf.GetComponentsInChildren<CircuitLabSignalTarget>(true).First(t => !t.isPort && t.name.EndsWith("_Q"));
        var probe = Object.FindObjectsOfType<CircuitLabProbe>().Single(p => p.gameObject.scene.path == ScenePath && p.channel == 0);
        var probeVM = UdonSharpEditorUtility.GetBackingUdonBehaviour(probe);
        probeVM.SendCustomEvent("SelectProbe");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(target).Interact();
        await Until(() => Int(vm, "queuedCommandCount") == 0 && ((int[])vm.GetProgramVariable("probeWidths"))[0] == 1, "Internal signal probe did not bind.");
        var before = await ReadState();
        var leafVM = UdonSharpEditorUtility.GetBackingUdonBehaviour(leaf);
        leafVM.SendCustomEvent("DetachFromParent");
        if (Vector3.Distance(leaf.transform.lossyScale, Vector3.one) > .001f) throw new InvalidOperationException("Extracting a summary module did not restore full scale.");
        leaf.transform.position = new Vector3(-3.4f, 1.8f, -.6f);
        leaf.transform.rotation = Quaternion.Euler(0f, 28f, 12f);
        UdonSharpEditorUtility.GetBackingUdonBehaviour(control).SendCustomEvent("DetachFromParent");
        control.transform.position = new Vector3(3.8f, 2.1f, 1.5f);
        control.transform.rotation = Quaternion.Euler(0, -20, 0);
        await Task.Delay(150);
        var moved = await ReadState();
        if (!before.SequenceEqual(moved)) throw new InvalidOperationException("Moving modules changed GPU circuit state.");
        var wires = Object.FindObjectsOfType<CircuitLabWire>(true).Where(w => w.line.enabled &&
            (w.source.IsChildOf(leaf.transform) || w.destination.IsChildOf(leaf.transform))).ToArray();
        if (wires.Length == 0) throw new InvalidOperationException("Extracted leaf has no boundary wires.");
        foreach (var wire in wires)
        {
            if (Vector3.Distance(wire.line.GetPosition(0), wire.source.position) > .001f || Vector3.Distance(wire.line.GetPosition(5), wire.destination.position) > .001f)
                throw new InvalidOperationException("Boundary wire did not follow extracted endpoints.");
            for (int point = 1; point < wire.line.positionCount; point++)
            {
                var difference = wire.line.GetPosition(point) - wire.line.GetPosition(point - 1);
                int axes = (Math.Abs(difference.x) > .001f ? 1 : 0) + (Math.Abs(difference.y) > .001f ? 1 : 0) + (Math.Abs(difference.z) > .001f ? 1 : 0);
                if (axes > 1) throw new InvalidOperationException("Boundary routing contains a non-orthogonal segment.");
            }
        }
        var position = leaf.transform.position;
        leafVM.SendCustomEvent("Collapse"); await Task.Delay(100);
        if (target.gameObject.activeInHierarchy || probeVM.GetProgramVariable("boundTarget") == null)
            throw new InvalidOperationException("Folding an internal probe target did not hide geometry and retain binding.");
        int cycle = Int(vm, "cycleCount");
        Event("Step"); await Until(() => Int(vm, "cycleCount") == cycle + 1, "Hidden probe cycle did not run.");
        var history = await ReadFloatTexture((RenderTexture)vm.GetProgramVariable("currentHistory"));
        var state = await ReadState();
        int last = Int(vm, "sampleCount") - 1;
        Color record = history[last % Proxy().historyCapacity];
        int bit = state[target.signalIds[0]].r >= 128 ? 1 : 0;
        if (record.g < .5f || Math.Abs(record.r - bit) > .1f || Math.Abs(record.a - (last + 1)) > .1f)
            throw new InvalidOperationException("Hidden internal signal did not continue recording real GPU data.");
        leafVM.SendCustomEvent("Expand"); await Task.Delay(100);
        if (!target.gameObject.activeInHierarchy || Vector3.Distance(position, leaf.transform.position) > .001f)
            throw new InvalidOperationException("Re-expansion did not preserve extracted pose.");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(adder).SendCustomEvent("RetrieveChildren"); await Task.Delay(100);
        if (leaf.transform.parent != adder.childrenDock || Bool(leafVM, "isDetached")
            || Vector3.Distance(leaf.transform.localScale, leaf.dockLocalScale) > .001f) throw new InvalidOperationException("Retrieval did not restore the logical dock and summary scale.");
        return Save("deep-probe-wires", new { passed = true, modulesSimultaneouslyExpanded = 4, extractedModule = leaf.modulePath,
            target = target.signalLabel, movingPreservedAllStateBits = true, followingBoundaryWires = wires.Length,
            hiddenTargetStillRecorded = true, poseRestoredAfterExpansion = true, retrieved = true });
    }

    public static async Task<object> ProbeLifecycleAndFormats()
    {
        var vm = VM();
        Event("Pause"); await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        var probe = Object.FindObjectsOfType<CircuitLabProbe>().Single(p => p.gameObject.scene.path == ScenePath && p.channel == 3);
        var target = Object.FindObjectsOfType<CircuitLabSignalTarget>(true).Single(t => t.gameObject.scene.path == ScenePath && t.isPort && t.ownerModule.logicalParent == null && t.signalLabel == "teaching_cpu/clk");
        var probeVM = UdonSharpEditorUtility.GetBackingUdonBehaviour(probe);
        probeVM.OnPickup();
        probe.transform.rotation = target.transform.rotation;
        probe.transform.position = target.transform.position - probe.transform.TransformVector(probe.tip.localPosition);
        await Until(() => probeVM.GetProgramVariable("candidate") != null, "Held probe did not preselect a nearby pin.");
        probeVM.OnPickupUseDown();
        await Until(() => probeVM.GetProgramVariable("boundTarget") != null && !Bool(probeVM, "isHeld")
            && Int(vm, "queuedCommandCount") == 0 && ((int[])vm.GetProgramVariable("probeWidths"))[3] == 1, "Pickup Use did not lock and bind the clock probe.");
        int bindingStart = ((int[])vm.GetProgramVariable("probeBindSamples"))[3];
        int cycle = Int(vm, "cycleCount");
        for (int i = 0; i < 4; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == cycle + 4 && Int(vm, "queuedStepCount") == 0, "Clock probe capture did not advance.");
        var pixels = await ReadFloatTexture((RenderTexture)vm.GetProgramVariable("currentHistory"));
        int capacity = Proxy().historyCapacity;
        int count = Int(vm, "sampleCount");
        for (int index = bindingStart; index < count; index++)
        {
            var record = pixels[3 * capacity + index % capacity];
            if (record.g < .5f || Math.Abs(record.r - index % 2) > .1f || Math.Abs(record.a - index - 1) > .1f)
                throw new InvalidOperationException("Clock waveform did not record both real low/high phases.");
        }
        var formats = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            int previous = Int(vm, "numberFormat");
            Event("CycleNumberFormat");
            await Until(() => Int(vm, "numberFormat") == (previous + 1) % 3, "Numeric format did not change.");
            formats.Add(Int(vm, "numberFormat"));
        }
        Event("ShowChannels"); await Until(() => Int(vm, "bitViewChannel") == -1, "Channel overview did not apply.");
        var views = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            int expected = i < 4 ? i : -1;
            Event("CycleBitView");
            await Until(() => Int(vm, "bitViewChannel") == expected, "Bus/bit view did not change.");
            views.Add(Int(vm, "bitViewChannel"));
        }
        probeVM.SendCustomEvent("Detach");
        await Until(() => ((int[])vm.GetProgramVariable("probeWidths"))[3] == 0, "Explicit detach did not stop future channel acquisition.");
        int detached = Int(vm, "sampleCount");
        Event("Step"); await Until(() => Int(vm, "sampleCount") == detached + 2, "Unbound capture did not advance.");
        var unbound = await ReadFloatTexture((RenderTexture)vm.GetProgramVariable("currentHistory"));
        if (unbound[3 * capacity + detached % capacity].g > .5f || unbound[3 * capacity + (detached + 1) % capacity].g > .5f)
            throw new InvalidOperationException("Unbound channel still generated valid samples.");
        probeVM.OnPickup();
        probe.transform.rotation = target.transform.rotation;
        probe.transform.position = target.transform.position - probe.transform.TransformVector(probe.tip.localPosition);
        await Until(() => probeVM.GetProgramVariable("candidate") != null, "Dropped probe lifecycle did not acquire a candidate.");
        probeVM.OnDrop();
        if (probeVM.GetProgramVariable("candidate") != null || Bool(probeVM, "isHeld") || !probe.panelLabel.text.Contains("UNBOUND"))
            throw new InvalidOperationException("Drop did not clear candidate, hold state and stale READY label.");
        return Save("probe-lifecycle-formats", new { passed = true, path = "OnPickup -> nearby candidate -> OnPickupUseDown -> GPU gather",
            recordedClockSamples = count - bindingStart, confirmedLowHigh = true, numericFormats = formats, channelBitViews = views,
            explicitDetachStoppedAcquisition = true, dropClearedPreselectionLabel = true });
    }

    public static async Task<object> DemoLayout()
    {
        var vm = VM();
        Event("Pause"); await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        var modules = Object.FindObjectsOfType<CircuitLabModule>(true).Where(m => m.gameObject.scene.path == ScenePath).ToArray();
        var root = modules.Single(m => m.logicalParent == null);
        foreach (var module in modules.Where(m => m != root)) UdonSharpEditorUtility.GetBackingUdonBehaviour(module).SendCustomEvent("Collapse");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(root).SendCustomEvent("Expand");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(root).SendCustomEvent("RetrieveChildren");
        for (int i = 0; i < root.childModules.Length; i++)
        {
            var module = root.childModules[i];
            UdonSharpEditorUtility.GetBackingUdonBehaviour(module).SendCustomEvent("DetachFromParent");
            float side = i % 2 == 0 ? -1 : 1;
            module.transform.position = i < 4 ? new Vector3(side * 4.65f, i < 2 ? 2.7f : 1.05f, 2.3f) : new Vector3(side * 7.85f, 1.35f, 2.8f);
            module.transform.rotation = Quaternion.Euler(0, side * -12, 0);
        }
        var ports = new[] { "pc", "instruction", "acc", "out" };
        for (int channel = 0; channel < 4; channel++)
        {
            var probe = Object.FindObjectsOfType<CircuitLabProbe>().Single(p => p.channel == channel);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(probe).SendCustomEvent("SelectProbe");
            var target = Object.FindObjectsOfType<CircuitLabSignalTarget>(true).Single(t => t.isPort && t.ownerModule.logicalParent == null && t.signalLabel == "teaching_cpu/" + ports[channel]);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(target).Interact();
        }
        if (Int(vm, "programSelect") != 0) Event("SelectProgram"); else Event("RestartExperiment");
        await Until(() => Int(vm, "cycleCount") == 0 && Int(vm, "queuedCommandCount") == 0, "Demo restart did not apply.");
        for (int i = 0; i < 96; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == 96 && Int(vm, "queuedStepCount") == 0, "Demo trace did not complete.");
        Event("ShowChannels"); Event("FollowLatest");
        await Telemetry();
        return new { ready = true, cycles = Int(vm, "cycleCount"), acc = Int(vm, "observedAcc"), output = Int(vm, "observedOut"),
            defaultProbePorts = ports, paused = !Bool(vm, "running") };
    }

    public static object FinalizeLabels()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Finalize the saved label in Edit Mode.");
        var scene = SceneManager.GetSceneByPath(ScenePath);
        var root = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<CircuitLabModule>(true)).Single(m => m.logicalParent == null);
        var title = root.header.GetComponentsInChildren<UnityEngine.UI.Text>(true).Single(t => t.name == "ModuleName");
        title.text = "TEACHING CPU / 19 MODULES";
        EditorUtility.SetDirty(title);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        return new { saved = true, title = title.text };
    }

    public static async Task<object> FocusFullAdder()
    {
        var vm = VM();
        var modules = Object.FindObjectsOfType<CircuitLabModule>(true);
        var leaf = modules.Single(m => m.modulePath == "teaching_cpu/alu/adder/fa0");
        foreach (var path in new[] { "teaching_cpu/alu", "teaching_cpu/alu/adder", "teaching_cpu/alu/adder/fa0" })
            UdonSharpEditorUtility.GetBackingUdonBehaviour(modules.Single(m => m.modulePath == path)).SendCustomEvent("Expand");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(leaf).SendCustomEvent("DetachFromParent");
        leaf.transform.position = new Vector3(-3.1f, 2.0f, -.8f); leaf.transform.rotation = Quaternion.identity;
        await Task.Delay(150);
        var probes = Object.FindObjectsOfType<CircuitLabProbe>();
        var targets = Object.FindObjectsOfType<CircuitLabSignalTarget>(true);
        var bindings = new[] { "sum", "clk", "a", "carry_out" };
        for (int channel = 0; channel < 4; channel++)
        {
            var target = bindings[channel] == "clk" ? targets.Single(t => t.isPort && t.ownerModule.logicalParent == null && t.signalLabel == "teaching_cpu/clk")
                : targets.Single(t => t.isPort && t.ownerModule == leaf && t.signalLabel == leaf.modulePath + "/" + bindings[channel]);
            UdonSharpEditorUtility.GetBackingUdonBehaviour(probes.Single(p => p.channel == channel)).SendCustomEvent("SelectProbe");
            UdonSharpEditorUtility.GetBackingUdonBehaviour(target).Interact();
        }
        int before = Int(vm, "cycleCount");
        for (int i = 0; i < 6; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == before + 6, "Full-adder preview clocks did not complete.");
        Event("FollowLatest"); await Telemetry();
        var camera = Object.FindObjectsOfType<Camera>(true).Single(c => c.name == "CircuitLabPreviewCamera");
        camera.transform.position = leaf.transform.position + new Vector3(0, .15f, -3.9f);
        camera.transform.LookAt(leaf.transform.position);
        camera.fieldOfView = 42;
        return new { ready = true, module = leaf.modulePath, probes = bindings, cycle = Int(vm, "cycleCount") };
    }

    public static object FocusWaveform()
    {
        var panel = GameObject.Find("MovableWaveformPanel").transform;
        var camera = Object.FindObjectsOfType<Camera>(true).Single(c => c.name == "CircuitLabPreviewCamera");
        camera.transform.position = panel.position + new Vector3(0, .05f, -5.7f);
        camera.transform.LookAt(panel.position);
        camera.fieldOfView = 55;
        return new { ready = true, focus = "four real GPU probe channels" };
    }

    public static async Task<object> VerifyAutomaticBootstrap()
    {
        // Intentionally do not call IsolatePlayMode: verify pressing Play alone.
        var vm = VM();
        await Until(() => vm.HasDoneStart && Bool(vm, "_initialized"), "Automatic additive bootstrap did not initialize Udon.", 35);
        await Until(() => !Bool(vm, "readbackPending"), "Initial telemetry did not arrive.");
        var smoke = SceneManager.GetSceneByPath("Assets/GpuNetlistSmoke/Scenes/GpuNetlistSmoke.unity");
        if (smoke.isLoaded && smoke.GetRootGameObjects().Any(g => g.activeSelf)) throw new InvalidOperationException("Old smoke world was not isolated in Play Mode.");
        if (Bool(vm, "running") || Int(vm, "cycleCount") != 0 || Int(vm, "observedZero") != 1)
            throw new InvalidOperationException("Automatic startup did not remain paused in the reset state.");
        Event("Step"); await Until(() => Int(vm, "cycleCount") == 1, "Manual Play startup did not dispatch a step.");
        return Save("automatic-bootstrap", new { passed = true, noManualIsolationCommand = true, actualUdonStarted = true,
            defaultPaused = true, resetZeroFlag = 1, singleStepCycle = 1, editorOnly = true });
    }

    public static async Task<object> RenderingState()
    {
        var vm = VM();
        var state = await ReadState();
        var data = JObject.Parse(File.ReadAllText("Assets/CircuitLab/Data/gpu-netlist.json"));
        var paths = new[] { "teaching_cpu/acc", "teaching_cpu/instruction", "teaching_cpu/alu/adder/fa0/a", "teaching_cpu/alu/adder/fa0/b", "teaching_cpu/alu/adder/fa0/sum", "teaching_cpu/alu/adder/fa0/carry_out" };
        var material = Proxy().displayMaterial;
        return new { cycle = Int(vm, "cycleCount"), observedAcc = Int(vm, "observedAcc"), observedInstruction = Int(vm, "observedInstruction"),
            signals = paths.Select(path => new { path, ids = ((JArray)data["signals"][path]).Values<int>().ToArray(), values = ((JArray)data["signals"][path]).Values<int>().Select(id => state[id].r >= 128 ? 1 : 0).ToArray() }).ToArray(),
            width = material.GetFloat("_TextureWidth"), signalOverride = material.GetFloat("_SignalOverride"), boundState = material.GetTexture("_StateTex").name,
            currentState = ((RenderTexture)vm.GetProgramVariable("currentState")).name,
            uvMin = Object.FindObjectsOfType<CircuitLabModule>(true).Single(m => m.modulePath.EndsWith("/fa0")).interior.GetComponentInChildren<MeshFilter>().sharedMesh.uv.Min(uv => uv.x) };
    }

    public static async Task<object> FrozenRebindingLabels()
    {
        var vm = VM();
        Event("Pause"); await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        int initial = Int(vm, "cycleCount");
        for (int i = 0; i < 32; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == initial + 32, "Pre-freeze capture did not complete.");
        Event("FreezeWaveform"); await Until(() => Bool(vm, "historyFrozen"), "Freeze did not apply.");
        var before = await ReadFloatTexture(Proxy().frozenHistory);
        var probe = Object.FindObjectsOfType<CircuitLabProbe>().Single(p => p.channel == 3);
        UdonSharpEditorUtility.GetBackingUdonBehaviour(probe).SendCustomEvent("SelectProbe");
        var target = Object.FindObjectsOfType<CircuitLabSignalTarget>(true).Single(t => t.isPort && t.ownerModule.logicalParent == null && t.signalLabel == "teaching_cpu/clk");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(target).Interact();
        await Until(() => ((int[])vm.GetProgramVariable("probeWidths"))[3] == 1 && Proxy().waveHudText.text.Contains("teaching_cpu/out"), "Frozen trace lost its original signal caption after rebind.");
        Event("Step"); await Until(() => Int(vm, "cycleCount") == initial + 33, "Background capture did not continue after rebind.");
        var after = await ReadFloatTexture(Proxy().frozenHistory);
        if (!before.SequenceEqual(after) || !probe.panelLabel.text.Contains("teaching_cpu/clk")) throw new InvalidOperationException("Frozen data or live physical probe caption is incorrect.");
        Event("ResumeWaveform"); await Until(() => !Bool(vm, "historyFrozen"), "Resume did not apply.");
        return Save("frozen-rebinding", new { passed = true, frozenSignal = "teaching_cpu/out", physicalProbeSignal = "teaching_cpu/clk", snapshotUnchanged = true, backgroundCaptureContinued = true });
    }

    public static async Task<object> MaterialReload()
    {
        var vm = VM();
        Event("Pause"); await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        if (Int(vm, "programSelect") != 0) Event("SelectProgram"); else Event("RestartExperiment");
        await Until(() => Int(vm, "cycleCount") == 0 && Int(vm, "queuedCommandCount") == 0, "Reload regression restart did not apply.");
        for (int i = 0; i < 9; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == 9, "Odd-cycle setup did not complete.");
        await Telemetry();
        var before = await ReadState();
        var material = (Material)vm.GetProgramVariable("simulationMaterial");
        var historyMaterial = (Material)vm.GetProgramVariable("historyMaterial");
        AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(material), ImportAssetOptions.ForceUpdate);
        AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(historyMaterial), ImportAssetOptions.ForceUpdate);
        foreach (var display in Proxy().displayMaterials)
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(display), ImportAssetOptions.ForceUpdate);
        await Task.Delay(150);
        var after = await ReadState();
        if (!before.SequenceEqual(after) || Int(vm, "cycleCount") != 9) throw new InvalidOperationException("Material import changed paused GPU state or simulation time.");
        if (material.GetInt("_NodeCount") != Proxy().nodeCount || historyMaterial.GetInt("_NodeCount") != Proxy().nodeCount
            || material.GetInt("_TextureWidth") != Proxy().textureWidth) throw new InvalidOperationException("Compute uniforms were not restored after material import.");
        var current = (RenderTexture)vm.GetProgramVariable("currentState");
        foreach (var display in Proxy().displayMaterials)
            if (display.GetTexture("_StateTex") != current || Math.Abs(display.GetFloat("_TextureWidth") - Proxy().textureWidth) > .1f)
                throw new InvalidOperationException("Paused display binding was not restored after material import.");
        Event("Step"); await Until(() => Int(vm, "cycleCount") == 10, "Post-import clock did not advance.");
        await Telemetry();
        if (Int(vm, "observedAcc") != 1 || Int(vm, "observedOut") != 1 || Int(vm, "observedPc") != 2)
            throw new InvalidOperationException("Post-import CPU state was corrupted.");
        return Save("material-reload", new { passed = true, pausedCyclePreserved = 9, allStateBitsPreserved = true,
            displayMaterialsRestored = Proxy().displayMaterials.Length, postImportCycle = 10,
            acc = Int(vm, "observedAcc"), output = Int(vm, "observedOut"), pc = Int(vm, "observedPc") });
    }

    public static async Task<object> RingWrap()
    {
        var vm = VM();
        Event("Pause"); await Until(() => !Bool(vm, "running"), "Pause did not apply.");
        if (Int(vm, "programSelect") != 0) Event("SelectProgram"); else Event("RestartExperiment");
        await Until(() => Int(vm, "cycleCount") == 0 && Int(vm, "queuedCommandCount") == 0, "Ring test restart did not apply.");
        var probe = Object.FindObjectsOfType<CircuitLabProbe>().Single(p => p.channel == 3);
        UdonSharpEditorUtility.GetBackingUdonBehaviour(probe).SendCustomEvent("SelectProbe");
        var clock = Object.FindObjectsOfType<CircuitLabSignalTarget>(true).Single(t => t.isPort && t.ownerModule.logicalParent == null && t.signalLabel == "teaching_cpu/clk");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(clock).Interact();
        for (int i = 0; i < 32; i++) Event("Step");
        await Until(() => Int(vm, "cycleCount") == 32, "Initial ring capture did not finish.");
        Event("FreezeWaveform"); await Until(() => Bool(vm, "historyFrozen"), "Ring test freeze did not apply.");
        var frozen = await ReadFloatTexture(Proxy().frozenHistory);
        int requiredCycles = Proxy().historyCapacity / 2 + 32;
        while (Int(vm, "cycleCount") < requiredCycles)
        {
            int current = Int(vm, "cycleCount");
            int batch = Math.Min(64, requiredCycles - current);
            for (int i = 0; i < batch; i++) Event("Step");
            await Until(() => Int(vm, "cycleCount") == current + batch && Int(vm, "queuedStepCount") == 0, "Ring capture batch did not complete.");
        }
        int count = Int(vm, "sampleCount"), capacity = Proxy().historyCapacity;
        var history = await ReadFloatTexture((RenderTexture)vm.GetProgramVariable("currentHistory"));
        for (int sample = count - capacity; sample < count; sample++)
        {
            Color record = history[3 * capacity + sample % capacity];
            if (record.g < .5f || Math.Abs(record.r - sample % 2) > .1f || Math.Abs(record.a - sample - 1) > .1f)
                throw new InvalidOperationException("Ring wrap lost or misordered sample " + sample);
        }
        var frozenAfter = await ReadFloatTexture(Proxy().frozenHistory);
        if (!frozen.SequenceEqual(frozenAfter) || Int(vm, "frozenSampleCount") != 64)
            throw new InvalidOperationException("Frozen snapshot did not survive live ring wrap.");
        Event("ResumeWaveform"); await Until(() => !Bool(vm, "historyFrozen"), "Resume did not apply after wrap.");
        return Save("ring-wrap", new { passed = true, capacity, totalSamples = count, retainedSamplesCompared = capacity,
            oldestRetainedSample = count - capacity, frozenSnapshotSurvivedOverwrite = true });
    }
}
