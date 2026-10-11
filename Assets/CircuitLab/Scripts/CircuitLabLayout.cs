using UdonSharp;
using UnityEngine;
using VRC.Udon.Common.Interfaces;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabLayout : UdonSharpBehaviour
{
    public CircuitLabNetworkCoordinator coordinator;
    public CircuitLabModule[] modules;
    public CircuitLabProbe[] probes;
    public CircuitLabMovableTool[] tools;
    [UdonSynced] public int resetRevision;
    private int _appliedRevision;
    private bool _receivedInitialState;

    public void ResetLayout()
    {
        if (coordinator != null && !coordinator.HasControl()) return;
        if (Utilities.IsValid(Networking.LocalPlayer)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        resetRevision++;
        SendCustomNetworkEvent(NetworkEventTarget.All, "ApplyInitialLayout");
        RequestSerialization();
    }
    public void ApplyInitialLayout()
    {
        _appliedRevision = resetRevision;
        for (int i = 0; i < modules.Length; i++) modules[i].RestoreInitialPose();
        if (tools != null) for (int i = 0; i < tools.Length; i++) tools[i].RestoreInitialPose();
        if (modules.Length > 0) modules[0].RefreshAllModules();
        for (int i = 0; i < modules.Length; i++) if (modules[i].isVisible) modules[i].EnsureAboveFloor();
        // Keep simulator, channel bindings and all live/frozen history intact.
    }
    public override void OnDeserialization()
    {
        // Late joins receive each module's latest flags and ObjectSync's latest pose.
        // Reapplying an old reset here would erase a later user's movement.
        if (!_receivedInitialState) { _receivedInitialState = true; _appliedRevision = resetRevision; return; }
        if (resetRevision != _appliedRevision) ApplyInitialLayout();
    }
    public void ReturnAllProbes()
    {
        if (coordinator != null && !coordinator.HasControl()) return;
        for (int i = 0; i < probes.Length; i++) probes[i].ReturnToRack();
    }
}
