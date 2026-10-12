using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using VRC.Udon;
using Object = UnityEngine.Object;

public static class ValidateCircuitLabTeaching
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static UdonBehaviour Vm(UdonSharpBehaviour p) => UdonSharpEditorUtility.GetBackingUdonBehaviour(p);
    private static CircuitLabSimulator Sim() => Object.FindObjectOfType<CircuitLabSimulator>();
    private static int Int(UdonBehaviour v, string f) => Convert.ToInt32(v.GetProgramVariable(f));
    private static bool Bool(UdonBehaviour v, string f) => Convert.ToBoolean(v.GetProgramVariable(f));
    private static bool HasControl()
    {
        var vm=Vm(Sim().coordinator);
        if(Bool(vm,"manualTestMode")) return Bool(vm,"manualTestAuthority");
        return VRC.SDKBase.Networking.LocalPlayer!=null && Int(vm,"controlAuthorityId")==VRC.SDKBase.Networking.LocalPlayer.playerId;
    }
    private static int RamByte(int address)
    {
        var v=Vm(Sim()); var bytes=v.GetProgramVariable("observedRam") as byte[];
        return !Bool(v,"observedRamStable") || bytes==null || address<0 || address>=bytes.Length ? -1 : bytes[address];
    }
    private static JObject Data() => JObject.Parse(AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/CircuitLab/Data/gpu-netlist.json").text);
    private static object Save(string name, object result) { Directory.CreateDirectory("Reports/CircuitLab"); File.WriteAllText("Reports/CircuitLab/" + name + ".json", JsonConvert.SerializeObject(result, Formatting.Indented)); return result; }
    private static async Task Until(Func<bool> test, string message, int seconds=45)
    { var end=DateTime.UtcNow.AddSeconds(seconds); while (!test()) { Check(DateTime.UtcNow<end,message); await Task.Delay(50); } }
    private static void Click(UdonSharpBehaviour target, string name)
    {
        var button=Object.FindObjectsOfType<CircuitLabButton>(true).First(b=>b.target==target && b.eventName==name);
        Check(button.uiButton!=null && button.uiButton.IsInteractable(),"Non-interactable button: "+name);
        Check(button.uiButton.onClick.GetPersistentTarget(0)==Vm(button),"Button not wired to Udon: "+name);
        ExecuteEvents.Execute(button.gameObject, new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left}, ExecuteEvents.pointerClickHandler);
    }
    private static void ClickLanguage(CircuitLabLocalization locale, string eventName)
    {
        var button=locale.transform.Find("AboutPanel/LocaleCanvas"); // locale is on world root
        var ui=button.GetComponentsInChildren<Button>(true).Single(b=>b.onClick.GetPersistentEventCount()>0 && b.onClick.GetPersistentTarget(0)==Vm(locale) && b.onClick.GetPersistentMethodName(0)=="SendCustomEvent" && b.name== (eventName=="SetChinese" ? "CHINESE" : eventName=="SetJapanese" ? "JAPANESE" : "ENGLISH"));
        Check(ui.IsInteractable(),"Local language button unavailable.");
        ExecuteEvents.Execute(ui.gameObject, new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left}, ExecuteEvents.pointerClickHandler);
    }
    public static object Structure()
    {
        Check(!EditorApplication.isPlaying,"Run authoring structure in Edit Mode.");
        var sim=Sim(); Check(sim.initialRam.Length==1024 && sim.ramA.width==32 && sim.ramA.height==32,"RAM not 1KiB/32x32.");
        Check(sim.ramReadAddressSignals.Length==10 && sim.ramWriteAddressSignals.Length==10 && sim.bankSignals.Length==2,"BANK bus mapping missing.");
        var manual=Object.FindObjectOfType<CircuitLabIsaManual>(); var display=Object.FindObjectOfType<CircuitLabOutputDisplay>();
        Check(manual.pagesEn.Length==39 && manual.pagesZh.Length==39 && manual.pagesJa.Length==39,"Missing ISA pages.");
        Check(manual.localization!=null && display.localization!=null,"Local language references missing.");
        Check(manual.GetComponent<VRC.SDK3.Components.VRCObjectSync>()==null,"ISA manual state shares an object with VRCObjectSync.");
        Check(manual.GetComponentInParent<CircuitLabMovableTool>()!=null,"ISA state lost its movable panel.");
        Check(manual.page==0 && manual.localization.languageOverride==-1,"A test page or local language override was saved.");
        Check(!Object.FindObjectsOfType<VRC.SDK3.Components.VRCObjectSync>(true)
            .Any(s=>s.GetComponents<UdonBehaviour>().Any(v=>v.SyncIsManual)),"Manual Udon state is mixed with VRCObjectSync.");
        foreach (string guid in AssetDatabase.FindAssets("t:Shader",new[]{"Assets/CircuitLab/Shaders"}))
            Check(!ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid))),"CircuitLab shader compiler error.");
        var layout=Object.FindObjectOfType<CircuitLabLayout>();
        foreach(var name in new[]{"IsaManualPanel","LedOutputPanel","LifeOutputPanel"}) Check(layout.tools.Any(t=>t.name==name),"New panel not included in layout reset: "+name);
        Check(display.lifeProgram>=0,"Life program not mapped.");
        var handles=Object.FindObjectsOfType<CircuitLabModule>(true);
        foreach(var module in handles)
        {
            Check(module.grabText==null || !module.grabText.gameObject.activeSelf,"GRAB text remains.");
            Check(module.grabVisual.localScale.y>module.grabVisual.localScale.x,"Handle is not vertical.");
            var material=module.grabVisual.GetComponent<Renderer>().sharedMaterial;
            Check(material.shader.name=="VrcCpuWorld/CircuitLabHandle" && material.color.a<1,"Handle not transparent.");
        }
        Check(Object.FindObjectsOfType<CircuitLabProgramPanel>().Single().isaText==null,"Dense embedded ISA text remains.");
        var floor=sim.transform.root.Find("Floor"); Check(floor.localScale.x==100 && floor.localScale.z==100,"Floor changed.");
        return Save("teaching-structure",new{passed=true,ramBytes=1024,ramTexture="32x32",isaPages=39,verticalHandles=handles.Length,lifeProgram=display.lifeProgram,programCount=sim.programCount,localLanguageAuto=true,isaSharedStateSeparate=true,manualSyncObjectConflicts=0,shaderErrors=0});
    }
    public static async Task<object> ManualAndLocale()
    {
        var sim=Sim(); var manual=Object.FindObjectOfType<CircuitLabIsaManual>(); var vm=Vm(manual); var locale=manual.localization; var lv=Vm(locale);
        if(!HasControl()) Vm(sim.coordinator).SendCustomEvent("RequestControl");
        await Until(()=>HasControl(),"Cannot use teaching controls.");
        Click(manual,"Flags"); await Until(()=>Int(vm,"page")==4,"FLAGS did not open.");
        ClickLanguage(locale,"SetChinese"); await Until(()=>Int(lv,"languageIndex")==1 && manual.bodyText.text.Contains("没有借位"),"Chinese/manual language failed.");
        int shared=Int(vm,"page"); ClickLanguage(locale,"SetJapanese"); await Until(()=>Int(lv,"languageIndex")==2 && manual.bodyText.text.Contains("借りなし"),"Japanese/manual language failed.");
        Check(Int(vm,"page")==shared,"Local language changed shared page.");
        ClickLanguage(locale,"SetEnglish"); await Until(()=>Int(lv,"languageIndex")==0 && manual.bodyText.text.Contains("NO BORROW"),"English/manual language failed.");
        Click(manual,"NextPage"); await Until(()=>Int(vm,"page")==5,"NEXT did not turn page.");
        Click(manual,"Overview"); await Until(()=>Int(vm,"page")==0,"OVERVIEW did not reset page.");
        var tablet=Object.FindObjectOfType<CircuitLabStateTablet>();var tv=Vm(tablet);
        Click(tablet,"ShowRam"); Click(tablet,"NextPage"); Click(tablet,"NextPage"); Click(tablet,"NextPage");
        await Until(()=>Int(tv,"page")==3 && tablet.ramCellLabels[255].text.StartsWith("3FF"),"RAM page3 label mapping failed.");
        var cell=tablet.ramCellObjects[255].GetComponent<CircuitLabTabletCell>();Vm(cell).SendCustomEvent("Select");
        await Until(()=>Int(tv,"selectRamAddress")==1023,"Last page selection not absolute.");
        Click(tablet,"ToggleRawRam"); await Task.Delay(100);
        Check(tablet.displayMaterial.GetInt("_RawRam")==1,"Raw full texture mode missing.");
        var tool=manual.GetComponentInParent<CircuitLabMovableTool>();tool.transform.position+=new Vector3(1,.5f,0);
        Vm(Object.FindObjectOfType<CircuitLabLayout>()).SendCustomEvent("ResetLayout"); await Task.Delay(250);
        Check(Vector3.Distance(tool.transform.position,tool.initialPosition)<.01f,"ISA panel layout reset failed.");
        return Save("teaching-manual-locale",new{passed=true,sharedPagePreservedAcrossLanguage=true,languageSwitches=3,lastRamAddress=Int(tv,"selectRamAddress"),panelReset=true});
    }
    public static async Task<object> StartPreset(string id)
    {
        var programs=Data()["programs"].Cast<JObject>().ToArray(); int index=Array.FindIndex(programs,p=>(string)p["id"]==id);
        Check(index>=0,"Unknown preset: "+id); var sim=Sim();var sv=Vm(sim);
        Check(HasControl(),"Acquire control before running preset tests.");
        if(Bool(sv,"running")) { sv.SendCustomEvent("Pause"); await Until(()=>!Bool(sv,"running"),"Pause failed."); }
        int rev=Int(sv,"programRevision"); Vm(Object.FindObjectOfType<CircuitLabProgramBrowser>()).SetProgramVariable("selectedProgram",index);
        Vm(Object.FindObjectOfType<CircuitLabProgramBrowser>()).SendCustomEvent("LoadSelectedPreset");
        await Until(()=>Int(sv,"programRevision")>rev && Int(sv,"queuedCommandCount")==0,"Preset load failed.");
        sv.SetProgramVariable("cyclesPerSecond",1024f); sv.SendCustomEvent("Run");
        return new{started=id,index,expectedCycles=programs[index]["expectedCycles"],expectedText=programs[index]["expectedText"]};
    }
    public static object PresetStatus()
    {
        var sim=Sim();var v=Vm(sim);var terminal=Vm(sim.terminal);
        return new{cycle=Int(v,"cycleCount"),pc=Int(v,"observedPc"),bank=Int(v,"observedBank"),halt=Int(v,"observedHalt"),running=Bool(v,"running"),actualHz=v.GetProgramVariable("actualCyclesPerSecond"),program=v.GetProgramVariable("activeProgramName"),output=terminal.GetProgramVariable("output"),lifeGeneration=((byte[])v.GetProgramVariable("observedRam"))[225]};
    }
    public static async Task<object> LifeRunningDisplay()
    {
        var v=Vm(Sim()); var display=Object.FindObjectOfType<CircuitLabOutputDisplay>();
        await Until(()=>Bool(v,"running") && Int(v,"programSelect")==display.lifeProgram && Int(v,"observedRamCycle")>0,"Life needs a completed GPU sample while RUN is active.");
        await Task.Delay(250);
        Check(!display.lifeStatus.text.Contains("-1"),"Life display lost its last completed RAM sample while running.");
        return Save("teaching-life-running-display",new{passed=true,running=Bool(v,"running"),cycle=Int(v,"cycleCount"),sampleCycle=Int(v,"observedRamCycle"),status=display.lifeStatus.text});
    }
    public static async Task<object> CheckCompletedPreset()
    {
        var sim=Sim();var v=Vm(sim);var programs=Data()["programs"].Cast<JObject>().ToArray(); int index=Int(v,"programSelect");Check(index>=0 && index<programs.Length,"Not a preset.");
        Check(Int(v,"observedHalt")==1,"Preset has not halted.");
        await Task.Delay(300);string actual=(string)Vm(sim.terminal).GetProgramVariable("output");string expected=(string)programs[index]["expectedText"];
        Check(actual==expected,"GPU terminal mismatch: "+actual+" expected "+expected);
        return Save("teaching-gpu-"+(string)programs[index]["id"],new{passed=true,id=(string)programs[index]["id"],cycles=Int(v,"cycleCount"),output=actual,bank=Int(v,"observedBank")});
    }
    private static async Task<Color32[]> ReadPixels(RenderTexture texture)
    {
        var task=new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture,0,r=> { if(r.hasError) task.TrySetException(new InvalidOperationException("GPU readback failed")); else task.TrySetResult(r.GetData<Color32>().ToArray()); });
        if(await Task.WhenAny(task.Task,Task.Delay(10000))!=task.Task) throw new TimeoutException("GPU readback timed out");
        return await task.Task;
    }
    public static async Task<object> CapturePresetState()
    {
        var sim=Sim();var v=Vm(sim);v.SendCustomEvent("Pause");
        await Until(()=>!Bool(v,"running") && Int(v,"queuedCommandCount")==0 && Int(v,"queuedStepCount")==0,"Capture pause failed.");
        var state=await ReadPixels((RenderTexture)v.GetProgramVariable("currentState"));
        var ram=await ReadPixels((RenderTexture)v.GetProgramVariable("currentRam"));
        var data=Data();int index=Int(v,"programSelect");Check(index>=0,"Capture requires a preset.");string id=(string)data["programs"][index]["id"];
        var registers=new JObject();foreach(string field in new[]{"pc","instruction","phase","acc","x","sp","out","zero","carry","halt","bank","terminal_sequence"})
        {
            int[] ids=data["ports"][field].ToObject<int[]>();int value=0;for(int bit=0;bit<ids.Length;bit++)if(state[ids[bit]].r>127)value|=1<<bit;registers[field]=value;
        }
        return Save("teaching-state-"+id,new{id,cycles=Int(v,"cycleCount"),registers,ram=ram.Select(c=>(int)c.r).ToArray(),terminal=Vm(sim.terminal).GetProgramVariable("output"),actualHz=v.GetProgramVariable("actualCyclesPerSecond")});
    }
    public static async Task<object> CheckLifeFrame()
    {
        var sim=Sim();var v=Vm(sim);v.SendCustomEvent("Pause");await Until(()=>!Bool(v,"running") && Int(v,"queuedCommandCount")==0,"Life pause failed.");await Task.Delay(300);
        var display=Object.FindObjectOfType<CircuitLabOutputDisplay>();int generation=RamByte(display.generationAddress); int selector=RamByte(display.selectorAddress);
        if(generation>=0 && selector>=0 && (generation & 1)!=selector) generation=(generation+1)&255;
        Check(generation>0 && generation<20,"Life generation out of verification range.");
        var programs=Data()["programs"].Cast<JObject>().ToArray();var initial=programs[display.lifeProgram]["initialRam"].ToObject<byte[]>();var expected=initial.Skip(display.bufferA).Take(64).ToArray();
        for(int g=0;g<generation;g++)
        {
            var next=new byte[64];for(int y=0;y<8;y++)for(int x=0;x<8;x++)
            {
                int count=0;for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++) {int nx=x+dx,ny=y+dy;if((dx!=0||dy!=0)&&nx>=0&&nx<8&&ny>=0&&ny<8)count+=expected[ny*8+nx];}
                next[y*8+x]=(byte)(count==3 || expected[y*8+x]!=0 && count==2 ? 1:0);
            }expected=next;
        }
        int front=RamByte(display.selectorAddress)==0 ? display.bufferA:display.bufferB;
        var actual=new byte[64];for(int i=0;i<64;i++){int b=RamByte(front+i);Check(b>=0,"Stable RAM sample unavailable.");actual[i]=(byte)b;}
        Check(actual.SequenceEqual(expected),"GPU Life grid differs from independent Conway generation.");
        return Save("teaching-gpu-life",new{passed=true,generation,front,cpuCycles=Int(v,"cycleCount"),cells=actual,rule="B3/S23 closed boundary"});
    }
}
