using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabProgramDraft : UdonSharpBehaviour
{
    public CircuitLabProgramPanel panel;
    public CircuitLabNetworkCoordinator coordinator;
    [UdonSynced] public string sourceText = "";
    [UdonSynced] public int sourceMode;
    [UdonSynced] public int selectedPreset;
    [UdonSynced] public int programPage;
    [UdonSynced] public int virtualWord;
    [UdonSynced] public int revision;

    public void Publish()
    {
        if (coordinator != null && !coordinator.HasControl()) return;
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        revision++;
        RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (panel != null) panel.ApplySharedDraft(sourceText, sourceMode, selectedPreset, programPage, virtualWord, revision);
    }
}
