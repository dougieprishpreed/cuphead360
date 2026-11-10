using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Cuphead360.Engine;

namespace Cuphead360.Player
{
    /// <summary>
    /// The console's 64-bit check at start-up (T64 lines): .NET's 64-bit number code (parsing and formatting
    /// longs, ulongs, doubles and floats, Math's 64-bit helpers, TimeSpan, IdText) against values worked out by
    /// hand. The build with rarely-run functions compiled -Os stopped at start-up in that code (hw31, a bare
    /// "Unhandled exception."). The test build (cuphead360_test.xex: console/xsdk's CH360_TEST64 with the C
    /// stage's --cold-os) logs every result (--test64 1) and stops before the game reads the saves, so the
    /// pattern of wrong values can name what -Os gets wrong. Lines go to the host's log through ch360.log_line,
    /// past the game's console output (whose code may be -Os too), with values written in hex from 32-bit
    /// halves; these methods are new since the hot list was sampled, so the C stage keeps them -O2. Play
    /// builds run it quietly: failures and a count.
    /// </summary>
    static unsafe class Test64
    {
        [DllImport("ch360", EntryPoint = "log_line"), WasmImportLinkage]
        static extern void LogLine(byte* text, int length);

        /// <summary>Lines through the host's log (the console builds; Run sets it).</summary>
        internal static bool HostLog;
        static bool verbose;
        static int checks, failed;

        static readonly string[] Longs = {
            "0", "1", "-1", "9", "10", "2147483647", "2147483648", "-2147483648", "-2147483649", "4294967295",
            "4294967296", "4294967297", "-4294967295", "-4294967296", "1000000000000", "5848540871337674781",
            "-2592357432173453771", "9223372036854775807", "-9223372036854775808" };
        static readonly ulong[] LongBits = {
            0x0, 0x1, 0xffffffff_ffffffff, 0x9, 0xa, 0x7fffffff, 0x80000000, 0xffffffff_80000000, 0xffffffff_7fffffff,
            0xffffffff, 0x1_00000000, 0x1_00000001, 0xffffffff_00000001, 0xffffffff_00000000, 0xe8_d4a51000,
            0x512a30ef_826f881d, 0xdc0618a6_7c751e35, 0x7fffffff_ffffffff, 0x80000000_00000000 };
        static readonly string[] LongHex = {
            "0", "1", "ffffffffffffffff", "9", "a", "7fffffff", "80000000", "ffffffff80000000", "ffffffff7fffffff",
            "ffffffff", "100000000", "100000001", "ffffffff00000001", "ffffffff00000000", "e8d4a51000",
            "512a30ef826f881d", "dc0618a67c751e35", "7fffffffffffffff", "8000000000000000" };
        static readonly string[] ULongs = { "0", "4294967295", "4294967296", "18446744069414584320", "18446744073709551615" };
        static readonly ulong[] ULongBits = { 0x0, 0xffffffff, 0x1_00000000, 0xffffffff_00000000, 0xffffffff_ffffffff };

        /// <summary>The checks; with <paramref name="all"/> every result is logged, otherwise only failures.
        /// Returns how many failed.</summary>
        internal static int Run(bool all)
        {
            verbose = all;
            checks = failed = 0;
            if (all) Line("T64 start: every result (ok/FAIL, value in hex as high_low)");
            for (int i = 0; i < Longs.Length; i++)
            {
                string s = Longs[i];
                ulong want = LongBits[i];
                try
                {
                    long v;
                    bool ok = long.TryParse(s, out v);
                    Check("long.TryParse(" + s + ") returned", ok ? 1UL : 0UL, 1UL);
                    Check("long.TryParse(" + s + ")", (ulong)v, want);
                }
                catch (Exception e) { Threw("long.TryParse(" + s + ")", e); }
                try { Check("long.Parse(" + s + ")", (ulong)long.Parse(s), want); }
                catch (Exception e) { Threw("long.Parse(" + s + ")", e); }
                try { Check("(" + Hex(want) + ").ToString()", ((long)want).ToString(), s); }
                catch (Exception e) { Threw("(" + Hex(want) + ").ToString()", e); }
                try { Check("(" + Hex(want) + ").ToString(x)", ((long)want).ToString("x"), LongHex[i]); }
                catch (Exception e) { Threw("(" + Hex(want) + ").ToString(x)", e); }
                try { Check("IdText.Of(" + Hex(want) + ")", IdText.Of((long)want), s); }
                catch (Exception e) { Threw("IdText.Of(" + Hex(want) + ")", e); }
            }
            for (int i = 0; i < ULongs.Length; i++)
            {
                string s = ULongs[i];
                ulong want = ULongBits[i];
                try { Check("ulong.Parse(" + s + ")", ulong.Parse(s), want); }
                catch (Exception e) { Threw("ulong.Parse(" + s + ")", e); }
                try { Check("(ulong " + Hex(want) + ").ToString()", want.ToString(), s); }
                catch (Exception e) { Threw("(ulong " + Hex(want) + ").ToString()", e); }
            }
            // 32-bit ones run at full speed in every build (sampled hot): the control.
            try { Check("int.Parse(-2147483648)", (ulong)(uint)int.Parse("-2147483648"), 0x80000000); }
            catch (Exception e) { Threw("int.Parse(-2147483648)", e); }
            try { Check("(int.MinValue).ToString()", int.MinValue.ToString(), "-2147483648"); }
            catch (Exception e) { Threw("(int.MinValue).ToString()", e); }

            long two31 = (long)LongBits[6], neg2_32 = (long)LongBits[13], mixed = (long)LongBits[12];
            try
            {
                Check("Math.Sign(2^31)", (ulong)(long)Math.Sign(two31), 1);
                Check("Math.Sign(-2^32)", (ulong)(long)Math.Sign(neg2_32), 0xffffffff_ffffffff);
                Check("Math.Sign(-2^32+1)", (ulong)(long)Math.Sign(mixed), 0xffffffff_ffffffff);
                Check("Math.Sign(0)", (ulong)(long)Math.Sign((long)LongBits[0]), 0);
                Check("Math.Abs(-2^32)", (ulong)Math.Abs(neg2_32), 0x1_00000000);
                Check("(2^31).CompareTo(-2^32)", (ulong)(long)two31.CompareTo(neg2_32), 1);
                Check("(-2^32).CompareTo(2^31)", (ulong)(long)neg2_32.CompareTo(two31), 0xffffffff_ffffffff);
                Check("Math.Max(2^32, 2^32-1)", Math.Max(ULongBits[2], ULongBits[1]), 0x1_00000000);
                Check("Math.Min(2^64-2^32, 2^32)", Math.Min(ULongBits[3], ULongBits[2]), 0x1_00000000);
                long q = Math.DivRem(neg2_32, 16, out long r);
                Check("Math.DivRem(-2^32, 16)", (ulong)q, 0xffffffff_f0000000);
                Check("Math.DivRem(-2^32, 16) remainder", (ulong)r, 0);
                ulong high = Math.BigMul(ULongBits[4], ULongBits[4], out ulong low);
                Check("Math.BigMul(2^64-1, 2^64-1) high", high, 0xffffffff_fffffffe);
                Check("Math.BigMul(2^64-1, 2^64-1) low", low, 1);
            }
            catch (Exception e) { Threw("Math", e); }

            try { Check("double.Parse(3.14159)", (ulong)BitConverter.DoubleToInt64Bits(double.Parse("3.14159")), 0x400921f9_f01b866e); }
            catch (Exception e) { Threw("double.Parse(3.14159)", e); }
            try { Check("double.Parse(1E+300)", (ulong)BitConverter.DoubleToInt64Bits(double.Parse("1E+300")), 0x7e37e43c_8800759c); }
            catch (Exception e) { Threw("double.Parse(1E+300)", e); }
            try { Check("float.Parse(0.1)", (ulong)(uint)BitConverter.SingleToInt32Bits(float.Parse("0.1")), 0x3dcccccd); }
            catch (Exception e) { Threw("float.Parse(0.1)", e); }
            try { Check("(3.14159).ToString()", BitConverter.Int64BitsToDouble(0x400921f9_f01b866e).ToString(), "3.14159"); }
            catch (Exception e) { Threw("(3.14159).ToString()", e); }
            try
            {
                double a = BitConverter.Int64BitsToDouble(0x3fb99999_9999999a), b = BitConverter.Int64BitsToDouble(0x3fc99999_9999999a);
                Check("(0.1+0.2).ToString()", (a + b).ToString(), "0.30000000000000004");
            }
            catch (Exception e) { Threw("(0.1+0.2).ToString()", e); }
            try { Check("(1E+300).ToString()", BitConverter.Int64BitsToDouble(0x7e37e43c_8800759c).ToString(), "1E+300"); }
            catch (Exception e) { Threw("(1E+300).ToString()", e); }
            try { Check("(-123456789.125).ToString()", (-123456789.125).ToString(), "-123456789.125"); }
            catch (Exception e) { Threw("(-123456789.125).ToString()", e); }
            try { Check("(1.5f).ToString()", (1.5f).ToString(), "1.5"); }
            catch (Exception e) { Threw("(1.5f).ToString()", e); }
            try { Check("TimeSpan(36612345678).ToString()", new TimeSpan(36612345678L).ToString(), "01:01:01.2345678"); }
            catch (Exception e) { Threw("TimeSpan(36612345678).ToString()", e); }

            if (all || failed > 0) Line("T64 " + (failed == 0 ? "ok" : "FAIL") + ": " + checks + " checks, " + failed + " wrong");
            return failed;
        }

        /// <summary>
        /// The test build's probes (T64 probe lines): what start-up code does, each result printed rather than
        /// checked, so the console's lines are compared with the PC's (host_native prints the same ones; a line
        /// that differs names the broken code). The second -O2/-Os test (hw32) passed every 64-bit check and
        /// stopped in the fallback font's load (KeyNotFoundException 'head'), so the font's load comes first,
        /// taken apart, then the library code start-up leans on. Values are printed by this class's own -O2 code.
        /// </summary>
        internal static void Probes(string data)
        {
            string arial = System.IO.Path.Combine(data, "fonts", "arial.ttf");
            byte[] font = null;
            Probe("font bytes", () =>
            {
                font = System.IO.File.ReadAllBytes(arial);
                return "len " + Hex((ulong)font.Length) + " fnv " + Hex(Fnv(font, 0, font.Length)) + " head " + HexBytes(font, 0, 16);
            });
            if (font == null || font.Length < 12) return;
            int n = (font[4] << 8) | font[5];
            Probe("font directory", () =>
            {
                var sb = new char[n * 9];
                for (int i = 0; i < n; i++)
                    for (int k = 0; k < 4; k++)
                    {
                        byte b = font[12 + i * 16 + k];
                        sb[i * 9 + k * 2] = Nibble((uint)b >> 4);
                        sb[i * 9 + k * 2 + 1] = Nibble(b);
                        if (k == 3) sb[i * 9 + 8] = ' ';
                    }
                return "n " + Hex((ulong)n) + " tags " + new string(sb);
            });
            var tags = new string[n];
            Probe("ASCII.GetString tags", () =>
            {
                string all = "";
                for (int i = 0; i < n; i++)
                {
                    tags[i] = System.Text.Encoding.ASCII.GetString(font, 12 + i * 16, 4);
                    all += Chars(tags[i]) + " ";
                }
                return all;
            });
            Probe("UTF8/Latin1.GetString tag 0", () => Chars(System.Text.Encoding.UTF8.GetString(font, 12, 4)) + " " + Chars(System.Text.Encoding.Latin1.GetString(font, 12, 4)));
            Probe("Dictionary of GetString tags", () => Lookups(tags));
            Probe("Dictionary of hand-made tags", () =>
            {
                var own = new string[n];
                for (int i = 0; i < n; i++)
                {
                    var c = new char[4];
                    for (int k = 0; k < 4; k++) c[k] = (char)font[12 + i * 16 + k];
                    own[i] = new string(c);
                }
                return Lookups(own);
            });
            Probe("TrueType", () => { var t = new TrueType(font); return "upem " + Hex((ulong)t.UnitsPerEm) + " glyphs " + Hex((ulong)t.NumGlyphs); });

            Probe("FileStream seek and read", () =>
            {
                using var fs = new System.IO.FileStream(arial, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                fs.Seek(100, System.IO.SeekOrigin.Begin);
                var b = new byte[16];
                int got = fs.Read(b, 0, 16);
                return "len " + Hex((ulong)fs.Length) + " got " + Hex((ulong)got) + " pos " + Hex((ulong)fs.Position) + " " + HexBytes(b, 0, 16);
            });
            Probe("File.Exists, FileInfo", () => (System.IO.File.Exists(arial) ? "y" : "n") + (System.IO.File.Exists(arial + ".none") ? "y" : "n") + " " + Hex((ulong)new System.IO.FileInfo(arial).Length));

            Probe("string.Format", () => string.Format("{0}|{1:X8}|{2,5}|{3}|{4:D6}", 42, 48879, "ab", true, -17));
            Probe("Split, Join", () => { var p = "a,b,,c,dd".Split(','); return p.Length + ":" + string.Join("|", p); });
            Probe("case, Replace, IndexOf, Substring, Trim", () => "Hello World".ToUpperInvariant() + " " + "Hello World".ToLowerInvariant() + " " +
                "Hello World".Replace("o", "0") + " " + "Hello World".IndexOf("World") + " " + "Hello World".Substring(3, 4) + " [" + "  x y  ".Trim() + "] " +
                ("Hello".StartsWith("He") ? "s" : "-") + ("Hello".EndsWith("lo") ? "e" : "-") + ("Hello".Contains("ll") ? "c" : "-"));
            Probe("StringBuilder", () =>
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < 300; i++) sb.Append(i * 7919 % 1000).Append(i % 7 == 0 ? ';' : ',');
                string s = sb.ToString();
                return Hex((ulong)s.Length) + " " + Hex(Fnv(s));
            });
            Probe("compare", () => string.CompareOrdinal("abc", "abd") + " " + string.Compare("a", "B", StringComparison.OrdinalIgnoreCase) + " " +
                ("abc".Equals("ABC", StringComparison.OrdinalIgnoreCase) ? "eq" : "ne") + " " + Comparer<string>.Default.Compare("b", "a"));
            Probe("UTF8 bytes", () =>
            {
                var b = System.Text.Encoding.UTF8.GetBytes("héllo € \U0001F600");
                return HexBytes(b, 0, b.Length) + " back " + Chars(System.Text.Encoding.UTF8.GetString(b));
            });

            Probe("List<int>.Sort", () =>
            {
                var l = new List<int>();
                uint x = 12345;
                for (int i = 0; i < 1000; i++) { x = x * 1103515245 + 12345; l.Add((int)(x >> 8)); }
                l.Sort();
                ulong h = 14695981039346656037;
                foreach (int v in l) h = (h ^ (uint)v) * 1099511628211;
                return Hex(h) + " first " + Hex((ulong)(uint)l[0]) + " last " + Hex((ulong)(uint)l[999]);
            });
            Probe("Array.Sort strings", () =>
            {
                var a = new string[60];
                for (int i = 0; i < a.Length; i++) a[i] = "k" + ((i * 37) % 61) + (i % 3 == 0 ? "x" : "");
                Array.Sort(a, StringComparer.Ordinal);
                return Hex(Fnv(string.Join(",", a)));
            });
            Probe("Dictionary<int,int>", () =>
            {
                var d = new Dictionary<int, int>();
                for (int i = 0; i < 5000; i++) d[i * 31 % 7919] = i;
                for (int i = 0; i < 5000; i += 3) d.Remove(i * 31 % 7919);
                long sum = 0;
                for (int i = 0; i < 7919; i++) if (d.TryGetValue(i, out int v)) sum += v + i;
                return Hex((ulong)d.Count) + " " + Hex((ulong)sum);
            });
            Probe("HashSet, SortedDictionary", () =>
            {
                var hs = new HashSet<string>();
                for (int i = 0; i < 200; i++) hs.Add("s" + (i % 150));
                var sd = new SortedDictionary<string, int> { ["pear"] = 1, ["apple"] = 2, ["fig"] = 3, ["banana"] = 4 };
                return Hex((ulong)hs.Count) + (hs.Contains("s149") ? " y" : " n") + (hs.Contains("s150") ? "y " : "n ") + string.Join(",", sd.Keys);
            });
            Probe("LINQ", () =>
            {
                int s = System.Linq.Enumerable.Sum(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(System.Linq.Enumerable.Range(0, 100), v => v % 3 == 0), v => v * v));
                var top = System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(new[] { 5, 3, 9, 1, 7, 9, 2 }, v => v), 3);
                return Hex((ulong)s) + " " + string.Join(",", top);
            });

            Probe("BinaryWriter/Reader", () =>
            {
                var ms = new System.IO.MemoryStream();
                var w = new System.IO.BinaryWriter(ms);
                w.Write(-123456789); w.Write(-1234567890123456789L); w.Write(1.5f); w.Write(-2.25); w.Write("tagé"); w.Write((ushort)65000);
                w.Flush();
                byte[] all = ms.ToArray();
                ms.Position = 0;
                var r = new System.IO.BinaryReader(ms);
                return HexBytes(all, 0, all.Length) + " | " + Hex((ulong)(uint)r.ReadInt32()) + " " + Hex((ulong)r.ReadInt64()) + " " +
                    Hex((ulong)(uint)BitConverter.SingleToInt32Bits(r.ReadSingle())) + " " + Hex((ulong)BitConverter.DoubleToInt64Bits(r.ReadDouble())) + " " +
                    Chars(r.ReadString()) + " " + Hex(r.ReadUInt16());
            });
            Probe("copies", () =>
            {
                var a = new byte[1000];
                for (int i = 0; i < a.Length; i++) a[i] = (byte)(i * 13);
                Array.Copy(a, 10, a, 15, 500);
                Buffer.BlockCopy(a, 600, a, 590, 300);
                a.AsSpan(900, 50).CopyTo(a.AsSpan(3, 50));
                Array.Fill(a, (byte)7, 400, 33);
                Array.Clear(a, 700, 21);
                var l = new long[64];
                for (int i = 0; i < l.Length; i++) l[i] = (long)i * -98765432101L;
                Array.Copy(l, 0, l, 1, 63);
                return Hex(Fnv(a, 0, a.Length)) + " " + Hex((ulong)l[1]) + " " + Hex((ulong)l[63]);
            });
            Probe("Math", () => FBits(Math.Sqrt(2)) + FBits(Math.Sin(1)) + FBits(Math.Cos(1)) + FBits(Math.Pow(2, 0.5)) + FBits(Math.Exp(1)) +
                FBits(Math.Log(10)) + FBits(Math.Atan2(1, 2)) + FBits(Math.Floor(-2.5)) + FBits(Math.Round(2.5)) + FBits(Math.Round(3.5)) +
                FBits(Math.Truncate(-2.7)) + Hex((ulong)(uint)BitConverter.SingleToInt32Bits(MathF.Sqrt(2f))) + " " +
                Hex((ulong)Convert.ToInt64(-1e10)) + " " + Hex((ulong)(uint)Convert.ToInt32(2.5)) + " " + Hex((ulong)(uint)Convert.ToInt32(3.5)));
            Probe("float and decimal text", () => Hex((ulong)(uint)BitConverter.SingleToInt32Bits(float.Parse("1e-5"))) + " " +
                FBits(double.Parse("123.456e10")) + (1.1m * 3).ToString() + " " + decimal.Parse("123.456").ToString() + " " + (2.0 / 3).ToString("F4") + " " + 1234567.891f.ToString());

            Probe("exceptions", () =>
            {
                int order = 0;
                try
                {
                    try { order = order * 10 + 1; throw new InvalidOperationException("x"); }
                    finally { order = order * 10 + 2; }
                }
                catch (InvalidOperationException e) when (e.Message == "x") { order = order * 10 + 3; }
                try { int[] z = new int[2]; z[order % 7 + 2] = 1; } catch (IndexOutOfRangeException) { order = order * 10 + 4; }
                try { object o = "s"; var u = (List<int>)o; } catch (InvalidCastException) { order = order * 10 + 5; }
                return Hex((ulong)order);
            });
            Probe("GC", () =>
            {
                Node head = null;
                for (int i = 0; i < 20000; i++) head = new Node { Next = head, Value = i, Text = (i % 50 == 0) ? "n" + i : null };
                var junk = new List<byte[]>();
                for (int i = 0; i < 200; i++) junk.Add(new byte[3000 + i]);
                junk = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                ulong h = 14695981039346656037;
                int count = 0;
                for (var p = head; p != null; p = p.Next) { h = (h ^ (uint)p.Value) * 1099511628211; if (p.Text != null) h = (h ^ Fnv(p.Text)) * 1099511628211; count++; }
                return Hex((ulong)count) + " " + Hex(h);
            });
            Probe("delegates, interfaces, generics", () =>
            {
                string trace = "";
                Action a = () => trace += "a";
                a += () => trace += "b";
                a += () => trace += "c";
                a();
                Func<int, int, int> f = Math.Max;
                IComparable<int> c = 5;
                IEnumerable<int> e = Count(4);
                int s = 0;
                foreach (int v in e) s = s * 10 + v;
                return trace + " " + f(3, 9) + " " + c.CompareTo(7) + " " + s + " " + EqualityComparer<string>.Default.Equals("q", "q");
            });
            Probe("enums, types", () => Probe64Kind.Beta.ToString() + " " + Enum.Parse<Probe64Kind>("Gamma").ToString() + " " + ((int)Probe64Kind.Gamma) + " " +
                typeof(Dictionary<string, List<int>>).FullName + " " + new KeyValuePair<int, string>(3, "z").ToString());
            Probe("DateTime, TimeSpan, Guid", () =>
            {
                var t = new DateTime(2026, 11, 9, 12, 34, 56, DateTimeKind.Utc);
                return t.ToString("yyyy-MM-dd HH:mm:ss") + " " + Hex((ulong)t.Ticks) + " " + t.AddDays(40).DayOfWeek + " " + TimeSpan.FromSeconds(4000.5).ToString() + " " +
                    Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e").ToString("N");
            });
            Probe("Random(42), Base64, Path, char", () =>
            {
                var r = new Random(42);
                string rs = r.Next() + "," + r.Next(100) + "," + Hex((ulong)BitConverter.DoubleToInt64Bits(r.NextDouble()));
                string b64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 250, 251, 252, 0, 99 });
                int letters = 0;
                for (int i = 0; i < 400; i++) if (char.IsLetterOrDigit((char)i)) letters++;
                return rs + " " + b64 + " " + HexBytes(Convert.FromBase64String(b64), 0, 8) + " " + System.IO.Path.GetFileNameWithoutExtension("a/b/c.d.txt") + " " +
                    System.IO.Path.Combine("x", "y", "z.q") + " " + Hex((ulong)letters) + " " + Chars(char.ToUpperInvariant('é').ToString());
            });
            Probe("Interlocked, lock", () =>
            {
                long v = 5;
                Interlocked.Increment(ref v);
                Interlocked.Add(ref v, 1L << 40);
                long old = Interlocked.CompareExchange(ref v, -1, (1L << 40) + 6);
                object gate = new object();
                int inside = 0;
                lock (gate) { lock (gate) inside = 2; }
                return Hex((ulong)v) + " " + Hex((ulong)old) + " " + inside;
            });
            Probe("System.Numerics", () =>
            {
                var m = System.Numerics.Matrix4x4.CreateRotationZ(0.5f) * System.Numerics.Matrix4x4.CreateTranslation(1, 2, 3);
                var v = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(1, 0, 0), m);
                return Hex((ulong)(uint)BitConverter.SingleToInt32Bits(v.X)) + " " + Hex((ulong)(uint)BitConverter.SingleToInt32Bits(v.Y)) + " " +
                    Hex((ulong)(uint)BitConverter.SingleToInt32Bits(v.Z)) + " " + Hex((ulong)(uint)BitConverter.SingleToInt32Bits(System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(3, 4, 12)).Z));
            });
            Line("T64 probes done");
        }

        sealed class Node { public Node Next; public int Value; public string Text; }
        enum Probe64Kind { Alpha, Beta, Gamma }

        static IEnumerable<int> Count(int n)
        {
            for (int i = 1; i <= n; i++) yield return i;
        }

        static string Lookups(string[] keys)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i < keys.Length; i++) d[keys[i]] = i;
            string r = Hex((ulong)d.Count);
            foreach (var k in new[] { "head", "hhea", "maxp", "cmap", "hmtx", "glyf", "nope" })
                r += " " + k + (d.TryGetValue(k, out int v) ? "=" + Hex((ulong)v) : "-");
            return r;
        }

        static void Probe(string what, Func<string> f)
        {
            string r;
            try { r = "= " + f(); }
            catch (Exception e) { r = "threw " + Program.Describe(e); }
            Line("T64 probe " + what + " " + r);
        }

        static string FBits(double d) => Hex((ulong)BitConverter.DoubleToInt64Bits(d)) + " ";

        /// <summary>A string's characters as hex (not trusting what may print it).</summary>
        static string Chars(string s)
        {
            if ((object)s == null) return "null";
            var c = new char[s.Length * 5];
            for (int i = 0; i < s.Length; i++)
            {
                uint v = s[i];
                c[i * 5] = Nibble(v >> 12); c[i * 5 + 1] = Nibble(v >> 8); c[i * 5 + 2] = Nibble(v >> 4); c[i * 5 + 3] = Nibble(v);
                c[i * 5 + 4] = '.';
            }
            return new string(c);
        }

        static string HexBytes(byte[] b, int start, int count)
        {
            var c = new char[count * 2];
            for (int i = 0; i < count; i++)
            {
                c[i * 2] = Nibble((uint)b[start + i] >> 4);
                c[i * 2 + 1] = Nibble(b[start + i]);
            }
            return new string(c);
        }

        static ulong Fnv(byte[] b, int start, int count)
        {
            ulong h = 14695981039346656037;
            for (int i = start; i < start + count; i++) h = (h ^ b[i]) * 1099511628211;
            return h;
        }

        static ulong Fnv(string s)
        {
            ulong h = 14695981039346656037;
            for (int i = 0; i < s.Length; i++) h = (h ^ s[i]) * 1099511628211;
            return h;
        }

        /// <summary>A step of the test build's start, logged before it runs (the log's last line names a step
        /// that stopped the console).</summary>
        internal static void Step(string what)
        {
            if (verbose) Line("T64 step: " + what);
        }

        static void Check(string what, ulong got, ulong want)
        {
            checks++;
            bool ok = got == want;
            if (!ok) failed++;
            if (verbose || !ok) Line((ok ? "T64 ok   " : "T64 FAIL ") + what + " = " + Hex(got) + (ok ? "" : " (want " + Hex(want) + ")"));
        }

        static void Check(string what, string got, string want)
        {
            checks++;
            bool ok = Same(got, want);
            if (!ok) failed++;
            if (verbose || !ok) Line((ok ? "T64 ok   " : "T64 FAIL ") + what + " = \"" + got + "\"" + (ok ? "" : " (want \"" + want + "\")"));
        }

        static void Threw(string what, Exception e)
        {
            checks++;
            failed++;
            Line("T64 FAIL " + what + " threw " + Program.Describe(e));
        }

        static bool Same(string a, string b)
        {
            if ((object)a == null || (object)b == null) return (object)a == (object)b;
            if (a.Length != b.Length) return false;
            for (int k = 0; k < a.Length; k++)
                if (a[k] != b[k]) return false;
            return true;
        }

        /// <summary>0xhhhhhhhh_llllllll from the two 32-bit halves (not through the formatting under test).</summary>
        static string Hex(ulong v)
        {
            uint hi = (uint)(v >> 32), lo = (uint)v;
            var c = new char[19];
            c[0] = '0'; c[1] = 'x'; c[10] = '_';
            for (int k = 0; k < 8; k++)
            {
                c[2 + k] = Nibble(hi >> (28 - 4 * k));
                c[11 + k] = Nibble(lo >> (28 - 4 * k));
            }
            return new string(c);
        }

        static char Nibble(uint n)
        {
            n &= 15;
            return (char)(n < 10 ? '0' + n : 'a' + n - 10);
        }

        /// <summary>A line to the log: the host's on the console (ASCII, past the game's console output), the
        /// console's own elsewhere.</summary>
        internal static void Line(string s)
        {
            if (!HostLog) { Console.WriteLine(s); return; }
            int n = s.Length < 2000 ? s.Length : 2000;
            byte* b = stackalloc byte[n + 1];
            for (int k = 0; k < n; k++)
            {
                char c = s[k];
                b[k] = c < 128 ? (byte)c : (byte)'?';
            }
            b[n] = (byte)'\n';
            LogLine(b, n + 1);
        }
    }
}
