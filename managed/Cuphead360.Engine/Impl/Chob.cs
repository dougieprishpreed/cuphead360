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
        // A font's m_FontData of 1 MB or more inside the object, found when a
        // large file is indexed: reads of the object leave it out (it becomes
        // a ChobBlob), so a font costs its few other bytes until it's drawn.
        internal long HoleAt;          // file offset of its first byte
        internal int HoleLen;
        object tree;
        // The parsed-tree cache: over TreeBudget (estimated bytes), the trees
        // used longest ago are let go and parsed again from the file if asked
        // for (trees set at run time are kept).
        string[] table;               // the file's strings, kept to parse again
        long lastUse, cost;
        static long clock, treeBytes;
        static readonly List<ChobObject> cached = new List<ChobObject>();
        /// <summary>Estimated bytes of parsed trees kept before the oldest are dropped (the console host sets it).</summary>
        public static long TreeBudget = long.TryParse(Environment.GetEnvironmentVariable("CH360_TREE_BUDGET_MB"), out var mb) ? mb << 20 : long.MaxValue;
        public static int TreesDropped, TreesReparsed;

        public object Tree
        {
            get
            {
                lastUse = ++clock;
                if (Strings != null)
                {
                    if (table != null) TreesReparsed++;
                    var buf = Data;
                    int at = Offset, holeAt = -1;
                    if (buf == null)
                    {
                        // From the drive, through the block cache: the object's bytes, less its font data.
                        int pre = HoleLen > 0 ? (int)(HoleAt - Offset) : Length, post = HoleLen > 0 ? Length - pre - HoleLen : 0;
                        buf = new byte[pre + post];
                        File.ReadAt(Offset, buf, 0, pre);
                        if (post > 0) File.ReadAt(HoleAt + HoleLen, buf, pre, post);
                        ChobFile.ObjectReads++;
                        ChobFile.ObjectBytes += buf.Length;
                        if (ChobFile.TraceReads) Console.WriteLine($"READ {File?.Name} {Offset} {Length} {HoleLen}");
                        at = 0;
                        if (HoleLen > 0) holeAt = pre;
                    }
                    var rd = new ChobFile.Reader(buf, at, Strings)
                    {
                        BlobPath = File?.SourcePath, BlobBase = Offset - at, HoleAt = holeAt, HoleLen = HoleLen, HoleFile = HoleAt
                    };
                    tree = rd.Value();
                    if (ParseStats != null)
                    {
                        ParseStats.TryGetValue(Type, out var st);
                        ParseStats[Type] = (st.n + 1, st.bytes + Length);
                    }
                    bool budgeted = File?.SourcePath != null && TreeBudget != long.MaxValue;
                    if (budgeted)
                    {
                        table = Strings;
                        cost = 64 + Length * 6L;   // boxed trees run ~6x their encoded size
                        treeBytes += cost;
                        cached.Add(this);
                    }
                    Data = null; Strings = null; DiskPath = null;
                    if (budgeted && treeBytes > TreeBudget) Trim(this);
                }
                return tree;
            }
            set { tree = value; Data = null; Strings = null; DiskPath = null; table = null; }
        }

        /// <summary>
        /// Lets go of the least recently used parsed trees down to 3/4 of the budget, never the
        /// one being handed out: a tree over 3/4 of the budget let itself go, the getter returned
        /// null and cleared its strings for good (the localization table's elements came out null).
        /// </summary>
        static void Trim(ChobObject keep)
        {
            cached.Sort((a, b) => a.lastUse.CompareTo(b.lastUse));
            int i = 0;
            bool kept = false;
            for (; i < cached.Count && treeBytes > TreeBudget * 3 / 4; i++)
            {
                var o = cached[i];
                if (o == keep) { kept = true; continue; }
                if (o.table == null || o.tree == null) { treeBytes -= o.cost; continue; }
                o.tree = null;
                o.Strings = o.table;
                o.DiskPath = o.File.SourcePath;
                o.Data = null;
                treeBytes -= o.cost;
                TreesDropped++;
            }
            cached.RemoveRange(0, i);
            if (kept) cached.Add(keep);
        }
        public Dictionary<string, object> Map => Tree as Dictionary<string, object>;
        public object this[string key] => Map != null && Map.TryGetValue(key, out var v) ? v : null;

        /// <summary>The parsed tree if there is one (nothing is parsed by asking).</summary>
        internal object Parsed => Strings == null ? tree : null;

        /// <summary>
        /// CH360_TRACE_MEM: what the parsed trees and still-held file bytes
        /// cost, by type (bytes estimated from the .NET object layout: a map
        /// entry ~40, a list slot 8 + a boxed number 24).
        /// </summary>
        public static string Census(IEnumerable<ChobFile> files)
        {
            var by = new Dictionary<string, (int n, long bytes)>();
            long held = 0;
            foreach (var f in files)
            {
                byte[] data = null;
                foreach (var o in f.Ordered)
                {
                    if (o.Data != null) data = o.Data;
                    var t = o.Parsed;
                    if (t == null) continue;
                    by.TryGetValue(o.Type, out var st);
                    by[o.Type] = (st.n + 1, st.bytes + Size(t));
                }
                if (data != null) held += data.Length;
            }
            var sb = new StringBuilder($"TREES file bytes held {held >> 20} MB;");
            long total = 0;
            foreach (var kv in by) total += kv.Value.bytes;
            sb.Append($" trees {total >> 20} MB:");
            var list = new List<KeyValuePair<string, (int n, long bytes)>>(by);
            list.Sort((a, b) => b.Value.bytes.CompareTo(a.Value.bytes));
            for (int i = 0; i < list.Count && i < 12; i++) sb.Append($" {list[i].Key} {list[i].Value.n}/{list[i].Value.bytes >> 20} MB");
            return sb.ToString();
        }

        static long Size(object v)
        {
            switch (v)
            {
                case Dictionary<string, object> m:
                    { long s = 80 + m.Count * 40L; foreach (var x in m.Values) s += Size(x); return s; }
                case List<object> l:
                    { long s = 40 + l.Capacity * 8L; foreach (var x in l) s += Size(x); return s; }
                case byte[] b: return 24 + b.Length;
                case string str: return 0;   // shared with the file's string table mostly
                case null: return 0;
                default: return 24;          // a boxed number or bool, a PPtr
            }
        }
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
        /// <summary>Objects read from the drive one at a time, and their bytes.</summary>
        public static long ObjectBytes; public static int ObjectReads;
        /// <summary>Files above this many bytes are read per object rather than kept in memory.</summary>
        public static int DiskThreshold = 2 << 20;
        /// <summary>When set, files that take over 100 ms to read and index are reported (the console host sets it).</summary>
        public static Action<string> Trace;

        // Large files' objects are read through 32 KB blocks, the least
        // recently used let go over BlockBudget, and a few of the files kept
        // open: a scene load parses thousands of their objects in no useful
        // order, each an open and a read of the drive before (6,000 for the
        // world map, half its 14 s load on the console). The bot's run reads
        // 15,872 objects: ~2,000 block reads with 8 MB of blocks; reading the
        // files whole instead needs ~18 MB at once and re-reads them.
        public const int BlockBytes = 32 << 10;
        /// <summary>Bytes of blocks kept (the console host sets a smaller budget).</summary>
        public static long BlockBudget = long.TryParse(Environment.GetEnvironmentVariable("CH360_BLOCK_BUDGET_MB"), out var bb) ? bb << 20 : 16L << 20;
        public static int BlockReads;
        internal static readonly bool TraceReads = Environment.GetEnvironmentVariable("CH360_TRACE_READS") == "1";

        sealed class Block { public ChobFile File; public int Index, Count; public byte[] Data; }
        static readonly Dictionary<(ChobFile, int), LinkedListNode<Block>> blocks = new Dictionary<(ChobFile, int), LinkedListNode<Block>>();
        static readonly LinkedList<Block> lru = new LinkedList<Block>();   // most recently used first
        static readonly List<ChobFile> open = new List<ChobFile>();        // most recently used first
        FileStream stream;
        long length;

        FileStream Stream()
        {
            if (stream != null)
            {
                if (open[0] != this) { open.Remove(this); open.Insert(0, this); }
                return stream;
            }
            stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            length = stream.Length;
            open.Insert(0, this);
            if (open.Count > 4)
            {
                var f = open[4];
                open.RemoveAt(4);
                f.stream.Dispose();
                f.stream = null;
            }
            return stream;
        }

        /// <summary>count bytes of the file from off into dst at at (through the blocks; long runs straight).</summary>
        internal void ReadAt(long off, byte[] dst, int at, int count)
        {
            if (count <= 0) return;
            if (count > 4 * BlockBytes || BlockBudget < BlockBytes)
            {
                var fs = Stream();
                fs.Seek(off, SeekOrigin.Begin);
                ReadFully(fs, dst, at, count);
                return;
            }
            while (count > 0)
            {
                var b = BlockAt((int)(off / BlockBytes));
                int within = (int)(off % BlockBytes), n = Math.Min(count, b.Count - within);
                if (n <= 0) throw new EndOfStreamException("short CHOB read");
                Buffer.BlockCopy(b.Data, within, dst, at, n);
                off += n; at += n; count -= n;
            }
        }

        Block BlockAt(int index)
        {
            if (blocks.TryGetValue((this, index), out var node))
            {
                if (node != lru.First) { lru.Remove(node); lru.AddFirst(node); }
                return node.Value;
            }
            if ((long)(lru.Count + 1) * BlockBytes > BlockBudget && lru.Last != null)
            {
                node = lru.Last;   // its array is used again
                lru.RemoveLast();
                blocks.Remove((node.Value.File, node.Value.Index));
            }
            else node = new LinkedListNode<Block>(new Block { Data = new byte[BlockBytes] });
            var b = node.Value;
            b.File = this; b.Index = index;
            var fs = Stream();
            long pos = (long)index * BlockBytes;
            fs.Seek(pos, SeekOrigin.Begin);
            int want = (int)Math.Min(BlockBytes, length - pos), got = 0, r;
            while (got < want && (r = fs.Read(b.Data, got, want - got)) > 0) got += r;
            b.Count = got;
            BlockReads++;
            lru.AddFirst(node);
            blocks[(this, index)] = node;
            return b;
        }

        internal static void ReadFully(Stream fs, byte[] buf, int at, int count)
        {
            int got = 0, n;
            while (got < count && (n = fs.Read(buf, at + got, count - got)) > 0) got += n;
            if (got != count) throw new EndOfStreamException("short CHOB read");
        }

        /// <summary>
        /// Indexes a converted file. Small files are read whole; large ones
        /// (scenes' shared assets, font and atlas bundles) are indexed from the
        /// drive, seeking over byte blobs (a font bundle's megabytes of font
        /// data), and their objects read when first parsed.
        /// </summary>
        public static ChobFile Load(string path, string name)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), t1;
            var f = new ChobFile { Name = name, SourcePath = path };
            string[] strings;
            uint n;
            long size, read;
            byte[] data = null;
            int start;
            using (var sc = new Scan(path))
            {
                size = sc.Length;
                if (sc.U32() != 0x424F4843) throw new InvalidDataException(path + ": not a CHOB file");
                if (sc.U32() != 1) throw new InvalidDataException(path + ": unsupported CHOB version");
                strings = new string[sc.U32()];
                for (int i = 0; i < strings.Length; i++) strings[i] = sc.Str();
                f.Externals = new string[sc.U32()];
                for (int i = 0; i < f.Externals.Length; i++) f.Externals[i] = strings[sc.U32()];
                n = sc.U32();
                start = (int)sc.Pos;
                if (size <= DiskThreshold)
                {
                    data = sc.ReadAll();
                    t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                }
                else if (sc.Index(n) is byte[] index)
                {
                    t1 = t0;
                    FromIndex(f, index, 0, n, strings, path, null);
                }
                else
                {
                    t1 = t0;
                    for (uint i = 0; i < n; i++)
                    {
                        var o = new ChobObject { PathId = sc.I64(), ClassId = (int)sc.U32(), Type = strings[sc.U32()], File = f };
                        o.Strings = strings; o.DiskPath = path;
                        o.Offset = (int)sc.Pos;
                        sc.SkipValue(strings, o, false);
                        o.Length = (int)(sc.Pos - o.Offset);
                        f.Objects[o.PathId] = o;
                        f.Ordered.Add(o);
                    }
                }
                read = sc.BytesRead;
            }
            LoadedBytes += read; LoadedFiles++;
            if (data != null && Trailer(data, data.Length - 16, data.Length, n, out long indexAt))
                FromIndex(f, data, (int)indexAt, n, strings, null, data);
            else if (data != null)
            {
                var r = new Reader(data, start, strings);
                for (uint i = 0; i < n; i++)
                {
                    var o = new ChobObject { PathId = r.I64(), ClassId = r.I32(), Type = strings[r.U32()], File = f };
                    o.Strings = strings; o.Offset = r.Pos;
                    r.Skip();
                    o.Length = r.Pos - o.Offset;
                    o.Data = data;
                    f.Objects[o.PathId] = o;
                    f.Ordered.Add(o);
                }
            }
            if (Trace != null)
            {
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp(), hz = System.Diagnostics.Stopwatch.Frequency;
                if ((t2 - t0) * 10 > hz)
                    Trace($"CHOB {name}: {size >> 10} KB, {n} objects, {read >> 10} KB read; read {(t1 - t0) * 1000 / hz} ms, indexed {(t2 - t1) * 1000 / hz} ms");
            }
            return f;
        }

        static uint U32At(byte[] b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);

        /// <summary>
        /// The converter's object index ('CHIX' at the end: tools/convert_objects.py): true and its
        /// offset when the trailer at t[at..at+16) belongs to a file of length bytes with n objects.
        /// </summary>
        static bool Trailer(byte[] t, int at, long length, uint n, out long indexAt)
        {
            indexAt = 0;
            if (at < 0 || length < 16) return false;
            uint off = U32At(t, at), count = U32At(t, at + 4);
            if (U32At(t, at + 8) != 0x58494843u || U32At(t, at + 12) != 1 || count != n || off + (long)count * 32 > length - 16) return false;
            indexAt = off;
            return true;
        }

        /// <summary>
        /// The file's objects from its index (no walk over their values, which took the console
        /// 5.7 s for a level's shared files): their place and length, and for a file read from the
        /// drive its fonts' large m_FontData (read when the font is drawn), as the walk noted them.
        /// </summary>
        static void FromIndex(ChobFile f, byte[] idx, int at, uint n, string[] strings, string diskPath, byte[] data)
        {
            for (uint i = 0; i < n; i++, at += 32)
            {
                var o = new ChobObject
                {
                    PathId = (long)(U32At(idx, at) | (ulong)U32At(idx, at + 4) << 32), ClassId = (int)U32At(idx, at + 8),
                    Type = strings[U32At(idx, at + 12)], File = f
                };
                o.Strings = strings;
                o.Offset = (int)U32At(idx, at + 16);
                o.Length = (int)U32At(idx, at + 20);
                if (data != null) o.Data = data;
                else
                {
                    o.DiskPath = diskPath;
                    uint holeLen = U32At(idx, at + 28);
                    if (holeLen > 0) { o.HoleAt = U32At(idx, at + 24); o.HoleLen = (int)holeLen; }
                }
                f.Objects[o.PathId] = o;
                f.Ordered.Add(o);
            }
        }

        /// <summary>A converted file read through a 64 KB buffer, seeking over what is skipped.</summary>
        sealed class Scan : IDisposable
        {
            readonly FileStream fs;
            readonly byte[] b = new byte[64 * 1024];
            int n, i;
            long at;   // file offset of b[0]
            public long BytesRead;
            public Scan(string path) { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1); Length = fs.Length; }
            public readonly long Length;
            public long Pos => at + i;
            void Fill()
            {
                at += n; i = 0;
                n = fs.Read(b, 0, b.Length);
                if (n <= 0) throw new EndOfStreamException("short CHOB file");
                BytesRead += n;
            }
            public byte Byte() { if (i >= n) Fill(); return b[i++]; }
            public uint U32()
            {
                if (n - i >= 4) { uint v = (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24); i += 4; return v; }
                uint x = Byte(); x |= (uint)Byte() << 8; x |= (uint)Byte() << 16; x |= (uint)Byte() << 24;
                return x;
            }
            public long I64() { ulong lo = U32(), hi = U32(); return (long)(lo | (hi << 32)); }
            public void Skip(long k)
            {
                if (k <= n - i) { i += (int)k; return; }
                long to = Pos + k;
                fs.Seek(to, SeekOrigin.Begin);
                at = to; n = 0; i = 0;
            }
            public string Str()
            {
                int k = (int)U32();
                if (k <= n - i) { var s = Encoding.UTF8.GetString(b, i, k); i += k; return s; }
                var t = new byte[k];
                for (int j = 0; j < k; j++) t[j] = Byte();
                return Encoding.UTF8.GetString(t);
            }
            /// <summary>The whole file (what was read is kept; the rest read now).</summary>
            /// <summary>The file's object index (n entries), or null: the scan then goes on where it was.</summary>
            public byte[] Index(uint count)
            {
                if (Length < 16) return null;
                var t = new byte[16];
                fs.Seek(Length - 16, SeekOrigin.Begin);
                ReadFully(fs, t, 0, 16);
                BytesRead += 16;
                byte[] idx = null;
                if (Trailer(t, 0, Length, count, out long indexAt))
                {
                    idx = new byte[count * 32];
                    fs.Seek(indexAt, SeekOrigin.Begin);
                    ReadFully(fs, idx, 0, idx.Length);
                    BytesRead += idx.Length;
                }
                fs.Seek(at + n, SeekOrigin.Begin);
                return idx;
            }

            public byte[] ReadAll()
            {
                var all = new byte[Length];
                long from = 0;
                if (at == 0) { Buffer.BlockCopy(b, 0, all, 0, n); from = n; }
                fs.Seek(from, SeekOrigin.Begin);
                ReadFully(fs, all, (int)from, (int)(Length - from));
                BytesRead += Length - from;
                return all;
            }
            /// <summary>Steps over one value, noting where a large m_FontData lies in o.</summary>
            public void SkipValue(string[] strings, ChobObject o, bool fontKey)
            {
                byte tag = Byte();
                switch (tag)
                {
                    case 0: case 1: case 2: return;
                    case 3: while ((Byte() & 0x80) != 0) { } return;
                    case 4: Skip(4); return;
                    case 5: Skip(8); return;
                    case 6: Skip(U32()); return;
                    case 7:
                        {
                            uint k = U32();
                            if (fontKey && k >= 1 << 20 && o.HoleLen == 0) { o.HoleAt = Pos; o.HoleLen = (int)k; }
                            Skip(k);
                            return;
                        }
                    case 8: { uint k = U32(); for (uint j = 0; j < k; j++) SkipValue(strings, o, false); return; }
                    case 9: { uint k = U32(); for (uint j = 0; j < k; j++) SkipValue(strings, o, strings[U32()] == "m_FontData"); return; }
                    case 10: Skip(12); return;
                }
                throw new InvalidDataException("bad CHOB value tag " + tag + " at " + (Pos - 1));
            }
            public void Dispose() => fs.Dispose();
        }

        internal sealed class Reader
        {
            readonly byte[] d; int p;
            public string[] Strings;
            public Reader(byte[] data) { d = data; }
            public Reader(byte[] data, int pos, string[] strings) { d = data; p = pos; Strings = strings; }
            /// <summary>Where this buffer sits in the file (byte blobs of 1 MB and more become ChobBlobs read from there).</summary>
            public string BlobPath; public long BlobBase;
            /// <summary>Where the object's font data was left out of the buffer (-1: nothing was), and where it is in the file.</summary>
            public int HoleAt = -1, HoleLen; public long HoleFile;
            long FileAt(int pos) => BlobBase + pos + (HoleAt >= 0 && pos >= HoleAt ? HoleLen : 0);
            bool lazyKey;   // the value being read is a font's m_FontData
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
                    case 7:
                        {
                            int n = (int)U32();
                            if (p == HoleAt && n == HoleLen) return new ChobBlob { Path = BlobPath, Offset = HoleFile, Count = n, Raw = true };
                            // A font's data stays in the file until the font is drawn (other
                            // byte values, TextAssets and script fields, are read as bytes).
                            if (n >= 1 << 20 && BlobPath != null && lazyKey)
                            {
                                var blob = new ChobBlob { Path = BlobPath, Offset = FileAt(p), Count = n, Raw = true };
                                p += n;
                                return blob;
                            }
                            var b = new byte[n]; Buffer.BlockCopy(d, p, b, 0, n); p += n; return b;
                        }
                    case 8:
                        {
                            int n = (int)U32();
                            // Byte blobs the converter wrote as integer lists (a font's
                            // m_FontData, index buffers): a byte[] rather than a boxed
                            // value per byte (2 GB of boxes for the game's fonts).
                            if (n >= 1 << 20 && d[p] == 3 && BlobPath != null)
                            {
                                var blob = new ChobBlob { Path = BlobPath, Offset = FileAt(p), Count = n };
                                for (int i = 0; i < n; i++) Skip();
                                return blob;
                            }
                            if (n >= 16 && d[p] == 3)
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
                            for (int i = 0; i < n; i++)
                            {
                                var k = Strings[U32()];
                                lazyKey = k == "m_FontData";
                                m[k] = Value();
                                lazyKey = false;
                            }
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
        public long Offset;   // the first byte (Raw) or the first element's tag
        public int Count;
        public bool Raw;      // stored as bytes (tag 7), not an integer list

        public byte[] Read()
        {
            if (Raw)
            {
                var whole = new byte[Count];
                using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
                {
                    fs.Seek(Offset, SeekOrigin.Begin);
                    int got = 0, n;
                    while (got < Count && (n = fs.Read(whole, got, Count - got)) > 0) got += n;
                }
                return whole;
            }
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
