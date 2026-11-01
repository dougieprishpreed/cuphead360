using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>
    /// What a frame draws: cameras in depth order, each with its visible
    /// sprites and UI meshes sorted the way Unity sorts transparent 2D
    /// geometry (sorting layer, sorting order, then distance along the
    /// camera's view). A backend turns it into pixels; Capture writes it
    /// as JSON for the Windows preview (tools/preview.py).
    /// </summary>
    public static class RenderList
    {
        public sealed class Item
        {
            public string Name = "";
            public Texture Texture;
            public Material Material;
            public Vector2[] Screen;   // pixels, origin bottom-left
            public Vector2[] Uv;       // Unity orientation (v up)
            public Color32[] Colors;   // per vertex, or null
            public int[] Triangles;
            public Color Tint = Color.white;
            public int Layer, Order;
            public float Depth;
            public int Sequence;
            public string Kind = "sprite";
        }

        public sealed class View
        {
            public NCamera Camera;
            public Rect PixelRect;
            public readonly List<Item> Items = new List<Item>();
        }

        public static Camera Current;
        static readonly Dictionary<NCanvasRenderer, int> depths = new Dictionary<NCanvasRenderer, int>();

        public static int DepthOf(NCanvasRenderer r) => r != null && depths.TryGetValue(r, out int d) ? d : -1;

        public static List<View> Build()
        {
            var views = new List<View>();
            var cams = Icalls.LiveCameras();
            cams.Sort((a, b) => a.Depth.CompareTo(b.Depth));   // stable: creation order breaks ties
            var renderers = new List<NSpriteRenderer>();
            var canvases = new List<NCanvas>();
            foreach (var o in Objects.Live())
            {
                if (o is NSpriteRenderer sr && sr.Enabled && sr.Sprite != null && sr.GameObject != null && sr.GameObject.ActiveInHierarchy) renderers.Add(sr);
                else if (o is NCanvas cv && cv.Enabled && cv.GameObject != null && cv.GameObject.ActiveInHierarchy && cv.IsRoot) canvases.Add(cv);
            }
            foreach (var cam in cams)
            {
                if (cam.Target != null) continue;   // render textures: not drawn by the preview yet
                var v = new View { Camera = cam, PixelRect = cam.PixelRect };
                int seq = 0;
                foreach (var sr in renderers)
                {
                    if ((cam.CullingMask & (1 << sr.GameObject.Layer)) == 0) continue;
                    var it = SpriteItem(cam, sr);
                    if (it == null) continue;
                    it.Sequence = seq++;
                    v.Items.Add(it);
                }
                foreach (var cv in canvases)
                {
                    bool mine = cv.Mode == RenderMode.WorldSpace || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera != null && Objects.Of(cv.WorldCamera) == cam)
                        || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera == null && false);
                    if (!mine || (cam.CullingMask & (1 << cv.GameObject.Layer)) == 0) continue;
                    foreach (var it in CanvasItems(cam, cv)) { it.Sequence = seq++; v.Items.Add(it); }
                }
                v.Items.Sort((a, b) =>
                {
                    int c = a.Layer.CompareTo(b.Layer); if (c != 0) return c;
                    c = a.Order.CompareTo(b.Order); if (c != 0) return c;
                    if (a.Kind == "ui" && b.Kind == "ui") return a.Sequence.CompareTo(b.Sequence);
                    c = b.Depth.CompareTo(a.Depth); if (c != 0) return c;   // farther first
                    return a.Sequence.CompareTo(b.Sequence);
                });
                views.Add(v);
            }
            // Screen-space overlay canvases draw last, over every camera.
            var overlay = new View { Camera = null, PixelRect = new Rect(0, 0, Icalls.ScreenWidth, Icalls.ScreenHeight) };
            canvases.Sort((a, b) => { int c = SortingLayers.Index(a.SortingLayerID).CompareTo(SortingLayers.Index(b.SortingLayerID)); return c != 0 ? c : a.SortingOrder.CompareTo(b.SortingOrder); });
            int oseq = 0;
            foreach (var cv in canvases)
                if (cv.Mode == RenderMode.ScreenSpaceOverlay || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera == null))
                    foreach (var it in CanvasItems(null, cv)) { it.Sequence = oseq++; overlay.Items.Add(it); }
            if (overlay.Items.Count > 0) views.Add(overlay);
            return views;
        }

        static Vector2 ToScreen(NCamera cam, Vector3 world)
        {
            if (cam == null) return new Vector2(world.x, world.y);
            var vp = cam.WorldToViewport(world);
            var p = cam.PixelRect;
            return new Vector2(p.x + vp.x * p.width, p.y + vp.y * p.height);
        }

        static Item SpriteItem(NCamera cam, NSpriteRenderer sr)
        {
            var info = Sprites.Of(sr.Sprite);
            if (info == null || info.Vertices.Length == 0 || info.Triangles.Length == 0) return null;
            if (!Sprites.Binding(sr.Sprite, out var tex, out _, out _)) tex = null;
            var m = sr.GameObject.Transform.LocalToWorld;
            var uv = Sprites.Uvs(sr.Sprite);
            var screen = new Vector2[info.Vertices.Length];
            for (int i = 0; i < screen.Length; i++)
            {
                var v = info.Vertices[i];
                if (sr.FlipX) v.x = -v.x;
                if (sr.FlipY) v.y = -v.y;
                screen[i] = ToScreen(cam, m.MultiplyPoint3x4(new Vector3(v.x, v.y, 0)));
            }
            var tris = new int[info.Triangles.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = info.Triangles[i];
            var pos = sr.GameObject.Transform.Position;
            return new Item
            {
                Name = sr.GameObject.Name + " [" + info.Name + "]", Texture = tex, Material = sr.Materials.Length > 0 ? sr.Materials[0] : null,
                Screen = screen, Uv = uv, Triangles = tris, Tint = sr.Color,
                Layer = SortingLayers.Index(sr.SortingLayerID), Order = sr.SortingOrder,
                Depth = cam.WorldToViewport(pos).z,
            };
        }

        static IEnumerable<Item> CanvasItems(NCamera cam, NCanvas root)
        {
            var list = new List<Item>();
            int depth = 0;
            int layer = SortingLayers.Index(root.SortingLayerID), order = root.SortingOrder;
            float d = cam != null ? cam.WorldToViewport(root.GameObject.Transform.Position).z : 0f;
            void Walk(NTransform t)
            {
                var go = t.GameObject;
                if (!go.ActiveSelf) return;
                foreach (var c in go.Components)
                    if (c is NCanvasRenderer cr && !cr.Destroyed)
                    {
                        depths[cr] = depth++;
                        if (cr.Cull || cr.Mesh == null || cr.Mesh.Vertices.Length == 0) continue;
                        var mesh = cr.Mesh;
                        var mtx = t.LocalToWorld;
                        var screen = new Vector2[mesh.Vertices.Length];
                        for (int i = 0; i < screen.Length; i++) screen[i] = ToScreen(cam, mtx.MultiplyPoint3x4(mesh.Vertices[i]));
                        var uv = new Vector2[screen.Length];
                        var uv0 = mesh.Uv[0];
                        for (int i = 0; i < uv.Length; i++) uv[i] = uv0 != null && i < uv0.Length ? new Vector2(uv0[i].x, uv0[i].y) : Vector2.zero;
                        var tint = cr.Color; tint.a *= cr.InheritedAlpha;
                        for (int sm = 0; sm < mesh.SubMeshes.Count; sm++)
                        {
                            if (mesh.SubMeshes[sm].Length == 0) continue;
                            var mat = sm < cr.Materials.Length ? cr.Materials[sm] : null;
                            var tex = sm == 0 && cr.Texture != null ? cr.Texture : MainTexture(mat);
                            list.Add(new Item
                            {
                                Name = go.Name, Texture = tex, Material = mat, Screen = screen, Uv = uv, Colors = mesh.Colors,
                                Triangles = mesh.SubMeshes[sm], Tint = tint, Layer = layer, Order = order, Depth = d, Kind = "ui",
                            });
                        }
                    }
                foreach (var ch in t.Children) Walk(ch);
            }
            Walk(root.GameObject.Transform);
            return list;
        }

        static readonly int MainTex = ShaderProps.Id("_MainTex");
        static Texture MainTexture(Material m) => MaterialState.Of(m) is MaterialState s && s.Textures.TryGetValue(MainTex, out var t) ? t : null;
    }

    /// <summary>Writes RenderList frames as JSON lines for tools/preview.py.</summary>
    public static class Capture
    {
        public static string Path;
        public static HashSet<int> Frames = new HashSet<int>();
        public static int Every;

        public static bool Wanted(int frame) => Path != null && (Frames.Contains(frame) || (Every > 0 && frame % Every == 0));

        public static void Write(int frame, List<RenderList.View> views)
        {
            var sb = new StringBuilder();
            var ic = CultureInfo.InvariantCulture;
            string F(float f) => float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("0.####", ic);
            string S(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            sb.Append("{\"frame\":").Append(frame).Append(",\"time\":").Append(F(Clock.Time))
              .Append(",\"scene\":").Append(S(Scenes.Active?.Name)).Append(",\"screen\":[").Append(Icalls.ScreenWidth).Append(',').Append(Icalls.ScreenHeight).Append("],\"views\":[");
            for (int vi = 0; vi < views.Count; vi++)
            {
                var v = views[vi];
                if (vi > 0) sb.Append(',');
                var c = v.Camera;
                sb.Append("{\"camera\":").Append(S(c?.GameObject.Name ?? "<overlay>"))
                  .Append(",\"rect\":[").Append(F(v.PixelRect.x)).Append(',').Append(F(v.PixelRect.y)).Append(',').Append(F(v.PixelRect.width)).Append(',').Append(F(v.PixelRect.height)).Append(']')
                  .Append(",\"clear\":").Append(c == null ? 4 : (int)c.ClearFlags);
                if (c != null) sb.Append(",\"background\":[").Append(F(c.Background.r)).Append(',').Append(F(c.Background.g)).Append(',').Append(F(c.Background.b)).Append(',').Append(F(c.Background.a)).Append(']');
                sb.Append(",\"items\":[");
                for (int ii = 0; ii < v.Items.Count; ii++)
                {
                    var it = v.Items[ii];
                    if (ii > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(S(it.Name)).Append(",\"kind\":").Append(S(it.Kind));
                    var tsrc = (Objects.Of(it.Texture) as NAsset)?.Source;
                    if (tsrc != null)
                        sb.Append(",\"texture\":{\"file\":").Append(S(tsrc.File.Path)).Append(",\"path\":").Append(tsrc.PathId)
                          .Append(",\"name\":").Append(S(Tree.Str(tsrc.Tree, "m_Name"))).Append(",\"size\":[").Append(Icalls.TextureWidth(it.Texture)).Append(',').Append(Icalls.TextureHeight(it.Texture)).Append("]}");
                    else if (it.Texture != null) sb.Append(",\"texture\":{\"runtime\":").Append(S(Objects.Of(it.Texture)?.Name)).Append('}');
                    var ms = MaterialState.Of(it.Material);
                    sb.Append(",\"material\":").Append(S(Objects.Of(it.Material)?.Name)).Append(",\"shader\":").Append(S(Objects.Of(ms?.Shader)?.Name));
                    if (ms != null && it.Kind == "ui")
                    {
                        // SDF text parameters (TextMesh Pro).
                        sb.Append(",\"props\":{");
                        bool first = true;
                        foreach (var kv in ms.Floats) { if (!first) sb.Append(','); first = false; sb.Append(S(ShaderProps.Name(kv.Key))).Append(':').Append(F(kv.Value)); }
                        foreach (var kv in ms.Vectors) { if (!first) sb.Append(','); first = false; sb.Append(S(ShaderProps.Name(kv.Key))).Append(":[").Append(F(kv.Value.x)).Append(',').Append(F(kv.Value.y)).Append(',').Append(F(kv.Value.z)).Append(',').Append(F(kv.Value.w)).Append(']'); }
                        sb.Append('}');
                    }
                    sb.Append(",\"tint\":[").Append(F(it.Tint.r)).Append(',').Append(F(it.Tint.g)).Append(',').Append(F(it.Tint.b)).Append(',').Append(F(it.Tint.a)).Append(']')
                      .Append(",\"layer\":").Append(it.Layer).Append(",\"order\":").Append(it.Order).Append(",\"depth\":").Append(F(it.Depth));
                    sb.Append(",\"xy\":[");
                    for (int k = 0; k < it.Screen.Length; k++) { if (k > 0) sb.Append(','); sb.Append(F(it.Screen[k].x)).Append(',').Append(F(it.Screen[k].y)); }
                    sb.Append("],\"uv\":[");
                    for (int k = 0; k < it.Uv.Length; k++) { if (k > 0) sb.Append(','); sb.Append(F(it.Uv[k].x)).Append(',').Append(F(it.Uv[k].y)); }
                    sb.Append("],\"tris\":[");
                    for (int k = 0; k < it.Triangles.Length; k++) { if (k > 0) sb.Append(','); sb.Append(it.Triangles[k]); }
                    sb.Append(']');
                    if (it.Colors != null)
                    {
                        sb.Append(",\"colors\":[");
                        for (int k = 0; k < it.Colors.Length; k++) { if (k > 0) sb.Append(','); var cc = it.Colors[k]; sb.Append(cc.r).Append(',').Append(cc.g).Append(',').Append(cc.b).Append(',').Append(cc.a); }
                        sb.Append(']');
                    }
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("]}\n");
            File.AppendAllText(Path, sb.ToString());
        }
    }
}
