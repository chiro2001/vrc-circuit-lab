using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabTabletSharedState : UdonSharpBehaviour
{
    public CircuitLabStateTablet tablet;
    public CircuitLabNetworkCoordinator coordinator;
    [UdonSynced] public int selectedNode = -1;
    [UdonSynced] public int selectedRamAddress;
    [UdonSynced] public int selectedRamBit;
    [UdonSynced] public int activeTab;
    [UdonSynced] public int page;
    [UdonSynced] public bool rawRam;
    [UdonSynced] public bool showInitialRam;
    [UdonSynced] public int ramSignalA = -1;
    [UdonSynced] public int ramSignalB = -1;
    [UdonSynced] public int ramSignalC = -1;
    [UdonSynced] public int ramSignalD = -1;
    [UdonSynced] public int ramWidthA = 1;
    [UdonSynced] public int ramWidthB = 1;
    [UdonSynced] public int ramWidthC = 1;
    [UdonSynced] public int ramWidthD = 1;
    [UdonSynced] public string ramLabelA = "RAM[00].0";
    [UdonSynced] public string ramLabelB = "RAM[00].0";
    [UdonSynced] public string ramLabelC = "RAM[00].0";
    [UdonSynced] public string ramLabelD = "RAM[00].0";

    public void Publish()
    {
        if (coordinator != null && !coordinator.HasControl()) return;
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        RequestSerialization();
    }

    public int SignalForChannel(int channel)
    {
        if (channel == 0) return ramSignalA;
        if (channel == 1) return ramSignalB;
        if (channel == 2) return ramSignalC;
        return ramSignalD;
    }

    public string LabelForChannel(int channel)
    {
        if (channel == 0) return ramLabelA;
        if (channel == 1) return ramLabelB;
        if (channel == 2) return ramLabelC;
        return ramLabelD;
    }

    public int WidthForChannel(int channel)
    {
        if (channel == 0) return ramWidthA;
        if (channel == 1) return ramWidthB;
        if (channel == 2) return ramWidthC;
        return ramWidthD;
    }

    public void SetChannel(int channel, int signal, int width, string label)
    {
        if (channel == 0) { ramSignalA = signal; ramWidthA = width; ramLabelA = label; }
        else if (channel == 1) { ramSignalB = signal; ramWidthB = width; ramLabelB = label; }
        else if (channel == 2) { ramSignalC = signal; ramWidthC = width; ramLabelC = label; }
        else { ramSignalD = signal; ramWidthD = width; ramLabelD = label; }
    }

    public void PublishViewState()
    {
        if (coordinator != null && !coordinator.HasControl()) return;
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        selectedNode = tablet == null ? selectedNode : tablet.selectNode;
        selectedRamAddress = tablet == null ? selectedRamAddress : tablet.selectRamAddress;
        selectedRamBit = tablet == null ? selectedRamBit : tablet.selectedRamBit;
        activeTab = tablet == null ? activeTab : tablet.activeTab;
        page = tablet == null ? page : tablet.page;
        rawRam = tablet != null && tablet.rawRam;
        showInitialRam = tablet != null && tablet.showInitialRam;
        RequestSerialization();
    }

    public void ApplyViewState()
    {
        if (tablet != null) tablet.ApplySharedViewState(selectedNode, selectedRamAddress, selectedRamBit, activeTab, page, rawRam, showInitialRam);
    }

    public override void OnDeserialization()
    {
        if (tablet != null) { tablet.ApplySharedRamTargets(); ApplyViewState(); }
    }
}
