using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class CaptureCircuitLabSource
{
    private static object Capture(Transform panel, string name, float distance, float fov)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Use Play Mode.");
        var camera = Object.FindObjectsOfType<Camera>(true).First(c => c.name == "CircuitLabPreviewCamera");
        var panelPosition = panel.position; var panelRotation = panel.rotation; var panelScale = panel.localScale;
        var position = camera.transform.position; var rotation = camera.transform.rotation; float field = camera.fieldOfView;
        var previous = camera.targetTexture; var active = RenderTexture.active;
        var target = new RenderTexture(1600, 1133, 24, RenderTextureFormat.ARGB32); target.Create();
        var pixels = new Texture2D(1600, 1133, TextureFormat.RGB24, false);
        try
        {
            // Keep neighboring panels from occluding this validation view. Only
            // transient Play Mode poses change and they are restored immediately.
            panel.position = new Vector3(0, 30, 0); panel.rotation = Quaternion.identity; panel.localScale = Vector3.one;
            camera.transform.position = panel.position - Vector3.forward * distance; camera.transform.rotation = Quaternion.identity; camera.fieldOfView = fov;
            camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1600, 1133), 0, 0); pixels.Apply();
            Directory.CreateDirectory("Reports/CircuitLab"); File.WriteAllBytes("Reports/CircuitLab/" + name + ".png", pixels.EncodeToPNG());
            return new { captured = true, file = name + ".png" };
        }
        finally
        {
            panel.position = panelPosition; panel.rotation = panelRotation; panel.localScale = panelScale;
            camera.transform.position = position; camera.transform.rotation = rotation; camera.fieldOfView = field; camera.targetTexture = previous;
            RenderTexture.active = active; target.Release(); Object.Destroy(target); Object.Destroy(pixels);
        }
    }
    public static object Verilog() => Capture(Object.FindObjectOfType<CircuitLabSourceViewer>().transform, "verilog-source-viewer", 8.5f, 48f);
    public static object Donut() => Capture(Object.FindObjectOfType<CircuitLabTerminal>().outputText.GetComponentInParent<CircuitLabMovableTool>().transform, "donut-terminal", 5f, 48f);
}
