using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabMovableTool : UdonSharpBehaviour
{
    public VRC_Pickup pickup;
    public VRCObjectSync objectSync;
    public Vector3 initialPosition;
    public Quaternion initialRotation = Quaternion.identity;
    public Vector3 initialScale = Vector3.one;
    public void RestoreInitialPose()
    {
        if (pickup != null && pickup.IsHeld && Networking.IsOwner(gameObject)) pickup.Drop();
        if (objectSync != null) objectSync.FlagDiscontinuity();
        transform.position = initialPosition; transform.rotation = initialRotation; transform.localScale = initialScale;
    }
}
