using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class CircuitLabNetworkClock : UdonSharpBehaviour
{
    public CircuitLabNetworkCoordinator coordinator;
    [UdonSynced] public int epoch;
    [UdonSynced] public int cycle;
    [UdonSynced] public int samples;
    [UdonSynced] public int inputMask;
    [UdonSynced] public bool running;
    [UdonSynced] public bool selfTest;
    [UdonSynced] public int window = 128;
    [UdonSynced] public int offset;
    [UdonSynced] public int cursorA;
    [UdonSynced] public int cursorB;
    [UdonSynced] public int bitView = -1;
    [UdonSynced] public int numberFormat;
    [UdonSynced] public bool followLatest = true;
    [UdonSynced] public bool analyticAA = true;
    [UdonSynced] public float edgeWidth = 1f;
    [UdonSynced] public float speed = 2f;
}
