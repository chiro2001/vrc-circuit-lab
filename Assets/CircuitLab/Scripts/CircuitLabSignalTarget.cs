using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabSignalTarget : UdonSharpBehaviour
{
    public int[] signalIds;
    public int targetIndex;
    public string signalLabel;
    public CircuitLabModule ownerModule;
    public CircuitLabProbe[] probes;
    public GameObject highlight;
    public bool isPort;
    public bool isPreselected;
    public int boundChannelMask;

    // Desktop selects the active physical probe, or the first available channel.
    public override void Interact()
    {
        CircuitLabProbe selected = null;
        if (probes == null) return;
        for (int i = 0; i < probes.Length; i++)
            if (probes[i] != null && probes[i].isSelected) selected = probes[i];
        if (selected == null)
            for (int i = 0; i < probes.Length; i++)
                if (probes[i] != null && probes[i].boundTarget == null) { selected = probes[i]; break; }
        if (selected == null && probes.Length > 0) selected = probes[0];
        if (selected != null) selected.BindTarget(this);
    }

    public void Preselect(bool value)
    {
        isPreselected = value;
        RefreshHighlight();
    }

    public void AddChannel(int channel)
    {
        boundChannelMask |= 1 << channel;
        CircuitLabModule module = ownerModule;
        for (int depth = 0; depth < 32 && module != null; depth++)
        {
            module.AddProbeChannel(channel);
            module = module.logicalParent;
        }
        RefreshHighlight();
    }

    public void RemoveChannel(int channel)
    {
        boundChannelMask &= ~(1 << channel);
        CircuitLabModule module = ownerModule;
        for (int depth = 0; depth < 32 && module != null; depth++)
        {
            module.RemoveProbeChannel(channel);
            module = module.logicalParent;
        }
        RefreshHighlight();
    }

    public void RefreshHighlight()
    {
        if (highlight != null) highlight.SetActive(isPreselected || boundChannelMask != 0);
    }
}
