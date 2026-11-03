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
        public bool YieldInFixed;
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

        public static void Track(NBehaviour b)
        {
            if (trackedSet.Add(b)) tracked.Add(b);
        }

        public static void QueueStart(NBehaviour b) => startQueue.Add(b);

        public static void QueueDestroy(NObject o, float delay)
        {
            if (delay > 0f) delayedDestroy.Add((o, Clock.Time + delay));
            else if (!destroyQueue.Contains(o)) destroyQueue.Add(o);
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
            P(7); if (Capture.Wanted(Clock.FrameCount)) Capture.Write(Clock.FrameCount, RenderList.Build());
            RenderFrame?.Invoke();
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
        /// <summary>With Profile on, a script call over 50 ms is logged with its type and method.</summary>
        public static void SlowCall(long start, string type, string method)
        {
            long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            if (ms >= 50) Log.Line($"SLOW call {type}.{method}: {ms} ms (frame {Clock.FrameCount})");
        }

        public static string ProfileReport(int frames)
        {
            var sb = new System.Text.StringBuilder("PROFILE ms/frame:");
            for (int i = 0; i < PhaseMs.Length; i++) { sb.Append(' ').Append(PhaseNames[i]).Append('=').Append((PhaseMs[i] / Math.Max(1, frames)).ToString("0.00")); PhaseMs[i] = 0; }
            return sb.ToString();
        }

        static void ForEach(Func<NBehaviour, System.Reflection.MethodInfo> pick, object arg)
        {
            int n = tracked.Count;   // objects created during the pass wait for the next frame
            for (int i = 0; i < n; i++)
            {
                var b = tracked[i];
                var m = pick(b);
                if (m == null || !b.Started || !Behaviours.ActiveAndEnabled(b)) continue;
                Behaviours.Send(b, m, arg);
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
                    if (b.Started || b.Destroyed) continue;
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

        static void ResumeCoroutines(bool fixedPhase, bool endOfFrame)
        {
            int n = tracked.Count;
            for (int i = 0; i < n; i++)
            {
                var b = tracked[i];
                if (b.Coroutines.Count == 0) continue;
                int count = b.Coroutines.Count;
                var routines = System.Buffers.ArrayPool<CoroutineState>.Shared.Rent(count);
                b.Coroutines.CopyTo(routines);
                for (int k = 0; k < count; k++)
                {
                    var c = routines[k];
                    if (c.Done || c.WaitOn != null) continue;
                    if (fixedPhase != c.FixedWait || endOfFrame != c.EndOfFrameWait) continue;
                    // Nothing resumes in the frame (or fixed step) it yielded in, except
                    // WaitForEndOfFrame and a WaitForFixedUpdate yielded inside FixedUpdate.
                    if (c.FixedWait) { if (Clock.FixedStepCount == c.YieldFixedStep && !c.YieldInFixed) continue; }
                    else if (!c.EndOfFrameWait && Clock.FrameCount == c.YieldFrame) continue;
                    if (c.Waiting is WaitForSeconds && Clock.Time < c.WakeTime) continue;
                    if (c.Waiting is CustomYieldInstruction cy && cy.keepWaiting) continue;
                    if (c.Waiting is AsyncOperation op && !Scenes.IsDone(op)) continue;
                    Coroutines.Step(c);
                }
                System.Buffers.ArrayPool<CoroutineState>.Shared.Return(routines, true);
                b.Coroutines.RemoveAll(x => x.Done);
            }
        }

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
            if (tracked.RemoveAll(b => b.Destroyed) > 0) trackedSet.RemoveWhere(b => b.Destroyed);
        }

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

        /// <summary>Runs the routine to its next yield and records what it waits for.</summary>
        public static void Step(CoroutineState c)
        {
            if (c.Done) return;
            bool more;
            long t0 = PlayerLoop.Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { more = c.Routine.MoveNext(); }
            catch (Exception e) { Exceptions.Report(e, c.Owner, "coroutine " + c.Routine.GetType().Name); more = false; }
            if (t0 != 0) PlayerLoop.SlowCall(t0, c.Routine.GetType().FullName, "MoveNext");
            if (!more) { Finish(c); return; }
            object y = c.Routine.Current;
            c.Waiting = y; c.FixedWait = false; c.EndOfFrameWait = false; c.WaitOn = null;
            c.YieldFrame = Clock.FrameCount; c.YieldFixedStep = Clock.FixedStepCount; c.YieldInFixed = Clock.InFixedTimeStep;
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
