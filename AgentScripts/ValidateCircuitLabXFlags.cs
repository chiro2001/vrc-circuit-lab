using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using Object = UnityEngine.Object;

// Exercise the shared HEX loader, real Udon VM and GPU CPU, without supplying
// reference values to the simulator. Call BackupAuthoring before Play Mode and
// RestoreAuthoring after stopping to retain the existing memory/material assets.
public static class ValidateCircuitLabXFlags
{
    private static UdonBehaviour Vm(UdonSharpBehaviour proxy) => UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
    private static int Int(UdonBehaviour vm, string field) => Convert.ToInt32(vm.GetProgramVariable(field));
    private static bool Bool(UdonBehaviour vm, string field) => Convert.ToBoolean(vm.GetProgramVariable(field));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> test, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!test()) { Check(DateTime.UtcNow < deadline, message); await Task.Delay(40); }
    }
    private static object Save(string name, object report)
    {
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/" + name + ".json", JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }
    public static object Authoring()
    {
        Check(!EditorApplication.isPlaying, "Check saved assets in Edit Mode.");
        var sim = Object.FindObjectOfType<CircuitLabSimulator>();
        var data = JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/gpu-netlist.json").text);
        Check(sim.nodeCount == (int)data["nodeCount"] && sim.expectedStates.Length == sim.nodeCount * sim.vectorCount, "Fixture dimensions differ.");
        foreach (string name in new[] { "acc", "x", "sp", "pc", "out", "instruction", "bank", "phase" })
        {
            var signals = (int[])typeof(CircuitLabSimulator).GetField(name + "Signals").GetValue(sim);
            Check(signals.SequenceEqual(data["ports"][name].ToObject<int[]>()), "Port mapping differs: " + name);
        }
        Check(sim.zeroSignal == (int)data["ports"]["zero"][0] && sim.carrySignal == (int)data["ports"]["carry"][0]
            && sim.haltSignal == (int)data["ports"]["halt"][0], "Flag mapping differs.");
        Check(sim.dffSignals.SequenceEqual(data["dffSignals"].ToObject<int[]>()), "DFF snapshot mapping differs.");
        var pixels = sim.gateTexture.GetPixels(); var indices = sim.restoreIndexTexture.GetPixels();
        foreach (JObject node in data["nodes"])
        {
            int id = (int)node["id"]; Color pixel = pixels[id];
            Check(pixel.r == (int)node["op"] && pixel.g == (int)node["a"] && pixel.b == (int)node["b"] && pixel.a == (int)node["c"], "Gate texture differs at " + id);
        }
        for (int i = 0; i < sim.dffSignals.Length; i++) Check(indices[sim.dffSignals[i]].r == i, "Restore index differs.");
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        Check(tablet.nodeCount == sim.nodeCount && tablet.signalNames.Length == sim.nodeCount, "Tablet metadata is stale.");
        foreach (JObject node in data["nodes"]) Check(tablet.signalNames[(int)node["id"]] == (string)node["name"], "Tablet node label differs.");
        foreach (var target in Object.FindObjectsOfType<CircuitLabSignalTarget>(true))
            Check(target.signalIds.All(id => id >= 0 && id < sim.nodeCount + 1024 * 8)
                && target.targetIndex >= 0 && target.targetIndex < tablet.signalTargets.Length
                && tablet.signalTargets[target.targetIndex] == target, "Probe target mapping is stale: " + target.name);
        foreach (var wire in Object.FindObjectsOfType<CircuitLabWire>(true))
        {
            var source = wire.source.GetComponent<CircuitLabSignalTarget>();
            var destination = wire.destination.GetComponent<CircuitLabSignalTarget>();
            int signal = Mathf.RoundToInt(wire.line.sharedMaterial.GetFloat("_SignalOverride"));
            Check(source != null && destination != null && signal == destination.signalIds[0]
                && source.signalIds.Contains(signal), "Wire material mapping differs: " + wire.name);
        }
        foreach (var mesh in Object.FindObjectsOfType<MeshFilter>(true).Select(m => m.sharedMesh).Where(m => m != null
            && AssetDatabase.GetAssetPath(m).StartsWith("Assets/CircuitLab/Meshes/Module_")).Distinct())
            Check(mesh.uv.All(uv => uv.x >= 0 && uv.x < sim.nodeCount && uv.x == Mathf.Round(uv.x)), "Mesh signal mapping is invalid: " + mesh.name);
        var locale = Object.FindObjectOfType<CircuitLabLocalization>();
        Check(locale.sourceTexts.All(t => t != null) && locale.sourceTexts.Length == locale.keepText.Length
            && locale.sourceTexts.Length == locale.englishDisplays.Length && locale.dynamicIndices.All(i => i < locale.sourceTexts.Length), "Localization tracking differs.");
        Check(locale.englishDisplays.All(t => t != null) && locale.chineseDisplays.All(t => t != null)
            && locale.japaneseDisplays.All(t => t != null), "SDF label overlays are missing.");
        Check(!UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, "Scene has unsaved changes.");
        return Save("x-flags-authoring", new { passed = true, sim.nodeCount, sim.vectorCount,
            gatePixelsCompared = sim.nodeCount, dffIndicesCompared = sim.dffSignals.Length, probeTargets = tablet.signalTargets.Length,
            localTexts = locale.sourceTexts.Length, saved = true });
    }
    private static async Task<UdonBehaviour> Run(int[] words, bool fullSettle = true)
    {
        Check(EditorApplication.isPlaying, "Enter Play Mode first.");
        var proxy = Object.FindObjectOfType<CircuitLabSimulator>();
        var sim = Vm(proxy); var network = Vm(proxy.coordinator);
        await Until(() => VRC.SDKBase.Networking.LocalPlayer != null, "ClientSim player is not ready.");
        network.SendCustomEvent("RequestControl");
        await Until(() => Bool(network, "ready") && Int(network, "controlAuthorityId") == VRC.SDKBase.Networking.LocalPlayer.playerId, "Cannot acquire shared control.");
        sim.SendCustomEvent("Pause");
        await Until(() => !Bool(sim, "running") && Int(sim, "queuedCommandCount") == 0
            && Int(network, "snapshotCaptureStage") == 0 && !Bool(network, "restorePending"), "Pause did not settle.");
        var panel = Object.FindObjectOfType<CircuitLabProgramPanel>(); var editor = Vm(panel);
        editor.SendCustomEvent("SetInputHex");
        panel.draftInput.text = string.Join("\n", words.Select(word => "0x" + word.ToString("X3")));
        int revision = Int(sim, "programRevision");
        editor.SendCustomEvent("LoadDraft");
        await Until(() => Int(sim, "programRevision") > revision && Int(sim, "queuedCommandCount") == 0,
            "HEX draft was not loaded: " + panel.editorStatus.text);
        // HALT telemetry from the preceding test can outlive the load command.
        // Sample the reset GPU state before waiting for this program's HALT.
        await Until(() => !Bool(sim, "readbackPending") && Int(network, "snapshotCaptureStage") == 0, "Loaded program did not settle.");
        int resetReads = Int(sim, "telemetryReadbackCount"); sim.SendCustomEvent("RequestTelemetry");
        await Until(() => Int(sim, "telemetryReadbackCount") > resetReads && !Bool(sim, "readbackPending"), "Reset telemetry timed out.");
        Check(Int(sim, "observedHalt") == 0, "Loaded program did not reset HALT.");
        sim.SetProgramVariable("cyclesPerSecond", 256f);
        sim.SetProgramVariable("adaptiveSettle", !fullSettle);
        sim.SendCustomEvent("Run");
        await Until(() => Int(sim, "observedHalt") == 1, "GPU program did not halt.");
        sim.SendCustomEvent("Pause");
        await Until(() => !Bool(sim, "running") && Int(sim, "queuedCommandCount") == 0
            && Int(network, "snapshotCaptureStage") == 0, "HALT pause did not settle.");
        int reads = Int(sim, "telemetryReadbackCount"); sim.SendCustomEvent("RequestTelemetry");
        await Until(() => Int(sim, "telemetryReadbackCount") > reads && !Bool(sim, "readbackPending"), "GPU telemetry timed out.");
        return sim;
    }
    public static async Task<object> PlayerRepro(string expected = "Y0", bool fullSettle = false)
    {
        // Exact debug.hex supplied by the player; its JZ observes Z immediately
        // after INX and TXA independently confirms the wrapped X value.
        var sim = await Run(new[] { 0x0ff, 0xe20, 0xe60, 0x706, 0x04e, 0x607,
            0x059, 0x500, 0xe30, 0x130, 0x500, 0xf00 }, fullSettle);
        var terminal = Vm(Object.FindObjectOfType<CircuitLabSimulator>().terminal);
        await Until(() => Int(terminal, "eventCount") == 2, "Terminal did not receive both OUT events.");
        string output = Convert.ToString(terminal.GetProgramVariable("output"));
        Check(output == expected, "Player repro output " + output + " != " + expected);
        Check(Int(sim, "observedX") == 0, "INX did not wrap X to zero.");
        return Save("x-flags-player-" + expected, new { passed = true, output, x = Int(sim, "observedX"),
            terminalEvents = Int(terminal, "eventCount"), adaptiveSettle = !fullSettle,
            source = "player debug.hex through shared HEX loader and GPU" });
    }
    public static async Task<object> Boundaries()
    {
        int cases = 0;
        foreach (bool down in new[] { false, true })
            foreach (int start in new[] { 0, 1, 2, 254, 255 })
                foreach (int carry in new[] { 0, 1 })
                {
                    // TAX sets X first, then seed C (and the opposite Z when
                    // C=0). Check the count flags and that ACC is untouched.
                    int result = (start + (down ? -1 : 1)) & 255;
                    int expectedZero = result == 0 ? 1 : 0;
                    int[] seed = carry == 1 ? new[] { 0x0ff, 0x101 }
                        : new[] { expectedZero == 1 ? 0x001 : 0x000 };
                    var words = new[] { start, 0xe20 }.Concat(seed)
                        .Concat(new[] { down ? 0xe70 : 0xe60, 0xf00 }).ToArray();
                    var sim = await Run(words);
                    Check(Int(sim, "observedX") == result && Int(sim, "observedZero") == expectedZero
                        && Int(sim, "observedCarry") == carry, "X/Z/C mismatch for " + (down ? "DEX" : "INX") + " " + start + " C" + carry
                        + ": X=" + Int(sim, "observedX") + " Z=" + Int(sim, "observedZero") + " C=" + Int(sim, "observedCarry"));
                    Check(Int(sim, "observedAcc") == (carry == 1 ? 0 : expectedZero == 1 ? 1 : 0), "Count changed ACC.");
                    cases++;
                }
        return Save("x-flags-gpu-boundaries", new { passed = true, cases, fullSettle = true,
            inxWrap = "255 -> 0, Z=1", dexZero = "1 -> 0, Z=1", dexWrap = "0 -> 255, Z=0", carryPreserved = true });
    }
}
