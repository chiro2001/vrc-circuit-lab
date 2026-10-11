using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Components;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabModule : UdonSharpBehaviour
{
    public string modulePath;
    public int moduleId;
    public int logicalDepth;
    public CircuitLabModule logicalParent;
    public CircuitLabModule[] childModules;
    public CircuitLabModule[] allModules;
    public Transform workspaceRoot;
    public Transform childrenDock;
    public Vector3 dockLocalPosition;
    public Vector3 dockLocalEuler;
    public Vector3 dockLocalScale = Vector3.one * .42f;
    public GameObject interior;
    public GameObject expandedShell;
    public GameObject foldedShell;
    public Transform header;
    public Transform footer;
    public Transform handle;
    public Transform grabVisual;
    public Text grabText;
    public BoxCollider pickupCollider;
    public VRC_Pickup pickup;
    public VRCObjectSync objectSync;
    public CircuitLabObjectState sharedState;
    public Transform probeAnchor;
    public Text channelBadge;
    public Text stateLabel;
    public Transform[] portTransforms;
    public Vector3[] expandedPortPositions;
    public Vector3[] foldedPortPositions;
    public float expandedHeight = 2f;
    public float expandedWidth = 3.3f;
    public float foldedHeight = 1.3f;
    public float handleSide = 1f;
    public float floorClearance = .05f;
    public bool expanded;
    public bool isDetached;
    public bool isVisible = true;
    public int boundChannelMask;
    public Vector3 initialPosition;
    public Quaternion initialRotation = Quaternion.identity;
    public Vector3 initialScale = Vector3.one;
    public bool initialExpanded;
    public bool initialDetached;
    public bool initialPoseCaptured;

    // The body has no Interact event: native Pickup exclusively owns its collider.
    public bool CanManipulate()
    {
        return pickup == null || !pickup.IsHeld || !Utilities.IsValid(Networking.LocalPlayer) || Networking.IsOwner(gameObject);
    }

    public bool ClaimObject()
    {
        if (!CanManipulate()) return false;
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        if (sharedState != null) sharedState.Claim();
        return true;
    }

    public override void OnPickup()
    {
        if (!ClaimObject()) { if (pickup != null) pickup.Drop(); return; }
        if (logicalParent != null)
        {
            transform.SetParent(workspaceRoot, true);
            transform.localScale = Vector3.one;
            isDetached = true;
        }
        PublishState();
    }

    public void DetachFromParent()
    {
        if (logicalParent == null || !ClaimObject()) return;
        transform.SetParent(workspaceRoot, true);
        transform.localScale = Vector3.one;
        isDetached = true;
        EnsureAboveFloor();
        PublishState();
    }

    public void ToggleExpanded() { if (expanded) Collapse(); else Expand(); }
    public void Expand()
    {
        if (!ClaimObject()) return;
        expanded = true;
        RefreshAllModules();
        EnsureAboveFloor();
        PublishState();
    }
    public void Collapse()
    {
        if (!ClaimObject()) return;
        expanded = false;
        RefreshAllModules();
        PublishState();
    }

    public void RefreshAllModules()
    {
        if (allModules == null) { RefreshVisibility(); return; }
        for (int i = 0; i < allModules.Length; i++)
            if (allModules[i] != null) allModules[i].RefreshVisibility();
    }

    public void RefreshVisibility()
    {
        isVisible = logicalParent == null || (logicalParent.isVisible && logicalParent.expanded);
        // Keep Udon and ObjectSync roots alive while descendants are visually folded.
        if (interior != null) interior.SetActive(isVisible && expanded);
        if (expandedShell != null) expandedShell.SetActive(isVisible && expanded);
        if (foldedShell != null) foldedShell.SetActive(isVisible && !expanded);
        if (header != null) header.gameObject.SetActive(isVisible);
        if (footer != null) footer.gameObject.SetActive(isVisible);
        if (handle != null) handle.gameObject.SetActive(isVisible);
        if (channelBadge != null) channelBadge.gameObject.SetActive(isVisible);
        if (pickupCollider != null) pickupCollider.enabled = isVisible;
        float height = expanded ? expandedHeight : foldedHeight;
        if (header != null) header.localPosition = new Vector3(0f, height * .5f - .19f, -.095f);
        if (footer != null) footer.localPosition = new Vector3(0f, -height * .5f + .28f, -.11f);
        Vector3 scale = transform.lossyScale;
        float sx = Mathf.Max(.0001f, Mathf.Abs(scale.x));
        float sy = Mathf.Max(.0001f, Mathf.Abs(scale.y));
        float sz = Mathf.Max(.0001f, Mathf.Abs(scale.z));
        Vector3 gripSize = new Vector3(Mathf.Max(.85f, .25f / sx), Mathf.Max(.15f, .20f / sy), Mathf.Max(.18f, .08f / sz));
        if (handle != null) handle.localPosition = new Vector3(handleSide * ((expanded ? expandedWidth : 2.8f) * .5f + gripSize.x * .5f + .04f / sx), 0f, -.04f);
        if (pickupCollider != null && handle != null) { pickupCollider.center = handle.localPosition; pickupCollider.size = gripSize; }
        if (grabVisual != null) grabVisual.localScale = gripSize;
        if (grabText != null)
        {
            grabText.transform.localScale = Vector3.one * (.01f / Mathf.Min(sx, sy));
            grabText.transform.localPosition = new Vector3(0f, 0f, -gripSize.z * .5f - .012f / sz);
        }
        if (probeAnchor != null) probeAnchor.localPosition = new Vector3(0f, -height * .5f + .16f, -.18f);
        if (channelBadge != null) channelBadge.transform.localPosition = new Vector3(0f, -height * .5f + .08f, -.13f);
        if (portTransforms != null)
            for (int i = 0; i < portTransforms.Length; i++)
            {
                portTransforms[i].gameObject.SetActive(isVisible);
                portTransforms[i].localPosition = expanded ? expandedPortPositions[i] : foldedPortPositions[i];
            }
        RefreshLabels();
    }

    // Eight transformed corners handle tilted boards and uniform docking scales.
    public void EnsureAboveFloor()
    {
        // Backing.Frame is .07 wider/taller than the nominal board; include rim
        // and a small numerical margin before transforming every corner.
        float w = ((expanded ? expandedWidth : 2.8f) + .075f) * .5f;
        float h = ((expanded ? expandedHeight : foldedHeight) + .075f) * .5f;
        float lowest = 100000f;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    lowest = Mathf.Min(lowest, transform.TransformPoint(new Vector3(x * w, y * h, z * .14f)).y);
        if (pickupCollider != null)
        {
            Vector3 half = pickupCollider.size * .5f;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        lowest = Mathf.Min(lowest, transform.TransformPoint(pickupCollider.center + new Vector3(x * half.x, y * half.y, z * half.z)).y);
        }
        if (lowest >= floorClearance) return;
        CircuitLabModule moving = this;
        for (int depth = 0; depth < 32 && !moving.isDetached && moving.logicalParent != null; depth++)
            moving = moving.logicalParent;
        if (!moving.ClaimObject()) return;
        if (moving.objectSync != null) moving.objectSync.FlagDiscontinuity();
        moving.transform.position += Vector3.up * (floorClearance + .002f - lowest);
    }

    public void RetrieveChildren()
    {
        if (allModules == null || !ClaimObject()) return;
        for (int i = 0; i < allModules.Length; i++)
        {
            CircuitLabModule child = allModules[i];
            if (child == null || child == this || child.logicalDepth <= logicalDepth) continue;
            CircuitLabModule ancestor = child.logicalParent;
            bool belongs = false;
            for (int depth = 0; depth < 32 && ancestor != null; depth++)
            {
                if (ancestor == this) { belongs = true; break; }
                ancestor = ancestor.logicalParent;
            }
            if (!belongs || child.logicalParent == null || !child.ClaimObject()) continue;
            child.ApplyDockPose();
            child.PublishState();
        }
        RefreshAllModules();
        for (int i = 0; i < allModules.Length; i++) if (allModules[i].isVisible) allModules[i].EnsureAboveFloor();
    }

    public void ApplyDockPose()
    {
        if (pickup != null && pickup.IsHeld && Networking.IsOwner(gameObject)) pickup.Drop();
        if (objectSync != null) objectSync.FlagDiscontinuity();
        transform.SetParent(logicalParent.childrenDock, false);
        transform.localPosition = dockLocalPosition;
        transform.localEulerAngles = dockLocalEuler;
        transform.localScale = dockLocalScale;
        isDetached = false;
    }

    public void CaptureInitialPose()
    {
        initialPosition = transform.position; initialRotation = transform.rotation; initialScale = transform.localScale;
        initialExpanded = expanded; initialDetached = isDetached; initialPoseCaptured = true;
    }

    public void RestoreInitialPose()
    {
        if (!initialPoseCaptured) return;
        if (pickup != null && pickup.IsHeld && Networking.IsOwner(gameObject)) pickup.Drop();
        if (objectSync != null) objectSync.FlagDiscontinuity();
        expanded = initialExpanded; isDetached = initialDetached;
        transform.SetParent(isDetached || logicalParent == null ? workspaceRoot : logicalParent.childrenDock, true);
        transform.position = initialPosition; transform.rotation = initialRotation; transform.localScale = initialScale;
        if (Networking.IsOwner(gameObject)) PublishState();
    }

    public void PublishState()
    {
        RefreshLabels();
        if (sharedState != null)
        {
            sharedState.expanded = expanded; sharedState.detached = isDetached;
            if (Utilities.IsValid(Networking.LocalPlayer) && Networking.IsOwner(gameObject)) sharedState.Publish();
        }
    }

    public void ApplySharedState(bool newExpanded, bool newDetached)
    {
        expanded = newExpanded; isDetached = newDetached;
        if (logicalParent != null)
        {
            if (isDetached) { transform.SetParent(workspaceRoot, true); transform.localScale = Vector3.one; }
            else ApplyDockPose();
        }
        RefreshAllModules();
    }

    public void AddProbeChannel(int channel) { boundChannelMask |= 1 << channel; RefreshLabels(); }
    public void RemoveProbeChannel(int channel) { boundChannelMask &= ~(1 << channel); RefreshLabels(); }
    public void RefreshLabels()
    {
        if (stateLabel != null) stateLabel.text = (expanded ? "EXPANDED" : "FOLDED") + (isDetached ? " / EXTRACTED" : " / DOCKED");
        if (channelBadge == null) return;
        string channels = "";
        if ((boundChannelMask & 1) != 0) channels += " A";
        if ((boundChannelMask & 2) != 0) channels += " B";
        if ((boundChannelMask & 4) != 0) channels += " C";
        if ((boundChannelMask & 8) != 0) channels += " D";
        channelBadge.text = channels.Length == 0 ? "PROBE: --" : "PROBE:" + channels;
        channelBadge.color = channels.Length == 0 ? new Color(.45f, .6f, .7f) : new Color(1f, .8f, .35f);
    }
    private void Start() { if (!initialPoseCaptured) CaptureInitialPose(); RefreshVisibility(); }
}
