using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.Udon;
using Object = UnityEngine.Object;

// Pinpoints why the localized (TMP) text overlays stop following their source
// Text components: reads the localization program's own variables instead of
// guessing from the visible result.
public static class DiagnoseCircuitLabUi
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private const string ReportDir = "Reports/CircuitLab";

    private static object Save(string name, object report)
    {
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, name + ".json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }

    private static CircuitLabLocalization Localization() =>
        Object.FindObjectsOfType<CircuitLabLocalization>(true).First();

    private static UdonBehaviour LocalizationVm() =>
        UdonSharpEditorUtility.GetBackingUdonBehaviour(Localization());

    private static UdonBehaviour SimVm(params string[] args) =>
        UdonSharpEditorUtility.GetBackingUdonBehaviour(Object.FindObjectsOfType<CircuitLabSimulator>(true).First());

    private static string OverlayText(Text source, string displayName)
    {
        Transform child = source.transform.Find(displayName);
        if (child == null) return "<missing overlay>";
        TextMeshProUGUI tmp = child.GetComponent<TextMeshProUGUI>();
        if (tmp == null) return "<no TMP component>";
        return (tmp.gameObject.activeInHierarchy ? "ON  " : "OFF ") + tmp.text;
    }

    // Reports one dynamic text (the simulator HUD) before and after a second, next
    // to the localization bookkeeping that is supposed to drive it.
    public static async Task<object> TextFlow()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabLocalization localization = Localization();
        UdonBehaviour vm = LocalizationVm();
        CircuitLabSimulator simulator = Object.FindObjectsOfType<CircuitLabSimulator>(true).First();
        UdonBehaviour sim = SimVm();

        Text hud = (Text)sim.GetProgramVariable("simHudText");
        Text status = (Text)sim.GetProgramVariable("statusText");
        Text wave = (Text)sim.GetProgramVariable("waveHudText");

        var indices = (int[])vm.GetProgramVariable("dynamicIndices");
        var sources = (Text[])vm.GetProgramVariable("sourceTexts");
        var lastText = (string[])vm.GetProgramVariable("_lastSourceText");
        int[] tracked = indices ?? new int[0];

        Func<Text, int, JObject> describe = (text, index) => new JObject
        {
            ["index"] = index,
            ["name"] = text == null ? "null" : text.gameObject.name,
            ["componentEnabled"] = text != null && text.enabled,
            ["activeInHierarchy"] = text != null && text.gameObject.activeInHierarchy,
            ["inDynamicSubset"] = text != null && Array.IndexOf(tracked, Array.IndexOf(sources, text)) >= 0,
            ["sourceText"] = text == null ? "" : text.text,
            ["overlay"] = text == null ? "" : OverlayText(text, "LocalizedTMP_EN")
        };

        JObject before = new JObject
        {
            ["hud"] = describe(hud, Array.IndexOf(sources, hud)),
            ["status"] = describe(status, Array.IndexOf(sources, status)),
            ["wave"] = describe(wave, Array.IndexOf(sources, wave))
        };
        string hudBefore = hud == null ? "" : hud.text;
        string overlayBefore = hud == null ? "" : OverlayText(hud, "LocalizedTMP_EN");
        await Task.Delay(1500);
        string hudAfter = hud == null ? "" : hud.text;
        string overlayAfter = hud == null ? "" : OverlayText(hud, "LocalizedTMP_EN");

        return Save("diag-text-flow", new
        {
            passed = true,
            sourceCount = sources == null ? 0 : sources.Length,
            dynamicCount = tracked.Length,
            slowScanSize = Convert.ToInt32(vm.GetProgramVariable("slowScanSize")),
            lastLanguage = Convert.ToInt32(vm.GetProgramVariable("_lastLanguage")),
            languageIndex = Convert.ToInt32(vm.GetProgramVariable("languageIndex")),
            componentEnabled = localization.enabled,
            privateNextTextSync = Convert.ToSingle(vm.GetProgramVariable("_nextTextSync")),
            slowCursor = Convert.ToInt32(vm.GetProgramVariable("_slowCursor")),
            lastSourceTextLength = lastText == null ? -1 : lastText.Length,
            indicesInRange = tracked.Count(index => index >= 0 && sources != null && index < sources.Length),
            indicesOutOfRange = tracked.Count(index => index < 0 || sources == null || index >= sources.Length),
            hudSourceChangedOver1s = hudBefore != hudAfter,
            hudOverlayChangedOver1s = overlayBefore != overlayAfter,
            before,
            after = new JObject
            {
                ["hud"] = describe(hud, Array.IndexOf(sources, hud)),
                ["status"] = describe(status, Array.IndexOf(sources, status)),
                ["wave"] = describe(wave, Array.IndexOf(sources, wave))
            },
            samples = new JArray
            {
                new JObject { ["when"] = "before", ["source"] = hudBefore, ["overlay"] = overlayBefore },
                new JObject { ["when"] = "after", ["source"] = hudAfter, ["overlay"] = overlayAfter }
            }
        });
    }

    // Reproduces the "everything stops updating after heavy load" report by running
    // the CPU hard while sampling every subsystem that the labels depend on, so the
    // first thing that stops is visible instead of inferred.
    public static async Task<object> WatchUnderLoad(float targetHz = 8192f, float seconds = 25f)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Object.FindObjectsOfType<CircuitLabSimulator>(true).First();
        UdonBehaviour sim = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        UdonBehaviour coord = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator.coordinator);
        UdonBehaviour terminal = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator.terminal);
        UdonBehaviour localization = LocalizationVm();
        // Control must be taken through the VM: calling the proxy method from Editor
        // code only writes the edit-time copy and leaves the running program without
        // authority, which looks exactly like a frozen world.
        if (Convert.ToInt32(coord.GetProgramVariable("controlAuthorityId")) != LocalPlayerId())
            coord.SendCustomEvent("RequestControl");
        DateTime controlLimit = DateTime.UtcNow.AddSeconds(20);
        while (Convert.ToInt32(coord.GetProgramVariable("controlAuthorityId")) != LocalPlayerId() && DateTime.UtcNow < controlLimit)
            await Task.Delay(80);
        Text hud = (Text)sim.GetProgramVariable("simHudText");

        int slowBefore = Convert.ToInt32(localization.GetProgramVariable("_slowCursor"));
        sim.SetProgramVariable("cyclesPerSecond", targetHz);
        sim.SendCustomEvent("Run");
        var samples = new JArray();
        int lastCycle = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
        int lastSlow = slowBefore;
        float started = Time.realtimeSinceStartup;
        float deadline = started + seconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            await Task.Delay(400);
            int cycle = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
            int slow = Convert.ToInt32(localization.GetProgramVariable("_slowCursor"));
            float now = Time.realtimeSinceStartup;
            samples.Add(new JObject
            {
                ["t"] = now - started,
                ["cycle"] = cycle,
                ["cycleDelta"] = cycle - lastCycle,
                ["slowCursor"] = slow,
                ["slowDelta"] = slow - lastSlow,
                ["localeEnabled"] = localization.enabled,
                ["hud"] = hud == null ? "" : hud.text,
                ["snapshotStage"] = Convert.ToInt32(coord.GetProgramVariable("snapshotCaptureStage")),
                ["submissionBlocked"] = BlockedVm(coord),
                ["blockedByStage"] = Convert.ToInt32(coord.GetProgramVariable("snapshotCaptureStage")) != 0,
                ["blockedBySnapshotRequest"] = Convert.ToBoolean(coord.GetProgramVariable("_snapshotRequested")),
                ["blockedBySerialization"] = Convert.ToBoolean(coord.GetProgramVariable("_serializationPending")),
                ["blockedByRestore"] = Convert.ToBoolean(coord.GetProgramVariable("restorePending")),
                ["coordReady"] = Convert.ToBoolean(coord.GetProgramVariable("ready")),
                ["authority"] = Convert.ToInt32(coord.GetProgramVariable("controlAuthorityId")),
                ["localPlayer"] = LocalPlayerId(),
                ["running"] = Convert.ToBoolean(sim.GetProgramVariable("running")),
                ["deniedCommands"] = Convert.ToInt32(sim.GetProgramVariable("deniedCommands")),
                ["terminalProcessed"] = Convert.ToInt32(terminal.GetProgramVariable("lastProcessedCycle")),
                ["terminalPending"] = Convert.ToBoolean(terminal.GetProgramVariable("readbackPending")),
                ["terminalEvents"] = Convert.ToInt32(terminal.GetProgramVariable("eventCount")),
                ["terminalGaps"] = Convert.ToInt32(terminal.GetProgramVariable("missedCycles")),
                ["terminalErrors"] = Convert.ToInt32(terminal.GetProgramVariable("readbackErrors")),
                ["readbackPending"] = Convert.ToBoolean(sim.GetProgramVariable("readbackPending")),
                ["depth"] = Convert.ToInt32(sim.GetProgramVariable("settleDepthCurrent")),
                ["truncations"] = Convert.ToInt32(sim.GetProgramVariable("settleTruncations")),
                ["fps"] = 0f
            });
            lastCycle = cycle; lastSlow = slow;
        }
        sim.SendCustomEvent("Pause");
        int frozenCycleStreak = 0, worstStreak = 0, frozenLocaleStreak = 0, worstLocaleStreak = 0;
        int previousCycle = -1, previousSlow = -1;
        foreach (JObject sample in samples)
        {
            int cycle = (int)sample["cycle"];
            int slow = (int)sample["slowCursor"];
            if (previousCycle >= 0 && cycle == previousCycle) worstStreak = Math.Max(worstStreak, ++frozenCycleStreak); else frozenCycleStreak = 0;
            if (previousSlow >= 0 && slow == previousSlow) worstLocaleStreak = Math.Max(worstLocaleStreak, ++frozenLocaleStreak); else frozenLocaleStreak = 0;
            previousCycle = cycle; previousSlow = slow;
        }
        return Save("diag-watch-load", new
        {
            passed = worstStreak <= 2 && worstLocaleStreak <= 2,
            targetHz,
            samplesTaken = samples.Count,
            longestFrozenCycleStreak = worstStreak,
            longestFrozenLocaleStreak = worstLocaleStreak,
            samples
        });
    }

    private static bool BlockedVm(UdonBehaviour coord)
    {
        return Convert.ToInt32(coord.GetProgramVariable("snapshotCaptureStage")) != 0
            || Convert.ToBoolean(coord.GetProgramVariable("_snapshotRequested"))
            || Convert.ToBoolean(coord.GetProgramVariable("_serializationPending"))
            || Convert.ToBoolean(coord.GetProgramVariable("restorePending"))
            || !Convert.ToBoolean(coord.GetProgramVariable("ready"));
    }

    private static int LocalPlayerId()
    {
        VRC.SDKBase.VRCPlayerApi player = VRC.SDKBase.Networking.LocalPlayer;
        return player == null ? -1 : player.playerId;
    }

    // Simulates the two handshakes that used to latch forever (a dropped
    // serialization callback and a snapshot stage whose readback never returns) and
    // proves the coordinator now recovers and keeps simulating.
    public static async Task<object> InjectStalls(float settleSeconds = 12f)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Object.FindObjectsOfType<CircuitLabSimulator>(true).First();
        UdonBehaviour sim = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        UdonBehaviour coord = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator.coordinator);
        if (Convert.ToInt32(coord.GetProgramVariable("controlAuthorityId")) != LocalPlayerId())
            coord.SendCustomEvent("RequestControl");
        DateTime limit = DateTime.UtcNow.AddSeconds(20);
        while (Convert.ToInt32(coord.GetProgramVariable("controlAuthorityId")) != LocalPlayerId() && DateTime.UtcNow < limit)
            await Task.Delay(80);

        sim.SetProgramVariable("cyclesPerSecond", 2048f);
        sim.SendCustomEvent("Run");
        await Task.Delay(1200);

        // 1. Stale acknowledgement loop: point the expectation at a revision no
        // acknowledgement can match, which is exactly the state that used to latch
        // _serializationPending forever.
        int serializationTimeoutsBefore = Convert.ToInt32(coord.GetProgramVariable("serializationTimeouts"));
        int staleBefore = Convert.ToInt32(coord.GetProgramVariable("staleSnapshotAcknowledgements"));
        int cycleBefore = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
        coord.SetProgramVariable("_pendingSnapshotRevision", Convert.ToInt32(coord.GetProgramVariable("snapshotRevision")) + 1000);
        coord.SetProgramVariable("_serializationPending", true);
        coord.RequestSerialization();
        float started = Time.realtimeSinceStartup;
        bool serializationRecovered = false;
        while (Time.realtimeSinceStartup - started < settleSeconds)
        {
            await Task.Delay(250);
            if (!Convert.ToBoolean(coord.GetProgramVariable("_serializationPending"))) { serializationRecovered = true; break; }
        }
        int cycleAfterSerialization = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
        int staleAfter = Convert.ToInt32(coord.GetProgramVariable("staleSnapshotAcknowledgements"));

        // 2. Snapshot capture whose readback never completes.
        int captureTimeoutsBefore = Convert.ToInt32(coord.GetProgramVariable("captureTimeouts"));
        coord.SetProgramVariable("snapshotCaptureStage", 3);
        coord.SetProgramVariable("_readbackPending", true);
        started = Time.realtimeSinceStartup;
        bool captureRecovered = false;
        while (Time.realtimeSinceStartup - started < settleSeconds + 4f)
        {
            await Task.Delay(250);
            if (Convert.ToInt32(coord.GetProgramVariable("snapshotCaptureStage")) == 0) { captureRecovered = true; break; }
        }
        await Task.Delay(1500);
        int cycleAfterCapture = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
        sim.SendCustomEvent("Pause");
        return Save("diag-inject-stalls", new
        {
            passed = serializationRecovered && captureRecovered
                && cycleAfterSerialization > cycleBefore && cycleAfterCapture > cycleAfterSerialization,
            serializationRecovered,
            captureRecovered,
            staleAcknowledgementsBefore = staleBefore,
            staleAcknowledgementsAfter = staleAfter,
            stalePathExercised = staleAfter > staleBefore,
            serializationTimeoutsBefore,
            serializationTimeoutsAfter = Convert.ToInt32(coord.GetProgramVariable("serializationTimeouts")),
            captureTimeoutsBefore,
            captureTimeoutsAfter = Convert.ToInt32(coord.GetProgramVariable("captureTimeouts")),
            cyclesDuringSerializationStall = cycleAfterSerialization - cycleBefore,
            cyclesDuringCaptureStall = cycleAfterCapture - cycleAfterSerialization,
            submissionBlockedAtEnd = BlockedVm(coord)
        });
    }

    // Runs the CPU and watches that the status and live-state labels keep following
    // the simulation instead of freezing on their last value.
    public static async Task<object> LiveUpdates()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabSimulator simulator = Object.FindObjectsOfType<CircuitLabSimulator>(true).First();
        UdonBehaviour sim = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        if (!simulator.coordinator.HasControl())
        {
            simulator.coordinator.RequestControl();
            DateTime limit = DateTime.UtcNow.AddSeconds(20);
            while (!simulator.coordinator.HasControl() && DateTime.UtcNow < limit) await Task.Delay(80);
        }
        Text hud = (Text)sim.GetProgramVariable("simHudText");
        CircuitLabStateTablet tablet = Object.FindObjectsOfType<CircuitLabStateTablet>(true).First();
        Text tabletStatus = tablet.memoryStatusText;
        Text tabletPage = tablet.pageText;

        sim.SetProgramVariable("cyclesPerSecond", 512f);
        sim.SendCustomEvent("Run");
        await Task.Delay(2500);
        string hudA = hud == null ? "" : hud.text;
        string overlayA = hud == null ? "" : OverlayText(hud, "LocalizedTMP_EN");
        string tabletA = tabletStatus == null ? "" : tabletStatus.text;
        string pageA = tabletPage == null ? "" : tabletPage.text;
        int cycleA = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
        await Task.Delay(2500);
        string hudB = hud == null ? "" : hud.text;
        string overlayB = hud == null ? "" : OverlayText(hud, "LocalizedTMP_EN");
        string tabletB = tabletStatus == null ? "" : tabletStatus.text;
        string pageB = tabletPage == null ? "" : tabletPage.text;
        int cycleB = Convert.ToInt32(sim.GetProgramVariable("cycleCount"));
        sim.SendCustomEvent("Pause");
        return Save("diag-live-updates", new
        {
            passed = cycleB > cycleA && hudA != hudB && overlayA != overlayB,
            cyclesAdvanced = cycleB - cycleA,
            hudChanged = hudA != hudB,
            hudOverlayChanged = overlayA != overlayB,
            tabletStatusChanged = tabletA != tabletB,
            tabletPageChanged = pageA != pageB,
            hudSamples = new JArray { hudA, hudB },
            hudOverlaySamples = new JArray { overlayA, overlayB },
            tabletSamples = new JArray { tabletA, tabletB },
            pageSamples = new JArray { pageA, pageB }
        });
    }

    // Checks whether the interactive path itself is healthy: control ownership,
    // button interactability, and whether a real program row click changes state.
    public static async Task<object> Interaction()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        CircuitLabProgramBrowser browser = Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First();
        UdonBehaviour vm = UdonSharpEditorUtility.GetBackingUdonBehaviour(browser);
        CircuitLabNetworkCoordinator coordinator = Object.FindObjectsOfType<CircuitLabNetworkCoordinator>(true).First();
        UdonBehaviour coordVm = UdonSharpEditorUtility.GetBackingUdonBehaviour(coordinator);

        var rows = Object.FindObjectsOfType<CircuitLabProgramRow>(true).OrderBy(row => row.programIndex).ToArray();
        CircuitLabProgramRow target = rows.FirstOrDefault(row => row.programIndex == 1) ?? rows.FirstOrDefault();
        int selectedBefore = Convert.ToInt32(vm.GetProgramVariable("selectedProgram"));
        bool interactable = target != null && target.GetComponent<CircuitLabButton>() != null
            && target.GetComponent<CircuitLabButton>().uiButton != null
            && target.GetComponent<CircuitLabButton>().uiButton.interactable;
        if (target != null)
            UnityEngine.EventSystems.ExecuteEvents.Execute(target.gameObject,
                new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left },
                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        await Task.Delay(600);
        int selectedAfter = Convert.ToInt32(vm.GetProgramVariable("selectedProgram"));

        Text browsing = (Text)vm.GetProgramVariable("browsingProgramText");
        Text source = (Text)vm.GetProgramVariable("sourceText");
        return Save("diag-interaction", new
        {
            passed = true,
            controlAuthorityId = Convert.ToInt32(coordVm.GetProgramVariable("controlAuthorityId")),
            ready = Convert.ToBoolean(coordVm.GetProgramVariable("ready")),
            canUseControls = coordinator.CanUseControls(),
            hasControl = coordinator.HasControl(),
            buttonInteractable = interactable,
            selectedBefore,
            selectedAfter,
            selectionChanged = selectedBefore != selectedAfter,
            browsingSource = browsing == null ? "" : browsing.text,
            browsingOverlay = browsing == null ? "" : OverlayText(browsing, "LocalizedTMP_EN"),
            sourceFirstLine = source == null ? "" : (source.text ?? "").Split('\n').FirstOrDefault(),
            sourceOverlayFirstLine = source == null ? "" : OverlayText(source, "LocalizedTMP_EN").Split('\n').FirstOrDefault()
        });
    }
}
