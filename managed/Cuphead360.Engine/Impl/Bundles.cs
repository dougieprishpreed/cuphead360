using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>
    /// Asset bundles from GameData/objects/bundles/&lt;bundle&gt;/*.chob. The
    /// game asks for StreamingAssets/AssetBundles/&lt;name&gt;; the bundle's
    /// AssetBundle object lists its assets (m_Container) as in Unity.
    /// </summary>
    public static class Bundles
    {
        public sealed class NBundle : NObject
        {
            public string BundleName;
            public ChobFile Main;
            public ChobObject Manifest;   // the AssetBundle object
            public readonly List<(string path, PPtr asset)> Container = new List<(string, PPtr)>();
        }

        static readonly Dictionary<string, NBundle> loaded = new Dictionary<string, NBundle>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<IntPtr, NBundle> createRequests = new Dictionary<IntPtr, NBundle>();
        static readonly Dictionary<IntPtr, UObject[]> assetRequests = new Dictionary<IntPtr, UObject[]>();

        public static AssetBundle Load(string path)
        {
            string name = Path.GetFileName((path ?? "").Replace('\\', '/'));
            if (loaded.TryGetValue(name, out var b) && !b.Destroyed)
            {
                Log.Line("The AssetBundle '" + name + "' can't be loaded because another AssetBundle with the same files is already loaded.");
                return null;
            }
            string dir = Path.Combine(AssetDb.Root, "objects", "bundles", name);
            if (!Directory.Exists(dir)) { AssetDb.Problem("bundle not converted: " + name); return null; }
            ChobFile main = null; ChobObject manifest = null;
            foreach (var f in Directory.GetFiles(dir, "*.chob"))
            {
                var cf = AssetDb.File(Path.GetFileNameWithoutExtension(f));
                if (cf == null) continue;
                var ab = cf.Ordered.FirstOrDefault(o => o.Type == "AssetBundle");
                if (ab != null) { main = cf; manifest = ab; }
            }
            if (manifest == null) { AssetDb.Problem("bundle without an AssetBundle object: " + name); return null; }
            var managed = (AssetBundle)Lifecycle.NewManaged(typeof(AssetBundle));
            var rec = Objects.Register(managed, new NBundle { BundleName = name, Main = main, Manifest = manifest }, Tree.Str(manifest.Tree, "m_Name"), persistent: true);
            foreach (var e in Tree.List(manifest.Tree, "m_Container"))
                if (e is List<object> pair && pair.Count == 2 && pair[0] is string p)
                    rec.Container.Add((p, Tree.Ptr(pair[1], "asset")));
            loaded[name] = rec;
            return managed;
        }

        static NBundle Of(AssetBundle b) => Objects.Of(b) as NBundle;

        static bool NameMatches(string containerPath, string name)
        {
            if (string.Equals(containerPath, name, StringComparison.OrdinalIgnoreCase)) return true;
            string file = Path.GetFileName(containerPath);
            return string.Equals(file, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase);
        }

        static UObject As(UObject o, Type t)
        {
            if (o == null || t == null || t.IsInstanceOfType(o)) return o;
            if (o is GameObject go && typeof(Component).IsAssignableFrom(t)) return Find.Component(Objects.GO(go), t);
            return null;
        }

        public static UObject LoadAsset(AssetBundle bundle, string name, Type type)
        {
            var b = Of(bundle);
            if (b == null || name == null) return null;
            foreach (var (p, ptr) in b.Container)
                if (NameMatches(p, name))
                {
                    var o = As(AssetDb.Resolve(b.Main, ptr), type);
                    if (o != null) return o;
                }
            return null;
        }

        public static UObject[] LoadWithSubAssets(AssetBundle bundle, string name, Type type)
        {
            var b = Of(bundle);
            var r = new List<UObject>();
            if (b != null)
                foreach (var (p, ptr) in b.Container)
                {
                    if (!NameMatches(p, name)) continue;
                    var o = As(AssetDb.Resolve(b.Main, ptr), type);
                    if (o != null && !r.Contains(o)) r.Add(o);
                }
            var arr = (UObject[])Array.CreateInstance(type ?? typeof(UObject), r.Count);
            for (int i = 0; i < r.Count; i++) arr[i] = r[i];
            return arr;
        }

        public static UObject[] LoadAll(AssetBundle bundle, Type type)
        {
            var b = Of(bundle);
            var r = new List<UObject>();
            if (b != null)
                foreach (var (p, ptr) in b.Container)
                {
                    var o = As(AssetDb.Resolve(b.Main, ptr), type);
                    if (o != null && !r.Contains(o)) r.Add(o);
                }
            var arr = (UObject[])Array.CreateInstance(type ?? typeof(UObject), r.Count);
            for (int i = 0; i < r.Count; i++) arr[i] = r[i];
            return arr;
        }

        public static void Unload(AssetBundle bundle, bool all)
        {
            var b = Of(bundle);
            if (b == null) return;
            loaded.Remove(b.BundleName);
            // Loaded assets stay alive unless all = true (then they are destroyed);
            // either way the next load of this bundle makes new objects.
            string dir = Path.Combine(AssetDb.Root, "objects", "bundles", b.BundleName);
            if (Directory.Exists(dir))
                foreach (var fpath in Directory.GetFiles(dir, "*.chob"))
                {
                    var cf = AssetDb.File(Path.GetFileNameWithoutExtension(fpath));
                    if (cf == null) continue;
                    if (all)
                        foreach (var o in cf.Ordered)
                            if (AssetDb.TryBuilt(cf, o.PathId, out var obj) && Objects.Of(obj) is NObject rec && !rec.Destroyed && !(rec is NComponent) && !(rec is NGameObject))
                                Lifecycle.DestroyNow(rec);
                    AssetDb.Forget(cf);
                }
            Objects.Release(b);
        }

        public static AssetBundleCreateRequest CreateRequest(AssetBundle bundle)
        {
            var req = new AssetBundleCreateRequest();
            var op = Scenes.NewOperation(new Scenes.Operation { Done = true, Progress = 1 });
            req.m_Ptr = op.m_Ptr;
            createRequests[req.m_Ptr] = Of(bundle);
            return req;
        }

        public static AssetBundleRequest AssetRequest(UObject[] assets)
        {
            var req = new AssetBundleRequest();
            var op = Scenes.NewOperation(new Scenes.Operation { Done = true, Progress = 1 });
            req.m_Ptr = op.m_Ptr;
            assetRequests[req.m_Ptr] = assets;
            return req;
        }

        public static AssetBundle RequestBundle(AssetBundleCreateRequest r) =>
            r != null && createRequests.TryGetValue(r.m_Ptr, out var b) && b != null && !b.Destroyed ? (AssetBundle)b.Managed : null;
        public static UObject[] RequestAssets(AssetBundleRequest r) =>
            r != null && assetRequests.TryGetValue(r.m_Ptr, out var a) ? a : new UObject[0];
        public static IEnumerable<NBundle> All => loaded.Values;
    }

    public static partial class Icalls
    {
        public static AssetBundle UnityEngine_AssetBundle__LoadFromFile(string path, uint crc, ulong offset) => Bundles.Load(path);
        public static AssetBundleCreateRequest UnityEngine_AssetBundle__LoadFromFileAsync(string path, uint crc, ulong offset) => Bundles.CreateRequest(Bundles.Load(path));
        public static AssetBundle UnityEngine_AssetBundleCreateRequest__get_assetBundle(AssetBundleCreateRequest self) => Bundles.RequestBundle(self);
        public static void UnityEngine_AssetBundleCreateRequest__DisableCompatibilityChecks(AssetBundleCreateRequest self) { }
        public static UObject UnityEngine_AssetBundle__LoadAsset_Internal(AssetBundle self, string name, Type type) => Bundles.LoadAsset(self, name, type);
        public static UObject UnityEngine_AssetBundle__Load(AssetBundle self, string name, Type type) => Bundles.LoadAsset(self, name, type);
        public static AssetBundleRequest UnityEngine_AssetBundle__LoadAssetAsync_Internal(AssetBundle self, string name, Type type)
        {
            var o = Bundles.LoadAsset(self, name, type);
            return Bundles.AssetRequest(o == null ? new UObject[0] : new[] { o });
        }
        public static AssetBundleRequest UnityEngine_AssetBundle__LoadAsync(AssetBundle self, string name, Type type) => UnityEngine_AssetBundle__LoadAssetAsync_Internal(self, name, type);
        // A bundle's m_Container has one entry per asset object, sub-assets
        // under their main asset's path; an empty name (LoadAllAssets) means
        // every entry.
        public static UObject[] UnityEngine_AssetBundle__LoadAssetWithSubAssets_Internal(AssetBundle self, string name, Type type)
            => string.IsNullOrEmpty(name) ? Bundles.LoadAll(self, type) : Bundles.LoadWithSubAssets(self, name, type);
        public static AssetBundleRequest UnityEngine_AssetBundle__LoadAssetWithSubAssetsAsync_Internal(AssetBundle self, string name, Type type)
            => Bundles.AssetRequest(UnityEngine_AssetBundle__LoadAssetWithSubAssets_Internal(self, name, type));
        public static UObject[] UnityEngine_AssetBundle__LoadAll(AssetBundle self, Type type) => Bundles.LoadAll(self, type);
        public static UObject UnityEngine_AssetBundleRequest__get_asset(AssetBundleRequest self) { var a = Bundles.RequestAssets(self); return a.Length > 0 ? a[0] : null; }
        public static UObject[] UnityEngine_AssetBundleRequest__get_allAssets(AssetBundleRequest self) => Bundles.RequestAssets(self);
        public static void UnityEngine_AssetBundle__Unload(AssetBundle self, bool unloadAllLoadedObjects) => Bundles.Unload(self, unloadAllLoadedObjects);
        public static void UnityEngine_AssetBundle__UnloadAllAssetBundles(bool unloadAllObjects) { foreach (var b in Bundles.All.ToArray()) Bundles.Unload((AssetBundle)b.Managed, unloadAllObjects); }
        public static AssetBundle[] UnityEngine_AssetBundle__GetAllLoadedAssetBundles_Internal() => Bundles.All.Select(b => (AssetBundle)b.Managed).ToArray();
        public static bool UnityEngine_AssetBundle__Contains(AssetBundle self, string name) => (Objects.Of(self) as Bundles.NBundle)?.Container.Any(c => string.Equals(c.path, name, StringComparison.OrdinalIgnoreCase)) ?? false;
        public static string[] UnityEngine_AssetBundle__GetAllAssetNames(AssetBundle self) => (Objects.Of(self) as Bundles.NBundle)?.Container.Select(c => c.path).Distinct().ToArray() ?? new string[0];
        public static string[] UnityEngine_AssetBundle__GetAllScenePaths(AssetBundle self) => new string[0];
        public static bool UnityEngine_AssetBundle__get_isStreamedSceneAssetBundle(AssetBundle self) => false;
        public static UObject UnityEngine_AssetBundle__get_mainAsset(AssetBundle self)
        {
            var b = Objects.Of(self) as Bundles.NBundle;
            return b == null ? null : AssetDb.Resolve(b.Main, Tree.Ptr(Tree.Get(b.Manifest.Tree, "m_MainAsset"), "asset"));
        }

        public static int UnityEngine_QualitySettings__GetQualityLevel() => 0;
        public static void UnityEngine_QualitySettings__SetQualityLevel(int index, bool applyExpensiveChanges) { }
        public static string[] UnityEngine_QualitySettings__get_names() => new[] { "Default" };
        public static int UnityEngine_QualitySettings__get_antiAliasing() => 0;
        public static int UnityEngine_QualitySettings__get_maxQueuedFrames() => 1;
        public static ColorSpace UnityEngine_QualitySettings__get_activeColorSpace() => ColorSpace.Gamma;
        public static ColorSpace UnityEngine_QualitySettings__get_desiredColorSpace() => ColorSpace.Gamma;
    }
}
