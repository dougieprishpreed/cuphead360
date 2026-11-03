using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    // Native side of Unity UI: RectTransform layout, Canvas, CanvasGroup,
    // CanvasRenderer and Mesh data. UGUI and TextMesh Pro (managed, the
    // game's own) build meshes during Canvas.SendWillRenderCanvases and hand
    // them to CanvasRenderers; RenderList draws them.

    public sealed class NRectTransform : NTransform
    {
        public Vector2 AnchorMin = new Vector2(0.5f, 0.5f), AnchorMax = new Vector2(0.5f, 0.5f);
        public Vector2 SizeDelta = new Vector2(100, 100), Pivot = new Vector2(0.5f, 0.5f);

        public override void Load(ChobObject co, SceneBuild b)
        {
            var t = co.Tree;
            AnchorMin = Tree.Vec2(t, "m_AnchorMin"); AnchorMax = Tree.Vec2(t, "m_AnchorMax");
            SizeDelta = Tree.Vec2(t, "m_SizeDelta"); Pivot = Tree.Vec2(t, "m_Pivot");
            SerializedAnchored = Tree.Vec2(t, "m_AnchoredPosition");
        }

        /// <summary>The layout input as saved; localPosition is derived from it once the parents are laid out.</summary>
        public Vector2 SerializedAnchored;

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NRectTransform)src;
            AnchorMin = s.AnchorMin; AnchorMax = s.AnchorMax; SizeDelta = s.SizeDelta; Pivot = s.Pivot;
        }

        public Rect ParentRect => Parent is NRectTransform p ? p.Rect : new Rect(0, 0, 0, 0);

        public Vector2 Size
        {
            get
            {
                var pr = ParentRect;
                return SizeDelta + Vector2.Scale(AnchorMax - AnchorMin, pr.size);
            }
        }

        /// <summary>The rect in local space (pivot at the origin).</summary>
        public Rect Rect
        {
            get
            {
                var s = Size;
                return new Rect(-Pivot.x * s.x, -Pivot.y * s.y, s.x, s.y);
            }
        }

        /// <summary>Where anchoredPosition is measured from, in the parent's local space.</summary>
        public Vector2 Reference
        {
            get
            {
                var pr = ParentRect;
                var a = new Vector2(Mathf.Lerp(AnchorMin.x, AnchorMax.x, Pivot.x), Mathf.Lerp(AnchorMin.y, AnchorMax.y, Pivot.y));
                return pr.position + Vector2.Scale(a, pr.size);
            }
        }

        public Vector2 AnchoredPosition
        {
            get => new Vector2(LocalPosition.x, LocalPosition.y) - Reference;
            set { var r = Reference; LocalPosition = new Vector3(r.x + value.x, r.y + value.y, LocalPosition.z); Invalidate(); }
        }

        /// <summary>Change layout inputs keeping every child's anchoredPosition (Unity re-lays out children).</summary>
        public void Change(Action change)
        {
            // The whole subtree is recorded first: a grandchild's anchoredPosition
            // depends on its parent's rect, which this change may resize.
            var snap = Snapshot(this);
            change();
            Restore(snap, 0, true);
        }

        /// <summary>Unity's OnRectTransformDimensionsChange (UGUI graphics and layout react to it).</summary>
        void DimensionsChanged()
        {
            if (GameObject == null || !GameObject.ActiveInHierarchy) return;
            foreach (var c in GameObject.Components.ToArray())
                if (c is NBehaviour b && Behaviours.ActiveAndEnabled(b))
                {
                    var m = MessageSet.Find(b.Managed.GetType(), "OnRectTransformDimensionsChange");
                    if (m != null) Behaviours.Send(b, m);
                }
        }

        /// <summary>
        /// The anchored positions and sizes of every rect in a hierarchy, parents
        /// first. Unity stores anchoredPosition, not localPosition, so a rect that
        /// moves to another parent (SetParent, Instantiate under a parent) keeps
        /// it and its local position follows the new parent's rect.
        /// </summary>
        public static List<(NRectTransform t, Vector2 ap, Vector2 size)> Snapshot(NTransform root)
        {
            var list = new List<(NRectTransform, Vector2, Vector2)>();
            void Walk(NTransform t, bool top)
            {
                var r = t as NRectTransform;
                if (r != null) list.Add((r, r.AnchoredPosition, r.Size));
                // Only a stretching rect's size follows its parent, so only its children can move.
                if (top || r == null || r.AnchorMin != r.AnchorMax)
                    foreach (var c in t.Children) Walk(c, false);
            }
            Walk(root, true);
            return list;
        }

        /// <summary>Re-apply a Snapshot (from entry <paramref name="from"/> on) after the hierarchy moved.</summary>
        public static void Restore(List<(NRectTransform t, Vector2 ap, Vector2 size)> snap, int from, bool notify)
        {
            for (int i = from; i < snap.Count; i++)
            {
                var (t, ap, size) = snap[i];
                t.AnchoredPosition = ap;
                var s = t.Size;
                if (notify && s != size) t.DimensionsChanged();
            }
        }
    }

    public sealed class NCanvas : NComponent
    {
        public RenderMode Mode;
        public Camera WorldCamera;
        public float PlaneDistance = 100f, ScaleFactor = 1f, ReferencePixelsPerUnit = 100f;
        public bool PixelPerfect, OverrideSorting, OverridePixelPerfect;
        public int SortingLayerID, SortingOrder, TargetDisplay;
        public AdditionalCanvasShaderChannels Channels;

        public override void Load(ChobObject co, SceneBuild b)
        {
            var t = co.Tree;
            Mode = (RenderMode)Tree.Int(t, "m_RenderMode");
            WorldCamera = b.Resolve(Tree.Ptr(t, "m_Camera")) as Camera;
            PlaneDistance = Tree.Float(t, "m_PlaneDistance", 100f);
            PixelPerfect = Tree.Bool(t, "m_PixelPerfect");
            OverrideSorting = Tree.Bool(t, "m_OverrideSorting");
            OverridePixelPerfect = Tree.Bool(t, "m_OverridePixelPerfect");
            SortingLayerID = (int)Tree.Int(t, "m_SortingLayerID");
            SortingOrder = (int)Tree.Int(t, "m_SortingOrder");
            TargetDisplay = (int)Tree.Int(t, "m_TargetDisplay");
            Channels = (AdditionalCanvasShaderChannels)Tree.Int(t, "m_AdditionalShaderChannelsFlag");
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NCanvas)src;
            Mode = s.Mode; PlaneDistance = s.PlaneDistance; ScaleFactor = s.ScaleFactor; ReferencePixelsPerUnit = s.ReferencePixelsPerUnit;
            PixelPerfect = s.PixelPerfect; OverrideSorting = s.OverrideSorting; OverridePixelPerfect = s.OverridePixelPerfect;
            SortingLayerID = s.SortingLayerID; SortingOrder = s.SortingOrder; TargetDisplay = s.TargetDisplay; Channels = s.Channels;
            WorldCamera = s.WorldCamera != null && map != null && Objects.Of(s.WorldCamera) is NObject wc && map.TryGetValue(wc, out var m) ? (Camera)m.Managed : s.WorldCamera;
        }

        public NCanvas Parent
        {
            get
            {
                for (var t = GameObject.Transform.Parent; t != null; t = t.Parent)
                    foreach (var c in t.GameObject.Components)
                        if (c is NCanvas cv && !cv.Destroyed) return cv;
                return null;
            }
        }

        public NCanvas Root { get { var c = this; for (var p = c.Parent; p != null; p = p.Parent) c = p; return c; } }
        public bool IsRoot => Parent == null;

        /// <summary>Unity sizes a screen-space root canvas to its screen or camera every frame.</summary>
        public void FitRoot()
        {
            if (!IsRoot || Mode == RenderMode.WorldSpace || !(GameObject.Transform is NRectTransform rt)) return;
            var cam = Mode == RenderMode.ScreenSpaceCamera ? Objects.Of(WorldCamera) as NCamera : null;
            Rect px = cam != null ? cam.PixelRect : new Rect(0, 0, Icalls.ScreenWidth, Icalls.ScreenHeight);
            float sf = ScaleFactor <= 0 ? 1f : ScaleFactor;
            var size = new Vector2(px.width / sf, px.height / sf);
            if (rt.AnchorMin != Vector2.zero || rt.AnchorMax != Vector2.zero || rt.SizeDelta != size || rt.Pivot != new Vector2(0.5f, 0.5f))
                rt.Change(() =>
                {
                    rt.AnchorMin = Vector2.zero; rt.AnchorMax = Vector2.zero; rt.SizeDelta = size;
                    rt.Pivot = new Vector2(0.5f, 0.5f);   // driven by the canvas
                });
            Vector3 pos; Quaternion rot; float scale;
            if (cam == null)
            {
                pos = new Vector3(px.x + px.width * rt.Pivot.x, px.y + px.height * rt.Pivot.y, 0);
                rot = Quaternion.identity;
                scale = sf;
            }
            else
            {
                var ct = cam.GameObject.Transform;
                float worldHeight = cam.Ortho ? cam.OrthoSize * 2f : 2f * PlaneDistance * Mathf.Tan(cam.Fov * 0.5f * Mathf.Deg2Rad);
                scale = size.y > 0 ? worldHeight / size.y : 1f;
                rot = ct.Rotation;
                pos = ct.Position + ct.Rotation * new Vector3(0, 0, PlaneDistance);
            }
            // The world pose goes through the parent (root canvases may sit under another object).
            var parent = rt.Parent;
            if (parent == null) { rt.LocalPosition = pos; rt.LocalRotation = rot; rt.LocalScale = new Vector3(scale, scale, scale); }
            else
            {
                MathX.Invert(parent.LocalToWorld, out var inv);
                rt.LocalPosition = inv.MultiplyPoint3x4(pos);
                rt.LocalRotation = MathX.Inverse(parent.Rotation) * rot;
                var ps = parent.LossyScale;
                rt.LocalScale = new Vector3(ps.x != 0 ? scale / ps.x : scale, ps.y != 0 ? scale / ps.y : scale, ps.z != 0 ? scale / ps.z : scale);
            }
            rt.Invalidate();
        }
    }

    public sealed class NCanvasGroup : NComponent
    {
        public float Alpha = 1f;
        public bool Interactable = true, BlocksRaycasts = true, IgnoreParentGroups;

        public override void Load(ChobObject co, SceneBuild b)
        {
            Alpha = Tree.Float(co.Tree, "m_Alpha", 1f);
            Interactable = Tree.Bool(co.Tree, "m_Interactable", true);
            BlocksRaycasts = Tree.Bool(co.Tree, "m_BlocksRaycasts", true);
            IgnoreParentGroups = Tree.Bool(co.Tree, "m_IgnoreParentGroups");
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NCanvasGroup)src;
            Alpha = s.Alpha; Interactable = s.Interactable; BlocksRaycasts = s.BlocksRaycasts; IgnoreParentGroups = s.IgnoreParentGroups;
        }
    }

    /// <summary>Vertex and index data of a Mesh (as the native object keeps it).</summary>
    public sealed class MeshData
    {
        public Vector3[] Vertices = new Vector3[0];
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Color32[] Colors;
        public Vector4[][] Uv = new Vector4[4][];
        public List<int[]> SubMeshes = new List<int[]> { new int[0] };
        public List<MeshTopology> Topologies = new List<MeshTopology> { MeshTopology.Triangles };
        public Bounds Bounds;

        static readonly ConditionalWeakTable<Mesh, MeshData> meshes = new ConditionalWeakTable<Mesh, MeshData>();

        public static MeshData Of(Mesh m)
        {
            if (m == null) return null;
            return meshes.GetValue(m, _ => new MeshData());
        }

        /// <summary>A copy in dst's arrays where their sizes match (UGUI hands every graphic the same worker mesh).</summary>
        public MeshData CopyInto(MeshData dst)
        {
            if (dst == null || dst == this) return Copy();
            dst.Vertices = Reuse(dst.Vertices, Vertices);
            dst.Normals = Normals == null ? null : Reuse(dst.Normals, Normals);
            dst.Tangents = Tangents == null ? null : Reuse(dst.Tangents, Tangents);
            dst.Colors = Colors == null ? null : Reuse(dst.Colors, Colors);
            for (int i = 0; i < 4; i++) dst.Uv[i] = Uv[i] == null ? null : Reuse(dst.Uv[i], Uv[i]);
            dst.Bounds = Bounds;
            for (int s = 0; s < SubMeshes.Count; s++)
            {
                if (s < dst.SubMeshes.Count) dst.SubMeshes[s] = Reuse(dst.SubMeshes[s], SubMeshes[s]);
                else dst.SubMeshes.Add((int[])SubMeshes[s].Clone());
            }
            if (dst.SubMeshes.Count > SubMeshes.Count) dst.SubMeshes.RemoveRange(SubMeshes.Count, dst.SubMeshes.Count - SubMeshes.Count);
            dst.Topologies.Clear();
            dst.Topologies.AddRange(Topologies);
            return dst;
        }

        static T[] Reuse<T>(T[] dst, T[] src)
        {
            if (dst == null || dst.Length != src.Length) dst = new T[src.Length];
            Array.Copy(src, dst, src.Length);
            return dst;
        }

        public MeshData Copy()
        {
            var c = new MeshData
            {
                Vertices = (Vector3[])Vertices.Clone(), Normals = (Vector3[])Normals?.Clone(), Tangents = (Vector4[])Tangents?.Clone(),
                Colors = (Color32[])Colors?.Clone(), Bounds = Bounds,
            };
            for (int i = 0; i < 4; i++) c.Uv[i] = (Vector4[])Uv[i]?.Clone();
            c.SubMeshes = SubMeshes.ConvertAll(s => (int[])s.Clone());
            c.Topologies = new List<MeshTopology>(Topologies);
            return c;
        }

        public void RecalculateBounds()
        {
            if (Vertices.Length == 0) { Bounds = default; return; }
            Vector3 min = Vertices[0], max = Vertices[0];
            foreach (var v in Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            Bounds = new Bounds((min + max) * 0.5f, max - min);
        }
    }

    public sealed class NCanvasRenderer : NComponent
    {
        public MeshData Mesh;
        public Material[] Materials = new Material[0];
        public Material[] PopMaterials = new Material[0];
        public Texture Texture, AlphaTexture;
        public Color Color = Color.white;
        public bool Cull, IsMask, HasPopInstruction;
        public Rect? Clip;

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NCanvasRenderer)src;
            Color = s.Color; Cull = s.Cull;
        }

        /// <summary>Alpha after the CanvasGroups above it (Unity's inherited alpha).</summary>
        public float InheritedAlpha
        {
            get
            {
                float a = 1f;
                for (var t = GameObject.Transform; t != null; t = t.Parent)
                {
                    foreach (var c in t.GameObject.Components)
                        if (c is NCanvasGroup g && g.Enabled)
                        {
                            a *= g.Alpha;
                            if (g.IgnoreParentGroups) return a;
                        }
                    foreach (var c in t.GameObject.Components)
                        if (c is NCanvas cv && cv.OverrideSorting) return a;
                }
                return a;
            }
        }

        public NCanvas Canvas
        {
            get
            {
                for (var t = GameObject.Transform; t != null; t = t.Parent)
                    foreach (var c in t.GameObject.Components)
                        if (c is NCanvas cv && !cv.Destroyed && cv.Enabled) return cv;
                return null;
            }
        }
    }

    public static class UI
    {
        /// <summary>Before rendering: fit root canvases, then let UGUI/TMP rebuild layout and meshes.</summary>
        static readonly List<NCanvas> canvases = new List<NCanvas>();

        public static void WillRender()
        {
            canvases.Clear();
            canvases.AddRange(Typed.Canvases);
            foreach (var c in canvases)
                if (c.Enabled && c.GameObject != null && c.GameObject.ActiveInHierarchy) c.FitRoot();
            canvases.Clear();
            try { Canvas.SendWillRenderCanvases(); }
            catch (Exception e) { Exceptions.Report(e, null, "Canvas.willRenderCanvases"); }
        }

        static Material defaultMaterial;

        public static Material DefaultMaterial()
        {
            if (defaultMaterial != null) return defaultMaterial;
            var m = Lifecycle.NewManaged<Material>();
            Objects.Register(m, new NObject(), "Default UI Material", persistent: true);
            MaterialState.Set(m, new MaterialState { Shader = Shaders.Find("UI/Default") });
            defaultMaterial = m;
            return m;
        }
    }

    public static partial class Icalls
    {
        // ------------------------------------------------------------ RectTransform

        static NRectTransform RT(RectTransform t) => Objects.Of(t) as NRectTransform;

        public static void UnityEngine_RectTransform__INTERNAL_get_rect(RectTransform self, out Rect value) => value = RT(self)?.Rect ?? default;
        public static void UnityEngine_RectTransform__INTERNAL_get_anchorMin(RectTransform self, out Vector2 value) => value = RT(self)?.AnchorMin ?? default;
        public static void UnityEngine_RectTransform__INTERNAL_set_anchorMin(RectTransform self, ref Vector2 value) { var t = RT(self); var v = value; t?.Change(() => t.AnchorMin = v); }
        public static void UnityEngine_RectTransform__INTERNAL_get_anchorMax(RectTransform self, out Vector2 value) => value = RT(self)?.AnchorMax ?? default;
        public static void UnityEngine_RectTransform__INTERNAL_set_anchorMax(RectTransform self, ref Vector2 value) { var t = RT(self); var v = value; t?.Change(() => t.AnchorMax = v); }
        public static void UnityEngine_RectTransform__INTERNAL_get_anchoredPosition(RectTransform self, out Vector2 value) => value = RT(self)?.AnchoredPosition ?? default;
        public static void UnityEngine_RectTransform__INTERNAL_set_anchoredPosition(RectTransform self, ref Vector2 value) { var t = RT(self); if (t != null) t.AnchoredPosition = value; }
        public static void UnityEngine_RectTransform__INTERNAL_get_sizeDelta(RectTransform self, out Vector2 value) => value = RT(self)?.SizeDelta ?? default;
        static readonly string traceRt = Environment.GetEnvironmentVariable("CH360_TRACE_RT");
        public static void UnityEngine_RectTransform__INTERNAL_set_sizeDelta(RectTransform self, ref Vector2 value)
        {
            var t = RT(self); var v = value;
            if (traceRt != null && t?.GameObject?.Name == traceRt) Log.Line($"TRACE rt {traceRt} sizeDelta {t.SizeDelta} -> {v} frame {Clock.FrameCount} {Environment.StackTrace}");
            t?.Change(() => t.SizeDelta = v);
        }
        public static void UnityEngine_RectTransform__INTERNAL_get_pivot(RectTransform self, out Vector2 value) => value = RT(self)?.Pivot ?? default;
        public static void UnityEngine_RectTransform__INTERNAL_set_pivot(RectTransform self, ref Vector2 value)
        {
            // Changing the pivot keeps anchoredPosition (the rect moves around the new pivot).
            var t = RT(self); var v = value; t?.Change(() => t.Pivot = v);
        }
        public static UObject UnityEngine_RectTransform__get_drivenByObject(RectTransform self) => null;
        public static void UnityEngine_RectTransform__set_drivenByObject(RectTransform self, UObject value) { }
        public static DrivenTransformProperties UnityEngine_RectTransform__get_drivenProperties(RectTransform self) => 0;
        public static void UnityEngine_RectTransform__set_drivenProperties(RectTransform self, DrivenTransformProperties value) { }
        public static void UnityEngine_RectTransform__ForceUpdateRectTransforms(RectTransform self) { }

        public static bool UnityEngine_RectTransformUtility__INTERNAL_CALL_RectangleContainsScreenPoint(RectTransform rect, ref Vector2 screenPoint, Camera cam)
        {
            var t = RT(rect);
            if (t == null) return false;
            var c = Objects.Of(cam) as NCamera;
            var r = t.Rect;
            var m = t.LocalToWorld;
            Vector3 world;
            if (c == null) world = new Vector3(screenPoint.x, screenPoint.y, 0);
            else { var sp = new Vector3(screenPoint.x, screenPoint.y, 0); UnityEngine_Camera__INTERNAL_CALL_ScreenToWorldPoint(cam, ref sp, out world); }
            MathX.Invert(m, out var inv);
            var local = inv.MultiplyPoint3x4(world);
            return r.Contains(new Vector2(local.x, local.y));
        }
        public static void UnityEngine_RectTransformUtility__INTERNAL_CALL_PixelAdjustPoint(ref Vector2 point, Transform elementTransform, Canvas canvas, out Vector2 value) => value = point;
        public static void UnityEngine_RectTransformUtility__INTERNAL_CALL_PixelAdjustRect(RectTransform rectTransform, Canvas canvas, out Rect value) => value = RT(rectTransform)?.Rect ?? default;

        // ------------------------------------------------------------ Canvas

        static NCanvas Cv(Canvas c) => Objects.Of(c) as NCanvas;

        public static RenderMode UnityEngine_Canvas__get_renderMode(Canvas self) => Cv(self)?.Mode ?? 0;
        public static void UnityEngine_Canvas__set_renderMode(Canvas self, RenderMode value) { var c = Cv(self); if (c != null) c.Mode = value; }
        public static bool UnityEngine_Canvas__get_isRootCanvas(Canvas self) => Cv(self)?.IsRoot ?? true;
        public static Canvas UnityEngine_Canvas__get_rootCanvas(Canvas self) => (Canvas)(Cv(self)?.Root.Managed ?? self);
        public static Camera UnityEngine_Canvas__get_worldCamera(Canvas self) => Cv(self)?.WorldCamera;
        public static void UnityEngine_Canvas__set_worldCamera(Canvas self, Camera value) { var c = Cv(self); if (c != null) c.WorldCamera = value; }
        public static void UnityEngine_Canvas__INTERNAL_get_pixelRect(Canvas self, out Rect value)
        {
            var c = Cv(self);
            var cam = c != null && c.Mode == RenderMode.ScreenSpaceCamera ? Objects.Of(c.WorldCamera) as NCamera : null;
            value = cam != null ? cam.PixelRect : new Rect(0, 0, ScreenWidth, ScreenHeight);
        }
        public static float UnityEngine_Canvas__get_scaleFactor(Canvas self) => Cv(self)?.ScaleFactor ?? 1f;
        public static void UnityEngine_Canvas__set_scaleFactor(Canvas self, float value) { var c = Cv(self); if (c != null) c.ScaleFactor = value; }
        public static float UnityEngine_Canvas__get_referencePixelsPerUnit(Canvas self) => Cv(self)?.ReferencePixelsPerUnit ?? 100f;
        public static void UnityEngine_Canvas__set_referencePixelsPerUnit(Canvas self, float value) { var c = Cv(self); if (c != null) c.ReferencePixelsPerUnit = value; }
        public static bool UnityEngine_Canvas__get_pixelPerfect(Canvas self) => Cv(self)?.PixelPerfect ?? false;
        public static void UnityEngine_Canvas__set_pixelPerfect(Canvas self, bool value) { var c = Cv(self); if (c != null) c.PixelPerfect = value; }
        public static bool UnityEngine_Canvas__get_overridePixelPerfect(Canvas self) => Cv(self)?.OverridePixelPerfect ?? false;
        public static void UnityEngine_Canvas__set_overridePixelPerfect(Canvas self, bool value) { var c = Cv(self); if (c != null) c.OverridePixelPerfect = value; }
        public static float UnityEngine_Canvas__get_planeDistance(Canvas self) => Cv(self)?.PlaneDistance ?? 100f;
        public static void UnityEngine_Canvas__set_planeDistance(Canvas self, float value) { var c = Cv(self); if (c != null) c.PlaneDistance = value; }
        public static bool UnityEngine_Canvas__get_overrideSorting(Canvas self) => Cv(self)?.OverrideSorting ?? false;
        public static void UnityEngine_Canvas__set_overrideSorting(Canvas self, bool value) { var c = Cv(self); if (c != null) c.OverrideSorting = value; }
        public static int UnityEngine_Canvas__get_sortingOrder(Canvas self) => Cv(self)?.SortingOrder ?? 0;
        public static void UnityEngine_Canvas__set_sortingOrder(Canvas self, int value) { var c = Cv(self); if (c != null) c.SortingOrder = value; }
        public static int UnityEngine_Canvas__get_sortingLayerID(Canvas self) => Cv(self)?.SortingLayerID ?? 0;
        public static void UnityEngine_Canvas__set_sortingLayerID(Canvas self, int value) { var c = Cv(self); if (c != null) c.SortingLayerID = value; }
        public static string UnityEngine_Canvas__get_sortingLayerName(Canvas self) => SortingLayers.Name(Cv(self)?.SortingLayerID ?? 0);
        public static void UnityEngine_Canvas__set_sortingLayerName(Canvas self, string value) { var c = Cv(self); if (c != null && SortingLayers.TryId(value, out int id)) c.SortingLayerID = id; }
        public static int UnityEngine_Canvas__get_cachedSortingLayerValue(Canvas self) => SortingLayers.Index(Cv(self)?.SortingLayerID ?? 0);
        public static int UnityEngine_Canvas__get_targetDisplay(Canvas self) => Cv(self)?.TargetDisplay ?? 0;
        public static void UnityEngine_Canvas__set_targetDisplay(Canvas self, int value) { var c = Cv(self); if (c != null) c.TargetDisplay = value; }
        public static AdditionalCanvasShaderChannels UnityEngine_Canvas__get_additionalShaderChannels(Canvas self) => Cv(self)?.Channels ?? 0;
        public static void UnityEngine_Canvas__set_additionalShaderChannels(Canvas self, AdditionalCanvasShaderChannels value) { var c = Cv(self); if (c != null) c.Channels = value; }
        public static int UnityEngine_Canvas__get_renderOrder(Canvas self) => 0;
        public static Material UnityEngine_Canvas__GetDefaultCanvasMaterial() => UI.DefaultMaterial();
        public static Material UnityEngine_Canvas__GetETC1SupportedCanvasMaterial() => UI.DefaultMaterial();
        public static Material UnityEngine_Canvas__GetDefaultCanvasTextMaterial() => UI.DefaultMaterial();
        public static void UnityEngine_UISystemProfilerApi__BeginSample(UISystemProfilerApi.SampleType type) { }
        public static void UnityEngine_UISystemProfilerApi__EndSample(UISystemProfilerApi.SampleType type) { }
        public static void UnityEngine_UISystemProfilerApi__AddMarker(string name, UObject obj) { }

        // ------------------------------------------------------------ CanvasGroup

        static NCanvasGroup CG(CanvasGroup g) => Objects.Of(g) as NCanvasGroup;
        public static float UnityEngine_CanvasGroup__get_alpha(CanvasGroup self) => CG(self)?.Alpha ?? 1f;
        public static void UnityEngine_CanvasGroup__set_alpha(CanvasGroup self, float value) { var g = CG(self); if (g != null) g.Alpha = value; }
        public static bool UnityEngine_CanvasGroup__get_interactable(CanvasGroup self) => CG(self)?.Interactable ?? true;
        public static void UnityEngine_CanvasGroup__set_interactable(CanvasGroup self, bool value) { var g = CG(self); if (g != null) g.Interactable = value; }
        public static bool UnityEngine_CanvasGroup__get_blocksRaycasts(CanvasGroup self) => CG(self)?.BlocksRaycasts ?? true;
        public static void UnityEngine_CanvasGroup__set_blocksRaycasts(CanvasGroup self, bool value) { var g = CG(self); if (g != null) g.BlocksRaycasts = value; }
        public static bool UnityEngine_CanvasGroup__get_ignoreParentGroups(CanvasGroup self) => CG(self)?.IgnoreParentGroups ?? false;
        public static void UnityEngine_CanvasGroup__set_ignoreParentGroups(CanvasGroup self, bool value) { var g = CG(self); if (g != null) g.IgnoreParentGroups = value; }

        // ------------------------------------------------------------ CanvasRenderer

        static NCanvasRenderer CR(CanvasRenderer r) => Objects.Of(r) as NCanvasRenderer;

        public static void UnityEngine_CanvasRenderer__INTERNAL_CALL_SetColor(CanvasRenderer self, ref Color color) { var r = CR(self); if (r != null) r.Color = color; }
        public static void UnityEngine_CanvasRenderer__INTERNAL_CALL_GetColor(CanvasRenderer self, out Color value) => value = CR(self)?.Color ?? Color.white;
        public static float UnityEngine_CanvasRenderer__GetAlpha(CanvasRenderer self) => CR(self)?.Color.a ?? 1f;
        public static void UnityEngine_CanvasRenderer__SetAlpha(CanvasRenderer self, float alpha) { var r = CR(self); if (r != null) { var c = r.Color; c.a = alpha; r.Color = c; } }
        public static bool UnityEngine_CanvasRenderer__get_isMask(CanvasRenderer self) => CR(self)?.IsMask ?? false;
        public static void UnityEngine_CanvasRenderer__set_isMask(CanvasRenderer self, bool value) { var r = CR(self); if (r != null) r.IsMask = value; }
        public static void UnityEngine_CanvasRenderer__INTERNAL_CALL_EnableRectClipping(CanvasRenderer self, ref Rect rect) { var r = CR(self); if (r != null) r.Clip = rect; }
        public static void UnityEngine_CanvasRenderer__DisableRectClipping(CanvasRenderer self) { var r = CR(self); if (r != null) r.Clip = null; }
        public static bool UnityEngine_CanvasRenderer__get_hasRectClipping(CanvasRenderer self) => CR(self)?.Clip.HasValue ?? false;
        public static bool UnityEngine_CanvasRenderer__get_hasPopInstruction(CanvasRenderer self) => CR(self)?.HasPopInstruction ?? false;
        public static void UnityEngine_CanvasRenderer__set_hasPopInstruction(CanvasRenderer self, bool value) { var r = CR(self); if (r != null) r.HasPopInstruction = value; }
        public static int UnityEngine_CanvasRenderer__get_materialCount(CanvasRenderer self) => CR(self)?.Materials.Length ?? 0;
        public static void UnityEngine_CanvasRenderer__set_materialCount(CanvasRenderer self, int value) { var r = CR(self); if (r != null) Array.Resize(ref r.Materials, Math.Max(0, value)); }
        public static void UnityEngine_CanvasRenderer__SetMaterial(CanvasRenderer self, Material material, int index)
        {
            var r = CR(self);
            if (r == null || index < 0) return;
            if (index >= r.Materials.Length) Array.Resize(ref r.Materials, index + 1);
            r.Materials[index] = material;
        }
        public static Material UnityEngine_CanvasRenderer__GetMaterial(CanvasRenderer self, int index) { var r = CR(self); return r != null && index >= 0 && index < r.Materials.Length ? r.Materials[index] : null; }
        public static int UnityEngine_CanvasRenderer__get_popMaterialCount(CanvasRenderer self) => CR(self)?.PopMaterials.Length ?? 0;
        public static void UnityEngine_CanvasRenderer__set_popMaterialCount(CanvasRenderer self, int value) { var r = CR(self); if (r != null) Array.Resize(ref r.PopMaterials, Math.Max(0, value)); }
        public static void UnityEngine_CanvasRenderer__SetPopMaterial(CanvasRenderer self, Material material, int index)
        {
            var r = CR(self);
            if (r == null || index < 0) return;
            if (index >= r.PopMaterials.Length) Array.Resize(ref r.PopMaterials, index + 1);
            r.PopMaterials[index] = material;
        }
        public static Material UnityEngine_CanvasRenderer__GetPopMaterial(CanvasRenderer self, int index) { var r = CR(self); return r != null && index >= 0 && index < r.PopMaterials.Length ? r.PopMaterials[index] : null; }
        public static void UnityEngine_CanvasRenderer__SetTexture(CanvasRenderer self, Texture texture) { var r = CR(self); if (r != null) r.Texture = texture; }
        public static void UnityEngine_CanvasRenderer__SetAlphaTexture(CanvasRenderer self, Texture texture) { var r = CR(self); if (r != null) r.AlphaTexture = texture; }
        /// <summary>The canvas renderer takes a copy of the mesh, as Unity's does.</summary>
        public static void UnityEngine_CanvasRenderer__SetMesh(CanvasRenderer self, Mesh mesh)
        {
            var r = CR(self);
            if (r == null) return;
            var src = MeshData.Of(mesh);
            r.Mesh = src == null ? null : src.CopyInto(r.Mesh);
        }
        public static void UnityEngine_CanvasRenderer__Clear(CanvasRenderer self) { var r = CR(self); if (r != null) { r.Mesh = null; r.Materials = new Material[0]; r.Texture = null; } }
        public static bool UnityEngine_CanvasRenderer__get_cull(CanvasRenderer self) => CR(self)?.Cull ?? false;
        public static void UnityEngine_CanvasRenderer__set_cull(CanvasRenderer self, bool value) { var r = CR(self); if (r != null) r.Cull = value; }
        public static int UnityEngine_CanvasRenderer__get_absoluteDepth(CanvasRenderer self) => RenderList.DepthOf(CR(self));
        public static int UnityEngine_CanvasRenderer__get_relativeDepth(CanvasRenderer self) => RenderList.DepthOf(CR(self));
        public static bool UnityEngine_CanvasRenderer__get_hasMoved(CanvasRenderer self) => false;

        // UIVertex stream helpers (VertexHelper): split/merge List<T> streams.
        public static void UnityEngine_CanvasRenderer__SplitUIVertexStreamsInternal(object verts, object positions, object colors, object uv0S, object uv1S, object uv2S, object uv3S, object normals, object tangents)
        {
            var v = (List<UIVertex>)verts;
            var p = (List<Vector3>)positions; var c = (List<Color32>)colors; var u0 = (List<Vector2>)uv0S; var u1 = (List<Vector2>)uv1S;
            var u2 = (List<Vector2>)uv2S; var u3 = (List<Vector2>)uv3S; var n = (List<Vector3>)normals; var t = (List<Vector4>)tangents;
            p.Clear(); c.Clear(); u0.Clear(); u1.Clear(); u2.Clear(); u3.Clear(); n.Clear(); t.Clear();
            foreach (var x in v) { p.Add(x.position); c.Add(x.color); u0.Add(x.uv0); u1.Add(x.uv1); u2.Add(x.uv2); u3.Add(x.uv3); n.Add(x.normal); t.Add(x.tangent); }
        }
        public static void UnityEngine_CanvasRenderer__SplitIndicesStreamsInternal(object verts, object indices)
        {
            var v = (List<UIVertex>)verts; var i = (List<int>)indices;
            i.Clear();
            for (int k = 0; k < v.Count; k++) i.Add(k);
        }
        public static void UnityEngine_CanvasRenderer__CreateUIVertexStreamInternal(object verts, object positions, object colors, object uv0S, object uv1S, object uv2S, object uv3S, object normals, object tangents, object indices)
        {
            var v = (List<UIVertex>)verts;
            var p = (List<Vector3>)positions; var c = (List<Color32>)colors; var u0 = (List<Vector2>)uv0S; var u1 = (List<Vector2>)uv1S;
            var u2 = (List<Vector2>)uv2S; var u3 = (List<Vector2>)uv3S; var n = (List<Vector3>)normals; var t = (List<Vector4>)tangents; var ind = (List<int>)indices;
            v.Clear();
            foreach (int k in ind)
            {
                var x = new UIVertex
                {
                    position = p[k], color = k < c.Count ? c[k] : (Color32)Color.white, uv0 = k < u0.Count ? u0[k] : Vector2.zero, uv1 = k < u1.Count ? u1[k] : Vector2.zero,
                    uv2 = k < u2.Count ? u2[k] : Vector2.zero, uv3 = k < u3.Count ? u3[k] : Vector2.zero,
                    normal = k < n.Count ? n[k] : Vector3.back, tangent = k < t.Count ? t[k] : new Vector4(1, 0, 0, -1),
                };
                v.Add(x);
            }
        }

        // ------------------------------------------------------------ Mesh

        static MeshData MD(Mesh m) => MeshData.Of(m);

        public static void UnityEngine_Mesh__Internal_Create(Mesh mono)
        {
            if (Objects.Of(mono) == null) Objects.Register(mono, new NObject(), "");
            MD(mono);
        }
        public static int UnityEngine_Mesh__get_vertexCount(Mesh self) => MD(self)?.Vertices.Length ?? 0;
        public static int UnityEngine_Mesh__get_subMeshCount(Mesh self) => MD(self)?.SubMeshes.Count ?? 0;
        public static void UnityEngine_Mesh__set_subMeshCount(Mesh self, int value)
        {
            var d = MD(self);
            if (d == null) return;
            while (d.SubMeshes.Count < value) { d.SubMeshes.Add(new int[0]); d.Topologies.Add(MeshTopology.Triangles); }
            while (d.SubMeshes.Count > Math.Max(1, value)) { d.SubMeshes.RemoveAt(d.SubMeshes.Count - 1); d.Topologies.RemoveAt(d.Topologies.Count - 1); }
        }
        public static bool UnityEngine_Mesh__get_isReadable(Mesh self) => true;
        public static bool UnityEngine_Mesh__get_canAccess(Mesh self) => true;
        public static int UnityEngine_Mesh__get_vertexBufferCount(Mesh self) => 1;
        public static UnityEngine.Rendering.IndexFormat UnityEngine_Mesh__get_indexFormat(Mesh self) => UnityEngine.Rendering.IndexFormat.UInt16;
        public static void UnityEngine_Mesh__set_indexFormat(Mesh self, UnityEngine.Rendering.IndexFormat value) { }
        public static int UnityEngine_Mesh__get_blendShapeCount(Mesh self) => 0;
        public static void UnityEngine_Mesh__ClearImpl(Mesh self, bool keepVertexLayout)
        {
            var d = MD(self);
            if (d == null) return;
            d.Vertices = new Vector3[0]; d.Normals = null; d.Tangents = null; d.Colors = null;
            for (int i = 0; i < 4; i++) d.Uv[i] = null;
            d.SubMeshes = new List<int[]> { new int[0] }; d.Topologies = new List<MeshTopology> { MeshTopology.Triangles };
            d.Bounds = default;
        }
        public static void UnityEngine_Mesh__RecalculateBoundsImpl(Mesh self) => MD(self)?.RecalculateBounds();
        public static void UnityEngine_Mesh__RecalculateNormalsImpl(Mesh self) { }
        public static void UnityEngine_Mesh__RecalculateTangentsImpl(Mesh self) { }
        public static void UnityEngine_Mesh__MarkDynamicImpl(Mesh self) { }
        public static void UnityEngine_Mesh__UploadMeshDataImpl(Mesh self, bool markNoLogerReadable) { }
        public static void UnityEngine_Mesh__get_bounds_Injected(Mesh self, out Bounds ret) => ret = MD(self)?.Bounds ?? default;
        public static void UnityEngine_Mesh__set_bounds_Injected(Mesh self, ref Bounds value) { var d = MD(self); if (d != null) d.Bounds = value; }
        public static MeshTopology UnityEngine_Mesh__GetTopologyImpl(Mesh self, int submesh) { var d = MD(self); return d != null && submesh >= 0 && submesh < d.Topologies.Count ? d.Topologies[submesh] : MeshTopology.Triangles; }
        public static bool UnityEngine_Mesh__HasChannel(Mesh self, Mesh.InternalShaderChannel ch)
        {
            var d = MD(self);
            if (d == null) return false;
            switch (ch)
            {
                case Mesh.InternalShaderChannel.Vertex: return d.Vertices.Length > 0;
                case Mesh.InternalShaderChannel.Normal: return d.Normals != null;
                case Mesh.InternalShaderChannel.Tangent: return d.Tangents != null;
                case Mesh.InternalShaderChannel.Color: return d.Colors != null;
                default: return d.Uv[(int)ch - (int)Mesh.InternalShaderChannel.TexCoord0] != null;
            }
        }

        /// <summary>Element i of a vector array as a Vector4, without boxing.</summary>
        static Vector4 At(Array values, int i) => values switch
        {
            Vector3[] a3 => a3[i],
            Vector2[] a2 => new Vector4(a2[i].x, a2[i].y, 0, 0),
            Vector4[] a4 => a4[i],
            float[] f => new Vector4(f[i], 0, 0, 0),
            _ => V4(values.GetValue(i)),
        };

        static Vector4 V4(object o)
        {
            switch (o)
            {
                case Vector2 v2: return new Vector4(v2.x, v2.y, 0, 0);
                case Vector3 v3: return new Vector4(v3.x, v3.y, v3.z, 0);
                case Vector4 v4: return v4;
                case float f: return new Vector4(f, 0, 0, 0);
                default: return default;
            }
        }

        public static void UnityEngine_Mesh__SetArrayForChannelImpl(Mesh self, Mesh.InternalShaderChannel channel, Mesh.InternalVertexChannelType format, int dim, Array values, int arraySize)
        {
            var d = MD(self);
            if (d == null) return;
            int n = values == null ? 0 : Math.Min(arraySize, values.Length);
            switch (channel)
            {
                case Mesh.InternalShaderChannel.Vertex:
                    {
                        var a = d.Vertices != null && d.Vertices.Length == n ? d.Vertices : new Vector3[n];
                        if (values is Vector3[] src3) Array.Copy(src3, a, n);
                        else for (int i = 0; i < n; i++) { var v = At(values, i); a[i] = new Vector3(v.x, v.y, v.z); }
                        d.Vertices = a;
                        break;
                    }
                case Mesh.InternalShaderChannel.Normal:
                    {
                        var a = d.Normals != null && d.Normals.Length == n ? d.Normals : new Vector3[n];
                        if (values is Vector3[] src3) Array.Copy(src3, a, n);
                        else for (int i = 0; i < n; i++) { var v = At(values, i); a[i] = new Vector3(v.x, v.y, v.z); }
                        d.Normals = n > 0 ? a : null;
                        break;
                    }
                case Mesh.InternalShaderChannel.Tangent:
                    {
                        var a = d.Tangents != null && d.Tangents.Length == n ? d.Tangents : new Vector4[n];
                        for (int i = 0; i < n; i++) a[i] = At(values, i);
                        d.Tangents = n > 0 ? a : null;
                        break;
                    }
                case Mesh.InternalShaderChannel.Color:
                    {
                        var a = d.Colors != null && d.Colors.Length == n ? d.Colors : new Color32[n];
                        if (values is Color32[] s32) Array.Copy(s32, a, n);
                        else if (values is Color[] sc) for (int i = 0; i < n; i++) a[i] = sc[i];
                        else for (int i = 0; i < n; i++) a[i] = new Color32(255, 255, 255, 255);
                        d.Colors = n > 0 ? a : null;
                        break;
                    }
                default:
                    {
                        int uv = (int)channel - (int)Mesh.InternalShaderChannel.TexCoord0;
                        var a = d.Uv[uv] != null && d.Uv[uv].Length == n ? d.Uv[uv] : new Vector4[n];
                        for (int i = 0; i < n; i++) a[i] = At(values, i);
                        d.Uv[uv] = n > 0 ? a : null;
                        break;
                    }
            }
        }

        public static Array UnityEngine_Mesh__GetAllocArrayFromChannelImpl(Mesh self, Mesh.InternalShaderChannel channel, Mesh.InternalVertexChannelType format, int dim)
        {
            var d = MD(self);
            int n = d?.Vertices.Length ?? 0;
            Array r = channel == Mesh.InternalShaderChannel.Color ? (Array)new Color32[n]
                : dim == 2 ? new Vector2[n] : dim == 3 ? new Vector3[n] : dim == 4 ? (Array)new Vector4[n] : new float[n];
            UnityEngine_Mesh__GetArrayFromChannelImpl(self, channel, format, dim, r);
            return r;
        }

        public static void UnityEngine_Mesh__GetArrayFromChannelImpl(Mesh self, Mesh.InternalShaderChannel channel, Mesh.InternalVertexChannelType format, int dim, Array values)
        {
            var d = MD(self);
            if (d == null || values == null) return;
            for (int i = 0; i < values.Length; i++)
            {
                Vector4 v;
                switch (channel)
                {
                    case Mesh.InternalShaderChannel.Vertex: v = i < d.Vertices.Length ? (Vector4)d.Vertices[i] : default; break;
                    case Mesh.InternalShaderChannel.Normal: v = d.Normals != null && i < d.Normals.Length ? (Vector4)d.Normals[i] : default; break;
                    case Mesh.InternalShaderChannel.Tangent: v = d.Tangents != null && i < d.Tangents.Length ? d.Tangents[i] : default; break;
                    case Mesh.InternalShaderChannel.Color:
                        {
                            var c = d.Colors != null && i < d.Colors.Length ? d.Colors[i] : new Color32(255, 255, 255, 255);
                            if (values is Color32[] c32) c32[i] = c; else if (values is Color[] cc) cc[i] = c;
                            continue;
                        }
                    default:
                        {
                            var uv = d.Uv[(int)channel - (int)Mesh.InternalShaderChannel.TexCoord0];
                            v = uv != null && i < uv.Length ? uv[i] : default;
                            break;
                        }
                }
                if (values is Vector2[] a2) a2[i] = v; else if (values is Vector3[] a3) a3[i] = v; else if (values is Vector4[] a4) a4[i] = v;
            }
        }

        public static void UnityEngine_Mesh__SetIndicesImpl(Mesh self, int submesh, MeshTopology topology, Array indices, int arraySize, bool calculateBounds, int baseVertex)
        {
            var d = MD(self);
            if (d == null) return;
            // submesh -1 (Mesh.triangles): the whole mesh becomes one submesh.
            if (submesh < 0) { UnityEngine_Mesh__set_subMeshCount(self, 1); submesh = 0; }
            UnityEngine_Mesh__set_subMeshCount(self, Math.Max(d.SubMeshes.Count, submesh + 1));
            int n = indices == null ? 0 : Math.Min(arraySize, indices.Length);
            var old = submesh < d.SubMeshes.Count ? d.SubMeshes[submesh] : null;
            var a = old != null && old.Length == n ? old : new int[n];
            if (indices is int[] ii) for (int i = 0; i < n; i++) a[i] = ii[i] + baseVertex;
            else if (indices is ushort[] us) for (int i = 0; i < n; i++) a[i] = us[i] + baseVertex;
            else for (int i = 0; i < n; i++) a[i] = Convert.ToInt32(indices.GetValue(i)) + baseVertex;
            d.SubMeshes[submesh] = a;
            d.Topologies[submesh] = topology;
            if (calculateBounds) d.RecalculateBounds();
        }
        static int[] Indices(Mesh self, int submesh)
        {
            var d = MD(self);
            if (d != null && submesh < 0)
            {
                var all = new List<int>();
                foreach (var sm in d.SubMeshes) all.AddRange(sm);
                return all.ToArray();
            }
            return d != null && submesh >= 0 && submesh < d.SubMeshes.Count ? d.SubMeshes[submesh] : new int[0];
        }
        public static int[] UnityEngine_Mesh__GetTrianglesImpl(Mesh self, int submesh, bool applyBaseVertex) => (int[])Indices(self, submesh).Clone();
        public static int[] UnityEngine_Mesh__GetIndicesImpl(Mesh self, int submesh, bool applyBaseVertex) => (int[])Indices(self, submesh).Clone();
        public static void UnityEngine_Mesh__GetTrianglesNonAllocImpl(Mesh self, Array values, int submesh, bool applyBaseVertex)
        {
            var s = Indices(self, submesh);
            for (int i = 0; i < s.Length && i < values.Length; i++) values.SetValue(s[i], i);
        }
        public static void UnityEngine_Mesh__GetIndicesNonAllocImpl(Mesh self, Array values, int submesh, bool applyBaseVertex) => UnityEngine_Mesh__GetTrianglesNonAllocImpl(self, values, submesh, applyBaseVertex);
        public static uint UnityEngine_Mesh__GetIndexStartImpl(Mesh self, int submesh) => 0;
        public static uint UnityEngine_Mesh__GetIndexCountImpl(Mesh self, int submesh) => (uint)Indices(self, submesh).Length;
        public static uint UnityEngine_Mesh__GetBaseVertexImpl(Mesh self, int submesh) => 0;
    }
}
