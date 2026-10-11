using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GpuNetlistButton : UdonSharpBehaviour
{
    public GpuNetlistSmoke target;
    public string eventName;

    public override void Interact()
    {
        target.SendCustomEvent(eventName);
    }
}
