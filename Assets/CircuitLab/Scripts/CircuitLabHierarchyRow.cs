using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CircuitLabHierarchyRow : UdonSharpBehaviour
{
    public CircuitLabHierarchyTree tree;
    public CircuitLabModule module;
    public int moduleIndex;
    public void ToggleDirectory() { if (tree != null) tree.ToggleDirectory(moduleIndex); }
    public void ToggleModule() { if (tree != null) tree.SelectModule(moduleIndex); if (module != null) module.ToggleExpanded(); }
    public void Extract() { if (module != null) module.DetachFromParent(); }
    public void ShowSource() { if (module != null) module.ShowSource(); }
}
