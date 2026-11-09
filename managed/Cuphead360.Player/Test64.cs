using System;
using System.Runtime.InteropServices;
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
