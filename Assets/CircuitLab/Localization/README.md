# Circuit Lab localization and fonts

The local language selector has four options: AUTO, 简体中文, 日本語, and English. Its override lives only in the local `CircuitLabLocalization` Udon behaviour; it is not synced. AUTO reads `VRCPlayerApi.GetCurrentLanguage()` and follows `OnLanguageChanged(string)` until the player chooses a manual language.

`CircuitLabLocalization.tsv` contains exact labels and reusable phrases. The editor builder creates three child TextMeshPro SDF displays per legacy `UnityEngine.UI.Text`, assigns each font before play, and leaves the existing Text as a hidden data source. The runtime bridge updates the text and color, then activates the selected language's display. It skips translation for program/encoding source, machine words, signal names, terminal output, and the three-language ISA/LED/Life text that their own controllers render. Draft `InputField` content remains uGUI so its caret and editing behaviour stay intact; the code area uses IBM Plex Mono and the placeholder follows the selected UI font.

`BuildCircuitLabLocalization.Apply` generates static TextMeshPro font assets from the strings serialized in the active CircuitLab scene, the locale catalog, and `ExtraGlyphs.txt`. English and Japanese Noto faces use the Simplified Chinese face only as a fallback for missing legacy CJK; characters present in their primary atlases keep their English/Japanese glyph design. Arrows and ceiling/floor operators that are absent from Noto CJK use a small Noto Sans Math fallback; superscript digits use a small IBM Plex Mono subset. The three code assets use IBM Plex Mono for ASCII and a locale-specific Noto fallback for non-ASCII text. The builder uses 2048×2048 SDF atlases for localized UI, 1024×1024 for code, and 512×512 for math symbols and IBM superscript digits. It reports missing glyphs or atlas overflow as an error. Unity creates the font `.asset` and `.meta` files; they are never authored by hand.

Run after the main scene builder in Edit Mode:

```text
unity command run_script --file AgentScripts/BuildCircuitLabLocalization.cs --entry BuildCircuitLabLocalization.Apply
```

## Font sources and licenses

- `FontSources/NotoSansSC-Regular.ttf` is the regular 400-weight instance of Google Fonts' [Noto Sans SC](https://github.com/google/fonts/tree/main/ofl/notosanssc).
- `FontSources/NotoSansJP-Regular.ttf` is the regular 400-weight instance of Google Fonts' [Noto Sans JP](https://github.com/google/fonts/tree/main/ofl/notosansjp).
- `FontSources/NotoSansMath-Regular.ttf` is [Noto Sans Math](https://github.com/google/fonts/tree/main/ofl/notosansmath), distributed under the SIL Open Font License in `FontSources/NotoSansMath-OFL.txt`.
- Both Noto files were instantiated from their variable TTFs at `wght=400` with fontTools `varLib.instancer`; the source's SIL Open Font License is in `FontSources/NotoSans-OFL.txt`.
- `FontSources/IBMPlexMono-Regular.ttf` is [IBM Plex Mono Regular](https://github.com/google/fonts/tree/main/ofl/ibmplexmono), distributed under the SIL Open Font License in `FontSources/IBMPlexMono-OFL.txt`. Its zero is slashed, and its 1, I, and l have distinct shapes.
