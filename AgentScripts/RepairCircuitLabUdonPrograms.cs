using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// UdonSharp writes compiled Udon programs into Assets/CircuitLab/Programs and
// Assets/SerializedUdonPrograms. When a script is changed while the editor is in
// Play Mode, the running program picks the change up but the on-disk program asset
// can stay stale, which would ship the previous behaviour. These entries find the
// rebuild entry point and force it.
public static class RepairCircuitLabUdonPrograms
{
    private const string ReportDir = "Reports/CircuitLab";
    private static readonly string[] Scripts = { "Assets/CircuitLab/Scripts/CircuitLabNetworkCoordinator.cs" };

    private static object Save(string name, object report)
    {
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, name + ".json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }

    private static IEnumerable<Type> CandidateTypes()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
            })
            .Where(type => type.Name.Contains("UdonSharpProgramAsset"));
    }

    public static object Probe()
    {
        var types = new JArray();
        foreach (Type type in CandidateTypes())
        {
            var methods = new JArray();
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (method.Name.ToLowerInvariant().Contains("compile") || method.Name.ToLowerInvariant().Contains("rebuild"))
                    methods.Add(method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")");
            types.Add(new JObject
            {
                ["type"] = type.FullName,
                ["assembly"] = type.Assembly.GetName().Name,
                ["staticMethods"] = methods
            });
        }
        return Save("udon-probe", new { passed = true, types });
    }

    public static object Rebuild()
    {
        AssetDatabase.SaveAssets();
        var before = new Dictionary<string, string>();
        foreach (string path in AssetDatabase.GetAllAssetPaths().Where(path => path.EndsWith(".asset") && path.Contains("SerializedUdonPrograms")))
            before[path] = AssetDatabase.GetAssetDependencyHash(path).ToString();
        Type programAsset = CandidateTypes().First(type => type.FullName == "UdonSharp.UdonSharpProgramAsset");
        MethodInfo compile = programAsset.GetMethod("CompileAllCsPrograms", BindingFlags.Public | BindingFlags.Static);
        if (compile == null) throw new InvalidOperationException("UdonSharp.UdonSharpProgramAsset.CompileAllCsPrograms was not found.");
        // (forceCompile, compileAllProgramAssets): the first pass rewrites every
        // program so a stale asset cannot survive a source-only change.
        compile.Invoke(null, new object[] { true, true });
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.SaveAssets();
        var after = new Dictionary<string, string>();
        foreach (string path in AssetDatabase.GetAllAssetPaths().Where(path => path.EndsWith(".asset") && path.Contains("SerializedUdonPrograms")))
            after[path] = AssetDatabase.GetAssetDependencyHash(path).ToString();
        var changed = before.Keys.Where(path => after.TryGetValue(path, out string hash) && hash != before[path])
            .Concat(after.Keys.Where(path => !before.ContainsKey(path)))
            .Distinct().ToArray();
        return Save("udon-rebuild", new
        {
            passed = true,
            compiledBy = "UdonSharp.UdonSharpProgramAsset.CompileAllCsPrograms(true, true)",
            programAssetsBefore = before.Count,
            programAssetsAfter = after.Count,
            changedProgramAssets = changed
        });
    }
}
