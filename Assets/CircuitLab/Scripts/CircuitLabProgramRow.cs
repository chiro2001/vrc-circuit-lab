using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabProgramRow : UdonSharpBehaviour
{
    public CircuitLabProgramBrowser browser;
    public int programIndex;
    public Text label;
    public Button uiButton;
    private int _lastBrowserSelection = -1;
    private int _lastLoadedPreset = -2;

    public void Select()
    {
        if (browser != null) browser.SelectProgram(programIndex);
    }

    private void Update()
    {
        if (browser == null || label == null || browser.simulator == null) return;
        int loaded = browser.simulator.programSelect;
        if (_lastBrowserSelection == browser.selectedProgram && _lastLoadedPreset == loaded) return;
        _lastBrowserSelection = browser.selectedProgram; _lastLoadedPreset = loaded;
        string name = browser.programNames != null && programIndex < browser.programNames.Length ? browser.programNames[programIndex] : "PROGRAM " + programIndex;
        bool isCurrent = programIndex == loaded;
        bool isBrowsing = programIndex == browser.selectedProgram;
        string marker = "";
        if (isCurrent) marker = "CURRENT";
        if (isBrowsing) marker = marker.Length == 0 ? "BROWSING" : marker + " / BROWSING";
        label.text = marker.Length == 0 ? name : marker + "  /  " + name;
        label.color = programIndex == browser.selectedProgram ? new Color(.85f, 1f, .83f) : Color.white;
    }
}
