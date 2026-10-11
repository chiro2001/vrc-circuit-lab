using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

// Incremental repair: modify only existing trace meshes and card pickup settings.
public static class RepairCircuitLabVisuals
{
    private const string Scene = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private static CircuitLabModule[] Modules() => Object.FindObjectsOfType<CircuitLabModule>(true).Where(m=>m.gameObject.scene.path==Scene).ToArray();
    private static IEnumerable<int> Junctions(Mesh mesh)
    {
        var triangles=mesh.triangles;
        for(int offset=0;offset+48<=triangles.Length;offset+=3)
        {
            int centre=triangles[offset];bool fan=centre+17<mesh.vertexCount;
            for(int segment=0;segment<16&&fan;segment++)
                fan=triangles[offset+segment*3]==centre&&triangles[offset+segment*3+1]==centre+segment+1&&triangles[offset+segment*3+2]==centre+segment+2;
            if(!fan)continue;
            yield return centre;offset+=45;
        }
    }
    private static Vector3 Expected(CircuitLabModule module,int signal)
    {
        var target=module.GetComponentsInChildren<CircuitLabSignalTarget>(true).FirstOrDefault(t=>t.ownerModule==module&&t.name=="Pin_"+signal+"_Q");
        if(target!=null)
        {
            var point=module.transform.InverseTransformPoint(target.transform.position);
            point.z=-.135f-(signal%3)*.009f;return point;
        }
        for(int index=0;index<module.portTransforms.Length;index++)
        {
            var port=module.portTransforms[index].GetComponent<CircuitLabSignalTarget>();
            if(port==null||!port.signalIds.Contains(signal))continue;
            var point=module.expandedPortPositions[index];
            bool clock=port.name=="Port_clk"&&module.interior.GetComponentsInChildren<CircuitLabSignalTarget>(true).Any(t=>t.name.EndsWith("_CLK"));
            point.z=clock?-.174f:-.135f-(signal%3)*.009f;return point;
        }
        throw new InvalidOperationException("Missing branch endpoint "+module.modulePath+" signal="+signal);
    }
    private static object Save(string name,object report)
    {
        Directory.CreateDirectory("Reports/CircuitLab");File.WriteAllText("Reports/CircuitLab/"+name+".json",JsonConvert.SerializeObject(report,Formatting.Indented));return report;
    }
    private static void Segment(List<Vector3> vertices,List<Vector2> uv,List<Color> colors,List<int> indices,Vector3 a,Vector3 b,int signal,float thickness)
    {
        if((b-a).sqrMagnitude<.00000001f)return;
        Vector3 direction=(b-a).normalized,normal=Vector3.Cross(direction,Vector3.forward).normalized;
        if(normal.sqrMagnitude<.01f)normal=Vector3.right;
        Vector3 binormal=Vector3.Cross(direction,normal).normalized*thickness*.5f;normal*=thickness*.5f;
        int start=vertices.Count;
        foreach(var point in new[]{a-normal-binormal,a+normal-binormal,a+normal+binormal,a-normal+binormal,b-normal-binormal,b+normal-binormal,b+normal+binormal,b-normal+binormal})
        {vertices.Add(point);uv.Add(new Vector2(signal,0));colors.Add(new Color(.65f,.65f,.65f));}
        foreach(int index in new[]{0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0})indices.Add(start+index);
    }
    public static object ConnectOutputPorts()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Author output routes in Edit Mode.");
        int routes=0;
        foreach(var module in Modules())
        {
            var vertices=new List<Vector3>();var uv=new List<Vector2>();var colors=new List<Color>();var indices=new List<int>();
            var pins=module.interior.GetComponentsInChildren<CircuitLabSignalTarget>(true).Where(t=>t.ownerModule==module&&t.name.EndsWith("_Q")).ToArray();
            for(int portIndex=0;portIndex<module.portTransforms.Length;portIndex++)
            {
                var port=module.portTransforms[portIndex].GetComponent<CircuitLabSignalTarget>();
                var label=port.transform.Find("PortLabel").GetComponent<UnityEngine.UI.Text>().text;
                if(label.StartsWith("> "))continue;
                foreach(int signal in port.signalIds)
                {
                    var pin=pins.FirstOrDefault(p=>p.signalIds.Contains(signal));if(pin==null)continue;
                    Vector3 a=module.transform.InverseTransformPoint(pin.transform.position),b=module.expandedPortPositions[portIndex];
                    float z=-.135f-(signal%3)*.009f,lane=(a.x+b.x)*.5f;
                    var path=new[]{a,new Vector3(a.x,a.y,z),new Vector3(lane,a.y,z),new Vector3(lane,b.y,z),new Vector3(b.x,b.y,z),b};
                    for(int i=1;i<path.Length;i++)Segment(vertices,uv,colors,indices,path[i-1],path[i],signal,.006f);
                    routes++;
                }
            }
            string assetPath="Assets/CircuitLab/Meshes/Module_"+module.moduleId+"_Outputs.asset";
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if(mesh==null){mesh=new Mesh();AssetDatabase.CreateAsset(mesh,assetPath);}else Undo.RegisterCompleteObjectUndo(mesh,"Connect module output ports");
            mesh.Clear();mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.SetColors(colors);mesh.SetTriangles(indices,0);mesh.RecalculateBounds();EditorUtility.SetDirty(mesh);
            var existing=module.interior.transform.Find("OutputPortTraces");GameObject go;
            if(existing==null){go=new GameObject("OutputPortTraces",typeof(MeshFilter),typeof(MeshRenderer));go.transform.SetParent(module.interior.transform,false);go.layer=17;Undo.RegisterCreatedObjectUndo(go,"Connect module output ports");}else go=existing.gameObject;
            go.GetComponent<MeshFilter>().sharedMesh=mesh;go.GetComponent<MeshRenderer>().sharedMaterial=module.interior.transform.Find("OrthogonalTraces").GetComponent<MeshRenderer>().sharedMaterial;
        }
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetSceneByPath(Scene));
        return Save("output-port-routes",new{connectedRoutes=routes,layoutAndSimulationUntouched=true});
    }
    public static object Audit()
    {
        int count=0,detached=0;float maxError=0;
        foreach(var module in Modules())
        {
            var mesh=module.interior.transform.Find("OrthogonalTraces").GetComponent<MeshFilter>().sharedMesh;
            var vertices=mesh.vertices;var uv=mesh.uv;
            foreach(int centre in Junctions(mesh))
            {
                count++;float error=(vertices[centre]-Expected(module,Mathf.RoundToInt(uv[centre].x))).magnitude;
                maxError=Mathf.Max(maxError,error);if(error>.0001f)detached++;
            }
        }
        var cards=Modules().Select(m=>m.GetComponent<VRCPickup>()).Concat(Object.FindObjectsOfType<CircuitLabMovableTool>(true).Where(t=>t.gameObject.scene.path==Scene).Select(t=>t.GetComponent<VRCPickup>())).ToArray();
        return Save("visual-pickup-audit",new{sceneDirty=UnityEngine.SceneManagement.SceneManager.GetSceneByPath(Scene).isDirty,junctions=count,detachedJunctions=detached,maximumLocalGap=maxError,cardPickups=cards.Length,forcedGripCards=cards.Count(p=>p.orientation!=VRC_Pickup.PickupOrientation.Any)});
    }
    public static object Apply()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Repair authored assets in Edit Mode.");
        int moved=0;
        foreach(var module in Modules())
        {
            var mesh=module.interior.transform.Find("OrthogonalTraces").GetComponent<MeshFilter>().sharedMesh;
            var vertices=mesh.vertices;var uv=mesh.uv;bool changed=false;
            foreach(int centre in Junctions(mesh))
            {
                Vector3 delta=Expected(module,Mathf.RoundToInt(uv[centre].x))-vertices[centre];
                if(delta.sqrMagnitude<.0000000001f)continue;
                if(!changed)Undo.RegisterCompleteObjectUndo(mesh,"Attach circuit branch junctions to wires");
                for(int vertex=centre;vertex<=centre+17;vertex++)vertices[vertex]+=delta;
                changed=true;moved++;
            }
            if(changed){mesh.vertices=vertices;mesh.RecalculateBounds();EditorUtility.SetDirty(mesh);}
        }
        var cards=Modules().Select(m=>m.GetComponent<VRCPickup>()).Concat(Object.FindObjectsOfType<CircuitLabMovableTool>(true).Where(t=>t.gameObject.scene.path==Scene).Select(t=>t.GetComponent<VRCPickup>())).ToArray();
        foreach(var pickup in cards)
        {
            Undo.RecordObject(pickup,"Preserve card orientation when picked up");
            pickup.orientation=VRC_Pickup.PickupOrientation.Any;EditorUtility.SetDirty(pickup);
        }
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetSceneByPath(Scene));
        return Save("visual-pickup-repair",new{movedJunctions=moved,freeOrientationCards=cards.Length,layoutAndSimulationUntouched=true,savedScene=false});
    }
    public static async Task<object> VerifyPlayMode()
    {
        if(!EditorApplication.isPlaying)throw new InvalidOperationException("Verify in Play Mode.");
        var leaf=Modules().First(m=>m.modulePath.EndsWith("/fa0"));
        var vm=UdonSharpEditorUtility.GetBackingUdonBehaviour(leaf);
        Quaternion original=leaf.transform.rotation;Vector3 originalPosition=leaf.transform.position;
        leaf.transform.rotation=Quaternion.Euler(21,37,13);
        Quaternion before=leaf.transform.rotation;vm.OnPickup();
        await Task.Delay(100);float rotationChange=Quaternion.Angle(before,leaf.transform.rotation);
        if(!Convert.ToBoolean(vm.GetProgramVariable("isDetached")))throw new InvalidOperationException("Actual SDK pickup callback did not detach the module.");
        if(rotationChange>.01f)throw new InvalidOperationException("Module pickup callback changed card orientation.");
        if(leaf.GetComponent<VRCPickup>().orientation!=VRC_Pickup.PickupOrientation.Any)throw new InvalidOperationException("Card pickup still forces grip orientation.");
        var mesh=leaf.interior.transform.Find("OrthogonalTraces").GetComponent<MeshFilter>().sharedMesh;
        var vertices=mesh.vertices;var uv=mesh.uv;int verified=0;
        foreach(int centre in Junctions(mesh))
        {
            if((vertices[centre]-Expected(leaf,Mathf.RoundToInt(uv[centre].x))).magnitude>.0001f)throw new InvalidOperationException("Leaf junction is detached from its wire.");
            verified++;
        }
        if(verified==0)throw new InvalidOperationException("Leaf test did not contain branch junctions.");
        var outputMesh=leaf.interior.transform.Find("OutputPortTraces").GetComponent<MeshFilter>().sharedMesh;
        if(outputMesh.vertexCount==0)throw new InvalidOperationException("Leaf output port routes are missing.");
        leaf.transform.rotation=original;leaf.transform.position=originalPosition;
        return Save("visual-pickup-playmode",new{passed=true,leaf=leaf.modulePath,attachedJunctionsVerified=verified,outputPortRoutesPresent=true,pickupCallbackRotationChangeDegrees=rotationChange,orientation="Any",nativeClientInputNotExercised=true});
    }
    public static object CaptureLeaf()
    {
        if(!EditorApplication.isPlaying)throw new InvalidOperationException("Capture a leaf in Play Mode.");
        var leaf=Modules().First(m=>m.modulePath.EndsWith("/fa0"));
        UdonSharpEditorUtility.GetBackingUdonBehaviour(leaf).SendCustomEvent("Expand");
        var camera=Object.FindObjectsOfType<Camera>(true).First(c=>c.name=="CircuitLabPreviewCamera");
        Vector3 savedPosition=camera.transform.position;Quaternion savedRotation=camera.transform.rotation;
        float savedFov=camera.fieldOfView;var savedTarget=camera.targetTexture;var savedActive=RenderTexture.active;
        var texture=new RenderTexture(1400,1000,24,RenderTextureFormat.ARGB32);texture.Create();
        var pixels=new Texture2D(1400,1000,TextureFormat.RGB24,false);
        var otherRenderers=Object.FindObjectsOfType<Renderer>(true).Where(r=>r.enabled&&!r.transform.IsChildOf(leaf.transform)).ToArray();
        var otherCanvases=Object.FindObjectsOfType<Canvas>(true).Where(c=>c.enabled&&!c.transform.IsChildOf(leaf.transform)).ToArray();
        try
        {
            foreach(var renderer in otherRenderers)renderer.enabled=false;
            foreach(var canvas in otherCanvases)canvas.enabled=false;
            Vector3 centre=leaf.transform.position;
            camera.transform.position=centre-leaf.transform.forward*4.4f-leaf.transform.up*.8f;
            camera.transform.LookAt(centre,leaf.transform.up);camera.fieldOfView=45;camera.targetTexture=texture;camera.Render();
            RenderTexture.active=texture;pixels.ReadPixels(new Rect(0,0,1400,1000),0,0);pixels.Apply();
            File.WriteAllBytes("Reports/CircuitLab/attached-leaf-junctions.png",pixels.EncodeToPNG());
            return new{captured=true,path="Reports/CircuitLab/attached-leaf-junctions.png"};
        }
        finally
        {
            foreach(var renderer in otherRenderers)renderer.enabled=true;
            foreach(var canvas in otherCanvases)canvas.enabled=true;
            camera.transform.position=savedPosition;camera.transform.rotation=savedRotation;camera.fieldOfView=savedFov;camera.targetTexture=savedTarget;
            RenderTexture.active=savedActive;texture.Release();Object.Destroy(texture);Object.Destroy(pixels);
        }
    }
}
