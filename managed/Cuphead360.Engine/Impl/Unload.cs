using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>
    /// Unity's UnloadUnusedAssets: after a single scene load (and when the game asks), every loaded
    /// asset and run-time asset (sprite, texture, material, mesh) nothing references any more is let
    /// go, so the next load of it makes a new object. Without it every scene's prefabs, sprites,
    /// clips and textures stayed for good: the fifteenth console run's heap grew from 71 to 168 MB
    /// over nine scenes until the textures had no memory left and the characters went invisible.
    ///
    /// The GC decides what is referenced: for the pass the engine's own strong references to the
    /// candidates (their records, the built-object table) become weak, a full collection runs, and
    /// whatever died is released. A prefab is one asset: its GameObjects and components live or go
    /// together (each member keeps the whole set alive through a ConditionalWeakTable). Assets held
    /// only by other assets that went in this pass go at the next one.
    /// </summary>
    public static class AssetUnload
    {
        static readonly bool off = Environment.GetEnvironmentVariable("CH360_NO_UNLOAD") == "1";
        public static int Released, Passes;
        static int lastFrame = -100000;
        static bool passedThisLoad;

        /// <summary>
        /// A single scene load activated. The game's loader (SceneLoader.load_cr) goes through its
        /// loading scene first, which is what destroys the old scene: the pass runs then, with the
        /// screen black and the hourglass only starting to fade in (a full collection costs about half a
        /// second on the console). The target scene's own activation, with the hourglass up, is then
        /// skipped; a load without the loading scene gets its pass at the target's activation.
        /// </summary>
        public static void SceneActivated(string name)
        {
            if (name == "scene_load_helper") { Run("after " + name); passedThisLoad = true; return; }
            if (passedThisLoad) { passedThisLoad = false; Settle(name); return; }
            Run("after " + name);
        }

        /// <summary>
        /// A level or map just activated, the screen still black: a full, compacting collection, so play
        /// starts without the load's garbage (the build, the warm-up, the activation). Its first gen1 and gen2
        /// collections came in play instead: the twenty-eighth quiet run's Frogs paused 2.3 s for 5 gen1 and
        /// 2 gen2 (60-90 and 300-600 ms each on the console).
        /// </summary>
        static void Settle(string name)
        {
            if (!(name.StartsWith("scene_level_") || name.StartsWith("scene_map_"))) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), heap0 = GC.GetTotalMemory(false);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Log.Line($"GC after {name}: heap {heap0 >> 20} -> {GC.GetTotalMemory(false) >> 20} MB in "
                + $"{(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms");
        }

        /// <summary>
        /// The game's own Resources.UnloadUnusedAssets right after each load (SceneLoader.loop_cr, the
        /// hourglass on screen): the pass of this load already did it.
        /// </summary>
        public static void Requested()
        {
            if (Clock.FrameCount - lastFrame < 1800) return;
            Run("Resources.UnloadUnusedAssets");
        }

        static bool Candidate(NObject r)
        {
            if (r.Destroyed || r.PendingDestroy || r.Managed == null) return false;
            if ((r.Flags & HideFlags.DontUnloadUnusedAsset) != 0) return false;
            var m = r.Managed;
            // Fonts stay: few, and each keeps its parsed outlines and rasterized glyph atlas (FontState),
            // which a reload would build again (glyph text was a second of a console activation).
            if (m is AssetBundle || m is Shader || m is Font) return false;
            if (r.Persistent)
            {
                // Engine-wide assets stay (settings, built-in resources).
                var file = (r as NAsset)?.Source?.File?.Name;
                if (file != null && (file.StartsWith("globalgamemanagers", StringComparison.Ordinal) || file.Contains("unity default resources") || file.Contains("unity_builtin_extra")))
                    return false;
                // Loaded GameObjects and components are prefabs' (scene objects aren't loaded assets here).
                if (r is NGameObject g) return !g.DontDestroy;
                if (r is NComponent c) return c.GameObject != null && c.GameObject.Persistent && !c.GameObject.DontDestroy;
                return true;
            }
            // Run-time assets (Sprite.Create, new Texture2D/Material/Mesh), as Unity unloads them too.
            return (m is Sprite || m is Material || m is Mesh || (m is Texture2D && !(m is RenderTexture)))
                && !(r is NGameObject) && !(r is NComponent);
        }

        public static void Run(string why)
        {
            if (off) return;
            lastFrame = Clock.FrameCount;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long heap0 = GC.GetTotalMemory(false);
            ControllerData.ForgetBuilt();
            RenderList.ForgetScene();
            Physics.ForgetScene();
            var cands = new List<NObject>();
            foreach (var r in Objects.Live())
                if (Candidate(r)) cands.Add(r);

            var units = Units(cands);

            // Animation data only live animators use stays cached (the rest is read again when needed).
            Animators.RetainLiveData();

            var weak = new WeakReference[cands.Count];
            for (int i = 0; i < cands.Count; i++) { cands[i].UnloadCandidate = true; weak[i] = new WeakReference(cands[i].Managed); }
            var aside = AssetDb.WeakenBuilt();
            var weakFiles = AssetDb.WeakenFiles();
            for (int i = 0; i < cands.Count; i++) cands[i].Managed = null;

            // One full collection: weak references clear as soon as their objects are unreachable.
            GC.Collect();
            GC.KeepAlive(units);
            int files = AssetDb.RestoreFiles(weakFiles);   // first: nothing may load a file while the table is empty

            int released = 0;
            for (int i = 0; i < cands.Count; i++)
            {
                var r = cands[i];
                r.UnloadCandidate = false;
                if (weak[i].Target is UObject o) r.Managed = o;
                else { Objects.Unloaded(r); released++; }
            }
            AssetDb.RestoreBuilt(aside);
            Audio.ReleaseUnloaded();   // the host mixer's copies of clips that went
            Atlases.Prune();
            Released += released;
            Passes++;
            long heap1 = GC.GetTotalMemory(false);
            Log.Line($"UNLOAD {why}: {released} of {cands.Count} assets let go in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms; "
                + $"heap {heap0 >> 20} -> {heap1 >> 20} MB, live objects {Objects.LiveCount}; {files} converted files no longer read let go");
        }

        /// <summary>Prefabs: each member keeps the whole hierarchy alive (in a method of its own, so no local holds one).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ConditionalWeakTable<UObject, object[]> Units(List<NObject> cands)
        {
            var units = new ConditionalWeakTable<UObject, object[]>();
            foreach (var r in cands)
            {
                if (!(r is NGameObject g) || g.Transform == null || g.Transform.Parent != null) continue;
                var members = new List<UObject>();
                Collect(g.Transform, members);
                var holder = members.ToArray();
                foreach (var m in holder) units.AddOrUpdate(m, holder);
            }
            return units;
        }

        static void Collect(NTransform t, List<UObject> into)
        {
            var go = t.GameObject;
            if (go?.Managed != null) into.Add(go.Managed);
            if (go != null)
                foreach (var c in go.Components)
                    if (c.Managed != null) into.Add(c.Managed);
            foreach (var child in t.Children) Collect(child, into);
        }
    }
}
