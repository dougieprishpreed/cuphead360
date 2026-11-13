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
            /// <summary>
            /// A mapped sprite's renderer (its instance id; 0 none): the console host matches items to the last
            /// frame's by it (and by their order among its items) for the frame it draws in between (ConsoleHost's
            /// locked 30).
            /// </summary>
            public int Key;
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
            /// <summary>
            /// Sprite masks: a mask item (Kind "mask", its sprite's alpha at or above Tint.a) adds one to the
            /// stencil (MaskAdd) or takes one away (MaskRemove); a sprite item that interacts with masks draws
            /// where the stencil is 1 or more (MaskInside) or 0 (MaskOutside).
            /// </summary>
            public int SpriteStencil;
            public const int MaskAdd = 1, MaskRemove = 2, MaskInside = 3, MaskOutside = 4;
            /// <summary>A blit's mode (Effects), fixed when it was recorded; -1: the material's shader's.</summary>
            public int Effect = -1;
            /// <summary>A blit's values (Effects.ParamCount), taken when it was recorded (owned, reused).</summary>
            public float[] P;
            /// <summary>A second texture the effect samples (the film grain's overlay).</summary>
            public Texture Tex2;
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
        static readonly List<NSpriteMask> masks = new List<NSpriteMask>();
        static readonly List<NMeshRenderer> meshRenderers = new List<NMeshRenderer>();
        static readonly List<Item> canvasOut = new List<Item>();

        static Item NewItem()
        {
            if (pooled == pool.Count) pool.Add(new Item());
            var it = pool[pooled++];
            it.BaseName = ""; it.Suffix = null; it.SpriteName = null; it.Texture = null; it.Material = null; it.Screen = null; it.Local = null; it.Uv = null; it.Colors = null;
            it.Triangles = null; it.Tint = Color.white; it.Layer = it.Order = 0; it.Depth = 0; it.Sequence = 0; it.Kind = "sprite"; it.GoLayer = 0;
            it.Stencil = null; it.SpriteStencil = 0; it.Effect = -1; it.Tex2 = null; it.Key = 0;
            return it;
        }

        static View NewView(NCamera cam, Rect rect, RenderTexture target = null)
        {
            if (viewsUsed == viewPool.Count) viewPool.Add(new View());
            var v = viewPool[viewsUsed++];
            v.Camera = cam; v.PixelRect = rect; v.Items.Clear();
            v.Target = target; v.IsBlit = false; v.Group = group; v.Resume = false;
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
            /// <summary>What it draws into: a render texture, or null for the screen.</summary>
            public RenderTexture Target;
            /// <summary>A blit (Camera null): one quad over the target that replaces it.</summary>
            public bool IsBlit;
            /// <summary>The camera it belongs to this frame (its view, its image effects' blits).</summary>
            public int Group;
            /// <summary>Goes on in its target with what it held (after a grab's copy): no clear.</summary>
            public bool Resume;
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

        /// <summary>Behaviours with OnWillRenderObject (SpriteErrorManager: the hand-drawn error frames).</summary>
        public static readonly List<NBehaviour> WillRender = new List<NBehaviour>();
        static readonly List<NCamera> callbackCams = new List<NCamera>();

        /// <summary>
        /// Before a frame is drawn, each camera in depth order: Camera.onPreCull, OnWillRenderObject on the
        /// scripts of each renderer it sees, Camera.onPreRender (TextMesh Pro rebuilds its text meshes there).
        /// Every frame, drawn or not, so the game runs the same with and without a renderer.
        /// </summary>
        public static void PreRender()
        {
            callbackCams.Clear();
            foreach (var c in Typed.Cameras)
                if (c.Enabled && c.GameObject != null && c.GameObject.ActiveInHierarchy) callbackCams.Add(c);
            // Cameras with a target texture first (Unity renders them before the others), each by depth.
            callbackCams.Sort((a, b) => a.Target != null != (b.Target != null) ? (a.Target != null ? -1 : 1) : a.Depth.CompareTo(b.Depth));
            if (WillRender.Count > 0) WillRender.RemoveAll(b => b.Destroyed);
            foreach (var cam in callbackCams)
            {
                var managed = (Camera)cam.Managed;
                Camera.onPreCull?.Invoke(managed);
                if (WillRender.Count > 0)
                {
                    var xf = CamXform.Of(cam);
                    for (int i = 0; i < WillRender.Count; i++)
                    {
                        var b = WillRender[i];
                        if (b.Destroyed || !b.Enabled || b.GameObject == null || !b.GameObject.ActiveInHierarchy || (cam.CullingMask & (1 << b.GameObject.Layer)) == 0) continue;
                        if (!Seen(cam, xf, b.GameObject)) continue;
                        try { b.Messages.OnWillRenderObject.Invoke(b.Managed, null); }
                        catch (System.Reflection.TargetInvocationException e) { Exceptions.Report(e.InnerException ?? e, b, "OnWillRenderObject"); }
                    }
                }
                Camera.onPreRender?.Invoke(managed);
            }
        }

        /// <summary>After the frame: Camera.onPostRender for the same cameras.</summary>
        public static void PostRender()
        {
            if (Camera.onPostRender == null) return;
            foreach (var cam in callbackCams) if (!cam.Destroyed) Camera.onPostRender((Camera)cam.Managed);
        }

        /// <summary>Whether a camera sees the GameObject's sprite (its box through the camera, as the culling in SpriteItem).</summary>
        static bool Seen(NCamera cam, in CamXform xf, NGameObject go)
        {
            NSpriteRenderer sr = null;
            for (int i = 0; i < go.Components.Count; i++) if (go.Components[i] is NSpriteRenderer s) { sr = s; break; }
            if (sr == null) return true;
            return SpriteSeen(cam, xf, sr);
        }

        static int visibleFrame = -1;
        static readonly List<(NCamera cam, CamXform xf)> visibleCams = new List<(NCamera, CamXform)>();

        /// <summary>
        /// AssetUnload's pass: the lists and pooled items kept from frame to frame let go of what they held. A
        /// level's UICamera stayed in visibleCams on the map (no isVisible asked there), and through its parent
        /// the level's whole Level_UI, 727 objects with their text meshes and glyphs: the heap left after each
        /// unload grew 61 -> 106 MB over the twenty-fifth run, the textures squeezed to 60-80 MB.
        /// </summary>
        public static void ForgetScene()
        {
            visibleCams.Clear();
            visibleFrame = -1;
            renderers.Clear(); canvases.Clear(); masks.Clear(); meshRenderers.Clear();
            canvasOut.Clear(); meshOut.Clear(); callbackCams.Clear(); fxChain.Clear();
            views.Clear();
            foreach (var v in viewPool) { v.Camera = null; v.Target = null; v.Items.Clear(); }
            foreach (var it in pool) { it.Texture = null; it.Material = null; it.Tex2 = null; }
            WillRender.RemoveAll(b => b.Destroyed);
            Current = null;
        }

        /// <summary>
        /// Renderer.isVisible: a camera this frame sees it (Unity: it was drawn by a camera). The run-and-gun
        /// levels' flying enemies switch their colliders on only while visible; every enabled renderer counted
        /// as visible, so they could be hit, and collided, off the screen.
        /// </summary>
        public static bool IsVisible(NRenderer r)
        {
            if (r == null || !r.Enabled || r.GameObject == null || !r.GameObject.ActiveInHierarchy) return false;
            if (!(r is NSpriteRenderer sr)) return true;   // other renderers: no bounds worked out here
            if (visibleFrame != Clock.FrameCount)
            {
                visibleFrame = Clock.FrameCount;
                visibleCams.Clear();
                foreach (var c in Typed.Cameras)
                    if (c.Enabled && c.GameObject != null && c.GameObject.ActiveInHierarchy) visibleCams.Add((c, CamXform.Of(c)));
            }
            int layer = 1 << r.GameObject.Layer;
            foreach (var (cam, xf) in visibleCams)
                if ((cam.CullingMask & layer) != 0 && SpriteSeen(cam, xf, sr)) return true;
            return false;
        }

        /// <remarks>
        /// A renderer with no sprite has Unity's empty bounds at its transform (Renderer.bounds here too) and is seen
        /// when that point is: Treetop Trouble's flying beetles draw a child sprite and switch their colliders on by
        /// their own sprite-less renderer's isVisible, so they could be neither shot nor touched.
        /// </remarks>
        static bool SpriteSeen(NCamera cam, in CamXform xf, NSpriteRenderer sr)
        {
            if (!sr.Enabled) return false;
            Vector4 b = default;
            if (sr.Sprite != null)
            {
                var info = Sprites.Of(sr.Sprite);
                if (info == null || info.Vertices.Length == 0) return false;
                b = info.LocalBounds;
            }
            var t = sr.GameObject.Transform;
            float depth = xf.Viewport(t.Position).z;
            if (depth < cam.Near || depth > cam.Far) return false;
            if (!xf.Ortho) return true;
            var pvm = xf.ProjView * t.LocalToWorld;
            float hx = 0.5f * xf.Pixel.width, hy = 0.5f * xf.Pixel.height, fx = sr.FlipX ? -1f : 1f, fy = sr.FlipY ? -1f : 1f;
            float a00 = hx * pvm.m00 * fx, a01 = hx * pvm.m01 * fy, b0 = xf.Pixel.x + hx * (pvm.m03 + 1f);
            float a10 = hy * pvm.m10 * fx, a11 = hy * pvm.m11 * fy, b1 = xf.Pixel.y + hy * (pvm.m13 + 1f);
            float cx = 0.5f * (b.x + b.z), cy = 0.5f * (b.y + b.w), ex = 0.5f * (b.z - b.x), ey = 0.5f * (b.w - b.y);
            float sx = a00 * cx + a01 * cy + b0, rx = Math.Abs(a00) * ex + Math.Abs(a01) * ey + 1f;
            float sy = a10 * cx + a11 * cy + b1, ry = Math.Abs(a10) * ex + Math.Abs(a11) * ey + 1f;
            var px = xf.Pixel;
            return !(sx + rx < px.x || sx - rx > px.x + px.width || sy + ry < px.y || sy - ry > px.y + px.height);
        }

        /// <summary>
        /// The host's read-ahead of a texture (the console's: ConsoleHost): the pages of sprites within half a
        /// screen of a camera's view, and of every frame of the clip a sprite on screen plays. Pages were read
        /// as they came into view, their frames waiting (most of the twentieth run's hitches in the levels).
        /// </summary>
        /// <remarks>The texture and the sprite's uv box (min u, min v, max u, max v): the host reads the sprite
        /// page holding it (an atlas page's sprites have pages of their own on the console).</remarks>
        public static Action<Texture2D, Vector4> ReadAhead;
        const int ClipAheadFrames = 120;   // a clip's pages asked again after this (still wanted: kept ahead of older ones)
        const int SpriteAheadFrames = 30;  // a sprite near the view asked again after this

        static void Ahead(Sprite s)
        {
            var info = s != null ? Sprites.Of(s) : null;
            if (info == null || (!sceneAsk && Clock.FrameCount - info.AheadFrame < SpriteAheadFrames)) return;
            info.AheadFrame = Clock.FrameCount;
            var tex = Sprites.BoundTexture(s);
            if (tex == null) return;
            ReadAhead(tex, Sprites.UvBox(s));
        }

        /// <summary>
        /// Every sprite the active scene's objects show, or their animators' clips play, active or not, read
        /// ahead: the console's boot reads the logo's and the title's pages while the screen is still black
        /// (ConsoleHost's hold). The number of sprites asked for; unbound: those whose atlas isn't here, asked for
        /// (Atlases.Touch, as a draw would: the DLC title's came only as it was revealed). The fonts its texts use are
        /// read too (the slot select's came from the drive as its menu text appeared).
        /// </summary>
        public static int ReadAheadScene(out int unbound, bool languageFonts = false)
        {
            unbound = 0;
            if (ReadAhead == null) return 0;
            int handle = Scenes.ActiveHandle, n = 0, missing = 0;
            void One(Sprite s)
            {
                if (s == null) return;
                var tex = Sprites.BoundTexture(s);
                if (tex == null) { Atlases.Touch(s); missing++; return; }
                ReadAhead(tex, Sprites.UvBox(s));
                n++;
            }
            FontsAhead(handle, languageFonts);
            foreach (var go in Typed.GameObjects)
            {
                // the persistent objects' too: the scene loader's iris wipe (16 textures) came from the drive as the
                // title screen appeared and as the wipe played
                if (go.IsAsset || (go.Scene != handle && go.Scene != Scenes.DontDestroy.Handle)) continue;
                foreach (var c in go.Components)
                {
                    if (c is NSpriteRenderer sr) One(sr.Sprite);
                    else if (c is NAnimator a && a.Controller != null && ControllerData.Of(a.Controller) is ControllerData d)
                        foreach (var clip in d.Clips) { if (clip != null) foreach (var o in clip.PPtrs) One(o as Sprite); }
                    else if (c.Managed is UnityEngine.UI.Image img) One(img.sprite);
                    else if (c.Managed is UnityEngine.UI.Text text && text.font != null) FontState.Of(text.font);
                }
            }
            unbound = missing;
            return n;
        }

        /// <summary>
        /// A level's first frames (ConsoleHost's level hold): the sprites its active renderers and images show
        /// and the clips its animators play now, read ahead (unbound: sprites whose atlas isn't here yet).
        /// Not every clip, as ReadAheadScene does for the title: a fight's would be all of its pages.
        /// </summary>
        public static int ReadAheadFirst(out int unbound)
        {
            unbound = 0;
            if (ReadAhead == null) return 0;
            int handle = Scenes.ActiveHandle, n = 0, missing = 0;
            var reach = Reach(CurrentReach(), out bool limited);
            foreach (var go in Typed.GameObjects)
            {
                if (go.IsAsset || go.Scene != handle || !go.ActiveInHierarchy) continue;
                foreach (var c in go.Components)
                {
                    Sprite s = null;
                    if (c is NAnimator a) { if (!limited || InBoxes(reach, go.Transform.Position)) a.ReadAheadCurrent(); continue; }
                    if (c is NSpriteRenderer sr) s = sr.Sprite;
                    else if (c.Managed is UnityEngine.UI.Image img) s = img.sprite;
                    if (s == null || (limited && c is NSpriteRenderer && !SpriteInBoxes(reach, go, s))) continue;
                    var tex = Sprites.BoundTexture(s);
                    if (tex == null) { Atlases.Touch(s); missing++; continue; }
                    ReadAhead(tex, Sprites.UvBox(s));
                    n++;
                }
            }
            unbound = missing;
            return n;
        }

        /// <summary>The host's textures still being read or queued (the console's; null elsewhere).</summary>
        public static Func<int> TexturesPending;

        /// <summary>
        /// A level or map being loaded, before its activation (the hourglass still up: SceneLoad's last stage):
        /// what its active renderers and images show and the clips its animators start with, read ahead, those
        /// nearest the scene's camera first. Levels began with their pages read as their first frames drew
        /// them (the Frogs' 234, 308 and 196 ms frames), and the map after a level read back what the level
        /// had let go as it showed it (13-17 hitches a 1,000 frames against 3 on its first visit, the
        /// twenty-eighth run). The sprites and clips asked for.
        /// </summary>
        // Active once its scene is (ActiveInHierarchy says no while the scene is still being built).
        static bool ActiveOnceBuilt(NGameObject go)
        {
            for (var t = go.Transform; t != null; t = t.Parent)
                if (!t.GameObject.ActiveSelf) return false;
            return true;
        }

        public static (int sprites, int clips) ReadAheadLoad(int sceneHandle, bool fill = false)
        {
            if (ReadAhead == null) return (0, 0);
            SceneReadAhead?.Invoke(true);
            sceneAsk = true;
            try
            {
                FontsAhead(sceneHandle); PersistentAhead();
                var r = LoadAhead(sceneHandle);
                Filled = fill && fillOn ? FillAhead(sceneHandle) : 0;
                return r;
            }
            finally { sceneAsk = false; SceneReadAhead?.Invoke(false); }
        }

        /// <summary>
        /// The host's keeping of a page's stored bytes (the console's: LZ4, a quarter to a fifth of the page) in its memory,
        /// without decoding it: a page drawn later decodes from there in 6-25 ms instead of 50-300 ms from the drive.
        /// </summary>
        public static Action<Texture2D, Vector4> ReadAheadStored;
        /// <summary>The sprites the last load's fill asked for.</summary>
        public static int Filled;
        /// <summary>
        /// The fill is off unless CH360_FILL=1 (with the console host's STORED_ARENA): tools/texture_cache_sim.py over the
        /// PC traces of Hilda Berg, Forest Follies and Treetop found its pages too far from what the fights draw (its
        /// first 32 MB held 27 MB of Hilda's 43), and its block's room taken from the decoded pages cost more waits
        /// than it saved (Forest Follies 21 -> 45 frames late at the console's room).
        /// </summary>
        static readonly bool fillOn = Environment.GetEnvironmentVariable("CH360_FILL") == "1";

        /// <summary>
        /// A level's load (under the hourglass, after its first pages are asked for): every sprite its objects show and
        /// every clip of their animators, active or not, and of the prefabs' animators the load read (bullets, a boss's
        /// later forms), their pages' stored bytes kept by the host. The thirty-ninth run's fights waited on pages first
        /// drawn with no read-ahead (Hilda Berg 107 loaded on demand, 63 of her 76 hitches): a boss's phases start their
        /// animations by script and its spawns appear at once, and a page from the drive took 50-300 ms. The host keeps
        /// what fits its block (the scene's own first, in this order: the scene's objects nearest the camera first, then
        /// the prefabs'), and decodes a page from it when drawn.
        /// </summary>
        static int FillAhead(int sceneHandle)
        {
            if (ReadAheadStored == null) return 0;
            int n = 0;
            var clipsSeen = new HashSet<ClipData>();
            var spritesSeen = new HashSet<Sprite>();
            void Guess(Sprite s)
            {
                if (s == null || !spritesSeen.Add(s)) return;
                var tex = Sprites.BoundTexture(s);
                if (tex == null) return;
                ReadAheadStored(tex, Sprites.UvBox(s));
                n++;
            }
            void Clip(ClipData clip)
            {
                if (clip == null || !clipsSeen.Add(clip)) return;
                foreach (var o in clip.PPtrs) if (o is Sprite s) Guess(s);
            }
            Vector3 eye = default;
            foreach (var c in Typed.Cameras)
                if (!c.Destroyed && c.GameObject != null && c.GameObject.Scene == sceneHandle) { eye = c.GameObject.Transform.Position; break; }
            var near = new List<(float d, NGameObject go)>();
            foreach (var go in Typed.GameObjects)
            {
                if (go.IsAsset || go.Scene != sceneHandle) continue;
                var p = go.Transform.Position;
                near.Add(((p.x - eye.x) * (p.x - eye.x) + (p.y - eye.y) * (p.y - eye.y), go));
            }
            near.Sort((x, y) => x.d.CompareTo(y.d));
            foreach (var (_, go) in near)
                foreach (var c in go.Components)
                {
                    if (c is NSpriteRenderer sr) Guess(sr.Sprite);
                    else if (c.Managed is UnityEngine.UI.Image img) Guess(img.sprite);
                    // built ones only (Peek): building an inactive animator's here changed what the unload pass kept
                    else if (c is NAnimator a && a.Controller != null && ControllerData.Peek(a.Controller) is ControllerData d)
                        foreach (var clip in d.Clips) Clip(clip);
                }
            foreach (var d in ControllerData.WarmedKeep)
                if (d != null) foreach (var clip in d.Clips) Clip(clip);
            return n;
        }

        // A new scene's read-ahead: every sprite and clip asked for again, however lately (what isn't asked for may
        // be let go to make its room).
        static bool sceneAsk;

        // The persistent objects' sprites and every clip of their animators (the scene loader's iris wipe, which
        // opens on the new scene): asked for with it, so the host's making room for it never lets them go.
        static void PersistentAhead()
        {
            int handle = Scenes.DontDestroy.Handle;
            foreach (var go in Typed.GameObjects)
            {
                if (go.IsAsset || go.Scene != handle) continue;
                foreach (var c in go.Components)
                {
                    Sprite s = null;
                    if (c is NSpriteRenderer sr) s = sr.Sprite;
                    else if (c.Managed is UnityEngine.UI.Image img) s = img.sprite;
                    else if (c is NAnimator a && a.Controller != null && ControllerData.Of(a.Controller) is ControllerData d)
                        foreach (var clip in d.Clips) AheadClip(clip);
                    if (s != null && Sprites.BoundTexture(s) != null) Ahead(s);
                }
            }
        }

        /// <summary>
        /// The host's mark of a new scene's read-ahead (the console's: what the scene before drew lately is let go
        /// first, its room made only once the new one has asked): true as it begins, false as it ends.
        /// </summary>
        public static Action<bool> SceneReadAhead;

        static (int sprites, int clips) LoadAhead(int sceneHandle)
        {
            Vector3 eye = default;
            var cams = new List<NCamera>();
            foreach (var c in Typed.Cameras)
                if (!c.Destroyed && c.GameObject != null && c.GameObject.Scene == sceneHandle)
                {
                    if (cams.Count == 0) eye = c.GameObject.Transform.Position;
                    if (c.Enabled && ActiveOnceBuilt(c.GameObject)) cams.Add(c);
                }
            var reach = Reach(cams, out bool limited);
            var sprites = new List<(float d, Sprite s)>();
            var clips = new List<(float d, ClipData c)>();
            foreach (var go in Typed.GameObjects)
            {
                if (go.IsAsset || go.Scene != sceneHandle || !ActiveOnceBuilt(go)) continue;
                var p = go.Transform.Position;
                float d = (p.x - eye.x) * (p.x - eye.x) + (p.y - eye.y) * (p.y - eye.y);
                foreach (var c in go.Components)
                {
                    if (c is NSpriteRenderer sr) { if (sr.Sprite != null && (!limited || SpriteInBoxes(reach, go, sr.Sprite))) sprites.Add((d, sr.Sprite)); }
                    else if (c.Managed is UnityEngine.UI.Image img) { if (img.sprite != null) sprites.Add((d, img.sprite)); }
                    else if (c is NAnimator a && a.Controller != null && (!limited || InBoxes(reach, p)) && ControllerData.Of(a.Controller) is ControllerData cd)
                        foreach (var clip in cd.StartClips()) clips.Add((d, clip));
                }
            }
            sprites.Sort((x, y) => x.d.CompareTo(y.d));
            clips.Sort((x, y) => x.d.CompareTo(y.d));
            int si = 0, ci = 0;
            // nearest first, sprites and clips in turn
            while (si < sprites.Count || ci < clips.Count)
            {
                if (si < sprites.Count && (ci >= clips.Count || sprites[si].d <= clips[ci].d))
                {
                    var s = sprites[si++].s;
                    if (Sprites.BoundTexture(s) == null) Atlases.Touch(s);
                    else Ahead(s);
                }
                else AheadClip(clips[ci++].c);
            }
            return (sprites.Count, clips.Count);
        }

        /// <summary>A clip's sprites read ahead (an animator's next states: NAnimator.ReadAheadNext).</summary>
        internal static void AheadClipOf(ClipData clip) { if (ReadAhead != null) AheadClip(clip); }

        static void AheadClip(ClipData clip)
        {
            if (clip == null || (!sceneAsk && Clock.FrameCount - clip.AheadFrame < ClipAheadFrames)) return;
            clip.AheadFrame = Clock.FrameCount;
            foreach (var o in clip.PPtrs)
                if (o is Sprite s) Ahead(s);
                else if (o is Texture2D t) ReadAhead(t, new Vector4(0, 0, 1, 1));   // a texture curve's (the scene loader's iris wipe)
        }

        /// <summary>
        /// The read-ahead's reach: each orthographic camera's view of the world (one that draws the default layer:
        /// not a UI camera's), with a screen and a half more to each side and a screen above and below. A load's
        /// sprites and clips, a level's first frames' and an animator's next clips are read ahead only there:
        /// every animator of a run-and-gun level read its next clips wherever it was, and the load read the whole
        /// level's, which kept memory full of pages screens away (the thirty-sixth run's Forest Follies: 276
        /// textures loaded on demand and 495 read again, its first seconds waiting on pages, against the Frogs' 32
        /// and 91). No such camera: everywhere. CH360_AHEAD_FAR=1: everywhere (a PC check of the difference).
        /// </summary>
        static readonly bool aheadFar = Environment.GetEnvironmentVariable("CH360_AHEAD_FAR") == "1";
        static readonly List<Vector4> reachNow = new List<Vector4>();
        static readonly List<NCamera> reachCams = new List<NCamera>();
        static int reachFrame = -1;
        static bool reachLimited;

        static List<NCamera> CurrentReach()
        {
            reachCams.Clear();
            foreach (var c in Typed.Cameras)
                if (!c.Destroyed && c.Enabled && c.GameObject != null && c.GameObject.ActiveInHierarchy) reachCams.Add(c);
            return reachCams;
        }

        static List<Vector4> Reach(List<NCamera> cams, out bool limited)
        {
            var boxes = new List<Vector4>();
            foreach (var c in cams)
            {
                if (!c.Ortho || c.Target != null || (c.CullingMask & 1) == 0) continue;
                var e = c.GameObject.Transform.Position;
                float hy = c.OrthoSize, hx = hy * c.Aspect;
                boxes.Add(new Vector4(e.x - 4f * hx, e.y - 3f * hy, e.x + 4f * hx, e.y + 3f * hy));
            }
            limited = !aheadFar && boxes.Count > 0;
            return boxes;
        }

        static bool InBoxes(List<Vector4> boxes, Vector3 p)
        {
            foreach (var b in boxes) if (p.x >= b.x && p.x <= b.z && p.y >= b.y && p.y <= b.w) return true;
            return false;
        }

        // A sprite's box in the world (its bounds by its transform's position and scale; rotation ignored, the
        // larger extent taken) meeting one of the boxes: a long background's pivot can lie well off its pixels.
        static bool SpriteInBoxes(List<Vector4> boxes, NGameObject go, Sprite s)
        {
            var info = Sprites.Of(s);
            var t = go.Transform;
            var p = t.Position;
            if (info == null) return InBoxes(boxes, p);
            var sc = t.LossyScale;
            var lb = info.LocalBounds;
            float ex = Math.Max(Math.Abs(lb.x), Math.Abs(lb.z)) * Math.Abs(sc.x), ey = Math.Max(Math.Abs(lb.y), Math.Abs(lb.w)) * Math.Abs(sc.y);
            float e = Math.Max(ex, ey);
            foreach (var b in boxes) if (p.x + e >= b.x && p.x - e <= b.z && p.y + e >= b.y && p.y - e <= b.w) return true;
            return false;
        }

        /// <summary>Whether an animator's next clips are read ahead from where it is (NAnimator.ReadAheadNext).</summary>
        internal static bool InReach(Vector3 p)
        {
            if (reachFrame != Clock.FrameCount)
            {
                reachFrame = Clock.FrameCount;
                var boxes = Reach(CurrentReach(), out reachLimited);
                reachNow.Clear();
                reachNow.AddRange(boxes);
            }
            return !reachLimited || InBoxes(reachNow, p);
        }

        /// <summary>
        /// The font atlases of the scene's TextMeshPro texts, active or not, and of the persistent objects': read
        /// with the scene's read-ahead, then kept (KeepFontsAhead). They came from the drive as their texts first
        /// showed: the slot select's three (8 MB) as its menu appeared, the death card's as it did (the
        /// thirty-sixth run's title hitch).
        /// </summary>
        static readonly List<Texture2D> sceneFonts = new List<Texture2D>();
        static readonly Dictionary<Type, System.Reflection.FieldInfo> tmpFontFields = new Dictionary<Type, System.Reflection.FieldInfo>();
        static Type fontAssetType;
        static System.Reflection.FieldInfo fontAtlasField, fontFallbacksField;
        const System.Reflection.BindingFlags AnyField = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        static readonly bool traceFonts = Environment.GetEnvironmentVariable("CH360_TRACE_PREFETCH") != null;

        static void FontsAhead(int sceneHandle, bool languageFonts = false)
        {
            sceneFonts.Clear();
            foreach (var go in Typed.GameObjects)
            {
                if (go.IsAsset || (go.Scene != sceneHandle && go.Scene != Scenes.DontDestroy.Handle)) continue;
                foreach (var c in go.Components)
                    if (c.Managed != null && TmpFont(c.Managed) is object font) FontAtlases(font, 0);
            }
            if (languageFonts) LanguageFonts();
            foreach (var t in sceneFonts) ReadAhead(t, new Vector4(0, 0, 1, 1));
            if (traceFonts && sceneFonts.Count > 0)
                Log.Line($"FONTS read ahead: {string.Join(", ", sceneFonts.ConvertAll(t => t.name))}");
        }

        /// <summary>The scene's font atlases asked for again (ConsoleHost, every second): kept as if drawn lately.</summary>
        public static void KeepFontsAhead()
        {
            if (ReadAhead == null) return;
            foreach (var t in sceneFonts) if (t != null) ReadAhead(t, new Vector4(0, 0, 1, 1));
        }

        /// <summary>
        /// The fonts the game's localization gives texts in the current language (Localization.Instance.m_Fonts
        /// [language].fonts[category].fontAsset; game code, found by name), for a menu (ConsoleHost's menu hold):
        /// its texts take theirs as they first show, so their atlases weren't those the scene's texts named (the
        /// slot select's two bundle fonts, 6 MB, came from the drive as its menu appeared).
        /// </summary>
        static void LanguageFonts()
        {
            try
            {
                var loc = Type.GetType("Localization, Assembly-CSharp");
                if (loc == null) return;
                const System.Reflection.BindingFlags Static = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var inst = loc.GetProperty("Instance", Static)?.GetValue(null);
                var lang = loc.GetProperty("language", Static)?.GetValue(null);
                if (inst == null || lang == null || !(loc.GetField("m_Fonts", AnyField)?.GetValue(inst) is Array table)) return;
                int li = Convert.ToInt32(lang);
                if (li < 0 || li >= table.Length) return;
                var langFonts = table.GetValue(li);
                if (!(langFonts?.GetType().GetField("fonts", AnyField)?.GetValue(langFonts) is Array cats)) return;
                System.Reflection.PropertyInfo asset = null;
                foreach (var cat in cats)
                {
                    if (cat == null) continue;
                    asset ??= cat.GetType().GetProperty("fontAsset", AnyField);
                    if (asset?.GetValue(cat) is object font) FontAtlases(font, 0);
                }
            }
            catch (Exception e) { if (traceFonts) Log.Line("FONTS language table: " + e.GetType().Name + ": " + e.Message); }
        }

        // TMP_Text.m_fontAsset (game code: found by name, as its base classes are)
        static object TmpFont(object component)
        {
            var type = component.GetType();
            if (!tmpFontFields.TryGetValue(type, out var f))
            {
                for (var t = type; t != null && f == null; t = t.BaseType)
                    if (t.FullName == "TMPro.TMP_Text") f = t.GetField("m_fontAsset", AnyField);
                tmpFontFields[type] = f;
            }
            return f?.GetValue(component);
        }

        // TMP_FontAsset.atlas and those of its fallbackFontAssets
        static void FontAtlases(object font, int depth)
        {
            if (font == null || depth > 3) return;
            var type = font.GetType();
            if (type != fontAssetType)
            {
                fontAssetType = type;
                fontAtlasField = type.GetField("atlas", AnyField);
                fontFallbacksField = type.GetField("fallbackFontAssets", AnyField);
            }
            if (fontAtlasField?.GetValue(font) is Texture2D atlas && atlas != null && !sceneFonts.Contains(atlas)) sceneFonts.Add(atlas);
            if (fontFallbacksField?.GetValue(font) is System.Collections.IList fallbacks)
                foreach (var fb in fallbacks) FontAtlases(fb, depth + 1);
        }

        public static List<View> Build()
        {
            pooled = 0; viewsUsed = 0;
            views.Clear(); renderers.Clear(); canvases.Clear(); masks.Clear(); meshRenderers.Clear();
            var cams = Icalls.LiveCameras();
            cams.Sort((a, b) => a.Depth.CompareTo(b.Depth));   // stable: creation order breaks ties
            bool interacts = false;
            foreach (var sr in Typed.SpriteRenderers)
                if (sr.Enabled && sr.Sprite != null && sr.GameObject != null && sr.GameObject.ActiveInHierarchy)
                {
                    renderers.Add(sr);
                    if (sr.MaskInteraction != SpriteMaskInteraction.None) interacts = true;
                }
            // Masks matter only when a sprite interacts with them.
            if (interacts)
                foreach (var m in Typed.SpriteMasks)
                    if (m.Enabled && m.Sprite != null && m.GameObject != null && m.GameObject.ActiveInHierarchy) masks.Add(m);
            foreach (var mr in Typed.MeshRenderers)
                if (mr.Enabled && mr.GameObject != null && mr.GameObject.ActiveInHierarchy) meshRenderers.Add(mr);
            foreach (var cv in Typed.Canvases)
                if (cv.Enabled && cv.GameObject != null && cv.GameObject.ActiveInHierarchy && cv.IsRoot) canvases.Add(cv);
            if (traceUi)
                foreach (var cv in canvases)
                {
                    var t = cv.GameObject.Transform;
                    Log.Line($"TRACE canvas {cv.GameObject.Name} mode={cv.Mode} cam={Objects.Of(cv.WorldCamera)?.Name} plane={cv.PlaneDistance} sf={cv.ScaleFactor} layer={cv.GameObject.Layer} pos={t.Position} scale={t.LossyScale} parent={t.Parent?.GameObject.Name} rect={(t as NRectTransform)?.Rect}");
                }
            // Cameras with a target texture first (Unity renders them before the others), then the
            // screen's; each by depth.
            group = 0;
            screenTarget = null;
            foreach (var cam in cams) if (cam.Target != null) CameraViews(cam, false);
            int lastScreen = -1;
            for (int i = 0; i < cams.Count; i++) if (cams[i].Target == null) lastScreen = i;
            for (int i = 0; i < cams.Count; i++) if (cams[i].Target == null) CameraViews(cams[i], i == lastScreen);
            SplitForGrabs();
            // The picture still in a texture (the last screen camera had no image effects to take it on): to the screen.
            if (screenTarget != null)
            {
                group++;
                var copy = AddBlit(null, Icalls.ScreenWidth, Icalls.ScreenHeight);
                copy.Texture = screenTarget;
                copy.Effect = Effects.Copy;
                copy.P ??= new float[Effects.ParamCount];
                Array.Clear(copy.P, 0, copy.P.Length);
                screenTarget = null;
            }
            // Screen-space overlay canvases draw last, over every camera (and its image effects).
            group++;
            var overlay = NewView(null, new Rect(0, 0, Icalls.ScreenWidth, Icalls.ScreenHeight));
            canvases.Sort((a, b) => { int c = SortingLayers.Index(a.SortingLayerID).CompareTo(SortingLayers.Index(b.SortingLayerID)); return c != 0 ? c : a.SortingOrder.CompareTo(b.SortingOrder); });
            int oseq = 0;
            foreach (var cv in canvases)
                if (cv.Mode == RenderMode.ScreenSpaceOverlay || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera == null))
                    foreach (var it in CanvasItems(null, default, cv)) { it.Sequence = oseq++; overlay.Items.Add(it); }
            if (overlay.Items.Count > 0) views.Add(overlay);
            DropUnseenTargets();
            Effects.FinishFrame();
            if (VertStats) CountCorners(views);
            return views;
        }

        // CH360_VERT_STATS=1: each scene's frame with the most triangle corners in its list (the console's glue
        // writes each, and its vertex ring holds 174,000; Hilda Berg's night sky filled the old one), with the
        // items that had the most: a VERTS line when the scene changes.
        static readonly bool VertStats = Environment.GetEnvironmentVariable("CH360_VERT_STATS") == "1";
        static string vertScene;
        static int vertMax, vertMaxFrame;
        static string vertTop = "";

        static void CountCorners(List<View> views)
        {
            string scene = Scenes.Active?.Name;
            if (scene != vertScene)
            {
                if (vertScene != null && vertMax > 0) Log.Line($"VERTS {vertScene}: at most {vertMax} corners, frame {vertMaxFrame}: {vertTop}");
                vertScene = scene; vertMax = 0; vertTop = "";
            }
            int total = 0;
            foreach (var v in views) foreach (var it in v.Items) total += it.Triangles?.Length ?? 0;
            if (total <= vertMax) return;
            vertMax = total; vertMaxFrame = Clock.FrameCount;
            var top = new List<Item>();
            foreach (var v in views) foreach (var it in v.Items) if ((it.Triangles?.Length ?? 0) >= 1000) top.Add(it);
            top.Sort((x, y) => y.Triangles.Length.CompareTo(x.Triangles.Length));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < top.Count && i < 6; i++) sb.Append(i > 0 ? ", " : "").Append(top[i].Name).Append(' ').Append(top[i].Triangles.Length);
            vertTop = sb.ToString();
        }

        static int group;
        /// <summary>The texture the screen's picture is in after an image-effect camera that wasn't the last to draw (null: the screen).</summary>
        static RenderTexture screenTarget;

        /// <summary>
        /// A camera's view, into its target texture or the screen. With image effects (OnRenderImage) it draws
        /// into a temporary texture the effects then blit on, the last into its target; one that doesn't clear
        /// starts from what is on the screen, so the screen's views so far draw into that texture instead. A
        /// screen camera's last effect blits onto the screen only if no camera draws there after it; otherwise
        /// into a texture the later cameras draw on (Unity hands it a texture, not null: the map's and levels'
        /// pause blur, BlurOptimized, reads its size), put on the screen at the end.
        /// </summary>
        static void CameraViews(NCamera cam, bool lastOnScreen)
        {
            group++;
            var fx = Effects.ImageEffects(cam);
            var target = cam.Target;
            if (fx.Count == 0)
            {
                var v = NewView(cam, cam.PixelRect, target ?? screenTarget);
                ViewItems(cam, CamXform.Of(cam), v.Items);
                views.Add(v);
                return;
            }
            int w = target != null ? Effects.Width(target) : Icalls.ScreenWidth, h = target != null ? Effects.Height(target) : Icalls.ScreenHeight;
            bool clears = cam.ClearFlags == CameraClearFlags.SolidColor || cam.ClearFlags == CameraClearFlags.Skybox;
            RenderTexture src;
            if (target == null && !clears && screenTarget != null) src = screenTarget;   // the picture so far is already in one
            else
            {
                src = Effects.GetTemporary(w, h);
                if (!clears && target == null)
                    foreach (var pv in views) if (pv.Target == null) pv.Target = src;
            }
            if (target == null) screenTarget = null;
            var cv = NewView(cam, cam.PixelRect, src);
            ViewItems(cam, CamXform.Of(cam), cv.Items);
            views.Add(cv);
            // The scripts are copied first: one could enable or disable another.
            var chain = fxChain;
            chain.Clear();
            chain.AddRange(fx);
            var cur = src;
            for (int i = 0; i < chain.Count; i++)
            {
                var dst = i < chain.Count - 1 ? Effects.GetTemporary(w, h) : target != null || lastOnScreen ? target : Effects.GetTemporary(w, h);
                Effects.RunImageEffect(chain[i], cur, dst);
                Effects.ReleaseTemporary(cur);
                cur = dst;
            }
            if (target == null && !lastOnScreen) screenTarget = cur;
        }
        static readonly List<NBehaviour> fxChain = new List<NBehaviour>();

        static readonly HashSet<string> grabWarned = new HashSet<string>();

        /// <summary>
        /// Unity's GrabPass: before an item whose shader reads what is behind it (Effects.NeedsGrab), the camera's
        /// view stops, a blit copies its target so far into a temporary texture the item samples (consecutive such
        /// items share one), and a resumed view of the same camera goes on in the target with what it held. Only a
        /// target texture can be copied: on the screen itself (no image-effect camera after it) the item stays plain.
        /// </summary>
        static void SplitForGrabs()
        {
            for (int vi = 0; vi < views.Count; vi++)
            {
                var v = views[vi];
                if (v.IsBlit) continue;
                int k = -1;
                for (int i = 0; i < v.Items.Count; i++)
                    if (v.Items[i].Tex2 == null && Effects.NeedsGrab(Effects.ModeOf(v.Items[i]))) { k = i; break; }
                if (k < 0) continue;
                if (v.Target == null)
                {
                    if (grabWarned.Add(v.Camera?.GameObject?.Name ?? "")) Log.Line($"EFFECT a grab on the screen itself ({v.Items[k].Name}): drawn plain");
                    continue;
                }
                int w = Effects.Width(v.Target), h = Effects.Height(v.Target);
                var grab = Effects.GetTemporary(w, h);
                var copyView = NewView(null, new Rect(0, 0, w, h), grab);
                copyView.IsBlit = true; copyView.Group = v.Group;
                var it = NewItem();
                var s = it.ScreenOf(4);
                s[0] = new Vector2(0, 0); s[1] = new Vector2(w, 0); s[2] = new Vector2(w, h); s[3] = new Vector2(0, h);
                it.Screen = s; it.Uv = quadUv; it.Triangles = quadTris; it.BaseName = "Grab"; it.Kind = "blit";
                it.Texture = v.Target; it.Effect = Effects.Copy;
                it.P ??= new float[Effects.ParamCount];
                Array.Clear(it.P, 0, it.P.Length);
                copyView.Items.Add(it);
                var rest = NewView(v.Camera, v.PixelRect, v.Target);
                rest.Group = v.Group; rest.Resume = true;
                for (int i = k; i < v.Items.Count; i++) rest.Items.Add(v.Items[i]);
                v.Items.RemoveRange(k, v.Items.Count - k);
                for (int i = 0; i < rest.Items.Count && Effects.NeedsGrab(Effects.ModeOf(rest.Items[i])); i++) rest.Items[i].Tex2 = grab;
                views.Insert(vi + 1, copyView);
                views.Insert(vi + 2, rest);
                vi++;   // the next look starts at the resumed view (its later grab items)
            }
        }

        public static int ViewCount => views.Count;
        public static View ViewAt(int i) => views[i];

        static readonly int[] quadTris = { 0, 1, 2, 0, 2, 3 };
        static readonly Vector2[] quadUv = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };

        /// <summary>A blit's view: one quad over the target (w x h pixels; null the screen), its item to fill in.</summary>
        internal static Item AddBlit(RenderTexture target, int w, int h)
        {
            var v = NewView(null, new Rect(0, 0, w, h), target);
            v.IsBlit = true;
            var it = NewItem();
            var s = it.ScreenOf(4);
            s[0] = new Vector2(0, 0); s[1] = new Vector2(w, 0); s[2] = new Vector2(w, h); s[3] = new Vector2(0, h);
            it.Screen = s; it.Uv = quadUv; it.Triangles = quadTris;
            it.BaseName = "Blit"; it.Kind = "blit";
            v.Items.Add(it);
            views.Add(v);
            return it;
        }

        static readonly Dictionary<RenderTexture, int> ownerGroup = new Dictionary<RenderTexture, int>(ReferenceEqualityComparer.Instance as IEqualityComparer<RenderTexture>);
        static readonly HashSet<int> seenGroups = new HashSet<int>();

        /// <summary>
        /// A camera drawing into a texture nothing else samples this frame (the Knight's glow between
        /// flashes) is left out: Unity draws it anyway, but nothing shows it, and on the console each such
        /// view is a pass of its own and a copy out of eDRAM.
        /// </summary>
        static void DropUnseenTargets()
        {
            ownerGroup.Clear(); seenGroups.Clear();
            foreach (var v in views)
                if (!v.IsBlit && v.Camera != null && v.Camera.Target != null) ownerGroup[v.Camera.Target] = v.Group;
            if (ownerGroup.Count == 0) return;
            foreach (var v in views)
                foreach (var it in v.Items)
                {
                    if (it.Texture is RenderTexture rt && ownerGroup.TryGetValue(rt, out int g) && g != v.Group) seenGroups.Add(g);
                    if (it.Tex2 is RenderTexture rt2 && ownerGroup.TryGetValue(rt2, out int g2) && g2 != v.Group) seenGroups.Add(g2);
                }
            views.RemoveAll(v => ownerGroup.ContainsValue(v.Group) && !seenGroups.Contains(v.Group));
        }

        static readonly Comparison<Item> drawOrder = (a, b) =>
        {
            int c = a.Layer.CompareTo(b.Layer); if (c != 0) return c;
            c = a.Order.CompareTo(b.Order); if (c != 0) return c;
            if (a.Kind == "ui" && b.Kind == "ui") return a.Sequence.CompareTo(b.Sequence);
            c = b.Depth.CompareTo(a.Depth); if (c != 0) return c;   // farther first
            return a.Sequence.CompareTo(b.Sequence);
        };

        /// <summary>A camera's items in draw order.</summary>
        static void ViewItems(NCamera cam, CamXform xf, List<Item> into)
        {
                var v = into;
                int seq = 0;
                foreach (var sr in renderers)
                {
                    if ((cam.CullingMask & (1 << sr.GameObject.Layer)) == 0) continue;
                    var it = SpriteItem(cam, xf, sr);
                    if (it == null) continue;
                    it.Sequence = seq++;
                    v.Add(it);
                    // a large mesh's other strips on the view, in its order
                    for (int k = 0; k < stripItems.Count; k++) { stripItems[k].Sequence = seq++; v.Add(stripItems[k]); }
                }
                // A mask adds to the stencil after the sprites at its back point and takes it away after those
                // at its front point (Depth -infinity sorts it after everything at its layer and order), so the
                // sprites between see it; without a custom range it adds before anything the camera draws.
                foreach (var m in masks)
                {
                    if ((cam.CullingMask & (1 << m.GameObject.Layer)) == 0) continue;
                    if (m.CustomRange)
                    {
                        var add = MaskItem(cam, xf, m, m.BackLayer, m.BackOrder, Item.MaskAdd);
                        if (add == null) continue;
                        add.Sequence = seq++; v.Add(add);
                        var remove = MaskItem(cam, xf, m, m.FrontLayer, m.FrontOrder, Item.MaskRemove);
                        remove.Sequence = seq++; v.Add(remove);
                    }
                    else if (MaskItem(cam, xf, m, int.MinValue, int.MinValue, Item.MaskAdd) is Item all) { all.Sequence = seq++; v.Add(all); }
                }
                foreach (var mr in meshRenderers)
                {
                    if ((cam.CullingMask & (1 << mr.GameObject.Layer)) == 0) continue;
                    foreach (var it in MeshItems(cam, xf, mr)) { it.Sequence = seq++; v.Add(it); }
                }
                foreach (var cv in canvases)
                {
                    bool mine = cv.Mode == RenderMode.WorldSpace || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera != null && Objects.Of(cv.WorldCamera) == cam)
                        || (cv.Mode == RenderMode.ScreenSpaceCamera && cv.WorldCamera == null && false);
                    if (!mine || (cam.CullingMask & (1 << cv.GameObject.Layer)) == 0) continue;
                    foreach (var it in CanvasItems(cam, xf, cv)) { it.Sequence = seq++; v.Add(it); }
                }
                v.Sort(drawOrder);
        }

        static Vector2 ToScreen(in CamXform xf, Vector3 world)
        {
            if (!xf.Has) return new Vector2(world.x, world.y);
            var vp = xf.Viewport(world);
            var p = xf.Pixel;
            return new Vector2(p.x + vp.x * p.width, p.y + vp.y * p.height);
        }

        // SpriteItem's strips after the one it returns (a large mesh: Sprites.StripsOf).
        static readonly List<Item> stripItems = new List<Item>();

        static Item SpriteItem(NCamera cam, in CamXform xf, NSpriteRenderer sr)
        {
            stripItems.Clear();
            var info = Sprites.Of(sr.Sprite);
            if (info == null || info.Vertices.Length == 0 || info.Triangles.Length == 0) return null;
            var m = sr.GameObject.Transform.LocalToWorld;
            var pos = sr.GameObject.Transform.Position;
            float depth = xf.Viewport(pos).z;
            if (depth < cam.Near || depth > cam.Far) return null;   // outside the clip planes
            bool mapped = HostTransforms && xf.Ortho, inside = false;
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
                if (sx + rx < px.x || sx - rx > px.x + px.width || sy + ry < px.y || sy - ry > px.y + px.height)
                {
                    // a screen to each side (run-and-gun levels scroll sideways: half a screen gave a page
                    // well under a second, read at 50-80 ms each behind others), half above and below
                    float mx = px.width, my = 0.5f * px.height;
                    if (ReadAhead != null && !(sx + rx < px.x - mx || sx - rx > px.x + px.width + mx || sy + ry < px.y - my || sy - ry > px.y + px.height + my))
                    {
                        Ahead(sr.Sprite);
                        // and the clip it plays (an animator coming into reach: its next clips are read from there)
                        AheadClip(sr.Clip);
                    }
                    return null;
                }
                if (ReadAhead != null) AheadClip(sr.Clip);
                // wholly on the view: a large mesh drawn whole (its strips would all be drawn, as more items)
                inside = sx - rx >= px.x && sx + rx <= px.x + px.width && sy - ry >= px.y && sy + ry <= px.y + px.height;
            }
            if (!Sprites.Binding(sr.Sprite, out var tex, out _, out _)) tex = null;
            var uv = Sprites.RenderUvs(sr.Sprite);
            var it = NewItem();
            if (mapped)
            {
                it.A00 = a00; it.A01 = a01; it.B0 = b0;
                it.A10 = a10; it.A11 = a11; it.B1 = b1;
                it.Local = info.Vertices;
                it.Key = sr.InstanceId;
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
            it.SpriteStencil = sr.MaskInteraction == SpriteMaskInteraction.VisibleInsideMask ? Item.MaskInside
                : sr.MaskInteraction == SpriteMaskInteraction.VisibleOutsideMask ? Item.MaskOutside : 0;
            if (mapped && !inside && tex != null && Sprites.StripsOf(info) is Sprites.Strip[] strips) return Strips(it, strips, xf.Pixel);
            return it;
        }

        /// <summary>
        /// A large sprite's item as its strips on the view (Sprites.StripsOf): the first returned, the rest
        /// in stripItems; null when none is. Each strip's box goes through the sprite's map as the whole
        /// sprite's does, a pixel to spare.
        /// </summary>
        static Item Strips(Item whole, Sprites.Strip[] strips, Rect px)
        {
            Item first = null;
            var uvAll = whole.Uv;   // the whole sprite's (the first strip takes the item over)
            foreach (var s in strips)
            {
                var b = s.Bounds;
                float cx = 0.5f * (b.x + b.z), cy = 0.5f * (b.y + b.w), ex = 0.5f * (b.z - b.x), ey = 0.5f * (b.w - b.y);
                float sx = whole.A00 * cx + whole.A01 * cy + whole.B0, rx = Math.Abs(whole.A00) * ex + Math.Abs(whole.A01) * ey + 1f;
                float sy = whole.A10 * cx + whole.A11 * cy + whole.B1, ry = Math.Abs(whole.A10) * ex + Math.Abs(whole.A11) * ey + 1f;
                if (sx + rx < px.x || sx - rx > px.x + px.width || sy + ry < px.y || sy - ry > px.y + px.height) continue;
                var it = first == null ? whole : NewItem();
                if (first != null)
                {
                    it.A00 = whole.A00; it.A01 = whole.A01; it.B0 = whole.B0;
                    it.A10 = whole.A10; it.A11 = whole.A11; it.B1 = whole.B1; it.Key = whole.Key;
                    it.BaseName = whole.BaseName; it.SpriteName = whole.SpriteName; it.Texture = whole.Texture;
                    it.Material = whole.Material; it.Tint = whole.Tint; it.Layer = whole.Layer; it.Order = whole.Order;
                    it.Depth = whole.Depth; it.GoLayer = whole.GoLayer; it.SpriteStencil = whole.SpriteStencil;
                    stripItems.Add(it);
                }
                else first = whole;
                it.Uv = Sprites.StripUvs(s, uvAll);
                it.Local = s.Vertices;
                it.Triangles = s.Triangles;
            }
            return first;
        }

        /// <summary>A sprite mask's stencil write at a sorting point: its sprite like a sprite's, its cutoff as the tint's alpha.</summary>
        static Item MaskItem(NCamera cam, in CamXform xf, NSpriteMask m, int layer, int order, int op)
        {
            var info = Sprites.Of(m.Sprite);
            if (info == null || info.Vertices.Length == 0 || info.Triangles.Length == 0) return null;
            var mtx = m.GameObject.Transform.LocalToWorld;
            if (!Sprites.Binding(m.Sprite, out var tex, out _, out _)) tex = null;
            var it = NewItem();
            if (HostTransforms && xf.Ortho)
            {
                var pvm = xf.ProjView * mtx;
                float hx = 0.5f * xf.Pixel.width, hy = 0.5f * xf.Pixel.height;
                it.A00 = hx * pvm.m00; it.A01 = hx * pvm.m01; it.B0 = xf.Pixel.x + hx * (pvm.m03 + 1f);
                it.A10 = hy * pvm.m10; it.A11 = hy * pvm.m11; it.B1 = xf.Pixel.y + hy * (pvm.m13 + 1f);
                it.Local = info.Vertices;
            }
            else
            {
                var screen = it.ScreenOf(info.Vertices.Length);
                for (int i = 0; i < screen.Length; i++) screen[i] = ToScreen(xf, mtx.MultiplyPoint3x4(new Vector3(info.Vertices[i].x, info.Vertices[i].y, 0)));
                it.Screen = screen;
            }
            it.BaseName = m.GameObject.Name; it.Suffix = op == Item.MaskAdd ? " (mask)" : " (mask end)"; it.Texture = tex;
            it.Uv = Sprites.RenderUvs(m.Sprite); it.Triangles = info.Triangles32; it.Tint = new Color(1f, 1f, 1f, m.AlphaCutoff);
            it.Layer = layer; it.Order = order; it.Depth = float.NegativeInfinity; it.Kind = "mask"; it.GoLayer = m.GameObject.Layer;
            it.SpriteStencil = op;
            return it;
        }

        static readonly List<Item> meshOut = new List<Item>();
        static readonly int MainTexId = ShaderProps.Id("_MainTex");

        /// <summary>A MeshRenderer's items: its MeshFilter's mesh through its transform, a submesh per material.</summary>
        static List<Item> MeshItems(NCamera cam, in CamXform xf, NMeshRenderer mr)
        {
            meshOut.Clear();
            NMeshFilter filter = null;
            var comps = mr.GameObject.Components;
            for (int i = 0; i < comps.Count; i++) if (comps[i] is NMeshFilter f && !f.Destroyed) { filter = f; break; }
            var mesh = MeshData.Of(filter?.Mesh);
            if (traceUi && Clock.FrameCount % 300 == 0)
                Log.Line($"TRACE mesh {mr.GameObject.Name}: filter {filter != null}, mesh {(filter?.Mesh == null ? "null" : filter.Mesh.name)}, vertices {mesh?.Vertices.Length}, submeshes {mesh?.SubMeshes.Count}, materials {mr.Materials.Length}, layer {mr.SortingLayerID}/{mr.SortingOrder}");
            if (mesh == null || mesh.Vertices.Length == 0 || mr.Materials.Length == 0) return meshOut;
            var t = mr.GameObject.Transform;
            float depth = xf.Viewport(t.Position).z;
            if (depth < cam.Near || depth > cam.Far) return meshOut;
            var mtx = t.LocalToWorld;
            var owner = NewItem();
            var screen = owner.ScreenOf(mesh.Vertices.Length);
            for (int i = 0; i < screen.Length; i++) screen[i] = ToScreen(xf, mtx.MultiplyPoint3x4(mesh.Vertices[i]));
            var uv0 = mesh.Uv[0];
            bool ownerUsed = false;
            int layer = SortingLayers.Index(mr.SortingLayerID);
            for (int sm = 0; sm < mesh.SubMeshes.Count && sm < mr.Materials.Length; sm++)
            {
                var tris = mesh.SubMeshes[sm];
                if (tris.Length == 0 || mesh.Topologies[sm] != MeshTopology.Triangles) continue;
                var mat = mr.Materials[sm];
                var ms = MaterialState.Of(mat);
                if (ms == null) continue;
                var it = ownerUsed ? NewItem() : owner;
                ownerUsed = true;
                // The material's main texture scale and offset (scrolling textures).
                var st = ms.TexST.TryGetValue(MainTexId, out var v) ? v : new Vector4(1, 1, 0, 0);
                var uv = it == owner ? owner.UvOf(screen.Length) : it.UvOf(screen.Length);
                for (int i = 0; i < uv.Length; i++)
                    uv[i] = uv0 != null && i < uv0.Length ? new Vector2(uv0[i].x * st.x + st.z, uv0[i].y * st.y + st.w) : Vector2.zero;
                it.BaseName = mr.GameObject.Name; it.Texture = MainTexture(mat); it.Material = mat; it.Screen = screen; it.Uv = uv;
                it.Colors = mesh.Colors != null && mesh.Colors.Length == screen.Length ? mesh.Colors : null;
                it.Triangles = tris; it.Tint = ms.Vectors.TryGetValue(ColorId, out var c) ? new Color(c.x, c.y, c.z, c.w) : Color.white;
                it.Layer = layer; it.Order = mr.SortingOrder; it.Depth = depth; it.Kind = "mesh"; it.GoLayer = mr.GameObject.Layer;
                meshOut.Add(it);
            }
            return meshOut;
        }
        static readonly int ColorId = ShaderProps.Id("_Color");

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
                // Under a group at alpha 0 nothing below draws, unless something below stops taking the alpha.
                if (alpha == 0f && !UiAlpha.ResetBelow(t)) return;
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
        static readonly float[] capParams = new float[Effects.ParamCount];
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
                if (v.Target != null)
                    sb.Append(",\"target\":{\"rt\":").Append(Effects.TargetId(v.Target)).Append(",\"name\":").Append(S(Objects.Of(v.Target)?.Name))
                      .Append(",\"size\":[").Append(Effects.Width(v.Target)).Append(',').Append(Effects.Height(v.Target)).Append("]}");
                if (v.IsBlit) sb.Append(",\"blit\":true");
                if (v.Resume) sb.Append(",\"resume\":true");
                if (c != null) sb.Append(",\"background\":[").Append(F(c.Background.r)).Append(',').Append(F(c.Background.g)).Append(',').Append(F(c.Background.b)).Append(',').Append(F(c.Background.a)).Append(']');
                sb.Append(",\"items\":[");
                for (int ii = 0; ii < v.Items.Count; ii++)
                {
                    var it = v.Items[ii];
                    if (ii > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(S(it.Name)).Append(",\"kind\":").Append(S(it.Kind)).Append(",\"goLayer\":").Append(it.GoLayer);
                    var tsrc = (Objects.Of(it.Texture) as NAsset)?.Source;
                    if (it.Texture is RenderTexture rtex)
                        sb.Append(",\"texture\":{\"rt\":").Append(Effects.TargetId(rtex)).Append(",\"name\":").Append(S(Objects.Of(rtex)?.Name))
                          .Append(",\"size\":[").Append(Effects.Width(rtex)).Append(',').Append(Effects.Height(rtex)).Append("]}");
                    else if (tsrc != null)
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
                    int mode = Effects.ModeOf(it);
                    if (mode >= Effects.KnightGlow)
                    {
                        var p = it.Effect >= 0 ? it.P : capParams;
                        if (it.Effect < 0) Effects.Params(it, mode, p);
                        sb.Append(",\"effect\":").Append(mode).Append(",\"p\":[");
                        for (int k = 0; k < p.Length; k++) { if (k > 0) sb.Append(','); sb.Append(p[k].ToString("0.######", ic)); }
                        sb.Append(']');
                        var t2 = (Objects.Of(it.Tex2) as NAsset)?.Source;
                        if (t2 != null)
                            sb.Append(",\"tex2\":{\"file\":").Append(S(t2.File.Path)).Append(",\"path\":").Append(t2.PathId)
                              .Append(",\"name\":").Append(S(Tree.Str(t2.Tree, "m_Name"))).Append(",\"size\":[").Append(Icalls.TextureWidth(it.Tex2)).Append(',').Append(Icalls.TextureHeight(it.Tex2)).Append("]}");
                    }
                    if (it.SpriteStencil != 0) sb.Append(",\"spriteStencil\":").Append(it.SpriteStencil);
                    sb.Append(",\"tint\":[").Append(F(it.Tint.r)).Append(',').Append(F(it.Tint.g)).Append(',').Append(F(it.Tint.b)).Append(',').Append(F(it.Tint.a)).Append(']')
                      .Append(",\"layer\":").Append(it.Layer).Append(",\"order\":").Append(it.Order).Append(",\"depth\":").Append(F(it.Depth));
                    sb.Append(",\"xy\":[");
                    if (it.Screen != null)
                        for (int k = 0; k < it.Screen.Length; k++) { if (k > 0) sb.Append(','); sb.Append(F(it.Screen[k].x)).Append(',').Append(F(it.Screen[k].y)); }
                    else if (it.Local != null)   // HostTransforms: the vertices through the item's map, as the host maps them
                        for (int k = 0; k < it.Local.Length; k++)
                        {
                            var lv = it.Local[k];
                            if (k > 0) sb.Append(',');
                            sb.Append(F(it.A00 * lv.x + it.A01 * lv.y + it.B0)).Append(',').Append(F(it.A10 * lv.x + it.A11 * lv.y + it.B1));
                        }
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
