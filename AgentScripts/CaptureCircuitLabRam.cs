using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UdonSharpEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Temporary preview renders; no camera or scene changes are persisted.
public static class CaptureCircuitLabRam
{
    private static object Capture(string objectName, string fileName, float distance, float yOffset = 0, float xOffset = 0)
    {
        var panel = Object.FindObjectsOfType<Transform>(true).Single(t => t.name == objectName
            && t.gameObject.scene.path == "Assets/CircuitLab/Scenes/CircuitLab.unity");
        var cameraObject = new GameObject("CircuitLabRamCaptureCamera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false; camera.fieldOfView = distance <= 4.1f ? 90 : 45;
        // Clip foreground tools for a readable close-up without moving them.
        camera.nearClipPlane = Mathf.Max(.05f, distance - .3f);
        camera.clearFlags = CameraClearFlags.Skybox;
        var target = new RenderTexture(2000, 1500, 24, RenderTextureFormat.ARGB32); target.Create();
        var pixels = new Texture2D(2000, 1500, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        var hiddenRenderers=Object.FindObjectsOfType<Renderer>(true).Where(r=>!r.transform.IsChildOf(panel)).ToArray();
        var rendererFlags=hiddenRenderers.Select(r=>r.forceRenderingOff).ToArray();
        var hiddenCanvases=Object.FindObjectsOfType<Canvas>(true).Where(c=>!c.transform.IsChildOf(panel)).ToArray();
        var canvasFlags=hiddenCanvases.Select(c=>c.enabled).ToArray();
        foreach(var r in hiddenRenderers) r.forceRenderingOff=true;
        foreach(var c in hiddenCanvases) c.enabled=false;
        try
        {
            camera.transform.position = panel.position - panel.forward * distance + panel.up * yOffset + panel.right * xOffset;
            camera.transform.rotation = panel.rotation;
            camera.targetTexture = target;
            Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 2000, 1500), 0, 0); pixels.Apply();
            Directory.CreateDirectory("Reports/CircuitLab");
            File.WriteAllBytes("Reports/CircuitLab/" + fileName, pixels.EncodeToPNG());
            return new { captured = true, file = fileName, width = 2000, height = 1500 };
        }
        finally
        {
            for(int i=0;i<hiddenRenderers.Length;i++) if(hiddenRenderers[i]!=null) hiddenRenderers[i].forceRenderingOff=rendererFlags[i];
            for(int i=0;i<hiddenCanvases.Length;i++) if(hiddenCanvases[i]!=null) hiddenCanvases[i].enabled=canvasFlags[i];
            RenderTexture.active = previous;
            target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); Object.DestroyImmediate(cameraObject);
        }
    }
    public static object Program() => Capture("ProgramPanel", "ram-program-panel.png", 4.1f);
    public static async Task<object> Browser()
    {
        var browser = Object.FindObjectOfType<CircuitLabProgramBrowser>();
        UdonSharpEditorUtility.GetBackingUdonBehaviour(browser).SendCustomEvent("ShowSource");
        await Task.Delay(150);
        return Capture("ProgramBrowserPanel", "program-browser-panel.png", 4.1f);
    }
    public static object About() => Capture("AboutPanel", "about-panel.png", 4.1f, 0, 1.5f);
    public static object Isa() => Capture("IsaManualPanel", "isa-manual.png", 4.1f);
    private static async Task<object> LocalizedIsa(string languageEvent, string fileName)
    {
        var locale = Object.FindObjectOfType<CircuitLabLocalization>();
        var vm = UdonSharpEditorUtility.GetBackingUdonBehaviour(locale);
        try
        {
            vm.SendCustomEvent(languageEvent);
            await Task.Delay(500);
            return Capture("IsaManualPanel", fileName, 4.1f);
        }
        finally { vm.SendCustomEvent("SetAuto"); }
    }
    public static Task<object> IsaChinese() => LocalizedIsa("SetChinese", "isa-manual-zh.png");
    public static Task<object> IsaJapanese() => LocalizedIsa("SetJapanese", "isa-manual-ja.png");
    public static object Led() => Capture("LedOutputPanel", "led-output.png", 4.1f);
    public static object Life() => Capture("LifeOutputPanel", "life-output.png", 4.1f);
    public static object Terminal() => Capture("TerminalPanel", "ram-pi-terminal.png", 4.8f);
    public static object Analyzer() => Capture("MovableWaveformPanel", "ram-analyzer-control-top.png", 7.8f);
    public static async Task<object> Ram()
    {
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        UdonSharpEditorUtility.GetBackingUdonBehaviour(tablet).SendCustomEvent("ShowRam");
        await Task.Delay(250);
        return Capture("StateTablet", "ram-state-tablet.png", 3.5f);
    }
    public static async Task<object> RawRam()
    {
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        var vm = UdonSharpEditorUtility.GetBackingUdonBehaviour(tablet);
        vm.SendCustomEvent("ShowRam"); vm.SetProgramVariable("rawRam", true);
        await Task.Delay(250);
        return Capture("StateTablet", "ram-raw-texture.png", 3.5f);
    }
}
