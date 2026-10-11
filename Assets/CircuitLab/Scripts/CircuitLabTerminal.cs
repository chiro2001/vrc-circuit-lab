using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabTerminal : UdonSharpBehaviour
{
    public CircuitLabSimulator simulator;
    public CircuitLabNetworkCoordinator coordinator;
    public Text outputText;
    public Text statusText;
    public Material captureMaterial;
    public RenderTexture eventA;
    public RenderTexture eventB;
    public int eventCapacity = 256;
    public int outputWriteSignal = -1;
    public int[] outputSignals;
    public int[] sequenceSignals;
    public int maxCharacters = 2048;
    public float readbackInterval = 0.1f;
    public string output = "";
    public int eventCount;
    public int lastProcessedCycle;
    public int missedCycles;
    public int readbackErrors;
    public bool readbackPending;
    private RenderTexture _readEvents;
    private RenderTexture _writeEvents;
    private Color32[] _pixels;
    private int _requestedCycle;
    private int _generation;
    private int _requestGeneration;
    public int cursorColumn;
    public int lineStart;
    private int _lastSequence = -1;
    private float _nextReadAt;
    private string _ascii = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

    private bool Prepare()
    {
        if (simulator == null || captureMaterial == null || eventA == null || eventB == null || outputSignals == null || sequenceSignals == null
            || outputSignals.Length < 8 || sequenceSignals.Length < 8 || outputWriteSignal < 0) return false;
        if (_readEvents == null) { _readEvents = eventA; _writeEvents = eventB; _pixels = new Color32[eventCapacity * 2]; }
        captureMaterial.SetInt("_EventMode", 1);
        captureMaterial.SetInt("_CopyMode", 0);
        captureMaterial.SetInt("_RestoreMode", 0);
        captureMaterial.SetInt("_ClearMode", 0);
        captureMaterial.SetInt("_TextureWidth", simulator.textureWidth);
        captureMaterial.SetInt("_EventCapacity", eventCapacity);
        captureMaterial.SetInt("_WriteSignal", outputWriteSignal);
        captureMaterial.SetVector("_OutputSignalsA", new Vector4(outputSignals[0], outputSignals[1], outputSignals[2], outputSignals[3]));
        captureMaterial.SetVector("_OutputSignalsB", new Vector4(outputSignals[4], outputSignals[5], outputSignals[6], outputSignals[7]));
        captureMaterial.SetVector("_SequenceSignalsA", new Vector4(sequenceSignals[0], sequenceSignals[1], sequenceSignals[2], sequenceSignals[3]));
        captureMaterial.SetVector("_SequenceSignalsB", new Vector4(sequenceSignals[4], sequenceSignals[5], sequenceSignals[6], sequenceSignals[7]));
        return true;
    }

    public void RecordCycle(RenderTexture state, int cycle)
    {
        if (!Prepare()) return;
        captureMaterial.SetTexture("_StateTex", state);
        captureMaterial.SetInt("_WriteSlot", cycle % eventCapacity);
        captureMaterial.SetVector("_EventCycleBytes", new Vector4(cycle & 255, (cycle >> 8) & 255, (cycle >> 16) & 255, (cycle >> 24) & 255));
        VRCGraphics.Blit(_readEvents, _writeEvents, captureMaterial);
        RenderTexture previous = _readEvents; _readEvents = _writeEvents; _writeEvents = previous;
    }

    public void ResetOutput()
    {
        output = ""; eventCount = 0; lastProcessedCycle = 0; cursorColumn = 0; lineStart = 0; _lastSequence = -1;
        _generation++;
        if (!Prepare()) return;
        captureMaterial.SetInt("_ClearMode", 1);
        VRCGraphics.Blit(_readEvents, _writeEvents, captureMaterial);
        RenderTexture previous = _readEvents; _readEvents = _writeEvents; _writeEvents = previous;
        captureMaterial.SetInt("_ClearMode", 0);
    }

    public void ApplySharedOutput(string text, int count, int cycle)
    {
        output = text == null ? "" : text;
        eventCount = count; lastProcessedCycle = cycle;
        lineStart = output.LastIndexOf("\n") + 1; cursorColumn = output.Length - lineStart;
        _lastSequence = -1; _generation++;
    }

    private void Update()
    {
        if (!Prepare()) return;
        if (!readbackPending && simulator.cycleCount > lastProcessedCycle && Time.realtimeSinceStartup >= _nextReadAt)
        {
            _requestedCycle = simulator.cycleCount; _requestGeneration = _generation;
            readbackPending = true;
            _nextReadAt = Time.realtimeSinceStartup + Mathf.Max(0.03f, readbackInterval);
            VRCAsyncGPUReadback.Request(_readEvents, 0, this);
        }
        if (outputText != null) outputText.text = output;
        if (statusText != null) statusText.text = "TERMINAL | " + eventCount + " writes" + (missedCycles == 0 ? "" : " | capture gap " + missedCycles);
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (_requestGeneration != _generation) return;
        if (request.hasError || !request.TryGetData(_pixels)) { readbackErrors++; return; }
        int first = Mathf.Max(lastProcessedCycle + 1, _requestedCycle - eventCapacity + 1);
        if (first > lastProcessedCycle + 1) missedCycles += first - lastProcessedCycle - 1;
        for (int cycle = first; cycle <= _requestedCycle; cycle++)
        {
            int slot = cycle % eventCapacity;
            Color32 stamp = _pixels[eventCapacity + slot];
            int recorded = stamp.r | (stamp.g << 8) | (stamp.b << 16) | (stamp.a << 24);
            if (recorded != cycle) { missedCycles++; continue; }
            Color32 value = _pixels[slot];
            if (value.g < 128) { _lastSequence = value.b; continue; }
            if (value.b == _lastSequence) continue;
            _lastSequence = value.b;
            AppendByte(value.r);
            eventCount++;
        }
        lastProcessedCycle = _requestedCycle;
    }

    private void AppendByte(int value)
    {
        if (value == 12) { output = ""; cursorColumn = 0; lineStart = 0; }
        else if (value == 13) cursorColumn = 0;
        else if (value == 10) { output += "\n"; lineStart = output.Length; cursorColumn = 0; }
        else if (value == 8)
        {
            if (cursorColumn > 0) { int position = lineStart + cursorColumn - 1; output = output.Substring(0, position) + output.Substring(position + 1); cursorColumn--; }
        }
        else if (value >= 32 && value <= 126)
        {
            string character = _ascii.Substring(value - 32, 1);
            int position = lineStart + cursorColumn;
            if (position < output.Length) output = output.Substring(0, position) + character + output.Substring(position + 1);
            else output += character;
            cursorColumn++;
        }
        if (output.Length > maxCharacters)
        {
            int trim = output.Length - maxCharacters;
            int newline = output.IndexOf("\n", trim);
            if (newline >= 0) trim = newline + 1;
            output = output.Substring(trim);
            lineStart = Mathf.Max(0, lineStart - trim);
            cursorColumn = Mathf.Min(cursorColumn, output.Length - lineStart);
        }
    }
}
