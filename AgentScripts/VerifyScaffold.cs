using System;
using System.Linq;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class VerifyScaffold
{
    public static object Run()
    {
        var packages = PackageInfo.GetAllRegisteredPackages();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var udonSharp = assemblies.Any(a => a.GetType("UdonSharp.UdonSharpBehaviour", false) != null);
        var readback = assemblies.Any(a => a.GetType("VRC.SDK3.Rendering.VRCAsyncGPUReadback", false) != null);
        var pipeline = packages.SingleOrDefault(p => p.name == "com.unity.pipeline");
        var worlds = packages.SingleOrDefault(p => p.name == "com.vrchat.worlds");
        var scenes = Enumerable.Range(0, SceneManager.sceneCount)
            .Select(i => SceneManager.GetSceneAt(i))
            .Select(scene => new { scene.name, scene.path, scene.isDirty })
            .ToArray();

        if (pipeline == null || worlds == null || !udonSharp || !readback)
            throw new InvalidOperationException("Scaffold dependencies or VRChat GPU readback API are missing.");

        return new
        {
            unityVersion = Application.unityVersion,
            projectPath = System.IO.Directory.GetParent(Application.dataPath).FullName,
            pipelineVersion = pipeline.version,
            pipelineSource = pipeline.source.ToString(),
            vrchatWorldsVersion = worlds.version,
            udonSharpAvailable = udonSharp,
            asyncGpuReadbackAvailable = readback,
            buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
            isCompiling = EditorApplication.isCompiling,
            isPlaying = EditorApplication.isPlaying,
            scenes,
        };
    }
}

