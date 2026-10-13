using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabSourceRow : UdonSharpBehaviour
{
    public CircuitLabSourceViewer viewer;
    public int moduleIndex;
    public void Select() { if (viewer != null) viewer.SelectModule(moduleIndex); }
}
