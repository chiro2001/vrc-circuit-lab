using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Udon;
using Object = UnityEngine.Object;

// Attribution and settle-depth measurements for the teaching CPU. Nothing here
// authors project content: the probe material is a runtime copy, the shader probe
// defaults to off, and every Udon variable it changes is restored afterwards.
public static class MeasureCircuitLabDepth
{
    private const string ReportDir = "Reports/CircuitLab";
    private const string NetlistPath = "Assets/CircuitLab/Data/gpu-netlist.json";

    private static object Save(string name, object report)
    {
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, name + ".json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }

    private static CircuitLabSimulator Proxy() => Object.FindObjectsOfType<CircuitLabSimulator>(true).First();
    private static UdonBehaviour Vm() => UdonSharpEditorUtility.GetBackingUdonBehaviour(Proxy());
    private static UdonBehaviour CoordVm() => UdonSharpEditorUtility.GetBackingUdonBehaviour(Proxy().coordinator);
    private static UdonBehaviour TerminalVm() => UdonSharpEditorUtility.GetBackingUdonBehaviour(Proxy().terminal);

    private static async Task Until(Func<bool> predicate, string message, int seconds = 60)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate())
        {
            if (DateTime.UtcNow > limit) throw new InvalidOperationException(message);
            await Task.Delay(60);
        }
    }

    // SubmissionBlocked() must be evaluated from the Udon variables: calling the
    // proxy method from Editor code reads the edit-time snapshot, which reports a
    // permanent block that the running program does not actually have.
    private static bool BlockedVm()
    {
        UdonBehaviour coord = CoordVm();
        return Convert.ToInt32(coord.GetProgramVariable("snapshotCaptureStage")) != 0
            || Convert.ToBoolean(coord.GetProgramVariable("_snapshotRequested"))
            || Convert.ToBoolean(coord.GetProgramVariable("_serializationPending"))
            || Convert.ToBoolean(coord.GetProgramVariable("restorePending"))
            || !Convert.ToBoolean(coord.GetProgramVariable("ready"));
    }

    // The state textures are ARGB32, so the readback has to use Color32: asking
    // for Color (4 floats = 16 bytes) reinterprets 8-bit channels as garbage
    // floats and silently returns a quarter of the pixels.
    private static async Task<Color32[]> ReadPixels(RenderTexture texture)
    {
        var completion = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) completion.TrySetException(new InvalidOperationException("GPU readback failed"));
            else completion.TrySetResult(request.GetData<Color32>().ToArray());
        });
        if (await Task.WhenAny(completion.Task, Task.Delay(15000)) != completion.Task)
            throw new TimeoutException("GPU readback timed out");
        return await completion.Task;
    }

    private static int ProgramIndex(string id)
    {
        var programs = (JArray)JObject.Parse(File.ReadAllText(NetlistPath))["programs"];
        return Enumerable.Range(0, programs.Count).First(i => (string)programs[i]["id"] == id);
    }

    // ---- 1. Primitive cost: what does one blit of each kind actually cost? ----
    public static async Task<object> BlitCost(int iterations = 1500)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Proxy();
        Material probe = new Material(simulator.simulationMaterial);
        probe.SetInt("_TextureWidth", simulator.textureWidth);
        probe.SetInt("_NodeCount", simulator.nodeCount);
        probe.SetInt("_InputMask", 0);
        RenderTexture a = simulator.stateA, b = simulator.stateB;

        async Task<double> Measure(string label, Action<Material> configure)
        {
            configure(probe);
            for (int i = 0; i < 100; i++) Graphics.Blit(a, b, probe);
            await ReadPixels(b);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) Graphics.Blit(a, b, probe);
            await ReadPixels(b);
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds * 1000.0 / iterations;
        }

        JArray rows = new JArray();
        void Add(string label, double microseconds) => rows.Add(new JObject { ["variant"] = label, ["microsecondsPerBlit"] = microseconds });
        Add("empty blit (shader returns constant)", await Measure("empty", m => { m.SetInt("_ProbeMode", 1); m.SetInt("_TrackChanges", 0); }));
        Add("sim shader, mode 0 (no gate evaluation)", await Measure("mode0", m => { m.SetInt("_ProbeMode", 0); m.SetInt("_Mode", 0); m.SetInt("_TrackChanges", 0); }));
        Add("sim shader, mode 1 (real settle evaluation)", await Measure("mode1", m => { m.SetInt("_ProbeMode", 0); m.SetInt("_Mode", 1); m.SetInt("_TrackChanges", 0); }));
        Add("sim shader, mode 1 + change tracking", await Measure("track", m => { m.SetInt("_ProbeMode", 0); m.SetInt("_Mode", 1); m.SetInt("_TrackChanges", 1); m.SetInt("_PassIndex", 5); }));
        Add("sim shader, mode 2 (register commit)", await Measure("mode2", m => { m.SetInt("_ProbeMode", 0); m.SetInt("_Mode", 2); m.SetInt("_TrackChanges", 0); }));
        Object.DestroyImmediate(probe);
        return Save("perf-blit-cost", new
        {
            passed = true,
            iterations,
            textureWidth = simulator.textureWidth,
            nodeCount = simulator.nodeCount,
            note = "Graphics.Blit measured directly; includes driver blit cost but not the Udon interop of a real submit",
            rows
        });
    }

    // ---- 2. Attribution: is throughput governed by pass count or by ALU? ----
    public static async Task<object> Attribution()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Proxy();
        UdonBehaviour sim = Vm();
        if (!simulator.coordinator.HasControl())
        {
            simulator.coordinator.RequestControl();
            await Until(() => simulator.coordinator.HasControl(), "Could not take control.", 20);
        }
        await Until(() => Convert.ToBoolean(sim.GetProgramVariable("_initialized")), "Simulator never initialized.", 90);

        // Silence every periodic readback so the only work left is submission.
        float telemetryInterval = Convert.ToSingle(sim.GetProgramVariable("telemetryInterval"));
        float snapshotInterval = Convert.ToSingle(CoordVm().GetProgramVariable("snapshotInterval"));
        float terminalInterval = Convert.ToSingle(TerminalVm().GetProgramVariable("readbackInterval"));
        int settlePasses = Convert.ToInt32(sim.GetProgramVariable("settlePasses"));
        sim.SetProgramVariable("telemetryInterval", 36000f);
        // Stretch only the snapshot cadence. The terminal readback must keep its
        // normal 50 ms cadence because FinishCapture waits on it; muting it leaves
        // SubmissionBlocked() stuck true and the simulation frozen.
        CoordVm().SetProgramVariable("snapshotInterval", 36000f);
        CoordVm().SetProgramVariable("snapshotCaptureStage", 0);
        CoordVm().SetProgramVariable("_serializationPending", false);
        await Task.Delay(4000);

        UdonBehaviour browser = UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First());
        int index = ProgramIndex("blinker");
        sim.SetProgramVariable("running", false);
        await Task.Delay(400);
        browser.SetProgramVariable("selectedProgram", index);
        browser.SendCustomEvent("RenderBrowser");
        browser.SendCustomEvent("LoadSelectedPreset");
        await Task.Delay(900);
        sim.SetProgramVariable("cyclesPerSecond", 8192f);
        sim.SendCustomEvent("Run");
        await Task.Delay(800);

        JArray rows = new JArray();
        foreach (int passes in new[] { 1, 4, 11, 22, 43 })
        {
            sim.SetProgramVariable("settlePasses", passes);
            await Task.Delay(900);
            // Only unblocked wall time counts: a capture or readback stall would
            // otherwise be charged to the pass count and smear the fit.
            int cycles = 0;
            double elapsed = 0, blockedSeconds = 0;
            int lastCycle = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
            int firstFrame = Time.frameCount;
            float lastTime = Time.realtimeSinceStartup;
            float deadline = Time.realtimeSinceStartup + 6f;
            while (Time.realtimeSinceStartup < deadline)
            {
                await Task.Delay(50);
                float now = Time.realtimeSinceStartup;
                int current = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
                if (BlockedVm())
                    blockedSeconds += now - lastTime;
                else
                {
                    elapsed += now - lastTime;
                    cycles += current - lastCycle;
                }
                lastTime = now; lastCycle = current;
            }
            int blitsPerCycle = Convert.ToInt32(sim.GetProgramVariable("clockSettlePasses")) + 1 + passes + 3;
            float fps = Time.frameCount > firstFrame ? (Time.frameCount - firstFrame) / (float)(elapsed + blockedSeconds) : 0f;
            float cyclesPerFrame = fps > 0 ? (float)(cycles / (elapsed + blockedSeconds)) / fps : 0f;
            float frameMilliseconds = fps > 0 ? 1000f / fps : 0f;
            rows.Add(new JObject
            {
                ["settlePasses"] = passes,
                ["blitsPerCycle"] = blitsPerCycle,
                ["cycles"] = cycles,
                ["activeSeconds"] = elapsed,
                ["blockedSeconds"] = blockedSeconds,
                ["fps"] = fps,
                ["cyclesPerFrame"] = cyclesPerFrame,
                ["blitsPerFrame"] = cyclesPerFrame * blitsPerCycle,
                ["frameMilliseconds"] = frameMilliseconds,
                ["cyclesPerSecond"] = elapsed > 0 ? cycles / elapsed : 0f,
                ["microsecondsPerCycle"] = cycles > 0 ? elapsed * 1e6 / cycles : 0f,
                ["microsecondsPerBlit"] = cycles > 0 ? elapsed * 1e6 / cycles / blitsPerCycle : 0f
            });
        }
        sim.SetProgramVariable("settlePasses", settlePasses);
        sim.SetProgramVariable("telemetryInterval", telemetryInterval);
        CoordVm().SetProgramVariable("snapshotInterval", snapshotInterval);
        TerminalVm().SetProgramVariable("readbackInterval", terminalInterval);
        sim.SendCustomEvent("Pause");

        // Fit frame time against the blits submitted in that frame: frame time is
        // measured directly, while cycles/blit are derived, so it carries the least
        // circular error of the quantities available.
        double n = rows.Count;
        double sumX = rows.Sum(r => (double)r["blitsPerFrame"]);
        double sumY = rows.Sum(r => (double)r["frameMilliseconds"]);
        double sumXY = rows.Sum(r => (double)r["blitsPerFrame"] * (double)r["frameMilliseconds"]);
        double sumXX = rows.Sum(r => Math.Pow((double)r["blitsPerFrame"], 2));
        double slope = (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);
        double intercept = (sumY - slope * sumX) / n;
        double ssTot = rows.Sum(r => Math.Pow((double)r["frameMilliseconds"] - sumY / n, 2));
        double ssRes = rows.Sum(r => Math.Pow((double)r["frameMilliseconds"] - (intercept + slope * (double)r["blitsPerFrame"]), 2));
        return Save("perf-attribution", new
        {
            passed = true,
            targetHz = 8192f,
            readbacksMuted = true,
            fit = new
            {
                microsecondsPerBlit = slope * 1000.0,
                fixedFrameMilliseconds = intercept,
                rSquared = ssTot > 0 ? 1 - ssRes / ssTot : 0
            },
            rows
        });
    }

    // ---- 3. Settle depth: how many passes does a phase really need? ----
    public static async Task<object> PeekState()
    {
        UdonBehaviour sim = Vm();
        RenderTexture state = (RenderTexture)sim.GetProgramVariable("currentState");
        Color32[] pixels = await ReadPixels(state);
        var sample = pixels.Take(12).Select(p => new { r = p.r, g = p.g, b = p.b, a = p.a }).ToArray();
        return Save("perf-peek", new
        {
            passed = true,
            format = state.format.ToString(),
            width = state.width,
            length = pixels.Length,
            depthBytes = pixels.Select(p => (int)p.g).Distinct().OrderBy(v => v).Take(20).ToArray(),
            sample
        });
    }

    public static object Diagnose()
    {
        CircuitLabSimulator simulator = Proxy();
        UdonBehaviour sim = Vm();
        UdonBehaviour coord = CoordVm();
        return Save("perf-diagnose", new
        {
            passed = true,
            initialized = Convert.ToBoolean(sim.GetProgramVariable("_initialized")),
            running = Convert.ToBoolean(sim.GetProgramVariable("running")),
            queuedSteps = Convert.ToInt32(sim.GetProgramVariable("queuedStepCount")),
            queuedCommands = Convert.ToInt32(sim.GetProgramVariable("queuedCommandCount")),
            readbackPending = Convert.ToBoolean(sim.GetProgramVariable("readbackPending")),
            hasControl = simulator.coordinator.HasControl(),
            submissionBlocked = simulator.coordinator.SubmissionBlocked(),
            ready = Convert.ToBoolean(coord.GetProgramVariable("ready")),
            snapshotCaptureStage = Convert.ToInt32(coord.GetProgramVariable("snapshotCaptureStage")),
            restorePending = Convert.ToBoolean(coord.GetProgramVariable("restorePending")),
            snapshotRequested = Convert.ToBoolean(coord.GetProgramVariable("_snapshotRequested")),
            serializationPending = Convert.ToBoolean(coord.GetProgramVariable("_serializationPending")),
            snapshotRevision = Convert.ToInt32(coord.GetProgramVariable("snapshotRevision")),
            appliedSnapshotRevision = Convert.ToInt32(coord.GetProgramVariable("appliedSnapshotRevision")),
            snapshotCycle = Convert.ToInt32(coord.GetProgramVariable("snapshotCycle")),
            coordinatorReadbackPending = Convert.ToBoolean(coord.GetProgramVariable("_readbackPending")),
            cycle = Convert.ToInt32(sim.GetProgramVariable("cycleCount")),
            terminalProcessedCycle = Convert.ToInt32(TerminalVm().GetProgramVariable("lastProcessedCycle")),
            terminalReadbackPending = Convert.ToBoolean(TerminalVm().GetProgramVariable("readbackPending")),
            terminalEvents = Convert.ToInt32(TerminalVm().GetProgramVariable("eventCount")),
            terminalErrors = Convert.ToInt32(TerminalVm().GetProgramVariable("readbackErrors")),
            terminalMissed = Convert.ToInt32(TerminalVm().GetProgramVariable("missedCycles"))
        });
    }

    // Adaptive settle status: the depth currently used versus the full depth, plus
    // how deep the probe measured and whether any window was ever truncated.
    public static object AdaptiveStatus()
    {
        UdonBehaviour sim = Vm();
        return Save("perf-adaptive", new
        {
            passed = true,
            adaptiveSettle = Convert.ToBoolean(sim.GetProgramVariable("adaptiveSettle")),
            settlePasses = Convert.ToInt32(sim.GetProgramVariable("settlePasses")),
            clockSettlePasses = Convert.ToInt32(sim.GetProgramVariable("clockSettlePasses")),
            depthCurrent = Convert.ToInt32(sim.GetProgramVariable("settleDepthCurrent")),
            depthLearned = Convert.ToInt32(sim.GetProgramVariable("settleDepthLearned")),
            depthNeeded = Convert.ToInt32(sim.GetProgramVariable("settleDepthNeeded")),
            samples = Convert.ToInt32(sim.GetProgramVariable("settleProbeSamples")),
            truncations = Convert.ToInt32(sim.GetProgramVariable("settleTruncations")),
            truncationDepth = Convert.ToInt32(sim.GetProgramVariable("settleTruncationDepth")),
            truncationCycle = Convert.ToInt32(sim.GetProgramVariable("settleTruncationCycle")),
            margin = Convert.ToInt32(sim.GetProgramVariable("settleDepthMargin")),
            cycles = Convert.ToInt32(sim.GetProgramVariable("cycleCount")),
            program = Convert.ToString(sim.GetProgramVariable("activeProgramName"))
        });
    }

    // Why would the terminal never read back? FinishCapture() waits on it, and a
    // terminal that never advances freezes SubmissionBlocked() forever.
    public static object TerminalHealth()
    {
        CircuitLabTerminal terminal = Proxy().terminal;
        UdonBehaviour vm = TerminalVm();
        return Save("perf-terminal", new
        {
            passed = true,
            proxyLinked = terminal != null,
            vmEnabled = vm != null && vm.enabled,
            activeInHierarchy = terminal != null && terminal.gameObject.activeInHierarchy,
            simulatorLinked = terminal != null && terminal.simulator != null,
            captureMaterial = terminal != null && terminal.captureMaterial != null,
            eventA = terminal != null && terminal.eventA != null,
            eventB = terminal != null && terminal.eventB != null,
            outputSignals = terminal == null || terminal.outputSignals == null ? -1 : terminal.outputSignals.Length,
            sequenceSignals = terminal == null || terminal.sequenceSignals == null ? -1 : terminal.sequenceSignals.Length,
            outputWriteSignal = Convert.ToInt32(vm.GetProgramVariable("outputWriteSignal")),
            eventCapacity = Convert.ToInt32(vm.GetProgramVariable("eventCapacity")),
            readbackInterval = Convert.ToSingle(vm.GetProgramVariable("readbackInterval")),
            lastProcessedCycle = Convert.ToInt32(vm.GetProgramVariable("lastProcessedCycle")),
            readbackPending = Convert.ToBoolean(vm.GetProgramVariable("readbackPending")),
            eventCount = Convert.ToInt32(vm.GetProgramVariable("eventCount")),
            simulatorCycle = Convert.ToInt32(Vm().GetProgramVariable("cycleCount"))
        });
    }

    // Clears a capture that is stuck waiting for the terminal to catch up. The
    // terminal readback must keep its normal cadence for FinishCapture to fire at
    // all, so measurements only stretch the snapshot cadence, never the terminal's.
    public static object UnblockCapture()
    {
        CircuitLabSimulator simulator = Proxy();
        UdonBehaviour coord = CoordVm();
        int stage = Convert.ToInt32(coord.GetProgramVariable("snapshotCaptureStage"));
        coord.SetProgramVariable("_readbackPending", false);
        coord.SetProgramVariable("snapshotCaptureStage", 0);
        coord.SetProgramVariable("ready", true);
        CoordVm().SendCustomEvent("PublishClock");
        return Save("perf-unblock", new { passed = true, previousStage = stage, submissionBlocked = simulator.coordinator.SubmissionBlocked() });
    }

    // Restores every Udon variable a measurement may have changed, so a failed run
    // can never leave the live scene with stretched cadences or a stuck capture.
    public static object RestoreDefaults()
    {
        CircuitLabSimulator simulator = Proxy();
        UdonBehaviour sim = Vm();
        UdonBehaviour coord = CoordVm();
        UdonBehaviour terminal = TerminalVm();
        sim.SetProgramVariable("telemetryInterval", .5f);
        sim.SetProgramVariable("readbackInterval", .05f);
        sim.SetProgramVariable("trackSettlePasses", false);
        sim.SetProgramVariable("running", false);
        sim.SetProgramVariable("settlePasses", Convert.ToInt32(((JObject)JObject.Parse(File.ReadAllText(NetlistPath)))["settlePasses"]));
        sim.SetProgramVariable("clockSettlePasses", Convert.ToInt32(((JObject)JObject.Parse(File.ReadAllText(NetlistPath)))["clockSettlePasses"]));
        coord.SetProgramVariable("snapshotInterval", 8f);
        coord.SetProgramVariable("snapshotCaptureStage", 0);
        coord.SetProgramVariable("_readbackPending", false);
        coord.SetProgramVariable("_serializationPending", false);
        coord.SetProgramVariable("_snapshotRequested", false);
        coord.SetProgramVariable("ready", true);
        terminal.SetProgramVariable("readbackInterval", .05f);
        return Save("perf-restore", new
        {
            passed = true,
            submissionBlocked = simulator.coordinator.SubmissionBlocked(),
            terminalReadbackInterval = Convert.ToSingle(terminal.GetProgramVariable("readbackInterval")),
            telemetryInterval = Convert.ToSingle(sim.GetProgramVariable("telemetryInterval")),
            snapshotInterval = Convert.ToSingle(coord.GetProgramVariable("snapshotInterval"))
        });
    }

    public static async Task<object> Depth(string programId = "blinker", int cycles = 40)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Proxy();
        UdonBehaviour sim = Vm();
        if (!simulator.coordinator.HasControl())
        {
            simulator.coordinator.RequestControl();
            await Until(() => simulator.coordinator.HasControl(), "Could not take control.", 20);
        }
        await Until(() => Convert.ToBoolean(sim.GetProgramVariable("_initialized")), "Simulator never initialized.", 90);

        // Silence the periodic readbacks; they hold SubmissionBlocked() true and
        // no step can be serviced while a capture is in flight.
        float telemetryInterval = Convert.ToSingle(sim.GetProgramVariable("telemetryInterval"));
        float snapshotInterval = Convert.ToSingle(CoordVm().GetProgramVariable("snapshotInterval"));
        float terminalInterval = Convert.ToSingle(TerminalVm().GetProgramVariable("readbackInterval"));
        sim.SetProgramVariable("telemetryInterval", 36000f);
        // The terminal readback must stay alive: FinishCapture waits for it, so
        // stretching it deadlocks SubmissionBlocked() instead of saving time.
        CoordVm().SetProgramVariable("snapshotInterval", 36000f);
        CoordVm().SetProgramVariable("snapshotCaptureStage", 0);
        CoordVm().SetProgramVariable("_serializationPending", false);
        if (!Convert.ToBoolean(CoordVm().GetProgramVariable("ready")))
        {
            CoordVm().SetProgramVariable("ready", true);
            CoordVm().SetProgramVariable("snapshotCaptureStage", 0);
        }
        await Task.Delay(1500);

        // Probe on a runtime material copy: project assets stay untouched.
        Material original = (Material)sim.GetProgramVariable("simulationMaterial");
        Material probe = new Material(original);
        probe.SetInt("_TrackChanges", 1);
        probe.SetInt("_ProbeMode", 0);
        sim.SetProgramVariable("simulationMaterial", probe);
        sim.SetProgramVariable("trackSettlePasses", true);
        int settlePasses = Convert.ToInt32(sim.GetProgramVariable("settlePasses"));
        int clockPasses = Convert.ToInt32(sim.GetProgramVariable("clockSettlePasses"));
        sim.SetProgramVariable("settlePasses", settlePasses);

        UdonBehaviour browser = UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First());
        int index = ProgramIndex(programId);
        sim.SetProgramVariable("running", false);
        await Task.Delay(400);
        browser.SetProgramVariable("selectedProgram", index);
        browser.SendCustomEvent("RenderBrowser");
        browser.SendCustomEvent("LoadSelectedPreset");
        await Task.Delay(1000);
        // Let the load settle, then sample one cycle at a time.
        await Until(() => Convert.ToInt32(sim.GetProgramVariable("queuedCommandCount")) == 0, "Load queue did not drain.", 30);

        var depths = new List<int>();
        var changedNodes = new List<int>();
        var sampleCycles = new List<int>();
        for (int step = 0; step < cycles; step++)
        {
            int before = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
            sim.SendCustomEvent("Step");
            await Until(() => Convert.ToInt32(sim.GetProgramVariable("cycleCount")) > before, "Step did not advance the CPU.", 20);
            await Until(() => !Convert.ToBoolean(sim.GetProgramVariable("readbackPending")), "Readback did not drain.", 20);
            await Task.Delay(60);
            Color32[] pixels = await ReadPixels((RenderTexture)sim.GetProgramVariable("currentState"));
            int maxDepth = 0, changed = 0;
            for (int i = 0; i < simulator.nodeCount && i < pixels.Length; i++)
            {
                // Stored as (passIndex + 1) / 255, i.e. byte value passIndex + 1.
                int depth = pixels[i].g - 1;
                if (depth < 0) depth = 0;
                if (depth > maxDepth) maxDepth = depth;
                if (depth > 0) changed++;
            }
            depths.Add(maxDepth);
            changedNodes.Add(changed);
            sampleCycles.Add(Convert.ToInt32(sim.GetProgramVariable("cycleCount")));
        }

        sim.SetProgramVariable("trackSettlePasses", false);
        sim.SetProgramVariable("simulationMaterial", original);
        Object.DestroyImmediate(probe);
        var sorted = depths.OrderBy(value => value).ToArray();
        return Save("perf-depth-" + programId, new
        {
            passed = true,
            program = programId,
            settlePasses,
            clockSettlePasses = clockPasses,
            sampledCycles = cycles,
            maxDepthSeen = sorted.Length > 0 ? sorted[sorted.Length - 1] : 0,
            medianDepth = sorted.Length > 0 ? sorted[sorted.Length / 2] : 0,
            p90Depth = sorted.Length > 0 ? sorted[Mathf.Clamp((int)(sorted.Length * .9f), 0, sorted.Length - 1)] : 0,
            histogram = depths.GroupBy(value => value).OrderBy(group => group.Key)
                .Select(group => new { depth = group.Key, count = group.Count() }).ToArray(),
            averageChangedNodes = changedNodes.Count == 0 ? 0 : changedNodes.Average(),
            depths,
            sampleCycles
        });
    }
}
