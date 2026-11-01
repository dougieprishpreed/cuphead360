using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>
    /// Loaded scenes and their root GameObjects. Scene loading from converted
    /// game data plugs in through Loader; until then a run starts in an empty
    /// scene created by the player host.
    /// </summary>
    public static class Scenes
    {
        public sealed class NScene
        {
            public int Handle, BuildIndex = -1;
            public string Name = "", Path = "";
            public bool Loaded;
            public readonly List<NGameObject> Roots = new List<NGameObject>();
        }

        static readonly List<NScene> scenes = new List<NScene>();
        static int nextHandle = 1;
        public static int ActiveHandle;
        /// <summary>Pseudo-scene of DontDestroyOnLoad objects.</summary>
        public static readonly NScene DontDestroy = new NScene { Handle = -1, Name = "DontDestroyOnLoad", Loaded = true };

        public static NScene Create(string name, int buildIndex)
        {
            var s = new NScene { Handle = nextHandle++, Name = name, BuildIndex = buildIndex, Loaded = true };
            scenes.Add(s);
            if (ActiveHandle == 0) ActiveHandle = s.Handle;
            return s;
        }

        public static NScene ByHandle(int h)
        {
            if (h == -1) return DontDestroy;
            foreach (var s in scenes) if (s.Handle == h) return s;
            return null;
        }

        public static NScene Active => ByHandle(ActiveHandle);
        public static IReadOnlyList<NScene> All => scenes;

        public static void AddRoot(NGameObject go)
        {
            var s = go.DontDestroy ? DontDestroy : ByHandle(go.Scene);
            if (s != null && !s.Roots.Contains(go)) s.Roots.Add(go);
        }

        public static void RemoveRoot(NGameObject go)
        {
            ByHandle(go.Scene)?.Roots.Remove(go);
            DontDestroy.Roots.Remove(go);
        }

        public static void Forget(NGameObject go) => RemoveRoot(go);

        public static void MarkDontDestroy(NGameObject go)
        {
            // Only roots move; a child follows its root.
            var root = go.Transform;
            while (root.Parent != null) root = root.Parent;
            var rg = root.GameObject;
            if (rg.DontDestroy) return;
            RemoveRoot(rg);
            SetSceneTree(root, -1);
            rg.DontDestroy = true;
            DontDestroy.Roots.Add(rg);
        }

        static void SetSceneTree(NTransform t, int handle)
        {
            t.GameObject.Scene = handle;
            foreach (var c in t.Children) SetSceneTree(c, handle);
        }

        /// <summary>Asynchronous scene operations (AsyncOperation.m_Ptr -> state).</summary>
        public sealed class Operation
        {
            public bool Done, AllowActivation = true;
            public float Progress;
            public int Priority;
            public Action Complete;
        }

        static readonly Dictionary<IntPtr, Operation> operations = new Dictionary<IntPtr, Operation>();
        static long nextOp = 1;

        public static AsyncOperation NewOperation(Operation op)
        {
            var a = new AsyncOperation();
            a.m_Ptr = (IntPtr)(nextOp++);
            operations[a.m_Ptr] = op;
            return a;
        }

        public static Operation Op(AsyncOperation a) => a != null && operations.TryGetValue(a.m_Ptr, out var o) ? o : null;
        public static bool IsDone(AsyncOperation a) => Op(a)?.Done ?? true;
    }
}
