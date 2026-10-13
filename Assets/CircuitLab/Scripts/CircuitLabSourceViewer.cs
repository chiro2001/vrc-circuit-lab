using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Each player browses independently; reading source never changes shared CPU state.
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class CircuitLabSourceViewer : UdonSharpBehaviour
{
    public CircuitLabLocalization localization;
    public int[] moduleIds, moduleDefinitions;
    public string[] modulePaths;
    public string[] definitionNames, definitionTexts;
    public int[] definitionFiles, definitionStartLines, definitionEndLines;
    public bool[] externalMemory;
    public string[] filePaths, fileTexts;
    public Text instanceText, contextText, sourceText, statusText, memoryNoteText;
    public Image[] rowImages;
    public TextMeshProUGUI[] sourceDisplays;
    // TMP layout is measured in the Editor because preferredHeight is not
    // exposed by Udon. Three language-face heights per definition/file.
    public float[] definitionHeights, fileHeights;
    public ScrollRect sourceScrollRect, moduleScrollRect;
    public RectTransform sourceContentRect, sourceTextRect;
    public int selectedIndex;
    public bool fullFile;
    public float lineHeight = 19f;
    public int wrapColumns = 76;
    private int _lastIndex = -1, _lastLanguage = -1;
    private bool _lastFullFile;

    private void Update()
    {
        int language = localization == null ? 0 : localization.languageIndex;
        if (selectedIndex != _lastIndex || fullFile != _lastFullFile || language != _lastLanguage) RenderSource();
    }
    public void ShowModuleById(int moduleId)
    {
        if (moduleIds == null) return;
        for (int i = 0; i < moduleIds.Length; i++)
            if (moduleIds[i] == moduleId) { SelectModule(i); return; }
    }
    public void SelectModule(int index)
    {
        if (moduleIds == null || index < 0 || index >= moduleIds.Length) return;
        selectedIndex = index; fullFile = false; RenderSource();
        if (moduleScrollRect != null)
        {
            float viewport = moduleScrollRect.viewport.rect.height;
            float overflow = moduleScrollRect.content.sizeDelta.y - viewport;
            if (overflow > 0f)
                moduleScrollRect.verticalNormalizedPosition = 1f - Mathf.Clamp(index * 35f + 17.5f - viewport * .5f, 0, overflow) / overflow;
        }
    }
    public void PreviousModule() { SelectModule(selectedIndex - 1); }
    public void NextModule() { SelectModule(selectedIndex + 1); }
    public void ShowModuleSource() { fullFile = false; RenderSource(); }
    public void ShowFullFile() { fullFile = true; RenderSource(); }
    public void ScrollSourceUp() { Scroll(sourceScrollRect, .18f); }
    public void ScrollSourceDown() { Scroll(sourceScrollRect, -.18f); }
    public void ScrollModulesUp() { Scroll(moduleScrollRect, .18f); }
    public void ScrollModulesDown() { Scroll(moduleScrollRect, -.18f); }
    private void Scroll(ScrollRect scroll, float delta)
    {
        if (scroll != null) scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + delta);
    }
    private string LineNumber(int value)
    {
        string result = "" + value;
        while (result.Length < 3) result = "0" + result;
        return result;
    }
    public void RenderSource()
    {
        if (moduleDefinitions == null || moduleDefinitions.Length == 0 || definitionTexts == null) return;
        selectedIndex = Mathf.Clamp(selectedIndex, 0, moduleDefinitions.Length - 1);
        int definition = moduleDefinitions[selectedIndex];
        if (definition < 0 || definition >= definitionTexts.Length) return;
        int file = definitionFiles[definition];
        string source = (fullFile ? fileTexts[file] : definitionTexts[definition]).Replace("\r", "").Replace("\t", "    ");
        int line = fullFile ? 1 : definitionStartLines[definition];
        int firstLine = line, start = 0, visualLines = 0;
        string listing = "";
        while (start < source.Length)
        {
            int end = source.IndexOf('\n', start);
            if (end < 0) end = source.Length;
            string content = source.Substring(start, end - start);
            listing += LineNumber(line++) + " | " + content + "\n";
            visualLines += Mathf.Max(1, (content.Length + 6 + wrapColumns - 1) / Mathf.Max(1, wrapColumns));
            start = end + 1;
        }
        if (instanceText != null) instanceText.text = modulePaths[selectedIndex];
        if (contextText != null) contextText.text = definitionNames[definition] + "  |  " + filePaths[file];
        if (sourceText != null) sourceText.text = listing;
        int language = localization == null ? 0 : localization.languageIndex;
        string scope = fullFile ? (language == 1 ? "完整文件" : language == 2 ? "ファイル全体" : "FULL FILE")
            : (language == 1 ? "模块源码" : language == 2 ? "モジュールソース" : "MODULE SOURCE");
        if (statusText != null) statusText.text = scope + "  |  " + firstLine + "-" + (line - 1)
            + "  |  " + (selectedIndex + 1) + " / " + moduleDefinitions.Length;
        if (memoryNoteText != null) memoryNoteText.text = externalMemory[definition]
            ? (language == 1 ? "ROM/RAM 的 Verilog 接口；存储内容由 GPU 存储纹理提供。"
                : language == 2 ? "ROM/RAMのVerilogインターフェース。メモリはGPUテクスチャで実装します。"
                : "ROM/RAM Verilog interface; storage is supplied by GPU memory textures.") : "";
        float preferred = (visualLines + 1) * lineHeight;
        if (sourceDisplays != null && language >= 0 && language < sourceDisplays.Length && sourceDisplays[language] != null)
        {
            sourceDisplays[language].text = listing;
        }
        int metric = (fullFile ? file : definition) * 3 + Mathf.Clamp(language, 0, 2);
        float[] metrics = fullFile ? fileHeights : definitionHeights;
        if (metrics != null && metric < metrics.Length && metrics[metric] > 0f) preferred = metrics[metric];
        float height = Mathf.Max(sourceScrollRect.viewport.rect.height, preferred + 16f);
        sourceContentRect.sizeDelta = new Vector2(sourceContentRect.sizeDelta.x, height);
        sourceTextRect.sizeDelta = new Vector2(sourceTextRect.sizeDelta.x, height);
        sourceScrollRect.verticalNormalizedPosition = 1f;
        if (rowImages != null)
            for (int i = 0; i < rowImages.Length; i++)
                if (rowImages[i] != null) rowImages[i].color = i == selectedIndex
                    ? new Color(.08f, .35f, .38f, 1f) : new Color(.045f, .09f, .12f, 1f);
        _lastIndex = selectedIndex; _lastFullFile = fullFile; _lastLanguage = language;
    }
}
