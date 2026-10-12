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
    public CircuitLabLocalization localization;
    public string[] programNames;
    public string[] programDescriptions;
    public string[] programSources;
    public string[] programExpectedTexts;
    public int[] programExpectedCycles;
    public string[] programNamesEn;
    public string[] programNamesZh;
    public string[] programNamesJa;
    public string[] programDescriptionsEn;
    public string[] programDescriptionsZh;
    public string[] programDescriptionsJa;
    public string[] programExpectedTextsEn;
    public string[] programExpectedTextsZh;
    public string[] programExpectedTextsJa;
    public string[] programSourceCommentsEn;
    public string[] programSourceCommentsZh;
    public string[] programSourceCommentsJa;
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
    public GameObject sourceScrollUpButton;
    public GameObject sourceScrollDownButton;
    public GameObject pagePreviousButton;
    public GameObject pageNextButton;
    public int selectedProgram;
    public int browsePage;
    public int browseMode;
    public int pageSize = 12;
    // TMP renders the visible listing; keep this generous so no wrapped line is
    // ever clipped by the scroll content rect.
    public float sourceLineHeight = 15f;
    public string browsingProgramName = "";
    public int browseRevision;

    private int _lastRenderedProgram = -1;
    private int _lastRenderedPage = -1;
    private int _lastRenderedMode = -1;
    private int _lastLoadedRevision = -1;
    private int _lastLoadedCycle = -1;
    private int _lastLoadedRunning = -1;
    private int _lastObservedHalt = -1;
    private int _lastLoadedLanguage = -1;
    private int _lastRenderedLanguage = -1;
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
        int language = CurrentLanguage();
        string loadedName = simulator.programSelect >= 0 ? GetProgramName(simulator.programSelect)
            : simulator.activeProgramName == null ? "" : simulator.activeProgramName;
        if (currentLoadedText != null && (simulator.programRevision != _lastLoadedRevision
            || simulator.cycleCount != _lastLoadedCycle || running != _lastLoadedRunning
            || observedHalt != _lastObservedHalt || loadedName != _lastLoadedName || language != _lastLoadedLanguage))
        {
            _lastLoadedRevision = simulator.programRevision;
            _lastLoadedCycle = simulator.cycleCount;
            _lastLoadedRunning = running;
            _lastObservedHalt = observedHalt;
            _lastLoadedLanguage = language;
            _lastLoadedName = loadedName;
            string state = running != 0 ? "RUNNING" : observedHalt != 0 ? "HALTED" : "PAUSED";
            currentLoadedText.text = "CURRENT LOADED  /  " + loadedName + "  /  REV " + simulator.programRevision + "  /  " + state;
        }
        if (selectedProgram != _lastRenderedProgram || browsePage != _lastRenderedPage || browseMode != _lastRenderedMode
            || language != _lastRenderedLanguage)
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
        if (browseMode == 0 || !TryControl()) return;
        browsePage = Mathf.Max(0, browsePage - 1);
        PublishBrowseState();
        RenderBrowser();
    }

    public void NextPage()
    {
        if (browseMode == 0 || !TryControl()) return;
        browsePage = Mathf.Min(PageCount() - 1, browsePage + 1);
        PublishBrowseState();
        RenderBrowser();
    }

    // Reading the listing is a local view operation: any player may scroll,
    // and the position is never published into the shared browse state.
    public void ScrollSourceUp() { ScrollSource(0.25f); }
    public void ScrollSourceDown() { ScrollSource(-0.25f); }

    private void ScrollSource(float delta)
    {
        if (sourceScrollRect == null) return;
        sourceScrollRect.verticalNormalizedPosition = Mathf.Clamp01(sourceScrollRect.verticalNormalizedPosition + delta);
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

    public int CurrentLanguage()
    {
        return localization == null ? 0 : Mathf.Clamp(localization.languageIndex, 0, 2);
    }

    public string GetProgramName(int index)
    {
        return GetLocalizedProgramValue(index, CurrentLanguage(), programNamesEn, programNamesZh, programNamesJa, programNames);
    }

    public string GetProgramDescription(int index)
    {
        return GetLocalizedProgramValue(index, CurrentLanguage(), programDescriptionsEn, programDescriptionsZh,
            programDescriptionsJa, programDescriptions);
    }

    public string GetProgramExpectedText(int index)
    {
        return GetLocalizedProgramValue(index, CurrentLanguage(), programExpectedTextsEn, programExpectedTextsZh,
            programExpectedTextsJa, programExpectedTexts);
    }

    public string GetProgramSourceComment(int index)
    {
        return GetLocalizedProgramValue(index, CurrentLanguage(), programSourceCommentsEn, programSourceCommentsZh,
            programSourceCommentsJa, null);
    }

    private string GetLocalizedProgramValue(int index, int language, string[] english, string[] chinese,
        string[] japanese, string[] fallback)
    {
        string[] values = language == 1 ? chinese : language == 2 ? japanese : english;
        if (values != null && index >= 0 && index < values.Length && values[index] != null) return values[index];
        if (fallback != null && index >= 0 && index < fallback.Length && fallback[index] != null) return fallback[index];
        return "";
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
        // Trailing newline terminates the last line; it must not add a blank one.
        if (source[source.Length - 1] == '\n') lines--;
        return lines;
    }

    private int PageCount()
    {
        // ASM source scrolls instead of paging; only the ROM word views page.
        if (browseMode == 0) return 1;
        int size = EffectivePageSize();
        int itemCount = 512;
        if (itemCount <= 0) return 1;
        return Mathf.Max(1, (itemCount + size - 1) / size);
    }

    private void RenderBrowser()
    {
        if (simulator == null || simulator.programCount <= 0) return;
        selectedProgram = Mathf.Clamp(selectedProgram, 0, simulator.programCount - 1);
        browseMode = Mathf.Clamp(browseMode, 0, 2);
        browsePage = Mathf.Clamp(browsePage, 0, PageCount() - 1);
        browsingProgramName = GetProgramName(selectedProgram);
        if (browsingProgramName.Length == 0) browsingProgramName = "PROGRAM " + selectedProgram;
        if (browsingProgramText != null)
            browsingProgramText.text = "BROWSING  /  " + browsingProgramName + "  /  PRESET " + selectedProgram;
        if (descriptionText != null)
        {
            string description = GetProgramDescription(selectedProgram);
            string comment = GetProgramSourceComment(selectedProgram);
            descriptionText.text = comment.Length == 0 ? description : description + "\n" + comment;
        }
        if (expectedOutputText != null)
        {
            string expected = GetProgramExpectedText(selectedProgram);
            expected = FormatExpectedOutput(expected);
            int cycles = programExpectedCycles != null && selectedProgram < programExpectedCycles.Length ? programExpectedCycles[selectedProgram] : 0;
            expectedOutputText.text = "EXPECTED RESULT  /  " + expected + (cycles > 0 ? "  /  " + cycles + " CYCLES" : "");
        }

        if (browseMode == 0) RenderSource();
        else RenderEncodingPage();
        if (sourcePane != null) sourcePane.SetActive(browseMode == 0);
        if (encodingPane != null) encodingPane.SetActive(browseMode != 0);
        bool paging = browseMode != 0;
        if (pagePreviousButton != null) pagePreviousButton.SetActive(paging);
        if (pageNextButton != null) pageNextButton.SetActive(paging);
        if (sourceScrollUpButton != null) sourceScrollUpButton.SetActive(!paging);
        if (sourceScrollDownButton != null) sourceScrollDownButton.SetActive(!paging);
        _lastRenderedProgram = selectedProgram;
        _lastRenderedPage = browsePage;
        _lastRenderedMode = browseMode;
        _lastRenderedLanguage = CurrentLanguage();
    }

    private string FormatExpectedOutput(string value)
    {
        if (value == null) return "";
        bool raw = false;
        for (int i = 0; i < value.Length; i++)
            if ((value[i] < 32 && value[i] != '\n' && value[i] != '\r') || value[i] > 126) raw = true;
        if (!raw) return value.Replace("\r", "").Replace("\n", "\\n");
        string result = "BYTES ";
        for (int i = 0; i < value.Length; i++) result += (i == 0 ? "" : " ") + Hex(value[i] & 255, 2);
        return result;
    }

    // The whole listing is rendered at once and read by scrolling; the pane has
    // no notion of pages so a program can never appear truncated.
    private void RenderSource()
    {
        string source = ProgramSource(selectedProgram);
        int total = SourceLineCount(source);
        string output = "";
        if (total <= 0)
            output = "ASM SOURCE NOT AVAILABLE\n";
        else
        {
            int lineNumber = 0;
            int lineStart = 0;
            for (int i = 0; i <= source.Length && lineNumber < total; i++)
            {
                if (i < source.Length && source[i] != '\n') continue;
                string line = source.Substring(lineStart, i - lineStart).Replace("\r", "");
                output += FormatLineNumber(lineNumber + 1) + " | " + WrapSourceLine(line) + "\n";
                lineNumber++;
                lineStart = i + 1;
            }
        }
        if (sourceText != null) sourceText.text = output;
        ResizeContent(output, sourceContentRect, sourceTextRect, sourceScrollRect, Mathf.Max(8f, sourceLineHeight));
        if (pageText != null)
            pageText.text = total <= 0 ? "ASM SOURCE  /  NOT PROVIDED"
                : "ASM SOURCE  /  " + total + " LINES  /  MONO  /  SCROLL UP-DOWN";
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
