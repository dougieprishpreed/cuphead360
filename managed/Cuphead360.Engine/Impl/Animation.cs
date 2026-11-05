using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    // Mecanim for the player: controller state machines (states, transitions,
    // parameters), clip sampling (streamed, dense and constant curves) and
    // bindings (sprites, transforms, active/enabled, script floats), plus
    // animation events. Read from the converted runtime constants
    // (AnimatorController.m_Controller, AnimationClip.m_MuscleClip).

    /// <summary>A sampled AnimationClip: curves, bindings and events.</summary>
    public sealed class ClipData
    {
        public UObject Clip;
        public string Name = "";
        public float Start, Stop, SampleRate = 60f;
        public bool Loop;
        public float Length => Math.Max(Stop - Start, 0f);
        public Binding[] Bindings = new Binding[0];
        public UObject[] PPtrs = new UObject[0];
        public Event[] Events = new Event[0];
        // Streamed: per curve, key times and cubic coefficients.
        float[][] keyTimes = new float[0][];
        float[][] keyCoeffs = new float[0][];
        int streamedCount, denseCount, constCount;
        float denseBegin, denseRate; int denseFrames; float[] dense = new float[0];
        float[] constants = new float[0];
        public int CurveCount => streamedCount + denseCount + constCount;

        public struct Binding
        {
            public uint Path, Attribute;
            public int TypeId, CustomType, FirstCurve, CurveCount;
            public bool IsPPtr;
            public Type Script;
        }

        public struct Event
        {
            public float Time;
            public string Function, Str;
            public float Float;
            public int Int, Options;
            public UObject Obj;
        }

        static readonly Dictionary<ChobObject, ClipData> cache = new Dictionary<ChobObject, ClipData>();

        /// <summary>AssetUnload: only the clips live animators use stay cached (their frames' sprites with them).</summary>
        public static void Retain(HashSet<ClipData> keep)
        {
            var drop = new List<ChobObject>();
            foreach (var kv in cache) if (!keep.Contains(kv.Value)) drop.Add(kv.Key);
            foreach (var k in drop) cache.Remove(k);
        }

        public static ClipData Of(UObject clip)
        {
            var src = (Objects.Of(clip) as NAsset)?.Source;
            if (src == null) return null;
            if (cache.TryGetValue(src, out var d)) { if (d.Clip == null) d.Clip = clip; return d; }
            d = new ClipData { Clip = clip };
            d.Read(src);
            cache[src] = d;
            return d;
        }

        void Read(ChobObject co)
        {
            var t = co.Tree;
            Name = Tree.Str(t, "m_Name");
            SampleRate = Tree.Float(t, "m_SampleRate", 60f);
            var mc = Tree.Get(t, "m_MuscleClip");
            Start = Tree.Float(mc, "m_StartTime");
            Stop = Tree.Float(mc, "m_StopTime");
            Loop = Tree.Bool(mc, "m_LoopTime");
            var clip = Tree.Get(Tree.Get(mc, "m_Clip"), "data");

            // Streamed curves: frames of (time, keys (curve, 4 coefficients)).
            var sc = Tree.Get(clip, "m_StreamedClip");
            streamedCount = (int)Tree.Int(sc, "curveCount");
            var words = Tree.Longs(sc, "data");
            var times = new List<float>[streamedCount];
            var coeffs = new List<float>[streamedCount];
            for (int i = 0; i < streamedCount; i++) { times[i] = new List<float>(); coeffs[i] = new List<float>(); }
            int p = 0;
            float F(int at) => BitConverter.Int32BitsToSingle((int)(uint)words[at]);
            while (p + 2 <= words.Length)
            {
                float time = F(p);
                int n = (int)words[p + 1];
                p += 2;
                for (int k = 0; k < n && p + 5 <= words.Length; k++, p += 5)
                {
                    int ci = (int)words[p];
                    if (ci < 0 || ci >= streamedCount) continue;
                    times[ci].Add(time);
                    coeffs[ci].Add(F(p + 1)); coeffs[ci].Add(F(p + 2)); coeffs[ci].Add(F(p + 3)); coeffs[ci].Add(F(p + 4));
                }
            }
            keyTimes = new float[streamedCount][];
            keyCoeffs = new float[streamedCount][];
            for (int i = 0; i < streamedCount; i++) { keyTimes[i] = times[i].ToArray(); keyCoeffs[i] = coeffs[i].ToArray(); }

            var dc = Tree.Get(clip, "m_DenseClip");
            denseCount = (int)Tree.Int(dc, "m_CurveCount");
            denseFrames = (int)Tree.Int(dc, "m_FrameCount");
            denseRate = Tree.Float(dc, "m_SampleRate", SampleRate);
            denseBegin = Tree.Float(dc, "m_BeginTime");
            dense = Tree.Floats(dc, "m_SampleArray");
            constants = Tree.Floats(Tree.Get(clip, "m_ConstantClip"), "data");
            constCount = constants.Length;

            var cb = Tree.Get(t, "m_ClipBindingConstant");
            var pp = Tree.List(cb, "pptrCurveMapping");
            PPtrs = new UObject[pp.Count];
            for (int i = 0; i < pp.Count; i++) if (pp[i] is PPtr ptr) PPtrs[i] = AssetDb.Resolve(co.File, ptr);
            var gb = Tree.List(cb, "genericBindings");
            var list = new List<Binding>();
            int curve = 0;
            foreach (var b in gb)
            {
                var bd = new Binding
                {
                    Path = (uint)Tree.Int(b, "path"), Attribute = (uint)Tree.Int(b, "attribute"),
                    TypeId = (int)Tree.Int(b, "typeID"), CustomType = (int)Tree.Int(b, "customType"),
                    IsPPtr = Tree.Int(b, "isPPtrCurve") != 0, FirstCurve = curve,
                };
                bd.CurveCount = bd.TypeId == 4 ? (bd.Attribute == 2 ? 4 : bd.Attribute >= 1 && bd.Attribute <= 4 ? 3 : 1) : 1;
                var sp = Tree.Ptr(b, "script");
                if (!sp.IsNull && AssetDb.Lookup(co.File, sp) is ChobObject ms)
                {
                    string ns = Tree.Str(ms.Tree, "m_Namespace"), cls = Tree.Str(ms.Tree, "m_ClassName");
                    bd.Script = Find.TypeByName(ns.Length > 0 ? ns + "." + cls : cls);
                }
                curve += bd.CurveCount;
                list.Add(bd);
            }
            Bindings = list.ToArray();

            var evs = new List<Event>();
            foreach (var e in Tree.List(t, "m_Events"))
                evs.Add(new Event
                {
                    Time = Tree.Float(e, "time"), Function = Tree.Str(e, "functionName"), Str = Tree.Str(e, "data"),
                    Float = Tree.Float(e, "floatParameter"), Int = (int)Tree.Int(e, "intParameter"),
                    Options = (int)Tree.Int(e, "messageOptions"), Obj = AssetDb.Resolve(co.File, Tree.Ptr(e, "objectReferenceParameter")),
                });
            evs.Sort((a, b) => a.Time.CompareTo(b.Time));
            Events = evs.ToArray();
        }


        /// <summary>Value of curve i at clip time t (seconds from the clip's start time).</summary>
        public float Sample(int i, float t)
        {
            float time = t + Start;
            if (i < streamedCount)
            {
                var kt = keyTimes[i];
                if (kt.Length == 0) return 0f;
                int lo = 0, hi = kt.Length - 1;
                if (time < kt[0]) lo = 0;
                else
                {
                    while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (kt[mid] <= time) lo = mid; else hi = mid - 1; }
                }
                var c = keyCoeffs[i];
                float dt = float.IsInfinity(kt[lo]) || kt[lo] < -1e30f ? 0f : time - kt[lo];
                return ((c[lo * 4] * dt + c[lo * 4 + 1]) * dt + c[lo * 4 + 2]) * dt + c[lo * 4 + 3];
            }
            i -= streamedCount;
            if (i < denseCount)
            {
                if (denseFrames == 0) return 0f;
                float f = (time - denseBegin) * denseRate;
                int f0 = Math.Max(0, Math.Min(denseFrames - 1, (int)Math.Floor(f)));
                int f1 = Math.Min(denseFrames - 1, f0 + 1);
                float a = Math.Max(0f, Math.Min(1f, f - f0));
                float v0 = dense[f0 * denseCount + i], v1 = dense[f1 * denseCount + i];
                return v0 + (v1 - v0) * a;
            }
            i -= denseCount;
            return i < constCount ? constants[i] : 0f;
        }
    }

    /// <summary>The runtime constant of an AnimatorController (or an override of one).</summary>
    public sealed class ControllerData
    {
        public sealed class Layer { public string Name = ""; public int Machine; public float DefaultWeight; public uint Binding; }
        public sealed class Condition { public int Mode; public uint Param; public float Threshold; }
        public sealed class Transition
        {
            public int Destination;
            public uint FullPath, Name, UserName;
            public float Duration, Offset, ExitTime;
            public bool HasExitTime, FixedDuration, CanToSelf;
            public Condition[] Conditions = new Condition[0];
        }
        public sealed class State
        {
            public uint NameId, PathId, FullPathId, TagId, SpeedParam;
            public float Speed = 1f, CycleOffset;
            public bool Loop, WriteDefaults = true;
            public int Clip = -1;
            public Transition[] Transitions = new Transition[0];
            /// <summary>A 1D blend tree: its parameter, the children's thresholds and clips (null otherwise).</summary>
            public uint BlendParam;
            public float[] BlendThresholds;
            public int[] BlendClips;
        }
        public sealed class Selector { public Transition[] Transitions = new Transition[0]; public bool IsEntry; public uint FullPath; }
        public sealed class Machine
        {
            public State[] States = new State[0];
            public Transition[] AnyState = new Transition[0];
            public Selector[] Selectors = new Selector[0];
            public int DefaultState;
        }
        public sealed class Param { public uint Id; public string Name = ""; public AnimatorControllerParameterType Type; public int Index; public float DefaultFloat; public int DefaultInt; public bool DefaultBool; }

        public Layer[] Layers = new Layer[0];
        public Machine[] Machines = new Machine[0];
        public Param[] Params = new Param[0];
        public ClipData[] Clips = new ClipData[0];
        public UObject[] ClipObjects = new UObject[0];
        public readonly Dictionary<uint, string> Names = new Dictionary<uint, string>();

        static readonly Dictionary<ChobObject, ControllerData> cache = new Dictionary<ChobObject, ControllerData>();

        /// <summary>AssetUnload: only the controllers live animators use stay cached (their clips with them).</summary>
        public static void Retain(HashSet<ControllerData> keep)
        {
            var drop = new List<ChobObject>();
            foreach (var kv in cache) if (!keep.Contains(kv.Value)) drop.Add(kv.Key);
            foreach (var k in drop) cache.Remove(k);
        }

        public static ControllerData Of(RuntimeAnimatorController c)
        {
            if (c == null) return null;
            if (c is AnimatorOverrideController oc) return Overrides.Of(oc);
            var src = (Objects.Of(c) as NAsset)?.Source;
            if (src == null || src.Type != "AnimatorController") return null;
            if (cache.TryGetValue(src, out var d)) return d;
            d = new ControllerData();
            d.Read(src);
            cache[src] = d;
            return d;
        }

        static Condition[] Conditions(object tr)
        {
            var list = new List<Condition>();
            foreach (var c in Tree.List(tr, "m_ConditionConstantArray"))
            {
                var cd = Tree.Get(c, "data");
                list.Add(new Condition { Mode = (int)Tree.Int(cd, "m_ConditionMode"), Param = (uint)Tree.Int(cd, "m_EventID"), Threshold = Tree.Float(cd, "m_EventThreshold") });
            }
            return list.ToArray();
        }

        static Transition ReadTransition(object x)
        {
            var d = Tree.Get(x, "data");
            return new Transition
            {
                Destination = (int)Tree.Int(d, "m_DestinationState"), FullPath = (uint)Tree.Int(d, "m_FullPathID"),
                Name = (uint)Tree.Int(d, "m_ID"), UserName = (uint)Tree.Int(d, "m_UserID"),
                Duration = Tree.Float(d, "m_TransitionDuration"), Offset = Tree.Float(d, "m_TransitionOffset"),
                ExitTime = Tree.Float(d, "m_ExitTime"), HasExitTime = Tree.Bool(d, "m_HasExitTime"),
                FixedDuration = Tree.Bool(d, "m_HasFixedDuration"), CanToSelf = Tree.Bool(d, "m_CanTransitionToSelf", true),
                Conditions = Conditions(d),
            };
        }

        void Read(ChobObject co)
        {
            var t = co.Tree;
            foreach (var e in Tree.List(t, "m_TOS"))
                if (e is List<object> pair && pair.Count == 2 && pair[0] is long h) Names[(uint)h] = pair[1] as string ?? "";
            var clips = Tree.List(t, "m_AnimationClips");
            ClipObjects = new UObject[clips.Count];
            Clips = new ClipData[clips.Count];
            for (int i = 0; i < clips.Count; i++)
                if (clips[i] is PPtr p) { ClipObjects[i] = AssetDb.Resolve(co.File, p); Clips[i] = ClipObjects[i] != null ? ClipData.Of(ClipObjects[i]) : null; }
            var ctl = Tree.Get(t, "m_Controller");
            var layers = new List<Layer>();
            foreach (var l in Tree.List(ctl, "m_LayerArray"))
            {
                var d = Tree.Get(l, "data");
                var layer = new Layer { Machine = (int)Tree.Int(d, "m_StateMachineIndex"), DefaultWeight = Tree.Float(d, "m_DefaultWeight"), Binding = (uint)Tree.Int(d, "m_Binding") };
                layer.Name = Names.TryGetValue(layer.Binding, out var n) ? n : "";
                layers.Add(layer);
            }
            Layers = layers.ToArray();
            if (Layers.Length > 0) Layers[0].DefaultWeight = 1f;   // the base layer always has full weight
            var machines = new List<Machine>();
            foreach (var m in Tree.List(ctl, "m_StateMachineArray"))
            {
                var d = Tree.Get(m, "data");
                var mach = new Machine { DefaultState = (int)Tree.Int(d, "m_DefaultState") };
                var states = new List<State>();
                foreach (var s in Tree.List(d, "m_StateConstantArray"))
                {
                    var sd = Tree.Get(s, "data");
                    var st = new State
                    {
                        NameId = (uint)Tree.Int(sd, "m_NameID"), PathId = (uint)Tree.Int(sd, "m_PathID"), FullPathId = (uint)Tree.Int(sd, "m_FullPathID"),
                        TagId = (uint)Tree.Int(sd, "m_TagID"), SpeedParam = (uint)Tree.Int(sd, "m_SpeedParamID"),
                        Speed = Tree.Float(sd, "m_Speed", 1f), CycleOffset = Tree.Float(sd, "m_CycleOffset"),
                        Loop = Tree.Bool(sd, "m_Loop"), WriteDefaults = Tree.Bool(sd, "m_WriteDefaultValues", true),
                    };
                    var trs = new List<Transition>();
                    foreach (var x in Tree.List(sd, "m_TransitionConstantArray")) trs.Add(ReadTransition(x));
                    st.Transitions = trs.ToArray();
                    // Motion: blend tree constant; plain states have one node with a clip id.
                    var bts = Tree.List(sd, "m_BlendTreeConstantArray");
                    if (bts.Count > 0)
                    {
                        var nodes = Tree.List(Tree.Get(bts[0], "data"), "m_NodeArray");
                        if (nodes.Count > 0)
                        {
                            var root = Tree.Get(nodes[0], "data");
                            long clipId = Tree.Int(root, "m_ClipID", -1);
                            if (clipId >= 0 && clipId < 0xFFFFFFFFL) st.Clip = (int)clipId;
                            var kids = Tree.List(root, "m_ChildIndices");
                            var thresholds = Tree.List(Tree.Get(Tree.Get(root, "m_Blend1dData"), "data"), "m_ChildThresholdArray");
                            if (nodes.Count > 1 && Tree.Int(root, "m_BlendType") == 0 && kids.Count > 0 && thresholds.Count == kids.Count)
                            {
                                // A 1D blend tree of clips (Werner Werman's saw blade, six by a float): the
                                // child weighing most plays (what a sprite animation shows of a blend).
                                st.BlendParam = (uint)Tree.Int(root, "m_BlendEventID");
                                st.BlendThresholds = new float[kids.Count];
                                st.BlendClips = new int[kids.Count];
                                for (int k = 0; k < kids.Count; k++)
                                {
                                    int node = (int)Convert.ToInt64(kids[k]);
                                    long cid = node >= 0 && node < nodes.Count ? Tree.Int(Tree.Get(nodes[node], "data"), "m_ClipID", -1) : -1;
                                    st.BlendClips[k] = cid >= 0 && cid < 0xFFFFFFFFL ? (int)cid : -1;
                                    st.BlendThresholds[k] = Convert.ToSingle(thresholds[k]);
                                }
                                if (st.Clip < 0) st.Clip = st.BlendClips[0];
                            }
                            else if (nodes.Count > 1) AssetDb.Problem("blend tree with " + nodes.Count + " nodes in " + co.File.Name + " " + co.PathId + " (first clip only)");
                        }
                    }
                    states.Add(st);
                }
                mach.States = states.ToArray();
                var any = new List<Transition>();
                foreach (var x in Tree.List(d, "m_AnyStateTransitionConstantArray")) any.Add(ReadTransition(x));
                mach.AnyState = any.ToArray();
                var sels = new List<Selector>();
                foreach (var x in Tree.List(d, "m_SelectorStateConstantArray"))
                {
                    var sd = Tree.Get(x, "data");
                    var sel = new Selector { IsEntry = Tree.Bool(sd, "m_IsEntry"), FullPath = (uint)Tree.Int(sd, "m_FullPathID") };
                    var trs = new List<Transition>();
                    foreach (var y in Tree.List(sd, "m_TransitionConstantArray"))
                    {
                        var yd = Tree.Get(y, "data");
                        trs.Add(new Transition { Destination = (int)Tree.Int(yd, "m_Destination"), Conditions = Conditions(yd) });
                    }
                    sel.Transitions = trs.ToArray();
                    sels.Add(sel);
                }
                mach.Selectors = sels.ToArray();
                machines.Add(mach);
            }
            Machines = machines.ToArray();

            var values = Tree.List(Tree.Get(Tree.Get(ctl, "m_Values"), "data"), "m_ValueArray");
            var defs = Tree.Get(Tree.Get(ctl, "m_DefaultValues"), "data");
            var df = Tree.List(defs, "m_FloatValues"); var di = Tree.List(defs, "m_IntValues"); var db = Tree.List(defs, "m_BoolValues");
            var ps = new List<Param>();
            foreach (var v in values)
            {
                var p = new Param { Id = (uint)Tree.Int(v, "m_ID"), Type = (AnimatorControllerParameterType)Tree.Int(v, "m_Type"), Index = (int)Tree.Int(v, "m_Index") };
                p.Name = Names.TryGetValue(p.Id, out var n) ? n : "";
                if (p.Type == AnimatorControllerParameterType.Float && p.Index < df.Count) p.DefaultFloat = df[p.Index] switch { float f => f, double dd => (float)dd, long l => l, _ => 0f };
                if (p.Type == AnimatorControllerParameterType.Int && p.Index < di.Count) p.DefaultInt = (int)(di[p.Index] is long l2 ? l2 : 0);
                if ((p.Type == AnimatorControllerParameterType.Bool || p.Type == AnimatorControllerParameterType.Trigger) && p.Index < db.Count) p.DefaultBool = db[p.Index] is bool b && b;
                ps.Add(p);
            }
            Params = ps.ToArray();
        }

        public ControllerData WithClips(UObject[] objs)
        {
            var d = (ControllerData)MemberwiseClone();
            d.ClipObjects = objs;
            d.Clips = new ClipData[objs.Length];
            for (int i = 0; i < objs.Length; i++) d.Clips[i] = objs[i] != null ? ClipData.Of(objs[i]) : null;
            return d;
        }

        public string NameOf(uint hash) => Names.TryGetValue(hash, out var n) ? n : "";
    }

    /// <summary>AnimatorOverrideController: the base controller's constant with clips replaced.</summary>
    public static class Overrides
    {
        public sealed class State { public RuntimeAnimatorController Base; public readonly Dictionary<UObject, UObject> Map = new Dictionary<UObject, UObject>(); public ControllerData Data; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AnimatorOverrideController, State> states = new System.Runtime.CompilerServices.ConditionalWeakTable<AnimatorOverrideController, State>();

        public static State StateOf(AnimatorOverrideController oc)
        {
            if (states.TryGetValue(oc, out var s)) return s;
            s = new State();
            var src = (Objects.Of(oc) as NAsset)?.Source;
            if (src != null)
            {
                s.Base = AssetDb.Resolve(src.File, Tree.Ptr(src.Tree, "m_Controller")) as RuntimeAnimatorController;
                foreach (var e in Tree.List(src.Tree, "m_Clips"))
                {
                    var o = AssetDb.Resolve(src.File, Tree.Ptr(e, "m_OriginalClip"));
                    var r = AssetDb.Resolve(src.File, Tree.Ptr(e, "m_OverrideClip"));
                    if (o != null && r != null) s.Map[o] = r;
                }
            }
            states.Add(oc, s);
            return s;
        }

        public static ControllerData Of(AnimatorOverrideController oc)
        {
            var s = StateOf(oc);
            if (s.Data != null) return s.Data;
            var b = ControllerData.Of(s.Base);
            if (b == null) return null;
            var objs = new UObject[b.ClipObjects.Length];
            for (int i = 0; i < objs.Length; i++) objs[i] = b.ClipObjects[i] != null && s.Map.TryGetValue(b.ClipObjects[i], out var r) ? r : b.ClipObjects[i];
            s.Data = b.WithClips(objs);
            return s.Data;
        }

        public static void Changed(AnimatorOverrideController oc)
        {
            StateOf(oc).Data = null;
            foreach (var o in Objects.Live())
                if (o is NAnimator a && a.Controller == oc) a.Rebind(keepState: true);
        }
    }

    public sealed class NAnimator : NComponent
    {
        public RuntimeAnimatorController Controller;
        public ControllerData Data;
        public AnimatorUpdateMode UpdateMode;
        public AnimatorCullingMode CullingMode;
        public bool ApplyRootMotion;
        public float Speed = 1f;
        public bool Initialized;
        public LayerState[] Layers = new LayerState[0];
        public float[] Floats = new float[0];
        public int[] Ints = new int[0];
        public bool[] Bools = new bool[0];
        // Bound targets and their default values (write defaults).
        readonly Dictionary<(uint path, uint attr, int type, int custom, Type script), Target> targets = new Dictionary<(uint, uint, int, int, Type), Target>();
        readonly List<Target> controlled = new List<Target>();

        public sealed class StatePlay
        {
            public int State = -1;
            public float Time;          // seconds into the state (speed applied)
            public float Prev;          // last frame's time, for events and exit times
            public bool Fresh = true;   // entered this frame: events at its start fire
        }

        public sealed class LayerState
        {
            public float Weight;
            public StatePlay Current = new StatePlay();
            public StatePlay Next;      // during a transition
            public readonly List<StatePlay> Retired = new List<StatePlay>();   // left this update: their events still fire
            public ControllerData.Transition Transition;
            public float TransitionTime, TransitionLength;
            public (int state, float normalizedTime)? PendingPlay;
            public (int state, float duration, float offset, bool fixedDuration)? PendingCross;
            /// <summary>Entered at Rebind: the entry's conditions are looked at again on the first update.</summary>
            public bool EntryPending;
        }

        public override void Load(ChobObject co, SceneBuild b)
        {
            var t = co.Tree;
            Controller = b.Resolve(Tree.Ptr(t, "m_Controller")) as RuntimeAnimatorController;
            UpdateMode = (AnimatorUpdateMode)Tree.Int(t, "m_UpdateMode");
            CullingMode = (AnimatorCullingMode)Tree.Int(t, "m_CullingMode");
            ApplyRootMotion = Tree.Bool(t, "m_ApplyRootMotion");
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NAnimator)src;
            Controller = s.Controller; UpdateMode = s.UpdateMode; CullingMode = s.CullingMode; ApplyRootMotion = s.ApplyRootMotion; Speed = s.Speed;
        }

        public override void Added() => Animators.Register(this);
        public override void Activated(bool on)
        {
            Animators.Register(this);
            if (on) Rebind(keepState: false);
        }
        public override void Removed() { }

        /// <summary>Bind the controller: default states at time 0, default parameter values, captured defaults.</summary>
        public void Rebind(bool keepState)
        {
            if (TraceName != null && GameObject != null && GameObject.Name.Contains(TraceName)) Log.Line($"TRACE animator {GameObject.Name} Rebind(keep={keepState}) frame {Clock.FrameCount} {Environment.StackTrace}");
            RestoreDefaults();
            targets.Clear();
            controlled.Clear();
            Data = ControllerData.Of(Controller);
            Initialized = Data != null;
            if (Data == null) { Layers = new LayerState[0]; return; }
            // The parameters before the entries: an entry transition's conditions read them (a new
            // animator's empty arrays threw there, and its object's activation stopped: the plane's
            // Peashooter bullets).
            if (!keepState || Floats.Length != Data.Params.Length)
            {
                int n = Data.Params.Length;
                Floats = new float[n]; Ints = new int[n]; Bools = new bool[n];
                for (int i = 0; i < n; i++) { var p = Data.Params[i]; Floats[i] = p.DefaultFloat; Ints[i] = p.DefaultInt; Bools[i] = p.DefaultBool; }
            }
            var old = Layers;
            Layers = new LayerState[Data.Layers.Length];
            for (int i = 0; i < Layers.Length; i++)
            {
                var l = Layers[i] = new LayerState { Weight = Data.Layers[i].DefaultWeight };
                if (keepState && old != null && i < old.Length) { l.Current = old[i].Current; l.Weight = old[i].Weight; }
                else { l.Current = new StatePlay { State = Entry(Data.Machines[Data.Layers[i].Machine]) }; l.EntryPending = true; }
            }
            // Every property any clip of the controller animates is under the animator's control.
            foreach (var c in Data.Clips)
                if (c != null) foreach (var b in c.Bindings) Bind(b);
        }

        int Entry(ControllerData.Machine m)
        {
            foreach (var sel in m.Selectors)
                if (sel.IsEntry)
                {
                    int d = Resolve(m, sel);
                    if (d >= 0) return d;
                }
            return m.DefaultState;
        }

        int Resolve(ControllerData.Machine m, ControllerData.Selector sel)
        {
            for (int guard = 0; guard < 16; guard++)
            {
                ControllerData.Transition pick = null;
                foreach (var tr in sel.Transitions) if (ConditionsHold(tr.Conditions, null, 0, false)) { pick = tr; break; }
                if (pick == null) return -1;
                if (pick.Destination < 30000) return pick.Destination;
                int si = pick.Destination - 30000;
                if (si < 0 || si >= m.Selectors.Length) return -1;
                // An exit selector leads back to the machine's entry.
                sel = m.Selectors[si];
                if (!sel.IsEntry && sel.Transitions.Length == 0) return m.DefaultState;
            }
            return -1;
        }

        public int ParamIndex(uint id)
        {
            if (Data == null) return -1;
            for (int i = 0; i < Data.Params.Length; i++) if (Data.Params[i].Id == id) return i;
            return -1;
        }

        bool ConditionsHold(ControllerData.Condition[] conds, List<int> triggers, float normalizedTime, bool exitReached)
        {
            foreach (var c in conds)
            {
                int i = ParamIndex(c.Param);
                if (c.Mode == 5) { if (!exitReached) return false; continue; }   // legacy exit-time condition
                if (i < 0) return false;
                var type = Data.Params[i].Type;
                float f = type == AnimatorControllerParameterType.Float ? Floats[i] : type == AnimatorControllerParameterType.Int ? Ints[i] : (Bools[i] ? 1 : 0);
                bool ok;
                switch (c.Mode)
                {
                    case 1: ok = Bools[i]; if (ok && type == AnimatorControllerParameterType.Trigger) triggers?.Add(i); break;
                    case 2: ok = !Bools[i]; break;
                    case 3: ok = f > c.Threshold; break;
                    case 4: ok = f < c.Threshold; break;
                    case 6: ok = Ints[i] == (int)c.Threshold; break;
                    case 7: ok = Ints[i] != (int)c.Threshold; break;
                    default: ok = false; break;
                }
                if (!ok) return false;
            }
            return true;
        }

        public ControllerData.State StateAt(int layer, int index)
        {
            var m = Data.Machines[Data.Layers[layer].Machine];
            return index >= 0 && index < m.States.Length ? m.States[index] : null;
        }

        /// <summary>The clip a state plays now: its own, or for a 1D blend tree the child weighing most.</summary>
        public int ClipIndex(ControllerData.State s)
        {
            if (s == null) return -1;
            var th = s.BlendThresholds;
            if (th == null) return s.Clip;
            int pi = ParamIndex(s.BlendParam);
            float v = pi >= 0 ? Floats[pi] : 0f;
            if (v <= th[0]) return s.BlendClips[0];
            for (int k = 0; k + 1 < th.Length; k++)
                if (v < th[k + 1])
                {
                    // between two thresholds: the nearer one carries half the weight or more
                    float w = th[k + 1] > th[k] ? (th[k + 1] - v) / (th[k + 1] - th[k]) : 1f;
                    return w >= 0.5f ? s.BlendClips[k] : s.BlendClips[k + 1];
                }
            return s.BlendClips[th.Length - 1];
        }

        public float StateLength(int layer, int index)
        {
            var s = StateAt(layer, index);
            int ci = ClipIndex(s);
            var c = ci >= 0 && ci < Data.Clips.Length ? Data.Clips[ci] : null;
            return c == null ? 1f : Math.Max(c.Length, 1e-4f);
        }

        float StateSpeed(int layer, int index)
        {
            var s = StateAt(layer, index);
            if (s == null) return 1f;
            float sp = s.Speed;
            if (s.SpeedParam != 0) { int pi = ParamIndex(s.SpeedParam); if (pi >= 0) sp *= Floats[pi]; }
            return sp;
        }

        public float Normalized(int layer, StatePlay p) => p.Time / StateLength(layer, p.State);

        public int FindState(int layer, uint hash)
        {
            if (Data == null || layer < 0 || layer >= Data.Layers.Length) return -1;
            var m = Data.Machines[Data.Layers[layer].Machine];
            for (int i = 0; i < m.States.Length; i++) if (m.States[i].FullPathId == hash) return i;
            for (int i = 0; i < m.States.Length; i++) if (m.States[i].PathId == hash) return i;
            for (int i = 0; i < m.States.Length; i++) if (m.States[i].NameId == hash) return i;
            return -1;
        }

        // ---------------------------------------------------------------- update

        public static string TraceName = Environment.GetEnvironmentVariable("CH360_TRACE_ANIM");
        // CH360_TRACE_ANIM_FRAMES=a-b: every update of the traced animator's layers in those frames.
        static readonly (int from, int to) traceFrames = ParseRange(Environment.GetEnvironmentVariable("CH360_TRACE_ANIM_FRAMES"));
        static (int, int) ParseRange(string v)
        {
            var p = (v ?? "").Split('-');
            return p.Length == 2 && int.TryParse(p[0], out int a) && int.TryParse(p[1], out int b) ? (a, b) : (-1, -1);
        }
        /// <summary>A trace window a host opens at run time (the console's EX check): WindowName's animator until frame WindowUntil.</summary>
        public static string WindowName;
        public static int WindowUntil = -1;
        /// <summary>State name hashes whose Play on WindowName's animator opens the window (WindowFrames frames, WindowsLeft times).</summary>
        public static HashSet<int> WindowOnPlay;
        public static int WindowFrames = 30, WindowsLeft;
        internal bool TraceNow => GameObject != null && (
            (TraceName != null && GameObject.Name.Contains(TraceName) && Clock.FrameCount >= traceFrames.from && Clock.FrameCount <= traceFrames.to)
            || (WindowName != null && Clock.FrameCount <= WindowUntil && GameObject.Name == WindowName));

        int updateDepth;

        public void Update(float dt)
        {
            if (!Initialized) { Rebind(false); if (!Initialized) return; }
            // Animator.Update from an animation event re-enters here (Moonshine Mob's anteater updates its
            // hands at once after a Play): allowed, but not without end.
            if (updateDepth >= 4) return;
            updateDepth++;
            try { UpdateNow(dt); }
            finally { updateDepth--; }
        }

        readonly List<int> consumedTriggers = new List<int>();
        static readonly List<int> triggerScratch = new List<int>();

        void UpdateNow(float dt)
        {
            int mark = consumedTriggers.Count;
            for (int li = 0; li < Layers.Length; li++) UpdateLayer(li, dt * Speed);
            for (int i = mark; i < consumedTriggers.Count; i++) Bools[consumedTriggers[i]] = false;
            consumedTriggers.RemoveRange(mark, consumedTriggers.Count - mark);
            if (TraceNow)
            {
                // One line a frame: each weighted layer's state (name hash), time / length, and a transition's target.
                var sb = new System.Text.StringBuilder($"TRACE anim frame {Clock.FrameCount} {GameObject.Name} dt {dt:0.0000}:");
                for (int li = 0; li < Layers.Length; li++)
                {
                    var l = Layers[li];
                    if (li > 0 && l.Weight <= 0f) continue;
                    var cs = StateAt(li, l.Current.State);
                    var ns = l.Next != null ? StateAt(li, l.Next.State) : null;
                    sb.Append($" L{li} w{l.Weight:0.##} {cs?.NameId:X8} {l.Current.Time:0.000}/{StateLength(li, l.Current.State):0.000}");
                    if (ns != null) sb.Append($" ->{ns.NameId:X8} {l.TransitionTime:0.000}/{l.TransitionLength:0.000}");
                }
                Log.Line(sb.ToString());
            }
            Apply();
            for (int li = 0; li < Layers.Length; li++) FireEvents(li);
        }

        void Enter(LayerState l, StatePlay p, int state, float time)
        {
            p.State = state; p.Time = time; p.Prev = time; p.Fresh = true;
        }

        void UpdateLayer(int li, float dt)
        {
            var l = Layers[li];
            if (l.EntryPending)
            {
                // Unity takes the entry's transitions at the animator's first update, so parameters
                // set since it was enabled (Awake, OnEnable, Start: a bullet's variant) choose the state.
                l.EntryPending = false;
                if (l.PendingPlay == null && l.PendingCross == null && l.Next == null && l.Current.Time == 0f)
                {
                    int e = Entry(Data.Machines[Data.Layers[li].Machine]);
                    if (e != l.Current.State) l.Current.State = e;
                }
            }
            l.Current.Prev = l.Current.Time;
            if (l.Next != null) l.Next.Prev = l.Next.Time;
            if (l.PendingPlay is (int ps, float pn))
            {
                l.PendingPlay = null;
                l.Next = null; l.Transition = null;
                bool same = ps == l.Current.State;
                if (float.IsNegativeInfinity(pn)) { if (!same) Enter(l, l.Current, ps, 0f); }
                else Enter(l, l.Current, ps, pn * StateLength(li, ps));
                return;   // the requested state is evaluated at its start this frame
            }
            if (l.PendingCross is (int cs, float cd, float co, bool cf))
            {
                l.PendingCross = null;
                l.Next = new StatePlay();
                Enter(l, l.Next, cs, float.IsNegativeInfinity(co) ? 0f : (cf ? co : co * StateLength(li, cs)));
                l.Transition = new ControllerData.Transition { Duration = cd, FixedDuration = cf };
                l.TransitionTime = 0f;
                l.TransitionLength = cf ? cd : cd * StateLength(li, l.Current.State);
                if (l.TransitionLength <= 0f) { l.Current = l.Next; l.Next = null; l.Transition = null; }
                return;
            }
            l.Current.Fresh = false;
            if (l.Next != null) l.Next.Fresh = false;
            l.Current.Time += dt * StateSpeed(li, l.Current.State);
            if (l.Next != null)
            {
                l.Next.Time += dt * StateSpeed(li, l.Next.State);
                l.TransitionTime += Math.Abs(dt);
                if (l.TransitionTime >= l.TransitionLength) { l.Retired.Add(l.Current); l.Current = l.Next; l.Next = null; l.Transition = null; }
                return;   // no interruptions (Cuphead's transitions use none)
            }
            var m = Data.Machines[Data.Layers[li].Machine];
            var cur = StateAt(li, l.Current.State);
            if (cur == null) return;
            float n0 = l.Current.Prev / StateLength(li, l.Current.State), n1 = Normalized(li, l.Current);
            ControllerData.Transition fire = null;
            // One list for every layer of every animator (nothing here calls game code): a new one per layer
            // per update was an allocation for each animator every frame.
            var triggers = triggerScratch;
            foreach (var tr in m.AnyState)
            {
                triggers.Clear();
                if (tr.Destination == l.Current.State && !tr.CanToSelf) continue;
                if (Passes(tr, n0, n1, cur.Loop, triggers)) { fire = tr; break; }
            }
            if (fire == null)
                foreach (var tr in cur.Transitions)
                {
                    triggers.Clear();
                    if (Passes(tr, n0, n1, cur.Loop, triggers)) { fire = tr; break; }
                }
            if (fire == null) return;
            // A trigger is consumed by the transition it fires, once every layer has had this update: the
            // other layers listening for it take it too (Beppi's helium clown: "Continue" moves layers 1 and 3).
            consumedTriggers.AddRange(triggers);
            int dest = fire.Destination;
            if (dest >= 30000)
            {
                int si = dest - 30000;
                dest = si >= 0 && si < m.Selectors.Length ? Resolve(m, m.Selectors[si]) : -1;
                if (dest < 0) dest = Entry(m);
            }
            float over = 0f, exitAt = float.NaN;
            if (fire.HasExitTime)
            {
                // Time past the exit point carries into the destination.
                float exitN = fire.ExitTime <= 1f ? (float)Math.Floor(n1 - fire.ExitTime) + fire.ExitTime : fire.ExitTime;
                over = Math.Max(0f, (n1 - exitN) * StateLength(li, l.Current.State));
                exitAt = exitN * StateLength(li, l.Current.State);
            }
            float length = fire.FixedDuration ? fire.Duration : fire.Duration * StateLength(li, l.Current.State);
            float start = fire.Offset * StateLength(li, dest);
            if (length <= 0f)
            {
                // A state left at its exit point fires its events up to there, not the next loop's: the
                // onion's cry break (looping, StopCrying at 0) left at its end stopped the tears it had
                // just started, so his long crying spell rained none.
                if (!float.IsNaN(exitAt) && exitAt > l.Current.Prev && exitAt < l.Current.Time) l.Current.Time = exitAt;
                l.Retired.Add(l.Current);
                l.Current = new StatePlay();
                Enter(l, l.Current, dest, start + over * StateSpeed(li, dest));
                l.Current.Prev = start;
            }
            else
            {
                l.Next = new StatePlay();
                Enter(l, l.Next, dest, start + over * StateSpeed(li, dest));
                l.Next.Prev = start;
                l.Transition = fire;
                l.TransitionTime = over;
                l.TransitionLength = length;
            }
        }

        bool Passes(ControllerData.Transition tr, float n0, float n1, bool loop, List<int> triggers)
        {
            bool exitReached = false;
            if (tr.HasExitTime)
            {
                float e = tr.ExitTime;
                if (e <= 1f)
                {
                    // Checked every loop at the exit fraction.
                    double k = Math.Ceiling(n0 - e);
                    if (k < 0) k = 0;
                    exitReached = n0 < k + e && k + e <= n1;
                    // A state that doesn't loop stays past its exit point once there. Exit time 0
                    // is first reached at the clip's end (its next loop), not on entry: the
                    // Ooze's Jump_Squish_End -> Up / Punch_Start (exit 0) left at once, before the
                    // punch's trigger, and the slime hung in Up; the game's many exit-0 hand-offs
                    // (intros, turns, squishes) played as one frame.
                    if (!loop && n1 >= (e > 0f ? e : 1f)) exitReached = true;
                }
                else exitReached = n0 < e && e <= n1 || (!loop && n1 >= e);
                if (!exitReached) return false;
            }
            else if (tr.Conditions.Length == 0) return false;   // no exit time and no conditions never fires
            bool held = ConditionsHold(tr.Conditions, triggers, n1, exitReached);
            if (TraceName != null && GameObject != null && GameObject.Name.Contains(TraceName) && Clock.FrameCount % 60 == 0 && tr.Conditions.Length > 0)
                Log.Line($"TRACE animator {GameObject.Name} transition ->{tr.Destination} conds={tr.Conditions.Length} c0=({tr.Conditions[0].Mode},{tr.Conditions[0].Param}) param={ParamIndex(tr.Conditions[0].Param)} bool={(ParamIndex(tr.Conditions[0].Param) >= 0 ? Bools[ParamIndex(tr.Conditions[0].Param)] : false)} held={held}");
            return held;
        }

        // ---------------------------------------------------------------- pose

        sealed class Target
        {
            public ClipData.Binding B;
            public NTransform Transform;
            public NComponent Component;    // SpriteRenderer, Behaviour, ...
            public FieldInfo Field;
            public float[] Default = new float[4];
            public UObject DefaultObj;
            public float[] Value = new float[4];
            public UObject Obj;
            public bool Written;
            public string Unbound;
            // Generic property access (script fields, RectTransform, CanvasGroup, ...).
            public Action<float> Set;
            public Action<UObject> SetObj;
            public NBehaviour Notify;   // gets OnDidApplyAnimationProperties after a write
        }

        static readonly string[] subNames = { "x", "y", "z", "w" }, colorNames = { "r", "g", "b", "a" };

        /// <summary>Script field bindings: plain values, object references, and struct members ("m_Color.a").</summary>
        bool BindScriptField(Target tg, NComponent comp)
        {
            var obj = comp.Managed;
            foreach (var f in Serialization.Fields(obj.GetType()))
            {
                var ft = f.FieldType;
                if (Icalls.Crc32(f.Name) == tg.B.Attribute)
                {
                    if (tg.B.IsPPtr && typeof(UObject).IsAssignableFrom(ft))
                    {
                        tg.DefaultObj = f.GetValue(obj) as UObject;
                        tg.SetObj = o => { if (o == null || ft.IsInstanceOfType(o)) f.SetValue(obj, o); };
                        return true;
                    }
                    if (ft == typeof(float) || ft == typeof(int) || ft == typeof(bool))
                    {
                        tg.Field = f;
                        tg.Default[0] = Convert.ToSingle(f.GetValue(obj));
                        return true;
                    }
                }
                string[] parts = ft == typeof(Color) ? colorNames : ft == typeof(Vector2) ? new[] { "x", "y" } : ft == typeof(Vector3) ? new[] { "x", "y", "z" } : ft == typeof(Vector4) ? subNames : null;
                if (parts == null) continue;
                for (int k = 0; k < parts.Length; k++)
                {
                    if (Icalls.Crc32(f.Name + "." + parts[k]) != tg.B.Attribute) continue;
                    int idx = k;
                    tg.Default[0] = Component(f.GetValue(obj), idx);
                    tg.Set = v => f.SetValue(obj, WithComponent(f.GetValue(obj), idx, v));
                    return true;
                }
            }
            return false;
        }

        static float Component(object o, int i) => o switch
        {
            Color c => c[i], Vector2 v2 => v2[i], Vector3 v3 => v3[i], Vector4 v4 => v4[i], _ => 0f,
        };

        static object WithComponent(object o, int i, float v)
        {
            switch (o)
            {
                case Color c: c[i] = v; return c;
                case Vector2 v2: v2[i] = v; return v2;
                case Vector3 v3: v3[i] = v; return v3;
                case Vector4 v4: v4[i] = v; return v4;
                default: return o;
            }
        }

        static readonly Dictionary<uint, (string field, int k)> rectProps = BuildRectProps();
        static Dictionary<uint, (string, int)> BuildRectProps()
        {
            var d = new Dictionary<uint, (string, int)>();
            foreach (var f in new[] { "m_AnchoredPosition", "m_SizeDelta", "m_AnchorMin", "m_AnchorMax", "m_Pivot" })
                for (int k = 0; k < 2; k++) d[Icalls.Crc32(f + "." + subNames[k])] = (f, k);
            foreach (var f in new[] { "m_LocalPosition", "m_LocalScale" })
                for (int k = 0; k < 3; k++) d[Icalls.Crc32(f + "." + subNames[k])] = (f, k);
            return d;
        }

        bool BindRect(Target tg, NRectTransform rt)
        {
            if (!rectProps.TryGetValue(tg.B.Attribute, out var p)) return false;
            int k = p.k;
            Vector2 Get2() => p.field == "m_AnchoredPosition" ? rt.AnchoredPosition : p.field == "m_SizeDelta" ? rt.SizeDelta : p.field == "m_AnchorMin" ? rt.AnchorMin : p.field == "m_AnchorMax" ? rt.AnchorMax : rt.Pivot;
            if (p.field == "m_LocalPosition" || p.field == "m_LocalScale")
            {
                tg.Default[0] = p.field == "m_LocalPosition" ? rt.LocalPosition[k] : rt.LocalScale[k];
                tg.Set = v =>
                {
                    if (p.field == "m_LocalPosition") { var lp = rt.LocalPosition; lp[k] = v; rt.LocalPosition = lp; }
                    else { var ls = rt.LocalScale; ls[k] = v; rt.LocalScale = ls; }
                    rt.Invalidate();
                };
                return true;
            }
            tg.Default[0] = Get2()[k];
            tg.Set = v =>
            {
                var cur = Get2(); cur[k] = v;
                switch (p.field)
                {
                    case "m_AnchoredPosition": rt.AnchoredPosition = cur; break;
                    case "m_SizeDelta": rt.Change(() => rt.SizeDelta = cur); break;
                    case "m_AnchorMin": rt.Change(() => rt.AnchorMin = cur); break;
                    case "m_AnchorMax": rt.Change(() => rt.AnchorMax = cur); break;
                    default: rt.Change(() => rt.Pivot = cur); break;
                }
            };
            return true;
        }

        static readonly uint IsActiveHash = Icalls.Crc32("m_IsActive");
        static readonly uint EnabledHash = Icalls.Crc32("m_Enabled");
        static readonly uint AlphaHash = Icalls.Crc32("m_Alpha");
        static readonly Dictionary<uint, string> spriteRendererProps = new Dictionary<uint, string>
        {
            [Icalls.Crc32("m_Color.r")] = "r", [Icalls.Crc32("m_Color.g")] = "g", [Icalls.Crc32("m_Color.b")] = "b", [Icalls.Crc32("m_Color.a")] = "a",
            [Icalls.Crc32("m_FlipX")] = "flipX", [Icalls.Crc32("m_FlipY")] = "flipY",
            [Icalls.Crc32("m_Size.x")] = "sizeX", [Icalls.Crc32("m_Size.y")] = "sizeY",
            [Icalls.Crc32("m_SortingOrder")] = "order",
        };
        static readonly HashSet<string> unboundLogged = new HashSet<string>();

        NTransform PathTarget(uint path)
        {
            var root = GameObject.Transform;
            if (path == 0) return root;
            NTransform found = null;
            void Walk(NTransform t, string prefix)
            {
                foreach (var c in t.Children)
                {
                    if (found != null) return;
                    string p = prefix.Length == 0 ? c.GameObject.Name : prefix + "/" + c.GameObject.Name;
                    if (Icalls.Crc32(p) == path) { found = c; return; }
                    Walk(c, p);
                }
            }
            Walk(root, "");
            return found;
        }

        Target Bind(ClipData.Binding b)
        {
            var key = (b.Path, b.Attribute, b.TypeId, b.CustomType, b.Script);
            return targets.TryGetValue(key, out var tg) ? tg : BindNew(b, key);
        }

        // The first binding of a curve (kept apart: its lambdas allocate on every call of the method holding them).
        Target BindNew(ClipData.Binding b, (uint, uint, int, int, Type) key)
        {
            var tg = new Target { B = b };
            targets[key] = tg;
            var tr = PathTarget(b.Path);
            if (tr == null) { tg.Unbound = "path " + b.Path; return tg; }
            tg.Transform = tr;
            var go = tr.GameObject;
            switch (b.TypeId)
            {
                case 4:
                    if (b.Attribute == 1) { var v = tr.LocalPosition; tg.Default[0] = v.x; tg.Default[1] = v.y; tg.Default[2] = v.z; }
                    else if (b.Attribute == 2) { var q = tr.LocalRotation; tg.Default[0] = q.x; tg.Default[1] = q.y; tg.Default[2] = q.z; tg.Default[3] = q.w; }
                    else if (b.Attribute == 3) { var v = tr.LocalScale; tg.Default[0] = v.x; tg.Default[1] = v.y; tg.Default[2] = v.z; }
                    else if (b.Attribute == 4) { var e = MathX.ToEulerRad(tr.LocalRotation) * Mathf.Rad2Deg; tg.Default[0] = e.x; tg.Default[1] = e.y; tg.Default[2] = e.z; }
                    else tg.Unbound = "transform attribute " + b.Attribute;
                    break;
                case 1:
                    if (b.Attribute == IsActiveHash) tg.Default[0] = go.ActiveSelf ? 1 : 0;
                    else tg.Unbound = "GameObject attribute " + b.Attribute;
                    break;
                case 212:
                    {
                        var sr = go.Components.Find(c => c is NSpriteRenderer) as NSpriteRenderer;
                        tg.Component = sr;
                        if (sr == null) { tg.Unbound = "no SpriteRenderer"; break; }
                        if (b.IsPPtr) tg.DefaultObj = sr.Sprite;
                        else if (b.Attribute == EnabledHash) tg.Default[0] = sr.Enabled ? 1 : 0;
                        else if (spriteRendererProps.TryGetValue(b.Attribute, out var prop))
                            tg.Default[0] = prop == "r" ? sr.Color.r : prop == "g" ? sr.Color.g : prop == "b" ? sr.Color.b : prop == "a" ? sr.Color.a : prop == "flipX" ? (sr.FlipX ? 1 : 0)
                                : prop == "sizeX" ? sr.Size.x : prop == "sizeY" ? sr.Size.y : prop == "order" ? sr.SortingOrder : (sr.FlipY ? 1 : 0);
                        else tg.Unbound = "SpriteRenderer attribute " + b.Attribute;
                        break;
                    }
                case 331:
                    {
                        // SpriteMask: its sprite (the DLC's animated masks: Glumstone's ruffles) and enabled.
                        var sm = go.Components.Find(c => c is NSpriteMask) as NSpriteMask;
                        tg.Component = sm;
                        if (sm == null) { tg.Unbound = "no SpriteMask"; break; }
                        if (b.IsPPtr) tg.DefaultObj = sm.Sprite;
                        else if (b.Attribute == EnabledHash) tg.Default[0] = sm.Enabled ? 1 : 0;
                        else tg.Unbound = "SpriteMask attribute " + b.Attribute;
                        break;
                    }
                case 114:
                    {
                        var comp = b.Script == null ? null : go.Components.Find(c => c is NBehaviour nb && b.Script.IsInstanceOfType(nb.Managed));
                        tg.Component = comp;
                        if (comp == null) { tg.Unbound = "no script " + b.Script; break; }
                        if (b.Attribute == EnabledHash) { tg.Default[0] = comp.Enabled ? 1 : 0; break; }
                        if (!BindScriptField(tg, comp)) { tg.Unbound = "script field " + b.Attribute + " on " + b.Script; break; }
                        tg.Notify = comp as NBehaviour;
                        break;
                    }
                case 224:
                    if (tr is NRectTransform rt && BindRect(tg, rt)) break;
                    tg.Unbound = "RectTransform attribute " + b.Attribute;
                    break;
                case 61:
                case 58:
                case 60:
                case 68:
                case 70:
                    {
                        var col = go.Components.Find(c => c is NCollider2D nc && Icalls.ClassId(nc.Managed.GetType()) == b.TypeId) as NCollider2D;
                        tg.Component = col;
                        if (col == null) { tg.Unbound = "no collider " + b.TypeId; break; }
                        if (b.Attribute == EnabledHash) { tg.Default[0] = col.Enabled ? 1 : 0; break; }
                        if (b.Attribute == Icalls.Crc32("m_IsTrigger")) { tg.Default[0] = col.IsTrigger ? 1 : 0; tg.Set = v => col.IsTrigger = v > 0.5f; break; }
                        if (b.Attribute == Icalls.Crc32("m_UsedByEffector")) { tg.Default[0] = col.UsedByEffector ? 1 : 0; tg.Set = v => { col.UsedByEffector = v > 0.5f; col.Version++; }; break; }
                        if (b.Attribute == Icalls.Crc32("m_Radius")) { tg.Default[0] = col.Radius; tg.Set = v => { col.Radius = v; col.Version++; }; break; }
                        bool bound = false;
                        foreach (var (fname, k) in new[] { ("m_Size", 0), ("m_Size", 1), ("m_Offset", 0), ("m_Offset", 1) })
                            if (b.Attribute == Icalls.Crc32(fname + "." + subNames[k]))
                            {
                                int kk = k; bool size = fname == "m_Size";
                                tg.Default[0] = size ? col.Size[k] : col.Offset[k];
                                tg.Set = v => { if (size) { var s2 = col.Size; s2[kk] = v; col.Size = s2; } else { var o2 = col.Offset; o2[kk] = v; col.Offset = o2; } col.Version++; };
                                bound = true;
                                break;
                            }
                        if (!bound) tg.Unbound = "collider attribute " + b.Attribute;
                        break;
                    }
                case 225:
                    {
                        var cg = go.Components.Find(c => c is NCanvasGroup) as NCanvasGroup;
                        tg.Component = cg;
                        if (cg == null) { tg.Unbound = "no CanvasGroup"; break; }
                        if (b.Attribute == AlphaHash) { tg.Default[0] = cg.Alpha; tg.Set = v => cg.Alpha = v; }
                        else if (b.Attribute == EnabledHash) tg.Default[0] = cg.Enabled ? 1 : 0;
                        else tg.Unbound = "CanvasGroup attribute " + b.Attribute;
                        break;
                    }
                default:
                    {
                        var comp = go.Components.Find(c => c.Managed != null && Icalls.ClassId(c.Managed.GetType()) == b.TypeId);
                        tg.Component = comp;
                        if (comp != null && b.Attribute == EnabledHash) tg.Default[0] = comp.Enabled ? 1 : 0;
                        else tg.Unbound = "class " + b.TypeId + " attribute " + b.Attribute;
                        break;
                    }
            }
            if (tg.Unbound != null)
            {
                string what = "animation binding unbound: " + tg.Unbound + " (" + (Objects.Of(Controller)?.Name ?? "") + ")";
                if (unboundLogged.Add(what)) AssetDb.Problem(what);
            }
            else controlled.Add(tg);
            return tg;
        }

        void Accumulate(int li, StatePlay p, float weight)
        {
            if (p == null || weight <= 0f) return;
            var st = StateAt(li, p.State);
            int ci = ClipIndex(st);
            if (ci < 0 || ci >= Data.Clips.Length || Data.Clips[ci] == null) return;
            var clip = Data.Clips[ci];
            float len = clip.Length;
            float t = p.Time;
            if (len > 0f)
            {
                if (clip.Loop || st.Loop) { t %= len; if (t < 0) t += len; }
                else t = Math.Max(0f, Math.Min(len, t));
            }
            foreach (var b in clip.Bindings)
            {
                var tg = Bind(b);
                if (tg.Unbound != null) continue;
                if (b.IsPPtr)
                {
                    int idx = (int)Math.Floor(clip.Sample(b.FirstCurve, t) + 0.0001f);
                    if (!tg.Written || weight >= 0.5f) tg.Obj = idx >= 0 && idx < clip.PPtrs.Length ? clip.PPtrs[idx] : null;
                }
                else
                {
                    for (int k = 0; k < b.CurveCount; k++)
                    {
                        float v = clip.Sample(b.FirstCurve + k, t);
                        tg.Value[k] = tg.Written ? tg.Value[k] + (v - tg.Value[k]) * weight : v;
                    }
                }
                tg.Written = true;
            }
        }

        void Apply()
        {
            foreach (var tg in controlled) { tg.Written = false; }
            for (int li = 0; li < Layers.Length; li++)
            {
                var l = Layers[li];
                if (li > 0 && l.Weight <= 0f) continue;
                float w = l.Next != null && l.TransitionLength > 0f ? Math.Min(1f, l.TransitionTime / l.TransitionLength) : 0f;
                Accumulate(li, l.Current, 1f);
                if (l.Next != null) Accumulate(li, l.Next, w);
            }
            bool writeDefaults = true;
            if (Layers.Length > 0) { var s = StateAt(0, Layers[0].Current.State); if (s != null) writeDefaults = s.WriteDefaults; }
            HashSet<NBehaviour> notify = null;
            foreach (var tg in controlled)
            {
                if (!tg.Written)
                {
                    if (!writeDefaults) continue;
                    Array.Copy(tg.Default, tg.Value, 4);
                    tg.Obj = tg.DefaultObj;
                }
                Write(tg);
                if (tg.Notify != null) (notify ??= new HashSet<NBehaviour>()).Add(tg.Notify);
            }
            // Unity tells scripts whose serialized fields the animation wrote (UGUI marks itself dirty).
            if (notify != null)
                foreach (var nb in notify)
                {
                    var m = MessageSet.Find(nb.Managed.GetType(), "OnDidApplyAnimationProperties");
                    if (m != null && !nb.Destroyed) Behaviours.Send(nb, m);
                }
        }

        void Write(Target tg)
        {
            var b = tg.B; var v = tg.Value;
            if (tg.SetObj != null) { tg.SetObj(tg.Obj); if (tg.Obj is Sprite sp) Atlases.Touch(sp); return; }
            if (tg.Set != null) { tg.Set(v[0]); return; }
            switch (b.TypeId)
            {
                case 4:
                    {
                        var tr = tg.Transform;
                        if (b.Attribute == 1) tr.LocalPosition = new Vector3(v[0], v[1], v[2]);
                        else if (b.Attribute == 2) tr.LocalRotation = MathX.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));
                        else if (b.Attribute == 3) tr.LocalScale = new Vector3(v[0], v[1], v[2]);
                        else if (b.Attribute == 4) tr.LocalRotation = MathX.FromEulerRad(new Vector3(v[0], v[1], v[2]) * Mathf.Deg2Rad);
                        tr.Invalidate();
                        break;
                    }
                case 1:
                    Lifecycle.SetActive(tg.Transform.GameObject, v[0] > 0.5f);
                    break;
                case 212:
                    {
                        var sr = (NSpriteRenderer)tg.Component;
                        if (b.IsPPtr) { if (tg.Obj is Sprite s || tg.Obj == null) { sr.Sprite = tg.Obj as Sprite; if (sr.Sprite != null) Atlases.Touch(sr.Sprite); } }
                        else if (b.Attribute == EnabledHash) sr.Enabled = v[0] > 0.5f;
                        else
                        {
                            var prop = spriteRendererProps[b.Attribute];
                            var c = sr.Color;
                            if (prop == "r") c.r = v[0]; else if (prop == "g") c.g = v[0]; else if (prop == "b") c.b = v[0]; else if (prop == "a") c.a = v[0];
                            else if (prop == "flipX") sr.FlipX = v[0] > 0.5f;
                            else if (prop == "sizeX") sr.Size = new Vector2(v[0], sr.Size.y);
                            else if (prop == "sizeY") sr.Size = new Vector2(sr.Size.x, v[0]);
                            else if (prop == "order") sr.SortingOrder = (int)Math.Round(v[0]);
                            else sr.FlipY = v[0] > 0.5f;
                            sr.Color = c;
                        }
                        break;
                    }
                case 331:
                    {
                        var sm = (NSpriteMask)tg.Component;
                        if (b.IsPPtr) { if (tg.Obj is Sprite s || tg.Obj == null) { sm.Sprite = tg.Obj as Sprite; if (sm.Sprite != null) Atlases.Touch(sm.Sprite); } }
                        else if (b.Attribute == EnabledHash) sm.Enabled = v[0] > 0.5f;
                        break;
                    }
                default:
                    if (tg.Field != null)
                    {
                        var ft = tg.Field.FieldType;
                        object val = ft == typeof(float) ? v[0] : ft == typeof(int) ? (object)(int)Math.Round(v[0]) : v[0] > 0.5f;
                        tg.Field.SetValue(tg.Component.Managed, val);
                    }
                    else if (tg.Component != null && b.Attribute == EnabledHash)
                    {
                        bool on = v[0] > 0.5f;
                        if (tg.Component.Enabled != on)
                        {
                            if (tg.Component is NBehaviour nb) Icalls.UnityEngine_Behaviour__set_enabled((Behaviour)nb.Managed, on);
                            else tg.Component.Enabled = on;
                        }
                    }
                    break;
            }
        }

        void RestoreDefaults() { }

        // ---------------------------------------------------------------- events

        void FireEvents(int li)
        {
            var lr = Layers[li];
            if (lr.Retired.Count > 0)
            {
                // A state left this update fires its events up to the point it was left. Taken off the
                // list first: an event that updates the animator again must not fire them again.
                var retired = lr.Retired.ToArray();
                lr.Retired.Clear();
                foreach (var old in retired) { old.Fresh = false; FireEvents(li, old, 1f); }
            }
            var l = Layers[li];
            float w = l.Next != null && l.TransitionLength > 0f ? Math.Min(1f, l.TransitionTime / l.TransitionLength) : 0f;
            FireEvents(li, l.Current, 1f - w);
            if (l.Next != null) FireEvents(li, l.Next, w);
        }

        void FireEvents(int li, StatePlay p, float weight)
        {
            var st = StateAt(li, p.State);
            int ci = ClipIndex(st);
            if (ci < 0 || ci >= Data.Clips.Length) return;
            var clip = Data.Clips[ci];
            if (clip == null || clip.Events.Length == 0) return;
            float len = clip.Length;
            float a = p.Prev, b = p.Time;
            if (b < a || (b == a && !p.Fresh)) return;
            bool loop = clip.Loop || st.Loop;
            if (!loop || len <= 0f)
            {
                foreach (var e in clip.Events)
                    if ((e.Time >= a && e.Time < b) || (p.Fresh && e.Time == a) || (!loop && b >= len && e.Time >= len && a < len)) Dispatch(e, li, p, clip, weight);
                return;
            }
            // Looping: walk the cycles covered by [a, b).
            double cycle = Math.Floor(a / len);
            float start = a;
            for (int guard = 0; guard < 64 && start <= b; guard++)
            {
                float cs = (float)(cycle * len);
                float lo = start - cs, hi = Math.Min(b - cs, len);
                foreach (var e in clip.Events)
                    if ((e.Time >= lo && e.Time < hi) || (p.Fresh && guard == 0 && e.Time == lo) || (hi >= len && e.Time >= len && lo < len)) Dispatch(e, li, p, clip, weight);
                if (b - cs <= len) break;
                cycle++;
                start = (float)(cycle * len);
            }
        }

        void Dispatch(ClipData.Event e, int li, StatePlay p, ClipData clip, float weight)
        {
            var go = GameObject;
            if (go == null || Destroyed || !Enabled || !go.ActiveInHierarchy) return;
            var ev = new AnimationEvent();
            ev.m_Time = e.Time; ev.m_FunctionName = e.Function; ev.m_StringParameter = e.Str; ev.m_FloatParameter = e.Float;
            ev.m_IntParameter = e.Int; ev.m_ObjectReferenceParameter = e.Obj; ev.m_MessageOptions = e.Options;
            ev.m_Source = (AnimationEventSource)2;   // Animator
            ev.m_AnimatorStateInfo = Icalls.StateInfo(this, li, p);
            ev.m_AnimatorClipInfo = new AnimatorClipInfo { m_ClipInstanceID = Objects.Of(clip.Clip)?.InstanceId ?? 0, m_Weight = weight };
            bool received = false;
            foreach (var c in go.Components.ToArray())
            {
                if (!(c is NBehaviour b) || b.Destroyed) continue;
                var t = b.Managed.GetType();
                MethodInfo m = null;
                object arg = null;
                foreach (var cand in new[] { 1, 0 })
                {
                    m = MessageSet.Find(t, e.Function, cand);
                    if (m == null) continue;
                    if (cand == 0) break;
                    var pt = m.GetParameters()[0].ParameterType;
                    if (pt == typeof(AnimationEvent)) arg = ev;
                    else if (pt == typeof(float)) arg = e.Float;
                    else if (pt == typeof(int)) arg = e.Int;
                    else if (pt == typeof(string)) arg = e.Str;
                    else if (typeof(UObject).IsAssignableFrom(pt)) arg = e.Obj;
                    else if (pt.IsEnum) arg = Enum.ToObject(pt, e.Int);
                    else { m = null; continue; }
                    break;
                }
                if (m == null) continue;
                received = true;
                var r = Behaviours.Send(b, m, m.GetParameters().Length == 1 ? arg : null);
                if (r is IEnumerator en) Coroutines.Start(b, en);
            }
            if (!received && e.Options == 0)
                Log.Line("UNITY Error: AnimationEvent '" + e.Function + "' on animation '" + clip.Name + "' has no receiver! Are you missing a component?");
        }
    }

    /// <summary>The animator pass of the player loop (after Update and coroutines, before LateUpdate).</summary>
    public static class Animators
    {
        static readonly List<NAnimator> all = new List<NAnimator>();
        static readonly HashSet<NAnimator> set = new HashSet<NAnimator>();

        public static void Register(NAnimator a) { if (set.Add(a)) all.Add(a); }

        /// <summary>AssetUnload: the animation caches keep what live animators use.</summary>
        public static void RetainLiveData()
        {
            var ctls = new HashSet<ControllerData>();
            var clips = new HashSet<ClipData>();
            foreach (var a in all)
            {
                if (a.Destroyed || a.Data == null) continue;
                ctls.Add(a.Data);
                foreach (var c in a.Data.Clips) if (c != null) clips.Add(c);
            }
            ControllerData.Retain(ctls);
            ClipData.Retain(clips);
        }

        public static void Step()
        {
            all.RemoveAll(a => { if (a.Destroyed) { set.Remove(a); return true; } return false; });
            int count = all.Count;
            var batch = System.Buffers.ArrayPool<NAnimator>.Shared.Rent(count);
            all.CopyTo(batch);
            for (int i = 0; i < count; i++)
            {
                var a = batch[i];
                if (a.Destroyed || !a.Enabled || a.GameObject == null || !a.GameObject.ActiveInHierarchy) continue;
                if (a.UpdateMode == AnimatorUpdateMode.AnimatePhysics) continue;
                float dt = a.UpdateMode == AnimatorUpdateMode.UnscaledTime ? Clock.UnscaledDeltaTime : Clock.DeltaTime;
                a.Update(dt);
            }
            System.Buffers.ArrayPool<NAnimator>.Shared.Return(batch, true);
        }

        public static void FixedStep()
        {
            int count = all.Count;
            var batch = System.Buffers.ArrayPool<NAnimator>.Shared.Rent(count);
            all.CopyTo(batch);
            for (int i = 0; i < count; i++)
            {
                var a = batch[i];
                if (!a.Destroyed && a.Enabled && a.GameObject != null && a.GameObject.ActiveInHierarchy && a.UpdateMode == AnimatorUpdateMode.AnimatePhysics)
                    a.Update(Clock.FixedDeltaTime);
            }
            System.Buffers.ArrayPool<NAnimator>.Shared.Return(batch, true);
        }
    }

    public static partial class Icalls
    {
        static readonly Dictionary<Type, int> classIds = new Dictionary<Type, int>
        {
            [typeof(GameObject)] = 1, [typeof(Transform)] = 4, [typeof(Camera)] = 20, [typeof(Animator)] = 95, [typeof(AudioSource)] = 82,
            [typeof(SpriteRenderer)] = 212, [typeof(MonoBehaviour)] = 114, [typeof(BoxCollider2D)] = 61, [typeof(CircleCollider2D)] = 58,
            [typeof(PolygonCollider2D)] = 60, [typeof(Rigidbody2D)] = 50, [typeof(MeshRenderer)] = 23, [typeof(ParticleSystem)] = 198,
            [typeof(EdgeCollider2D)] = 68, [typeof(CapsuleCollider2D)] = 70, [typeof(RectTransform)] = 224, [typeof(CanvasGroup)] = 225,
            [typeof(SpriteMask)] = 331, [typeof(MeshFilter)] = 33,
        };

        public static int ClassId(Type t)
        {
            for (var c = t; c != null; c = c.BaseType) if (classIds.TryGetValue(c, out int id)) return id;
            return 0;
        }

        static NAnimator Anim(Animator a) => Objects.Of(a) as NAnimator;

        static NAnimator Ready(Animator a)
        {
            var n = Anim(a);
            if (n != null && !n.Initialized) n.Rebind(false);
            return n;
        }

        public static int UnityEngine_Animator__StringToHash(string name) => (int)Crc32(name ?? "");

        public static AnimatorStateInfo StateInfo(NAnimator a, int layer, NAnimator.StatePlay p)
        {
            var info = new AnimatorStateInfo();
            var s = p == null ? null : a.StateAt(layer, p.State);
            if (s == null) return info;
            info.m_Name = (int)s.NameId; info.m_Path = (int)s.PathId; info.m_FullPath = (int)s.FullPathId;
            float len = a.StateLength(layer, p.State);
            float speed = s.Speed;
            info.m_NormalizedTime = p.Time / len;
            info.m_Length = len;
            info.m_Speed = speed;
            info.m_SpeedMultiplier = 1f;
            if (s.SpeedParam != 0) { int pi = a.ParamIndex(s.SpeedParam); if (pi >= 0) info.m_SpeedMultiplier = a.Floats[pi]; }
            info.m_Tag = (int)s.TagId;
            int ci = a.ClipIndex(s);
            var c = ci >= 0 && ci < a.Data.Clips.Length ? a.Data.Clips[ci] : null;
            info.m_Loop = (s.Loop || (c?.Loop ?? false)) ? 1 : 0;
            return info;
        }

        public static AnimatorStateInfo UnityEngine_Animator__GetCurrentAnimatorStateInfo(Animator self, int layerIndex)
        {
            var a = Ready(self);
            if (a == null || layerIndex < 0 || layerIndex >= a.Layers.Length) return default;
            return StateInfo(a, layerIndex, a.Layers[layerIndex].Current);
        }
        public static AnimatorStateInfo UnityEngine_Animator__GetNextAnimatorStateInfo(Animator self, int layerIndex)
        {
            var a = Ready(self);
            if (a == null || layerIndex < 0 || layerIndex >= a.Layers.Length || a.Layers[layerIndex].Next == null) return default;
            return StateInfo(a, layerIndex, a.Layers[layerIndex].Next);
        }
        public static bool UnityEngine_Animator__IsInTransition(Animator self, int layerIndex)
        {
            var a = Ready(self);
            return a != null && layerIndex >= 0 && layerIndex < a.Layers.Length && a.Layers[layerIndex].Next != null;
        }
        public static void UnityEngine_Animator__GetAnimatorTransitionInfo(Animator self, int layerIndex, out AnimatorTransitionInfo info)
        {
            info = default;
            var a = Ready(self);
            if (a == null || layerIndex < 0 || layerIndex >= a.Layers.Length) return;
            var l = a.Layers[layerIndex];
            if (l.Next == null || l.Transition == null) return;
            info.m_FullPath = (int)l.Transition.FullPath; info.m_Name = (int)l.Transition.Name; info.m_UserName = (int)l.Transition.UserName;
            info.m_Duration = l.Transition.Duration; info.m_HasFixedDuration = l.Transition.FixedDuration;
            info.m_NormalizedTime = l.TransitionLength > 0 ? l.TransitionTime / l.TransitionLength : 1f;
        }
        public static string UnityEngine_Animator__GetLayerName(Animator self, int layerIndex)
        {
            var a = Ready(self);
            return a != null && a.Data != null && layerIndex >= 0 && layerIndex < a.Data.Layers.Length ? a.Data.Layers[layerIndex].Name : null;
        }
        public static int UnityEngine_Animator__GetLayerIndex(Animator self, string layerName)
        {
            var a = Ready(self);
            if (a?.Data == null) return -1;
            for (int i = 0; i < a.Data.Layers.Length; i++) if (a.Data.Layers[i].Name == layerName) return i;
            return -1;
        }
        public static int UnityEngine_Animator__get_layerCount(Animator self) => Ready(self)?.Layers.Length ?? 0;
        public static float UnityEngine_Animator__GetLayerWeight(Animator self, int layerIndex)
        {
            var a = Ready(self);
            return a != null && layerIndex >= 0 && layerIndex < a.Layers.Length ? a.Layers[layerIndex].Weight : 0f;
        }
        public static void UnityEngine_Animator__SetLayerWeight(Animator self, int layerIndex, float weight)
        {
            var a = Ready(self);
            if (a != null && layerIndex > 0 && layerIndex < a.Layers.Length)
            {
                if (a.TraceNow && a.Layers[layerIndex].Weight != weight) Log.Line($"TRACE anim frame {Clock.FrameCount} {a.GameObject.Name} layer {layerIndex} weight {weight}");
                a.Layers[layerIndex].Weight = weight;
            }
        }
        public static bool UnityEngine_Animator__HasState(Animator self, int layerIndex, int stateID) => Ready(self)?.FindState(layerIndex, (uint)stateID) >= 0;

        /// <summary>Unity's warning, once per controller, state and layer, naming them (as names when the controller knows them).</summary>
        static void NoState(NAnimator a, int hash, int layer)
        {
            if (!warnedParams.Add((a.Data, hash ^ (layer << 24)))) return;
            string n = a.Data.NameOf((uint)hash);
            Log.Line($"UNITY Warning: Animator.GotoState: State could not be found ({a.GameObject?.Name}, {(string.IsNullOrEmpty(n) ? "hash " + hash : n)}, layer {layer})");
        }

        static void PlayRequest(Animator self, int hash, int layer, float normalizedTime, float? cross, float offset, bool fixedTime)
        {
            var a = Ready(self);
            if (a?.Data == null) return;
            int st;
            if (hash == 0)
            {
                // Unity: a state hash of 0 is the layer's current state (Esther Winchester's projectiles start
                // their loop at a random point with Play(0, 0, t)).
                if (layer < 0) layer = 0;
                if (layer >= a.Layers.Length) return;
                var cur = a.Layers[layer];
                st = cur.PendingPlay is (int ps, float _) ? ps : cur.Current.State;
            }
            else
            {
                if (layer < 0)
                {
                    for (int i = 0; i < a.Layers.Length; i++) if (a.FindState(i, (uint)hash) >= 0) { layer = i; break; }
                    if (layer < 0) { NoState(a, hash, layer); return; }
                }
                if (layer >= a.Layers.Length) return;
                st = a.FindState(layer, (uint)hash);
                if (st < 0) { NoState(a, hash, layer); return; }
            }
            var l = a.Layers[layer];
            if (NAnimator.WindowOnPlay != null && NAnimator.WindowsLeft > 0 && a.GameObject?.Name == NAnimator.WindowName && NAnimator.WindowOnPlay.Contains(hash))
            {
                NAnimator.WindowsLeft--;
                NAnimator.WindowUntil = Clock.FrameCount + NAnimator.WindowFrames;
                Log.Line($"TRACE anim frame {Clock.FrameCount} {a.GameObject.Name}: window opened by its play of {hash:X8} on layer {layer}");
            }
            if (a.TraceNow) Log.Line($"TRACE anim frame {Clock.FrameCount} {a.GameObject.Name} play layer {layer} state {hash:X8} (#{st}) at {normalizedTime} cross {cross}");
            if (cross.HasValue) { l.PendingCross = (st, cross.Value, offset, fixedTime); l.PendingPlay = null; }
            else
            {
                float n = fixedTime && !float.IsNegativeInfinity(normalizedTime) ? normalizedTime / a.StateLength(layer, st) : normalizedTime;
                l.PendingPlay = (st, n); l.PendingCross = null;
            }
        }

        public static void UnityEngine_Animator__Play(Animator self, int stateNameHash, int layer, float normalizedTime) => PlayRequest(self, stateNameHash, layer, normalizedTime, null, 0, false);
        public static void UnityEngine_Animator__PlayInFixedTime(Animator self, int stateNameHash, int layer, float fixedTime) => PlayRequest(self, stateNameHash, layer, fixedTime, null, 0, true);
        public static void UnityEngine_Animator__CrossFade(Animator self, int stateHashName, float normalizedTransitionDuration, int layer, float normalizedTimeOffset, float normalizedTransitionTime)
            => PlayRequest(self, stateHashName, layer, 0, normalizedTransitionDuration, normalizedTimeOffset, false);
        public static void UnityEngine_Animator__CrossFadeInFixedTime(Animator self, int stateHashName, float fixedTransitionDuration, int layer, float fixedTimeOffset, float normalizedTransitionTime)
            => PlayRequest(self, stateHashName, layer, 0, fixedTransitionDuration, fixedTimeOffset, true);

        public static void UnityEngine_Animator__Update(Animator self, float deltaTime) => Ready(self)?.Update(deltaTime);
        public static void UnityEngine_Animator__Rebind(Animator self) => Anim(self)?.Rebind(false);
        public static bool UnityEngine_Animator__get_isInitialized(Animator self) => Anim(self)?.Initialized ?? false;
        public static bool UnityEngine_Animator__get_hasBoundPlayables(Animator self) => false;
        public static float UnityEngine_Animator__get_speed(Animator self) => Anim(self)?.Speed ?? 1f;
        public static void UnityEngine_Animator__set_speed(Animator self, float value) { var a = Anim(self); if (a != null) a.Speed = value; }
        public static AnimatorUpdateMode UnityEngine_Animator__get_updateMode(Animator self) => Anim(self)?.UpdateMode ?? 0;
        public static void UnityEngine_Animator__set_updateMode(Animator self, AnimatorUpdateMode value) { var a = Anim(self); if (a != null) a.UpdateMode = value; }
        public static AnimatorCullingMode UnityEngine_Animator__get_cullingMode(Animator self) => Anim(self)?.CullingMode ?? 0;
        public static void UnityEngine_Animator__set_cullingMode(Animator self, AnimatorCullingMode value) { var a = Anim(self); if (a != null) a.CullingMode = value; }
        public static bool UnityEngine_Animator__get_applyRootMotion(Animator self) => Anim(self)?.ApplyRootMotion ?? false;
        public static void UnityEngine_Animator__set_applyRootMotion(Animator self, bool value) { var a = Anim(self); if (a != null) a.ApplyRootMotion = value; }
        public static bool UnityEngine_Animator__get_hasRootMotion(Animator self) => false;
        public static bool UnityEngine_Animator__get_isHuman(Animator self) => false;
        public static bool UnityEngine_Animator__get_hasTransformHierarchy(Animator self) => true;
        public static RuntimeAnimatorController UnityEngine_Animator__get_runtimeAnimatorController(Animator self) => Anim(self)?.Controller;
        public static void UnityEngine_Animator__set_runtimeAnimatorController(Animator self, RuntimeAnimatorController value)
        {
            var a = Anim(self);
            if (a == null || a.Controller == value) return;
            a.Controller = value;
            a.Rebind(false);
        }

        // Playback mode replays what StartRecording recorded; nothing records here, so there is nothing to play
        // back and the animator keeps running as before (Saltbaker stops playback on its animators).
        public static void UnityEngine_Animator__StartPlayback(Animator self) { }
        public static void UnityEngine_Animator__StopPlayback(Animator self) { }

        static int Param(NAnimator a, int id, AnimatorControllerParameterType? want, string what)
        {
            int i = a?.ParamIndex((uint)id) ?? -1;
            if (i < 0)
            {
                // Once per controller and parameter: projectiles set "Parry" on animators without one
                // hundreds of times a fight, and each log line costs on the console.
                if (a?.Data != null && warnedParams.Add((a.Data, id)))
                    Log.Line("UNITY Warning: Parameter '" + (a.Data.NameOf((uint)id) is string n && n.Length > 0 ? n : "Hash " + id) + "' does not exist.");
                return -1;
            }
            return i;
        }
        static readonly HashSet<(object, int)> warnedParams = new HashSet<(object, int)>();
        public static void UnityEngine_Animator__SetFloatID(Animator self, int id, float value) { var a = Ready(self); int i = Param(a, id, null, "SetFloat"); if (i >= 0) a.Floats[i] = value; }
        public static void UnityEngine_Animator__SetFloatString(Animator self, string name, float value) => UnityEngine_Animator__SetFloatID(self, (int)Crc32(name ?? ""), value);
        public static void UnityEngine_Animator__SetFloatIDDamp(Animator self, int id, float value, float dampTime, float deltaTime)
        {
            var a = Ready(self); int i = Param(a, id, null, "SetFloat");
            if (i < 0) return;
            // Unity's damper: critically damped approach over dampTime.
            float cur = a.Floats[i];
            a.Floats[i] = dampTime <= 0f ? value : cur + (value - cur) * Math.Min(1f, deltaTime / dampTime);
        }
        public static void UnityEngine_Animator__SetFloatStringDamp(Animator self, string name, float value, float dampTime, float deltaTime)
            => UnityEngine_Animator__SetFloatIDDamp(self, (int)Crc32(name ?? ""), value, dampTime, deltaTime);
        public static float UnityEngine_Animator__GetFloatID(Animator self, int id) { var a = Ready(self); int i = Param(a, id, null, "GetFloat"); return i >= 0 ? a.Floats[i] : 0f; }
        public static float UnityEngine_Animator__GetFloatString(Animator self, string name) => UnityEngine_Animator__GetFloatID(self, (int)Crc32(name ?? ""));
        public static void UnityEngine_Animator__SetIntegerID(Animator self, int id, int value) { var a = Ready(self); int i = Param(a, id, null, "SetInteger"); if (i >= 0) a.Ints[i] = value; }
        public static void UnityEngine_Animator__SetIntegerString(Animator self, string name, int value) => UnityEngine_Animator__SetIntegerID(self, (int)Crc32(name ?? ""), value);
        public static int UnityEngine_Animator__GetIntegerID(Animator self, int id) { var a = Ready(self); int i = Param(a, id, null, "GetInteger"); return i >= 0 ? a.Ints[i] : 0; }
        public static int UnityEngine_Animator__GetIntegerString(Animator self, string name) => UnityEngine_Animator__GetIntegerID(self, (int)Crc32(name ?? ""));
        public static void UnityEngine_Animator__SetBoolID(Animator self, int id, bool value)
        {
            var a = Ready(self); int i = Param(a, id, null, "SetBool");
            if (NAnimator.TraceName != null && a?.GameObject != null && a.GameObject.Name.Contains(NAnimator.TraceName)) Log.Line($"TRACE animator {a.GameObject.Name} SetBool/Trigger {id}={value} index {i} frame {Clock.FrameCount}");
            else if (a != null && a.TraceNow && i >= 0 && a.Bools[i] != value) Log.Line($"TRACE anim frame {Clock.FrameCount} {a.GameObject.Name} bool {id:X8} {value}");
            if (i >= 0) a.Bools[i] = value;
        }
        public static void UnityEngine_Animator__SetBoolString(Animator self, string name, bool value) => UnityEngine_Animator__SetBoolID(self, (int)Crc32(name ?? ""), value);
        public static bool UnityEngine_Animator__GetBoolID(Animator self, int id) { var a = Ready(self); int i = Param(a, id, null, "GetBool"); return i >= 0 && a.Bools[i]; }
        public static bool UnityEngine_Animator__GetBoolString(Animator self, string name) => UnityEngine_Animator__GetBoolID(self, (int)Crc32(name ?? ""));
        public static void UnityEngine_Animator__SetTriggerID(Animator self, int id) => UnityEngine_Animator__SetBoolID(self, id, true);
        public static void UnityEngine_Animator__SetTriggerString(Animator self, string name) => UnityEngine_Animator__SetBoolID(self, (int)Crc32(name ?? ""), true);
        public static void UnityEngine_Animator__ResetTriggerID(Animator self, int id) => UnityEngine_Animator__SetBoolID(self, id, false);
        public static void UnityEngine_Animator__ResetTriggerString(Animator self, string name) => UnityEngine_Animator__SetBoolID(self, (int)Crc32(name ?? ""), false);
        public static int UnityEngine_Animator__get_parameterCount(Animator self) => Ready(self)?.Data?.Params.Length ?? 0;
        public static AnimatorControllerParameter[] UnityEngine_Animator__get_parameters(Animator self)
        {
            var a = Ready(self);
            if (a?.Data == null) return new AnimatorControllerParameter[0];
            var r = new AnimatorControllerParameter[a.Data.Params.Length];
            for (int i = 0; i < r.Length; i++)
            {
                var p = a.Data.Params[i];
                r[i] = new AnimatorControllerParameter { m_Name = p.Name, m_Type = p.Type, m_DefaultFloat = p.DefaultFloat, m_DefaultInt = p.DefaultInt, m_DefaultBool = p.DefaultBool };
            }
            return r;
        }
        public static bool UnityEngine_Animator__IsParameterControlledByCurveID(Animator self, int id) => false;
        public static bool UnityEngine_Animator__IsParameterControlledByCurveString(Animator self, string name) => false;

        static List<AnimatorClipInfo> ClipInfos(Animator self, int layerIndex, bool current)
        {
            var list = new List<AnimatorClipInfo>();
            var a = Ready(self);
            if (a == null || layerIndex < 0 || layerIndex >= a.Layers.Length) return list;
            var l = a.Layers[layerIndex];
            var p = current ? l.Current : l.Next;
            var s = p == null ? null : a.StateAt(layerIndex, p.State);
            int ci = a.ClipIndex(s);
            if (ci < 0 || ci >= a.Data.ClipObjects.Length || a.Data.ClipObjects[ci] == null) return list;
            float w = l.Next != null && l.TransitionLength > 0 ? Math.Min(1f, l.TransitionTime / l.TransitionLength) : 0f;
            list.Add(new AnimatorClipInfo { m_ClipInstanceID = Objects.Of(a.Data.ClipObjects[ci])?.InstanceId ?? 0, m_Weight = current ? 1f - w : w });
            return list;
        }
        public static AnimatorClipInfo[] UnityEngine_Animator__GetCurrentAnimatorClipInfo(Animator self, int layerIndex) => ClipInfos(self, layerIndex, true).ToArray();
        public static AnimatorClipInfo[] UnityEngine_Animator__GetNextAnimatorClipInfo(Animator self, int layerIndex) => ClipInfos(self, layerIndex, false).ToArray();
        public static int UnityEngine_Animator__GetCurrentAnimatorClipInfoCount(Animator self, int layerIndex) => ClipInfos(self, layerIndex, true).Count;
        public static int UnityEngine_Animator__GetNextAnimatorClipInfoCount(Animator self, int layerIndex) => ClipInfos(self, layerIndex, false).Count;
        public static void UnityEngine_Animator__GetAnimatorClipInfoInternal(Animator self, int layerIndex, bool isCurrent, object clips)
        {
            if (clips is List<AnimatorClipInfo> l) { l.Clear(); l.AddRange(ClipInfos(self, layerIndex, isCurrent)); }
        }
        public static AnimationClip UnityEngine_AnimatorClipInfo__ClipInstanceToScriptingObject(int instanceID) => Objects.ByInstanceId(instanceID)?.Managed as AnimationClip;

        // ------------------------------------------------------------ clips and controllers

        public static AnimationClip[] UnityEngine_RuntimeAnimatorController__get_animationClips(RuntimeAnimatorController self)
        {
            var d = ControllerData.Of(self);
            if (d == null) return new AnimationClip[0];
            var list = new List<AnimationClip>();
            foreach (var o in d.ClipObjects) if (o is AnimationClip c && !list.Contains(c)) list.Add(c);
            return list.ToArray();
        }
        public static float UnityEngine_AnimationClip__get_length(AnimationClip self) => ClipData.Of(self)?.Length ?? 0f;
        public static float UnityEngine_AnimationClip__get_frameRate(AnimationClip self) => ClipData.Of(self)?.SampleRate ?? 60f;
        public static float UnityEngine_AnimationClip__get_startTime(AnimationClip self) => ClipData.Of(self)?.Start ?? 0f;
        public static float UnityEngine_AnimationClip__get_stopTime(AnimationClip self) => ClipData.Of(self)?.Stop ?? 0f;
        public static bool UnityEngine_AnimationClip__get_legacy(AnimationClip self) => Tree.Bool((Objects.Of(self) as NAsset)?.Source?.Tree, "m_Legacy");
        public static bool UnityEngine_AnimationClip__get_empty(AnimationClip self) => (ClipData.Of(self)?.CurveCount ?? 0) == 0;
        public static bool UnityEngine_AnimationClip__get_humanMotion(AnimationClip self) => false;
        public static bool UnityEngine_AnimationClip__get_hasRootMotion(AnimationClip self) => false;
        public static WrapMode UnityEngine_AnimationClip__get_wrapMode(AnimationClip self) => (WrapMode)Tree.Int((Objects.Of(self) as NAsset)?.Source?.Tree, "m_WrapMode");
        public static bool UnityEngine_Motion__get_isLooping(Motion self) => self is AnimationClip c && (ClipData.Of(c)?.Loop ?? false);
        public static bool UnityEngine_Motion__get_legacy(Motion self) => self is AnimationClip c && UnityEngine_AnimationClip__get_legacy(c);
        public static bool UnityEngine_Motion__get_isHumanMotion(Motion self) => false;
        public static float UnityEngine_Motion__get_averageDuration(Motion self) => self is AnimationClip c ? UnityEngine_AnimationClip__get_length(c) : 0f;
        public static Array UnityEngine_AnimationClip__GetEventsInternal(AnimationClip self)
        {
            var d = ClipData.Of(self);
            var list = new List<AnimationEvent>();
            if (d != null)
                foreach (var e in d.Events)
                {
                    var ev = new AnimationEvent();
                    ev.m_Time = e.Time; ev.m_FunctionName = e.Function; ev.m_StringParameter = e.Str; ev.m_FloatParameter = e.Float;
                    ev.m_IntParameter = e.Int; ev.m_ObjectReferenceParameter = e.Obj; ev.m_MessageOptions = e.Options;
                    list.Add(ev);
                }
            return list.ToArray();
        }

        public static void UnityEngine_AnimatorOverrideController__Internal_CreateAnimatorOverrideController(AnimatorOverrideController self, RuntimeAnimatorController controller)
        {
            if (Objects.Of(self) == null) Objects.Register(self, new NAsset(), "");
            Overrides.StateOf(self).Base = controller;
            Overrides.Changed(self);
        }
        public static RuntimeAnimatorController UnityEngine_AnimatorOverrideController__get_runtimeAnimatorController(AnimatorOverrideController self) => Overrides.StateOf(self).Base;
        public static void UnityEngine_AnimatorOverrideController__set_runtimeAnimatorController(AnimatorOverrideController self, RuntimeAnimatorController value)
        {
            var s = Overrides.StateOf(self);
            s.Base = value; s.Map.Clear();
            Overrides.Changed(self);
        }
        public static AnimationClip UnityEngine_AnimatorOverrideController__Internal_GetClip(AnimatorOverrideController self, AnimationClip originalClip, bool returnEffectiveClip)
        {
            var s = Overrides.StateOf(self);
            return originalClip != null && s.Map.TryGetValue(originalClip, out var r) ? r as AnimationClip : (returnEffectiveClip ? originalClip : null);
        }
        public static AnimationClip UnityEngine_AnimatorOverrideController__Internal_GetClipByName(AnimatorOverrideController self, string name, bool returnEffectiveClip)
        {
            var s = Overrides.StateOf(self);
            var b = ControllerData.Of(s.Base);
            if (b == null) return null;
            foreach (var o in b.ClipObjects)
                if (o is AnimationClip c && Objects.Of(c)?.Name == name) return UnityEngine_AnimatorOverrideController__Internal_GetClip(self, c, returnEffectiveClip);
            return null;
        }
        public static void UnityEngine_AnimatorOverrideController__Internal_SetClip(AnimatorOverrideController self, AnimationClip originalClip, AnimationClip overrideClip, bool notify)
        {
            if (originalClip == null) return;
            var s = Overrides.StateOf(self);
            if (overrideClip == null) s.Map.Remove(originalClip); else s.Map[originalClip] = overrideClip;
            if (notify) Overrides.Changed(self); else s.Data = null;
        }
        public static void UnityEngine_AnimatorOverrideController__Internal_SetClipByName(AnimatorOverrideController self, string name, AnimationClip clip)
        {
            var s = Overrides.StateOf(self);
            var b = ControllerData.Of(s.Base);
            if (b == null) return;
            foreach (var o in b.ClipObjects)
                if (o is AnimationClip c && Objects.Of(c)?.Name == name) { UnityEngine_AnimatorOverrideController__Internal_SetClip(self, c, clip, true); return; }
        }
        public static void UnityEngine_AnimatorOverrideController__SendNotification(AnimatorOverrideController self) => Overrides.Changed(self);
        public static int UnityEngine_AnimatorOverrideController__get_overridesCount(AnimatorOverrideController self) => ControllerData.Of(Overrides.StateOf(self).Base)?.ClipObjects.Length ?? 0;
        public static AnimationClip UnityEngine_AnimatorOverrideController__Internal_GetOriginalClip(AnimatorOverrideController self, int index)
        {
            var b = ControllerData.Of(Overrides.StateOf(self).Base);
            return b != null && index >= 0 && index < b.ClipObjects.Length ? b.ClipObjects[index] as AnimationClip : null;
        }
        public static AnimationClip UnityEngine_AnimatorOverrideController__Internal_GetOverrideClip(AnimatorOverrideController self, AnimationClip originalClip)
            => UnityEngine_AnimatorOverrideController__Internal_GetClip(self, originalClip, false);
    }
}
