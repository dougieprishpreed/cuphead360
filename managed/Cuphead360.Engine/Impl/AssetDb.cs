using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>Engine record of a loaded asset (Sprite, Material, AudioClip, ...): its converted fields.</summary>
    public sealed class NAsset : NObject
    {
        public ChobObject Source;
    }

    /// <summary>
    /// The converted game data (GameData/objects/*.chob) and the managed objects
    /// made from it. Assets are built on first reference, as Unity loads them.
    /// </summary>
    public static class AssetDb
    {
        public static string Root;
        static readonly Dictionary<string, ChobFile> files = new Dictionary<string, ChobFile>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<(ChobFile, long), UObject> built = new Dictionary<(ChobFile, long), UObject>();
        public static readonly List<string> Problems = new List<string>();

        public static string[] BuildScenes = new string[0];
        public static readonly Dictionary<int, string> Tags = new Dictionary<int, string>
        {
            [0] = "Untagged", [1] = "Respawn", [2] = "Finish", [3] = "EditorOnly", [5] = "MainCamera", [6] = "Player", [7] = "GameController",
        };
        public static readonly string[] Layers = new string[32];
        public static readonly Dictionary<string, PPtr> ResourcePaths = new Dictionary<string, PPtr>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<KeyValuePair<string, PPtr>> ResourceList = new List<KeyValuePair<string, PPtr>>();
        public static object PlayerSettings;
        /// <summary>Non-zero while objects are being built from data.</summary>
        public static int Loading;

        public static void Init(string gameData)
        {
            Root = gameData;
            // Load every assembly the game references, so type lookups by name
            // see all Unity modules (the runtime loads them lazily otherwise).
            var seen = new HashSet<string>();
            void Walk(Assembly asm)
            {
                foreach (var r in asm.GetReferencedAssemblies())
                    if (seen.Add(r.Name))
                        try { Walk(Assembly.Load(r)); } catch (Exception) { }
            }
            Walk(Assembly.Load("Assembly-CSharp"));
            string objects = Path.Combine(gameData, "objects");
            foreach (var f in Directory.GetFiles(objects, "*.chob", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                paths[name] = f;
            }
            var ggm = File("globalgamemanagers");
            foreach (var o in ggm.Ordered)
            {
                switch (o.Type)
                {
                    case "TimeManager":
                        Clock.FixedDeltaTime = Tree.Float(o.Tree, "Fixed Timestep", 0.02f);
                        Clock.MaximumDeltaTime = Tree.Float(o.Tree, "Maximum Allowed Timestep", 1f / 3f);
                        Clock.TimeScale = Tree.Float(o.Tree, "m_TimeScale", 1f);
                        break;
                    case "BuildSettings":
                        BuildScenes = Tree.List(o.Tree, "scenes").Select(x => x as string ?? "").ToArray();
                        break;
                    case "TagManager":
                        {
                            var tags = Tree.List(o.Tree, "tags");
                            for (int i = 0; i < tags.Count; i++) Tags[20000 + i] = tags[i] as string ?? "";
                            var layers = Tree.List(o.Tree, "layers");
                            for (int i = 0; i < layers.Count && i < 32; i++) Layers[i] = layers[i] as string ?? "";
                            break;
                        }
                    case "ResourceManager":
                        foreach (var e in Tree.List(o.Tree, "m_Container"))
                        {
                            // (path, PPtr) pairs; several objects may share a path.
                            if (e is List<object> pair && pair.Count == 2 && pair[0] is string p && pair[1] is PPtr ptr)
                            {
                                ResourceList.Add(new KeyValuePair<string, PPtr>(p, ptr));
                                if (!ResourcePaths.ContainsKey(p)) ResourcePaths[p] = ptr;
                            }
                        }
                        break;
                    case "PlayerSettings":
                        PlayerSettings = o.Tree;
                        break;
                }
            }
            Log.Line($"AssetDb: {paths.Count} files, {BuildScenes.Length} build scenes, fixed step {Clock.FixedDeltaTime:0.######}, {ResourcePaths.Count} resource paths");
        }

        public static ChobFile File(string name)
        {
            string key = Normalize(name);
            if (files.TryGetValue(key, out var f)) return f;
            if (!paths.TryGetValue(key, out var path))
            {
                files[key] = null;
                Problem("missing converted file " + name);
                return null;
            }
            f = ChobFile.Load(path, key);
            files[key] = f;
            return f;
        }

        /// <summary>An external reference as the converter stores it: "resources.assets", "archive:/CAB-x/CAB-x", ...</summary>
        public static string Normalize(string name)
        {
            name = name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            return name;
        }

        public static ChobFile Target(ChobFile from, PPtr p)
        {
            if (p.FileId == 0) return from;
            if (p.FileId < 0 || p.FileId > from.Externals.Length) return null;
            return File(from.Externals[p.FileId - 1]);
        }

        public static ChobObject Lookup(ChobFile from, PPtr p)
        {
            if (p.IsNull) return null;
            var f = Target(from, p);
            if (f == null) return null;
            return f.Objects.TryGetValue(p.PathId, out var o) ? o : null;
        }

        public static void Problem(string what)
        {
            if (Problems.Contains(what)) return;
            Problems.Add(what);
            Log.Line("DATA " + what);
        }

        public static bool TryBuilt(ChobFile f, long pathId, out UObject o) => built.TryGetValue((f, pathId), out o);
        public static void SetBuilt(ChobFile f, long pathId, UObject o) => built[(f, pathId)] = o;

        /// <summary>The managed object for a PPtr (an asset is built on first use).</summary>
        public static UObject Resolve(ChobFile from, PPtr p, SceneBuild scene = null)
        {
            if (p.IsNull) return null;
            var f = Target(from, p);
            if (f == null) { Problem("unresolved external file " + p + " from " + from.Name); return null; }
            if (scene != null && f == scene.File && scene.Objects.TryGetValue(p.PathId, out var so)) return so;
            if (built.TryGetValue((f, p.PathId), out var o)) return o;
            if (!f.Objects.TryGetValue(p.PathId, out var co)) { Problem("missing object " + p.PathId + " in " + f.Name); return null; }
            return BuildAsset(co);
        }

        static UObject BuildAsset(ChobObject co)
        {
            switch (co.Type)
            {
                case "GameObject":
                    {
                        var build = SceneBuild.Prefab(co);
                        return build.Objects.TryGetValue(co.PathId, out var go) ? go : null;
                    }
                case "MonoBehaviour":
                    {
                        var goPtr = Tree.Ptr(co.Tree, "m_GameObject");
                        if (!goPtr.IsNull)
                        {
                            // A prefab component: build its hierarchy.
                            var goObj = Lookup(co.File, goPtr);
                            if (goObj != null) BuildAsset(goObj);
                            return built.TryGetValue((co.File, co.PathId), out var c) ? c : null;
                        }
                        var type = ScriptType(co);
                        if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
                        {
                            Problem("asset MonoBehaviour without a ScriptableObject type: " + co.File.Name + " " + co.PathId + " " + type);
                            return null;
                        }
                        Loading++;
                        ScriptableObject so;
                        try { so = (ScriptableObject)Lifecycle.NewManaged(type); }
                        finally { Loading--; }
                        // The constructor registered it as a run-time object; it is an asset.
                        var rec = (NAsset)Objects.Of(so);
                        rec.Source = co; rec.Name = Tree.Str(co.Tree, "m_Name"); rec.Persistent = true;
                        built[(co.File, co.PathId)] = so;
                        FieldDecoder.Apply(so, co, null);
                        Icalls.ScriptableMessages(so);
                        return so;
                    }
                default:
                    {
                        if (IsComponentClass(co.Type))
                        {
                            var goObj = Lookup(co.File, Tree.Ptr(co.Tree, "m_GameObject"));
                            if (goObj != null) BuildAsset(goObj);
                            return built.TryGetValue((co.File, co.PathId), out var c) ? c : null;
                        }
                        var type = UnityType(co.Type);
                        if (type == null || !typeof(UObject).IsAssignableFrom(type) || type.IsAbstract)
                        {
                            Problem("no managed type for asset class " + co.Type);
                            return null;
                        }
                        var managed = Lifecycle.NewManaged(type);
                        Objects.Register(managed, new NAsset { Source = co }, Tree.Str(co.Tree, "m_Name"), persistent: true);
                        built[(co.File, co.PathId)] = managed;
                        Assets.OnBuilt?.Invoke(managed, co);
                        return managed;
                    }
            }
        }

        static readonly Dictionary<string, Type> unityTypes = new Dictionary<string, Type>();

        // Editor-side class names of assets whose player type has another name.
        static readonly Dictionary<string, string> playerClass = new Dictionary<string, string>
        {
            ["AudioMixerController"] = "AudioMixer",
            ["AudioMixerGroupController"] = "AudioMixerGroup",
            ["AudioMixerSnapshotController"] = "AudioMixerSnapshot",
        };

        public static Type UnityType(string className)
        {
            if (unityTypes.TryGetValue(className, out var t)) return t;
            if (playerClass.TryGetValue(className, out var pc)) { t = UnityType(pc); unityTypes[className] = t; return t; }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("UnityEngine")) continue;
                t = asm.GetType("UnityEngine." + className, false) ?? asm.GetType("UnityEngine.Audio." + className, false)
                    ?? asm.GetType("UnityEngine.U2D." + className, false);
                if (t != null) break;
            }
            unityTypes[className] = t;
            return t;
        }

        static readonly HashSet<string> nonComponents = new HashSet<string> { "GameObject", "MonoBehaviour" };

        public static bool IsComponentClass(string className)
        {
            if (nonComponents.Contains(className)) return false;
            var t = UnityType(className);
            return t != null && typeof(Component).IsAssignableFrom(t);
        }

        static readonly Dictionary<(ChobFile, long), Type> scriptTypes = new Dictionary<(ChobFile, long), Type>();

        /// <summary>The managed class of a MonoBehaviour object, from its MonoScript.</summary>
        public static Type ScriptType(ChobObject mb)
        {
            var sp = Tree.Ptr(mb.Tree, "m_Script");
            var script = Lookup(mb.File, sp);
            if (script == null) { Problem("MonoBehaviour without a MonoScript: " + mb.File.Name + " " + mb.PathId); return null; }
            if (scriptTypes.TryGetValue((script.File, script.PathId), out var t)) return t;
            string asmName = Tree.Str(script.Tree, "m_AssemblyName");
            if (asmName.EndsWith(".dll")) asmName = asmName.Substring(0, asmName.Length - 4);
            string ns = Tree.Str(script.Tree, "m_Namespace"), cls = Tree.Str(script.Tree, "m_ClassName");
            string full = ns.Length > 0 ? ns + "." + cls : cls;
            try { t = Assembly.Load(asmName).GetType(full, false); }
            catch (Exception) { t = null; }
            if (t == null) Problem("script class not found: " + asmName + " " + full);
            scriptTypes[(script.File, script.PathId)] = t;
            return t;
        }

        public static string TagName(long id) => Tags.TryGetValue((int)id, out var s) ? s : "Untagged";
    }

    public static class Assets
    {
        /// <summary>Engine modules fill in their state for a newly built asset (sprites, clips, ...).</summary>
        public static Action<UObject, ChobObject> OnBuilt;
    }

    /// <summary>
    /// Builds the GameObjects of one serialized file: a scene (live, activated)
    /// or a prefab hierarchy (an asset: never awake, never updated).
    /// </summary>
    public sealed class SceneBuild
    {
        public ChobFile File;
        public bool IsAsset;
        public int SceneHandle;
        public readonly Dictionary<long, UObject> Objects = new Dictionary<long, UObject>();
        public readonly List<NGameObject> GameObjects = new List<NGameObject>();
        public readonly List<(NComponent rec, ChobObject src)> Components = new List<(NComponent, ChobObject)>();

        public static SceneBuild Scene(ChobFile f, int sceneHandle)
        {
            var b = new SceneBuild { File = f, SceneHandle = sceneHandle };
            b.Create(f.Ordered.Where(o => o.Type == "GameObject").ToList());
            b.Finish();
            return b;
        }

        /// <summary>The prefab hierarchy containing a GameObject asset.</summary>
        public static SceneBuild Prefab(ChobObject goObj)
        {
            // Climb to the root, then take the whole tree.
            var f = goObj.File;
            var root = goObj;
            for (int guard = 0; guard < 256; guard++)
            {
                var tr = TransformOf(root);
                var father = tr == null ? null : AssetDb.Lookup(f, Tree.Ptr(tr.Tree, "m_Father"));
                if (father == null) break;
                var parentGo = AssetDb.Lookup(f, Tree.Ptr(father.Tree, "m_GameObject"));
                if (parentGo == null) break;
                root = parentGo;
            }
            var list = new List<ChobObject>();
            void Collect(ChobObject g)
            {
                list.Add(g);
                var tr = TransformOf(g);
                if (tr == null) return;
                foreach (var c in Tree.List(tr.Tree, "m_Children"))
                    if (c is PPtr cp && AssetDb.Lookup(f, cp) is ChobObject ct && AssetDb.Lookup(f, Tree.Ptr(ct.Tree, "m_GameObject")) is ChobObject cg)
                        Collect(cg);
            }
            Collect(root);
            var b = new SceneBuild { File = f, IsAsset = true };
            b.Create(list);
            b.Finish();
            return b;
        }

        static ChobObject TransformOf(ChobObject go)
        {
            foreach (var c in Tree.List(go.Tree, "m_Component"))
            {
                var p = Tree.Ptr(c, "component");
                var co = AssetDb.Lookup(go.File, p);
                if (co != null && (co.Type == "Transform" || co.Type == "RectTransform")) return co;
            }
            return null;
        }

        void Create(List<ChobObject> gameObjects)
        {
            foreach (var g in gameObjects)
            {
                var managed = (GameObject)Lifecycle.NewManaged(typeof(GameObject));
                var go = Lifecycle.CreateGameObject(managed, Tree.Str(g.Tree, "m_Name"), persistent: true);
                go.IsAsset = IsAsset;
                go.Scene = IsAsset ? 0 : SceneHandle;
                go.Layer = (int)Tree.Int(g.Tree, "m_Layer");
                go.Tag = AssetDb.TagName(Tree.Int(g.Tree, "m_Tag"));
                go.ActiveSelf = Tree.Bool(g.Tree, "m_IsActive", true);
                Objects[g.PathId] = managed;
                AssetDb.SetBuilt(File, g.PathId, managed);
                GameObjects.Add(go);
                bool firstTransform = true;
                foreach (var c in Tree.List(g.Tree, "m_Component"))
                {
                    var co = AssetDb.Lookup(File, Tree.Ptr(c, "component"));
                    if (co == null) continue;
                    if ((co.Type == "Transform" || co.Type == "RectTransform") && firstTransform)
                    {
                        firstTransform = false;
                        // CreateGameObject made a plain Transform; a RectTransform replaces it.
                        if (co.Type == "RectTransform") ReplaceTransform(go, typeof(RectTransform));
                        Objects[co.PathId] = go.Transform.Managed;
                        AssetDb.SetBuilt(File, co.PathId, go.Transform.Managed);
                        Components.Add((go.Transform, co));
                        continue;
                    }
                    var rec = CreateComponent(go, co);
                    if (rec == null) continue;
                    Objects[co.PathId] = rec.Managed;
                    AssetDb.SetBuilt(File, co.PathId, rec.Managed);
                    Components.Add((rec, co));
                }
            }
        }

        static void ReplaceTransform(NGameObject go, Type t)
        {
            var old = go.Transform;
            var managed = (Transform)Lifecycle.NewManaged(t);
            var rec = Engine.Objects.Register(managed, new NTransform(), go.Name, persistent: true);
            rec.GameObject = go;
            go.Components[go.Components.IndexOf(old)] = rec;
            go.Transform = rec;
            Engine.Objects.Release(old);
        }

        NComponent CreateComponent(NGameObject go, ChobObject co)
        {
            if (co.Type == "MonoBehaviour")
            {
                var type = AssetDb.ScriptType(co);
                if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    AssetDb.Problem("missing script on " + go.Name + " (" + File.Name + " " + co.PathId + ")");
                    return null;
                }
                var mb = (MonoBehaviour)Lifecycle.NewManaged(type);
                var b = Behaviours.Attach(mb, go);
                b.Persistent = true;
                b.Enabled = Tree.Bool(co.Tree, "m_Enabled", true);
                return b;
            }
            var t = AssetDb.UnityType(co.Type);
            if (t == null || !typeof(Component).IsAssignableFrom(t) || t.IsAbstract)
            {
                AssetDb.Problem("no managed component type " + co.Type);
                return null;
            }
            var managed = (Component)Lifecycle.NewManaged(t);
            var rec = Engine.Objects.Register(managed, Builtins.Create(t), go.Name, persistent: true);
            rec.GameObject = go;
            rec.Enabled = Tree.Bool(co.Tree, "m_Enabled", true);
            rec.Data = co;
            go.Components.Add(rec);
            return rec;
        }

        void Finish()
        {
            // Hierarchy.
            foreach (var (rec, co) in Components)
            {
                if (!(rec is NTransform t)) continue;
                t.LocalPosition = Tree.Vec3(co.Tree, "m_LocalPosition");
                t.LocalRotation = Tree.Quat(co.Tree, "m_LocalRotation");
                t.LocalScale = Tree.Vec3(co.Tree, "m_LocalScale");
                t.Data = co;
                foreach (var c in Tree.List(co.Tree, "m_Children"))
                    if (c is PPtr cp && Resolve(cp) is Transform ct && Engine.Objects.Tr(ct) is NTransform child)
                    {
                        child.Parent = t;
                        t.Children.Add(child);
                    }
            }
            foreach (var go in GameObjects)
                if (go.Transform.Parent == null && !IsAsset) Scenes.AddRoot(go);
            // Fields: scripts by Unity's rules, built-ins by their modules.
            foreach (var (rec, co) in Components)
            {
                if (rec is NBehaviour) FieldDecoder.Apply(rec.Managed, co, this);
                else if (!(rec is NTransform)) Builtins.OnLoaded(rec, co, this);
                else Builtins.OnLoaded(rec, co, this);
            }
        }

        public UObject Resolve(PPtr p) => AssetDb.Resolve(File, p, this);

        /// <summary>Unity's scene activation: Awake + OnEnable for every active object.</summary>
        public void Activate()
        {
            foreach (var go in GameObjects)
                if (go.Transform.Parent == null && go.ActiveInHierarchy) Lifecycle.ActivateTreeNew(go.Transform);
        }
    }
}
