using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.Udon;

// Run after BuildCircuitLab.BuildScene / UpgradeScene:
// unity command run_script --file AgentScripts/BuildCircuitLabLocalization.cs --entry BuildCircuitLabLocalization.Apply
public static class BuildCircuitLabLocalization
{
    private const string Root = "Assets/CircuitLab/Localization";
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private const string ScriptPath = "Assets/CircuitLab/Scripts/CircuitLabLocalization.cs";
    private const string ProgramPath = "Assets/CircuitLab/Programs/CircuitLabLocalization.asset";
    private const string CatalogPath = Root + "/CircuitLabLocalization.tsv";
    private const int InteractionLayer = 17;
    private const string SupplementaryMathGlyphs = "\u21A6\u21B3\u2308\u2309\u230A\u230B";

    private const string SuperscriptGlyphs = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    private sealed class Phrase
    {
        public string english;
        public string chinese;
        public string japanese;
    }

    private sealed class GlyphSets
    {
        public string english;
        public string chinese;
        public string japanese;
        public string symbols;
    }

    public static async Task<object> Apply()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || scene.path != ScenePath)
            throw new InvalidOperationException("Activate the CircuitLab scene in Edit Mode before applying localization.");

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        await EnsureTmpResources();
        RemoveUnusedVariableFontSources();
        EnsureLocalizationProgram();
        var world = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "CircuitLabWorld");
        if (world == null) throw new InvalidOperationException("CircuitLabWorld is missing; run the main builder first.");

        CircuitLabLocalization localization = world.GetComponent<CircuitLabLocalization>();
        if (localization == null) localization = world.AddUdonSharpComponent<CircuitLabLocalization>();
        Undo.RecordObject(localization, "Apply CircuitLab Localization");

        EnsureLocalePanel(world.transform, localization);
        var sourceTexts = new List<Text>();
        foreach (GameObject root in scene.GetRootGameObjects())
            sourceTexts.AddRange(root.GetComponentsInChildren<Text>(true));
        sourceTexts = sourceTexts.Distinct().Where(text => text != null).ToList();

        var codeFlags = new List<bool>();
        var keepFlags = new List<bool>();
        var inputFontTargets = new List<Text>();
        var codeInputTargets = new List<Text>();
        var inputPlaceholders = new List<Text>();
        var placeholderEnglish = new List<string>();
        var includedSources = new List<Text>();
        foreach (Text source in sourceTexts)
        {
            InputField input = source.GetComponentInParent<InputField>(true);
            if (input != null && (input.textComponent == source || input.placeholder == source))
            {
                if (input.textComponent == source && !codeInputTargets.Contains(source)) codeInputTargets.Add(source);
                if (input.placeholder == source)
                {
                    if (!inputFontTargets.Contains(source)) inputFontTargets.Add(source);
                    inputPlaceholders.Add(source);
                    placeholderEnglish.Add(source.text == null ? "" : source.text);
                }
                continue;
            }

            includedSources.Add(source);
            codeFlags.Add(IsCodeText(source));
            keepFlags.Add(MustPreserveText(source));
        }

        GlyphSets glyphs = CollectGlyphs(scene, sourceTexts);
        if (!AssetDatabase.IsValidFolder(Root + "/Fonts")) AssetDatabase.CreateFolder(Root, "Fonts");
        TMP_FontAsset zhFont = EnsureStaticFont("SimplifiedChinese-SDF.asset", "FontSources/NotoSansSC-Regular.ttf", glyphs.chinese, 2048, 40, 6);
        TMP_FontAsset enFont = EnsureStaticFont("EnglishUI-SDF.asset", "FontSources/NotoSansJP-Regular.ttf", glyphs.english, 2048, 40, 6, zhFont);
        TMP_FontAsset jaFont = EnsureStaticFont("Japanese-SDF.asset", "FontSources/NotoSansJP-Regular.ttf", glyphs.japanese, 2048, 40, 6, zhFont);
        TMP_FontAsset symbolFont = EnsureStaticFont("MathSymbols-SDF.asset", "FontSources/NotoSansMath-Regular.ttf", glyphs.symbols, 512, 40, 6);
        TMP_FontAsset superscriptFont = EnsureStaticFont("Superscripts-IBM-SDF.asset", "FontSources/IBMPlexMono-Regular.ttf", SuperscriptGlyphs, 512, 40, 6);
        // The JP face remains authoritative for every glyph it contains. Simplified Chinese is
        // consulted only for legacy CJK metadata or symbols missing from the primary face.
        // Consumer scripts refresh their local-language source on the following frame.
        // Keep Japanese glyphs readable during the transition back to English.
        SetFallback(enFont, jaFont, zhFont, symbolFont, superscriptFont);
        SetFallback(zhFont, symbolFont, superscriptFont);
        SetFallback(jaFont, zhFont, symbolFont, superscriptFont);
        TMP_FontAsset codeFontEn = EnsureStaticFont("Code-IBM-Plex-Mono-En-SDF.asset", "FontSources/IBMPlexMono-Regular.ttf",
            AsciiGlyphs(), 1024, 40, 6);
        TMP_FontAsset codeFontZh = EnsureStaticFont("Code-IBM-Plex-Mono-Zh-SDF.asset", "FontSources/IBMPlexMono-Regular.ttf",
            AsciiGlyphs(), 1024, 40, 6);
        TMP_FontAsset codeFontJa = EnsureStaticFont("Code-IBM-Plex-Mono-Ja-SDF.asset", "FontSources/IBMPlexMono-Regular.ttf",
            AsciiGlyphs(), 1024, 40, 6);
        SetFallback(codeFontEn, enFont, zhFont, symbolFont, superscriptFont);
        SetFallback(codeFontZh, zhFont, symbolFont, superscriptFont);
        SetFallback(codeFontJa, jaFont, zhFont, symbolFont, superscriptFont);

        int previewLanguage = localization.languageOverride >= 0 ? Mathf.Clamp(localization.languageOverride, 0, 2) : 0;
        var englishDisplays = new List<TextMeshProUGUI>();
        var chineseDisplays = new List<TextMeshProUGUI>();
        var japaneseDisplays = new List<TextMeshProUGUI>();
        for (int i = 0; i < includedSources.Count; i++)
        {
            if (i % 128 == 0) await Task.Yield();
            Text source = includedSources[i];
            bool mono = codeFlags[i];
            TMP_FontAsset englishFace = mono ? codeFontEn : enFont;
            TMP_FontAsset chineseFace = mono ? codeFontZh : zhFont;
            TMP_FontAsset japaneseFace = mono ? codeFontJa : jaFont;
            englishDisplays.Add(EnsureTextDisplay(source, "LocalizedTMP_EN", englishFace, previewLanguage == 0));
            chineseDisplays.Add(EnsureTextDisplay(source, "LocalizedTMP_ZH", chineseFace, previewLanguage == 1));
            japaneseDisplays.Add(EnsureTextDisplay(source, "LocalizedTMP_JA", japaneseFace, previewLanguage == 2));
        }

        List<Phrase> phrases = ReadPhrases();
        localization.sourceTexts = includedSources.ToArray();
        localization.englishDisplays = englishDisplays.ToArray();
        localization.chineseDisplays = chineseDisplays.ToArray();
        localization.japaneseDisplays = japaneseDisplays.ToArray();
        localization.keepText = keepFlags.ToArray();
        localization.inputFontTargets = inputFontTargets.ToArray();
        localization.codeInputTargets = codeInputTargets.ToArray();
        localization.inputPlaceholders = inputPlaceholders.ToArray();
        localization.inputPlaceholderEnglish = placeholderEnglish.ToArray();
        localization.englishInputFont = AssetDatabase.LoadAssetAtPath<Font>(Root + "/FontSources/NotoSansJP-Regular.ttf");
        localization.chineseInputFont = AssetDatabase.LoadAssetAtPath<Font>(Root + "/FontSources/NotoSansSC-Regular.ttf");
        localization.japaneseInputFont = AssetDatabase.LoadAssetAtPath<Font>(Root + "/FontSources/NotoSansJP-Regular.ttf");
        localization.codeInputFont = AssetDatabase.LoadAssetAtPath<Font>(Root + "/FontSources/IBMPlexMono-Regular.ttf");
        localization.englishPhrases = phrases.Select(phrase => phrase.english).ToArray();
        localization.chinesePhrases = phrases.Select(phrase => phrase.chinese).ToArray();
        localization.japanesePhrases = phrases.Select(phrase => phrase.japanese).ToArray();
        Font inputFont = previewLanguage == 1 ? localization.chineseInputFont : previewLanguage == 2 ? localization.japaneseInputFont : localization.englishInputFont;
        for (int i = 0; i < inputFontTargets.Count; i++) if (inputFontTargets[i] != null) inputFontTargets[i].font = inputFont;
        for (int i = 0; i < codeInputTargets.Count; i++) if (codeInputTargets[i] != null) codeInputTargets[i].font = localization.codeInputFont;

        BindLocalization(world);
        CopyProxy(localization);
        EditorUtility.SetDirty(localization);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        return new
        {
            applied = true,
            scene = scene.path,
            localizedTextSources = includedSources.Count,
            localizedDisplayObjects = englishDisplays.Count + chineseDisplays.Count + japaneseDisplays.Count,
            inputTextSources = inputFontTargets.Count,
            codeInputSources = codeInputTargets.Count,
            phraseCount = phrases.Count,
            glyphCount = glyphs.english.Length + glyphs.chinese.Length + glyphs.japanese.Length + glyphs.symbols.Length + SuperscriptGlyphs.Length,
            localePanel = "AboutPanel/LocaleCanvas",
            localOnly = true,
            fontAssets = 8,
            boundPanels = new[] { "ProgramPanel", "ProgramBrowser", "IsaManualPanel", "OutputDisplayController" }
        };
    }

    public static object RepairTransitionFallback()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Repair authored font assets in Edit Mode.");
        TMP_FontAsset en = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(Root + "/Fonts/EnglishUI-SDF.asset");
        TMP_FontAsset ja = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(Root + "/Fonts/Japanese-SDF.asset");
        TMP_FontAsset zh = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(Root + "/Fonts/SimplifiedChinese-SDF.asset");
        TMP_FontAsset math = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(Root + "/Fonts/MathSymbols-SDF.asset");
        TMP_FontAsset superscripts = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(Root + "/Fonts/Superscripts-IBM-SDF.asset");
        if (en == null || ja == null || zh == null || math == null || superscripts == null)
            throw new InvalidOperationException("Apply localization before repairing its font chain.");
        SetFallback(en, ja, zh, math, superscripts);
        AssetDatabase.SaveAssets();
        return new { repaired = true, englishTransitionFallback = ja.name };
    }

    private static void EnsureLocalizationProgram()
    {
        UdonSharpProgramAsset program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramPath);
        if (program == null) throw new InvalidOperationException("Run the main builder's CompileUdon entry first; CircuitLabLocalization is not registered.");
        MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null) throw new InvalidOperationException("CircuitLabLocalization.cs has not been imported yet.");
        if (program.sourceCsScript != script)
            throw new InvalidOperationException("The main builder's ProgramNames list must register CircuitLabLocalization.cs.");
    }

    private static async Task EnsureTmpResources()
    {
        const string shaderName = "TextMeshPro/Distance Field";
        if (Shader.Find(shaderName) != null) return;

        string packageRoot = "Packages/com.unity.textmeshpro";
        UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_FontAsset).Assembly);
        if (package != null && !string.IsNullOrEmpty(package.resolvedPath)) packageRoot = package.resolvedPath;
        string resourcePackage = Path.Combine(packageRoot, "Package Resources/TMP Essential Resources.unitypackage");
        if (!File.Exists(resourcePackage))
        {
            string fallback = Path.Combine("Packages/com.unity.textmeshpro", "Package Resources/TMP Essential Resources.unitypackage");
            if (File.Exists(fallback)) resourcePackage = fallback;
            else throw new FileNotFoundException("TMP distance-field shader is missing and TMP Essential Resources could not be found.", resourcePackage);
        }

        AssetDatabase.ImportPackage(resourcePackage, false);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        // ImportPackage queues work on the Editor; pumping updates completes it.
        DateTime deadline = DateTime.UtcNow.AddSeconds(35);
        while (Shader.Find(shaderName) == null && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        if (Shader.Find(shaderName) == null)
            throw new InvalidOperationException("Imported TMP Essential Resources from " + resourcePackage + " but Shader.Find still cannot locate " + shaderName + ".");
    }

    private static void RemoveUnusedVariableFontSources()
    {
        foreach (string path in new[]
        {
            Root + "/FontSources/NotoSansSC-variable.ttf",
            Root + "/FontSources/NotoSansJP-variable.ttf"
        })
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
    }

    private static void EnsureLocalePanel(Transform world, CircuitLabLocalization localization)
    {
        Transform about = world.Find("AboutPanel");
        if (about == null) throw new InvalidOperationException("AboutPanel is missing; run the main builder first.");
        Transform existing = about.Find("LocaleCanvas");
        GameObject canvasObject;
        if (existing == null)
        {
            canvasObject = new GameObject("LocaleCanvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster),
                typeof(VRCUiShape), typeof(BoxCollider));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create local language panel");
            canvasObject.layer = InteractionLayer;
            canvasObject.transform.SetParent(about, false);
            canvasObject.transform.localPosition = new Vector3(4.25f, 0f, -.105f);
            canvasObject.transform.localRotation = Quaternion.identity;
            canvasObject.transform.localScale = Vector3.one * .01f;
            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(300f, 138f);
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            BoxCollider collider = canvasObject.GetComponent<BoxCollider>();
            collider.size = new Vector3(300f, 138f, 2f);
            collider.isTrigger = false;

            var background = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            Undo.RegisterCreatedObjectUndo(background, "Create language panel background");
            background.layer = InteractionLayer;
            background.transform.SetParent(canvasObject.transform, false);
            RectTransform backgroundRect = background.GetComponent<RectTransform>();
            backgroundRect.anchorMin = Vector2.zero; backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero; backgroundRect.offsetMax = Vector2.zero;
            Image image = background.GetComponent<Image>();
            image.color = new Color(.035f, .085f, .11f, .97f);
            image.raycastTarget = true;

            AddPanelText(canvasObject.transform, "LocaleTitle", "LANGUAGE / THIS PLAYER ONLY", new Vector2(0f, 43f),
                new Vector2(286f, 28f), 14, TextAnchor.MiddleCenter);
            AddLocaleButton(canvasObject.transform, "AUTO", "AUTO", new Vector2(-106.5f, -17f), new Vector2(64f, 30f), localization, "SetAuto");
            AddLocaleButton(canvasObject.transform, "CHINESE", "简体中文", new Vector2(-35.5f, -17f), new Vector2(64f, 30f), localization, "SetChinese");
            AddLocaleButton(canvasObject.transform, "JAPANESE", "日本語", new Vector2(35.5f, -17f), new Vector2(64f, 30f), localization, "SetJapanese");
            AddLocaleButton(canvasObject.transform, "ENGLISH", "English", new Vector2(106.5f, -17f), new Vector2(64f, 30f), localization, "SetEnglish");
        }
        else canvasObject = existing.gameObject;

        localization.languageButtonImages = new[]
        {
            canvasObject.transform.Find("AUTO") == null ? null : canvasObject.transform.Find("AUTO").GetComponent<Image>(),
            canvasObject.transform.Find("ENGLISH") == null ? null : canvasObject.transform.Find("ENGLISH").GetComponent<Image>(),
            canvasObject.transform.Find("CHINESE") == null ? null : canvasObject.transform.Find("CHINESE").GetComponent<Image>(),
            canvasObject.transform.Find("JAPANESE") == null ? null : canvasObject.transform.Find("JAPANESE").GetComponent<Image>()
        };
    }

    private static void AddPanelText(Transform parent, string name, string value, Vector2 position, Vector2 size,
        int fontSize, TextAnchor alignment)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        Undo.RegisterCreatedObjectUndo(go, "Create local language text");
        go.layer = InteractionLayer; go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        Text text = go.GetComponent<Text>();
        text.text = value; text.fontSize = fontSize; text.color = Color.white; text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
    }

    private static void AddLocaleButton(Transform parent, string name, string label, Vector2 position, Vector2 size,
        CircuitLabLocalization localization, string eventName)
    {
        Transform found = parent.Find(name);
        GameObject go = found == null ? new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button)) : found.gameObject;
        if (found == null) Undo.RegisterCreatedObjectUndo(go, "Create local language button");
        go.layer = InteractionLayer; go.transform.SetParent(parent, false);
        RectTransform rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        Image image = go.GetComponent<Image>(); image.color = new Color(.07f, .19f, .26f, 1f); image.raycastTarget = true;
        Button button = go.GetComponent<Button>(); button.targetGraphic = image;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        Transform labelTransform = go.transform.Find("Label");
        if (labelTransform == null)
        {
            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            Undo.RegisterCreatedObjectUndo(labelObject, "Create local language button label");
            labelObject.layer = InteractionLayer; labelObject.transform.SetParent(go.transform, false);
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one; labelRect.offsetMin = Vector2.zero; labelRect.offsetMax = Vector2.zero;
            Text text = labelObject.GetComponent<Text>(); text.text = label; text.fontSize = 12; text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false;
        }
        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(localization);
        if (button.onClick.GetPersistentEventCount() == 0)
            UnityEventTools.AddStringPersistentListener(button.onClick, backing.SendCustomEvent, eventName);
    }

    private static TextMeshProUGUI EnsureTextDisplay(Text source, string displayName, TMP_FontAsset font, bool active)
    {
        Transform obsolete = source.transform.Find("LocalizedTMP");
        if (obsolete != null) Undo.DestroyObjectImmediate(obsolete.gameObject);
        Transform child = source.transform.Find(displayName);
        TextMeshProUGUI display;
        if (child == null)
        {
            var go = new GameObject(displayName, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            Undo.RegisterCreatedObjectUndo(go, "Create SDF text display");
            go.layer = source.gameObject.layer;
            go.transform.SetParent(source.transform, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.pivot = source.rectTransform.pivot;
            rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
            rect.localPosition = Vector3.zero; rect.localRotation = Quaternion.identity; rect.localScale = Vector3.one;
            display = go.GetComponent<TextMeshProUGUI>();
            Undo.RecordObject(source, "Keep legacy text as hidden data source");
            source.enabled = false;
            EditorUtility.SetDirty(source);
        }
        else display = child.GetComponent<TextMeshProUGUI>();

        Undo.RecordObject(display, "Configure SDF text display");
        display.text = source.text;
        display.font = font;
        display.color = Color.Lerp(source.color, Color.white, .10f);
        display.fontSize = Mathf.Max(5f, source.fontSize * 1.08f);
        display.fontStyle = ConvertStyle(source.fontStyle);
        display.alignment = ConvertAlignment(source.alignment);
        display.enableWordWrapping = source.horizontalOverflow == HorizontalWrapMode.Wrap;
        display.overflowMode = source.verticalOverflow == VerticalWrapMode.Overflow ? TextOverflowModes.Overflow : TextOverflowModes.Truncate;
        display.richText = source.supportRichText;
        display.raycastTarget = false;
        display.lineSpacing = source.name == "IsaBody" ? 3.5f : 1.5f;
        display.margin = Vector4.zero;
        if (source.resizeTextForBestFit)
        {
            display.enableAutoSizing = true;
            display.fontSizeMin = Mathf.Max(5f, source.resizeTextMinSize);
            display.fontSizeMax = Mathf.Max(display.fontSizeMin, source.resizeTextMaxSize * 1.08f);
        }
        else display.enableAutoSizing = false;
        if (display.gameObject.activeSelf != active) display.gameObject.SetActive(active);
        EditorUtility.SetDirty(display);
        return display;
    }

    private static FontStyles ConvertStyle(FontStyle style)
    {
        if (style == FontStyle.Bold) return FontStyles.Bold;
        if (style == FontStyle.Italic) return FontStyles.Italic;
        if (style == FontStyle.BoldAndItalic) return FontStyles.Bold | FontStyles.Italic;
        return FontStyles.Normal;
    }

    private static TextAlignmentOptions ConvertAlignment(TextAnchor anchor)
    {
        switch (anchor)
        {
            case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
            case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
            case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
            case TextAnchor.MiddleLeft: return TextAlignmentOptions.MidlineLeft;
            case TextAnchor.MiddleRight: return TextAlignmentOptions.MidlineRight;
            case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
            case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
            case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
            default: return TextAlignmentOptions.Center;
        }
    }

    private static bool IsCodeText(Text text)
    {
        string name = text.gameObject.name;
        string path = HierarchyPath(text.transform);
        return name == "TerminalOutput" || name == "TerminalStatus" || name == "ISADetails"
            || name == "VirtualWordPreview" || name == "ProgramListingHeader" || name == "LedStatus"
            || name == "LedBitLabels" || name == "SelectedRamDetails" || name == "MemoryPortStatus"
            || name.StartsWith("ProgramWord_") || name.StartsWith("SignalCell_") || name.StartsWith("RamCell_")
            || name.StartsWith("PinLabel_") || name.StartsWith("GateLabel_") || name == "PortLabel"
            || name.StartsWith("VIRTUAL_BIT_") || name.StartsWith("TABLET_RAM_BIT_") || name == "ModulePath"
            || path.Contains("/SourceScrollRegion/") || path.Contains("/EncodingScrollRegion/");
    }

    private static bool MustPreserveText(Text text)
    {
        string name = text.gameObject.name;
        string path = HierarchyPath(text.transform);
        return name == "TerminalOutput" || name == "ISADetails" || name == "VirtualWordPreview"
            || name == "LedStatus" || name == "LifeStatus" || name == "IsaPageTitle" || name == "IsaBody" || name == "IsaPageNumber"
            || name == "ModulePath" || name == "PortLabel" || name.StartsWith("PinLabel_") || name.StartsWith("GateLabel_")
            || name.StartsWith("ProgramWord_") || name.StartsWith("SignalCell_") || name.StartsWith("RamCell_")
            || name.StartsWith("VIRTUAL_BIT_") || name.StartsWith("TABLET_RAM_BIT_")
            || path.Contains("/SourceScrollRegion/") || path.Contains("/EncodingScrollRegion/");
    }

    private static string HierarchyPath(Transform transform)
    {
        string path = transform.name;
        Transform parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return "/" + path;
    }

    private static void BindLocalization(GameObject world)
    {
        CircuitLabProgramPanel panel = world.GetComponentInChildren<CircuitLabProgramPanel>(true);
        if (panel != null) { panel.localization = world.GetComponent<CircuitLabLocalization>(); CopyProxy(panel); }
        CircuitLabProgramBrowser browser = world.GetComponentInChildren<CircuitLabProgramBrowser>(true);
        if (browser != null) { browser.localization = world.GetComponent<CircuitLabLocalization>(); CopyProxy(browser); }
        CircuitLabIsaManual manual = world.GetComponentInChildren<CircuitLabIsaManual>(true);
        if (manual != null) { manual.localization = world.GetComponent<CircuitLabLocalization>(); CopyProxy(manual); }
        CircuitLabOutputDisplay output = world.GetComponentInChildren<CircuitLabOutputDisplay>(true);
        if (output != null) { output.localization = world.GetComponent<CircuitLabLocalization>(); CopyProxy(output); }
    }

    private static void CopyProxy(UdonSharpBehaviour proxy)
    {
        if (proxy == null) return;
        UdonSharpEditorUtility.CopyProxyToUdon(proxy);
        EditorUtility.SetDirty(proxy);
        UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
        if (backing != null) EditorUtility.SetDirty(backing);
    }

    private static TMP_FontAsset EnsureStaticFont(string assetFile, string sourceFile, string characters,
        int atlasSize, int samplingSize, int padding, TMP_FontAsset verifiedFallback = null)
    {
        string assetPath = Root + "/Fonts/" + assetFile;
        string fontPath = Root + "/" + sourceFile;
        TMP_FontAsset fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        if (fontAsset == null)
        {
            Font source = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (source == null) throw new InvalidOperationException("Font source is missing or not imported as Font: " + fontPath);
            fontAsset = TMP_FontAsset.CreateFontAsset(source, samplingSize, padding, GlyphRenderMode.SDFAA,
                atlasSize, atlasSize, AtlasPopulationMode.Dynamic, false);
            if (fontAsset == null) throw new InvalidOperationException("Could not create SDF font asset from " + fontPath);
            fontAsset.name = Path.GetFileNameWithoutExtension(assetFile);
            fontAsset.TryAddCharacters(characters, out string missingCharacters);
            ValidateMissingCharacters(missingCharacters, assetFile, verifiedFallback);
            fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            AssetDatabase.CreateAsset(fontAsset, assetPath);
            AddFontSubAssets(fontAsset, assetPath);
        }
        else
        {
            // TMP 3.0.6 can report the whole input as missing when all characters already exist.
            // Only ask its dynamic atlas builder for glyphs absent from this primary face.
            fontAsset.ReadFontAssetDefinition();
            string pending = new string(characters.Where(c => !fontAsset.HasCharacter(c)).ToArray());
            if (pending.Length > 0)
            {
                fontAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                fontAsset.TryAddCharacters(pending, out string missingCharacters);
                ValidateMissingCharacters(missingCharacters, assetFile, verifiedFallback);
            }
            fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;
            AddFontSubAssets(fontAsset, assetPath);
        }
        EditorUtility.SetDirty(fontAsset);
        return fontAsset;
    }

    private static void ValidateMissingCharacters(string missing, string assetFile, TMP_FontAsset fallback)
    {
        if (string.IsNullOrEmpty(missing)) return;
        char[] unresolved = missing.Where(c => fallback == null || !fallback.HasCharacter(c)).ToArray();
        if (unresolved.Length > 0)
            throw new InvalidOperationException("Font has uncovered glyphs: " + assetFile + " " + string.Join(" ", unresolved.Select(c => "U+" + ((int)c).ToString("X4"))));
    }

    private static void AddFontSubAssets(TMP_FontAsset fontAsset, string assetPath)
    {
        if (fontAsset.material != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(fontAsset.material)))
            AssetDatabase.AddObjectToAsset(fontAsset.material, assetPath);
        Texture2D[] textures = fontAsset.atlasTextures;
        if (textures != null)
            for (int i = 0; i < textures.Length; i++)
                if (textures[i] != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(textures[i])))
                    AssetDatabase.AddObjectToAsset(textures[i], assetPath);
    }

    private static void SetFallback(TMP_FontAsset font, params TMP_FontAsset[] fallbacks)
    {
        if (font == null) return;
        font.fallbackFontAssetTable = fallbacks.Where(fallback => fallback != null && fallback != font).Distinct().ToList();
        foreach (TMP_FontAsset fallback in fallbacks)
            if (fallback != null && fallback != font && !font.fallbackFontAssetTable.Contains(fallback))
                throw new InvalidOperationException("Could not register TMP fallback " + fallback.name + " on " + font.name);
        EditorUtility.SetDirty(font);
    }

    private static GlyphSets CollectGlyphs(Scene scene, List<Text> textSources)
    {
        var common = new HashSet<char>();
        var english = new HashSet<char>();
        var chinese = new HashSet<char>();
        var japanese = new HashSet<char>();
        foreach (Text text in textSources) if (text != null) AddCharacters(common, text.text);
        foreach (Phrase phrase in ReadPhrases())
        {
            AddCharacters(common, phrase.english);
            AddCharacters(chinese, phrase.chinese);
            AddCharacters(japanese, phrase.japanese);
        }
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                if (!(component is UdonSharpBehaviour)) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty iterator = serialized.GetIterator();
                bool enterChildren = true;
                while (iterator.Next(enterChildren))
                {
                    enterChildren = true;
                    if (iterator.propertyType != SerializedPropertyType.String) continue;
                    string propertyPath = iterator.propertyPath;
                    if (propertyPath.Contains("Zh")) AddCharacters(chinese, iterator.stringValue);
                    else if (propertyPath.Contains("Ja")) AddCharacters(japanese, iterator.stringValue);
                    else if (propertyPath.Contains("En")) AddCharacters(english, iterator.stringValue);
                    else AddCharacters(common, iterator.stringValue);
                }
            }
        AddManualGlyphs(english, chinese, japanese);
        AddProgramGlyphs(common, english, chinese, japanese);
        string extraPath = Root + "/ExtraGlyphs.txt";
        if (File.Exists(extraPath)) AddCharacters(common, File.ReadAllText(extraPath, Encoding.UTF8));
        var symbolSet = new HashSet<char>((SupplementaryMathGlyphs + SuperscriptGlyphs).ToCharArray());
        common.ExceptWith(symbolSet); english.ExceptWith(symbolSet); chinese.ExceptWith(symbolSet); japanese.ExceptWith(symbolSet);
        foreach (char c in AsciiGlyphs()) common.Add(c);
        foreach (char c in common)
        {
            if (IsCjk(c)) chinese.Add(c);
            else { english.Add(c); chinese.Add(c); japanese.Add(c); }
        }
        return new GlyphSets
        {
            english = SortedCharacters(english),
            chinese = SortedCharacters(chinese),
            japanese = SortedCharacters(japanese),
            symbols = SupplementaryMathGlyphs
        };
    }

    private static bool IsCjk(char c)
    {
        int code = c;
        return code >= 0x2E80 && code <= 0x2FFF || code >= 0x3000 && code <= 0x30FF
            || code >= 0x3100 && code <= 0x31FF || code >= 0x3400 && code <= 0x4DBF
            || code >= 0x4E00 && code <= 0x9FFF || code >= 0xF900 && code <= 0xFAFF;
    }

    private static void AddCharacters(HashSet<char> characters, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsSurrogate(c) || char.IsControl(c)) continue;
            characters.Add(c);
        }
    }

    private static void AddManualGlyphs(HashSet<char> english, HashSet<char> chinese, HashSet<char> japanese)
    {
        TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/isa-manual.json");
        if (asset == null) return;
        JArray pages = JObject.Parse(asset.text)["pages"] as JArray;
        if (pages == null) return;
        foreach (JObject page in pages.Cast<JObject>())
        {
            AddLocalizedArray(english, chinese, japanese, page["titles"]);
            AddLocalizedArray(english, chinese, japanese, page["bodies"]);
        }
    }

    private static void AddProgramGlyphs(HashSet<char> common, HashSet<char> english, HashSet<char> chinese,
        HashSet<char> japanese)
    {
        TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/gpu-netlist.json");
        if (asset == null) return;
        JArray programs = JObject.Parse(asset.text)["programs"] as JArray;
        if (programs == null) return;
        foreach (JObject program in programs.Cast<JObject>())
        {
            AddLocalizedArray(english, chinese, japanese, program["nameTranslations"]);
            AddLocalizedArray(english, chinese, japanese, program["descriptionTranslations"]);
            AddLocalizedArray(english, chinese, japanese, program["expectedTextTranslations"]);
            AddLocalizedArray(english, chinese, japanese, program["sourceCommentTranslations"]);
            AddLegacyText(english, chinese, (string)program["name"]);
            AddLegacyText(english, chinese, (string)program["description"]);
            AddLegacyText(english, chinese, (string)program["expectedText"]);
            AddLegacyText(english, chinese, (string)program["sourceComment"]);
            string source = (string)program["source"];
            if (ContainsNonAscii(source)) AddCharacters(chinese, source);
            else AddCharacters(common, source);
        }
    }

    private static void AddLegacyText(HashSet<char> english, HashSet<char> chinese, string value)
    {
        if (ContainsNonAscii(value)) AddCharacters(chinese, value);
        else AddCharacters(english, value);
    }

    private static bool ContainsNonAscii(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (int i = 0; i < value.Length; i++) if (value[i] > 127) return true;
        return false;
    }

    private static void AddLocalizedArray(HashSet<char> english, HashSet<char> chinese, HashSet<char> japanese, JToken token)
    {
        JArray values = token as JArray;
        if (values == null) return;
        if (values.Count > 0) AddCharacters(english, (string)values[0]);
        if (values.Count > 1) AddCharacters(chinese, (string)values[1]);
        if (values.Count > 2) AddCharacters(japanese, (string)values[2]);
    }

    private static string AsciiGlyphs()
    {
        var chars = new char[95];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)(32 + i);
        return new string(chars);
    }

    private static string SortedCharacters(HashSet<char> characters)
    {
        var ordered = characters.ToList(); ordered.Sort();
        return new string(ordered.ToArray());
    }

    private static List<Phrase> ReadPhrases()
    {
        if (!File.Exists(CatalogPath)) throw new InvalidOperationException("Localization catalog is missing: " + CatalogPath);
        string[] lines = File.ReadAllLines(CatalogPath, Encoding.UTF8);
        var byEnglish = new Dictionary<string, Phrase>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].StartsWith("#")) continue;
            string[] columns = lines[i].Split('\t');
            if (columns.Length < 3 || columns[0].Length == 0) continue;
            byEnglish[columns[0]] = new Phrase { english = columns[0], chinese = columns[1], japanese = columns[2] };
        }
        return byEnglish.Values.OrderByDescending(phrase => phrase.english.Length).ThenBy(phrase => phrase.english, StringComparer.Ordinal).ToList();
    }
}
