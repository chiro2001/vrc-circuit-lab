using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabSimulator : UdonSharpBehaviour
{
    public const int RamPageSize = 256;
    public const int RamPageCount = 4;
    public const int RamByteCount = RamPageSize * RamPageCount;
    public const int RamTextureWidth = 32;
    public const int RamAddressBits = 10;
    public const int RamProbeCount = RamByteCount * 8;

    public Material simulationMaterial;
    public Material displayMaterial;
    public Material[] displayMaterials;
    public Material historyMaterial;
    public Material waveformMaterial;
    public Material ramMaterial;
    public Texture2D gateTexture;
    public Texture2D programTexture;
    public Texture2D ramInitialTexture;
    public Texture2D restoreRamTexture;
    public Texture2D restoreStateTexture;
    public RenderTexture stateA;
    public RenderTexture stateB;
    public RenderTexture ramA;
    public RenderTexture ramB;
    public RenderTexture currentRam;
    public RenderTexture historyA;
    public RenderTexture historyB;
    public RenderTexture frozenHistory;
    public Text simHudText;
    public Text waveHudText;
    public Text statusText;
    public CircuitLabNetworkCoordinator coordinator;
    public CircuitLabTerminal terminal;
    public Texture2D restoreIndexTexture;
    public int[] dffSignals;
    public bool analyticAA = true;
    public float waveEdgeWidth = 1f;
    public float waveAspect = 3.1f;
    public int currentInputMask;
    public int sharedEpoch;
    public int sharedRestoreProgress;
    public int nodeCount;
    public int textureWidth;
    public int settlePasses;
    public int[] inputMasks;
    public byte[] expectedStates;
    public int vectorCount;
    public int[] accSignals;
    public int[] xSignals;
    public int[] spSignals;
    public int[] pcSignals;
    public int[] outSignals;
    public int[] instructionSignals;
    public int[] qSignals;
    public int phaseSignal = -1;
    public int[] phaseSignals;
    public int zeroSignal = -1;
    public int carrySignal = -1;
    public int haltSignal = -1;
    public int[] probe0Signals;
    public int[] probe1Signals;
    public int[] probe2Signals;
    public int[] probe3Signals;
    public string probe0Label = "CH1";
    public string probe1Label = "CH2";
    public string probe2Label = "CH3";
    public string probe3Label = "CH4";
    public bool runOnStart;
    public float cyclesPerSecond = 2f;
    public float telemetryInterval = 0.5f;
    public int programSelect;
    public int[] romAddressSignals;
    public int[] ramReadAddressSignals;
    public int[] ramReadDataSignals;
    public int[] ramWriteAddressSignals;
    public int[] ramWriteDataSignals;
    public int[] bankSignals;
    public int ramWriteEnableSignal = -1;
    public int[] programWords;
    public byte[] initialRam;
    public int[] presetProgramWords;
    public byte[] presetInitialRam;
    public string[] programNames;
    public string[] programDescriptions;
    public int programCount;
    public string activeProgramName;
    public int programRevision;
    public int[] fixturePrograms;
    public bool[] fixtureRamReset;
    public int historyCapacity = 1024;
    public int waveWindowSamples = 128;
    public int waveOffset;
    public int cursorA;
    public int cursorB = 16;
    public bool followLatest = true;
    public int bitViewChannel = -1;
    public int numberFormat;

    // All telemetry describes actual GPU work. Expected fixtures never drive simulation.
    public int cycleCount;
    public int sampleCount;
    public int historyHead;
    public bool running;
    public bool testRunning;
    public bool readbackPending;
    public bool historyFrozen;
    // 0 paused, 1 running, 2 testing, 3 test passed, 4 test failed, 5 invalid configuration.
    public int statusCode;
    public int completedVectors;
    public int mismatchCount;
    public int observedAcc;
    public int observedPc;
    public int observedOut;
    public int observedInstruction;
    public int observedPhase;
    public int observedZero;
    public int observedCarry;
    public int observedHalt;
    public int observedQ;
    public int observedX;
    public int observedSp;
    public int observedPhaseValue;
    public byte[] observedRam = new byte[RamByteCount];
    public bool observedRamStable;
    public int observedRamCycle;
    public int observedRamReadAddress;
    public int observedRamReadData;
    public int observedRamWriteAddress;
    public int observedRamWriteData;
    public int observedRamWriteEnable;
    public int observedBank;
    public float actualCyclesPerSecond;
    public float catchupLag;
    public int selfTestRunId;
    public int completedSelfTestRunId;
    public int telemetryReadbackCount;
    public int lastReadbackCycle;
    public int lastSelfTestComparedBits;
    public int lastSubmittedInputMask;
    public int queuedCommandCount;
    public int queuedStepCount;
    public int completedCommandCount;
    public int rejectedCommandCount;
    public int lastCommandCode;
    public float elapsedMilliseconds;
    public int[] probeWidths;
    public int[] probeSegments;
    public int[] probeBindSamples;
    public int frozenSampleCount;
    public int waveViewStart;
    public RenderTexture currentState;
    public RenderTexture currentHistory;
    public string[] probeLabels;

    private RenderTexture _readState;
    private RenderTexture _writeState;
    private RenderTexture _readRam;
    private RenderTexture _writeRam;
    private RenderTexture _readHistory;
    private RenderTexture _writeHistory;
    private Color32[] _pixels;
    private Color32[] _ramReadbackPixels;
    private Color32[] _uploadPixels;
    private Color32[] _uploadRamPixels;
    private Color32[] _restorePixels;
    private int[] _commands;
    private int[] _commandProbeIds;
    private int[] _commandProbeWidths;
    private string[] _commandProbeLabels;
    private int[] _loadQueueWords;
    private byte[] _loadQueueRam;
    private string[] _loadQueueNames;
    private int[] _loadQueuePresetIds;
    private int _activeCommandSlot;
    private int _commandHead;
    private int[] _probeIds;
    private string[] _probeLabels;
    private int[] _queuedProbeIds;
    private int[] _queuedProbeWidths;
    private string[] _queuedProbeLabels;
    private int[] _frozenWidths;
    private int[] _frozenSegments;
    private int[] _frozenBindSamples;
    private string[] _frozenLabels;
    private bool _initialized;
    private bool _configurationValid;
    private bool _observedStateAvailable;
    private bool _telemetryRequested;
    private bool _readbackForTest;
    private bool _runAfterTest;
    private bool _placeCursors = true;
    private int _requestedVector;
    private int _requestedCycle;
    private int _fixtureCount;
    private int _loadQueueHead;
    private int _loadQueueCount;
    private int _readbackStage;
    private float _rateWindowStart;
    private int _rateWindowCycle;
    private float _lastFrameAt;
    private float _frameDelta = 0.016f;
    private float _nextCycleAt;
    private float _nextTelemetryAt;
    private float _nextHudAt;
    private float _testStartedAt;
    private int _restorePhase;
    private int _restoreIndex;
    private int[] _sharedInts;
    private string[] _sharedLabels;
    private byte[] _sharedLiveBytes;
    private byte[] _sharedFrozenBytes;
    private int[] _sharedDffBits;
    private int[] _sharedProgramWords;
    private byte[] _sharedInitialRam;
    private byte[] _sharedRam;
    private string _sharedProgramName;
    private int _sharedProgramRevision;
    public int sharedRestoreRevision;

    public void CopySnapshotMetadata()
    {
        if (coordinator == null) return;
        for (int i = 0; i < 64; i++) coordinator.snapshotProbeIds[i] = _probeIds[i];
        for (int i = 0; i < 4; i++)
        {
            coordinator.snapshotWidths[i] = probeWidths[i]; coordinator.snapshotSegments[i] = probeSegments[i];
            coordinator.snapshotBindSamples[i] = probeBindSamples[i]; coordinator.snapshotLabels[i] = _probeLabels[i];
            coordinator.snapshotFrozenWidths[i] = _frozenWidths[i]; coordinator.snapshotFrozenSegments[i] = _frozenSegments[i];
            coordinator.snapshotFrozenBindSamples[i] = _frozenBindSamples[i]; coordinator.snapshotFrozenLabels[i] = _frozenLabels[i];
        }
    }

    public void CacheSharedSnapshot()
    {
        _sharedInts = new int[104]; _sharedLabels = new string[8];
        _sharedDffBits = new int[coordinator.snapshotDffBits.Length];
        for (int i = 0; i < _sharedDffBits.Length; i++) _sharedDffBits[i] = coordinator.snapshotDffBits[i];
        _sharedProgramWords = new int[512];
        _sharedInitialRam = new byte[RamByteCount];
        _sharedRam = new byte[RamByteCount];
        for (int i = 0; i < 512; i++) _sharedProgramWords[i] = coordinator.snapshotProgramWords[i];
        for (int i = 0; i < RamByteCount; i++)
        {
            _sharedInitialRam[i] = coordinator.snapshotInitialRam[i];
            _sharedRam[i] = coordinator.snapshotRam[i];
        }
        _sharedProgramName = coordinator.snapshotProgramName;
        _sharedProgramRevision = coordinator.snapshotProgramRevision;
        _sharedInts[2] = coordinator.snapshotEpoch; _sharedInts[3] = coordinator.snapshotCycle; _sharedInts[4] = coordinator.snapshotSamples;
        _sharedInts[5] = coordinator.snapshotInputMask; _sharedInts[6] = coordinator.snapshotProgram;
        _sharedInts[7] = coordinator.snapshotFrozen ? 1 : 0; _sharedInts[8] = coordinator.snapshotFrozenSamples;
        _sharedInts[9] = coordinator.snapshotRunning ? 1 : 0;
        for (int i = 0; i < 64; i++) _sharedInts[16 + i] = coordinator.snapshotProbeIds[i];
        for (int i = 0; i < 4; i++)
        {
            _sharedInts[80 + i] = coordinator.snapshotWidths[i]; _sharedInts[84 + i] = coordinator.snapshotSegments[i];
            _sharedInts[88 + i] = coordinator.snapshotBindSamples[i]; _sharedInts[92 + i] = coordinator.snapshotFrozenWidths[i];
            _sharedInts[96 + i] = coordinator.snapshotFrozenSegments[i]; _sharedInts[100 + i] = coordinator.snapshotFrozenBindSamples[i];
            _sharedLabels[i] = coordinator.snapshotLabels[i]; _sharedLabels[4 + i] = coordinator.snapshotFrozenLabels[i];
        }
        _sharedLiveBytes = new byte[coordinator.snapshotHistory.Length];
        _sharedFrozenBytes = new byte[coordinator.snapshotFrozenHistory.Length];
        for (int i = 0; i < _sharedLiveBytes.Length; i++) _sharedLiveBytes[i] = coordinator.snapshotHistory[i];
        for (int i = 0; i < _sharedFrozenBytes.Length; i++) _sharedFrozenBytes[i] = coordinator.snapshotFrozenHistory[i];
        sharedRestoreRevision = coordinator.snapshotRevision;
    }

    public void BeginSharedSnapshot()
    {
        EnsureStateArrays();
        for (int i = 0; i < 512; i++) programWords[i] = _sharedProgramWords[i];
        for (int i = 0; i < RamByteCount; i++) initialRam[i] = _sharedInitialRam[i];
        activeProgramName = _sharedProgramName;
        programRevision = _sharedProgramRevision;
        ApplyProgramTexture();
        UploadRamTexture(ramInitialTexture, initialRam);
        UploadRamTexture(restoreRamTexture, _sharedRam);
        InitializeRamBuffers(restoreRamTexture);
        PrepareComputeUniforms();
        running = _sharedInts[9] != 0 && (coordinator.manualTestMode || coordinator.controlAuthorityId >= 0);
        testRunning = coordinator.clock != null && coordinator.clock.selfTest;
        _fixtureCount = vectorCount > 0 ? vectorCount : inputMasks == null ? 0 : inputMasks.Length;
        queuedStepCount = 0;
        currentInputMask = _sharedInts[5]; programSelect = _sharedInts[6]; sharedEpoch = _sharedInts[2];
        cycleCount = _sharedInts[3]; sampleCount = _sharedInts[4]; historyHead = sampleCount % historyCapacity;
        completedVectors = testRunning ? Mathf.Clamp(cycleCount, 0, _fixtureCount) : 0;
        historyFrozen = _sharedInts[7] != 0; frozenSampleCount = _sharedInts[8];
        for (int i = 0; i < RamByteCount; i++) observedRam[i] = _sharedRam[i];
        observedRamStable = true;
        observedRamCycle = cycleCount;
        _observedStateAvailable = false;
        _rateWindowStart = Time.realtimeSinceStartup;
        _rateWindowCycle = cycleCount;
        for (int i = 0; i < 64; i++) _probeIds[i] = _sharedInts[16 + i];
        for (int i = 0; i < 4; i++)
        {
            probeWidths[i] = _sharedInts[80 + i]; probeSegments[i] = _sharedInts[84 + i]; probeBindSamples[i] = _sharedInts[88 + i];
            _frozenWidths[i] = _sharedInts[92 + i]; _frozenSegments[i] = _sharedInts[96 + i]; _frozenBindSamples[i] = _sharedInts[100 + i];
            _probeLabels[i] = _sharedLabels[i]; _frozenLabels[i] = _sharedLabels[4 + i];
        }
        BuildRestoreStateTexture(_sharedDffBits);
        simulationMaterial.SetInt("_InputMask", currentInputMask);
        _readState = stateA; _writeState = stateB;
        StatePass(3); Settle(); PublishState(); UpdateProbeUniforms();
        _restorePhase = historyFrozen ? 1 : 2; _restoreIndex = 0; sharedRestoreProgress = 0;
        ClearSharedHistoryTarget();
    }

    private void ClearSharedHistoryTarget()
    {
        _readHistory = historyA; _writeHistory = historyB;
        historyMaterial.SetInt("_RestoreMode", 0); historyMaterial.SetInt("_CopyMode", 0); historyMaterial.SetInt("_EventMode", 0);
        historyMaterial.SetInt("_ClearMode", 1);
        VRCGraphics.Blit(_readHistory, _writeHistory, historyMaterial);
        RenderTexture previous = _readHistory; _readHistory = _writeHistory; _writeHistory = previous;
        currentHistory = _readHistory; historyMaterial.SetInt("_ClearMode", 0);
    }

    public bool RestoreSharedHistoryBatch()
    {
        byte[] data = _restorePhase == 1 ? _sharedFrozenBytes : _sharedLiveBytes;
        int count = _restorePhase == 1 ? frozenSampleCount : sampleCount;
        int[] segments = _restorePhase == 1 ? _frozenSegments : probeSegments;
        historyMaterial.SetVector("_ProbeSegments", new Vector4(segments[0], segments[1], segments[2], segments[3]));
        historyMaterial.SetInt("_RestoreMode", 1);
        for (int work = 0; work < 32 && _restoreIndex < historyCapacity; work++)
        {
            int slot = _restoreIndex++;
            int index = count - 1 - ((count - 1 - slot + historyCapacity) % historyCapacity);
            if (index < 0) continue;
            int offset = slot * 9, valid = data[offset + 8];
            historyMaterial.SetVector("_RestoreValues", new Vector4(data[offset] | (data[offset + 1] << 8), data[offset + 2] | (data[offset + 3] << 8), data[offset + 4] | (data[offset + 5] << 8), data[offset + 6] | (data[offset + 7] << 8)));
            historyMaterial.SetVector("_RestoreValidity", new Vector4((valid & 1) != 0 ? 1 : 0, (valid & 2) != 0 ? 1 : 0, (valid & 4) != 0 ? 1 : 0, (valid & 8) != 0 ? 1 : 0));
            historyMaterial.SetInt("_WriteSlot", slot); historyMaterial.SetInt("_SampleIndex", index);
            VRCGraphics.Blit(_readHistory, _writeHistory, historyMaterial);
            RenderTexture previous = _readHistory; _readHistory = _writeHistory; _writeHistory = previous;
            currentHistory = _readHistory;
        }
        historyMaterial.SetInt("_RestoreMode", 0);
        sharedRestoreProgress = (_restorePhase == 1 ? 0 : historyFrozen ? historyCapacity : 0) + _restoreIndex;
        if (_restoreIndex < historyCapacity) return false;
        if (_restorePhase == 1)
        {
            historyMaterial.SetInt("_CopyMode", 1); VRCGraphics.Blit(_readHistory, frozenHistory, historyMaterial); historyMaterial.SetInt("_CopyMode", 0);
            _restorePhase = 2; _restoreIndex = 0; ClearSharedHistoryTarget(); return false;
        }
        UpdateProbeUniforms(); _telemetryRequested = true; return true;
    }

    private void MirrorClock()
    {
        if (coordinator.restorePending || !coordinator.ready || coordinator.clock == null) return;
        CircuitLabNetworkClock clock = coordinator.clock;
        if (clock.epoch != sharedEpoch) { coordinator.RequestSnapshot(); return; }
        if (clock.programRevision != programRevision)
        {
            if (clock.programSelect >= 0 && CopyPresetToActive(clock.programSelect))
            {
                programRevision = clock.programRevision;
                activeProgramName = clock.activeProgramName;
                ApplyProgramTexture();
                UploadRamTexture(ramInitialTexture, initialRam);
            }
            else { coordinator.RequestSnapshot(); return; }
        }
        programSelect = clock.programSelect;
        activeProgramName = clock.activeProgramName;
        CatchUpSharedClock(clock.cycle, clock.selfTest);
        running = clock.running; cyclesPerSecond = Mathf.Clamp(clock.speed, 0.25f, 256f);
        waveWindowSamples = clock.window; waveOffset = clock.offset; cursorA = clock.cursorA; cursorB = clock.cursorB;
        bitViewChannel = clock.bitView; numberFormat = clock.numberFormat; followLatest = clock.followLatest;
        analyticAA = clock.analyticAA; waveEdgeWidth = clock.edgeWidth;
        if (!readbackPending && (_telemetryRequested || Time.realtimeSinceStartup >= _nextTelemetryAt))
        {
            _telemetryRequested = false; _nextTelemetryAt = Time.realtimeSinceStartup + telemetryInterval; RequestReadback(false);
        }
    }

    public bool CatchUpSharedClock(int targetCycle, bool selfTest)
    {
        if (readbackPending)
        {
            catchupLag = Mathf.Max(0, targetCycle - cycleCount);
            return cycleCount >= targetCycle;
        }
        float rate = Mathf.Clamp(cyclesPerSecond, 0.25f, 256f);
        int budget = Mathf.Clamp(Mathf.CeilToInt(rate * Mathf.Max(_frameDelta, 0.01f)) + 2, 1, 256);
        for (int i = 0; i < budget && cycleCount < targetCycle; i++)
        {
            if (selfTest) ApplyFixtureVector(cycleCount);
            int mask = selfTest && inputMasks != null && cycleCount < inputMasks.Length ? inputMasks[cycleCount] : 0;
            SubmitCycle(mask);
        }
        catchupLag = Mathf.Max(0, targetCycle - cycleCount);
        return cycleCount >= targetCycle;
    }

    private void Start()
    {
        EnsureQueues();
    }

    private void EnsureQueues()
    {
        if (_commands != null) return;
        _commands = new int[128];
        _commandProbeIds = new int[128 * 16];
        _commandProbeWidths = new int[128];
        _commandProbeLabels = new string[128];
        _probeIds = new int[64];
        _probeLabels = new string[4];
        probeLabels = _probeLabels;
        _queuedProbeIds = new int[64];
        _queuedProbeWidths = new int[4];
        _queuedProbeLabels = new string[4];
        _loadQueueWords = new int[4 * 512];
        _loadQueueRam = new byte[4 * RamByteCount];
        _loadQueueNames = new string[4];
        _loadQueuePresetIds = new int[4];
        probeWidths = new int[4];
        probeSegments = new int[4];
        probeBindSamples = new int[4];
        _frozenWidths = new int[4];
        _frozenSegments = new int[4];
        _frozenBindSamples = new int[4];
        _frozenLabels = new string[4];
        for (int i = 0; i < 64; i++) _probeIds[i] = -1;
    }

    private bool ValidConfiguration()
    {
        return simulationMaterial != null && historyMaterial != null && waveformMaterial != null && ramMaterial != null && gateTexture != null
            && programTexture != null && ramInitialTexture != null && restoreRamTexture != null && restoreStateTexture != null
            && stateA != null && stateB != null && stateA != stateB && historyA != null && historyB != null
            && ramA != null && ramB != null && ramA != ramB
            && historyA != historyB && frozenHistory != null && frozenHistory != historyA && frozenHistory != historyB
            && nodeCount > 0 && textureWidth >= nodeCount && settlePasses > 0 && historyCapacity >= 8
            && stateA.width == textureWidth && stateB.width == textureWidth && stateA.height == 1 && stateB.height == 1
            && restoreStateTexture.width == textureWidth && restoreStateTexture.height == 1
            && programTexture.width == 512 && programTexture.height == 1
            && ramInitialTexture.width == RamTextureWidth && ramInitialTexture.height == RamTextureWidth
            && restoreRamTexture.width == RamTextureWidth && restoreRamTexture.height == RamTextureWidth
            && ramA.width == RamTextureWidth && ramA.height == RamTextureWidth
            && ramB.width == RamTextureWidth && ramB.height == RamTextureWidth
            && ramA.format == RenderTextureFormat.ARGB32 && ramB.format == RenderTextureFormat.ARGB32
            && programTexture.format == TextureFormat.RGBA32 && ramInitialTexture.format == TextureFormat.RGBA32
            && restoreRamTexture.format == TextureFormat.RGBA32 && restoreStateTexture.format == TextureFormat.RGBA32
            && historyA.width == historyCapacity && historyB.width == historyCapacity && frozenHistory.width == historyCapacity
            && historyA.height == 4 && historyB.height == 4 && frozenHistory.height == 4
            && historyA.format == RenderTextureFormat.ARGBFloat && historyB.format == RenderTextureFormat.ARGBFloat
            && frozenHistory.format == RenderTextureFormat.ARGBFloat;
    }

    private void Initialize()
    {
        EnsureQueues();
        _initialized = true;
        _configurationValid = ValidConfiguration();
        if (!_configurationValid)
        {
            statusCode = 5;
            Debug.LogError("CIRCUIT_LAB_CONFIGURATION_ERROR: prebuilt state/history textures or materials are missing or have incorrect dimensions.");
            UpdateHud();
            return;
        }
        if (programCount <= 0 && presetProgramWords != null) programCount = presetProgramWords.Length / 512;
        if ((programWords == null || programWords.Length < 512) && programCount > 0)
            CopyPresetToActive(Mathf.Clamp(programSelect, 0, programCount - 1));
        EnsureStateArrays();
        _pixels = new Color32[textureWidth];
        _ramReadbackPixels = new Color32[RamByteCount];
        _uploadPixels = new Color32[512];
        _uploadRamPixels = new Color32[RamByteCount];
        _restorePixels = new Color32[textureWidth];
        programTexture.filterMode = FilterMode.Point;
        programTexture.wrapMode = TextureWrapMode.Clamp;
        ramInitialTexture.filterMode = FilterMode.Point;
        ramInitialTexture.wrapMode = TextureWrapMode.Clamp;
        restoreRamTexture.filterMode = FilterMode.Point;
        restoreRamTexture.wrapMode = TextureWrapMode.Clamp;
        restoreStateTexture.filterMode = FilterMode.Point;
        restoreStateTexture.wrapMode = TextureWrapMode.Clamp;
        ramA.filterMode = FilterMode.Point;
        ramA.wrapMode = TextureWrapMode.Clamp;
        ramB.filterMode = FilterMode.Point;
        ramB.wrapMode = TextureWrapMode.Clamp;
        ApplyProgramTexture();
        UploadRamTexture(ramInitialTexture, initialRam);
        PrepareComputeUniforms();
        if (displayMaterial != null) displayMaterial.SetFloat("_TextureWidth", textureWidth);
        ApplyBinding(0, probe0Signals, probe0Label);
        ApplyBinding(1, probe1Signals, probe1Label);
        ApplyBinding(2, probe2Signals, probe2Label);
        ApplyBinding(3, probe3Signals, probe3Label);
        RestartState();
        running = runOnStart;
        statusCode = running ? 1 : 0;
        _nextCycleAt = Time.realtimeSinceStartup;
        _rateWindowStart = Time.realtimeSinceStartup;
        _rateWindowCycle = cycleCount;
        _telemetryRequested = true;
    }

    private void EnsureStateArrays()
    {
        if (programWords == null || programWords.Length != 512) programWords = new int[512];
        if (initialRam == null || initialRam.Length != RamByteCount) initialRam = new byte[RamByteCount];
        if (programCount <= 0 && presetProgramWords != null) programCount = presetProgramWords.Length / 512;
        if (_uploadPixels == null || _uploadPixels.Length != 512) _uploadPixels = new Color32[512];
        if (_uploadRamPixels == null || _uploadRamPixels.Length != RamByteCount) _uploadRamPixels = new Color32[RamByteCount];
        if (_restorePixels == null || _restorePixels.Length != textureWidth) _restorePixels = new Color32[textureWidth];
        if (observedRam == null || observedRam.Length != RamByteCount) observedRam = new byte[RamByteCount];
    }

    private bool ApplyProgramTexture()
    {
        if (programTexture == null || programWords == null || programWords.Length < 512) return false;
        for (int i = 0; i < 512; i++)
        {
            int word = programWords[i] & 4095;
            _uploadPixels[i] = new Color32((byte)(word & 255), (byte)((word >> 8) & 15), 0, 255);
        }
        programTexture.filterMode = FilterMode.Point;
        programTexture.wrapMode = TextureWrapMode.Clamp;
        programTexture.SetPixels32(_uploadPixels);
        programTexture.Apply(false, false);
        simulationMaterial.SetTexture("_ProgramTex", programTexture);
        return true;
    }

    private void UploadRamTexture(Texture2D texture, byte[] data)
    {
        if (texture == null || data == null || data.Length < RamByteCount) return;
        for (int i = 0; i < RamByteCount; i++) _uploadRamPixels[i] = new Color32(data[i], 0, 0, 255);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(_uploadRamPixels);
        texture.Apply(false, false);
    }

    private void InitializeRamBuffers(Texture2D source)
    {
        if (source == null || ramA == null || ramB == null) return;
        ramMaterial.SetTexture("_InitialRAM", source);
        ramMaterial.SetInt("_Mode", 1);
        VRCGraphics.Blit(stateA, ramA, ramMaterial);
        VRCGraphics.Blit(stateA, ramB, ramMaterial);
        _readRam = ramA;
        _writeRam = ramB;
        currentRam = _readRam;
        ramMaterial.SetInt("_Mode", 0);
        if (simulationMaterial != null) simulationMaterial.SetTexture("_RamTex", currentRam);
        if (historyMaterial != null) historyMaterial.SetTexture("_RamTex", currentRam);
    }

    private Vector4 SignalVector(int[] ids, int start)
    {
        if (ids == null) return new Vector4(-1, -1, -1, -1);
        return new Vector4(start < ids.Length ? ids[start] : -1, start + 1 < ids.Length ? ids[start + 1] : -1,
            start + 2 < ids.Length ? ids[start + 2] : -1, start + 3 < ids.Length ? ids[start + 3] : -1);
    }

    private void UpdateMemoryUniforms()
    {
        if (simulationMaterial != null)
        {
            simulationMaterial.SetVector("_RomAddressA", SignalVector(romAddressSignals, 0));
            simulationMaterial.SetVector("_RomAddressB", SignalVector(romAddressSignals, 4));
            simulationMaterial.SetVector("_RomAddressC", SignalVector(romAddressSignals, 8));
            simulationMaterial.SetVector("_RamReadAddressA", SignalVector(ramReadAddressSignals, 0));
            simulationMaterial.SetVector("_RamReadAddressB", SignalVector(ramReadAddressSignals, 4));
            simulationMaterial.SetVector("_RamReadAddressC", SignalVector(ramReadAddressSignals, 8));
        }
        if (ramMaterial != null)
        {
            ramMaterial.SetVector("_RamWriteAddressA", SignalVector(ramWriteAddressSignals, 0));
            ramMaterial.SetVector("_RamWriteAddressB", SignalVector(ramWriteAddressSignals, 4));
            ramMaterial.SetVector("_RamWriteAddressC", SignalVector(ramWriteAddressSignals, 8));
            ramMaterial.SetVector("_RamWriteDataA", SignalVector(ramWriteDataSignals, 0));
            ramMaterial.SetVector("_RamWriteDataB", SignalVector(ramWriteDataSignals, 4));
            ramMaterial.SetInt("_RamWriteEnableSignal", ramWriteEnableSignal);
        }
    }

    private void PrepareComputeUniforms()
    {
        // Editor asset refresh can erase uniforms outside Shader Properties. Reapply
        // the VM's configuration before submitting work, without resetting its state.
        simulationMaterial.SetTexture("_Gates", gateTexture);
        simulationMaterial.SetInt("_NodeCount", nodeCount);
        simulationMaterial.SetInt("_TextureWidth", textureWidth);
        if (programTexture != null) simulationMaterial.SetTexture("_ProgramTex", programTexture);
        if (currentRam != null) simulationMaterial.SetTexture("_RamTex", currentRam);
        if (restoreStateTexture != null) simulationMaterial.SetTexture("_RestoreState", restoreStateTexture);
        historyMaterial.SetInt("_NodeCount", nodeCount);
        historyMaterial.SetInt("_TextureWidth", textureWidth);
        historyMaterial.SetInt("_HistoryCapacity", historyCapacity);
        if (currentRam != null) historyMaterial.SetTexture("_RamTex", currentRam);
        UpdateMemoryUniforms();
        UpdateProbeUniforms();
        if (_readState != null) PublishState();
    }

    private void StatePass(int mode)
    {
        simulationMaterial.SetInt("_Mode", mode);
        VRCGraphics.Blit(_readState, _writeState, simulationMaterial);
        RenderTexture previous = _readState;
        _readState = _writeState;
        _writeState = previous;
        currentState = _readState;
        if (displayMaterial != null) displayMaterial.SetTexture("_StateTex", _readState);
    }

    private void BuildRestoreStateTexture(int[] dffWords)
    {
        if (restoreStateTexture == null || _restorePixels == null) return;
        for (int i = 0; i < _restorePixels.Length; i++) _restorePixels[i] = new Color32(0, 0, 0, 255);
        if (dffSignals != null && dffWords != null)
        {
            int count = Mathf.Min(dffSignals.Length, dffWords.Length * 32);
            for (int i = 0; i < count; i++)
            {
                int signal = dffSignals[i];
                if (signal < 0 || signal >= textureWidth) continue;
                bool set = ((dffWords[i / 32] >> (i % 32)) & 1) != 0;
                _restorePixels[signal] = new Color32(set ? (byte)255 : (byte)0, 0, 0, 255);
            }
        }
        restoreStateTexture.filterMode = FilterMode.Point;
        restoreStateTexture.wrapMode = TextureWrapMode.Clamp;
        restoreStateTexture.SetPixels32(_restorePixels);
        restoreStateTexture.Apply(false, false);
        simulationMaterial.SetTexture("_RestoreState", restoreStateTexture);
    }

    private void Settle()
    {
        if (currentRam != null) simulationMaterial.SetTexture("_RamTex", currentRam);
        for (int i = 0; i < settlePasses; i++) StatePass(1);
    }

    private void RestartState()
    {
        EnsureStateArrays();
        ApplyProgramTexture();
        UploadRamTexture(ramInitialTexture, initialRam);
        InitializeRamBuffers(ramInitialTexture);
        PrepareComputeUniforms();
        running = false;
        testRunning = false;
        _runAfterTest = false;
        queuedStepCount = 0;
        _readState = stateA;
        _writeState = stateB;
        simulationMaterial.SetInt("_InputMask", 0);
        StatePass(0);
        Settle();
        // Establish the circuit's synchronous reset values (including registered flags),
        // without treating initialization as a user clock or recording partial history.
        simulationMaterial.SetInt("_InputMask", 2);
        Settle();
        simulationMaterial.SetInt("_InputMask", 3);
        StatePass(2);
        RamPass(_writeState, true);
        Settle();
        currentInputMask = 0;
        simulationMaterial.SetInt("_InputMask", currentInputMask);
        Settle();
        PublishState();
        cycleCount = 0;
        sampleCount = 0;
        historyHead = 0;
        historyFrozen = false;
        frozenSampleCount = 0;
        waveOffset = 0;
        followLatest = true;
        cursorA = 0;
        cursorB = 16;
        _placeCursors = true;
        completedVectors = 0;
        mismatchCount = 0;
        lastSelfTestComparedBits = 0;
        elapsedMilliseconds = 0;
        statusCode = 0;
        for (int channel = 0; channel < 4; channel++)
        {
            probeSegments[channel]++;
            probeBindSamples[channel] = 0;
        }
        _readHistory = historyA;
        _writeHistory = historyB;
        historyMaterial.SetInt("_CopyMode", 0);
        historyMaterial.SetInt("_RestoreMode", 0);
        historyMaterial.SetInt("_EventMode", 0);
        historyMaterial.SetInt("_ClearMode", 1);
        VRCGraphics.Blit(_readHistory, _writeHistory, historyMaterial);
        RenderTexture previous = _readHistory;
        _readHistory = _writeHistory;
        _writeHistory = previous;
        currentHistory = _readHistory;
        historyMaterial.SetInt("_ClearMode", 0);
        UpdateProbeUniforms();
        _telemetryRequested = true;
        _nextCycleAt = Time.realtimeSinceStartup;
        _rateWindowStart = Time.realtimeSinceStartup;
        _rateWindowCycle = cycleCount;
        observedRamStable = false;
        _observedStateAvailable = false;
        if (terminal != null) terminal.ResetOutput();
    }

    private void RecordStableHalfCycle()
    {
        historyMaterial.SetTexture("_StateTex", _readState);
        if (currentRam != null) historyMaterial.SetTexture("_RamTex", currentRam);
        historyMaterial.SetInt("_WriteSlot", sampleCount % historyCapacity);
        historyMaterial.SetInt("_SampleIndex", sampleCount);
        VRCGraphics.Blit(_readHistory, _writeHistory, historyMaterial);
        RenderTexture previous = _readHistory;
        _readHistory = _writeHistory;
        _writeHistory = previous;
        currentHistory = _readHistory;
        sampleCount++;
        historyHead = sampleCount % historyCapacity;
        if (!historyFrozen && !followLatest) waveOffset++;
    }

    private void SubmitCycle(int mask)
    {
        // Intermediate propagation passes are never recorded: this is a binary, zero-delay clock model.
        simulationMaterial.SetInt("_InputMask", mask & ~1);
        Settle();
        RecordStableHalfCycle();
        simulationMaterial.SetInt("_InputMask", mask | 1);
        StatePass(2);
        RamPass(_writeState, (mask & 2) != 0);
        Settle();
        RecordStableHalfCycle();
        cycleCount++;
        lastSubmittedInputMask = mask | 1;
        currentInputMask = mask | 1;
        observedRamStable = false;
        _observedStateAvailable = false;
        if (terminal != null) terminal.RecordCycle(_readState, cycleCount);
        PublishState();
    }

    private void RamPass(RenderTexture stableOldState, bool resetActive)
    {
        if (ramMaterial == null || _readRam == null || _writeRam == null) return;
        ramMaterial.SetInt("_Mode", 0);
        ramMaterial.SetInt("_Reset", resetActive ? 1 : 0);
        ramMaterial.SetInt("_TextureWidth", textureWidth);
        ramMaterial.SetInt("_NodeCount", nodeCount);
        ramMaterial.SetInt("_RamWriteEnableSignal", ramWriteEnableSignal);
        ramMaterial.SetTexture("_StateTex", stableOldState);
        VRCGraphics.Blit(_readRam, _writeRam, ramMaterial);
        RenderTexture previous = _readRam;
        _readRam = _writeRam;
        _writeRam = previous;
        currentRam = _readRam;
        simulationMaterial.SetTexture("_RamTex", currentRam);
        historyMaterial.SetTexture("_RamTex", currentRam);
        ramMaterial.SetInt("_Reset", 0);
    }

    private void PublishState()
    {
        // All gate and wire displays observe the final stable texture before cameras render.
        RestoreDisplayBinding(displayMaterial);
        if (displayMaterials == null) return;
        for (int i = 0; i < displayMaterials.Length; i++)
            RestoreDisplayBinding(displayMaterials[i]);
    }

    private void RestoreDisplayBinding(Material material)
    {
        if (material == null) return;
        if (material.GetTexture("_StateTex") != _readState) material.SetTexture("_StateTex", _readState);
        if (material.GetFloat("_TextureWidth") != textureWidth) material.SetFloat("_TextureWidth", textureWidth);
    }

    private void Update()
    {
        if (!_initialized) Initialize();
        if (!_configurationValid) return;
        float frameNow = Time.realtimeSinceStartup;
        _frameDelta = _lastFrameAt > 0 ? Mathf.Max(0.01f, frameNow - _lastFrameAt) : 0.016f;
        _lastFrameAt = frameNow;
        cyclesPerSecond = Mathf.Clamp(cyclesPerSecond, 0.25f, 256f);
        PrepareComputeUniforms();
        if (coordinator != null && (!coordinator.HasControl() || coordinator.restorePending))
        {
            MirrorClock();
            UpdateRateTelemetry(Time.realtimeSinceStartup);
            UpdateWaveformUniforms();
            UpdateHud();
            return;
        }
        // Interactions only append CPU-side commands. All Blits and readback requests originate here.
        ProcessCommands();
        float now = Time.realtimeSinceStartup;
        if (coordinator != null && coordinator.SubmissionBlocked())
        {
            UpdateWaveformUniforms(); UpdateHud(); return;
        }
        if (testRunning)
        {
            if (!readbackPending && completedVectors < _fixtureCount)
            {
                ApplyFixtureVector(completedVectors);
                SubmitCycle(inputMasks[completedVectors]);
                RequestReadback(true);
            }
        }
        else if (!readbackPending && queuedStepCount > 0)
        {
            queuedStepCount--;
            SubmitCycle(0);
            _telemetryRequested = true;
        }
        else if (!readbackPending && running)
        {
            float rate = Mathf.Clamp(cyclesPerSecond, 0.25f, 256f);
            float interval = 1f / rate;
            int budget = Mathf.Clamp(Mathf.CeilToInt(rate * Mathf.Max(_frameDelta, 0.01f)) + 2, 1, 256);
            int submitted = 0;
            if (_nextCycleAt <= 0) _nextCycleAt = now;
            while (submitted < budget && now >= _nextCycleAt)
            {
                SubmitCycle(0);
                _nextCycleAt += interval;
                submitted++;
            }
            catchupLag = Mathf.Max(0, Mathf.Ceil((now - _nextCycleAt) * rate));
        }
        else if (!running) catchupLag = 0;
        if (!testRunning && !readbackPending && (_telemetryRequested || (running && now >= _nextTelemetryAt)))
        {
            _telemetryRequested = false;
            _nextTelemetryAt = now + Mathf.Max(0.1f, telemetryInterval);
            RequestReadback(false);
        }
        UpdateWaveformUniforms();
        UpdateRateTelemetry(now);
        if (now >= _nextHudAt)
        {
            _nextHudAt = now + 0.15f;
            UpdateHud();
        }
    }

    private void UpdateRateTelemetry(float now)
    {
        if (_rateWindowStart <= 0)
        {
            _rateWindowStart = now;
            _rateWindowCycle = cycleCount;
            return;
        }
        float elapsed = now - _rateWindowStart;
        if (elapsed < 0.5f) return;
        actualCyclesPerSecond = Mathf.Max(0, cycleCount - _rateWindowCycle) / elapsed;
        _rateWindowStart = now;
        _rateWindowCycle = cycleCount;
    }

    private void Enqueue(int command)
    {
        if (coordinator != null && !coordinator.HasControl()) { coordinator.deniedCommands++; return; }
        EnsureQueues();
        if (queuedCommandCount >= _commands.Length)
        {
            rejectedCommandCount++;
            Debug.LogWarning("CIRCUIT_LAB_COMMAND_QUEUE_FULL");
            return;
        }
        _commands[(_commandHead + queuedCommandCount) % _commands.Length] = command;
        queuedCommandCount++;
    }

    private bool CanQueueProgram()
    {
        if (coordinator != null && !coordinator.HasControl())
        {
            coordinator.deniedCommands++;
            return false;
        }
        EnsureQueues();
        if (queuedCommandCount >= _commands.Length || _loadQueueCount >= _loadQueuePresetIds.Length)
        {
            rejectedCommandCount++;
            return false;
        }
        return true;
    }

    public bool LoadProgram(int[] words, byte[] ram, string name, int presetId)
    {
        if (words == null || words.Length < 512 || ram == null || ram.Length < RamByteCount || !CanQueueProgram()) return false;
        int loadSlot = (_loadQueueHead + _loadQueueCount) % _loadQueuePresetIds.Length;
        for (int i = 0; i < 512; i++) _loadQueueWords[loadSlot * 512 + i] = words[i] & 4095;
        for (int i = 0; i < RamByteCount; i++) _loadQueueRam[loadSlot * RamByteCount + i] = ram[i];
        _loadQueueNames[loadSlot] = name == null ? "CUSTOM" : name;
        _loadQueuePresetIds[loadSlot] = presetId;
        QueueProgramCommand();
        return true;
    }

    public void LoadPreset(int id)
    {
        if (id < 0 || id >= programCount || presetProgramWords == null || presetProgramWords.Length < (id + 1) * 512
            || presetInitialRam == null || presetInitialRam.Length < (id + 1) * RamByteCount || !CanQueueProgram()) return;
        int loadSlot = (_loadQueueHead + _loadQueueCount) % _loadQueuePresetIds.Length;
        for (int i = 0; i < 512; i++) _loadQueueWords[loadSlot * 512 + i] = presetProgramWords[id * 512 + i] & 4095;
        for (int i = 0; i < RamByteCount; i++) _loadQueueRam[loadSlot * RamByteCount + i] = presetInitialRam[id * RamByteCount + i];
        _loadQueueNames[loadSlot] = programNames != null && id < programNames.Length ? programNames[id] : "PROGRAM " + id;
        _loadQueuePresetIds[loadSlot] = id;
        QueueProgramCommand();
    }

    private void QueueProgramCommand()
    {
        int commandSlot = (_commandHead + queuedCommandCount) % _commands.Length;
        _commands[commandSlot] = 33;
        queuedCommandCount++;
        _loadQueueCount++;
    }

    private void ApplyQueuedProgram()
    {
        if (_loadQueueCount <= 0) return;
        int slot = _loadQueueHead;
        for (int i = 0; i < 512; i++) programWords[i] = _loadQueueWords[slot * 512 + i];
        for (int i = 0; i < RamByteCount; i++) initialRam[i] = _loadQueueRam[slot * RamByteCount + i];
        activeProgramName = _loadQueueNames[slot];
        programSelect = _loadQueuePresetIds[slot];
        programRevision++;
        _loadQueueNames[slot] = null;
        _loadQueueHead = (_loadQueueHead + 1) % _loadQueuePresetIds.Length;
        _loadQueueCount--;
        RestartState();
    }

    private bool CopyPresetToActive(int id)
    {
        if (id < 0 || id >= programCount || presetProgramWords == null || presetProgramWords.Length < (id + 1) * 512
            || presetInitialRam == null || presetInitialRam.Length < (id + 1) * RamByteCount) return false;
        EnsureStateArrays();
        for (int i = 0; i < 512; i++) programWords[i] = presetProgramWords[id * 512 + i] & 4095;
        for (int i = 0; i < RamByteCount; i++) initialRam[i] = presetInitialRam[id * RamByteCount + i];
        activeProgramName = programNames != null && id < programNames.Length ? programNames[id] : "PROGRAM " + id;
        programSelect = id;
        return true;
    }

    private void ApplyFixtureVector(int vector)
    {
        int id = fixturePrograms != null && vector < fixturePrograms.Length ? fixturePrograms[vector] : programSelect;
        if (id >= 0 && id < programCount && id != programSelect && CopyPresetToActive(id))
        {
            programRevision++;
            ApplyProgramTexture();
            UploadRamTexture(ramInitialTexture, initialRam);
        }
        if (fixtureRamReset != null && vector < fixtureRamReset.Length && fixtureRamReset[vector])
        {
            UploadRamTexture(restoreRamTexture, initialRam);
            InitializeRamBuffers(restoreRamTexture);
        }
        PrepareComputeUniforms();
        Settle();
        PublishState();
    }

    public void Run() { Enqueue(1); }
    public void Pause() { Enqueue(2); }
    public void Step() { Enqueue(3); }
    public void ResetCircuit() { Enqueue(4); }
    public void RestartExperiment() { Enqueue(5); }
    public void SpeedUp() { Enqueue(6); }
    public void SpeedDown() { Enqueue(7); }
    public void SelectProgram() { Enqueue(8); }
    public void FreezeWaveform() { Enqueue(9); }
    public void ResumeWaveform() { Enqueue(10); }
    public void RunSelfTest() { Enqueue(11); }
    public void RequestTelemetry() { Enqueue(12); }
    public void ZoomIn() { Enqueue(13); }
    public void ZoomOut() { Enqueue(14); }
    public void PanLeft() { Enqueue(15); }
    public void PanRight() { Enqueue(16); }
    public void FollowLatest() { Enqueue(17); }
    public void MoveCursorALeft() { Enqueue(18); }
    public void MoveCursorARight() { Enqueue(19); }
    public void MoveCursorBLeft() { Enqueue(20); }
    public void MoveCursorBRight() { Enqueue(21); }
    public void CycleBitView() { Enqueue(22); }
    public void CycleNumberFormat() { Enqueue(23); }
    public void ShowChannels() { Enqueue(24); }
    public void ShowBits0() { Enqueue(25); }
    public void ShowBits1() { Enqueue(26); }
    public void ShowBits2() { Enqueue(27); }
    public void ShowBits3() { Enqueue(28); }
    public void ToggleAA() { Enqueue(29); }
    public void EdgeWidthUp() { Enqueue(30); }
    public void EdgeWidthDown() { Enqueue(31); }
    public void ClearTerminal() { Enqueue(32); }

    private void ProcessCommands()
    {
        int available = queuedCommandCount;
        for (int i = 0; i < available; i++)
        {
            if (coordinator != null && coordinator.SubmissionBlocked()) break;
            int command = _commands[_commandHead];
            // Keep the readback's source and fixture interpretation intact until its callback.
            if (readbackPending && (command == 4 || command == 5 || command == 8 || command == 11 || command == 33)) break;
            _activeCommandSlot = _commandHead;
            _commandHead = (_commandHead + 1) % _commands.Length;
            queuedCommandCount--;
            completedCommandCount++;
            lastCommandCode = command;
            if (command == 1)
            {
                if (testRunning) _runAfterTest = true;
                else { running = true; statusCode = 1; _nextCycleAt = Time.realtimeSinceStartup; }
            }
            else if (command == 2)
            {
                running = false;
                _runAfterTest = false;
                if (!testRunning) statusCode = 0;
                _telemetryRequested = true;
            }
            else if (command == 3)
            {
                running = false;
                _runAfterTest = false;
                queuedStepCount++;
                if (!testRunning) statusCode = 0;
            }
            else if (command == 4)
            {
                running = false;
                testRunning = false;
                _runAfterTest = false;
                statusCode = 0;
                SubmitCycle(2);
                _telemetryRequested = true;
            }
            else if (command == 5) RestartState();
            else if (command == 6) cyclesPerSecond = Mathf.Min(256f, cyclesPerSecond * 2f);
            else if (command == 7) cyclesPerSecond = Mathf.Max(0.25f, cyclesPerSecond * 0.5f);
            else if (command == 8)
            {
                int next = programCount <= 0 ? -1 : programSelect < 0 ? 0 : (programSelect + 1) % programCount;
                if (CopyPresetToActive(next)) { programRevision++; RestartState(); }
            }
            else if (command == 33) ApplyQueuedProgram();
            else if (command == 9) FreezeHistory();
            else if (command == 10) { historyFrozen = false; followLatest = true; waveOffset = 0; _placeCursors = true; }
            else if (command == 11) BeginSelfTest();
            else if (command == 12) _telemetryRequested = true;
            else if (command == 13) { waveWindowSamples = Mathf.Max(8, waveWindowSamples / 2); _placeCursors = true; }
            else if (command == 14) { waveWindowSamples = Mathf.Min(historyCapacity, waveWindowSamples * 2); _placeCursors = true; }
            else if (command == 15) { followLatest = false; waveOffset += Mathf.Max(1, waveWindowSamples / 4); _placeCursors = true; }
            else if (command == 16) { followLatest = false; waveOffset = Mathf.Max(0, waveOffset - Mathf.Max(1, waveWindowSamples / 4)); _placeCursors = true; }
            else if (command == 17) { followLatest = true; waveOffset = 0; _placeCursors = true; }
            else if (command == 18) cursorA--;
            else if (command == 19) cursorA++;
            else if (command == 20) cursorB--;
            else if (command == 21) cursorB++;
            else if (command == 22) bitViewChannel = bitViewChannel >= 3 ? -1 : bitViewChannel + 1;
            else if (command == 23) numberFormat = (numberFormat + 1) % 3;
            else if (command == 24) bitViewChannel = -1;
            else if (command >= 25 && command <= 28) bitViewChannel = command - 25;
            else if (command == 29) analyticAA = !analyticAA;
            else if (command == 30) waveEdgeWidth = Mathf.Min(3f, waveEdgeWidth + 0.25f);
            else if (command == 31) waveEdgeWidth = Mathf.Max(0.5f, waveEdgeWidth - 0.25f);
            else if (command == 32 && terminal != null) terminal.ApplySharedOutput("", 0, cycleCount);
            else if (command >= 100 && command <= 103) ApplyQueuedBinding(command - 100);
            else if (command >= 110 && command <= 113) ApplyBinding(command - 110, null, "未绑定");
            if (coordinator != null)
            {
                if (command == 4 || command == 5 || command == 8 || command == 9 || command == 10 || command == 11 || command == 32 || command == 33 || command >= 100)
                    coordinator.ExperimentChanged();
                coordinator.PublishClock();
            }
            // Submit this step before applying any later binding or freeze command.
            if (command == 3) break;
        }
    }

    private void RequestReadback(bool forTest)
    {
        readbackPending = true;
        observedRamStable = false;
        _readbackStage = 1;
        _readbackForTest = forTest;
        _requestedVector = completedVectors;
        _requestedCycle = cycleCount;
        VRCAsyncGPUReadback.Request(_readState, 0, this);
    }

    private void BeginSelfTest()
    {
        RestartState();
        selfTestRunId++;
        _fixtureCount = vectorCount > 0 ? vectorCount : inputMasks == null ? 0 : inputMasks.Length;
        if (inputMasks == null || expectedStates == null || _fixtureCount <= 0 || inputMasks.Length < _fixtureCount
            || expectedStates.Length < _fixtureCount * nodeCount
            || (fixturePrograms != null && fixturePrograms.Length < _fixtureCount)
            || (fixtureRamReset != null && fixtureRamReset.Length < _fixtureCount))
        {
            statusCode = 4;
            mismatchCount = 1;
            completedSelfTestRunId = selfTestRunId;
            Debug.LogError("CIRCUIT_LAB_SELF_TEST_FAIL invalid_fixture");
            return;
        }
        if (fixturePrograms != null)
        {
            for (int i = 0; i < _fixtureCount; i++)
            {
                if (fixturePrograms[i] < 0 || fixturePrograms[i] >= programCount)
                {
                    statusCode = 4;
                    mismatchCount = 1;
                    completedSelfTestRunId = selfTestRunId;
                    Debug.LogError("CIRCUIT_LAB_SELF_TEST_FAIL invalid_fixture_program vector=" + i);
                    return;
                }
            }
        }
        _telemetryRequested = false;
        _testStartedAt = Time.realtimeSinceStartup;
        testRunning = true;
        statusCode = 2;
        Debug.Log("CIRCUIT_LAB_SELF_TEST_START run=" + selfTestRunId + " vectors=" + _fixtureCount + " nodes=" + nodeCount);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        if (_readbackStage == 2)
        {
            bool ramSuccess = !request.hasError && request.TryGetData(_ramReadbackPixels);
            readbackPending = false;
            _readbackStage = 0;
            if (!ramSuccess)
            {
                observedRamStable = false;
                if (_readbackForTest)
                {
                    running = false;
                    testRunning = false;
                    statusCode = 4;
                    mismatchCount++;
                    completedSelfTestRunId = selfTestRunId;
                }
                Debug.LogError("CIRCUIT_LAB_RAM_READBACK_FAIL cycle=" + _requestedCycle);
                UpdateHud();
                return;
            }
            for (int i = 0; i < RamByteCount; i++) observedRam[i] = _ramReadbackPixels[i].r;
            observedRamStable = true;
            observedRamCycle = _requestedCycle;
            UpdateHud();
            return;
        }
        if (request.hasError || !request.TryGetData(_pixels))
        {
            readbackPending = false;
            _readbackStage = 0;
            running = false;
            testRunning = false;
            statusCode = 4;
            mismatchCount++;
            if (_readbackForTest) completedSelfTestRunId = selfTestRunId;
            Debug.LogError("CIRCUIT_LAB_GPU_READBACK_FAIL cycle=" + _requestedCycle);
            UpdateHud();
            return;
        }
        telemetryReadbackCount++;
        lastReadbackCycle = _requestedCycle;
        _observedStateAvailable = true;
        observedAcc = ReadBus(accSignals);
        observedPc = ReadBus(pcSignals);
        observedOut = ReadBus(outSignals);
        observedInstruction = ReadBus(instructionSignals);
        observedQ = qSignals == null ? observedAcc : ReadBus(qSignals);
        observedX = ReadBus(xSignals);
        observedSp = ReadBus(spSignals);
        observedPhaseValue = phaseSignals != null && phaseSignals.Length > 0 ? ReadBus(phaseSignals) : ReadBit(phaseSignal);
        observedPhase = observedPhaseValue;
        observedZero = ReadBit(zeroSignal);
        observedCarry = ReadBit(carrySignal);
        observedHalt = ReadBit(haltSignal);
        observedRamReadAddress = ReadBus(ramReadAddressSignals);
        observedRamReadData = ReadBus(ramReadDataSignals);
        observedRamWriteAddress = ReadBus(ramWriteAddressSignals);
        observedRamWriteData = ReadBus(ramWriteDataSignals);
        observedRamWriteEnable = ReadBit(ramWriteEnableSignal);
        observedBank = ReadBus(bankSignals);
        if (_readbackForTest && testRunning)
        {
            int offset = _requestedVector * nodeCount;
            for (int id = 0; id < nodeCount; id++)
            {
                int actual = _pixels[id].r >= 128 ? 1 : 0;
                if (actual != expectedStates[offset + id])
                {
                    mismatchCount++;
                    if (mismatchCount <= 8)
                        Debug.LogError("CIRCUIT_LAB_NODE_MISMATCH vector=" + _requestedVector + " signal=" + id + " actual=" + actual + " expected=" + expectedStates[offset + id]);
                }
            }
            lastSelfTestComparedBits += nodeCount;
            completedVectors++;
            if (mismatchCount != 0 || completedVectors == _fixtureCount)
            {
                testRunning = false;
                running = _runAfterTest && mismatchCount == 0;
                statusCode = mismatchCount == 0 ? 3 : 4;
                completedSelfTestRunId = selfTestRunId;
                elapsedMilliseconds = (Time.realtimeSinceStartup - _testStartedAt) * 1000f;
                _nextCycleAt = Time.realtimeSinceStartup;
                string result = mismatchCount == 0 ? "PASS" : "FAIL";
                Debug.Log("CIRCUIT_LAB_SELF_TEST_" + result + " run=" + selfTestRunId + " vectors=" + completedVectors + " compared_bits=" + lastSelfTestComparedBits + " mismatches=" + mismatchCount + " elapsed_ms=" + elapsedMilliseconds);
            }
        }
        _readbackStage = 2;
        VRCAsyncGPUReadback.Request(currentRam, 0, this);
        UpdateHud();
    }

    public int GetObservedSignal(int signalId)
    {
        if (_pixels == null || !_observedStateAvailable) return -1;
        if (signalId >= 0 && signalId < nodeCount) return _pixels[signalId].r >= 128 ? 1 : 0;
        int virtualIndex = signalId - nodeCount;
        if (virtualIndex < 0 || virtualIndex >= RamProbeCount || !observedRamStable || observedRam == null || observedRam.Length < RamByteCount) return -1;
        int address = virtualIndex / 8;
        int bit = virtualIndex & 7;
        return (observedRam[address] >> bit) & 1;
    }

    public int GetRamByte(int address)
    {
        if (address < 0 || address >= RamByteCount || !observedRamStable || observedRam == null || observedRam.Length < RamByteCount) return -1;
        return observedRam[address];
    }

    private int ReadBit(int id)
    {
        return id >= 0 && id < nodeCount && _pixels[id].r >= 128 ? 1 : 0;
    }

    private int ReadBus(int[] ids)
    {
        if (ids == null) return 0;
        int value = 0;
        int width = Mathf.Min(ids.Length, 16);
        for (int i = 0; i < width; i++) if (ReadBit(ids[i]) != 0) value |= 1 << i;
        return value;
    }

    public void BindProbe(int channel, int[] signalIds, string label)
    {
        if (channel < 0 || channel > 3) return;
        EnsureQueues();
        if (coordinator != null && !coordinator.HasControl()) { coordinator.deniedCommands++; return; }
        if (queuedCommandCount >= _commands.Length) { rejectedCommandCount++; return; }
        int width = signalIds == null ? 0 : Mathf.Min(16, signalIds.Length);
        int slot = (_commandHead + queuedCommandCount) % _commands.Length;
        _commandProbeWidths[slot] = width;
        _commandProbeLabels[slot] = label;
        for (int bit = 0; bit < 16; bit++) _commandProbeIds[slot * 16 + bit] = bit < width ? signalIds[bit] : -1;
        Enqueue(100 + channel);
    }

    public void UnbindProbe(int channel)
    {
        if (channel >= 0 && channel < 4) Enqueue(110 + channel);
    }

    private void ApplyBinding(int channel, int[] ids, string label)
    {
        int width = ids == null ? 0 : Mathf.Min(16, ids.Length);
        probeWidths[channel] = width;
        probeSegments[channel]++;
        probeBindSamples[channel] = sampleCount;
        _probeLabels[channel] = label == null ? "未绑定" : label;
        for (int bit = 0; bit < 16; bit++) _probeIds[channel * 16 + bit] = bit < width ? ids[bit] : -1;
        UpdateProbeUniforms();
    }

    private void ApplyQueuedBinding(int channel)
    {
        probeWidths[channel] = _commandProbeWidths[_activeCommandSlot];
        probeSegments[channel]++;
        probeBindSamples[channel] = sampleCount;
        _probeLabels[channel] = _commandProbeLabels[_activeCommandSlot] == null ? "未绑定" : _commandProbeLabels[_activeCommandSlot];
        for (int bit = 0; bit < 16; bit++) _probeIds[channel * 16 + bit] = _commandProbeIds[_activeCommandSlot * 16 + bit];
        UpdateProbeUniforms();
    }

    private Vector4 ProbeVector(int offset)
    {
        return new Vector4(_probeIds[offset], _probeIds[offset + 1], _probeIds[offset + 2], _probeIds[offset + 3]);
    }

    private void UpdateProbeUniforms()
    {
        historyMaterial.SetVector("_ProbeWidths", new Vector4(probeWidths[0], probeWidths[1], probeWidths[2], probeWidths[3]));
        historyMaterial.SetVector("_ProbeSegments", new Vector4(probeSegments[0], probeSegments[1], probeSegments[2], probeSegments[3]));
        historyMaterial.SetVector("_BindSamples", new Vector4(probeBindSamples[0], probeBindSamples[1], probeBindSamples[2], probeBindSamples[3]));
        historyMaterial.SetVector("_Probe0A", ProbeVector(0)); historyMaterial.SetVector("_Probe0B", ProbeVector(4));
        historyMaterial.SetVector("_Probe0C", ProbeVector(8)); historyMaterial.SetVector("_Probe0D", ProbeVector(12));
        historyMaterial.SetVector("_Probe1A", ProbeVector(16)); historyMaterial.SetVector("_Probe1B", ProbeVector(20));
        historyMaterial.SetVector("_Probe1C", ProbeVector(24)); historyMaterial.SetVector("_Probe1D", ProbeVector(28));
        historyMaterial.SetVector("_Probe2A", ProbeVector(32)); historyMaterial.SetVector("_Probe2B", ProbeVector(36));
        historyMaterial.SetVector("_Probe2C", ProbeVector(40)); historyMaterial.SetVector("_Probe2D", ProbeVector(44));
        historyMaterial.SetVector("_Probe3A", ProbeVector(48)); historyMaterial.SetVector("_Probe3B", ProbeVector(52));
        historyMaterial.SetVector("_Probe3C", ProbeVector(56)); historyMaterial.SetVector("_Probe3D", ProbeVector(60));
    }

    private void FreezeHistory()
    {
        if (historyFrozen) return;
        historyMaterial.SetInt("_CopyMode", 1);
        VRCGraphics.Blit(_readHistory, frozenHistory, historyMaterial);
        historyMaterial.SetInt("_CopyMode", 0);
        historyFrozen = true;
        frozenSampleCount = sampleCount;
        _placeCursors = true;
        for (int channel = 0; channel < 4; channel++)
        {
            _frozenWidths[channel] = probeWidths[channel];
            _frozenSegments[channel] = probeSegments[channel];
            _frozenBindSamples[channel] = probeBindSamples[channel];
            _frozenLabels[channel] = _probeLabels[channel];
        }
    }

    private void UpdateWaveformUniforms()
    {
        int count = historyFrozen ? frozenSampleCount : sampleCount;
        int[] widths = historyFrozen ? _frozenWidths : probeWidths;
        int[] segments = historyFrozen ? _frozenSegments : probeSegments;
        int[] boundaries = historyFrozen ? _frozenBindSamples : probeBindSamples;
        waveWindowSamples = Mathf.Clamp(waveWindowSamples, 8, historyCapacity);
        waveOffset = Mathf.Clamp(waveOffset, 0, Mathf.Max(0, Mathf.Min(count, historyCapacity) - 1));
        int end = Mathf.Max(0, count - 1 - waveOffset);
        waveViewStart = end - waveWindowSamples + 1;
        int oldest = Mathf.Max(0, count - historyCapacity);
        int visibleFirst = Mathf.Max(oldest, waveViewStart);
        int visibleLast = Mathf.Max(visibleFirst, end);
        if (_placeCursors && count > 0)
        {
            cursorA = visibleFirst;
            cursorB = visibleLast;
            _placeCursors = false;
        }
        cursorA = Mathf.Clamp(cursorA, visibleFirst, visibleLast);
        cursorB = Mathf.Clamp(cursorB, visibleFirst, visibleLast);
        waveformMaterial.SetTexture("_HistoryTex", historyFrozen ? frozenHistory : _readHistory);
        waveformMaterial.SetFloat("_HistoryCapacity", historyCapacity);
        waveformMaterial.SetFloat("_DisplaySampleCount", count);
        waveformMaterial.SetFloat("_ViewStart", waveViewStart);
        waveformMaterial.SetFloat("_WindowSamples", waveWindowSamples);
        waveformMaterial.SetFloat("_CursorA", cursorA);
        waveformMaterial.SetFloat("_CursorB", cursorB);
        waveformMaterial.SetInt("_BitViewChannel", Mathf.Clamp(bitViewChannel, -1, 3));
        waveformMaterial.SetInt("_NumberFormat", Mathf.Clamp(numberFormat, 0, 2));
        waveformMaterial.SetFloat("_AnalyticAA", analyticAA ? 1f : 0f);
        waveformMaterial.SetFloat("_EdgeWidth", waveEdgeWidth);
        waveformMaterial.SetFloat("_WaveAspect", waveAspect);
        waveformMaterial.SetVector("_DisplayWidths", new Vector4(widths[0], widths[1], widths[2], widths[3]));
        waveformMaterial.SetVector("_DisplaySegments", new Vector4(segments[0], segments[1], segments[2], segments[3]));
        waveformMaterial.SetVector("_DisplayBindSamples", new Vector4(boundaries[0], boundaries[1], boundaries[2], boundaries[3]));
    }

    private void UpdateHud()
    {
        string state = running || testRunning ? "RUNNING" : "PAUSED";
        if (statusCode == 5) state = "CONFIG ERROR";
        string text = state + " | TARGET " + cyclesPerSecond + " Hz | ACTUAL " + Mathf.Round(actualCyclesPerSecond * 10f) / 10f + " Hz"
            + "\nCYCLE " + cycleCount + "  LAG " + catchupLag + " cycles  PHASE " + observedPhase
            + "\nPC " + observedPc + "  ACC " + observedAcc + "  X " + observedX + "  OUT " + observedOut
            + "  Z" + observedZero + " C" + observedCarry + " HALT" + observedHalt;
        if (simHudText != null) simHudText.text = text;
        if (statusText != null && statusText != simHudText)
        {
            string result = testRunning ? "RUNNING" : statusCode == 3 ? "PASS" : statusCode == 4 ? "FAIL" : "READY";
            statusText.text = "SELF TEST " + result + " | " + completedVectors + "/" + vectorCount + " | mismatches " + mismatchCount;
        }
        if (waveHudText == null) return;
        string format = numberFormat == 1 ? "BIN" : numberFormat == 2 ? "DEC" : "HEX";
        string channel = bitViewChannel == 0 ? "A" : bitViewChannel == 1 ? "B" : bitViewChannel == 2 ? "C" : "D";
        string view = bitViewChannel < 0 ? "4 CHANNELS" : channel + " BITS, MSB FIRST";
        string wave = (historyFrozen ? "FROZEN" : followLatest ? "LIVE" : "BROWSING") + " | WINDOW " + waveWindowSamples + " | " + format + " | " + view
            + "\nA " + cursorA + "  B " + cursorB + "  DELTA " + (cursorB - cursorA) + " half-cycles | values: upper A / lower B";
        if (historyFrozen)
        {
            wave += "\n";
            for (int i = 0; i < 4; i++)
            {
                string label = _frozenLabels[i] == null ? "UNBOUND" : _frozenLabels[i];
                int last = label.LastIndexOf("/");
                if (last > 0)
                {
                    int previous = label.Substring(0, last).LastIndexOf("/");
                    if (previous >= 0) label = label.Substring(previous + 1);
                }
                string letter = i == 0 ? "A" : i == 1 ? "B" : i == 2 ? "C" : "D";
                wave += (i == 0 ? "" : " | ") + letter + ":" + label;
            }
            waveHudText.fontSize = 12;
        }
        else waveHudText.fontSize = 14;
        waveHudText.text = wave;
    }
}
