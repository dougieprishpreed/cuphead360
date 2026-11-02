using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    // Dynamic fonts and legacy UI text: what Unity's FreeType-based font
    // system and TextGenerator do natively. Fonts carry their TTF bytes
    // (m_FontData); glyphs are rasterized on demand into a per-font Alpha8
    // atlas (the font material's _MainTex), and TextGenerator lays strings
    // out the way UnityEngine.UI.Text expects (one quad per character plus a
    // final cursor quad).

    /// <summary>A TrueType font: metrics, character map, glyph outlines.</summary>
    public sealed class TrueType
    {
        readonly byte[] d;
        readonly Dictionary<string, int> tables = new Dictionary<string, int>();
        public int UnitsPerEm = 1000, Ascender, Descender, LineGap, NumGlyphs;
        int numHMetrics, locFormat, glyf, loca, hmtx;
        readonly Dictionary<int, int> cmap = new Dictionary<int, int>();
        readonly Dictionary<(int, int), int> kern = new Dictionary<(int, int), int>();

        public TrueType(byte[] data)
        {
            d = data;
            int n = U16(4);
            for (int i = 0; i < n; i++)
            {
                int r = 12 + i * 16;
                string tag = System.Text.Encoding.ASCII.GetString(d, r, 4);
                tables[tag] = (int)U32(r + 8);
            }
            int head = tables["head"];
            UnitsPerEm = U16(head + 18);
            locFormat = S16(head + 50);
            int hhea = tables["hhea"];
            Ascender = S16(hhea + 4); Descender = S16(hhea + 6); LineGap = S16(hhea + 8);
            numHMetrics = U16(hhea + 34);
            NumGlyphs = U16(tables["maxp"] + 4);
            glyf = tables.TryGetValue("glyf", out int g) ? g : -1;
            loca = tables.TryGetValue("loca", out int l) ? l : -1;
            hmtx = tables["hmtx"];
            ReadCmap(tables["cmap"]);
            if (tables.TryGetValue("kern", out int k)) ReadKern(k);
            if (glyf < 0 && tables.TryGetValue("CFF ", out int cffAt)) cff = new Cff(d, cffAt);
        }

        readonly Cff cff;

        int U16(int p) => (d[p] << 8) | d[p + 1];
        int S16(int p) => (short)U16(p);
        uint U32(int p) => (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);

        void ReadCmap(int t)
        {
            int n = U16(t + 2), best = -1, bestScore = -1;
            for (int i = 0; i < n; i++)
            {
                int pid = U16(t + 4 + i * 8), eid = U16(t + 6 + i * 8);
                int off = t + (int)U32(t + 8 + i * 8);
                int fmt = U16(off);
                int score = (fmt == 12 ? 4 : fmt == 4 ? 2 : 0) + (pid == 3 && (eid == 1 || eid == 10) ? 1 : pid == 0 ? 1 : 0);
                if ((fmt == 4 || fmt == 12) && score > bestScore) { bestScore = score; best = off; }
            }
            if (best < 0) return;
            if (U16(best) == 4)
            {
                int segX2 = U16(best + 6), ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                for (int s = 0; s < segX2 / 2; s++)
                {
                    int end = U16(ends + s * 2), start = U16(starts + s * 2), delta = S16(deltas + s * 2), ro = U16(ranges + s * 2);
                    for (int c = start; c <= end && c != 0xFFFF; c++)
                    {
                        int gi;
                        if (ro == 0) gi = (c + delta) & 0xFFFF;
                        else
                        {
                            int p = ranges + s * 2 + ro + (c - start) * 2;
                            gi = U16(p);
                            if (gi != 0) gi = (gi + delta) & 0xFFFF;
                        }
                        if (gi != 0) cmap[c] = gi;
                    }
                }
            }
            else
            {
                uint groups = U32(best + 12);
                for (uint i = 0; i < groups; i++)
                {
                    int p = best + 16 + (int)i * 12;
                    uint s = U32(p), e = U32(p + 4), gi = U32(p + 8);
                    for (uint c = s; c <= e && c - s < 0x10000; c++) cmap[(int)c] = (int)(gi + c - s);
                }
            }
        }

        void ReadKern(int t)
        {
            int n = U16(t + 2), p = t + 4;
            for (int i = 0; i < n; i++)
            {
                int len = U16(p + 2), cov = U16(p + 4);
                if ((cov >> 8) == 0 && (cov & 1) != 0)
                {
                    int pairs = U16(p + 6);
                    for (int k = 0; k < pairs; k++)
                    {
                        int q = p + 14 + k * 6;
                        kern[(U16(q), U16(q + 2))] = S16(q + 4);
                    }
                }
                p += len;
            }
        }

        public int Glyph(int c) => cmap.TryGetValue(c, out int g) ? g : 0;
        public bool Has(int c) => cmap.ContainsKey(c);
        public int Kerning(int a, int b) => kern.TryGetValue((a, b), out int v) ? v : 0;

        public int Advance(int g)
        {
            if (g >= numHMetrics) g = numHMetrics - 1;
            return U16(hmtx + g * 4);
        }

        int GlyphOffset(int g, out int len)
        {
            len = 0;
            if (glyf < 0 || g >= NumGlyphs) return -1;
            int a = locFormat == 0 ? U16(loca + g * 2) * 2 : (int)U32(loca + g * 4);
            int b = locFormat == 0 ? U16(loca + g * 2 + 2) * 2 : (int)U32(loca + g * 4 + 4);
            len = b - a;
            return len > 0 ? glyf + a : -1;
        }

        /// <summary>Glyph outline as line segments (font units), quadratics flattened.</summary>
        public void Outline(int g, List<(float x0, float y0, float x1, float y1)> segs, float m00 = 1, float m01 = 0, float m10 = 0, float m11 = 1, float dx = 0, float dy = 0, int depth = 0)
        {
            if (cff != null)
            {
                if (depth > 0) return;
                var raw = new List<(float x0, float y0, float x1, float y1)>();
                cff.Outline(g, raw);
                foreach (var (x0, y0, x1, y1) in raw)
                    segs.Add((m00 * x0 + m01 * y0 + dx, m10 * x0 + m11 * y0 + dy, m00 * x1 + m01 * y1 + dx, m10 * x1 + m11 * y1 + dy));
                return;
            }
            int p = GlyphOffset(g, out _);
            if (p < 0 || depth > 8) return;
            int nc = S16(p);
            if (nc < 0)
            {
                // Composite glyph.
                int q = p + 10;
                while (true)
                {
                    int flags = U16(q), gi = U16(q + 2);
                    q += 4;
                    float ox, oy;
                    if ((flags & 1) != 0) { ox = S16(q); oy = S16(q + 2); q += 4; }
                    else { ox = (sbyte)d[q]; oy = (sbyte)d[q + 1]; q += 2; }
                    float a = 1, b = 0, c = 0, e = 1;
                    if ((flags & 8) != 0) { a = e = F2Dot14(q); q += 2; }
                    else if ((flags & 0x40) != 0) { a = F2Dot14(q); e = F2Dot14(q + 2); q += 4; }
                    else if ((flags & 0x80) != 0) { a = F2Dot14(q); b = F2Dot14(q + 2); c = F2Dot14(q + 4); e = F2Dot14(q + 6); q += 8; }
                    // child transform: [a c; b e] then offset, composed with the parent's
                    float n00 = m00 * a + m01 * b, n01 = m00 * c + m01 * e, n10 = m10 * a + m11 * b, n11 = m10 * c + m11 * e;
                    float ndx = m00 * ox + m01 * oy + dx, ndy = m10 * ox + m11 * oy + dy;
                    Outline(gi, segs, n00, n01, n10, n11, ndx, ndy, depth + 1);
                    if ((flags & 0x20) == 0) break;
                }
                return;
            }
            var endPts = new int[nc];
            for (int i = 0; i < nc; i++) endPts[i] = U16(p + 10 + i * 2);
            int npts = nc == 0 ? 0 : endPts[nc - 1] + 1;
            int ilen = U16(p + 10 + nc * 2);
            int fp = p + 12 + nc * 2 + ilen;
            var flagsArr = new byte[npts];
            for (int i = 0; i < npts;)
            {
                byte f = d[fp++];
                flagsArr[i++] = f;
                if ((f & 8) != 0) { int rep = d[fp++]; while (rep-- > 0 && i < npts) flagsArr[i++] = f; }
            }
            var xs = new int[npts]; var ys = new int[npts];
            int v = 0;
            for (int i = 0; i < npts; i++)
            {
                byte f = flagsArr[i];
                if ((f & 2) != 0) { int dd = d[fp++]; v += (f & 16) != 0 ? dd : -dd; }
                else if ((f & 16) == 0) { v += S16(fp); fp += 2; }
                xs[i] = v;
            }
            v = 0;
            for (int i = 0; i < npts; i++)
            {
                byte f = flagsArr[i];
                if ((f & 4) != 0) { int dd = d[fp++]; v += (f & 32) != 0 ? dd : -dd; }
                else if ((f & 32) == 0) { v += S16(fp); fp += 2; }
                ys[i] = v;
            }
            (float, float) T(float x, float y) => (m00 * x + m01 * y + dx, m10 * x + m11 * y + dy);
            int start = 0;
            for (int c = 0; c < nc; c++)
            {
                int end = endPts[c], cnt = end - start + 1;
                if (cnt < 2) { start = end + 1; continue; }
                // Expand to on/off points; implicit on-points between two off-points.
                var pts = new List<(float x, float y, bool on)>();
                for (int i = 0; i < cnt; i++)
                {
                    int k = start + i;
                    bool on = (flagsArr[k] & 1) != 0;
                    var cur = (x: (float)xs[k], y: (float)ys[k]);
                    if (pts.Count > 0 && !on && !pts[pts.Count - 1].on)
                        pts.Add(((pts[pts.Count - 1].x + cur.x) / 2, (pts[pts.Count - 1].y + cur.y) / 2, true));
                    pts.Add((cur.x, cur.y, on));
                }
                if (!pts[0].on && !pts[pts.Count - 1].on) pts.Add(((pts[0].x + pts[pts.Count - 1].x) / 2, (pts[0].y + pts[pts.Count - 1].y) / 2, true));
                // Rotate so the contour starts on an on-curve point.
                int s0 = pts.FindIndex(q => q.on);
                var ring = new List<(float x, float y, bool on)>();
                for (int i = 0; i < pts.Count; i++) ring.Add(pts[(s0 + i) % pts.Count]);
                ring.Add(ring[0]);
                var prev = ring[0];
                for (int i = 1; i < ring.Count; i++)
                {
                    var q = ring[i];
                    if (q.on) { var a = T(prev.x, prev.y); var b = T(q.x, q.y); segs.Add((a.Item1, a.Item2, b.Item1, b.Item2)); prev = q; }
                    else
                    {
                        var n = ring[i + 1];
                        const int steps = 6;
                        float px = prev.x, py = prev.y;
                        for (int s = 1; s <= steps; s++)
                        {
                            float t = s / (float)steps, u = 1 - t;
                            float x = u * u * prev.x + 2 * u * t * q.x + t * t * n.x, y = u * u * prev.y + 2 * u * t * q.y + t * t * n.y;
                            var a = T(px, py); var b = T(x, y);
                            segs.Add((a.Item1, a.Item2, b.Item1, b.Item2));
                            px = x; py = y;
                        }
                        prev = n; i++;
                    }
                }
                start = end + 1;
            }
        }

        float F2Dot14(int p) => S16(p) / 16384f;
    }

    /// <summary>Anti-aliased coverage of line segments (signed-area accumulation).</summary>
    public static class Raster
    {
        public static byte[] Fill(List<(float x0, float y0, float x1, float y1)> segs, int w, int h)
        {
            var acc = new float[w * h + 2];
            foreach (var (ax, ay, bx, by) in segs) Line(acc, w, h, ax, ay, bx, by);
            var r = new byte[w * h];
            float sum = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (i % w == 0) sum = 0;
                sum += acc[i];
                r[i] = (byte)Math.Min(255, (int)(Math.Abs(sum) * 255f + 0.5f));
            }
            return r;
        }

        static void Line(float[] acc, int w, int h, float x0, float y0, float x1, float y1)
        {
            if (y0 == y1) return;
            float dir = 1;
            if (y0 > y1) { dir = -1; (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }
            float dxdy = (x1 - x0) / (y1 - y0);
            float x = x0;
            if (y0 < 0) { x -= y0 * dxdy; }
            int yEnd = Math.Min(h, (int)Math.Ceiling(y1));
            for (int y = Math.Max(0, (int)y0); y < yEnd; y++)
            {
                int line = y * w;
                float dy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                float xnext = x + dxdy * dy;
                float d = dy * dir;
                float xa = Math.Min(x, xnext), xb = Math.Max(x, xnext);
                float x0floor = (float)Math.Floor(xa);
                int x0i = (int)x0floor;
                float x1ceil = (float)Math.Ceiling(xb);
                int x1i = (int)x1ceil;
                if (x1i <= x0i + 1)
                {
                    float xmf = 0.5f * (x + xnext) - x0floor;
                    Add(acc, w, line, x0i, d - d * xmf);
                    Add(acc, w, line, x0i + 1, d * xmf);
                }
                else
                {
                    float s = 1f / (xb - xa);
                    float x0f = xa - x0floor;
                    float a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
                    float x1f = xb - x1ceil + 1;
                    float am = 0.5f * s * x1f * x1f;
                    Add(acc, w, line, x0i, d * a0);
                    if (x1i == x0i + 2) Add(acc, w, line, x0i + 1, d * (1 - a0 - am));
                    else
                    {
                        float a1 = s * (1.5f - x0f);
                        Add(acc, w, line, x0i + 1, d * (a1 - a0));
                        for (int xi = x0i + 2; xi < x1i - 1; xi++) Add(acc, w, line, xi, d * s);
                        float a2 = a1 + (x1i - x0i - 3) * s;
                        Add(acc, w, line, x1i - 1, d * (1 - a2 - am));
                    }
                    Add(acc, w, line, x1i, d * am);
                }
                x = xnext;
            }
        }

        static void Add(float[] acc, int w, int line, int x, float v)
        {
            if (x < 0) x = 0;
            if (x >= w) return;
            acc[line + x] += v;
        }
    }

    /// <summary>A dynamic font's glyph cache and atlas texture.</summary>
    public sealed class FontState
    {
        public TrueType TT;
        public Texture2D Atlas;
        public int Width = 256, Height = 256;
        public byte[] Pixels;
        public int Version;
        int penX = 1, penY = 1, rowH;
        public readonly Dictionary<(int c, int size, FontStyle style), CharacterInfo> Glyphs = new Dictionary<(int, int, FontStyle), CharacterInfo>();
        public bool Rebuilt;   // the atlas was reallocated: cached UVs are stale
        readonly UObject font;

        static readonly ConditionalWeakTable<Font, FontState> states = new ConditionalWeakTable<Font, FontState>();

        FontState(Font f) { font = f; }

        public static FontState Of(Font f)
        {
            if (f == null) return null;
            if (states.TryGetValue(f, out var s)) return s;
            s = new FontState(f);
            var src = (Objects.Of(f) as NAsset)?.Source;
            var data = Tree.Get(src?.Tree, "m_FontData");
            byte[] ttf = data as byte[] ?? (data as ChobBlob)?.Read();
            if (ttf == null && data is List<object> l) { ttf = new byte[l.Count]; for (int i = 0; i < l.Count; i++) ttf[i] = (byte)(l[i] is long v ? v : 0); }
            if (ttf != null && ttf.Length > 12)
                try { s.TT = new TrueType(ttf); } catch (Exception e) { AssetDb.Problem("font " + Objects.Of(f)?.Name + " TTF unreadable: " + e.Message); }
            s.Pixels = new byte[s.Width * s.Height];
            s.Atlas = Lifecycle.NewManaged<Texture2D>();
            Objects.Register(s.Atlas, new NAsset(), "Font Texture");
            Icalls.RuntimeTexture(s.Atlas, s.Width, s.Height, (int)TextureFormat.Alpha8);
            RuntimePixels.Set(s.Atlas, s.Pixels, s.Width, s.Height, 1, 0);
            var mat = Icalls.FontMaterial(f);
            if (mat != null) MaterialState.Of(mat).Textures[ShaderProps.Id("_MainTex")] = s.Atlas;
            states.Add(f, s);
            return s;
        }

        public float Scale(int size) => TT == null ? 0 : size / (float)TT.UnitsPerEm;

        /// <summary>The platform's default font (Unity falls back to an OS font such as Arial); set by the host.</summary>
        public static TrueType OsFallback;

        List<TrueType> fallbacks;
        List<TrueType> Fallbacks()
        {
            if (fallbacks != null) return fallbacks;
            fallbacks = new List<TrueType>();
            var src = (Objects.Of(font) as NAsset)?.Source;
            var seen = new HashSet<UObject> { font };
            void Walk(ChobObject s, int depth)
            {
                if (s == null || depth > 4) return;
                foreach (var e in Tree.List(s.Tree, "m_FallbackFonts"))
                    if (e is PPtr p && !p.IsNull && AssetDb.Resolve(s.File, p) is Font f && seen.Add(f))
                    {
                        var st = Of(f);
                        if (st?.TT != null) fallbacks.Add(st.TT);
                        Walk((Objects.Of(f) as NAsset)?.Source, depth + 1);
                    }
            }
            Walk(src, 0);
            return fallbacks;
        }
        public float Ascent(int size) => TT == null ? size * 0.8f : TT.Ascender * Scale(size);
        public float Descent(int size) => TT == null ? -size * 0.2f : TT.Descender * Scale(size);
        public float LineHeight(int size) => TT == null ? size * 1.15f : (TT.Ascender - TT.Descender + TT.LineGap) * Scale(size);

        /// <summary>Rasterize a glyph into the atlas (if needed) and return its info.</summary>
        public CharacterInfo Get(int c, int size, FontStyle style)
        {
            if (size <= 0) size = 16;
            if (Glyphs.TryGetValue((c, size, style), out var ci)) return ci;
            ci = new CharacterInfo { index = c, size = size, style = style };
            if (TT == null) { Glyphs[(c, size, style)] = ci; return ci; }
            // A character the font lacks comes from its fallback fonts (m_FallbackFonts), as in Unity.
            var tt = TT;
            if (!tt.Has(c) && c > 32)
            {
                tt = null;
                foreach (var fb in Fallbacks()) if (fb.Has(c)) { tt = fb; break; }
                if (tt == null && OsFallback != null && OsFallback.Has(c)) tt = OsFallback;   // Unity's last resort: an OS font
                if (tt == null)
                {
                    AssetDb.Problem("character U+" + ((int)c).ToString("X4") + " in no font of the " + Objects.Of(font)?.Name + " fallback chain (Unity would use an OS font)");
                    Glyphs[(c, size, style)] = ci;
                    return ci;
                }
            }
            int g = tt.Glyph(c);
            float sc = size / (float)tt.UnitsPerEm;
            ci.width = (float)Math.Round(tt.Advance(g) * sc);
            var segs = new List<(float, float, float, float)>();
            tt.Outline(g, segs);
            if (segs.Count > 0)
            {
                float minx = float.MaxValue, miny = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue;
                foreach (var (x0, y0, x1, y1) in segs)
                {
                    minx = Math.Min(minx, Math.Min(x0, x1)); maxx = Math.Max(maxx, Math.Max(x0, x1));
                    miny = Math.Min(miny, Math.Min(y0, y1)); maxy = Math.Max(maxy, Math.Max(y0, y1));
                }
                int ix0 = (int)Math.Floor(minx * sc), iy0 = (int)Math.Floor(miny * sc);
                int ix1 = (int)Math.Ceiling(maxx * sc), iy1 = (int)Math.Ceiling(maxy * sc);
                int gw = Math.Max(1, ix1 - ix0), gh = Math.Max(1, iy1 - iy0);
                // Bitmap rows top-down: y flipped.
                var px = new List<(float, float, float, float)>(segs.Count);
                foreach (var (x0, y0, x1, y1) in segs) px.Add((x0 * sc - ix0, iy1 - y0 * sc, x1 * sc - ix0, iy1 - y1 * sc));
                var bmp = Raster.Fill(px, gw, gh);
                var at = Place(gw, gh);
                for (int y = 0; y < gh; y++)
                    Buffer.BlockCopy(bmp, y * gw, Pixels, (at.y + y) * Width + at.x, gw);
                Version++;
                RuntimePixels.Set(Atlas, Pixels, Width, Height, 1, Version);
                // vert: x = minX, y = maxY, width, height = -(glyph height) (Unity's legacy layout)
                ci.vert = new Rect(ix0, iy1, gw, -gh);
                // uv: atlas rows are stored top-down; Unity's uv origin is bottom-left.
                float u0 = at.x / (float)Width, u1 = (at.x + gw) / (float)Width;
                float vTop = 1f - at.y / (float)Height, vBot = 1f - (at.y + gh) / (float)Height;
                ci.uv = new Rect(u0, vTop, u1 - u0, vBot - vTop);
                ci.flipped = false;
            }
            Glyphs[(c, size, style)] = ci;
            return ci;
        }

        (int x, int y) Place(int w, int h)
        {
            while (true)
            {
                if (penX + w + 1 > Width) { penX = 1; penY += rowH + 1; rowH = 0; }
                if (penY + h + 1 <= Height && penX + w + 1 <= Width)
                {
                    var r = (penX, penY);
                    penX += w + 1; rowH = Math.Max(rowH, h);
                    return r;
                }
                Grow();
            }
        }

        void Grow()
        {
            // Unity rebuilds the font texture bigger and re-renders the requested glyphs.
            var keep = new List<(int c, int size, FontStyle style)>(Glyphs.Keys);
            if (Width <= Height) Width *= 2; else Height *= 2;
            Pixels = new byte[Width * Height];
            Glyphs.Clear();
            penX = 1; penY = 1; rowH = 0;
            Icalls.RuntimeTexture(Atlas, Width, Height, (int)TextureFormat.Alpha8);
            foreach (var k in keep) Get(k.c, k.size, k.style);
            if (!Rebuilt) rebuilt.Add(this);
            Rebuilt = true;
        }

        /// <summary>After a rebuild Unity tells the UI (Font.textureRebuilt) so texts regenerate.</summary>
        static readonly List<FontState> rebuilt = new List<FontState>();

        public static void NotifyRebuilds()
        {
            if (rebuilt.Count == 0) return;
            var list = rebuilt.ToArray();
            rebuilt.Clear();
            foreach (var s in list)
            {
                s.Rebuilt = false;
                try { Font.InvokeTextureRebuilt_Internal((Font)s.font); }
                catch (Exception e) { Exceptions.Report(e, null, "Font.textureRebuilt"); }
            }
        }
    }

    /// <summary>CPU pixels of textures made at run time (font atlases), for render backends.</summary>
    public static class RuntimePixels
    {
        public sealed class Entry { public byte[] Data; public int W, H, Channels, Version; }
        static readonly ConditionalWeakTable<Texture, Entry> table = new ConditionalWeakTable<Texture, Entry>();
        public static void Set(Texture t, byte[] data, int w, int h, int channels, int version)
        {
            var e = table.GetValue(t, _ => new Entry());
            e.Data = data; e.W = w; e.H = h; e.Channels = channels; e.Version = version;
        }
        public static Entry Get(Texture t) => t != null && table.TryGetValue(t, out var e) ? e : null;
    }

    /// <summary>TextGenerator's native state: the generated vertices, characters and lines.</summary>
    public sealed class TextGenState
    {
        public readonly List<UIVertex> Verts = new List<UIVertex>();
        public readonly List<UICharInfo> Chars = new List<UICharInfo>();
        public readonly List<UILineInfo> Lines = new List<UILineInfo>();
        public Rect Extents;
        public int FontSizeUsed;

        static readonly ConditionalWeakTable<TextGenerator, TextGenState> states = new ConditionalWeakTable<TextGenerator, TextGenState>();
        public static TextGenState Of(TextGenerator g) => states.GetValue(g, _ => new TextGenState());

        struct Run { public int Start, End; public float Width; }

        sealed class Glyph
        {
            public char C;
            public bool Visible;   // a printable character (not a tag, not a newline)
            public Color32 Color;
            public int Size;
            public FontStyle Style;
            public CharacterInfo Info;
            public float Advance;
        }

        public bool Populate(string str, Font font, Color color, int fontSize, float scaleFactor, float lineSpacing, FontStyle style, bool richText,
            bool bestFit, int minSize, int maxSize, int vOverflow, int hOverflow, TextAnchor anchor, float extX, float extY, float pivotX, float pivotY)
        {
            Verts.Clear(); Chars.Clear(); Lines.Clear();
            str = str ?? "";
            var fs = FontState.Of(font);
            if (fs == null) return false;
            if (scaleFactor <= 0) scaleFactor = 1;
            int size = fontSize;
            if (bestFit)
            {
                // Largest size between min and max at which the text fits the rect.
                int lo = minSize, hi = Math.Max(minSize, maxSize), best = lo;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (Layout(str, fs, color, mid, scaleFactor, lineSpacing, style, richText, hOverflow, extX, out float h, out bool fitsW, measureOnly: true) && h <= extY + 0.01f && fitsW)
                    { best = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                size = best;
            }
            FontSizeUsed = size;
            Layout(str, fs, color, size, scaleFactor, lineSpacing, style, richText, hOverflow, extX, out _, out _, measureOnly: false, anchor: anchor, extY: extY, pivotX: pivotX, pivotY: pivotY, vOverflow: vOverflow);
            FontState.NotifyRebuilds();
            return true;
        }

        bool Layout(string str, FontState fs, Color color, int size, float scale, float lineSpacing, FontStyle style, bool rich, int hOverflow, float extX,
            out float totalHeight, out bool fitsWidth, bool measureOnly, TextAnchor anchor = TextAnchor.UpperLeft, float extY = 0, float pivotX = 0.5f, float pivotY = 0.5f, int vOverflow = 1)
        {
            // Parse into glyphs (rich text tags become invisible zero-width entries).
            var glyphs = new List<Glyph>(str.Length);
            var colors = new Stack<Color32>(); var sizes = new Stack<int>(); var styles = new Stack<FontStyle>();
            Color32 col = color; int sz = size; FontStyle st = style;
            for (int i = 0; i < str.Length; i++)
            {
                char c = str[i];
                if (rich && c == '<' && TryTag(str, i, out int tagEnd, out string tag, out string val, out bool closing))
                {
                    if (tag == "color") { if (closing) { if (colors.Count > 0) col = colors.Pop(); } else { colors.Push(col); col = ParseColor(val, col); } }
                    else if (tag == "size") { if (closing) { if (sizes.Count > 0) sz = sizes.Pop(); } else { sizes.Push(sz); if (int.TryParse(val, out int nsz)) sz = nsz; } }
                    else if (tag == "b") { if (closing) { if (styles.Count > 0) st = styles.Pop(); } else { styles.Push(st); st |= FontStyle.Bold; } }
                    else if (tag == "i") { if (closing) { if (styles.Count > 0) st = styles.Pop(); } else { styles.Push(st); st |= FontStyle.Italic; } }
                    for (int k = i; k <= tagEnd; k++) glyphs.Add(new Glyph { C = str[k], Visible = false, Color = col, Size = sz, Style = st });
                    i = tagEnd;
                    continue;
                }
                var g = new Glyph { C = c, Visible = c != '\n' && c != '\r', Color = col, Size = sz, Style = st };
                if (g.Visible)
                {
                    int px = Math.Max(1, (int)Math.Round(sz * scale));
                    g.Info = fs.Get(c, px, FontStyle.Normal);
                    g.Advance = g.Info.width / scale;
                }
                glyphs.Add(g);
            }
            // Lines, wrapping at spaces when the rect is too narrow.
            float lineH = fs.LineHeight(size) * lineSpacing;
            var lines = new List<Run>();
            int ls = 0; float w = 0; int lastSpace = -1; float wAtSpace = 0;
            fitsWidth = true;
            for (int i = 0; i < glyphs.Count; i++)
            {
                var g = glyphs[i];
                if (g.C == '\n') { lines.Add(new Run { Start = ls, End = i, Width = w }); ls = i + 1; w = 0; lastSpace = -1; continue; }
                if (!g.Visible) continue;
                if (g.C == ' ') { lastSpace = i; wAtSpace = w; }
                if (hOverflow == 0 && w + g.Advance > extX + 0.01f && w > 0)
                {
                    if (lastSpace > ls) { lines.Add(new Run { Start = ls, End = lastSpace, Width = wAtSpace }); ls = lastSpace + 1; w = 0; for (int k = ls; k <= i; k++) if (glyphs[k].Visible) w += glyphs[k].Advance; lastSpace = -1; continue; }
                    lines.Add(new Run { Start = ls, End = i, Width = w }); ls = i; w = 0; lastSpace = -1;
                    if (g.Advance > extX) fitsWidth = false;
                }
                w += g.Advance;
            }
            lines.Add(new Run { Start = ls, End = glyphs.Count, Width = w });
            foreach (var r in lines) if (r.Width > extX + 0.01f) fitsWidth = hOverflow == 1;
            totalHeight = lines.Count * lineH;
            if (measureOnly) return true;

            // Placement inside the rect (origin at the pivot).
            float left = -pivotX * extX, top = (1 - pivotY) * extY;
            int ah = (int)anchor % 3, av = (int)anchor / 3;
            float blockTop = av == 0 ? top : av == 1 ? top - (extY - totalHeight) / 2 : top - (extY - totalHeight);
            float ascent = fs.Ascent(size);
            float lineGap = lineH - fs.LineHeight(size);
            var verts = new UIVertex[(glyphs.Count + 1) * 4];
            var chars = new UICharInfo[glyphs.Count + 1];
            int li = 0;
            float cursorY = blockTop;
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (var r in lines)
            {
                float x = ah == 0 ? left : ah == 1 ? left + (extX - r.Width) / 2 : left + extX - r.Width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x + r.Width);
                // Unity puts the extra line spacing above each line, the first included
                // (spacing under 1 clips the first line's top), so the block moves down.
                float baseline = cursorY - lineGap - ascent;
                Lines.Add(new UILineInfo { startCharIdx = r.Start, height = (int)Math.Round(lineH), topY = cursorY, leading = lineGap });
                for (int i = r.Start; i < r.End; i++)
                {
                    var g = glyphs[i];
                    chars[i] = new UICharInfo { cursorPos = new Vector2(x, cursorY), charWidth = g.Visible ? g.Advance : 0 };
                    if (g.Visible && g.Info.uv.width != 0)
                    {
                        var ci = g.Info;
                        float x0 = x + ci.vert.x / scale, y0 = baseline + ci.vert.y / scale;
                        float x1 = x0 + ci.vert.width / scale, y1 = y0 + ci.vert.height / scale;
                        var uv = ci.uv;
                        verts[i * 4 + 0] = V(x0, y0, uv.xMin, uv.yMin, g.Color);
                        verts[i * 4 + 1] = V(x1, y0, uv.xMax, uv.yMin, g.Color);
                        verts[i * 4 + 2] = V(x1, y1, uv.xMax, uv.yMax, g.Color);
                        verts[i * 4 + 3] = V(x0, y1, uv.xMin, uv.yMax, g.Color);
                    }
                    else for (int k = 0; k < 4; k++) verts[i * 4 + k] = V(x, baseline, 0, 0, new Color32(0, 0, 0, 0));
                    if (g.Visible) x += g.Advance;
                }
                // The break character (space/newline) ending a wrapped line.
                if (r.End < glyphs.Count && chars[r.End].charWidth == 0) chars[r.End] = new UICharInfo { cursorPos = new Vector2(x, cursorY) };
                cursorY -= lineH;
                li++;
            }
            for (int k = 0; k < 4; k++) verts[glyphs.Count * 4 + k] = V(0, 0, 0, 0, new Color32(0, 0, 0, 0));
            chars[glyphs.Count] = new UICharInfo { cursorPos = new Vector2(left, cursorY) };
            Verts.AddRange(verts);
            Chars.AddRange(chars);
            if (minX > maxX) { minX = maxX = left; }
            Extents = new Rect(minX, blockTop - totalHeight, maxX - minX, totalHeight);
            return true;
        }

        static UIVertex V(float x, float y, float u, float v, Color32 c)
            => new UIVertex { position = new Vector3(x, y, 0), color = c, uv0 = new Vector2(u, v), normal = Vector3.back, tangent = new Vector4(1, 0, 0, -1) };

        static bool TryTag(string s, int i, out int end, out string tag, out string val, out bool closing)
        {
            end = s.IndexOf('>', i); tag = val = ""; closing = false;
            if (end < 0) return false;
            string body = s.Substring(i + 1, end - i - 1);
            if (body.StartsWith("/")) { closing = true; body = body.Substring(1); }
            int eq = body.IndexOf('=');
            tag = (eq >= 0 ? body.Substring(0, eq) : body).Trim().ToLowerInvariant();
            val = eq >= 0 ? body.Substring(eq + 1).Trim().Trim('"') : "";
            return tag == "color" || tag == "size" || tag == "b" || tag == "i" || tag == "material" || tag == "quad";
        }

        static Color32 ParseColor(string v, Color32 cur)
        {
            if (v.StartsWith("#"))
            {
                string h = v.Substring(1);
                if (uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint n))
                {
                    if (h.Length == 6) return new Color32((byte)(n >> 16), (byte)(n >> 8), (byte)n, 255);
                    if (h.Length == 8) return new Color32((byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);
                }
                return cur;
            }
            switch (v.ToLowerInvariant())
            {
                case "red": return new Color32(255, 0, 0, 255); case "green": return new Color32(0, 128, 0, 255); case "blue": return new Color32(0, 0, 255, 255);
                case "white": return new Color32(255, 255, 255, 255); case "black": return new Color32(0, 0, 0, 255); case "yellow": return new Color32(255, 255, 0, 255);
                case "orange": return new Color32(255, 165, 0, 255); case "grey": case "gray": return new Color32(128, 128, 128, 255);
                default: return cur;
            }
        }
    }

    public static partial class Icalls
    {
        public static Material FontMaterial(Font f)
        {
            if (Props.TryGet<Material>(f, "material", out var m)) return m;
            var src = (Objects.Of(f) as NAsset)?.Source;
            m = src == null ? null : AssetDb.Resolve(src.File, Tree.Ptr(src.Tree, "m_DefaultMaterial")) as Material;
            if (m == null)
            {
                m = Lifecycle.NewManaged<Material>();
                Objects.Register(m, new NObject(), "Font Material");
                MaterialState.Set(m, new MaterialState { Shader = Shaders.Find("UI/Default Font") ?? Shaders.Find("GUI/Text Shader") });
            }
            Props.Set(f, "material", m, "engine Font.material");
            return m;
        }

        public static Material UnityEngine_Font__get_material(Font self) { var m = FontMaterial(self); FontState.Of(self); return m; }
        public static void UnityEngine_Font__set_material(Font self, Material value) => Props.Set(self, "material", value, "engine Font.material");
        public static bool UnityEngine_Font__get_dynamic(Font self) => FontState.Of(self)?.TT != null;
        public static bool UnityEngine_Font__HasCharacter(Font self, char c) => FontState.Of(self)?.TT?.Has(c) ?? false;
        public static string[] UnityEngine_Font__get_fontNames(Font self)
        {
            var l = Tree.List((Objects.Of(self) as NAsset)?.Source?.Tree, "m_FontNames");
            return l.ConvertAll(x => x as string ?? "").ToArray();
        }
        public static int UnityEngine_Font__get_fontSize(Font self) => (int)Tree.Float((Objects.Of(self) as NAsset)?.Source?.Tree, "m_FontSize", 16);
        public static int UnityEngine_Font__get_ascent(Font self) { var s = FontState.Of(self); return (int)Math.Round(s?.Ascent(UnityEngine_Font__get_fontSize(self)) ?? 0); }
        public static int UnityEngine_Font__get_lineHeight(Font self) { var s = FontState.Of(self); return (int)Math.Round(s?.LineHeight(UnityEngine_Font__get_fontSize(self)) ?? 0); }
        public static void UnityEngine_Font__RequestCharactersInTexture(Font self, string characters, int size, FontStyle style)
        {
            var s = FontState.Of(self);
            if (s == null || characters == null) return;
            if (size == 0) size = UnityEngine_Font__get_fontSize(self);
            foreach (char c in characters) s.Get(c, size, style);
            FontState.NotifyRebuilds();
        }
        public static bool UnityEngine_Font__GetCharacterInfo(Font self, char ch, out CharacterInfo info, int size, FontStyle style)
        {
            info = default;
            var s = FontState.Of(self);
            if (s == null) return false;
            if (size == 0) size = UnityEngine_Font__get_fontSize(self);
            if (!s.Glyphs.TryGetValue((ch, size, style), out info)) return false;
            return true;
        }
        public static CharacterInfo[] UnityEngine_Font__get_characterInfo(Font self) => FontState.Of(self) is FontState fs ? new List<CharacterInfo>(fs.Glyphs.Values).ToArray() : new CharacterInfo[0];
        public static void UnityEngine_Font__set_characterInfo(Font self, CharacterInfo[] value) { }

        public static void UnityEngine_TextGenerator__Init(TextGenerator self) => TextGenState.Of(self);
        public static void UnityEngine_TextGenerator__Dispose_cpp(TextGenerator self) { }
        public static bool UnityEngine_TextGenerator__INTERNAL_CALL_Populate_Internal_cpp(TextGenerator self, string str, Font font, ref Color color, int fontSize, float scaleFactor,
            float lineSpacing, FontStyle style, bool richText, bool resizeTextForBestFit, int resizeTextMinSize, int resizeTextMaxSize, int verticalOverFlow, int horizontalOverflow,
            bool updateBounds, TextAnchor anchor, float extentsX, float extentsY, float pivotX, float pivotY, bool generateOutOfBounds, bool alignByGeometry, out uint error)
        {
            error = 0;
            if (font == null) { error = 4; return false; }
            return TextGenState.Of(self).Populate(str, font, color, fontSize, scaleFactor, lineSpacing, style, richText, resizeTextForBestFit, resizeTextMinSize, resizeTextMaxSize,
                verticalOverFlow, horizontalOverflow, anchor, extentsX, extentsY, pivotX, pivotY);
        }
        public static void UnityEngine_TextGenerator__INTERNAL_get_rectExtents(TextGenerator self, out Rect value) => value = TextGenState.Of(self).Extents;
        public static int UnityEngine_TextGenerator__get_vertexCount(TextGenerator self) => TextGenState.Of(self).Verts.Count;
        public static void UnityEngine_TextGenerator__GetVerticesInternal(TextGenerator self, object vertices) { if (vertices is List<UIVertex> l) { l.Clear(); l.AddRange(TextGenState.Of(self).Verts); } }
        public static UIVertex[] UnityEngine_TextGenerator__GetVerticesArray(TextGenerator self) => TextGenState.Of(self).Verts.ToArray();
        public static int UnityEngine_TextGenerator__get_characterCount(TextGenerator self) => TextGenState.Of(self).Chars.Count;
        public static void UnityEngine_TextGenerator__GetCharactersInternal(TextGenerator self, object characters) { if (characters is List<UICharInfo> l) { l.Clear(); l.AddRange(TextGenState.Of(self).Chars); } }
        public static UICharInfo[] UnityEngine_TextGenerator__GetCharactersArray(TextGenerator self) => TextGenState.Of(self).Chars.ToArray();
        public static int UnityEngine_TextGenerator__get_lineCount(TextGenerator self) => TextGenState.Of(self).Lines.Count;
        public static void UnityEngine_TextGenerator__GetLinesInternal(TextGenerator self, object lines) { if (lines is List<UILineInfo> l) { l.Clear(); l.AddRange(TextGenState.Of(self).Lines); } }
        public static UILineInfo[] UnityEngine_TextGenerator__GetLinesArray(TextGenerator self) => TextGenState.Of(self).Lines.ToArray();
        public static int UnityEngine_TextGenerator__get_fontSizeUsedForBestFit(TextGenerator self) => TextGenState.Of(self).FontSizeUsed;
    }
}
