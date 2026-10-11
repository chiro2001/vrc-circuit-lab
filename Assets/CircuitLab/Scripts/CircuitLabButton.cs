using UdonSharp;
using UnityEngine.UI;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabButton : UdonSharpBehaviour
{
    public UdonSharpBehaviour target;
    public string eventName;
    public CircuitLabNetworkCoordinator coordinator;
    public bool requireControl;
    public Button uiButton;

    public override void Interact() { Press(); }
    public void Press()
    {
        if (requireControl && coordinator != null && !coordinator.TryAcquireUnclaimedControl()) return;
        if (target != null && eventName != null && eventName.Length > 0)
            target.SendCustomEvent(eventName);
    }
    private void Update()
    {
        if (uiButton != null && requireControl)
            uiButton.interactable = coordinator == null || coordinator.CanUseControls();
    }
}
