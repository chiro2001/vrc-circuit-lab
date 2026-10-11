using UdonSharp;
using VRC.SDKBase;

// Reliable logical state lives away from the physical root's VRCObjectSync.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabObjectState : UdonSharpBehaviour
{
    public CircuitLabModule module;
    public CircuitLabProbe probe;
    [UdonSynced] public bool expanded;
    [UdonSynced] public bool detached;
    [UdonSynced] public bool selected;
    [UdonSynced] public int boundTargetIndex = -1;
    public void Claim()
    {
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }
    public void Publish()
    {
        Claim();
        if (Utilities.IsValid(Networking.LocalPlayer) && Networking.IsOwner(gameObject)) RequestSerialization();
    }
    public override void OnDeserialization()
    {
        if (module != null) module.ApplySharedState(expanded, detached);
        if (probe != null) probe.ApplySharedState(boundTargetIndex, selected);
    }
}
