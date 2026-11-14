using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>Scene loading from the converted build scenes (levelN.chob).</summary>
    public static class SceneLoader
    {
        static readonly bool traceMem = Environment.GetEnvironmentVariable("CH360_TRACE_MEM") == "1";
        /// <summary>Log each load's time and the converted files it read (the console host sets it).</summary>
        public static bool Timing = Environment.GetEnvironmentVariable("CH360_SCENE_TIMING") == "1";
        static readonly bool MidLoadCollect = Environment.GetEnvironmentVariable("CH360_LOAD_GC") != "0";
        /// <summary>
        /// The heap a build or an activation may reach before it is collected (bytes; CH360_LOAD_HEAP_MB, 0: never, the
        /// default). The console's player memory steps up 16 MB when the heap outgrows it and never comes back: the
        /// thirty-ninth run's Frogs build reached 77 MB reading its animator controllers and the player grew 135 -> 151 MB
        /// (Forest Follies' end of build at 73: 118 -> 134). A cap of 62 MB took 38 collections in the Frogs' build on
        /// the PC (some 26 s on the console) and still reached 78 MB: what a level's build keeps alive (the base ~46 MB,
        /// the warmed controllers' data) is that much. The peaks are logged either way.
        /// </summary>
        static readonly long LoadHeapCap = (long.TryParse(Environment.GetEnvironmentVariable("CH360_LOAD_HEAP_MB"), out var capMb) ? capMb : 0) << 20;

        /// <summary>
        /// A scene's activation in progress (SceneLoad.Activate): each Instantiate (Clone) keeps the heap under LoadHeapCap
        /// too. The map's activations peaked at 70-80 MB (hw39: 50 -> 80 MB over its 17 instantiations, its unload pass
        /// back to 50), above any level build's, and the thirty-eighth run's player grew 135 -> 151 MB at one.
        /// </summary>
        static bool activating;
        internal static bool Activating => activating;
        static long actCap, actMs, actPeak, actLast;
        static int actCollects;
        static string actLastName;
        static readonly Dictionary<string, long> actGrowth = new Dictionary<string, long>();
        /// <summary>A scene activated (its name): the console host's learned read-ahead begins for it.</summary>
        public static Action<string> Activated;

        // The heap's growth since the last instantiation (or the activation's start), put to that one's name: what an
        // activation's peak is made of (the map's went 50 -> 76-80 MB, the unload pass back to 50).
        static void ActGrowth(long h, string next)
        {
            if (actLastName != null && h > actLast) actGrowth[actLastName] = (actGrowth.TryGetValue(actLastName, out var g) ? g : 0) + (h - actLast);
            actLast = h;
            actLastName = next;
        }

        static string ActGrowthTop()
        {
            var list = new List<KeyValuePair<string, long>>(actGrowth);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < 6; i++)
                sb.Append(i > 0 ? ", " : "").Append(list[i].Key).Append(" +").Append((list[i].Value / 1048576.0).ToString("0.0")).Append(" MB");
            return sb.ToString();
        }

        internal static void HeapCheck(string name = null)
        {
            if (!activating) return;
            long h = GC.GetTotalMemory(false);
            ActGrowth(h, name ?? "?");
            if (h > actPeak) actPeak = h;
            if (!MidLoadCollect || LoadHeapCap <= 0 || h < actCap) return;
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            GC.Collect();
            actMs += Ms(c0, System.Diagnostics.Stopwatch.GetTimestamp());
            actCollects++;
            long after = GC.GetTotalMemory(false);
            if (after > actCap - (4L << 20)) actCap = after + (4L << 20);
        }
        sealed class Pending
        {
            public int BuildIndex;
            public bool Additive, Sync;
            public Scenes.Operation Op;
            public int RequestFrame;
            public SceneLoad Work;   // a load built a slice a frame (SliceMs)
        }

        /// <summary>
        /// Milliseconds of a scene's building a frame, 0 to build it in one go. The console host
        /// sets it (CH360_LOAD_SLICE_MS for a PC check): built in one frame, a level's load held
        /// the loading screen's hourglass still for seconds.
        /// </summary>
        public static int SliceMs = int.TryParse(Environment.GetEnvironmentVariable("CH360_LOAD_SLICE_MS"), out var sliceMs) ? sliceMs : 0;

        /// <summary>
        /// A scene built over frames, as Unity's asynchronous loads are: its file, its GameObjects
        /// and components, their hierarchy and fields, SliceMs of work a frame while the current
        /// scene (the loading screen) goes on; then, in one frame, the old scene unloaded and the
        /// new one activated (Awake, OnEnable). Until then its objects are Building (inactive to
        /// everything) and its scene not loaded.
        /// </summary>
        sealed class SceneLoad
        {
            public int Index;
            public bool Additive;
            public float Progress;
            string name, path;
            ChobFile file;
            Scenes.NScene scene;
            SceneBuild build;
            List<ChobObject> gameObjects;
            int stage, next, frames, warmed0, prepBest, prepSprites, prepClips, prepLeft, capCollects;
            long t0, tWarm, tPrep, tReady, files0, bytes0, prepAt, heap0, collectMs, heapBefore, heapAfter, capNow, capMs, heapPeak, dropT0;
            int dropC0;

            public static SceneLoad Begin(int index, bool additive)
            {
                ControllerData.NewLoad();
                var l = new SceneLoad { Index = index, Additive = additive, path = AssetDb.BuildScenes[index] };
                l.name = System.IO.Path.GetFileNameWithoutExtension(l.path);
                l.t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                l.files0 = ChobFile.LoadedFiles;
                l.bytes0 = ChobFile.LoadedBytes;
                l.heap0 = GC.GetTotalMemory(false);
                l.dropC0 = ChobObject.DropCollects;
                l.dropT0 = ChobObject.DropCollectTicks;
                return l;
            }

            /// <summary>Builds until done (true) or until ms have gone by (false: more next frame).</summary>
            public bool Step(int ms)
            {
                ChobObject.LoadPhase = true;
                try { return StepCore(ms); }
                finally { ChobObject.LoadPhase = false; }
            }

            bool StepCore(int ms)
            {
                long hz = System.Diagnostics.Stopwatch.Frequency;
                long deadline = ms <= 0 ? long.MaxValue : System.Diagnostics.Stopwatch.GetTimestamp() + ms * hz / 1000;
                bool Over() => System.Diagnostics.Stopwatch.GetTimestamp() > deadline;
                frames++;
                while (true)
                {
                    switch (stage)
                    {
                        case 0:
                            file = AssetDb.File("level" + Index);
                            if (file == null) { Log.Line("SCENE level" + Index + " (" + name + ") is not converted"); stage = 9; return true; }
                            scene = Scenes.Create(name, Index, building: true);
                            scene.Path = path;
                            build = new SceneBuild { File = file, SceneHandle = scene.Handle, Building = true };
                            gameObjects = file.Ordered.Where(o => o.Type == "GameObject").ToList();
                            stage = 1; next = 0;
                            if (Over()) return false;
                            break;
                        case 1:
                            while (next < gameObjects.Count)
                            {
                                build.CreateOne(gameObjects[next++]);
                                KeepHeapDown();
                                if (Over()) { Progress = 0.6f * next / gameObjects.Count; return false; }
                            }
                            stage = 2;
                            break;
                        case 2:
                            build.FinishHierarchy();
                            stage = 3; next = 0;
                            if (Over()) { Progress = 0.6f; return false; }
                            break;
                        case 3:
                            while (next < build.Components.Count)
                            {
                                build.ApplyFields(next++);
                                KeepHeapDown();
                                if (Over()) { Progress = 0.6f + 0.4f * next / build.Components.Count; return false; }
                            }
                            stage = 4;
                            tWarm = System.Diagnostics.Stopwatch.GetTimestamp();
                            warmed0 = ControllerData.Warmed;
                            if (Over()) return false;
                            break;
                        case 4:
                            // The animator controllers the build resolved (its prefabs'), read while the
                            // loading screen is up: their first spawns read them in a frame of play. Levels
                            // only (ControllerData.WarmScene): the map's raised its heap 68 -> 96 MB.
                            if (!ControllerData.WarmScene(name)) ControllerData.ForgetBuilt();
                            else while (ControllerData.WarmNext())
                            {
                                KeepHeapDown();
                                if (Over()) return false;
                            }
                            Audio.PreloadFresh();
                            stage = 5;
                            tPrep = prepAt = System.Diagnostics.Stopwatch.GetTimestamp();
                            prepBest = int.MaxValue;
                            if (Prepared(name)) (prepSprites, prepClips) = RenderList.ReadAheadLoad(scene.Handle, fill: name.StartsWith("scene_level_"));
                            CollectBuild();
                            if (Over()) return false;
                            break;
                        case 5:
                            {
                                // Their pages read while the hourglass turns: until none is coming, one second
                                // without progress (memory holds the rest back), or eight in all.
                                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                                prepLeft = RenderList.TexturesPending?.Invoke() ?? 0;
                                if (prepLeft < prepBest) { prepBest = prepLeft; prepAt = now; }
                                if (prepLeft > 0 && now - prepAt < hz && now - tPrep < 8 * hz) return false;
                                stage = 6;
                                tReady = now;
                                Progress = 1f;
                                return true;
                            }
                        default:
                            return true;
                    }
                }
            }

            /// <summary>
            /// The build's garbage collected while its pages are read (stage 5 waits for them anyway; the hourglass
            /// is drawn again meanwhile): the heap peaked at the activation with the build's garbage still in it
            /// (79-95 MB against 47-57 after its collection), and the console's player grew 135 -> 151 MB at a map's
            /// load, 16 MB the textures never got back (Forest Follies after it: 169 textures loaded on demand
            /// against 64 at 135, the thirty-eighth run). Only when the build grew the heap by 8 MB; CH360_LOAD_GC=0:
            /// none (a PC check).
            /// </summary>
            /// <summary>
            /// The build's garbage collected whenever the heap reaches LoadHeapCap (the hourglass is drawn again
            /// meanwhile), so the build never outgrows the player's memory; when what it keeps is that much already,
            /// the next collection waits for 4 MB more.
            /// </summary>
            void KeepHeapDown()
            {
                long h = GC.GetTotalMemory(false);
                if (h > heapPeak) heapPeak = h;
                if (!MidLoadCollect || LoadHeapCap <= 0) return;
                if (capNow == 0) capNow = LoadHeapCap;
                if (h < capNow) return;
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                GC.Collect();
                capMs += Ms(c0, System.Diagnostics.Stopwatch.GetTimestamp());
                capCollects++;
                long after = GC.GetTotalMemory(false);
                if (after > capNow - (4L << 20)) capNow = after + (4L << 20);
            }

            void CollectBuild()
            {
                heapBefore = GC.GetTotalMemory(false);
                if (heapBefore > heapPeak) heapPeak = heapBefore;
                if (!MidLoadCollect || heapBefore - heap0 < (8L << 20)) return;
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                GC.Collect();
                collectMs = Ms(c0, System.Diagnostics.Stopwatch.GetTimestamp());
                heapAfter = GC.GetTotalMemory(false);
            }

            /// <summary>The old scene unloaded (a single load), the new one loaded and activated.</summary>
            public void Activate()
            {
                if (stage == 9) return;
                activating = true;
                ChobObject.LoadPhase = true;
                int dc0 = ChobObject.DropCollects;
                long dt0 = ChobObject.DropCollectTicks;
                actCap = LoadHeapCap;
                actMs = actPeak = 0;
                actCollects = 0;
                actGrowth.Clear();
                actLast = GC.GetTotalMemory(false);
                long actStart = actLast;
                actLastName = "(unloading the old scene)";
                if (!Additive) UnloadAll();
                ActGrowth(GC.GetTotalMemory(false), "(Awake and OnEnable before any instantiation)");
                scene.Building = false;
                scene.Loaded = true;
                if (!Additive) { Scenes.ActiveHandle = scene.Handle; Clock.TimeSinceLevelLoadBase = Clock.Time; }
                Loads++;
                Log.Line($"SCENE loaded {name} (build {Index}): {build.GameObjects.Count} GameObjects, {build.Components.Count} components");
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                long calls0 = Clone.Calls, copied0 = Clone.Copied, tree0 = Clone.TreeTicks, fields0 = Clone.FieldTicks;
                long bind0 = Behaviours.BindTicks, objs0 = Behaviours.BoundObjects, thunks0 = Behaviours.BoundThunks;
                build.Activate();
                long actEnd = GC.GetTotalMemory(false);
                ActGrowth(actEnd, null);
                activating = false;
                ChobObject.LoadPhase = false;
                if (Timing) Log.Line($"SCENE {name} activation's heap: {actStart >> 20} -> {actEnd >> 20} MB (at most {Math.Max(actPeak, actEnd) >> 20}); grown by {ActGrowthTop()}"
                    + $"; trees let go collected {ChobObject.DropCollects - dc0} times ({Ms(dt0, ChobObject.DropCollectTicks)} ms)");
                if (!Additive) AssetUnload.SceneActivated(name);
                if (Timing)
                {
                    Log.Line($"SCENE {name}: built in {Ms(t0, tReady)} ms over {frames} frames ({ChobFile.LoadedFiles - files0} files read, "
                        + $"{(ChobFile.LoadedBytes - bytes0) >> 20} MB; {ControllerData.Warmed - warmed0} animator controllers read in {Ms(tWarm, tPrep)} ms; "
                        + $"pages read ahead for {prepSprites} sprites and {prepClips} clips"
                        + (name.StartsWith("scene_level_") ? $" (and the stored bytes of {RenderList.Filled} sprites' pages)" : "")
                        + $" in {Ms(tPrep, tReady)} ms, {prepLeft} still coming; "
                        + (capCollects > 0 ? $"the heap kept near {LoadHeapCap >> 20} MB by {capCollects} collections in {capMs} ms, at most {heapPeak >> 20} MB; " : $"the heap at most {heapPeak >> 20} MB; ")
                        + $"trees let go collected {ChobObject.DropCollects - dropC0} times in {Ms(dropT0, ChobObject.DropCollectTicks)} ms; "
                        + (collectMs > 0 ? $"the build's garbage collected while they were read: heap {heapBefore >> 20} -> {heapAfter >> 20} MB in {collectMs} ms), " : $"heap {heapBefore >> 20} MB before activation), ")
                        + $"activated in {Ms(t1, System.Diagnostics.Stopwatch.GetTimestamp())} ms "
                        + (actCollects > 0 ? $"(its heap kept near {LoadHeapCap >> 20} MB by {actCollects} collections in {actMs} ms, at most {actPeak >> 20} MB) " : $"(its heap at most {actPeak >> 20} MB) ")
                        + $"({Instantiated(calls0, copied0, tree0, fields0, bind0, objs0, thunks0)})");
                    MemoryLine();
                }
                AfterLoad(scene, Index, Additive);
                if (!Additive) Activated?.Invoke(name);
            }
        }
        static readonly List<Pending> pending = new List<Pending>();
        public static int Loads;
        /// <summary>A scene load asked for and not yet activated.</summary>
        public static bool Loading => pending.Count > 0;

        /// <summary>
        /// Whether a scene's first frames are prepared behind the loader's black: its pages read ahead (its
        /// load's last stage) and, once it is active, the game held until its frames draw without waiting
        /// (ConsoleHost.LevelHold). Every scene that comes in from black: levels, maps, the slot select, the
        /// win screen, cutscenes. Only levels and maps were (the slot select read its fonts and pages as it
        /// faded in, a 250 ms frame in every run). Not the boot's scenes (ConsoleHost.BootHold) nor the
        /// loader's empty one.
        /// </summary>
        public static bool Prepared(string name) =>
            name != null && name != "scene_start" && name != "scene_title" && name != "scene_load_helper";

        public static int IndexOf(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return -1;
            for (int i = 0; i < AssetDb.BuildScenes.Length; i++)
            {
                string p = AssetDb.BuildScenes[i];
                string n = System.IO.Path.GetFileNameWithoutExtension(p);
                if (string.Equals(p, nameOrPath, StringComparison.OrdinalIgnoreCase) || string.Equals(n, nameOrPath, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public static AsyncOperation Request(int index, bool additive, bool sync)
        {
            if (index < 0 || index >= AssetDb.BuildScenes.Length)
            {
                Log.Line("Scene with build index " + index + " couldn't be loaded because it has not been added to the build settings");
                return null;
            }
            var op = new Scenes.Operation();
            pending.Add(new Pending { BuildIndex = index, Additive = additive, Sync = sync, Op = op, RequestFrame = Clock.FrameCount });
            return Scenes.NewOperation(op);
        }

        /// <summary>
        /// The console's loads (ConsoleHost): a full, compacting collection whenever the heap grew this much
        /// since the last one while a scene builds, and one after its activation, under the loading screen.
        /// The player's memory only grows (wasm), and each load's peak set it for good: 102 -> 151 -> 167 ->
        /// 183 -> 215 MB over the twenty-third run, the textures squeezed from 109 MB to 41 and refused.
        /// </summary>
        public static long CollectEveryBytes;
        static long collectAt;

        static void CollectIfGrown(bool always)
        {
            if (CollectEveryBytes <= 0) return;
            if (!always && GC.GetTotalMemory(false) < collectAt) return;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            collectAt = GC.GetTotalMemory(false) + CollectEveryBytes;
        }

        /// <summary>Called at the start of a frame: loads requested in an earlier frame complete now.</summary>
        public static void Pump()
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                if (p.RequestFrame >= Clock.FrameCount) continue;
                // Built over frames (the first load waiting at a time), unless asked to load at once.
                if (SliceMs > 0 && !p.Sync)
                {
                    if (i > 0 && pending[0].Work != null) continue;
                    if (p.Work == null && CollectEveryBytes > 0) collectAt = GC.GetTotalMemory(false) + CollectEveryBytes;
                    p.Work ??= SceneLoad.Begin(p.BuildIndex, p.Additive);
                    bool built = p.Work.Step(SliceMs);
                    CollectIfGrown(false);
                    if (!built) { p.Op.Progress = 0.9f * p.Work.Progress; continue; }
                }
                if (!p.Op.AllowActivation) { p.Op.Progress = 0.9f; continue; }
                pending.RemoveAt(i--);
                if (p.Work != null) { p.Work.Activate(); CollectIfGrown(true); }
                else Load(p.BuildIndex, p.Additive);
                p.Op.Progress = 1f;
                p.Op.Done = true;
                p.Op.Complete?.Invoke();
            }
        }

        public static void Load(int index, bool additive)
        {
            string path = AssetDb.BuildScenes[index];
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), files0 = ChobFile.LoadedFiles, bytes0 = ChobFile.LoadedBytes;
            ControllerData.NewLoad();
            var file = AssetDb.File("level" + index);
            if (file == null) { Log.Line("SCENE level" + index + " (" + name + ") is not converted"); return; }
            if (!additive) UnloadAll();
            var scene = Scenes.Create(name, index);
            scene.Path = path;
            if (!additive) { Scenes.ActiveHandle = scene.Handle; Clock.TimeSinceLevelLoadBase = Clock.Time; }
            var build = SceneBuild.Scene(file, scene.Handle);
            if (!ControllerData.WarmScene(name)) ControllerData.ForgetBuilt();
            else while (ControllerData.WarmNext()) { }   // as the sliced loads do (SceneLoad's stage 4)
            Audio.PreloadFresh(restNow: true);   // the scene before is gone already (UnloadAll above)
            Loads++;
            Log.Line($"SCENE loaded {name} (build {index}): {build.GameObjects.Count} GameObjects, {build.Components.Count} components"
                + (traceMem ? $"; heap {GC.GetTotalMemory(true) >> 20} MB, files read {ChobFile.LoadedFiles} ({ChobFile.LoadedBytes >> 20} MB), live objects {Objects.LiveCount}" : ""));
            if (traceMem) Log.Line(ChobObject.Census(AssetDb.LoadedFiles) + $"; trees dropped {ChobObject.TreesDropped}, parsed again {ChobObject.TreesReparsed}");
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            long calls0 = Clone.Calls, copied0 = Clone.Copied, tree0 = Clone.TreeTicks, fields0 = Clone.FieldTicks;
            long bind0 = Behaviours.BindTicks, objs0 = Behaviours.BoundObjects, thunks0 = Behaviours.BoundThunks;
            build.Activate();
            if (!additive) AssetUnload.SceneActivated(name);
            if (Timing)
            {
                Log.Line($"SCENE {name}: built in {Ms(t0, t1)} ms ({ChobFile.LoadedFiles - files0} files read, {(ChobFile.LoadedBytes - bytes0) >> 20} MB), "
                    + $"activated in {Ms(t1, System.Diagnostics.Stopwatch.GetTimestamp())} ms ({Instantiated(calls0, copied0, tree0, fields0, bind0, objs0, thunks0)})");
                MemoryLine();
            }
            AfterLoad(scene, index, additive);
        }

        /// <summary>The managed callbacks the native player makes after a load.</summary>
        static void AfterLoad(Scenes.NScene scene, int index, bool additive)
        {
            var mode = additive ? LoadSceneMode.Additive : LoadSceneMode.Single;
            var s = new Scene { m_Handle = scene.Handle };
            Invoke(typeof(SceneManager), "Internal_SceneLoaded", s, mode);
            if (!additive) Invoke(typeof(SceneManager), "Internal_ActiveSceneChanged", default(Scene), s);
            if (!additive)
                foreach (var b in Typed.Behaviours.ToArray())
                    if (b.Messages.OnLevelWasLoaded != null && Behaviours.ActiveAndEnabled(b)) Behaviours.Send(b, b.Messages.OnLevelWasLoaded, index);
        }

        static long Ms(long a, long b) => (b - a) * 1000 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>What an activation instantiated since the counters were read (the scene timing lines).</summary>
        static string Instantiated(long calls0, long copied0, long tree0, long fields0, long bind0, long objs0, long thunks0) =>
            $"{Clone.Calls - calls0} instantiated, {Clone.Copied - copied0} objects: copies made in {Ms(tree0, Clone.TreeTicks)} ms, "
            + $"their state copied in {Ms(fields0, Clone.FieldTicks)} ms; classes copied so far by generated copies {Serialization.FastClasses}, by reflection {Serialization.SlowClasses}; "
            + $"messages bound in {Ms(bind0, Behaviours.BindTicks)} ms ({Behaviours.BoundThunks - thunks0} thunks, {Behaviours.BoundObjects - objs0} delegates for single objects)";

        /// <summary>
        /// With Timing (the console), what the heap holds after a load: its size (no collection
        /// forced), live objects, and the converted files' bytes and parsed trees by type. The
        /// player's memory grew 101 -> 216 MB over the twelfth run.
        /// </summary>
        static void MemoryLine()
        {
            Log.Line($"MEMORY after the load: heap {GC.GetTotalMemory(false) >> 20} MB, live objects {Objects.LiveCount}, files loaded {AssetDb.LoadedFiles.Count()}; "
                + ChobObject.Census(AssetDb.LoadedFiles));
            Log.Line(Objects.Census(14));
        }

        static void UnloadAll()
        {
            foreach (var sc in Scenes.All.ToArray())
            {
                if (!sc.Loaded || sc.Building) continue;
                foreach (var root in sc.Roots.ToArray()) Lifecycle.DestroyNow(root);
                sc.Loaded = false;
                Invoke(typeof(SceneManager), "Internal_SceneUnloaded", new Scene { m_Handle = sc.Handle });
            }
            Scenes.DropUnloaded();
        }

        static void Invoke(Type t, string method, params object[] args)
        {
            var m = t.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) return;
            try { m.Invoke(null, args); }
            catch (TargetInvocationException e) { Exceptions.Report(e.InnerException, null, method); }
        }
    }

    public static partial class Icalls
    {
        static Scenes.NScene SceneOf(int h) => Scenes.ByHandle(h);

        public static bool UnityEngine_SceneManagement_Scene__IsValidInternal(int sceneHandle) => SceneOf(sceneHandle) != null;
        public static string UnityEngine_SceneManagement_Scene__GetPathInternal(int sceneHandle) => SceneOf(sceneHandle)?.Path ?? "";
        public static string UnityEngine_SceneManagement_Scene__GetNameInternal(int sceneHandle) => SceneOf(sceneHandle)?.Name ?? "";
        public static void UnityEngine_SceneManagement_Scene__SetNameInternal(int sceneHandle, string name) { var s = SceneOf(sceneHandle); if (s != null) s.Name = name; }
        public static string UnityEngine_SceneManagement_Scene__GetGUIDInternal(int sceneHandle) => "";
        public static bool UnityEngine_SceneManagement_Scene__GetIsLoadedInternal(int sceneHandle) => SceneOf(sceneHandle)?.Loaded ?? false;
        public static Scene.LoadingState UnityEngine_SceneManagement_Scene__GetLoadingStateInternal(int sceneHandle)
            => SceneOf(sceneHandle)?.Loaded == true ? Scene.LoadingState.Loaded : Scene.LoadingState.NotLoaded;
        public static bool UnityEngine_SceneManagement_Scene__GetIsDirtyInternal(int sceneHandle) => false;
        public static int UnityEngine_SceneManagement_Scene__GetBuildIndexInternal(int sceneHandle) => SceneOf(sceneHandle)?.BuildIndex ?? -1;
        public static int UnityEngine_SceneManagement_Scene__GetRootCountInternal(int sceneHandle) => SceneOf(sceneHandle)?.Roots.Count ?? 0;
        public static void UnityEngine_SceneManagement_Scene__GetRootGameObjectsInternal(int sceneHandle, object resultRootList)
        {
            if (resultRootList is IList l) { l.Clear(); var s = SceneOf(sceneHandle); if (s != null) foreach (var r in s.Roots) l.Add(r.Managed); }
        }

        public static int UnityEngine_SceneManagement_SceneManager__get_sceneCount() => Scenes.All.Count(s => s.Loaded);
        public static int UnityEngine_SceneManagement_SceneManager__get_sceneCountInBuildSettings() => AssetDb.BuildScenes.Length;
        public static void UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_GetActiveScene(out Scene value) => value = new Scene { m_Handle = Scenes.ActiveHandle };
        public static bool UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_SetActiveScene(ref Scene scene)
        {
            var s = SceneOf(scene.m_Handle);
            if (s == null || !s.Loaded) return false;
            var old = new Scene { m_Handle = Scenes.ActiveHandle };
            Scenes.ActiveHandle = s.Handle;
            return true;
        }
        public static void UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_GetSceneByPath(string scenePath, out Scene value)
            => value = new Scene { m_Handle = Scenes.All.FirstOrDefault(s => s.Loaded && string.Equals(s.Path, scenePath, StringComparison.OrdinalIgnoreCase))?.Handle ?? 0 };
        public static void UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_GetSceneByName(string name, out Scene value)
            => value = new Scene { m_Handle = Scenes.All.FirstOrDefault(s => s.Loaded && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Handle ?? 0 };
        public static void UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_GetSceneByBuildIndex(int buildIndex, out Scene value)
            => value = new Scene { m_Handle = Scenes.All.FirstOrDefault(s => s.Loaded && s.BuildIndex == buildIndex)?.Handle ?? 0 };
        public static void UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_GetSceneAt(int index, out Scene value)
        {
            var loaded = Scenes.All.Where(s => s.Loaded).ToList();
            value = new Scene { m_Handle = index >= 0 && index < loaded.Count ? loaded[index].Handle : 0 };
        }
        public static AsyncOperation UnityEngine_SceneManagement_SceneManager__LoadSceneAsyncNameIndexInternal(string sceneName, int sceneBuildIndex, bool isAdditive, bool mustCompleteNextFrame)
        {
            int index = sceneBuildIndex >= 0 ? sceneBuildIndex : SceneLoader.IndexOf(sceneName);
            return SceneLoader.Request(index, isAdditive, mustCompleteNextFrame);
        }
        public static void UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_CreateScene(string sceneName, out Scene value)
            => value = new Scene { m_Handle = Scenes.Create(sceneName, -1).Handle };
        public static void UnityEngine_SceneManagement_SceneManager__INTERNAL_CALL_MoveGameObjectToScene(GameObject go, ref Scene scene)
        {
            var g = Engine.Objects.GO(go);
            if (g == null || g.Transform.Parent != null) return;
            Scenes.RemoveRoot(g);
            g.Scene = scene.m_Handle; g.DontDestroy = false;
            Scenes.AddRoot(g);
        }

        public static void UnityEngine_AsyncOperation__InternalDestroy(AsyncOperation self) { }
        public static bool UnityEngine_AsyncOperation__get_isDone(AsyncOperation self) => Scenes.IsDone(self);
        public static float UnityEngine_AsyncOperation__get_progress(AsyncOperation self) => Scenes.Op(self)?.Progress ?? 1f;
        public static int UnityEngine_AsyncOperation__get_priority(AsyncOperation self) => Scenes.Op(self)?.Priority ?? 0;
        public static void UnityEngine_AsyncOperation__set_priority(AsyncOperation self, int value) { var o = Scenes.Op(self); if (o != null) o.Priority = value; }
        public static bool UnityEngine_AsyncOperation__get_allowSceneActivation(AsyncOperation self) => Scenes.Op(self)?.AllowActivation ?? true;
        public static void UnityEngine_AsyncOperation__set_allowSceneActivation(AsyncOperation self, bool value) { var o = Scenes.Op(self); if (o != null) o.AllowActivation = value; }

        // ------------------------------------------------------------ Resources

        public static UObject UnityEngine_Resources__Load(string path, Type systemTypeInstance)
        {
            if (path == null) return null;
            string key = path.Replace('\\', '/').ToLowerInvariant();
            var ggm = AssetDb.File("globalgamemanagers");
            foreach (var kv in AssetDb.ResourceList)
            {
                if (!string.Equals(kv.Key, key, StringComparison.Ordinal)) continue;
                var o = AsType(AssetDb.Resolve(ggm, kv.Value), systemTypeInstance);
                if (o != null) return o;
            }
            return null;
        }
        public static UObject[] UnityEngine_Resources__LoadAll(string path, Type systemTypeInstance)
        {
            string key = (path ?? "").Replace('\\', '/').ToLowerInvariant().TrimEnd('/');
            var ggm = AssetDb.File("globalgamemanagers");
            var r = new List<UObject>();
            foreach (var kv in AssetDb.ResourceList)
            {
                if (!(key.Length == 0 || kv.Key == key || kv.Key.StartsWith(key + "/", StringComparison.Ordinal))) continue;
                var o = AsType(AssetDb.Resolve(ggm, kv.Value), systemTypeInstance);
                if (o != null) r.Add(o);
            }
            var arr = (UObject[])Array.CreateInstance(systemTypeInstance ?? typeof(UObject), r.Count);
            for (int i = 0; i < r.Count; i++) arr[i] = r[i];
            return arr;
        }
        /// <summary>Resources.Load&lt;T&gt; with a component type answers that component of the prefab.</summary>
        static UObject AsType(UObject o, Type t)
        {
            if (o == null || t == null || t.IsInstanceOfType(o)) return o;
            if (o is GameObject go && typeof(Component).IsAssignableFrom(t)) return Find.Component(Engine.Objects.GO(go), t);
            return null;
        }

        public static ResourceRequest UnityEngine_Resources__LoadAsyncInternal(string path, Type type)
        {
            // ResourceRequest.asset is Resources.Load(m_Path, m_Type); the load itself is synchronous.
            var req = new ResourceRequest { m_Path = path, m_Type = type };
            var op = Scenes.NewOperation(new Scenes.Operation { Done = true, Progress = 1 });
            req.m_Ptr = op.m_Ptr;
            return req;
        }
        public static UObject[] UnityEngine_Resources__FindObjectsOfTypeAll(Type type) => Find.ObjectsOfType(type, true);
        public static void UnityEngine_Resources__UnloadAsset(UObject assetToUnload) { }
        public static AsyncOperation UnityEngine_Resources__UnloadUnusedAssets()
        {
            AssetUnload.Requested();
            return Scenes.NewOperation(new Scenes.Operation { Done = true, Progress = 1 });
        }
    }
}
