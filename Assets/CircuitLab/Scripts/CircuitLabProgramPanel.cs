using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabProgramPanel : UdonSharpBehaviour
{
    private const int ProgramCapacity = 512;
    private const int HaltWord = 0xF00;
    public CircuitLabSimulator simulator;
    public CircuitLabNetworkCoordinator coordinator;
    public CircuitLabProgramDraft sharedDraft;
    public CircuitLabLocalization localization;
    public InputField draftInput;
    public Text programCurrentTitle;
    public Text analyzerProgramTitle;
    public Text currentDescription;
    public Text listingText;
    public Text isaText;
    public Text editorStatus;
    public Text selectedPresetText;
    public Text virtualWordText;
    public Text[] bitTexts;
    public Text[] instructionRows;
    public int inputMode;
    public int selectedPreset;
    public int programPage;
    public int virtualWord;
    public string currentProgramName = "";
    public int displayedProgramRevision = -1;
    public string[] opcodeNames;
    public int[] opcodeValues;
    public string[] extensionNames;
    public string[] programDescriptions;
    public string[] programNames;
    public string[] expectedText;
    public int[] expectedCycles;
    public string[] programNamesEn;
    public string[] programNamesZh;
    public string[] programNamesJa;
    public string[] programDescriptionsEn;
    public string[] programDescriptionsZh;
    public string[] programDescriptionsJa;
    public string[] expectedTextsEn;
    public string[] expectedTextsZh;
    public string[] expectedTextsJa;
    public int programWordCount = ProgramCapacity;
    public int pageSize = 8;

    private string _lastDraft = "";
    private int _lastInputMode = -1;
    private bool _applyingSharedDraft;
    private float _shareAt;
    private int _lastRenderedRevision = -1;
    private int[] _parsedWords = new int[ProgramCapacity];
    private int _parsedCount;
    private int _parseErrorCount;
    private string _parseErrors = "";

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
        currentProgramName = simulator.activeProgramName == null ? "" : simulator.activeProgramName;
        displayedProgramRevision = simulator.programRevision;
        bool hasControl = coordinator == null || coordinator.HasControl();
        if (draftInput != null)
        {
            draftInput.interactable = coordinator == null || coordinator.CanUseControls();
            bool draftChanged = draftInput.text != _lastDraft || inputMode != _lastInputMode;
            if (!_applyingSharedDraft && draftChanged && !hasControl)
            {
                if (coordinator != null && coordinator.controlAuthorityId < 0 && coordinator.TryAcquireUnclaimedControl())
                    hasControl = true;
                else
                {
                    if (coordinator != null) coordinator.RequestControl();
                    _applyingSharedDraft = true;
                    draftInput.text = _lastDraft;
                    _applyingSharedDraft = false;
                    inputMode = _lastInputMode < 0 ? inputMode : _lastInputMode;
                    draftChanged = false;
                }
            }
            if (hasControl && !_applyingSharedDraft && draftChanged)
            {
                _lastDraft = draftInput.text;
                _lastInputMode = inputMode;
                _shareAt = Time.realtimeSinceStartup + 0.65f;
            }
            bool sharedChanged = sharedDraft != null && (sharedDraft.sourceText != _lastDraft || sharedDraft.sourceMode != inputMode
                || sharedDraft.selectedPreset != selectedPreset || sharedDraft.programPage != programPage || sharedDraft.virtualWord != virtualWord);
            if (hasControl && sharedDraft != null && sharedChanged && Time.realtimeSinceStartup >= _shareAt)
            {
                sharedDraft.sourceText = _lastDraft;
                sharedDraft.sourceMode = inputMode;
                sharedDraft.selectedPreset = selectedPreset;
                sharedDraft.programPage = programPage;
                sharedDraft.virtualWord = virtualWord;
                sharedDraft.Publish();
            }
        }
        if (selectedPresetText != null)
        {
            string presetName = selectedPreset >= 0 ? ProgramName(selectedPreset) : "CUSTOM";
            if (presetName.Length == 0) presetName = "CUSTOM";
            selectedPresetText.text = "SELECTED PRESET " + selectedPreset + "  /  " + presetName;
        }
        if (_lastRenderedRevision != simulator.programRevision || _lastPage != programPage
            || _lastObservedPc != simulator.observedPc || _lastObservedPhase != simulator.observedPhase)
        {
            _lastRenderedRevision = simulator.programRevision;
            _lastPage = programPage;
            _lastObservedPc = simulator.observedPc;
            _lastObservedPhase = simulator.observedPhase;
            RenderProgram();
        }
        if (programCurrentTitle != null)
        {
            string execution = simulator.running ? "RUNNING" : simulator.observedHalt != 0 ? "HALTED" : "PAUSED";
            string localizedName = simulator.programSelect >= 0 ? ProgramName(simulator.programSelect) : currentProgramName;
            programCurrentTitle.text = "CURRENT PROGRAM  /  " + localizedName + "  /  REV " + displayedProgramRevision + "  /  " + execution;
            if (analyzerProgramTitle != null)
                analyzerProgramTitle.text = "PROGRAM  /  " + localizedName + "  /  REV " + displayedProgramRevision + "  /  " + execution;
        }
        if (currentDescription != null)
        {
            string description = "";
            if (simulator.programSelect >= 0)
                description = ProgramDescription(simulator.programSelect);
            else
                description = CurrentLanguage() == 1 ? "由控制者加载的共享自定义草稿。"
                    : CurrentLanguage() == 2 ? "操作権を持つプレイヤーがロードした共有カスタムドラフト。"
                    : "Custom shared draft loaded by the controller.";
            if (simulator.programSelect >= 0 && ProgramExpectedText(simulator.programSelect).Length > 0)
            {
                string output = ProgramExpectedText(simulator.programSelect).Replace("\r", "").Replace("\n", "\\n");
                description += "  /  EXPECTED: " + output;
            }
            if (simulator.programSelect >= 0 && expectedCycles != null && simulator.programSelect < expectedCycles.Length
                && expectedCycles[simulator.programSelect] > 0)
                description += "  /  EXPECTED CYCLES: " + expectedCycles[simulator.programSelect];
            currentDescription.text = description;
        }
        if (virtualWordText != null) virtualWordText.text = "WORD 0x" + Hex(virtualWord, 3) + "  /  " + Binary(virtualWord, 12);
        if (bitTexts != null)
            for (int bit = 0; bit < bitTexts.Length; bit++)
                if (bitTexts[bit] != null) bitTexts[bit].text = "B" + (11 - bit) + "  " + (((virtualWord >> (11 - bit)) & 1) == 0 ? "0" : "1");
    }

    private void Start()
    {
        if (draftInput != null) _lastDraft = draftInput.text;
        _lastInputMode = inputMode;
    }

    private int _lastPage = -1;
    private int _lastObservedPc = -1;
    private int _lastObservedPhase = -1;

    private int CurrentLanguage()
    {
        return localization == null ? 0 : Mathf.Clamp(localization.languageIndex, 0, 2);
    }

    private string LocalizedValue(int index, int language, string[] english, string[] chinese, string[] japanese,
        string[] fallback)
    {
        string[] values = language == 1 ? chinese : language == 2 ? japanese : english;
        if (values != null && index >= 0 && index < values.Length && values[index] != null) return values[index];
        if (fallback != null && index >= 0 && index < fallback.Length && fallback[index] != null) return fallback[index];
        return "";
    }

    private string ProgramName(int index)
    {
        return LocalizedValue(index, CurrentLanguage(), programNamesEn, programNamesZh, programNamesJa, programNames);
    }

    private string ProgramDescription(int index)
    {
        return LocalizedValue(index, CurrentLanguage(), programDescriptionsEn, programDescriptionsZh,
            programDescriptionsJa, programDescriptions);
    }

    private string ProgramExpectedText(int index)
    {
        return LocalizedValue(index, CurrentLanguage(), expectedTextsEn, expectedTextsZh,
            expectedTextsJa, expectedText);
    }


    public void ApplySharedDraft(string text, int mode, int preset, int sharedPage, int word, int revision)
    {
        if (coordinator != null && coordinator.HasControl()) return;
        _applyingSharedDraft = true;
        inputMode = mode;
        _lastDraft = text == null ? "" : text;
        _lastInputMode = inputMode;
        selectedPreset = preset;
        programPage = Mathf.Clamp(sharedPage, 0, (ProgramCapacity - 1) / Mathf.Max(1, pageSize));
        virtualWord = word & 0xFFF;
        if (draftInput != null) draftInput.text = _lastDraft;
        _applyingSharedDraft = false;
        if (editorStatus != null) editorStatus.text = "SHARED DRAFT  /  REV " + revision;
    }

    public void ClaimControl()
    {
        if (coordinator == null || coordinator.HasControl()) return;
        if (coordinator.controlAuthorityId < 0) coordinator.TryAcquireUnclaimedControl();
        else coordinator.RequestControl();
    }

    public void SetInputHex() { if (!TryControl()) return; inputMode = 0; SetEditorStatus("INPUT MODE: HEX"); }
    public void SetInputBinary() { if (!TryControl()) return; inputMode = 1; SetEditorStatus("INPUT MODE: BINARY"); }

    public void PreviousPreset()
    {
        if (!TryControl()) return;
        int count = simulator == null ? 0 : simulator.programCount;
        if (count <= 0) return;
        selectedPreset = (selectedPreset + count - 1) % count;
    }

    public void NextPreset()
    {
        if (!TryControl()) return;
        int count = simulator == null ? 0 : simulator.programCount;
        if (count <= 0) return;
        selectedPreset = (selectedPreset + 1) % count;
    }

    public void LoadSelectedPreset()
    {
        if (simulator == null || !TryControl()) return;
        if (selectedPreset < 0 || selectedPreset >= simulator.programCount)
        { SetEditorStatus("PRESET INDEX OUT OF RANGE"); return; }
        int queuedBefore = simulator.queuedCommandCount;
        int rejectedBefore = simulator.rejectedCommandCount;
        simulator.LoadPreset(selectedPreset);
        if (simulator.queuedCommandCount == queuedBefore || simulator.rejectedCommandCount > rejectedBefore)
        { SetEditorStatus("PRESET LOAD REJECTED  /  LOAD QUEUE UNAVAILABLE"); return; }
        SetEditorStatus("LOAD QUEUED  /  CPU RESET + RAM INITIALIZED AT STABLE EDGE");
    }

    public void LoadDraft()
    {
        if (simulator == null || draftInput == null || !TryControl()) return;
        ParseDraft(draftInput.text);
        if (_parseErrorCount > 0)
        {
            string more = _parseErrorCount > 12 ? "  +" + (_parseErrorCount - 12) + " MORE" : "";
            SetEditorStatus("DRAFT NOT LOADED  /  " + _parseErrorCount + " ERROR(S)\n" + _parseErrors + more);
            return;
        }
        if (_parsedCount == 0) { SetEditorStatus("DRAFT NOT LOADED  /  NO WORDS"); return; }
        int[] words = new int[ProgramCapacity];
        for (int i = 0; i < ProgramCapacity; i++) words[i] = i < _parsedCount ? _parsedWords[i] : HaltWord;
        byte[] ram = new byte[1024];
        if (simulator.initialRam != null)
            for (int i = 0; i < ram.Length && i < simulator.initialRam.Length; i++) ram[i] = simulator.initialRam[i];
        string name = "CUSTOM DRAFT";
        if (!simulator.LoadProgram(words, ram, name, -1))
        { SetEditorStatus("LOAD REJECTED  /  CONTROL OR LOAD QUEUE UNAVAILABLE"); return; }
        SetEditorStatus("LOAD QUEUED  /  CPU RESET + RAM INIT  /  " + _parsedCount + " WORDS  /  HALT PADDING");
    }

    private void ParseDraft(string source)
    {
        _parsedCount = 0;
        _parseErrorCount = 0;
        _parseErrors = "";
        if (source == null) source = "";
        int cursor = 0;
        int lineNumber = 0;
        while (cursor <= source.Length)
        {
            int newline = source.IndexOf("\n", cursor);
            int end = newline < 0 ? source.Length : newline;
            string line = source.Substring(cursor, end - cursor);
            lineNumber++;
            cursor = newline < 0 ? source.Length + 1 : newline + 1;
            line = StripComment(line);
            line = Trim(line);
            if (line.Length == 0) continue;
            if (_parsedCount >= ProgramCapacity)
            {
                AddParseError(lineNumber, "ROM capacity is 512 words");
                continue;
            }
            int value = ParseWord(line);
            if (value < 0)
            {
                AddParseError(lineNumber, "expected one 12-bit HEX or BIN word");
                continue;
            }
            _parsedWords[_parsedCount++] = value;
        }
    }

    private string StripComment(string line)
    {
        int hash = line.IndexOf("#");
        int slash = line.IndexOf("//");
        int semicolon = line.IndexOf(";");
        int cut = line.Length;
        if (hash >= 0 && hash < cut) cut = hash;
        if (slash >= 0 && slash < cut) cut = slash;
        if (semicolon >= 0 && semicolon < cut) cut = semicolon;
        return cut == line.Length ? line : line.Substring(0, cut);
    }

    private string Trim(string value)
    {
        int first = 0;
        int last = value.Length - 1;
        while (first <= last && IsSpace(value[first])) first++;
        while (last >= first && IsSpace(value[last])) last--;
        return first == 0 && last == value.Length - 1 ? value : value.Substring(first, last - first + 1);
    }

    private bool IsSpace(char value) { return value == ' ' || value == '\t' || value == '\r'; }

    private int ParseWord(string token)
    {
        string compact = "";
        for (int i = 0; i < token.Length; i++)
        {
            char character = token[i];
            if (character == ' ' || character == '\t' || character == '\r' || character == '_') continue;
            compact += token.Substring(i, 1);
        }
        token = compact;
        int radix = inputMode == 1 ? 2 : 16;
        if (token.Length >= 2)
        {
            string prefix = token.Substring(0, 2).ToUpper();
            if (prefix == "0X") { radix = 16; token = token.Substring(2); }
            else if (prefix == "0B") { radix = 2; token = token.Substring(2); }
        }
        if (token.Length == 0 || (radix == 16 && token.Length > 3) || (radix == 2 && token.Length > 12)) return -1;
        int value = 0;
        for (int i = 0; i < token.Length; i++)
        {
            int digit = Digit(token[i]);
            if (digit < 0 || digit >= radix) return -1;
            value = value * radix + digit;
            if (value > 0xFFF) return -1;
        }
        return value;
    }

    private int Digit(char value)
    {
        if (value >= '0' && value <= '9') return value - '0';
        if (value >= 'a' && value <= 'f') value = (char)(value - 'a' + 'A');
        if (value >= 'A' && value <= 'F') return value - 'A' + 10;
        return -1;
    }

    private void AddParseError(int line, string message)
    {
        _parseErrorCount++;
        if (_parseErrorCount <= 12)
        {
            string code = message.IndexOf("capacity") >= 0 ? "CAPACITY" : "12-BIT WORD";
            _parseErrors += (_parseErrors.Length == 0 ? "" : "  ") + "L" + line + "=" + code;
        }
    }

    public void PreviousPage() { programPage = Mathf.Max(0, programPage - 1); }
    public void NextPage() { programPage = Mathf.Min((ProgramCapacity - 1) / pageSize, programPage + 1); }
    public void GoToPcPage() { if (simulator != null) programPage = Mathf.Clamp(simulator.observedPc / pageSize, 0, (ProgramCapacity - 1) / pageSize); }

    private void RenderProgram()
    {
        if (instructionRows == null || simulator == null || simulator.programWords == null) return;
        for (int row = 0; row < instructionRows.Length; row++)
        {
            int address = programPage * pageSize + row;
            if (instructionRows[row] == null) continue;
            if (address >= ProgramCapacity) { instructionRows[row].text = ""; continue; }
            int word = simulator.programWords[address] & 0xFFF;
            string marker = address == simulator.observedPc ? ">" : " ";
            string phase = address == simulator.observedPc ? " P" + simulator.observedPhase : "  ";
            instructionRows[row].text = marker + Hex(address, 3) + "  " + Hex(word, 3) + "  " + Binary(word, 12) + "  " + Disassemble(word, address) + phase;
        }
        if (listingText != null) listingText.text = "ROM VIEW  /  PAGE " + (programPage + 1) + " OF " + ((ProgramCapacity + pageSize - 1) / pageSize)
            + "  /  PC " + Hex(simulator.observedPc, 3) + "  PHASE " + simulator.observedPhase;
    }

    private string Disassemble(int word, int address)
    {
        int code = (word >> 8) & 15;
        if (code == 14)
        {
            if (word >= 0xE21 && word <= 0xE24) return "BANK " + (word - 0xE21);
            int extension = (word >> 4) & 15;
            if (extensionNames != null && extension < extensionNames.Length && extensionNames[extension] != null)
            {
                string extensionName = extensionNames[extension];
                if (extension >= 10 && extension <= 14)
                {
                    int target = address + 1 < ProgramCapacity && simulator.programWords != null
                        ? simulator.programWords[address + 1] & 0x1FF : 0;
                    return extensionName + " 0x" + Hex(target, 3) + "  (2 WORDS)";
                }
                return extensionName;
            }
            return "E" + Hex(extension, 1) + " " + Hex(word & 15, 1);
        }
        string name = "OP" + Hex(code, 1);
        if (opcodeValues != null && opcodeNames != null)
            for (int i = 0; i < opcodeValues.Length && i < opcodeNames.Length; i++)
                if (opcodeValues[i] == code) { name = opcodeNames[i]; break; }
        int operand = word & 255;
        if (code == 15) return name;
        if (name == "LD" || name == "ST" || name == "ADD" || name == "SUB" || name == "ADC" || name == "SBC")
            return name + " [0x" + Hex(operand, 2) + "]";
        if (name == "JMP" || name == "JZ" || name == "CALL" || name == "JC" || name == "JNC")
            return name + " 0x" + Hex(operand, 2);
        if (code >= 0 && code <= 4) return name + " #0x" + Hex(operand, 2);
        if (code == 5) return name;
        return name + " 0x" + Hex(operand, 2);
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

    public void InsertBit0() { ToggleVirtualBit(0); }
    public void InsertBit1() { ToggleVirtualBit(1); }
    public void InsertBit2() { ToggleVirtualBit(2); }
    public void InsertBit3() { ToggleVirtualBit(3); }
    public void InsertBit4() { ToggleVirtualBit(4); }
    public void InsertBit5() { ToggleVirtualBit(5); }
    public void InsertBit6() { ToggleVirtualBit(6); }
    public void InsertBit7() { ToggleVirtualBit(7); }
    public void InsertBit8() { ToggleVirtualBit(8); }
    public void InsertBit9() { ToggleVirtualBit(9); }
    public void InsertBit10() { ToggleVirtualBit(10); }
    public void InsertBit11() { ToggleVirtualBit(11); }

    private void ToggleVirtualBit(int bit)
    {
        if (!TryControl()) return;
        virtualWord ^= 1 << bit;
    }

    public void CommitWord()
    {
        if (draftInput == null || !TryControl()) return;
        string word = inputMode == 1 ? Binary(virtualWord, 12) : "0x" + Hex(virtualWord, 3);
        if (draftInput.text.Length > 0 && !draftInput.text.EndsWith("\n")) draftInput.text += "\n";
        draftInput.text += word + "\n";
        virtualWord = 0;
        _shareAt = Time.realtimeSinceStartup;
    }

    public void BackspaceBit()
    {
        if (draftInput == null || !TryControl()) return;
        string text = draftInput.text;
        if (text.Length == 0) return;
        int lastContent = text.Length - 1;
        while (lastContent >= 0 && (text[lastContent] == '\n' || text[lastContent] == '\r')) lastContent--;
        if (lastContent < 0) { draftInput.text = ""; return; }
        int previousNewline = text.LastIndexOf("\n", lastContent);
        int keepLength = previousNewline < 0 ? 0 : previousNewline + 1;
        draftInput.text = text.Substring(0, keepLength);
        _shareAt = Time.realtimeSinceStartup;
    }

    private void SetEditorStatus(string value) { if (editorStatus != null) editorStatus.text = value; }
}
