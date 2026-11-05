using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.U2D;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    // Render state of cameras, renderers, sprites, sprite atlases, materials and
    // shaders: what the native player keeps for drawing. Drawing itself is done
    // by a backend from RenderList (Windows preview, later the console).

    public sealed class NCamera : NComponent
    {
        public CameraClearFlags ClearFlags = CameraClearFlags.Skybox;
        public Color Background = new Color(0.19f, 0.3f, 0.47f, 0f);
        public Rect Rect = new Rect(0, 0, 1, 1);
        public float Near = 0.3f, Far = 1000f, Fov = 60f, OrthoSize = 5f, Depth;
        public bool Ortho;
        public int CullingMask = -1;
        public RenderTexture Target;
        public float? AspectOverride;

        public override void Load(ChobObject co, SceneBuild b)
        {
            var t = co.Tree;
            ClearFlags = (CameraClearFlags)Tree.Int(t, "m_ClearFlags", 1);
            Background = Tree.Color(t, "m_BackGroundColor");
            var r = Tree.Get(t, "m_NormalizedViewPortRect");
            Rect = new Rect(Tree.Float(r, "x"), Tree.Float(r, "y"), Tree.Float(r, "width", 1), Tree.Float(r, "height", 1));
            Near = Tree.Float(t, "near clip plane", 0.3f);
            Far = Tree.Float(t, "far clip plane", 1000f);
            Fov = Tree.Float(t, "field of view", 60f);
            Ortho = Tree.Bool(t, "orthographic");
            OrthoSize = Tree.Float(t, "orthographic size", 5f);
            Depth = Tree.Float(t, "m_Depth");
            CullingMask = (int)Tree.Int(Tree.Get(t, "m_CullingMask"), "m_Bits", -1);
            Target = b.Resolve(Tree.Ptr(t, "m_TargetTexture")) as RenderTexture;
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NCamera)src;
            ClearFlags = s.ClearFlags; Background = s.Background; Rect = s.Rect; Near = s.Near; Far = s.Far; Fov = s.Fov;
            OrthoSize = s.OrthoSize; Depth = s.Depth; Ortho = s.Ortho; CullingMask = s.CullingMask; Target = s.Target; AspectOverride = s.AspectOverride;
        }

        public Rect PixelRect
        {
            get
            {
                float w = Icalls.ScreenWidth, h = Icalls.ScreenHeight;
                float x0 = Mathf.Clamp01(Rect.xMin), y0 = Mathf.Clamp01(Rect.yMin), x1 = Mathf.Clamp01(Rect.xMax), y1 = Mathf.Clamp01(Rect.yMax);
                return new Rect(x0 * w, y0 * h, (x1 - x0) * w, (y1 - y0) * h);
            }
        }

        public float Aspect
        {
            get
            {
                if (AspectOverride.HasValue) return AspectOverride.Value;
                var p = PixelRect;
                return p.height > 0 ? p.width / p.height : 1f;
            }
        }

        /// <summary>Unity's view matrix: the camera transform without scale, z flipped (camera looks down -z in view space).</summary>
        public Matrix4x4 WorldToCamera
        {
            get
            {
                var t = GameObject.Transform;
                var m = MathX.TRS(t.Position, t.Rotation, new Vector3(1, 1, -1));
                MathX.Invert(m, out var inv);
                return inv;
            }
        }

        public Matrix4x4 Projection
        {
            get
            {
                if (Ortho)
                {
                    float h = OrthoSize, w = OrthoSize * Aspect;
                    return Matrix4x4.Ortho(-w, w, -h, h, Near, Far);
                }
                return Matrix4x4.Perspective(Fov, Aspect, Near, Far);
            }
        }

        /// <summary>World point -> viewport (0..1, z = distance along the view direction).</summary>
        public Vector3 WorldToViewport(Vector3 p)
        {
            var v = WorldToCamera.MultiplyPoint3x4(p);   // view space, camera looks down -z
            float depth = -v.z;
            var clip = Projection * new Vector4(v.x, v.y, v.z, 1f);
            if (clip.w == 0f) return new Vector3(0.5f, 0.5f, depth);
            return new Vector3((clip.x / clip.w + 1f) * 0.5f, (clip.y / clip.w + 1f) * 0.5f, depth);
        }

        public Vector3 ViewportToWorld(Vector3 vp)
        {
            var proj = Projection;
            MathX.Invert(proj * WorldToCamera, out var inv);
            // Unity: z is the distance from the camera along its forward axis.
            var cam = WorldToCamera;
            float nx = vp.x * 2f - 1f, ny = vp.y * 2f - 1f;
            if (Ortho)
            {
                MathX.Invert(proj, out var pinv);
                var vs = pinv.MultiplyPoint(new Vector3(nx, ny, 0f));
                MathX.Invert(cam, out var c2w);
                return c2w.MultiplyPoint3x4(new Vector3(vs.x, vs.y, -vp.z));
            }
            float tan = Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            var view = new Vector3(nx * tan * Aspect * vp.z, ny * tan * vp.z, -vp.z);
            MathX.Invert(cam, out var c2w2);
            return c2w2.MultiplyPoint3x4(view);
        }
    }

    public class NRenderer : NComponent
    {
        public Material[] Materials = new Material[0];
        public int SortingLayerID, SortingOrder;
        /// <summary>Materials made by Renderer.material (instances owned by this renderer).</summary>
        public bool[] Instanced = new bool[0];

        public override void Load(ChobObject co, SceneBuild b)
        {
            var list = Tree.List(co.Tree, "m_Materials");
            Materials = new Material[list.Count];
            Instanced = new bool[list.Count];
            for (int i = 0; i < list.Count; i++)
                if (list[i] is PPtr p) Materials[i] = b.Resolve(p) as Material;
            SortingLayerID = (int)Tree.Int(co.Tree, "m_SortingLayerID");
            SortingOrder = (int)Tree.Int(co.Tree, "m_SortingOrder");
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NRenderer)src;
            Materials = (Material[])s.Materials.Clone();
            Instanced = new bool[Materials.Length];
            SortingLayerID = s.SortingLayerID; SortingOrder = s.SortingOrder;
        }
    }

    public sealed class NSpriteRenderer : NRenderer
    {
        public Sprite Sprite;
        public Color Color = Color.white;
        public bool FlipX, FlipY;
        public SpriteDrawMode DrawMode;
        public Vector2 Size = Vector2.one;
        public SpriteMaskInteraction MaskInteraction;
        public SpriteTileMode TileMode;
        public float AdaptiveThreshold = 0.5f;

        public override void Load(ChobObject co, SceneBuild b)
        {
            base.Load(co, b);
            var t = co.Tree;
            Sprite = b.Resolve(Tree.Ptr(t, "m_Sprite")) as Sprite;
            Color = Tree.Color(t, "m_Color");
            FlipX = Tree.Bool(t, "m_FlipX"); FlipY = Tree.Bool(t, "m_FlipY");
            DrawMode = (SpriteDrawMode)Tree.Int(t, "m_DrawMode");
            Size = Tree.Vec2(t, "m_Size");
            MaskInteraction = (SpriteMaskInteraction)Tree.Int(t, "m_MaskInteraction");
            TileMode = (SpriteTileMode)Tree.Int(t, "m_SpriteTileMode");
            AdaptiveThreshold = Tree.Float(t, "m_AdaptiveModeThreshold", 0.5f);
            if (Sprite != null) Atlases.Touch(Sprite);
        }

        public override void Activated(bool on) { if (on && Sprite != null) Atlases.Touch(Sprite); }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            base.CopyFrom(src, map);
            var s = (NSpriteRenderer)src;
            Sprite = s.Sprite; Color = s.Color; FlipX = s.FlipX; FlipY = s.FlipY; DrawMode = s.DrawMode; Size = s.Size;
            MaskInteraction = s.MaskInteraction; TileMode = s.TileMode; AdaptiveThreshold = s.AdaptiveThreshold;
        }
    }

    /// <summary>Geometry and texture binding of Sprite assets.</summary>
    public static class Sprites
    {
        public sealed class Info
        {
            public string Name = "";
            public Rect Rect;
            public Vector2 Pivot = new Vector2(0.5f, 0.5f);   // normalized
            public float PixelsPerUnit = 100f;
            public Vector4 Border;
            public Vector2[] Vertices = new Vector2[0];        // local units, pivot at origin
            public ushort[] Triangles = new ushort[0];
            ushort[] tris32From; int[] tris32;
            // Atlases.Bound's result while its atlas table is unchanged.
            internal int BoundVersion;
            internal SpriteAtlas BoundAtlas;
            internal Texture2D BoundTex;
            internal Rect BoundRect;
            internal Vector4 BoundUv;
            internal int BoundSettings;
            // Sprites.RenderUvs' cache and what it was worked out from.
            internal Vector2[] UvCache, UvVertices;
            internal Texture UvTex;
            internal Rect UvRect;
            internal Vector4 UvMap;
            internal int UvSettings;
            internal float UvW, UvH;
            /// <summary>Triangles as ints for the render list (made once, not per frame).</summary>
            public int[] Triangles32
            {
                get
                {
                    if (tris32From != Triangles) { tris32 = Array.ConvertAll(Triangles, t => (int)t); tris32From = Triangles; }
                    return tris32;
                }
            }
            Vector2[] boundsFrom; Vector4 bounds;
            /// <summary>The vertices' box (x0, y0, x1, y1), for the render list's culling (worked out once per mesh).</summary>
            public Vector4 LocalBounds
            {
                get
                {
                    if (boundsFrom != Vertices)
                    {
                        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                        foreach (var v in Vertices)
                        {
                            if (v.x < x0) x0 = v.x;
                            if (v.x > x1) x1 = v.x;
                            if (v.y < y0) y0 = v.y;
                            if (v.y > y1) y1 = v.y;
                        }
                        bounds = new Vector4(x0, y0, x1, y1);
                        boundsFrom = Vertices;
                    }
                    return bounds;
                }
            }
            public string[] AtlasTags = new string[0];
            public string RenderKey;                           // GUID + local id, matches SpriteAtlas render data
            public Texture2D Texture;                          // own texture (not late-bound)
            public Rect TextureRect;
            public Vector2 TextureRectOffset;
            public Vector4 UvTransform;                        // texel = vertex * (x, z) + (y, w)
            public int SettingsRaw;
            public SpriteAtlas IncludedAtlas;                  // atlas shipped in the build (no late binding)
            public ChobFile AtlasFile; public PPtr AtlasPtr;
        }

        static readonly ConditionalWeakTable<Sprite, Info> infos = new ConditionalWeakTable<Sprite, Info>();

        public static Info Of(Sprite s)
        {
            if (s == null) return null;
            if (infos.TryGetValue(s, out var i)) return i;
            i = FromData((Objects.Of(s) as NAsset)?.Source) ?? new Info();
            infos.Add(s, i);
            return i;
        }

        public static void Set(Sprite s, Info i) { infos.Remove(s); infos.Add(s, i); }

        public static string Key(object renderDataKey)
        {
            if (!(renderDataKey is List<object> k) || k.Count < 2) return null;
            var g = k[0];
            return Tree.Int(g, "data[0]") + "-" + Tree.Int(g, "data[1]") + "-" + Tree.Int(g, "data[2]") + "-" + Tree.Int(g, "data[3]") + ":" + (k[1] is long l ? l : 0);
        }

        static Info FromData(ChobObject co)
        {
            if (co == null) return null;
            var t = co.Tree;
            var i = new Info
            {
                Name = Tree.Str(t, "m_Name"),
                PixelsPerUnit = Tree.Float(t, "m_PixelsToUnits", 100f),
                RenderKey = Key(Tree.Get(t, "m_RenderDataKey")),
            };
            var r = Tree.Get(t, "m_Rect");
            i.Rect = new Rect(Tree.Float(r, "x"), Tree.Float(r, "y"), Tree.Float(r, "width"), Tree.Float(r, "height"));
            i.Pivot = Tree.Vec2(t, "m_Pivot");
            var bo = Tree.Get(t, "m_Border");
            i.Border = new Vector4(Tree.Float(bo, "x"), Tree.Float(bo, "y"), Tree.Float(bo, "z"), Tree.Float(bo, "w"));
            var tags = Tree.List(t, "m_AtlasTags");
            i.AtlasTags = new string[tags.Count];
            for (int k = 0; k < tags.Count; k++) i.AtlasTags[k] = tags[k] as string ?? "";
            var rd = Tree.Get(t, "m_RD");
            i.Texture = AssetDb.Resolve(co.File, Tree.Ptr(rd, "texture")) as Texture2D;
            var atlasPtr = Tree.Ptr(t, "m_SpriteAtlas");
            if (!atlasPtr.IsNull) { i.AtlasFile = co.File; i.AtlasPtr = atlasPtr; i.IncludedAtlas = AssetDb.Resolve(co.File, atlasPtr) as SpriteAtlas; }
            ReadBinding(rd, out i.TextureRect, out i.TextureRectOffset, out i.UvTransform, out i.SettingsRaw);
            ReadMesh(rd, i);
            return i;
        }

        public static void ReadBinding(object rd, out Rect textureRect, out Vector2 rectOffset, out Vector4 uvTransform, out int settings)
        {
            var tr = Tree.Get(rd, "textureRect");
            textureRect = new Rect(Tree.Float(tr, "x"), Tree.Float(tr, "y"), Tree.Float(tr, "width"), Tree.Float(tr, "height"));
            rectOffset = Tree.Vec2(rd, "textureRectOffset");
            var u = Tree.Get(rd, "uvTransform");
            uvTransform = new Vector4(Tree.Float(u, "x"), Tree.Float(u, "y"), Tree.Float(u, "z"), Tree.Float(u, "w"));
            settings = (int)Tree.Int(rd, "settingsRaw");
        }

        // Unity 2017 vertex channel formats: 0 float32, 1 float16, 2 unorm8, ...
        static readonly int[] formatSize = { 4, 2, 1, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4 };

        static void ReadMesh(object rd, Info i)
        {
            var vd = Tree.Get(rd, "m_VertexData");
            int count = (int)Tree.Int(vd, "m_VertexCount");
            var data = Tree.Get(vd, "m_DataSize") as byte[];
            var channels = Tree.List(vd, "m_Channels");
            if (data == null || count == 0 || channels.Count == 0) return;
            // Streams are laid out one after another; channels of a stream interleave.
            int streams = 0;
            foreach (var c in channels) if (Tree.Int(c, "dimension") > 0) streams = Math.Max(streams, (int)Tree.Int(c, "stream") + 1);
            var stride = new int[streams];
            foreach (var c in channels)
            {
                int dim = (int)(Tree.Int(c, "dimension") & 0xF);
                if (dim == 0) continue;
                int s = (int)Tree.Int(c, "stream");
                stride[s] = Math.Max(stride[s], (int)Tree.Int(c, "offset") + dim * formatSize[Math.Min(12, (int)Tree.Int(c, "format"))]);
            }
            var start = new int[streams];
            for (int s = 1; s < streams; s++) start[s] = (start[s - 1] + stride[s - 1] * count + 15) & ~15;
            var pos = channels[0];
            int ps = (int)Tree.Int(pos, "stream"), po = (int)Tree.Int(pos, "offset");
            i.Vertices = new Vector2[count];
            for (int v = 0; v < count; v++)
            {
                int at = start[ps] + v * stride[ps] + po;
                if (at + 8 > data.Length) break;
                i.Vertices[v] = new Vector2(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4));
            }
            var bytes = Tree.Get(rd, "m_IndexBuffer") as byte[];
            if (bytes == null)
            {
                var ib = Tree.List(rd, "m_IndexBuffer");
                bytes = new byte[ib.Count];
                for (int k = 0; k < ib.Count; k++) bytes[k] = (byte)(ib[k] is long l ? l : 0);
            }
            i.Triangles = new ushort[bytes.Length / 2];
            for (int k = 0; k < i.Triangles.Length; k++) i.Triangles[k] = BitConverter.ToUInt16(bytes, k * 2);
        }

        /// <summary>The texture a sprite draws from now: its own, or its registered SpriteAtlas's.</summary>
        public static bool Binding(Sprite s, out Texture2D tex, out Rect textureRect, out Vector4 uvTransform) => Binding(s, out tex, out textureRect, out uvTransform, out _);

        public static bool Binding(Sprite s, out Texture2D tex, out Rect textureRect, out Vector4 uvTransform, out int settings)
        {
            var i = Of(s);
            tex = null; textureRect = default; uvTransform = default; settings = 0;
            if (i == null) return false;
            if (i.Texture != null) { tex = i.Texture; textureRect = i.TextureRect; uvTransform = i.UvTransform; settings = i.SettingsRaw; return true; }
            return Atlases.Bound(i, out tex, out textureRect, out uvTransform, out settings);
        }

        /// <summary>Texture coordinates (0..1, Unity orientation) of a sprite's vertices.</summary>
        /// <summary>A sprite's UVs, the game's own copy (Sprite.uv).</summary>
        public static Vector2[] Uvs(Sprite s) => (Vector2[])RenderUvs(s).Clone();

        /// <summary>
        /// A sprite's UVs for the render list: kept on its Info until its
        /// binding, texture size or mesh changes (worked out and allocated for
        /// every sprite every frame before). Callers don't change it.
        /// </summary>
        internal static Vector2[] RenderUvs(Sprite s)
        {
            var i = Of(s);
            if (i == null) return new Vector2[0];
            if (!Binding(s, out var tex, out var tr, out var u, out int settings) || tex == null) return new Vector2[i.Vertices.Length];
            float w = Icalls.TextureWidth(tex), h = Icalls.TextureHeight(tex);
            if (w <= 0 || h <= 0) return new Vector2[i.Vertices.Length];
            // Field by field (float.Equals, as Rect's and Vector4's Equals do): their Equals(object)
            // boxed the argument, an allocation per sprite drawn each frame.
            if (i.UvCache != null && ReferenceEquals(i.UvTex, tex) && i.UvSettings == settings && i.UvW == w && i.UvH == h
                && i.UvRect.x.Equals(tr.x) && i.UvRect.y.Equals(tr.y) && i.UvRect.width.Equals(tr.width) && i.UvRect.height.Equals(tr.height)
                && i.UvMap.x.Equals(u.x) && i.UvMap.y.Equals(u.y) && i.UvMap.z.Equals(u.z) && i.UvMap.w.Equals(u.w)
                && ReferenceEquals(i.UvVertices, i.Vertices))
                return i.UvCache;
            var uv = new Vector2[i.Vertices.Length];
            // Packing rotation: the sprite is stored flipped/rotated inside its packed rect.
            int rot = (settings >> 2) & 0xF;
            float cx = tr.x + tr.width * 0.5f, cy = tr.y + tr.height * 0.5f;
            for (int k = 0; k < uv.Length; k++)
            {
                float tx = i.Vertices[k].x * u.x + u.y, ty = i.Vertices[k].y * u.z + u.w;
                switch (rot)
                {
                    case 1: tx = 2 * cx - tx; break;                          // FlipHorizontal
                    case 2: ty = 2 * cy - ty; break;                          // FlipVertical
                    case 3: tx = 2 * cx - tx; ty = 2 * cy - ty; break;        // Rotate180
                    case 4: { float dx = tx - cx, dy = ty - cy; tx = cx + dy; ty = cy - dx; break; }   // Rotate90 (unverified)
                }
                uv[k] = new Vector2(tx / w, ty / h);
            }
            if (rot == 4) AssetDb.Problem("sprite packed with Rotate90 (UV mapping unverified): " + i.Name);
            i.UvCache = uv; i.UvTex = tex; i.UvRect = tr; i.UvMap = u; i.UvSettings = settings; i.UvW = w; i.UvH = h; i.UvVertices = i.Vertices;
            return uv;
        }

        public static Bounds LocalBounds(Info i)
        {
            if (i == null || i.Vertices.Length == 0) return default;
            Vector2 min = i.Vertices[0], max = i.Vertices[0];
            foreach (var v in i.Vertices) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
            // Unity's sprite bounds are its rect, not the tight mesh.
            float w = i.Rect.width / i.PixelsPerUnit, h = i.Rect.height / i.PixelsPerUnit;
            var center = new Vector3((0.5f - i.Pivot.x) * w, (0.5f - i.Pivot.y) * h, 0);
            return new Bounds(center, new Vector3(w, h, 0.2f));
        }
    }

    /// <summary>
    /// Late-bound SpriteAtlases. A sprite packed into an atlas that isn't
    /// included in the build asks for it by tag through
    /// SpriteAtlasManager.RequestAtlas (the game's SpriteAtlasLoader answers,
    /// loading the atlas bundle); the atlas arrives through Register.
    /// </summary>
    public static class Atlases
    {
        static readonly Dictionary<string, SpriteAtlas> byTag = new Dictionary<string, SpriteAtlas>(StringComparer.Ordinal);
        static readonly Dictionary<string, (SpriteAtlas atlas, object data)> byKey = new Dictionary<string, (SpriteAtlas, object)>(StringComparer.Ordinal);
        // Bumped whenever byKey changes: a sprite's Bound result is kept until then (it read the tree each frame).
        static int version = 1;
        static readonly bool noBoundCache = Environment.GetEnvironmentVariable("CH360_NO_BOUND_CACHE") == "1";
        static readonly HashSet<string> requested = new HashSet<string>(StringComparer.Ordinal);
        static readonly List<string> pending = new List<string>();
        static readonly HashSet<string> unheard = new HashSet<string>(StringComparer.Ordinal);
        static readonly bool Trace = Environment.GetEnvironmentVariable("CH360_TRACE_ATLAS") == "1";

        /// <summary>A sprite was loaded or assigned: request its atlas if it isn't here.</summary>
        public static void Touch(Sprite s)
        {
            var i = Sprites.Of(s);
            if (i == null || i.Texture != null) return;
            // An atlas included in the build binds its sprites when it loads.
            if (i.IncludedAtlas != null)
            {
                // Unloaded since: the next load is a new atlas object.
                if (!(Objects.Of(i.IncludedAtlas) is NAsset ia) || ia.Destroyed) i.IncludedAtlas = AssetDb.Resolve(i.AtlasFile, i.AtlasPtr) as SpriteAtlas;
                if (i.IncludedAtlas != null && (!byKey.TryGetValue(i.RenderKey ?? "", out var cur) || cur.atlas != i.IncludedAtlas)) Register(i.IncludedAtlas);
                return;
            }
            foreach (var tag in i.AtlasTags)
                if (!Live(tag) && requested.Add(tag)) pending.Add(tag);
        }

        static bool Live(string tag) => byTag.TryGetValue(tag, out var a) && Objects.Of(a) is NObject o && !o.Destroyed;

        /// <summary>Send queued atlas requests (main thread, outside object construction).</summary>
        public static void Flush()
        {
            if (pending.Count == 0) return;
            var tags = pending.ToArray();
            pending.Clear();
            foreach (var tag in tags)
            {
                if (Live(tag)) continue;
                bool heard;
                if (Trace) Log.Line($"TRACE atlas request {tag} frame {Clock.FrameCount}");
                try { heard = SpriteAtlasManager.RequestAtlas(tag); }
                catch (Exception e) { Exceptions.Report(e, null, "SpriteAtlasManager.atlasRequested"); heard = true; }
                if (!heard)
                {
                    if (unheard.Add(tag)) Log.Line("UNITY Warning: SpriteAtlasManager.atlasRequested wasn't listened to while " + tag + " requested.");
                    requested.Remove(tag);   // asked again when a sprite needs it next
                }
            }
        }

        public static void Register(SpriteAtlas atlas)
        {
            var src = (Objects.Of(atlas) as NAsset)?.Source;
            if (src == null) return;
            string tag = Tree.Str(src.Tree, "m_Tag");
            if (Trace) Log.Line($"TRACE atlas register {tag} id={Objects.Of(atlas)?.InstanceId} destroyed={Objects.Of(atlas)?.Destroyed} frame {Clock.FrameCount}");
            byTag[tag] = atlas;
            requested.Remove(tag);   // answered: if this atlas is unloaded later, sprites ask again
            foreach (var e in Tree.List(src.Tree, "m_RenderDataMap"))
                if (e is List<object> pair && pair.Count == 2)
                {
                    string key = Sprites.Key(pair[0]);
                    if (key != null) { byKey[key] = (atlas, pair[1]); version++; }
                }
        }

        public static bool Bound(Sprites.Info i, out Texture2D tex, out Rect textureRect, out Vector4 uvTransform, out int settings)
        {
            // Kept while the atlas table is unchanged and the atlas and texture are alive (a
            // destroyed texture is resolved again, as before).
            if (i.BoundVersion == version && !noBoundCache
                && (i.BoundAtlas == null || (Objects.Of(i.BoundAtlas) is NAsset live && !live.Destroyed))
                && (i.BoundTex == null || (Objects.Of(i.BoundTex) is NObject lt && !lt.Destroyed)))
            {
                tex = i.BoundTex; textureRect = i.BoundRect; uvTransform = i.BoundUv; settings = i.BoundSettings;
                return tex != null;
            }
            tex = null; textureRect = default; uvTransform = default; settings = 0;
            i.BoundVersion = version; i.BoundAtlas = null; i.BoundTex = null;
            if (i.RenderKey == null || !byKey.TryGetValue(i.RenderKey, out var b)) return false;
            if (!(Objects.Of(b.atlas) is NAsset a) || a.Destroyed) { i.BoundVersion = 0; return false; }
            tex = AssetDb.Resolve(a.Source.File, Tree.Ptr(b.data, "texture")) as Texture2D;
            Sprites.ReadBinding(b.data, out textureRect, out _, out uvTransform, out settings);
            i.BoundAtlas = b.atlas; i.BoundTex = tex; i.BoundRect = textureRect; i.BoundUv = uvTransform; i.BoundSettings = settings;
            return tex != null;
        }

        public static string Tag(SpriteAtlas a) => Tree.Str((Objects.Of(a) as NAsset)?.Source?.Tree, "m_Tag");

        public static List<Sprite> Packed(SpriteAtlas a)
        {
            var list = new List<Sprite>();
            var src = (Objects.Of(a) as NAsset)?.Source;
            if (src == null) return list;
            foreach (var p in Tree.List(src.Tree, "m_PackedSprites"))
                if (p is PPtr pp && AssetDb.Resolve(src.File, pp) is Sprite s) list.Add(s);
            return list;
        }
    }

    /// <summary>Material property state (from the asset's saved properties, then set at run time).</summary>
    public sealed class MaterialState
    {
        public Shader Shader;
        public readonly Dictionary<int, float> Floats = new Dictionary<int, float>();
        public readonly Dictionary<int, Vector4> Vectors = new Dictionary<int, Vector4>();   // colors and vectors
        public readonly Dictionary<int, Texture> Textures = new Dictionary<int, Texture>();
        public readonly Dictionary<int, Vector4> TexST = new Dictionary<int, Vector4>();     // scale xy, offset zw
        public readonly List<string> Keywords = new List<string>();
        public int RenderQueue = -1;

        static readonly ConditionalWeakTable<Material, MaterialState> states = new ConditionalWeakTable<Material, MaterialState>();

        public static MaterialState Of(Material m)
        {
            if (m == null) return null;
            if (states.TryGetValue(m, out var s)) return s;
            s = new MaterialState();
            var src = (Objects.Of(m) as NAsset)?.Source;
            if (src != null) s.LoadFrom(src);
            states.Add(m, s);
            return s;
        }

        public static void Set(Material m, MaterialState s) { states.Remove(m); states.Add(m, s); }

        void LoadFrom(ChobObject co)
        {
            var t = co.Tree;
            Shader = AssetDb.Resolve(co.File, Tree.Ptr(t, "m_Shader")) as Shader;
            RenderQueue = (int)Tree.Int(t, "m_CustomRenderQueue", -1);
            foreach (var k in Tree.Str(t, "m_ShaderKeywords").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) Keywords.Add(k);
            var sp = Tree.Get(t, "m_SavedProperties");
            foreach (var e in Tree.List(sp, "m_TexEnvs"))
                if (e is List<object> p && p.Count == 2 && p[0] is string name)
                {
                    int id = ShaderProps.Id(name);
                    Textures[id] = AssetDb.Resolve(co.File, Tree.Ptr(p[1], "m_Texture")) as Texture;
                    var sc = Tree.Vec2(p[1], "m_Scale"); var of = Tree.Vec2(p[1], "m_Offset");
                    TexST[id] = new Vector4(sc.x, sc.y, of.x, of.y);
                }
            foreach (var e in Tree.List(sp, "m_Floats"))
                if (e is List<object> p && p.Count == 2 && p[0] is string name)
                    Floats[ShaderProps.Id(name)] = p[1] switch { float f => f, double d => (float)d, long l => l, _ => 0f };
            foreach (var e in Tree.List(sp, "m_Colors"))
                if (e is List<object> p && p.Count == 2 && p[0] is string name)
                    Vectors[ShaderProps.Id(name)] = new Vector4(Tree.Float(p[1], "r"), Tree.Float(p[1], "g"), Tree.Float(p[1], "b"), Tree.Float(p[1], "a"));
        }

        public MaterialState Clone()
        {
            var c = new MaterialState { Shader = Shader, RenderQueue = RenderQueue };
            foreach (var kv in Floats) c.Floats[kv.Key] = kv.Value;
            foreach (var kv in Vectors) c.Vectors[kv.Key] = kv.Value;
            foreach (var kv in Textures) c.Textures[kv.Key] = kv.Value;
            foreach (var kv in TexST) c.TexST[kv.Key] = kv.Value;
            c.Keywords.AddRange(Keywords);
            return c;
        }

        public bool Has(int id) => Floats.ContainsKey(id) || Vectors.ContainsKey(id) || Textures.ContainsKey(id) || ShaderDeclares(id);

        // The shaders ship as name-only stubs, so the built-in UI shaders' property
        // blocks are listed here (UGUI's masking checks HasProperty("_Stencil") etc.).
        static readonly HashSet<int> uiProps = new HashSet<int>();
        bool ShaderDeclares(int id)
        {
            string n = Objects.Of(Shader)?.Name;
            if (n == null || !n.StartsWith("UI/", StringComparison.Ordinal)) return false;
            if (uiProps.Count == 0)
                foreach (var p in new[] { "_MainTex", "_Color", "_StencilComp", "_Stencil", "_StencilOp", "_StencilWriteMask", "_StencilReadMask", "_ColorMask", "_ClipRect", "_UseUIAlphaClip" })
                    uiProps.Add(ShaderProps.Id(p));
            return uiProps.Contains(id);
        }
    }

    /// <summary>Shader property names and ids (Shader.PropertyToID), global shader state.</summary>
    public static class ShaderProps
    {
        static readonly Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
        static readonly List<string> names = new List<string>();
        public static readonly MaterialState Globals = new MaterialState();

        public static int Id(string name)
        {
            name = name ?? "";
            if (ids.TryGetValue(name, out int id)) return id;
            id = names.Count;
            names.Add(name);
            ids[name] = id;
            return id;
        }

        public static string Name(int id) => id >= 0 && id < names.Count ? names[id] : "";
    }

    public static partial class Icalls
    {
        // ------------------------------------------------------------ Camera

        static NCamera Cam(Camera c) => Objects.Of(c) as NCamera;

        public static void UnityEngine_Camera__INTERNAL_get_rect(Camera self, out Rect value) => value = Cam(self)?.Rect ?? new Rect(0, 0, 1, 1);
        public static void UnityEngine_Camera__INTERNAL_set_rect(Camera self, ref Rect value) { var c = Cam(self); if (c != null) c.Rect = value; }
        public static void UnityEngine_Camera__INTERNAL_get_pixelRect(Camera self, out Rect value) => value = Cam(self)?.PixelRect ?? default;
        public static void UnityEngine_Camera__INTERNAL_set_pixelRect(Camera self, ref Rect value)
        {
            var c = Cam(self);
            if (c == null) return;
            float w = ScreenWidth, h = ScreenHeight;
            c.Rect = new Rect(value.x / w, value.y / h, value.width / w, value.height / h);
        }
        public static int UnityEngine_Camera__get_pixelWidth(Camera self) => (int)(Cam(self)?.PixelRect.width ?? 0);
        public static int UnityEngine_Camera__get_pixelHeight(Camera self) => (int)(Cam(self)?.PixelRect.height ?? 0);
        public static int UnityEngine_Camera__get_scaledPixelWidth(Camera self) => UnityEngine_Camera__get_pixelWidth(self);
        public static int UnityEngine_Camera__get_scaledPixelHeight(Camera self) => UnityEngine_Camera__get_pixelHeight(self);
        public static float UnityEngine_Camera__get_aspect(Camera self) => Cam(self)?.Aspect ?? 1f;
        public static void UnityEngine_Camera__set_aspect(Camera self, float value) { var c = Cam(self); if (c != null) c.AspectOverride = value; }
        public static void UnityEngine_Camera__INTERNAL_CALL_ResetAspect(Camera self) { var c = Cam(self); if (c != null) c.AspectOverride = null; }
        public static CameraClearFlags UnityEngine_Camera__get_clearFlags(Camera self) => Cam(self)?.ClearFlags ?? CameraClearFlags.Skybox;
        public static void UnityEngine_Camera__set_clearFlags(Camera self, CameraClearFlags value) { var c = Cam(self); if (c != null) c.ClearFlags = value; }
        public static void UnityEngine_Camera__INTERNAL_get_backgroundColor(Camera self, out Color value) => value = Cam(self)?.Background ?? default;
        public static void UnityEngine_Camera__INTERNAL_set_backgroundColor(Camera self, ref Color value) { var c = Cam(self); if (c != null) c.Background = value; }
        public static bool UnityEngine_Camera__get_orthographic(Camera self) => Cam(self)?.Ortho ?? false;
        public static void UnityEngine_Camera__set_orthographic(Camera self, bool value) { var c = Cam(self); if (c != null) c.Ortho = value; }
        public static float UnityEngine_Camera__get_orthographicSize(Camera self) => Cam(self)?.OrthoSize ?? 5f;
        public static void UnityEngine_Camera__set_orthographicSize(Camera self, float value) { var c = Cam(self); if (c != null) c.OrthoSize = value; }
        public static float UnityEngine_Camera__get_fieldOfView(Camera self) => Cam(self)?.Fov ?? 60f;
        public static void UnityEngine_Camera__set_fieldOfView(Camera self, float value) { var c = Cam(self); if (c != null) c.Fov = value; }
        public static float UnityEngine_Camera__get_nearClipPlane(Camera self) => Cam(self)?.Near ?? 0.3f;
        public static void UnityEngine_Camera__set_nearClipPlane(Camera self, float value) { var c = Cam(self); if (c != null) c.Near = value; }
        public static float UnityEngine_Camera__get_farClipPlane(Camera self) => Cam(self)?.Far ?? 1000f;
        public static void UnityEngine_Camera__set_farClipPlane(Camera self, float value) { var c = Cam(self); if (c != null) c.Far = value; }
        public static float UnityEngine_Camera__get_depth(Camera self) => Cam(self)?.Depth ?? 0f;
        public static void UnityEngine_Camera__set_depth(Camera self, float value) { var c = Cam(self); if (c != null) c.Depth = value; }
        public static int UnityEngine_Camera__get_cullingMask(Camera self) => Cam(self)?.CullingMask ?? -1;
        public static void UnityEngine_Camera__set_cullingMask(Camera self, int value) { var c = Cam(self); if (c != null) c.CullingMask = value; }
        public static RenderTexture UnityEngine_Camera__get_targetTexture(Camera self) => Cam(self)?.Target;
        public static void UnityEngine_Camera__set_targetTexture(Camera self, RenderTexture value) { var c = Cam(self); if (c != null) c.Target = value; }
        public static void UnityEngine_Camera__INTERNAL_get_worldToCameraMatrix(Camera self, out Matrix4x4 value) => value = Cam(self)?.WorldToCamera ?? Matrix4x4.identity;
        public static void UnityEngine_Camera__INTERNAL_get_projectionMatrix(Camera self, out Matrix4x4 value) => value = Cam(self)?.Projection ?? Matrix4x4.identity;
        public static void UnityEngine_Camera__INTERNAL_get_cameraToWorldMatrix(Camera self, out Matrix4x4 value)
        {
            value = Matrix4x4.identity;
            var c = Cam(self);
            if (c != null) MathX.Invert(c.WorldToCamera, out value);
        }

        public static void UnityEngine_Camera__INTERNAL_CALL_WorldToViewportPoint(Camera self, ref Vector3 position, out Vector3 value)
            => value = Cam(self)?.WorldToViewport(position) ?? default;
        public static void UnityEngine_Camera__INTERNAL_CALL_ViewportToWorldPoint(Camera self, ref Vector3 position, out Vector3 value)
            => value = Cam(self)?.ViewportToWorld(position) ?? default;
        public static void UnityEngine_Camera__INTERNAL_CALL_WorldToScreenPoint(Camera self, ref Vector3 position, out Vector3 value)
        {
            var c = Cam(self);
            if (c == null) { value = default; return; }
            var v = c.WorldToViewport(position);
            var p = c.PixelRect;
            value = new Vector3(p.x + v.x * p.width, p.y + v.y * p.height, v.z);
        }
        public static void UnityEngine_Camera__INTERNAL_CALL_ScreenToWorldPoint(Camera self, ref Vector3 position, out Vector3 value)
        {
            var c = Cam(self);
            if (c == null) { value = default; return; }
            var p = c.PixelRect;
            var vp = new Vector3(p.width > 0 ? (position.x - p.x) / p.width : 0, p.height > 0 ? (position.y - p.y) / p.height : 0, position.z);
            value = c.ViewportToWorld(vp);
        }
        public static void UnityEngine_Camera__INTERNAL_CALL_ViewportToScreenPoint(Camera self, ref Vector3 position, out Vector3 value)
        {
            var p = Cam(self)?.PixelRect ?? default;
            value = new Vector3(p.x + position.x * p.width, p.y + position.y * p.height, position.z);
        }
        public static void UnityEngine_Camera__INTERNAL_CALL_ScreenToViewportPoint(Camera self, ref Vector3 position, out Vector3 value)
        {
            var p = Cam(self)?.PixelRect ?? default;
            value = new Vector3(p.width > 0 ? (position.x - p.x) / p.width : 0, p.height > 0 ? (position.y - p.y) / p.height : 0, position.z);
        }
        public static void UnityEngine_Camera__INTERNAL_CALL_ScreenPointToRay(Camera self, ref Vector3 position, out Ray value)
        {
            var c = Cam(self);
            if (c == null) { value = default; return; }
            var p = c.PixelRect;
            float vx = p.width > 0 ? (position.x - p.x) / p.width : 0, vy = p.height > 0 ? (position.y - p.y) / p.height : 0;
            var a = c.ViewportToWorld(new Vector3(vx, vy, c.Near));
            var b = c.ViewportToWorld(new Vector3(vx, vy, c.Near + 1f));
            value = new Ray(a, (b - a).normalized);
        }
        public static void UnityEngine_Camera__INTERNAL_CALL_ViewportPointToRay(Camera self, ref Vector3 position, out Ray value)
        {
            var c = Cam(self);
            if (c == null) { value = default; return; }
            var a = c.ViewportToWorld(new Vector3(position.x, position.y, c.Near));
            var b = c.ViewportToWorld(new Vector3(position.x, position.y, c.Near + 1f));
            value = new Ray(a, (b - a).normalized);
        }

        /// <summary>Enabled cameras in active GameObjects, in creation order (Unity's list).</summary>
        public static List<NCamera> LiveCameras()
        {
            var list = new List<NCamera>();
            foreach (var c in Typed.Cameras)
                if (c.Enabled && c.GameObject != null && c.GameObject.ActiveInHierarchy) list.Add(c);
            return list;
        }

        public static Camera UnityEngine_Camera__get_main()
        {
            foreach (var c in LiveCameras()) if (c.GameObject.Tag == "MainCamera") return (Camera)c.Managed;
            return null;
        }
        public static Camera UnityEngine_Camera__get_current() => RenderList.Current;
        public static int UnityEngine_Camera__get_allCamerasCount() => LiveCameras().Count;
        public static Camera[] UnityEngine_Camera__get_allCameras() => LiveCameras().ConvertAll(c => (Camera)c.Managed).ToArray();
        public static int UnityEngine_Camera__GetAllCameras(Camera[] cameras)
        {
            var live = LiveCameras();
            int n = Math.Min(live.Count, cameras?.Length ?? 0);
            for (int i = 0; i < n; i++) cameras[i] = (Camera)live[i].Managed;
            return n;
        }
        public static void UnityEngine_Camera__CopyFrom(Camera self, Camera other)
        {
            var d = Cam(self); var s = Cam(other);
            if (d != null && s != null) d.CopyFrom(s, null);
        }
        public static void UnityEngine_Camera__Render(Camera self) { }

        // ------------------------------------------------------------ Renderer

        static NRenderer Rend(Renderer r) => Objects.Of(r) as NRenderer;

        public static bool UnityEngine_Renderer__get_enabled(Renderer self) => Objects.Comp(self)?.Enabled ?? false;
        public static void UnityEngine_Renderer__set_enabled(Renderer self, bool value) { var c = Objects.Comp(self); if (c != null) c.Enabled = value; }
        public static bool UnityEngine_Renderer__get_isVisible(Renderer self)
        {
            var r = Rend(self);
            return r != null && r.Enabled && r.GameObject.ActiveInHierarchy;
        }
        public static int UnityEngine_Renderer__get_sortingOrder(Renderer self) => Rend(self)?.SortingOrder ?? 0;
        public static void UnityEngine_Renderer__set_sortingOrder(Renderer self, int value) { var r = Rend(self); if (r != null) r.SortingOrder = (short)value; }
        public static int UnityEngine_Renderer__get_sortingLayerID(Renderer self) => Rend(self)?.SortingLayerID ?? 0;
        public static void UnityEngine_Renderer__set_sortingLayerID(Renderer self, int value)
        {
            var r = Rend(self);
            if (r != null && SortingLayers.Index(value) >= 0) r.SortingLayerID = value;
        }
        public static string UnityEngine_Renderer__get_sortingLayerName(Renderer self) => SortingLayers.Name(Rend(self)?.SortingLayerID ?? 0);
        public static void UnityEngine_Renderer__set_sortingLayerName(Renderer self, string value)
        {
            var r = Rend(self);
            if (r != null && SortingLayers.TryId(value, out int id)) r.SortingLayerID = id;
        }
        public static Material UnityEngine_Renderer__GetSharedMaterial(Renderer self)
        {
            var r = Rend(self);
            return r != null && r.Materials.Length > 0 ? r.Materials[0] : null;
        }
        public static Material[] UnityEngine_Renderer__GetSharedMaterialArray(Renderer self) => (Material[])(Rend(self)?.Materials.Clone() ?? new Material[0]);
        public static void UnityEngine_Renderer__SetMaterial(Renderer self, Material m)
        {
            var r = Rend(self);
            if (r == null) return;
            if (r.Materials.Length == 0) { r.Materials = new Material[1]; r.Instanced = new bool[1]; }
            r.Materials[0] = m; r.Instanced[0] = false;
        }
        public static void UnityEngine_Renderer__SetMaterialArrayImpl(Renderer self, Material[] m)
        {
            var r = Rend(self);
            if (r == null) return;
            r.Materials = (Material[])(m?.Clone() ?? new Material[0]);
            r.Instanced = new bool[r.Materials.Length];
        }
        /// <summary>Renderer.material: the renderer's own instance of its first material, made on first use.</summary>
        public static Material UnityEngine_Renderer__GetMaterial(Renderer self)
        {
            var r = Rend(self);
            if (r == null || r.Materials.Length == 0 || r.Materials[0] == null) return null;
            if (!r.Instanced[0]) { r.Materials[0] = InstanceMaterial(r.Materials[0]); r.Instanced[0] = true; }
            return r.Materials[0];
        }
        public static Material[] UnityEngine_Renderer__GetMaterialArray(Renderer self)
        {
            var r = Rend(self);
            if (r == null) return new Material[0];
            for (int i = 0; i < r.Materials.Length; i++)
                if (r.Materials[i] != null && !r.Instanced[i]) { r.Materials[i] = InstanceMaterial(r.Materials[i]); r.Instanced[i] = true; }
            return (Material[])r.Materials.Clone();
        }
        static Material InstanceMaterial(Material m)
        {
            var copy = Lifecycle.NewManaged<Material>();
            Objects.Register(copy, new NObject(), (Objects.Of(m)?.Name ?? "") + " (Instance)");
            MaterialState.Set(copy, MaterialState.Of(m).Clone());
            return copy;
        }
        public static void UnityEngine_Renderer__get_bounds_Injected(Renderer self, out Bounds ret)
        {
            ret = default;
            if (Objects.Of(self) is NSpriteRenderer sr && sr.Sprite != null)
            {
                var lb = Sprites.LocalBounds(Sprites.Of(sr.Sprite));
                ret = TransformBounds(sr.GameObject.Transform.LocalToWorld, lb);
            }
            else if (Objects.Comp(self) is NComponent c) ret = new Bounds(c.GameObject.Transform.Position, Vector3.zero);
        }
        static Bounds TransformBounds(Matrix4x4 m, Bounds b)
        {
            var c = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var ext = new Vector3(
                Math.Abs(m.m00) * e.x + Math.Abs(m.m01) * e.y + Math.Abs(m.m02) * e.z,
                Math.Abs(m.m10) * e.x + Math.Abs(m.m11) * e.y + Math.Abs(m.m12) * e.z,
                Math.Abs(m.m20) * e.x + Math.Abs(m.m21) * e.y + Math.Abs(m.m22) * e.z);
            return new Bounds(c, ext * 2f);
        }
        public static void UnityEngine_Renderer__get_localToWorldMatrix_Injected(Renderer self, out Matrix4x4 ret) => ret = Objects.Comp(self)?.GameObject.Transform.LocalToWorld ?? Matrix4x4.identity;
        public static void UnityEngine_Renderer__get_worldToLocalMatrix_Injected(Renderer self, out Matrix4x4 ret)
        {
            ret = Matrix4x4.identity;
            var c = Objects.Comp(self);
            if (c != null) MathX.Invert(c.GameObject.Transform.LocalToWorld, out ret);
        }

        // ------------------------------------------------------------ SpriteRenderer

        static NSpriteRenderer SR(SpriteRenderer r) => Objects.Of(r) as NSpriteRenderer;

        public static Sprite UnityEngine_SpriteRenderer__GetSprite_INTERNAL(SpriteRenderer self) => SR(self)?.Sprite;
        public static void UnityEngine_SpriteRenderer__SetSprite_INTERNAL(SpriteRenderer self, Sprite sprite)
        {
            var r = SR(self);
            if (r == null) return;
            r.Sprite = sprite;
            if (sprite != null) Atlases.Touch(sprite);
        }
        public static void UnityEngine_SpriteRenderer__INTERNAL_get_color(SpriteRenderer self, out Color value) => value = SR(self)?.Color ?? Color.white;
        public static void UnityEngine_SpriteRenderer__INTERNAL_set_color(SpriteRenderer self, ref Color value) { var r = SR(self); if (r != null) r.Color = value; }
        public static bool UnityEngine_SpriteRenderer__get_flipX(SpriteRenderer self) => SR(self)?.FlipX ?? false;
        public static void UnityEngine_SpriteRenderer__set_flipX(SpriteRenderer self, bool value) { var r = SR(self); if (r != null) r.FlipX = value; }
        public static bool UnityEngine_SpriteRenderer__get_flipY(SpriteRenderer self) => SR(self)?.FlipY ?? false;
        public static void UnityEngine_SpriteRenderer__set_flipY(SpriteRenderer self, bool value) { var r = SR(self); if (r != null) r.FlipY = value; }
        public static SpriteDrawMode UnityEngine_SpriteRenderer__get_drawMode(SpriteRenderer self) => SR(self)?.DrawMode ?? 0;
        public static void UnityEngine_SpriteRenderer__set_drawMode(SpriteRenderer self, SpriteDrawMode value) { var r = SR(self); if (r != null) r.DrawMode = value; }
        public static void UnityEngine_SpriteRenderer__INTERNAL_get_size(SpriteRenderer self, out Vector2 value) => value = SR(self)?.Size ?? Vector2.one;
        public static void UnityEngine_SpriteRenderer__INTERNAL_set_size(SpriteRenderer self, ref Vector2 value) { var r = SR(self); if (r != null) r.Size = value; }
        public static SpriteMaskInteraction UnityEngine_SpriteRenderer__get_maskInteraction(SpriteRenderer self) => SR(self)?.MaskInteraction ?? 0;
        public static void UnityEngine_SpriteRenderer__set_maskInteraction(SpriteRenderer self, SpriteMaskInteraction value) { var r = SR(self); if (r != null) r.MaskInteraction = value; }
        public static SpriteTileMode UnityEngine_SpriteRenderer__get_tileMode(SpriteRenderer self) => SR(self)?.TileMode ?? 0;
        public static void UnityEngine_SpriteRenderer__set_tileMode(SpriteRenderer self, SpriteTileMode value) { var r = SR(self); if (r != null) r.TileMode = value; }
        public static float UnityEngine_SpriteRenderer__get_adaptiveModeThreshold(SpriteRenderer self) => SR(self)?.AdaptiveThreshold ?? 0.5f;
        public static void UnityEngine_SpriteRenderer__set_adaptiveModeThreshold(SpriteRenderer self, float value) { var r = SR(self); if (r != null) r.AdaptiveThreshold = value; }
        public static bool UnityEngine_SpriteRenderer__get_shouldSupportTiling(SpriteRenderer self) => (SR(self)?.DrawMode ?? 0) != SpriteDrawMode.Simple;
        public static void UnityEngine_SpriteRenderer__INTERNAL_CALL_GetSpriteBounds(SpriteRenderer self, out Bounds value)
            => value = Sprites.LocalBounds(Sprites.Of(SR(self)?.Sprite));

        // ------------------------------------------------------------ Sprite

        public static void UnityEngine_Sprite__INTERNAL_get_rect(Sprite self, out Rect value) => value = Sprites.Of(self)?.Rect ?? default;
        public static void UnityEngine_Sprite__INTERNAL_get_bounds(Sprite self, out Bounds value) => value = Sprites.LocalBounds(Sprites.Of(self));
        public static float UnityEngine_Sprite__get_pixelsPerUnit(Sprite self) => Sprites.Of(self)?.PixelsPerUnit ?? 100f;
        public static void UnityEngine_Sprite__INTERNAL_get_border(Sprite self, out Vector4 value) => value = Sprites.Of(self)?.Border ?? default;
        public static void UnityEngine_Sprite__Internal_GetPivot(Sprite sprite, out Vector2 output)
        {
            var i = Sprites.Of(sprite);
            output = i == null ? default : new Vector2(i.Pivot.x * i.Rect.width, i.Pivot.y * i.Rect.height);
        }
        public static Texture2D UnityEngine_Sprite__get_texture(Sprite self) => Sprites.Binding(self, out var t, out _, out _) ? t : null;
        public static Texture2D UnityEngine_Sprite__get_associatedAlphaSplitTexture(Sprite self) => null;
        public static void UnityEngine_Sprite__INTERNAL_get_textureRect(Sprite self, out Rect value) => value = Sprites.Binding(self, out _, out var r, out _) ? r : default;
        public static void UnityEngine_Sprite__Internal_GetTextureRectOffset(Sprite sprite, out Vector2 output) => output = Sprites.Of(sprite)?.TextureRectOffset ?? default;
        public static bool UnityEngine_Sprite__get_packed(Sprite self) => ((Sprites.Of(self)?.SettingsRaw ?? 0) & 1) != 0;
        public static SpritePackingMode UnityEngine_Sprite__get_packingMode(Sprite self) => (SpritePackingMode)(((Sprites.Of(self)?.SettingsRaw ?? 0) >> 1) & 1);
        public static SpritePackingRotation UnityEngine_Sprite__get_packingRotation(Sprite self) => (SpritePackingRotation)(((Sprites.Of(self)?.SettingsRaw ?? 0) >> 2) & 0xF);
        public static Vector2[] UnityEngine_Sprite__get_vertices(Sprite self) => (Vector2[])(Sprites.Of(self)?.Vertices.Clone() ?? new Vector2[0]);
        public static ushort[] UnityEngine_Sprite__get_triangles(Sprite self) => (ushort[])(Sprites.Of(self)?.Triangles.Clone() ?? new ushort[0]);
        public static Vector2[] UnityEngine_Sprite__get_uv(Sprite self) => Sprites.Uvs(self);

        public static Sprite UnityEngine_Sprite__INTERNAL_CALL_Create(Texture2D texture, ref Rect rect, ref Vector2 pivot, float pixelsPerUnit, uint extrude, SpriteMeshType meshType, ref Vector4 border, bool generateFallbackPhysicsShape)
        {
            if (texture == null) return null;
            var s = Lifecycle.NewManaged<Sprite>();
            Objects.Register(s, new NAsset(), "");
            float w = rect.width / pixelsPerUnit, h = rect.height / pixelsPerUnit;
            float x0 = -pivot.x * w, y0 = -pivot.y * h;
            var i = new Sprites.Info
            {
                Rect = rect, Pivot = pivot, PixelsPerUnit = pixelsPerUnit, Border = border, Texture = texture, TextureRect = rect,
                Vertices = new[] { new Vector2(x0, y0 + h), new Vector2(x0 + w, y0), new Vector2(x0 + w, y0 + h), new Vector2(x0, y0) },
                Triangles = new ushort[] { 0, 1, 2, 1, 0, 3 },
                UvTransform = new Vector4(pixelsPerUnit, rect.x + pivot.x * rect.width, pixelsPerUnit, rect.y + pivot.y * rect.height),
            };
            Sprites.Set(s, i);
            return s;
        }

        /// <summary>GameObject.CreatePrimitive: a GameObject with a MeshFilter, MeshRenderer and the matching collider.</summary>
        public static GameObject UnityEngine_GameObject__CreatePrimitive(PrimitiveType type)
        {
            var managed = Lifecycle.NewManaged<GameObject>();
            var go = Lifecycle.CreateGameObject(managed, type.ToString());
            Lifecycle.AddComponent(go, typeof(MeshFilter));
            Lifecycle.AddComponent(go, typeof(MeshRenderer));
            var col = type == PrimitiveType.Sphere ? typeof(SphereCollider) : type == PrimitiveType.Capsule ? typeof(CapsuleCollider)
                : type == PrimitiveType.Quad || type == PrimitiveType.Plane ? typeof(MeshCollider) : typeof(BoxCollider);
            Lifecycle.AddComponent(go, col);
            Scenes.AddRoot(go);
            return managed;
        }

        // ------------------------------------------------------------ SpriteAtlas

        public static void UnityEngine_U2D_SpriteAtlasManager__Register(SpriteAtlas spriteAtlas) { if (spriteAtlas != null) Atlases.Register(spriteAtlas); }
        public static string UnityEngine_U2D_SpriteAtlas__get_tag(SpriteAtlas self) => Atlases.Tag(self);
        public static bool UnityEngine_U2D_SpriteAtlas__get_isVariant(SpriteAtlas self) => Tree.Bool((Objects.Of(self) as NAsset)?.Source?.Tree, "m_IsVariant");
        public static int UnityEngine_U2D_SpriteAtlas__get_spriteCount(SpriteAtlas self) => Atlases.Packed(self).Count;
        // SpriteAtlas.GetSprite(s) returns clones of the packed sprites.
        static Sprite CloneSprite(Sprite s)
        {
            var c = Lifecycle.NewManaged<Sprite>();
            Objects.Register(c, new NAsset { Source = (Objects.Of(s) as NAsset)?.Source }, (Objects.Of(s)?.Name ?? "") + "(Clone)");
            return c;
        }
        public static Sprite UnityEngine_U2D_SpriteAtlas__GetSprite(SpriteAtlas self, string name)
        {
            foreach (var s in Atlases.Packed(self)) if (Objects.Of(s)?.Name == name) return CloneSprite(s);
            return null;
        }
        public static int UnityEngine_U2D_SpriteAtlas__GetSprites(SpriteAtlas self, Sprite[] sprites)
        {
            var list = Atlases.Packed(self);
            int n = Math.Min(list.Count, sprites?.Length ?? 0);
            for (int i = 0; i < n; i++) sprites[i] = CloneSprite(list[i]);
            return n;
        }
        public static int UnityEngine_U2D_SpriteAtlas__GetSpritesByName(SpriteAtlas self, Sprite[] sprites, string name)
        {
            int n = 0;
            foreach (var s in Atlases.Packed(self))
                if (Objects.Of(s)?.Name == name && sprites != null && n < sprites.Length) sprites[n++] = CloneSprite(s);
            return n;
        }

        // ------------------------------------------------------------ Texture

        static readonly ConditionalWeakTable<Texture, int[]> runtimeTextures = new ConditionalWeakTable<Texture, int[]>();

        public static int TextureWidth(Texture t)
        {
            if (t == null) return 0;
            if (runtimeTextures.TryGetValue(t, out var d)) return d[0];
            return (int)Tree.Int((Objects.Of(t) as NAsset)?.Source?.Tree, "m_Width");
        }
        public static int TextureHeight(Texture t)
        {
            if (t == null) return 0;
            if (runtimeTextures.TryGetValue(t, out var d)) return d[1];
            return (int)Tree.Int((Objects.Of(t) as NAsset)?.Source?.Tree, "m_Height");
        }
        public static void RuntimeTexture(Texture t, int w, int h, int format) { runtimeTextures.Remove(t); runtimeTextures.Add(t, new[] { w, h, format }); }

        public static int UnityEngine_Texture__Internal_GetWidth(Texture t) => TextureWidth(t);
        public static int UnityEngine_Texture__Internal_GetHeight(Texture t) => TextureHeight(t);
        public static UnityEngine.Rendering.TextureDimension UnityEngine_Texture__Internal_GetDimension(Texture t) => UnityEngine.Rendering.TextureDimension.Tex2D;
        public static void UnityEngine_Texture__INTERNAL_get_texelSize(Texture self, out Vector2 value)
        {
            int w = TextureWidth(self), h = TextureHeight(self);
            value = new Vector2(w > 0 ? 1f / w : 0f, h > 0 ? 1f / h : 0f);
        }
        /// <summary>A run-time texture's pixel: kept as RGBA, top row first (RuntimePixels), shown after Apply.</summary>
        public static void UnityEngine_Texture2D__INTERNAL_CALL_SetPixel(Texture2D self, int x, int y, ref Color color)
        {
            int w = TextureWidth(self), h = TextureHeight(self);
            if (w <= 0 || h <= 0) return;
            x = ((x % w) + w) % w; y = ((y % h) + h) % h;   // Unity wraps out-of-range coordinates
            var e = RuntimePixels.Get(self);
            if (e == null || e.Data == null || e.W != w || e.H != h || e.Channels != 4)
            {
                RuntimePixels.Set(self, new byte[w * h * 4], w, h, 4, e?.Version ?? 0);
                e = RuntimePixels.Get(self);
            }
            Color32 c = color;
            int i = ((h - 1 - y) * w + x) * 4;
            e.Data[i] = c.r; e.Data[i + 1] = c.g; e.Data[i + 2] = c.b; e.Data[i + 3] = c.a;
        }
        public static void UnityEngine_Texture2D__Apply(Texture2D self, bool updateMipmaps, bool makeNoLongerReadable)
        {
            var e = RuntimePixels.Get(self);
            if (e != null) e.Version++;
        }

        // Render textures aren't drawn into yet; their size is kept for the scripts that ask it.
        public static int UnityEngine_RenderTexture__Internal_GetWidth(RenderTexture mono)
            => Props.TryGet<int>(mono, "rt.width", out int w) ? w : (int)Tree.Int((Objects.Of(mono) as NAsset)?.Source?.Tree, "m_Width", ScreenWidth);
        public static int UnityEngine_RenderTexture__Internal_GetHeight(RenderTexture mono)
            => Props.TryGet<int>(mono, "rt.height", out int h) ? h : (int)Tree.Int((Objects.Of(mono) as NAsset)?.Source?.Tree, "m_Height", ScreenHeight);
        public static void UnityEngine_RenderTexture__Internal_SetWidth(RenderTexture mono, int width) => Props.Set(mono, "rt.width", width, "engine RenderTexture.width");
        public static void UnityEngine_RenderTexture__Internal_SetHeight(RenderTexture mono, int width) => Props.Set(mono, "rt.height", width, "engine RenderTexture.height");

        public static int UnityEngine_SortingLayer__NameToID(string name) => SortingLayers.TryId(name, out int id) ? id : 0;

        public static TextureFormat UnityEngine_Texture2D__get_format(Texture2D self)
            => runtimeTextures.TryGetValue(self, out var d) ? (TextureFormat)d[2] : (TextureFormat)Tree.Int((Objects.Of(self) as NAsset)?.Source?.Tree, "m_TextureFormat", 4);
        public static int UnityEngine_Texture2D__get_mipmapCount(Texture2D self) => (int)Tree.Int((Objects.Of(self) as NAsset)?.Source?.Tree, "m_MipCount", 1);

        static Texture2D white, black;
        static Texture2D SolidTexture(string name, byte v, byte a)
        {
            var t = Lifecycle.NewManaged<Texture2D>();
            Objects.Register(t, new NAsset(), name, persistent: true);
            RuntimeTexture(t, 4, 4, (int)TextureFormat.RGBA32);
            var px = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++) { px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = a; }
            RuntimePixels.Set(t, px, 4, 4, 4, 1);
            return t;
        }
        public static Texture2D UnityEngine_Texture2D__get_whiteTexture() => white ??= SolidTexture("UnityWhite", 255, 255);
        public static Texture2D UnityEngine_Texture2D__get_blackTexture() => black ??= SolidTexture("UnityBlack", 0, 255);

        // UI.Image geometry helpers (Sprites.DataUtility).
        public static void UnityEngine_Sprites_DataUtility__INTERNAL_CALL_GetOuterUV(Sprite sprite, out Vector4 value)
        {
            value = new Vector4(0, 0, 1, 1);
            if (!Sprites.Binding(sprite, out var tex, out var tr, out _) || tex == null) return;
            float w = TextureWidth(tex), h = TextureHeight(tex);
            if (w > 0 && h > 0) value = new Vector4(tr.xMin / w, tr.yMin / h, tr.xMax / w, tr.yMax / h);
        }
        public static void UnityEngine_Sprites_DataUtility__INTERNAL_CALL_GetInnerUV(Sprite sprite, out Vector4 value)
        {
            UnityEngine_Sprites_DataUtility__INTERNAL_CALL_GetOuterUV(sprite, out var o);
            var i = Sprites.Of(sprite);
            value = o;
            if (i == null || !Sprites.Binding(sprite, out var tex, out var tr, out _) || tex == null) return;
            float w = TextureWidth(tex), h = TextureHeight(tex);
            var b = i.Border;   // left, bottom, right, top (pixels)
            value = new Vector4((tr.xMin + b.x) / w, (tr.yMin + b.y) / h, (tr.xMax - b.z) / w, (tr.yMax - b.w) / h);
        }
        public static void UnityEngine_Sprites_DataUtility__INTERNAL_CALL_GetPadding(Sprite sprite, out Vector4 value)
        {
            value = default;
            var i = Sprites.Of(sprite);
            if (i == null || !Sprites.Binding(sprite, out _, out var tr, out _)) return;
            // Transparent margins trimmed by tight packing: left, bottom, right, top.
            var off = i.TextureRectOffset;
            value = new Vector4(off.x, off.y, i.Rect.width - tr.width - off.x, i.Rect.height - tr.height - off.y);
        }
        public static void UnityEngine_Sprites_DataUtility__Internal_GetMinSize(Sprite sprite, out Vector2 output)
        {
            var b = Sprites.Of(sprite)?.Border ?? default;
            output = new Vector2(b.x + b.z, b.y + b.w);
        }
        public static void UnityEngine_Matrix4x4__INTERNAL_CALL_Perspective(float fov, float aspect, float zNear, float zFar, out Matrix4x4 value)
        {
            float f = 1f / (float)Math.Tan(fov * Math.PI / 360.0);
            value = new Matrix4x4();
            value.m00 = f / aspect; value.m11 = f;
            value.m22 = (zFar + zNear) / (zNear - zFar); value.m23 = 2f * zFar * zNear / (zNear - zFar);
            value.m32 = -1f;
        }
        public static void UnityEngine_Texture2D__Internal_Create(Texture2D mono, int width, int height, TextureFormat format, bool mipmap, bool linear, IntPtr nativeTex)
        {
            if (Objects.Of(mono) == null) Objects.Register(mono, new NAsset(), "");
            RuntimeTexture(mono, width, height, (int)format);
        }

        // ------------------------------------------------------------ Material / Shader

        public static int UnityEngine_Shader__PropertyToID(string name) => ShaderProps.Id(name);
        public static bool UnityEngine_Shader__get_isSupported(Shader self) => true;
        public static Shader UnityEngine_Shader__Find(string name) => Shaders.Find(name);
        public static Shader UnityEngine_Shader__FindBuiltin(string name) => Shaders.Find(name);
        public static void UnityEngine_Shader__SetGlobalFloatImpl(int nameID, float value) => ShaderProps.Globals.Floats[nameID] = value;
        public static void UnityEngine_Shader__SetGlobalIntImpl(int nameID, int value) => ShaderProps.Globals.Floats[nameID] = value;
        public static void UnityEngine_Shader__INTERNAL_CALL_SetGlobalVectorImpl(int nameID, ref Vector4 value) => ShaderProps.Globals.Vectors[nameID] = value;
        public static void UnityEngine_Shader__INTERNAL_CALL_SetGlobalColorImpl(int nameID, ref Color value) => ShaderProps.Globals.Vectors[nameID] = value;
        public static void UnityEngine_Shader__SetGlobalTextureImpl(int nameID, Texture value) => ShaderProps.Globals.Textures[nameID] = value;
        public static float UnityEngine_Shader__GetGlobalFloatImpl(int nameID) => ShaderProps.Globals.Floats.TryGetValue(nameID, out var v) ? v : 0f;
        public static int UnityEngine_Shader__GetGlobalIntImpl(int nameID) => (int)UnityEngine_Shader__GetGlobalFloatImpl(nameID);
        public static void UnityEngine_Shader__INTERNAL_CALL_GetGlobalVectorImpl(int nameID, out Vector4 value) => ShaderProps.Globals.Vectors.TryGetValue(nameID, out value);
        public static void UnityEngine_Shader__INTERNAL_CALL_GetGlobalColorImpl(int nameID, out Color value) => value = ShaderProps.Globals.Vectors.TryGetValue(nameID, out var v) ? (Color)v : default;
        public static Texture UnityEngine_Shader__GetGlobalTextureImpl(int nameID) => ShaderProps.Globals.Textures.TryGetValue(nameID, out var t) ? t : null;

        static MaterialState Mat(Material m) => MaterialState.Of(m);

        public static void UnityEngine_Material__Internal_CreateWithShader(Material mono, Shader shader)
        {
            if (Objects.Of(mono) == null) Objects.Register(mono, new NObject(), Objects.Of(shader)?.Name ?? "");
            MaterialState.Set(mono, new MaterialState { Shader = shader });
        }
        public static void UnityEngine_Material__Internal_CreateWithMaterial(Material mono, Material source)
        {
            if (Objects.Of(mono) == null) Objects.Register(mono, new NObject(), Objects.Of(source)?.Name ?? "");
            MaterialState.Set(mono, source != null ? Mat(source).Clone() : new MaterialState());
        }
        public static void UnityEngine_Material__Internal_CreateWithString(Material mono, string contents)
        {
            if (Objects.Of(mono) == null) Objects.Register(mono, new NObject(), "");
            MaterialState.Set(mono, new MaterialState());
        }
        public static void UnityEngine_Material__CopyPropertiesFromMaterial(Material self, Material mat)
        {
            if (self == null || mat == null) return;
            var s = Mat(mat).Clone();
            s.Shader = Mat(self).Shader;
            MaterialState.Set(self, s);
        }
        public static Shader UnityEngine_Material__get_shader(Material self) => Mat(self)?.Shader;
        public static void UnityEngine_Material__set_shader(Material self, Shader value) { var s = Mat(self); if (s != null) s.Shader = value; }
        public static int UnityEngine_Material__get_renderQueue(Material self) => Mat(self)?.RenderQueue is int q && q >= 0 ? q : 3000;
        public static void UnityEngine_Material__set_renderQueue(Material self, int value) { var s = Mat(self); if (s != null) s.RenderQueue = value; }
        public static bool UnityEngine_Material__HasProperty(Material self, int nameID) => Mat(self)?.Has(nameID) ?? false;
        public static void UnityEngine_Material__SetFloatImpl(Material self, int nameID, float value) { var s = Mat(self); if (s != null) s.Floats[nameID] = value; }
        public static void UnityEngine_Material__SetIntImpl(Material self, int nameID, int value) { var s = Mat(self); if (s != null) s.Floats[nameID] = value; }
        public static float UnityEngine_Material__GetFloatImpl(Material self, int nameID) => Mat(self) is MaterialState s && s.Floats.TryGetValue(nameID, out var v) ? v : 0f;
        public static int UnityEngine_Material__GetIntImpl(Material self, int nameID) => (int)UnityEngine_Material__GetFloatImpl(self, nameID);
        public static void UnityEngine_Material__INTERNAL_CALL_SetColorImpl(Material self, int nameID, ref Color value) { var s = Mat(self); if (s != null) s.Vectors[nameID] = value; }
        public static void UnityEngine_Material__INTERNAL_CALL_SetVectorImpl(Material self, int nameID, ref Vector4 value) { var s = Mat(self); if (s != null) s.Vectors[nameID] = value; }
        public static void UnityEngine_Material__INTERNAL_CALL_GetColorImpl(Material self, int nameID, out Color value) => value = Mat(self) is MaterialState s && s.Vectors.TryGetValue(nameID, out var v) ? (Color)v : default;
        public static void UnityEngine_Material__INTERNAL_CALL_GetVectorImpl(Material self, int nameID, out Vector4 value) { value = default; Mat(self)?.Vectors.TryGetValue(nameID, out value); }
        public static void UnityEngine_Material__SetTextureImpl(Material self, int nameID, Texture value) { var s = Mat(self); if (s != null) s.Textures[nameID] = value; }
        public static Texture UnityEngine_Material__GetTextureImpl(Material self, int nameID) => Mat(self) is MaterialState s && s.Textures.TryGetValue(nameID, out var t) ? t : null;
        public static void UnityEngine_Material__INTERNAL_CALL_GetTextureScaleAndOffsetImpl(Material self, int nameID, out Vector4 value)
            => value = Mat(self) is MaterialState s && s.TexST.TryGetValue(nameID, out var v) ? v : new Vector4(1, 1, 0, 0);
        public static void UnityEngine_Material__INTERNAL_CALL_SetTextureOffsetImpl(Material self, int nameID, ref Vector2 offset)
        {
            var s = Mat(self);
            if (s == null) return;
            var v = s.TexST.TryGetValue(nameID, out var o) ? o : new Vector4(1, 1, 0, 0);
            s.TexST[nameID] = new Vector4(v.x, v.y, offset.x, offset.y);
        }
        public static void UnityEngine_Material__INTERNAL_CALL_SetTextureScaleImpl(Material self, int nameID, ref Vector2 scale)
        {
            var s = Mat(self);
            if (s == null) return;
            var v = s.TexST.TryGetValue(nameID, out var o) ? o : new Vector4(1, 1, 0, 0);
            s.TexST[nameID] = new Vector4(scale.x, scale.y, v.z, v.w);
        }
        public static void UnityEngine_Material__EnableKeyword(Material self, string keyword) { var s = Mat(self); if (s != null && !s.Keywords.Contains(keyword)) s.Keywords.Add(keyword); }
        public static void UnityEngine_Material__DisableKeyword(Material self, string keyword) => Mat(self)?.Keywords.Remove(keyword);
        public static bool UnityEngine_Material__IsKeywordEnabled(Material self, string keyword) => Mat(self)?.Keywords.Contains(keyword) ?? false;
        public static string[] UnityEngine_Material__get_shaderKeywords(Material self) => Mat(self)?.Keywords.ToArray() ?? new string[0];
        public static void UnityEngine_Material__set_shaderKeywords(Material self, string[] value)
        {
            var s = Mat(self);
            if (s == null) return;
            s.Keywords.Clear();
            if (value != null) s.Keywords.AddRange(value);
        }
        /// <summary>QualitySettings.vSyncCount: kept for the presenter (the console presents at vblank anyway).</summary>
        public static int VSyncCount = 1;
        public static int UnityEngine_QualitySettings__get_vSyncCount() => VSyncCount;
        public static void UnityEngine_QualitySettings__set_vSyncCount(int value) => VSyncCount = value;

        public static string UnityEngine_Material__GetTag(Material self, string tag, bool searchFallbacks, string defaultValue) => defaultValue;
        public static int UnityEngine_Material__get_passCount(Material self) => 1;
    }

    /// <summary>TagManager sorting layers: id -> order.</summary>
    public static class SortingLayers
    {
        public static readonly List<(string name, int id)> Layers = new List<(string, int)> { ("Default", 0) };

        public static int Index(int id)
        {
            for (int i = 0; i < Layers.Count; i++) if (Layers[i].id == id) return i;
            return -1;
        }
        public static string Name(int id) { int i = Index(id); return i >= 0 ? Layers[i].name : ""; }
        public static bool TryId(string name, out int id)
        {
            foreach (var l in Layers) if (l.name == name) { id = l.id; return true; }
            id = 0;
            return false;
        }
    }

    /// <summary>Shader.Find over the shaders the build ships (name-only stubs from the converter).</summary>
    public static class Shaders
    {
        static Dictionary<string, (ChobFile file, long pathId)> index;
        static readonly string[] files = { "globalgamemanagers.assets", "resources.assets", "unity default resources", "unity_builtin_extra" };

        public static Shader Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (index == null)
            {
                index = new Dictionary<string, (ChobFile, long)>(StringComparer.Ordinal);
                foreach (var fn in files)
                {
                    var f = AssetDb.File(fn);
                    if (f == null) continue;
                    foreach (var o in f.Ordered)
                        if (o.Type == "Shader")
                        {
                            string n = Tree.Str(o.Tree, "m_Name");
                            if (n.Length > 0 && !index.ContainsKey(n)) index[n] = (f, o.PathId);
                        }
                }
            }
            if (!index.TryGetValue(name, out var e)) return null;
            return AssetDb.Resolve(e.file, new PPtr(0, e.pathId)) as Shader;
        }
    }
}
