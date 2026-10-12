using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

// Display only: every bit and cell comes from the GPU's actual state/RAM.
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabOutputDisplay : UdonSharpBehaviour
{
    public CircuitLabSimulator simulator;
    public CircuitLabLocalization localization;
    public Material ledMaterial, lifeMaterial;
    public Text ledStatus, lifeStatus;
    public int lifeProgram = -1;
    public int bufferA = 256, bufferB = 320, selectorAddress = 224, generationAddress = 225;
    private void Update()
    {
        if (simulator == null) return;
        int language = localization == null ? 0 : localization.languageIndex;
        if (ledMaterial != null)
        {
            ledMaterial.SetTexture("_StateTex", simulator.currentState);
            ledMaterial.SetInt("_StateWidth", simulator.textureWidth);
            int[] signals = simulator.outSignals;
            if (signals != null && signals.Length >= 8)
            {
                ledMaterial.SetVector("_Out0", new Vector4(signals[0], signals[1], signals[2], signals[3]));
                ledMaterial.SetVector("_Out1", new Vector4(signals[4], signals[5], signals[6], signals[7]));
            }
        }
        if (lifeMaterial != null)
        {
            lifeMaterial.SetTexture("_RamTex", simulator.currentRam);
            lifeMaterial.SetInt("_BufferA", bufferA); lifeMaterial.SetInt("_BufferB", bufferB);
            lifeMaterial.SetInt("_Selector", selectorAddress);
            lifeMaterial.SetInt("_Enabled", simulator.programSelect == lifeProgram ? 1 : 0);
        }
        if (ledStatus != null) ledStatus.text = "OUT[7:0] = 0x" + Hex(simulator.observedOut) + "  /  " + Binary(simulator.observedOut)
            + "\n" + simulator.activeProgramName;
        if (lifeStatus != null)
        {
            bool active = simulator.programSelect == lifeProgram;
            // The displayed counter follows the last completed stable GPU readback while RUN advances.
            byte[] ram = simulator.observedRam;
            int generation = ram != null && simulator.observedRamCycle >= 0 ? ram[generationAddress] : -1;
            int selector = ram != null && simulator.observedRamCycle >= 0 ? ram[selectorAddress] : -1;
            if (generation >= 0 && selector >= 0 && (generation & 1) != selector) generation = (generation + 1) & 255;
            lifeStatus.text = active
                ? (language == 1 ? "代数 " : language == 2 ? "世代 " : "GENERATION ") + generation + " / mod 256"
                    + "  ·  " + (language == 1 ? "CPU 双缓冲 RAM" : language == 2 ? "CPU ダブルバッファ RAM" : "CPU DOUBLE BUFFER RAM")
                : language == 1 ? "在程序列表加载 Conway 生命游戏" : language == 2 ? "プログラム一覧から Conway ライフゲームをロード" : "LOAD CONWAY LIFE FROM THE PROGRAM LIST";
        }
    }
    private string Hex(int value) { string chars = "0123456789ABCDEF"; return "" + chars[(value >> 4) & 15] + chars[value & 15]; }
    private string Binary(int value)
    {
        string result = "";
        for (int bit = 7; bit >= 0; bit--) result += ((value >> bit) & 1) == 0 ? "0" : "1";
        return result;
    }
}
