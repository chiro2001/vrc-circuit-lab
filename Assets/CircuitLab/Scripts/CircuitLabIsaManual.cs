using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CircuitLabIsaManual : UdonSharpBehaviour
{
    public CircuitLabNetworkCoordinator coordinator;
    public CircuitLabLocalization localization;
    public Text titleText, bodyText, pageText;
    public string[] titlesEn, titlesZh, titlesJa;
    public string[] pagesEn, pagesZh, pagesJa;
    [UdonSynced] public int page;
    private int _lastPage = -1;
    private int _lastLanguage = -1;

    private void Update()
    {
        int language = localization == null ? 0 : localization.languageIndex;
        if (page != _lastPage || language != _lastLanguage) RenderPage();
    }
    public void PreviousPage() { ChangePage(page - 1); }
    public void NextPage() { ChangePage(page + 1); }
    public void Overview() { ChangePage(0); }
    public void Encoding() { ChangePage(1); }
    public void Instructions() { ChangePage(2); }
    public void Extensions() { ChangePage(3); }
    public void Flags() { ChangePage(4); }
    public void Examples() { ChangePage(5); }
    public void ClaimControl()
    {
        if (coordinator == null || coordinator.HasControl()) return;
        if (coordinator.controlAuthorityId < 0) coordinator.TryAcquireUnclaimedControl();
        else coordinator.RequestControl();
    }
    private void ChangePage(int target)
    {
        if (coordinator != null && !coordinator.TryAcquireUnclaimedControl()) return;
        if (Utilities.IsValid(Networking.LocalPlayer) && !Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        page = Mathf.Clamp(target, 0, pagesEn.Length - 1);
        RenderPage();
        RequestSerialization();
    }
    public void RenderPage()
    {
        if (pagesEn == null || pagesEn.Length == 0) return;
        page = Mathf.Clamp(page, 0, pagesEn.Length - 1);
        int language = localization == null ? 0 : localization.languageIndex;
        string[] titles = language == 1 ? titlesZh : language == 2 ? titlesJa : titlesEn;
        string[] pages = language == 1 ? pagesZh : language == 2 ? pagesJa : pagesEn;
        if (titleText != null) titleText.text = titles[page];
        if (bodyText != null) bodyText.text = pages[page];
        if (pageText != null) pageText.text = (language == 1 ? "共享页面 " : language == 2 ? "共有ページ " : "SHARED PAGE ") + (page + 1) + " / " + pagesEn.Length;
        _lastPage = page; _lastLanguage = language;
    }
    public override void OnDeserialization() { RenderPage(); }
}
