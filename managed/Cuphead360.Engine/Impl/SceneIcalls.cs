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
        sealed class Pending
        {
            public int BuildIndex;
            public bool Additive;
            public Scenes.Operation Op;
            public int RequestFrame;
        }
        static readonly List<Pending> pending = new List<Pending>();
        public static int Loads;

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
            pending.Add(new Pending { BuildIndex = index, Additive = additive, Op = op, RequestFrame = Clock.FrameCount });
            return Scenes.NewOperation(op);
        }

        /// <summary>Called at the start of a frame: loads requested in an earlier frame complete now.</summary>
        public static void Pump()
        {
            for (int i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                if (p.RequestFrame >= Clock.FrameCount) continue;
                if (!p.Op.AllowActivation) { p.Op.Progress = 0.9f; continue; }
                pending.RemoveAt(i--);
                Load(p.BuildIndex, p.Additive);
                p.Op.Progress = 1f;
                p.Op.Done = true;
                p.Op.Complete?.Invoke();
            }
        }

        public static void Load(int index, bool additive)
        {
            string path = AssetDb.BuildScenes[index];
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            var file = AssetDb.File("level" + index);
            if (file == null) { Log.Line("SCENE level" + index + " (" + name + ") is not converted"); return; }
            if (!additive) UnloadAll();
            var scene = Scenes.Create(name, index);
            scene.Path = path;
            if (!additive) { Scenes.ActiveHandle = scene.Handle; Clock.TimeSinceLevelLoadBase = Clock.Time; }
            var build = SceneBuild.Scene(file, scene.Handle);
            Loads++;
            Log.Line($"SCENE loaded {name} (build {index}): {build.GameObjects.Count} GameObjects, {build.Components.Count} components");
            build.Activate();
            // Managed callbacks the native player makes after a load.
            var mode = additive ? LoadSceneMode.Additive : LoadSceneMode.Single;
            var s = new Scene { m_Handle = scene.Handle };
            Invoke(typeof(SceneManager), "Internal_SceneLoaded", s, mode);
            if (!additive) Invoke(typeof(SceneManager), "Internal_ActiveSceneChanged", default(Scene), s);
            if (!additive)
                foreach (var b in Typed.Behaviours.ToArray())
                    if (b.Messages.OnLevelWasLoaded != null && Behaviours.ActiveAndEnabled(b)) Behaviours.Send(b, b.Messages.OnLevelWasLoaded, index);
        }

        static void UnloadAll()
        {
            foreach (var sc in Scenes.All.ToArray())
            {
                if (!sc.Loaded) continue;
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
        public static AsyncOperation UnityEngine_Resources__UnloadUnusedAssets() => Scenes.NewOperation(new Scenes.Operation { Done = true, Progress = 1 });
    }
}
