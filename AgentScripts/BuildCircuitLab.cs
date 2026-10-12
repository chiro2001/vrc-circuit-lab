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
    private static readonly string[] ProgramNames = { "CircuitLabSimulator", "CircuitLabModule", "CircuitLabProbe", "CircuitLabSignalTarget", "CircuitLabButton", "CircuitLabWire", "CircuitLabLayout", "CircuitLabMovableTool", "CircuitLabPlayerSetup", "CircuitLabHierarchyTree", "CircuitLabHierarchyRow", "CircuitLabNetworkCoordinator", "CircuitLabNetworkClock", "CircuitLabControlRequest", "CircuitLabTerminal", "CircuitLabObjectState", "CircuitLabProgramPanel", "CircuitLabProgramDraft", "CircuitLabStateTablet", "CircuitLabTabletCell", "CircuitLabTabletSharedState", "CircuitLabProgramBrowser", "CircuitLabProgramRow", "CircuitLabIsaManual", "CircuitLabOutputDisplay", "CircuitLabLocalization" };
    private static Font _font;
    private static Material _board, _edge, _textDark, _highlight, _handle;
    private static Material _display;
    private static Transform _workspace;
    private static CircuitLabNetworkCoordinator _coordinator;
    private static CircuitLabLayout _layout;
    private const int InteractionLayer = 17;
    private const float PickupDistance = 10f;
    // 2048 cycles of terminal capture: covers a readback window at 8192 cycles/s.
    private const int TerminalEventCapacity = 2048;
    private sealed class Pose { public Vector3 position, scale; public Quaternion rotation; public bool detached, expanded; }
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

    private static RectTransform SetProgramRect(Transform canvas, string name, Vector2 position, Vector2 size)
    {
        var child = canvas.Find(name);
        if (child == null) throw new InvalidOperationException("Missing panel UI element " + name);
        var rect = child.GetComponent<RectTransform>();
        if (rect == null) throw new InvalidOperationException("Panel UI element has no RectTransform: " + name);
        Undo.RecordObject(rect, "Repair Panel Layout");
        rect.anchoredPosition = position; rect.sizeDelta = size;
        EditorUtility.SetDirty(rect);
        return rect;
    }

    private static Transform SetPanelLocalY(Transform panel, string name, float value)
    {
        var child = panel.Find(name);
        if (child == null) throw new InvalidOperationException("Missing panel layout element " + name);
        Vector3 position = child.localPosition;
        position.y = value;
        Undo.RecordObject(child, "Repair Panel Layout");
        child.localPosition = position;
        EditorUtility.SetDirty(child);
        return child;
    }

    public static object RepairProgramPanelLayout()
    {
        var scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || scene.path != ScenePath)
            throw new InvalidOperationException("Activate the CircuitLab scene in Edit Mode before repairing the ProgramPanel.");
        var world = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "CircuitLabWorld");
        if (world == null) throw new InvalidOperationException("CircuitLabWorld is missing.");
        var panel = world.transform.Find("ProgramPanel");
        var canvas = panel == null ? null : panel.Find("ProgramCanvas");
        if (canvas == null) throw new InvalidOperationException("ProgramPanel/ProgramCanvas is missing.");

        SetProgramRect(canvas, "CurrentProgramTitle", new Vector2(-120, 314), new Vector2(440, 42));
        SetProgramRect(canvas, "REQUEST_PROGRAM_CONTROL", new Vector2(230, 316), new Vector2(220, 25));
        var title = canvas.Find("CurrentProgramTitle").GetComponent<Text>();
        Undo.RecordObject(title, "Repair Program Panel Title");
        title.resizeTextForBestFit = true; title.resizeTextMinSize = 9; title.resizeTextMaxSize = 17;
        title.horizontalOverflow = HorizontalWrapMode.Wrap; title.verticalOverflow = VerticalWrapMode.Truncate;
        EditorUtility.SetDirty(title);
        SetProgramRect(canvas, "CurrentProgramDescription", new Vector2(0, 274), new Vector2(680, 30));
        var description = canvas.Find("CurrentProgramDescription").GetComponent<Text>();
        Undo.RecordObject(description, "Repair Program Description Text");
        description.resizeTextForBestFit = true; description.resizeTextMinSize = 8; description.resizeTextMaxSize = 11;
        description.horizontalOverflow = HorizontalWrapMode.Wrap; description.verticalOverflow = VerticalWrapMode.Truncate;
        EditorUtility.SetDirty(description);

        for (int bit = 0; bit < 12; bit++)
        {
            int row = bit / 6, column = bit % 6;
            SetProgramRect(canvas, "VIRTUAL_BIT_" + (11 - bit), new Vector2(-23 + column * 67, -88 - row * 34), new Vector2(62, 31));
        }
        SetProgramRect(canvas, "VirtualWordPreview", new Vector2(145, -58), new Vector2(410, 22));
        SetProgramRect(canvas, "VIRTUAL_BACKSPACE", new Vector2(-17, -160), new Vector2(84, 24));
        SetProgramRect(canvas, "VIRTUAL_COMMIT", new Vector2(86, -160), new Vector2(114, 24));
        SetProgramRect(canvas, "LOAD_DRAFT", new Vector2(244, -160), new Vector2(200, 24));
        RepairSingleLineButtonLabel(canvas, "VIRTUAL_BACKSPACE", "DELETE LINE", 8);
        RepairSingleLineButtonLabel(canvas, "VIRTUAL_COMMIT", "APPEND WORD", 8);
        RepairSingleLineButtonLabel(canvas, "LOAD_DRAFT", "LOAD DRAFT", 8);
        SetProgramRect(canvas, "ISAHeading", new Vector2(145, -184), new Vector2(410, 20));
        SetProgramRect(canvas, "ISADetails", new Vector2(145, -236), new Vector2(410, 74));

        var tabletCanvas = world.transform.Find("StateTablet/StateTabletCanvas");
        if (tabletCanvas != null)
        {
            SetProgramRect(tabletCanvas, "TabletTitle", new Vector2(-110, 267), new Vector2(420, 24));
            SetProgramRect(tabletCanvas, "TABLET_REQUEST_CONTROL", new Vector2(224, 267), new Vector2(212, 25));
            var tabletTitle = tabletCanvas.Find("TabletTitle").GetComponent<Text>();
            Undo.RecordObject(tabletTitle, "Repair State Tablet Title");
            tabletTitle.resizeTextForBestFit = true; tabletTitle.resizeTextMinSize = 12; tabletTitle.resizeTextMaxSize = 17;
            tabletTitle.horizontalOverflow = HorizontalWrapMode.Overflow; tabletTitle.verticalOverflow = VerticalWrapMode.Truncate;
            EditorUtility.SetDirty(tabletTitle);
        }

        var waveformPanel = world.transform.Find("MovableWaveformPanel");
        if (waveformPanel != null)
        {
            SetPanelLocalY(waveformPanel, "WaveformScreen", 1.09f);
            SetPanelLocalY(waveformPanel, "ControlStatus", 2.10f);
            SetPanelLocalY(waveformPanel, "RequestControl_Canvas", 2.33f);
            SetPanelLocalY(waveformPanel, "GrantControl_Canvas", 2.33f);
            SetPanelLocalY(waveformPanel, "ReleaseControl_Canvas", 2.33f);
            var simulator = world.GetComponentInChildren<CircuitLabSimulator>(true);
            if (simulator != null && simulator.simHudText != null)
            {
                Undo.RecordObject(simulator.simHudText, "Repair Analyzer Status Text");
                simulator.simHudText.resizeTextForBestFit = true;
                simulator.simHudText.resizeTextMinSize = 9; simulator.simHudText.resizeTextMaxSize = 13;
                simulator.simHudText.horizontalOverflow = HorizontalWrapMode.Wrap;
                simulator.simHudText.verticalOverflow = VerticalWrapMode.Truncate;
                RectTransform statusRect = simulator.simHudText.GetComponent<RectTransform>();
                Undo.RecordObject(statusRect, "Repair Analyzer Status Rect");
                statusRect.sizeDelta = new Vector2(520, 48);
                EditorUtility.SetDirty(simulator.simHudText); EditorUtility.SetDirty(statusRect);
            }
        }
        HideLegacyProgramControls(world.transform);
        EditorSceneManager.MarkSceneDirty(scene);
        return new { repaired = true, scene = scene.path, keyboardButtons = 12, draftActions = 3, movedISA = true, analyzerControlsAtTop = waveformPanel != null, titleBestFit = true, descriptionBestFit = true };
    }

    private static object BuildSceneInternal(bool upgrade)
    {
        var scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || scene.path != ScenePath)
            throw new InvalidOperationException("Activate the additive CircuitLab scene in Edit Mode before building; other scenes are preserved.");
        foreach (string folder in new[] { "/Materials", "/Meshes", "/Textures", "/Programs" }) Folder(Root + folder);
        var source = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Data/gpu-netlist.json");
        if (source == null) throw new InvalidOperationException("Generate schema3 gpu-netlist.json first.");
        var data = JObject.Parse(source.text);
        if ((int?)data["schemaVersion"] != 3 && (int?)data["schema"] != 3) throw new InvalidOperationException("Expected schema3 teaching CPU data.");
        int width = (int)data["textureWidth"], count = (int)data["nodeCount"];
        var nodes = ((JArray)data["nodes"]).Cast<JObject>().ToDictionary(node => (int)node["id"]);
        var programData = ((JArray)data["programs"]).Cast<JObject>().ToArray();
        if (programData.Length == 0) throw new InvalidOperationException("Schema3 requires at least one program.");
        int defaultProgramIndex = (int?)data["defaultProgramIndex"] ?? -1;
        string defaultProgramId = data["defaultProgramId"] == null ? null : data["defaultProgramId"].ToString();
        if (defaultProgramIndex < 0 && defaultProgramId != null)
            for (int i = 0; i < programData.Length; i++) if (programData[i]["id"] != null && programData[i]["id"].ToString() == defaultProgramId) { defaultProgramIndex = i; break; }
        if (defaultProgramIndex < 0) defaultProgramIndex = 0;
        if (defaultProgramIndex >= programData.Length) throw new InvalidOperationException("defaultProgramIndex is outside the program catalog.");
        var programWords = new int[programData.Length][];
        var initialRam = new byte[programData.Length][];
        var programNames = new string[programData.Length];
        var programDescriptions = new string[programData.Length];
        var flatProgramWords = new int[programData.Length * 512];
        var flatInitialRam = new byte[programData.Length * 1024];
        for (int program = 0; program < programData.Length; program++)
        {
            programWords[program] = programData[program]["words"].ToObject<int[]>();
            initialRam[program] = programData[program]["initialRam"].ToObject<byte[]>();
            if (programWords[program].Length != 512 || initialRam[program].Length != 1024)
                throw new InvalidOperationException("Program " + program + " must contain 512 words and 1024 initial RAM bytes.");
            for (int word = 0; word < 512; word++)
            {
                if (programWords[program][word] < 0 || programWords[program][word] > 0xFFF)
                    throw new InvalidOperationException("Program word outside 12 bits: id=" + program + " address=" + word);
                flatProgramWords[program * 512 + word] = programWords[program][word];
            }
            for (int address = 0; address < 1024; address++) flatInitialRam[program * 1024 + address] = initialRam[program][address];
            programNames[program] = programData[program]["nameTranslations"] is JArray localizedNames ? (string)localizedNames[0] : (string)programData[program]["name"] ?? ("Program " + program);
            programDescriptions[program] = programData[program]["descriptionTranslations"] is JArray localizedDescriptions ? (string)localizedDescriptions[0] : (string)programData[program]["description"] ?? "";
            string lowerName = programNames[program].ToLowerInvariant();
            string lowerDescription = programDescriptions[program].ToLowerInvariant();
            bool statesFirstSix = lowerDescription.Contains("first six") || lowerDescription.Contains("前六") || lowerDescription.Contains("前 6");
            bool statesSeventh = lowerDescription.Contains("seventh") || lowerDescription.Contains("第七") || lowerDescription.Contains("第 7");
            if (lowerName.Contains("355")
                && (!statesFirstSix || !statesSeventh))
                programDescriptions[program] += " 【近似】355/113 长除法：小数前六位与 π 一致，第七位起不同。";
        }
        var memoryPorts = (JObject)data["memoryPorts"];
        var previous = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "CircuitLabWorld");
        if (upgrade && previous == null) throw new InvalidOperationException("Upgrade requires an existing CircuitLabWorld; environment is never recreated.");
        if (!upgrade && previous != null) throw new InvalidOperationException("Existing worlds must use UpgradeScene to preserve the environment.");
        var previousPoses = previous == null ? new Dictionary<string, Pose>() : previous.GetComponentsInChildren<CircuitLabModule>(true).ToDictionary(module => module.modulePath, module => new Pose { position = module.transform.position, rotation = module.transform.rotation, scale = module.transform.localScale, detached = module.isDetached, expanded = module.expanded });
        var previousTools = new Dictionary<string, Pose>();
        var preservedWorkspaceChildren = new List<Transform>();
        if (upgrade)
        {
            foreach (string name in new[] { "MovableWaveformPanel", "ProbeAndHierarchyRack", "HierarchyPanel", "ANSISymbolGuide", "TerminalPanel", "ProgramPanel", "StateTablet", "AboutPanel", "ProgramBrowserPanel" })
            {
                var owned = previous.transform.Find(name);
                if (owned == null) continue;
                previousTools[name] = new Pose { position = owned.position, rotation = owned.rotation, scale = owned.localScale };
                if (name == "AboutPanel" || name == "ProgramBrowserPanel") continue;
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
        _handle = Material("PickupHandles", "VrcCpuWorld/CircuitLabHandle", new Color(.12f, .65f, .82f, .48f));
        _display = Material("Signals", "VrcCpuWorld/CircuitLabDisplay", Color.white);
        _display.SetFloat("_TextureWidth", width); _display.SetFloat("_SignalOverride", -1);
        var simulation = Material("Simulation", "VrcCpuWorld/CircuitLabSim", Color.white);
        var history = Material("History", "VrcCpuWorld/CircuitLabHistory", Color.white);
        var wave = Material("Waveform", "VrcCpuWorld/CircuitLabWaveform", Color.white);
        var tabletMaterial = Material("TabletDisplay", "VrcCpuWorld/CircuitLabTabletDisplay", Color.white);
        var ramMaterial = Material("RAM", "VrcCpuWorld/CircuitLabRam", Color.white);
        var gates = Asset(Root + "/Textures/GateInstructions.asset", () => new Texture2D(width, 1, TextureFormat.RGBAFloat, false, true));
        if (gates.width != width) gates.Reinitialize(width, 1, TextureFormat.RGBAFloat, false);
        gates.filterMode = FilterMode.Point; gates.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[width];
        foreach (var node in nodes.Values) pixels[(int)node["id"]] = new Color((int)node["op"], (int)node["a"], (int)node["b"], (int)node["c"]);
        gates.SetPixels(pixels); gates.Apply(false, false); EditorUtility.SetDirty(gates);
        var programTexture = Texture2DAsset("ProgramROM", 512, 1, TextureFormat.RGBA32);
        var programPixels = new Color32[512];
        for (int address = 0; address < 512; address++)
        {
            int word = programWords[defaultProgramIndex][address];
            programPixels[address] = new Color32((byte)(word & 255), (byte)((word >> 8) & 15), 0, 255);
        }
        programTexture.SetPixels32(programPixels); programTexture.Apply(false, false); EditorUtility.SetDirty(programTexture);
        var ramInitialTexture = Texture2DAsset("RAMInitial", 32, 32, TextureFormat.RGBA32);
        var ramInitialPixels = new Color32[1024];
        var restoreRamPixels = new Color32[1024];
        for (int address = 0; address < 1024; address++)
        {
            ramInitialPixels[address] = new Color32(initialRam[defaultProgramIndex][address], 0, 0, 255);
            restoreRamPixels[address] = new Color32(0, 0, 0, 255);
        }
        ramInitialTexture.SetPixels32(ramInitialPixels); ramInitialTexture.Apply(false, false); EditorUtility.SetDirty(ramInitialTexture);
        var restoreRamTexture = Texture2DAsset("RAMRestore", 32, 32, TextureFormat.RGBA32);
        restoreRamTexture.SetPixels32(restoreRamPixels); restoreRamTexture.Apply(false, false); EditorUtility.SetDirty(restoreRamTexture);
        var restoreStateTexture = Texture2DAsset("DffRestoreState", width, 1, TextureFormat.RGBA32);
        var stateRestorePixels = new Color32[width];
        for (int i = 0; i < width; i++) stateRestorePixels[i] = new Color32(0, 0, 0, 255);
        restoreStateTexture.SetPixels32(stateRestorePixels); restoreStateTexture.Apply(false, false); EditorUtility.SetDirty(restoreStateTexture);
        var stateA = RenderTexture("StateA", width, 1, RenderTextureFormat.ARGB32);
        var stateB = RenderTexture("StateB", width, 1, RenderTextureFormat.ARGB32);
        var historyA = RenderTexture("HistoryA", 2048, 4, RenderTextureFormat.ARGBFloat);
        var historyB = RenderTexture("HistoryB", 2048, 4, RenderTextureFormat.ARGBFloat);
        var frozen = RenderTexture("FrozenHistory", 2048, 4, RenderTextureFormat.ARGBFloat);
        var ramA = RenderTexture("RamA", 32, 32, RenderTextureFormat.ARGB32);
        var ramB = RenderTexture("RamB", 32, 32, RenderTextureFormat.ARGB32);
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
        simulator.ramMaterial = ramMaterial; simulator.programTexture = programTexture; simulator.ramInitialTexture = ramInitialTexture;
        simulator.restoreRamTexture = restoreRamTexture; simulator.restoreStateTexture = restoreStateTexture;
        simulator.ramA = ramA; simulator.ramB = ramB; simulator.currentRam = ramA;
        simulator.programCount = programData.Length; simulator.presetProgramWords = flatProgramWords; simulator.presetInitialRam = flatInitialRam;
        simulator.programNames = programNames; simulator.programDescriptions = programDescriptions;
        simulator.programWords = programWords[defaultProgramIndex]; simulator.initialRam = initialRam[defaultProgramIndex]; simulator.activeProgramName = programNames[defaultProgramIndex]; simulator.programSelect = defaultProgramIndex; simulator.programRevision = 1;
        simulator.romAddressSignals = memoryPorts["romAddress"].ToObject<int[]>();
        simulator.ramReadAddressSignals = memoryPorts["ramReadAddress"].ToObject<int[]>();
        simulator.ramReadDataSignals = memoryPorts["ramReadData"].ToObject<int[]>();
        simulator.ramWriteAddressSignals = memoryPorts["ramWriteAddress"].ToObject<int[]>();
        simulator.ramWriteDataSignals = memoryPorts["ramWriteData"].ToObject<int[]>();
        simulator.ramWriteEnableSignal = (int)memoryPorts["ramWriteEnable"];
        simulator.fixturePrograms = data["fixturePrograms"] == null ? new int[0] : data["fixturePrograms"].ToObject<int[]>();
        simulator.fixtureRamReset = data["fixtureRamReset"] == null ? new bool[0] : data["fixtureRamReset"].ToObject<bool[]>();
        simulator.nodeCount = count; simulator.textureWidth = width; simulator.settlePasses = (int)data["settlePasses"];
        simulator.clockSettlePasses = (int?)data["clockSettlePasses"] ?? 2;
        simulator.inputMasks = data["inputMasks"] == null ? new int[0] : data["inputMasks"].ToObject<int[]>();
        simulator.expectedStates = data["expectedStates"] == null ? new byte[0] : data["expectedStates"].ToObject<byte[]>();
        simulator.vectorCount = (int?)data["vectorCount"] ?? 0;
        simulator.accSignals = data["ports"]["acc"].ToObject<int[]>();
        simulator.pcSignals = data["ports"]["pc"].ToObject<int[]>();
        simulator.outSignals = data["ports"]["out"].ToObject<int[]>();
        simulator.instructionSignals = data["ports"]["instruction"].ToObject<int[]>();
        simulator.xSignals = data["ports"]["x"] == null ? new int[0] : data["ports"]["x"].ToObject<int[]>();
        simulator.bankSignals = data["ports"]["bank"] == null ? new int[0] : data["ports"]["bank"].ToObject<int[]>();
        simulator.spSignals = data["ports"]["sp"] == null ? new int[0] : data["ports"]["sp"].ToObject<int[]>();
        simulator.phaseSignals = data["ports"]["phaseSignals"] == null ? data["ports"]["phase"].ToObject<int[]>() : data["ports"]["phaseSignals"].ToObject<int[]>();
        simulator.phaseSignal = simulator.phaseSignals.Length > 0 ? simulator.phaseSignals[0] : -1;
        simulator.zeroSignal = (int)data["ports"]["zero"][0];
        simulator.carrySignal = (int)data["ports"]["carry"][0];
        simulator.haltSignal = (int)data["ports"]["halt"][0];
        simulator.dffSignals = nodes.Values.Where(node => (int)node["op"] == 3).Select(node => (int)node["id"]).OrderBy(id => id).ToArray();
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
        foreach (var module in allModules) { module.expanded = upgrade && previousPoses.TryGetValue(module.modulePath, out var existingPose) ? existingPose.expanded : module.childModules.Length > 0; module.RefreshVisibility(); module.EnsureAboveFloor(); }
        var rootModule = allModules.First(module => module.logicalParent == null);
        if (!upgrade) { rootModule.transform.position = new Vector3(-3.85f, 1.8f, .5f); rootModule.transform.rotation = Quaternion.identity; rootModule.transform.localScale = Vector3.one * .65f; } rootModule.EnsureAboveFloor();
        foreach (var module in allModules) { module.CaptureInitialPose(); module.sharedState.expanded = module.expanded; module.sharedState.detached = module.isDetached; }
        GateReference(world.transform, nodes);
        var wireMaterials = BuildExternalWires(ordered, nodes, width, stateA);
        simulator.displayMaterials = (new[] { _display }).Concat(wireMaterials).Distinct().ToArray();
        var probes = BuildPanel(world.transform, simulator, wave, allModules);
        BuildTerminal(world.transform, simulator, data);
        var programPanel = BuildProgramPanel(world.transform, simulator, data, programData, programNames, programDescriptions, defaultProgramIndex);
        var stateTablet = BuildStateTablet(world.transform, simulator, data, nodes, tabletMaterial);
        var programBrowser = BuildPresentationPanels(world.transform, simulator, programData, programNames, programDescriptions, programPanel, programPanel.sharedDraft, defaultProgramIndex);
        var waveformPanel = world.transform.Find("MovableWaveformPanel");
        programPanel.analyzerProgramTitle = WorldText(waveformPanel, "WaveProgramName", "PROGRAM", new Vector3(0, 2.53f, -.14f), new Vector2(5.2f, .16f), 12, new Color(.76f, .91f, .78f));
        for (int i = 0; i < Targets.Count; i++) { Targets[i].targetIndex = i; Targets[i].probes = probes; }
        CircuitLabSignalTarget[] signalTargets = Targets.ToArray();
        foreach (var probe in probes) { probe.probes = probes; probe.signalTargets = signalTargets; probe.coordinator = _coordinator; }
        programPanel.coordinator = _coordinator; programPanel.simulator = simulator;
        stateTablet.coordinator = _coordinator; stateTablet.simulator = simulator; stateTablet.probes = probes; stateTablet.signalTargets = signalTargets;
        stateTablet.ApplySharedRamTargets();
        programBrowser.coordinator = _coordinator; programBrowser.simulator = simulator; programBrowser.editorPanel = programPanel;
        programPanel.sharedDraft.browser = programBrowser; programBrowser.sharedState = programPanel.sharedDraft;
        programBrowser.selectedProgram = Mathf.Clamp(programBrowser.selectedProgram, 0, programData.Length - 1);
        programPanel.selectedPreset = programBrowser.selectedProgram;
        programPanel.sharedDraft.selectedPreset = programBrowser.selectedProgram;
        programPanel.sharedDraft.browsePage = programBrowser.browsePage;
        programPanel.sharedDraft.browseMode = programBrowser.browseMode;
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
        _layout.tools = world.GetComponentsInChildren<CircuitLabMovableTool>(true);
        HideLegacyProgramControls(world.transform);
        foreach (var tool in _layout.tools)
        {
            tool.coordinator = _coordinator;
            if (!Behaviours.Contains(tool)) Behaviours.Add(tool);
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
        return new { upgraded = upgrade, saved = !upgrade, preservedEnvironment = upgrade, scene = scene.path, modules = views.Count, nodes = count, programs = programData.Length, dffs = simulator.dffSignals.Length, signalTargets = Targets.Count, udonBehaviours = Behaviours.Count, externalWires = Object.FindObjectsOfType<CircuitLabWire>(true).Length, shadersSupported = new[] { simulation, history, wave, _display, tabletMaterial, ramMaterial }.All(material => material.shader.isSupported), previewCameraEnabled = false };
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

    private static Texture2D Texture2DAsset(string name, int width, int height, TextureFormat format)
    {
        var texture = Asset(Root + "/Textures/" + name + ".asset", () => new Texture2D(width, height, format, false, true));
        if (texture.width != width || texture.height != height || texture.format != format) texture.Reinitialize(width, height, format, false);
        texture.name = name; texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
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
        module.grabVisual = Primitive(module.handle, "GrabBar", PrimitiveType.Cube, Vector3.zero, new Vector3(.12f, .62f, .14f), _handle, false).transform;
        module.pickupCollider = go.AddComponent<BoxCollider>(); module.pickupCollider.size = new Vector3(.12f, .62f, .14f);
        module.grabText = null;
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
        var memoryHints = new HashSet<int>();
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
            if ((op == 15 || op == 16) && memoryHints.Add(op))
                WorldText(module.interior.transform, "MemoryAddressHint_" + signal, op == 15 ? "ROM  /  9-BIT ADDRESS" : "RAM  /  8-BIT ADDRESS", position + new Vector3(0, -.09f * view.gateScale, -.02f), new Vector2(.62f, .12f), 7, new Color(.72f, .86f, .94f));
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

    private static List<Material> BuildExternalWires(ModuleView[] views, Dictionary<int, JObject> nodes, int textureWidth, RenderTexture stateTexture)
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
                // Authored binding: a finished cycle always leaves the live state in
                // StateA, so the runtime never writes these shared assets.
                material.SetTexture("_StateTex", stateTexture);
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
        var screen = Primitive(panel.transform, "WaveformScreen", PrimitiveType.Quad, new Vector3(0, 1.09f, -.10f), new Vector3(5.05f, 1.85f, 1), waveform, false);
        simulator.waveHudText = WorldText(panel.transform, "WaveHud", "LIVE  /  FOLLOWING LATEST  /  FREEZE LOCKS THE VIEW", new Vector3(0, -.06f, -.14f), new Vector2(5.2f, .38f), 14, new Color(.6f, .83f, .9f));
        simulator.simHudText = WorldText(panel.transform, "SimulationHud", "PAUSED  /  STEP TO EXECUTE ONE CLOCK", new Vector3(0, -1.52f, -.14f), new Vector2(5.2f, .48f), 13, new Color(.68f, .90f, .82f));
        simulator.simHudText.resizeTextForBestFit = true; simulator.simHudText.resizeTextMinSize = 9; simulator.simHudText.resizeTextMaxSize = 13;
        simulator.simHudText.horizontalOverflow = HorizontalWrapMode.Wrap; simulator.simHudText.verticalOverflow = VerticalWrapMode.Truncate;
        simulator.statusText = WorldText(panel.transform, "TestStatus", "SELF TEST READY", new Vector3(0, -2.52f, -.14f), new Vector2(5.3f, .18f), 12, new Color(.55f, .78f, .86f));
        Controls(panel.transform, simulator, new[] { "FreezeWaveform", "ResumeWaveform", "ZoomIn", "ZoomOut", "PanLeft", "PanRight", "FollowLatest" }, new[] { "FREEZE", "LIVE", "ZOOM +", "ZOOM -", "< PAN", "PAN >", "LATEST" }, -.45f, 5.05f);
        Controls(panel.transform, simulator, new[] { "MoveCursorALeft", "MoveCursorARight", "MoveCursorBLeft", "MoveCursorBRight" }, new[] { "CURSOR A <", "A >", "CURSOR B <", "B >" }, -.78f, 5.05f);
        Controls(panel.transform, simulator, new[] { "CycleBitView", "CycleNumberFormat", "ToggleAA", "EdgeWidthDown", "EdgeWidthUp" }, new[] { "BUS / BITS", "HEX / BIN / DEC", "SMOOTH", "EDGE -", "EDGE +" }, -1.11f, 5.05f);
        Controls(panel.transform, simulator, new[] { "Run", "Pause", "Step", "SpeedDown", "SpeedUp", "ResetCircuit" }, new[] { "RUN", "PAUSE", "STEP", "SLOWER", "FASTER", "RESET" }, -1.94f, 5.05f);
        Controls(panel.transform, simulator, new[] { "RestartExperiment", "RunSelfTest" }, new[] { "RESTART", "SELF TEST" }, -2.28f, 5.05f);
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
        _coordinator.statusText = WorldText(panel.transform, "ControlStatus", "CONTROL: UNCLAIMED | CLICK A CONTROL TO BEGIN", new Vector3(0, 2.10f, -.14f), new Vector2(5.2f, .15f), 11, new Color(.65f, .90f, .8f));
        Controls(panel.transform, _coordinator, new[] { "RequestControl", "GrantControl", "ReleaseControl" }, new[] { "REQUEST CONTROL", "GRANT REQUEST", "RELEASE CONTROL" }, 2.33f, 5.05f);
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
        // The capture ring must cover one readback window even at the maximum
        // simulation rate, otherwise high-speed runs report a capture gap.
        terminal.eventA = RenderTexture("TerminalEventsA", TerminalEventCapacity, 2, RenderTextureFormat.ARGB32);
        terminal.eventB = RenderTexture("TerminalEventsB", TerminalEventCapacity, 2, RenderTextureFormat.ARGB32);
        terminal.eventCapacity = TerminalEventCapacity;
        terminal.readbackInterval = .05f;
        terminal.outputWriteSignal = (int)data["ports"]["terminal_write"][0]; terminal.outputSignals = data["ports"]["terminal_data"].ToObject<int[]>(); terminal.sequenceSignals = data["ports"]["terminal_sequence"].ToObject<int[]>();
        Button(panel.transform, "CLEAR_TERMINAL", "CLEAR TERMINAL", simulator, "ClearTerminal", new Vector3(0, -1.44f, -.14f), new Vector2(3.15f, .23f), 12);
    }

    private static GameObject MovablePanel(Transform world, string name, Vector3 position, Vector3 euler, float width, float height)
    {
        var panel = Child(world, name); panel.layer = InteractionLayer; panel.transform.position = position; panel.transform.eulerAngles = euler;
        Backing(panel.transform, width, height);
        var handle = Primitive(panel.transform, "PanelGrabHandle", PrimitiveType.Cube, new Vector3(0, -height * .5f + .13f, 0), new Vector3(1.45f, .17f, .22f), _handle, false);
        var collider = panel.AddComponent<BoxCollider>(); collider.center = handle.transform.localPosition; collider.size = new Vector3(1.45f, .17f, .22f);
        WorldText(panel.transform, "PanelGrabHint", "GRAB PANEL", handle.transform.localPosition + new Vector3(0, 0, -.12f), new Vector2(1.3f, .13f), 11, Color.white);
        var body = panel.AddComponent<Rigidbody>(); body.useGravity = false; body.isKinematic = true;
        AddMovableTool(panel, handle.transform);
        return panel;
    }

    private static GameObject PanelCanvas(Transform panel, string name, float width, float height)
    {
        return CreateCanvas(panel, name, new Vector3(0, 0, -.105f), new Vector2(width, height));
    }

    private static Text PanelText(Transform parent, string name, string value, Vector2 position, Vector2 size, int fontSize, Color color, TextAnchor alignment = TextAnchor.MiddleCenter)
    {
        var text = UiText(parent, name, value, position, size, fontSize, color);
        text.alignment = alignment; text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static CircuitLabButton PanelButton(Transform canvas, string name, string label, UdonSharpBehaviour target, string eventName, Vector2 position, Vector2 size, int fontSize)
    {
        return UiButton(canvas, name, label, target, eventName, position, size, fontSize);
    }

    private static void SetSingleLineButtonLabel(CircuitLabButton button, int fontSize)
    {
        var label = button == null ? null : button.GetComponentInChildren<Text>();
        if (label == null) throw new InvalidOperationException("Button is missing its label.");
        label.resizeTextForBestFit = false;
        label.fontSize = fontSize;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.supportRichText = false;
        EditorUtility.SetDirty(label);
    }

    private static void RepairSingleLineButtonLabel(Transform canvas, string buttonName, string labelText, int fontSize)
    {
        var labelTransform = canvas.Find(buttonName + "/Label");
        if (labelTransform == null) throw new InvalidOperationException("Missing button label " + buttonName);
        var label = labelTransform.GetComponent<Text>();
        if (label == null) throw new InvalidOperationException("Button label has no Text component: " + buttonName);
        Undo.RecordObject(label, "Repair Program Button Label");
        label.text = labelText;
        label.resizeTextForBestFit = false;
        label.fontSize = fontSize;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.supportRichText = false;
        EditorUtility.SetDirty(label);
    }

    private static InputField PanelInputField(Transform canvas, string name, string placeholderValue, Vector2 position, Vector2 size, out Text contentText)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
        go.layer = InteractionLayer; go.transform.SetParent(canvas, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        var image = go.GetComponent<Image>(); image.color = new Color(.018f, .038f, .055f, .96f); image.raycastTarget = true;
        var viewportObject = new GameObject("TextViewport", typeof(RectTransform), typeof(RectMask2D));
        viewportObject.layer = InteractionLayer; viewportObject.transform.SetParent(go.transform, false);
        var viewportRect = viewportObject.GetComponent<RectTransform>(); viewportRect.anchorMin = Vector2.zero; viewportRect.anchorMax = Vector2.one; viewportRect.pivot = new Vector2(.5f, .5f); viewportRect.offsetMin = new Vector2(8, 7); viewportRect.offsetMax = new Vector2(-8, -7);
        var contentObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        contentObject.layer = InteractionLayer; contentObject.transform.SetParent(viewportObject.transform, false);
        var contentRect = contentObject.GetComponent<RectTransform>(); contentRect.anchorMin = Vector2.zero; contentRect.anchorMax = Vector2.one; contentRect.pivot = new Vector2(.5f, .5f); contentRect.offsetMin = Vector2.zero; contentRect.offsetMax = Vector2.zero;
        contentText = contentObject.GetComponent<Text>(); contentText.font = _font; contentText.fontSize = 12; contentText.color = new Color(.78f, .91f, .92f); contentText.alignment = TextAnchor.UpperLeft; contentText.horizontalOverflow = HorizontalWrapMode.Wrap; contentText.verticalOverflow = VerticalWrapMode.Overflow; contentText.supportRichText = false; contentText.raycastTarget = false;
        var placeholderObject = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        placeholderObject.layer = InteractionLayer; placeholderObject.transform.SetParent(viewportObject.transform, false);
        var placeholderRect = placeholderObject.GetComponent<RectTransform>(); placeholderRect.anchorMin = Vector2.zero; placeholderRect.anchorMax = Vector2.one; placeholderRect.pivot = new Vector2(.5f, .5f); placeholderRect.offsetMin = new Vector2(8, 7); placeholderRect.offsetMax = new Vector2(-8, -7);
        var placeholder = placeholderObject.GetComponent<Text>(); placeholder.font = _font; placeholder.fontSize = 11; placeholder.color = new Color(.38f, .55f, .62f); placeholder.alignment = TextAnchor.UpperLeft; placeholder.horizontalOverflow = HorizontalWrapMode.Wrap; placeholder.verticalOverflow = VerticalWrapMode.Overflow; placeholder.text = placeholderValue; placeholder.raycastTarget = false;
        var field = go.GetComponent<InputField>(); field.textComponent = contentText; field.placeholder = placeholder; field.targetGraphic = image; field.lineType = InputField.LineType.MultiLineNewline; field.contentType = InputField.ContentType.Standard; field.characterLimit = 8192; field.caretColor = Color.white; field.selectionColor = new Color(.18f, .58f, .68f, .65f); field.interactable = true; field.navigation = new Navigation { mode = Navigation.Mode.None };
        return field;
    }

    private static void DirectCellButton(Transform canvas, string name, string label, Vector2 position, Vector2 size, CircuitLabStateTablet tablet, int slot, bool ramAddressCell, out GameObject cellObject, out Text cellText)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(UnityEngine.UI.Button));
        go.layer = InteractionLayer; go.transform.SetParent(canvas, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        var image = go.GetComponent<Image>(); image.color = new Color(.02f, .05f, .07f, .05f); image.raycastTarget = true;
        var ui = go.GetComponent<UnityEngine.UI.Button>(); ui.targetGraphic = image; ui.navigation = new Navigation { mode = Navigation.Mode.None };
        var cell = Behaviour<CircuitLabTabletCell>(go); cell.tablet = tablet; cell.slot = slot; cell.ramAddressCell = ramAddressCell;
        UnityEventTools.AddStringPersistentListener(ui.onClick, UdonSharpEditorUtility.GetBackingUdonBehaviour(cell).SendCustomEvent, "Select");
        cellText = UiText(go.transform, "Label", label, Vector2.zero, size * .94f, 8, Color.white);
        cellText.resizeTextForBestFit = true; cellText.resizeTextMinSize = 5; cellText.resizeTextMaxSize = 8;
        cellObject = go;
    }

    private static CircuitLabProgramPanel BuildProgramPanel(Transform world, CircuitLabSimulator simulator, JObject data, JObject[] programs, string[] names, string[] descriptions, int defaultPreset)
    {
        var panel = MovablePanel(world, "ProgramPanel", new Vector3(-12f, 3.7f, -.4f), new Vector3(0, -60f, 0), 7.2f, 6.8f);
        var canvas = PanelCanvas(panel.transform, "ProgramCanvas", 7.2f, 6.8f);
        var program = Behaviour<CircuitLabProgramPanel>(panel);
        program.simulator = simulator; program.coordinator = _coordinator; program.pageSize = 8;
        program.programNames = names; program.programDescriptions = descriptions; program.programWordCount = 512;
        program.expectedText = new string[programs.Length]; program.expectedCycles = new int[programs.Length];
        for (int index = 0; index < programs.Length; index++)
        {
            program.expectedText[index] = (string)programs[index]["expectedText"] ?? "";
            program.expectedCycles[index] = (int?)programs[index]["expectedCycles"] ?? 0;
        }
        program.opcodeValues = new int[16]; program.opcodeNames = new string[16];
        for (int i = 0; i < 16; i++) { program.opcodeValues[i] = i; program.opcodeNames[i] = "OP" + i; }
        program.extensionNames = new string[16];
        JObject isa = data["isa"] as JObject;
        var isaLines = new List<string>();
        if (isa != null && isa["opcodes"] is JArray opcodeData)
            foreach (JObject opcode in opcodeData.Cast<JObject>())
            {
                int code = IsaCode(opcode["code"]);
                string mnemonic = (string)opcode["mnemonic"] ?? "OP";
                string format = (string)opcode["format"] ?? "";
                string label = code >= 0xE0 ? "E." + (code & 15).ToString("X") : code.ToString("X");
                isaLines.Add(label + " " + mnemonic + " " + format);
                if (code >= 0xE0 && code <= 0xEF) program.extensionNames[code & 15] = mnemonic;
                else if (code >= 0 && code <= 15) program.opcodeNames[code] = mnemonic;
            }
        if (isa != null && isa["extensions"] is JArray extensionData)
            foreach (JObject extension in extensionData.Cast<JObject>())
            {
                int subopcode = (int)extension["subopcode"];
                if (subopcode < 0 || subopcode >= program.extensionNames.Length) continue;
                string mnemonic = (string)extension["mnemonic"] ?? "EXT";
                if (mnemonic == "BANK") continue; // E20 stays TAX; BANK is E21..E24.
                string label = "E." + subopcode.ToString("X");
                program.extensionNames[subopcode] = mnemonic;
                isaLines.Add(label + " " + mnemonic);
            }
        if (isa != null)
        {
            isaLines.Insert(0, "12-bit instructions  /  9-bit PC  /  512 ROM words  /  1024 RAM bytes  /  ACC,X,SP = 8 bits");
            if (isa["registers"] is JObject registers)
            {
                string registerLine = "REGISTERS  ";
                foreach (var register in registers.Properties()) registerLine += register.Name.ToUpperInvariant() + "=" + register.Value + "  ";
                isaLines.Insert(1, registerLine);
            }
        }
        for (int code = 0; code < 16; code++) program.opcodeValues[code] = code;

        program.programCurrentTitle = PanelText(canvas.transform, "CurrentProgramTitle", "CURRENT PROGRAM", new Vector2(-120, 314), new Vector2(440, 42), 17, Color.white);
        program.programCurrentTitle.resizeTextForBestFit = true; program.programCurrentTitle.resizeTextMinSize = 9; program.programCurrentTitle.resizeTextMaxSize = 17;
        program.programCurrentTitle.horizontalOverflow = HorizontalWrapMode.Wrap; program.programCurrentTitle.verticalOverflow = VerticalWrapMode.Truncate;
        program.currentDescription = PanelText(canvas.transform, "CurrentProgramDescription", "", new Vector2(0, 274), new Vector2(680, 30), 11, new Color(.63f, .83f, .87f));
        program.currentDescription.resizeTextForBestFit = true; program.currentDescription.resizeTextMinSize = 8; program.currentDescription.resizeTextMaxSize = 11;
        program.currentDescription.horizontalOverflow = HorizontalWrapMode.Wrap; program.currentDescription.verticalOverflow = VerticalWrapMode.Truncate;
        PanelButton(canvas.transform, "REQUEST_PROGRAM_CONTROL", "REQUEST / CLAIM CONTROL", program, "ClaimControl", new Vector2(230, 316), new Vector2(220, 25), 10);
        PanelText(canvas.transform, "DraftTitle", "SHARED DRAFT  /  ONE 12-BIT WORD PER LINE", new Vector2(-207, 246), new Vector2(284, 26), 12, new Color(.66f, .88f, .88f));
        program.draftInput = PanelInputField(canvas.transform, "DraftInput", "HEX or BIN, one word per line\n0x prefix accepted; #, ; and // comments\nEmpty lines are ignored. Capacity: 512 words.", new Vector2(-207, 46), new Vector2(284, 362), out Text draftContent);
        program.editorStatus = PanelText(canvas.transform, "EditorStatus", "DRAFT READY  /  RUNNING PROGRAM SHOWN AT RIGHT", new Vector2(-207, -151), new Vector2(284, 44), 10, new Color(.75f, .84f, .78f), TextAnchor.MiddleLeft);
        program.editorStatus.fontSize = 8;
        PanelText(canvas.transform, "DraftHint", "DRAFT IS SEPARATE FROM CURRENT LOADED ROM", new Vector2(-207, -192), new Vector2(284, 20), 9, new Color(.58f, .77f, .82f));
        PanelButton(canvas.transform, "INPUT_HEX", "HEX", program, "SetInputHex", new Vector2(-281, -253), new Vector2(66, 27), 10);
        PanelButton(canvas.transform, "INPUT_BINARY", "BIN", program, "SetInputBinary", new Vector2(-207, -253), new Vector2(66, 27), 10);

        PanelText(canvas.transform, "LoadedProgramTitle", "LOADED ROM  /  HEX · BIN · DISASSEMBLY", new Vector2(145, 246), new Vector2(410, 24), 12, new Color(.66f, .88f, .88f));
        program.listingText = PanelText(canvas.transform, "ProgramListingHeader", "ROM VIEW", new Vector2(145, 222), new Vector2(410, 19), 10, new Color(.64f, .80f, .86f));
        program.instructionRows = new Text[8];
        for (int row = 0; row < program.instructionRows.Length; row++)
        {
            var line = PanelText(canvas.transform, "ProgramWord_" + row, "", new Vector2(145, 192 - row * 24), new Vector2(410, 23), 9, new Color(.83f, .91f, .89f), TextAnchor.MiddleLeft);
            line.horizontalOverflow = HorizontalWrapMode.Overflow; program.instructionRows[row] = line;
        }
        PanelButton(canvas.transform, "PROGRAM_PAGE_PREVIOUS", "< PAGE", program, "PreviousPage", new Vector2(26, -18), new Vector2(115, 27), 10);
        PanelButton(canvas.transform, "PROGRAM_PAGE_PC", "GO TO PC", program, "GoToPcPage", new Vector2(145, -18), new Vector2(115, 27), 10);
        PanelButton(canvas.transform, "PROGRAM_PAGE_NEXT", "PAGE >", program, "NextPage", new Vector2(264, -18), new Vector2(115, 27), 10);
        program.virtualWordText = PanelText(canvas.transform, "VirtualWordPreview", "WORD 0x000  /  000000000000", new Vector2(145, -58), new Vector2(410, 22), 11, new Color(.78f, .90f, .75f));
        program.bitTexts = new Text[12];
        for (int bit = 0; bit < 12; bit++)
        {
            int column = bit % 6;
            int row = bit / 6;
            var button = PanelButton(canvas.transform, "VIRTUAL_BIT_" + (11 - bit), "B" + (11 - bit) + " 0", program, "InsertBit" + (11 - bit), new Vector2(-23 + column * 67, -88 - row * 34), new Vector2(62, 31), 8);
            program.bitTexts[bit] = button.GetComponentInChildren<Text>();
        }
        SetSingleLineButtonLabel(PanelButton(canvas.transform, "VIRTUAL_BACKSPACE", "DELETE LINE", program, "BackspaceBit", new Vector2(-17, -160), new Vector2(84, 24), 8), 8);
        SetSingleLineButtonLabel(PanelButton(canvas.transform, "VIRTUAL_COMMIT", "APPEND WORD", program, "CommitWord", new Vector2(86, -160), new Vector2(114, 24), 8), 8);
        SetSingleLineButtonLabel(PanelButton(canvas.transform, "LOAD_DRAFT", "LOAD DRAFT", program, "LoadDraft", new Vector2(244, -160), new Vector2(200, 24), 8), 8);
        PanelText(canvas.transform, "ISAHeading", "INSTRUCTIONS → ISA MANUAL PANEL", new Vector2(145, -210), new Vector2(410, 28), 13, new Color(.66f, .88f, .88f));
        program.isaText = null;
        var draftState = Behaviour<CircuitLabProgramDraft>(Child(panel.transform, "SharedDraftState"));
        draftState.panel = program; draftState.coordinator = _coordinator; program.sharedDraft = draftState;
        program.programNamesEn = ProgramTranslations(programs, "nameTranslations", 0, "name");
        program.programNamesZh = ProgramTranslations(programs, "nameTranslations", 1, "name");
        program.programNamesJa = ProgramTranslations(programs, "nameTranslations", 2, "name");
        program.programDescriptionsEn = ProgramTranslations(programs, "descriptionTranslations", 0, "description");
        program.programDescriptionsZh = ProgramTranslations(programs, "descriptionTranslations", 1, "description");
        program.programDescriptionsJa = ProgramTranslations(programs, "descriptionTranslations", 2, "description");
        program.expectedTextsEn = ProgramTranslations(programs, "expectedTextTranslations", 0, "expectedText");
        program.expectedTextsZh = ProgramTranslations(programs, "expectedTextTranslations", 1, "expectedText");
        program.expectedTextsJa = ProgramTranslations(programs, "expectedTextTranslations", 2, "expectedText");
        program.inputMode = 0; program.selectedPreset = defaultPreset; program.programPage = 0; program.virtualWord = 0;
        draftState.selectedPreset = defaultPreset;
        return program;
    }

    // Pipeline entry for adding the presentation boards to an already-built scene.
    // Unlike UpgradeScene, this path never rebuilds modules, tools, or the environment.
    public static object AddPresentationPanels()
    {
        var scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || scene.path != ScenePath)
            throw new InvalidOperationException("Activate the CircuitLab scene in Edit Mode before adding presentation panels.");
        var worldObject = scene.GetRootGameObjects().FirstOrDefault(root => root.name == "CircuitLabWorld");
        if (worldObject == null) throw new InvalidOperationException("CircuitLabWorld is missing; this entry only augments an existing build.");
        var world = worldObject.transform;
        var simulator = worldObject.GetComponentInChildren<CircuitLabSimulator>(true);
        _coordinator = worldObject.GetComponentInChildren<CircuitLabNetworkCoordinator>(true);
        _layout = worldObject.GetComponentInChildren<CircuitLabLayout>(true);
        var editor = worldObject.GetComponentInChildren<CircuitLabProgramPanel>(true);
        if (simulator == null || _coordinator == null || _layout == null || editor == null || editor.sharedDraft == null)
            throw new InvalidOperationException("Existing CPU, coordinator, layout, ProgramPanel, or shared draft is missing.");

        var source = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Data/gpu-netlist.json");
        if (source == null) throw new InvalidOperationException("CircuitLab program data is missing.");
        var data = JObject.Parse(source.text);
        if ((int?)data["schemaVersion"] != 3 && (int?)data["schema"] != 3)
            throw new InvalidOperationException("Expected schema3 teaching CPU program data.");
        if (!(data["programs"] is JArray programArray)) throw new InvalidOperationException("CircuitLab program catalog is missing.");
        var programs = programArray.Cast<JObject>().ToArray();
        if (programs.Length == 0 || programs.Length != simulator.programCount
            || simulator.programNames == null || simulator.programNames.Length != programs.Length
            || simulator.programDescriptions == null || simulator.programDescriptions.Length != programs.Length
            || simulator.presetProgramWords == null || simulator.presetProgramWords.Length < programs.Length * 512
            || simulator.presetInitialRam == null || simulator.presetInitialRam.Length < programs.Length * 1024)
            throw new InvalidOperationException("Program data does not match the existing simulator catalog; build matching CPU data first.");

        string[] names = new string[programs.Length];
        string[] descriptions = new string[programs.Length];
        for (int i = 0; i < programs.Length; i++)
        {
            names[i] = (string)programs[i]["name"] ?? ("Program " + i);
            descriptions[i] = (string)programs[i]["description"] ?? "";
            string lowerName = names[i].ToLowerInvariant();
            string lowerDescription = descriptions[i].ToLowerInvariant();
            bool statesFirstSix = lowerDescription.Contains("first six") || lowerDescription.Contains("前六") || lowerDescription.Contains("前 6");
            bool statesSeventh = lowerDescription.Contains("seventh") || lowerDescription.Contains("第七") || lowerDescription.Contains("第 7");
            if (lowerName.Contains("355") && (!statesFirstSix || !statesSeventh))
                descriptions[i] += " 【近似】355/113 长除法：小数前六位与 π 一致，第七位起不同。";
        }

        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _board = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/ModuleBoard.mat");
        _edge = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/ModuleRim.mat");
        _handle = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/PickupHandles.mat");
        if (_font == null || _board == null || _edge == null || _handle == null)
            throw new InvalidOperationException("Existing panel font or materials are missing.");

        var existingToolIds = new HashSet<int>(worldObject.GetComponentsInChildren<CircuitLabMovableTool>(true).Select(tool => tool.GetInstanceID()));
        bool addedAbout = world.Find("AboutPanel") == null;
        bool addedBrowser = world.Find("ProgramBrowserPanel") == null;
        int initialPreset = editor.sharedDraft.selectedPreset;
        if (initialPreset < 0 || initialPreset >= programs.Length) initialPreset = editor.selectedPreset;
        if (initialPreset < 0 || initialPreset >= programs.Length) initialPreset = simulator.programSelect;
        initialPreset = Mathf.Clamp(initialPreset, 0, programs.Length - 1);
        Behaviours.Clear();
        var browser = BuildPresentationPanels(world, simulator, programs, names, descriptions, editor, editor.sharedDraft, initialPreset);
        editor.sharedDraft.panel = editor;
        editor.sharedDraft.browser = browser;
        editor.sharedDraft.coordinator = _coordinator;
        browser.sharedState = editor.sharedDraft;
        browser.editorPanel = editor;
        browser.simulator = simulator;
        browser.coordinator = _coordinator;
        if (addedBrowser)
        {
            editor.sharedDraft.selectedPreset = browser.selectedProgram;
            editor.sharedDraft.browsePage = browser.browsePage;
            editor.sharedDraft.browseMode = browser.browseMode;
        }
        if (!Behaviours.Contains(editor.sharedDraft)) Behaviours.Add(editor.sharedDraft);
        _layout.tools = worldObject.GetComponentsInChildren<CircuitLabMovableTool>(true);
        foreach (var tool in _layout.tools)
        {
            if (existingToolIds.Contains(tool.GetInstanceID())) continue;
            tool.initialPosition = tool.transform.position;
            tool.initialRotation = tool.transform.rotation;
            tool.initialScale = tool.transform.localScale;
            if (!Behaviours.Contains(tool)) Behaviours.Add(tool);
        }
        HideLegacyProgramControls(world);
        foreach (var behaviour in Behaviours)
        {
            UdonSharpEditorUtility.CopyProxyToUdon(behaviour);
            EditorUtility.SetDirty(behaviour);
            EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(behaviour));
        }
        UdonSharpEditorUtility.CopyProxyToUdon(_layout);
        EditorUtility.SetDirty(_layout);
        EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(_layout));
        EditorSceneManager.MarkSceneDirty(scene);
        return new
        {
            addedAboutPanel = addedAbout,
            addedProgramBrowserPanel = addedBrowser,
            programRows = world.GetComponentsInChildren<CircuitLabProgramRow>(true).Length,
            layoutTools = _layout.tools.Length,
            preservedExistingToolInitialPoses = true,
            scene = scene.path,
            saved = false
        };
    }

    private static string[] ProgramTranslations(JObject[] programs, string field, int language, string fallback)
    {
        return programs.Select(p => p[field] is JArray array && array.Count > language ? (string)array[language] ?? "" : (string)p[fallback] ?? "").ToArray();
    }

    private static CircuitLabProgramBrowser BuildPresentationPanels(Transform world, CircuitLabSimulator simulator, JObject[] programs,
        string[] names, string[] descriptions, CircuitLabProgramPanel editor, CircuitLabProgramDraft sharedState, int defaultPreset)
    {
        BuildAboutPanel(world);
        BuildTeachingPanels(world, simulator);
        var browser = BuildProgramBrowserPanel(world, simulator, programs, names, descriptions, editor, sharedState, defaultPreset);
        if (_layout != null) _layout.tools = world.GetComponentsInChildren<CircuitLabMovableTool>(true);
        return browser;
    }

    private static void BuildTeachingPanels(Transform world, CircuitLabSimulator simulator)
    {
        var manualData = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Data/isa-manual.json");
        if (manualData == null) throw new InvalidOperationException("Generate isa-manual.json before building teaching panels.");
        var pages = JObject.Parse(manualData.text)["pages"].Cast<JObject>().ToArray();
        var existing = world.Find("IsaManualPanel");
        var panel = existing == null ? MovablePanel(world, "IsaManualPanel", new Vector3(-11.5f, 3.6f, 5.5f), new Vector3(0, -55, 0), 6.5f, 5.4f) : existing.gameObject;
        MigrateTeachingPose(panel.transform, new Vector3(-9.5f, 3.5f, -10.4f), new Vector3(-11.5f, 3.6f, 5.5f), -55);
        var oldCanvas = panel.transform.Find("IsaCanvas");
        if (oldCanvas != null) Undo.DestroyObjectImmediate(oldCanvas.gameObject);
        var canvas = PanelCanvas(panel.transform, "IsaCanvas", 6.5f, 5.4f);
        var manual = EnsureIsaState(panel); manual.coordinator = _coordinator;
        manual.titlesEn = pages.Select(p => (string)p["titles"][0]).ToArray();
        manual.titlesZh = pages.Select(p => (string)p["titles"][1]).ToArray();
        manual.titlesJa = pages.Select(p => (string)p["titles"][2]).ToArray();
        manual.pagesEn = pages.Select(p => (string)p["bodies"][0]).ToArray();
        manual.pagesZh = pages.Select(p => (string)p["bodies"][1]).ToArray();
        manual.pagesJa = pages.Select(p => (string)p["bodies"][2]).ToArray();
        PanelText(canvas.transform, "IsaHeader", "ISA MANUAL", new Vector2(-140, 239), new Vector2(300, 30), 19, Color.white, TextAnchor.MiddleLeft);
        PanelButton(canvas.transform, "ISA_REQUEST", "REQUEST / CLAIM CONTROL", manual, "ClaimControl", new Vector2(167, 239), new Vector2(265, 28), 12);
        string[] events = { "Overview", "Encoding", "Instructions", "Extensions", "Flags", "Examples" };
        string[] labels = { "OVERVIEW", "ENCODING", "MAIN OPS", "EXT OPS", "FLAGS", "EXAMPLES" };
        for (int i = 0; i < events.Length; i++)
            PanelButton(canvas.transform, "ISA_" + events[i], labels[i], manual, events[i], new Vector2(-251 + i * 101, 188), new Vector2(96, 30), 11);
        manual.titleText = PanelText(canvas.transform, "IsaPageTitle", manual.titlesEn[manual.page], new Vector2(0, 140), new Vector2(592, 36), 20, new Color(.3f, .95f, .8f), TextAnchor.MiddleLeft);
        manual.bodyText = PanelText(canvas.transform, "IsaBody", manual.pagesEn[manual.page], new Vector2(0, -27), new Vector2(592, 282), 16, new Color(.87f, .94f, .97f), TextAnchor.UpperLeft);
        manual.bodyText.lineSpacing = 1.2f;
        manual.pageText = PanelText(canvas.transform, "IsaPageNumber", "SHARED PAGE 1 / " + pages.Length, new Vector2(0, -226), new Vector2(240, 30), 13, Color.white);
        PanelButton(canvas.transform, "ISA_PREVIOUS", "< PREVIOUS", manual, "PreviousPage", new Vector2(-214, -226), new Vector2(158, 30), 13);
        PanelButton(canvas.transform, "ISA_NEXT", "NEXT >", manual, "NextPage", new Vector2(214, -226), new Vector2(158, 30), 13);

        var display = Behaviour<CircuitLabOutputDisplay>(OwnedChild(world, "OutputDisplayController")); display.simulator = simulator;
        var ledExisting = world.Find("LedOutputPanel");
        var led = ledExisting == null ? MovablePanel(world, "LedOutputPanel", new Vector3(-6.3f, 2.8f, 6f), new Vector3(0, -35, 0), 4.9f, 2.3f) : ledExisting.gameObject;
        MigrateTeachingPose(led.transform, new Vector3(-4.5f, 2.5f, -7.5f), new Vector3(-6.3f, 2.8f, 6f), -35);
        var ledCanvas = led.transform.Find("LedCanvas"); if (ledCanvas != null) Undo.DestroyObjectImmediate(ledCanvas.gameObject);
        var lc = PanelCanvas(led.transform, "LedCanvas", 4.9f, 2.3f);
        PanelText(lc.transform, "LedTitle", "8-BIT LED OUTPUT", new Vector2(0, 80), new Vector2(450, 30), 19, Color.white);
        display.ledMaterial = Material("LedOutput", "VrcCpuWorld/CircuitLabOutputView", Color.white); display.ledMaterial.SetInt("_Mode", 0);
        OutputImage(lc.transform, "LedView", new Vector2(0, 30), new Vector2(430, 57), display.ledMaterial);
        PanelText(lc.transform, "LedBitLabels", "7      6      5      4      3      2      1      0", new Vector2(0, -13), new Vector2(430, 22), 12, new Color(.6f, .8f, .9f));
        display.ledStatus = PanelText(lc.transform, "LedStatus", "OUT[7:0] = 0x00", new Vector2(0, -60), new Vector2(420, 56), 15, new Color(.76f, .94f, .84f));

        var lifeExisting = world.Find("LifeOutputPanel");
        var life = lifeExisting == null ? MovablePanel(world, "LifeOutputPanel", new Vector3(8.7f, 3.6f, 5.3f), new Vector3(0, 40, 0), 4.9f, 5.1f) : lifeExisting.gameObject;
        // The board used to sit half inside the backdrop. Chain the two historical
        // defaults forward so a saved scene lands in front of the wall while a
        // player-moved board keeps its authored pose.
        bool lifeMoved = MigrateTeachingPose(life.transform, new Vector3(4.5f, 3.25f, -9.3f), new Vector3(8.7f, 3.6f, 5.3f), 40);
        lifeMoved |= MigrateTeachingPose(life.transform, new Vector3(8.7f, 3.6f, 6.3f), new Vector3(8.7f, 3.6f, 5.3f), 40);
        if (lifeMoved)
        {
            life.transform.position = new Vector3(8.7f, 3.6f, 5.3f);
            life.transform.eulerAngles = new Vector3(0, 40, 0);
            var lifeTool = life.GetComponent<CircuitLabMovableTool>();
            if (lifeTool != null)
            {
                Undo.RecordObject(lifeTool, "Move Conway Life board in front of the backdrop");
                lifeTool.initialPosition = life.transform.position;
                lifeTool.initialRotation = life.transform.rotation;
                UdonSharpEditorUtility.CopyProxyToUdon(lifeTool);
                EditorUtility.SetDirty(lifeTool);
                EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(lifeTool));
            }
        }
        var lifeCanvas = life.transform.Find("LifeCanvas"); if (lifeCanvas != null) Undo.DestroyObjectImmediate(lifeCanvas.gameObject);
        var gc = PanelCanvas(life.transform, "LifeCanvas", 4.9f, 5.1f);
        PanelText(gc.transform, "LifeTitle", "CONWAY LIFE  /  8 × 8", new Vector2(0, 219), new Vector2(460, 30), 19, Color.white);
        display.lifeMaterial = Material("LifeOutput", "VrcCpuWorld/CircuitLabOutputView", Color.white); display.lifeMaterial.SetInt("_Mode", 1);
        OutputImage(gc.transform, "LifeGrid", new Vector2(0, 17), new Vector2(355, 355), display.lifeMaterial);
        display.lifeStatus = PanelText(gc.transform, "LifeStatus", "LOAD CONWAY LIFE FROM THE PROGRAM LIST", new Vector2(0, -184), new Vector2(440, 32), 13, new Color(.76f, .94f, .84f));
        PanelText(gc.transform, "LifeRule", "B3 / S23  ·  CLOSED BOUNDARY  ·  CPU UPDATES RAM", new Vector2(0, -214), new Vector2(460, 20), 11, new Color(.65f, .8f, .9f));
        var programData = JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Data/gpu-netlist.json").text)["programs"].Cast<JObject>().ToArray();
        display.lifeProgram = Array.FindIndex(programData, p => ((string)p["id"] ?? "").Contains("life"));
        if (display.lifeProgram >= 0)
        {
            var metadata = programData[display.lifeProgram]["display"] as JObject;
            display.bufferA = (int?)metadata?["displayRamBase"] ?? 256;
            display.bufferB = (int?)metadata?["displayAlternateBase"] ?? 320;
            display.selectorAddress = (int?)metadata?["selectorAddress"] ?? 224;
            display.generationAddress = (int?)metadata?["generationAddress"] ?? 225;
        }
    }

    private static CircuitLabIsaManual EnsureIsaState(GameObject panel)
    {
        var legacy = panel.GetComponent<CircuitLabIsaManual>();
        var state = Behaviour<CircuitLabIsaManual>(OwnedChild(panel.transform, "IsaSharedState"));
        if (legacy != null)
        {
            foreach (var field in typeof(CircuitLabIsaManual).GetFields(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                field.SetValue(state, field.GetValue(legacy));
            foreach (var button in panel.GetComponentsInChildren<CircuitLabButton>(true))
                if (button.target == legacy)
                {
                    button.target = state;
                    UdonSharpEditorUtility.CopyProxyToUdon(button);
                    EditorUtility.SetDirty(button);
                    EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(button));
                }
            UdonSharpEditorUtility.DestroyImmediate(legacy);
        }
        UdonSharpEditorUtility.CopyProxyToUdon(state);
        EditorUtility.SetDirty(state);
        EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(state));
        return state;
    }

    public static object RepairIsaNetworkState()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Repair authored ISA network state in Edit Mode.");
        var world = SceneManager.GetActiveScene().GetRootGameObjects().Single(g => g.name == "CircuitLabWorld");
        var state = EnsureIsaState(world.transform.Find("IsaManualPanel").gameObject);
        EditorSceneManager.MarkSceneDirty(world.scene);
        return new { repaired = true, stateObject = state.name, sharedPage = state.page };
    }

    // Widens the terminal capture ring on an existing scene so high simulation
    // rates do not lose OUT events between readbacks. Does not touch layout.
    // Adopts the annotated half-phase settle depths and the runtime text subset on
    // an existing scene: both come from data the scene cannot derive itself.
    public static object UpgradeSimulationTuning()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before updating simulation tuning.");
        var world = SceneManager.GetActiveScene().GetRootGameObjects().Single(g => g.name == "CircuitLabWorld");
        var simulator = world.GetComponentInChildren<CircuitLabSimulator>(true);
        var localization = world.GetComponentInChildren<CircuitLabLocalization>(true);
        if (simulator == null || localization == null) throw new InvalidOperationException("Simulator or localization component is missing.");
        var data = JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Data/gpu-netlist.json").text);
        Undo.RecordObject(simulator, "Adopt half-phase settle depths");
        simulator.settlePasses = (int)data["settlePasses"];
        simulator.clockSettlePasses = (int?)data["clockSettlePasses"] ?? 2;
        // Adaptive settle: learn the depth each phase actually needs from the GPU
        // probe channels, then stop paying for the full combinational depth.
        simulator.adaptiveSettle = true;
        simulator.settleDepthMargin = 6;
        simulator.settleMinDepth = 5;
        simulator.settleLearnWindows = 2;
        simulator.settleDepthCurrent = simulator.settlePasses;
        simulator.settleDepthLearned = 0;
        simulator.settleTruncations = 0;
        simulator.trackSettlePasses = false;
        UdonSharpEditorUtility.CopyProxyToUdon(simulator);
        EditorUtility.SetDirty(simulator);
        EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(simulator));

        // Texts that Udon writes at runtime: every Text field referenced by a
        // CircuitLab behaviour. Everything else only needs the slow full sweep.
        var dynamicTexts = new HashSet<Text>();
        foreach (UdonSharpBehaviour behaviour in world.GetComponentsInChildren<UdonSharpBehaviour>(true))
        {
            // The localization component owns the source list itself; its own
            // fields are bookkeeping, not texts that change at runtime.
            if (behaviour == null || behaviour is CircuitLabLocalization) continue;
            foreach (var field in behaviour.GetType().GetFields())
            {
                if (field.FieldType == typeof(Text))
                {
                    var text = field.GetValue(behaviour) as Text;
                    if (text != null) dynamicTexts.Add(text);
                }
                else if (field.FieldType == typeof(Text[]))
                {
                    var texts = field.GetValue(behaviour) as Text[];
                    if (texts == null) continue;
                    foreach (Text text in texts) if (text != null) dynamicTexts.Add(text);
                }
            }
        }
        var indices = new List<int>();
        if (localization.sourceTexts != null)
            for (int i = 0; i < localization.sourceTexts.Length; i++)
                if (localization.sourceTexts[i] != null && dynamicTexts.Contains(localization.sourceTexts[i])) indices.Add(i);
        Undo.RecordObject(localization, "Adopt the runtime text subset");
        localization.dynamicIndices = indices.ToArray();
        localization.fullScanInterval = 2f;
        UdonSharpEditorUtility.CopyProxyToUdon(localization);
        EditorUtility.SetDirty(localization);
        EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(localization));
        EditorSceneManager.MarkSceneDirty(world.scene);
        EditorSceneManager.SaveScene(world.scene);
        AssetDatabase.SaveAssets();
        return new
        {
            updated = true,
            settlePasses = simulator.settlePasses,
            clockSettlePasses = simulator.clockSettlePasses,
            localizedTexts = localization.sourceTexts == null ? 0 : localization.sourceTexts.Length,
            dynamicTexts = indices.Count,
            scanReduction = localization.sourceTexts == null || localization.sourceTexts.Length == 0
                ? 0f : 1f - (float)indices.Count / localization.sourceTexts.Length
        };
    }

    // Binds every gate/wire material to StateA, the texture a finished cycle always
    // leaves the live state in. With that authored binding the simulator never has
    // to touch those shared materials at runtime, which both saves ~50 interop calls
    // per rebind and stops Play Mode from dirtying the material assets.
    public static object BindWireStateTextures()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before rebinding the wire materials.");
        var world = SceneManager.GetActiveScene().GetRootGameObjects().Single(g => g.name == "CircuitLabWorld");
        var simulator = world.GetComponentInChildren<CircuitLabSimulator>(true);
        if (simulator == null || simulator.stateA == null) throw new InvalidOperationException("Simulator or StateA texture is missing.");
        var materials = new List<Material>();
        if (simulator.displayMaterial != null) materials.Add(simulator.displayMaterial);
        if (simulator.displayMaterials != null) materials.AddRange(simulator.displayMaterials);
        // Every wire signal material in the project, including rows a line renderer
        // shares, so no stale asset keeps a scratch-buffer binding.
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { Root + "/Materials" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".mat") || !System.IO.Path.GetFileName(path).StartsWith("WireSignal_")) continue;
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) materials.Add(material);
        }
        int bound = 0;
        foreach (Material material in materials.Distinct())
        {
            if (material == null) continue;
            Undo.RecordObject(material, "Bind wire state texture");
            material.SetTexture("_StateTex", simulator.stateA);
            material.SetFloat("_TextureWidth", simulator.textureWidth);
            EditorUtility.SetDirty(material);
            bound++;
        }
        AssetDatabase.SaveAssets();
        return new { bound, stateTexture = simulator.stateA.name, textureWidth = simulator.textureWidth };
    }

    public static object UpgradeTerminalEventBuffer()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before resizing the terminal buffer.");
        var world = SceneManager.GetActiveScene().GetRootGameObjects().Single(g => g.name == "CircuitLabWorld");
        var terminal = world.GetComponentInChildren<CircuitLabTerminal>(true);
        if (terminal == null) throw new InvalidOperationException("CircuitLabTerminal is missing.");
        Undo.RecordObject(terminal, "Widen the terminal capture ring");
        RenderTexture a = AssetDatabase.LoadAssetAtPath<RenderTexture>(Root + "/Textures/TerminalEventsA.renderTexture");
        RenderTexture b = AssetDatabase.LoadAssetAtPath<RenderTexture>(Root + "/Textures/TerminalEventsB.renderTexture");
        foreach (RenderTexture texture in new[] { a, b })
        {
            if (texture == null) throw new InvalidOperationException("Terminal event texture is missing.");
            if (texture.width != TerminalEventCapacity || texture.height != 2)
            {
                texture.Release();
                texture.width = TerminalEventCapacity;
                texture.height = 2;
            }
            texture.filterMode = FilterMode.Point; texture.wrapMode = TextureWrapMode.Clamp;
            EditorUtility.SetDirty(texture);
        }
        terminal.eventA = a; terminal.eventB = b; terminal.eventCapacity = TerminalEventCapacity;
        terminal.readbackInterval = .05f;
        UdonSharpEditorUtility.CopyProxyToUdon(terminal);
        EditorUtility.SetDirty(terminal);
        EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(terminal));
        EditorSceneManager.MarkSceneDirty(world.scene);
        EditorSceneManager.SaveScene(world.scene);
        AssetDatabase.SaveAssets();
        return new { upgraded = true, eventCapacity = terminal.eventCapacity, readbackInterval = terminal.readbackInterval, widthA = a.width, widthB = b.width };
    }

    public static object RepairTeachingOutputSpacing()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Repair authored output spacing in Edit Mode.");
        var world = SceneManager.GetActiveScene().GetRootGameObjects().Single(g => g.name == "CircuitLabWorld");
        var canvas = world.transform.Find("LifeOutputPanel/LifeCanvas");
        var status = canvas.Find("LifeStatus").GetComponent<RectTransform>();
        var rule = canvas.Find("LifeRule").GetComponent<RectTransform>();
        Undo.RecordObjects(new UnityEngine.Object[] { status, rule }, "Separate Life rules from pickup handle");
        status.anchoredPosition = new Vector2(0, -184); status.sizeDelta = new Vector2(440, 32);
        rule.anchoredPosition = new Vector2(0, -214); rule.sizeDelta = new Vector2(460, 20);
        EditorSceneManager.MarkSceneDirty(world.scene);
        return new { repaired = true, panel = "LifeOutputPanel" };
    }

    public static object CorrectTeachingPanelPositions()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Correct authored panel positions in Edit Mode.");
        var world = SceneManager.GetActiveScene().GetRootGameObjects().Single(g => g.name == "CircuitLabWorld");
        var names = new[] { "IsaManualPanel", "LedOutputPanel", "LifeOutputPanel" };
        var previous = new[] { new Vector3(-9.5f, 3.5f, -10.4f), new Vector3(-4.5f, 2.5f, -7.5f), new Vector3(4.5f, 3.25f, -9.3f) };
        var current = new[] { new Vector3(-11.5f, 3.6f, 5.5f), new Vector3(-6.3f, 2.8f, 6f), new Vector3(8.7f, 3.6f, 5.3f) };
        var yaws = new[] { -55f, -35f, 40f };
        for (int i = 0; i < names.Length; i++)
        {
            var panel = world.transform.Find(names[i]);
            bool migrated = MigrateTeachingPose(panel, previous[i], current[i], yaws[i]);
            if (names[i] == "LifeOutputPanel")
                migrated |= MigrateTeachingPose(panel, new Vector3(8.7f, 3.6f, 6.3f), current[i], yaws[i]);
            if (!migrated) continue;
            var tool = panel.GetComponent<CircuitLabMovableTool>();
            Undo.RecordObject(tool, "Correct authored panel position");
            tool.initialPosition = panel.position; tool.initialRotation = panel.rotation;
            UdonSharpEditorUtility.CopyProxyToUdon(tool); EditorUtility.SetDirty(tool);
            EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(tool));
        }
        EditorSceneManager.MarkSceneDirty(world.scene);
        return new { corrected = true, panels = names };
    }

    private static bool MigrateTeachingPose(Transform panel, Vector3 previousDefault, Vector3 currentDefault, float yaw)
    {
        // Only correct the task's early defaults; preserve authored/manual placement.
        if (Vector3.Distance(panel.position, previousDefault) > .001f) return false;
        Undo.RecordObject(panel, "Face teaching panel toward entrance");
        panel.position = currentDefault; panel.eulerAngles = new Vector3(0, yaw, 0);
        EditorUtility.SetDirty(panel);
        return true;
    }

    private static RawImage OutputImage(Transform parent, string name, Vector2 position, Vector2 size, Material material)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        go.transform.SetParent(parent, false); var rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        var image = go.GetComponent<RawImage>(); image.material = material; image.texture = Texture2D.whiteTexture; image.raycastTarget = false;
        return image;
    }

    private static GameObject BuildAboutPanel(Transform world)
    {
        var existing = world.Find("AboutPanel");
        if (existing != null)
        {
            MakeRoomForLocalePanel(existing);
            return existing.gameObject;
        }

        var panel = MovablePanel(world, "AboutPanel", new Vector3(-12.2f, 3.25f, -5.35f), new Vector3(0, -60f, 0), 5.4f, 3.7f);
        var canvas = PanelCanvas(panel.transform, "AboutCanvas", 5.4f, 3.7f);
        var title = PanelText(canvas.transform, "AboutTitle", "CIRCUIT LAB  /  VRCHAT CPU MAP", new Vector2(0, 137), new Vector2(500, 34), 17, Color.white);
        title.resizeTextForBestFit = true; title.resizeTextMinSize = 12; title.resizeTextMaxSize = 17;
        var description = PanelText(canvas.transform, "ProjectDescription",
            "Explore a synthesized gate-level teaching CPU in VRChat. Inspect the circuit hierarchy, probe live logic signals, follow clock waveforms, and browse or load shared ROM programs.",
            new Vector2(0, 46), new Vector2(480, 104), 12, new Color(.74f, .87f, .89f), TextAnchor.UpperLeft);
        description.resizeTextForBestFit = true; description.resizeTextMinSize = 9; description.resizeTextMaxSize = 12;
        PanelText(canvas.transform, "ProjectAuthor", "AUTHOR  /  chiro2001", new Vector2(0, -55), new Vector2(480, 28), 12, new Color(.69f, .90f, .80f), TextAnchor.MiddleLeft);
        var repository = PanelText(canvas.transform, "ProjectRepository",
            "SOURCE REPOSITORY\nhttps://github.com/chiro2001/vrc-circuit-lab",
            new Vector2(-135, -112), new Vector2(250, 54), 9, new Color(.66f, .82f, .90f), TextAnchor.MiddleLeft);
        repository.resizeTextForBestFit = true; repository.resizeTextMinSize = 8; repository.resizeTextMaxSize = 10;
        return panel;
    }

    // The language selector is embedded into the About board's lower right corner,
    // so the repository block moves left instead of running underneath it.
    private static void MakeRoomForLocalePanel(Transform about)
    {
        Transform repository = about.Find("AboutCanvas/ProjectRepository");
        if (repository == null) return;
        RectTransform rect = repository.GetComponent<RectTransform>();
        Text text = repository.GetComponent<Text>();
        Undo.RecordObjects(new Object[] { rect, text }, "Make room for the language panel");
        rect.anchoredPosition = new Vector2(-135, -112);
        rect.sizeDelta = new Vector2(250, 54);
        text.fontSize = 9;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 8;
        text.resizeTextMaxSize = 10;
        EditorUtility.SetDirty(rect);
        EditorUtility.SetDirty(text);
    }

    private static CircuitLabProgramBrowser BuildProgramBrowserPanel(Transform world, CircuitLabSimulator simulator, JObject[] programs,
        string[] names, string[] descriptions, CircuitLabProgramPanel editor, CircuitLabProgramDraft sharedState, int defaultPreset)
    {
        var existing = world.Find("ProgramBrowserPanel");
        bool isNew = existing == null;
        var panel = isNew
            ? MovablePanel(world, "ProgramBrowserPanel", new Vector3(12.2f, 3.75f, -5.35f), new Vector3(0, 60f, 0), 8.8f, 6.8f)
            : existing.gameObject;
        var browser = Behaviour<CircuitLabProgramBrowser>(panel);
        browser.simulator = simulator; browser.coordinator = _coordinator; browser.editorPanel = editor; browser.sharedState = sharedState;
        browser.pageSize = 12;
        browser.programNames = names; browser.programDescriptions = descriptions;
        browser.programNamesEn = ProgramTranslations(programs, "nameTranslations", 0, "name");
        browser.programNamesZh = ProgramTranslations(programs, "nameTranslations", 1, "name");
        browser.programNamesJa = ProgramTranslations(programs, "nameTranslations", 2, "name");
        browser.programDescriptionsEn = ProgramTranslations(programs, "descriptionTranslations", 0, "description");
        browser.programDescriptionsZh = ProgramTranslations(programs, "descriptionTranslations", 1, "description");
        browser.programDescriptionsJa = ProgramTranslations(programs, "descriptionTranslations", 2, "description");
        browser.programExpectedTextsEn = ProgramTranslations(programs, "expectedTextTranslations", 0, "expectedText");
        browser.programExpectedTextsZh = ProgramTranslations(programs, "expectedTextTranslations", 1, "expectedText");
        browser.programExpectedTextsJa = ProgramTranslations(programs, "expectedTextTranslations", 2, "expectedText");
        browser.programSourceCommentsEn = ProgramTranslations(programs, "sourceCommentTranslations", 0, "sourceComment");
        browser.programSourceCommentsZh = ProgramTranslations(programs, "sourceCommentTranslations", 1, "sourceComment");
        browser.programSourceCommentsJa = ProgramTranslations(programs, "sourceCommentTranslations", 2, "sourceComment");
        browser.programSources = programs.Select(program => (string)program["source"] ?? "").ToArray();
        browser.programExpectedTexts = programs.Select(program => (string)program["expectedText"] ?? "").ToArray();
        browser.programExpectedCycles = programs.Select(program => (int?)program["expectedCycles"] ?? 0).ToArray();
        int previousSelected = browser.selectedProgram;
        int previousPage = browser.browsePage;
        int previousMode = browser.browseMode;
        var previousCanvas = panel.transform.Find("ProgramBrowserCanvas");
        if (previousCanvas != null) Undo.DestroyObjectImmediate(previousCanvas.gameObject);
        var canvas = PanelCanvas(panel.transform, "ProgramBrowserCanvas", 8.8f, 6.8f);
        PanelText(canvas.transform, "ProgramBrowserTitle", "PROGRAM BROWSER", new Vector2(-97, 310), new Vector2(580, 34), 20, Color.white);
        PanelButton(canvas.transform, "BROWSER_REQUEST_CONTROL", "REQUEST / CLAIM CONTROL", browser, "ClaimControl", new Vector2(339, 309), new Vector2(190, 26), 9);
        browser.currentLoadedText = PanelText(canvas.transform, "CurrentLoadedStatus", "CURRENT LOADED", new Vector2(0, 270), new Vector2(830, 29), 12, new Color(.78f, .92f, .76f));
        PanelText(canvas.transform, "ProgramListHeading", "PRESET PROGRAMS", new Vector2(-309, 225), new Vector2(248, 23), 11, new Color(.65f, .85f, .88f));
        browser.browsingProgramText = PanelText(canvas.transform, "BrowsingProgram", "BROWSING", new Vector2(142, 228), new Vector2(535, 26), 12, new Color(.86f, .96f, .78f));
        browser.descriptionText = PanelText(canvas.transform, "BrowsingDescription", "", new Vector2(142, 188), new Vector2(535, 48), 10, new Color(.68f, .83f, .87f), TextAnchor.UpperLeft);
        browser.descriptionText.verticalOverflow = VerticalWrapMode.Truncate;
        browser.expectedOutputText = PanelText(canvas.transform, "ExpectedOutput", "EXPECTED RESULT", new Vector2(142, 148), new Vector2(535, 30), 9, new Color(.84f, .85f, .67f), TextAnchor.MiddleLeft);
        browser.expectedOutputText.resizeTextForBestFit = true; browser.expectedOutputText.resizeTextMinSize = 7; browser.expectedOutputText.resizeTextMaxSize = 9;

        PanelButton(canvas.transform, "BROWSER_SHOW_SOURCE", "ASM SOURCE", browser, "ShowSource", new Vector2(-19, 112), new Vector2(132, 25), 9);
        PanelButton(canvas.transform, "BROWSER_SHOW_HEX", "HEX WORDS", browser, "ShowEncoding", new Vector2(124, 112), new Vector2(116, 25), 9);
        PanelButton(canvas.transform, "BROWSER_SHOW_BIN", "BIN WORDS", browser, "ShowBinaryEncoding", new Vector2(256, 112), new Vector2(116, 25), 9);
        browser.pageText = PanelText(canvas.transform, "BrowserPageInfo", "ASM SOURCE", new Vector2(142, 82), new Vector2(535, 20), 9, new Color(.59f, .78f, .82f));

        GameObject sourcePane = PanelScrollText(canvas.transform, "SourceScrollRegion", new Vector2(142, -65), new Vector2(535, 270), 10,
            new Color(.79f, .90f, .86f), out browser.sourceScrollRect, out browser.sourceContentRect, out browser.sourceText, out browser.sourceTextRect);
        GameObject encodingPane = PanelScrollText(canvas.transform, "EncodingScrollRegion", new Vector2(142, -65), new Vector2(535, 270), 11,
            new Color(.79f, .90f, .86f), out browser.encodingScrollRect, out browser.encodingContentRect, out browser.encodingText, out browser.encodingTextRect);
        // Listings are code: IBM Plex Mono for the legacy text and, through the
        // localization code-font path, for the SDF display that players actually see.
        Font monoFont = AssetDatabase.LoadAssetAtPath<Font>(Root + "/Localization/FontSources/IBMPlexMono-Regular.ttf");
        if (monoFont != null) { browser.sourceText.font = monoFont; browser.encodingText.font = monoFont; }
        browser.sourceLineHeight = 15f;
        browser.sourcePane = sourcePane; browser.encodingPane = encodingPane;
        encodingPane.SetActive(false);

        browser.pagePreviousButton = PanelButton(canvas.transform, "BROWSER_PREVIOUS_PAGE", "< PREVIOUS", browser, "PreviousPage", new Vector2(-5, -231), new Vector2(135, 25), 9).gameObject;
        browser.pageNextButton = PanelButton(canvas.transform, "BROWSER_NEXT_PAGE", "NEXT >", browser, "NextPage", new Vector2(289, -231), new Vector2(135, 25), 9).gameObject;
        // The ASM listing scrolls; these mirror the preset-list scroll buttons and
        // stay usable by every player because the position is local-only.
        var sourceScrollUp = PanelButton(canvas.transform, "BROWSER_SOURCE_SCROLL_UP", "SCROLL UP", browser, "ScrollSourceUp", new Vector2(-5, -231), new Vector2(135, 25), 9);
        var sourceScrollDown = PanelButton(canvas.transform, "BROWSER_SOURCE_SCROLL_DOWN", "SCROLL DOWN", browser, "ScrollSourceDown", new Vector2(289, -231), new Vector2(135, 25), 9);
        sourceScrollUp.requireControl = false; sourceScrollDown.requireControl = false;
        SetSingleLineButtonLabel(sourceScrollUp, 9); SetSingleLineButtonLabel(sourceScrollDown, 9);
        browser.sourceScrollUpButton = sourceScrollUp.gameObject;
        browser.sourceScrollDownButton = sourceScrollDown.gameObject;
        PanelButton(canvas.transform, "LOAD_SELECTED_TO_ROM", "LOAD SELECTED TO ROM", browser, "LoadSelectedPreset", new Vector2(142, -273), new Vector2(292, 30), 10);
        browser.statusText = PanelText(canvas.transform, "BrowserStatus", "SELECTING A PROGRAM ONLY BROWSES IT", new Vector2(142, -306), new Vector2(535, 19), 8, new Color(.63f, .76f, .80f));

        browser.programListScrollRect = PanelScrollRegion(canvas.transform, "ProgramListScrollRegion", new Vector2(-309, -5), new Vector2(250, 365), out RectTransform programListContent);
        const float rowHeight = 42f;
        programListContent.sizeDelta = new Vector2(programListContent.sizeDelta.x, programs.Length * rowHeight);
        for (int index = 0; index < programs.Length; index++)
        {
            var rowButton = UiButton(programListContent, "ProgramRow_" + index, names[index], null, "", Vector2.zero, Vector2.zero, 10);
            var rowObject = rowButton.gameObject;
            var rowRect = rowObject.GetComponent<RectTransform>();
            rowRect.anchorMin = new Vector2(0, 1); rowRect.anchorMax = Vector2.one; rowRect.pivot = new Vector2(.5f, 1);
            rowRect.anchoredPosition = new Vector2(0, -index * rowHeight); rowRect.sizeDelta = new Vector2(0, rowHeight - 1f);
            var rowLabel = rowObject.transform.Find("Label").GetComponent<Text>();
            rowLabel.alignment = TextAnchor.MiddleLeft; rowLabel.fontSize = 13; rowLabel.resizeTextMinSize = 12; rowLabel.resizeTextMaxSize = 13;
            rowLabel.horizontalOverflow = HorizontalWrapMode.Wrap; rowLabel.verticalOverflow = VerticalWrapMode.Truncate;
            rowLabel.resizeTextForBestFit = false;
            var rowLabelRect = rowLabel.GetComponent<RectTransform>();
            rowLabelRect.anchorMin = Vector2.zero; rowLabelRect.anchorMax = Vector2.one;
            rowLabelRect.offsetMin = new Vector2(7, 0); rowLabelRect.offsetMax = new Vector2(-7, 0);
            var row = Behaviour<CircuitLabProgramRow>(rowObject); row.browser = browser; row.programIndex = index; row.label = rowLabel; row.uiButton = rowButton.uiButton;
            var clickHandler = rowObject.GetComponent<CircuitLabButton>();
            clickHandler.target = row; clickHandler.eventName = "Select"; clickHandler.requireControl = true;
        }
        PanelButton(canvas.transform, "PROGRAM_LIST_SCROLL_UP", "SCROLL UP", browser, "ScrollListUp", new Vector2(-309, 190), new Vector2(250, 24), 9);
        PanelButton(canvas.transform, "PROGRAM_LIST_SCROLL_DOWN", "SCROLL DOWN", browser, "ScrollListDown", new Vector2(-309, -202), new Vector2(250, 24), 9);
        browser.selectedProgram = Mathf.Clamp(isNew ? defaultPreset : previousSelected, 0, Mathf.Max(0, programs.Length - 1));
        browser.browsePage = isNew ? 0 : previousPage; browser.browseMode = isNew ? 0 : previousMode;
        HideLegacyProgramControls(panel.transform);
        return browser;
    }

    private static ScrollRect PanelScrollRegion(Transform parent, string name, Vector2 position, Vector2 size, out RectTransform contentRect)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        root.layer = InteractionLayer; root.transform.SetParent(parent, false);
        var rootRect = root.GetComponent<RectTransform>(); rootRect.anchoredPosition = position; rootRect.sizeDelta = size;
        var background = root.GetComponent<Image>(); background.color = new Color(.012f, .031f, .046f, .98f); background.raycastTarget = true;
        var scroll = root.GetComponent<ScrollRect>(); scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.inertia = false;
        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        viewport.layer = InteractionLayer; viewport.transform.SetParent(root.transform, false);
        var viewportRect = viewport.GetComponent<RectTransform>(); viewportRect.anchorMin = Vector2.zero; viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero; viewportRect.offsetMax = Vector2.zero;
        viewport.GetComponent<Image>().color = new Color(1, 1, 1, .015f);
        var content = new GameObject("Content", typeof(RectTransform)); content.layer = InteractionLayer; content.transform.SetParent(viewport.transform, false);
        contentRect = content.GetComponent<RectTransform>(); contentRect.anchorMin = new Vector2(0, 1); contentRect.anchorMax = Vector2.one;
        contentRect.pivot = new Vector2(.5f, 1); contentRect.sizeDelta = new Vector2(0, size.y); contentRect.anchoredPosition = Vector2.zero;
        scroll.viewport = viewportRect; scroll.content = contentRect;
        return scroll;
    }

    private static GameObject PanelScrollText(Transform parent, string name, Vector2 position, Vector2 size, int fontSize, Color color,
        out ScrollRect scroll, out RectTransform contentRect, out Text text, out RectTransform textRect)
    {
        scroll = PanelScrollRegion(parent, name, position, size, out contentRect);
        var textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.layer = InteractionLayer; textObject.transform.SetParent(contentRect, false);
        textRect = textObject.GetComponent<RectTransform>(); textRect.anchorMin = new Vector2(0, 1); textRect.anchorMax = Vector2.one;
        textRect.pivot = new Vector2(.5f, 1); textRect.sizeDelta = new Vector2(0, size.y); textRect.anchoredPosition = Vector2.zero;
        text = textObject.GetComponent<Text>(); text.font = _font; text.fontSize = fontSize; text.color = color;
        text.alignment = TextAnchor.UpperLeft; text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = false; text.raycastTarget = false;
        return scroll.transform.gameObject;
    }

    private static void HideLegacyProgramControls(Transform root)
    {
        var analyzerSelector = root.Find("MovableWaveformPanel/SelectProgram_Canvas");
        if (analyzerSelector != null) analyzerSelector.gameObject.SetActive(false);
        foreach (string name in new[] { "SelectProgram_Canvas", "PRESET_PREVIOUS", "PRESET_NEXT", "PresetSelection", "PREVIOUS_PRESET", "NEXT_PRESET", "LOAD_PRESET", "LOAD_SELECTED_PRESET" })
        {
            var legacy = root.Find(name);
            if (legacy != null) legacy.gameObject.SetActive(false);
        }
        var canvas = root.Find("ProgramPanel/ProgramCanvas");
        if (canvas == null) canvas = root.Find("ProgramCanvas");
        if (canvas != null)
            foreach (string name in new[] { "PRESET_PREVIOUS", "PRESET_NEXT", "PresetSelection", "PREVIOUS_PRESET", "NEXT_PRESET", "LOAD_PRESET", "LOAD_SELECTED_PRESET" })
            {
                var legacy = canvas.Find(name);
                if (legacy != null) legacy.gameObject.SetActive(false);
            }
    }

    private static int IsaCode(JToken token)
    {
        if (token == null) return -1;
        if (token.Type == JTokenType.Integer) return (int)token;
        string text = ((string)token ?? "").Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt32(text.Substring(2), 16);
        if (text.StartsWith("E.", StringComparison.OrdinalIgnoreCase)) return 0xE0 + Convert.ToInt32(text.Substring(2), 16);
        return Convert.ToInt32(text, 16);
    }

    private static CircuitLabStateTablet BuildStateTablet(Transform world, CircuitLabSimulator simulator, JObject data, Dictionary<int, JObject> nodes, Material tabletMaterial)
    {
        var panel = MovablePanel(world, "StateTablet", new Vector3(12f, 3.2f, -.4f), new Vector3(0, 60f, 0), 6.8f, 5.8f);
        var canvas = PanelCanvas(panel.transform, "StateTabletCanvas", 6.8f, 5.8f);
        var tablet = Behaviour<CircuitLabStateTablet>(panel); tablet.simulator = simulator; tablet.coordinator = _coordinator;
        tablet.nodeCount = simulator.nodeCount; tablet.signalIds = Enumerable.Range(0, simulator.nodeCount).ToArray();
        tablet.signalNames = new string[simulator.nodeCount]; tablet.signalModules = new string[simulator.nodeCount];
        tablet.signalTypes = new string[simulator.nodeCount]; tablet.signalInputs = new string[simulator.nodeCount]; tablet.signalOpcodes = new int[simulator.nodeCount];
        var modulePaths = ((JArray)data["modules"]).Cast<JObject>().ToDictionary(module => (int)module["id"], module => (string)module["path"]);
        int[] romAddress = data["memoryPorts"]["romAddress"].ToObject<int[]>();
        int[] ramAddress = data["memoryPorts"]["ramReadAddress"].ToObject<int[]>();
        foreach (var pair in nodes)
        {
            int id = pair.Key; JObject node = pair.Value; int op = (int)node["op"];
            tablet.signalNames[id] = (string)node["name"] ?? ("signal_" + id);
            int moduleId = (int?)node["moduleId"] ?? -1;
            tablet.signalModules[id] = modulePaths.ContainsKey(moduleId) ? modulePaths[moduleId] : "module " + moduleId;
            tablet.signalTypes[id] = GateName(op);
            tablet.signalOpcodes[id] = op;
            int[] inputs = op == 15 ? romAddress : op == 16 ? ramAddress : Inputs(node);
            if (op == 3)
            {
                int[] clockSignals = data["ports"]["clk"].ToObject<int[]>();
                int[] dffInputs = new int[inputs.Length + clockSignals.Length];
                for (int bit = 0; bit < inputs.Length; bit++) dffInputs[bit] = inputs[bit];
                for (int bit = 0; bit < clockSignals.Length; bit++) dffInputs[inputs.Length + bit] = clockSignals[bit];
                inputs = dffInputs;
            }
            string inputText = "";
            for (int pin = 0; pin < inputs.Length; pin++)
            {
                int inputId = inputs[pin];
                string inputName = nodes.ContainsKey(inputId) ? (string)nodes[inputId]["name"] : "signal_" + inputId;
                inputText += (pin == 0 ? "" : ", ") + inputName + " (#" + inputId + ")";
            }
            tablet.signalInputs[id] = inputText.Length == 0 ? "(none)" : inputText;
        }
        tablet.displayMaterial = tabletMaterial;
        var displayObject = new GameObject("TextureView", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        displayObject.layer = InteractionLayer; displayObject.transform.SetParent(canvas.transform, false);
        var displayRect = displayObject.GetComponent<RectTransform>(); displayRect.anchoredPosition = new Vector2(-124, 2); displayRect.sizeDelta = new Vector2(352, 352);
        tablet.textureView = displayObject.GetComponent<RawImage>(); tablet.textureView.texture = Texture2D.whiteTexture; tablet.textureView.material = tabletMaterial; tablet.textureView.raycastTarget = false;
        PanelText(canvas.transform, "TabletTitle", "LIVE STATE  /  RAM  /  ROM", new Vector2(-110, 267), new Vector2(420, 24), 17, Color.white);
        PanelButton(canvas.transform, "TABLET_REQUEST_CONTROL", "REQUEST / CLAIM CONTROL", tablet, "ClaimControl", new Vector2(224, 267), new Vector2(212, 25), 9);
        PanelText(canvas.transform, "TabletStatus", "VALUES ARE GPU READBACKS  /  STABLE SAMPLE AFTER RAM + CPU COMMIT", new Vector2(0, 241), new Vector2(640, 18), 9, new Color(.59f, .80f, .85f));
        tablet.pageText = PanelText(canvas.transform, "TabletPage", "SIGNALS", new Vector2(-124, 194), new Vector2(352, 18), 9, new Color(.69f, .87f, .88f));
        tablet.tabText = PanelText(canvas.transform, "TabletTab", "LIVE SIGNAL STATE", new Vector2(190, 194), new Vector2(250, 20), 11, new Color(.75f, .9f, .84f));
        tablet.detailText = PanelText(canvas.transform, "SelectedSignalDetails", "SELECT A SIGNAL FROM THE GRID", new Vector2(190, 95), new Vector2(250, 110), 9, new Color(.81f, .90f, .90f), TextAnchor.UpperLeft);
        tablet.memoryStatusText = PanelText(canvas.transform, "MemoryPortStatus", "A READ  /  B WRITE", new Vector2(190, -18), new Vector2(250, 112), 9, new Color(.71f, .85f, .89f), TextAnchor.UpperLeft);
        tablet.ramDetailText = PanelText(canvas.transform, "SelectedRamDetails", "RAM[00]", new Vector2(190, -132), new Vector2(250, 58), 10, new Color(.88f, .91f, .68f), TextAnchor.UpperLeft);
        PanelButton(canvas.transform, "TABLET_SHOW_SIGNALS", "SIGNALS", tablet, "ShowSignals", new Vector2(-250, 218), new Vector2(90, 24), 9);
        PanelButton(canvas.transform, "TABLET_SHOW_GATES", "GATES", tablet, "ShowGates", new Vector2(-150, 218), new Vector2(82, 24), 9);
        PanelButton(canvas.transform, "TABLET_SHOW_ROM", "ROM", tablet, "ShowRom", new Vector2(-59, 218), new Vector2(62, 24), 9);
        PanelButton(canvas.transform, "TABLET_SHOW_RAM", "RAM", tablet, "ShowRam", new Vector2(13, 218), new Vector2(62, 24), 9);
        PanelButton(canvas.transform, "TABLET_SELECT_NODE", "SHOW NODE", tablet, "SelectNode", new Vector2(131, 163), new Vector2(102, 22), 8);
        PanelButton(canvas.transform, "TABLET_SELECT_RAM_ADDRESS", "SELECT ADDRESS", tablet, "SelectRamAddress", new Vector2(248, 163), new Vector2(118, 22), 8);
        PanelButton(canvas.transform, "TABLET_PAGE_PREVIOUS", "<", tablet, "PreviousPage", new Vector2(-250, -192), new Vector2(48, 23), 10);
        PanelButton(canvas.transform, "TABLET_PAGE_NEXT", ">", tablet, "NextPage", new Vector2(-196, -192), new Vector2(48, 23), 10);
        PanelButton(canvas.transform, "TABLET_TOGGLE_RAW", "RAW TEXTURE", tablet, "ToggleRawRam", new Vector2(128, -194), new Vector2(104, 22), 8);
        PanelButton(canvas.transform, "TABLET_TOGGLE_INITIAL", "INIT / CURRENT", tablet, "ToggleInitialRam", new Vector2(246, -194), new Vector2(112, 22), 8);
        PanelButton(canvas.transform, "TABLET_BIND_SELECTED", "BIND BIT", tablet, "BindSelectedToProbe", new Vector2(128, -221), new Vector2(104, 24), 8);
        PanelButton(canvas.transform, "TABLET_BIND_RAM_BYTE", "PROBE BYTE", tablet, "BindSelectedRamByte", new Vector2(246, -221), new Vector2(112, 24), 8);
        for (int bit = 0; bit < 8; bit++)
            PanelButton(canvas.transform, "TABLET_RAM_BIT_" + bit, "B" + bit, tablet, "SelectRamBit" + bit, new Vector2(108 + bit * 24, -172), new Vector2(22, 20), 7);

        tablet.signalCellLabels = new Text[64]; tablet.signalCellObjects = new GameObject[64];
        float signalCell = 352f / 8f;
        for (int slot = 0; slot < 64; slot++)
        {
            int row = slot / 8, column = slot % 8;
            Vector2 pos = new Vector2(-124 - 176 + (column + .5f) * signalCell, 2 + 176 - (row + .5f) * signalCell);
            DirectCellButton(canvas.transform, "SignalCell_" + slot, "", pos, new Vector2(signalCell, signalCell), tablet, slot, false, out tablet.signalCellObjects[slot], out tablet.signalCellLabels[slot]);
        }
        tablet.ramCellLabels = new Text[256]; tablet.ramCellObjects = new GameObject[256];
        float ramCell = 352f / 16f;
        for (int address = 0; address < 256; address++)
        {
            int row = address / 16, column = address % 16;
            Vector2 pos = new Vector2(-124 - 176 + (column + .5f) * ramCell, 2 + 176 - (row + .5f) * ramCell);
            DirectCellButton(canvas.transform, "RamCell_" + address.ToString("X2"), "00", pos, new Vector2(ramCell, ramCell), tablet, address, true, out tablet.ramCellObjects[address], out tablet.ramCellLabels[address]);
        }
        var ramTargets = new CircuitLabSignalTarget[4];
        for (int channel = 0; channel < ramTargets.Length; channel++)
        {
            Vector3 point = new Vector3(2.55f + channel * .20f, -2.36f, -.18f);
            ramTargets[channel] = SignalTarget(panel.transform, "RAMProbeTarget_" + (char)('A' + channel), new[] { simulator.nodeCount }, "RAM[00].0", null, false, point, .035f);
        }
        tablet.ramProbeTargets = ramTargets;
        var shared = Behaviour<CircuitLabTabletSharedState>(Child(panel.transform, "SharedState"));
        shared.tablet = tablet; shared.coordinator = _coordinator; tablet.sharedState = shared;
        shared.ramSignalA = simulator.nodeCount; shared.ramSignalB = simulator.nodeCount; shared.ramSignalC = simulator.nodeCount; shared.ramSignalD = simulator.nodeCount;
        tablet.activeTab = 0; tablet.selectNode = 0; tablet.selectRamAddress = 0; tablet.selectedRamBit = 0;
        return tablet;
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
        if (op == 15 || op == 16) return new int[0];
        if (op <= 2) return new int[0];
        if (op <= 5) return new[] { (int)node["a"] };
        if (op == 10) return new[] { (int)node["a"], (int)node["b"], (int)node["c"] };
        return new[] { (int)node["a"], (int)node["b"] };
    }

    private static string GateName(int op)
    {
        if (op >= 0 && op <= 14) return new[] { "0", "1", "IN", "DFF", "BUF", "NOT", "AND", "OR", "XOR", "XNOR", "MUX", "NAND", "NOR", "ANDNOT", "ORNOT" }[op];
        if (op == 15) return "ROM_READ";
        if (op == 16) return "RAM_READ";
        return "OP_" + op;
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
        handler.requireControl = target is CircuitLabSimulator || target is CircuitLabProbe || target is CircuitLabLayout || target is CircuitLabHierarchyRow || target is CircuitLabHierarchyTree
            || (target is CircuitLabIsaManual && eventName != "ClaimControl")
            || (target is CircuitLabProgramBrowser && eventName != "ClaimControl") || target is CircuitLabProgramRow
            || (target is CircuitLabProgramPanel && eventName != "ClaimControl") || (target is CircuitLabStateTablet && eventName != "ClaimControl");
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
        var tool = Behaviour<CircuitLabMovableTool>(go); tool.coordinator = _coordinator; tool.pickup = ConfigurePickup(go, grip);
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
        var programPanel = Object.FindObjectOfType<CircuitLabProgramPanel>();
        var stateTablet = Object.FindObjectOfType<CircuitLabStateTablet>();
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
            currentProgram = simulator.activeProgramName, programRevision = simulator.programRevision,
            ramSampleStable = simulator.observedRamStable, ramSampleCycle = simulator.observedRamCycle,
            programPanel = programPanel == null ? null : new { currentProgram = programPanel.currentProgramName, title = programPanel.programCurrentTitle.text, draftCharacters = programPanel.draftInput.text.Length },
            stateTablet = stateTablet == null ? null : new { selectedNode = stateTablet.selectNode, selectedRamAddress = stateTablet.selectRamAddress, tab = stateTablet.activeTab },
            shaders = new[] { "CircuitLabSim", "CircuitLabRam", "CircuitLabHistory", "CircuitLabWaveform", "CircuitLabDisplay", "CircuitLabTabletDisplay" }.Select(name => { var shader = Shader.Find("VrcCpuWorld/" + name); return new { name, supported = shader != null && shader.isSupported, messages = shader == null ? new string[0] : ShaderUtil.GetShaderMessages(shader).Select(message => message.message).ToArray() }; }).ToArray()
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
        var programPanel = roots.SelectMany(root => root.GetComponentsInChildren<CircuitLabProgramPanel>(true)).Single();
        var stateTablet = roots.SelectMany(root => root.GetComponentsInChildren<CircuitLabStateTablet>(true)).Single();
        var layout = roots.SelectMany(root => root.GetComponentsInChildren<CircuitLabLayout>(true)).Single();
        if (modules.Length == 0 || targets.Length < simulator.nodeCount) throw new InvalidOperationException("Incomplete module/pin construction.");
        foreach (GameObject panel in new[] { programPanel.gameObject, stateTablet.gameObject })
        {
            var tool = panel.GetComponent<CircuitLabMovableTool>();
            string canvasName = panel == programPanel.gameObject ? "ProgramCanvas" : "StateTabletCanvas";
            var canvasTransform = panel.transform.Find(canvasName);
            var canvas = canvasTransform == null ? null : canvasTransform.GetComponent<Canvas>();
            if (tool == null || panel.GetComponent<VRC_Pickup>() == null || panel.GetComponent<VRCObjectSync>() == null
                || panel.GetComponent<Rigidbody>() == null || canvas == null || canvas.GetComponent<VRCUiShape>() == null
                || canvas.GetComponent<GraphicRaycaster>() == null || canvas.GetComponent<BoxCollider>() == null)
                throw new InvalidOperationException("Incomplete movable VR panel " + panel.name);
            if (!layout.tools.Contains(tool)) throw new InvalidOperationException("Layout reset omits panel " + panel.name);
        }
        if (programPanel.sharedDraft == null || programPanel.sharedDraft.transform.parent != programPanel.transform
            || stateTablet.sharedState == null || stateTablet.sharedState.transform.parent != stateTablet.transform)
            throw new InvalidOperationException("Panel Manual sync state must live on an independent child object.");
        foreach (var module in modules)
        {
            if (module.GetComponent<VRC_Pickup>() == null || module.GetComponent<Rigidbody>() == null || module.childModules == null || module.allModules == null)
                throw new InvalidOperationException("Incomplete module " + module.modulePath);
            foreach (var renderer in module.interior.GetComponentsInChildren<MeshRenderer>(true))
            { var filter = renderer.GetComponent<MeshFilter>(); if (filter != null && !AssetDatabase.Contains(filter.sharedMesh)) throw new InvalidOperationException("Non-persistent mesh " + renderer.name); }
        }
        foreach (var target in targets)
            if (target.signalIds == null || target.signalIds.Length == 0 || target.probes == null || target.probes.Length != 4) throw new InvalidOperationException("Unwired signal target " + target.name);
        foreach (var texture in new[] { simulator.stateA, simulator.stateB, simulator.historyA, simulator.historyB, simulator.frozenHistory, simulator.ramA, simulator.ramB })
            if (texture == null || !AssetDatabase.Contains(texture) || texture.filterMode != FilterMode.Point) throw new InvalidOperationException("Invalid persistent render texture.");
        foreach (var texture in new[] { simulator.programTexture, simulator.ramInitialTexture, simulator.restoreRamTexture, simulator.restoreStateTexture })
            if (texture == null || !AssetDatabase.Contains(texture) || texture.filterMode != FilterMode.Point || texture.format != TextureFormat.RGBA32)
                throw new InvalidOperationException("Invalid persistent ROM/RAM restore texture.");
        if (simulator.programTexture.width != 512 || simulator.programTexture.height != 1 || simulator.ramInitialTexture.width != 32 || simulator.ramInitialTexture.height != 32
            || simulator.restoreRamTexture.width != 32 || simulator.restoreRamTexture.height != 32 || simulator.restoreStateTexture.width != simulator.textureWidth || simulator.restoreStateTexture.height != 1
            || simulator.ramA.width != 32 || simulator.ramA.height != 32 || simulator.ramB.width != 32 || simulator.ramB.height != 32
            || simulator.programCount <= 0 || simulator.programWords == null || simulator.programWords.Length != 512 || simulator.initialRam == null || simulator.initialRam.Length != 1024)
            throw new InvalidOperationException("Invalid program, RAM, or DFF restore data.");
        if (simulator.dffSignals == null || simulator.restoreStateTexture == null || simulator.currentRam != simulator.ramA && simulator.currentRam != simulator.ramB)
            throw new InvalidOperationException("Full-state restoration or current RAM reference is not configured.");
        if (GameObject.Find("CircuitLabPreviewCamera").GetComponent<Camera>().enabled) throw new InvalidOperationException("Preview camera must remain disabled.");
        return new { passed = true, modules = modules.Length, nodeCount = simulator.nodeCount, dffs = simulator.dffSignals.Length, pinTargets = targets.Length, persistentMeshes = AssetDatabase.FindAssets("t:Mesh", new[] { Root + "/Meshes" }).Length, persistentRenderTextures = 7, romRamTextures = 4, physicalProbes = 4, moduleMeshDraws = modules.Length * 2 };
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
