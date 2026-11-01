using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Cuphead360.Engine
{
    /// <summary>A PPtr as stored: file index (0 = same file, n = externals[n-1]) and path id.</summary>
    public readonly struct PPtr
    {
        public readonly int FileId;
        public readonly long PathId;
        public PPtr(int f, long p) { FileId = f; PathId = p; }
        public bool IsNull => PathId == 0;
        public override string ToString() => "PPtr(" + FileId + "," + PathId + ")";
    }

    public sealed class ChobObject
    {
        public long PathId;
        public int ClassId;
        public string Type;
        public object Tree;
        public ChobFile File;
        public Dictionary<string, object> Map => Tree as Dictionary<string, object>;
        public object this[string key] => Map != null && Map.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>One converted Unity serialized file (tools/convert_objects.py, CHOB v1).</summary>
    public sealed class ChobFile
    {
        public string Name;
        /// <summary>Path below GameData/objects without the extension's effect on lookups (e.g. bundles/atlas_x/CAB-y.chob).</summary>
        public string Path = "";
        public string[] Externals;
        public readonly Dictionary<long, ChobObject> Objects = new Dictionary<long, ChobObject>();
        public readonly List<ChobObject> Ordered = new List<ChobObject>();

        public static ChobFile Load(string path, string name)
        {
            var data = System.IO.File.ReadAllBytes(path);
            var r = new Reader(data);
            if (r.U32() != 0x424F4843) throw new InvalidDataException(path + ": not a CHOB file");
            if (r.U32() != 1) throw new InvalidDataException(path + ": unsupported CHOB version");
            var strings = new string[r.U32()];
            for (int i = 0; i < strings.Length; i++) strings[i] = r.Str();
            r.Strings = strings;
            var f = new ChobFile { Name = name };
            f.Externals = new string[r.U32()];
            for (int i = 0; i < f.Externals.Length; i++) f.Externals[i] = strings[r.U32()];
            uint n = r.U32();
            for (uint i = 0; i < n; i++)
            {
                var o = new ChobObject { PathId = r.I64(), ClassId = r.I32(), Type = strings[r.U32()], File = f };
                o.Tree = r.Value();
                f.Objects[o.PathId] = o;
                f.Ordered.Add(o);
            }
            return f;
        }

        sealed class Reader
        {
            readonly byte[] d; int p;
            public string[] Strings;
            public Reader(byte[] data) { d = data; }
            public uint U32() { uint v = BitConverter.ToUInt32(d, p); p += 4; if (!BitConverter.IsLittleEndian) v = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v); return v; }
            public int I32() => (int)U32();
            public long I64() { ulong lo = U32(), hi = U32(); return (long)(lo | (hi << 32)); }
            public string Str() { int n = (int)U32(); var s = Encoding.UTF8.GetString(d, p, n); p += n; return s; }
            public object Value()
            {
                byte tag = d[p++];
                switch (tag)
                {
                    case 0: return null;
                    case 1: return false;
                    case 2: return true;
                    case 3:
                        {
                            ulong z = 0; int shift = 0; byte b;
                            do { b = d[p++]; z |= (ulong)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
                            return (long)(z >> 1) ^ -(long)(z & 1);
                        }
                    case 4: { float f = BitConverter.Int32BitsToSingle(I32()); return f; }
                    case 5: { double v = BitConverter.Int64BitsToDouble(I64()); return v; }
                    case 6: return Str();
                    case 7: { int n = (int)U32(); var b = new byte[n]; Buffer.BlockCopy(d, p, b, 0, n); p += n; return b; }
                    case 8:
                        {
                            int n = (int)U32();
                            var l = new List<object>(n);
                            for (int i = 0; i < n; i++) l.Add(Value());
                            return l;
                        }
                    case 9:
                        {
                            int n = (int)U32();
                            var m = new Dictionary<string, object>(n, StringComparer.Ordinal);
                            for (int i = 0; i < n; i++) { var k = Strings[U32()]; m[k] = Value(); }
                            return m;
                        }
                    case 10: { int f = I32(); long pid = I64(); return new PPtr(f, pid); }
                }
                throw new InvalidDataException("bad CHOB value tag " + tag + " at " + (p - 1));
            }
        }
    }

    /// <summary>Helpers for reading value trees.</summary>
    public static class Tree
    {
        public static object Get(object t, string key) => t is Dictionary<string, object> m && m.TryGetValue(key, out var v) ? v : null;
        public static long Int(object t, string key, long def = 0) => Get(t, key) is long l ? l : Get(t, key) is bool b ? (b ? 1 : 0) : def;
        public static float Float(object t, string key, float def = 0) => Get(t, key) switch { float f => f, double d => (float)d, long l => l, _ => def };
        public static bool Bool(object t, string key, bool def = false) => Get(t, key) switch { bool b => b, long l => l != 0, _ => def };
        public static string Str(object t, string key) => Get(t, key) as string ?? "";
        public static List<object> List(object t, string key) => Get(t, key) as List<object> ?? new List<object>();
        public static PPtr Ptr(object t, string key) => Get(t, key) is PPtr p ? p : default;
        public static UnityEngine.Vector3 Vec3(object t, string key)
        {
            var v = Get(t, key);
            return new UnityEngine.Vector3(Float(v, "x"), Float(v, "y"), Float(v, "z"));
        }
        public static UnityEngine.Vector2 Vec2(object t, string key)
        {
            var v = Get(t, key);
            return new UnityEngine.Vector2(Float(v, "x"), Float(v, "y"));
        }
        public static UnityEngine.Quaternion Quat(object t, string key)
        {
            var v = Get(t, key);
            return new UnityEngine.Quaternion(Float(v, "x"), Float(v, "y"), Float(v, "z"), Float(v, "w", 1));
        }
        public static UnityEngine.Color Color(object t, string key)
        {
            var v = Get(t, key);
            return new UnityEngine.Color(Float(v, "r", 1), Float(v, "g", 1), Float(v, "b", 1), Float(v, "a", 1));
        }
    }
}
