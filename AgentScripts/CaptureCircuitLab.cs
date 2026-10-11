using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class CaptureCircuitLab
{
    public static object Screens()
    {
        if(!EditorApplication.isPlaying)throw new InvalidOperationException("Capture in Play Mode.");
        var camera=Object.FindObjectsOfType<Camera>(true).First(c=>c.name=="CircuitLabPreviewCamera");
        Vector3 position=camera.transform.position;Quaternion rotation=camera.transform.rotation;
        float fov=camera.fieldOfView;bool enabled=camera.enabled;var oldTarget=camera.targetTexture;
        var target=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32);target.Create();
        var pixels=new Texture2D(1600,900,TextureFormat.RGB24,false);
        var oldActive=RenderTexture.active;
        Directory.CreateDirectory("Reports/CircuitLab");
        Action<string> capture=name=>
        {
            camera.targetTexture=target;camera.Render();RenderTexture.active=target;
            pixels.ReadPixels(new Rect(0,0,1600,900),0,0);pixels.Apply();
            File.WriteAllBytes("Reports/CircuitLab/"+name+".png",pixels.EncodeToPNG());
        };
        try
        {
            capture("optimized-overview");
            camera.transform.position=new Vector3(0,1.7f,-3.3f);camera.transform.rotation=Quaternion.identity;camera.fieldOfView=90;
            capture("optimized-spawn-view");
            var wave=Object.FindObjectsOfType<Renderer>().First(r=>r.sharedMaterial!=null&&r.sharedMaterial.shader.name=="VrcCpuWorld/CircuitLabWaveform");
            camera.transform.position=wave.bounds.center-wave.transform.forward*3.6f;
            camera.transform.LookAt(wave.bounds.center,wave.transform.up);camera.fieldOfView=58;
            capture("optimized-waveform");
            return new{captured=true,width=1600,height=900,files=new[]{"optimized-overview.png","optimized-spawn-view.png","optimized-waveform.png"}};
        }
        finally
        {
            camera.transform.position=position;camera.transform.rotation=rotation;camera.fieldOfView=fov;camera.enabled=enabled;camera.targetTexture=oldTarget;
            RenderTexture.active=oldActive;target.Release();Object.Destroy(target);Object.Destroy(pixels);
        }
    }
}
