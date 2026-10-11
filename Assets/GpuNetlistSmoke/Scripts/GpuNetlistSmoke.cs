using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GpuNetlistSmoke : UdonSharpBehaviour
{
    public Material simulationMaterial;
    public Material displayMaterial;
    public Texture2D gateTexture;
    public RenderTexture stateA;
    public RenderTexture stateB;
    public Text statusText;
    public int nodeCount;
    public int textureWidth;
    public int settlePasses;
    public int[] inputMasks;
    public byte[] expectedStates;
    public int[] qSignals;
    public int zeroSignal;
    public int paritySignal;
    public bool runOnStart = true;
    public int dataValue = 1;

    // Public telemetry is also readable through the backing UdonBehaviour in the Editor.
    public int statusCode;
    public int completedVectors;
    public int mismatchCount;
    public int observedQ;
    public int cycleCount;
    public float elapsedMilliseconds;
    public int smokeRunId;

    private RenderTexture _readState;
    private RenderTexture _writeState;
    private Color32[] _pixels;
    private bool _initialized;
    private bool _smokeRunning;
    private bool _readbackPending;
    private float _startedAt;
    private bool _smokeRequested;
    private int[] _manualQueue;
    private int _manualHead;
    private int _manualCount;

    private void Start()
    {
        _manualQueue = new int[16];
        _smokeRequested = runOnStart;
    }

    private void InitializeState()
    {
        _readState = stateA;
        _writeState = stateB;
        _pixels = new Color32[textureWidth];
        simulationMaterial.SetTexture("_Gates", gateTexture);
        simulationMaterial.SetInt("_NodeCount", nodeCount);
        simulationMaterial.SetInt("_TextureWidth", textureWidth);
        simulationMaterial.SetInt("_InputMask", 0);
        displayMaterial.SetInt("_TextureWidth", textureWidth);
        Pass(0);
        Settle();
        _initialized = true;
        cycleCount = 0;
        observedQ = 0;
    }

    private void Pass(int mode)
    {
        simulationMaterial.SetInt("_Mode", mode);
        VRCGraphics.Blit(_readState, _writeState, simulationMaterial);
        RenderTexture previous = _readState;
        _readState = _writeState;
        _writeState = previous;
        displayMaterial.SetTexture("_StateTex", _readState);
    }

    private void Settle()
    {
        for (int i = 0; i < settlePasses; i++) Pass(1);
    }

    private void Clock(int mask)
    {
        // Inject the low-clock inputs, settle D, commit every flip-flop simultaneously,
        // then settle combinational outputs. No simulation is tied to camera rendering.
        simulationMaterial.SetInt("_InputMask", mask & ~1);
        Settle();
        simulationMaterial.SetInt("_InputMask", mask | 1);
        Pass(2);
        Settle();
        cycleCount++;
        _readbackPending = true;
        VRCAsyncGPUReadback.Request(_readState, 0, this);
    }

    public void RunSmoke()
    {
        _smokeRequested = true;
    }

    private void BeginSmoke()
    {
        InitializeState();
        _manualHead = 0;
        _manualCount = 0;
        _smokeRequested = false;
        smokeRunId++;
        statusCode = 1;
        completedVectors = 0;
        mismatchCount = 0;
        _startedAt = Time.realtimeSinceStartup;
        _smokeRunning = true;
        UpdateStatus();
        Debug.Log("GPU_NETLIST_SMOKE_START vectors=" + inputMasks.Length + " nodes=" + nodeCount);
    }

    private void Update()
    {
        // All GPU submissions happen in the same frame phase, including manual
        // interactions. An input event only enqueues a request.
        if (_readbackPending) return;
        if (_smokeRequested) BeginSmoke();
        else if (!_initialized) InitializeState();
        if (_smokeRunning && completedVectors < inputMasks.Length)
            Clock(inputMasks[completedVectors]);
        else if (_manualCount > 0)
        {
            int mask = _manualQueue[_manualHead];
            _manualHead = (_manualHead + 1) % _manualQueue.Length;
            _manualCount--;
            statusCode = 0;
            Clock(mask);
        }
    }

    public void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        _readbackPending = false;
        if (request.hasError || !request.TryGetData(_pixels))
        {
            _smokeRunning = false;
            statusCode = 3;
            mismatchCount++;
            Debug.LogError("GPU_NETLIST_SMOKE_FAIL readback_error cycle=" + cycleCount);
            UpdateStatus();
            return;
        }
        observedQ = 0;
        for (int i = 0; i < qSignals.Length; i++)
            if (_pixels[qSignals[i]].r >= 128) observedQ |= 1 << i;

        if (_smokeRunning)
        {
            int offset = completedVectors * nodeCount;
            for (int i = 0; i < nodeCount; i++)
            {
                int actual = _pixels[i].r >= 128 ? 1 : 0;
                if (actual != expectedStates[offset + i])
                {
                    mismatchCount++;
                    if (mismatchCount <= 8)
                        Debug.LogError("GPU_NETLIST_MISMATCH vector=" + completedVectors + " signal=" + i + " actual=" + actual + " expected=" + expectedStates[offset + i]);
                }
            }
            if (mismatchCount != 0)
            {
                _smokeRunning = false;
                statusCode = 3;
                Debug.LogError("GPU_NETLIST_SMOKE_FAIL mismatches=" + mismatchCount);
            }
            else
            {
                completedVectors++;
                if (completedVectors == inputMasks.Length)
                {
                    _smokeRunning = false;
                    statusCode = 2;
                    elapsedMilliseconds = (Time.realtimeSinceStartup - _startedAt) * 1000f;
                    Debug.Log("GPU_NETLIST_SMOKE_PASS vectors=" + completedVectors + " nodes=" + nodeCount + " compared_bits=" + completedVectors * nodeCount + " mismatches=0 elapsed_ms=" + elapsedMilliseconds);
                }
            }
        }
        if (!_smokeRunning || completedVectors % 16 == 0) UpdateStatus();
    }

    public void ManualStep()
    {
        EnqueueManual(1 | (1 << 3) | ((dataValue & 15) << 4));
    }

    public void ManualLoad()
    {
        EnqueueManual(1 | (1 << 2) | (9 << 4));
    }

    public void ManualReset()
    {
        EnqueueManual(1 | (1 << 1));
    }

    private void EnqueueManual(int mask)
    {
        if (_smokeRunning || _smokeRequested) return;
        if (_manualCount >= _manualQueue.Length)
        {
            Debug.LogWarning("GPU_NETLIST_MANUAL_QUEUE_FULL");
            return;
        }
        _manualQueue[(_manualHead + _manualCount) % _manualQueue.Length] = mask;
        _manualCount++;
    }

    private void UpdateStatus()
    {
        if (statusText == null) return;
        string state = "MANUAL";
        if (statusCode == 1) state = "TESTING";
        else if (statusCode == 2) state = "PASS";
        else if (statusCode == 3) state = "FAIL";
        statusText.text = state + "   " + completedVectors + " / " + inputMasks.Length + " vectors\nGPU q = " + observedQ + "   cycle = " + cycleCount + "   mismatches = " + mismatchCount;
        statusText.color = statusCode == 3 ? new Color(1f, .3f, .3f) : new Color(.65f, 1f, .8f);
    }
}
