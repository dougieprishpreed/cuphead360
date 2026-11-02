using System;
using System.Linq;
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
        public ChobFile File;
        // The value tree is parsed when first read: a file's objects are
        // indexed at load, and most of them are never looked at (memory).
        /// <summary>CH360_TRACE_MEM=1: trees parsed per type (count, raw bytes).</summary>
        public static readonly Dictionary<string, (int n, long bytes)> ParseStats =
            Environment.GetEnvironmentVariable("CH360_TRACE_MEM") == "1" ? new Dictionary<string, (int, long)>() : null;
        internal byte[] Data;          // the file's bytes (small files), or null: read from DiskPath
        internal string DiskPath;
        internal string[] Strings;     // non-null until parsed
        internal int Offset, Length;
        object tree;
        public object Tree
        {
            get
            {
                if (Strings != null)
                {
                    var buf = Data;
                    int at = Offset;
                    if (buf == null)
                    {
                        buf = new byte[Length];
                        using (var fs = new FileStream(DiskPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
                        {
                            fs.Seek(Offset, SeekOrigin.Begin);
                            int got = 0, n;
                            while (got < Length && (n = fs.Read(buf, got, Length - got)) > 0) got += n;
                        }
                        at = 0;
                    }
                    var rd = new ChobFile.Reader(buf, at, Strings) { BlobPath = File?.SourcePath, BlobBase = Offset - at };
                    tree = rd.Value();
                    if (ParseStats != null)
                    {
                        ParseStats.TryGetValue(Type, out var st);
                        ParseStats[Type] = (st.n + 1, st.bytes + Length);
                    }
                    Data = null; Strings = null; DiskPath = null;
                }
                return tree;
            }
            set { tree = value; Data = null; Strings = null; DiskPath = null; }
        }
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
        /// <summary>The .chob file on disk (large files' objects are read from it when first parsed).</summary>
        public string SourcePath;
        public readonly Dictionary<long, ChobObject> Objects = new Dictionary<long, ChobObject>();
        public readonly List<ChobObject> Ordered = new List<ChobObject>();

        public static long LoadedBytes; public static int LoadedFiles;
        /// <summary>Files above this many bytes are read per object rather than kept in memory.</summary>
        public static int DiskThreshold = 2 << 20;

        public static ChobFile Load(string path, string name)
        {
            var data = System.IO.File.ReadAllBytes(path);
            LoadedBytes += data.Length; LoadedFiles++;
            var r = new Reader(data);
            if (r.U32() != 0x424F4843) throw new InvalidDataException(path + ": not a CHOB file");
            if (r.U32() != 1) throw new InvalidDataException(path + ": unsupported CHOB version");
            var strings = new string[r.U32()];
            for (int i = 0; i < strings.Length; i++) strings[i] = r.Str();
            r.Strings = strings;
            var f = new ChobFile { Name = name, SourcePath = path };
            // Large files (font and atlas bundles, big scenes) keep only their
            // index: an object's bytes are read from disk when it's first parsed.
            bool keep = data.Length <= DiskThreshold;
            f.Externals = new string[r.U32()];
            for (int i = 0; i < f.Externals.Length; i++) f.Externals[i] = strings[r.U32()];
            uint n = r.U32();
            for (uint i = 0; i < n; i++)
            {
                var o = new ChobObject { PathId = r.I64(), ClassId = r.I32(), Type = strings[r.U32()], File = f };
                o.Strings = strings; o.Offset = r.Pos;
                r.Skip();
                o.Length = r.Pos - o.Offset;
                if (keep) o.Data = data; else o.DiskPath = path;
                f.Objects[o.PathId] = o;
                f.Ordered.Add(o);
            }
            return f;
        }

        internal sealed class Reader
        {
            readonly byte[] d; int p;
            public string[] Strings;
            public Reader(byte[] data) { d = data; }
            public Reader(byte[] data, int pos, string[] strings) { d = data; p = pos; Strings = strings; }
            /// <summary>Where this buffer sits in the file (byte blobs of 1 MB and more become ChobBlobs read from there).</summary>
            public string BlobPath; public long BlobBase;
            public int Pos => p;

            /// <summary>Step over one value without building it.</summary>
            public void Skip()
            {
                byte tag = d[p++];
                switch (tag)
                {
                    case 0: case 1: case 2: return;
                    case 3: while ((d[p++] & 0x80) != 0) { } return;
                    case 4: p += 4; return;
                    case 5: p += 8; return;
                    case 6: case 7: { int n = (int)U32(); p += n; return; }
                    case 8: { int n = (int)U32(); for (int i = 0; i < n; i++) Skip(); return; }
                    case 9: { int n = (int)U32(); for (int i = 0; i < n; i++) { p += 4; Skip(); } return; }
                    case 10: p += 12; return;
                }
                throw new InvalidDataException("bad CHOB value tag " + tag + " at " + (p - 1));
            }
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
                            // Byte blobs the converter wrote as integer lists (a font's
                            // m_FontData, index buffers): a byte[] rather than a boxed
                            // value per byte (2 GB of boxes for the game's fonts).
                            if (n >= 1 << 20 && d[p] == 3 && BlobPath != null)
                            {
                                var blob = new ChobBlob { Path = BlobPath, Offset = BlobBase + p, Count = n };
                                for (int i = 0; i < n; i++) Skip();
                                return blob;
                            }
                            if (n >= 4096 && d[p] == 3)
                            {
                                int save = p;
                                var bytes = new byte[n];
                                int i = 0;
                                for (; i < n && d[p] == 3; i++)
                                {
                                    p++;
                                    ulong z = 0; int shift = 0; byte b;
                                    do { b = d[p++]; z |= (ulong)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
                                    long v = (long)(z >> 1) ^ -(long)(z & 1);
                                    if (v < 0 || v > 255) break;
                                    bytes[i] = (byte)v;
                                }
                                if (i == n) return bytes;
                                p = save;
                            }
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

    /// <summary>
    /// A large byte blob (a font's m_FontData) left in the converted file:
    /// Read() decodes it from disk when the bytes are needed, so the parsed
    /// tree doesn't hold megabytes nobody may use.
    /// </summary>
    public sealed class ChobBlob
    {
        public string Path;
        public long Offset;   // the first element's tag
        public int Count;

        public byte[] Read()
        {
            var raw = new byte[Math.Min((long)Count * 11, new FileInfo(Path).Length - Offset)];
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
            {
                fs.Seek(Offset, SeekOrigin.Begin);
                int got = 0, n;
                while (got < raw.Length && (n = fs.Read(raw, got, raw.Length - got)) > 0) got += n;
            }
            var bytes = new byte[Count];
            int p = 0;
            for (int i = 0; i < Count; i++)
            {
                p++;   // tag 3
                ulong z = 0; int shift = 0; byte b;
                do { b = raw[p++]; z |= (ulong)(b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
                bytes[i] = (byte)((long)(z >> 1) ^ -(long)(z & 1));
            }
            return bytes;
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
        public static List<object> List(object t, string key) => Get(t, key) switch
        {
            List<object> l => l,
            byte[] b => b.Select(x => (object)(long)x).ToList(),   // a byte blob read back as a list
            _ => new List<object>(),
        };
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
