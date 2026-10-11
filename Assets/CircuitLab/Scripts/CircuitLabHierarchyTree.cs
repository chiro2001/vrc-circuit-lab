using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabHierarchyTree : UdonSharpBehaviour
{
    public CircuitLabModule[] modules;
    public CircuitLabNetworkCoordinator coordinator;
    public GameObject[] rowObjects;
    public RectTransform[] rowRects;
    public Text[] directoryLabels;
    [UdonSynced] public bool[] directoryExpanded;
    [UdonSynced] public float sharedScroll = 1f;
    [UdonSynced] public int selectedIndex = -1;
    public int[] parentIndices;
    public RectTransform content;
    public ScrollRect scrollRect;
    public float rowHeight = 44f;
    private bool _applying;
    private bool _publishPending;
    private bool CanControl() { return coordinator == null || coordinator.HasControl(); }
    private void Claim()
    {
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }
    public void ToggleDirectory(int moduleIndex)
    {
        if (!CanControl() || moduleIndex < 0 || moduleIndex >= directoryExpanded.Length) return;
        Claim();
        directoryExpanded[moduleIndex] = !directoryExpanded[moduleIndex];
        RefreshTree();
        Publish();
    }
    public void SelectModule(int moduleIndex) { if (!CanControl()) return; Claim(); selectedIndex = moduleIndex; Publish(); }
    public void RefreshTree()
    {
        int visibleRows = 0;
        for (int i = 0; i < modules.Length; i++)
        {
            bool visible = true; int parent = parentIndices[i];
            for (int depth = 0; depth < 32 && parent >= 0; depth++)
            { if (!directoryExpanded[parent]) { visible = false; break; } parent = parentIndices[parent]; }
            rowObjects[i].SetActive(visible);
            if (visible) rowRects[i].anchoredPosition = new Vector2(0, -(visibleRows++ + .5f) * rowHeight);
            if (directoryLabels[i] != null) directoryLabels[i].text = modules[i].childModules.Length == 0 ? "-" : (directoryExpanded[i] ? "v" : ">");
        }
        if (content != null) content.sizeDelta = new Vector2(content.sizeDelta.x, visibleRows * rowHeight);
    }
    public void ScrollUp() { if (CanControl() && scrollRect != null) { scrollRect.verticalNormalizedPosition = Mathf.Clamp01(scrollRect.verticalNormalizedPosition + .2f); ScrollChanged(); } }
    public void ScrollDown() { if (CanControl() && scrollRect != null) { scrollRect.verticalNormalizedPosition = Mathf.Clamp01(scrollRect.verticalNormalizedPosition - .2f); ScrollChanged(); } }
    public void ScrollChanged()
    {
        if (_applying || !CanControl() || scrollRect == null) return;
        Claim(); sharedScroll = scrollRect.verticalNormalizedPosition;
        if (!_publishPending) { _publishPending = true; SendCustomEventDelayedSeconds("Publish", .1f); }
    }
    public void Publish() { _publishPending = false; if (Utilities.IsValid(Networking.LocalPlayer) && Networking.IsOwner(gameObject)) RequestSerialization(); }
    public override void OnDeserialization() { RefreshTree(); ApplySharedScroll(); }
    public void ApplySharedScroll() { _applying = true; if (scrollRect != null) scrollRect.verticalNormalizedPosition = sharedScroll; _applying = false; }
    private void Update()
    {
        if (scrollRect == null) return;
        bool canControl = CanControl(); scrollRect.enabled = canControl;
        if (scrollRect.verticalScrollbar != null) scrollRect.verticalScrollbar.interactable = canControl;
    }
    private void Start() { RefreshTree(); ApplySharedScroll(); }
}
