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
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;
using Object = UnityEngine.Object;

// Regressions use the actual Udon VM and GPU, never a CPU substitute simulator.
public static class ValidateCircuitLabOptimizations
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private static T[] Find<T>() where T : Component => Object.FindObjectsOfType<T>(true).Where(t => t.gameObject.scene.path == ScenePath).ToArray();
    private static UdonBehaviour VM(UdonSharpBehaviour proxy) => UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
    private static UdonBehaviour Sim() => VM(Find<CircuitLabSimulator>().Single());
    private static UdonBehaviour Network() => VM(Find<CircuitLabNetworkCoordinator>().Single());
    private static int Int(UdonBehaviour vm, string name) => Convert.ToInt32(vm.GetProgramVariable(name));
    private static bool Bool(UdonBehaviour vm, string name) => Convert.ToBoolean(vm.GetProgramVariable(name));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Event(UdonSharpBehaviour proxy, string name) => VM(proxy).SendCustomEvent(name);
    private static async Task Until(Func<bool> predicate, string message, int seconds = 15)
    {
        var deadline=DateTime.UtcNow.AddSeconds(seconds);
        while(!predicate()&&DateTime.UtcNow<deadline) await Task.Delay(20);
        Check(predicate(),message);
    }
    private static object Save(string name, object result)
    {
        Directory.CreateDirectory("Reports/CircuitLab");
        File.WriteAllText("Reports/CircuitLab/"+name+".json",JsonConvert.SerializeObject(result,Formatting.Indented));
        return result;
    }

    public static object RefreshAuthoredInitialLayout()
    {
        Check(!EditorApplication.isPlaying,"Refresh authored initial layout in Edit Mode.");
        var modules=Find<CircuitLabModule>().OrderBy(m=>m.logicalDepth).ThenBy(m=>m.moduleId).ToArray();
        foreach(var module in modules)module.RefreshVisibility();
        foreach(var module in modules)if(module.isVisible)module.EnsureAboveFloor();
        foreach(var module in modules)
        {
            module.CaptureInitialPose();UdonSharpEditorUtility.CopyProxyToUdon(module);
            EditorUtility.SetDirty(module);EditorUtility.SetDirty(VM(module));
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneByPath(ScenePath));
        return new{updatedInitialPoses=modules.Length,minimumVisibleFrameY=modules.Where(m=>m.isVisible).Select(m=>(m.expanded?m.expandedShell:m.foldedShell).GetComponentsInChildren<Renderer>(true).Min(r=>r.bounds.min.y)).Min(),environmentUntouched=true,saved=false};
    }

    public static object FinalAuthoredPresentation()
    {
        Check(!EditorApplication.isPlaying,"Adjust authored presentation in Edit Mode.");
        var modules=Find<CircuitLabModule>();var root=modules.Single(m=>m.logicalParent==null);
        var previous=root.transform.position;
        root.transform.position=new Vector3(-3.85f,1.8f,.5f);root.transform.rotation=Quaternion.identity;root.transform.localScale=Vector3.one*.65f;
        EditorUtility.SetDirty(root.transform);
        int labels=0;
        foreach(var button in Find<Button>())foreach(var label in button.GetComponentsInChildren<Text>(true))
        {
            if(label.name!="Label")continue;
            label.resizeTextForBestFit=true;label.resizeTextMinSize=Mathf.Max(6,label.fontSize-3);label.resizeTextMaxSize=label.fontSize;
            EditorUtility.SetDirty(label);labels++;
        }
        var refreshed=RefreshAuthoredInitialLayout();
        var waveform=Find<MeshRenderer>().FirstOrDefault(r=>r.sharedMaterial!=null&&r.sharedMaterial.shader!=null&&r.sharedMaterial.shader.name=="VrcCpuWorld/CircuitLabWaveform");
        Func<Vector3,object> vector=v=>new{x=v.x,y=v.y,z=v.z};
        return Save("final-presentation",new{adjusted=true,previousRootPosition=vector(previous),rootPosition=vector(root.transform.position),
            rootScale=vector(root.transform.localScale),rootExpandedWidth=root.expandedWidth,
            waveformPosition=waveform==null?null:vector(waveform.transform.position),buttonLabelsBestFit=labels,initialLayout=refreshed});
    }

    public static object CaptureFinalSpawn()
    {
        var camera=Find<Camera>().First(c=>c.name=="CircuitLabPreviewCamera");
        var position=camera.transform.position;var rotation=camera.transform.rotation;float fov=camera.fieldOfView;
        var oldTarget=camera.targetTexture;var oldActive=RenderTexture.active;
        var target=new RenderTexture(1600,900,24,RenderTextureFormat.ARGB32);target.Create();
        var pixels=new Texture2D(1600,900,TextureFormat.RGB24,false);
        try
        {
            camera.transform.position=new Vector3(0,1.7f,-3.3f);camera.transform.rotation=Quaternion.identity;camera.fieldOfView=90;
            camera.targetTexture=target;camera.Render();RenderTexture.active=target;
            pixels.ReadPixels(new Rect(0,0,1600,900),0,0);pixels.Apply();
            Directory.CreateDirectory("Reports/CircuitLab");File.WriteAllBytes("Reports/CircuitLab/optimized-spawn-view.png",pixels.EncodeToPNG());
            return new{captured=true,file="optimized-spawn-view.png",width=1600,height=900,editorStopped=!EditorApplication.isPlaying};
        }
        finally
        {
            camera.transform.position=position;camera.transform.rotation=rotation;camera.fieldOfView=fov;camera.targetTexture=oldTarget;
            RenderTexture.active=oldActive;target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(pixels);
        }
    }

    public static object SceneStructure()
    {
        var floor=SceneManager.GetSceneByPath(ScenePath).GetRootGameObjects().Single(g=>g.name=="CircuitLabWorld").transform.Find("Floor");
        Check(floor!=null,"User floor is missing.");
        Check((floor.localScale-new Vector3(100,.2f,100)).sqrMagnitude<.0001f,"User floor size was overwritten.");
        Check(Enumerable.Range(0,SceneManager.sceneCount).All(i=>!SceneManager.GetSceneAt(i).path.Contains("NetlistSmoke")),"Old smoke scene was reopened.");
        var modules=Find<CircuitLabModule>();
        var root=modules.Single(m=>m.logicalParent==null);
        Check(Mathf.Abs(Vector3.Dot(root.transform.forward,Vector3.up))<.5f,"Initial top-level board is still nearly horizontal.");
        var pickups=modules.Select(m=>m.GetComponent<VRCPickup>()).ToArray();
        Check(pickups.All(p=>p!=null&&p.version==VRCPickup.Version.Version_1_1&&p.proximity>=10&&p.ExactGrip!=null&&p.DisallowTheft),"Module pickup reach/version/grip/theft configuration is incomplete.");
        var owned=modules.SelectMany(m=>m.GetComponentsInChildren<Collider>(true))
            .Concat(Find<CircuitLabProbe>().SelectMany(p=>p.GetComponentsInChildren<Collider>(true)))
            .Concat(Find<VRCUiShape>().SelectMany(s=>s.GetComponents<Collider>())).Distinct().ToArray();
        var blockers=owned.Where(c=>!c.isTrigger&&(!Physics.GetIgnoreLayerCollision(c.gameObject.layer,9)||!Physics.GetIgnoreLayerCollision(c.gameObject.layer,10))).Select(c=>c.name+":"+LayerMask.LayerToName(c.gameObject.layer)).ToArray();
        Check(blockers.Length==0,"Experiment interaction colliders block players: "+string.Join(",",blockers.Take(10)));
        var invalidSync=Find<VRCObjectSync>().Where(s=>s.GetComponents<UdonBehaviour>().Any(vm=>vm.SyncIsManual)).Select(s=>s.name).ToArray();
        Check(invalidSync.Length==0,"SDK build blocker: ObjectSync shares a GameObject with Manual Udon: "+string.Join(",",invalidSync));
        var shapes=Find<VRCUiShape>();
        Check(shapes.Length>0,"Ray UI shapes are missing.");
        Check(shapes.All(s=>s.GetComponent<Canvas>()!=null&&s.GetComponent<GraphicRaycaster>()!=null&&s.GetComponent<BoxCollider>()!=null&&s.gameObject.layer!=5),"A UI shape lacks same-object Canvas/raycaster/collider or uses the UI layer.");
        var buttons=Find<Button>();
        Check(buttons.Length>0&&buttons.All(b=>b.navigation.mode==Navigation.Mode.None),"UI buttons require Navigation.None.");
        Check(Find<CircuitLabLayout>().Length==1&&Find<CircuitLabPlayerSetup>().Length==1&&Find<CircuitLabHierarchyTree>().Length==1,"Layout/tree/player setup is missing.");
        var network=Find<CircuitLabNetworkCoordinator>().Single();
        Check(!network.manualTestMode,"Production scene must not enable manual network test mode.");
        return Save("optimization-structure",new{passed=true,userFloorPreserved=true,modules=modules.Length,checkedInteractionColliders=owned.Length,playerBlockingColliders=blockers.Length,rayUiShapes=shapes.Length,uiButtons=buttons.Length,pickupReach=pickups.Min(p=>p.proximity),pickupVersion="1.1",nativeClientInputNotExercised=true});
    }

    public static async Task<object> ClaimControl()
    {
        Check(EditorApplication.isPlaying,"Enter Play Mode first.");
        await Until(()=>Networking.LocalPlayer!=null,"ClientSim player is missing.",35);
        Network().SendCustomEvent("RequestControl");
        await Until(()=>Bool(Network(),"ready")&&!Bool(Network(),"restorePending")&&Int(Network(),"snapshotCaptureStage")==0,"Shared experiment initialization is incomplete.",35);
        Check(Networking.LocalPlayer.GetJumpImpulse()>0,"Local player cannot jump: jump impulse is zero.");
        return Save("optimization-player-control",new{passed=true,playerId=Networking.LocalPlayer.playerId,jumpImpulse=Networking.LocalPlayer.GetJumpImpulse(),controlAuthorityId=Int(Network(),"controlAuthorityId"),snapshotRevision=Int(Network(),"snapshotRevision")});
    }

    private static float Lowest(CircuitLabModule module)
    {
        bool expanded=Bool(VM(module),"expanded");
        var shell=expanded?module.expandedShell:module.foldedShell;
        var renderers=shell.GetComponentsInChildren<Renderer>(true);
        Check(renderers.Length>0,"Module shell renderer is missing.");
        return renderers.Min(r=>r.bounds.min.y);
    }

    public static async Task<object> GeometryAndTree()
    {
        await ClaimControl();
        Sim().SendCustomEvent("Pause");
        await Until(()=>!Bool(Sim(),"running")&&!Bool(Sim(),"readbackPending"),"Pause did not settle.");
        int cycle=Int(Sim(),"cycleCount"),samples=Int(Sim(),"sampleCount");
        var root=Find<CircuitLabModule>().Single(m=>m.logicalParent==null);
        bool original=Bool(VM(root),"expanded");
        VM(root).Interact();await Task.Delay(100);
        Check(Bool(VM(root),"expanded")==original,"Native grab collider still toggles expansion through Interact.");
        var tree=Find<CircuitLabHierarchyTree>().Single();
        bool directoryInitiallyExpanded=((bool[])VM(tree).GetProgramVariable("directoryExpanded"))[0];
        var row=Find<CircuitLabHierarchyRow>().First(r=>r.module==root);
        Event(row,"ToggleDirectory");await Task.Delay(100);
        Check(((bool[])VM(tree).GetProgramVariable("directoryExpanded"))[0]!=directoryInitiallyExpanded,"Directory fold did not change list state.");
        Check(Bool(VM(root),"expanded")==original,"Directory fold unexpectedly changed 3D circuit expansion.");
        Event(row,"ToggleDirectory");
        float previousScroll=tree.scrollRect.verticalNormalizedPosition;
        Event(tree,"ScrollDown");await Task.Delay(100);
        Check(tree.scrollRect.verticalNormalizedPosition<previousScroll,"Hierarchy scroll control did not move the list.");
        var candidate=Find<CircuitLabModule>().First(m=>m.logicalParent!=null&&m.childModules.Length>0);
        Event(candidate,"DetachFromParent");
        candidate.transform.position=new Vector3(3,-1,1);
        candidate.transform.rotation=Quaternion.Euler(31,18,47);
        Event(candidate,"Expand");await Task.Delay(100);
        float lowest=Lowest(candidate);
        Check(lowest>=candidate.floorClearance-.001f,"Tilted expanded board enters the floor.");
        Event(candidate,"Collapse");
        Check(candidate.gameObject.activeInHierarchy,"A folded module disabled its network root.");
        var layout=Find<CircuitLabLayout>().Single();Event(layout,"ResetLayout");await Task.Delay(150);
        Check((root.transform.position-root.initialPosition).sqrMagnitude<.0001f,"Global layout reset did not restore initial root position.");
        Check((candidate.transform.position-candidate.initialPosition).sqrMagnitude<.0001f,"Global layout reset did not restore extracted module position.");
        Check(Bool(VM(candidate),"expanded")==candidate.initialExpanded,"Layout reset did not restore initial expansion.");
        Check(Int(Sim(),"cycleCount")==cycle&&Int(Sim(),"sampleCount")==samples,"Layout/tree operations advanced the CPU.");
        var handlePoint=root.transform.TransformPoint(root.pickupCollider.center);
        var ray=new Ray(handlePoint-root.transform.forward*8,root.transform.forward);
        RaycastHit hit;
        Check(root.pickupCollider.Raycast(ray,out hit,10),"An 8m ray cannot intersect the native grab handle.");
        return Save("optimization-geometry-tree",new{passed=true,tiltedExpandedMinimumY=lowest,directoryIndependentOfCircuit=true,scrollable=true,layoutResetRestoredInitialState=true,cpuStateUnaffected=true,handleRayHitDistance=hit.distance,nativeClientPickupNotExercised=true});
    }

    private static async Task<Color[]> Read(RenderTexture texture)
    {
        var completion=new TaskCompletionSource<Color[]>();
        AsyncGPUReadback.Request(texture,0,request=>{if(request.hasError)completion.TrySetException(new InvalidOperationException("Frozen waveform readback failed."));else completion.TrySetResult(request.GetData<Color>().ToArray());});
        if(await Task.WhenAny(completion.Task,Task.Delay(8000))!=completion.Task)throw new TimeoutException("Frozen waveform readback timed out.");
        return await completion.Task;
    }

    public static async Task<object> PrepareGpuReference()
    {
        await ClaimControl();var sim=Sim();var proxy=Find<CircuitLabSimulator>().Single();
        sim.SendCustomEvent("Pause");await Until(()=>!Bool(sim,"running")&&!Bool(sim,"readbackPending")&&Int(sim,"queuedCommandCount")==0,"GPU reference setup did not pause.");
        var root=Find<CircuitLabModule>().Single(m=>m.logicalParent==null);
        var probes=Find<CircuitLabProbe>().OrderBy(p=>p.channel).ToArray();
        int[][] channels={proxy.pcSignals,proxy.instructionSignals,proxy.accSignals,proxy.outSignals};
        for(int channel=0;channel<4;channel++)
        {
            var target=Find<CircuitLabSignalTarget>().First(t=>t.ownerModule==root&&t.isPort&&t.signalIds.SequenceEqual(channels[channel]));
            Event(probes[channel],"SelectProbe");VM(target).Interact();
        }
        await Until(()=>Int(sim,"queuedCommandCount")==0,"Standard reference probe bindings were not applied.");
        var ids=(int[])sim.GetProgramVariable("_probeIds");var widths=(int[])sim.GetProgramVariable("probeWidths");
        for(int channel=0;channel<4;channel++)Check(ids.Skip(channel*16).Take(widths[channel]).SequenceEqual(channels[channel]),"Standard reference probe ordering differs.");
        return new{passed=true,channels=new[]{"pc","instruction","acc","out"}};
    }

    public static async Task<object> PrepareProgramRegression()
    {
        await ClaimControl();var sim=Sim();sim.SendCustomEvent("Pause");
        await Until(()=>!Bool(sim,"running")&&!Bool(sim,"readbackPending")&&Int(sim,"queuedCommandCount")==0,"Program regression setup did not pause.");
        for(int i=0;i<4&&Int(sim,"programSelect")!=0;i++)
        {
            int previous=Int(sim,"programSelect");sim.SendCustomEvent("SelectProgram");
            await Until(()=>Int(sim,"programSelect")!=previous&&Int(sim,"queuedCommandCount")==0,"Program regression selection did not apply.");
        }
        Check(Int(sim,"programSelect")==0,"Program regression must begin at program zero.");
        sim.SetProgramVariable("cyclesPerSecond",2f);
        return new{passed=true,initialProgram=0,initialSpeed=2f};
    }

    public static async Task<object> ModuleAndProbeInteractions()
    {
        await ClaimControl();var sim=Sim();sim.SendCustomEvent("Pause");
        await Until(()=>!Bool(sim,"running")&&!Bool(sim,"readbackPending"),"Interaction setup did not pause.");
        int cycle=Int(sim,"cycleCount");var modules=Find<CircuitLabModule>().OrderBy(m=>m.logicalDepth).ToArray();
        var root=modules.Single(m=>m.logicalParent==null);var child=modules.First(m=>m.logicalParent==root);
        Event(root,"Expand");Event(child,"DetachFromParent");
        child.transform.position+=new Vector3(.31f,.22f,-.27f);child.transform.Rotate(0,19,7);
        var movedPosition=child.transform.position;var movedRotation=child.transform.rotation;
        Event(root,"Collapse");await Task.Delay(100);
        Check(child.gameObject.activeInHierarchy&&VM(child).enabled,"Folding disabled a network/Udon root.");
        Check(!Bool(VM(child),"isVisible")&&!child.interior.activeInHierarchy&&!child.expandedShell.activeInHierarchy&&!child.foldedShell.activeInHierarchy&&!child.pickupCollider.enabled,
            "Extracted descendants must hide their visuals and grab collider with the collapsed logical parent.");
        Event(root,"Expand");await Task.Delay(100);
        Check(Bool(VM(child),"isVisible")&&Vector3.Distance(child.transform.position,movedPosition)<.001f&&Quaternion.Angle(child.transform.rotation,movedRotation)<.01f,
            "Re-expansion changed extracted placement or did not restore visibility.");
        var probe=Find<CircuitLabProbe>().Single(p=>p.channel==0);Event(probe,"SelectProbe");
        var target=Find<CircuitLabSignalTarget>().First(t=>t.ownerModule==child&&t.isPort);
        VM(target).Interact();await Until(()=>(UdonBehaviour)VM(probe).GetProgramVariable("boundTarget")==VM(target),"Desktop target/probe binding failed.");
        Check((Int(VM(child),"boundChannelMask")&1)!=0,"Bound channel was not propagated to the module.");
        Event(child,"Collapse");await Task.Delay(100);
        Check((UdonBehaviour)VM(probe).GetProgramVariable("boundTarget")==VM(target),"Folding lost the physical probe binding.");
        Event(probe,"Detach");await Until(()=>VM(probe).GetProgramVariable("boundTarget")==null,"Explicit detach failed.");
        Event(Find<CircuitLabLayout>().Single(),"ResetLayout");await Task.Delay(150);
        Check(Int(sim,"cycleCount")==cycle,"Module/probe interaction advanced the CPU.");
        return Save("modules-probes",new{passed=true,path="Actual backing Udon events + SignalTarget.Interact -> Probe.BindTarget -> Simulator.BindProbe",
            networkRootsStayActive=true,hiddenDescendantVisuals=true,extractedPlacementPreserved=true,foldedBindingPreserved=true,explicitDetach=true,cpuUnaffected=true});
    }

    public static async Task<object> ProbeResetPreservesFreeze()
    {
        await ClaimControl();
        Sim().SendCustomEvent("Pause");await Until(()=>!Bool(Sim(),"running")&&!Bool(Sim(),"readbackPending"),"Pause failed.");
        Sim().SendCustomEvent("FreezeWaveform");await Until(()=>Bool(Sim(),"historyFrozen"),"Waveform did not freeze.");
        var texture=(RenderTexture)Sim().GetProgramVariable("frozenHistory");var before=await Read(texture);
        int cycle=Int(Sim(),"cycleCount"),frozenSamples=Int(Sim(),"frozenSampleCount");
        Event(Find<CircuitLabLayout>().Single(),"ReturnAllProbes");await Task.Delay(250);
        Check(Find<CircuitLabProbe>().All(p=>VM(p).GetProgramVariable("boundTarget")==null),"Probe reset left a channel bound.");
        Check(Find<CircuitLabProbe>().All(p=>p.rackAnchor!=null&&(p.transform.position-p.rackAnchor.position).sqrMagnitude<.0001f),"Probe reset did not return needles to the rack.");
        var after=await Read(texture);
        Check(before.SequenceEqual(after),"Probe reset changed frozen GPU waveform data.");
        Check(Bool(Sim(),"historyFrozen")&&Int(Sim(),"frozenSampleCount")==frozenSamples,"Probe reset changed frozen metadata.");
        Check(Int(Sim(),"cycleCount")==cycle,"Probe reset reset or advanced the CPU.");
        return Save("optimization-probe-reset",new{passed=true,channelsUnbound=4,frozenPixelsCompared=before.Length,frozenWaveformUnchanged=true,cpuUnaffected=true});
    }

    public static async Task<object> TerminalOutput()
    {
        await ClaimControl();
        var terminal=VM(Find<CircuitLabTerminal>().Single());
        int missedBefore=Int(terminal,"missedCycles"),errorsBefore=Int(terminal,"readbackErrors");
        var sim=Sim();sim.SendCustomEvent("Pause");
        await Until(()=>!Bool(sim,"running")&&!Bool(sim,"readbackPending"),"Terminal setup pause failed.");
        for(int i=0;i<4&&Int(sim,"programSelect")!=2;i++)
        {
            int before=Int(sim,"programSelect");sim.SendCustomEvent("SelectProgram");
            await Until(()=>Int(sim,"programSelect")!=before,"Program selection did not execute.",25);
        }
        Check(Int(sim,"programSelect")==2,"Text program is unavailable.");
        sim.SendCustomEvent("RestartExperiment");
        await Until(()=>Int(sim,"cycleCount")==0&&Int(sim,"queuedCommandCount")==0,"Text program restart failed.",25);
        for(int i=0;i<4;i++)sim.SendCustomEvent("Step");
        await Until(()=>Int(sim,"cycleCount")==4&&Int(terminal,"eventCount")==1,"First actual OUT write was not captured.");
        Check((string)terminal.GetProgramVariable("output")=="H","First text byte is incorrect.");
        sim.SendCustomEvent("ResetCircuit");
        await Until(()=>Int(sim,"cycleCount")==5,"Synchronous reset did not commit one cycle.");
        for(int i=0;i<4;i++)sim.SendCustomEvent("Step");
        await Until(()=>Int(sim,"cycleCount")==9&&Int(terminal,"eventCount")==2,"First write after reset was incorrectly deduplicated.");
        Check((string)terminal.GetProgramVariable("output")=="HH","Synchronous reset erased output or lost the repeated first byte.");
        sim.SendCustomEvent("RestartExperiment");
        await Until(()=>Int(sim,"cycleCount")==0&&Int(terminal,"eventCount")==0&&Int(sim,"queuedCommandCount")==0,"Fresh text program restart failed.",25);
        sim.SetProgramVariable("cyclesPerSecond",32f);sim.SendCustomEvent("Run");
        await Until(()=>Int(terminal,"eventCount")==6&&(string)terminal.GetProgramVariable("output")=="HELLO\n","Actual GPU OUT events did not produce HELLO with repeated L.",30);
        sim.SendCustomEvent("Pause");await Until(()=>!Bool(sim,"running")&&!Bool(sim,"readbackPending"),"Text program pause failed.");
        int start=Int(sim,"cycleCount");for(int i=0;i<16;i++)sim.SendCustomEvent("Step");
        await Until(()=>Int(sim,"cycleCount")==start+16&&Int(terminal,"lastProcessedCycle")>=start+16,"Post-HALT terminal sampling failed.",25);
        Check(Int(terminal,"eventCount")==6&&(string)terminal.GetProgramVariable("output")=="HELLO\n","HALT duplicated terminal output.");
        Check(Int(terminal,"missedCycles")==missedBefore&&Int(terminal,"readbackErrors")==errorsBefore,"Terminal event ring lost samples in this run.");
        bool aa=Bool(sim,"analyticAA");int cycle=Int(sim,"cycleCount");
        sim.SendCustomEvent("ToggleAA");await Until(()=>Bool(sim,"analyticAA")!=aa,"Waveform AA toggle did not execute.");
        Check(Int(sim,"cycleCount")==cycle,"Display AA toggle advanced the circuit.");
        sim.SendCustomEvent("ToggleAA");
        return Save("optimization-terminal",new{passed=true,text="HELLO\n",actualWriteEvents=6,repeatedCharacterRetained=true,firstWriteAfterResetRetained=true,haltDoesNotDuplicate=true,missedCycles=0,readbackErrors=0,aaToggleDoesNotChangeCircuit=true});
    }

    private static async Task<Color32[]> ReadState(UdonBehaviour sim)
    {
        var completion=new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request((RenderTexture)sim.GetProgramVariable("currentState"),0,request=>{if(request.hasError)completion.TrySetException(new InvalidOperationException("Replica state readback failed."));else completion.TrySetResult(request.GetData<Color32>().ToArray());});
        if(await Task.WhenAny(completion.Task,Task.Delay(8000))!=completion.Task)throw new TimeoutException("Replica state readback timed out.");
        return await completion.Task;
    }

    private static void CopySynced(UdonSharpBehaviour sourceProxy,UdonBehaviour source,UdonBehaviour target)
    {
        foreach(var field in sourceProxy.GetType().GetFields().Where(f=>f.GetCustomAttributes(false).Any(a=>a.GetType().Name=="UdonSyncedAttribute")))
        {
            object value=source.GetProgramVariable(field.Name);
            if(value is Array) value=((Array)value).Clone();
            target.SetProgramVariable(field.Name,value);
        }
    }

    private static int CompareVisibleHistory(Color[] a,Color[] b,int count,int capacity,int[] segments,int[] bindSamples)
    {
        int compared=0;
        for(int channel=0;channel<4;channel++)for(int slot=0;slot<capacity;slot++)
        {
            Color left=a[channel*capacity+slot],right=b[channel*capacity+slot];
            int li=Mathf.RoundToInt(left.a)-1,ri=Mathf.RoundToInt(right.a)-1;
            bool lv=left.g>.5f&&Mathf.RoundToInt(left.b)==segments[channel]&&li>=Mathf.Max(bindSamples[channel],Mathf.Max(0,count-capacity))&&li<count&&li%capacity==slot;
            bool rv=right.g>.5f&&Mathf.RoundToInt(right.b)==segments[channel]&&ri>=Mathf.Max(bindSamples[channel],Mathf.Max(0,count-capacity))&&ri<count&&ri%capacity==slot;
            Check(lv==rv,"Replica history validity mismatch channel="+channel+" slot="+slot);
            if(lv){Check(li==ri&&Mathf.RoundToInt(left.r)==Mathf.RoundToInt(right.r),"Replica history value/sample mismatch.");compared++;}
        }
        return compared;
    }

    public static async Task<object> SharedGpuReplica()
    {
        await ClaimControl();
        var sourceProxy=Find<CircuitLabSimulator>().Single();var source=VM(sourceProxy);
        var networkProxy=Find<CircuitLabNetworkCoordinator>().Single();var network=VM(networkProxy);
        source.SendCustomEvent("Pause");await Until(()=>!Bool(source,"running")&&!Bool(source,"readbackPending")&&Int(source,"queuedCommandCount")==0,"Replica setup pause failed.");
        source.SendCustomEvent("ResumeWaveform");await Until(()=>!Bool(source,"historyFrozen")&&Int(source,"queuedCommandCount")==0,"Replica setup must resume before capturing a new frozen window.");
        var rootModule=Find<CircuitLabModule>().Single(m=>m.logicalParent==null);
        var probes=Find<CircuitLabProbe>().OrderBy(p=>p.channel).ToArray();
        int[][] channels={sourceProxy.accSignals,sourceProxy.pcSignals,sourceProxy.instructionSignals,sourceProxy.outSignals};
        for(int channel=0;channel<4;channel++)
        {
            var target=Find<CircuitLabSignalTarget>().First(t=>t.ownerModule==rootModule&&t.isPort&&t.signalIds.SequenceEqual(channels[channel]));
            Event(probes[channel],"SelectProbe");VM(target).Interact();
        }
        await Until(()=>Int(source,"queuedCommandCount")==0&&((int[])source.GetProgramVariable("probeWidths")).All(w=>w>0),"Replica setup needs four real bound probe channels.");
        int priorCycle=Int(source,"cycleCount");for(int i=0;i<16;i++)source.SendCustomEvent("Step");
        await Until(()=>Int(source,"cycleCount")==priorCycle+16&&Int(source,"queuedStepCount")==0&&!Bool(source,"readbackPending"),"Replica frozen sample generation failed.",25);
        int completed=Int(source,"completedCommandCount");source.SendCustomEvent("FreezeWaveform");
        await Until(()=>Bool(source,"historyFrozen")&&Int(source,"completedCommandCount")>completed,"Replica frozen view failed.");
        int freezeSamples=Int(source,"frozenSampleCount");priorCycle=Int(source,"cycleCount");
        for(int i=0;i<8;i++)source.SendCustomEvent("Step");
        await Until(()=>Int(source,"cycleCount")==priorCycle+8&&Int(source,"queuedStepCount")==0&&!Bool(source,"readbackPending"),"Replica live sample generation failed.",25);
        Check(Int(source,"sampleCount")>freezeSamples,"Replica fixture must contain distinct frozen and live sample boundaries.");
        int revision=Int(network,"snapshotRevision");network.SendCustomEvent("RequestSnapshot");
        await Until(()=>Int(network,"snapshotRevision")>revision&&Int(network,"snapshotCaptureStage")==0,"Actual GPU snapshot capture failed.",30);
        var expectedState=await ReadState(source);var expectedLive=await Read((RenderTexture)source.GetProgramVariable("currentHistory"));var expectedFrozen=await Read(sourceProxy.frozenHistory);
        int expectedCycle=Int(source,"cycleCount"),expectedSamples=Int(source,"sampleCount");
        var testRoot=new GameObject("CircuitLabReplicaValidation");testRoot.SetActive(false);
        var owned=new System.Collections.Generic.List<Object>();
        try
        {
            var replicaProxy=Object.Instantiate(sourceProxy.gameObject,testRoot.transform).GetComponent<CircuitLabSimulator>();
            var replicaNetwork=Object.Instantiate(networkProxy.gameObject,testRoot.transform).GetComponent<CircuitLabNetworkCoordinator>();
            var replicaTerminal=Object.Instantiate(sourceProxy.terminal.gameObject,testRoot.transform).GetComponent<CircuitLabTerminal>();
            var replicaClock=Object.Instantiate(networkProxy.clock.gameObject,testRoot.transform).GetComponent<CircuitLabNetworkClock>();
            Func<Material,Material> mat=value=>{var copy=Object.Instantiate(value);owned.Add(copy);return copy;};
            Func<RenderTexture,RenderTexture> rt=value=>{var copy=Object.Instantiate(value);copy.Create();owned.Add(copy);return copy;};
            replicaProxy.simulationMaterial=mat(sourceProxy.simulationMaterial);replicaProxy.displayMaterial=mat(sourceProxy.displayMaterial);
            replicaProxy.historyMaterial=mat(sourceProxy.historyMaterial);replicaProxy.waveformMaterial=mat(sourceProxy.waveformMaterial);
            replicaProxy.displayMaterials=new Material[0];replicaProxy.stateA=rt(sourceProxy.stateA);replicaProxy.stateB=rt(sourceProxy.stateB);
            replicaProxy.historyA=rt(sourceProxy.historyA);replicaProxy.historyB=rt(sourceProxy.historyB);replicaProxy.frozenHistory=rt(sourceProxy.frozenHistory);
            replicaProxy.coordinator=replicaNetwork;replicaProxy.terminal=replicaTerminal;replicaProxy.simHudText=null;replicaProxy.waveHudText=null;replicaProxy.statusText=null;
            replicaNetwork.simulator=replicaProxy;replicaNetwork.terminal=replicaTerminal;replicaNetwork.clock=replicaClock;replicaNetwork.statusText=null;
            replicaNetwork.requestSlots=new CircuitLabControlRequest[0];replicaNetwork.manualTestMode=true;replicaNetwork.manualTestAuthority=false;
            replicaTerminal.simulator=replicaProxy;replicaTerminal.coordinator=replicaNetwork;replicaTerminal.outputText=null;replicaTerminal.statusText=null;
            replicaTerminal.captureMaterial=mat(sourceProxy.terminal.captureMaterial);replicaTerminal.eventA=rt(sourceProxy.terminal.eventA);replicaTerminal.eventB=rt(sourceProxy.terminal.eventB);
            replicaClock.coordinator=replicaNetwork;
            foreach(var proxy in new UdonSharpBehaviour[]{replicaProxy,replicaNetwork,replicaTerminal,replicaClock})
            {
                UdonManager.Instance.RegisterUdonBehaviour(VM(proxy));
            }
            // Play-mode proxy serialization writes the initialized Udon heap.
            // Register all inactive VMs first, then configure their real heaps.
            foreach(var proxy in new UdonSharpBehaviour[]{replicaProxy,replicaNetwork,replicaTerminal,replicaClock})
                UdonSharpEditorUtility.CopyProxyToUdon(proxy);
            Check(Bool(VM(replicaNetwork),"manualTestMode")&&!Bool(VM(replicaNetwork),"manualTestAuthority"),"Replica fixture authority was not copied into its Udon heap.");
            Check((RenderTexture)VM(replicaProxy).GetProgramVariable("stateA")==replicaProxy.stateA&&replicaProxy.stateA!=sourceProxy.stateA,
                "Replica fixture must use independent GPU state buffers.");
            testRoot.SetActive(true);var replica=VM(replicaProxy);var replicaNet=VM(replicaNetwork);var clock=VM(replicaClock);
            try{await Until(()=>replica.HasDoneStart&&Bool(replica,"_initialized"),"Second Udon GPU instance did not initialize.",25);}
            catch(Exception){throw new InvalidOperationException("Second GPU startup: started="+replica.HasDoneStart+" initialized="+replica.GetProgramVariable("_initialized")+" status="+replica.GetProgramVariable("statusCode")+" active="+replica.gameObject.activeInHierarchy+" enabled="+replica.enabled);}
            CopySynced(networkProxy,network,replicaNet);CopySynced(networkProxy.clock,VM(networkProxy.clock),clock);
            replicaNet.SendCustomEvent("ImportSnapshot");
            await Until(()=>Bool(replicaNet,"ready")&&!Bool(replicaNet,"restorePending")&&Int(replicaNet,"appliedSnapshotRevision")==Int(network,"snapshotRevision"),"Second GPU snapshot restore failed.",35);
            var restored=await ReadState(replica);int compared=sourceProxy.nodeCount;
            Check(expectedState.Take(compared).SequenceEqual(restored.Take(compared)),"Actual restored GPU netlist differs from authoritative state.");
            Check(Int(replica,"cycleCount")==expectedCycle&&Int(replica,"sampleCount")==expectedSamples,
                "Replica cycle/sample boundary mismatch: expected="+expectedCycle+"/"+expectedSamples+
                " actual="+Int(replica,"cycleCount")+"/"+Int(replica,"sampleCount")+
                " snapshot="+Int(replicaNet,"snapshotCycle")+"/"+Int(replicaNet,"snapshotSamples")+
                " clock="+Int(clock,"cycle")+"/"+Int(clock,"samples")+
                " manual="+Bool(replicaNet,"manualTestMode")+" authority="+Bool(replicaNet,"manualTestAuthority"));
            int liveCompared=CompareVisibleHistory(expectedLive,await Read((RenderTexture)replica.GetProgramVariable("currentHistory")),expectedSamples,sourceProxy.historyCapacity,(int[])network.GetProgramVariable("snapshotSegments"),(int[])network.GetProgramVariable("snapshotBindSamples"));
            int frozenCompared=CompareVisibleHistory(expectedFrozen,await Read(replicaProxy.frozenHistory),Int(network,"snapshotFrozenSamples"),sourceProxy.historyCapacity,(int[])network.GetProgramVariable("snapshotFrozenSegments"),(int[])network.GetProgramVariable("snapshotFrozenBindSamples"));
            Check(liveCompared>0&&frozenCompared>0,"Replica restoration must compare real valid live and frozen samples: live="+liveCompared+" frozen="+frozenCompared+
                " gValid="+expectedLive.Count(v=>v.g>.5f)+"/"+expectedFrozen.Count(v=>v.g>.5f)+
                " samples="+expectedSamples+"/"+Int(network,"snapshotFrozenSamples")+
                " widths="+string.Join(",",(int[])network.GetProgramVariable("snapshotWidths"))+
                " segments="+string.Join(",",(int[])network.GetProgramVariable("snapshotSegments"))+
                " binds="+string.Join(",",(int[])network.GetProgramVariable("snapshotBindSamples"))+
                " liveMax="+expectedLive.OrderByDescending(v=>v.a).First());
            int denied=Int(replicaNet,"deniedCommands"),before=Int(replica,"cycleCount");replica.SendCustomEvent("Step");await Task.Delay(100);
            Check(Int(replica,"cycleCount")==before&&Int(replicaNet,"deniedCommands")==denied+1,"Non-controller changed the replica experiment.");
            var sourceTerminal=VM(sourceProxy.terminal);var targetTerminal=VM(replicaTerminal);
            Check((string)sourceTerminal.GetProgramVariable("output")== (string)targetTerminal.GetProgramVariable("output")&&Int(sourceTerminal,"eventCount")==Int(targetTerminal,"eventCount"),"Replica terminal snapshot differs.");
            for(int i=0;i<16;i++)source.SendCustomEvent("Step");await Until(()=>Int(source,"cycleCount")==expectedCycle+16,"Authoritative burst failed.",25);
            var sourceClock=VM(networkProxy.clock);
            await Until(()=>Int(sourceClock,"cycle")==Int(source,"cycleCount"),"Authoritative clock publication did not reach the completed burst.",25);
            CopySynced(networkProxy.clock,sourceClock,clock);
            try{await Until(()=>Int(replica,"cycleCount")==Int(source,"cycleCount"),"Replica did not catch up through local GPU cycles.",25);}
            catch(Exception){throw new InvalidOperationException("Replica catchup: source="+Int(source,"cycleCount")+" replica="+Int(replica,"cycleCount")+
                " clock="+Int(clock,"cycle")+" epochs="+Int(source,"sharedEpoch")+"/"+Int(replica,"sharedEpoch")+"/"+Int(clock,"epoch")+
                " ready="+Bool(replicaNet,"ready")+" restore="+Bool(replicaNet,"restorePending")+" initialized="+Bool(replica,"_initialized"));}
            Check((await ReadState(source)).Take(compared).SequenceEqual((await ReadState(replica)).Take(compared)),"Replica GPU diverged during cycle catch-up.");
            return Save("optimization-shared-gpu",new{passed=true,independentGpuBuffers=true,nodesCompared=compared,liveHistoryValuesCompared=liveCompared,frozenHistoryValuesCompared=frozenCompared,restoredCycle=expectedCycle,catchupCycles=16,permissionDenialVerified=true,terminalSnapshotVerified=true,transport="Synced payload copied between two actual Udon/GPU instances in one Editor; real multiplayer transport and native ownership transfer not exercised."});
        }
        finally{Object.Destroy(testRoot);foreach(var value in owned){if(value is RenderTexture)((RenderTexture)value).Release();Object.Destroy(value);}await Task.Delay(100);}
    }
}
