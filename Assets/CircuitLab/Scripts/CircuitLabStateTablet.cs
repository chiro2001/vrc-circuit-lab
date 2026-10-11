using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabStateTablet : UdonSharpBehaviour
{
    public CircuitLabSimulator simulator;
    public CircuitLabNetworkCoordinator coordinator;
    public CircuitLabTabletSharedState sharedState;
    public CircuitLabProbe[] probes;
    public CircuitLabSignalTarget[] signalTargets;
    public CircuitLabSignalTarget[] ramProbeTargets;
    public RawImage textureView;
    public Text detailText;
    public Text ramDetailText;
    public Text memoryStatusText;
    public Text pageText;
    public Text tabText;
    public Text[] signalCellLabels;
    public GameObject[] signalCellObjects;
    public GameObject[] ramCellObjects;
    public Text[] ramCellLabels;
    public string[] signalNames;
    public string[] signalModules;
    public string[] signalTypes;
    public string[] signalInputs;
    public int[] signalOpcodes;
    public int[] signalIds;
    public int nodeCount;
    public int page;
    public int activeTab;
    public int selectNode = -1;
    public int selectRamAddress;
    public int selectedRamBit;
    public bool rawRam;
    public bool showInitialRam;
    public Material displayMaterial;

    private int _lastRamCycle = -1;
    private int _lastSignalSample = -1;
    private int _lastSelectedNode = -2;
    private int _lastSelectedAddress = -1;
    private int _lastPage = -1;
    private int _lastTab = -1;
    private bool _lastRaw;
    private bool _lastInitial;
    private bool _applyingSharedState;
    private CircuitLabSignalTarget _selectedTarget;
    private bool _hasInteractableState;
    private bool _lastCanInteract;

    private void Start()
    {
        ApplySharedRamTargets();
        if (sharedState != null) sharedState.ApplyViewState();
        RefreshGridLabels();
    }

    private bool HasControl() { return coordinator == null || coordinator.HasControl(); }

    private void Update()
    {
        if (simulator == null) return;
        bool canInteract = coordinator == null || coordinator.CanUseControls();
        if (!_hasInteractableState || canInteract != _lastCanInteract)
        {
            _hasInteractableState = true; _lastCanInteract = canInteract;
            if (signalCellObjects != null)
                for (int i = 0; i < signalCellObjects.Length; i++)
                {
                    UnityEngine.UI.Button button = signalCellObjects[i] == null ? null : signalCellObjects[i].GetComponent<UnityEngine.UI.Button>();
                    if (button != null) button.interactable = canInteract;
                }
            if (ramCellObjects != null)
                for (int i = 0; i < ramCellObjects.Length; i++)
                {
                    UnityEngine.UI.Button button = ramCellObjects[i] == null ? null : ramCellObjects[i].GetComponent<UnityEngine.UI.Button>();
                    if (button != null) button.interactable = canInteract;
                }
        }
        if (textureView != null)
        {
            if (displayMaterial == null) displayMaterial = textureView.material;
            if (displayMaterial != null)
            {
                displayMaterial.SetTexture("_StateTex", simulator.currentState);
                displayMaterial.SetTexture("_GateTex", simulator.gateTexture);
                displayMaterial.SetTexture("_RomTex", simulator.programTexture);
                Texture ramTexture = simulator.currentRam;
                if (showInitialRam) ramTexture = simulator.ramInitialTexture;
                displayMaterial.SetTexture("_RamTex", ramTexture);
                displayMaterial.SetInt("_TextureWidth", simulator.textureWidth);
                displayMaterial.SetInt("_NodeCount", simulator.nodeCount);
                displayMaterial.SetInt("_PageStart", activeTab == 0 ? page * 64 : activeTab == 1 ? page * 256 : 0);
                displayMaterial.SetInt("_SelectedSignal", activeTab == 3 ? selectRamAddress : selectNode);
                displayMaterial.SetInt("_DisplayMode", activeTab);
                displayMaterial.SetInt("_RawRam", rawRam ? 1 : 0);
            }
        }
        if (activeTab != _lastTab || page != _lastPage || selectNode != _lastSelectedNode || rawRam != _lastRaw || showInitialRam != _lastInitial)
        {
            _lastTab = activeTab; _lastPage = page; _lastSelectedNode = selectNode; _lastRaw = rawRam; _lastInitial = showInitialRam;
            RefreshGridLabels();
            if (activeTab == 3) RefreshRamGrid();
        }
        if (selectNode >= 0 && simulator.telemetryReadbackCount != _lastSignalSample)
        { _lastSignalSample = simulator.telemetryReadbackCount; RefreshSelectedSignal(); }
        if (activeTab == 3 && (simulator.observedRamCycle != _lastRamCycle || selectRamAddress != _lastSelectedAddress || rawRam != _lastRaw || showInitialRam != _lastInitial))
        {
            _lastRamCycle = simulator.observedRamCycle;
            _lastSelectedAddress = selectRamAddress;
            _lastRaw = rawRam;
            _lastInitial = showInitialRam;
            RefreshRamGrid();
        }
        if (activeTab == 3) RefreshRamDetails();
        RefreshMemoryStatus();
        if (pageText != null)
        {
            if (activeTab == 0) pageText.text = "SIGNALS  /  PAGE " + (page + 1) + " OF " + PageCount();
            else if (activeTab == 1) pageText.text = "GATE TEXTURE  /  PAGE " + (page + 1) + " OF " + PageCount();
            else if (activeTab == 2) pageText.text = "ROM TEXTURE  /  512 WORDS";
            else pageText.text = "CYCLE " + simulator.cycleCount;
        }
        if (tabText != null) tabText.text = activeTab == 0 ? "LIVE SIGNAL STATE" : activeTab == 1 ? "GATE TEXTURE" : activeTab == 2 ? "PROGRAM ROM TEXTURE" : "RAM 16 x 16";
    }

    public void SelectCell(int slot)
    {
        if (!EnsureControl()) return;
        int signal = page * 64 + slot;
        if (signal < 0 || signal >= nodeCount) return;
        selectNode = signal;
        activeTab = 0;
        RevealSelectedSignal(signal);
        RefreshSelectedSignal();
        PublishViewState();
    }

    public void SelectNode()
    {
        if (!EnsureControl()) return;
        if (selectNode < 0 || selectNode >= nodeCount) { if (detailText != null) detailText.text = "SELECT A SIGNAL ID FROM THE GRID"; return; }
        page = Mathf.Clamp(selectNode / 64, 0, Mathf.Max(0, (nodeCount - 1) / 64));
        activeTab = 0;
        RevealSelectedSignal(selectNode);
        RefreshSelectedSignal();
        PublishViewState();
    }

    public void SelectRamAddress()
    {
        if (!EnsureControl()) return;
        selectRamAddress = Mathf.Clamp(selectRamAddress, 0, 255);
        activeTab = 3; page = 0;
        RefreshRamDetails();
        PublishViewState();
    }

    public void SelectRamBit0() { SelectRamBit(0); }
    public void SelectRamBit1() { SelectRamBit(1); }
    public void SelectRamBit2() { SelectRamBit(2); }
    public void SelectRamBit3() { SelectRamBit(3); }
    public void SelectRamBit4() { SelectRamBit(4); }
    public void SelectRamBit5() { SelectRamBit(5); }
    public void SelectRamBit6() { SelectRamBit(6); }
    public void SelectRamBit7() { SelectRamBit(7); }
    private void SelectRamBit(int bit) { if (!EnsureControl()) return; selectedRamBit = bit; RefreshRamDetails(); PublishViewState(); }

    public void PreviousPage() { if (!EnsureControl()) return; page = Mathf.Max(0, page - 1); PublishViewState(); }
    public void NextPage() { if (!EnsureControl()) return; page = Mathf.Min(PageCount() - 1, page + 1); PublishViewState(); }
    public void ShowSignals() { if (!_applyingSharedState && !EnsureControl()) return; activeTab = 0; page = 0; PublishViewState(); }
    public void ShowGates() { if (!EnsureControl()) return; activeTab = 1; page = 0; PublishViewState(); }
    public void ShowRom() { if (!EnsureControl()) return; activeTab = 2; page = 0; PublishViewState(); }
    public void ShowRam() { if (!EnsureControl()) return; activeTab = 3; page = 0; PublishViewState(); }
    public void ToggleRawRam() { if (!EnsureControl()) return; rawRam = !rawRam; PublishViewState(); }
    public void ToggleInitialRam() { if (!EnsureControl()) return; showInitialRam = !showInitialRam; PublishViewState(); }

    public void ClaimControl()
    {
        if (coordinator == null || coordinator.HasControl()) return;
        if (coordinator.controlAuthorityId < 0) coordinator.TryAcquireUnclaimedControl();
        else coordinator.RequestControl();
    }

    public void BindSelectedToProbe()
    {
        if (simulator == null || probes == null || !EnsureControl()) return;
        CircuitLabProbe probe = null;
        for (int i = 0; i < probes.Length; i++)
            if (probes[i] != null && probes[i].isSelected) { probe = probes[i]; break; }
        if (probe == null && probes.Length > 0) probe = probes[0];
        if (probe == null) return;
        if (activeTab == 3)
        {
            BindRamProbe(probe, 1);
            return;
        }
        CircuitLabSignalTarget selected = FindSignalTarget(selectNode);
        if (selected == null) { if (detailText != null) detailText.text += "\nNO PHYSICAL NODE TARGET"; return; }
        RevealOwner(selected.ownerModule);
        probe.BindTarget(selected);
    }

    public void BindSelectedRamByte()
    {
        if (simulator == null || activeTab != 3 || probes == null || !EnsureControl()) return;
        CircuitLabProbe probe = SelectedProbe();
        if (probe != null) BindRamProbe(probe, 8);
    }

    private CircuitLabProbe SelectedProbe()
    {
        if (probes == null) return null;
        for (int i = 0; i < probes.Length; i++)
            if (probes[i] != null && probes[i].isSelected) return probes[i];
        return probes.Length > 0 ? probes[0] : null;
    }

    private void BindRamProbe(CircuitLabProbe probe, int width)
    {
        int channel = Mathf.Clamp(probe.channel, 0, 3);
        int signal = simulator.nodeCount + selectRamAddress * 8;
        string label = width == 8 ? "RAM[" + Hex(selectRamAddress, 2) + "][7:0]" : "RAM[" + Hex(selectRamAddress, 2) + "]." + selectedRamBit;
        if (width == 1) signal += selectedRamBit;
        CircuitLabSignalTarget target = ramProbeTargets != null && channel < ramProbeTargets.Length ? ramProbeTargets[channel] : null;
        if (target == null) return;
        int[] ids = new int[width];
        for (int bit = 0; bit < width; bit++) ids[bit] = signal + bit;
        target.signalIds = ids;
        target.signalLabel = label;
        if (sharedState != null)
        {
            sharedState.SetChannel(channel, signal, width, label);
            sharedState.Publish();
        }
        probe.BindTarget(target);
    }

    public void ApplySharedRamTargets()
    {
        if (sharedState == null || ramProbeTargets == null) return;
        for (int channel = 0; channel < ramProbeTargets.Length && channel < 4; channel++)
        {
            CircuitLabSignalTarget target = ramProbeTargets[channel];
            if (target == null) continue;
            int signal = sharedState.SignalForChannel(channel);
            int width = Mathf.Clamp(sharedState.WidthForChannel(channel), 1, 8);
            target.signalIds = new int[width];
            for (int bit = 0; bit < width; bit++) target.signalIds[bit] = signal + bit;
            target.signalLabel = sharedState.LabelForChannel(channel);
        }
    }

    public void ApplySharedViewState(int node, int ramAddress, int ramBit, int tab, int currentPage, bool showRaw, bool initial)
    {
        if (HasControl()) return;
        _applyingSharedState = true;
        selectNode = node; selectRamAddress = Mathf.Clamp(ramAddress, 0, 255); selectedRamBit = Mathf.Clamp(ramBit, 0, 7);
        activeTab = Mathf.Clamp(tab, 0, 3); page = Mathf.Clamp(currentPage, 0, Mathf.Max(0, PageCount() - 1)); rawRam = showRaw; showInitialRam = initial;
        _applyingSharedState = false;
    }

    private void RevealSelectedSignal(int signal)
    {
        CircuitLabSignalTarget target = FindSignalTarget(signal);
        if (target == null) return;
        RevealOwner(target.ownerModule);
        if (_selectedTarget != null && _selectedTarget != target) _selectedTarget.Preselect(false);
        _selectedTarget = target;
        if (target.isPreselected) return;
        target.Preselect(true);
    }

    private void PublishViewState()
    {
        if (!_applyingSharedState && sharedState != null && HasControl()) sharedState.PublishViewState();
    }

    private int PageCount()
    {
        if (activeTab == 0) return Mathf.Max(1, (nodeCount + 63) / 64);
        if (activeTab == 1) return Mathf.Max(1, (nodeCount + 255) / 256);
        return 1;
    }

    private bool EnsureControl()
    {
        if (coordinator == null || coordinator.HasControl()) return true;
        if (coordinator.controlAuthorityId < 0) return coordinator.TryAcquireUnclaimedControl();
        coordinator.RequestControl();
        return false;
    }

    private CircuitLabSignalTarget FindSignalTarget(int signal)
    {
        if (signal < 0 || signalTargets == null) return null;
        CircuitLabSignalTarget fallback = null;
        for (int i = 0; i < signalTargets.Length; i++)
        {
            CircuitLabSignalTarget target = signalTargets[i];
            if (target == null || target.signalIds == null) continue;
            for (int j = 0; j < target.signalIds.Length; j++)
                if (target.signalIds[j] == signal)
                {
                    if (!target.isPort && target.signalIds.Length == 1) return target;
                    if (fallback == null) fallback = target;
                }
        }
        return fallback;
    }

    private void RevealOwner(CircuitLabModule module)
    {
        if (module == null) return;
        CircuitLabModule[] chain = new CircuitLabModule[64];
        int count = 0;
        CircuitLabModule item = module;
        while (item != null && count < chain.Length) { chain[count++] = item; item = item.logicalParent; }
        for (int i = count - 1; i >= 0; i--)
            if (!chain[i].expanded) chain[i].Expand();
    }

    private void RefreshGridLabels()
    {
        bool signals = activeTab == 0;
        bool ram = activeTab == 3;
        if (signalCellObjects != null)
            for (int i = 0; i < signalCellObjects.Length; i++) if (signalCellObjects[i] != null) signalCellObjects[i].SetActive(signals);
        if (ramCellObjects != null)
            for (int i = 0; i < ramCellObjects.Length; i++) if (ramCellObjects[i] != null) ramCellObjects[i].SetActive(ram && !rawRam);
        if (signalCellLabels != null)
            for (int slot = 0; slot < signalCellLabels.Length; slot++)
            {
                int id = page * 64 + slot;
                if (signalCellLabels[slot] == null) continue;
                if (id >= nodeCount) { signalCellLabels[slot].text = ""; continue; }
                string name = signalNames != null && id < signalNames.Length ? signalNames[id] : "";
                if (name != null && name.Length > 13) name = name.Substring(name.Length - 13);
                signalCellLabels[slot].text = Hex(id, 4) + "\n" + name;
            }
        if (pageText != null && signals) pageText.text = "SIGNALS  /  PAGE " + (page + 1) + " OF " + Mathf.Max(1, (nodeCount + 63) / 64);
    }

    private void RefreshRamGrid()
    {
        if (ramCellLabels == null || (!showInitialRam && simulator.observedRam == null)) return;
        for (int address = 0; address < ramCellLabels.Length && address < 256; address++)
        {
            if (ramCellLabels[address] == null) continue;
            int value = showInitialRam && simulator.initialRam != null ? simulator.initialRam[address] : simulator.observedRam[address];
            ramCellLabels[address].text = Hex(address, 2) + "\n" + Hex(value, 2);
        }
    }

    private void RefreshSelectedSignal()
    {
        if (detailText == null || simulator == null || selectNode < 0 || selectNode >= nodeCount) return;
        string name = signalNames != null && selectNode < signalNames.Length ? signalNames[selectNode] : "SIGNAL " + selectNode;
        string module = signalModules != null && selectNode < signalModules.Length ? signalModules[selectNode] : "";
        string type = signalTypes != null && selectNode < signalTypes.Length ? signalTypes[selectNode] : "UNKNOWN";
        string inputs = signalInputs != null && selectNode < signalInputs.Length ? signalInputs[selectNode] : "";
        int value = simulator.GetObservedSignal(selectNode);
        detailText.text = "NODE " + Hex(selectNode, 4) + "  /  " + name + "\nMODULE " + module + "\nTYPE " + type
            + "\nINPUTS " + inputs + "\nVALUE " + (value < 0 ? "WAITING FOR GPU SAMPLE" : value.ToString())
            + "\nPC " + Hex(simulator.observedPc, 3) + "  PHASE " + simulator.observedPhase + "  CYCLE " + simulator.cycleCount;
    }

    private void RefreshRamDetails()
    {
        if (ramDetailText == null || simulator == null) return;
        byte[] ram = showInitialRam ? simulator.initialRam : simulator.observedRam;
        if (ram == null || ram.Length <= selectRamAddress) { ramDetailText.text = "RAM SAMPLE WAITING"; return; }
        int value = ram[selectRamAddress];
        int bitValue = simulator.GetObservedSignal(simulator.nodeCount + selectRamAddress * 8 + selectedRamBit);
        ramDetailText.text = "RAM[" + Hex(selectRamAddress, 2) + "] = 0x" + Hex(value, 2) + "  /  " + Binary(value, 8)
            + "\nBIT " + selectedRamBit + " = " + (bitValue < 0 ? "WAIT" : bitValue.ToString())
            + (showInitialRam ? "  /  INITIAL" : "  /  GPU CURRENT RAM");
    }

    private void RefreshMemoryStatus()
    {
        if (memoryStatusText == null || simulator == null) return;
        string stable = simulator.observedRamStable ? "STABLE GPU SAMPLE" : "WAITING FOR STABLE SAMPLE";
        memoryStatusText.text = "CYCLE " + simulator.cycleCount + "  /  RAM SAMPLE " + simulator.observedRamCycle + "  /  " + stable
            + "\nA READ  0x" + Hex(simulator.observedRamReadAddress, 2) + " -> 0x" + Hex(simulator.observedRamReadData, 2)
            + "\nB NEXT EDGE WRITE INTENT  0x" + Hex(simulator.observedRamWriteAddress, 2) + " <- 0x" + Hex(simulator.observedRamWriteData, 2)
            + "  WE " + simulator.observedRamWriteEnable;
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
