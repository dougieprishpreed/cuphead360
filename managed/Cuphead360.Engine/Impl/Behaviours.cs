using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>
    /// A MonoBehaviour's engine state: which Unity messages its class defines
    /// (found once per type, as the Unity player does) and its lifecycle flags.
    /// </summary>
    public sealed class NBehaviour : NComponent
    {
        public MessageSet Messages;
        public bool Awoken, Started, StartQueued;
        public readonly List<CoroutineState> Coroutines = new List<CoroutineState>();
        public int TrackSeq;                 // the order the player loop first tracked it in
        public bool InInvokers, CoroutinesDirty;
        public readonly List<InvokeState> Invokes = new List<InvokeState>();
        public long Order;
        /// <summary>Its messages' time since the last PlayerLoop.TopReport (with Profile on).</summary>
        public long ProfTicks;
        /// <summary>Messages without arguments, bound to this behaviour (reflection's Invoke is slow and allocates).</summary>
        public Dictionary<MethodInfo, Action> Calls;
        /// <summary>
        /// The messages sent to it, each with its bound delegate (an Action, or an Action of Collider2D
        /// or Collision2D for the physics messages; null when it must go through reflection), found by
        /// reference: two dictionary lookups a message (a MethodInfo key hashes slowly) were about 3%
        /// of the first run-and-gun level's PC samples.
        /// </summary>
        internal MethodInfo[] SentKeys;
        internal Delegate[] SentCalls;
        internal int SentCount;
        /// <summary>Its class's script execution order (ScriptOrder).</summary>
        public int ExecOrder;
    }

    /// <summary>
    /// Unity's script execution order (Project Settings), from each MonoScript's m_ExecutionOrder in
    /// the build; a class takes its nearest base class's, which is where Cuphead sets most of them
    /// (Level -90, the player's controller and components, the camera; ParallaxLayer 50,
    /// LevelPlayerWeaponManager 100, ObjectPool 200). It orders Awake and OnEnable within an
    /// activation, Start, and each update pass. Without it the Devil's giant head woke before the
    /// level and found no Level.Current.
    /// </summary>
    public static class ScriptOrder
    {
        static Dictionary<string, int> byName;
        static readonly Dictionary<Type, int> byType = new Dictionary<Type, int>();

        public static int Of(Type t)
        {
            if (t == null) return 0;
            if (byType.TryGetValue(t, out int o)) return o;
            byName ??= Load();
            o = 0;
            for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
                if (byName.TryGetValue(c.FullName ?? c.Name, out int v)) { o = v; break; }
            byType[t] = o;
            return o;
        }

        static Dictionary<string, int> Load()
        {
            var d = new Dictionary<string, int>();
            var f = AssetDb.File("globalgamemanagers.assets");
            if (f == null) return d;
            foreach (var co in f.Ordered)
            {
                if (co.Type != "MonoScript") continue;
                int v = (int)Tree.Int(co.Tree, "m_ExecutionOrder");
                if (v == 0) continue;
                string ns = Tree.Str(co.Tree, "m_Namespace"), cls = Tree.Str(co.Tree, "m_ClassName");
                d[string.IsNullOrEmpty(ns) ? cls : ns + "." + cls] = v;
            }
            return d;
        }
    }

    public sealed class MessageSet
    {
        public MethodInfo Awake, Start, Update, FixedUpdate, LateUpdate, OnEnable, OnDisable, OnDestroy;
        public MethodInfo OnTriggerEnter2D, OnTriggerStay2D, OnTriggerExit2D, OnCollisionEnter2D, OnCollisionStay2D, OnCollisionExit2D;
        public MethodInfo OnApplicationQuit, OnLevelWasLoaded, OnBecameVisible, OnBecameInvisible, OnWillRenderObject, OnRenderImage;
        public bool StartIsCoroutine;

        static readonly Dictionary<Type, MessageSet> cache = new Dictionary<Type, MessageSet>();

        public static MessageSet For(Type t)
        {
            if (cache.TryGetValue(t, out var s)) return s;
            s = new MessageSet
            {
                Awake = Find(t, "Awake"), Start = Find(t, "Start"), Update = Find(t, "Update"),
                FixedUpdate = Find(t, "FixedUpdate"), LateUpdate = Find(t, "LateUpdate"),
                OnEnable = Find(t, "OnEnable"), OnDisable = Find(t, "OnDisable"), OnDestroy = Find(t, "OnDestroy"),
                OnTriggerEnter2D = Find(t, "OnTriggerEnter2D", 1), OnTriggerStay2D = Find(t, "OnTriggerStay2D", 1),
                OnTriggerExit2D = Find(t, "OnTriggerExit2D", 1), OnCollisionEnter2D = Find(t, "OnCollisionEnter2D", 1),
                OnCollisionStay2D = Find(t, "OnCollisionStay2D", 1), OnCollisionExit2D = Find(t, "OnCollisionExit2D", 1),
                OnApplicationQuit = Find(t, "OnApplicationQuit"), OnLevelWasLoaded = Find(t, "OnLevelWasLoaded", 1),
                OnBecameVisible = Find(t, "OnBecameVisible"), OnBecameInvisible = Find(t, "OnBecameInvisible"),
                OnWillRenderObject = Find(t, "OnWillRenderObject"), OnRenderImage = Find(t, "OnRenderImage", 2),
            };
            s.StartIsCoroutine = s.Start != null && typeof(IEnumerator).IsAssignableFrom(s.Start.ReturnType);
            cache[t] = s;
            return s;
        }

        /// <summary>
        /// Unity's message lookup: the most derived declaration of that name with
        /// the expected parameter count, public or not, up to MonoBehaviour.
        /// </summary>
        public static MethodInfo Find(Type t, string name, int parms = 0)
        {
            if (found.TryGetValue((t, name, parms), out var hit)) return hit;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo r = null;
            for (var c = t; r == null && c != null && c != typeof(MonoBehaviour) && c != typeof(Behaviour); c = c.BaseType)
                foreach (var m in c.GetMethods(F))
                    if (m.Name == name && m.GetParameters().Length == parms && !m.IsGenericMethodDefinition) { r = m; break; }
            found[(t, name, parms)] = r;
            return r;
        }

        static readonly Dictionary<(Type, string, int), MethodInfo> found = new Dictionary<(Type, string, int), MethodInfo>();
        static readonly Dictionary<MethodInfo, bool> noArgVoid = new Dictionary<MethodInfo, bool>();

        /// <summary>Whether a message takes no arguments and returns nothing (callable as an Action).</summary>
        public static bool NoArgVoid(MethodInfo m)
        {
            if (!noArgVoid.TryGetValue(m, out bool r))
                noArgVoid[m] = r = m.ReturnType == typeof(void) && m.GetParameters().Length == 0;
            return r;
        }
        static readonly Dictionary<MethodInfo, int> parmCounts = new Dictionary<MethodInfo, int>();
        public static int Params(MethodInfo m)
        {
            if (!parmCounts.TryGetValue(m, out int n)) parmCounts[m] = n = m.GetParameters().Length;
            return n;
        }
    }

    public sealed class InvokeState
    {
        public string Method;
        public float Due, Repeat;   // Repeat < 0: once
        public bool Cancelled;
    }

    public static class Behaviours
    {
        static long order;

        public static NBehaviour Attach(MonoBehaviour mb, NGameObject go)
        {
            var rec = Objects.Register(mb, new NBehaviour(), go.Name);
            rec.GameObject = go;
            rec.Messages = MessageSet.For(mb.GetType());
            rec.Order = ++order;
            rec.ExecOrder = ScriptOrder.Of(mb.GetType());
            go.Components.Add(rec);
            PlayerLoop.Track(rec);
            if (rec.Messages.OnWillRenderObject != null) RenderList.WillRender.Add(rec);
            return rec;
        }

        public static bool ActiveAndEnabled(NBehaviour b) => b.Enabled && !b.Destroyed && b.GameObject.ActiveInHierarchy;

        /// <summary>Calls a message; an exception is logged and the frame goes on, as in Unity.</summary>
        public static object Send(NBehaviour b, MethodInfo m, object arg = null)
        {
            if (m == null) return null;
            long t0 = PlayerLoop.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                var bound = Bound(b, m);
                if (bound is Action<object, object> thunk) { thunk(b.Managed, arg); return null; }
                if (bound is Action call && arg == null) { call(); return null; }
                if (bound is Action<Collider2D> onCollider && (arg == null || arg is Collider2D)) { onCollider((Collider2D)arg); return null; }
                if (bound is Action<Collision2D> onCollision && (arg == null || arg is Collision2D)) { onCollision((Collision2D)arg); return null; }
                if (arg == null && MessageSet.NoArgVoid(m))
                {
                    var thunk2 = Thunk(m);
                    if (thunk2 != null) { thunk2(b.Managed, null); return null; }
                    var calls = b.Calls ??= new Dictionary<MethodInfo, Action>();
                    if (!calls.TryGetValue(m, out var call2))
                    {
                        try { call2 = (Action)m.CreateDelegate(typeof(Action), b.Managed); } catch (Exception) { call2 = null; }
                        calls[m] = call2;
                    }
                    if (call2 != null) { call2(); return null; }
                }
                return m.Invoke(b.Managed, arg == null && MessageSet.Params(m) == 0 ? null : new[] { arg });
            }
            catch (TargetInvocationException e) { Exceptions.Report(e.InnerException, b, m.Name); }
            catch (Exception e) { Exceptions.Report(e, b, m.Name); }
            finally { if (t0 != 0) b.ProfTicks += PlayerLoop.SlowCall(t0, b.Managed, m); }
            return null;
        }

        static readonly Dictionary<MethodInfo, Action<object, object>> thunks = new Dictionary<MethodInfo, Action<object, object>>();
        static readonly bool noThunks = Environment.GetEnvironmentVariable("CH360_REFLECT_CALLS") == "1";
        /// <summary>Delegates made for single objects (no thunk), and the time making them and the thunks' (the scene timing lines).</summary>
        public static long BoundObjects, BoundThunks, BindTicks;

        /// <summary>
        /// The patcher's static thunk for a message method (__ch360_m_name_kind, shared by every object
        /// of the class), or null: a void method of a non-generic MonoBehaviour taking nothing, a
        /// Collider2D or a Collision2D. CH360_REFLECT_CALLS=1 makes a delegate per object as before.
        /// </summary>
        public static Action<object, object> Thunk(MethodInfo m)
        {
            if (thunks.TryGetValue(m, out var d)) return d;
            var dt = m.DeclaringType;
            if (!noThunks && dt != null && !dt.IsGenericType && m.ReturnType == typeof(void))
            {
                var ps = m.GetParameters();
                string kind = ps.Length == 0 ? "0" : ps.Length != 1 ? null
                    : ps[0].ParameterType == typeof(Collider2D) ? "C" : ps[0].ParameterType == typeof(Collision2D) ? "K" : null;
                var t = kind == null ? null : dt.GetMethod("__ch360_m_" + m.Name + "_" + kind, BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (t != null) { d = (Action<object, object>)t.CreateDelegate(typeof(Action<object, object>)); BoundThunks++; }
            }
            thunks[m] = d;
            return d;
        }

        /// <summary>The delegate bound for this message on this behaviour (made the first time), or null.</summary>
        static Delegate Bound(NBehaviour b, MethodInfo m)
        {
            var keys = b.SentKeys;
            int n = b.SentCount;
            for (int i = 0; i < n; i++) if ((object)keys[i] == m) return b.SentCalls[i];
            if (n == 16) return null;   // sent by name with many methods: the dictionary path
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Delegate d = Thunk(m);
            if (d == null)
            {
                BoundObjects++;
                try
                {
                    if (m.ReturnType == typeof(void))
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 0) d = m.CreateDelegate(typeof(Action), b.Managed);
                        else if (ps.Length == 1 && ps[0].ParameterType == typeof(Collider2D)) d = m.CreateDelegate(typeof(Action<Collider2D>), b.Managed);
                        else if (ps.Length == 1 && ps[0].ParameterType == typeof(Collision2D)) d = m.CreateDelegate(typeof(Action<Collision2D>), b.Managed);
                    }
                }
                catch (Exception) { d = null; }
            }
            BindTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (keys == null) { b.SentKeys = keys = new MethodInfo[4]; b.SentCalls = new Delegate[4]; }
            else if (n == keys.Length) { Array.Resize(ref b.SentKeys, n * 2); Array.Resize(ref b.SentCalls, n * 2); keys = b.SentKeys; }
            keys[n] = m;
            b.SentCalls[n] = d;
            b.SentCount = n + 1;
            return d;
        }

        /// <summary>Awake once, then OnEnable, for a behaviour whose GameObject is active.</summary>
        public static void Activate(NBehaviour b)
        {
            if (b.Destroyed || !b.GameObject.ActiveInHierarchy) return;
            if (!b.Awoken)
            {
                b.Awoken = true;
                Send(b, b.Messages.Awake);
                if (b.Destroyed || !b.GameObject.ActiveInHierarchy) return;
            }
            if (b.Enabled)
            {
                Send(b, b.Messages.OnEnable);
                QueueStart(b);
            }
        }

        public static void QueueStart(NBehaviour b)
        {
            if (b.Started || b.StartQueued) return;
            b.StartQueued = true;
            PlayerLoop.QueueStart(b);
        }

        public static void SetEnabled(NBehaviour b, bool value)
        {
            if (b.Enabled == value) return;
            b.Enabled = value;
            if (!b.GameObject.ActiveInHierarchy) return;
            if (value)
            {
                if (!b.Awoken) { Activate(b); return; }
                Send(b, b.Messages.OnEnable);
                QueueStart(b);
            }
            else Send(b, b.Messages.OnDisable);
        }

        public static void Invoke(NBehaviour b, string method, float time, float repeat)
        {
            b.Invokes.Add(new InvokeState { Method = method, Due = Clock.Time + Math.Max(0f, time), Repeat = repeat });
            PlayerLoop.Track(b);
            PlayerLoop.NoteInvoke(b);
        }

        public static MethodInfo FindByName(NBehaviour b, string name, int parms)
        {
            return MessageSet.Find(b.Managed.GetType(), name, parms);
        }
    }

    public static class Exceptions
    {
        public static int Count;
        public static void Report(Exception e, NObject where, string what)
        {
            Count++;
            Log.Line("EXCEPTION in " + (where?.Managed?.GetType().FullName ?? "?") + "." + what + " ('" + where?.Name + "'): " + e.GetType().FullName + ": " + e.Message);
            Log.Line(Frames(e));
        }

        /// <summary>
        /// Stack with IL offsets (the game DLLs have no PDBs): "at Type.Method+IL_xxxx".
        /// Match an offset with `Cuphead360.Patcher dump --in DLL --type T --method M`.
        /// </summary>
        public static string Frames(Exception e)
        {
            var sb = new System.Text.StringBuilder();
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x != e) sb.Append("   --- inner " + x.GetType().FullName + ": " + x.Message + "\n");
                foreach (var f in new System.Diagnostics.StackTrace(x, false).GetFrames())
                {
                    var m = f.GetMethod();
                    if (m == null || m.DeclaringType?.Namespace?.StartsWith("System.Reflection") == true) continue;
                    int il = f.GetILOffset();
                    sb.Append("   at " + m.DeclaringType?.FullName + "." + m.Name + (il >= 0 ? "+IL_" + il.ToString("x4") : "") + "\n");
                }
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}
