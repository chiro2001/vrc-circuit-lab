using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using Object = UnityEngine.Object;

// Restores an actual captured Udon/GPU snapshot into a second, isolated GPU VM.
// The copied synced payload is a local transport stand-in; it does not exercise
// VRChat's real multiplayer serialization or ownership transfer.
public static class ValidateCircuitLabRamReplica
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";

    private static T[] Find<T>() where T : Component => Object.FindObjectsOfType<T>(true)
        .Where(component => component.gameObject.scene.path == ScenePath).ToArray();
    private static UdonBehaviour Vm(UdonSharpBehaviour proxy) => UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
    private static int Int(UdonBehaviour vm, string field) => Convert.ToInt32(vm.GetProgramVariable(field));
    private static bool Bool(UdonBehaviour vm, string field) => Convert.ToBoolean(vm.GetProgramVariable(field));
    private static string Str(UdonBehaviour vm, string field) => Convert.ToString(vm.GetProgramVariable(field));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> predicate, string message, int seconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(30);
        Check(predicate(), message);
    }

    private static object Save(object result)
    {
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/ram-replica.json", JsonConvert.SerializeObject(result, Formatting.Indented));
        return result;
    }

    private static async Task<Color32[]> ReadBytes(RenderTexture texture, string label)
    {
        var completion = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) completion.TrySetException(new InvalidOperationException(label + " GPU readback failed."));
            else completion.TrySetResult(request.GetData<Color32>().ToArray());
        });
        if (await Task.WhenAny(completion.Task, Task.Delay(10000)) != completion.Task)
            throw new TimeoutException(label + " GPU readback timed out.");
        return await completion.Task;
    }

    private static async Task<Color[]> ReadFloats(RenderTexture texture, string label)
    {
        var completion = new TaskCompletionSource<Color[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) completion.TrySetException(new InvalidOperationException(label + " waveform readback failed."));
            else completion.TrySetResult(request.GetData<Color>().ToArray());
        });
        if (await Task.WhenAny(completion.Task, Task.Delay(10000)) != completion.Task)
            throw new TimeoutException(label + " waveform readback timed out.");
        return await completion.Task;
    }

    private static byte[] RamBytes(Color32[] pixels)
    {
        Check(pixels.Length >= 256, "RAM readback did not contain 256 pixels.");
        byte[] result = new byte[256];
        for (int i = 0; i < result.Length; i++) result[i] = pixels[i].r;
        return result;
    }

    private static int CompareVisibleHistory(Color[] expected, Color[] actual, int sampleCount,
        int capacity, int[] segments, int[] bindSamples, string label)
    {
        Check(expected.Length >= capacity * 4 && actual.Length >= capacity * 4, label + " history dimensions differ.");
        int compared = 0;
        for (int channel = 0; channel < 4; channel++)
        {
            for (int slot = 0; slot < capacity; slot++)
            {
                Color left = expected[channel * capacity + slot];
                Color right = actual[channel * capacity + slot];
                int leftIndex = Mathf.RoundToInt(left.a) - 1;
                int rightIndex = Mathf.RoundToInt(right.a) - 1;
                int oldest = Mathf.Max(bindSamples[channel], Mathf.Max(0, sampleCount - capacity));
                bool leftValid = left.g > .5f && Mathf.RoundToInt(left.b) == segments[channel]
                    && leftIndex >= oldest && leftIndex < sampleCount && leftIndex % capacity == slot;
                bool rightValid = right.g > .5f && Mathf.RoundToInt(right.b) == segments[channel]
                    && rightIndex >= oldest && rightIndex < sampleCount && rightIndex % capacity == slot;
                Check(leftValid == rightValid, label + " validity differs at channel=" + channel + " slot=" + slot);
                if (!leftValid) continue;
                Check(leftIndex == rightIndex && Mathf.RoundToInt(left.r) == Mathf.RoundToInt(right.r),
                    label + " sample differs at channel=" + channel + " slot=" + slot);
                compared++;
            }
        }
        return compared;
    }

    private static void CopySynced(UdonSharpBehaviour sourceProxy, UdonBehaviour source, UdonBehaviour target)
    {
        foreach (var field in sourceProxy.GetType().GetFields()
            .Where(field => field.GetCustomAttributes(false).Any(attribute => attribute.GetType().Name == "UdonSyncedAttribute")))
        {
            object value = source.GetProgramVariable(field.Name);
            if (value is Array) value = ((Array)value).Clone();
            target.SetProgramVariable(field.Name, value);
        }
    }

    private static void RegisterAndCopyProxy(UdonSharpBehaviour[] proxies)
    {
        for (int i = 0; i < proxies.Length; i++)
            UdonManager.Instance.RegisterUdonBehaviour(Vm(proxies[i]));
        for (int i = 0; i < proxies.Length; i++)
            UdonSharpEditorUtility.CopyProxyToUdon(proxies[i]);
    }

    private static Object OwnMaterial(Material source, string suffix, System.Collections.Generic.List<Object> owned)
    {
        if (source == null) return null;
        Material clone = Object.Instantiate(source);
        clone.name = source.name + suffix;
        owned.Add(clone);
        return clone;
    }

    private static RenderTexture OwnRenderTexture(RenderTexture source, string suffix,
        System.Collections.Generic.List<Object> owned)
    {
        if (source == null) return null;
        RenderTexture clone = new RenderTexture(source.descriptor);
        clone.name = source.name + suffix;
        clone.filterMode = source.filterMode;
        clone.wrapMode = source.wrapMode;
        clone.Create();
        owned.Add(clone);
        return clone;
    }

    private static Texture2D OwnTexture(Texture2D source, string suffix, System.Collections.Generic.List<Object> owned)
    {
        if (source == null) return null;
        Texture2D clone = Object.Instantiate(source);
        clone.name = source.name + suffix;
        clone.filterMode = source.filterMode;
        clone.wrapMode = source.wrapMode;
        owned.Add(clone);
        return clone;
    }

    private static int[] Clone(int[] source) => source == null ? null : (int[])source.Clone();
    private static byte[] Clone(byte[] source) => source == null ? null : (byte[])source.Clone();
    private static string[] Clone(string[] source) => source == null ? null : (string[])source.Clone();
    private static bool[] Clone(bool[] source) => source == null ? null : (bool[])source.Clone();

    private static void CopyActiveProgram(UdonBehaviour source, CircuitLabSimulator replica)
    {
        replica.programWords = Clone((int[])source.GetProgramVariable("programWords"));
        replica.initialRam = Clone((byte[])source.GetProgramVariable("initialRam"));
        replica.presetProgramWords = Clone((int[])source.GetProgramVariable("presetProgramWords"));
        replica.presetInitialRam = Clone((byte[])source.GetProgramVariable("presetInitialRam"));
        replica.programNames = Clone((string[])source.GetProgramVariable("programNames"));
        replica.programDescriptions = Clone((string[])source.GetProgramVariable("programDescriptions"));
        replica.programSelect = Int(source, "programSelect");
        replica.programCount = Int(source, "programCount");
        replica.activeProgramName = Str(source, "activeProgramName");
        replica.programRevision = Int(source, "programRevision");
        // The validation VM must run from the restored snapshot, never from fixture vectors.
        replica.inputMasks = null;
        replica.expectedStates = null;
        replica.fixturePrograms = null;
        replica.fixtureRamReset = null;
        replica.vectorCount = 0;
        replica.runOnStart = false;
    }

    private static void CheckDffSnapshot(Color32[] state, int nodeCount, int[] dffSignals, int[] packed,
        string label)
    {
        int requiredWords = (dffSignals.Length + 31) / 32;
        Check(packed != null && packed.Length == requiredWords,
            label + " packed DFF length mismatch: expected=" + requiredWords + " actual=" + (packed == null ? -1 : packed.Length));
        for (int i = 0; i < dffSignals.Length; i++)
        {
            int signal = dffSignals[i];
            Check(signal >= 0 && signal < nodeCount, label + " DFF signal is out of range at index " + i);
            bool bit = ((packed[i / 32] >> (i % 32)) & 1) != 0;
            bool stateBit = state[signal].r >= 128;
            Check(bit == stateBit, label + " DFF bit mismatch at index=" + i + " signal=" + signal);
        }
    }

    private static void CheckIndependentResources(UdonBehaviour source, UdonBehaviour replica,
        CircuitLabSimulator sourceProxy, CircuitLabSimulator replicaProxy)
    {
        string[] textureFields = { "programTexture", "ramInitialTexture", "restoreRamTexture", "restoreStateTexture" };
        for (int i = 0; i < textureFields.Length; i++)
            Check(source.GetProgramVariable(textureFields[i]) != replica.GetProgramVariable(textureFields[i]),
                "Mutable upload/restore texture is shared: " + textureFields[i]);
        string[] materialFields = { "simulationMaterial", "displayMaterial", "historyMaterial", "waveformMaterial", "ramMaterial" };
        for (int i = 0; i < materialFields.Length; i++)
            Check(source.GetProgramVariable(materialFields[i]) != replica.GetProgramVariable(materialFields[i]),
                "Mutable material is shared: " + materialFields[i]);
        string[] renderFields = { "stateA", "stateB", "ramA", "ramB", "historyA", "historyB", "frozenHistory" };
        for (int i = 0; i < renderFields.Length; i++)
            Check(source.GetProgramVariable(renderFields[i]) != replica.GetProgramVariable(renderFields[i]),
                "Mutable GPU render buffer is shared: " + renderFields[i]);
        Check(source.GetProgramVariable("gateTexture") == replica.GetProgramVariable("gateTexture"),
            "Static gate texture should be shared by the replica.");
        Check(source.GetProgramVariable("restoreIndexTexture") == replica.GetProgramVariable("restoreIndexTexture"),
            "Static restore-index texture should be shared by the replica.");

        Check(source.GetProgramVariable("programWords") != replica.GetProgramVariable("programWords"),
            "Active program arrays are shared.");
        Check(source.GetProgramVariable("initialRam") != replica.GetProgramVariable("initialRam"),
            "Initial RAM arrays are shared.");
        Check(sourceProxy.programTexture != replicaProxy.programTexture
            && sourceProxy.ramInitialTexture != replicaProxy.ramInitialTexture
            && sourceProxy.restoreRamTexture != replicaProxy.restoreRamTexture
            && sourceProxy.restoreStateTexture != replicaProxy.restoreStateTexture,
            "Simulator proxy still points at a source mutable texture.");
    }

    private static async Task StepSource(UdonBehaviour source, int count)
    {
        int start = Int(source, "cycleCount");
        for (int i = 0; i < count; i++) source.SendCustomEvent("Step");
        await Until(() => Int(source, "cycleCount") == start + count
            && Int(source, "queuedStepCount") == 0 && !Bool(source, "readbackPending"),
            "Source did not complete exactly " + count + " actual GPU cycles.", 35);
    }

    public static async Task<object> SharedGpuRestoreAndCatchup()
    {
        CircuitLabSimulator sourceProxy = Find<CircuitLabSimulator>().Single();
        CircuitLabNetworkCoordinator sourceNetworkProxy = Find<CircuitLabNetworkCoordinator>().Single();
        UdonBehaviour source = Vm(sourceProxy);
        UdonBehaviour sourceNetwork = Vm(sourceNetworkProxy);
        UdonBehaviour sourceClock = Vm(sourceNetworkProxy.clock);
        CircuitLabTerminal sourceTerminalProxy = sourceProxy.terminal;
        UdonBehaviour sourceTerminal = Vm(sourceTerminalProxy);

        await Until(() => Bool(source, "_initialized") && Bool(sourceNetwork, "ready"),
            "Source simulator/network is not initialized.", 40);
        Check(!Bool(source, "running") && !Bool(source, "testRunning") && !Bool(source, "readbackPending")
            && Int(source, "queuedCommandCount") == 0 && Int(source, "queuedStepCount") == 0,
            "Prepare a paused, stable source before replica validation.");
        Check(Bool(source, "historyFrozen") && Int(source, "frozenSampleCount") > 0,
            "Source needs a populated frozen waveform before the replica test.");
        Check(Int(source, "cycleCount") > 0 && !string.IsNullOrEmpty(Str(source, "activeProgramName")),
            "Source needs a loaded program that has advanced beyond its initial state.");
        Check(sourceProxy.dffSignals != null && sourceProxy.dffSignals.Length > 64,
            "This test requires the expanded CPU's DFF array to exceed the old 64-bit limit.");
        int[] sourceProgramWords = (int[])source.GetProgramVariable("programWords");
        byte[] sourceInitialRam = (byte[])source.GetProgramVariable("initialRam");
        Check(sourceProgramWords != null && sourceProgramWords.Length >= 512
            && sourceInitialRam != null && sourceInitialRam.Length >= 256,
            "Source active program or initial RAM is missing.");

        int sourceCycleBeforeSnapshot = Int(source, "cycleCount");
        byte[] sourceRamBeforeSnapshot = RamBytes(await ReadBytes(
            (RenderTexture)source.GetProgramVariable("currentRam"), "source RAM before snapshot"));
        Check(sourceRamBeforeSnapshot.Any(value => value != 0),
            "Source RAM must contain real nonzero values before the snapshot.");
        Color32[] sourceStateBeforeSnapshot = await ReadBytes(
            (RenderTexture)source.GetProgramVariable("currentState"), "source CPU state before snapshot");
        int nodeCount = Int(source, "nodeCount");
        Check(sourceStateBeforeSnapshot.Length >= nodeCount, "Source state texture is too small.");
        Color[] sourceLiveBeforeSnapshot = await ReadFloats(
            (RenderTexture)source.GetProgramVariable("currentHistory"), "source live waveform");
        Color[] sourceFrozenBeforeSnapshot = await ReadFloats(sourceProxy.frozenHistory, "source frozen waveform");

        int revisionBefore = Int(sourceNetwork, "snapshotRevision");
        sourceNetwork.SendCustomEvent("RequestSnapshot");
        await Until(() => Int(sourceNetwork, "snapshotRevision") > revisionBefore
            && Int(sourceNetwork, "snapshotCaptureStage") == 0 && !Bool(sourceNetwork, "restorePending"),
            "Actual source GPU snapshot did not complete.", 40);
        Check(Int(source, "cycleCount") == sourceCycleBeforeSnapshot,
            "Source advanced while the stable snapshot was captured.");

        int[] snapshotDffBits = (int[])sourceNetwork.GetProgramVariable("snapshotDffBits");
        int[] snapshotProgramWords = (int[])sourceNetwork.GetProgramVariable("snapshotProgramWords");
        byte[] snapshotInitialRam = (byte[])sourceNetwork.GetProgramVariable("snapshotInitialRam");
        byte[] snapshotRam = (byte[])sourceNetwork.GetProgramVariable("snapshotRam");
        Check(snapshotDffBits != null && snapshotDffBits.Length == (sourceProxy.dffSignals.Length + 31) / 32,
            "Snapshot DFF packing was truncated.");
        Check(snapshotProgramWords != null && snapshotProgramWords.Length == 512
            && snapshotInitialRam != null && snapshotInitialRam.Length == 256
            && snapshotRam != null && snapshotRam.Length == 256,
            "Snapshot program or RAM dimensions are incomplete.");
        Check(snapshotProgramWords.SequenceEqual((int[])source.GetProgramVariable("programWords")),
            "Snapshot ROM differs from the active source program.");
        Check(snapshotInitialRam.SequenceEqual((byte[])source.GetProgramVariable("initialRam")),
            "Snapshot initial RAM differs from the active source program.");
        Check(snapshotRam.SequenceEqual(sourceRamBeforeSnapshot),
            "Snapshot RAM differs from actual source GPU RAM.");
        CheckDffSnapshot(sourceStateBeforeSnapshot, nodeCount, sourceProxy.dffSignals, snapshotDffBits, "source capture");

        int expectedCycle = Int(sourceNetwork, "snapshotCycle");
        int expectedSamples = Int(sourceNetwork, "snapshotSamples");
        int frozenSamples = Int(sourceNetwork, "snapshotFrozenSamples");
        int expectedProgram = Int(sourceNetwork, "snapshotProgram");
        int expectedProgramRevision = Int(sourceNetwork, "snapshotProgramRevision");
        string expectedProgramName = Str(sourceNetwork, "snapshotProgramName");
        string expectedTerminal = Str(sourceNetwork, "snapshotTerminal");
        int expectedTerminalEvents = Int(sourceNetwork, "snapshotTerminalEvents");
        int expectedTerminalColumn = Int(sourceNetwork, "snapshotTerminalColumn");
        int expectedTerminalLineStart = Int(sourceNetwork, "snapshotTerminalLineStart");

        GameObject testRoot = new GameObject("CircuitLabRamReplicaValidation");
        testRoot.SetActive(false);
        var owned = new System.Collections.Generic.List<Object>();
        try
        {
            CircuitLabSimulator replicaProxy = Object.Instantiate(sourceProxy.gameObject, testRoot.transform)
                .GetComponent<CircuitLabSimulator>();
            CircuitLabNetworkCoordinator replicaNetworkProxy = Object.Instantiate(sourceNetworkProxy.gameObject, testRoot.transform)
                .GetComponent<CircuitLabNetworkCoordinator>();
            CircuitLabTerminal replicaTerminalProxy = Object.Instantiate(sourceTerminalProxy.gameObject, testRoot.transform)
                .GetComponent<CircuitLabTerminal>();
            CircuitLabNetworkClock replicaClockProxy = Object.Instantiate(sourceNetworkProxy.clock.gameObject, testRoot.transform)
                .GetComponent<CircuitLabNetworkClock>();

            replicaProxy.simulationMaterial = (Material)OwnMaterial(sourceProxy.simulationMaterial, "-RamReplica", owned);
            replicaProxy.displayMaterial = (Material)OwnMaterial(sourceProxy.displayMaterial, "-RamReplica", owned);
            replicaProxy.historyMaterial = (Material)OwnMaterial(sourceProxy.historyMaterial, "-RamReplica", owned);
            replicaProxy.waveformMaterial = (Material)OwnMaterial(sourceProxy.waveformMaterial, "-RamReplica", owned);
            replicaProxy.ramMaterial = (Material)OwnMaterial(sourceProxy.ramMaterial, "-RamReplica", owned);
            replicaProxy.displayMaterials = new Material[0];

            replicaProxy.gateTexture = sourceProxy.gateTexture;
            replicaProxy.restoreIndexTexture = sourceProxy.restoreIndexTexture;
            replicaProxy.programTexture = OwnTexture(sourceProxy.programTexture, "-RamReplica", owned);
            replicaProxy.ramInitialTexture = OwnTexture(sourceProxy.ramInitialTexture, "-RamReplica", owned);
            replicaProxy.restoreRamTexture = OwnTexture(sourceProxy.restoreRamTexture, "-RamReplica", owned);
            replicaProxy.restoreStateTexture = OwnTexture(sourceProxy.restoreStateTexture, "-RamReplica", owned);
            replicaProxy.stateA = OwnRenderTexture(sourceProxy.stateA, "-RamReplica", owned);
            replicaProxy.stateB = OwnRenderTexture(sourceProxy.stateB, "-RamReplica", owned);
            replicaProxy.ramA = OwnRenderTexture(sourceProxy.ramA, "-RamReplica", owned);
            replicaProxy.ramB = OwnRenderTexture(sourceProxy.ramB, "-RamReplica", owned);
            replicaProxy.historyA = OwnRenderTexture(sourceProxy.historyA, "-RamReplica", owned);
            replicaProxy.historyB = OwnRenderTexture(sourceProxy.historyB, "-RamReplica", owned);
            replicaProxy.frozenHistory = OwnRenderTexture(sourceProxy.frozenHistory, "-RamReplica", owned);
            replicaProxy.currentState = replicaProxy.stateA;
            replicaProxy.currentRam = replicaProxy.ramA;
            replicaProxy.currentHistory = replicaProxy.historyA;
            CopyActiveProgram(source, replicaProxy);
            replicaProxy.coordinator = replicaNetworkProxy;
            replicaProxy.terminal = replicaTerminalProxy;
            replicaProxy.simHudText = null;
            replicaProxy.waveHudText = null;
            replicaProxy.statusText = null;

            replicaNetworkProxy.simulator = replicaProxy;
            replicaNetworkProxy.terminal = replicaTerminalProxy;
            replicaNetworkProxy.clock = replicaClockProxy;
            replicaNetworkProxy.statusText = null;
            replicaNetworkProxy.requestSlots = new CircuitLabControlRequest[0];
            replicaNetworkProxy.manualTestMode = true;
            replicaNetworkProxy.manualTestAuthority = false;

            replicaTerminalProxy.simulator = replicaProxy;
            replicaTerminalProxy.coordinator = replicaNetworkProxy;
            replicaTerminalProxy.outputText = null;
            replicaTerminalProxy.statusText = null;
            replicaTerminalProxy.captureMaterial = (Material)OwnMaterial(sourceTerminalProxy.captureMaterial, "-RamReplica", owned);
            replicaTerminalProxy.eventA = OwnRenderTexture(sourceTerminalProxy.eventA, "-RamReplica", owned);
            replicaTerminalProxy.eventB = OwnRenderTexture(sourceTerminalProxy.eventB, "-RamReplica", owned);

            replicaClockProxy.coordinator = replicaNetworkProxy;

            UdonSharpBehaviour[] replicaProxies = { replicaProxy, replicaNetworkProxy, replicaTerminalProxy, replicaClockProxy };
            RegisterAndCopyProxy(replicaProxies);
            Check(Bool(Vm(replicaNetworkProxy), "manualTestMode")
                && !Bool(Vm(replicaNetworkProxy), "manualTestAuthority"),
                "Replica must restore locally without claiming real network ownership.");
            testRoot.SetActive(true);

            UdonBehaviour replica = Vm(replicaProxy);
            UdonBehaviour replicaNetwork = Vm(replicaNetworkProxy);
            UdonBehaviour replicaClock = Vm(replicaClockProxy);
            await Until(() => replica.HasDoneStart && Bool(replica, "_initialized"),
                "Independent Udon/GPU replica failed to initialize.", 30);

            CheckIndependentResources(source, replica, sourceProxy, replicaProxy);
            CopySynced(sourceNetworkProxy, sourceNetwork, replicaNetwork);
            CopySynced(sourceNetworkProxy.clock, sourceClock, replicaClock);
            replicaNetwork.SendCustomEvent("ImportSnapshot");
            await Until(() => Bool(replicaNetwork, "ready") && !Bool(replicaNetwork, "restorePending")
                && Int(replicaNetwork, "appliedSnapshotRevision") == Int(sourceNetwork, "snapshotRevision"),
                "Independent Udon/GPU snapshot restore did not finish.", 45);

            Color32[] restoredState = await ReadBytes((RenderTexture)replica.GetProgramVariable("currentState"), "restored CPU state");
            byte[] restoredRam = RamBytes(await ReadBytes((RenderTexture)replica.GetProgramVariable("currentRam"), "restored RAM"));
            int[] restoredProgram = (int[])replica.GetProgramVariable("programWords");
            byte[] restoredInitialRam = (byte[])replica.GetProgramVariable("initialRam");
            int[] replicaDffSignals = replicaProxy.dffSignals;
            Check(replicaDffSignals != null && replicaDffSignals.Length == sourceProxy.dffSignals.Length,
                "Replica DFF layout differs from the source.");
            CheckDffSnapshot(restoredState, nodeCount, replicaDffSignals, snapshotDffBits, "replica restore");
            Check(sourceStateBeforeSnapshot.Take(nodeCount).SequenceEqual(restoredState.Take(nodeCount)),
                "Restored GPU netlist differs from the actual source snapshot.");
            Check(sourceRamBeforeSnapshot.SequenceEqual(restoredRam), "Restored 256-byte RAM differs from source GPU RAM.");
            Check(snapshotProgramWords.SequenceEqual(restoredProgram) && snapshotInitialRam.SequenceEqual(restoredInitialRam),
                "Restored active 512-word program or initial RAM differs from the captured snapshot.");
            Check(Int(replica, "programSelect") == expectedProgram
                && Int(replica, "programRevision") == expectedProgramRevision
                && Str(replica, "activeProgramName") == expectedProgramName,
                "Restored active program metadata differs.");
            Check(Int(replica, "cycleCount") == expectedCycle && Int(replica, "sampleCount") == expectedSamples,
                "Restored CPU cycle/history boundary differs.");
            Check(Bool(replica, "historyFrozen") && Int(replica, "frozenSampleCount") == frozenSamples,
                "Frozen waveform state was not restored.");

            int[] liveSegments = (int[])sourceNetwork.GetProgramVariable("snapshotSegments");
            int[] liveBinds = (int[])sourceNetwork.GetProgramVariable("snapshotBindSamples");
            int[] frozenSegments = (int[])sourceNetwork.GetProgramVariable("snapshotFrozenSegments");
            int[] frozenBinds = (int[])sourceNetwork.GetProgramVariable("snapshotFrozenBindSamples");
            int liveCompared = CompareVisibleHistory(sourceLiveBeforeSnapshot,
                await ReadFloats((RenderTexture)replica.GetProgramVariable("currentHistory"), "restored live waveform"),
                expectedSamples, sourceProxy.historyCapacity, liveSegments, liveBinds, "live waveform");
            int frozenCompared = CompareVisibleHistory(sourceFrozenBeforeSnapshot,
                await ReadFloats(replicaProxy.frozenHistory, "restored frozen waveform"),
                frozenSamples, sourceProxy.historyCapacity, frozenSegments, frozenBinds, "frozen waveform");
            Check(liveCompared > 0 && frozenCompared > 0,
                "Snapshot needs real valid live and frozen waveform samples.");

            UdonBehaviour replicaTerminal = Vm(replicaTerminalProxy);
            await Until(() => Str(replicaTerminal, "output") == expectedTerminal
                && Int(replicaTerminal, "eventCount") == expectedTerminalEvents,
                "Terminal output/event count were not restored.", 20);
            Check(Int(replicaTerminal, "lastProcessedCycle") == expectedCycle,
                "Terminal cycle boundary differs after restore.");
            Check(Int(replicaTerminal, "cursorColumn") == expectedTerminalColumn
                && Int(replicaTerminal, "lineStart") == expectedTerminalLineStart,
                "Terminal cursor/line position differs after restore.");

            await StepSource(source, 16);
            await Until(() => Int(sourceClock, "cycle") == Int(source, "cycleCount"),
                "Source clock did not publish its 16-cycle advance.", 20);
            CopySynced(sourceNetworkProxy.clock, sourceClock, replicaClock);
            await Until(() => Int(replica, "cycleCount") == Int(source, "cycleCount")
                && !Bool(replicaNetwork, "restorePending"),
                "Replica failed to catch up using local GPU cycles.", 35);

            Color32[] sourceStateAfter = await ReadBytes((RenderTexture)source.GetProgramVariable("currentState"), "source state after catch-up");
            Color32[] replicaStateAfter = await ReadBytes((RenderTexture)replica.GetProgramVariable("currentState"), "replica state after catch-up");
            byte[] sourceRamAfter = RamBytes(await ReadBytes((RenderTexture)source.GetProgramVariable("currentRam"), "source RAM after catch-up"));
            byte[] replicaRamAfter = RamBytes(await ReadBytes((RenderTexture)replica.GetProgramVariable("currentRam"), "replica RAM after catch-up"));
            Check(sourceStateAfter.Take(nodeCount).SequenceEqual(replicaStateAfter.Take(nodeCount)),
                "Source and replica CPU/GPU states diverged after sixteen cycles.");
            Check(sourceRamAfter.SequenceEqual(replicaRamAfter),
                "Source and replica RAM diverged after sixteen cycles.");
            Check(Int(replica, "cycleCount") == expectedCycle + 16,
                "Replica catch-up did not execute exactly sixteen cycles.");
            CheckIndependentResources(source, replica, sourceProxy, replicaProxy);

            return Save(new
            {
                passed = true,
                setup = "source paused with loaded program, nonzero actual RAM, and frozen live/frozen waveforms",
                independentGpuBuffersAndMutableTextures = true,
                staticGateAndRestoreIndexTexturesShared = true,
                netlistNodesCompared = nodeCount,
                dffBitsCompared = sourceProxy.dffSignals.Length,
                dffWordsCopied = snapshotDffBits.Length,
                activeProgramWordsCompared = restoredProgram.Length,
                ramBytesCompared = restoredRam.Length,
                ramChangedDuringCatchup = !sourceRamBeforeSnapshot.SequenceEqual(sourceRamAfter),
                sourceCycle = expectedCycle,
                catchupCycles = 16,
                liveHistorySamplesCompared = liveCompared,
                frozenHistorySamplesCompared = frozenCompared,
                terminalEvents = expectedTerminalEvents,
                programName = expectedProgramName,
                transport = "Synced arrays copied locally between independent Udon/GPU instances in one Editor; real multiplayer serialization and ownership transfer are not exercised."
            });
        }
        finally
        {
            Object.Destroy(testRoot);
            foreach (Object value in owned)
            {
                if (value is RenderTexture) ((RenderTexture)value).Release();
                Object.Destroy(value);
            }
            await Task.Delay(100);
        }
    }
}
