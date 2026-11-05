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
            // The name is for captures and tools: built when asked, not per frame.
            internal string BaseName = "", Suffix, SpriteName;
            public string Name => SpriteName != null ? BaseName + " [" + SpriteName + "]" : Suffix == null ? BaseName : BaseName + Suffix;
            public Texture Texture;
            public Material Material;
            public Vector2[] Screen;   // pixels, origin bottom-left
            /// <summary>
            /// With HostTransforms, a sprite under an orthographic camera leaves
            /// Screen null and carries its mesh's own vertices (shared, not to be
            /// written) and the map to pixels: x' = A00 x + A01 y + B0,
            /// y' = A10 x + A11 y + B1 (flips folded in).
            /// </summary>
            public Vector2[] Local;
            public float A00, A01, B0, A10, A11, B1;
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
            // Arrays this item fills, kept for its next use from the pool.
            internal Vector2[] OwnScreen, OwnUv;

            internal Vector2[] ScreenOf(int n) => OwnScreen != null && OwnScreen.Length == n ? OwnScreen : OwnScreen = new Vector2[n];
            internal Vector2[] UvOf(int n) => OwnUv != null && OwnUv.Length == n ? OwnUv : OwnUv = new Vector2[n];
        }

        // The lists, items and their vertex arrays are kept from frame to frame
        // (each Build's result is used before the next Build): built anew,
        // they were a garbage collection every frame or two on the world map.
        // Items are handed out in build order, which stays the same while the
        // same things are drawn, so their arrays mostly fit again.
        static readonly List<Item> pool = new List<Item>();
        static int pooled;
        static readonly List<View> viewPool = new List<View>();
        static int viewsUsed;
        static readonly List<View> views = new List<View>();
        static readonly List<NSpriteRenderer> renderers = new List<NSpriteRenderer>();
        static readonly List<NCanvas> canvases = new List<NCanvas>();
        static readonly List<Item> canvasOut = new List<Item>();

        static Item NewItem()
        {
            if (pooled == pool.Count) pool.Add(new Item());
            var it = pool[pooled++];
            it.BaseName = ""; it.Suffix = null; it.SpriteName = null; it.Texture = null; it.Material = null; it.Screen = null; it.Local = null; it.Uv = null; it.Colors = null;
            it.Triangles = null; it.Tint = Color.white; it.Layer = it.Order = 0; it.Depth = 0; it.Sequence = 0; it.Kind = "sprite"; it.GoLayer = 0;
            it.Stencil = null;
            return it;
        }

        static View NewView(NCamera cam, Rect rect)
        {
            if (viewsUsed == viewPool.Count) viewPool.Add(new View());
            var v = viewPool[viewsUsed++];
            v.Camera = cam; v.PixelRect = rect; v.Items.Clear();
            return v;
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
        static readonly bool traceUi = Environment.GetEnvironmentVariable("CH360_TRACE_UI") == "1";

        /// <summary>
        /// A camera's view and projection, taken once a frame: WorldToViewport
        /// builds and inverts both for each point (34,500 vertices a frame on
        /// the world map). The same arithmetic in the same order, so the
        /// results are the same bits.
        /// </summary>
        /// <summary>
        /// The console host transforms sprites itself (native code): items get
        /// Local and the affine map instead of Screen. Off on Windows (captures
        /// and the live window read Screen).
        /// </summary>
        public static bool HostTransforms;

        struct CamXform
        {
            public bool Has, Ortho;
            public Matrix4x4 View, Proj, ProjView;
            public Rect Pixel;
            public static CamXform Of(NCamera cam)
            {
                if (cam == null) return default;
                var x = new CamXform { Has = true, View = cam.WorldToCamera, Proj = cam.Projection, Pixel = cam.PixelRect };
                x.ProjView = x.Proj * x.View;
                x.Ortho = x.Proj.m30 == 0f && x.Proj.m31 == 0f && x.Proj.m32 == 0f && x.Proj.m33 == 1f;
                return x;
            }
            public Vector3 Viewport(Vector3 p)
            {
                var v = View.MultiplyPoint3x4(p);
                float depth = -v.z;
                var clip = Proj * new Vector4(v.x, v.y, v.z, 1f);
                if (clip.w == 0f) return new Vector3(0.5f, 0.5f, depth);
                return new Vector3((clip.x / clip.w + 1f) * 0.5f, (clip.y / clip.w + 1f) * 0.5f, depth);
            }
        }

        public static int DepthOf(NCanvasRenderer r) => r != null ? r.Depth : -1;

        public static List<View> Build()
        {
            pooled = 0; viewsUsed = 0;
            views.Clear(); renderers.Clear(); canvases.Clear();
            var cams = Icalls.LiveCameras();
            cams.Sort((a, b) => a.Depth.CompareTo(b.Depth));   // stable: creation order breaks ties
            foreach (var sr in Typed.SpriteRenderers)
                if (sr.Enabled && sr.Sprite != null && sr.GameObject != null && sr.GameObject.ActiveInHierarchy) renderers.Add(sr);
            foreach (var cv in Typed.Canvases)
                if (cv.Enabled && cv.GameObject != null && cv.GameObject.ActiveInHierarchy && cv.IsRoot) canvases.Add(cv);
            if (traceUi)
                foreach (var cv in canvases)
                {
                    var t = cv.GameObject.Transform;
                    Log.Line($"TRACE canvas {cv.GameObject.Name} mode={cv.Mode} cam={Objects.Of(cv.WorldCamera)?.Name} plane={cv.PlaneDistance} sf={cv.ScaleFactor} layer={cv.GameObject.Layer} pos={t.Position} scale={t.LossyScale} parent={t.Parent?.GameObject.Name} rect={(t as NRectTransform)?.Rect}");
                }
            foreach (var cam in cams)
            {
                if (cam.Target != null) continue;   // render textures: not drawn by the preview yet
                var v = NewView(cam, cam.PixelRect);
                var xf = CamXform.Of(cam);
                int seq = 0;
                foreach (var sr in renderers)
                {
                    if ((cam.CullingMask & (1 << sr.GameObject.Layer)) == 0) continue;
                    var it = SpriteItem(cam, xf, sr);
                    if (it == null) continue;
                    it.Sequence = seq++;
                    v.Items.Add(it);
                }
                foreach (var cv in canvases)
                {
                    bool mine = cv.Mode == RenderMode.WorldSpace || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera != null && Objects.Of(cv.WorldCamera) == cam)
                        || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera == null && false);
                    if (!mine || (cam.CullingMask & (1 << cv.GameObject.Layer)) == 0) continue;
                    foreach (var it in CanvasItems(cam, xf, cv)) { it.Sequence = seq++; v.Items.Add(it); }
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
            var overlay = NewView(null, new Rect(0, 0, Icalls.ScreenWidth, Icalls.ScreenHeight));
            canvases.Sort((a, b) => { int c = SortingLayers.Index(a.SortingLayerID).CompareTo(SortingLayers.Index(b.SortingLayerID)); return c != 0 ? c : a.SortingOrder.CompareTo(b.SortingOrder); });
            int oseq = 0;
            foreach (var cv in canvases)
                if (cv.Mode == RenderMode.ScreenSpaceOverlay || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera == null))
                    foreach (var it in CanvasItems(null, default, cv)) { it.Sequence = oseq++; overlay.Items.Add(it); }
            if (overlay.Items.Count > 0) views.Add(overlay);
            return views;
        }

        static Vector2 ToScreen(in CamXform xf, Vector3 world)
        {
            if (!xf.Has) return new Vector2(world.x, world.y);
            var vp = xf.Viewport(world);
            var p = xf.Pixel;
            return new Vector2(p.x + vp.x * p.width, p.y + vp.y * p.height);
        }

        static Item SpriteItem(NCamera cam, in CamXform xf, NSpriteRenderer sr)
        {
            var info = Sprites.Of(sr.Sprite);
            if (info == null || info.Vertices.Length == 0 || info.Triangles.Length == 0) return null;
            var m = sr.GameObject.Transform.LocalToWorld;
            var pos = sr.GameObject.Transform.Position;
            float depth = xf.Viewport(pos).z;
            if (depth < cam.Near || depth > cam.Far) return null;   // outside the clip planes
            bool mapped = HostTransforms && xf.Ortho;
            float a00 = 0, a01 = 0, b0 = 0, a10 = 0, a11 = 0, b1 = 0;
            if (mapped)
            {
                // One map for the whole sprite (the camera is orthographic, its z is 0): the host applies it per vertex.
                var pvm = xf.ProjView * m;
                float hx = 0.5f * xf.Pixel.width, hy = 0.5f * xf.Pixel.height, fx = sr.FlipX ? -1f : 1f, fy = sr.FlipY ? -1f : 1f;
                a00 = hx * pvm.m00 * fx; a01 = hx * pvm.m01 * fy; b0 = xf.Pixel.x + hx * (pvm.m03 + 1f);
                a10 = hy * pvm.m10 * fx; a11 = hy * pvm.m11 * fy; b1 = xf.Pixel.y + hy * (pvm.m13 + 1f);
                // Wholly outside the camera's pixels (its box through the map, a pixel to spare): the host
                // would skip it after the glue copied it. In the first run-and-gun level 371 items and
                // 202,000 vertices a frame were handed over for 10,000 drawn.
                var b = info.LocalBounds;
                float cx = 0.5f * (b.x + b.z), cy = 0.5f * (b.y + b.w), ex = 0.5f * (b.z - b.x), ey = 0.5f * (b.w - b.y);
                float sx = a00 * cx + a01 * cy + b0, rx = Math.Abs(a00) * ex + Math.Abs(a01) * ey + 1f;
                float sy = a10 * cx + a11 * cy + b1, ry = Math.Abs(a10) * ex + Math.Abs(a11) * ey + 1f;
                var px = xf.Pixel;
                if (sx + rx < px.x || sx - rx > px.x + px.width || sy + ry < px.y || sy - ry > px.y + px.height) return null;
            }
            if (!Sprites.Binding(sr.Sprite, out var tex, out _, out _)) tex = null;
            var uv = Sprites.RenderUvs(sr.Sprite);
            var it = NewItem();
            if (mapped)
            {
                it.A00 = a00; it.A01 = a01; it.B0 = b0;
                it.A10 = a10; it.A11 = a11; it.B1 = b1;
                it.Local = info.Vertices;
            }
            else
            {
                var screen = it.ScreenOf(info.Vertices.Length);
                for (int i = 0; i < screen.Length; i++)
                {
                    var v = info.Vertices[i];
                    if (sr.FlipX) v.x = -v.x;
                    if (sr.FlipY) v.y = -v.y;
                    screen[i] = ToScreen(xf, m.MultiplyPoint3x4(new Vector3(v.x, v.y, 0)));
                }
                it.Screen = screen;
            }
            it.BaseName = sr.GameObject.Name; it.SpriteName = info.Name; it.Texture = tex;
            it.Material = sr.Materials.Length > 0 ? sr.Materials[0] : null;
            it.Uv = uv; it.Triangles = info.Triangles32; it.Tint = sr.Color;
            it.Layer = SortingLayers.Index(sr.SortingLayerID); it.Order = sr.SortingOrder;
            it.Depth = depth; it.GoLayer = sr.GameObject.Layer;
            return it;
        }

        static List<Item> CanvasItems(NCamera cam, CamXform xf, NCanvas root)
        {
            var list = canvasOut;
            list.Clear();
            int depth = 0;
            int layer = SortingLayers.Index(root.SortingLayerID), order = root.SortingOrder;
            float d = cam != null ? xf.Viewport(root.GameObject.Transform.Position).z : 0f;
            // A canvas on or behind the camera's near plane (or past the far one) is clipped away.
            if (cam != null && (d < cam.Near || d > cam.Far)) return list;
            void Walk(NTransform t, float above)
            {
                var go = t.GameObject;
                if (!go.ActiveSelf) return;
                float alpha = NCanvasRenderer.AlphaBelow(go, above);
                List<Item> pops = null;
                // Indexed loops: the translated List enumerators were 5% of a world map frame.
                var comps = go.Components;
                for (int ci = 0; ci < comps.Count; ci++)
                    if (comps[ci] is NCanvasRenderer cr && !cr.Destroyed)
                    {
                        cr.Depth = depth++;
                        if (cr.Cull || cr.Mesh == null || cr.Mesh.Vertices.Length == 0) continue;
                        // Wholly transparent and writing no stencil: nothing to draw. The world
                        // map's hidden menus (CanvasGroups at 0) were 316 of its 323 UI items,
                        // 6,700 vertices transformed a frame.
                        if ((alpha == 0f || cr.Color.a == 0f) && !WritesStencil(cr)) continue;
                        var mesh = cr.Mesh;
                        var mtx = t.LocalToWorld;
                        // The first item made here owns the arrays; the renderer's other submeshes and pops share them.
                        var owner = NewItem();
                        var screen = owner.ScreenOf(mesh.Vertices.Length);
                        for (int i = 0; i < screen.Length; i++) screen[i] = ToScreen(xf, mtx.MultiplyPoint3x4(mesh.Vertices[i]));
                        var uv = owner.UvOf(screen.Length);
                        bool ownerUsed = false;
                        var uv0 = mesh.Uv[0];
                        for (int i = 0; i < uv.Length; i++) uv[i] = uv0 != null && i < uv0.Length ? new Vector2(uv0[i].x, uv0[i].y) : Vector2.zero;
                        var tint = cr.Color; tint.a *= cr.InheritedAlpha;
                        for (int sm = 0; sm < mesh.SubMeshes.Count; sm++)
                        {
                            if (mesh.SubMeshes[sm].Length == 0) continue;
                            var mat = sm < cr.Materials.Length ? cr.Materials[sm] : null;
                            var tex = sm == 0 && cr.Texture != null ? cr.Texture : MainTexture(mat);
                            if (tex == null && traceUi)
                                foreach (var c2 in go.Components)
                                    if (c2.Managed != null && c2.Managed.GetType().Name == "Image")
                                    {
                                        var sp = c2.Managed.GetType().GetField("m_Sprite", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(c2.Managed) as Sprite;
                                        var si = Sprites.Of(sp);
                                        Log.Line($"TRACE untextured {go.Name}: sprite={(sp == null ? "null" : si?.Name)} tags={string.Join(",", si?.AtlasTags ?? new string[0])} included={si?.IncludedAtlas != null} key={si?.RenderKey} bound={Sprites.Binding(sp, out _, out _, out _)}");
                                    }
                            var it = ownerUsed ? NewItem() : owner;
                            ownerUsed = true;
                            it.BaseName = go.Name; it.Texture = tex; it.Material = mat; it.Screen = screen; it.Uv = uv; it.Colors = mesh.Colors;
                            it.Triangles = mesh.SubMeshes[sm]; it.Tint = tint; it.Layer = layer; it.Order = order; it.Depth = d; it.Kind = "ui";
                            it.GoLayer = go.Layer; it.Stencil = StencilOf(mat);
                            list.Add(it);
                        }
                        // Pop materials draw after the children (a Mask clearing its stencil).
                        foreach (var pm in cr.PopMaterials)
                            if (pm != null && mesh.SubMeshes.Count > 0)
                            {
                                var it = ownerUsed ? NewItem() : owner;
                                ownerUsed = true;
                                it.BaseName = go.Name; it.Suffix = " (pop)"; it.Texture = cr.Texture; it.Material = pm; it.Screen = screen; it.Uv = uv;
                                it.Colors = mesh.Colors; it.Triangles = mesh.SubMeshes[0]; it.Tint = tint; it.Layer = layer; it.Order = order; it.Depth = d;
                                it.Kind = "ui"; it.GoLayer = go.Layer; it.Stencil = StencilOf(pm);
                                (pops ??= new List<Item>()).Add(it);
                            }
                    }
                var kids = t.Children;
                for (int k = 0; k < kids.Count; k++) Walk(kids[k], alpha);
                if (pops != null) list.AddRange(pops);
            }
            var rt = root.GameObject.Transform;
            Walk(rt, rt.Parent != null ? NCanvasRenderer.AlphaFrom(rt.Parent) : 1f);
            return list;
        }

        /// <summary>A renderer that writes the stencil (a UGUI mask, or its pop): drawn even when transparent.</summary>
        static bool WritesStencil(NCanvasRenderer cr)
        {
            foreach (var m in cr.Materials) if (StencilOf(m) is int[] s && s[2] != 0) return true;
            foreach (var pm in cr.PopMaterials) if (StencilOf(pm) is int[] s && s[2] != 0) return true;
            return false;
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
