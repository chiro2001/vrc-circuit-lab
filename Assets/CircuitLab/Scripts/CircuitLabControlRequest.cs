using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabControlRequest : UdonSharpBehaviour
{
    public CircuitLabNetworkCoordinator coordinator;
    public int slotId;
    [UdonSynced] public int requesterId = -1;
    [UdonSynced] public double requestedAt;

    public void Claim()
    {
        if (Networking.LocalPlayer == null) return;
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        requesterId = Networking.LocalPlayer.playerId;
        requestedAt = Networking.GetServerTimeInSeconds();
        RequestSerialization();
        if (coordinator != null) coordinator.RefreshControlRequests();
    }

    public void ClearRequest()
    {
        if (!Networking.IsOwner(gameObject)) return;
        requesterId = -1;
        RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (coordinator != null) coordinator.RefreshControlRequests();
    }
}
