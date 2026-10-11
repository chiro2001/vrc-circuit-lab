using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabProgramBrowser : UdonSharpBehaviour
{
    public CircuitLabSimulator simulator;
    public CircuitLabNetworkCoordinator coordinator;
    public CircuitLabProgramDraft sharedState;
    public CircuitLabProgramPanel editorPanel;
    public string[] programNames;
    public string[] programDescriptions;
    public string[] programSources;
    public string[] programExpectedTexts;
    public int[] programExpectedCycles;
    public Text currentLoadedText;
    public Text browsingProgramText;
    public Text descriptionText;
    public Text expectedOutputText;
    public Text sourceText;
    public Text encodingText;
    public Text pageText;
    public Text statusText;
    public GameObject sourcePane;
    public GameObject encodingPane;
    public ScrollRect sourceScrollRect;
    public RectTransform sourceContentRect;
    public RectTransform sourceTextRect;
    public ScrollRect encodingScrollRect;
    public RectTransform encodingContentRect;
    public RectTransform encodingTextRect;
    public ScrollRect programListScrollRect;
    public int selectedProgram;
    public int browsePage;
    public int browseMode;
    public int pageSize = 12;
    public string browsingProgramName = "";
    public int browseRevision;

    private int _lastRenderedProgram = -1;
    private int _lastRenderedPage = -1;
    private int _lastRenderedMode = -1;
    private int _lastLoadedRevision = -1;
    private int _lastLoadedCycle = -1;
    private int _lastLoadedRunning = -1;
    private int _lastObservedHalt = -1;
    private string _lastLoadedName = "";

    private bool TryControl()
    {
        if (coordinator == null || coordinator.HasControl()) return true;
        if (coordinator.controlAuthorityId < 0) return coordinator.TryAcquireUnclaimedControl();
        coordinator.RequestControl();
        return false;
    }

    private void Update()
    {
        if (simulator == null) return;
        if (programListScrollRect != null)
            programListScrollRect.enabled = coordinator == null || coordinator.CanUseControls();

        int running = simulator.running ? 1 : 0;
        int observedHalt = simulator.observedHalt;
        string loadedName = simulator.activeProgramName == null ? "" : simulator.activeProgramName;
        if (currentLoadedText != null && (simulator.programRevision != _lastLoadedRevision
            || simulator.cycleCount != _lastLoadedCycle || running != _lastLoadedRunning
            || observedHalt != _lastObservedHalt || loadedName != _lastLoadedName))
        {
            _lastLoadedRevision = simulator.programRevision;
            _lastLoadedCycle = simulator.cycleCount;
            _lastLoadedRunning = running;
            _lastObservedHalt = observedHalt;
            _lastLoadedName = loadedName;
            string state = running != 0 ? "RUNNING" : observedHalt != 0 ? "HALTED" : "PAUSED";
            currentLoadedText.text = "CURRENT LOADED  /  " + loadedName + "  /  REV " + simulator.programRevision + "  /  " + state;
        }
        if (selectedProgram != _lastRenderedProgram || browsePage != _lastRenderedPage || browseMode != _lastRenderedMode)
            RenderBrowser();
    }

    public void SelectProgram(int index)
    {
        if (!TryControl()) return;
        if (simulator == null || index < 0 || index >= simulator.programCount)
        { SetStatus("PROGRAM INDEX OUT OF RANGE"); return; }
        selectedProgram = index;
        browsePage = 0;
        PublishBrowseState();
        RenderBrowser();
    }

    public void LoadSelectedPreset()
    {
        if (simulator == null || !TryControl()) return;
        if (selectedProgram < 0 || selectedProgram >= simulator.programCount)
        { SetStatus("SELECT A PRESET FIRST"); return; }
        int queued = simulator.queuedCommandCount;
        int rejected = simulator.rejectedCommandCount;
        simulator.LoadPreset(selectedProgram);
        if (simulator.queuedCommandCount == queued || simulator.rejectedCommandCount > rejected)
        { SetStatus("LOAD REJECTED  /  LOAD QUEUE UNAVAILABLE"); return; }
        SetStatus("LOAD QUEUED  /  CPU RESET + RAM INITIALIZED AT STABLE EDGE");
    }

    public void PreviousPage()
    {
        if (!TryControl()) return;
        browsePage = Mathf.Max(0, browsePage - 1);
        PublishBrowseState();
        RenderBrowser();
    }

    public void NextPage()
    {
        if (!TryControl()) return;
        browsePage = Mathf.Min(PageCount() - 1, browsePage + 1);
        PublishBrowseState();
        RenderBrowser();
    }

    public void ShowSource() { SetBrowseMode(0); }
    public void ShowEncoding() { SetBrowseMode(1); }
    public void ShowBinaryEncoding() { SetBrowseMode(2); }

    public void ScrollListUp()
    {
        if (!TryControl() || programListScrollRect == null) return;
        programListScrollRect.verticalNormalizedPosition = Mathf.Clamp01(programListScrollRect.verticalNormalizedPosition + .18f);
    }

    public void ScrollListDown()
    {
        if (!TryControl() || programListScrollRect == null) return;
        programListScrollRect.verticalNormalizedPosition = Mathf.Clamp01(programListScrollRect.verticalNormalizedPosition - .18f);
    }

    public void ClaimControl()
    {
        if (coordinator == null || coordinator.HasControl()) return;
        if (coordinator.controlAuthorityId < 0) coordinator.TryAcquireUnclaimedControl();
        else coordinator.RequestControl();
    }

    public void ApplySharedBrowserState(int program, int page, int mode, int revision)
    {
        if (coordinator != null && coordinator.HasControl()) return;
        selectedProgram = Mathf.Clamp(program, 0, Mathf.Max(0, simulator == null ? 0 : simulator.programCount - 1));
        browseMode = Mathf.Clamp(mode, 0, 2);
        browsePage = Mathf.Clamp(page, 0, PageCount() - 1);
        browseRevision = revision;
        RenderBrowser();
    }

    public void PublishBrowseState()
    {
        if (sharedState == null || coordinator != null && !coordinator.HasControl()) return;
        sharedState.selectedPreset = selectedProgram;
        sharedState.browsePage = browsePage;
        sharedState.browseMode = browseMode;
        sharedState.Publish();
        if (editorPanel != null) editorPanel.selectedPreset = selectedProgram;
    }

    private void SetBrowseMode(int mode)
    {
        if (!TryControl()) return;
        browseMode = mode;
        browsePage = 0;
        PublishBrowseState();
        RenderBrowser();
    }

    private int EffectivePageSize()
    {
        return Mathf.Max(1, pageSize);
    }

    private string ProgramSource(int index)
    {
        if (programSources == null || index < 0 || index >= programSources.Length || programSources[index] == null)
            return "";
        return programSources[index];
    }

    private int SourceLineCount(string source)
    {
        if (source == null || source.Length == 0) return 0;
        int lines = 1;
        for (int i = 0; i < source.Length; i++)
            if (source[i] == '\n') lines++;
        return lines;
    }

    private int PageCount()
    {
        int size = EffectivePageSize();
        int itemCount = browseMode == 0 ? SourceLineCount(ProgramSource(selectedProgram)) : 512;
        if (itemCount <= 0) return 1;
        return Mathf.Max(1, (itemCount + size - 1) / size);
    }

    private void RenderBrowser()
    {
        if (simulator == null || simulator.programCount <= 0) return;
        selectedProgram = Mathf.Clamp(selectedProgram, 0, simulator.programCount - 1);
        browseMode = Mathf.Clamp(browseMode, 0, 2);
        browsePage = Mathf.Clamp(browsePage, 0, PageCount() - 1);
        browsingProgramName = programNames != null && selectedProgram < programNames.Length ? programNames[selectedProgram] : "PROGRAM " + selectedProgram;
        if (browsingProgramText != null)
            browsingProgramText.text = "BROWSING  /  " + browsingProgramName + "  /  PRESET " + selectedProgram;
        if (descriptionText != null)
            descriptionText.text = programDescriptions != null && selectedProgram < programDescriptions.Length ? programDescriptions[selectedProgram] : "";
        if (expectedOutputText != null)
        {
            string expected = programExpectedTexts != null && selectedProgram < programExpectedTexts.Length ? programExpectedTexts[selectedProgram] : "";
            expected = expected == null ? "" : expected.Replace("\r", "").Replace("\n", "\\n");
            int cycles = programExpectedCycles != null && selectedProgram < programExpectedCycles.Length ? programExpectedCycles[selectedProgram] : 0;
            expectedOutputText.text = "EXPECTED RESULT  /  " + expected + (cycles > 0 ? "  /  " + cycles + " CYCLES" : "");
        }

        if (browseMode == 0) RenderSourcePage();
        else RenderEncodingPage();
        if (sourcePane != null) sourcePane.SetActive(browseMode == 0);
        if (encodingPane != null) encodingPane.SetActive(browseMode != 0);
        _lastRenderedProgram = selectedProgram;
        _lastRenderedPage = browsePage;
        _lastRenderedMode = browseMode;
    }

    private void RenderSourcePage()
    {
        string source = ProgramSource(selectedProgram);
        int total = SourceLineCount(source);
        int size = EffectivePageSize();
        int firstLine = browsePage * size;
        int endLine = Mathf.Min(total, firstLine + size);
        string output = "";
        if (total <= 0)
            output = "ASM SOURCE NOT AVAILABLE\n";
        else
        {
            int lineNumber = 0;
            int lineStart = 0;
            for (int i = 0; i <= source.Length; i++)
            {
                if (i < source.Length && source[i] != '\n') continue;
                if (lineNumber >= firstLine && lineNumber < endLine)
                {
                    string line = source.Substring(lineStart, i - lineStart).Replace("\r", "");
                    output += FormatLineNumber(lineNumber + 1) + " | " + WrapSourceLine(line) + "\n";
                }
                lineNumber++;
                lineStart = i + 1;
            }
        }
        if (sourceText != null) sourceText.text = output;
        ResizeContent(output, sourceContentRect, sourceTextRect, sourceScrollRect, 15f);
        if (pageText != null)
        {
            if (total <= 0) pageText.text = "ASM SOURCE  /  NOT PROVIDED";
            else pageText.text = "ASM SOURCE  /  LINES " + (firstLine + 1) + "–" + endLine + " OF " + total
                + "  /  PAGE " + (browsePage + 1) + " OF " + PageCount();
        }
        if (sourceScrollRect != null) sourceScrollRect.verticalNormalizedPosition = 1f;
    }

    private string WrapSourceLine(string line)
    {
        const int width = 68;
        string output = "";
        for (int i = 0; i < line.Length; i++)
        {
            if (i > 0 && i % width == 0) output += "\n      ";
            output += line[i];
        }
        return output;
    }

    private string FormatLineNumber(int lineNumber)
    {
        string value = "" + lineNumber;
        while (value.Length < 3) value = "0" + value;
        return value;
    }

    private void RenderEncodingPage()
    {
        int size = EffectivePageSize();
        int start = browsePage * size;
        string output = "";
        for (int row = 0; row < size; row++)
        {
            int address = start + row;
            if (address >= 512) break;
            int index = selectedProgram * 512 + address;
            int word = simulator.presetProgramWords != null && index < simulator.presetProgramWords.Length
                ? simulator.presetProgramWords[index] & 0xFFF : 0;
            output += "0x" + Hex(address, 3) + "  "
                + (browseMode == 1 ? "0x" + Hex(word, 3) : Binary(word, 12)) + "\n";
        }
        if (encodingText != null) encodingText.text = output;
        ResizeContent(output, encodingContentRect, encodingTextRect, encodingScrollRect, 18f);
        if (pageText != null)
            pageText.text = "ROM WORDS  /  PAGE " + (browsePage + 1) + " OF " + PageCount()
                + (browseMode == 1 ? "  /  HEX" : "  /  BIN");
        if (encodingScrollRect != null) encodingScrollRect.verticalNormalizedPosition = 1f;
    }

    private void ResizeContent(string text, RectTransform content, RectTransform textRect, ScrollRect scrollRect, float lineHeight)
    {
        if (content == null) return;
        int lineCount = 1;
        if (text != null)
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') lineCount++;
        float minimumHeight = scrollRect != null && scrollRect.viewport != null ? scrollRect.viewport.rect.height : 240f;
        float height = Mathf.Max(minimumHeight, lineCount * lineHeight + 8f);
        content.sizeDelta = new Vector2(content.sizeDelta.x, height);
        if (textRect != null) textRect.sizeDelta = new Vector2(textRect.sizeDelta.x, height);
    }

    private void SetStatus(string value)
    {
        if (statusText != null) statusText.text = value;
    }

    private string Hex(int value, int digits)
    {
        string chars = "0123456789ABCDEF";
        string output = "";
        for (int i = digits - 1; i >= 0; i--) output += chars[(value >> (i * 4)) & 15];
        return output;
    }

    private string Binary(int value, int digits)
    {
        string output = "";
        for (int i = digits - 1; i >= 0; i--) output += ((value >> i) & 1) == 0 ? "0" : "1";
        return output;
    }
}
