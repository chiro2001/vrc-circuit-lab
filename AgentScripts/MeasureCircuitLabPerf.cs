using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;
using Object = UnityEngine.Object;

// Performance probe for the GPU teaching CPU. It only measures; it never
// authors scene content. Run the entries from Play Mode.
public static class MeasureCircuitLabPerf
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private const string ReportDir = "Reports/CircuitLab";

    private static object Save(string name, object report)
    {
        System.IO.Directory.CreateDirectory(ReportDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(ReportDir, name + ".json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }

    private static CircuitLabSimulator Sim() =>
        Object.FindObjectsOfType<CircuitLabSimulator>(true).First();

    private static async Task Until(Func<bool> predicate, string message, int seconds = 60)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate())
        {
            if (DateTime.UtcNow > limit) throw new InvalidOperationException(message);
            await Task.Delay(100);
        }
    }

    // Takes shared control so control-gated commands (self test, presets) can run.
    public static async Task<object> TakeControl()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Sim();
        if (!simulator.coordinator.HasControl())
        {
            simulator.coordinator.RequestControl();
            await Until(() => simulator.coordinator.HasControl(), "Could not take control.", 20);
        }
        return new { hasControl = simulator.coordinator.HasControl(), authority = simulator.coordinator.controlAuthorityId };
    }

    // How many interop operations does one simulated cycle actually issue?
    public static object Inventory()
    {
        CircuitLabSimulator simulator = Sim();
        UdonBehaviour sim = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        Material[] displays = simulator.displayMaterials ?? new Material[0];
        int settledStates = 0;
        IEnumerable<Material> all = displays.Concat(new[]
        {
            simulator.simulationMaterial, simulator.historyMaterial, simulator.ramMaterial,
            simulator.displayMaterial, simulator.waveformMaterial,
            simulator.terminal == null ? null : simulator.terminal.captureMaterial
        }).Where(item => item != null);
        return Save("perf-inventory", new
        {
            passed = true,
            displayMaterials = displays.Length,
            uniqueMaterials = all.Distinct().Count(),
            settlePasses = simulator.settlePasses,
            textureWidth = simulator.textureWidth,
            nodeCount = simulator.nodeCount,
            historyCapacity = simulator.historyCapacity,
            statePassesPerCycle = simulator.settlePasses * 2 + 1,
            blitsPerCycle = simulator.settlePasses * 2 + 1 + 2 + 1,
            note = "2 settles + 1 edge + 2 history + 1 RAM; PublishState then rebinds every display material",
            settledStates = settledStates,
            localization = Object.FindObjectsOfType<CircuitLabLocalization>(true).Select(item => new
            {
                sources = item.sourceTexts == null ? 0 : item.sourceTexts.Length,
                scanIntervalSeconds = .2f,
                scansPerSecond = 5f
            }).ToArray()
        });
    }

    // Ablation throughput: which part of a cycle actually costs the time?
    // Each variant is a runtime-only mutation inside Play Mode; nothing is saved.
    public static async Task<object> Ablations()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Sim();
        UdonBehaviour sim = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        if (!simulator.coordinator.HasControl())
        {
            simulator.coordinator.RequestControl();
            await Until(() => simulator.coordinator.HasControl(), "Could not take control.", 20);
        }
        await Until(() => Convert.ToBoolean(sim.GetProgramVariable("_initialized")), "Simulator never initialized.", 90);
        var programs = (JArray)JObject.Parse(System.IO.File.ReadAllText("Assets/CircuitLab/Data/gpu-netlist.json"))["programs"];
        int index = Enumerable.Range(0, programs.Count).First(i => (string)programs[i]["id"] == "blinker");
        UdonBehaviour browser = UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First());
        browser.SetProgramVariable("selectedProgram", index);
        browser.SendCustomEvent("RenderBrowser");
        browser.SendCustomEvent("LoadSelectedPreset");
        await Task.Delay(800);
        sim.SetProgramVariable("cyclesPerSecond", 8192f);
        sim.SendCustomEvent("Run");
        await Task.Delay(700);

        var results = new JArray();
        Func<string, int, Task<double>> measure = async (label, milliseconds) =>
        {
            await Task.Delay(400);
            int startCycle = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
            float started = Time.realtimeSinceStartup;
            await Task.Delay(milliseconds);
            int cycles = Convert.ToInt32(sim.GetProgramVariable("cycleCount")) - startCycle;
            float elapsed = Time.realtimeSinceStartup - started;
            double rate = elapsed > 0 ? cycles / elapsed : 0;
            results.Add(new JObject { ["variant"] = label, ["cyclesPerSecond"] = rate, ["cycles"] = cycles });
            return rate;
        };

        // Ablate on the Udon variable table: the running program reads these, not
        // the proxy fields.
        CircuitLabLocalization localization = Object.FindObjectsOfType<CircuitLabLocalization>(true).First();
        UdonBehaviour localizationVm = UdonSharpEditorUtility.GetBackingUdonBehaviour(localization);
        Material[] displayMaterials = (Material[])sim.GetProgramVariable("displayMaterials");
        int settlePasses = Convert.ToInt32(sim.GetProgramVariable("settlePasses"));

        await measure("baseline", 3000);
        sim.SetProgramVariable("displayMaterials", new Material[0]);
        await measure("no display rebinding", 3000);
        sim.SetProgramVariable("displayMaterials", displayMaterials);
        localizationVm.enabled = false;
        await measure("no localization scan", 3000);
        localizationVm.enabled = true;
        sim.SetProgramVariable("displayMaterials", new Material[0]);
        localizationVm.enabled = false;
        await measure("neither", 3000);
        sim.SetProgramVariable("displayMaterials", displayMaterials);
        localizationVm.enabled = true;
        foreach (int candidate in new[] { 1, 4, 11, 22, settlePasses })
        {
            sim.SetProgramVariable("settlePasses", candidate);
            await measure("settlePasses=" + candidate, 2000);
        }
        sim.SetProgramVariable("settlePasses", settlePasses);
        sim.SetProgramVariable("displayMaterials", displayMaterials);
        sim.SendCustomEvent("Pause");
        return Save("perf-ablations", new
        {
            passed = true,
            target = 8192f,
            settlePasses,
            displayMaterials = displayMaterials == null ? 0 : displayMaterials.Length,
            historyCapacity = Convert.ToInt32(sim.GetProgramVariable("historyCapacity")),
            localizationSources = localization.sourceTexts == null ? 0 : localization.sourceTexts.Length,
            results
        });
    }

    // Per-frame diagnosis: how many cycles each frame submits, and what gates
    // submission (telemetry readback, coordinator snapshot work, etc.).
    public static async Task<object> Frames(float target = 8192f, float seconds = 3f)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Sim();
        UdonBehaviour sim = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        UdonBehaviour coordinator = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator.coordinator);
        if (!simulator.coordinator.HasControl())
        {
            simulator.coordinator.RequestControl();
            await Until(() => simulator.coordinator.HasControl(), "Could not take control.", 20);
        }
        await Until(() => Convert.ToBoolean(sim.GetProgramVariable("_initialized")), "Simulator never initialized.", 90);
        var programs = (JArray)JObject.Parse(System.IO.File.ReadAllText("Assets/CircuitLab/Data/gpu-netlist.json"))["programs"];
        int index = Enumerable.Range(0, programs.Count).First(i => (string)programs[i]["id"] == "blinker");
        UdonBehaviour browser = UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First());
        browser.SetProgramVariable("selectedProgram", index);
        browser.SendCustomEvent("RenderBrowser");
        browser.SendCustomEvent("LoadSelectedPreset");
        await Task.Delay(800);
        sim.SetProgramVariable("cyclesPerSecond", target);
        sim.SendCustomEvent("Run");
        await Task.Delay(500);

        int lastFrame = Time.frameCount;
        int lastCycle = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
        float lastTime = Time.realtimeSinceStartup;
        int readbackFrames = 0, blockedFrames = 0, activeFrames = 0, frames = 0;
        int submittedTotal = 0, maxPerFrame = 0, minPerFrame = int.MaxValue;
        float firstTime = lastTime;
        while (Time.realtimeSinceStartup - firstTime < seconds)
        {
            await Task.Yield();
            if (Time.frameCount == lastFrame) continue;
            int cycles = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
            bool pending = Convert.ToBoolean(sim.GetProgramVariable("readbackPending"));
            bool blocked = simulator.coordinator.SubmissionBlocked();
            int submitted = cycles - lastCycle;
            frames++;
            submittedTotal += submitted;
            if (submitted > maxPerFrame) maxPerFrame = submitted;
            if (submitted < minPerFrame) minPerFrame = submitted;
            if (pending) readbackFrames++;
            if (blocked) blockedFrames++;
            if (submitted > 0) activeFrames++;
            lastFrame = Time.frameCount; lastCycle = cycles; lastTime = Time.realtimeSinceStartup;
        }
        float elapsed = Time.realtimeSinceStartup - firstTime;
        sim.SendCustomEvent("Pause");
        return Save("perf-frames", new
        {
            passed = true,
            targetHz = target,
            seconds = elapsed,
            frames,
            fps = frames / elapsed,
            cyclesPerSecond = submittedTotal / elapsed,
            maxCyclesPerFrame = maxPerFrame,
            minCyclesPerFrame = minPerFrame == int.MaxValue ? 0 : minPerFrame,
            averageCyclesPerFrame = frames == 0 ? 0f : (float)submittedTotal / frames,
            framesWithReadbackPending = readbackFrames,
            framesSubmissionBlocked = blockedFrames,
            framesThatSubmitted = activeFrames,
            frameDelta = Convert.ToSingle(sim.GetProgramVariable("_frameDelta")),
            telemetryInterval = Convert.ToSingle(sim.GetProgramVariable("telemetryInterval")),
            snapshotCaptureStage = Convert.ToInt32(coordinator.GetProgramVariable("snapshotCaptureStage")),
            serializationPending = Convert.ToBoolean(coordinator.GetProgramVariable("_serializationPending"))
        });
    }

    // Cycles per second of the real simulator at a fixed target.
    public static async Task<object> Rates(float target = 8192f, float seconds = 5f)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Sim();
        UdonBehaviour sim = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        if (!simulator.coordinator.HasControl())
        {
            simulator.coordinator.RequestControl();
            await Until(() => simulator.coordinator.HasControl(), "Could not take control.", 20);
        }
        await Until(() => Convert.ToBoolean(sim.GetProgramVariable("_initialized")), "Simulator never initialized.", 90);
        var programs = (JArray)JObject.Parse(System.IO.File.ReadAllText("Assets/CircuitLab/Data/gpu-netlist.json"))["programs"];
        int index = Enumerable.Range(0, programs.Count).First(i => (string)programs[i]["id"] == "blinker");
        UdonBehaviour browser = UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First());
        browser.SetProgramVariable("selectedProgram", index);
        browser.SendCustomEvent("RenderBrowser");
        browser.SendCustomEvent("LoadSelectedPreset");
        await Task.Delay(800);
        sim.SetProgramVariable("cyclesPerSecond", target);
        sim.SendCustomEvent("Run");
        await Task.Delay(500);

        var samples = new JArray();
        foreach (float current in new[] { 256f, 1024f, 8192f })
        {
            sim.SetProgramVariable("cyclesPerSecond", current);
            await Task.Delay(700);
            int startCycle = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
            float started = Time.realtimeSinceStartup;
            await Task.Delay((int)(seconds * 1000));
            int cycles = Convert.ToInt32(sim.GetProgramVariable("cycleCount")) - startCycle;
            float elapsed = Time.realtimeSinceStartup - started;
            samples.Add(new JObject
            {
                ["targetHz"] = current,
                ["cycles"] = cycles,
                ["elapsedSeconds"] = elapsed,
                ["cyclesPerSecond"] = elapsed > 0 ? cycles / elapsed : 0f,
                ["reportedHz"] = Convert.ToSingle(sim.GetProgramVariable("actualCyclesPerSecond")),
                ["lag"] = Convert.ToInt32(sim.GetProgramVariable("catchupLag"))
            });
        }
        sim.SendCustomEvent("Pause");
        return Save("perf-rates", new { passed = true, samples });
    }
}
