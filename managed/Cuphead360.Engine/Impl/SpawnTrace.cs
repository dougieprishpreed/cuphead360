using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>
    /// CH360_TRACE_SPAWN=name1,name2 (a check for the console too: env.txt next to the XEX): each Instantiate of an object
    /// whose name holds one of the names is followed from its creation to its destruction. SPAWN lines: where it was made,
    /// each of its first 24 frames' sprites drawn (sprite, screen box, alpha, layer and order, camera) or why none was,
    /// and at its end how many frames drew it. "*" follows every spawn's creation and end only (to find the names).
    /// </summary>
    public static class SpawnTrace
    {
        static readonly string[] names = (Environment.GetEnvironmentVariable("CH360_TRACE_SPAWN") ?? "")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        public static readonly bool On = names.Length > 0;
        static readonly bool all = Array.IndexOf(names, "*") >= 0;
        const int MaxLines = 4000, Followed = 24;
        static int lines;

        sealed class Rec
        {
            public string Name;
            public int Frame, Drawn = -1, FirstDrawn = -1, LastDrawn = -1, Frames;
            public bool Detailed;
        }

        static void Say(string s)
        {
            if (lines >= MaxLines) return;
            if (++lines == MaxLines) s += " (the trace's last line)";
            Log.Line("SPAWN " + s);
        }

        static bool Named(string n)
        {
            if (n == null) return false;
            foreach (var x in names) if (x != "*" && n.IndexOf(x, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static void Mark(NTransform t, Rec r)
        {
            t.GameObject.Spawn = r;
            for (int i = 0; i < t.Children.Count; i++) Mark(t.Children[i], r);
        }

        /// <summary>After Instantiate made root from src (before its Awake).</summary>
        public static void Made(NGameObject src, NGameObject root)
        {
            bool named = Named(src.Name);
            if (!named && !all) return;
            var r = new Rec { Name = root.Name, Frame = Clock.FrameCount, Detailed = named };
            Mark(root.Transform, r);
            var p = root.Transform.Position;
            Say($"made {root.Name} at frame {r.Frame}, ({p.x:0},{p.y:0}){(root.ActiveInHierarchy ? "" : " inactive")}, {CountSprites(root.Transform)} sprite renderers");
        }

        static int CountSprites(NTransform t)
        {
            int n = 0;
            foreach (var c in t.GameObject.Components) if (c is NSpriteRenderer) n++;
            for (int i = 0; i < t.Children.Count; i++) n += CountSprites(t.Children[i]);
            return n;
        }

        /// <summary>A traced object's sprite renderer listed by a camera (it: the item, null when it wasn't drawn).</summary>
        public static void Sprite(NCamera cam, NSpriteRenderer sr, RenderList.Item it, string why)
        {
            if (!(sr.GameObject.Spawn is Rec r) || !r.Detailed) return;
            int f = Clock.FrameCount;
            if (it != null)
            {
                if (r.FirstDrawn < 0) r.FirstDrawn = f;
                if (r.LastDrawn != f) { r.LastDrawn = f; r.Frames++; }
            }
            if (f - r.Frame >= Followed) return;
            string what = Sprites.Of(sr.Sprite)?.Name ?? (sr.Sprite == null ? "no sprite" : "?");
            if (it == null) { Say($"{r.Name}/{sr.GameObject.Name} frame +{f - r.Frame} on {cam.GameObject.Name}: {what} not drawn ({why})"); return; }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            if (it.Screen != null)
                foreach (var s in it.Screen) { x0 = Math.Min(x0, s.x); x1 = Math.Max(x1, s.x); y0 = Math.Min(y0, s.y); y1 = Math.Max(y1, s.y); }
            else if (it.Local != null)
                foreach (var lv in it.Local)
                {
                    float sx = it.A00 * lv.x + it.A01 * lv.y + it.B0, sy = it.A10 * lv.x + it.A11 * lv.y + it.B1;
                    x0 = Math.Min(x0, sx); x1 = Math.Max(x1, sx); y0 = Math.Min(y0, sy); y1 = Math.Max(y1, sy);
                }
            Say($"{r.Name}/{sr.GameObject.Name} frame +{f - r.Frame} on {cam.GameObject.Name}: {what} at ({x0:0},{y0:0})-({x1:0},{y1:0}) alpha {it.Tint.a:0.00} layer {it.Layer} order {it.Order} texture {Objects.Of(it.Texture)?.Name}");
        }

        /// <summary>A traced object destroyed.</summary>
        public static void Gone(NGameObject go)
        {
            if (!(go.Spawn is Rec r) || r.Name == null) return;
            // only the spawn's root says it (its children share the record)
            if (go.Transform.Parent != null && go.Transform.Parent.GameObject.Spawn == r) return;
            int f = Clock.FrameCount;
            Say($"gone {r.Name} at frame {f} after {f - r.Frame} frames; drawn in {r.Frames}" + (r.FirstDrawn >= 0 ? $" (+{r.FirstDrawn - r.Frame} to +{r.LastDrawn - r.Frame})" : ""));
            r.Name = null;
        }
    }
}
