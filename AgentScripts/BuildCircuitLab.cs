using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;
using Object = UnityEngine.Object;

// Run with Pipeline: run_script --file AgentScripts/BuildCircuitLab.cs --entry BuildCircuitLab.BuildScene
// All meshes, materials, render textures and Udon programs are authored and saved in Edit Mode.
public static class BuildCircuitLab
{
    private const string Root = "Assets/CircuitLab";
    private const string ScenePath = Root + "/Scenes/CircuitLab.unity";
    private static readonly string[] ProgramNames = { "CircuitLabSimulator", "CircuitLabModule", "CircuitLabProbe", "CircuitLabSignalTarget", "CircuitLabButton", "CircuitLabWire", "CircuitLabLayout", "CircuitLabMovableTool", "CircuitLabPlayerSetup", "CircuitLabHierarchyTree", "CircuitLabHierarchyRow", "CircuitLabNetworkCoordinator", "CircuitLabNetworkClock", "CircuitLabControlRequest", "CircuitLabTerminal", "CircuitLabObjectState" };
    private static Font _font;
    private static Material _board, _edge, _textDark, _highlight, _handle;
    private static Material _display;
    private static Transform _workspace;
    private static CircuitLabNetworkCoordinator _coordinator;
    private static CircuitLabLayout _layout;
    private const int InteractionLayer = 17;
    private const float PickupDistance = 10f;
    private sealed class Pose { public Vector3 position, scale; public Quaternion rotation; public bool detached; }
    private static readonly List<CircuitLabSignalTarget> Targets = new List<CircuitLabSignalTarget>();
    private static readonly List<UdonSharpBehaviour> Behaviours = new List<UdonSharpBehaviour>();

    private sealed class Geometry
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<Color> colors = new List<Color>();
        public readonly List<int> triangles = new List<int>();
        public void Segment(Vector3 a, Vector3 b, int signal, float thickness, float brightness = 1f)
        {
            Vector3 direction = (b - a).normalized;
            if ((b - a).sqrMagnitude < .00000001f) return;
            Vector3 normal = Vector3.Cross(direction, Vector3.forward).normalized;
            if (normal.sqrMagnitude < .01f) normal = Vector3.right;
            Vector3 binormal = Vector3.Cross(direction, normal).normalized * thickness * .5f;
            normal *= thickness * .5f;
            int start = vertices.Count;
            foreach (var point in new[] { a - normal - binormal, a + normal - binormal, a + normal + binormal, a - normal + binormal, b - normal - binormal, b + normal - binormal, b + normal + binormal, b - normal + binormal })
            { vertices.Add(point); uv.Add(new Vector2(signal, 0)); colors.Add(new Color(brightness, brightness, brightness)); }
            foreach (int index in new[] { 0,1,2,0,2,3, 4,6,5,4,7,6, 0,4,5,0,5,1, 1,5,6,1,6,2, 2,6,7,2,7,3, 3,7,4,3,4,0 }) triangles.Add(start + index);
        }
        public void Path(IEnumerable<Vector3> path, int signal, float thickness, float brightness = 1f)
        {
            Vector3? last = null;
            foreach (var point in path) { if (last.HasValue) Segment(last.Value, point, signal, thickness, brightness); last = point; }
        }
        public void Circle(Vector3 point, float radius, int signal, bool fill = false, float brightness = 1f)
        {
            if (!fill)
            {
                var path = Enumerable.Range(0, 25).Select(i => point + new Vector3(Mathf.Cos(i * Mathf.PI / 12), Mathf.Sin(i * Mathf.PI / 12)) * radius);
                Path(path, signal, radius * .25f, brightness); return;
            }
            int start = vertices.Count;
            vertices.Add(point); uv.Add(new Vector2(signal, 0)); colors.Add(new Color(brightness, brightness, brightness));
            for (int i = 0; i <= 16; i++)
            {
                vertices.Add(point + new Vector3(Mathf.Cos(i * Mathf.PI / 8), Mathf.Sin(i * Mathf.PI / 8)) * radius);
                uv.Add(new Vector2(signal, 0)); colors.Add(new Color(brightness, brightness, brightness));
                if (i > 0) triangles.AddRange(new[] { start, start + i, start + i + 1 });
            }
        }
    }

    private sealed class ModuleView
    {
        public JObject data;
        public CircuitLabModule behaviour;
        public readonly Dictionary<int, Vector3> gatePositions = new Dictionary<int, Vector3>();
        public readonly Dictionary<int, CircuitLabSignalTarget> outputs = new Dictionary<int, CircuitLabSignalTarget>();
        public readonly List<CircuitLabSignalTarget> ports = new List<CircuitLabSignalTarget>();
        public readonly List<string> portDirections = new List<string>();
        public float width, height, gateScale, dockArea;
    }

    private static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        if (slash < 0) throw new InvalidOperationException("Missing asset root " + path);
        Folder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }

    private static T Asset<T>(string path, Func<T> create) where T : Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null) return asset;
        if (AssetDatabase.LoadMainAssetAtPath(path) != null) throw new InvalidOperationException("Asset type conflict at " + path);
        asset = create(); AssetDatabase.CreateAsset(asset, path); return asset;
    }

    public static object CompileUdon()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Compile in Edit Mode.");
        Folder(Root + "/Programs");
        foreach (string name in ProgramNames)
        {
            var program = Asset(Root + "/Programs/" + name + ".asset", () => ScriptableObject.CreateInstance<UdonSharpProgramAsset>());
            program.sourceCsScript = AssetDatabase.LoadAssetAtPath<MonoScript>(Root + "/Scripts/" + name + ".cs");
            if (program.sourceCsScript == null) throw new InvalidOperationException("Missing " + name + ".cs");
            EditorUtility.SetDirty(program);
        }
        AssetDatabase.SaveAssets();
        UdonSharpProgramAsset.CompileAllCsPrograms(true, true);
        if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError()) throw new InvalidOperationException("UdonSharp compilation failed; inspect Console.");
        return new { compiled = true, programs = ProgramNames.Length };
    }

    public static object BuildScene() { return BuildSceneInternal(false); }
    public static object UpgradeScene() { return BuildSceneInternal(true); }

    private static object BuildSceneInternal(bool upgrade)
    {
        var scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || scene.path != ScenePath)
            throw new InvalidOperationException("Activate the additive CircuitLab scene in Edit Mode before building; other scenes are preserved.");
        foreach (string folder in new[] { "/Materials", "/Meshes", "/Textures", "/Programs" }) Folder(Root + folder);
        var source = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Data/gpu-netlist.json");
        if (source == null) throw new InvalidOperationException("Generate schema2 gpu-netlist.json first.");
        var data = JObject.Parse(source.text);
        if ((int?)data["schemaVersion"] != 2 && (int?)data["schema"] != 2) throw new InvalidOperationException("Expected schema2 teaching CPU data.");
        int width = (int)data["textureWidth"], count = (int)data["nodeCount"];
        var nodes = ((JArray)data["nodes"]).Cast<JObject>().ToDictionary(node => (int)node["id"]);
        var previous = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "CircuitLabWorld");
        if (upgrade && previous == null) throw new InvalidOperationException("Upgrade requires an existing CircuitLabWorld; environment is never recreated.");
        if (!upgrade && previous != null) throw new InvalidOperationException("Existing worlds must use UpgradeScene to preserve the environment.");
        var previousPoses = previous == null ? new Dictionary<string, Pose>() : previous.GetComponentsInChildren<CircuitLabModule>(true).ToDictionary(module => module.modulePath, module => new Pose { position = module.transform.position, rotation = module.transform.rotation, scale = module.transform.localScale, detached = module.isDetached });
        var previousTools = new Dictionary<string, Pose>();
        var preservedWorkspaceChildren = new List<Transform>();
        if (upgrade)
        {
            foreach (string name in new[] { "MovableWaveformPanel", "ProbeAndHierarchyRack", "HierarchyPanel", "ANSISymbolGuide", "TerminalPanel" })
            {
                var owned = previous.transform.Find(name);
                if (owned == null) continue;
                previousTools[name] = new Pose { position = owned.position, rotation = owned.rotation, scale = owned.localScale };
                Undo.DestroyObjectImmediate(owned.gameObject);
            }
            var oldWorkspace = previous.transform.Find("Workspace");
            if (oldWorkspace != null)
            {
                for (int i = oldWorkspace.childCount - 1; i >= 0; i--)
                {
                    var child = oldWorkspace.GetChild(i);
                    if (child.name.StartsWith("Module_") || child.name.StartsWith("Probe_") || child.name.StartsWith("BoundaryWire_")) continue;
                    child.SetParent(previous.transform, true); preservedWorkspaceChildren.Add(child);
                }
                Undo.DestroyObjectImmediate(oldWorkspace.gameObject);
            }
        }
        else if (previous != null) Undo.DestroyObjectImmediate(previous);
        Targets.Clear(); Behaviours.Clear();
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _board = Material("ModuleBoard", "Unlit/Color", new Color(.025f, .055f, .085f));
        _edge = Material("ModuleRim", "Unlit/Color", new Color(.08f, .19f, .25f));
        _textDark = Material("Panel", "Unlit/Color", new Color(.025f, .045f, .065f));
        _highlight = Material("ProbeHighlight", "Unlit/Color", new Color(.2f, 1f, .88f));
        _handle = Material("PickupHandles", "Unlit/Color", new Color(.20f, .52f, .62f));
        _display = Material("Signals", "VrcCpuWorld/CircuitLabDisplay", Color.white);
        _display.SetFloat("_TextureWidth", width); _display.SetFloat("_SignalOverride", -1);
        var simulation = Material("Simulation", "VrcCpuWorld/CircuitLabSim", Color.white);
        var history = Material("History", "VrcCpuWorld/CircuitLabHistory", Color.white);
        var wave = Material("Waveform", "VrcCpuWorld/CircuitLabWaveform", Color.white);
        var gates = Asset(Root + "/Textures/GateInstructions.asset", () => new Texture2D(width, 1, TextureFormat.RGBAFloat, false, true));
        if (gates.width != width) gates.Reinitialize(width, 1, TextureFormat.RGBAFloat, false);
        gates.filterMode = FilterMode.Point; gates.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[width];
        foreach (var node in nodes.Values) pixels[(int)node["id"]] = new Color((int)node["op"], (int)node["a"], (int)node["b"], (int)node["c"]);
        gates.SetPixels(pixels); gates.Apply(false, false); EditorUtility.SetDirty(gates);
        var stateA = RenderTexture("StateA", width, 1, RenderTextureFormat.ARGB32);
        var stateB = RenderTexture("StateB", width, 1, RenderTextureFormat.ARGB32);
        var historyA = RenderTexture("HistoryA", 2048, 4, RenderTextureFormat.ARGBFloat);
        var historyB = RenderTexture("HistoryB", 2048, 4, RenderTextureFormat.ARGBFloat);
        var frozen = RenderTexture("FrozenHistory", 2048, 4, RenderTextureFormat.ARGBFloat);
        _display.SetTexture("_StateTex", stateA);
        simulation.SetTexture("_Gates", gates);

        var world = upgrade ? previous : new GameObject("CircuitLabWorld");
        if (!upgrade) { SceneManager.MoveGameObjectToScene(world, scene); Undo.RegisterCreatedObjectUndo(world, "Build circuit laboratory"); Environment(world.transform); }
        _workspace = Child(world.transform, "Workspace").transform;
        foreach (var child in preservedWorkspaceChildren) child.SetParent(_workspace, true);
        var simulator = Behaviour<CircuitLabSimulator>(OwnedChild(world.transform, "GpuSimulator"));
        _coordinator = Behaviour<CircuitLabNetworkCoordinator>(OwnedChild(world.transform, "NetworkCoordinator"));
        var clock = Behaviour<CircuitLabNetworkClock>(OwnedChild(world.transform, "NetworkClock"));
        clock.coordinator = _coordinator; _coordinator.clock = clock; _coordinator.simulator = simulator; simulator.coordinator = _coordinator;
        var requests = new CircuitLabControlRequest[16];
        for (int i = 0; i < requests.Length; i++) { requests[i] = Behaviour<CircuitLabControlRequest>(OwnedChild(world.transform, "ControlRequest_" + i)); requests[i].coordinator = _coordinator; requests[i].slotId = i; }
        _coordinator.requestSlots = requests;
        _layout = Behaviour<CircuitLabLayout>(OwnedChild(world.transform, "LayoutController")); _layout.coordinator = _coordinator;
        Behaviour<CircuitLabPlayerSetup>(OwnedChild(world.transform, "LocalPlayerSetup"));
        simulator.simulationMaterial = simulation; simulator.displayMaterial = _display;
        simulator.historyMaterial = history; simulator.waveformMaterial = wave;
        simulator.gateTexture = gates; simulator.stateA = stateA; simulator.stateB = stateB;
        simulator.historyA = historyA; simulator.historyB = historyB; simulator.frozenHistory = frozen;
        simulator.nodeCount = count; simulator.textureWidth = width; simulator.settlePasses = (int)data["settlePasses"];
        simulator.inputMasks = data["inputMasks"].ToObject<int[]>();
        simulator.expectedStates = data["expectedStates"].ToObject<byte[]>();
        simulator.vectorCount = (int)data["vectorCount"];
        simulator.accSignals = data["ports"]["acc"].ToObject<int[]>();
        simulator.pcSignals = data["ports"]["pc"].ToObject<int[]>();
        simulator.outSignals = data["ports"]["out"].ToObject<int[]>();
        simulator.instructionSignals = data["ports"]["instruction"].ToObject<int[]>();
        simulator.phaseSignal = (int)data["ports"]["phase"][0];
        simulator.zeroSignal = (int)data["ports"]["zero"][0];
        simulator.carrySignal = (int)data["ports"]["carry"][0];
        simulator.haltSignal = (int)data["ports"]["halt"][0];
        simulator.dffSignals = nodes.Values.Where(node => (int)node["op"] == 3).Select(node => (int)node["id"]).OrderBy(id => id).ToArray();
        if (simulator.dffSignals.Length > 64) throw new InvalidOperationException("Network snapshot supports up to 64 DFFs.");
        var restoreIndex = Asset(Root + "/Textures/DffIndexTexture.asset", () => new Texture2D(width, 1, TextureFormat.RGBAFloat, false, true));
        if (restoreIndex.width != width) restoreIndex.Reinitialize(width, 1, TextureFormat.RGBAFloat, false);
        var restorePixels = Enumerable.Repeat(new Color(-1, 0, 0, 0), width).ToArray();
        for (int i = 0; i < simulator.dffSignals.Length; i++) restorePixels[simulator.dffSignals[i]] = new Color(i, 0, 0, 0);
        restoreIndex.SetPixels(restorePixels); restoreIndex.Apply(false, false); restoreIndex.filterMode = FilterMode.Point; restoreIndex.wrapMode = TextureWrapMode.Clamp;
        simulator.restoreIndexTexture = restoreIndex; EditorUtility.SetDirty(restoreIndex);
        simulator.runOnStart = false;
        simulator.historyFrozen = false; simulator.followLatest = true; simulator.waveOffset = 0;
        simulator.historyCapacity = 2048;
        simulator.waveWindowSamples = 128;
        simulator.waveAspect = 5.05f / 1.85f; simulator.analyticAA = true; simulator.waveEdgeWidth = 1f;
        var views = ((JArray)data["modules"]).Cast<JObject>().Select(module => BuildModule(module, nodes)).ToDictionary(view => view.behaviour.moduleId);
        var ordered = HierarchyOrder(views);
        var allModules = ordered.Select(view => view.behaviour).ToArray();
        foreach (var view in ordered)
        {
            var module = view.behaviour;
            int parentId = (int)view.data["parentId"];
            module.logicalParent = parentId < 0 ? null : views[parentId].behaviour;
            module.logicalDepth = ((string)view.data["path"]).Count(character => character == '/');
            module.allModules = allModules;
            module.childModules = ((JArray)view.data["children"]).Select(child => views[(int)child].behaviour).ToArray();
        }
        PlaceModules(ordered, views);
        if (upgrade)
            foreach (var module in allModules)
                if (previousPoses.TryGetValue(module.modulePath, out var pose))
                {
                    module.isDetached = pose.detached;
                    module.transform.SetParent(module.isDetached || module.logicalParent == null ? _workspace : module.logicalParent.childrenDock, true);
                    module.transform.position = pose.position; module.transform.rotation = pose.rotation; module.transform.localScale = pose.scale;
                }
        foreach (var module in allModules) { module.expanded = module.childModules.Length > 0; module.RefreshVisibility(); module.EnsureAboveFloor(); }
        var rootModule = allModules.First(module => module.logicalParent == null);
        rootModule.transform.position = new Vector3(-3.85f, 1.8f, .5f); rootModule.transform.rotation = Quaternion.identity; rootModule.transform.localScale = Vector3.one * .65f; rootModule.EnsureAboveFloor();
        foreach (var module in allModules) { module.CaptureInitialPose(); module.sharedState.expanded = module.expanded; module.sharedState.detached = module.isDetached; }
        GateReference(world.transform, nodes);
        var wireMaterials = BuildExternalWires(ordered, nodes, width);
        simulator.displayMaterials = (new[] { _display }).Concat(wireMaterials).Distinct().ToArray();
        var probes = BuildPanel(world.transform, simulator, wave, allModules);
        for (int i = 0; i < Targets.Count; i++) { Targets[i].targetIndex = i; Targets[i].probes = probes; }
        foreach (var probe in probes) { probe.probes = probes; probe.signalTargets = Targets.ToArray(); probe.coordinator = _coordinator; }
        var rootView = ordered.First(view => view.behaviour.logicalParent == null);
        rootView.behaviour.header.Find("ModuleName").GetComponent<Text>().text = "TEACHING CPU  /  " + views.Count + " MODULES";
        var initialNames = new[] { "pc", "instruction", "acc", "out" };
        for (int channel = 0; channel < 4; channel++)
        {
            var target = rootView.ports.First(port => port.name == "Port_" + initialNames[channel]);
            probes[channel].initialTarget = target; probes[channel].boundTargetIndex = target.targetIndex;
            probes[channel].sharedState.boundTargetIndex = target.targetIndex; probes[channel].sharedState.selected = probes[channel].isSelected;
        }
        simulator.probe0Signals = simulator.pcSignals; simulator.probe0Label = "teaching_cpu/pc";
        simulator.probe1Signals = simulator.instructionSignals; simulator.probe1Label = "teaching_cpu/instruction";
        simulator.probe2Signals = simulator.accSignals; simulator.probe2Label = "teaching_cpu/acc";
        simulator.probe3Signals = simulator.outSignals; simulator.probe3Label = "teaching_cpu/out";
        _layout.modules = allModules; _layout.probes = probes;
        BuildTerminal(world.transform, simulator, data);
        _layout.tools = world.GetComponentsInChildren<CircuitLabMovableTool>(true);
        foreach (var tool in _layout.tools)
        {
            if (upgrade && previousTools.TryGetValue(tool.name, out var pose)) { tool.transform.position = pose.position; tool.transform.rotation = pose.rotation; }
            tool.initialPosition = tool.transform.position; tool.initialRotation = tool.transform.rotation; tool.initialScale = tool.transform.localScale;
        }
        foreach (var module in allModules) module.RefreshVisibility();
        foreach (var behaviour in Behaviours)
        {
            UdonSharpEditorUtility.CopyProxyToUdon(behaviour);
            EditorUtility.SetDirty(behaviour);
            EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(behaviour));
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        if (!upgrade) EditorSceneManager.SaveScene(scene);
        return new { upgraded = upgrade, saved = !upgrade, preservedEnvironment = upgrade, scene = scene.path, modules = views.Count, nodes = count, signalTargets = Targets.Count, udonBehaviours = Behaviours.Count, externalWires = Object.FindObjectsOfType<CircuitLabWire>(true).Length, shadersSupported = new[] { simulation, history, wave, _display }.All(material => material.shader.isSupported), previewCameraEnabled = false };
    }

    private static RenderTexture RenderTexture(string name, int width, int height, RenderTextureFormat format)
    {
        var texture = Asset(Root + "/Textures/" + name + ".renderTexture", () => new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear));
        if (texture.width != width || texture.height != height || texture.format != format)
        { texture.Release(); texture.width = width; texture.height = height; texture.format = format; }
        texture.name = name; texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
        texture.useMipMap = false; texture.autoGenerateMips = false; texture.antiAliasing = 1;
        EditorUtility.SetDirty(texture); return texture;
    }

    private static Material Material(string name, string shaderName, Color color)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null || !shader.isSupported) throw new InvalidOperationException("Unsupported shader " + shaderName);
        var material = Asset(Root + "/Materials/" + name + ".mat", () => new Material(shader));
        material.shader = shader;
        if (material.HasProperty("_Color")) material.color = color;
        EditorUtility.SetDirty(material); return material;
    }

    private static void Environment(Transform world)
    {
        var floor = Material("Floor", "Unlit/Color", new Color(.055f, .075f, .10f));
        var wall = Material("Wall", "Unlit/Color", new Color(.025f, .04f, .06f));
        Primitive(world, "Floor", PrimitiveType.Cube, new Vector3(0, -.12f, 2), new Vector3(24, .2f, 19), floor);
        Primitive(world, "Backdrop", PrimitiveType.Cube, new Vector3(0, 4.2f, 6), new Vector3(24, 8.4f, .15f), wall);
        Primitive(world, "MainWorkbench", PrimitiveType.Cube, new Vector3(0, .70f, .7f), new Vector3(6.4f, .18f, 3.7f), _edge);
        foreach (float x in new[] { -2.7f, 2.7f })
            Primitive(world, "BenchLeg", PrimitiveType.Cube, new Vector3(x, .3f, .7f), new Vector3(.15f, .65f, 3.2f), _board);
        for (int i = -4; i <= 4; i++)
            Primitive(world, "FloorGrid", PrimitiveType.Cube, new Vector3(i * 2, -.009f, 2), new Vector3(.015f, .008f, 18), _edge);
        var title = WorldText(world, "LabTitle", "CIRCUIT LAB  /  TEACHING CPU", new Vector3(0, 8.0f, 5.84f), new Vector2(12, .6f), 44, Color.white);
        WorldText(world, "LabSubtitle", "MOVE MODULES  /  EXPAND THE HIERARCHY  /  PROBE ANY PIN", new Vector3(0, 7.5f, 5.82f), new Vector2(12, .35f), 25, new Color(.55f, .80f, .88f));
        WorldText(world, "Legend", "BLUE 0    GOLD 1    DOT = REAL BRANCH    CROSSING = NO CONNECTION", new Vector3(0, .13f, -1.22f), new Vector2(6, .26f), 18, new Color(.55f, .75f, .86f));
        var descriptorObject = Child(world, "VRCWorld");
        var spawn = Child(world, "Spawn"); spawn.transform.position = new Vector3(0, .04f, -3.3f);
        descriptorObject.AddComponent<VRCSceneDescriptor>().spawns = new[] { spawn.transform };
        descriptorObject.AddComponent<VRC.Core.PipelineManager>();
        var cameraObject = Child(world, "CircuitLabPreviewCamera");
        cameraObject.transform.position = new Vector3(0, 4.9f, -11.0f);
        cameraObject.transform.LookAt(new Vector3(0, 3.0f, 1.8f));
        var camera = cameraObject.AddComponent<Camera>(); camera.fieldOfView = 56;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.025f, .04f, .06f);
        camera.enabled = false; cameraObject.tag = "Untagged";
        RenderSettings.ambientLight = new Color(.4f, .45f, .5f);
    }

    private static ModuleView BuildModule(JObject data, Dictionary<int, JObject> nodes)
    {
        int id = (int)data["id"];
        var go = Child(_workspace, "Module_" + id + "_" + (string)data["name"]); go.layer = InteractionLayer;
        var module = Behaviour<CircuitLabModule>(go);
        module.moduleId = id; module.modulePath = (string)data["path"]; module.workspaceRoot = _workspace;
        module.childrenDock = Child(go.transform, "ChildrenDock").transform;
        var view = new ModuleView { data = data, behaviour = module };
        int[] driven = data["nodes"].ToObject<int[]>();
        var levels = driven.Select(signal => (int)nodes[signal]["level"]).Distinct().OrderBy(level => level).ToArray();
        var columns = driven.GroupBy(signal => (int)nodes[signal]["level"]).OrderBy(group => group.Key).Select(group => group.OrderBy(signal => signal).ToArray()).ToArray();
        if (driven.Length > 0 && driven.All(signal => (int)nodes[signal]["op"] <= 2))
            columns = driven.Select((signal, index) => new { signal, column = index / 2 }).GroupBy(item => item.column).Select(group => group.Select(item => item.signal).ToArray()).ToArray();
        int rowCount = columns.Length == 0 ? 1 : columns.Max(column => column.Length);
        float rawWidth = Mathf.Max(1.6f, columns.Length * .88f + .75f);
        float rawHeight = Mathf.Max(1.1f, rowCount * .64f + .3f);
        view.gateScale = Mathf.Min(1f, 5.8f / rawWidth, 3.3f / rawHeight);
        view.width = Mathf.Max(3.3f, rawWidth * view.gateScale + .65f);
        view.height = Mathf.Max(2.05f, rawHeight * view.gateScale + 1.25f);
        int childCount = ((JArray)data["children"]).Count;
        if (childCount > 0 && (int)data["parentId"] >= 0)
        {
            view.dockArea = Mathf.Ceil(childCount / 3f) * .84f + .30f;
            view.height += view.dockArea; view.width = Mathf.Max(view.width, 4.8f);
        }
        int maxPortCount = ((JArray)data["ports"]).Cast<JObject>().GroupBy(port => (string)port["direction"]).Max(group => group.Count());
        module.expandedHeight = view.height; module.expandedWidth = view.width; module.foldedHeight = Mathf.Max(1.45f, maxPortCount * .115f + .86f);
        module.expandedShell = Child(go.transform, "ExpandedShell");
        Backing(module.expandedShell.transform, view.width, view.height);
        module.foldedShell = Child(go.transform, "FoldedShell");
        Backing(module.foldedShell.transform, 2.8f, module.foldedHeight);
        module.interior = Child(go.transform, "Interior");
        module.header = Child(go.transform, "Header").transform;
        string heading = (int)data["parentId"] < 0 ? "TEACHING CPU / TOP SOURCES" : ((string)data["name"]).ToUpperInvariant() + "  /  " + driven.Length + " GATES";
        WorldText(module.header, "ModuleName", heading, new Vector3(0, 0, 0), new Vector2(2.65f, .24f), 20, Color.white);
        WorldText(module.header, "ModulePath", (string)data["path"], new Vector3(0, -.23f, 0), new Vector2(2.65f, .19f), 12, new Color(.58f, .75f, .84f));
        module.stateLabel = WorldText(module.header, "ModuleState", "", new Vector3(0, -.39f, 0), new Vector2(2.5f, .16f), 11, new Color(.43f, .8f, .75f));
        module.handle = Child(go.transform, "PickupHandle").transform;
        module.grabVisual = Primitive(module.handle, "GrabBar", PrimitiveType.Cube, Vector3.zero, new Vector3(.85f, .15f, .18f), _handle, false).transform;
        module.pickupCollider = go.AddComponent<BoxCollider>(); module.pickupCollider.size = new Vector3(.85f, .15f, .18f);
        module.grabText = WorldText(module.handle, "GrabHint", "GRAB", new Vector3(0, .01f, -.102f), new Vector2(.30f, .11f), 9, Color.white);
        var body = go.AddComponent<Rigidbody>(); body.useGravity = false; body.isKinematic = true;
        module.objectSync = go.AddComponent<VRCObjectSync>();
        module.sharedState = Behaviour<CircuitLabObjectState>(Child(go.transform, "SharedObjectState")); module.sharedState.module = module;
        module.pickup = ConfigurePickup(go, module.handle);
        module.probeAnchor = Child(go.transform, "ProbeAnchor").transform;
        module.channelBadge = WorldText(go.transform, "ProbeChannelBadge", "PROBE: --", new Vector3(0, -.45f, -.13f), new Vector2(1.1f, .12f), 10, Color.white);
        module.footer = Child(go.transform, "Footer").transform;
        Button(module.footer, "EXPAND_FOLD", "EXPAND / FOLD", module, "ToggleExpanded", new Vector3(-.87f, 0f, 0f), new Vector2(.79f, .22f), 11);
        Button(module.footer, "RETRIEVE", "RETRIEVE", module, "RetrieveChildren", new Vector3(.84f, 0f, 0f), new Vector2(.67f, .22f), 11);
        if ((int)data["parentId"] >= 0)
            Button(module.footer, "EXTRACT", "EXTRACT", module, "DetachFromParent", new Vector3(.84f, .23f, 0f), new Vector2(.67f, .18f), 9);
        var portTransforms = new List<Transform>(); var unfolded = new List<Vector3>(); var folded = new List<Vector3>();
        var ports = ((JArray)data["ports"]).Cast<JObject>().ToArray();
        foreach (string direction in new[] { "input", "output", "inout" })
        {
            var sameSide = ports.Where(port => ((string)port["direction"]).ToLowerInvariant() == direction).ToArray();
            for (int index = 0; index < sameSide.Length; index++)
            {
                var port = sameSide[index]; int[] ids = port["signals"].ToObject<int[]>();
                float side = direction == "input" ? -1 : 1;
                float t = (index + .5f) / sameSide.Length;
                Vector3 expanded = new Vector3(side * (view.width * .5f + .015f), (1f - t) * (view.height - 1.25f) - (view.height - 1.25f) * .5f - .12f, -.12f);
                float portSpan = module.foldedHeight - .86f;
                Vector3 compact = new Vector3(side * 1.415f, (.5f - t) * portSpan + .02f, -.12f);
                string name = (string)port["name"];
                var target = SignalTarget(go.transform, "Port_" + name, ids, module.modulePath + "/" + name, module, true, expanded, .065f);
                string bus = ids.Length > 1 ? "[" + (ids.Length - 1) + ":0]" : "";
                string arrow = direction == "input" ? "> " : " >";
                WorldText(target.transform, "PortLabel", direction == "input" ? arrow + name + bus : name + bus + arrow, new Vector3(side * -.33f, .08f, -.025f), new Vector2(.64f, .13f), 9, new Color(.69f, .83f, .9f));
                Primitive(target.transform, "Socket", PrimitiveType.Sphere, Vector3.zero, new Vector3(.072f, .072f, .05f), _handle, false);
                portTransforms.Add(target.transform); unfolded.Add(expanded); folded.Add(compact);
                view.ports.Add(target); view.portDirections.Add(direction);
            }
        }
        module.portTransforms = portTransforms.ToArray(); module.expandedPortPositions = unfolded.ToArray(); module.foldedPortPositions = folded.ToArray();
        for (int column = 0; column < columns.Length; column++)
        {
            int[] ids = columns[column];
            for (int row = 0; row < ids.Length; row++)
                view.gatePositions[ids[row]] = new Vector3((column - (columns.Length - 1) * .5f) * .88f * view.gateScale, ((ids.Length - 1) * .5f - row) * .64f * view.gateScale - .17f + view.dockArea * .5f, -.12f);
        }
        var symbols = new Geometry(); var traces = new Geometry();
        foreach (int signal in driven)
        {
            var node = nodes[signal]; var position = view.gatePositions[signal]; int op = (int)node["op"];
            Gate(symbols, position, op, signal, view.gateScale);
            Vector3 output = position + new Vector3(.32f * view.gateScale, 0f, -.01f);
            view.outputs[signal] = SignalTarget(module.interior.transform, "Pin_" + signal + "_Q", new[] { signal }, (string)node["name"], module, false, output, Mathf.Max(.021f, .052f * view.gateScale));
            var outputLabel = WorldText(module.interior.transform, "PinLabel_" + signal + "_Q", op == 3 ? "Q" : "Y", output + new Vector3(.018f, .035f, -.01f) * view.gateScale, new Vector2(.12f, .10f), 6, new Color(.72f, .86f, .94f));
            outputLabel.transform.localScale = Vector3.one * .01f * view.gateScale;
            string shortName = (string)node["name"];
            if (shortName.Length > 24) shortName = shortName.Substring(shortName.Length - 24);
            var gateLabel = WorldText(module.interior.transform, "GateLabel_" + signal, GateName(op) + "  #" + signal, position + new Vector3(0, .23f * view.gateScale, -.012f), new Vector2(.7f, .12f), 10, new Color(.67f, .81f, .9f));
            gateLabel.transform.localScale = Vector3.one * .01f * view.gateScale;
            int[] inputs = Inputs(node);
            for (int pin = 0; pin < inputs.Length; pin++)
            {
                Vector3 input = InputPosition(position, op, pin, inputs.Length, view.gateScale);
                string label = (string)nodes[inputs[pin]]["name"] + " -> " + GateName(op) + "#" + signal + "." + PinName(op, pin);
                SignalTarget(module.interior.transform, "Pin_" + signal + "_" + PinName(op, pin), new[] { inputs[pin] }, label, module, false, input, Mathf.Max(.019f, .046f * view.gateScale));
                Vector3 entry = op == 10 && pin == 2 ? position + new Vector3(0, -.16f * view.gateScale, -.01f) : position + new Vector3(-.20f * view.gateScale, input.y - position.y, -.01f);
                symbols.Segment(input, entry, inputs[pin], .015f * view.gateScale);
                var inputLabel = WorldText(module.interior.transform, "PinLabel_" + signal + "_" + PinName(op, pin), PinName(op, pin), input + new Vector3(-.025f, .034f, -.01f) * view.gateScale, new Vector2(.14f, .10f), 6, new Color(.67f, .83f, .92f));
                inputLabel.transform.localScale = Vector3.one * .01f * view.gateScale;
            }
            if (op == 3)
            {
                var clockPort = view.ports.FirstOrDefault(port => port.name == "Port_clk");
                if (clockPort != null && clockPort.signalIds.Length > 0)
                {
                    int clockSignal = clockPort.signalIds[0];
                    Vector3 clockPin = position + new Vector3(-.32f, -.13f, -.01f) * view.gateScale;
                    SignalTarget(module.interior.transform, "Pin_" + signal + "_CLK", new[] { clockSignal }, module.modulePath + "/clk -> DFF#" + signal + ".CLK", module, false, clockPin, Mathf.Max(.019f, .046f * view.gateScale));
                    symbols.Segment(clockPin, position + new Vector3(-.21f, -.13f, -.01f) * view.gateScale, clockSignal, .015f * view.gateScale);
                    var clockLabel = WorldText(module.interior.transform, "PinLabel_" + signal + "_CLK", "CLK", clockPin + new Vector3(-.03f, -.04f, -.01f) * view.gateScale, new Vector2(.22f, .10f), 5, new Color(.67f, .83f, .92f));
                    clockLabel.transform.localScale = Vector3.one * .01f * view.gateScale;
                }
            }
        }
        var fanout = new Dictionary<int, int>();
        foreach (int signal in driven)
        {
            var node = nodes[signal]; int op = (int)node["op"]; int[] inputs = Inputs(node);
            for (int pin = 0; pin < inputs.Length; pin++)
            {
                int sourceId = inputs[pin]; Vector3 source;
                if (view.gatePositions.ContainsKey(sourceId)) source = view.gatePositions[sourceId] + new Vector3(.32f * view.gateScale, 0f, -.01f);
                else
                {
                    var port = view.ports.FirstOrDefault(target => target.signalIds.Contains(sourceId));
                    source = port != null ? port.transform.localPosition : new Vector3(-view.width * .5f, 0, -.12f);
                }
                Vector3 end = InputPosition(view.gatePositions[signal], op, pin, inputs.Length, view.gateScale);
                float z = -.135f - (sourceId % 3) * .009f;
                float mid = source.x < end.x ? (source.x + end.x) * .5f : -view.width * .5f + .12f + (sourceId % 5) * .026f;
                traces.Path(new[] { source, new Vector3(source.x, source.y, z), new Vector3(mid, source.y, z), new Vector3(mid, end.y, z), new Vector3(end.x, end.y, z), end }, sourceId, Mathf.Max(.006f, .011f * view.gateScale), .65f);
                fanout[sourceId] = fanout.ContainsKey(sourceId) ? fanout[sourceId] + 1 : 1;
            }
            if (op == 3)
            {
                var clockPort = view.ports.FirstOrDefault(port => port.name == "Port_clk");
                if (clockPort != null && clockPort.signalIds.Length > 0)
                {
                    int clockId = clockPort.signalIds[0];
                    Vector3 a = clockPort.transform.localPosition;
                    Vector3 b = view.gatePositions[signal] + new Vector3(-.32f, -.13f, -.01f) * view.gateScale;
                    float layer = -.174f;
                    float lane = -view.width * .5f + .08f + (signal % 5) * .018f;
                    traces.Path(new[] { a, new Vector3(a.x, a.y, layer), new Vector3(lane, a.y, layer), new Vector3(lane, b.y, layer), new Vector3(b.x, b.y, layer), b }, clockId, Mathf.Max(.005f, .008f * view.gateScale), .52f);
                    fanout[clockId] = fanout.ContainsKey(clockId) ? fanout[clockId] + 1 : 1;
                }
            }
        }
        foreach (var branch in fanout.Where(pair => pair.Value > 1))
        {
            float branchLayer = -.135f - (branch.Key % 3) * .009f;
            if (view.gatePositions.ContainsKey(branch.Key))
            {
                Vector3 junction = view.gatePositions[branch.Key] + new Vector3(.32f * view.gateScale, 0f, -.01f);
                junction.z = branchLayer;
                traces.Circle(junction, .023f * view.gateScale, branch.Key, true);
            }
            else
            {
                var port = view.ports.FirstOrDefault(target => target.signalIds.Contains(branch.Key));
                if (port != null)
                {
                    Vector3 junction = port.transform.localPosition;
                    junction.z = port.name == "Port_clk" && driven.Any(nodeId => (int)nodes[nodeId]["op"] == 3) ? -.174f : branchLayer;
                    traces.Circle(junction, .022f, branch.Key, true);
                }
            }
        }
        MeshObject(module.interior.transform, "ANSIGates", "Module_" + id + "_Gates", symbols, _display);
        MeshObject(module.interior.transform, "OrthogonalTraces", "Module_" + id + "_Traces", traces, _display);
        var outputTraces = new Geometry();
        for (int portIndex = 0; portIndex < view.ports.Count; portIndex++)
        {
            if (view.portDirections[portIndex] == "input") continue;
            var port = view.ports[portIndex];
            foreach (int signal in port.signalIds)
            {
                if (!view.gatePositions.ContainsKey(signal)) continue;
                Vector3 source = view.gatePositions[signal] + new Vector3(.32f * view.gateScale, 0, -.01f);
                Vector3 end = unfolded[portIndex];
                float layer = -.135f - (signal % 3) * .009f;
                float lane = (source.x + end.x) * .5f;
                outputTraces.Path(new[] { source, new Vector3(source.x, source.y, layer), new Vector3(lane, source.y, layer), new Vector3(lane, end.y, layer), new Vector3(end.x, end.y, layer), end }, signal, Mathf.Max(.006f, .011f * view.gateScale), .65f);
            }
        }
        MeshObject(module.interior.transform, "OutputPortTraces", "Module_" + id + "_Outputs", outputTraces, _display);
        if (view.dockArea > 0)
            WorldText(module.interior.transform, "DockHint", "DOCKED CHILDREN: GRAB / EXTRACT TO EXAMINE AT FULL SIZE", new Vector3(0, -view.height * .5f + view.dockArea + .36f, -.14f), new Vector2(view.width - .25f, .13f), 9, new Color(.5f, .66f, .74f));
        else if ((int)data["parentId"] < 0)
            WorldText(module.interior.transform, "PinsHint", "PIN: SELECT PROBE A-D, THEN CLICK  /  VR: TOUCH NEEDLE + USE", new Vector3(0, -view.height * .5f + .64f, -.14f), new Vector2(view.width - .25f, .13f), 9, new Color(.5f, .66f, .74f));
        return view;
    }

    private static void Backing(Transform parent, float width, float height)
    {
        Primitive(parent, "Frame", PrimitiveType.Cube, new Vector3(0, 0, .035f), new Vector3(width + .07f, height + .07f, .09f), _edge, false);
        Primitive(parent, "Surface", PrimitiveType.Cube, new Vector3(0, 0, -.012f), new Vector3(width, height, .04f), _board, false);
    }

    private static void PlaceModules(ModuleView[] ordered, Dictionary<int, ModuleView> views)
    {
        var root = ordered.First(view => view.behaviour.logicalParent == null);
        root.behaviour.expanded = true;
        root.behaviour.transform.position = new Vector3(0, 1.02f, .60f);
        root.behaviour.transform.eulerAngles = new Vector3(80, 0, 0);
        foreach (var view in ordered.Where(view => view != root))
        {
            var module = view.behaviour; var parent = views[module.logicalParent.moduleId];
            var siblings = parent.behaviour.childModules; int slot = Array.IndexOf(siblings, module);
            int dockRows = Mathf.CeilToInt(siblings.Length / 3f);
            float dockY = parent.behaviour.logicalParent == null ? 1.80f + (slot / 3) * .82f : -parent.height * .5f + .88f + (dockRows - 1 - slot / 3) * .84f;
            module.dockLocalPosition = new Vector3((slot % 3 - 1) * 1.48f, dockY, -.22f);
            module.dockLocalEuler = Vector3.zero;
            module.transform.SetParent(parent.behaviour.childrenDock, false);
            module.transform.localPosition = module.dockLocalPosition;
            module.transform.localEulerAngles = module.dockLocalEuler;
            module.transform.localScale = module.dockLocalScale;
            module.expanded = false;
            if (module.logicalDepth == 1)
            {
                module.transform.SetParent(_workspace, true); module.isDetached = true;
                module.transform.localScale = Vector3.one;
                int index = Array.IndexOf(root.behaviour.childModules, module);
                float side = index % 2 == 0 ? -1f : 1f;
                module.handleSide = side;
                module.transform.position = index < 4 ? new Vector3(side * 4.65f, index < 2 ? 2.70f : 1.05f, 2.3f) : new Vector3(side * 7.85f, 1.35f, 2.8f);
                module.transform.eulerAngles = new Vector3(0, side * -12f, 0);
            }
        }
        // The complete-adder branch is intentionally folded initially, keeping the entry view readable.
    }

    private static ModuleView[] HierarchyOrder(Dictionary<int, ModuleView> views)
    {
        var ordered = new List<ModuleView>();
        var visited = new HashSet<int>();
        Action<ModuleView> visit = null;
        visit = view =>
        {
            if (!visited.Add(view.behaviour.moduleId)) throw new InvalidOperationException("Cyclic or duplicate module hierarchy.");
            ordered.Add(view);
            foreach (var child in (JArray)view.data["children"]) visit(views[(int)child]);
        };
        foreach (var root in views.Values.Where(view => (int)view.data["parentId"] < 0).OrderBy(view => view.behaviour.moduleId)) visit(root);
        if (ordered.Count != views.Count) throw new InvalidOperationException("Module hierarchy contains unreachable instances.");
        return ordered.ToArray();
    }

    private static List<Material> BuildExternalWires(ModuleView[] views, Dictionary<int, JObject> nodes, int textureWidth)
    {
        var materials = new Dictionary<int, Material>();
        var ports = views.SelectMany(view => view.ports.Select((target, index) => new { view, target, direction = view.portDirections[index] })).ToArray();
        var used = new HashSet<string>(); int wireIndex = 0;
        foreach (var endpoint in ports)
        {
            var ids = endpoint.target.signalIds;
            if (ids.Length == 0) continue;
            int driver = (int)nodes[ids[0]]["moduleId"];
            var candidates = ports.Where(port => port.view != endpoint.view && ids.All(id => port.target.signalIds.Contains(id))).ToArray();
            var source = candidates.OrderBy(port => port.view.behaviour.moduleId == driver ? 0 : 1)
                .ThenBy(port => port.direction == "output" ? 0 : 1)
                .ThenBy(port => Math.Abs(port.view.behaviour.logicalDepth - endpoint.view.behaviour.logicalDepth)).FirstOrDefault();
            if (source == null) continue;
            if (endpoint.direction == "output" && endpoint.view.behaviour.moduleId == driver) continue;
            string key = string.Join(",", new[] { source.target.GetInstanceID(), endpoint.target.GetInstanceID() }.OrderBy(value => value));
            if (!used.Add(key)) continue;
            int signal = ids[0];
            if (!materials.ContainsKey(signal))
            {
                var material = Material("WireSignal_" + signal, "VrcCpuWorld/CircuitLabDisplay", Color.white);
                material.SetFloat("_TextureWidth", textureWidth); material.SetFloat("_SignalOverride", signal);
                materials[signal] = material;
            }
            var go = Child(_workspace, "BoundaryWire_" + wireIndex + "_" + signal);
            var line = go.AddComponent<LineRenderer>(); line.sharedMaterial = materials[signal];
            line.positionCount = 6; line.useWorldSpace = true; line.startWidth = ids.Length > 1 ? .020f : .012f; line.endWidth = line.startWidth;
            line.numCornerVertices = 2; line.numCapVertices = 2; line.shadowCastingMode = ShadowCastingMode.Off; line.receiveShadows = false;
            for (int point = 0; point < 6; point++) line.SetPosition(point, source.target.transform.position);
            var wire = Behaviour<CircuitLabWire>(go); wire.line = line; wire.source = source.target.transform; wire.destination = endpoint.target.transform;
            wire.routingDepth = .2f + (wireIndex % 5) * .035f; wireIndex++;
        }
        return materials.Values.ToList();
    }

    private static CircuitLabProbe[] BuildPanel(Transform world, CircuitLabSimulator simulator, Material waveform, CircuitLabModule[] modules)
    {
        var panel = Child(world, "MovableWaveformPanel"); panel.transform.position = new Vector3(0, 3.75f, 1.6f);
        Backing(panel.transform, 5.6f, 6.0f);
        var handle = Primitive(panel.transform, "PanelGrabHandle", PrimitiveType.Cube, new Vector3(0, -2.84f, 0), new Vector3(1.4f, .17f, .22f), _handle, false);
        var panelCollider = panel.AddComponent<BoxCollider>(); panelCollider.center = handle.transform.localPosition; panelCollider.size = new Vector3(1.4f, .17f, .22f);
        foreach (float side in new[] { -1f, 1f })
        {
            Vector3 sidePosition = new Vector3(side * 2.97f, -2.0f, 0f);
            Primitive(panel.transform, side < 0 ? "LeftPanelHandle" : "RightPanelHandle", PrimitiveType.Cube, sidePosition, new Vector3(.17f, .78f, .22f), _handle, false);
            var sideCollider = panel.AddComponent<BoxCollider>(); sideCollider.center = sidePosition; sideCollider.size = new Vector3(.17f, .78f, .22f);
            var sideLabel = WorldText(panel.transform, side < 0 ? "LeftGrabHint" : "RightGrabHint", "GRAB PANEL", sidePosition + new Vector3(0, 0, -.12f), new Vector2(.72f, .13f), 10, Color.white);
            sideLabel.transform.localRotation = Quaternion.Euler(0, 0, 90);
        }
        WorldText(panel.transform, "PanelGrabHint", "GRAB PANEL", handle.transform.localPosition + new Vector3(0, 0, -.12f), new Vector2(1.3f, .13f), 11, Color.white);
        var body = panel.AddComponent<Rigidbody>(); body.useGravity = false; body.isKinematic = true;
        AddMovableTool(panel, handle.transform);
        WorldText(panel.transform, "WaveTitle", "4 CHANNEL LOGIC ANALYZER", new Vector3(0, 2.78f, -.08f), new Vector2(5.3f, .32f), 27, Color.white);
        var screen = Primitive(panel.transform, "WaveformScreen", PrimitiveType.Quad, new Vector3(0, 1.12f, -.10f), new Vector3(5.05f, 1.85f, 1), waveform, false);
        simulator.waveHudText = WorldText(panel.transform, "WaveHud", "LIVE  /  FOLLOWING LATEST  /  FREEZE LOCKS THE VIEW", new Vector3(0, -.06f, -.14f), new Vector2(5.2f, .38f), 14, new Color(.6f, .83f, .9f));
        simulator.simHudText = WorldText(panel.transform, "SimulationHud", "PAUSED  /  STEP TO EXECUTE ONE CLOCK", new Vector3(0, -1.52f, -.14f), new Vector2(5.2f, .40f), 16, new Color(.68f, .90f, .82f));
        simulator.statusText = WorldText(panel.transform, "TestStatus", "SELF TEST READY", new Vector3(0, -2.52f, -.14f), new Vector2(5.3f, .18f), 12, new Color(.55f, .78f, .86f));
        Controls(panel.transform, simulator, new[] { "FreezeWaveform", "ResumeWaveform", "ZoomIn", "ZoomOut", "PanLeft", "PanRight", "FollowLatest" }, new[] { "FREEZE", "LIVE", "ZOOM +", "ZOOM -", "< PAN", "PAN >", "LATEST" }, -.45f, 5.05f);
        Controls(panel.transform, simulator, new[] { "MoveCursorALeft", "MoveCursorARight", "MoveCursorBLeft", "MoveCursorBRight" }, new[] { "CURSOR A <", "A >", "CURSOR B <", "B >" }, -.78f, 5.05f);
        Controls(panel.transform, simulator, new[] { "CycleBitView", "CycleNumberFormat", "ToggleAA", "EdgeWidthDown", "EdgeWidthUp" }, new[] { "BUS / BITS", "HEX / BIN / DEC", "SMOOTH", "EDGE -", "EDGE +" }, -1.11f, 5.05f);
        Controls(panel.transform, simulator, new[] { "Run", "Pause", "Step", "SpeedDown", "SpeedUp", "ResetCircuit" }, new[] { "RUN", "PAUSE", "STEP", "SLOWER", "FASTER", "RESET" }, -1.94f, 5.05f);
        Controls(panel.transform, simulator, new[] { "SelectProgram", "RestartExperiment", "RunSelfTest" }, new[] { "PROGRAM 0-3", "RESTART", "SELF TEST" }, -2.28f, 5.05f);
        WorldText(panel.transform, "Hints", "VR: GRAB = DETACH, TOUCH PIN + USE = LOCK   /   DESKTOP: SELECT A-D, CLICK PIN", new Vector3(0, -3.0f, -.13f), new Vector2(5.3f, .15f), 9, new Color(.5f, .65f, .73f));
        var rack = Child(world, "ProbeAndHierarchyRack"); rack.transform.position = new Vector3(0, .92f, -1.17f); rack.transform.eulerAngles = new Vector3(65, 0, 0);
        Backing(rack.transform, 5.7f, 1.65f);
        var rackHandle = Primitive(rack.transform, "RackHandle", PrimitiveType.Cube, new Vector3(3.05f, 0, 0), new Vector3(.2f, .8f, .18f), _handle, false);
        var rackCollider = rack.AddComponent<BoxCollider>(); rackCollider.center = rackHandle.transform.localPosition; rackCollider.size = new Vector3(.2f, .8f, .18f);
        AddMovableTool(rack, rackHandle.transform);
        var probes = new CircuitLabProbe[4];
        var channelColors = new[] { new Color(.20f, .90f, 1f), new Color(1f, .78f, .3f), new Color(.64f, 1f, .42f), new Color(1f, .45f, .65f) };
        for (int channel = 0; channel < 4; channel++)
        {
            var go = Child(_workspace, "Probe_" + (char)('A' + channel));
            go.transform.position = new Vector3(-1.45f + channel * .97f, 1.16f, -1.82f);
            go.transform.eulerAngles = new Vector3(35, 0, 0);
            var probeMaterial = Material("Probe" + channel, "Unlit/Color", channelColors[channel]);
            Primitive(go.transform, "ProbeGrip", PrimitiveType.Capsule, new Vector3(0, 0, 0), new Vector3(.072f, .15f, .072f), probeMaterial, false);
            var probeCollider = go.AddComponent<CapsuleCollider>(); probeCollider.height = .30f; probeCollider.radius = .042f;
            Primitive(go.transform, "Needle", PrimitiveType.Cylinder, new Vector3(0, -.20f, 0), new Vector3(.013f, .08f, .013f), _highlight, false);
            var probe = Behaviour<CircuitLabProbe>(go); probe.simulator = simulator; probe.channel = channel;
            probe.tip = Child(go.transform, "NeedleTip").transform; probe.tip.localPosition = new Vector3(0, -.28f, 0);
            probe.probeLabel = WorldText(go.transform, "NeedleLabel", "", new Vector3(.13f, .045f, -.035f), new Vector2(.25f, .22f), 10, channelColors[channel]);
            probe.panelLabel = WorldText(rack.transform, "Channel" + channel, ((char)('A' + channel)) + "    UNBOUND", new Vector3(0, .43f - channel * .24f, -.11f), new Vector2(4.3f, .18f), 12, channelColors[channel]);
            var rigidbody = go.AddComponent<Rigidbody>(); rigidbody.useGravity = false; rigidbody.isKinematic = true;
            probe.pickup = ConfigurePickup(go, go.transform, true); probe.objectSync = go.AddComponent<VRCObjectSync>(); probe.workspaceRoot = _workspace;
            probe.sharedState = Behaviour<CircuitLabObjectState>(Child(go.transform, "SharedObjectState")); probe.sharedState.probe = probe;
            probe.rackAnchor = Child(rack.transform, "RackAnchor_" + channel).transform;
            probe.rackAnchor.localPosition = new Vector3(-1.45f + channel * .97f, -.25f, -.20f);
            probe.rackAnchor.localRotation = Quaternion.identity;
            Button(rack.transform, "SELECT_" + channel, "SELECT " + (char)('A' + channel), probe, "SelectProbe", new Vector3(-2.32f, .43f - channel * .24f, -.11f), new Vector2(.69f, .18f), 10);
            Button(rack.transform, "DETACH_" + channel, "RETURN", probe, "ReturnToRack", new Vector3(2.33f, .43f - channel * .24f, -.11f), new Vector2(.65f, .18f), 10);
            probes[channel] = probe;
        }
        probes[0].isSelected = true;
        Button(rack.transform, "RETURN_ALL_PROBES", "RETURN ALL PROBES", _layout, "ReturnAllProbes", new Vector3(-1.45f, -.70f, -.12f), new Vector2(2.45f, .24f), 12);
        Button(rack.transform, "RESET_LAYOUT", "RESET LAYOUT", _layout, "ResetLayout", new Vector3(1.35f, -.70f, -.12f), new Vector2(2.45f, .24f), 12);
        BuildHierarchy(world, modules);
        _coordinator.statusText = WorldText(panel.transform, "ControlStatus", "CONTROL: UNCLAIMED | CLICK A CONTROL TO BEGIN", new Vector3(0, 2.53f, -.14f), new Vector2(5.2f, .15f), 12, new Color(.65f, .90f, .8f));
        Controls(panel.transform, _coordinator, new[] { "RequestControl", "GrantControl", "ReleaseControl" }, new[] { "REQUEST CONTROL", "GRANT REQUEST", "RELEASE CONTROL" }, 2.27f, 5.05f);
        return probes;
    }

    private static void GateReference(Transform world, Dictionary<int, JObject> nodes)
    {
        var reference = Child(world, "ANSISymbolGuide"); reference.transform.position = new Vector3(8.4f, 3.95f, 1.9f);
        Backing(reference.transform, 2.65f, 2.85f);
        WorldText(reference.transform, "GuideTitle", "ANSI GATE SYMBOLS", new Vector3(0, 1.2f, -.1f), new Vector2(2.45f, .24f), 18, Color.white);
        WorldText(reference.transform, "GuideHint", "INPUTS LEFT  /  OUTPUT RIGHT", new Vector3(0, .96f, -.1f), new Vector2(2.4f, .16f), 10, new Color(.6f, .8f, .88f));
        var geometry = new Geometry();
        int[] operations = { 6, 7, 8, 5, 10, 3 };
        for (int i = 0; i < operations.Length; i++)
        {
            int op = operations[i]; int signal = (int)nodes.Values.First(node => (int)node["op"] == op)["id"];
            Vector3 point = new Vector3(i % 2 == 0 ? -.62f : .62f, .52f - (i / 2) * .70f, -.12f);
            Gate(geometry, point, op, signal, 1.05f);
            geometry.Segment(point + new Vector3(-.34f, .1f, 0), point + new Vector3(-.20f, .1f, 0), signal, .014f);
            if (op != 5 && op != 3) geometry.Segment(point + new Vector3(-.34f, -.1f, 0), point + new Vector3(-.20f, -.1f, 0), signal, .014f);
            WorldText(reference.transform, "Guide_" + op, GateName(op), point + new Vector3(0, -.28f, -.02f), new Vector2(1.1f, .14f), 13, new Color(.75f, .86f, .94f));
        }
        MeshObject(reference.transform, "GuideSymbols", "ANSISymbolGuide", geometry, _display);
        WorldText(reference.transform, "GuideFooter", "TOUCH ANY REAL PIN WITH A PROBE", new Vector3(0, -1.26f, -.12f), new Vector2(2.45f, .14f), 9, new Color(.6f, .8f, .88f));
    }

    private static void BuildHierarchy(Transform world, CircuitLabModule[] modules)
    {
        var panel = Child(world, "HierarchyPanel"); panel.transform.position = new Vector3(-7.5f, 1.95f, -1.4f);
        Backing(panel.transform, 3.6f, 2.85f);
        WorldText(panel.transform, "HierarchyTitle", "SHARED MODULE HIERARCHY", new Vector3(0, 1.21f, -.12f), new Vector2(3.3f, .24f), 19, Color.white);
        WorldText(panel.transform, "HierarchyHint", "ARROW: DIRECTORY  /  NAME: 3D EXPAND  /  +: EXTRACT", new Vector3(0, .98f, -.12f), new Vector2(3.4f, .15f), 9, new Color(.6f, .8f, .88f));
        var grip = Primitive(panel.transform, "HierarchyGrip", PrimitiveType.Cube, new Vector3(-2.04f, 0, 0), new Vector3(.18f, .78f, .2f), _handle, false);
        var gripCollider = panel.AddComponent<BoxCollider>(); gripCollider.center = grip.transform.localPosition; gripCollider.size = new Vector3(.18f, .78f, .2f);
        AddMovableTool(panel, grip.transform);
        var canvas = CreateCanvas(panel.transform, "HierarchyScrollCanvas", new Vector3(0, -.05f, -.16f), new Vector2(3.4f, 1.85f));
        var scroll = canvas.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.inertia = false;
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D)); viewport.layer = InteractionLayer; viewport.transform.SetParent(canvas.transform, false);
        var viewportRect = viewport.GetComponent<RectTransform>(); viewportRect.anchorMin = Vector2.zero; viewportRect.anchorMax = Vector2.one; viewportRect.offsetMin = Vector2.zero; viewportRect.offsetMax = new Vector2(-18, 0);
        viewport.GetComponent<Image>().color = new Color(.01f, .04f, .07f, .9f);
        var content = new GameObject("Content", typeof(RectTransform)); content.layer = InteractionLayer; content.transform.SetParent(viewport.transform, false);
        var contentRect = content.GetComponent<RectTransform>(); contentRect.anchorMin = new Vector2(0, 1); contentRect.anchorMax = Vector2.one; contentRect.pivot = new Vector2(.5f, 1); contentRect.sizeDelta = new Vector2(0, modules.Length * 26f); contentRect.anchoredPosition = Vector2.zero;
        scroll.content = contentRect; scroll.viewport = viewportRect;
        var tree = Behaviour<CircuitLabHierarchyTree>(OwnedChild(world, "SharedHierarchyState")); tree.coordinator = _coordinator; tree.modules = modules; tree.content = contentRect; tree.scrollRect = scroll; tree.rowHeight = 26f;
        tree.rowObjects = new GameObject[modules.Length]; tree.rowRects = new RectTransform[modules.Length]; tree.directoryLabels = new Text[modules.Length]; tree.directoryExpanded = new bool[modules.Length]; tree.parentIndices = new int[modules.Length];
        for (int i = 0; i < modules.Length; i++)
        {
            var row = new GameObject("HierarchyRow_" + modules[i].moduleId, typeof(RectTransform)); row.layer = InteractionLayer; row.transform.SetParent(content.transform, false);
            var rect = row.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = new Vector2(0, 26f);
            var handler = Behaviour<CircuitLabHierarchyRow>(row); handler.tree = tree; handler.module = modules[i]; handler.moduleIndex = i;
            tree.rowObjects[i] = row; tree.rowRects[i] = rect; tree.directoryExpanded[i] = true; tree.parentIndices[i] = Array.IndexOf(modules, modules[i].logicalParent);
            var arrow = UiButton(row.transform, "DIRECTORY_" + modules[i].moduleId, "v", handler, "ToggleDirectory", new Vector2(-144 + modules[i].logicalDepth * 8f, 0), new Vector2(22, 22), 12);
            tree.directoryLabels[i] = arrow.GetComponentInChildren<Text>();
            string label = modules[i].modulePath.Split('/').Last() + " [3D]";
            UiButton(row.transform, "HIERARCHY_" + modules[i].moduleId, label, handler, "ToggleModule", new Vector2(-6 + modules[i].logicalDepth * 4f, 0), new Vector2(220 - modules[i].logicalDepth * 8f, 22), 11);
            UiButton(row.transform, "EXTRACT_ROW_" + modules[i].moduleId, "+", handler, "Extract", new Vector2(136, 0), new Vector2(24, 22), 12);
        }
        var barObject = new GameObject("Scrollbar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Scrollbar)); barObject.layer = InteractionLayer; barObject.transform.SetParent(canvas.transform, false);
        var barRect = barObject.GetComponent<RectTransform>(); barRect.anchorMin = new Vector2(1, 0); barRect.anchorMax = Vector2.one; barRect.pivot = new Vector2(1, .5f); barRect.sizeDelta = new Vector2(16, 0); barRect.anchoredPosition = Vector2.zero;
        barObject.GetComponent<Image>().color = new Color(.06f, .13f, .19f);
        var thumb = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)); thumb.layer = InteractionLayer; thumb.transform.SetParent(barObject.transform, false);
        var thumbRect = thumb.GetComponent<RectTransform>(); thumbRect.anchorMin = Vector2.zero; thumbRect.anchorMax = Vector2.one; thumbRect.offsetMin = new Vector2(2, 2); thumbRect.offsetMax = new Vector2(-2, -2);
        thumb.GetComponent<Image>().color = new Color(.25f, .65f, .7f);
        var bar = barObject.GetComponent<Scrollbar>(); bar.handleRect = thumbRect; bar.targetGraphic = thumb.GetComponent<Image>(); bar.direction = Scrollbar.Direction.BottomToTop; scroll.verticalScrollbar = bar;
        bar.navigation = new Navigation { mode = Navigation.Mode.None };
        UnityEventTools.AddStringPersistentListener(scroll.onValueChanged, UdonSharpEditorUtility.GetBackingUdonBehaviour(tree).SendCustomEvent, "ScrollChanged");
        Button(panel.transform, "TREE_SCROLL_UP", "SCROLL UP", tree, "ScrollUp", new Vector3(-1.18f, -1.18f, -.12f), new Vector2(1.1f, .22f), 10);
        Button(panel.transform, "TREE_SCROLL_DOWN", "SCROLL DOWN", tree, "ScrollDown", new Vector3(0, -1.18f, -.12f), new Vector2(1.1f, .22f), 10);
        Button(panel.transform, "TREE_RETRIEVE", "RETRIEVE", modules[0], "RetrieveChildren", new Vector3(1.18f, -1.18f, -.12f), new Vector2(1.1f, .22f), 10);
        tree.RefreshTree();
    }

    private static void BuildTerminal(Transform world, CircuitLabSimulator simulator, JObject data)
    {
        var panel = Child(world, "TerminalPanel"); panel.transform.position = new Vector3(7.0f, 2.7f, -.4f);
        Backing(panel.transform, 3.6f, 3.3f);
        var grip = Primitive(panel.transform, "TerminalGrip", PrimitiveType.Cube, new Vector3(2.05f, 0, 0), new Vector3(.18f, .78f, .2f), _handle, false);
        var collider = panel.AddComponent<BoxCollider>(); collider.center = grip.transform.localPosition; collider.size = new Vector3(.18f, .78f, .2f); AddMovableTool(panel, grip.transform);
        WorldText(panel.transform, "TerminalTitle", "SHARED CPU TERMINAL", new Vector3(0, 1.37f, -.12f), new Vector2(3.35f, .25f), 20, Color.white);
        var terminal = Behaviour<CircuitLabTerminal>(OwnedChild(world, "TerminalController")); terminal.simulator = simulator; terminal.coordinator = _coordinator; simulator.terminal = terminal; _coordinator.terminal = terminal;
        terminal.outputText = WorldText(panel.transform, "TerminalOutput", "", new Vector3(0, .02f, -.14f), new Vector2(3.2f, 2.15f), 13, new Color(.72f, 1f, .75f)); terminal.outputText.alignment = TextAnchor.UpperLeft; terminal.outputText.horizontalOverflow = HorizontalWrapMode.Wrap;
        terminal.statusText = WorldText(panel.transform, "TerminalStatus", "READY", new Vector3(0, -1.15f, -.14f), new Vector2(3.3f, .20f), 11, new Color(.6f, .8f, .88f));
        terminal.captureMaterial = Material("TerminalCapture", "VrcCpuWorld/CircuitLabHistory", Color.white);
        terminal.eventA = RenderTexture("TerminalEventsA", 256, 2, RenderTextureFormat.ARGB32); terminal.eventB = RenderTexture("TerminalEventsB", 256, 2, RenderTextureFormat.ARGB32); terminal.eventCapacity = 256;
        terminal.outputWriteSignal = (int)data["ports"]["terminal_write"][0]; terminal.outputSignals = data["ports"]["terminal_data"].ToObject<int[]>(); terminal.sequenceSignals = data["ports"]["terminal_sequence"].ToObject<int[]>();
        Button(panel.transform, "CLEAR_TERMINAL", "CLEAR TERMINAL", simulator, "ClearTerminal", new Vector3(0, -1.44f, -.14f), new Vector2(3.15f, .23f), 12);
    }

    private static void Controls(Transform parent, UdonSharpBehaviour target, string[] events, string[] labels, float y, float width)
    {
        for (int i = 0; i < events.Length; i++)
        {
            float size = width / events.Length;
            var button = Button(parent, events[i], labels[i], target, events[i], new Vector3((i - (events.Length - 1) * .5f) * size, y, -.145f), new Vector2(size - .05f, .24f), 11);
            Interaction(button, labels[i], 3.5f);
        }
    }

    private static int[] Inputs(JObject node)
    {
        int op = (int)node["op"];
        if (op <= 2) return new int[0];
        if (op <= 5) return new[] { (int)node["a"] };
        if (op == 10) return new[] { (int)node["a"], (int)node["b"], (int)node["c"] };
        return new[] { (int)node["a"], (int)node["b"] };
    }

    private static string GateName(int op)
    {
        return new[] { "0", "1", "IN", "DFF", "BUF", "NOT", "AND", "OR", "XOR", "XNOR", "MUX", "NAND", "NOR", "ANDNOT", "ORNOT" }[op];
    }

    private static string PinName(int op, int pin)
    {
        if (op == 3) return "D";
        if (op == 10 && pin == 2) return "S";
        return pin == 0 ? "A" : "B";
    }

    private static Vector3 InputPosition(Vector3 position, int op, int pin, int count, float scale)
    {
        if (op == 10 && pin == 2) return position + new Vector3(0, -.23f * scale, -.01f);
        int normalCount = op == 10 ? 2 : count;
        return position + new Vector3(-.32f * scale, (normalCount == 1 ? 0 : (pin == 0 ? .10f : -.10f)) * scale, -.01f);
    }

    private static void Gate(Geometry mesh, Vector3 centre, int op, int signal, float scale)
    {
        float thickness = .017f * scale;
        Action<IEnumerable<Vector2>> path = points => mesh.Path(points.Select(point => centre + new Vector3(point.x, point.y, 0) * scale), signal, thickness);
        bool bubble = op == 5 || op == 9 || op == 11 || op == 12;
        if (op == 4 || op == 5)
            path(new[] { new Vector2(-.20f, -.17f), new Vector2(-.20f, .17f), new Vector2(.18f, 0), new Vector2(-.20f, -.17f) });
        else if (op == 6 || op == 11 || op == 13)
        {
            var points = new List<Vector2> { new Vector2(-.21f, -.18f), new Vector2(-.21f, .18f), new Vector2(0, .18f) };
            for (int i = 0; i <= 18; i++) { float angle = Mathf.PI * .5f - i * Mathf.PI / 18; points.Add(new Vector2(Mathf.Cos(angle) * .20f, Mathf.Sin(angle) * .18f)); }
            points.Add(new Vector2(-.21f, -.18f)); path(points);
        }
        else if (op == 7 || op == 8 || op == 9 || op == 12 || op == 14)
        {
            var points = new List<Vector2>();
            for (int i = 0; i <= 14; i++) { float t = i / 14f; points.Add(new Vector2(-.23f + .45f * t, .18f * (1 - t * t))); }
            for (int i = 0; i <= 14; i++) { float t = i / 14f; points.Add(new Vector2(.22f - .45f * t, -.18f * (1 - (1 - t) * (1 - t)))); }
            for (int i = 0; i <= 14; i++) { float t = i / 14f; points.Add(new Vector2(-.23f + .08f * Mathf.Sin(t * Mathf.PI), -.18f + .36f * t)); }
            path(points);
            if (op == 8 || op == 9) path(Enumerable.Range(0, 15).Select(i => new Vector2(-.275f + .08f * Mathf.Sin(i / 14f * Mathf.PI), -.18f + .36f * i / 14f)));
        }
        else if (op == 10)
            path(new[] { new Vector2(-.21f, -.19f), new Vector2(-.21f, .19f), new Vector2(.20f, .12f), new Vector2(.20f, -.12f), new Vector2(-.21f, -.19f) });
        else
        {
            path(new[] { new Vector2(-.21f, -.18f), new Vector2(-.21f, .18f), new Vector2(.21f, .18f), new Vector2(.21f, -.18f), new Vector2(-.21f, -.18f) });
            if (op == 3) path(new[] { new Vector2(-.21f, -.08f), new Vector2(-.13f, -.12f), new Vector2(-.21f, -.16f) });
        }
        if (bubble) mesh.Circle(centre + new Vector3(.25f, 0, -.005f) * scale, .032f * scale, signal);
        if (op == 13 || op == 14) mesh.Circle(centre + new Vector3(-.245f, -.10f, -.005f) * scale, .026f * scale, signal);
        mesh.Segment(centre + new Vector3(bubble ? .285f : .21f, 0f, 0f) * scale, centre + new Vector3(.32f, 0f, 0f) * scale, signal, thickness);
    }

    private static CircuitLabSignalTarget SignalTarget(Transform parent, string name, int[] signals, string label, CircuitLabModule owner, bool port, Vector3 position, float radius)
    {
        var go = Child(parent, name); go.transform.localPosition = position; go.layer = InteractionLayer;
        var collider = go.AddComponent<SphereCollider>(); collider.radius = radius; collider.isTrigger = false;
        var target = Behaviour<CircuitLabSignalTarget>(go); target.signalIds = signals; target.signalLabel = label; target.ownerModule = owner; target.isPort = port;
        var marker = Primitive(go.transform, "PreselectHighlight", PrimitiveType.Sphere, Vector3.zero, Vector3.one * radius * 1.6f, _highlight, false);
        marker.SetActive(false); target.highlight = marker;
        Interaction(target, "Probe " + label, 1.5f); Targets.Add(target); return target;
    }

    private static void MeshObject(Transform parent, string name, string assetName, Geometry geometry, Material material)
    {
        var mesh = Asset(Root + "/Meshes/" + assetName + ".asset", () => new Mesh());
        mesh.Clear(); mesh.indexFormat = IndexFormat.UInt32; mesh.SetVertices(geometry.vertices); mesh.SetUVs(0, geometry.uv); mesh.SetColors(geometry.colors); mesh.SetTriangles(geometry.triangles, 0); mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        var go = Child(parent, name); go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
    }

    private static GameObject OwnedChild(Transform parent, string name)
    { var existing = parent.Find(name); return existing == null ? Child(parent, name) : existing.gameObject; }

    private static GameObject Child(Transform parent, string name)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false); return go;
    }

    private static GameObject Primitive(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, bool collider = true)
    {
        var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = scale;
        var renderer = go.GetComponent<Renderer>(); renderer.sharedMaterial = material; renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    private static T Behaviour<T>(GameObject go) where T : UdonSharpBehaviour
    {
        var behaviour = go.GetComponent<T>(); if (behaviour == null) behaviour = go.AddUdonSharpComponent<T>(); if (!Behaviours.Contains(behaviour)) Behaviours.Add(behaviour); return behaviour;
    }

    private static Text WorldText(Transform parent, string name, string value, Vector3 position, Vector2 size, int fontSize, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasRenderer), typeof(Text)); go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = Vector3.one * .01f;
        var rect = go.GetComponent<RectTransform>(); rect.sizeDelta = size * 100f;
        go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var text = go.GetComponent<Text>(); text.font = _font; text.fontSize = fontSize; text.text = value; text.color = color; text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false; text.horizontalOverflow = HorizontalWrapMode.Overflow; text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static CircuitLabButton Button(Transform parent, string name, string label, UdonSharpBehaviour target, string eventName, Vector3 position, Vector2 size, int fontSize)
    {
        var canvas = CreateCanvas(parent, name + "_Canvas", position, size);
        return UiButton(canvas.transform, name, label, target, eventName, Vector2.zero, size * 100f, fontSize);
    }

    private static GameObject CreateCanvas(Transform parent, string name, Vector3 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(VRCUiShape), typeof(BoxCollider));
        go.layer = InteractionLayer; go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = Vector3.one * .01f;
        var rect = go.GetComponent<RectTransform>(); rect.sizeDelta = size * 100f;
        go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        var collider = go.GetComponent<BoxCollider>(); collider.size = new Vector3(size.x * 100, size.y * 100, 2f); collider.isTrigger = false;
        return go;
    }

    private static CircuitLabButton UiButton(Transform parent, string name, string label, UdonSharpBehaviour target, string eventName, Vector2 position, Vector2 size, int fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(UnityEngine.UI.Button)); go.layer = InteractionLayer; go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        var image = go.GetComponent<Image>(); image.color = new Color(.07f, .19f, .26f); image.raycastTarget = true;
        var ui = go.GetComponent<UnityEngine.UI.Button>(); ui.targetGraphic = image;
        ui.navigation = new Navigation { mode = Navigation.Mode.None };
        var handler = Behaviour<CircuitLabButton>(go); handler.target = target; handler.eventName = eventName; handler.coordinator = _coordinator; handler.uiButton = ui;
        handler.requireControl = target is CircuitLabSimulator || target is CircuitLabProbe || target is CircuitLabLayout || target is CircuitLabHierarchyRow || target is CircuitLabHierarchyTree;
        UnityEventTools.AddStringPersistentListener(ui.onClick, UdonSharpEditorUtility.GetBackingUdonBehaviour(handler).SendCustomEvent, "Press");
        UiText(go.transform, "Label", label, Vector2.zero, size * .94f, fontSize, Color.white);
        return handler;
    }

    private static Text UiText(Transform parent, string name, string label, Vector2 position, Vector2 size, int fontSize, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)); go.layer = InteractionLayer; go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        var text = go.GetComponent<Text>(); text.font = _font; text.fontSize = fontSize; text.text = label; text.alignment = TextAnchor.MiddleCenter; text.color = color; text.raycastTarget = false;
        if (name == "Label") { text.resizeTextForBestFit = true; text.resizeTextMinSize = Mathf.Max(6, fontSize - 3); text.resizeTextMaxSize = fontSize; }
        return text;
    }

    private static VRC_Pickup ConfigurePickup(GameObject go, Transform grip, bool alignToGrip = false)
    {
        go.layer = InteractionLayer;
        var pickup = go.GetComponent<VRCPickup>();
        if (pickup == null) { go.AddComponent<VRCPickup>(); pickup = go.GetComponent<VRCPickup>(); }
        if (pickup == null) throw new InvalidOperationException("Pickup creation failed: " + go.name);
        pickup.version = VRCPickup.Version.Version_1_1; pickup.DisallowTheft = true; pickup.ExactGrip = grip;
        pickup.orientation = alignToGrip ? VRC_Pickup.PickupOrientation.Grip : VRC_Pickup.PickupOrientation.Any;
        pickup.AutoHold = VRC_Pickup.AutoHoldMode.Yes;
        pickup.proximity = PickupDistance; pickup.pickupable = true; pickup.allowManipulationWhenEquipped = true;
        foreach (var collider in go.GetComponents<Collider>()) collider.isTrigger = false;
        return pickup;
    }

    private static CircuitLabMovableTool AddMovableTool(GameObject go, Transform grip)
    {
        var rigidbody = go.GetComponent<Rigidbody>();
        if (rigidbody == null) { go.AddComponent<Rigidbody>(); rigidbody = go.GetComponent<Rigidbody>(); }
        if (rigidbody == null) throw new InvalidOperationException("Rigidbody creation failed: " + go.name);
        rigidbody.useGravity = false; rigidbody.isKinematic = true;
        var tool = Behaviour<CircuitLabMovableTool>(go); tool.pickup = ConfigurePickup(go, grip);
        var sync = go.GetComponent<VRCObjectSync>(); if (sync == null) { go.AddComponent<VRCObjectSync>(); sync = go.GetComponent<VRCObjectSync>(); } tool.objectSync = sync;
        return tool;
    }

    private static void Interaction(UdonSharpBehaviour proxy, string label, float distance)
    {
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy); udon.interactText = label; udon.proximity = distance;
    }

    public static object Status()
    {
        var simulator = Object.FindObjectOfType<CircuitLabSimulator>();
        if (simulator == null) throw new InvalidOperationException("CircuitLab simulator not found.");
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator);
        var modules = Object.FindObjectsOfType<CircuitLabModule>(true).OrderBy(module => module.moduleId).ToArray();
        return new
        {
            scene = simulator.gameObject.scene.path, playing = EditorApplication.isPlaying,
            statusCode = udon.GetProgramVariable("statusCode"), cycleCount = udon.GetProgramVariable("cycleCount"),
            completedVectors = udon.GetProgramVariable("completedVectors"), mismatchCount = udon.GetProgramVariable("mismatchCount"),
            historyFrozen = udon.GetProgramVariable("historyFrozen"),
            modules = modules.Select(module => new { module.moduleId, module.modulePath, visible = module.gameObject.activeInHierarchy, expanded = UdonSharpEditorUtility.GetBackingUdonBehaviour(module).GetProgramVariable("expanded"), position = module.transform.position }).ToArray(),
            signalTargets = Object.FindObjectsOfType<CircuitLabSignalTarget>(true).Length,
            previewCameraEnabled = GameObject.Find("CircuitLabPreviewCamera").GetComponent<Camera>().enabled,
            simHud = simulator.simHudText.text, waveHud = simulator.waveHudText.text,
            shaders = new[] { "CircuitLabSim", "CircuitLabHistory", "CircuitLabWaveform", "CircuitLabDisplay" }.Select(name => { var shader = Shader.Find("VrcCpuWorld/" + name); return new { name, supported = shader != null && shader.isSupported, messages = shader == null ? new string[0] : ShaderUtil.GetShaderMessages(shader).Select(message => message.message).ToArray() }; }).ToArray()
        };
    }

    public static object ValidateAssets()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath) throw new InvalidOperationException("Activate CircuitLab scene.");
        var roots = scene.GetRootGameObjects();
        var modules = roots.SelectMany(root => root.GetComponentsInChildren<CircuitLabModule>(true)).ToArray();
        var targets = roots.SelectMany(root => root.GetComponentsInChildren<CircuitLabSignalTarget>(true)).ToArray();
        var simulator = roots.SelectMany(root => root.GetComponentsInChildren<CircuitLabSimulator>(true)).Single();
        if (modules.Length < 10 || targets.Length < simulator.nodeCount) throw new InvalidOperationException("Incomplete module/pin construction.");
        foreach (var module in modules)
        {
            if (module.GetComponent<VRC_Pickup>() == null || module.GetComponent<Rigidbody>() == null || module.childModules == null || module.allModules == null)
                throw new InvalidOperationException("Incomplete module " + module.modulePath);
            foreach (var renderer in module.interior.GetComponentsInChildren<MeshRenderer>(true))
            { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null && !AssetDatabase.Contains(filter.sharedMesh)) throw new InvalidOperationException("Non-persistent mesh " + renderer.name); }
        }
        foreach (var target in targets)
            if (target.signalIds == null || target.signalIds.Length == 0 || target.probes == null || target.probes.Length != 4) throw new InvalidOperationException("Unwired signal target " + target.name);
        foreach (var texture in new[] { simulator.stateA, simulator.stateB, simulator.historyA, simulator.historyB, simulator.frozenHistory })
            if (texture == null || !AssetDatabase.Contains(texture) || texture.filterMode != FilterMode.Point) throw new InvalidOperationException("Invalid persistent render texture.");
        if (GameObject.Find("CircuitLabPreviewCamera").GetComponent<Camera>().enabled) throw new InvalidOperationException("Preview camera must remain disabled.");
        return new { passed = true, modules = modules.Length, pinTargets = targets.Length, persistentMeshes = AssetDatabase.FindAssets("t:Mesh", new[] { Root + "/Meshes" }).Length, persistentRenderTextures = 5, physicalProbes = 4, moduleMeshDraws = modules.Length * 2 };
    }

    public static async Task<object> ValidateInteractions()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode to test the backing Udon event chain.");
        var modules = Object.FindObjectsOfType<CircuitLabModule>(true).OrderBy(module => module.logicalDepth).ToArray();
        var root = modules.First(module => module.logicalParent == null);
        var child = modules.First(module => module.logicalParent == root);
        var rootUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(root);
        var childUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(child);
        rootUdon.SendCustomEvent("Expand"); childUdon.SendCustomEvent("DetachFromParent");
        var oldPosition = child.transform.position; var oldRotation = child.transform.rotation;
        child.transform.position += new Vector3(.31f, .22f, -.27f); child.transform.Rotate(0, 19, 7);
        var movedPosition = child.transform.position; var movedRotation = child.transform.rotation;
        rootUdon.SendCustomEvent("Collapse"); await Task.Delay(100);
        if (child.gameObject.activeInHierarchy) throw new InvalidOperationException("Extracted descendants must hide with collapsed logical parent.");
        rootUdon.SendCustomEvent("Expand"); await Task.Delay(100);
        if (!child.gameObject.activeInHierarchy || Vector3.Distance(child.transform.position, movedPosition) > .001f || Quaternion.Angle(child.transform.rotation, movedRotation) > .01f)
            throw new InvalidOperationException("Re-expansion must restore extracted placement.");
        var probe = Object.FindObjectsOfType<CircuitLabProbe>().First(probe => probe.channel == 0);
        UdonSharpEditorUtility.GetBackingUdonBehaviour(probe).SendCustomEvent("SelectProbe");
        var target = child.GetComponentsInChildren<CircuitLabSignalTarget>(true).First(target => target.isPort);
        UdonSharpEditorUtility.GetBackingUdonBehaviour(target).Interact(); await Task.Delay(100);
        var probeUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(probe);
        if (probeUdon.GetProgramVariable("boundTarget") == null || Convert.ToInt32(childUdon.GetProgramVariable("boundChannelMask")) != 1)
            throw new InvalidOperationException("Desktop pin -> probe -> simulator binding failed.");
        UdonSharpEditorUtility.GetBackingUdonBehaviour(child).SendCustomEvent("Collapse"); await Task.Delay(100);
        if (probeUdon.GetProgramVariable("boundTarget") == null) throw new InvalidOperationException("Folding lost probe binding.");
        probeUdon.SendCustomEvent("Detach");
        if (probeUdon.GetProgramVariable("boundTarget") != null) throw new InvalidOperationException("Explicit probe detach failed.");
        child.transform.position = oldPosition; child.transform.rotation = oldRotation;
        return new { passed = true, path = "backing Udon events + SignalTarget.Interact -> Probe.BindTarget -> Simulator.BindProbe", extractedPlacementPreserved = true, hiddenDescendants = true, foldedBindingPreserved = true, explicitDetach = true };
    }
}
