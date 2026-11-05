using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>Unity's Time, driven by the player loop.</summary>
    public static class Clock
    {
        public static float Time, UnscaledTime, DeltaTime, UnscaledDeltaTime;
        public static float FixedTime, FixedUnscaledTime;
        public static float FixedDeltaTime = 0.02f, MaximumDeltaTime = 1f / 3f, TimeScale = 1f;
        public static float RealtimeSinceStartup, TimeSinceLevelLoadBase;
        public static int FrameCount;
        public static bool InFixedTimeStep;
        public static int FixedStepCount;
    }

    /// <summary>
    /// One running coroutine. Unity runs a coroutine's body to its first yield
    /// inside StartCoroutine, then resumes it from the player loop according to
    /// what it yielded.
    /// </summary>
    public sealed class CoroutineState
    {
        public NBehaviour Owner;
        public IEnumerator Routine;
        public Coroutine Handle;
        public object Waiting;        // null: next frame
        public float WakeTime;        // WaitForSeconds (scaled time)
        public CoroutineState WaitOn; // nested coroutine
        public string Name;           // StartCoroutine(string)
        public bool Done, FixedWait, EndOfFrameWait;
        public int YieldFrame, YieldFixedStep;   // when it last yielded
        public long YieldSeq;                    // the order of its last yield among all coroutines' (Unity resumes in that order)
        public bool YieldInFixed;
        public long ProfTicks;        // its steps' time since the last PlayerLoop.TopReport
        public readonly List<CoroutineState> Waiters = new List<CoroutineState>();
    }

    public static class PlayerLoop
    {
        // Every behaviour with an update message, coroutine or invoke, in creation order.
        static readonly List<NBehaviour> tracked = new List<NBehaviour>();
        static readonly HashSet<NBehaviour> trackedSet = new HashSet<NBehaviour>();
        static readonly List<NBehaviour> startQueue = new List<NBehaviour>();
        static readonly List<NObject> destroyQueue = new List<NObject>();
        static readonly List<(NObject obj, float at)> delayedDestroy = new List<(NObject, float)>();
        static float fixedAccumulator;

        public static Action PhysicsStep;   // set by the 2D physics module
        public static Action AnimationStep;
        public static Action RenderFrame;
        public static Action InputUpdate;

        // The update passes' order: script execution order, then creation. Behaviours made during a
        // pass join after it (they wait for the next frame, as before).
        static readonly List<NBehaviour> ordered = new List<NBehaviour>();
        static readonly List<NBehaviour> orderPending = new List<NBehaviour>();
        static int orderWalks;

        public static void Track(NBehaviour b)
        {
            if (!trackedSet.Add(b)) return;
            tracked.Add(b);
            if (orderWalks > 0) orderPending.Add(b); else InsertOrdered(b);
        }

        static void InsertOrdered(NBehaviour b)
        {
            int i = ordered.Count;
            while (i > 0 && ordered[i - 1].ExecOrder > b.ExecOrder) i--;
            ordered.Insert(i, b);
        }

        public static void QueueStart(NBehaviour b) => startQueue.Add(b);

        public static void QueueDestroy(NObject o, float delay)
        {
            if (delay > 0f) delayedDestroy.Add((o, Clock.Time + delay));
            else if (!destroyQueue.Contains(o)) { destroyQueue.Add(o); o.PendingDestroy = true; }
        }

        /// <summary>
        /// The behaviour, or a GameObject above it, is to be destroyed this frame: Unity calls no Start
        /// on it (a scene's own Player object destroys itself in Awake when the level makes the
        /// players; its Start then threw in the Mausoleum).
        /// </summary>
        static bool Doomed(NBehaviour b)
        {
            if (b.PendingDestroy) return true;
            for (var t = b.GameObject?.Transform; t != null; t = t.Parent)
                if (t.GameObject.PendingDestroy) return true;
            return false;
        }

        /// <summary>One frame of Unity's player loop with the given real elapsed time.</summary>
        /// <summary>The wall-clock length of the frame for the mixer when the host caps realDelta (the console), else -1.</summary>
        public static float AudioDelta = -1;

        public static void Frame(float realDelta)
        {
            Clock.FrameCount++;
            Clock.RealtimeSinceStartup += realDelta;
            float dt = Math.Min(realDelta, Clock.MaximumDeltaTime);
            Clock.UnscaledDeltaTime = realDelta;
            Clock.UnscaledTime += realDelta;
            Clock.DeltaTime = dt * Clock.TimeScale;
            Clock.Time += Clock.DeltaTime;

            P(0); SceneLoader.Pump();
            Atlases.Flush();
            Inputs.Update();
            InputUpdate?.Invoke();

            // Fixed steps catch up with scaled time.
            fixedAccumulator += Clock.DeltaTime;
            Clock.InFixedTimeStep = true;
            while (Clock.FixedDeltaTime > 0f && fixedAccumulator >= Clock.FixedDeltaTime)
            {
                fixedAccumulator -= Clock.FixedDeltaTime;
                Clock.FixedTime += Clock.FixedDeltaTime;
                Clock.FixedStepCount++;
                Clock.FixedUnscaledTime += Clock.FixedDeltaTime;
                P(1); RunStarts();
                ForEach(b => b.Messages.FixedUpdate, null);
                Animators.FixedStep();
                P(2); Physics.Step(Clock.FixedDeltaTime);
                PhysicsStep?.Invoke();
                P(1); ResumeCoroutines(fixedPhase: true, endOfFrame: false);
            }
            Clock.InFixedTimeStep = false;

            P(3); RunStarts();
            ForEach(b => b.Messages.Update, null);
            RunInvokes();
            ResumeCoroutines(fixedPhase: false, endOfFrame: false);
            AnimationStep?.Invoke();
            P(4); Animators.Step();
            Atlases.Flush();
            P(5); RunStarts();
            ForEach(b => b.Messages.LateUpdate, null);
            P(6); UI.WillRender();
            Atlases.Flush();
            P(7); RenderList.PreRender();
            if (Capture.Wanted(Clock.FrameCount)) Capture.Write(Clock.FrameCount, RenderList.Build());
            RenderFrame?.Invoke();
            RenderList.PostRender();
            ResumeCoroutines(fixedPhase: false, endOfFrame: true);
            ProcessDestroys();
            P(8); Audio.Step(AudioDelta >= 0 ? AudioDelta : realDelta);   // audio runs in unscaled time
            P(-1);
        }

        // Phase timing (CH360_PROFILE=1): load, fixed scripts, physics, update, animators, late, UI, render/destroy, audio.
        public static bool Profile = Environment.GetEnvironmentVariable("CH360_PROFILE") == "1";
        public static readonly string[] PhaseNames = { "pump", "fixed", "physics", "update", "animators", "late", "ui", "end", "audio" };
        public static readonly double[] PhaseMs = new double[9];
        static int phase = -1;
        static long phaseStart;
        static void P(int next)
        {
            if (!Profile) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (phase >= 0) PhaseMs[phase] += (now - phaseStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            phase = next; phaseStart = now;
        }
        /// <summary>
        /// With Profile on: a script call's time (Stopwatch ticks, which TopReport
        /// adds up by type), logged with its type and method when it took 50 ms
        /// or more. The names are worked out only then: NativeAOT decodes a
        /// type's name from its metadata at each ask, and asking for every
        /// coroutine step and message was 16% of a world map frame (host_native,
        /// sampled).
        /// </summary>
        public static long SlowCall(long start, object target, System.Reflection.MethodInfo method, string methodName = null)
        {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (ticks * 1000 / System.Diagnostics.Stopwatch.Frequency >= 50)
                Log.Line($"SLOW call {(target is Type ty ? ty.FullName : target?.GetType().Name)}.{methodName ?? method?.Name}: {ticks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms (frame {Clock.FrameCount})");
            return ticks;
        }

        internal static readonly Dictionary<Type, long> FinishedTicks = new Dictionary<Type, long>();   // coroutines done since the report

        /// <summary>
        /// The script types that took most time since the last report (every
        /// message to a behaviour of the type, and its coroutines' steps), ms a
        /// frame over frames. Clears what it reports.
        /// </summary>
        public static string TopReport(int frames, int top)
        {
            var byType = new Dictionary<Type, long>(FinishedTicks);
            FinishedTicks.Clear();
            void Add(Type t, long ticks) { if (ticks > 0) { byType.TryGetValue(t, out long v); byType[t] = v + ticks; } }
            foreach (var b in tracked)
            {
                if (b.Managed != null) Add(b.Managed.GetType(), b.ProfTicks);
                b.ProfTicks = 0;
                foreach (var c in b.Coroutines)
                {
                    if (c.Routine != null) Add(c.Routine.GetType(), c.ProfTicks);
                    c.ProfTicks = 0;
                }
            }
            var list = new List<KeyValuePair<Type, long>>(byType);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            double scale = 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, frames);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < top; i++)
            {
                var t = list[i].Key;
                string name = t.DeclaringType != null ? t.DeclaringType.Name + "." + t.Name : t.Name;
                sb.Append(i == 0 ? " " : ", ").Append(name).Append(' ').Append((list[i].Value * scale).ToString("0.00"));
            }
            return sb.ToString();
        }

        public static string ProfileReport(int frames)
        {
            var sb = new System.Text.StringBuilder("PROFILE ms/frame:");
            for (int i = 0; i < PhaseMs.Length; i++) { sb.Append(' ').Append(PhaseNames[i]).Append('=').Append((PhaseMs[i] / Math.Max(1, frames)).ToString("0.00")); PhaseMs[i] = 0; }
            return sb.ToString();
        }

        static void ForEach(Func<NBehaviour, System.Reflection.MethodInfo> pick, object arg)
        {
            orderWalks++;
            try
            {
                int n = ordered.Count;   // objects created during the pass wait for the next frame
                for (int i = 0; i < n; i++)
                {
                    var b = ordered[i];
                    var m = pick(b);
                    if (m == null || !b.Started || !Behaviours.ActiveAndEnabled(b)) continue;
                    Behaviours.Send(b, m, arg);
                }
            }
            finally
            {
                if (--orderWalks == 0 && orderPending.Count > 0)
                {
                    foreach (var b in orderPending) InsertOrdered(b);
                    orderPending.Clear();
                }
            }
        }

        public static void RunStarts()
        {
            while (startQueue.Count > 0)
            {
                var batch = startQueue.ToArray();
                startQueue.Clear();
                foreach (var b in batch)
                {
                    b.StartQueued = false;
                    if (b.Started || b.Destroyed || Doomed(b)) continue;
                    if (!Behaviours.ActiveAndEnabled(b)) continue;   // re-queued when enabled
                    b.Started = true;
                    var r = Behaviours.Send(b, b.Messages.Start);
                    if (b.Messages.StartIsCoroutine && r is IEnumerator e) Coroutines.Start(b, e);
                }
            }
        }

        static void RunInvokes()
        {
            int n = tracked.Count;
            for (int i = 0; i < n; i++)
            {
                var b = tracked[i];
                if (b.Invokes.Count == 0 || b.Destroyed) continue;
                int count = b.Invokes.Count;
                var invokes = System.Buffers.ArrayPool<InvokeState>.Shared.Rent(count);
                b.Invokes.CopyTo(invokes);
                for (int k = 0; k < count; k++)
                {
                    var inv = invokes[k];
                    if (inv.Cancelled || Clock.Time < inv.Due) continue;
                    // Invoke runs on disabled behaviours too, but not inactive GameObjects.
                    if (!b.GameObject.ActiveInHierarchy) continue;
                    var m = Behaviours.FindByName(b, inv.Method, 0);
                    if (inv.Repeat > 0f) inv.Due += inv.Repeat; else inv.Cancelled = true;
                    Behaviours.Send(b, m);
                }
                System.Buffers.ArrayPool<InvokeState>.Shared.Return(invokes, true);
                b.Invokes.RemoveAll(x => x.Cancelled);
            }
        }

        static readonly List<CoroutineState> due = new List<CoroutineState>();
        static readonly Comparison<CoroutineState> byYield = (a, b) => a.YieldSeq.CompareTo(b.YieldSeq);

        /// <summary>
        /// The coroutines whose wait is over, in the order they last yielded across every behaviour,
        /// as Unity's scheduler runs them (each yield queues the coroutine at the end). In start order
        /// per behaviour, Sally Stageplay's phase 2 never came: her level starts a jump coroutine every
        /// other frame, all waiting for her to be idle with the coroutine that starts phase 2, and the
        /// older jumps always took the idle frame first.
        /// </summary>
        static bool IsDue(CoroutineState c, bool fixedPhase, bool endOfFrame)
        {
            if (c.Done || c.WaitOn != null) return false;
            if (fixedPhase != c.FixedWait || endOfFrame != c.EndOfFrameWait) return false;
            // Nothing resumes in the frame (or fixed step) it yielded in, except
            // WaitForEndOfFrame and a WaitForFixedUpdate yielded inside FixedUpdate.
            if (c.FixedWait) { if (Clock.FixedStepCount == c.YieldFixedStep && !c.YieldInFixed) return false; }
            else if (!c.EndOfFrameWait && Clock.FrameCount == c.YieldFrame) return false;
            if (c.Waiting is WaitForSeconds && Clock.Time < c.WakeTime) return false;
            if (c.Waiting is CustomYieldInstruction cy && cy.keepWaiting) return false;
            if (c.Waiting is AsyncOperation op && !Scenes.IsDone(op)) return false;
            return true;
        }

        static void ResumeCoroutines(bool fixedPhase, bool endOfFrame)
        {
            due.Clear();
            int n = tracked.Count;
            for (int i = 0; i < n; i++)
            {
                var cs = tracked[i].Coroutines;
                for (int k = 0; k < cs.Count; k++) if (IsDue(cs[k], fixedPhase, endOfFrame)) due.Add(cs[k]);
            }
            if (due.Count > 1) due.Sort(byYield);
            // A coroutine stopped, or its wait changed, by one that ran before it this pass is looked at again.
            long upTo = yieldSeqNow;
            for (int i = 0; i < due.Count; i++)
            {
                var c = due[i];
                if (c.YieldSeq > upTo || !IsDue(c, fixedPhase, endOfFrame)) continue;
                Coroutines.Step(c);
            }
            due.Clear();
            for (int i = 0; i < n; i++)
            {
                var cs = tracked[i].Coroutines;
                if (cs.Count > 0) cs.RemoveAll(x => x.Done);
            }
        }
        static long yieldSeqNow => Coroutines.YieldSeqNow;

        static void ProcessDestroys()
        {
            for (int i = delayedDestroy.Count - 1; i >= 0; i--)
                if (Clock.Time >= delayedDestroy[i].at)
                {
                    if (!destroyQueue.Contains(delayedDestroy[i].obj)) destroyQueue.Add(delayedDestroy[i].obj);
                    delayedDestroy.RemoveAt(i);
                }
            while (destroyQueue.Count > 0)
            {
                var batch = destroyQueue.ToArray();
                destroyQueue.Clear();
                foreach (var o in batch) Lifecycle.DestroyNow(o);
            }
            // a sweep of every tracked behaviour each frame, only when something was released since
            if (Objects.Releases != releasesSeen)
            {
                releasesSeen = Objects.Releases;
                if (tracked.RemoveAll(b => b.Destroyed) > 0) { trackedSet.RemoveWhere(b => b.Destroyed); ordered.RemoveAll(b => b.Destroyed); }
            }
        }
        static int releasesSeen = -1;

        public static void Untrack(NBehaviour b)
        {
            // removed lazily in ProcessDestroys
        }
    }

    public static class Coroutines
    {
        static long nextHandle = 1;

        public static Coroutine Start(NBehaviour owner, IEnumerator routine)
        {
            if (routine == null) return null;
            var c = new CoroutineState { Owner = owner, Routine = routine };
            c.Handle = new Coroutine();
            c.Handle.m_Ptr = (IntPtr)(nextHandle++);
            handles[c.Handle] = c;
            owner.Coroutines.Add(c);
            PlayerLoop.Track(owner);
            Step(c);
            return c.Handle;
        }

        static readonly Dictionary<Coroutine, CoroutineState> handles = new Dictionary<Coroutine, CoroutineState>(ReferenceEqualityComparer.Instance as IEqualityComparer<Coroutine>);

        public static CoroutineState Of(Coroutine h) => h != null && handles.TryGetValue(h, out var c) ? c : null;

        static long yieldSeq;
        public static long YieldSeqNow => yieldSeq;

        /// <summary>Runs the routine to its next yield and records what it waits for.</summary>
        public static void Step(CoroutineState c)
        {
            if (c.Done) return;
            bool more;
            long t0 = PlayerLoop.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { more = c.Routine.MoveNext(); }
            catch (Exception e) { Exceptions.Report(e, c.Owner, "coroutine " + c.Routine.GetType().Name); more = false; }
            if (t0 != 0) c.ProfTicks += PlayerLoop.SlowCall(t0, c.Routine.GetType(), null, "MoveNext");
            if (!more) { Finish(c); return; }
            object y = c.Routine.Current;
            c.Waiting = y; c.FixedWait = false; c.EndOfFrameWait = false; c.WaitOn = null;
            c.YieldFrame = Clock.FrameCount; c.YieldFixedStep = Clock.FixedStepCount; c.YieldInFixed = Clock.InFixedTimeStep;
            c.YieldSeq = ++yieldSeq;
            switch (y)
            {
                case null: break;
                case WaitForSeconds w: c.WakeTime = Clock.Time + w.m_Seconds; break;
                case WaitForFixedUpdate _: c.FixedWait = true; break;
                case WaitForEndOfFrame _: c.EndOfFrameWait = true; break;
                case Coroutine h:
                    {
                        var inner = Of(h);
                        if (inner != null && !inner.Done) { c.WaitOn = inner; inner.Waiters.Add(c); }
                        break;
                    }
                case IEnumerator nested when !(y is CustomYieldInstruction):
                    {
                        // Unity treats a yielded IEnumerator as a nested coroutine.
                        var inner = new CoroutineState { Owner = c.Owner, Routine = nested, Handle = new Coroutine() };
                        c.Owner.Coroutines.Add(inner);
                        c.WaitOn = inner; inner.Waiters.Add(c);
                        Step(inner);
                        break;
                    }
            }
        }

        static void Finish(CoroutineState c)
        {
            c.Done = true;
            if (c.ProfTicks > 0 && c.Routine != null)
            {
                PlayerLoop.FinishedTicks.TryGetValue(c.Routine.GetType(), out long v);
                PlayerLoop.FinishedTicks[c.Routine.GetType()] = v + c.ProfTicks;
                c.ProfTicks = 0;
            }
            if (c.Handle != null) handles.Remove(c.Handle);
            foreach (var w in c.Waiters)
            {
                w.WaitOn = null;
                // The waiter resumes in the same frame its inner coroutine finished.
                if (!w.Done) Step(w);
            }
            c.Waiters.Clear();
        }

        public static void Stop(CoroutineState c)
        {
            if (c == null || c.Done) return;
            c.Done = true;
            if (c.Handle != null) handles.Remove(c.Handle);
            // Waiters on a stopped coroutine wait forever, as in Unity.
        }

        public static void StopAll(NBehaviour b)
        {
            foreach (var c in b.Coroutines) Stop(c);
        }
    }
}
