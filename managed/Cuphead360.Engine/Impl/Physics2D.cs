using System;
using System.Collections.Generic;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    // 2D physics as Cuphead uses it: queries (ray/box/circle/capsule casts,
    // overlaps) against colliders, and the fixed-step contact pass that sends
    // OnTrigger*2D / OnCollision*2D. Dynamic bodies integrate gravity and
    // velocity; there is no collision response (Cuphead moves its actors with
    // its own motors and queries).

    public enum ColliderKind { Box, Circle, Polygon, Edge, Capsule }

    /// <summary>
    /// The physics queries' index: colliders by the left edge of their world box, so a cast or overlap
    /// looks only at those whose x extent meets its own (a cast walked every collider of the level: the
    /// run-and-gun level's ground enemies cast several times each fixed step, two steps a frame at 30 fps,
    /// most of its 23 ms fixed phase on the console). Colliders far wider than most (floors spanning the
    /// level) sit in a list every query looks at. A collider whose transform or shape changed is dirty and
    /// takes its place again before the next query, so queries see the boxes as they are.
    /// </summary>
    public static class QueryIndex
    {
        static readonly List<NCollider2D> sorted = new List<NCollider2D>(), wide = new List<NCollider2D>(), dirty = new List<NCollider2D>();
        static readonly List<NCollider2D> scratch = new List<NCollider2D>();
        static float maxNarrow, wideOver = float.PositiveInfinity;
        static bool unsorted, holes;
        static int seq, loadsSeen = -1;

        public static void Added(NCollider2D c) { c.Seq = ++seq; Dirty(c); }

        public static void Dirty(NCollider2D c)
        {
            if (c.IndexDirty) return;
            c.IndexDirty = true;
            dirty.Add(c);
        }

        public static void Moved(NGameObject go)
        {
            var cs = go.Components;
            for (int i = 0; i < cs.Count; i++) if (cs[i] is NCollider2D c) Dirty(c);
        }

        static void Rebuild()
        {
            loadsSeen = SceneLoader.Loads;
            foreach (var c in sorted) c.InIndex = false;
            foreach (var c in wide) c.InIndex = false;
            sorted.Clear(); wide.Clear();
            // A collider wider than four times the 90th percentile of widths is "wide".
            var widths = new List<float>();
            foreach (var c in Typed.Colliders)
            {
                if (c.Destroyed || c.GameObject == null || c.GameObject.IsAsset || c.GameObject.Transform == null) continue;
                c.Shapes();
                widths.Add(c.AABB.width);
            }
            widths.Sort();
            wideOver = widths.Count >= 8 ? Math.Max(widths[(int)(widths.Count * 0.9f)] * 4f, 1e-3f) : float.PositiveInfinity;
            maxNarrow = 0f;
            foreach (var c in Typed.Colliders) Dirty(c);
        }

        static void Update()
        {
            if (loadsSeen != SceneLoader.Loads) Rebuild();
            if (dirty.Count > 0)
            {
                int keep = 0;
                for (int i = 0; i < dirty.Count; i++)
                {
                    var c = dirty[i];
                    if (c.Destroyed) { c.IndexDirty = false; if (c.InIndex) { c.InIndex = false; holes = true; } continue; }
                    var go = c.GameObject;
                    // Not attached yet: placed once it is.
                    if (go == null || go.Transform == null) { dirty[keep++] = c; continue; }
                    c.IndexDirty = false;
                    if (go.IsAsset) continue;   // a prefab asset's (never Live, so never in a query's results)
                    go.HasColliders = true;
                    c.Shapes();
                    float w = c.AABB.width;
                    bool isWide = w > wideOver;
                    if (c.InIndex && c.IndexWide != isWide) { c.InIndex = false; holes = true; }
                    c.IndexMinX = c.AABB.xMin;
                    if (!c.InIndex)
                    {
                        c.InIndex = true;
                        c.IndexWide = isWide;
                        (isWide ? wide : sorted).Add(c);
                    }
                    if (!isWide) { if (w > maxNarrow) maxNarrow = w; unsorted = true; }
                }
                dirty.RemoveRange(keep, dirty.Count - keep);
            }
            if (holes)
            {
                // Entries that left (destroyed, or moved between the lists) go; a list has each collider once.
                sorted.RemoveAll(c => !c.InIndex || c.IndexWide);
                wide.RemoveAll(c => !c.InIndex || !c.IndexWide);
                holes = false;
            }
            if (unsorted)
            {
                // Insertion sort: between queries few colliders move far.
                for (int i = 1; i < sorted.Count; i++)
                {
                    var c = sorted[i];
                    float k = c.IndexMinX;
                    int j = i - 1;
                    while (j >= 0 && sorted[j].IndexMinX > k) { sorted[j + 1] = sorted[j]; j--; }
                    sorted[j + 1] = c;
                }
                unsorted = false;
            }
        }

        /// <summary>The colliders whose world box may meet [xMin, xMax] in x, in creation order (a list reused by the next call).</summary>
        public static List<NCollider2D> Candidates(float xMin, float xMax)
        {
            Update();
            scratch.Clear();
            float lo = xMin - maxNarrow;
            // The first entry with IndexMinX >= lo.
            int a = 0, b = sorted.Count;
            while (a < b) { int m = (a + b) >> 1; if (sorted[m].IndexMinX < lo) a = m + 1; else b = m; }
            for (int i = a; i < sorted.Count; i++)
            {
                var c = sorted[i];
                if (c.IndexMinX > xMax) break;
                scratch.Add(c);
            }
            scratch.AddRange(wide);
            if (scratch.Count > 1) scratch.Sort((x, y) => x.Seq.CompareTo(y.Seq));
            return scratch;
        }
    }

    public sealed class NCollider2D : NComponent
    {
        public ColliderKind Kind;
        public Vector2 Offset, Size = Vector2.one;
        public float Radius = 0.5f, EdgeRadius, Density = 1f;
        public bool IsTrigger, UsedByEffector;
        public CapsuleDirection2D Direction;
        public List<Vector2[]> Paths = new List<Vector2[]>();
        public Vector2[] Points = new Vector2[0];
        public PhysicsMaterial2D Material;
        // Cached world shape.
        Matrix4x4 cachedM; bool cached; int cachedVersion = -1;
        int version;
        /// <summary>Changes with the collider's shape (size, offset, points...): it moves in the queries' index.</summary>
        public int Version { get => version; set { version = value; QueryIndex.Dirty(this); } }
        // The physics queries' index (QueryIndex).
        public int Seq;
        public bool IndexDirty, InIndex, IndexWide;
        public float IndexMinX;
        public readonly List<Shape> World = new List<Shape>();
        public Rect AABB;

        public static ColliderKind KindOf(Type t) =>
            typeof(CircleCollider2D).IsAssignableFrom(t) ? ColliderKind.Circle : typeof(PolygonCollider2D).IsAssignableFrom(t) ? ColliderKind.Polygon :
            typeof(EdgeCollider2D).IsAssignableFrom(t) ? ColliderKind.Edge : typeof(CapsuleCollider2D).IsAssignableFrom(t) ? ColliderKind.Capsule : ColliderKind.Box;

        public override void Load(ChobObject co, SceneBuild b)
        {
            var t = co.Tree;
            Offset = Tree.Vec2(t, "m_Offset");
            IsTrigger = Tree.Bool(t, "m_IsTrigger");
            UsedByEffector = Tree.Bool(t, "m_UsedByEffector");
            Density = Tree.Float(t, "m_Density", 1f);
            Material = b.Resolve(Tree.Ptr(t, "m_Material")) as PhysicsMaterial2D;
            switch (Kind)
            {
                case ColliderKind.Box: Size = Tree.Vec2(t, "m_Size"); EdgeRadius = Tree.Float(t, "m_EdgeRadius"); break;
                case ColliderKind.Circle: Radius = Tree.Float(t, "m_Radius", 0.5f); break;
                case ColliderKind.Capsule: Size = Tree.Vec2(t, "m_Size"); Direction = (CapsuleDirection2D)Tree.Int(t, "m_Direction"); break;
                case ColliderKind.Edge:
                    {
                        EdgeRadius = Tree.Float(t, "m_EdgeRadius");
                        var l = Tree.List(t, "m_Points");
                        Points = l.ConvertAll(p => new Vector2(Tree.Float(p, "x"), Tree.Float(p, "y"))).ToArray();
                        break;
                    }
                case ColliderKind.Polygon:
                    {
                        Paths.Clear();
                        foreach (var path in Tree.List(Tree.Get(t, "m_Points"), "m_Paths"))
                            if (path is List<object> pl) Paths.Add(pl.ConvertAll(p => new Vector2(Tree.Float(p, "x"), Tree.Float(p, "y"))).ToArray());
                        break;
                    }
            }
            Version++;
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NCollider2D)src;
            Kind = s.Kind; Offset = s.Offset; Size = s.Size; Radius = s.Radius; EdgeRadius = s.EdgeRadius; Density = s.Density;
            IsTrigger = s.IsTrigger; UsedByEffector = s.UsedByEffector; Direction = s.Direction; Material = s.Material;
            Paths = s.Paths.ConvertAll(p => (Vector2[])p.Clone()); Points = (Vector2[])s.Points.Clone();
            Version++;
        }

        public override void Activated(bool on) { if (!on) Physics.ColliderGone(this); }
        public override void Removed() => Physics.ColliderGone(this);

        public bool Live => !Destroyed && Enabled && GameObject != null && GameObject.ActiveInHierarchy && (Rigidbody == null || Rigidbody.Simulated);

        // The body found last, kept while no parent changed and no rigidbody came or went (casts and
        // Live asked for it per collider per cast: a walk up the hierarchy through every component).
        NRigidbody2D body;
        int bodyVersion = -1;

        public NRigidbody2D Rigidbody
        {
            get
            {
                if (bodyVersion == NTransform.HierarchyVersion && (body == null || !body.Destroyed)) return body;
                NRigidbody2D found = null;
                for (var t = GameObject.Transform; t != null && found == null; t = t.Parent)
                    foreach (var c in t.GameObject.Components)
                        if (c is NRigidbody2D rb && !rb.Destroyed) { found = rb; break; }
                body = found;
                bodyVersion = NTransform.HierarchyVersion;
                return found;
            }
        }

        /// <summary>World-space convex pieces, recomputed when the transform or shape changes.</summary>
        public List<Shape> Shapes()
        {
            // The transform not invalidated since: no matrix to fetch and compare (each cast asked
            // every collider: 4% of the first run-and-gun level's PC samples).
            var t = GameObject.Transform;
            if (cached && cachedVersion == Version && cachedT == t && cachedTV == t.WorldVersion) return World;
            var m = t.LocalToWorld;
            cachedT = t; cachedTV = t.WorldVersion;
            if (cached && cachedVersion == Version && m == cachedM) return World;
            cached = true; cachedVersion = Version; cachedM = m;
            Recompute(m);
            return World;
        }
        NTransform cachedT;
        int cachedTV;

        // Apart from Shapes: its local function P is made a delegate for polygons, so the
        // compiler puts the matrix it captures in a heap object at the method's start: every
        // call allocated one, cached or not (the largest allocation site in a run-and-gun level).
        void Recompute(Matrix4x4 m)
        {
            Vector2 P(Vector2 local) { var w = m.MultiplyPoint3x4(new Vector3(local.x + Offset.x, local.y + Offset.y, 0)); return new Vector2(w.x, w.y); }
            var ls = GameObject.Transform.LossyScale;
            float rs = Math.Max(Math.Abs(ls.x), Math.Abs(ls.y));
            // A box, circle or capsule keeps its shape and arrays as it moves (the same values as new
            // ones): moving colliders' new shapes were most of a run-and-gun level's allocations, and
            // its garbage collections nearly one a frame on the console. Nothing keeps World's shapes.
            Shape Reuse(int pts)
            {
                if (World.Count == 1 && World[0].Pts.Length == pts) return World[0];
                World.Clear();
                var sh = new Shape { Pts = new Vector2[pts] };
                World.Add(sh);
                return sh;
            }
            switch (Kind)
            {
                case ColliderKind.Box:
                    {
                        float hx = Size.x / 2, hy = Size.y / 2;
                        var sh = Reuse(4);
                        sh.Pts[0] = P(new Vector2(-hx, -hy)); sh.Pts[1] = P(new Vector2(hx, -hy)); sh.Pts[2] = P(new Vector2(hx, hy)); sh.Pts[3] = P(new Vector2(-hx, hy));
                        Geometry.CcwInPlace(sh.Pts);
                        sh.R = EdgeRadius * rs;
                        break;
                    }
                case ColliderKind.Circle:
                    {
                        var sh = Reuse(1);
                        sh.Pts[0] = P(Vector2.zero);
                        sh.R = Radius * rs;
                        break;
                    }
                case ColliderKind.Capsule:
                    {
                        bool vertical = Direction == CapsuleDirection2D.Vertical;
                        float r = (vertical ? Size.x : Size.y) / 2, half = Math.Max(0, (vertical ? Size.y : Size.x) / 2 - r);
                        var a = vertical ? new Vector2(0, -half) : new Vector2(-half, 0);
                        var b = vertical ? new Vector2(0, half) : new Vector2(half, 0);
                        var sh = Reuse(2);
                        sh.Pts[0] = P(a); sh.Pts[1] = P(b);
                        sh.R = r * rs;
                        break;
                    }
                case ColliderKind.Edge:
                    World.Clear();
                    for (int i = 0; i + 1 < Points.Length; i++) World.Add(Shape.Poly(new[] { P(Points[i]), P(Points[i + 1]) }, EdgeRadius * rs));
                    break;
                case ColliderKind.Polygon:
                    World.Clear();
                    foreach (var path in Paths)
                    {
                        var world = Array.ConvertAll(path, P);
                        foreach (var piece in Geometry.ConvexPieces(world)) World.Add(Shape.Poly(piece, 0));
                    }
                    break;
            }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var s in World) { var r = s.Bounds(); x0 = Math.Min(x0, r.xMin); y0 = Math.Min(y0, r.yMin); x1 = Math.Max(x1, r.xMax); y1 = Math.Max(y1, r.yMax); }
            AABB = World.Count == 0 ? new Rect(P(Vector2.zero), Vector2.zero) : Rect.MinMaxRect(x0, y0, x1, y1);
        }
    }

    public sealed class NRigidbody2D : NComponent
    {
        public RigidbodyType2D BodyType;
        public bool Simulated = true, UseFullKinematicContacts, UseAutoMass;
        public Vector2 Velocity;
        public float AngularVelocity, GravityScale = 1f, Mass = 1f, Drag, AngularDrag = 0.05f, Inertia;
        public RigidbodyConstraints2D Constraints;
        public RigidbodyInterpolation2D Interpolation;
        public RigidbodySleepMode2D SleepMode = RigidbodySleepMode2D.StartAwake;
        public CollisionDetectionMode2D Detection;
        public PhysicsMaterial2D Material;
        public Vector2? MoveTo;
        public float? RotateTo;

        public override void Load(ChobObject co, SceneBuild b)
        {
            var t = co.Tree;
            BodyType = (RigidbodyType2D)Tree.Int(t, "m_BodyType");
            Simulated = Tree.Bool(t, "m_Simulated", true);
            UseFullKinematicContacts = Tree.Bool(t, "m_UseFullKinematicContacts");
            UseAutoMass = Tree.Bool(t, "m_UseAutoMass");
            Mass = Tree.Float(t, "m_Mass", 1f);
            Drag = Tree.Float(t, "m_LinearDrag");
            AngularDrag = Tree.Float(t, "m_AngularDrag", 0.05f);
            GravityScale = Tree.Float(t, "m_GravityScale", 1f);
            Constraints = (RigidbodyConstraints2D)Tree.Int(t, "m_Constraints");
            Interpolation = (RigidbodyInterpolation2D)Tree.Int(t, "m_Interpolate");
            SleepMode = (RigidbodySleepMode2D)Tree.Int(t, "m_SleepingMode", 1);
            Detection = (CollisionDetectionMode2D)Tree.Int(t, "m_CollisionDetection");
            Material = b.Resolve(Tree.Ptr(t, "m_Material")) as PhysicsMaterial2D;
        }

        public override void CopyFrom(NComponent src, Dictionary<NObject, NObject> map)
        {
            var s = (NRigidbody2D)src;
            BodyType = s.BodyType; Simulated = s.Simulated; UseFullKinematicContacts = s.UseFullKinematicContacts; UseAutoMass = s.UseAutoMass;
            Velocity = s.Velocity; AngularVelocity = s.AngularVelocity; GravityScale = s.GravityScale; Mass = s.Mass; Drag = s.Drag; AngularDrag = s.AngularDrag;
            Constraints = s.Constraints; Interpolation = s.Interpolation; SleepMode = s.SleepMode; Detection = s.Detection; Material = s.Material;
        }

        public override void Added() => Physics.Track(this);
        public override void Activated(bool on) { Physics.Track(this); }
    }

    /// <summary>A convex polygon (2+ points) or circle, optionally rounded by a radius.</summary>
    public sealed class Shape
    {
        public Vector2[] Pts;      // convex, counter-clockwise (or a segment / point)
        public float R;            // rounding radius (circle: Pts = { center })

        public static Shape Poly(Vector2[] p, float r) => new Shape { Pts = Geometry.Ccw(p), R = r };
        public static Shape Circle(Vector2 c, float r) => new Shape { Pts = new[] { c }, R = r };

        public Rect Bounds()
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var p in Pts) { x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y); x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y); }
            return Rect.MinMaxRect(x0 - R, y0 - R, x1 + R, y1 + R);
        }
    }

    public static class Geometry
    {
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Ccw's order, the points reversed in place when clockwise.</summary>
        public static void CcwInPlace(Vector2[] p)
        {
            if (p.Length < 3) return;
            float area = 0;
            for (int i = 0; i < p.Length; i++) area += Cross(p[i], p[(i + 1) % p.Length]);
            if (area < 0) Array.Reverse(p);
        }

        public static Vector2[] Ccw(Vector2[] p)
        {
            if (p.Length < 3) return p;
            float area = 0;
            for (int i = 0; i < p.Length; i++) area += Cross(p[i], p[(i + 1) % p.Length]);
            if (area >= 0) return p;
            var r = (Vector2[])p.Clone();
            Array.Reverse(r);
            return r;
        }

        /// <summary>A simple polygon as convex pieces (ear-clipped triangles).</summary>
        public static List<Vector2[]> ConvexPieces(Vector2[] poly)
        {
            var result = new List<Vector2[]>();
            var p = new List<Vector2>(Ccw(poly));
            if (p.Count < 3) { if (p.Count > 0) result.Add(p.ToArray()); return result; }
            if (IsConvex(p)) { result.Add(p.ToArray()); return result; }
            int guard = 0;
            while (p.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int i = 0; i < p.Count; i++)
                {
                    var a = p[(i + p.Count - 1) % p.Count]; var b = p[i]; var c = p[(i + 1) % p.Count];
                    if (Cross(b - a, c - b) <= 0) continue;
                    bool inside = false;
                    for (int k = 0; k < p.Count && !inside; k++)
                    {
                        var q = p[k];
                        if (q == a || q == b || q == c) continue;
                        inside = Cross(b - a, q - a) > 0 && Cross(c - b, q - b) > 0 && Cross(a - c, q - c) > 0;
                    }
                    if (inside) continue;
                    result.Add(new[] { a, b, c });
                    p.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            result.Add(p.ToArray());
            return result;
        }

        static bool IsConvex(List<Vector2> p)
        {
            for (int i = 0; i < p.Count; i++)
                if (Cross(p[(i + 1) % p.Count] - p[i], p[(i + 2) % p.Count] - p[(i + 1) % p.Count]) < -1e-6f) return false;
            return true;
        }

        static readonly Vector2[] circle16 = BuildCircle(16);
        static Vector2[] BuildCircle(int n)
        {
            var r = new Vector2[n];
            for (int i = 0; i < n; i++) { double a = 2 * Math.PI * i / n; r[i] = new Vector2((float)Math.Cos(a), (float)Math.Sin(a)); }
            return r;
        }

        /// <summary>Points of a shape with its rounding turned into a polygon (for Minkowski sums), into l.</summary>
        static List<Vector2> Expanded(Shape s, List<Vector2> l)
        {
            l.Clear();
            if (s.R <= 0) { l.AddRange(s.Pts); return l; }
            foreach (var p in s.Pts) foreach (var d in circle16) l.Add(p + d * s.R);
            return l;
        }

        // Scratch for Minkowski and Hull (physics runs on the one game thread, never nested).
        static readonly List<Vector2> scratchA = new List<Vector2>(), scratchB = new List<Vector2>(), scratchSum = new List<Vector2>();
        static Vector2[] scratchHull = new Vector2[64];
        static readonly Vector2[][] negByCount = new Vector2[64][];
        static readonly Shape negShape = new Shape();
        static readonly Comparison<Vector2> byXY = (a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y);

        /// <summary>
        /// Whether two shapes' boxes are apart by more than tol: then no
        /// Minkowski test can find them touching (a rounding radius is inside
        /// its box), so the hulls needn't be built.
        /// </summary>
        static bool Apart(Shape a, Shape b, float tol)
        {
            var ra = a.Bounds(); var rb = b.Bounds();
            return ra.xMax + tol < rb.xMin || rb.xMax + tol < ra.xMin || ra.yMax + tol < rb.yMin || rb.yMax + tol < ra.yMin;
        }

        /// <summary>-b, in a shared shape (valid until the next call).</summary>
        static Shape Negated(Shape b)
        {
            int n = b.Pts.Length;
            var pts = n < negByCount.Length ? (negByCount[n] ??= new Vector2[n]) : new Vector2[n];
            for (int i = 0; i < n; i++) pts[i] = -b.Pts[i];
            negShape.Pts = pts;
            negShape.R = b.R;
            return negShape;
        }

        public static Vector2[] Hull(List<Vector2> pts)
        {
            pts.Sort(byXY);
            if (pts.Count < 3) return pts.ToArray();
            if (scratchHull.Length < pts.Count * 2) scratchHull = new Vector2[pts.Count * 4];
            var h = scratchHull;
            int k = 0;
            for (int i = 0; i < pts.Count; i++) { while (k >= 2 && Cross(h[k - 1] - h[k - 2], pts[i] - h[k - 2]) <= 0) k--; h[k++] = pts[i]; }
            for (int i = pts.Count - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(h[k - 1] - h[k - 2], pts[i] - h[k - 2]) <= 0) k--; h[k++] = pts[i]; }
            // The callers read the hull at once and keep nothing: one array per size, reused (a new
            // one per test was most of the physics' allocations in the run-and-gun levels).
            int len = Math.Max(1, k - 1);
            var r = len < hullByCount.Length ? (hullByCount[len] ??= new Vector2[len]) : new Vector2[len];
            Array.Copy(h, r, len);
            return r;
        }
        static readonly Vector2[][] hullByCount = new Vector2[64][];

        /// <summary>An axis-aligned rectangle's bounds (four distinct corners, no rounding): most boxes and box casts.</summary>
        static bool AxisRect(Shape s, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = y0 = x1 = y1 = 0;
            var p = s.Pts;
            if (s.R > 0 || p.Length != 4) return false;
            x0 = Math.Min(Math.Min(p[0].x, p[1].x), Math.Min(p[2].x, p[3].x)); x1 = Math.Max(Math.Max(p[0].x, p[1].x), Math.Max(p[2].x, p[3].x));
            y0 = Math.Min(Math.Min(p[0].y, p[1].y), Math.Min(p[2].y, p[3].y)); y1 = Math.Max(Math.Max(p[0].y, p[1].y), Math.Max(p[2].y, p[3].y));
            if (!(x1 > x0 && y1 > y0)) return false;
            int corners = 0;
            for (int i = 0; i < 4; i++)
            {
                bool lx = p[i].x == x0, hx = p[i].x == x1, ly = p[i].y == y0, hy = p[i].y == y1;
                if (!(lx || hx) || !(ly || hy)) return false;
                corners |= 1 << ((hx ? 1 : 0) | (hy ? 2 : 0));
            }
            return corners == 15;
        }
        static readonly Vector2[] rectSum = new Vector2[4];

        /// <summary>Minkowski sum of a collider shape and the (origin-centred) cast shape.</summary>
        public static Vector2[] Minkowski(Shape s, Shape cast)
        {
            // Two axis-aligned rectangles: their sum is the rectangle of summed corners, in the order the
            // hull below gives it (from the lowest x and y, counter-clockwise), so results are the same.
            if (AxisRect(s, out float ax0, out float ay0, out float ax1, out float ay1) && AxisRect(cast, out float bx0, out float by0, out float bx1, out float by1))
            {
                rectSum[0] = new Vector2(ax0 + bx0, ay0 + by0); rectSum[1] = new Vector2(ax1 + bx1, ay0 + by0);
                rectSum[2] = new Vector2(ax1 + bx1, ay1 + by1); rectSum[3] = new Vector2(ax0 + bx0, ay1 + by1);
                return rectSum;
            }
            var a = Expanded(s, scratchA); var b = Expanded(cast, scratchB);
            var sum = scratchSum;
            sum.Clear();
            foreach (var p in a) foreach (var q in b) sum.Add(p + q);
            return Hull(sum);
        }

        /// <summary>Ray against a convex polygon: entry fraction and normal, or false. Inside = true when the origin is inside.</summary>
        public static bool RayPoly(Vector2 o, Vector2 d, float maxT, Vector2[] poly, out float t, out Vector2 n, out bool inside)
        {
            t = 0; n = Vector2.zero; inside = false;
            if (poly.Length == 0) return false;
            if (poly.Length < 3)
            {
                // Degenerate (point or segment): treat as a thin segment.
                var a = poly[0]; var b = poly.Length > 1 ? poly[1] : poly[0];
                return RaySegment(o, d, maxT, a, b, out t, out n);
            }
            float tEnter = 0, tExit = maxT;
            Vector2 enterN = Vector2.zero;
            bool startInside = true;
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Length];
                var e = b - a;
                var nrm = new Vector2(e.y, -e.x);   // outward for CCW
                float len = nrm.magnitude;
                if (len < 1e-9f) continue;
                nrm /= len;
                float num = Vector2.Dot(a - o, nrm), den = Vector2.Dot(d, nrm);
                if (num < 0) startInside = false;
                if (Math.Abs(den) < 1e-12f) { if (num < 0) return false; continue; }
                float tt = num / den;
                if (den < 0) { if (tt > tEnter) { tEnter = tt; enterN = nrm; } }
                else if (tt < tExit) tExit = tt;
                if (tEnter > tExit) return false;
            }
            if (startInside) { inside = true; t = 0; n = -d; return true; }
            if (tEnter > maxT) return false;
            t = tEnter; n = enterN;
            return true;
        }

        static bool RaySegment(Vector2 o, Vector2 d, float maxT, Vector2 a, Vector2 b, out float t, out Vector2 n)
        {
            t = 0; n = Vector2.zero;
            var e = b - a;
            float den = Cross(d, e);
            if (Math.Abs(den) < 1e-12f) return false;
            float tt = Cross(a - o, e) / den, u = Cross(a - o, d) / den;
            if (tt < 0 || tt > maxT || u < 0 || u > 1) return false;
            t = tt;
            n = new Vector2(e.y, -e.x).normalized;
            if (Vector2.Dot(n, d) > 0) n = -n;
            return true;
        }

        public static bool RayCircle(Vector2 o, Vector2 d, float maxT, Vector2 c, float r, out float t, out Vector2 n, out bool inside)
        {
            t = 0; n = Vector2.zero; inside = false;
            var m = o - c;
            float cc = Vector2.Dot(m, m) - r * r;
            if (cc <= 0) { inside = true; n = -d; return true; }
            float b = Vector2.Dot(m, d), a = Vector2.Dot(d, d);
            float disc = b * b - a * cc;
            if (disc < 0 || b > 0) return false;
            t = (-b - (float)Math.Sqrt(disc)) / a;
            if (t > maxT) return false;
            n = ((o + d * t) - c).normalized;
            return true;
        }

        /// <summary>Distance between a shape and a point (negative inside).</summary>
        public static float Distance(Shape s, Vector2 p)
        {
            if (s.Pts.Length == 1) return (p - s.Pts[0]).magnitude - s.R;
            if (s.Pts.Length == 2) return SegDist(p, s.Pts[0], s.Pts[1]) - s.R;
            // Inside when p is on the same side of every edge, either winding: a mirrored collider's
            // points run clockwise (the counter-clockwise test let Forest Follies' lobbed bombs fall
            // through flipped ground once their centres were past its edge).
            bool left = true, right = true; float best = float.MaxValue;
            for (int i = 0; i < s.Pts.Length; i++)
            {
                var a = s.Pts[i]; var b = s.Pts[(i + 1) % s.Pts.Length];
                float c = Cross(b - a, p - a);
                if (c < 0) left = false;
                if (c > 0) right = false;
                best = Math.Min(best, SegDist(p, a, b));
            }
            return (left || right ? -best : best) - s.R;
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var e = b - a;
            float l2 = Vector2.Dot(e, e);
            float t = l2 > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, e) / l2) : 0;
            return (p - (a + e * t)).magnitude;
        }

        /// <summary>
        /// Penetration of a into b: the translation that moves a out (zero if
        /// they don't overlap). From the Minkowski difference a - b: the
        /// nearest boundary point to the origin.
        /// </summary>
        public static Vector2 Separation(Shape a, Shape b)
        {
            if (Apart(a, b, 1e-3f)) return Vector2.zero;
            var md = Minkowski(a, Negated(b));
            if (md.Length < 3) return Vector2.zero;
            float best = float.MaxValue; Vector2 bestN = Vector2.zero;
            for (int i = 0; i < md.Length; i++)
            {
                var p0 = md[i]; var p1 = md[(i + 1) % md.Length];
                var e = p1 - p0;
                var n = new Vector2(e.y, -e.x);
                float len = n.magnitude;
                if (len < 1e-9f) continue;
                n /= len;
                float d = Vector2.Dot(p0, n);   // distance from the origin to this edge's line (positive: origin inside)
                if (d < 0) return Vector2.zero;   // origin outside this edge: no overlap
                if (d < best) { best = d; bestN = n; }
            }
            return best == float.MaxValue ? Vector2.zero : -bestN * best;
        }

        /// <summary>Do two shapes overlap (touching counts)?</summary>
        public static bool Overlap(Shape a, Shape b)
        {
            if (Apart(a, b, 1e-3f)) return false;
            // A circle meets a shape when its centre is within its radius of it: exact, where the hull
            // below took the circle as an inscribed 16-gon and sorted the sum's points (Overlap's
            // hulls were 2.6% of a Forest Follies frame on host_native: its projectiles and enemies).
            if (a.Pts.Length == 1) return Distance(b, a.Pts[0]) <= a.R + 1e-5f;
            if (b.Pts.Length == 1) return Distance(a, b.Pts[0]) <= b.R + 1e-5f;
            // Point-in-Minkowski-difference: origin inside (a - b).
            var md = Minkowski(a, Negated(b));
            if (md.Length == 1) return md[0].sqrMagnitude <= 1e-10f;
            if (md.Length == 2) return SegDist(Vector2.zero, md[0], md[1]) <= 1e-5f;
            for (int i = 0; i < md.Length; i++)
                if (Cross(md[(i + 1) % md.Length] - md[i], -md[i]) < -1e-6f) return false;
            return true;
        }
    }

    /// <summary>The 2D world: settings, queries, the contact pass.</summary>
    public static class Physics
    {
        public static Vector2 Gravity = new Vector2(0, -9.81f);
        static int[] sweepOrder = new int[0];
        static float[] sweepMin = new float[0];
        static readonly List<long> sweepPairs = new List<long>();
        public static bool QueriesHitTriggers = true, QueriesStartInColliders = true, AutoSimulation = true, CallbacksOnDisable = true, AutoSyncTransforms = true, ChangeStopsCallbacks;
        public static uint[] LayerMatrix = new uint[32];
        static readonly HashSet<(NCollider2D, NCollider2D)> ignored = new HashSet<(NCollider2D, NCollider2D)>();
        static readonly List<NRigidbody2D> bodies = new List<NRigidbody2D>();
        static readonly HashSet<NRigidbody2D> bodySet = new HashSet<NRigidbody2D>();

        public static void LoadSettings(object t)
        {
            Gravity = Tree.Vec2(t, "m_Gravity");
            QueriesHitTriggers = Tree.Bool(t, "m_QueriesHitTriggers", true);
            QueriesStartInColliders = Tree.Bool(t, "m_QueriesStartInColliders", true);
            AutoSimulation = Tree.Bool(t, "m_AutoSimulation", true);
            CallbacksOnDisable = Tree.Bool(t, "m_CallbacksOnDisable", true);
            ChangeStopsCallbacks = Tree.Bool(t, "m_ChangeStopsCallbacks");
            var m = Tree.List(t, "m_LayerCollisionMatrix");
            for (int i = 0; i < 32; i++) LayerMatrix[i] = i < m.Count && m[i] is long v ? (uint)v : 0xFFFFFFFFu;
        }

        public static void Track(NRigidbody2D b) { if (bodySet.Add(b)) bodies.Add(b); }

        public static bool LayersCollide(int a, int b) => (LayerMatrix[a & 31] & (1u << (b & 31))) != 0;

        static (NCollider2D, NCollider2D) Key(NCollider2D a, NCollider2D b) => a.InstanceId < b.InstanceId ? (a, b) : (b, a);
        public static void Ignore(NCollider2D a, NCollider2D b, bool on) { if (on) ignored.Add(Key(a, b)); else ignored.Remove(Key(a, b)); }
        public static bool Ignored(NCollider2D a, NCollider2D b) => ignored.Contains(Key(a, b));

        public static List<NCollider2D> Colliders()
        {
            var l = new List<NCollider2D>(Typed.Colliders.Count);
            foreach (var c in Typed.Colliders) if (c.Live) l.Add(c);
            return l;
        }

        // ---------------------------------------------------------------- filters

        static bool Accepts(NCollider2D c, ContactFilter2D f)
        {
            // The managed query wrappers set useTriggers from Physics2D.queriesHitTriggers.
            if (c.IsTrigger && !f.useTriggers) return false;
            if (f.useLayerMask && (f.layerMask.value & (1 << c.GameObject.Layer)) == 0) return false;
            if (f.useDepth)
            {
                // The legacy queries set depth from -Infinity to Infinity: every position is in it
                // (fetching each collider's position was most of this test).
                bool unbounded = float.IsNegativeInfinity(f.minDepth) && float.IsPositiveInfinity(f.maxDepth);
                float z = unbounded ? 0f : c.GameObject.Transform.Position.z;
                bool inRange = unbounded || (z >= f.minDepth && z <= f.maxDepth);
                if (f.useOutsideDepth ? inRange : !inRange) return false;
            }
            return true;
        }

        static bool NormalOk(Vector2 n, ContactFilter2D f)
        {
            if (!f.useNormalAngle) return true;
            float a = Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg;
            if (a < 0) a += 360;
            bool inRange = a >= f.minNormalAngle && a <= f.maxNormalAngle;
            return f.useOutsideNormalAngle ? !inRange : inRange;
        }

        // ---------------------------------------------------------------- casts

        /// <summary>Sweep a shape (relative to origin) along dir for distance; every hit sorted by fraction.</summary>
        public static List<RaycastHit2D> Cast(Vector2 origin, Shape castShape, Vector2 dir, float distance, ContactFilter2D filter, NCollider2D exclude = null, NRigidbody2D excludeBody = null)
        {
            var hits = new List<RaycastHit2D>();
            float len = dir.magnitude;
            if (len < 1e-9f) dir = Vector2.zero; else dir /= len;
            if (float.IsInfinity(distance) || distance > 1e6f) distance = 1e6f;
            if (distance < 0) distance = 0;
            var castBounds = castShape.Bounds();
            var sweep = Rect.MinMaxRect(origin.x + castBounds.xMin + Math.Min(0, dir.x * distance), origin.y + castBounds.yMin + Math.Min(0, dir.y * distance),
                origin.x + castBounds.xMax + Math.Max(0, dir.x * distance), origin.y + castBounds.yMax + Math.Max(0, dir.y * distance));
            // Only the colliders whose x extent meets the sweep's (QueryIndex), in creation order as the
            // walk of all was; then the cheap filters before Live's walks up the hierarchy.
            var all = QueryIndex.Candidates(sweep.xMin, sweep.xMax);
            for (int ci = 0; ci < all.Count; ci++)
            {
                var c = all[ci];
                if (c == exclude || c.Destroyed || !c.Enabled || c.GameObject == null || !Accepts(c, filter) || !c.Live) continue;
                if (excludeBody != null && c.Rigidbody == excludeBody) continue;
                c.Shapes();
                if (!c.AABB.Overlaps(sweep)) continue;
                float best = float.MaxValue; Vector2 bestN = Vector2.zero; bool found = false;
                foreach (var s in c.World)
                {
                    bool hit, inside; float t; Vector2 n;
                    bool castIsPoint = castShape.Pts.Length == 1 && castShape.R == 0;
                    if (castIsPoint && s.Pts.Length == 1) hit = Geometry.RayCircle(origin, dir, distance, s.Pts[0], s.R, out t, out n, out inside);
                    else if (castShape.Pts.Length == 1 && s.Pts.Length == 1) hit = Geometry.RayCircle(origin, dir, distance, s.Pts[0], s.R + castShape.R, out t, out n, out inside);
                    else if (castIsPoint && s.R == 0) hit = Geometry.RayPoly(origin, dir, distance, s.Pts, out t, out n, out inside);
                    else hit = Geometry.RayPoly(origin, dir, distance, Geometry.Minkowski(s, castShape), out t, out n, out inside);
                    if (!hit) continue;
                    if (inside && !QueriesStartInColliders) continue;
                    if (t < best) { best = t; bestN = n; found = true; }
                }
                if (!found || !NormalOk(bestN, filter)) continue;
                var centroid = origin + dir * best;
                var h = new RaycastHit2D();
                h.m_Centroid = centroid;
                h.m_Normal = bestN;
                h.m_Point = ContactPoint(c, centroid, castShape, bestN);
                h.m_Distance = best;
                h.m_Fraction = distance > 0 ? best / distance : 0;
                h.m_Collider = (Collider2D)c.Managed;
                hits.Add(h);
            }
            hits.Sort((a, b) => a.m_Distance.CompareTo(b.m_Distance));
            return hits;
        }

        static Vector2 ContactPoint(NCollider2D c, Vector2 centroid, Shape cast, Vector2 normal)
        {
            // The cast shape's support point toward the surface.
            if (cast.Pts.Length == 1) return cast.Pts[0] + centroid - normal * cast.R;
            Vector2 best = cast.Pts[0]; float bd = float.MaxValue;
            foreach (var p in cast.Pts) { float d = Vector2.Dot(p, normal); if (d < bd) { bd = d; best = p; } }
            return centroid + best - normal * cast.R;
        }

        public static Shape BoxShape(Vector2 size, float angle)
        {
            float hx = size.x / 2, hy = size.y / 2;
            float a = angle * Mathf.Deg2Rad, cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            Vector2 R(float x, float y) => new Vector2(x * cs - y * sn, x * sn + y * cs);
            return Shape.Poly(new[] { R(-hx, -hy), R(hx, -hy), R(hx, hy), R(-hx, hy) }, 0);
        }

        public static Shape CapsuleShape(Vector2 size, CapsuleDirection2D dir, float angle)
        {
            bool vertical = dir == CapsuleDirection2D.Vertical;
            float r = (vertical ? size.x : size.y) / 2, half = Math.Max(0, (vertical ? size.y : size.x) / 2 - r);
            float a = angle * Mathf.Deg2Rad, cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            Vector2 R(float x, float y) => new Vector2(x * cs - y * sn, x * sn + y * cs);
            return Shape.Poly(vertical ? new[] { R(0, -half), R(0, half) } : new[] { R(-half, 0), R(half, 0) }, r);
        }

        public static readonly Shape PointShape = Shape.Circle(Vector2.zero, 0);

        public static List<NCollider2D> OverlapShape(Shape world, ContactFilter2D filter)
        {
            var r = new List<NCollider2D>();
            var b = world.Bounds();
            var all = QueryIndex.Candidates(b.xMin, b.xMax);
            for (int ci = 0; ci < all.Count; ci++)
            {
                var c = all[ci];
                if (c.Destroyed || !c.Enabled || c.GameObject == null || !Accepts(c, filter) || !c.Live) continue;
                c.Shapes();
                if (!c.AABB.Overlaps(b) && !(b.width == 0 && c.AABB.Contains(b.position))) continue;
                foreach (var s in c.World) if (Geometry.Overlap(s, world)) { r.Add(c); break; }
            }
            return r;
        }

        public static Shape Moved(Shape s, Vector2 by) => new Shape { Pts = Array.ConvertAll(s.Pts, p => p + by), R = s.R };

        // ---------------------------------------------------------------- simulation and contacts

        sealed class Contact { public NCollider2D A, B; public bool Trigger; }
        static Dictionary<(NCollider2D, NCollider2D), Contact> contacts = new Dictionary<(NCollider2D, NCollider2D), Contact>();


        /// <summary>
        /// A collider is being disabled or destroyed: with callbacksOnDisable
        /// Unity sends the exit callbacks now, while both objects are valid.
        /// </summary>
        public static void ColliderGone(NCollider2D c)
        {
            var ended = new List<(NCollider2D, NCollider2D)>();
            foreach (var k in contacts.Keys) if (k.Item1 == c || k.Item2 == c) ended.Add(k);
            foreach (var k in ended)
            {
                if (!contacts.TryGetValue(k, out var ct)) continue;
                contacts.Remove(k);
                if (CallbacksOnDisable) Send(ct, 2);
            }
        }

        public static void Step(float dt)
        {
            if (!AutoSimulation) return;
            Simulate(dt);
        }

        public static void Simulate(float dt)
        {
            // Bodies.
            bodies.RemoveAll(b => { if (b.Destroyed) { bodySet.Remove(b); return true; } return false; });
            foreach (var b in bodies)
            {
                if (!b.Simulated || !b.Enabled || b.GameObject == null || !b.GameObject.ActiveInHierarchy) continue;
                var t = b.GameObject.Transform;
                if (b.BodyType == RigidbodyType2D.Dynamic)
                {
                    b.Velocity += Gravity * b.GravityScale * dt;
                    if (b.Drag > 0) b.Velocity *= 1f / (1f + dt * b.Drag);
                    if ((b.Constraints & RigidbodyConstraints2D.FreezePositionX) != 0) b.Velocity.x = 0;
                    if ((b.Constraints & RigidbodyConstraints2D.FreezePositionY) != 0) b.Velocity.y = 0;
                }
                if (b.MoveTo.HasValue) { SetWorld(t, b.MoveTo.Value); b.MoveTo = null; }
                else if (b.BodyType != RigidbodyType2D.Static && b.Velocity != Vector2.zero)
                {
                    var p = t.Position;
                    SetWorld(t, new Vector2(p.x, p.y) + b.Velocity * dt);
                }
                if (b.RotateTo.HasValue) { t.LocalRotation = Quaternion.Euler(0, 0, b.RotateTo.Value); t.Invalidate(); b.RotateTo = null; }
                else if (b.BodyType == RigidbodyType2D.Dynamic && b.AngularVelocity != 0 && (b.Constraints & RigidbodyConstraints2D.FreezeRotation) == 0)
                {
                    t.LocalRotation = t.LocalRotation * Quaternion.Euler(0, 0, b.AngularVelocity * dt);
                    t.Invalidate();
                }
            }
            // Dynamic bodies are pushed out of the solid colliders they moved into.
            Resolve();
            // Contacts. Each collider's rigidbody once (the pairs asked for it each: a walk up the
            // hierarchy per pair, every step); nothing here changes the hierarchy.
            var cols = Colliders();
            foreach (var c in cols) c.Shapes();
            if (TraceContact != null)
                foreach (var c in cols) if (Traced(c)) Log.Line($"TRACE contact frame {Clock.FrameCount} collider {Describe(c)}");
            var bodyOf = BodiesOf(cols);
            var now = new Dictionary<(NCollider2D, NCollider2D), Contact>();
            // Sort and sweep on the boxes' x ranges: only pairs whose ranges meet are looked at (every pair,
            // each step, was most of the run-and-gun levels' physics). The pairs found are put back in index
            // order, so the contacts and their callbacks come in the same order as from the full loop.
            int nc = cols.Count;
            if (sweepOrder.Length < nc) { sweepOrder = new int[nc * 2]; sweepMin = new float[nc * 2]; }
            for (int i = 0; i < nc; i++) { sweepOrder[i] = i; sweepMin[i] = cols[i].AABB.xMin; }
            Array.Sort(sweepMin, sweepOrder, 0, nc);
            sweepPairs.Clear();
            for (int oi = 0; oi < nc; oi++)
            {
                int i = sweepOrder[oi];
                float maxX = cols[i].AABB.xMax;
                for (int oj = oi + 1; oj < nc && sweepMin[oj] <= maxX; oj++)
                {
                    int j = sweepOrder[oj];
                    sweepPairs.Add(i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i);
                }
            }
            sweepPairs.Sort();
            foreach (long pair in sweepPairs)
            {
                int i = (int)(pair >> 32), j = (int)(pair & 0xFFFFFFFF);
                var a = cols[i]; var ra = bodyOf[i];
                {
                    var b = cols[j]; var rb = bodyOf[j];
                    if (ra == null && rb == null) continue;                  // two static colliders
                    if (ra != null && ra == rb) continue;                    // same body
                    bool trigger = a.IsTrigger || b.IsTrigger;
                    if (!trigger)
                    {
                        bool aDyn = ra != null && ra.BodyType == RigidbodyType2D.Dynamic, bDyn = rb != null && rb.BodyType == RigidbodyType2D.Dynamic;
                        bool aFull = ra != null && ra.BodyType == RigidbodyType2D.Kinematic && ra.UseFullKinematicContacts;
                        bool bFull = rb != null && rb.BodyType == RigidbodyType2D.Kinematic && rb.UseFullKinematicContacts;
                        if (!aDyn && !bDyn && !aFull && !bFull) continue;
                    }
                    if (!LayersCollide(a.GameObject.Layer, b.GameObject.Layer)) continue;
                    if (!a.AABB.Overlaps(b.AABB)) continue;
                    if (Ignored(a, b)) continue;
                    bool touch = false;
                    foreach (var sa in a.World) { foreach (var sb in b.World) if (Geometry.Overlap(sa, sb)) { touch = true; break; } if (touch) break; }
                    if (TraceContact != null && (Traced(a) || Traced(b)))
                        Log.Line($"TRACE contact frame {Clock.FrameCount} {(touch ? "touch" : "boxes meet, shapes don't")}: {Describe(a)} / {Describe(b)}");
                    if (!touch) continue;
                    var key = Key(a, b);
                    now[key] = new Contact { A = key.Item1, B = key.Item2, Trigger = trigger };
                }
            }
            var old = contacts;
            contacts = now;
            // Callbacks may disable or destroy colliders (which edits the contact set): walk snapshots.
            foreach (var kv in new List<KeyValuePair<(NCollider2D, NCollider2D), Contact>>(now))
            {
                if (!contacts.ContainsKey(kv.Key)) continue;
                bool stay = old.TryGetValue(kv.Key, out var prev) && prev.Trigger == kv.Value.Trigger;
                Send(kv.Value, stay ? 1 : 0);
            }
            foreach (var kv in old)
                if (!now.ContainsKey(kv.Key) && Alive(kv.Value.A) && Alive(kv.Value.B)) Send(kv.Value, 2);
        }

        static NRigidbody2D[] bodiesBuf = new NRigidbody2D[64];

        /// <summary>The rigidbody of each collider (NCollider2D.Rigidbody), in a buffer kept from call to call.</summary>
        static NRigidbody2D[] BodiesOf(List<NCollider2D> cols)
        {
            if (bodiesBuf.Length < cols.Count) bodiesBuf = new NRigidbody2D[Math.Max(cols.Count, bodiesBuf.Length * 2)];
            for (int i = 0; i < cols.Count; i++) bodiesBuf[i] = cols[i].Rigidbody;
            return bodiesBuf;
        }

        static bool Alive(NCollider2D c) => !c.Destroyed && c.GameObject != null && !c.GameObject.Destroyed;

        /// <summary>
        /// Collision response for dynamic bodies against solid (non-trigger)
        /// colliders: separate along the minimum translation vector and remove
        /// the velocity into the surface. Static, kinematic and other bodies'
        /// colliders all block; a few iterations settle corners.
        /// </summary>
        static void Resolve()
        {
            List<NCollider2D> all = null;
            NRigidbody2D[] bodyOf = null;
            foreach (var b in bodies)
            {
                if (b.BodyType != RigidbodyType2D.Dynamic || !b.Simulated || !b.Enabled || b.GameObject == null || !b.GameObject.ActiveInHierarchy) continue;
                if (all == null) { all = Colliders(); bodyOf = BodiesOf(all); }
                var mine = new List<NCollider2D>();
                for (int k = 0; k < all.Count; k++) if (!all[k].IsTrigger && bodyOf[k] == b) mine.Add(all[k]);
                if (mine.Count == 0) continue;
                var t = b.GameObject.Transform;
                for (int iter = 0; iter < 4; iter++)
                {
                    Vector2 push = Vector2.zero;
                    foreach (var c in mine)
                    {
                        c.Shapes();
                        for (int k = 0; k < all.Count; k++)
                        {
                            var o = all[k];
                            if (o.IsTrigger || bodyOf[k] == b || o.UsedByEffector) continue;
                            if (!LayersCollide(c.GameObject.Layer, o.GameObject.Layer) || Ignored(c, o)) continue;
                            o.Shapes();
                            if (!c.AABB.Overlaps(o.AABB)) continue;
                            foreach (var sa in c.World)
                                foreach (var sb in o.World)
                                {
                                    var sep = Geometry.Separation(Moved(sa, push), sb);
                                    if (sep.sqrMagnitude > 1e-12f) push += sep;
                                }
                        }
                    }
                    if (push.sqrMagnitude < 1e-12f) break;
                    var p = t.Position;
                    SetWorld(t, new Vector2(p.x, p.y) + push);
                    var n = push.normalized;
                    float into = Vector2.Dot(b.Velocity, n);
                    if (into < 0) b.Velocity -= n * into;
                }
            }
        }

        static void SetWorld(NTransform t, Vector2 p)
        {
            var z = t.Position.z;
            if (t.Parent == null) t.LocalPosition = new Vector3(p.x, p.y, z);
            else { MathX.Invert(t.Parent.LocalToWorld, out var inv); t.LocalPosition = inv.MultiplyPoint3x4(new Vector3(p.x, p.y, z)); }
            t.Invalidate();
        }

        // CH360_TRACE_CONTACT=NAME: each step's pairs whose boxes meet, for colliders on objects whose name has NAME.
        public static readonly string TraceContact = Environment.GetEnvironmentVariable("CH360_TRACE_CONTACT");
        static bool Traced(NCollider2D c) => c.GameObject != null && c.GameObject.Name.Contains(TraceContact);
        static string Describe(NCollider2D c)
        {
            var go = c.GameObject;
            var sb = new System.Text.StringBuilder();
            sb.Append(go.Name).Append(" [").Append(go.Tag).Append(", layer ").Append(go.Layer).Append(c.IsTrigger ? ", trigger" : "").Append("]");
            foreach (var s in c.World)
            {
                sb.Append(s.Pts.Length == 1 ? " circle r=" + s.R.ToString("0.###") : " " + s.Pts.Length + "-gon");
                foreach (var p in s.Pts) sb.Append(" (").Append(p.x.ToString("0.##")).Append(',').Append(p.y.ToString("0.##")).Append(')');
            }
            return sb.ToString();
        }

        static readonly string[] triggerNames = { "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D" };
        static readonly string[] collisionNames = { "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D" };

        static void Send(Contact c, int phase)
        {
            Deliver(c.A, c.B, c.Trigger, phase);
            Deliver(c.B, c.A, c.Trigger, phase);
        }

        /// <summary>Messages go to the collider's GameObject and, if different, its rigidbody's GameObject.</summary>
        static void Deliver(NCollider2D self, NCollider2D other, bool trigger, int phase)
        {
            if (!Alive(self) || self.GameObject == null || !self.GameObject.ActiveInHierarchy) return;
            string name = trigger ? triggerNames[phase] : collisionNames[phase];
            object arg;
            if (trigger) arg = other.Managed;
            else
            {
                var col = new Collision2D();
                col.m_Collider = other.InstanceId; col.m_OtherCollider = self.InstanceId;
                var orb = other.Rigidbody; var srb = self.Rigidbody;
                col.m_Rigidbody = orb?.InstanceId ?? 0; col.m_OtherRigidbody = srb?.InstanceId ?? 0;
                col.m_RelativeVelocity = (orb?.Velocity ?? Vector2.zero) - (srb?.Velocity ?? Vector2.zero);
                col.m_Enabled = 1;
                arg = col;
            }
            var targets = new List<NGameObject> { self.GameObject };
            var rb = self.Rigidbody;
            if (rb != null && rb.GameObject != self.GameObject) targets.Add(rb.GameObject);
            foreach (var go in targets)
                foreach (var comp in go.Components.ToArray())
                {
                    if (!(comp is NBehaviour b) || b.Destroyed) continue;
                    var ms = b.Messages;
                    var m = trigger
                        ? (phase == 0 ? ms.OnTriggerEnter2D : phase == 1 ? ms.OnTriggerStay2D : ms.OnTriggerExit2D)
                        : (phase == 0 ? ms.OnCollisionEnter2D : phase == 1 ? ms.OnCollisionStay2D : ms.OnCollisionExit2D);
                    if (m == null) continue;
                    var r = Behaviours.Send(b, m, arg);
                    if (r is System.Collections.IEnumerator e) Coroutines.Start(b, e);
                }
        }

        public static bool Touching(NCollider2D a, NCollider2D b) => contacts.ContainsKey(Key(a, b));
        public static IEnumerable<NCollider2D> TouchingOf(NCollider2D c)
        {
            foreach (var k in contacts.Keys) { if (k.Item1 == c) yield return k.Item2; else if (k.Item2 == c) yield return k.Item1; }
        }
    }

    public static partial class Icalls
    {
        static NCollider2D Col(Collider2D c) => Objects.Of(c) as NCollider2D;
        static NRigidbody2D Rb(Rigidbody2D r) => Objects.Of(r) as NRigidbody2D;

        static ContactFilter2D DefaultFilter(ContactFilter2D f) => f;

        static int Fill(List<RaycastHit2D> hits, RaycastHit2D[] results)
        {
            if (results == null) return 0;
            int n = Math.Min(hits.Count, results.Length);
            for (int i = 0; i < n; i++) results[i] = hits[i];
            return n;
        }
        static int Fill(List<NCollider2D> cols, Collider2D[] results)
        {
            if (results == null) return 0;
            int n = Math.Min(cols.Count, results.Length);
            for (int i = 0; i < n; i++) results[i] = (Collider2D)cols[i].Managed;
            return n;
        }
        static RaycastHit2D First(List<RaycastHit2D> h) => h.Count > 0 ? h[0] : default;

        // Raycast / linecast
        public static void UnityEngine_Physics2D__INTERNAL_CALL_Internal_Raycast(ref Vector2 origin, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, out RaycastHit2D raycastHit)
            => raycastHit = First(Physics.Cast(origin, Physics.PointShape, direction, distance, contactFilter));
        public static RaycastHit2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_RaycastAll(ref Vector2 origin, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter)
            => Physics.Cast(origin, Physics.PointShape, direction, distance, contactFilter).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_RaycastNonAlloc(ref Vector2 origin, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, RaycastHit2D[] results)
            => Fill(Physics.Cast(origin, Physics.PointShape, direction, distance, contactFilter), results);
        public static void UnityEngine_Physics2D__INTERNAL_CALL_Internal_Linecast(ref Vector2 start, ref Vector2 end, ref ContactFilter2D contactFilter, out RaycastHit2D raycastHit)
            => raycastHit = First(Physics.Cast(start, Physics.PointShape, end - start, (end - start).magnitude, contactFilter));
        public static RaycastHit2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_LinecastAll(ref Vector2 start, ref Vector2 end, ref ContactFilter2D contactFilter)
            => Physics.Cast(start, Physics.PointShape, end - start, (end - start).magnitude, contactFilter).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_LinecastNonAlloc(ref Vector2 start, ref Vector2 end, ref ContactFilter2D contactFilter, RaycastHit2D[] results)
            => Fill(Physics.Cast(start, Physics.PointShape, end - start, (end - start).magnitude, contactFilter), results);

        // Circle / box / capsule casts
        public static void UnityEngine_Physics2D__INTERNAL_CALL_Internal_CircleCast(ref Vector2 origin, float radius, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, out RaycastHit2D raycastHit)
            => raycastHit = First(Physics.Cast(origin, Shape.Circle(Vector2.zero, radius), direction, distance, contactFilter));
        public static RaycastHit2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_CircleCastAll(ref Vector2 origin, float radius, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter)
            => Physics.Cast(origin, Shape.Circle(Vector2.zero, radius), direction, distance, contactFilter).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_CircleCastNonAlloc(ref Vector2 origin, float radius, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, RaycastHit2D[] results)
            => Fill(Physics.Cast(origin, Shape.Circle(Vector2.zero, radius), direction, distance, contactFilter), results);
        public static void UnityEngine_Physics2D__INTERNAL_CALL_Internal_BoxCast(ref Vector2 origin, ref Vector2 size, float angle, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, out RaycastHit2D raycastHit)
            => raycastHit = First(Physics.Cast(origin, Physics.BoxShape(size, angle), direction, distance, contactFilter));
        public static RaycastHit2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_BoxCastAll(ref Vector2 origin, ref Vector2 size, float angle, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter)
            => Physics.Cast(origin, Physics.BoxShape(size, angle), direction, distance, contactFilter).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_BoxCastNonAlloc(ref Vector2 origin, ref Vector2 size, float angle, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, RaycastHit2D[] results)
            => Fill(Physics.Cast(origin, Physics.BoxShape(size, angle), direction, distance, contactFilter), results);
        public static void UnityEngine_Physics2D__INTERNAL_CALL_Internal_CapsuleCast(ref Vector2 origin, ref Vector2 size, CapsuleDirection2D capsuleDirection, float angle, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, out RaycastHit2D raycastHit)
            => raycastHit = First(Physics.Cast(origin, Physics.CapsuleShape(size, capsuleDirection, angle), direction, distance, contactFilter));
        public static RaycastHit2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_CapsuleCastAll(ref Vector2 origin, ref Vector2 size, CapsuleDirection2D capsuleDirection, float angle, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter)
            => Physics.Cast(origin, Physics.CapsuleShape(size, capsuleDirection, angle), direction, distance, contactFilter).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_CapsuleCastNonAlloc(ref Vector2 origin, ref Vector2 size, CapsuleDirection2D capsuleDirection, float angle, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, RaycastHit2D[] results)
            => Fill(Physics.Cast(origin, Physics.CapsuleShape(size, capsuleDirection, angle), direction, distance, contactFilter), results);
        public static void UnityEngine_Physics2D__INTERNAL_CALL_Internal_GetRayIntersection(ref Ray ray, float distance, int layerMask, out RaycastHit2D raycastHit)
        {
            var f = new ContactFilter2D { useTriggers = Physics.QueriesHitTriggers, useLayerMask = true, layerMask = layerMask };
            raycastHit = First(Physics.Cast(new Vector2(ray.origin.x, ray.origin.y), Physics.PointShape, Vector2.zero, 0, f));
        }

        // Overlaps
        public static Collider2D UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapPoint(ref Vector2 point, ref ContactFilter2D contactFilter)
        { var l = Physics.OverlapShape(Shape.Circle(point, 0), contactFilter); return l.Count > 0 ? (Collider2D)l[0].Managed : null; }
        public static Collider2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapPointAll(ref Vector2 point, ref ContactFilter2D contactFilter)
            => Physics.OverlapShape(Shape.Circle(point, 0), contactFilter).ConvertAll(c => (Collider2D)c.Managed).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapPointNonAlloc(ref Vector2 point, ref ContactFilter2D contactFilter, Collider2D[] results)
            => Fill(Physics.OverlapShape(Shape.Circle(point, 0), contactFilter), results);
        public static Collider2D UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapCircle(ref Vector2 point, float radius, ref ContactFilter2D contactFilter)
        { var l = Physics.OverlapShape(Shape.Circle(point, radius), contactFilter); return l.Count > 0 ? (Collider2D)l[0].Managed : null; }
        public static Collider2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapCircleAll(ref Vector2 point, float radius, ref ContactFilter2D contactFilter)
            => Physics.OverlapShape(Shape.Circle(point, radius), contactFilter).ConvertAll(c => (Collider2D)c.Managed).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapCircleNonAlloc(ref Vector2 point, float radius, ref ContactFilter2D contactFilter, Collider2D[] results)
            => Fill(Physics.OverlapShape(Shape.Circle(point, radius), contactFilter), results);
        public static Collider2D UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapBox(ref Vector2 point, ref Vector2 size, float angle, ref ContactFilter2D contactFilter)
        { var l = Physics.OverlapShape(Physics.Moved(Physics.BoxShape(size, angle), point), contactFilter); return l.Count > 0 ? (Collider2D)l[0].Managed : null; }
        public static Collider2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapBoxAll(ref Vector2 point, ref Vector2 size, float angle, ref ContactFilter2D contactFilter)
            => Physics.OverlapShape(Physics.Moved(Physics.BoxShape(size, angle), point), contactFilter).ConvertAll(c => (Collider2D)c.Managed).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapBoxNonAlloc(ref Vector2 point, ref Vector2 size, float angle, ref ContactFilter2D contactFilter, Collider2D[] results)
            => Fill(Physics.OverlapShape(Physics.Moved(Physics.BoxShape(size, angle), point), contactFilter), results);
        static Shape Area(Vector2 a, Vector2 b) => Shape.Poly(new[] { new Vector2(Math.Min(a.x, b.x), Math.Min(a.y, b.y)), new Vector2(Math.Max(a.x, b.x), Math.Min(a.y, b.y)), new Vector2(Math.Max(a.x, b.x), Math.Max(a.y, b.y)), new Vector2(Math.Min(a.x, b.x), Math.Max(a.y, b.y)) }, 0);
        public static Collider2D UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapArea(ref Vector2 pointA, ref Vector2 pointB, ref ContactFilter2D contactFilter)
        { var l = Physics.OverlapShape(Area(pointA, pointB), contactFilter); return l.Count > 0 ? (Collider2D)l[0].Managed : null; }
        public static Collider2D[] UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapAreaAll(ref Vector2 pointA, ref Vector2 pointB, ref ContactFilter2D contactFilter)
            => Physics.OverlapShape(Area(pointA, pointB), contactFilter).ConvertAll(c => (Collider2D)c.Managed).ToArray();
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapAreaNonAlloc(ref Vector2 pointA, ref Vector2 pointB, ref ContactFilter2D contactFilter, Collider2D[] results)
            => Fill(Physics.OverlapShape(Area(pointA, pointB), contactFilter), results);
        public static Collider2D UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapCapsule(ref Vector2 point, ref Vector2 size, CapsuleDirection2D direction, float angle, ref ContactFilter2D contactFilter)
        { var l = Physics.OverlapShape(Physics.Moved(Physics.CapsuleShape(size, direction, angle), point), contactFilter); return l.Count > 0 ? (Collider2D)l[0].Managed : null; }
        public static int UnityEngine_Physics2D__INTERNAL_CALL_Internal_OverlapCapsuleNonAlloc(ref Vector2 point, ref Vector2 size, CapsuleDirection2D direction, float angle, ref ContactFilter2D contactFilter, Collider2D[] results)
            => Fill(Physics.OverlapShape(Physics.Moved(Physics.CapsuleShape(size, direction, angle), point), contactFilter), results);
        public static int UnityEngine_Physics2D__INTERNAL_CALL_OverlapCollider(Collider2D collider, ref ContactFilter2D contactFilter, Collider2D[] results)
        {
            var c = Col(collider);
            if (c == null) return 0;
            var found = new List<NCollider2D>();
            foreach (var s in c.Shapes())
                foreach (var o in Physics.OverlapShape(s, contactFilter))
                    if (o != c && !found.Contains(o)) found.Add(o);
            return Fill(found, results);
        }

        public static int UnityEngine_LayerMask__NameToLayer(string layerName)
        {
            for (int i = 0; i < 32; i++) if (AssetDb.Layers[i] == layerName) return i;
            return -1;
        }
        public static string UnityEngine_LayerMask__LayerToName(int layer) => layer >= 0 && layer < 32 ? AssetDb.Layers[layer] ?? "" : "";

        public static Rigidbody2D UnityEngine_Physics2D__GetRigidbodyFromInstanceID(int instanceID) => Objects.ByInstanceId(instanceID)?.Managed as Rigidbody2D;
        public static Collider2D UnityEngine_Physics2D__GetColliderFromInstanceID(int instanceID) => Objects.ByInstanceId(instanceID)?.Managed as Collider2D;
        public static ContactPoint2D[] UnityEngine_Collision2D__CreateCollisionContacts(Collider2D collider, Collider2D otherCollider, Rigidbody2D rigidbody, Rigidbody2D otherRigidbody, bool enabled)
        {
            var a = Col(collider); var b = Col(otherCollider);
            if (a == null || b == null) return new ContactPoint2D[0];
            var ca = a.AABB.center; var cb = b.AABB.center;
            var p = new ContactPoint2D();
            p.m_Point = (ca + cb) / 2; p.m_Normal = (cb - ca).normalized;
            p.m_Collider = a.InstanceId; p.m_OtherCollider = b.InstanceId; p.m_Enabled = enabled ? 1 : 0;
            return new[] { p };
        }

        // Settings
        public static void UnityEngine_Physics2D__get_gravity_Injected(out Vector2 ret) => ret = Physics.Gravity;
        public static void UnityEngine_Physics2D__set_gravity_Injected(ref Vector2 value) => Physics.Gravity = value;
        public static bool UnityEngine_Physics2D__get_queriesHitTriggers() => Physics.QueriesHitTriggers;
        public static void UnityEngine_Physics2D__set_queriesHitTriggers(bool value) => Physics.QueriesHitTriggers = value;
        public static bool UnityEngine_Physics2D__get_queriesStartInColliders() => Physics.QueriesStartInColliders;
        public static void UnityEngine_Physics2D__set_queriesStartInColliders(bool value) => Physics.QueriesStartInColliders = value;
        public static bool UnityEngine_Physics2D__get_autoSimulation() => Physics.AutoSimulation;
        public static void UnityEngine_Physics2D__set_autoSimulation(bool value) => Physics.AutoSimulation = value;
        public static bool UnityEngine_Physics2D__get_autoSyncTransforms() => Physics.AutoSyncTransforms;
        public static void UnityEngine_Physics2D__set_autoSyncTransforms(bool value) => Physics.AutoSyncTransforms = value;
        public static bool UnityEngine_Physics2D__get_callbacksOnDisable() => Physics.CallbacksOnDisable;
        public static void UnityEngine_Physics2D__set_callbacksOnDisable(bool value) => Physics.CallbacksOnDisable = value;
        public static bool UnityEngine_Physics2D__get_changeStopsCallbacks() => Physics.ChangeStopsCallbacks;
        public static void UnityEngine_Physics2D__set_changeStopsCallbacks(bool value) => Physics.ChangeStopsCallbacks = value;
        public static bool UnityEngine_Physics2D__Simulate(float step) { Physics.Simulate(step); return true; }
        public static void UnityEngine_Physics2D__SyncTransforms() { }
        public static void UnityEngine_Physics2D__IgnoreCollision(Collider2D collider1, Collider2D collider2, bool ignore)
        { var a = Col(collider1); var b = Col(collider2); if (a != null && b != null) Physics.Ignore(a, b, ignore); }
        public static bool UnityEngine_Physics2D__GetIgnoreCollision(Collider2D collider1, Collider2D collider2)
        { var a = Col(collider1); var b = Col(collider2); return a != null && b != null && Physics.Ignored(a, b); }
        public static void UnityEngine_Physics2D__IgnoreLayerCollision_Internal(int layer1, int layer2, bool ignore)
        {
            if (ignore) { Physics.LayerMatrix[layer1] &= ~(1u << layer2); Physics.LayerMatrix[layer2] &= ~(1u << layer1); }
            else { Physics.LayerMatrix[layer1] |= 1u << layer2; Physics.LayerMatrix[layer2] |= 1u << layer1; }
        }
        public static bool UnityEngine_Physics2D__GetIgnoreLayerCollision_Internal(int layer1, int layer2) => !Physics.LayersCollide(layer1, layer2);

        // Collider2D
        public static void UnityEngine_Collider2D__get_offset_Injected(Collider2D self, out Vector2 ret) => ret = Col(self)?.Offset ?? default;
        public static void UnityEngine_Collider2D__set_offset_Injected(Collider2D self, ref Vector2 value) { var c = Col(self); if (c != null) { c.Offset = value; c.Version++; } }
        public static bool UnityEngine_Collider2D__get_isTrigger(Collider2D self) => Col(self)?.IsTrigger ?? false;
        public static void UnityEngine_Collider2D__set_isTrigger(Collider2D self, bool value) { var c = Col(self); if (c != null) c.IsTrigger = value; }
        public static bool UnityEngine_Collider2D__get_usedByEffector(Collider2D self) => Col(self)?.UsedByEffector ?? false;
        public static void UnityEngine_Collider2D__set_usedByEffector(Collider2D self, bool value) { var c = Col(self); if (c != null) c.UsedByEffector = value; }
        public static bool UnityEngine_Collider2D__get_usedByComposite(Collider2D self) => false;
        public static void UnityEngine_Collider2D__set_usedByComposite(Collider2D self, bool value) { }
        public static float UnityEngine_Collider2D__get_density(Collider2D self) => Col(self)?.Density ?? 1f;
        public static void UnityEngine_Collider2D__set_density(Collider2D self, float value) { var c = Col(self); if (c != null) c.Density = value; }
        public static Rigidbody2D UnityEngine_Collider2D__get_attachedRigidbody(Collider2D self) => Col(self)?.Rigidbody?.Managed as Rigidbody2D;
        public static int UnityEngine_Collider2D__get_shapeCount(Collider2D self) => Col(self)?.Shapes().Count ?? 0;
        public static PhysicsMaterial2D UnityEngine_Collider2D__get_sharedMaterial(Collider2D self) => Col(self)?.Material;
        public static void UnityEngine_Collider2D__set_sharedMaterial(Collider2D self, PhysicsMaterial2D value) { var c = Col(self); if (c != null) c.Material = value; }
        public static void UnityEngine_Collider2D__get_bounds_Injected(Collider2D self, out Bounds ret)
        {
            var c = Col(self);
            if (c == null) { ret = default; return; }
            c.Shapes();
            var r = c.AABB;
            ret = new Bounds(new Vector3(r.center.x, r.center.y, c.GameObject.Transform.Position.z), new Vector3(r.width, r.height, 0));
        }
        public static bool UnityEngine_Collider2D__IsTouching(Collider2D self, Collider2D collider) { var a = Col(self); var b = Col(collider); return a != null && b != null && Physics.Touching(a, b); }
        public static bool UnityEngine_Collider2D__IsTouching_OtherColliderWithFilter_Injected(Collider2D self, Collider2D collider, ref ContactFilter2D contactFilter) => UnityEngine_Collider2D__IsTouching(self, collider);
        public static bool UnityEngine_Collider2D__IsTouchingLayers(Collider2D self, int layerMask)
        {
            var c = Col(self);
            if (c == null) return false;
            foreach (var o in Physics.TouchingOf(c)) if ((layerMask & (1 << o.GameObject.Layer)) != 0) return true;
            return false;
        }
        public static bool UnityEngine_Collider2D__OverlapPoint_Injected(Collider2D self, ref Vector2 point)
        {
            var c = Col(self);
            if (c == null) return false;
            foreach (var s in c.Shapes()) if (Geometry.Distance(s, point) <= 0) return true;
            return false;
        }
        public static int UnityEngine_Collider2D__INTERNAL_CALL_Internal_Raycast(Collider2D self, ref Vector2 direction, float distance, ref ContactFilter2D contactFilter, RaycastHit2D[] results)
        {
            var c = Col(self);
            if (c == null) return 0;
            c.Shapes();
            return Fill(Physics.Cast(c.AABB.center, Physics.PointShape, direction, distance, contactFilter, exclude: c), results);
        }
        public static int UnityEngine_Collider2D__INTERNAL_CALL_Internal_Cast(Collider2D self, ref Vector2 direction, ref ContactFilter2D contactFilter, float distance, bool ignoreSiblingColliders, RaycastHit2D[] results)
        {
            var c = Col(self);
            if (c == null || c.World.Count == 0 && c.Shapes().Count == 0) return 0;
            var center = c.AABB.center;
            var s = c.World[0];
            var rel = new Shape { Pts = Array.ConvertAll(s.Pts, p => p - center), R = s.R };
            return Fill(Physics.Cast(center, rel, direction, distance, contactFilter, exclude: c, excludeBody: ignoreSiblingColliders ? c.Rigidbody : null), results);
        }
        public static void UnityEngine_BoxCollider2D__get_size_Injected(BoxCollider2D self, out Vector2 ret) => ret = Col(self)?.Size ?? Vector2.one;
        public static void UnityEngine_BoxCollider2D__set_size_Injected(BoxCollider2D self, ref Vector2 value) { var c = Col(self); if (c != null) { c.Size = value; c.Version++; } }
        public static float UnityEngine_BoxCollider2D__get_edgeRadius(BoxCollider2D self) => Col(self)?.EdgeRadius ?? 0;
        public static void UnityEngine_BoxCollider2D__set_edgeRadius(BoxCollider2D self, float value) { var c = Col(self); if (c != null) { c.EdgeRadius = value; c.Version++; } }
        public static bool UnityEngine_BoxCollider2D__get_autoTiling(BoxCollider2D self) => false;
        public static void UnityEngine_BoxCollider2D__set_autoTiling(BoxCollider2D self, bool value) { }
        public static float UnityEngine_CircleCollider2D__get_radius(CircleCollider2D self) => Col(self)?.Radius ?? 0.5f;
        public static void UnityEngine_CircleCollider2D__set_radius(CircleCollider2D self, float value) { var c = Col(self); if (c != null) { c.Radius = value; c.Version++; } }
        public static void UnityEngine_CapsuleCollider2D__get_size_Injected(CapsuleCollider2D self, out Vector2 ret) => ret = Col(self)?.Size ?? Vector2.one;
        public static void UnityEngine_CapsuleCollider2D__set_size_Injected(CapsuleCollider2D self, ref Vector2 value) { var c = Col(self); if (c != null) { c.Size = value; c.Version++; } }
        public static CapsuleDirection2D UnityEngine_CapsuleCollider2D__get_direction(CapsuleCollider2D self) => Col(self)?.Direction ?? 0;
        public static void UnityEngine_CapsuleCollider2D__set_direction(CapsuleCollider2D self, CapsuleDirection2D value) { var c = Col(self); if (c != null) { c.Direction = value; c.Version++; } }
        public static Vector2[] UnityEngine_PolygonCollider2D__get_points(PolygonCollider2D self) { var c = Col(self); return c != null && c.Paths.Count > 0 ? (Vector2[])c.Paths[0].Clone() : new Vector2[0]; }
        public static void UnityEngine_PolygonCollider2D__set_points(PolygonCollider2D self, Vector2[] value)
        {
            var c = Col(self);
            if (c == null) return;
            if (c.Paths.Count == 0) c.Paths.Add(new Vector2[0]);
            c.Paths[0] = (Vector2[])(value?.Clone() ?? new Vector2[0]);
            c.Version++;
        }
        public static Vector2[] UnityEngine_PolygonCollider2D__GetPath(PolygonCollider2D self, int index) { var c = Col(self); return c != null && index >= 0 && index < c.Paths.Count ? (Vector2[])c.Paths[index].Clone() : new Vector2[0]; }
        public static void UnityEngine_PolygonCollider2D__SetPath(PolygonCollider2D self, int index, Vector2[] points)
        {
            var c = Col(self);
            if (c == null || index < 0) return;
            while (c.Paths.Count <= index) c.Paths.Add(new Vector2[0]);
            c.Paths[index] = (Vector2[])(points?.Clone() ?? new Vector2[0]);
            c.Version++;
        }
        public static int UnityEngine_PolygonCollider2D__get_pathCount(PolygonCollider2D self) => Col(self)?.Paths.Count ?? 0;
        public static void UnityEngine_PolygonCollider2D__set_pathCount(PolygonCollider2D self, int value)
        {
            var c = Col(self);
            if (c == null) return;
            while (c.Paths.Count < value) c.Paths.Add(new Vector2[0]);
            while (c.Paths.Count > value) c.Paths.RemoveAt(c.Paths.Count - 1);
            c.Version++;
        }
        public static int UnityEngine_PolygonCollider2D__GetTotalPointCount(PolygonCollider2D self) { int n = 0; var c = Col(self); if (c != null) foreach (var p in c.Paths) n += p.Length; return n; }
        public static Vector2[] UnityEngine_EdgeCollider2D__get_points(EdgeCollider2D self) => (Vector2[])(Col(self)?.Points.Clone() ?? new Vector2[0]);
        public static void UnityEngine_EdgeCollider2D__set_points(EdgeCollider2D self, Vector2[] value) { var c = Col(self); if (c != null) { c.Points = (Vector2[])(value?.Clone() ?? new Vector2[0]); c.Version++; } }
        public static int UnityEngine_EdgeCollider2D__get_pointCount(EdgeCollider2D self) => Col(self)?.Points.Length ?? 0;
        public static int UnityEngine_EdgeCollider2D__get_edgeCount(EdgeCollider2D self) => Math.Max(0, (Col(self)?.Points.Length ?? 0) - 1);
        public static float UnityEngine_EdgeCollider2D__get_edgeRadius(EdgeCollider2D self) => Col(self)?.EdgeRadius ?? 0;
        public static void UnityEngine_EdgeCollider2D__set_edgeRadius(EdgeCollider2D self, float value) { var c = Col(self); if (c != null) { c.EdgeRadius = value; c.Version++; } }

        // Rigidbody2D
        public static void UnityEngine_Rigidbody2D__get_velocity_Injected(Rigidbody2D self, out Vector2 ret) => ret = Rb(self)?.Velocity ?? default;
        public static void UnityEngine_Rigidbody2D__set_velocity_Injected(Rigidbody2D self, ref Vector2 value) { var r = Rb(self); if (r != null) { r.Velocity = value; Physics.Track(r); } }
        public static float UnityEngine_Rigidbody2D__get_angularVelocity(Rigidbody2D self) => Rb(self)?.AngularVelocity ?? 0;
        public static void UnityEngine_Rigidbody2D__set_angularVelocity(Rigidbody2D self, float value) { var r = Rb(self); if (r != null) r.AngularVelocity = value; }
        public static void UnityEngine_Rigidbody2D__get_position_Injected(Rigidbody2D self, out Vector2 ret) { var p = Rb(self)?.GameObject.Transform.Position ?? default; ret = new Vector2(p.x, p.y); }
        public static void UnityEngine_Rigidbody2D__set_position_Injected(Rigidbody2D self, ref Vector2 value)
        {
            var r = Rb(self);
            if (r == null) return;
            var t = r.GameObject.Transform;
            var z = t.Position.z;
            if (t.Parent == null) t.LocalPosition = new Vector3(value.x, value.y, z);
            else { MathX.Invert(t.Parent.LocalToWorld, out var inv); t.LocalPosition = inv.MultiplyPoint3x4(new Vector3(value.x, value.y, z)); }
            t.Invalidate();
        }
        public static void UnityEngine_Rigidbody2D__MovePosition_Injected(Rigidbody2D self, ref Vector2 position) { var r = Rb(self); if (r != null) { r.MoveTo = position; Physics.Track(r); } }
        public static float UnityEngine_Rigidbody2D__get_rotation(Rigidbody2D self) => Rb(self)?.GameObject.Transform.Rotation.eulerAngles.z ?? 0;
        public static void UnityEngine_Rigidbody2D__set_rotation(Rigidbody2D self, float value) { var r = Rb(self); if (r != null) { var t = r.GameObject.Transform; t.LocalRotation = Quaternion.Euler(0, 0, value); t.Invalidate(); } }
        public static void UnityEngine_Rigidbody2D__MoveRotation(Rigidbody2D self, float angle) { var r = Rb(self); if (r != null) { r.RotateTo = angle; Physics.Track(r); } }
        public static float UnityEngine_Rigidbody2D__get_gravityScale(Rigidbody2D self) => Rb(self)?.GravityScale ?? 1;
        public static void UnityEngine_Rigidbody2D__set_gravityScale(Rigidbody2D self, float value) { var r = Rb(self); if (r != null) r.GravityScale = value; }
        public static RigidbodyType2D UnityEngine_Rigidbody2D__get_bodyType(Rigidbody2D self) => Rb(self)?.BodyType ?? 0;
        public static void UnityEngine_Rigidbody2D__set_bodyType(Rigidbody2D self, RigidbodyType2D value) { var r = Rb(self); if (r != null) r.BodyType = value; }
        public static bool UnityEngine_Rigidbody2D__get_simulated(Rigidbody2D self) => Rb(self)?.Simulated ?? true;
        public static void UnityEngine_Rigidbody2D__set_simulated(Rigidbody2D self, bool value) { var r = Rb(self); if (r != null) r.Simulated = value; }
        public static float UnityEngine_Rigidbody2D__get_mass(Rigidbody2D self) => Rb(self)?.Mass ?? 1;
        public static void UnityEngine_Rigidbody2D__set_mass(Rigidbody2D self, float value) { var r = Rb(self); if (r != null) r.Mass = value; }
        public static float UnityEngine_Rigidbody2D__get_drag(Rigidbody2D self) => Rb(self)?.Drag ?? 0;
        public static void UnityEngine_Rigidbody2D__set_drag(Rigidbody2D self, float value) { var r = Rb(self); if (r != null) r.Drag = value; }
        public static float UnityEngine_Rigidbody2D__get_angularDrag(Rigidbody2D self) => Rb(self)?.AngularDrag ?? 0;
        public static void UnityEngine_Rigidbody2D__set_angularDrag(Rigidbody2D self, float value) { var r = Rb(self); if (r != null) r.AngularDrag = value; }
        public static RigidbodyConstraints2D UnityEngine_Rigidbody2D__get_constraints(Rigidbody2D self) => Rb(self)?.Constraints ?? 0;
        public static void UnityEngine_Rigidbody2D__set_constraints(Rigidbody2D self, RigidbodyConstraints2D value) { var r = Rb(self); if (r != null) r.Constraints = value; }
        public static bool UnityEngine_Rigidbody2D__get_freezeRotation(Rigidbody2D self) => ((Rb(self)?.Constraints ?? 0) & RigidbodyConstraints2D.FreezeRotation) != 0;
        public static void UnityEngine_Rigidbody2D__set_freezeRotation(Rigidbody2D self, bool value)
        { var r = Rb(self); if (r != null) r.Constraints = value ? r.Constraints | RigidbodyConstraints2D.FreezeRotation : r.Constraints & ~RigidbodyConstraints2D.FreezeRotation; }
        public static bool UnityEngine_Rigidbody2D__get_fixedAngle(Rigidbody2D self) => UnityEngine_Rigidbody2D__get_freezeRotation(self);
        public static void UnityEngine_Rigidbody2D__set_fixedAngle(Rigidbody2D self, bool value) => UnityEngine_Rigidbody2D__set_freezeRotation(self, value);
        public static bool UnityEngine_Rigidbody2D__get_useFullKinematicContacts(Rigidbody2D self) => Rb(self)?.UseFullKinematicContacts ?? false;
        public static void UnityEngine_Rigidbody2D__set_useFullKinematicContacts(Rigidbody2D self, bool value) { var r = Rb(self); if (r != null) r.UseFullKinematicContacts = value; }
        public static RigidbodyInterpolation2D UnityEngine_Rigidbody2D__get_interpolation(Rigidbody2D self) => Rb(self)?.Interpolation ?? 0;
        public static void UnityEngine_Rigidbody2D__set_interpolation(Rigidbody2D self, RigidbodyInterpolation2D value) { var r = Rb(self); if (r != null) r.Interpolation = value; }
        public static RigidbodySleepMode2D UnityEngine_Rigidbody2D__get_sleepMode(Rigidbody2D self) => Rb(self)?.SleepMode ?? 0;
        public static void UnityEngine_Rigidbody2D__set_sleepMode(Rigidbody2D self, RigidbodySleepMode2D value) { var r = Rb(self); if (r != null) r.SleepMode = value; }
        public static CollisionDetectionMode2D UnityEngine_Rigidbody2D__get_collisionDetectionMode(Rigidbody2D self) => Rb(self)?.Detection ?? 0;
        public static void UnityEngine_Rigidbody2D__set_collisionDetectionMode(Rigidbody2D self, CollisionDetectionMode2D value) { var r = Rb(self); if (r != null) r.Detection = value; }
        public static bool UnityEngine_Rigidbody2D__IsSleeping(Rigidbody2D self) => false;
        public static bool UnityEngine_Rigidbody2D__IsAwake(Rigidbody2D self) => true;
        public static void UnityEngine_Rigidbody2D__Sleep(Rigidbody2D self) { }
        public static void UnityEngine_Rigidbody2D__WakeUp(Rigidbody2D self) { }
        public static void UnityEngine_Rigidbody2D__AddForce_Injected(Rigidbody2D self, ref Vector2 force, ForceMode2D mode)
        {
            var r = Rb(self);
            if (r == null || r.BodyType != RigidbodyType2D.Dynamic) return;
            r.Velocity += mode == ForceMode2D.Impulse ? force / Math.Max(1e-6f, r.Mass) : force / Math.Max(1e-6f, r.Mass) * Clock.FixedDeltaTime;
            Physics.Track(r);
        }
        public static void UnityEngine_Rigidbody2D__AddTorque(Rigidbody2D self, float torque, ForceMode2D mode) { }
        public static int UnityEngine_Rigidbody2D__get_attachedColliderCount(Rigidbody2D self)
        {
            var r = Rb(self);
            int n = 0;
            if (r != null) foreach (var o in Objects.Live()) if (o is NCollider2D c && c.Rigidbody == r) n++;
            return n;
        }
        public static int UnityEngine_Rigidbody2D__GetAttachedColliders(Rigidbody2D self, Collider2D[] results)
        {
            var r = Rb(self);
            var l = new List<NCollider2D>();
            if (r != null) foreach (var o in Objects.Live()) if (o is NCollider2D c && c.Rigidbody == r) l.Add(c);
            return Fill(l, results);
        }
        public static bool UnityEngine_Rigidbody2D__IsTouching(Rigidbody2D self, Collider2D collider)
        {
            var r = Rb(self); var b = Col(collider);
            if (r == null || b == null) return false;
            foreach (var o in Physics.TouchingOf(b)) if (o.Rigidbody == r) return true;
            return false;
        }
        public static bool UnityEngine_Rigidbody2D__IsTouchingLayers(Rigidbody2D self, int layerMask)
        {
            var r = Rb(self);
            if (r == null) return false;
            foreach (var o in Objects.Live())
                if (o is NCollider2D c && c.Rigidbody == r && UnityEngine_Collider2D__IsTouchingLayers((Collider2D)c.Managed, layerMask)) return true;
            return false;
        }
    }
}
