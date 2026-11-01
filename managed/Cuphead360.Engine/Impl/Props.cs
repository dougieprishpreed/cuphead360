using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>
    /// State of built-in property accessors that no engine module implements
    /// yet (Patcher gen routes plain get_X/set_X icalls here). A value set is
    /// kept; an unset value comes from the object's converted data (m_X, X, ...)
    /// or is default. Every accessor is listed in its own ledger so a run shows
    /// which behaviour is state-only (e.g. an AudioSource volume that is stored
    /// but plays nothing).
    /// </summary>
    public static class Props
    {
        static readonly ConditionalWeakTable<object, Dictionary<string, object>> bags = new ConditionalWeakTable<object, Dictionary<string, object>>();
        static readonly Dictionary<string, string> uses = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly List<string> order = new List<string>();

        static readonly Dictionary<string, string[]> aliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["clip"] = new[] { "m_audioClip" },
            ["mute"] = new[] { "Mute" },
            ["loop"] = new[] { "Loop", "m_Loop" },
            ["outputAudioMixerGroup"] = new[] { "OutputAudioMixerGroup" },
            ["panStereo"] = new[] { "Pan2D" },
            ["sortingOrder"] = new[] { "m_SortingOrder" },
            ["sortingLayerID"] = new[] { "m_SortingLayerID" },
        };

        static void Note(string id, string how)
        {
            if (uses.ContainsKey(id)) return;
            uses[id] = how;
            order.Add(id);
        }

        public static IEnumerable<KeyValuePair<string, string>> Report() { foreach (var id in order) yield return new KeyValuePair<string, string>(id, uses[id]); }
        public static int Count => order.Count;

        public static void Set<T>(object self, string name, T value, string id)
        {
            if (self == null) return;
            bags.GetValue(self, _ => new Dictionary<string, object>(StringComparer.Ordinal))[name] = value;
            Note(id, "set");
        }

        public static T Get<T>(object self, string name, string id)
        {
            if (self == null) return default;
            var bag = bags.GetValue(self, _ => new Dictionary<string, object>(StringComparer.Ordinal));
            if (bag.TryGetValue(name, out var v)) { Note(id, "get"); return v is T t ? t : default; }
            var src = Source(self as UObject);
            if (src != null && Seed(src, name, typeof(T), out var seeded))
            {
                bag[name] = seeded;
                Note(id, "seeded");
                return seeded is T st ? st : default;
            }
            Note(id, "default");
            return default;
        }

        /// <summary>Explicit state for engine modules (same bag the accessors use).</summary>
        public static bool TryGet<T>(object self, string name, out T value)
        {
            value = default;
            if (self == null || !bags.TryGetValue(self, out var bag) || !bag.TryGetValue(name, out var v) || !(v is T t)) return false;
            value = t;
            return true;
        }

        static ChobObject Source(UObject o)
        {
            var rec = Objects.Of(o);
            return (rec as NComponent)?.Data ?? (rec as NAsset)?.Source;
        }

        static IEnumerable<string> Keys(string name)
        {
            if (aliases.TryGetValue(name, out var a)) foreach (var k in a) yield return k;
            string up = char.ToUpperInvariant(name[0]) + name.Substring(1);
            yield return "m_" + up;
            yield return "m_" + name;
            yield return name;
            yield return up;
        }

        static bool Seed(ChobObject src, string name, Type t, out object value)
        {
            value = null;
            foreach (var k in Keys(name))
            {
                var raw = Tree.Get(src.Tree, k);
                if (raw == null) continue;
                if (Convert(src, raw, t, out value)) return true;
            }
            return false;
        }

        static bool Convert(ChobObject src, object raw, Type t, out object value)
        {
            value = null;
            try
            {
                if (t.IsEnum) { value = Enum.ToObject(t, raw is bool b0 ? (b0 ? 1L : 0L) : System.Convert.ToInt64(raw)); return true; }
                if (t == typeof(bool)) { value = raw is bool b ? b : System.Convert.ToInt64(raw) != 0; return true; }
                if (t == typeof(string)) { value = raw as string; return value != null; }
                if (t.IsPrimitive) { value = System.Convert.ChangeType(raw is bool bb ? (bb ? 1 : 0) : raw, t, System.Globalization.CultureInfo.InvariantCulture); return true; }
                if (typeof(UObject).IsAssignableFrom(t))
                {
                    if (!(raw is PPtr p)) return false;
                    var o = AssetDb.Resolve(src.File, p);
                    value = o != null && t.IsInstanceOfType(o) ? o : null;
                    return true;
                }
                if (t == typeof(Vector2)) { value = new Vector2(Tree.Float(raw, "x"), Tree.Float(raw, "y")); return true; }
                if (t == typeof(Vector3)) { value = new Vector3(Tree.Float(raw, "x"), Tree.Float(raw, "y"), Tree.Float(raw, "z")); return true; }
                if (t == typeof(Vector4)) { value = new Vector4(Tree.Float(raw, "x"), Tree.Float(raw, "y"), Tree.Float(raw, "z"), Tree.Float(raw, "w")); return true; }
                if (t == typeof(Quaternion)) { value = new Quaternion(Tree.Float(raw, "x"), Tree.Float(raw, "y"), Tree.Float(raw, "z"), Tree.Float(raw, "w", 1)); return true; }
                if (t == typeof(Color)) { value = new Color(Tree.Float(raw, "r", 1), Tree.Float(raw, "g", 1), Tree.Float(raw, "b", 1), Tree.Float(raw, "a", 1)); return true; }
                if (t == typeof(Rect)) { value = new Rect(Tree.Float(raw, "x"), Tree.Float(raw, "y"), Tree.Float(raw, "width"), Tree.Float(raw, "height")); return true; }
            }
            catch (Exception) { }
            return false;
        }
    }
}
