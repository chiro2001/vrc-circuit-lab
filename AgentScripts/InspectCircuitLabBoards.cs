using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.Udon;
using Object = UnityEngine.Object;

// Read-only layout/utilization audit for the teaching panels. Run from the
// Editor with: unity command run_script --file AgentScripts/InspectCircuitLabBoards.cs --entry InspectCircuitLabBoards.Layout
public static class InspectCircuitLabBoards
{
    private const string ScenePath = "Assets/CircuitLab/Scenes/CircuitLab.unity";
    private const string ReportDir = "Reports/CircuitLab";
    private const string NetlistPath = "Assets/CircuitLab/Data/gpu-netlist.json";

    private static Transform World() =>
        SceneManager.GetSceneByPath(ScenePath).GetRootGameObjects().First(root => root.name == "CircuitLabWorld").transform;

    private static object Save(string name, object report)
    {
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, name + ".json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        return report;
    }

    private static Bounds WorldBounds(Transform target)
    {
        bool has = false;
        Bounds bounds = new Bounds(target.position, Vector3.zero);
        foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(true))
        {
            if (!has) { bounds = renderer.bounds; has = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        foreach (Collider collider in target.GetComponentsInChildren<Collider>(true))
        {
            if (!has) { bounds = collider.bounds; has = true; }
            else bounds.Encapsulate(collider.bounds);
        }
        return bounds;
    }

    private static JObject PoseOf(string key, Transform target)
    {
        Bounds bounds = WorldBounds(target);
        Vector3 euler = target.eulerAngles;
        return new JObject
        {
            ["key"] = key,
            ["name"] = target.name,
            ["position"] = new JArray(target.position.x, target.position.y, target.position.z),
            ["euler"] = new JArray(euler.x, euler.y, euler.z),
            ["boundsCenter"] = new JArray(bounds.center.x, bounds.center.y, bounds.center.z),
            ["boundsSize"] = new JArray(bounds.size.x, bounds.size.y, bounds.size.z),
            ["boundsMin"] = new JArray(bounds.min.x, bounds.min.y, bounds.min.z),
            ["boundsMax"] = new JArray(bounds.max.x, bounds.max.y, bounds.max.z)
        };
    }

    private static bool ContainsPoint(Bounds bounds, Vector3 point) =>
        point.x >= bounds.min.x && point.x <= bounds.max.x &&
        point.y >= bounds.min.y && point.y <= bounds.max.y &&
        point.z >= bounds.min.z && point.z <= bounds.max.z;

    // Oriented-box overlap so two parallel, rotated panels are compared in 3D
    // instead of by their overlapping world AABBs.
    private static bool PanelObbsOverlap(Transform a, Vector3 aHalf, Transform b, Vector3 bHalf)
    {
        Vector3[] axes =
        {
            a.right, a.up, a.forward, b.right, b.up, b.forward,
            Vector3.Cross(a.right, b.right), Vector3.Cross(a.right, b.up), Vector3.Cross(a.right, b.forward),
            Vector3.Cross(a.up, b.right), Vector3.Cross(a.up, b.up), Vector3.Cross(a.up, b.forward),
            Vector3.Cross(a.forward, b.right), Vector3.Cross(a.forward, b.up), Vector3.Cross(a.forward, b.forward)
        };
        Vector3 delta = b.position - a.position;
        foreach (Vector3 axis in axes)
        {
            if (axis.sqrMagnitude < .000001f) continue;
            Vector3 normal = axis.normalized;
            float radiusA = Mathf.Abs(Vector3.Dot(normal, a.right)) * aHalf.x
                + Mathf.Abs(Vector3.Dot(normal, a.up)) * aHalf.y
                + Mathf.Abs(Vector3.Dot(normal, a.forward)) * aHalf.z;
            float radiusB = Mathf.Abs(Vector3.Dot(normal, b.right)) * bHalf.x
                + Mathf.Abs(Vector3.Dot(normal, b.up)) * bHalf.y
                + Mathf.Abs(Vector3.Dot(normal, b.forward)) * bHalf.z;
            if (Mathf.Abs(Vector3.Dot(delta, normal)) > radiusA + radiusB) return false;
        }
        return true;
    }

    private static Vector3 PanelHalfExtents(Transform panel)
    {
        Vector3 size = Vector3.zero;
        foreach (RectTransform rect in panel.GetComponentsInChildren<RectTransform>(true))
        {
            if (rect == panel) continue;
            size = Vector3.Max(size, new Vector3(
                rect.rect.width * Mathf.Abs(rect.lossyScale.x),
                rect.rect.height * Mathf.Abs(rect.lossyScale.y),
                0f));
            size.z = Mathf.Max(size.z, .2f);
        }
        if (size.x <= 0f) size = new Vector3(4f, 4f, .2f);
        return size * .5f;
    }

    private static float[] Corners(Transform target)
    {
        Bounds local = new Bounds();
        bool has = false;
        foreach (Collider collider in target.GetComponentsInChildren<Collider>(true))
        {
            Bounds world = collider.bounds;
            Bounds localBound = new Bounds(target.InverseTransformPoint(world.center), Vector3.zero);
            // Local AABB via the 8 world corners of this collider.
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = target.InverseTransformPoint(world.center + Vector3.Scale(world.extents, new Vector3(x, y, z)));
                        if (!has) { local = new Bounds(corner, Vector3.zero); has = true; }
                        else local.Encapsulate(corner);
                    }
        }
        float[] result = new float[24];
        if (!has) return result;
        int index = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = target.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3(x, y, z)));
                    result[index++] = corner.x; result[index++] = corner.y; result[index++] = corner.z;
                }
        return result;
    }

    private static JArray RamFlags(int[] ram, int start, int count)
    {
        JArray flags = new JArray();
        for (int i = 0; i < count; i++) flags.Add(ram[start + i]);
        return flags;
    }

    private static JObject RayProbe(string label, Vector3 origin, Vector3 target)
    {
        Vector3 direction = (target - origin).normalized;
        float distance = Vector3.Distance(origin, target) + 2f;
        RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance, ~0, QueryTriggerInteraction.Collide);
        var ordered = hits.OrderBy(hit => hit.distance).ToArray();
        JArray hitList = new JArray();
        foreach (RaycastHit hit in ordered.Take(6))
            hitList.Add(new JObject
            {
                ["name"] = hit.transform.name,
                ["root"] = hit.transform.root.name,
                ["distance"] = hit.distance,
                ["point"] = new JArray(hit.point.x, hit.point.y, hit.point.z)
            });
        return new JObject { ["probe"] = label, ["origin"] = new JArray(origin.x, origin.y, origin.z), ["target"] = new JArray(target.x, target.y, target.z), ["hits"] = hitList };
    }

    private static JArray SurroundingHits(Transform subject, Vector3[] origins)
    {
        Bounds bounds = WorldBounds(subject);
        var subjects = subject.GetComponentsInChildren<Collider>(true).Select(collider => collider.name).ToHashSet();
        var samples = new List<Vector3> { bounds.center };
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                samples.Add(new Vector3(
                    bounds.center.x + bounds.extents.x * x,
                    bounds.center.y + bounds.extents.y * y,
                    bounds.center.z));
        JArray results = new JArray();
        foreach (Vector3 origin in origins)
            foreach (Vector3 sample in samples)
            {
                Vector3 direction = (sample - origin).normalized;
                float distance = Vector3.Distance(origin, sample) + 2f;
                RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance, ~0, QueryTriggerInteraction.Collide);
                var ordered = hits.OrderBy(hit => hit.distance).ToArray();
                string first = ordered.Length == 0 ? "NONE" : ordered[0].transform.name;
                bool self = ordered.Length > 0 && (ordered[0].transform == subject || ordered[0].transform.IsChildOf(subject));
                JArray blockers = new JArray();
                foreach (RaycastHit hit in ordered.Take(4))
                    if (!(hit.transform == subject || hit.transform.IsChildOf(subject)))
                        blockers.Add(hit.transform.name + "@" + hit.distance.ToString("F2"));
                results.Add(new JObject
                {
                    ["origin"] = new JArray(origin.x, origin.y, origin.z),
                    ["sample"] = new JArray(sample.x, sample.y, sample.z),
                    ["first"] = first,
                    ["firstIsSelf"] = self,
                    ["blockersBeforeSelf"] = blockers
                });
            }
        return results;
    }

    private static void Capture(Camera camera, string name)
    {
        const int width = 1600, height = 900;
        RenderTexture target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        target.Create();
        Texture2D pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
        RenderTexture previous = RenderTexture.active;
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            pixels.Apply();
            Directory.CreateDirectory(ReportDir);
            File.WriteAllBytes(Path.Combine(ReportDir, name + ".png"), pixels.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            target.Release();
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(pixels);
        }
    }

    public static object Layout()
    {
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open the CircuitLab scene first.");
        Transform world = World();
        var panels = new List<Transform>();
        foreach (string name in new[]
        {
            "Floor", "Backdrop", "Spawn", "MovableWaveformPanel", "ProbeAndHierarchyRack",
            "ProgramPanel", "StateTablet", "TerminalPanel", "AboutPanel", "ProgramBrowserPanel",
            "IsaManualPanel", "LedOutputPanel", "LifeOutputPanel"
        })
        {
            Transform found = world.Find(name);
            if (found != null) panels.Add(found);
        }
        Transform about = world.Find("AboutPanel");
        Transform locale = about == null ? null : about.Find("LocaleCanvas");
        if (locale != null) panels.Add(locale);
        JArray poseList = new JArray();
        foreach (Transform panel in panels) poseList.Add(PoseOf((panel == locale ? "LocaleCanvas" : panel.name), panel));

        Transform life = world.Find("LifeOutputPanel");
        Transform backdrop = world.Find("Backdrop");
        Bounds lifeBounds = WorldBounds(life);
        Bounds backdropBounds = WorldBounds(backdrop);

        // How much of the Life board sits behind the backdrop plane along -Z (toward the players).
        float[] corners = Corners(life);
        JArray cornerList = new JArray();
        int behind = 0, front = 0, inside = 0;
        for (int i = 0; i < corners.Length; i += 3)
        {
            Vector3 corner = new Vector3(corners[i], corners[i + 1], corners[i + 2]);
            bool isInside = ContainsPoint(backdropBounds, corner);
            if (isInside) inside++;
            if (corner.z > backdropBounds.max.z) behind++; else front++;
            cornerList.Add(new JObject
            {
                ["point"] = new JArray(corner.x, corner.y, corner.z),
                ["insideBackdrop"] = isInside,
                ["frontOfBackdrop"] = corner.z <= backdropBounds.max.z
            });
        }

        JArray watchOrigins = new JArray
        {
            new JArray(0f, 1.7f, -3.3f),
            new JArray(0f, 1.7f, 0f),
            new JArray(4f, 1.7f, -2f),
            new JArray(8f, 1.7f, -1f),
            new JArray(-4f, 1.7f, -2f),
            new JArray(0f, 4.5f, -6f)
        };
        JArray probes = new JArray();
        foreach (JArray origin in watchOrigins)
        {
            Vector3 point = new Vector3((float)origin[0], (float)origin[1], (float)origin[2]);
            probes.Add(RayProbe("life-center", point, lifeBounds.center));
        }

        // Locale panel clearance vs. neighbours.
        Bounds localeBounds = locale == null ? new Bounds() : WorldBounds(locale);
        JArray clearance = new JArray();
        if (locale != null)
            foreach (Transform panel in panels)
            {
                if (panel == locale || panel == about) continue;
                Bounds other = WorldBounds(panel);
                float gap = Vector3.Distance(localeBounds.center, other.center) - localeBounds.extents.magnitude - other.extents.magnitude;
                clearance.Add(new JObject
                {
                    ["name"] = panel.name,
                    ["gap"] = gap,
                    ["intersects"] = localeBounds.Intersects(other),
                    ["otherCenter"] = new JArray(other.center.x, other.center.y, other.center.z)
                });
            }

        var clearanceSorted = clearance.OrderBy(item => (float)item["gap"]).ToArray();

        // Program source completeness in the baked Udon data.
        var browser = Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First();
        UdonBehaviour vm = UdonSharpEditorUtility.GetBackingUdonBehaviour(browser);
        string[] bakedUdon = (vm.GetProgramVariable("programSources") as System.Collections.IEnumerable)?.Cast<object>().Select(value => value as string).ToArray() ?? new string[0];
        string[] proxySources = browser.programSources ?? new string[0];
        JArray programs = (JArray)JObject.Parse(File.ReadAllText(NetlistPath))["programs"];
        JArray sourceReport = new JArray();
        for (int index = 0; index < programs.Count; index++)
        {
            string source = (string)programs[index]["source"] ?? "";
            int sourceLines = source.Length == 0 ? 0 : 1 + source.Count(character => character == '\n');
            int pageCount = Mathf.Max(1, (sourceLines + 11) / 12);
            string udonSource = index < bakedUdon.Length ? bakedUdon[index] : null;
            string proxySource = index < proxySources.Length ? proxySources[index] : null;
            sourceReport.Add(new JObject
            {
                ["id"] = (string)programs[index]["id"],
                ["jsonChars"] = source.Length,
                ["udonChars"] = udonSource == null ? -1 : udonSource.Length,
                ["proxyChars"] = proxySource == null ? -1 : proxySource.Length,
                ["proxyMatchesJson"] = proxySource == source,
                ["sourceLines"] = sourceLines,
                ["browsePageCountAt12"] = pageCount,
                ["lastPageLines"] = sourceLines - (pageCount - 1) * 12
            });
        }

        var report = new
        {
            passed = true,
            scene = scene.path,
            panels = poseList,
            life = new
            {
                bounds = PoseOf("LifeOutputPanel", life),
                backdrop = PoseOf("Backdrop", backdrop),
                intersectsBackdropBounds = lifeBounds.Intersects(backdropBounds),
                cornersInsideBackdrop = inside,
                cornersBehindBackdropPlane = behind,
                cornersInFront = front,
                corners = cornerList,
                rayProbes = probes,
                playerSamples = SurroundingHits(life, new[]
                {
                    new Vector3(0f, 1.7f, -3.3f),
                    new Vector3(4f, 1.7f, -2f),
                    new Vector3(8f, 1.7f, -1f)
                })
            },
            locale = new
            {
                bounds = locale == null ? null : PoseOf("LocaleCanvas", locale),
                parent = about == null ? "" : about.name,
                nearestPanels = clearanceSorted.Take(6).ToArray(),
                rayProbes = locale == null ? new JArray() : SurroundingHits(locale, new[]
                {
                    new Vector3(0f, 1.7f, -3.3f),
                    new Vector3(-4f, 1.7f, -2f),
                    new Vector3(-8f, 1.7f, -1f)
                })
            },
            programSources = sourceReport
        };
        return Save("boards-inspection", report);
    }

    // Reloads material assets after the runtime-texture cleanup and saves the
    // authored scene so the snapshot captures a clean, deterministic state.
    public static object SaveScene()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before saving.");
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool saved = EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        var tool = Object.FindObjectsOfType<CircuitLabMovableTool>(true)
            .FirstOrDefault(item => item.name == "LifeOutputPanel");
        return Save("boards-save", new
        {
            saved,
            dirty = SceneManager.GetSceneByPath(ScenePath).isDirty,
            scene = ScenePath,
            lifeInitial = tool == null ? null : new JArray(tool.initialPosition.x, tool.initialPosition.y, tool.initialPosition.z)
        });
    }

    // Framed captures of the three touched surfaces; safe in Edit and Play Mode.
    public static object Shots()
    {
        Transform world = World();
        Bounds life = WorldBounds(world.Find("LifeOutputPanel"));
        Bounds about = WorldBounds(world.Find("AboutPanel"));
        Bounds browser = WorldBounds(world.Find("ProgramBrowserPanel"));
        GameObject cameraObject = new GameObject("BoardInspectionCamera", typeof(Camera));
        Camera camera = cameraObject.GetComponent<Camera>();
        camera.enabled = false;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 200f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.cullingMask = ~0;
        try
        {
            Action<Vector3, Vector3, float, string> shot = (position, target, fov, name) =>
            {
                camera.transform.position = position;
                camera.transform.rotation = Quaternion.LookRotation(target - position);
                camera.fieldOfView = fov;
                Capture(camera, name);
            };
            shot(new Vector3(0f, 1.7f, -3.3f), life.center, 65f, "boards-fix-life-spawn");
            shot(new Vector3(3f, 1.8f, -2f), life.center, 65f, "boards-fix-life-mid");
            shot(new Vector3(-6.6f, 3.25f, -9.6f), about.center, 50f, "boards-fix-about-front");
            shot(new Vector3(-2f, 4.5f, -11f), about.center, 60f, "boards-fix-about-wide");
            shot(new Vector3(7f, 3.75f, -9f), browser.center, 55f, "boards-fix-browser");
            return Save("boards-shots", new
            {
                passed = true,
                playMode = EditorApplication.isPlaying,
                files = new[]
                {
                    "boards-fix-life-spawn.png", "boards-fix-life-mid.png",
                    "boards-fix-about-front.png", "boards-fix-about-wide.png", "boards-fix-browser.png"
                }
            });
        }
        finally
        {
            Object.DestroyImmediate(cameraObject);
        }
    }

    public static object Screens()
    {
        Transform world = World();
        Transform life = world.Find("LifeOutputPanel");
        Transform about = world.Find("AboutPanel");
        Transform locale = about.Find("LocaleCanvas");
        Bounds lifeBounds = WorldBounds(life);
        Bounds localeBounds = WorldBounds(locale);

        GameObject cameraObject = new GameObject("BoardInspectionCamera", typeof(Camera));
        Camera camera = cameraObject.GetComponent<Camera>();
        camera.enabled = false;
        camera.fieldOfView = 60f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 200f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.cullingMask = ~0;
        try
        {
            camera.transform.position = new Vector3(0f, 1.7f, -3.3f);
            camera.transform.rotation = Quaternion.LookRotation(lifeBounds.center - camera.transform.position);
            Capture(camera, "boards-life-spawn");

            camera.transform.position = new Vector3(5.4f, 2.2f, 0.4f);
            camera.transform.rotation = Quaternion.LookRotation(lifeBounds.center - camera.transform.position);
            camera.fieldOfView = 55f;
            Capture(camera, "boards-life-close");

            camera.transform.position = new Vector3(11.2f, 2.0f, 0.2f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(9.6f, 3.8f, 6.2f) - camera.transform.position);
            Capture(camera, "boards-life-side");

            camera.transform.position = new Vector3(3.2f, 5.6f, -6.4f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(8.7f, 3.4f, 6.0f) - camera.transform.position);
            camera.fieldOfView = 62f;
            Capture(camera, "boards-backdrop-wide");

            camera.transform.position = new Vector3(-4.0f, 2.0f, -1.0f);
            camera.transform.rotation = Quaternion.LookRotation(localeBounds.center - camera.transform.position);
            camera.fieldOfView = 55f;
            Capture(camera, "boards-locale");

            camera.transform.position = new Vector3(-5.0f, 5.2f, -8.0f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(-11.2f, 3.0f, -2.2f) - camera.transform.position);
            camera.fieldOfView = 55f;
            Capture(camera, "boards-locale-wide");
            return new { passed = true, files = new[] { "boards-life-spawn.png", "boards-life-close.png", "boards-life-side.png", "boards-backdrop-wide.png", "boards-locale.png", "boards-locale-wide.png" } };
        }
        finally
        {
            Object.DestroyImmediate(cameraObject);
        }
    }

    private static string[] SourceLines(string source)
    {
        if (source == null) return new string[0];
        return source.Split('\n').Select(line => line.Replace("\r", "")).ToArray();
    }

    private static string FormatLineNumber(int lineNumber)
    {
        string value = "" + lineNumber;
        while (value.Length < 3) value = "0" + value;
        return value;
    }

    private static string WrapSourceLine(string line)
    {
        const int width = 68;
        string output = "";
        for (int i = 0; i < line.Length; i++)
        {
            if (i > 0 && i % width == 0) output += "\n      ";
            output += line[i];
        }
        return output;
    }

    // Mirrors CircuitLabProgramBrowser.RenderSource (whole listing, no pages)
    // and reverses the line-number/wrap decoration to prove nothing is dropped.
    private static JObject RebuildSource(string source)
    {
        string[] lines = SourceLines(source);
        if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
            lines = lines.Take(lines.Length - 1).ToArray();
        int total = lines.Length;
        var output = new System.Text.StringBuilder();
        for (int lineNumber = 0; lineNumber < total; lineNumber++)
            output.Append(FormatLineNumber(lineNumber + 1)).Append(" | ").Append(WrapSourceLine(lines[lineNumber])).Append('\n');
        string[] rendered = output.ToString().Split('\n');
        var rebuilt = new List<string>();
        string current = null;
        for (int i = 0; i < rendered.Length; i++)
        {
            string text = rendered[i];
            if (i == rendered.Length - 1 && text.Length == 0) continue;
            if (text.Length >= 5 && text[3] == ' ' && text[4] == '|' && char.IsDigit(text[0]))
            {
                if (current != null) rebuilt.Add(current);
                current = text.Substring(6);
            }
            else if (current != null)
            {
                current += text.StartsWith("      ") ? text.Substring(6) : text;
            }
        }
        if (current != null) rebuilt.Add(current);
        bool identical = rebuilt.Count == total;
        for (int i = 0; identical && i < total; i++) identical = rebuilt[i] == lines[i];
        return new JObject
        {
            ["totalLines"] = total,
            ["rebuiltLines"] = rebuilt.Count,
            ["allLinesMatch"] = identical,
            ["lastLine"] = total == 0 ? "" : lines[total - 1]
        };
    }

    public static object Programs()
    {
        var browser = Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First();
        var simulator = Object.FindObjectsOfType<CircuitLabSimulator>(true).First();
        UdonBehaviour vm = UdonSharpEditorUtility.GetBackingUdonBehaviour(browser);
        string[] udonSources = (vm.GetProgramVariable("programSources") as System.Collections.IEnumerable)?.Cast<object>().Select(value => value as string).ToArray() ?? new string[0];
        JArray programs = (JArray)JObject.Parse(File.ReadAllText(NetlistPath))["programs"];
        JArray rows = new JArray();
        bool allComplete = true;
        for (int index = 0; index < programs.Count; index++)
        {
            string id = (string)programs[index]["id"];
            string source = (string)programs[index]["source"] ?? "";
            JObject verification = RebuildSource(source);
            string udonSource = index < udonSources.Length ? udonSources[index] : null;
            bool matches = verification["allLinesMatch"] != null && (bool)verification["allLinesMatch"];
            allComplete &= matches;
            rows.Add(new JObject
            {
                ["id"] = id,
                ["sourceLines"] = verification["totalLines"],
                ["allLinesMatch"] = matches,
                ["udonChars"] = udonSource == null ? -1 : udonSource.Length,
                ["jsonChars"] = source.Length,
                ["udonMatchesJson"] = udonSource == source
            });
        }
        int[] words = simulator.presetProgramWords;
        int programsInWords = words == null ? 0 : words.Length / 512;
        JArray wordCounts = new JArray();
        for (int index = 0; index < programsInWords; index++)
        {
            int lastNonHalt = -1;
            for (int address = 0; address < 512; address++)
                if ((words[index * 512 + address] & 0xFFF) != 0xF00) lastNonHalt = address;
            wordCounts.Add(new JObject { ["id"] = (string)programs[index]["id"], ["lastNonHaltWord"] = lastNonHalt, ["romWords"] = 512 });
        }
        return Save("boards-programs", new
        {
            passed = allComplete,
            browsePanel = new { mode = "scroll", programs = rows },
            romView = new { programCapacity = 512, instructionRows = 8, pages = 64, presetProgramWords = words == null ? 0 : words.Length, programsInWords, wordCounts }
        });
    }

    public static object LocalePreview()
    {
        Transform world = World();
        Transform about = world.Find("AboutPanel");
        Transform locale = about.Find("LocaleCanvas");
        Vector3 originalPosition = locale.localPosition;
        Quaternion originalRotation = locale.localRotation;
        Vector3 originalScale = locale.localScale;
        Bounds aboutBounds = WorldBounds(about);
        Bounds localeBounds = WorldBounds(locale);

        GameObject cameraObject = new GameObject("BoardInspectionCamera", typeof(Camera));
        Camera camera = cameraObject.GetComponent<Camera>();
        camera.enabled = false;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 200f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.cullingMask = ~0;
        try
        {
            Action<Vector3, Vector3, float, string> shot = (position, target, fov, name) =>
            {
                camera.transform.position = position;
                camera.transform.rotation = Quaternion.LookRotation(target - position);
                camera.fieldOfView = fov;
                Capture(camera, name);
            };

            var report = new JArray();
            Action<string, Vector3, Vector3, Vector3> place = (name, localPosition, localScale, lookAt) =>
            {
                locale.localPosition = localPosition;
                locale.localRotation = Quaternion.identity;
                locale.localScale = localScale;
                Physics.SyncTransforms();
                Bounds placed = WorldBounds(locale);
                var probes = SurroundingHits(locale, new[]
                {
                    new Vector3(0f, 1.7f, -3.3f),
                    new Vector3(-4f, 1.7f, -2f),
                    new Vector3(-8f, 1.7f, -1f)
                });
                int visible = probes.Count(item => (bool)item["firstIsSelf"]);
                report.Add(new JObject
                {
                    ["name"] = name,
                    ["localPosition"] = new JArray(localPosition.x, localPosition.y, localPosition.z),
                    ["localScale"] = localScale.x,
                    ["worldCenter"] = new JArray(placed.center.x, placed.center.y, placed.center.z),
                    ["overlapsAbout"] = placed.Intersects(aboutBounds),
                    ["selfVisibleSamples"] = visible,
                    ["totalSamples"] = probes.Count,
                    ["probes"] = probes
                });
                shot(new Vector3(0f, 1.7f, -3.3f), aboutBounds.center, 70f, "boards-locale-" + name + "-spawn");
                shot(new Vector3(-5.3f, 3.25f, -9.4f), aboutBounds.center, 45f, "boards-locale-" + name + "-front");
                shot(new Vector3(-2.0f, 4.5f, -11.0f), aboutBounds.center, 60f, "boards-locale-" + name + "-wide");
                shot(new Vector3(-7.5f, 2.8f, -7.5f), placed.center, 50f, "boards-locale-" + name + "-close");
            };

            int originalVisible = SurroundingHits(locale, new[]
            {
                new Vector3(0f, 1.7f, -3.3f),
                new Vector3(-4f, 1.7f, -2f),
                new Vector3(-8f, 1.7f, -1f)
            }).Count(item => (bool)item["firstIsSelf"]);
            place("current", locale.localPosition, locale.localScale, localeBounds.center);
            place("candidate-a-embedded", new Vector3(1.20f, -0.90f, -0.105f), Vector3.one * 0.01f, Vector3.zero);
            place("candidate-b-header", new Vector3(0f, 2.62f, -0.105f), Vector3.one * 0.01f, Vector3.zero);
            place("candidate-c-outside-low", new Vector3(4.10f, -1.10f, -0.105f), Vector3.one * 0.009f, Vector3.zero);
            return Save("boards-locale-preview", new
            {
                passed = true,
                originalVisibleSamples = originalVisible,
                originalLocalPosition = new JArray(originalPosition.x, originalPosition.y, originalPosition.z),
                originalLocalScale = originalScale.x,
                candidates = report
            });
        }
        finally
        {
            locale.localPosition = originalPosition;
            locale.localRotation = originalRotation;
            locale.localScale = originalScale;
            Object.DestroyImmediate(cameraObject);
        }
    }

    private static async Task Until(Func<bool> predicate, string message, int seconds = 60)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!predicate())
        {
            if (DateTime.UtcNow > limit) throw new InvalidOperationException(message);
            await Task.Delay(100);
        }
    }

    private static void Click(CircuitLabButton handler)
    {
        if (handler == null || !handler.uiButton.IsActive() || !handler.uiButton.IsInteractable())
            throw new InvalidOperationException("Button unavailable: " + (handler == null ? "null" : handler.eventName));
        UnityEngine.EventSystems.ExecuteEvents.Execute(handler.gameObject,
            new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { button = UnityEngine.EventSystems.PointerEventData.InputButton.Left },
            UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
    }

    public static async Task<object> RuntimePrograms()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter Play Mode first.");
        var browser = Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First();
        UdonBehaviour vm = UdonSharpEditorUtility.GetBackingUdonBehaviour(browser);
        await Until(() => vm.GetProgramVariable("programSources") is string[] values && values.Length > 0, "Udon programSources never became available.", 60);
        string[] sources = (string[])vm.GetProgramVariable("programSources");
        JArray programs = (JArray)JObject.Parse(File.ReadAllText(NetlistPath))["programs"];

        JArray sourceAudit = new JArray();
        for (int index = 0; index < programs.Count; index++)
        {
            string id = (string)programs[index]["id"];
            string source = (string)programs[index]["source"] ?? "";
            string udonSource = index < sources.Length ? sources[index] : null;
            sourceAudit.Add(new JObject
            {
                ["id"] = id,
                ["udonChars"] = udonSource == null ? -1 : udonSource.Length,
                ["jsonChars"] = source.Length,
                ["matchesJson"] = udonSource == source
            });
        }

        JArray pageAudit = new JArray();
        foreach (string wanted in new[] { "pi-spigot-100", "life-8x8", "prime-sieve" })
        {
            int index = Enumerable.Range(0, programs.Count).First(i => (string)programs[i]["id"] == wanted);
            string source = (string)programs[index]["source"] ?? "";
            string[] lines = SourceLines(source);
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0) lines = lines.Take(lines.Length - 1).ToArray();
            vm.SetProgramVariable("selectedProgram", index);
            vm.SetProgramVariable("browseMode", 0);
            vm.SetProgramVariable("browsePage", 0);
            vm.SendCustomEvent("RenderBrowser");
            await Task.Delay(250);
            string rendered = browser.sourceText.text ?? "";
            string firstPrefix = FormatLineNumber(1) + " | " + WrapSourceLine(lines[0]);
            string expectedPrefix = FormatLineNumber(lines.Length) + " | " + WrapSourceLine(lines[lines.Length - 1]);
            TextMeshProUGUI[] overlays = browser.sourceText.GetComponentsInChildren<TextMeshProUGUI>(true);
            TextMeshProUGUI active = overlays.FirstOrDefault(item => item.gameObject.activeInHierarchy);
            float contentHeight = browser.sourceContentRect == null ? 0f : browser.sourceContentRect.rect.height;
            float viewportHeight = browser.sourceScrollRect != null && browser.sourceScrollRect.viewport != null
                ? browser.sourceScrollRect.viewport.rect.height : 0f;
            float before = 1f;
            if (browser.sourceScrollRect != null) browser.sourceScrollRect.verticalNormalizedPosition = 1f;
            vm.SendCustomEvent("ScrollSourceDown");
            await Task.Delay(60);
            if (browser.sourceScrollRect != null) before = browser.sourceScrollRect.verticalNormalizedPosition;
            vm.SendCustomEvent("ScrollSourceUp");
            await Task.Delay(60);
            float after = browser.sourceScrollRect == null ? 0f : browser.sourceScrollRect.verticalNormalizedPosition;
            bool sourceButtonsVisible = browser.sourceScrollUpButton != null && browser.sourceScrollUpButton.activeSelf
                && browser.sourceScrollDownButton != null && browser.sourceScrollDownButton.activeSelf
                && browser.pagePreviousButton != null && !browser.pagePreviousButton.activeSelf
                && browser.pageNextButton != null && !browser.pageNextButton.activeSelf;
            pageAudit.Add(new JObject
            {
                ["id"] = wanted,
                ["sourceLines"] = lines.Length,
                ["firstLinePresent"] = rendered.Contains(firstPrefix),
                ["lastLinePresent"] = rendered.Contains(expectedPrefix),
                ["pageLabel"] = browser.pageText == null ? "" : browser.pageText.text,
                ["pageLabelMatches"] = browser.pageText != null && browser.pageText.text.Contains(lines.Length + " LINES"),
                ["renderedLines"] = rendered.Split('\n').Where(line => line.Length > 0).Count(),
                ["contentHeight"] = contentHeight,
                ["viewportHeight"] = viewportHeight,
                ["scrollable"] = contentHeight > viewportHeight + 1f,
                ["scrollDownMoved"] = before < .999f,
                ["scrollUpRestored"] = after > before,
                ["sourceButtonsVisible"] = sourceButtonsVisible,
                ["activeFont"] = active == null || active.font == null ? "" : active.font.name,
                ["monoFont"] = active != null && active.font != null && active.font.name.Contains("Plex-Mono"),
                ["sourceLineSample"] = lines[lines.Length - 1].Length > 90 ? lines[lines.Length - 1].Substring(0, 90) : lines[lines.Length - 1]
            });
        }
        vm.SetProgramVariable("browseMode", 1);
        vm.SetProgramVariable("browsePage", 0);
        vm.SendCustomEvent("RenderBrowser");
        await Task.Delay(200);
        bool encodingButtonsVisible = browser.pagePreviousButton != null && browser.pagePreviousButton.activeSelf
            && browser.pageNextButton != null && browser.pageNextButton.activeSelf
            && browser.sourceScrollUpButton != null && !browser.sourceScrollUpButton.activeSelf;
        vm.SetProgramVariable("browseMode", 0);
        vm.SendCustomEvent("RenderBrowser");
        await Task.Delay(200);
        bool allMatch = sourceAudit.All(item => (bool)item["matchesJson"]);
        bool allDisplayed = pageAudit.All(item => (bool)item["lastLinePresent"] && (bool)item["firstLinePresent"]
            && (bool)item["scrollable"] && (bool)item["scrollDownMoved"] && (bool)item["sourceButtonsVisible"]
            && (bool)item["monoFont"]);
        return Save("boards-runtime-programs", new
        {
            passed = allMatch && allDisplayed,
            allUdonSourcesMatchJson = allMatch,
            allLastLinesDisplayed = allDisplayed,
            encodingButtonsVisible,
            sourceAudit,
            pageAudit
        });
    }

    public static object LifePreview()
    {
        Transform world = World();
        Transform life = world.Find("LifeOutputPanel");
        Vector3 original = life.position;
        Quaternion originalRotation = life.rotation;
        Vector3 originalScale = life.localScale;

        GameObject cameraObject = new GameObject("BoardInspectionCamera", typeof(Camera));
        Camera camera = cameraObject.GetComponent<Camera>();
        camera.enabled = false;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 200f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.cullingMask = ~0;
        Vector3[] origins =
        {
            new Vector3(0f, 1.7f, -3.3f),
            new Vector3(4f, 1.7f, -2f),
            new Vector3(8f, 1.7f, -1f)
        };
        try
        {
            JArray report = new JArray();
            Action<string, Vector3> place = (name, position) =>
            {
                life.position = position;
                Physics.SyncTransforms();
                Bounds bounds = WorldBounds(life);
                var probes = SurroundingHits(life, origins);
                int visible = probes.Count(item => (bool)item["firstIsSelf"]);
                report.Add(new JObject
                {
                    ["name"] = name,
                    ["position"] = new JArray(position.x, position.y, position.z),
                    ["boundsMinZ"] = bounds.min.z,
                    ["boundsMaxZ"] = bounds.max.z,
                    ["visibleSamples"] = visible,
                    ["totalSamples"] = probes.Count,
                    ["probes"] = probes
                });
                Action<Vector3, string> shot = (cameraPosition, suffix) =>
                {
                    camera.transform.position = cameraPosition;
                    camera.transform.rotation = Quaternion.LookRotation(bounds.center - cameraPosition);
                    camera.fieldOfView = 65f;
                    Capture(camera, "boards-life-" + name + suffix);
                };
                shot(new Vector3(0f, 1.7f, -3.3f), "-spawn");
                shot(new Vector3(3.0f, 1.7f, -2.0f), "-mid");
            };
            place("current", original);
            place("forward-1m", original + new Vector3(0f, 0f, -1.0f));
            place("forward-2m", original + new Vector3(0f, 0f, -2.0f));
            return Save("boards-life-preview", new { passed = true, original = new JArray(original.x, original.y, original.z), candidates = report });
        }
        finally
        {
            life.position = original;
            life.rotation = originalRotation;
            life.localScale = originalScale;
            Physics.SyncTransforms();
            Object.DestroyImmediate(cameraObject);
        }
    }

    // Post-fix audit: Conway Life clear of the backdrop, language panel embedded
    // into the About board, and the source pane wired for mono scrolling.
    public static object VerifyBoards()
    {
        Transform world = World();
        Transform life = world.Find("LifeOutputPanel");
        Transform about = world.Find("AboutPanel");
        Transform locale = about == null ? null : about.Find("LocaleCanvas");
        Transform repo = about == null ? null : about.Find("AboutCanvas/ProjectRepository");
        Transform program = world.Find("ProgramPanel");
        var browser = Object.FindObjectsOfType<CircuitLabProgramBrowser>(true).First();

        Bounds lifeBounds = WorldBounds(life);
        Bounds localeBounds = locale == null ? new Bounds() : WorldBounds(locale);
        Bounds programBounds = program == null ? new Bounds() : WorldBounds(program);
        var probes = SurroundingHits(life, new[]
        {
            new Vector3(0f, 1.7f, -3.3f),
            new Vector3(4f, 1.7f, -2f),
            new Vector3(8f, 1.7f, -1f)
        });
        int lifeVisible = probes.Count(item => (bool)item["firstIsSelf"]);
        var lifeTool = life.GetComponent<CircuitLabMovableTool>();
        var localeRect = locale == null ? null : locale.GetComponent<RectTransform>();
        var repoRect = repo == null ? null : repo.GetComponent<RectTransform>();
        var repoText = repo == null ? null : repo.GetComponent<Text>();

        // About-canvas pixel space: the locale canvas stores its own scale.
        float repoRight = repoRect == null ? 0f : repoRect.anchoredPosition.x + repoRect.sizeDelta.x * .5f;
        float localeScale = localeRect == null ? .01f : localeRect.localScale.x;
        float localeLeft = localeRect == null ? 0f
            : localeRect.anchoredPosition.x * 100f - localeRect.sizeDelta.x * .5f * (localeScale / .01f);

        bool lifePlaced = (life.position - new Vector3(8.7f, 3.6f, 5.3f)).sqrMagnitude < .0001f;
        bool lifeInitialMatches = lifeTool != null && (lifeTool.initialPosition - new Vector3(8.7f, 3.6f, 5.3f)).sqrMagnitude < .0001f;
        bool localeEmbedded = localeRect != null
            && (localeRect.anchoredPosition - new Vector2(1.30f, -1.05f)).sqrMagnitude < .0001f
            && Mathf.Abs(locale.localScale.x - .0092f) < .00005f;
        bool localeClearOfProgram = !PanelObbsOverlap(locale,
            localeRect == null ? Vector3.zero : new Vector3(localeRect.sizeDelta.x, localeRect.sizeDelta.y, 2f) * localeScale * .5f,
            program, PanelHalfExtents(program));
        bool repositoryClearsLocale = repoRight < localeLeft;

        return Save("boards-verify", new
        {
            passed = lifePlaced && lifeInitialMatches && localeEmbedded && localeClearOfProgram && repositoryClearsLocale
                && browser.sourceScrollUpButton != null && browser.sourceScrollDownButton != null
                && browser.pagePreviousButton != null && browser.pageNextButton != null,
            life = new
            {
                position = new JArray(life.position.x, life.position.y, life.position.z),
                initialPosition = lifeTool == null ? null : new JArray(lifeTool.initialPosition.x, lifeTool.initialPosition.y, lifeTool.initialPosition.z),
                placedInFrontOfBackdrop = lifePlaced,
                initialPoseUpdated = lifeInitialMatches,
                boundsMinZ = lifeBounds.min.z,
                visibleSamples = lifeVisible,
                totalSamples = probes.Count,
                probes
            },
            locale = new
            {
                anchoredPosition = localeRect == null ? null : new JArray(localeRect.anchoredPosition.x, localeRect.anchoredPosition.y),
                scale = locale == null ? 0f : locale.localScale.x,
                embedded = localeEmbedded,
                clearOfProgramPanel = localeClearOfProgram,
                repositoryRightEdge = repoRight,
                localeLeftEdge = localeLeft,
                repositoryClearsLocale,
                repositoryFontSize = repoText == null ? 0 : repoText.fontSize,
                repositoryWidth = repoRect == null ? 0f : repoRect.sizeDelta.x
            },
            browser = new
            {
                sourceLineHeight = browser.sourceLineHeight,
                hasScrollButtons = browser.sourceScrollUpButton != null && browser.sourceScrollDownButton != null,
                hasPageButtons = browser.pagePreviousButton != null && browser.pageNextButton != null,
                legacySourceFont = browser.sourceText == null || browser.sourceText.font == null ? "" : browser.sourceText.font.name
            }
        });
    }

    // Reports the effective font and per-line metrics of the ASM source pane,
    public static object Fonts()
    {
        Transform world = World();
        Transform browser = world.Find("ProgramBrowserPanel");
        Transform source = browser == null ? null : browser.Find("ProgramBrowserCanvas/SourceScrollRegion/Viewport/Content/Text");
        Transform encoding = browser == null ? null : browser.Find("ProgramBrowserCanvas/EncodingScrollRegion/Viewport/Content/Text");
        Transform about = world.Find("AboutPanel");
        Transform locale = about == null ? null : about.Find("LocaleCanvas");
        Transform life = world.Find("LifeOutputPanel");

        Func<Transform, JObject> describe = target =>
        {
            if (target == null) return null;
            Text legacy = target.GetComponent<Text>();
            TextMeshProUGUI[] overlays = target.GetComponentsInChildren<TextMeshProUGUI>(true);
            JArray fonts = new JArray();
            foreach (TextMeshProUGUI overlay in overlays)
                fonts.Add(new JObject
                {
                    ["name"] = overlay.gameObject.name,
                    ["active"] = overlay.gameObject.activeInHierarchy,
                    ["font"] = overlay.font == null ? "null" : overlay.font.name,
                    ["sourceFont"] = overlay.font == null || overlay.font.sourceFontFile == null ? "" : overlay.font.sourceFontFile.name,
                    ["fontSize"] = overlay.fontSize,
                    ["lineSpacing"] = overlay.lineSpacing,
                    ["enableWordWrapping"] = overlay.enableWordWrapping,
                    ["overflow"] = overlay.overflowMode.ToString(),
                    ["rectHeight"] = overlay.rectTransform.rect.height
                });
            string preferred = null;
            if (overlays.Length > 0)
            {
                TextMeshProUGUI probe = overlays.FirstOrDefault(item => item.gameObject.activeSelf) ?? overlays[0];
                string original = probe.text;
                probe.text = string.Concat(Enumerable.Repeat("M\n", 100));
                Canvas.ForceUpdateCanvases();
                preferred = probe.preferredHeight.ToString("F2") + " px / 100 lines = " + (probe.preferredHeight / 100f).ToString("F3") + " px per line";
                probe.text = original;
            }
            return new JObject
            {
                ["path"] = target.name,
                ["legacyEnabled"] = legacy != null && legacy.enabled,
                ["legacyFont"] = legacy == null || legacy.font == null ? "" : legacy.font.name,
                ["legacyFontSize"] = legacy == null ? 0 : legacy.fontSize,
                ["legacyLineSpacing"] = legacy == null ? 0 : legacy.lineSpacing,
                ["legacyRectHeight"] = legacy == null ? 0 : legacy.rectTransform.rect.height,
                ["contentHeight"] = target.parent == null ? 0 : target.parent.GetComponent<RectTransform>().rect.height,
                ["overlays"] = fonts,
                ["probeLineHeight"] = preferred
            };
        };

        return Save("boards-fonts", new
        {
            passed = true,
            source = describe(source),
            encoding = describe(encoding),
            localePanel = locale == null ? null : PoseOf("LocaleCanvas", locale),
            lifePanel = life == null ? null : PoseOf("LifeOutputPanel", life)
        });
    }
}
