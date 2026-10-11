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
    private static object Capture(string objectName, string fileName, float distance, float yOffset = 0)
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
        try
        {
            camera.transform.position = panel.position - panel.forward * distance + panel.up * yOffset;
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
            RenderTexture.active = previous;
            target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); Object.DestroyImmediate(cameraObject);
        }
    }
    public static object Program() => Capture("ProgramPanel", "ram-program-panel.png", 4.1f);
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
