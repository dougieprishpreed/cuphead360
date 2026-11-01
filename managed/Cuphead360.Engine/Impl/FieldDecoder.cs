using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>
    /// Decodes a script's serialized field bytes (Unity 2017.4 binary format,
    /// no type tree) by walking the real managed type with Unity's
    /// serialization rules. Every object must consume its bytes exactly; a
    /// mismatch is reported once per type and the object keeps its defaults
    /// from that field on.
    /// </summary>
    public static class FieldDecoder
    {
        public static int Decoded, Mismatched;
        static readonly HashSet<Type> reported = new HashSet<Type>();

        sealed class Bin
        {
            public readonly byte[] D; public int P;
            public readonly ChobFile File; public readonly SceneBuild Scene;
            public Bin(byte[] d, ChobFile f, SceneBuild s) { D = d; File = f; Scene = s; }
            public void Need(int n) { if (P + n > D.Length) throw new FormatException("read past end at " + P + "+" + n + " of " + D.Length); }
            public int I32() { Need(4); int v = D[P] | D[P + 1] << 8 | D[P + 2] << 16 | D[P + 3] << 24; P += 4; return v; }
            public long I64() { long lo = (uint)I32(), hi = (uint)I32(); return lo | hi << 32; }
            public float F32() => BitConverter.Int32BitsToSingle(I32());
            public double F64() => BitConverter.Int64BitsToDouble(I64());
            public byte U8() { Need(1); return D[P++]; }
            public short I16() { Need(2); short v = (short)(D[P] | D[P + 1] << 8); P += 2; return v; }
            public void Align() { P = (P + 3) & ~3; }
        }

        public static void Apply(object target, ChobObject co, SceneBuild scene)
        {
            if (!(Tree.Get(co.Tree, "_raw") is byte[] raw)) return;
            var b = new Bin(raw, co.File, scene);
            var t = target.GetType();
            try
            {
                ReadFields(b, target, t, 0);
                if (b.P != raw.Length) Mismatch(t, $"consumed {b.P} of {raw.Length} bytes");
                else Decoded++;
            }
            catch (Exception e) { Mismatch(t, e.GetType().Name + " at byte " + b.P + " of " + raw.Length + ": " + e.Message); }
        }

        static string Show(object v)
        {
            if (v == null) return "null";
            if (v is string s) return "\"" + (s.Length > 40 ? s.Substring(0, 40) + "..." : s) + "\"";
            if (v is Array a) return v.GetType().GetElementType().Name + "[" + a.Length + "]";
            if (v is IList l) return "List[" + l.Count + "]";
            return v.ToString();
        }

        static void Mismatch(Type t, string what)
        {
            Mismatched++;
            if (reported.Add(t)) AssetDb.Problem("serialized fields of " + t.FullName + ": " + what);
        }

        static void ReadFields(Bin b, object obj, Type t, int depth)
        {
            foreach (var f in Serialization.Fields(t))
            {
                if (!Serialization.DepthAllows(f.FieldType, depth)) continue;
                int at = b.P;
                object v = Read(b, f.FieldType, depth, true);
                Trace?.Invoke(new string(' ', depth * 2) + at + " " + f.DeclaringType.Name + "." + f.Name + " : " + f.FieldType.Name + " = " + Show(v));
                f.SetValue(obj, v);
            }
        }

        static object Read(Bin b, Type t, int depth, bool aligned)
        {
            if (t.IsEnum)
            {
                var u = Enum.GetUnderlyingType(t);
                return Enum.ToObject(t, Read(b, u, depth, aligned));
            }
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: { var v = b.U8() != 0; if (aligned) b.Align(); return v; }
                case TypeCode.Byte: { var v = b.U8(); if (aligned) b.Align(); return v; }
                case TypeCode.SByte: { var v = (sbyte)b.U8(); if (aligned) b.Align(); return v; }
                case TypeCode.Int16: { var v = b.I16(); if (aligned) b.Align(); return v; }
                case TypeCode.UInt16: { var v = (ushort)b.I16(); if (aligned) b.Align(); return v; }
                case TypeCode.Char: { var v = (char)b.I16(); if (aligned) b.Align(); return v; }
                case TypeCode.Int32: return b.I32();
                case TypeCode.UInt32: return (uint)b.I32();
                case TypeCode.Int64: return b.I64();
                case TypeCode.UInt64: return (ulong)b.I64();
                case TypeCode.Single: return b.F32();
                case TypeCode.Double: return b.F64();
                case TypeCode.String:
                    {
                        int n = b.I32();
                        b.Need(n);
                        var s = System.Text.Encoding.UTF8.GetString(b.D, b.P, n);
                        b.P += n; b.Align();
                        return s;
                    }
            }
            if (typeof(UObject).IsAssignableFrom(t))
            {
                var p = new PPtr(b.I32(), b.I64());
                if (p.IsNull) return null;
                var o = AssetDb.Resolve(b.File, p, b.Scene);
                return o != null && t.IsInstanceOfType(o) ? o : null;
            }
            if (t.IsArray)
            {
                var et = t.GetElementType();
                int n = Count(b);
                var arr = Array.CreateInstance(et, n);
                for (int i = 0; i < n; i++) arr.SetValue(Read(b, et, depth + 1, false), i);
                b.Align();
                return arr;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                int n = Count(b);
                var list = (IList)Activator.CreateInstance(t);
                for (int i = 0; i < n; i++) list.Add(Read(b, et, depth + 1, false));
                b.Align();
                return list;
            }
            if (UnityStruct(b, t, out var us)) return us;
            // [Serializable] class or struct, inline. Unity runs a parameterless
            // constructor when the class has one, else makes it uninitialised.
            var inst = New(t);
            ReadFields(b, inst, t, depth + 1);
            return inst;
        }

        public static object New(Type t)
        {
            if (t.IsValueType) return Activator.CreateInstance(t);
            var ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return ctor != null ? ctor.Invoke(null) : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        }

        /// <summary>When set, every decoded field is reported (offset, name, type, value).</summary>
        public static Action<string> Trace;

        static int Count(Bin b)
        {
            int n = b.I32();
            if (n < 0 || n > b.D.Length - b.P + 1 && n > 1 << 20) throw new FormatException("bad element count " + n);
            return n;
        }

        static bool UnityStruct(Bin b, Type t, out object v)
        {
            v = null;
            if (t.Namespace != "UnityEngine") return false;
            switch (t.Name)
            {
                case "Vector2": v = new Vector2(b.F32(), b.F32()); return true;
                case "Vector3": v = new Vector3(b.F32(), b.F32(), b.F32()); return true;
                case "Vector4": v = new Vector4(b.F32(), b.F32(), b.F32(), b.F32()); return true;
                case "Quaternion": v = new Quaternion(b.F32(), b.F32(), b.F32(), b.F32()); return true;
                case "Color": v = new Color(b.F32(), b.F32(), b.F32(), b.F32()); return true;
                case "Color32": v = new Color32(b.U8(), b.U8(), b.U8(), b.U8()); return true;
                case "Rect": v = new Rect(b.F32(), b.F32(), b.F32(), b.F32()); return true;
                case "Bounds":
                    {
                        var c = new Vector3(b.F32(), b.F32(), b.F32());
                        var e = new Vector3(b.F32(), b.F32(), b.F32());
                        v = new Bounds(c, e * 2f); return true;
                    }
                case "LayerMask": { LayerMask m = b.I32(); v = m; return true; }
                case "Vector2Int": v = new Vector2Int(b.I32(), b.I32()); return true;
                case "Vector3Int": v = new Vector3Int(b.I32(), b.I32(), b.I32()); return true;
                case "Matrix4x4":
                    {
                        var m = new Matrix4x4();
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) m[r, c] = b.F32();
                        v = m; return true;
                    }
                case "AnimationCurve":
                    {
                        int n = Count(b);
                        var keys = new Keyframe[n];
                        for (int i = 0; i < n; i++) keys[i] = new Keyframe(b.F32(), b.F32(), b.F32(), b.F32());
                        int pre = b.I32(), post = b.I32(); b.I32();   // rotation order
                        var curve = new AnimationCurve(keys);
                        curve.preWrapMode = Icalls.CurveWrapFromInternal(pre);
                        curve.postWrapMode = Icalls.CurveWrapFromInternal(post);
                        v = curve; return true;
                    }
                case "Gradient":
                    {
                        var cols = new Color[8];
                        for (int i = 0; i < 8; i++) cols[i] = new Color(b.F32(), b.F32(), b.F32(), b.F32());
                        var ct = new ushort[8]; var at = new ushort[8];
                        for (int i = 0; i < 8; i++) ct[i] = (ushort)b.I16();
                        for (int i = 0; i < 8; i++) at[i] = (ushort)b.I16();
                        int mode = b.I32();
                        int nc = b.U8(), na = b.U8(); b.Align();
                        var g = new Gradient();
                        var ck = new GradientColorKey[nc];
                        for (int i = 0; i < nc; i++) ck[i] = new GradientColorKey(cols[i], ct[i] / 65535f);
                        var ak = new GradientAlphaKey[na];
                        for (int i = 0; i < na; i++) ak[i] = new GradientAlphaKey(cols[i].a, at[i] / 65535f);
                        g.SetKeys(ck, ak);
                        g.mode = (GradientMode)mode;
                        v = g; return true;
                    }
                case "RectOffset": v = new RectOffset(b.I32(), b.I32(), b.I32(), b.I32()); return true;
            }
            return false;
        }
    }
}
