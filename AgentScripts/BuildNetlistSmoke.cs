using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using VRC.Udon;
using Object = UnityEngine.Object;

public static class BuildNetlistSmoke
{
    private const string Root = "Assets/GpuNetlistSmoke";
    private const string ScenePath = Root + "/Scenes/GpuNetlistSmoke.unity";

    private static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = path.Substring(0, path.LastIndexOf('/'));
        Folder(parent);
        AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
    }

    private static T Asset<T>(string path, Func<T> create) where T : Object
    {
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (existing != null) return existing;
        if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            throw new InvalidOperationException("Unexpected existing asset: " + path);
        var asset = create();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    public static object CompileUdon()
    {
        Folder(Root + "/Programs");
        foreach (var name in new[] { "GpuNetlistSmoke", "GpuNetlistButton" })
        {
            var program = Asset(Root + "/Programs/" + name + ".asset", () => ScriptableObject.CreateInstance<UdonSharpProgramAsset>());
            program.sourceCsScript = AssetDatabase.LoadAssetAtPath<MonoScript>(Root + "/Scripts/" + name + ".cs");
            if (program.sourceCsScript == null) throw new InvalidOperationException("Missing script: " + name);
            EditorUtility.SetDirty(program);
        }
        AssetDatabase.SaveAssets();
        UdonSharpProgramAsset.CompileAllCsPrograms(true, true);
        if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
            throw new InvalidOperationException("UdonSharp compile failed; inspect Console.");
        return new { compiled = true, programs = 2 };
    }

    public static object BuildScene()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath || EditorApplication.isPlaying)
            throw new InvalidOperationException("Open the smoke scene in Edit Mode before building it.");
        var previous = scene.GetRootGameObjects().FirstOrDefault(o => o.name == "GpuNetlistSmokeWorld");
        if (previous != null) Undo.DestroyObjectImmediate(previous);
        foreach (var folder in new[] { "/Data", "/Materials", "/Meshes", "/Textures" }) Folder(Root + folder);
        var dataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Data/gpu-netlist.json");
        if (dataAsset == null) throw new InvalidOperationException("Missing generated netlist data.");
        var data = JObject.Parse(dataAsset.text);
        int width = (int)data["textureWidth"], count = (int)data["nodeCount"];
        var nodes = (JArray)data["nodes"];
        var gateTexture = Asset(Root + "/Textures/GateInstructions.asset", () => new Texture2D(width, 1, TextureFormat.RGBAFloat, false, true));
        gateTexture.filterMode = FilterMode.Point;
        gateTexture.wrapMode = TextureWrapMode.Clamp;
        var instructions = new Color[width];
        foreach (var node in nodes)
            instructions[(int)node["id"]] = new Color((int)node["op"], (int)node["a"], (int)node["b"], (int)node["c"]);
        gateTexture.SetPixels(instructions);
        gateTexture.Apply(false, false);
        EditorUtility.SetDirty(gateTexture);
        Func<string, RenderTexture> state = name => Asset(Root + "/Textures/" + name + ".renderTexture", () =>
            new RenderTexture(width, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false });
        var stateA = state("StateA");
        var stateB = state("StateB");
        var simulation = Material("Simulation", "VrcCpuWorld/NetlistSim", Color.white);
        simulation.SetTexture("_Gates", gateTexture);
        var display = Material("Signals", "VrcCpuWorld/NetlistDisplay", Color.white);
        display.SetFloat("_TextureWidth", width);
        var dark = Material("Board", "Unlit/Color", new Color(.015f, .025f, .05f));
        var floorMat = Material("Floor", "Unlit/Color", new Color(.08f, .10f, .14f));
        var buttonMat = Material("Buttons", "Unlit/Color", new Color(.08f, .17f, .25f));
        var world = new GameObject("GpuNetlistSmokeWorld");
        Undo.RegisterCreatedObjectUndo(world, "Build GPU netlist smoke");
        var floor = Primitive(world.transform, "Floor", PrimitiveType.Cube, new Vector3(0, -.1f, 0), new Vector3(18, .2f, 16), floorMat);
        Primitive(world.transform, "NetlistBoard", PrimitiveType.Quad, new Vector3(0, 3, 3.04f), new Vector3(10.4f, 5.8f, 1), dark);
        var descriptorObject = Child(world.transform, "VRCWorld");
        var spawn = Child(world.transform, "Spawn");
        spawn.transform.position = new Vector3(0, .05f, -4.5f);
        var descriptor = descriptorObject.AddComponent<VRCSceneDescriptor>();
        descriptor.spawns = new[] { spawn.transform };
        descriptorObject.AddComponent<VRC.Core.PipelineManager>();

        var cameraObject = Child(world.transform, "NetlistPreviewCamera");
        // Disabled by default: keep it available for Editor captures without driving
        // the simulation or overriding the VRChat player's camera.
        cameraObject.tag = "Untagged";
        cameraObject.transform.position = new Vector3(0, 3, -8);
        cameraObject.transform.LookAt(new Vector3(0, 3, 3));
        var camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 34;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.025f, .035f, .06f);
        camera.enabled = false;

        var canvasObject = new GameObject("Labels", typeof(RectTransform), typeof(Canvas));
        canvasObject.transform.SetParent(world.transform, false);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(1300, 750);
        canvasObject.transform.position = new Vector3(0, 3, 2.75f);
        canvasObject.transform.localScale = Vector3.one * .008f;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Text(canvas.transform, font, "Title", "GPU NETLIST SMOKE", new Vector2(0, 324), new Vector2(1250, 45), 29, Color.white);
        Text(canvas.transform, font, "Subtitle", "4-bit accumulator | reset > load > add > hold | blue = 0, gold = 1", new Vector2(0, 280), new Vector2(1250, 35), 17, new Color(.65f, .75f, .9f));
        var status = Text(canvas.transform, font, "Status", "READY - automatic self-test runs on world entry", new Vector2(0, -337), new Vector2(1200, 62), 22, Color.white);

        var positions = new Vector2[count];
        foreach (var node in nodes) positions[(int)node["id"]] = new Vector2((float)node["x"], (float)node["y"]);
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var colors = new List<Color>();
        var indices = new List<int>();
        foreach (var node in nodes)
        {
            int id = (int)node["id"], op = (int)node["op"];
            var parents = new List<int>();
            if (op == 3 || op >= 4) parents.Add((int)node["a"]);
            if (op >= 6) parents.Add((int)node["b"]);
            if (op == 10) parents.Add((int)node["c"]);
            foreach (var source in parents)
            {
                var a = positions[source];
                var b = positions[id];
                if (op == 3)
                {
                    float route = -2.12f - id * .002f;
                    Segment(a, new Vector2(a.x, route), source, .012f, vertices, uvs, colors, indices);
                    Segment(new Vector2(a.x, route), new Vector2(b.x, route), source, .012f, vertices, uvs, colors, indices);
                    Segment(new Vector2(b.x, route), b, source, .012f, vertices, uvs, colors, indices);
                }
                else Segment(a, b, source, .010f, vertices, uvs, colors, indices);
            }
        }
        MeshObject(world.transform, "SignalWires", "Wires", vertices, uvs, colors, indices, display);
        vertices.Clear(); uvs.Clear(); colors.Clear(); indices.Clear();
        foreach (var node in nodes)
        {
            int id = (int)node["id"];
            var point = positions[id];
            int start = vertices.Count;
            vertices.Add(new Vector3(point.x, point.y, -.015f)); uvs.Add(new Vector2(id, 0)); colors.Add(Color.white);
            for (int i = 0; i <= 12; i++)
            {
                float angle = i * Mathf.PI * 2 / 12;
                vertices.Add(new Vector3(point.x + Mathf.Cos(angle) * .055f, point.y + Mathf.Sin(angle) * .055f, -.015f));
                uvs.Add(new Vector2(id, 0)); colors.Add(Color.white);
                if (i > 0) { indices.Add(start); indices.Add(start + i); indices.Add(start + i + 1); }
            }
            Text(canvas.transform, font, "Signal_" + id, (string)node["name"], (point + new Vector2(0, .095f)) / .008f, new Vector2(100, 20), 11, new Color(.75f, .83f, .94f));
        }
        MeshObject(world.transform, "SignalNodes", "Nodes", vertices, uvs, colors, indices, display);
        var controllerObject = Child(world.transform, "GpuSimulator");
        var controller = controllerObject.AddUdonSharpComponent<GpuNetlistSmoke>();
        controller.simulationMaterial = simulation;
        controller.displayMaterial = display;
        controller.gateTexture = gateTexture;
        controller.stateA = stateA;
        controller.stateB = stateB;
        controller.statusText = status;
        controller.nodeCount = count;
        controller.textureWidth = width;
        controller.settlePasses = (int)data["settlePasses"];
        controller.inputMasks = data["inputMasks"].ToObject<int[]>();
        controller.expectedStates = data["expectedStates"].ToObject<byte[]>();
        controller.qSignals = data["ports"]["q"].ToObject<int[]>();
        controller.zeroSignal = (int)data["ports"]["zero"][0];
        controller.paritySignal = (int)data["ports"]["parity"][0];
        controller.runOnStart = true;
        UdonSharpEditorUtility.CopyProxyToUdon(controller);
        EditorUtility.SetDirty(controller);
        var events = new[] { "ManualStep", "ManualLoad", "ManualReset", "RunSmoke" };
        var labels = new[] { "CLOCK +1", "LOAD 9", "RESET", "RUN TEST" };
        for (int i = 0; i < events.Length; i++)
        {
            float x = -3 + 2 * i;
            var button = Primitive(world.transform, labels[i], PrimitiveType.Cube, new Vector3(x, .75f, 2.89f), new Vector3(1.8f, .34f, .2f), buttonMat);
            var handler = button.AddUdonSharpComponent<GpuNetlistButton>();
            handler.target = controller;
            handler.eventName = events[i];
            UdonSharpEditorUtility.CopyProxyToUdon(handler);
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(handler);
            backing.interactText = labels[i];
            backing.proximity = 3;
            EditorUtility.SetDirty(handler); EditorUtility.SetDirty(backing);
            Text(canvas.transform, font, "ButtonLabel_" + i, labels[i], new Vector2(x / .008f, (.75f - 3) / .008f), new Vector2(215, 30), 18, Color.white);
        }
        EditorUtility.SetDirty(UdonSharpEditorUtility.GetBackingUdonBehaviour(controller));
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        return new { scene = scene.path, nodes = count, vectors = controller.inputMasks.Length, settlePasses = controller.settlePasses, shaderSupported = simulation.shader.isSupported };
    }

    private static Material Material(string name, string shaderName, Color color)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null || !shader.isSupported) throw new InvalidOperationException("Unsupported shader: " + shaderName);
        var material = Asset(Root + "/Materials/" + name + ".mat", () => new Material(shader));
        material.shader = shader;
        if (material.HasProperty("_Color")) material.color = color;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static GameObject Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    private static GameObject Primitive(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name; go.transform.SetParent(parent, false);
        go.transform.position = position; go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    private static Text Text(Transform parent, Font font, string name, string value, Vector2 position, Vector2 size, int fontSize, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchoredPosition = position; rect.sizeDelta = size;
        var text = go.GetComponent<Text>(); text.font = font; text.fontSize = fontSize;
        text.text = value; text.color = color; text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false;
        return text;
    }

    private static void Segment(Vector2 a, Vector2 b, int signal, float thickness, List<Vector3> vertices, List<Vector2> uvs, List<Color> colors, List<int> triangles)
    {
        var direction = (b - a).normalized;
        var normal = new Vector2(-direction.y, direction.x) * thickness / 2;
        int start = vertices.Count;
        foreach (var point in new[] { a - normal, a + normal, b + normal, b - normal })
        { vertices.Add(new Vector3(point.x, point.y, 0)); uvs.Add(new Vector2(signal, 0)); colors.Add(new Color(.43f, .43f, .43f, 1)); }
        triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
    }

    private static void MeshObject(Transform parent, string name, string assetName, List<Vector3> vertices, List<Vector2> uvs, List<Color> colors, List<int> triangles, Material material)
    {
        var mesh = Asset(Root + "/Meshes/" + assetName + ".asset", () => new Mesh());
        mesh.Clear(); mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.SetColors(colors); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        var go = Child(parent, name); go.transform.position = new Vector3(0, 3, 3);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    public static object Status()
    {
        var proxy = Object.FindObjectOfType<GpuNetlistSmoke>();
        if (proxy == null) throw new InvalidOperationException("Simulator not found in the scene.");
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
        var shader = Shader.Find("VrcCpuWorld/NetlistSim");
        var diagnostics = ShaderUtil.GetShaderMessages(shader).Select(message => new { message.message, severity = message.severity.ToString(), message.line }).ToArray();
        var result = new
        {
            playing = EditorApplication.isPlaying,
            statusCode = udon.GetProgramVariable("statusCode"),
            completedVectors = udon.GetProgramVariable("completedVectors"),
            mismatchCount = udon.GetProgramVariable("mismatchCount"),
            observedQ = udon.GetProgramVariable("observedQ"),
            cycleCount = udon.GetProgramVariable("cycleCount"),
            elapsedMilliseconds = udon.GetProgramVariable("elapsedMilliseconds"),
            smokeRunId = udon.GetProgramVariable("smokeRunId"),
            dataValue = udon.GetProgramVariable("dataValue"),
            proxyDataValue = proxy.dataValue,
            inputMask = proxy.simulationMaterial.GetInt("_InputMask"),
            readbackPending = udon.GetProgramVariable("_readbackPending"),
            shaderSupported = shader.isSupported,
            shaderDiagnostics = diagnostics,
            statusText = proxy.statusText.text,
            graphicsDevice = SystemInfo.graphicsDeviceName,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
        };
        if (EditorApplication.isPlaying)
        {
            Directory.CreateDirectory("Reports/NetlistSmoke");
            File.WriteAllText("Reports/NetlistSmoke/editor-gpu.json", Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
        }
        return result;
    }

    public static async Task<object> BuildClient()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before building the world.");
        EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
        await Task.Delay(1500);
        IVRCSdkWorldBuilderApi builder;
        if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out builder))
            throw new InvalidOperationException("VRChat SDK world builder is unavailable. Check SDK panel initialization.");
        builder.RegisterBuilder(EditorWindow.GetWindow<VRCSdkControlPanel>());
        string validationMessage;
        if (!builder.IsValidBuilder(out validationMessage))
            throw new InvalidOperationException("Invalid world builder: " + validationMessage);
        builder.Initialize();
        string bundlePath = null;
        EventHandler<string> success = (sender, path) => bundlePath = path;
        builder.OnSdkBuildSuccess += success;
        try { await builder.BuildAndTest(); }
        finally { builder.OnSdkBuildSuccess -= success; }
        var result = new { builtAndLaunched = true, bundlePath, scene = SceneManager.GetActiveScene().path };
        Directory.CreateDirectory("Reports/NetlistSmoke");
        File.WriteAllText("Reports/NetlistSmoke/world-build.json", Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
        return result;
    }

    public static object ValidationIssues()
    {
        var panel = EditorWindow.GetWindow<VRCSdkControlPanel>();
        var descriptor = Object.FindObjectOfType<VRCSceneDescriptor>();
        return new
        {
            world = panel.GetGuiErrorsOrIssuesForItem(descriptor).Select(issue => issue.issueText).ToArray(),
            project = panel.GetGuiErrorsOrIssuesForItem(panel).Select(issue => issue.issueText).ToArray(),
        };
    }

    public static object SdkSetupAPI()
    {
        var types = new List<Type>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { types.AddRange(assembly.GetTypes().Where(type => type.Name == "UpdateLayers")); }
            catch (System.Reflection.ReflectionTypeLoadException) { }
        }
        return types.Select(type => new { type = type.FullName, methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(method => new { method.Name, parameters = method.GetParameters().Select(parameter => parameter.ParameterType.FullName).ToArray() }).ToArray() }).ToArray();
    }

    public static object SetupSdk()
    {
        bool layersBefore = UpdateLayers.AreLayersSetup();
        bool matrixBefore = UpdateLayers.IsCollisionLayerMatrixSetup();
        if (!layersBefore || !matrixBefore)
        {
            Directory.CreateDirectory("Reports/NetlistSmoke");
            var previous = new { layersBefore, matrixBefore, layers = Enumerable.Range(0, 32).Select(index => LayerMask.LayerToName(index)).ToArray(), collisionMatrix = UpdateLayers.GetLayerCollisionArray() };
            File.WriteAllText("Reports/NetlistSmoke/sdk-before-setup.json", Newtonsoft.Json.JsonConvert.SerializeObject(previous, Newtonsoft.Json.Formatting.Indented));
        }
        if (!layersBefore) UpdateLayers.SetupEditorLayers();
        if (!matrixBefore) UpdateLayers.SetupCollisionLayerMatrix();
        AssetDatabase.SaveAssets();
        return new { layersReady = UpdateLayers.AreLayersSetup(), collisionMatrixReady = UpdateLayers.IsCollisionLayerMatrixSetup() };
    }

    public static async Task<object> ManualControls()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode before testing Udon interactions.");
        var proxy = Object.FindObjectOfType<GpuNetlistSmoke>();
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while ((Convert.ToInt32(udon.GetProgramVariable("smokeRunId")) == 0 || Convert.ToInt32(udon.GetProgramVariable("statusCode")) == 1) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        if (Convert.ToInt32(udon.GetProgramVariable("statusCode")) != 2)
            throw new InvalidOperationException("The complete GPU reference self-test must pass before interaction tests.");
        udon.SetProgramVariable("dataValue", 1);
        var names = new[] { "RESET", "CLOCK +1", "CLOCK +1", "LOAD 9", "CLOCK +1", "RESET" };
        var expected = new[] { 0, 1, 2, 9, 10, 0 };
        var results = new List<object>();
        for (int i = 0; i < names.Length; i++)
        {
            var button = GameObject.Find(names[i]);
            var handler = button.GetComponent<UdonBehaviour>();
            int before = Convert.ToInt32(udon.GetProgramVariable("cycleCount"));
            handler.Interact();
            deadline = DateTime.UtcNow.AddSeconds(3);
            while ((Convert.ToBoolean(udon.GetProgramVariable("_readbackPending")) || Convert.ToInt32(udon.GetProgramVariable("cycleCount")) == before || Convert.ToInt32(udon.GetProgramVariable("observedQ")) != expected[i]) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            int actual = Convert.ToInt32(udon.GetProgramVariable("observedQ"));
            int cycle = Convert.ToInt32(udon.GetProgramVariable("cycleCount"));
            if (actual != expected[i] || cycle != before + 1)
                throw new InvalidOperationException("Interaction failed: " + names[i] + " expected=" + expected[i] + " actual=" + actual + " cycle=" + cycle + " data=" + udon.GetProgramVariable("dataValue") + " mask=" + proxy.simulationMaterial.GetInt("_InputMask") + " pending=" + udon.GetProgramVariable("_readbackPending"));
            results.Add(new { action = names[i], expected = expected[i], actual, cycle });
        }
        var result = new { passed = true, path = "UdonBehaviour.Interact -> button Udon -> simulator Udon -> GPU -> async readback", cases = results };
        Directory.CreateDirectory("Reports/NetlistSmoke");
        File.WriteAllText("Reports/NetlistSmoke/manual-controls.json", Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
        return result;
    }

    public static async Task<object> RunButtonSelfTest()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode before testing the Run Test button.");
        var proxy = Object.FindObjectOfType<GpuNetlistSmoke>();
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
        if (Convert.ToBoolean(udon.GetProgramVariable("_readbackPending")))
            throw new InvalidOperationException("Wait for the pending GPU readback before restarting the test.");
        int previousRun = Convert.ToInt32(udon.GetProgramVariable("smokeRunId"));
        GameObject.Find("RUN TEST").GetComponent<UdonBehaviour>().Interact();
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while ((Convert.ToInt32(udon.GetProgramVariable("smokeRunId")) == previousRun || Convert.ToInt32(udon.GetProgramVariable("statusCode")) == 1) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        int completed = Convert.ToInt32(udon.GetProgramVariable("completedVectors"));
        int mismatches = Convert.ToInt32(udon.GetProgramVariable("mismatchCount"));
        if (Convert.ToInt32(udon.GetProgramVariable("statusCode")) != 2 || completed != proxy.inputMasks.Length || mismatches != 0)
            throw new InvalidOperationException("The Run Test button did not complete a successful GPU self-test.");
        var result = new { passed = true, action = "RUN TEST", vectors = completed, mismatches, elapsedMilliseconds = udon.GetProgramVariable("elapsedMilliseconds") };
        Directory.CreateDirectory("Reports/NetlistSmoke");
        File.WriteAllText("Reports/NetlistSmoke/run-button.json", Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
        return result;
    }

    public static async Task<object> BurstControls()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode before testing queued clocks.");
        var proxy = Object.FindObjectOfType<GpuNetlistSmoke>();
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
        if (Convert.ToBoolean(udon.GetProgramVariable("_readbackPending")) || Convert.ToInt32(udon.GetProgramVariable("statusCode")) == 1)
            throw new InvalidOperationException("Finish the active GPU operation before testing queued clocks.");
        udon.SetProgramVariable("dataValue", 1);
        int initialQ = Convert.ToInt32(udon.GetProgramVariable("observedQ"));
        int initialCycle = Convert.ToInt32(udon.GetProgramVariable("cycleCount"));
        var button = GameObject.Find("CLOCK +1").GetComponent<UdonBehaviour>();
        for (int i = 0; i < 16; i++) button.Interact();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((Convert.ToInt32(udon.GetProgramVariable("cycleCount")) < initialCycle + 16 || Convert.ToBoolean(udon.GetProgramVariable("_readbackPending"))) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        int finalQ = Convert.ToInt32(udon.GetProgramVariable("observedQ"));
        int finalCycle = Convert.ToInt32(udon.GetProgramVariable("cycleCount"));
        if (finalCycle != initialCycle + 16 || finalQ != initialQ)
            throw new InvalidOperationException("Sixteen queued clocks did not execute exactly once each.");
        var result = new { passed = true, queuedClocks = 16, initialQ, finalQ, initialCycle, finalCycle };
        Directory.CreateDirectory("Reports/NetlistSmoke");
        File.WriteAllText("Reports/NetlistSmoke/burst-controls.json", Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
        return result;
    }
}
