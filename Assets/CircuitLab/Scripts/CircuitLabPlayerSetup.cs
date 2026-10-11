using UdonSharp;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabPlayerSetup : UdonSharpBehaviour
{
    public float jumpImpulse = 3.5f;
    public float walkSpeed = 2f;
    public float runSpeed = 4f;
    public float strafeSpeed = 2f;
    public float gravityStrength = 1f;
    private void Start() { InitializeLocalPlayer(); }
    public void InitializeLocalPlayer()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) { SendCustomEventDelayedSeconds("InitializeLocalPlayer", 1f); return; }
        player.SetJumpImpulse(jumpImpulse); player.SetWalkSpeed(walkSpeed);
        player.SetRunSpeed(runSpeed); player.SetStrafeSpeed(strafeSpeed); player.SetGravityStrength(gravityStrength);
    }
    public override void OnPlayerRespawn(VRCPlayerApi player) { if (player.isLocal) InitializeLocalPlayer(); }
}
