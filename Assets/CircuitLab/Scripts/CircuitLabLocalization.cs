using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabLocalization : UdonSharpBehaviour
{
    // Public consumer interface: 0 English, 1 Simplified Chinese, 2 Japanese.
    // languageOverride is local to this player: -1 AUTO, 0 English, 1 Chinese, 2 Japanese.
    public int languageIndex;
    public int languageOverride = -1;

    public Text[] sourceTexts;
    // Subset of sourceTexts that Udon code actually writes at runtime (terminal
    // output, HUD, tablet and program labels). Scanning 5.5k legacy texts five
    // times a second dominated the frame budget, so only this subset is polled
    // frequently and everything else is refreshed on a slow full sweep.
    public int[] dynamicIndices = new int[0];
    public float fullScanInterval = 2f;
    public TextMeshProUGUI[] englishDisplays;
    public TextMeshProUGUI[] chineseDisplays;
    public TextMeshProUGUI[] japaneseDisplays;
    public bool[] keepText;
    public Text[] inputFontTargets;
    public Text[] codeInputTargets;
    public Text[] inputPlaceholders;
    public string[] inputPlaceholderEnglish;
    public Image[] languageButtonImages;

    public Font englishInputFont;
    public Font chineseInputFont;
    public Font japaneseInputFont;
    public Font codeInputFont;

    // Phrases are populated from Localization/CircuitLabLocalization.tsv by the
    // editor builder. Phrase substitution preserves counters, names and values
    // emitted by the existing Udon scripts.
    public string[] englishPhrases;
    public string[] chinesePhrases;
    public string[] japanesePhrases;

    public Color selectedButtonColor = new Color(.08f, .48f, .48f, 1f);
    public Color normalButtonColor = new Color(.07f, .19f, .26f, 1f);

    private string[] _lastSourceText;
    private Color[] _lastSourceColor;
    private bool[] _lastActive;
    private int _lastLanguage = -1;
    private float _nextTextSync;
    private float _nextFullScan;

    private void Start()
    {
        _lastSourceText = new string[sourceTexts == null ? 0 : sourceTexts.Length];
        _lastSourceColor = new Color[_lastSourceText.Length];
        _lastActive = new bool[_lastSourceText.Length];
        if (languageOverride < 0) languageIndex = ResolveLanguage(VRCPlayerApi.GetCurrentLanguage());
        ApplyLocale(true);
    }

    private void Update()
    {
        if (Time.time < _nextTextSync) return;
        _nextTextSync = Time.time + .20f;
        bool fullScan = dynamicIndices == null || dynamicIndices.Length == 0 || Time.time >= _nextFullScan;
        if (fullScan) _nextFullScan = Time.time + Mathf.Max(.5f, fullScanInterval);
        ApplyLocale(false, fullScan);
    }

    private void ScanSources(bool fullScan)
    {
        int[] indices = fullScan ? null : dynamicIndices;
        int count = fullScan ? (sourceTexts == null ? 0 : sourceTexts.Length) : indices.Length;
        for (int slot = 0; slot < count; slot++)
            SyncSource(fullScan ? slot : indices[slot]);
    }

    private void SyncSource(int index) { SyncSource(index, false); }

    private void SyncSource(int index, bool force)
    {
        int i = index;
        if (englishDisplays == null || chineseDisplays == null || japaneseDisplays == null) return;
        if (i < 0 || i >= sourceTexts.Length || i >= englishDisplays.Length
            || i >= chineseDisplays.Length || i >= japaneseDisplays.Length) return;
        Text source = sourceTexts[i];
        TextMeshProUGUI englishTarget = englishDisplays[i];
        TextMeshProUGUI chineseTarget = chineseDisplays[i];
        TextMeshProUGUI japaneseTarget = japaneseDisplays[i];
        if (source == null || englishTarget == null || chineseTarget == null || japaneseTarget == null) return;
        if (!source.gameObject.activeInHierarchy) { _lastActive[i] = false; return; }
        bool justActivated = !_lastActive[i];
        _lastActive[i] = true;
        string value = source.text == null ? "" : source.text;
        bool textChanged = force || justActivated || _lastSourceText[i] != value;
        bool colorChanged = force || justActivated || _lastSourceColor[i] != source.color;
        if (!textChanged && !colorChanged) return;
        bool keep = keepText != null && i < keepText.Length && keepText[i];
        if (textChanged)
        {
            englishTarget.text = keep ? value : Localize(value, 0);
            chineseTarget.text = keep ? value : Localize(value, 1);
            japaneseTarget.text = keep ? value : Localize(value, 2);
            _lastSourceText[i] = value;
        }
        if (colorChanged)
        {
            Color displayColor = Color.Lerp(source.color, Color.white, .10f);
            englishTarget.color = displayColor;
            chineseTarget.color = displayColor;
            japaneseTarget.color = displayColor;
            _lastSourceColor[i] = source.color;
        }
    }

    public override void OnLanguageChanged(string language)
    {
        if (languageOverride >= 0) return;
        languageIndex = ResolveLanguage(language);
        ApplyLocale(true);
    }

    public void SetAuto()
    {
        languageOverride = -1;
        languageIndex = ResolveLanguage(VRCPlayerApi.GetCurrentLanguage());
        ApplyLocale(true);
    }

    public void SetEnglish() { SetManualLanguage(0); }
    public void SetChinese() { SetManualLanguage(1); }
    public void SetJapanese() { SetManualLanguage(2); }

    private void SetManualLanguage(int index)
    {
        languageOverride = index;
        languageIndex = index;
        ApplyLocale(true);
    }

    private int ResolveLanguage(string language)
    {
        if (language == null) return 0;
        string lower = language.ToLower();
        if (lower.IndexOf("zh") >= 0 || lower.IndexOf("chinese") >= 0) return 1;
        if (lower.IndexOf("ja") >= 0 || lower.IndexOf("japanese") >= 0) return 2;
        return 0;
    }

    private Font CurrentInputFont()
    {
        if (languageIndex == 1 && chineseInputFont != null) return chineseInputFont;
        if (languageIndex == 2 && japaneseInputFont != null) return japaneseInputFont;
        return englishInputFont;
    }

    private void ApplyLocale(bool force)
    {
        ApplyLocale(force, true);
    }

    private void ApplyLocale(bool force, bool fullScan)
    {
        bool languageChanged = _lastLanguage != languageIndex;
        if (languageChanged || force)
        {
            SetDisplayLanguage();
            Font inputFont = CurrentInputFont();
            if (inputFontTargets != null && inputFont != null)
                for (int i = 0; i < inputFontTargets.Length; i++)
                    if (inputFontTargets[i] != null) inputFontTargets[i].font = inputFont;
            if (codeInputTargets != null && codeInputFont != null)
                for (int i = 0; i < codeInputTargets.Length; i++)
                    if (codeInputTargets[i] != null) codeInputTargets[i].font = codeInputFont;
            RefreshLanguageButtons();
            _lastLanguage = languageIndex;
        }

        if (sourceTexts == null || englishDisplays == null || chineseDisplays == null || japaneseDisplays == null) return;
        if ((force || languageChanged) && inputPlaceholders != null && inputPlaceholderEnglish != null)
            for (int i = 0; i < inputPlaceholders.Length && i < inputPlaceholderEnglish.Length; i++)
                if (inputPlaceholders[i] != null)
                    inputPlaceholders[i].text = Localize(inputPlaceholderEnglish[i] == null ? "" : inputPlaceholderEnglish[i], languageIndex);
        if (_lastSourceText == null || _lastSourceText.Length != sourceTexts.Length)
        {
            _lastSourceText = new string[sourceTexts.Length];
            _lastSourceColor = new Color[sourceTexts.Length];
            _lastActive = new bool[sourceTexts.Length];
        }
        // A forced pass or a language switch must refresh every text; the routine
        // poll only walks the runtime-driven subset.
        bool scanAll = fullScan || force || languageChanged;
        if (scanAll)
        {
            int count = Mathf.Min(sourceTexts.Length, Mathf.Min(englishDisplays.Length, Mathf.Min(chineseDisplays.Length, japaneseDisplays.Length)));
            for (int i = 0; i < count; i++) SyncSource(i, force || languageChanged);
        }
        else ScanSources(false);
    }

    private void SetDisplayLanguage()
    {
        SetLanguageGroup(englishDisplays, languageIndex == 0);
        SetLanguageGroup(chineseDisplays, languageIndex == 1);
        SetLanguageGroup(japaneseDisplays, languageIndex == 2);
    }

    private void SetLanguageGroup(TextMeshProUGUI[] displays, bool active)
    {
        if (displays == null) return;
        for (int i = 0; i < displays.Length; i++)
            if (displays[i] != null) displays[i].gameObject.SetActive(active);
    }

    private string Localize(string value, int language)
    {
        if (language == 0 || englishPhrases == null) return value;
        string[] translated = language == 1 ? chinesePhrases : japanesePhrases;
        if (translated == null) return value;

        // Exact labels are common and avoid doing replacements for every phrase.
        for (int i = 0; i < englishPhrases.Length && i < translated.Length; i++)
            if (englishPhrases[i] == value) return translated[i];

        string result = value;
        for (int i = 0; i < englishPhrases.Length && i < translated.Length; i++)
        {
            string phrase = englishPhrases[i];
            if (phrase == null || phrase.Length == 0 || phrase.Length > result.Length) continue;
            if (result.IndexOf(phrase) >= 0) result = result.Replace(phrase, translated[i]);
        }
        return result;
    }

    private void RefreshLanguageButtons()
    {
        if (languageButtonImages == null) return;
        int selected = languageOverride < 0 ? 0 : languageOverride + 1;
        for (int i = 0; i < languageButtonImages.Length; i++)
            if (languageButtonImages[i] != null)
                languageButtonImages[i].color = i == selected ? selectedButtonColor : normalButtonColor;
    }
}
