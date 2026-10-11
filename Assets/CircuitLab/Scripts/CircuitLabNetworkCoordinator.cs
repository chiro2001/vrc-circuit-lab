using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabNetworkCoordinator : UdonSharpBehaviour
{
    public CircuitLabSimulator simulator;
    public CircuitLabNetworkClock clock;
    public CircuitLabTerminal terminal;
    public CircuitLabControlRequest[] requestSlots;
    public Text statusText;
    public float snapshotInterval = 8f;
    public bool manualTestMode;
    public bool manualTestAuthority;
    [UdonSynced] public int controlAuthorityId = -1;
    [UdonSynced] public int snapshotRevision;
    [UdonSynced] public int snapshotEpoch;
    [UdonSynced] public int snapshotCycle;
    [UdonSynced] public int snapshotSamples;
    [UdonSynced] public int snapshotInputMask;
    [UdonSynced] public int snapshotProgram;
    [UdonSynced] public bool snapshotRunning;
    [UdonSynced] public bool snapshotFrozen;
    [UdonSynced] public int snapshotFrozenSamples;
    [UdonSynced] public int[] snapshotDffBits = new int[2];
    [UdonSynced] public int[] snapshotProbeIds = new int[64];
    [UdonSynced] public int[] snapshotWidths = new int[4];
    [UdonSynced] public int[] snapshotSegments = new int[4];
    [UdonSynced] public int[] snapshotBindSamples = new int[4];
    [UdonSynced] public string[] snapshotLabels = new string[4];
    [UdonSynced] public int[] snapshotFrozenWidths = new int[4];
    [UdonSynced] public int[] snapshotFrozenSegments = new int[4];
    [UdonSynced] public int[] snapshotFrozenBindSamples = new int[4];
    [UdonSynced] public string[] snapshotFrozenLabels = new string[4];
    [UdonSynced] public byte[] snapshotHistory = new byte[0];
    [UdonSynced] public byte[] snapshotFrozenHistory = new byte[0];
    [UdonSynced] public string snapshotTerminal = "";
    [UdonSynced] public int snapshotTerminalEvents;
    [UdonSynced] public int snapshotTerminalColumn;
    [UdonSynced] public int snapshotTerminalLineStart;
    public int appliedSnapshotRevision;
    public int snapshotCaptureStage;
    public bool restorePending;
    public bool ready;
    public int pendingRequesterId = -1;
    public int deniedCommands;
    public int snapshotErrors;
    public int snapshotBytes;
    public int restoreProgress;
    public int sendingSnapshotRevision = -1;
    public int sendingSnapshotEpoch = -1;
    public int acknowledgedSnapshotRevision = -1;
    public int staleSnapshotAcknowledgements;
    private bool _snapshotRequested = true;
    private bool _releaseRequested;
    private int _handoffId = -1;
    private bool _handoffSerialized;
    private bool _readbackPending;
    private Color32[] _statePixels;
    private Color[] _historyPixels;
    private float _nextSnapshotAt;
    private bool _restoreStarted;
    private bool _serializationPending;
    private int _pendingSnapshotRevision = -1;
    private int _pendingSnapshotEpoch = -1;
    private int _handoffSnapshotRevision = -1;
    private int _handoffSnapshotEpoch = -1;
    private float _nextRequestAt;
    private string _cachedTerminal;
    private int _cachedTerminalEvents;
    private int _cachedTerminalColumn;
    private int _cachedTerminalLineStart;
    private int _cachedCycle;
    private int _localEpoch;
    private int _takeoverTarget;
    private bool _takeoverTest;
    private bool _takeoverCatchup;

    public bool HasControl()
    {
        if (manualTestMode) return manualTestAuthority;
        if (Networking.LocalPlayer == null) return true;
        return Networking.IsOwner(gameObject) && controlAuthorityId == Networking.LocalPlayer.playerId;
    }

    public bool SubmissionBlocked()
    {
        return snapshotCaptureStage != 0 || _snapshotRequested || _serializationPending || restorePending || !ready;
    }

    // An idle experiment may be claimed by the first control click. An active
    // controller still uses the explicit request/grant handoff protocol.
    public bool CanUseControls()
    {
        if (manualTestMode) return manualTestAuthority;
        return HasControl() || controlAuthorityId < 0;
    }

    public bool TryAcquireUnclaimedControl()
    {
        if (HasControl()) return true;
        if (manualTestMode || controlAuthorityId >= 0) return false;
        RequestControl();
        return HasControl();
    }

    public void RequestControl()
    {
        if (manualTestMode) { manualTestAuthority = true; ready = snapshotRevision == 0 || appliedSnapshotRevision == snapshotRevision; return; }
        if (Networking.LocalPlayer == null) { ready = true; return; }
        if (controlAuthorityId < 0)
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            if (clock != null) Networking.SetOwner(Networking.LocalPlayer, clock.gameObject);
            controlAuthorityId = Networking.LocalPlayer.playerId;
            ClearLocalRequests();
            ready = snapshotRevision == 0;
            if (!ready) ImportSnapshot();
            else _snapshotRequested = true;
            RequestSerialization();
            return;
        }
        if (HasControl()) return;
        if (requestSlots == null) return;
        for (int i = 0; i < requestSlots.Length; i++)
        {
            if (requestSlots[i] == null) continue;
            if (requestSlots[i].requesterId == Networking.LocalPlayer.playerId) return;
            if (requestSlots[i].requesterId >= 0 && VRCPlayerApi.GetPlayerById(requestSlots[i].requesterId) != null) continue;
            requestSlots[i].Claim();
            return;
        }
    }

    public void RefreshControlRequests()
    {
        pendingRequesterId = -1;
        double oldest = double.MaxValue;
        if (requestSlots == null) return;
        for (int i = 0; i < requestSlots.Length; i++)
        {
            CircuitLabControlRequest slot = requestSlots[i];
            if (slot == null || slot.requesterId < 0 || slot.requesterId == controlAuthorityId || VRCPlayerApi.GetPlayerById(slot.requesterId) == null) continue;
            if (slot.requestedAt < oldest) { oldest = slot.requestedAt; pendingRequesterId = slot.requesterId; }
        }
    }

    private void ClearLocalRequests()
    {
        if (Networking.LocalPlayer == null || requestSlots == null) return;
        for (int i = 0; i < requestSlots.Length; i++)
            if (requestSlots[i] != null && requestSlots[i].requesterId == Networking.LocalPlayer.playerId) requestSlots[i].ClearRequest();
    }

    public void GrantControl()
    {
        if (!HasControl()) { deniedCommands++; return; }
        RefreshControlRequests();
        if (pendingRequesterId < 0) return;
        _handoffId = pendingRequesterId;
        _snapshotRequested = true;
    }

    public void ReleaseControl()
    {
        if (!HasControl()) { deniedCommands++; return; }
        _releaseRequested = true;
        _snapshotRequested = true;
    }

    public void RequestSnapshot()
    {
        if (manualTestMode || Networking.LocalPlayer == null || Networking.IsOwner(gameObject)) _snapshotRequested = true;
        else if (Time.realtimeSinceStartup >= _nextRequestAt)
        {
            _nextRequestAt = Time.realtimeSinceStartup + 5f;
            SendCustomNetworkEvent(NetworkEventTarget.Owner, "RequestSnapshot");
        }
    }

    public void ExperimentChanged()
    {
        if (!HasControl()) return;
        _localEpoch = Mathf.Max(_localEpoch, clock == null ? 0 : clock.epoch) + 1;
        _snapshotRequested = true;
    }

    public void PublishClock()
    {
        if (!HasControl() || clock == null || simulator == null) return;
        if (_snapshotRequested || snapshotCaptureStage != 0 || _serializationPending) return;
        clock.epoch = _localEpoch;
        simulator.sharedEpoch = _localEpoch;
        clock.cycle = simulator.cycleCount;
        clock.samples = simulator.sampleCount;
        clock.inputMask = simulator.lastSubmittedInputMask;
        clock.running = simulator.running;
        clock.selfTest = simulator.testRunning;
        clock.window = simulator.waveWindowSamples;
        clock.offset = simulator.waveOffset;
        clock.cursorA = simulator.cursorA;
        clock.cursorB = simulator.cursorB;
        clock.bitView = simulator.bitViewChannel;
        clock.numberFormat = simulator.numberFormat;
        clock.followLatest = simulator.followLatest;
        clock.analyticAA = simulator.analyticAA;
        clock.edgeWidth = simulator.waveEdgeWidth;
        clock.speed = simulator.cyclesPerSecond;
    }

    private void Update()
    {
        if (simulator == null || simulator.currentState == null) return;
        RecoverMissingController();
        if (!ready && snapshotRevision == 0 && HasControl()) ready = true;
        if (restorePending)
        {
            if (!_restoreStarted) { simulator.BeginSharedSnapshot(); _restoreStarted = true; }
            if (simulator.RestoreSharedHistoryBatch())
            {
                restorePending = false;
                _restoreStarted = false;
                appliedSnapshotRevision = simulator.sharedRestoreRevision;
                _localEpoch = simulator.sharedEpoch;
                ready = true;
                if (terminal != null)
                {
                    terminal.ApplySharedOutput(_cachedTerminal, _cachedTerminalEvents, _cachedCycle);
                    terminal.lineStart = Mathf.Clamp(_cachedTerminalLineStart, 0, terminal.output.Length);
                    terminal.cursorColumn = Mathf.Clamp(_cachedTerminalColumn, 0, terminal.output.Length - terminal.lineStart);
                }
                if (HasControl() && _takeoverTarget > simulator.cycleCount)
                { ready = false; _takeoverCatchup = true; }
            }
            restoreProgress = simulator.sharedRestoreProgress;
        }
        if (_takeoverCatchup)
        {
            if (simulator.CatchUpSharedClock(_takeoverTarget, _takeoverTest)) { _takeoverCatchup = false; ready = true; }
        }
        if (HasControl() && ready && !restorePending)
        {
            PublishClock();
            if (!_serializationPending && snapshotCaptureStage == 0 && (_snapshotRequested || Time.realtimeSinceStartup >= _nextSnapshotAt)) BeginCapture();
            else if (!_readbackPending && snapshotCaptureStage == 2) RequestHistory(false);
            else if (!_readbackPending && snapshotCaptureStage == 3) RequestHistory(true);
            else if (snapshotCaptureStage == 4 && (terminal == null || terminal.lastProcessedCycle >= snapshotCycle)) FinishCapture();
        }
        if (statusText != null)
        {
            VRCPlayerApi owner = controlAuthorityId < 0 ? null : VRCPlayerApi.GetPlayerById(controlAuthorityId);
            string name = HasControl() ? "YOU CONTROL" : owner == null ? "UNCLAIMED" : owner.displayName;
            string hint = HasControl() ? "" : controlAuthorityId < 0 ? " | CLICK A CONTROL TO BEGIN" : " | REQUEST CONTROL TO OPERATE";
            statusText.text = "CONTROL: " + name + hint + (pendingRequesterId < 0 ? "" : " | REQUEST " + pendingRequesterId)
                + (restorePending ? " | RESTORING " + restoreProgress : snapshotCaptureStage != 0 ? " | SYNCING" : "");
        }
    }

    private void StopUnclaimedClock()
    {
        simulator.running = false;
        simulator.testRunning = false;
        if (clock == null) return;
        if (!manualTestMode && Networking.LocalPlayer != null && !Networking.IsOwner(clock.gameObject))
            Networking.SetOwner(Networking.LocalPlayer, clock.gameObject);
        clock.running = false;
        clock.selfTest = false;
    }

    private void RecoverMissingController()
    {
        if (manualTestMode || Networking.LocalPlayer == null || !Networking.IsOwner(gameObject) || controlAuthorityId < 0
            || controlAuthorityId == Networking.LocalPlayer.playerId || Utilities.IsValid(VRCPlayerApi.GetPlayerById(controlAuthorityId))) return;
        controlAuthorityId = -1;
        StopUnclaimedClock();
        _snapshotRequested = false;
        snapshotCaptureStage = 0;
        ready = false;
        if (snapshotRevision > 0) ImportSnapshot();
        RequestSerialization();
    }

    private void BeginCapture()
    {
        if (simulator.readbackPending) return;
        if (simulator.dffSignals == null || simulator.dffSignals.Length > 64 || simulator.restoreIndexTexture == null)
        { snapshotErrors++; _snapshotRequested = false; _nextSnapshotAt = Time.realtimeSinceStartup + 8f; return; }
        _snapshotRequested = false;
        snapshotCaptureStage = 1;
        snapshotEpoch = _localEpoch;
        snapshotCycle = simulator.cycleCount;
        snapshotSamples = simulator.sampleCount;
        snapshotInputMask = simulator.currentInputMask;
        snapshotProgram = simulator.programSelect;
        snapshotRunning = simulator.running;
        snapshotFrozen = simulator.historyFrozen;
        snapshotFrozenSamples = simulator.frozenSampleCount;
        simulator.CopySnapshotMetadata();
        snapshotHistory = new byte[simulator.historyCapacity * 9];
        snapshotFrozenHistory = snapshotFrozen ? new byte[simulator.historyCapacity * 9] : new byte[0];
        _statePixels = new Color32[simulator.textureWidth];
        _historyPixels = new Color[simulator.historyCapacity * 4];
        _readbackPending = true;
        VRCAsyncGPUReadback.Request(simulator.currentState, 0, this);
    }

    private void RequestHistory(bool frozen)
    {
        _readbackPending = true;
        VRCAsyncGPUReadback.Request(frozen ? simulator.frozenHistory : simulator.currentHistory, 0, this);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        _readbackPending = false;
        if (snapshotCaptureStage == 0 || restorePending || !HasControl()) return;
        bool success = !request.hasError;
        if (snapshotCaptureStage == 1)
        {
            success = success && request.TryGetData(_statePixels);
            if (success)
            {
                snapshotDffBits[0] = 0; snapshotDffBits[1] = 0;
                for (int i = 0; i < simulator.dffSignals.Length; i++)
                    if (_statePixels[simulator.dffSignals[i]].r >= 128) snapshotDffBits[i / 32] |= 1 << (i % 32);
                snapshotCaptureStage = 2;
            }
        }
        else
        {
            success = success && request.TryGetData(_historyPixels);
            if (success)
            {
                EncodeHistory(snapshotCaptureStage == 3);
                if (snapshotCaptureStage == 2 && snapshotFrozen) snapshotCaptureStage = 3;
                else FinishCapture();
            }
        }
        if (!success) { snapshotErrors++; snapshotCaptureStage = 0; _nextSnapshotAt = Time.realtimeSinceStartup + 1f; }
    }

    private void EncodeHistory(bool frozen)
    {
        byte[] bytes = frozen ? snapshotFrozenHistory : snapshotHistory;
        int count = frozen ? snapshotFrozenSamples : snapshotSamples;
        int[] segments = frozen ? snapshotFrozenSegments : snapshotSegments;
        int capacity = simulator.historyCapacity;
        for (int slot = 0; slot < capacity; slot++)
        {
            int valid = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                Color record = _historyPixels[channel * capacity + slot];
                int index = Mathf.RoundToInt(record.a) - 1;
                if (record.g < 0.5f || Mathf.RoundToInt(record.b) != segments[channel] || index < Mathf.Max(0, count - capacity) || index >= count || index % capacity != slot) continue;
                int value = Mathf.RoundToInt(record.r);
                bytes[slot * 9 + channel * 2] = (byte)(value & 255);
                bytes[slot * 9 + channel * 2 + 1] = (byte)((value >> 8) & 255);
                valid |= 1 << channel;
            }
            bytes[slot * 9 + 8] = (byte)valid;
        }
    }

    private void FinishCapture()
    {
        if (terminal != null && terminal.lastProcessedCycle < snapshotCycle) { snapshotCaptureStage = 4; return; }
        snapshotTerminal = terminal == null ? "" : terminal.output;
        snapshotTerminalEvents = terminal == null ? 0 : terminal.eventCount;
        snapshotTerminalColumn = terminal == null ? 0 : terminal.cursorColumn;
        snapshotTerminalLineStart = terminal == null ? 0 : terminal.lineStart;
        snapshotRevision++;
        appliedSnapshotRevision = snapshotRevision;
        snapshotCaptureStage = 0;
        snapshotBytes = snapshotHistory.Length + snapshotFrozenHistory.Length;
        _nextSnapshotAt = Time.realtimeSinceStartup + Mathf.Max(snapshotInterval, snapshotBytes / 2500f);
        if (_handoffId >= 0 || _releaseRequested)
        {
            if (_releaseRequested)
            {
                snapshotRunning = false;
                StopUnclaimedClock();
            }
            controlAuthorityId = _releaseRequested ? -1 : _handoffId;
            _handoffSerialized = true;
            _handoffSnapshotRevision = snapshotRevision;
            _handoffSnapshotEpoch = snapshotEpoch;
        }
        if (!manualTestMode)
        {
            _pendingSnapshotRevision = snapshotRevision;
            _pendingSnapshotEpoch = snapshotEpoch;
            _serializationPending = true;
            RequestSerialization();
        }
        else PublishClock();
        if (manualTestMode && _releaseRequested) manualTestAuthority = false;
    }

    public void ImportSnapshot()
    {
        if (snapshotRevision <= 0 || simulator == null || snapshotDffBits.Length < 2 || snapshotHistory.Length != simulator.historyCapacity * 9
            || (snapshotFrozen && snapshotFrozenHistory.Length != simulator.historyCapacity * 9))
        { snapshotErrors++; return; }
        if (restorePending) { snapshotErrors++; return; }
        restorePending = true;
        ready = false;
        _restoreStarted = false;
        _snapshotRequested = false;
        simulator.CacheSharedSnapshot();
        _cachedTerminal = snapshotTerminal; _cachedTerminalEvents = snapshotTerminalEvents; _cachedCycle = snapshotCycle;
        _cachedTerminalColumn = snapshotTerminalColumn; _cachedTerminalLineStart = snapshotTerminalLineStart;
        _takeoverTarget = clock != null && clock.epoch == snapshotEpoch ? Mathf.Max(snapshotCycle, clock.cycle) : snapshotCycle;
        _takeoverTest = clock != null && clock.selfTest;
    }

    public override void OnDeserialization()
    {
        RefreshControlRequests();
        if (!ready || appliedSnapshotRevision == 0 || (clock != null && (snapshotEpoch != simulator.sharedEpoch || clock.cycle - simulator.cycleCount > simulator.historyCapacity / 2))) ImportSnapshot();
    }

    public override void OnPreSerialization()
    {
        // Cache the payload actually entering the SDK encoder, not the request's time.
        sendingSnapshotRevision = snapshotRevision;
        sendingSnapshotEpoch = snapshotEpoch;
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (!result.success) { if (_serializationPending) RequestSerialization(); return; }
        if (!_serializationPending) return;
        if (sendingSnapshotRevision != _pendingSnapshotRevision || sendingSnapshotEpoch != _pendingSnapshotEpoch)
        {
            staleSnapshotAcknowledgements++;
            RequestSerialization();
            return;
        }
        if (_handoffSerialized && (sendingSnapshotRevision != _handoffSnapshotRevision || sendingSnapshotEpoch != _handoffSnapshotEpoch))
        {
            staleSnapshotAcknowledgements++;
            RequestSerialization();
            return;
        }
        _serializationPending = false;
        acknowledgedSnapshotRevision = sendingSnapshotRevision;
        PublishClock();
        if (!_handoffSerialized) return;
        _handoffSerialized = false;
        if (_handoffId >= 0)
        {
            VRCPlayerApi player = VRCPlayerApi.GetPlayerById(_handoffId);
            if (player != null) { Networking.SetOwner(player, gameObject); if (clock != null) Networking.SetOwner(player, clock.gameObject); }
        }
        _handoffId = -1;
        _releaseRequested = false;
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        _serializationPending = false; _snapshotRequested = false; _handoffSerialized = false;
        _pendingSnapshotRevision = -1; _pendingSnapshotEpoch = -1;
        _handoffSnapshotRevision = -1; _handoffSnapshotEpoch = -1;
        sendingSnapshotRevision = -1; sendingSnapshotEpoch = -1;
        _handoffId = -1; _releaseRequested = false; snapshotCaptureStage = 0;
        if (player == null || !player.isLocal || snapshotRevision == 0) return;
        ClearLocalRequests();
        ImportSnapshot();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (player != null && player.playerId == controlAuthorityId && Networking.IsOwner(gameObject))
        {
            controlAuthorityId = -1;
            StopUnclaimedClock();
            ready = false;
            if (snapshotRevision > 0) ImportSnapshot();
            RequestSerialization();
        }
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (HasControl()) _snapshotRequested = true;
    }
}
