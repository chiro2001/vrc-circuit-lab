using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabWire : UdonSharpBehaviour
{
    public LineRenderer line;
    public Transform source;
    public Transform destination;
    public float routingDepth = .25f;

    private void LateUpdate()
    {
        if (line == null || source == null || destination == null) return;
        bool visible = source.gameObject.activeInHierarchy && destination.gameObject.activeInHierarchy;
        line.enabled = visible;
        if (!visible) return;
        Vector3 a = source.position;
        Vector3 b = destination.position;
        // Six prebuilt points: orthogonal x/y routing in a separate shallow z plane.
        float z = Mathf.Max(a.z, b.z) + routingDepth;
        float x = (a.x + b.x) * .5f;
        line.SetPosition(0, a);
        line.SetPosition(1, new Vector3(a.x, a.y, z));
        line.SetPosition(2, new Vector3(x, a.y, z));
        line.SetPosition(3, new Vector3(x, b.y, z));
        line.SetPosition(4, new Vector3(b.x, b.y, z));
        line.SetPosition(5, b);
    }
}
