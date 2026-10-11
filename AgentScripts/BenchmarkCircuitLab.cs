using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using Object = UnityEngine.Object;

// A bounded 2D-texture experiment, not a claim that LCVEX has been imported.
public static class BenchmarkCircuitLab
{
    private const string Root = "Assets/CircuitLab/ScaleBenchmark";
    private const int Passes = 32;
    private const int Cycles = 16;

    private static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        Folder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }

    private static T Asset<T>(string path, Func<T> create) where T : Object
    {
        var value = AssetDatabase.LoadAssetAtPath<T>(path);
        if (value != null) return value;
        value = create();
        AssetDatabase.CreateAsset(value, path);
        return value;
    }

    private static Color[] Instructions(int count, int capacity)
    {
        var result = new Color[capacity];
        result[1] = new Color(1,0,0,0);
        result[2] = new Color(2,0,0,0);
        result[3] = new Color(2,1,0,0);
        for (int id = 4; id < count; id++)
        {
            int first = 4 + (id - 4) / 32 * 32, stage = (id - 4) % 32;
            if (stage == 31) result[id] = new Color(3,id-1,0,0);
            else
            {
                int feedback = first + 31 < count ? first + 31 : 0;
                int a = stage == 0 ? feedback : id - 1;
                int group = (id - 4) / 32;
                int b = group % 5 == 0 ? 1 : 2 + (group / 7 % 2);
                int kind = (stage + group % 3) % 3;
                int op = kind == 0 ? 8 : kind == 1 ? 6 : 7;
                result[id] = new Color(op,a,b,0);
            }
        }
        return result;
    }

    public static object Prepare()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Prepare benchmark assets in Edit Mode.");
        Folder(Root);
        var shader = Shader.Find("VrcCpuWorld/CircuitLabScaleBenchmark");
        if (shader == null || !shader.isSupported) throw new InvalidOperationException("Benchmark shader is unsupported.");
        foreach (int count in new[] { 1024, 10000, 100000 })
        {
            int width = 1024, height = (count + width - 1) / width;
            var gates = Asset(Root + "/Gates" + count + ".asset", () => new Texture2D(width,height,TextureFormat.RGBAFloat,false,true));
            gates.SetPixels(Instructions(count,width*height));
            gates.filterMode = FilterMode.Point; gates.wrapMode = TextureWrapMode.Clamp; gates.Apply(false,false);
            EditorUtility.SetDirty(gates);
            var material = Asset(Root + "/Material" + count + ".mat", () => new Material(shader));
            material.SetTexture("_Gates",gates); EditorUtility.SetDirty(material);
            foreach (string side in new[] { "A", "B" })
            {
                var state = Asset(Root + "/State" + side + count + ".renderTexture", () => new RenderTexture(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear));
                state.filterMode = FilterMode.Point; state.wrapMode = TextureWrapMode.Clamp;
                state.useMipMap = false; state.autoGenerateMips = false; state.antiAliasing = 1;
                EditorUtility.SetDirty(state);
            }
        }
        AssetDatabase.SaveAssets();
        return new { prepared = true, sizes = new[] { 1024,10000,100000 }, settlePasses = Passes, timedCycles = Cycles, maxTextureSize = SystemInfo.maxTextureSize };
    }

    private static async Task<Color32[]> Read(RenderTexture texture)
    {
        var completion = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(texture,0,request =>
        {
            if (request.hasError) completion.TrySetException(new InvalidOperationException("Benchmark readback failed."));
            else completion.TrySetResult(request.GetData<Color32>().ToArray());
        });
        if (await Task.WhenAny(completion.Task,Task.Delay(15000)) != completion.Task) throw new TimeoutException("Benchmark GPU completion timed out.");
        return await completion.Task;
    }

    private static void Pass(Material material, int mode, ref RenderTexture read, ref RenderTexture write)
    {
        material.SetInt("_Mode",mode);
        VRCGraphics.Blit(read,write,material);
        var previous=read; read=write; write=previous;
    }

    private static void Settle(Material material, ref RenderTexture read, ref RenderTexture write)
    {
        for (int i=0;i<Passes;i++) Pass(material,1,ref read,ref write);
    }

    private static void ReferenceSettle(Color[] instructions, byte[] state, int mask)
    {
        state[0]=0; state[1]=1; state[2]=(byte)(mask&1); state[3]=(byte)((mask>>1)&1);
        for(int id=4;id<state.Length;id++)
        {
            var gate=instructions[id]; int op=(int)gate.r;
            if(op==3) continue;
            byte a=state[(int)gate.g],b=state[(int)gate.b];
            state[id]=(byte)(op==6 ? a&b : op==7 ? a|b : a^b);
        }
    }

    public static async Task<object> Run()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Run benchmark in Play Mode.");
        var rows=new List<object>();
        foreach(int count in new[]{1024,10000,100000})
        {
            var material=Object.Instantiate(AssetDatabase.LoadAssetAtPath<Material>(Root+"/Material"+count+".mat"));
            var read=AssetDatabase.LoadAssetAtPath<RenderTexture>(Root+"/StateA"+count+".renderTexture");
            var write=AssetDatabase.LoadAssetAtPath<RenderTexture>(Root+"/StateB"+count+".renderTexture");
            if(material==null||read==null||write==null) throw new InvalidOperationException("Prepare benchmark assets first.");
            try
            {
                material.SetInt("_NodeCount",count);material.SetInt("_TextureWidth",read.width);material.SetInt("_TextureHeight",read.height);
                material.SetInt("_InputMask",0);Pass(material,0,ref read,ref write);Settle(material,ref read,ref write);
                await Read(read); // Warm shader and drain work before the timed batch.
                var instructions=Instructions(count,read.width*read.height);
                var state=new byte[count];ReferenceSettle(instructions,state,0);
                var timing=Stopwatch.StartNew();
                for(int cycle=1;cycle<=Cycles;cycle++)
                {
                    material.SetInt("_InputMask",(cycle*3+1)&3);Settle(material,ref read,ref write);Pass(material,2,ref read,ref write);Settle(material,ref read,ref write);
                }
                var pixels=await Read(read);timing.Stop();
                for(int cycle=1;cycle<=Cycles;cycle++)
                {
                    ReferenceSettle(instructions,state,(cycle*3+1)&3);
                    for(int id=4;id<count;id++) if((int)instructions[id].r==3) state[id]=state[(int)instructions[id].g];
                    ReferenceSettle(instructions,state,(cycle*3+1)&3);
                }
                int mismatches=0;for(int id=0;id<count;id++) if((pixels[id].r>127?1:0)!=state[id]) mismatches++;
                if(mismatches!=0) throw new InvalidOperationException("2D benchmark mismatch count="+mismatches+" nodes="+count);
                rows.Add(new{nodes=count,textureWidth=read.width,textureHeight=read.height,dffs=(count-4)/32,cycles=Cycles,blitsPerCycle=Passes*2+1,elapsedMilliseconds=timing.Elapsed.TotalMilliseconds,cyclesPerSecond=Cycles/timing.Elapsed.TotalSeconds,comparedBits=count,mismatches,stateBufferBytes=(long)read.width*read.height*4*2,instructionBytes=(long)read.width*read.height*16});
            }
            finally{Object.Destroy(material);}
        }
        var result=new{passed=true,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),measuredAt=DateTime.UtcNow,measurement="Wall time from CPU submission to GPU readback completion; includes dispatch, scheduling and one readback. No claim of isolated GPU kernel timing or actual LCVEX speed.",workload="Synthetic independent 31-gate AND/OR/XOR feedback chains with atomic DFF commit; two 32-pass settles per cycle; no RAM, multiplier, geometry or network traffic.",rows};
        Directory.CreateDirectory("Reports/CircuitLab");File.WriteAllText("Reports/CircuitLab/scale-benchmark.json",JsonConvert.SerializeObject(result,Formatting.Indented));
        return result;
    }
}
