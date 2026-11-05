using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    public static class Find
    {
        static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        public static Type TypeByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (typeCache.TryGetValue(name, out var t)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(name, false) ?? asm.GetType("UnityEngine." + name, false);
                if (t != null) break;
            }
            if (t == null)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetTypes().FirstOrDefault(x => x.Name == name && typeof(Component).IsAssignableFrom(x)); }
                    catch (ReflectionTypeLoadException) { }
                    if (t != null) break;
                }
            typeCache[name] = t;
            return t;
        }

        public static Component Component(NGameObject go, Type type)
        {
            if (go == null || type == null) return null;
            foreach (var c in go.Components)
                if (type.IsInstanceOfType(c.Managed)) return (Component)c.Managed;
            return null;
        }

        /// <summary>
        /// Active for the component queries: in a scene, active in the hierarchy; a prefab asset by its own
        /// active flags (Unity answers GetComponentInChildren on a prefab: the Funhouse run-and-gun level reads
        /// its duck prefabs' sprite sizes so).
        /// </summary>
        static bool Active(NGameObject g)
        {
            if (g.ActiveInHierarchy) return true;
            if (!g.IsAsset) return false;
            for (var t = g.Transform; t != null; t = t.Parent) if (!t.GameObject.ActiveSelf) return false;
            return true;
        }

        public static Component InChildren(NGameObject go, Type type, bool includeInactive)
        {
            if (go == null || type == null) return null;
            if (includeInactive || Active(go))
            {
                var c = Component(go, type);
                if (c != null) return c;
            }
            foreach (var child in go.Transform.Children)
            {
                var c = InChildren(child.GameObject, type, includeInactive);
                if (c != null) return c;
            }
            return null;
        }

        public static Component InParent(NGameObject go, Type type)
        {
            for (var t = go?.Transform; t != null; t = t.Parent)
            {
                if (!Active(t.GameObject)) continue;
                var c = Component(t.GameObject, type);
                if (c != null) return c;
            }
            return null;
        }

        public static Array Components(NGameObject go, Type type, bool typedArray, bool recursive, bool includeInactive, bool reverse, object resultList)
        {
            var found = new List<Component>();
            void Collect(NGameObject g, bool top)
            {
                if (top || includeInactive || Active(g))
                    foreach (var c in g.Components)
                        if (type == null || type.IsInstanceOfType(c.Managed)) found.Add((Component)c.Managed);
                if (!recursive) return;
                foreach (var child in g.Transform.Children)
                    if (includeInactive || Active(child.GameObject)) Collect(child.GameObject, false);
            }
            if (go != null)
            {
                if (reverse)
                {
                    // GetComponentsInParent: this object then its ancestors.
                    for (var t = go.Transform; t != null; t = t.Parent)
                        if (includeInactive || Active(t.GameObject))
                            foreach (var c in t.GameObject.Components)
                                if (type == null || type.IsInstanceOfType(c.Managed)) found.Add((Component)c.Managed);
                }
                else Collect(go, true);
            }
            if (resultList is IList list)
            {
                list.Clear();
                foreach (var c in found) list.Add(c);
                return null;
            }
            var arr = Array.CreateInstance(typedArray && type != null ? type : typeof(Component), found.Count);
            for (int i = 0; i < found.Count; i++) arr.SetValue(found[i], i);
            return arr;
        }

        public static UObject[] ObjectsOfType(Type type, bool includeAssets)
        {
            var result = new List<UObject>();
            IEnumerable<NObject> pool = typeof(MonoBehaviour).IsAssignableFrom(type) ? Typed.Behaviours.ToArray()
                : type == typeof(GameObject) ? Typed.GameObjects.ToArray() : Objects.Live();
            foreach (var o in pool)
            {
                if (!type.IsInstanceOfType(o.Managed)) continue;
                var go = o as NGameObject ?? (o as NComponent)?.GameObject;
                if (go == null) { if (includeAssets || !o.Persistent) result.Add(o.Managed); continue; }
                if (!includeAssets && !go.ActiveInHierarchy) continue;   // FindObjectsOfType skips inactive objects
                if (o is NComponent c && !(o is NTransform) && o is NBehaviour b && !includeAssets && !b.Enabled) { result.Add(o.Managed); continue; }
                result.Add(o.Managed);
            }
            var arr = (UObject[])Array.CreateInstance(type, result.Count);
            for (int i = 0; i < result.Count; i++) arr[i] = result[i];
            return arr;
        }

        public static GameObject[] WithTag(string tag, bool first)
        {
            var r = new List<GameObject>();
            foreach (var g in Typed.GameObjects)
                if (g.Tag == tag && g.ActiveInHierarchy) { r.Add((GameObject)g.Managed); if (first) break; }
            return r.ToArray();
        }

        /// <summary>GameObject.Find: a name, or a path from a root ("/A/B" or "A/B").</summary>
        public static GameObject ByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            bool rooted = path.StartsWith("/");
            string[] parts = path.Trim('/').Split('/');
            foreach (var g in Typed.GameObjects)
            {
                if (!g.ActiveInHierarchy) continue;
                if (g.Name != parts[parts.Length - 1]) continue;
                var t = g.Transform;
                bool ok = true;
                for (int i = parts.Length - 2; i >= 0; i--)
                {
                    t = t.Parent;
                    if (t == null || t.GameObject.Name != parts[i]) { ok = false; break; }
                }
                if (ok && (!rooted || t.Parent == null)) return (GameObject)g.Managed;
            }
            return null;
        }

        public static Transform ChildByPath(NTransform t, string path)
        {
            if (t == null || path == null) return null;
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0) continue;
                if (part == "..") { t = t.Parent; if (t == null) return null; continue; }
                NTransform next = null;
                foreach (var c in t.Children) if (c.GameObject.Name == part) { next = c; break; }
                if (next == null) return null;
                t = next;
            }
            return (Transform)t.Managed;
        }
    }

    public static class Messages
    {
        /// <summary>SendMessage (mode 0), SendMessageUpwards (1), BroadcastMessage (2).</summary>
        public static void Send(NGameObject go, string method, object arg, SendMessageOptions options, int mode)
        {
            if (go == null) return;
            bool received = false;
            void On(NGameObject g)
            {
                foreach (var c in g.Components.ToArray())
                {
                    if (!(c is NBehaviour b) || !Behaviours.ActiveAndEnabled(b)) continue;
                    var m = (arg != null ? MessageSet.Find(b.Managed.GetType(), method, 1) : null) ?? MessageSet.Find(b.Managed.GetType(), method, 0);
                    if (m == null) continue;
                    received = true;
                    var r = Behaviours.Send(b, m, m.GetParameters().Length == 1 ? arg : null);
                    if (r is IEnumerator e) Coroutines.Start(b, e);
                }
            }
            void Down(NTransform t) { if (!t.GameObject.ActiveInHierarchy) return; On(t.GameObject); foreach (var c in t.Children.ToArray()) Down(c); }
            if (mode == 0) On(go);
            else if (mode == 1) for (var t = go.Transform; t != null; t = t.Parent) On(t.GameObject);
            else Down(go.Transform);
            if (!received && options == SendMessageOptions.RequireReceiver)
                Log.Line("SendMessage " + method + " has no receiver! (" + go.Name + ")");
        }
    }

    /// <summary>Object.Instantiate: a deep copy of a GameObject hierarchy with Unity's serialization rules.</summary>
    public static class Clone
    {
        /// <summary>Instantiate calls, objects copied, and the time making the copies and copying their state (the scene timing lines).</summary>
        public static long Calls, Copied, TreeTicks, FieldTicks;

        public static UObject Instantiate(UObject original, NTransform parent, bool worldPositionStays, Vector3? pos, Quaternion? rot)
        {
            var src = Objects.Of(original);
            if (src == null) throw new ArgumentException("The Object you want to instantiate is null.");
            var srcGo = src as NGameObject ?? (src as NComponent)?.GameObject;
            if (srcGo == null) return CloneAsset(src);

            Calls++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var map = new Dictionary<NObject, NObject>();
            var srcLayout = NRectTransform.Snapshot(srcGo.Transform);
            var root = CloneTree(srcGo.Transform, map);
            root.GameObject.Name = srcGo.Name + "(Clone)";
            foreach (var c in root.GameObject.Components) c.Name = root.GameObject.Name;
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            // Remap references to objects inside the copied hierarchy.
            foreach (var kv in map)
                if (kv.Key is NComponent sc && kv.Value is NComponent dc)
                {
                    if (sc is NBehaviour) Serialization.CopyFields(sc.Managed, dc.Managed, map);
                    else Builtins.CopyState(sc, dc, map);
                }
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            Copied += map.Count;
            TreeTicks += t1 - t0;
            FieldTicks += t2 - t1;
            var rt = root;
            if (parent != null) { rt.Parent = parent; parent.Children.Add(rt); }
            else Scenes.AddRoot(rt.GameObject);
            if (pos.HasValue)
            {
                Vector3 p = pos.Value; Quaternion q = rot ?? Quaternion.identity;
                if (parent != null)
                {
                    MathX.Invert(parent.LocalToWorld, out var inv);
                    rt.LocalPosition = inv.MultiplyPoint3x4(p);
                    rt.LocalRotation = MathX.Inverse(parent.Rotation) * q;
                }
                else { rt.LocalPosition = p; rt.LocalRotation = q; }
            }
            else if (parent != null && worldPositionStays)
            {
                var sp = srcGo.Transform;
                MathX.Invert(parent.LocalToWorld, out var inv);
                rt.LocalPosition = inv.MultiplyPoint3x4(sp.Position);
                rt.LocalRotation = MathX.Inverse(parent.Rotation) * sp.Rotation;
            }
            // The copies keep the originals' anchored positions (Unity stores those),
            // so rects follow the new parent's size; a root placed in world space keeps its pose.
            bool rootPlaced = pos.HasValue || (parent != null && worldPositionStays);
            for (int i = 0; i < srcLayout.Count; i++)
            {
                if (rootPlaced && srcLayout[i].t == srcGo.Transform) continue;
                if (map.TryGetValue(srcLayout[i].t, out var copy) && copy is NRectTransform crt) crt.AnchoredPosition = srcLayout[i].ap;
            }
            rt.Invalidate();
            // Lifecycle of the copy: Awake/OnEnable for active objects, now.
            if (rt.GameObject.ActiveInHierarchy) Lifecycle.ActivateTreeNew(rt);
            var result = map[src];
            return result.Managed;
        }

        static NTransform CloneTree(NTransform st, Dictionary<NObject, NObject> map)
        {
            var sg = st.GameObject;
            var go = Lifecycle.CreateGameObject((GameObject)Lifecycle.NewManaged(typeof(GameObject)), sg.Name);
            go.Layer = sg.Layer; go.Tag = sg.Tag; go.ActiveSelf = sg.ActiveSelf;
            var t = st is NRectTransform ? Lifecycle.SwapTransform(go, true) : go.Transform;
            t.LocalPosition = st.LocalPosition; t.LocalRotation = st.LocalRotation; t.LocalScale = st.LocalScale;
            if (t is NRectTransform rt) rt.CopyFrom(st, null);
            map[sg] = go; map[st] = t;
            foreach (var sc in sg.Components)
            {
                if (sc is NTransform) continue;
                var managed = (Component)Lifecycle.NewManaged(sc.Managed.GetType());
                NComponent dc;
                if (managed is MonoBehaviour mb) { dc = Behaviours.Attach(mb, go); dc.Enabled = sc.Enabled; }
                else
                {
                    dc = Objects.Register(managed, Builtins.Create(managed.GetType()), go.Name);
                    dc.GameObject = go; dc.Enabled = sc.Enabled;
                    go.Components.Add(dc);
                }
                map[sc] = dc;
            }
            foreach (var sch in st.Children)
            {
                var ch = CloneTree(sch, map);
                ch.Parent = t; t.Children.Add(ch);
            }
            return t;
        }

        static UObject CloneAsset(NObject src)
        {
            var managed = Lifecycle.NewManaged(src.Managed.GetType());
            var rec = Objects.Register(managed, new NObject(), src.Name + "(Clone)");
            Serialization.CopyFields(src.Managed, managed, null);
            Builtins.CopyAsset(src, rec);
            return managed;
        }
    }

    /// <summary>Unity's rules for which fields of a script are serialized.</summary>
    public static class Serialization
    {
        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        public static FieldInfo[] Fields(Type t)
        {
            if (fieldCache.TryGetValue(t, out var f)) return f;
            var list = new List<FieldInfo>();
            var chain = new List<Type>();
            for (var c = t; c != null && c != typeof(MonoBehaviour) && c != typeof(ScriptableObject) && c != typeof(object); c = c.BaseType) chain.Add(c);
            chain.Reverse();   // base class fields first, as Unity lays them out
            foreach (var c in chain)
                foreach (var fi in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (IsSerialized(fi)) list.Add(fi);
            f = list.ToArray();
            fieldCache[t] = f;
            return f;
        }

        public static bool IsSerialized(FieldInfo fi)
        {
            if (fi.IsStatic || fi.IsInitOnly || fi.IsLiteral || fi.IsNotSerialized) return false;
            var dt = fi.DeclaringType.IsGenericType ? fi.DeclaringType.GetGenericTypeDefinition() : fi.DeclaringType;
            bool wasPublic = fi.IsPublic && !Publicized.Fields.Contains(dt.FullName + "::" + fi.Name);
            if (!wasPublic && fi.GetCustomAttribute<SerializeField>() == null) return false;
            return IsSerializableType(fi.FieldType);
        }

        public static bool IsSerializableType(Type t)
        {
            if (t.IsPrimitive || t.IsEnum || t == typeof(string)) return t != typeof(IntPtr) && t != typeof(UIntPtr);
            if (typeof(UObject).IsAssignableFrom(t)) return true;
            if (typeof(Delegate).IsAssignableFrom(t)) return false;
            if (t == typeof(object)) return false;
            if (t.IsArray) return t.GetArrayRank() == 1 && IsElementType(t.GetElementType());
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return IsElementType(t.GetGenericArguments()[0]);
            if (t.Namespace == "UnityEngine" && t.IsValueType) return true;   // Vector3, Color, Rect, ...
            if (t == typeof(AnimationCurve) || t == typeof(Gradient) || t == typeof(RectOffset)) return true;   // native serialized classes
            if (t.IsGenericType || t.IsAbstract || t.IsInterface) return false;
            return t.GetCustomAttribute<SerializableAttribute>() != null || t.IsSerializable;
        }

        /// <summary>Unity stops serializing nested [Serializable] classes below depth 7.</summary>
        public static bool DepthAllows(Type t, int depth)
        {
            if (depth <= 7) return true;
            if (t.IsArray) t = t.GetElementType();
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) t = t.GetGenericArguments()[0];
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || typeof(UObject).IsAssignableFrom(t) || (t.IsValueType && t.Namespace == "UnityEngine");
        }

        static bool IsElementType(Type e) => !(e.IsArray || (e.IsGenericType && e.GetGenericTypeDefinition() == typeof(List<>))) && IsSerializableType(e);

        public static void CopyFields(object src, object dst, Dictionary<NObject, NObject> map) => CopyInto(src, dst, map, 0);

        /// <summary>
        /// How a type's serialized fields are copied, a step per class that declares some (base classes
        /// first): the class's generated copy (the patcher's __ch360_copy, used when its field names are
        /// the ones Fields picks), or reflection.
        /// </summary>
        sealed class CopyStep
        {
            public Action<object, object, object, int> Fast;
            public FieldInfo[] Fields;
        }

        static readonly Dictionary<Type, CopyStep[]> plans = new Dictionary<Type, CopyStep[]>();
        static readonly bool noFastCopies = Environment.GetEnvironmentVariable("CH360_REFLECT_COPIES") == "1";
        /// <summary>Classes copied by their generated copies and by reflection (logged with the scene timing).</summary>
        public static int FastClasses, SlowClasses;

        static CopyStep[] PlanOf(Type t)
        {
            if (plans.TryGetValue(t, out var p)) return p;
            var steps = new List<CopyStep>();
            var all = Fields(t);
            for (int i = 0; i < all.Length;)
            {
                var level = all[i].DeclaringType;
                int j = i;
                while (j < all.Length && all[j].DeclaringType == level) j++;
                var fs = all[i..j];
                i = j;
                Action<object, object, object, int> fast = null;
                if (!noFastCopies && !level.IsGenericType)
                {
                    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly;
                    var copy = level.GetMethod("__ch360_copy", flags);
                    var names = level.GetMethod("__ch360_fields", flags);
                    if (copy != null && names != null)
                    {
                        var generated = ((string)names.Invoke(null, null)).Split(',');
                        var mine = Array.ConvertAll(fs, f => f.Name);
                        Array.Sort(generated, StringComparer.Ordinal);
                        Array.Sort(mine, StringComparer.Ordinal);
                        if (string.Join(",", generated) == string.Join(",", mine))
                            fast = (Action<object, object, object, int>)copy.CreateDelegate(typeof(Action<object, object, object, int>));
                        else
                            Log.Line($"CLONE {level.FullName}: the generated copy's fields ({string.Join(",", generated)}) aren't the serialized ones ({string.Join(",", mine)}); copied by reflection");
                    }
                }
                if (fast != null) FastClasses++; else SlowClasses++;
                steps.Add(new CopyStep { Fast = fast, Fields = fs });
            }
            p = steps.ToArray();
            plans[t] = p;
            return p;
        }

        /// <summary>The serialized fields of src into dst, each copied as CopyValue does at that depth.</summary>
        static void CopyInto(object src, object dst, Dictionary<NObject, NObject> map, int depth)
        {
            foreach (var step in PlanOf(src.GetType()))
            {
                // A generated copy takes UnityEngine structs as they are: CopyValue drops them below depth 7.
                if (step.Fast != null && depth <= 7) step.Fast(src, dst, map, depth);
                else
                    foreach (var f in step.Fields)
                        f.SetValue(dst, CopyValue(f.GetValue(src), f.FieldType, map, depth));
            }
        }

        /// <summary>A generated copy's object field: references into the copied hierarchy point at the copies.</summary>
        public static UObject CloneRef(UObject v, object map)
        {
            if ((object)v == null) return null;
            var rec = Objects.Of(v);
            if (rec != null && map is Dictionary<NObject, NObject> m && m.TryGetValue(rec, out var mapped)) return (UObject)mapped.Managed;
            return v;
        }

        /// <summary>A generated copy's other fields (arrays, lists, nested classes, curves), as CopyValue copies them.</summary>
        public static object CloneValue(object v, Type t, object map, int depth) => CopyValue(v, t, map as Dictionary<NObject, NObject>, depth);

        static object CopyValue(object v, Type t, Dictionary<NObject, NObject> map, int depth)
        {
            if (v == null) return null;
            if (v is UObject uo)
            {
                var rec = Objects.Of(uo);
                if (rec != null && map != null && map.TryGetValue(rec, out var mapped)) return mapped.Managed;
                return uo;
            }
            if (t.IsPrimitive || t.IsEnum || v is string) return v;
            if (depth > 7) return null;   // Unity's serialization depth limit
            if (v is Array a)
            {
                var et = a.GetType().GetElementType();
                var r = Array.CreateInstance(et, a.Length);
                for (int i = 0; i < a.Length; i++) r.SetValue(CopyValue(a.GetValue(i), et, map, depth + 1), i);
                return r;
            }
            if (v is IList list && v.GetType().IsGenericType)
            {
                var et = v.GetType().GetGenericArguments()[0];
                var r = (IList)Activator.CreateInstance(v.GetType());
                foreach (var x in list) r.Add(CopyValue(x, et, map, depth + 1));
                return r;
            }
            if (t.IsValueType && t.Namespace == "UnityEngine") return v;
            // Native-backed classes keep their state outside the managed fields: copy it through the API.
            if (v is RectOffset ro) return new RectOffset(ro.left, ro.right, ro.top, ro.bottom);
            if (v is AnimationCurve ac) return new AnimationCurve(ac.keys) { preWrapMode = ac.preWrapMode, postWrapMode = ac.postWrapMode };
            if (v is Gradient gr) { var g = new Gradient { mode = gr.mode }; g.SetKeys(gr.colorKeys, gr.alphaKeys); return g; }
            // [Serializable] class or struct: copied by value, field by field.
            var copy = FieldDecoder.New(v.GetType());
            CopyInto(v, copy, map, depth + 1);
            return copy;
        }
    }

    public static class Perlin
    {
        static readonly int[] p = Build();
        static int[] Build()
        {
            int[] perm = {151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180};
            var r = new int[512];
            for (int i = 0; i < 512; i++) r[i] = perm[i & 255];
            return r;
        }
        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        static float Grad(int h, float x, float y) { switch (h & 3) { case 0: return x + y; case 1: return -x + y; case 2: return x - y; default: return -x - y; } }
        public static float Noise(float x, float y)
        {
            int X = (int)MathF.Floor(x) & 255, Y = (int)MathF.Floor(y) & 255;
            x -= MathF.Floor(x); y -= MathF.Floor(y);
            float u = Fade(x), v = Fade(y);
            int A = p[X] + Y, B = p[X + 1] + Y;
            float n = Lerp(v, Lerp(u, Grad(p[A], x, y), Grad(p[B], x - 1, y)), Lerp(u, Grad(p[A + 1], x, y - 1), Grad(p[B + 1], x - 1, y - 1)));
            return (n + 0.69f) / 1.483f;
        }
        static float Lerp(float t, float a, float b) => a + t * (b - a);
    }
}
