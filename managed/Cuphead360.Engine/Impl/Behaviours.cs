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
        public readonly List<InvokeState> Invokes = new List<InvokeState>();
        public long Order;
        /// <summary>Messages without arguments, bound to this behaviour (reflection's Invoke is slow and allocates).</summary>
        public Dictionary<MethodInfo, Action> Calls;
    }

    public sealed class MessageSet
    {
        public MethodInfo Awake, Start, Update, FixedUpdate, LateUpdate, OnEnable, OnDisable, OnDestroy;
        public MethodInfo OnTriggerEnter2D, OnTriggerStay2D, OnTriggerExit2D, OnCollisionEnter2D, OnCollisionStay2D, OnCollisionExit2D;
        public MethodInfo OnApplicationQuit, OnLevelWasLoaded, OnBecameVisible, OnBecameInvisible;
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
            go.Components.Add(rec);
            PlayerLoop.Track(rec);
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
                if (arg == null && MessageSet.NoArgVoid(m))
                {
                    var calls = b.Calls ??= new Dictionary<MethodInfo, Action>();
                    if (!calls.TryGetValue(m, out var call))
                    {
                        try { call = (Action)m.CreateDelegate(typeof(Action), b.Managed); } catch (Exception) { call = null; }
                        calls[m] = call;
                    }
                    if (call != null) { call(); return null; }
                }
                return m.Invoke(b.Managed, arg == null && MessageSet.Params(m) == 0 ? null : new[] { arg });
            }
            catch (TargetInvocationException e) { Exceptions.Report(e.InnerException, b, m.Name); }
            catch (Exception e) { Exceptions.Report(e, b, m.Name); }
            finally { if (t0 != 0) PlayerLoop.SlowCall(t0, b.Managed?.GetType().Name, m.Name); }
            return null;
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
