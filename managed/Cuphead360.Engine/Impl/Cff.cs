using System;
using System.Collections.Generic;

namespace Cuphead360.Engine
{
    /// <summary>
    /// CFF outlines (OpenType 'OTTO' fonts): the CharStrings INDEX and a
    /// Type 2 charstring interpreter producing flattened line segments.
    /// CID-keyed fonts use the first Private DICT of their FDArray.
    /// </summary>
    public sealed class Cff
    {
        readonly byte[] d;
        readonly int[] charStrings;      // offsets into d (n + 1)
        readonly int[] gsubrs, lsubrs;
        readonly int gbias, lbias;
        readonly List<int[]> fdLocal = new List<int[]>();
        readonly List<int> fdBias = new List<int>();
        readonly byte[] fdSelect;

        public Cff(byte[] data, int start)
        {
            d = data;
            int p = start + d[start + 2];               // header size
            p = Index(p, out _);                          // Name INDEX
            p = Index(p, out var topDicts);               // Top DICT INDEX
            p = Index(p, out _);                          // String INDEX
            Index(p, out gsubrs);                          // Global Subr INDEX
            gbias = Bias(gsubrs.Length - 1);
            var top = Dict(topDicts[0], topDicts[1]);
            int cs = (int)Get(top, 17, 0) + start;
            Index(cs, out charStrings);
            if (top.ContainsKey(1230) && top.ContainsKey(1236))   // ROS: CID-keyed
            {
                Index((int)top[1236][0] + start, out var fds);   // FDArray (12 36)
                for (int i = 0; i + 1 < fds.Length; i++)
                {
                    var fd = Dict(fds[i], fds[i + 1]);
                    LoadPrivate(fd, start, out var subrs, out int bias);
                    fdLocal.Add(subrs); fdBias.Add(bias);
                }
                int sel = (int)Get(top, 1237, 0) + start;       // FDSelect (12 37)
                int n = charStrings.Length - 1;
                fdSelect = new byte[n];
                if (d[sel] == 0) Array.Copy(d, sel + 1, fdSelect, 0, n);
                else if (d[sel] == 3)
                {
                    int ranges = U16(sel + 1), q = sel + 3;
                    for (int r = 0; r < ranges; r++)
                    {
                        int first = U16(q), fdi = d[q + 2], next = U16(q + 3);
                        for (int g = first; g < next && g < n; g++) fdSelect[g] = (byte)fdi;
                        q += 3;
                    }
                }
                lsubrs = new int[0];
            }
            else
            {
                LoadPrivate(top, start, out lsubrs, out lbias);
            }
        }

        void LoadPrivate(Dictionary<int, double[]> dict, int start, out int[] subrs, out int bias)
        {
            subrs = new int[] { 0 };
            bias = 107;
            if (!dict.TryGetValue(18, out var priv) || priv.Length < 2) return;
            int size = (int)priv[0], off = (int)priv[1] + start;
            var pd = Dict(off, off + size);
            if (pd.TryGetValue(19, out var so)) { Index(off + (int)so[0], out subrs); bias = Bias(subrs.Length - 1); }
        }

        static int Bias(int n) => n < 1240 ? 107 : n < 33900 ? 1131 : 32768;
        int U16(int p) => (d[p] << 8) | d[p + 1];

        int Index(int p, out int[] offsets)
        {
            int count = U16(p);
            if (count == 0) { offsets = new int[] { p + 2 }; return p + 2; }
            int osz = d[p + 2];
            int baseOff = p + 3 + (count + 1) * osz - 1;
            offsets = new int[count + 1];
            for (int i = 0; i <= count; i++)
            {
                int v = 0;
                for (int k = 0; k < osz; k++) v = (v << 8) | d[p + 3 + i * osz + k];
                offsets[i] = baseOff + v;
            }
            return offsets[count];
        }

        static double Get(Dictionary<int, double[]> dict, int key, double def) => dict.TryGetValue(key, out var v) && v.Length > 0 ? v[0] : def;

        Dictionary<int, double[]> Dict(int p, int end)
        {
            var r = new Dictionary<int, double[]>();
            var ops = new List<double>();
            while (p < end)
            {
                int b = d[p];
                if (b <= 21)
                {
                    int key = b;
                    p++;
                    if (b == 12) { key = 1200 + d[p]; p++; }
                    r[key] = ops.ToArray();   // escape operators (12 n) are 1200 + n
                    ops.Clear();
                    continue;
                }
                if (b == 28) { ops.Add((short)U16(p + 1)); p += 3; }
                else if (b == 29) { ops.Add((d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]); p += 5; }
                else if (b == 30)
                {
                    // Real number: nibbles.
                    var s = new System.Text.StringBuilder();
                    p++;
                    bool done = false;
                    while (!done && p < end)
                    {
                        int by = d[p++];
                        foreach (int nib in new[] { by >> 4, by & 15 })
                        {
                            if (nib <= 9) s.Append((char)('0' + nib));
                            else if (nib == 10) s.Append('.');
                            else if (nib == 11) s.Append('E');
                            else if (nib == 12) s.Append("E-");
                            else if (nib == 14) s.Append('-');
                            else if (nib == 15) { done = true; break; }
                        }
                    }
                    double.TryParse(s.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v);
                    ops.Add(v);
                }
                else if (b >= 32 && b <= 246) { ops.Add(b - 139); p++; }
                else if (b >= 247 && b <= 250) { ops.Add((b - 247) * 256 + d[p + 1] + 108); p += 2; }
                else if (b >= 251 && b <= 254) { ops.Add(-(b - 251) * 256 - d[p + 1] - 108); p += 2; }
                else p++;
            }
            return r;
        }

        public int GlyphCount => charStrings.Length - 1;

        // ---------------------------------------------------------------- Type 2 charstrings

        sealed class State
        {
            public readonly List<(float, float, float, float)> Segs;
            public double X, Y, StartX, StartY;
            public bool Open;
            public int Stems;
            public bool WidthSeen;
            public readonly double[] Stack = new double[48];
            public int N;
            public State(List<(float, float, float, float)> segs) { Segs = segs; }
        }

        public void Outline(int glyph, List<(float x0, float y0, float x1, float y1)> segs)
        {
            if (glyph < 0 || glyph >= GlyphCount) return;
            var st = new State(segs);
            var local = lsubrs; int bias = lbias;
            if (fdSelect != null && glyph < fdSelect.Length && fdSelect[glyph] < fdLocal.Count) { local = fdLocal[fdSelect[glyph]]; bias = fdBias[fdSelect[glyph]]; }
            Run(st, charStrings[glyph], charStrings[glyph + 1], local, bias, 0);
            Close(st);
        }

        static void Line(State s, double x, double y)
        {
            s.Segs.Add(((float)s.X, (float)s.Y, (float)x, (float)y));
            s.X = x; s.Y = y;
        }

        static void Curve(State s, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            const int steps = 8;
            double px = s.X, py = s.Y, x0 = s.X, y0 = s.Y;
            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps, u = 1 - t;
                double x = u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
                double y = u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
                s.Segs.Add(((float)px, (float)py, (float)x, (float)y));
                px = x; py = y;
            }
            s.X = x3; s.Y = y3;
        }

        static void MoveTo(State s, double x, double y)
        {
            Close(s);
            s.X = s.StartX = x; s.Y = s.StartY = y;
            s.Open = true;
        }

        static void Close(State s)
        {
            if (s.Open && (s.X != s.StartX || s.Y != s.StartY)) s.Segs.Add(((float)s.X, (float)s.Y, (float)s.StartX, (float)s.StartY));
            s.Open = false;
        }

        // Odd argument counts carry the advance width first (once per glyph).
        static int Args(State s, int expectEven)
        {
            if (!s.WidthSeen) { s.WidthSeen = true; if ((s.N & 1) != expectEven) return 1; }
            return 0;
        }

        bool Run(State s, int p, int end, int[] local, int lbias, int depth)
        {
            if (depth > 10) return true;
            while (p < end)
            {
                int b = d[p++];
                if (b >= 32 || b == 28)
                {
                    double v;
                    if (b == 28) { v = (short)U16(p); p += 2; }
                    else if (b <= 246) v = b - 139;
                    else if (b <= 250) v = (b - 247) * 256 + d[p++] + 108;
                    else if (b <= 254) v = -(b - 251) * 256 - d[p++] - 108;
                    else { v = ((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]) / 65536.0; p += 4; }
                    if (s.N < s.Stack.Length) s.Stack[s.N++] = v;
                    continue;
                }
                var a = s.Stack;
                int i;
                switch (b)
                {
                    case 1: case 3: case 18: case 23:   // hstem vstem hstemhm vstemhm
                        i = Args(s, 0);
                        s.Stems += (s.N - i) / 2; s.N = 0; break;
                    case 19: case 20:                   // hintmask cntrmask
                        i = Args(s, 0);
                        s.Stems += (s.N - i) / 2; s.N = 0;
                        p += (s.Stems + 7) / 8; break;
                    case 21:                            // rmoveto
                        i = Args(s, 0);
                        MoveTo(s, s.X + a[i], s.Y + a[i + 1]); s.N = 0; break;
                    case 22:                            // hmoveto
                        i = Args(s, 1);
                        MoveTo(s, s.X + a[i], s.Y); s.N = 0; break;
                    case 4:                             // vmoveto
                        i = Args(s, 1);
                        MoveTo(s, s.X, s.Y + a[i]); s.N = 0; break;
                    case 5:                             // rlineto
                        for (i = 0; i + 1 < s.N; i += 2) Line(s, s.X + a[i], s.Y + a[i + 1]);
                        s.N = 0; break;
                    case 6: case 7:                     // hlineto vlineto
                        {
                            bool h = b == 6;
                            for (i = 0; i < s.N; i++, h = !h) Line(s, h ? s.X + a[i] : s.X, h ? s.Y : s.Y + a[i]);
                            s.N = 0; break;
                        }
                    case 8:                             // rrcurveto
                        for (i = 0; i + 5 < s.N; i += 6)
                        {
                            double x1 = s.X + a[i], y1 = s.Y + a[i + 1], x2 = x1 + a[i + 2], y2 = y1 + a[i + 3];
                            Curve(s, x1, y1, x2, y2, x2 + a[i + 4], y2 + a[i + 5]);
                        }
                        s.N = 0; break;
                    case 24:                            // rcurveline
                        for (i = 0; i + 5 < s.N - 2; i += 6)
                        {
                            double x1 = s.X + a[i], y1 = s.Y + a[i + 1], x2 = x1 + a[i + 2], y2 = y1 + a[i + 3];
                            Curve(s, x1, y1, x2, y2, x2 + a[i + 4], y2 + a[i + 5]);
                        }
                        Line(s, s.X + a[i], s.Y + a[i + 1]); s.N = 0; break;
                    case 25:                            // rlinecurve
                        for (i = 0; i + 1 < s.N - 6; i += 2) Line(s, s.X + a[i], s.Y + a[i + 1]);
                        {
                            double x1 = s.X + a[i], y1 = s.Y + a[i + 1], x2 = x1 + a[i + 2], y2 = y1 + a[i + 3];
                            Curve(s, x1, y1, x2, y2, x2 + a[i + 4], y2 + a[i + 5]);
                        }
                        s.N = 0; break;
                    case 26:                            // vvcurveto
                        {
                            i = 0; double dx1 = 0;
                            if ((s.N & 1) == 1) dx1 = a[i++];
                            for (; i + 3 < s.N; i += 4)
                            {
                                double x1 = s.X + dx1, y1 = s.Y + a[i], x2 = x1 + a[i + 1], y2 = y1 + a[i + 2];
                                Curve(s, x1, y1, x2, y2, x2, y2 + a[i + 3]); dx1 = 0;
                            }
                            s.N = 0; break;
                        }
                    case 27:                            // hhcurveto
                        {
                            i = 0; double dy1 = 0;
                            if ((s.N & 1) == 1) dy1 = a[i++];
                            for (; i + 3 < s.N; i += 4)
                            {
                                double x1 = s.X + a[i], y1 = s.Y + dy1, x2 = x1 + a[i + 1], y2 = y1 + a[i + 2];
                                Curve(s, x1, y1, x2, y2, x2 + a[i + 3], y2); dy1 = 0;
                            }
                            s.N = 0; break;
                        }
                    case 30: case 31:                   // vhcurveto hvcurveto
                        {
                            bool h = b == 31;
                            for (i = 0; i + 3 < s.N; i += 4, h = !h)
                            {
                                bool last = i + 8 > s.N;   // the final curve may take a 5th argument
                                double extra = last && s.N - i == 5 ? a[i + 4] : 0;
                                if (h)
                                {
                                    double x1 = s.X + a[i], y1 = s.Y, x2 = x1 + a[i + 1], y2 = y1 + a[i + 2];
                                    Curve(s, x1, y1, x2, y2, x2 + extra, y2 + a[i + 3]);
                                }
                                else
                                {
                                    double x1 = s.X, y1 = s.Y + a[i], x2 = x1 + a[i + 1], y2 = y1 + a[i + 2];
                                    Curve(s, x1, y1, x2, y2, x2 + a[i + 3], y2 + extra);
                                }
                            }
                            s.N = 0; break;
                        }
                    case 10:                            // callsubr
                        {
                            int idx = (int)a[--s.N] + lbias;
                            if (idx >= 0 && idx + 1 < local.Length && Run(s, local[idx], local[idx + 1], local, lbias, depth + 1)) return true;
                            break;
                        }
                    case 29:                            // callgsubr
                        {
                            int idx = (int)a[--s.N] + gbias;
                            if (idx >= 0 && idx + 1 < gsubrs.Length && Run(s, gsubrs[idx], gsubrs[idx + 1], local, lbias, depth + 1)) return true;
                            break;
                        }
                    case 11: return false;              // return
                    case 14:                            // endchar
                        Close(s); return true;
                    case 12:
                        {
                            int e = d[p++];
                            double x0 = s.X, y0 = s.Y;
                            if (e == 35 && s.N >= 12)        // flex
                            {
                                Curve(s, x0 + a[0], y0 + a[1], x0 + a[0] + a[2], y0 + a[1] + a[3], x0 + a[0] + a[2] + a[4], y0 + a[1] + a[3] + a[5]);
                                double x1 = s.X, y1 = s.Y;
                                Curve(s, x1 + a[6], y1 + a[7], x1 + a[6] + a[8], y1 + a[7] + a[9], x1 + a[6] + a[8] + a[10], y1 + a[7] + a[9] + a[11]);
                            }
                            else if (e == 34 && s.N >= 7)    // hflex
                            {
                                Curve(s, x0 + a[0], y0, x0 + a[0] + a[1], y0 + a[2], x0 + a[0] + a[1] + a[3], y0 + a[2]);
                                double x1 = s.X;
                                Curve(s, x1 + a[4], y0 + a[2], x1 + a[4] + a[5], y0, x1 + a[4] + a[5] + a[6], y0);
                            }
                            else if (e == 36 && s.N >= 9)    // hflex1
                            {
                                Curve(s, x0 + a[0], y0 + a[1], x0 + a[0] + a[2], y0 + a[1] + a[3], x0 + a[0] + a[2] + a[4], y0 + a[1] + a[3]);
                                double x1 = s.X, y1 = s.Y;
                                Curve(s, x1 + a[5], y1, x1 + a[5] + a[6], y1 + a[7], x1 + a[5] + a[6] + a[8], y0);
                            }
                            else if (e == 37 && s.N >= 11)   // flex1
                            {
                                double dx = a[0] + a[2] + a[4] + a[6] + a[8], dy = a[1] + a[3] + a[5] + a[7] + a[9];
                                Curve(s, x0 + a[0], y0 + a[1], x0 + a[0] + a[2], y0 + a[1] + a[3], x0 + a[0] + a[2] + a[4], y0 + a[1] + a[3] + a[5]);
                                double x1 = s.X, y1 = s.Y;
                                bool horiz = Math.Abs(dx) > Math.Abs(dy);
                                Curve(s, x1 + a[6], y1 + a[7], x1 + a[6] + a[8], y1 + a[7] + a[9], horiz ? x1 + a[6] + a[8] + a[10] : x0, horiz ? y0 : y1 + a[7] + a[9] + a[10]);
                            }
                            s.N = 0; break;
                        }
                    default:
                        s.N = 0; break;
                }
            }
            return false;
        }
    }
}
