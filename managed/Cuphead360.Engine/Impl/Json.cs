using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Cuphead360.Engine
{
    /// <summary>
    /// UnityEngine.JsonUtility: the same fields as Unity serialization;
    /// Object references as {"instanceID":n}; enums as integers; missing keys
    /// keep the object's current values (FromJsonOverwrite).
    /// </summary>
    public static class Json
    {
        public static string ToJson(object obj, bool pretty)
        {
            if (obj == null) return "";
            var sb = new StringBuilder();
            WriteObject(sb, obj, obj.GetType(), pretty, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, bool pretty, int level)
        {
            if (!pretty) return;
            sb.Append('\n').Append(' ', level * 4);
        }

        static void WriteObject(StringBuilder sb, object obj, Type t, bool pretty, int level)
        {
            sb.Append('{');
            bool first = true;
            foreach (var f in Serialization.Fields(t))
            {
                if (!Serialization.DepthAllows(f.FieldType, level)) continue;
                if (!first) sb.Append(',');
                first = false;
                Indent(sb, pretty, level + 1);
                WriteString(sb, f.Name);
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, f.GetValue(obj), f.FieldType, pretty, level + 1);
            }
            if (!first) Indent(sb, pretty, level);
            sb.Append('}');
        }

        static void WriteValue(StringBuilder sb, object v, Type t, bool pretty, int level)
        {
            if (t.IsEnum) { sb.Append(System.Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); return; }
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: sb.Append((bool)v ? "true" : "false"); return;
                case TypeCode.Single: sb.Append(Num((double)(float)v)); return;
                case TypeCode.Double: sb.Append(Num((double)v)); return;
                case TypeCode.String: WriteString(sb, (string)v ?? ""); return;
                case TypeCode.Char: sb.Append((int)(char)v); return;
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                    sb.Append(System.Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
            if (typeof(UObject).IsAssignableFrom(t))
            {
                var o = v as UObject;
                sb.Append("{\"instanceID\":").Append(o != null ? o.GetInstanceID() : 0).Append('}');
                return;
            }
            if (t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)))
            {
                var et = t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0];
                sb.Append('[');
                bool first = true;
                if (v is IEnumerable e)
                    foreach (var x in e)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Indent(sb, pretty, level + 1);
                        WriteValue(sb, x, et, pretty, level + 1);
                    }
                if (!first) Indent(sb, pretty, level);
                sb.Append(']');
                return;
            }
            if (v == null) v = FieldDecoder.New(t);   // Unity never writes null for serializable classes
            WriteObject(sb, v, t, pretty, level);
        }

        static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ reading

        public static object FromJson(string json, Type t)
        {
            if (string.IsNullOrEmpty(json)) return null;
            if (typeof(UObject).IsAssignableFrom(t)) throw new ArgumentException("Cannot deserialize JSON to new instances of type '" + t.Name + ".'");
            var obj = FieldDecoder.New(t);
            Overwrite(json, obj);
            return obj;
        }

        public static void Overwrite(string json, object obj)
        {
            if (string.IsNullOrEmpty(json) || obj == null) return;
            int p = 0;
            var tree = Parse(json, ref p);
            if (tree is Dictionary<string, object> m) Assign(obj, obj.GetType(), m, 0);
            else throw new ArgumentException("JSON parse error: the document root must be an object.");
        }

        static void Assign(object obj, Type t, Dictionary<string, object> m, int depth)
        {
            foreach (var f in Serialization.Fields(t))
            {
                if (!m.TryGetValue(f.Name, out var v)) continue;
                f.SetValue(obj, Convert(v, f.FieldType, f.GetValue(obj), depth));
            }
        }

        static object Convert(object v, Type t, object current, int depth)
        {
            if (t.IsEnum) return Enum.ToObject(t, System.Convert.ToInt64(v is double d0 ? Math.Round(d0) : v ?? 0L, CultureInfo.InvariantCulture));
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: return v is bool b ? b : v is double db && db != 0;
                case TypeCode.String: return v as string ?? "";
                case TypeCode.Single: return v is double f ? (float)f : 0f;
                case TypeCode.Double: return v is double dd ? dd : 0.0;
                case TypeCode.Char: return v is double dc ? (char)(int)dc : '\0';
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                    return System.Convert.ChangeType(v is double n ? Math.Truncate(n) : 0.0, t, CultureInfo.InvariantCulture);
            }
            if (typeof(UObject).IsAssignableFrom(t))
            {
                long id = v is Dictionary<string, object> r && r.TryGetValue("instanceID", out var iv) && iv is double di ? (long)di : 0;
                var rec = id == 0 ? null : Objects.ByInstanceId((int)id);
                return rec != null && t.IsInstanceOfType(rec.Managed) ? rec.Managed : null;
            }
            if (t.IsArray)
            {
                var et = t.GetElementType();
                var l = v as List<object> ?? new List<object>();
                var arr = Array.CreateInstance(et, l.Count);
                for (int i = 0; i < l.Count; i++) arr.SetValue(Convert(l[i], et, null, depth + 1), i);
                return arr;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(t);
                foreach (var x in v as List<object> ?? new List<object>()) list.Add(Convert(x, et, null, depth + 1));
                return list;
            }
            var inst = current ?? FieldDecoder.New(t);
            if (v is Dictionary<string, object> m) Assign(inst, t, m, depth + 1);
            return inst;
        }

        static object Parse(string s, ref int p)
        {
            Ws(s, ref p);
            if (p >= s.Length) throw new ArgumentException("JSON parse error: unexpected end.");
            char c = s[p];
            if (c == '{')
            {
                p++;
                var m = new Dictionary<string, object>(StringComparer.Ordinal);
                Ws(s, ref p);
                if (p < s.Length && s[p] == '}') { p++; return m; }
                while (true)
                {
                    Ws(s, ref p);
                    string k = Str(s, ref p);
                    Ws(s, ref p);
                    if (p >= s.Length || s[p] != ':') throw new ArgumentException("JSON parse error: missing ':'.");
                    p++;
                    m[k] = Parse(s, ref p);
                    Ws(s, ref p);
                    if (p < s.Length && s[p] == ',') { p++; continue; }
                    if (p < s.Length && s[p] == '}') { p++; return m; }
                    throw new ArgumentException("JSON parse error: missing ',' or '}'.");
                }
            }
            if (c == '[')
            {
                p++;
                var l = new List<object>();
                Ws(s, ref p);
                if (p < s.Length && s[p] == ']') { p++; return l; }
                while (true)
                {
                    l.Add(Parse(s, ref p));
                    Ws(s, ref p);
                    if (p < s.Length && s[p] == ',') { p++; continue; }
                    if (p < s.Length && s[p] == ']') { p++; return l; }
                    throw new ArgumentException("JSON parse error: missing ',' or ']'.");
                }
            }
            if (c == '"') return Str(s, ref p);
            if (string.CompareOrdinal(s, p, "true", 0, 4) == 0) { p += 4; return true; }
            if (string.CompareOrdinal(s, p, "false", 0, 5) == 0) { p += 5; return false; }
            if (string.CompareOrdinal(s, p, "null", 0, 4) == 0) { p += 4; return null; }
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            if (p == start) throw new ArgumentException("JSON parse error: invalid value at " + p + ".");
            return double.Parse(s.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static void Ws(string s, ref int p) { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

        static string Str(string s, ref int p)
        {
            if (p >= s.Length || s[p] != '"') throw new ArgumentException("JSON parse error: expected a string.");
            p++;
            var sb = new StringBuilder();
            while (p < s.Length && s[p] != '"')
            {
                char c = s[p++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[p++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber)); p += 4; break;
                    default: sb.Append(e); break;
                }
            }
            p++;
            return sb.ToString();
        }
    }

    public static partial class Icalls
    {
        public static string UnityEngine_JsonUtility__ToJson(object obj, bool prettyPrint) => Json.ToJson(obj, prettyPrint);
        public static object UnityEngine_JsonUtility__FromJson(string json, Type type) => Json.FromJson(json, type);
        public static void UnityEngine_JsonUtility__FromJsonOverwrite(string json, object objectToOverwrite) => Json.Overwrite(json, objectToOverwrite);
    }
}
