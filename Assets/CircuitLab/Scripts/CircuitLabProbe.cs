using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Components;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabProbe : UdonSharpBehaviour
{
    public CircuitLabSimulator simulator;
    public CircuitLabNetworkCoordinator coordinator;
    public CircuitLabProbe[] probes;
    public CircuitLabSignalTarget[] signalTargets;
    public Transform tip;
    public VRC_Pickup pickup;
    public VRCObjectSync objectSync;
    public CircuitLabObjectState sharedState;
    public Transform rackAnchor;
    public Transform workspaceRoot;
    public Text probeLabel;
    public Text panelLabel;
    public int channel;
    public float preselectDistance = .13f;
    public CircuitLabSignalTarget candidate;
    public CircuitLabSignalTarget boundTarget;
    public CircuitLabSignalTarget initialTarget;
    public bool isSelected;
    public int boundTargetIndex = -1;
    public bool isHeld;

    private bool CanControl() { return coordinator == null || coordinator.HasControl(); }
    public void ClaimObject()
    {
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        if (sharedState != null) sharedState.Claim();
    }
    public void Publish()
    {
        if (sharedState != null)
        {
            sharedState.selected = isSelected; sharedState.boundTargetIndex = boundTargetIndex;
            if (Utilities.IsValid(Networking.LocalPlayer) && Networking.IsOwner(gameObject)) sharedState.Publish();
        }
    }

    public void SelectProbe()
    {
        if (!CanControl()) return;
        if (probes != null)
        {
            for (int i = 0; i < probes.Length; i++)
                if (probes[i] != null) { probes[i].ClaimObject(); probes[i].isSelected = probes[i] == this; probes[i].RefreshLabels(); probes[i].Publish(); }
        }
        else isSelected = true;
        RefreshLabels();
    }

    public override void OnPickup()
    {
        if (!CanControl()) { if (pickup != null) pickup.Drop(); return; }
        ClaimObject();
        Detach();
        isHeld = true;
        SelectProbe();
    }

    public override void OnDrop() { isHeld = false; ClearCandidate(); RefreshLabels(); }
    public override void OnPickupUseDown() { ConfirmBinding(); }

    public void ConfirmBinding()
    {
        if (candidate != null) BindTarget(candidate);
    }

    public void BindTarget(CircuitLabSignalTarget target)
    {
        if (!CanControl() || target == null || simulator == null || target.signalIds == null || target.signalIds.Length == 0) return;
        ClaimObject();
        if (boundTarget != null) boundTarget.RemoveChannel(channel);
        ClearCandidate();
        boundTarget = target;
        boundTargetIndex = target.targetIndex;
        boundTarget.AddChannel(channel);
        simulator.BindProbe(channel, target.signalIds, target.signalLabel);
        // Release the VR hand before following the fixed pin; otherwise the hand and
        // binding would both drive the same transform during LateUpdate.
        if (pickup != null && pickup.IsHeld) pickup.Drop();
        isHeld = false;
        RefreshLabels();
        Publish();
    }

    public void Detach()
    {
        if (!CanControl()) return;
        ClaimObject();
        if (boundTarget != null) boundTarget.RemoveChannel(channel);
        boundTarget = null;
        boundTargetIndex = -1;
        ClearCandidate();
        if (simulator != null) simulator.UnbindProbe(channel);
        RefreshLabels();
        Publish();
    }

    public void ReturnToRack()
    {
        if (!CanControl()) return;
        ClaimObject();
        if (pickup != null && pickup.IsHeld) pickup.Drop();
        Detach();
        isHeld = false;
        if (rackAnchor != null)
        {
            if (objectSync != null) objectSync.FlagDiscontinuity();
            transform.SetParent(workspaceRoot, true);
            transform.position = rackAnchor.position; transform.rotation = rackAnchor.rotation;
        }
        Publish();
    }

    public void ApplySharedState(int targetIndex, bool selected)
    {
        boundTargetIndex = targetIndex; isSelected = selected;
        if (boundTarget != null) boundTarget.RemoveChannel(channel);
        boundTarget = boundTargetIndex >= 0 && boundTargetIndex < signalTargets.Length ? signalTargets[boundTargetIndex] : null;
        if (boundTarget != null) boundTarget.AddChannel(channel);
        ClearCandidate(); RefreshLabels();
        // CircuitLabNetworkCoordinator independently replicates simulator channel data.
    }

    public void ClearCandidate()
    {
        if (candidate != null) candidate.Preselect(false);
        candidate = null;
    }

    private void Update()
    {
        // Only a held needle searches. Desktop selection binds through Target.Interact.
        if (!isHeld || boundTarget != null || tip == null || signalTargets == null) return;
        CircuitLabSignalTarget nearest = null;
        float best = preselectDistance * preselectDistance;
        Vector3 point = tip.position;
        for (int i = 0; i < signalTargets.Length; i++)
        {
            CircuitLabSignalTarget target = signalTargets[i];
            if (target == null || !target.gameObject.activeInHierarchy) continue;
            float distance = (target.transform.position - point).sqrMagnitude;
            if (distance < best) { best = distance; nearest = target; }
        }
        if (nearest == candidate) return;
        ClearCandidate();
        candidate = nearest;
        if (candidate != null) candidate.Preselect(true);
        RefreshLabels();
    }

    private void LateUpdate()
    {
        if (boundTarget == null || tip == null || isHeld) return;
        Transform anchor = boundTarget.transform;
        if (!boundTarget.gameObject.activeInHierarchy && boundTarget.ownerModule != null)
        {
            CircuitLabModule module = boundTarget.ownerModule;
            for (int depth = 0; depth < 32 && !module.isVisible && module.logicalParent != null; depth++)
                module = module.logicalParent;
            anchor = module.probeAnchor;
        }
        if (anchor == null) return;
        transform.rotation = anchor.rotation;
        if (boundTarget.gameObject.activeInHierarchy && boundTarget.isPort)
            transform.rotation = anchor.rotation * Quaternion.Euler(0f, 0f, anchor.localPosition.x > 0f ? -90f : 90f);
        Vector3 offset = boundTarget.gameObject.activeInHierarchy ? Vector3.zero : new Vector3((channel - 1.5f) * .10f, 0f, 0f);
        transform.position = anchor.position + anchor.TransformVector(offset) - transform.TransformVector(tip.localPosition);
    }

    public void RefreshLabels()
    {
        string letter = channel == 0 ? "A" : (channel == 1 ? "B" : (channel == 2 ? "C" : "D"));
        string value = boundTarget != null ? boundTarget.signalLabel : (candidate != null ? "READY: " + candidate.signalLabel : "UNBOUND");
        if (probeLabel != null) probeLabel.text = letter + (isSelected ? " *" : "") + "\n" + (boundTarget != null ? "LOCK" : "USE");
        if (panelLabel != null) panelLabel.text = letter + (isSelected ? " *  " : "    ") + value;
    }

    private void Start()
    {
        if (boundTargetIndex >= 0) ApplySharedState(boundTargetIndex, isSelected);
        RefreshLabels();
    }
}
