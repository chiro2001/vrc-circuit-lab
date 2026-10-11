#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.ClientSim.EncodeDecoders;
using VRC.Udon;

// ClientSim has no general world-variable transport. Exercise its real encoder
// and serialization callbacks so the production acknowledgement barrier can
// run in a single-client Editor. This never enters a VRChat world build and
// never supplies simulation values, network delivery, or ownership guarantees.
[InitializeOnLoad]
public static class CircuitLabClientSimSerialization
{
    private static readonly HashSet<UdonBehaviour> Pending = new HashSet<UdonBehaviour>();
    private static readonly ClientSimUdonEncodeDecode Encoder = new ClientSimUdonEncodeDecode();
    private static double _nextRecoveryScan;

    static CircuitLabClientSimSerialization()
    {
        UdonBehaviour.RequestSerializationHook += Requested;
        EditorApplication.update += Tick;
        AssemblyReloadEvents.beforeAssemblyReload += Unregister;
    }

    private static void Unregister()
    {
        UdonBehaviour.RequestSerializationHook -= Requested;
        EditorApplication.update -= Tick;
        Pending.Clear();
    }

    private static void Requested(UdonBehaviour behaviour)
    {
        if (!EditorApplication.isPlaying || behaviour == null || !behaviour.SyncIsManual
            || behaviour.gameObject.scene.path != "Assets/CircuitLab/Scenes/CircuitLab.unity") return;
        Pending.Add(behaviour);
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) { Pending.Clear(); return; }
        // Recover a request issued before the Editor bridge finished loading.
        if (EditorApplication.timeSinceStartup >= _nextRecoveryScan)
        {
            _nextRecoveryScan = EditorApplication.timeSinceStartup + .5;
            foreach (var coordinator in Object.FindObjectsOfType<CircuitLabNetworkCoordinator>())
            {
                var behaviour = UdonSharpEditor.UdonSharpEditorUtility.GetBackingUdonBehaviour(coordinator);
                if (behaviour != null && behaviour.HasDoneStart && behaviour.GetProgramVariable("_serializationPending") is bool
                    && (bool)behaviour.GetProgramVariable("_serializationPending")) Requested(behaviour);
            }
        }
        var work = new List<UdonBehaviour>(Pending);
        Pending.Clear();
        foreach (var behaviour in work)
        {
            if (behaviour == null || !behaviour.HasDoneStart || behaviour.SyncMetadataTable == null) continue;
            if (Networking.LocalPlayer != null && !Networking.IsOwner(behaviour.gameObject)) continue;
            Encoder.PreEncode(behaviour);
            var data = Encoder.Encode(behaviour);
            Encoder.PostEncode(behaviour,data);
        }
    }
}
#endif
