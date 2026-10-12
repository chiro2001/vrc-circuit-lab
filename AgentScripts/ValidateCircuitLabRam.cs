using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;
using Object = UnityEngine.Object;

// Integration checks use actual Udon events and actual GPU readback. Reference
// outputs are assertions only and never provide production simulation state.
public static class ValidateCircuitLabRam
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private static CircuitLabSimulator Simulator() => Object.FindObjectsOfType<CircuitLabSimulator>(true).Single(s => s.gameObject.scene.path == ScenePath);
    private static UdonBehaviour Vm(UdonSharpBehaviour proxy) => UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
    private static int Int(UdonBehaviour vm, string field) => Convert.ToInt32(vm.GetProgramVariable(field));
    private static bool Bool(UdonBehaviour vm, string field) => Convert.ToBoolean(vm.GetProgramVariable(field));
    private static string Str(UdonBehaviour vm, string field) => Convert.ToString(vm.GetProgramVariable(field));
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static JObject Data() => JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/gpu-netlist.json").text);
    private static int ProgramIndex(string id)
    {
        var programs = (JArray)Data()["programs"];
        for (int i = 0; i < programs.Count; i++)
            if (string.Equals((string)programs[i]["id"], id, StringComparison.Ordinal)) return i;
        return -1;
    }
    private static async Task Until(Func<bool> condition, string message, int seconds = 30)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { Check(DateTime.UtcNow < limit, message); await Task.Delay(40); }
    }
    private static object Save(string name, object report)
    {
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/" + name + ".json", JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }
    private static void Click(string eventName)
    {
        var handler = Object.FindObjectsOfType<CircuitLabButton>().FirstOrDefault(b => b.eventName == eventName && b.gameObject.scene.path == ScenePath);
        Click(handler);
    }
    private static void Click(CircuitLabButton handler)
    {
        string eventName = handler == null ? "missing" : handler.eventName;
        Check(handler != null && handler.uiButton != null && handler.uiButton.IsInteractable(), "UI button unavailable: " + eventName);
        Check(handler.uiButton.onClick.GetPersistentTarget(0) == Vm(handler), "UI event bypasses backing Udon: " + eventName);
        ExecuteEvents.Execute(handler.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
    }
    private static async Task<Color32[]> ReadBytes(RenderTexture texture)
    {
        var source = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) source.TrySetException(new InvalidOperationException("GPU byte readback failed."));
            else source.TrySetResult(request.GetData<Color32>().ToArray());
        });
        if (await Task.WhenAny(source.Task, Task.Delay(10000)) != source.Task) throw new TimeoutException("GPU byte readback timeout.");
        return await source.Task;
    }
    private static async Task<Color[]> ReadFloats(RenderTexture texture)
    {
        var source = new TaskCompletionSource<Color[]>();
        AsyncGPUReadback.Request(texture, 0, request =>
        {
            if (request.hasError) source.TrySetException(new InvalidOperationException("GPU float readback failed."));
            else source.TrySetResult(request.GetData<Color>().ToArray());
        });
        if (await Task.WhenAny(source.Task, Task.Delay(10000)) != source.Task) throw new TimeoutException("GPU float readback timeout.");
        return await source.Task;
    }
    private static async Task<byte[]> Ram()
    {
        var pixels = await ReadBytes((RenderTexture)Vm(Simulator()).GetProgramVariable("currentRam"));
        byte[] bytes = pixels.Select(p => p.r).ToArray();
        Check(bytes.Length == CircuitLabSimulator.RamByteCount, "GPU RAM readback must contain all 1024 bytes.");
        return bytes;
    }
    private static async Task RefreshTelemetry()
    {
        var sim = Vm(Simulator());
        int previous = Int(sim, "telemetryReadbackCount");
        sim.SendCustomEvent("RequestTelemetry");
        await Until(() => Int(sim, "telemetryReadbackCount") > previous && !Bool(sim, "readbackPending"),
            "GPU state telemetry did not complete.");
    }
    private static CircuitLabProgramPanel Panel() => Object.FindObjectOfType<CircuitLabProgramPanel>();
    private static async Task Quiescent()
    {
        var s = Vm(Simulator()); var n = Vm(Simulator().coordinator);
        await Until(() => !Bool(s, "running") && Int(s, "queuedCommandCount") == 0 && Int(s, "queuedStepCount") == 0
            && !Bool(n, "restorePending") && Int(n, "snapshotCaptureStage") == 0, "Experiment did not reach a stable boundary.");
    }
    private static async Task LoadDraft(string text, bool binary = false)
    {
        var panel = Panel(); var vm = Vm(panel); var sim = Vm(Simulator());
        if (!Simulator().coordinator.HasControl()) Click("ClaimControl");
        await Until(() => panel.draftInput.IsInteractable(), "Controller cannot edit the program.");
        Click(binary ? "SetInputBinary" : "SetInputHex");
        panel.draftInput.text = text;
        int previous = Int(sim, "programRevision");
        Click("LoadDraft");
        await Until(() => Int(sim, "programRevision") > previous, "Valid draft was not loaded: " + panel.editorStatus.text);
        await Quiescent();
    }
    private static async Task Step(int count)
    {
        var sim = Vm(Simulator());
        for (int i = 0; i < count; i++)
        {
            int before = Int(sim, "cycleCount"); Click("Step");
            await Until(() => Int(sim, "cycleCount") == before + 1 && Int(sim, "queuedCommandCount") == 0 && Int(sim, "queuedStepCount") == 0, "STEP did not complete exactly one cycle.");
        }
        await Quiescent();
    }
    public static object BackupAuthoring()
    {
        Check(!EditorApplication.isPlaying, "Back up authored assets in Edit Mode.");
        var records = new JArray();
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/CircuitLab/Materials" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid); var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var textures = new JObject(); var serialized = new SerializedObject(material);
            var pairs = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
            for (int i = 0; pairs != null && i < pairs.arraySize; i++)
            {
                var pair = pairs.GetArrayElementAtIndex(i);
                var texture = pair.FindPropertyRelative("second.m_Texture").objectReferenceValue;
                textures[pair.FindPropertyRelative("first").stringValue] = texture == null ? "" : AssetDatabase.GetAssetPath(texture);
            }
            Check(material.shader != null && material.shader.isSupported && !material.shader.name.Contains("InternalError"), "Invalid authored material: " + path);
            records.Add(new JObject { ["kind"] = "material", ["path"] = path, ["json"] = EditorJsonUtility.ToJson(material), ["shader"] = AssetDatabase.GetAssetPath(material.shader), ["shaderName"] = material.shader.name, ["textures"] = textures });
        }
        var s = Simulator();
        foreach (var texture in new[] { s.programTexture, s.ramInitialTexture, s.restoreStateTexture, s.restoreRamTexture })
        {
            if (texture == null) continue;
            Check(!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)), "Cannot back up an unsaved runtime texture.");
            Check(texture.format == TextureFormat.RGBA32, "Runtime-mutable texture format unexpected: " + texture.name);
            var pixels = texture.GetPixels32(); var bytes = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++) { bytes[4*i] = pixels[i].r; bytes[4*i+1] = pixels[i].g; bytes[4*i+2] = pixels[i].b; bytes[4*i+3] = pixels[i].a; }
            records.Add(new JObject { ["kind"] = "texture", ["path"] = AssetDatabase.GetAssetPath(texture), ["rgba"] = Convert.ToBase64String(bytes) });
        }
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/ram-authoring-backup.json", records.ToString());
        return new { saved = true, records = records.Count };
    }
    public static object SaveAndBackup()
    {
        Check(!EditorApplication.isPlaying, "Save the completed upgrade in Edit Mode.");
        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
        Check(scene.isLoaded, "CircuitLab scene is not loaded.");
        Check(EditorSceneManager.SaveScene(scene), "Saving the authored scene failed.");
        return BackupAuthoring();
    }
    public static object RestoreAuthoringMemoryImages()
    {
        Check(!EditorApplication.isPlaying,"Restore initial memory images in Edit Mode.");
        var s=Simulator(); var preset=Data()["programs"][0];
        Check(s.programSelect==0 && s.programWords.SequenceEqual(preset["words"].ToObject<int[]>())
            && s.initialRam.SequenceEqual(preset["initialRam"].ToObject<byte[]>()),"Authored default differs from the validated preset.");
        var rom=new Color32[512];var ram=new Color32[CircuitLabSimulator.RamByteCount];
        for(int i=0;i<rom.Length;i++) rom[i]=new Color32((byte)(s.programWords[i]&255),(byte)(s.programWords[i]>>8),0,255);
        for(int i=0;i<ram.Length;i++) ram[i]=new Color32(s.initialRam[i],0,0,255);
        s.programTexture.SetPixels32(rom);s.programTexture.Apply(false,false);EditorUtility.SetDirty(s.programTexture);
        s.ramInitialTexture.SetPixels32(ram);s.ramInitialTexture.Apply(false,false);EditorUtility.SetDirty(s.ramInitialTexture);
        AssetDatabase.SaveAssets();
        return new{restored=true,programWords=rom.Length,initialRamBytes=ram.Length,source="validated authoring preset"};
    }
    public static object RestoreAuthoring()
    {
        Check(!EditorApplication.isPlaying, "Restore authored assets after exiting Play Mode.");
        var records = JArray.Parse(File.ReadAllText("Reports/CircuitLab/ram-authoring-backup.json"));
        foreach (var record in records)
        {
            string path = (string)record["path"];
            if ((string)record["kind"] == "material")
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                EditorJsonUtility.FromJsonOverwrite((string)record["json"], material);
                string shaderName = (string)record["shaderName"];
                Shader shader = string.IsNullOrEmpty(shaderName) ? null : Shader.Find(shaderName);
                if (shader == null && !string.IsNullOrEmpty((string)record["shader"])) shader = AssetDatabase.LoadAssetAtPath<Shader>((string)record["shader"]);
                if (shader == null) shader = material.shader; // Keep the valid reference restored by EditorJsonUtility.
                Check(shader != null && shader.isSupported && !shader.name.Contains("InternalError"), "Cannot restore material shader: " + path);
                material.shader = shader;
                foreach (var pair in ((JObject)record["textures"]).Properties())
                {
                    string texturePath = (string)pair.Value;
                    material.SetTexture(pair.Name, string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.LoadAssetAtPath<Texture>(texturePath));
                }
                EditorUtility.SetDirty(material);
            }
            else
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path); var bytes = Convert.FromBase64String((string)record["rgba"]);
                var pixels = new Color32[bytes.Length / 4];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(bytes[4*i], bytes[4*i+1], bytes[4*i+2], bytes[4*i+3]);
                texture.SetPixels32(pixels); texture.Apply(false, false); EditorUtility.SetDirty(texture);
            }
        }
        AssetDatabase.SaveAssets();
        return new { restored = true, records = records.Count };
    }
    public static object RepairBuiltinMaterials()
    {
        Check(!EditorApplication.isPlaying, "Repair authored shader references in Edit Mode.");
        Shader shader = Shader.Find("Unlit/Color"); Check(shader != null && shader.isSupported, "Built-in Unlit/Color unavailable.");
        string[] names = { "ModuleBoard", "ModuleRim", "Panel", "ProbeHighlight", "PickupHandles", "Floor", "Wall", "Probe0", "Probe1", "Probe2", "Probe3" };
        foreach (string name in names)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/CircuitLab/Materials/" + name + ".mat");
            Check(material != null, "Missing expected authored material: " + name);
            material.shader = shader; EditorUtility.SetDirty(material);
        }
        AssetDatabase.SaveAssets();
        return new { repaired = true, materials = names.Length, shader = shader.name };
    }
    public static async Task<object> Initialization()
    {
        Check(EditorApplication.isPlaying, "Enter Play Mode first.");
        var simulator = Simulator(); var sim = Vm(simulator);
        await Until(() => Networking.LocalPlayer != null && sim.HasDoneStart && Bool(sim, "_initialized"), "ClientSim CPU startup incomplete.", 40);
        Check(Int(sim, "statusCode") != 5, "Invalid GPU configuration.");
        Check(!Bool(sim, "historyFrozen") && Bool(sim, "followLatest") && simulator.waveHudText.text.StartsWith("LIVE"), "Analyzer did not start LIVE.");
        Check(((RenderTexture)sim.GetProgramVariable("currentRam")) != ((RenderTexture)sim.GetProgramVariable("currentState")), "RAM and gate state are not independent textures.");
        var currentRam = (RenderTexture)sim.GetProgramVariable("currentRam");
        var initialRam = (Texture2D)sim.GetProgramVariable("ramInitialTexture");
        var restoreRam = (Texture2D)sim.GetProgramVariable("restoreRamTexture");
        var initialRamBytes = (byte[])sim.GetProgramVariable("initialRam");
        Check(currentRam.width == 32 && currentRam.height == 32 && initialRam.width == 32 && initialRam.height == 32,
            "RAM textures are not 32x32.");
        Check(restoreRam.width == 32 && restoreRam.height == 32
            && Simulator().ramA.width == 32 && Simulator().ramA.height == 32
            && Simulator().ramB.width == 32 && Simulator().ramB.height == 32,
            "RAM restore/buffer textures are not 32x32.");
        Check(initialRamBytes != null && initialRamBytes.Length == CircuitLabSimulator.RamByteCount,
            "Initial RAM does not contain all 1024 bytes.");
        Check(((byte[])sim.GetProgramVariable("observedRam")).Length == CircuitLabSimulator.RamByteCount,
            "Observed RAM is not a 1024-byte buffer.");
        Check((await Ram()).Length == CircuitLabSimulator.RamByteCount, "RAM does not contain 1024 bytes.");
        return Save("ram-initialization", new { passed = true, startsLive = true, ramBytes = CircuitLabSimulator.RamByteCount, textureWidth = 32, textureHeight = 32, separateStateTextures = true, initialProgram = Str(sim, "activeProgramName") });
    }
    public static async Task<object> IsolateRamShader()
    {
        var sim=Simulator(); var material=new Material(sim.ramMaterial);
        var state=new Texture2D(sim.textureWidth,1,TextureFormat.RGBA32,false,true);
        var input=new Texture2D(32,32,TextureFormat.RGBA32,false,true);
        var target=new RenderTexture(32,32,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);target.Create();
        try
        {
            var pixels=new Color32[sim.textureWidth];pixels[sim.ramWriteEnableSignal]=new Color32(255,0,0,255);
            for(int bit=0;bit<10;bit++)pixels[sim.ramWriteAddressSignals[bit]]=new Color32((byte)(((32>>bit)&1)*255),0,0,255);
            for(int bit=0;bit<8;bit++)pixels[sim.ramWriteDataSignals[bit]]=new Color32((byte)(((171>>bit)&1)*255),0,0,255);
            state.SetPixels32(pixels);state.Apply();state.filterMode=FilterMode.Point;
            input.SetPixels32(new Color32[1024]);input.Apply();input.filterMode=FilterMode.Point;
            material.SetTexture("_StateTex",state);material.SetInt("_Mode",0);material.SetInt("_Reset",0);
            material.SetInt("_RamWriteEnableSignal",sim.ramWriteEnableSignal);
            int[] a=sim.ramWriteAddressSignals,d=sim.ramWriteDataSignals;
            material.SetVector("_RamWriteAddressA",new Vector4(a[0],a[1],a[2],a[3]));
            material.SetVector("_RamWriteAddressB",new Vector4(a[4],a[5],a[6],a[7]));
            material.SetVector("_RamWriteAddressC",new Vector4(a[8],a[9],-1,-1));
            material.SetVector("_RamWriteDataA",new Vector4(d[0],d[1],d[2],d[3]));
            material.SetVector("_RamWriteDataB",new Vector4(d[4],d[5],d[6],d[7]));
            material.SetInt("_TextureWidth",sim.textureWidth);material.SetInt("_NodeCount",sim.nodeCount);
            Graphics.Blit(input,target,material);
            var bytes=await ReadBytes(target);
            var changes=bytes.Select((b,i)=>new {address=i,value=(int)b.r}).Where(x=>x.value!=0).ToArray();
            Check(changes.Length==1 && changes[0].address==32 && changes[0].value==171,"RAM shader synthetic write failed.");
            return new { passed=true, address=32, value=171, ramBytes=1024 };
        }
        finally {target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(material);Object.DestroyImmediate(state);Object.DestroyImmediate(input);}
    }
    public static async Task<object> MemoryDiagnostic()
    {
        var v = Vm(Simulator()); var ram = await Ram(); var init = (byte[])v.GetProgramVariable("initialRam");
        return new { cycle = Int(v,"cycleCount"), acc = Int(v,"observedAcc"), bank = Int(v,"observedBank"), readAddress = Int(v,"observedRamReadAddress"), writeAddress = Int(v,"observedRamWriteAddress"), writeData = Int(v,"observedRamWriteData"), differences = ram.Select((b,i)=>new { address=i, value=(int)b, initial=(int)init[i] }).Where(x=>x.value!=x.initial).ToArray() };
    }
    public static async Task<object> EditingAndReset()
    {
        await LoadDraft("0x0aB ; LDI AB\n920 // STORE 20\nF00\n");
        var sim = Vm(Simulator());
        byte[] initialRam = (byte[])sim.GetProgramVariable("initialRam");
        Check(initialRam != null && initialRam.Length == CircuitLabSimulator.RamByteCount, "Draft initial RAM is incomplete.");
        byte[] expectedRam = (byte[])initialRam.Clone();
        var words = (int[])sim.GetProgramVariable("programWords");
        Check(words.Length == 512 && words[0] == 0x0ab && words[1] == 0x920 && words.Skip(2).All(w => w == 0xf00), "HEX parser/image padding incorrect.");
        int revision = Int(sim, "programRevision");
        Panel().draftInput.text = "1000"; Click("LoadDraft"); await Task.Delay(150);
        Check(Int(sim, "programRevision") == revision, "Invalid HEX changed the executing image.");
        Check(Panel().editorStatus.text.ToLowerInvariant().Contains("line") || Panel().editorStatus.text.Contains("1"), "Invalid input has no line diagnostic.");
        await Step(3); // LDI fetched/executed, STORE fetched but not committed.
        Check((await Ram()).SequenceEqual(initialRam), "STORE happened before its EXEC rising edge.");
        Click("ResetCircuit"); await Quiescent();
        Check((await Ram()).SequenceEqual(initialRam), "RESET allowed the pending STORE to commit.");
        await Step(4);
        expectedRam[0x20] = 0xab;
        var written = await Ram(); Check(written.SequenceEqual(expectedRam), "GPU RAM write address/data incorrect.");
        Click("ResetCircuit"); await Quiescent();
        Check((await Ram()).SequenceEqual(written), "Warm RESET altered RAM.");
        Click("RestartExperiment"); await Quiescent();
        Check((await Ram()).SequenceEqual(initialRam), "RESTART did not initialize all RAM bytes.");
        await LoadDraft("0000 01000001\n0101 00000000\n1111 00000000", true);
        await Step(6);
        await Until(() => Str(Vm(Simulator().terminal), "output") == "A", "Binary program did not emit actual OUT.");
        return Save("ram-edit-reset", new { passed = true, hexAndBinary = true, commentsAndPrefix = true, invalidImagePreservesProgram = true, pendingStoreCancelled = true, warmResetPreservesAllRam = true, restartInitializesRam = true, binaryTerminalOutput = "A", inputPath = "Unity Button pointerClick -> backing Udon -> queued actual GPU work" });
    }
    public static async Task<object> BankedRamAndStack()
    {
        // Seed BANK2[0x20], write BANK1[0x20], then read each bank back into
        // a separate byte. Results come from the production GPU RAM texture.
        await LoadDraft("E23\n0CD\n920\nE22\n0AB\n920\nE23\n000\n820\n921\nE22\n820\n922\nF00");
        byte[] expectedBanked = (byte[])((byte[])Vm(Simulator()).GetProgramVariable("initialRam")).Clone();
        expectedBanked[0x220] = 0xcd;
        expectedBanked[0x221] = 0xcd;
        expectedBanked[0x120] = 0xab;
        expectedBanked[0x122] = 0xab;
        await Step(28);
        byte[] banked = await Ram();
        Check(banked.SequenceEqual(expectedBanked),
            "BANKed LD/ST did not use the selected high address bits.");
        await RefreshTelemetry();
        Check(Int(Vm(Simulator()), "observedBank") == 1, "BANK1 telemetry did not report the actual BANK output.");

        Click("ResetCircuit"); await Quiescent();
        byte[] afterReset = await Ram();
        Check(afterReset.SequenceEqual(banked), "RESET changed GPU RAM across BANK pages.");
        await RefreshTelemetry();
        Check(Int(Vm(Simulator()), "observedBank") == 0, "Synchronous RESET did not return BANK to page 0.");
        int resetBank = Int(Vm(Simulator()), "observedBank");

        // Clear the two possible stack locations in both page 0 and page 3,
        // then CALL from page 3. CALL/RET must use page 0 independently of BANK.
        await LoadDraft("E21\n000\n900\n901\nE24\n000\n900\n901\nEE0\n00B\nF00\n0AB\n920\nEF0");
        byte[] expectedStack = (byte[])((byte[])Vm(Simulator()).GetProgramVariable("initialRam")).Clone();
        expectedStack[0] = 10;
        expectedStack[1] = 0;
        expectedStack[0x300] = 0;
        expectedStack[0x301] = 0;
        expectedStack[0x320] = 0xab;
        await Step(31);
        byte[] stack = await Ram();
        Check(stack.SequenceEqual(expectedStack),
            "CALL/RET stack traffic was not fixed to RAM page 0 while BANK3 data access stayed banked.");
        await RefreshTelemetry();
        Check(Int(Vm(Simulator()), "observedBank") == 3 && Int(Vm(Simulator()), "observedSp") == 0,
            "CALL/RET did not preserve BANK3 and restore SP after returning.");
        return Save("ram-banks-stack", new
        {
            passed = true,
            ramBytes = CircuitLabSimulator.RamByteCount,
            highPageLdSt = true,
            resetPreservedAllPages = true,
            resetBank,
            callRetStackPage = 0,
            bank3DataAddress = 0x320,
            stackLow = stack[0],
            stackHigh = stack[1],
            execution = "actual Udon UI events -> GPU netlist and GPU RAM -> texture readback"
        });
    }
    public static async Task<object> VirtualKeyboard()
    {
        var panel = Panel(); var vm = Vm(panel);
        if (!Simulator().coordinator.HasControl()) Click("ClaimControl");
        await Until(() => panel.draftInput.IsInteractable(), "Controller cannot use the virtual keyboard.");
        Click("SetInputHex"); panel.draftInput.text = "0AB\n";
        vm.SetProgramVariable("virtualWord", 0);
        Click("InsertBit11"); Click("InsertBit0");
        Check(Int(vm, "virtualWord") == 0x801, "Bit keyboard ordering is incorrect.");
        Click("CommitWord");
        Check(panel.draftInput.text == "0AB\n0x801\n" && Int(vm, "virtualWord") == 0, "APPEND WORD did not append and clear the virtual word.");
        Click("BackspaceBit"); Check(panel.draftInput.text == "0AB\n", "DELETE LINE did not remove a trailing-newline word.");
        Click("BackspaceBit"); Check(panel.draftInput.text == "", "DELETE LINE did not remove the final line.");
        Click("SetInputBinary"); Click("InsertBit0"); Click("CommitWord");
        Check(panel.draftInput.text == "000000000001\n", "Binary keyboard did not append twelve bits.");
        Click("BackspaceBit"); Check(panel.draftInput.text == "", "Binary DELETE LINE did not remove the appended word.");
        return Save("ram-keyboard", new { passed = true, bitOrdering = true, appendHexAndBinary = true, clearsVirtualWord = true, deletesTrailingNewlineWord = true, inputPath = "actual Unity pointerClick -> backing Udon" });
    }
    public static async Task<object> RamProbe()
    {
        const int highAddress = 0x320;
        await LoadDraft("E24\n0AB\n920\nF00");
        byte[] initialRam = (byte[])Vm(Simulator()).GetProgramVariable("initialRam");
        Check(initialRam != null && initialRam.Length == CircuitLabSimulator.RamByteCount, "Probe program initial RAM is incomplete.");
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        Check(tablet != null, "State tablet missing.");
        Vm(tablet).SetProgramVariable("selectRamAddress", highAddress);
        Click("SelectRamAddress"); Click("BindSelectedRamByte");
        var sim = Vm(Simulator());
        await Until(() => ((int[])sim.GetProgramVariable("probeWidths"))[0] == 8, "RAM probe was not bound.");
        await Step(6);
        var history = await ReadFloats((RenderTexture)sim.GetProgramVariable("currentHistory"));
        int segment = ((int[])sim.GetProgramVariable("probeSegments"))[0];
        for (int sample = 0; sample < 12; sample++)
        {
            var record = history[sample]; int expected = sample == 11 ? 0xab : initialRam[highAddress];
            Check(record.g > .5f && Mathf.RoundToInt(record.r) == expected && Mathf.RoundToInt(record.b) == segment && Mathf.RoundToInt(record.a) == sample + 1, "High-page RAM probe waveform is not actual stable RAM at sample " + sample);
        }
        Check((await Ram())[highAddress] == 0xab, "High-page probe target differs from actual GPU RAM.");
        Click("FreezeWaveform"); await Quiescent();
        var frozen = await ReadFloats(Simulator().frozenHistory);
        Click("ReturnAllProbes"); await Quiescent();
        Check((await ReadFloats(Simulator().frozenHistory)).SequenceEqual(frozen), "Returning a RAM probe rewrote frozen history.");
        return Save("ram-probe", new { passed = true, address = highAddress, stableHalfSamples = 12, storeVisibleOnlyAfterExecEdge = true, highAddressVirtualRamPointReadsGpuMemory = true, frozenHistoryPreservedOnReturn = true });
    }
    public static async Task<object> StartPi()
    {
        var programs = (JArray)Data()["programs"];
        int index = ProgramIndex("pi-355-113");
        Check(index >= 0 && index < programs.Count, "Legacy pi-355-113 preset is missing.");
        var panel = Panel(); var p = Vm(panel); var sim = Vm(Simulator());
        if (!Simulator().coordinator.HasControl()) Click("ClaimControl");
        var browser = Object.FindObjectOfType<CircuitLabProgramBrowser>();
        if (browser != null)
        {
            var row = Object.FindObjectsOfType<CircuitLabProgramRow>().Single(r => r.browser == browser && r.programIndex == index);
            Click(row.GetComponent<CircuitLabButton>());
        }
        else p.SetProgramVariable("selectedPreset", index);
        int revision = Int(sim, "programRevision"); Click("LoadSelectedPreset");
        await Until(() => Int(sim, "programRevision") > revision, "PI preset did not load.");
        await Quiescent();
        for (int i = 0; i < 8; i++) Click("SpeedUp");
        await Quiescent(); Check(Convert.ToSingle(sim.GetProgramVariable("cyclesPerSecond")) == 256f, "Speed ceiling is not 256 cycles/s.");
        Click("Run");
        return new { started = true, preset = index, id = (string)programs[index]["id"], name = (string)programs[index]["name"], expectedText = (string)programs[index]["expectedText"], expectedCycles = programs[index]["expectedCycles"], targetHz = 256 };
    }
    public static async Task<object> PrepareReplica()
    {
        await LoadDraft("E24\n0AB\n920\n101\n920\nF00");
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        Vm(tablet).SetProgramVariable("selectRamAddress", 0x320);
        Click("SelectRamAddress"); Click("BindSelectedRamByte");
        await Step(6);
        Check((await Ram())[0x320] == 0xab, "Replica source high-page RAM write is missing.");
        Click("FreezeWaveform"); await Quiescent();
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        await Until(() => !Bool(sim, "readbackPending") && Int(terminal, "lastProcessedCycle") >= Int(sim, "cycleCount"), "Replica source readbacks did not settle.");
        return new { ready = true, cycle = Int(sim, "cycleCount"), bank3Address = 0x320, ramValue = 0xab, catchupRamValue = 0xac };
    }
    public static object PiStatus()
    {
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        return new { cycle = Int(sim, "cycleCount"), output = Str(terminal, "output"), actualHz = sim.GetProgramVariable("actualCyclesPerSecond"), targetHz = sim.GetProgramVariable("cyclesPerSecond"), halt = Int(sim, "observedHalt"), queued = Int(sim, "queuedCommandCount"), snapshotStage = Int(Vm(Simulator().coordinator), "snapshotCaptureStage"), errors = Int(terminal, "readbackErrors") };
    }
    public static async Task<object> RunPi()
    {
        await StartPi();
        DateTime started = DateTime.UtcNow;
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        string expectedOutput = (string)Data()["programs"][ProgramIndex("pi-355-113")]["expectedText"];
        float peakHz = 0;
        await Until(() =>
        {
            peakHz = Mathf.Max(peakHz, Convert.ToSingle(sim.GetProgramVariable("actualCyclesPerSecond")));
            return Int(sim, "observedHalt") == 1 && Str(terminal, "output") == expectedOutput;
        }, "Actual GPU PI program did not finish.", 45);
        var report = JObject.FromObject(await CompletePi());
        report["wallSeconds"] = (DateTime.UtcNow - started).TotalSeconds;
        report["peakObservedHz"] = peakHz;
        var piProgram = Data()["programs"][ProgramIndex("pi-355-113")];
        report["expectedCpuRunCycles"] = piProgram["expectedCycles"];
        report["clockContinuesAfterHalt"] = true;
        report["terminalReadbackErrors"] = Int(terminal, "readbackErrors");
        report["terminalMissedCycles"] = Int(terminal, "missedCycles");
        return Save("ram-pi-gpu", report);
    }
    public static object AuthoringLayout()
    {
        var panel = Panel(); var canvas = panel.transform.Find("ProgramCanvas");
        var input = (RectTransform)canvas.Find("DraftInput");
        Rect Bounds(RectTransform rect) => new Rect(rect.anchoredPosition - rect.sizeDelta * .5f, rect.sizeDelta);
        int keys = 0;
        foreach (Transform t in canvas)
        {
            if (!t.name.StartsWith("VIRTUAL_") && t.name != "LOAD_DRAFT") continue;
            Check(!Bounds(input).Overlaps(Bounds((RectTransform)t)), "Program key overlaps the input: " + t.name);
            keys++;
        }
        Check(keys == 15, "Program virtual keyboard is incomplete.");
        var request = Object.FindObjectsOfType<CircuitLabButton>(true).First(b => b.eventName == "RequestControl");
        var analyzer = request.transform.root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "MovableWaveformPanel");
        var wave = analyzer.GetComponentsInChildren<Transform>(true).Single(t => t.name == "WaveformScreen");
        foreach (string name in new[] { "RequestControl", "GrantControl", "ReleaseControl" })
        {
            var button = analyzer.GetComponentsInChildren<CircuitLabButton>(true).Single(b => b.eventName == name);
            float y = analyzer.InverseTransformPoint(button.transform.position).y;
            float waveTop = analyzer.InverseTransformPoint(wave.position).y + wave.localScale.y * .5f;
            Check(y - .12f > waveTop, "Shared toolbar is below the waveform: " + name);
        }
        Check(((RectTransform)canvas.Find("REQUEST_PROGRAM_CONTROL")).anchoredPosition.y > 300, "Program request is below the header.");
        var tablet = Object.FindObjectOfType<CircuitLabStateTablet>();
        Check(((RectTransform)tablet.transform.Find("StateTabletCanvas/TABLET_REQUEST_CONTROL")).anchoredPosition.y > 250, "Tablet request is below the header.");
        return Save("ram-ui-layout", new { passed = true, controlToolbarAboveWaveform = true, programAndTabletControlAtTop = true, keyboardControlsChecked = keys, noInputKeyboardOverlap = true });
    }
    public static object FinalAuthoring()
    {
        Check(!EditorApplication.isPlaying, "Leave the Editor stopped for delivery.");
        var simulator = Simulator(); var scene = simulator.gameObject.scene;
        Check(!scene.isDirty, "Authored scene has unsaved changes.");
        Check(!simulator.running && !simulator.testRunning && !simulator.historyFrozen && simulator.followLatest, "Authored CPU/analyzer defaults were changed by tests.");
        Check(simulator.coordinator.controlAuthorityId == -1 && !simulator.coordinator.manualTestMode, "Test control authority was persisted.");
        Check(simulator.programSelect == 0 && Panel().draftInput.text == "" && simulator.terminal.outputText.text == "", "A test program, draft or terminal was persisted.");
        int materials = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/CircuitLab/Materials" }))
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            Check(material.shader != null && material.shader.isSupported && !material.shader.name.Contains("InternalError"), "Invalid delivered shader: " + material.name);
            materials++;
        }
        var rom = simulator.programTexture.GetPixels32(); var ram = simulator.ramInitialTexture.GetPixels32();
        Check(simulator.programTexture.width == 512 && simulator.programTexture.height == 1
            && simulator.ramInitialTexture.width == 32 && simulator.ramInitialTexture.height == 32
            && simulator.restoreRamTexture.width == 32 && simulator.restoreRamTexture.height == 32
            && simulator.ramA.width == 32 && simulator.ramA.height == 32
            && simulator.ramB.width == 32 && simulator.ramB.height == 32
            && rom.Length == 512 && ram.Length == CircuitLabSimulator.RamByteCount,
            "Authored ROM/RAM texture dimensions changed.");
        for (int i = 0; i < 512; i++) Check((rom[i].r | (rom[i].g << 8)) == simulator.programWords[i], "Test ROM bytes were persisted at " + i);
        for (int i = 0; i < CircuitLabSimulator.RamByteCount; i++) Check(ram[i].r == simulator.initialRam[i], "Test RAM bytes were persisted at " + i);
        var floor = scene.GetRootGameObjects().Single(g => g.name == "CircuitLabWorld").transform.Find("Floor");
        Check(Mathf.Approximately(floor.localScale.x, 100) && Mathf.Approximately(floor.localScale.z, 100), "User floor scale was altered.");
        Check(Object.FindObjectsOfType<Camera>(true).Where(c => c.name.Contains("Preview")).All(c => !c.enabled), "Preview camera was left enabled.");
        return Save("ram-final-authoring", new { passed = true, editorStopped = true, sceneSaved = true, startsPausedAndLive = true, unclaimedControl = true, defaultPi = true, noTestDraftOrTerminal = true, validMaterialShaders = materials, initialProgramWordsCompared = 512, initialRamBytesCompared = CircuitLabSimulator.RamByteCount, userFloorPreserved = true });
    }
    public static async Task<object> CompletePi()
    {
        var sim = Vm(Simulator()); var terminal = Vm(Simulator().terminal);
        int index = Int(sim, "programSelect"); var program = Data()["programs"][index];
        string expected = (string)program["expectedText"];
        Check(Str(terminal, "output") == expected, "GPU PI output differs from independent RTL/gate result.");
        Click("Pause"); await Quiescent();
        int telemetry = Int(sim, "telemetryReadbackCount"); sim.SendCustomEvent("RequestTelemetry");
        await Until(() => Int(sim, "telemetryReadbackCount") > telemetry && !Bool(sim, "readbackPending"), "PI final telemetry missing.");
        Check(Int(sim, "observedHalt") == 1, "PI program did not execute HALT.");
        byte[] ram = await Ram(); int compared = 0;
        if (program["expectedFinalRam"] != null)
        {
            var reference = program["expectedFinalRam"].ToObject<byte[]>();
            Check(ram.SequenceEqual(reference), "GPU PI final RAM differs from independent RTL/gate result."); compared = ram.Length;
        }
        return Save("ram-pi-gpu", new { passed = true, algorithm = "355/113 decimal long division; first six PI decimals agree", program = Str(sim, "activeProgramName"), output = expected, writes = Int(terminal, "eventCount"), targetHz = sim.GetProgramVariable("cyclesPerSecond"), cycles = Int(sim, "cycleCount"), finalRamBytesCompared = compared, source = "actual synthesized CPU + separate GPU RAM + real OUT event readback", nativeClientAndRealMultiplayerNotExercised = true });
    }
}
