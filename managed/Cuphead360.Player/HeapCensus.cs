using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cuphead360.Player
{
    /// <summary>
    /// CH360_HEAP_CENSUS_AT=frame (the PC player): what the managed heap holds, by the static field it is
    /// reached from first and by type. Walks the object graph from every static field of the engine's,
    /// the game's and Unity's types (reflection), each object counted once, sizes estimated from the
    /// field layout for the console's 32-bit heap (8-byte headers, 4-byte references). Objects held only
    /// by frames on the stack or by weak tables aren't reached. A diagnostic: slow and allocation-heavy.
    /// </summary>
    static class HeapCensus
    {
        sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly Dictionary<Type, FieldInfo[]> fields = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, long> shallow = new Dictionary<Type, long>();

        static FieldInfo[] FieldsOf(Type t)
        {
            if (fields.TryGetValue(t, out var f)) return f;
            var list = new List<FieldInfo>();
            for (var c = t; c != null && c != typeof(object); c = c.BaseType)
                list.AddRange(c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            f = list.ToArray();
            fields[t] = f;
            return f;
        }

        static long PrimSize(Type t)
        {
            if (!t.IsValueType) return 4;
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: case TypeCode.Byte: case TypeCode.SByte: return 1;
                case TypeCode.Char: case TypeCode.Int16: case TypeCode.UInt16: return 2;
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Single: return 4;
                case TypeCode.Int64: case TypeCode.UInt64: case TypeCode.Double: return 8;
                case TypeCode.Decimal: return 16;
            }
            if (t == typeof(IntPtr) || t == typeof(UIntPtr)) return 4;
            long s = 0;
            foreach (var f in FieldsOf(t)) s += PrimSize(f.FieldType);
            return Math.Max(1, s);
        }

        // The object's own bytes on a 32-bit heap.
        static long Size(object o)
        {
            var t = o.GetType();
            if (o is string s) return 12 + 2L * s.Length;
            if (o is Array a)
            {
                var et = t.GetElementType();
                return 12 + (long)a.Length * PrimSize(et);
            }
            if (shallow.TryGetValue(t, out var n)) return n;
            n = 8;
            foreach (var f in FieldsOf(t)) n += PrimSize(f.FieldType);
            n = Math.Max(12, (n + 3) & ~3L);
            shallow[t] = n;
            return n;
        }

        public static void Run(int frame)
        {
            var seen = new HashSet<object>(new RefEq());
            var byRoot = new Dictionary<string, (long bytes, long n)>();
            // Destroyed engine objects each root still reaches (what a destroyed scene leaves held), by type
            var deadByRoot = new Dictionary<string, Dictionary<string, long>>();
            var byType = new Dictionary<Type, (long bytes, long n)>();
            var stack = new Stack<object>();
            long total = 0, objects = 0;
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .OrderBy(a => a.GetName().Name == "Cuphead360.Engine" ? 0 : a.GetName().Name.StartsWith("UnityEngine") ? 2 : 1).ToList();
            foreach (var asm in asms)
            {
                string an = asm.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Microsoft") || an == "netstandard" || an == "mscorlib") continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                foreach (var t in types)
                {
                    if (t.ContainsGenericParameters) continue;
                    FieldInfo[] sf;
                    try { sf = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); } catch { continue; }
                    foreach (var f in sf)
                    {
                        if (f.IsLiteral || f.FieldType.IsPrimitive) continue;
                        object v;
                        try { v = f.GetValue(null); } catch { continue; }
                        if (v == null || seen.Contains(v)) continue;
                        long rb = 0, rn = 0;
                        Dictionary<string, long> dead = null;
                        seen.Add(v); stack.Push(v);
                        while (stack.Count > 0)
                        {
                            var o = stack.Pop();
                            long sz = Size(o);
                            rb += sz; rn++;
                            var ot = o.GetType();
                            if (o is Cuphead360.Engine.NObject no && no.Destroyed)
                            {
                                dead ??= new Dictionary<string, long>();
                                string dk = ot.Name + " " + (no is Cuphead360.Engine.NComponent nc ? nc.Managed?.GetType().Name : no.Name);
                                dead.TryGetValue(dk, out long dn); dead[dk] = dn + 1;
                            }
                            byType.TryGetValue(ot, out var bt);
                            byType[ot] = (bt.bytes + sz, bt.n + 1);
                            if (o is string) continue;
                            if (o is Array arr)
                            {
                                var et = ot.GetElementType();
                                if (et.IsPrimitive || et.IsEnum) continue;
                                if (!et.IsValueType)
                                {
                                    foreach (var x in arr) if (x != null && seen.Add(x)) stack.Push(x);
                                }
                                else if (FieldsOf(et).Any(ff => !ff.FieldType.IsPrimitive && !ff.FieldType.IsEnum))
                                {
                                    foreach (var x in arr) Children(x, seen, stack);
                                }
                                continue;
                            }
                            Children(o, seen, stack);
                        }
                        total += rb; objects += rn;
                        string key = (t.FullName ?? t.Name) + "::" + f.Name;
                        byRoot[key] = (rb, rn);
                        if (dead != null) deadByRoot[key] = dead;
                    }
                }
            }
            Console.WriteLine($"HEAP CENSUS frame {frame}: {objects} objects, ~{total >> 20} MB on a 32-bit heap reached from static fields; GC.GetTotalMemory {GC.GetTotalMemory(true) >> 20} MB (64-bit)");
            foreach (var kv in byRoot.OrderByDescending(k => k.Value.bytes).Take(40))
                Console.WriteLine($"HEAP root {kv.Value.bytes / 1048576.0,8:0.00} MB {kv.Value.n,9}  {kv.Key}");
            foreach (var kv in byType.OrderByDescending(k => k.Value.bytes).Take(50))
                Console.WriteLine($"HEAP type {kv.Value.bytes / 1048576.0,8:0.00} MB {kv.Value.n,9}  {kv.Key.FullName}");
            foreach (var kv in deadByRoot.OrderByDescending(k => k.Value.Values.Sum()).Take(20))
                Console.WriteLine($"HEAP dead {kv.Value.Values.Sum(),7} destroyed objects held by {kv.Key}: "
                    + string.Join(", ", kv.Value.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value}")));
        }

        static void Children(object o, HashSet<object> seen, Stack<object> stack)
        {
            foreach (var f in FieldsOf(o.GetType()))
            {
                var ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(IntPtr) || ft.IsPointer) continue;
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (v == null) continue;
                if (ft.IsValueType) { Children(v, seen, stack); continue; }   // a struct's references
                if (seen.Add(v)) stack.Push(v);
            }
        }
    }
}
