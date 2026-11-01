using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Cuphead360.Engine
{
    /// <summary>Minimal RGBA PNG writer (zlib via .NET).</summary>
    public static class Png
    {
        static readonly uint[] crcTable = BuildCrc();
        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            return t;
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
            s.Write(len, 0, 4);
            var tb = Encoding.ASCII.GetBytes(type);
            s.Write(tb, 0, 4);
            s.Write(data, 0, data.Length);
            uint c = 0xFFFFFFFFu;
            foreach (var b in tb) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (var b in data) c = crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            c ^= 0xFFFFFFFFu;
            s.Write(new[] { (byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c }, 0, 4);
        }

        public static void Write(string path, byte[] rgba, int w, int h)
        {
            using var f = File.Create(path);
            f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var ihdr = new byte[13];
            ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
            ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
            ihdr[8] = 8; ihdr[9] = 6;
            Chunk(f, "IHDR", ihdr);
            using (var ms = new MemoryStream())
            {
                using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                {
                    for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
                }
                Chunk(f, "IDAT", ms.ToArray());
            }
            Chunk(f, "IEND", new byte[0]);
        }
    }

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
            public int GoLayer;
            /// <summary>Stencil state from the material (UGUI masks): ref, comp, op, readMask, writeMask, colorMask; null when off.</summary>
            public int[] Stencil;
        }

        static readonly int stencilCompId = ShaderProps.Id("_StencilComp"), stencilId = ShaderProps.Id("_Stencil"), stencilOpId = ShaderProps.Id("_StencilOp"),
            stencilReadId = ShaderProps.Id("_StencilReadMask"), stencilWriteId = ShaderProps.Id("_StencilWriteMask"), colorMaskId = ShaderProps.Id("_ColorMask");

        /// <summary>The stencil test and writes a UI material asks for (CompareFunction / StencilOp codes), or null for none.</summary>
        static int[] StencilOf(Material m)
        {
            var ms = MaterialState.Of(m);
            if (ms == null) return null;
            int F(int id, int def) => ms.Floats.TryGetValue(id, out float v) ? (int)v : def;
            int comp = F(stencilCompId, 8), op = F(stencilOpId, 0), colorMask = F(colorMaskId, 15);
            if ((comp == 8 || comp == 0) && op == 0 && colorMask == 15) return null;
            return new[] { F(stencilId, 0), comp == 0 ? 8 : comp, op, F(stencilReadId, 255), F(stencilWriteId, 255), colorMask };
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
            foreach (var sr in Typed.SpriteRenderers)
                if (sr.Enabled && sr.Sprite != null && sr.GameObject != null && sr.GameObject.ActiveInHierarchy) renderers.Add(sr);
            foreach (var cv in Typed.Canvases)
                if (cv.Enabled && cv.GameObject != null && cv.GameObject.ActiveInHierarchy && cv.IsRoot) canvases.Add(cv);
            if (Environment.GetEnvironmentVariable("CH360_TRACE_UI") == "1")
                foreach (var cv in canvases)
                {
                    var t = cv.GameObject.Transform;
                    Log.Line($"TRACE canvas {cv.GameObject.Name} mode={cv.Mode} cam={Objects.Of(cv.WorldCamera)?.Name} plane={cv.PlaneDistance} sf={cv.ScaleFactor} layer={cv.GameObject.Layer} pos={t.Position} scale={t.LossyScale} parent={t.Parent?.GameObject.Name} rect={(t as NRectTransform)?.Rect}");
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
            float depth = cam.WorldToViewport(pos).z;
            if (depth < cam.Near || depth > cam.Far) return null;   // outside the clip planes
            return new Item
            {
                Name = sr.GameObject.Name + " [" + info.Name + "]", Texture = tex, Material = sr.Materials.Length > 0 ? sr.Materials[0] : null,
                Screen = screen, Uv = uv, Triangles = tris, Tint = sr.Color,
                Layer = SortingLayers.Index(sr.SortingLayerID), Order = sr.SortingOrder,
                Depth = depth, GoLayer = sr.GameObject.Layer,
            };
        }

        static IEnumerable<Item> CanvasItems(NCamera cam, NCanvas root)
        {
            var list = new List<Item>();
            int depth = 0;
            int layer = SortingLayers.Index(root.SortingLayerID), order = root.SortingOrder;
            float d = cam != null ? cam.WorldToViewport(root.GameObject.Transform.Position).z : 0f;
            // A canvas on or behind the camera's near plane (or past the far one) is clipped away.
            if (cam != null && (d < cam.Near || d > cam.Far)) return list;
            void Walk(NTransform t)
            {
                var go = t.GameObject;
                if (!go.ActiveSelf) return;
                List<Item> pops = null;
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
                            if (tex == null && Environment.GetEnvironmentVariable("CH360_TRACE_UI") == "1")
                                foreach (var c2 in go.Components)
                                    if (c2.Managed != null && c2.Managed.GetType().Name == "Image")
                                    {
                                        var sp = c2.Managed.GetType().GetField("m_Sprite", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(c2.Managed) as Sprite;
                                        var si = Sprites.Of(sp);
                                        Log.Line($"TRACE untextured {go.Name}: sprite={(sp == null ? "null" : si?.Name)} tags={string.Join(",", si?.AtlasTags ?? new string[0])} included={si?.IncludedAtlas != null} key={si?.RenderKey} bound={Sprites.Binding(sp, out _, out _, out _)}");
                                    }
                            list.Add(new Item
                            {
                                Name = go.Name, Texture = tex, Material = mat, Screen = screen, Uv = uv, Colors = mesh.Colors,
                                Triangles = mesh.SubMeshes[sm], Tint = tint, Layer = layer, Order = order, Depth = d, Kind = "ui", GoLayer = go.Layer,
                                Stencil = StencilOf(mat),
                            });
                        }
                        // Pop materials draw after the children (a Mask clearing its stencil).
                        foreach (var pm in cr.PopMaterials)
                            if (pm != null && mesh.SubMeshes.Count > 0)
                                (pops ??= new List<Item>()).Add(new Item
                                {
                                    Name = go.Name + " (pop)", Texture = cr.Texture, Material = pm, Screen = screen, Uv = uv, Colors = mesh.Colors,
                                    Triangles = mesh.SubMeshes[0], Tint = tint, Layer = layer, Order = order, Depth = d, Kind = "ui", GoLayer = go.Layer,
                                    Stencil = StencilOf(pm),
                                });
                    }
                foreach (var ch in t.Children) Walk(ch);
                if (pops != null) list.AddRange(pops);
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
        static readonly Dictionary<(int id, int version), string> written = new Dictionary<(int, int), string>();

        /// <summary>A run-time texture (font atlas, solid colour) as a PNG next to the capture file.</summary>
        static string RuntimePng(Texture t, RuntimePixels.Entry e)
        {
            int id = Objects.Of(t)?.InstanceId ?? 0;
            if (written.TryGetValue((id, e.Version), out var name)) return name;
            name = "runtime_" + (id < 0 ? "m" + (-id) : id.ToString()) + "_" + e.Version + ".png";
            var rgba = new byte[e.W * e.H * 4];
            for (int i = 0; i < e.W * e.H; i++)
            {
                if (e.Channels == 1) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = e.Data[i]; }
                else Buffer.BlockCopy(e.Data, i * 4, rgba, i * 4, 4);
            }
            // Pixels are stored top row first, which is PNG order.
            Png.Write(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), name), rgba, e.W, e.H);
            written[(id, e.Version)] = name;
            return name;
        }

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
                  .Append(",\"clear\":").Append(c == null ? 4 : (int)c.ClearFlags).Append(",\"mask\":").Append(c == null ? -1 : c.CullingMask);
                if (c != null) sb.Append(",\"background\":[").Append(F(c.Background.r)).Append(',').Append(F(c.Background.g)).Append(',').Append(F(c.Background.b)).Append(',').Append(F(c.Background.a)).Append(']');
                sb.Append(",\"items\":[");
                for (int ii = 0; ii < v.Items.Count; ii++)
                {
                    var it = v.Items[ii];
                    if (ii > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(S(it.Name)).Append(",\"kind\":").Append(S(it.Kind)).Append(",\"goLayer\":").Append(it.GoLayer);
                    var tsrc = (Objects.Of(it.Texture) as NAsset)?.Source;
                    if (tsrc != null)
                        sb.Append(",\"texture\":{\"file\":").Append(S(tsrc.File.Path)).Append(",\"path\":").Append(tsrc.PathId)
                          .Append(",\"name\":").Append(S(Tree.Str(tsrc.Tree, "m_Name"))).Append(",\"size\":[").Append(Icalls.TextureWidth(it.Texture)).Append(',').Append(Icalls.TextureHeight(it.Texture)).Append("]}");
                    else if (RuntimePixels.Get(it.Texture) is RuntimePixels.Entry rp)
                        sb.Append(",\"texture\":{\"png\":").Append(S(RuntimePng(it.Texture, rp))).Append(",\"size\":[").Append(rp.W).Append(',').Append(rp.H).Append("]}");
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
                    if (it.Stencil != null) sb.Append(",\"stencil\":[").Append(string.Join(",", it.Stencil)).Append(']');
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
