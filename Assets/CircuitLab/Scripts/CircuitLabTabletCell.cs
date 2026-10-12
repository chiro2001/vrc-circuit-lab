using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabTabletCell : UdonSharpBehaviour
{
    public CircuitLabStateTablet tablet;
    public int slot;
    public bool ramAddressCell;

    public void Select()
    {
        if (tablet == null) return;
        if (ramAddressCell)
        {
            tablet.SelectRamCell(slot);
        }
        else
        {
            tablet.SelectCell(slot);
        }
    }
}
