#include "runtime/interpreter.h"
#include "core/endian.h"
#include "core/log.h"
#include "core/mem.h"

#include <math.h>
#include <string.h>
#include <stdlib.h>

namespace ch360 {

typedef bool (*NativeFn)(Interpreter&, const MemberRefInfo&, u32, Value*, Value*);

struct NativeEntry {
    const char* type;
    const char* name;
    NativeFn fn;
};

static f32 Clamp01f(f32 v) { return v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v); }
static f32 Clampf(f32 v, f32 lo, f32 hi) { return v < lo ? lo : (v > hi ? hi : v); }
static f64 Clamp01d(f64 v) { return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v); }

static bool RetF32(Value* ret, f32 v) { if (ret) *ret = Value::R4(v); return true; }
static bool RetF64(Value* ret, f64 v) { if (ret) *ret = Value::R8(v); return true; }
static bool RetI32(Value* ret, s32 v) { if (ret) *ret = Value::I4(v); return true; }
static bool RetBool(Value* ret, bool v) { if (ret) *ret = Value::Bool(v); return true; }
static bool RetVoid(Value* ret) { if (ret) *ret = Value::Void(); return true; }

// ---------------------------------------------------------------------------
// UnityEngine.Mathf
// ---------------------------------------------------------------------------

static bool Mathf_Lerp(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, a[0].data.f32 + (a[1].data.f32 - a[0].data.f32) * Clamp01f(a[2].data.f32));
}
static bool Mathf_LerpUnclamped(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, a[0].data.f32 + (a[1].data.f32 - a[0].data.f32) * a[2].data.f32);
}
static bool Mathf_Clamp(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, Clampf(a[0].data.f32, a[1].data.f32, a[2].data.f32));
}
static bool Mathf_Clamp01(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, Clamp01f(a[0].data.f32));
}
static bool Mathf_Abs(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, fabsf(a[0].data.f32));
}
static bool Mathf_Sin(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, sinf(a[0].data.f32));
}
static bool Mathf_Cos(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, cosf(a[0].data.f32));
}
static bool Mathf_Pow(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, powf(a[0].data.f32, a[1].data.f32));
}
static bool Mathf_Sqrt(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, sqrtf(a[0].data.f32));
}
static bool Mathf_Floor(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, floorf(a[0].data.f32));
}
static bool Mathf_Ceil(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, ceilf(a[0].data.f32));
}
static bool Mathf_Round(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, floorf(a[0].data.f32 + 0.5f));
}
static bool Mathf_Min(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, a[0].data.f32 < a[1].data.f32 ? a[0].data.f32 : a[1].data.f32);
}
static bool Mathf_Max(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, a[0].data.f32 > a[1].data.f32 ? a[0].data.f32 : a[1].data.f32);
}
static bool Mathf_Sign(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF32(ret, a[0].data.f32 < 0.0f ? -1.0f : 1.0f);
}
static bool Mathf_Approximately(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    f32 d = a[0].data.f32 - a[1].data.f32;
    if (d < 0) d = -d;
    return RetBool(ret, d < 0.000001f);
}

// ---------------------------------------------------------------------------
// System.Math
// ---------------------------------------------------------------------------

static bool Math_Abs(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, fabs(a[0].data.f64));
}
static bool Math_Sin(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, sin(a[0].data.f64));
}
static bool Math_Cos(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, cos(a[0].data.f64));
}
static bool Math_Pow(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, pow(a[0].data.f64, a[1].data.f64));
}
static bool Math_Sqrt(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, sqrt(a[0].data.f64));
}
static bool Math_Floor(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, floor(a[0].data.f64));
}
static bool Math_Ceiling(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, ceil(a[0].data.f64));
}
static bool Math_Round(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetF64(ret, floor(a[0].data.f64 + 0.5));
}

// ---------------------------------------------------------------------------
// System.Object and System.String
// ---------------------------------------------------------------------------

static u32 StringLength(const Value& v) {
    return (v.tag == VT_STRING && v.data.str) ? v.data.str->length : 0;
}
static const u16* StringChars(const Value& v) {
    return (v.tag == VT_STRING && v.data.str) ? v.data.str->Chars() : 0;
}
static bool StringEquals(const Value& a, const Value& b) {
    if (a.tag != VT_STRING || b.tag != VT_STRING) return a.data.obj == b.data.obj;
    if (a.data.str == b.data.str) return true;
    u32 al = StringLength(a), bl = StringLength(b);
    if (al != bl) return false;
    const u16* ac = StringChars(a);
    const u16* bc = StringChars(b);
    for (u32 i = 0; i < al; ++i) if (ac[i] != bc[i]) return false;
    return true;
}

static bool Object_ctor(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetVoid(ret);
}
static bool Object_ToString(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (ret) {
        if (a[0].tag == VT_STRING) { *ret = a[0]; return true; }
        if (a[0].tag == VT_OBJECT || a[0].tag == VT_STRUCT) {
            Object* o = a[0].data.obj;
            if (o) {
                const TypeInfo& t = in.Metadata().Type(o->type_id);
                if (t.is_enum) {
                    s64 value = o->slots > 0 ? Interpreter::ToI64(o->Fields()[0]) : 0;
                    for (u32 i = 0; i < t.field_count; ++i) {
                        u32 fid = t.field_start + i;
                        const FieldInfo& f = in.Metadata().Field(fid);
                        if (!(f.flags & FIELD_FLAG_LITERAL) || strcmp(f.name, "value__") == 0) continue;
                        s64 constant = 0;
                        if (in.Metadata().FieldConstant(fid, &constant) && constant == value) {
                            *ret = in.NewStringAscii(f.name);
                            return true;
                        }
                    }
                    char buf[32];
                    sprintf(buf, "%d", (s32)value);
                    *ret = in.NewStringAscii(buf);
                    return true;
                }
            }
        }
        if (a[0].tag == VT_I4) {
            char buf[32];
            sprintf(buf, "%d", a[0].data.i32);
            *ret = in.NewStringAscii(buf);
            return true;
        }
        *ret = in.NewStringAscii("Object");
    }
    return true;
}
static bool Object_Equals(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_STRING && a[1].tag == VT_STRING) return RetBool(ret, StringEquals(a[0], a[1]));
    return RetBool(ret, a[0].data.obj == a[1].data.obj);
}
static bool Object_GetHashCode(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetI32(ret, (s32)(size_t)a[0].data.obj);
}
static bool Object_ReferenceEquals(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetBool(ret, a[0].data.obj == a[1].data.obj);
}

static bool String_Concat2(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 al = StringLength(a[0]), bl = StringLength(a[1]);
    u16* out = (u16*)MemAlloc((al + bl + 1) * 2);
    if (!out) return false;
    const u16* ac = StringChars(a[0]);
    const u16* bc = StringChars(a[1]);
    for (u32 i = 0; i < al; ++i) out[i] = ac ? ac[i] : 0;
    for (u32 i = 0; i < bl; ++i) out[al + i] = bc ? bc[i] : 0;
    if (ret) *ret = in.NewStringUtf16(out, al + bl);
    MemFree(out);
    return true;
}
static bool String_Concat3(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Value ab = Value::Null();
    u32 al = StringLength(a[0]), bl = StringLength(a[1]);
    u16* tmp = (u16*)MemAlloc((al + bl + 1) * 2);
    if (!tmp) return false;
    const u16* ac = StringChars(a[0]);
    const u16* bc = StringChars(a[1]);
    for (u32 i = 0; i < al; ++i) tmp[i] = ac ? ac[i] : 0;
    for (u32 i = 0; i < bl; ++i) tmp[al + i] = bc ? bc[i] : 0;
    ab = in.NewStringUtf16(tmp, al + bl);
    MemFree(tmp);
    Value pair[2];
    pair[0] = ab; pair[1] = a[2];
    return String_Concat2(in, *(const MemberRefInfo*)0, 2, pair, ret);
}
static bool String_Length(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetI32(ret, (s32)StringLength(a[0]));
}
static bool String_Chars(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 i = (u32)Interpreter::ToI32(a[1]);
    if (i >= StringLength(a[0])) return RetI32(ret, 0);
    return RetI32(ret, StringChars(a[0])[i]);
}
static bool String_IsNullOrEmpty(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetBool(ret, a[0].tag == VT_NULL || StringLength(a[0]) == 0);
}
static bool String_Equality(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetBool(ret, StringEquals(a[0], a[1]));
}
static bool String_Inequality(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetBool(ret, !StringEquals(a[0], a[1]));
}
static bool String_Substring1(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 start = (u32)Interpreter::ToI32(a[1]);
    u32 n = StringLength(a[0]);
    if (start > n) start = n;
    if (ret) *ret = in.NewStringUtf16(StringChars(a[0]) + start, n - start);
    return true;
}
static bool String_Substring2(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 start = (u32)Interpreter::ToI32(a[1]);
    u32 len = (u32)Interpreter::ToI32(a[2]);
    u32 n = StringLength(a[0]);
    if (start > n) start = n;
    if (len > n - start) len = n - start;
    if (ret) *ret = in.NewStringUtf16(StringChars(a[0]) + start, len);
    return true;
}
static bool String_ToUpper(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 n = StringLength(a[0]);
    u16* out = (u16*)MemAlloc((n + 1) * 2);
    if (!out) return false;
    const u16* s = StringChars(a[0]);
    for (u32 i = 0; i < n; ++i) out[i] = (s[i] >= 'a' && s[i] <= 'z') ? (u16)(s[i] - 32) : s[i];
    if (ret) *ret = in.NewStringUtf16(out, n);
    MemFree(out);
    return true;
}
static bool String_ToLower(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 n = StringLength(a[0]);
    u16* out = (u16*)MemAlloc((n + 1) * 2);
    if (!out) return false;
    const u16* s = StringChars(a[0]);
    for (u32 i = 0; i < n; ++i) out[i] = (s[i] >= 'A' && s[i] <= 'Z') ? (u16)(s[i] + 32) : s[i];
    if (ret) *ret = in.NewStringUtf16(out, n);
    MemFree(out);
    return true;
}
static bool String_Trim(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 n = StringLength(a[0]);
    const u16* s = StringChars(a[0]);
    u32 b = 0, e = n;
    while (b < e && (s[b] == ' ' || s[b] == '\t' || s[b] == '\r' || s[b] == '\n')) ++b;
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\r' || s[e - 1] == '\n')) --e;
    if (ret) *ret = in.NewStringUtf16(s + b, e - b);
    return true;
}
static s32 StringFind(const Value& hay, const Value& needle) {
    u32 n = StringLength(hay), m = StringLength(needle);
    const u16* s = StringChars(hay);
    const u16* t = StringChars(needle);
    if (m == 0) return 0;
    if (m > n) return -1;
    for (u32 i = 0; i + m <= n; ++i) {
        u32 j = 0;
        while (j < m && s[i + j] == t[j]) ++j;
        if (j == m) return (s32)i;
    }
    return -1;
}
static bool String_Contains(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetBool(ret, StringFind(a[0], a[1]) >= 0);
}
static bool String_StartsWith(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 n = StringLength(a[0]), m = StringLength(a[1]);
    if (m > n) return RetBool(ret, false);
    const u16* s = StringChars(a[0]);
    const u16* t = StringChars(a[1]);
    for (u32 i = 0; i < m; ++i) if (s[i] != t[i]) return RetBool(ret, false);
    return RetBool(ret, true);
}
static bool String_EndsWith(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 n = StringLength(a[0]), m = StringLength(a[1]);
    if (m > n) return RetBool(ret, false);
    const u16* s = StringChars(a[0]);
    const u16* t = StringChars(a[1]);
    for (u32 i = 0; i < m; ++i) if (s[n - m + i] != t[i]) return RetBool(ret, false);
    return RetBool(ret, true);
}
static bool String_IndexOf(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetI32(ret, StringFind(a[0], a[1]));
}
static bool Char_IsUpper(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    s32 c = Interpreter::ToI32(a[0]);
    return RetBool(ret, c >= 'A' && c <= 'Z');
}
static bool Char_IsLower(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    s32 c = Interpreter::ToI32(a[0]);
    return RetBool(ret, c >= 'a' && c <= 'z');
}
static bool Char_IsDigit(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    s32 c = Interpreter::ToI32(a[0]);
    return RetBool(ret, c >= '0' && c <= '9');
}
static bool Char_IsLetter(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    s32 c = Interpreter::ToI32(a[0]);
    return RetBool(ret, (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
}
static bool Char_IsWhiteSpace(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    s32 c = Interpreter::ToI32(a[0]);
    return RetBool(ret, c == ' ' || c == '\t' || c == '\r' || c == '\n');
}
static bool Char_ToLower(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    s32 c = Interpreter::ToI32(a[0]);
    return RetI32(ret, (c >= 'A' && c <= 'Z') ? c + 32 : c);
}
static bool Char_ToUpper(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    s32 c = Interpreter::ToI32(a[0]);
    return RetI32(ret, (c >= 'a' && c <= 'z') ? c - 32 : c);
}

// ---------------------------------------------------------------------------
// UnityEngine.Vector2 / Vector3 (value types)
// ---------------------------------------------------------------------------

static Vec2* AsVec2(const Value& v) {
    Value x = v;
    if (x.tag == VT_BYREF && x.data.ref) x = *x.data.ref;
    return (x.tag == VT_VEC2 && x.data.ptr) ? (Vec2*)x.data.ptr : 0;
}
static Vec3* AsVec3(const Value& v) {
    Value x = v;
    if (x.tag == VT_BYREF && x.data.ref) x = *x.data.ref;
    return (x.tag == VT_VEC3 && x.data.ptr) ? (Vec3*)x.data.ptr : 0;
}

static bool Vec2_Ctor(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_BYREF && a[0].data.ref) {
        *a[0].data.ref = in.NewVec2(a[1].data.f32, a[2].data.f32);
        return RetVoid(ret);
    }
    if (ret) *ret = in.NewVec2(a[1].data.f32, a[2].data.f32);
    return true;
}
static bool Vec2_GetX(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* v = AsVec2(a[0]); return v ? RetF32(ret, v->x) : false;
}
static bool Vec2_GetY(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* v = AsVec2(a[0]); return v ? RetF32(ret, v->y) : false;
}
static bool Vec2_SetX(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* v = AsVec2(a[0]); if (!v) return false; v->x = a[1].data.f32; return RetVoid(ret);
}
static bool Vec2_SetY(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* v = AsVec2(a[0]); if (!v) return false; v->y = a[1].data.f32; return RetVoid(ret);
}
static bool Vec2_Zero(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec2(0.0f, 0.0f); return true;
}
static bool Vec2_One(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec2(1.0f, 1.0f); return true;
}
static bool Vec2_Left(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec2(-1.0f, 0.0f); return true;
}
static bool Vec2_Right(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec2(1.0f, 0.0f); return true;
}
static bool Vec2_Up(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec2(0.0f, 1.0f); return true;
}
static bool Vec2_Down(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec2(0.0f, -1.0f); return true;
}
static bool Vec2_Normalized(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* v = AsVec2(a[0]);
    if (!v) { if (ret) *ret = in.NewVec2(0.0f, 0.0f); return true; }
    f32 m = sqrtf(v->x * v->x + v->y * v->y);
    if (m < 0.000001f) { if (ret) *ret = in.NewVec2(0.0f, 0.0f); return true; }
    if (ret) *ret = in.NewVec2(v->x / m, v->y / m);
    return true;
}
static bool Vec2_Distance(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]); Vec2* y = AsVec2(a[1]);
    if (!x || !y) return false;
    f32 dx = x->x - y->x, dy = x->y - y->y;
    return RetF32(ret, sqrtf(dx * dx + dy * dy));
}
static bool Vec2_Dot(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]); Vec2* y = AsVec2(a[1]);
    if (!x || !y) return false;
    return RetF32(ret, x->x * y->x + x->y * y->y);
}
static bool Vec2_Add(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]); Vec2* y = AsVec2(a[1]);
    if (!x || !y) return false;
    if (ret) *ret = in.NewVec2(x->x + y->x, x->y + y->y);
    return true;
}
static bool Vec2_Sub(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]); Vec2* y = AsVec2(a[1]);
    if (!x || !y) return false;
    if (ret) *ret = in.NewVec2(x->x - y->x, x->y - y->y);
    return true;
}
static bool Vec2_Mul(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]);
    if (!x) return false;
    f32 s = (a[1].tag == VT_VEC2) ? 0.0f : a[1].data.f32;
    if (a[1].tag == VT_VEC2) {
        Vec2* y = AsVec2(a[1]);
        if (!y) return false;
        if (ret) *ret = in.NewVec2(x->x * y->x, x->y * y->y);
    } else if (ret) {
        *ret = in.NewVec2(x->x * s, x->y * s);
    }
    return true;
}
static bool Vec2_Div(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]);
    if (!x) return false;
    f32 s = a[1].data.f32;
    if (ret) *ret = in.NewVec2(x->x / s, x->y / s);
    return true;
}
static bool Vec2_Magnitude(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]);
    return x ? RetF32(ret, sqrtf(x->x * x->x + x->y * x->y)) : false;
}
static bool Vec2_SqrMagnitude(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]);
    return x ? RetF32(ret, x->x * x->x + x->y * x->y) : false;
}
static bool Vec2_Lerp(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec2* x = AsVec2(a[0]); Vec2* y = AsVec2(a[1]);
    if (!x || !y) return false;
    f32 t = Clamp01f(a[2].data.f32);
    if (ret) *ret = in.NewVec2(x->x + (y->x - x->x) * t, x->y + (y->y - x->y) * t);
    return true;
}
static bool Vec2_ImplicitV3(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_VEC2 && a[0].data.ptr) {
        const Vec2* x = (const Vec2*)a[0].data.ptr;
        if (ret) *ret = in.NewVec3(x->x, x->y, 0.0f);
        return true;
    }
    if (a[0].tag == VT_VEC3 && a[0].data.ptr) {
        const Vec3* x = (const Vec3*)a[0].data.ptr;
        if (ret) *ret = in.NewVec2(x->x, x->y);
        return true;
    }
    return false;
}
static bool Vec3_GetX(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec3* v = AsVec3(a[0]); return v ? RetF32(ret, v->x) : false;
}
static bool Vec3_GetY(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec3* v = AsVec3(a[0]); return v ? RetF32(ret, v->y) : false;
}
static bool Vec3_GetZ(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Vec3* v = AsVec3(a[0]); return v ? RetF32(ret, v->z) : false;
}

// ---------------------------------------------------------------------------
// System.Collections.Generic.List<T>
// ---------------------------------------------------------------------------

static ListObject* AsList(const Value& v) {
    if (v.tag != VT_OBJECT || !v.data.obj) return 0;
    return (ListObject*)v.data.obj;
}

static u32 ListElementType(Interpreter& in, const ListObject* list) {
    if (!list) return 0;
    const TypeInfo& t = in.Metadata().Type(list->type_id);
    return t.generic_arg_count ? in.Metadata().GenericArg(t.generic_arg_start) : 0;
}

static bool EnumValueNumber(Interpreter& in, const Value& v, s64* out) {
    if (v.tag != VT_OBJECT && v.tag != VT_STRUCT) return false;
    Object* o = v.data.obj;
    if (!o) return false;
    const TypeInfo& t = in.Metadata().Type(o->type_id);
    if (!t.is_enum) return false;
    *out = o->slots > 0 ? Interpreter::ToI64(o->Fields()[0]) : 0;
    return true;
}

static bool ValueEqual(Interpreter& in, const Value& a, const Value& b) {
    if (a.tag == VT_STRING || b.tag == VT_STRING) return StringEquals(a, b);
    if (a.tag == VT_NULL || b.tag == VT_NULL) return a.data.obj == b.data.obj;
    s64 ae = 0, be = 0;
    bool a_enum = EnumValueNumber(in, a, &ae);
    bool b_enum = EnumValueNumber(in, b, &be);
    if (a_enum && b_enum) return ae == be;
    if (a_enum && (b.tag == VT_I1 || b.tag == VT_U1 || b.tag == VT_I2 ||
                   b.tag == VT_U2 || b.tag == VT_I4 || b.tag == VT_U4 ||
                   b.tag == VT_I8 || b.tag == VT_U8)) {
        return ae == Interpreter::ToI64(b);
    }
    if (b_enum && (a.tag == VT_I1 || a.tag == VT_U1 || a.tag == VT_I2 ||
                   a.tag == VT_U2 || a.tag == VT_I4 || a.tag == VT_U4 ||
                   a.tag == VT_I8 || a.tag == VT_U8)) {
        return be == Interpreter::ToI64(a);
    }
    if (a.tag == VT_OBJECT || a.tag == VT_ARRAY || a.tag == VT_STRUCT || a.tag == VT_DELEGATE) {
        return a.data.obj == b.data.obj;
    }
    if (a.tag == VT_R4 || a.tag == VT_R8 || b.tag == VT_R4 || b.tag == VT_R8) {
        return Interpreter::ToF64(a) == Interpreter::ToF64(b);
    }
    return Interpreter::ToI64(a) == Interpreter::ToI64(b);
}

static bool List_Ensure(Interpreter& in, ListObject* list, u32 count) {
    if (count <= list->capacity) return true;
    u32 cap = list->capacity ? list->capacity : 4;
    while (cap < count) cap *= 2;
    Value* fresh = (Value*)MemAlloc(cap * sizeof(Value));
    if (!fresh) return false;
    for (u32 i = 0; i < list->count; ++i) fresh[i] = list->items[i];
    for (u32 i = list->count; i < cap; ++i) fresh[i] = Value::Null();
    MemFree(list->items);
    list->items = fresh;
    list->capacity = cap;
    (void)in;
    return true;
}

static bool List_Ctor(Interpreter& in, const MemberRefInfo& member, u32 argc, Value* a, Value* ret) {
    (void)argc; (void)a;
    ListObject* list = (ListObject*)in.TrackAllocation(MemAlloc(sizeof(ListObject)));
    if (!list) return false;
    memset(list, 0, sizeof(ListObject));
    list->type_id = member.decl_id;
    list->element_type = 0;
    const TypeInfo& t = in.Metadata().Type(member.decl_id);
    if (t.generic_arg_count) list->element_type = in.Metadata().GenericArg(t.generic_arg_start);
    if (ret) *ret = Value::ObjectRef((Object*)list);
    return true;
}

static bool List_Add(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    (void)argc;
    ListObject* list = AsList(a[0]);
    if (!list || !List_Ensure(in, list, list->count + 1)) return false;
    list->items[list->count++] = in.CopyValue(a[1]);
    if (ret) *ret = Value::Void();
    return true;
}

static bool List_GetCount(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    return RetI32(ret, list ? (s32)list->count : 0);
}

static bool List_ToArray(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    u32 elem = ListElementType(in, list);
    Value arr = in.NewArrayValue(elem, list ? list->count : 0);
    if (arr.tag == VT_ARRAY && arr.data.array && list) {
        for (u32 i = 0; i < list->count; ++i) {
            arr.data.array->Elements()[i] = in.CopyValue(list->items[i]);
        }
    }
    if (ret) *ret = arr;
    return true;
}

static bool List_GetItem(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    s32 i = Interpreter::ToI32(a[1]);
    if (!list || i < 0 || (u32)i >= list->count) return false;
    if (ret) *ret = list->items[i];
    return true;
}

static bool List_SetItem(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    s32 i = Interpreter::ToI32(a[1]);
    if (!list || i < 0 || (u32)i >= list->count) return false;
    list->items[i] = in.CopyValue(a[2]);
    if (ret) *ret = Value::Void();
    return true;
}

static bool List_Clear(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    if (list) list->count = 0;
    if (ret) *ret = Value::Void();
    return true;
}

static bool List_Contains(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    if (!list) return RetBool(ret, false);
    for (u32 i = 0; i < list->count; ++i) {
        if (ValueEqual(in, list->items[i], a[1])) return RetBool(ret, true);
    }
    return RetBool(ret, false);
}

static bool List_IndexOf(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    if (!list) return RetI32(ret, -1);
    for (u32 i = 0; i < list->count; ++i) {
        if (ValueEqual(in, list->items[i], a[1])) return RetI32(ret, (s32)i);
    }
    return RetI32(ret, -1);
}

static bool List_RemoveAt(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    s32 i = Interpreter::ToI32(a[1]);
    if (!list || i < 0 || (u32)i >= list->count) return false;
    for (u32 j = (u32)i; j + 1 < list->count; ++j) list->items[j] = list->items[j + 1];
    --list->count;
    if (ret) *ret = Value::Void();
    return true;
}

static bool List_Remove(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    if (!list) return RetBool(ret, false);
    for (u32 i = 0; i < list->count; ++i) {
        if (ValueEqual(in, list->items[i], a[1])) {
            for (u32 j = i; j + 1 < list->count; ++j) list->items[j] = list->items[j + 1];
            --list->count;
            return RetBool(ret, true);
        }
    }
    return RetBool(ret, false);
}

static bool List_Sort(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    if (!list || list->count < 2) return RetVoid(ret);
    bool has_cmp = argc >= 2 && a[1].tag == VT_DELEGATE && a[1].data.del;
    for (u32 i = 1; i < list->count; ++i) {
        Value key = list->items[i];
        s32 j = (s32)i - 1;
        while (j >= 0) {
            s32 cmp = 0;
            if (has_cmp) {
                DelegateObject* d = a[1].data.del;
                Value cargs[3];
                u32 cargc = 0;
                if (d->flags & 1u) {
                    cargs[0] = list->items[j];
                    cargs[1] = key;
                    cargc = 2;
                } else {
                    cargs[0] = d->target;
                    cargs[1] = list->items[j];
                    cargs[2] = key;
                    cargc = 3;
                }
                Value r;
                if (!in.Invoke(d->method_id, cargs, cargc, &r)) return false;
                cmp = Interpreter::ToI32(r);
            } else if (list->items[j].tag == VT_STRING && key.tag == VT_STRING) {
                const u16* x = list->items[j].data.str ? list->items[j].data.str->Chars() : 0;
                const u16* y = key.data.str ? key.data.str->Chars() : 0;
                u32 xn = list->items[j].data.str ? list->items[j].data.str->length : 0;
                u32 yn = key.data.str ? key.data.str->length : 0;
                u32 n = xn < yn ? xn : yn;
                for (u32 k = 0; k < n && cmp == 0; ++k) {
                    if (x[k] < y[k]) cmp = -1;
                    else if (x[k] > y[k]) cmp = 1;
                }
                if (cmp == 0) cmp = xn < yn ? -1 : (xn > yn ? 1 : 0);
            } else {
                f64 x = Interpreter::ToF64(list->items[j]);
                f64 y = Interpreter::ToF64(key);
                cmp = x < y ? -1 : (x > y ? 1 : 0);
            }
            if (cmp <= 0) break;
            list->items[j + 1] = list->items[j];
            --j;
        }
        list->items[j + 1] = key;
    }
    return RetVoid(ret);
}

static bool List_GetEnumerator(Interpreter& in, const MemberRefInfo& member, u32, Value* a, Value* ret) {    ListObject* list = AsList(a[0]);
    if (!list) return false;
    const MethodSigInfo& ms = in.Metadata().MethodSig(member.sig_index);
    u32 et = in.Metadata().Sig(ms.return_sig).type_id;
    Object* e = in.AllocObjectWithSlots(et, 2);
    if (!e) return false;
    e->Fields()[0] = a[0];
    e->Fields()[1] = Value::I4(-1);
    if (ret) *ret = Value::StructRef(e);
    return true;
}

static bool List_Enumerator_MoveNext(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_STRUCT || !a[0].data.obj) return RetBool(ret, false);
    Object* e = a[0].data.obj;
    ListObject* list = AsList(e->Fields()[0]);
    if (!list) return RetBool(ret, false);
    s32 i = Interpreter::ToI32(e->Fields()[1]) + 1;
    e->Fields()[1] = Value::I4(i);
    return RetBool(ret, i >= 0 && (u32)i < list->count);
}

static bool List_Enumerator_Current(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_STRUCT || !a[0].data.obj) return false;
    Object* e = a[0].data.obj;
    ListObject* list = AsList(e->Fields()[0]);
    s32 i = Interpreter::ToI32(e->Fields()[1]);
    if (!list || i < 0 || (u32)i >= list->count) return false;
    if (ret) *ret = list->items[i];
    return true;
}

static bool List_Enumerator_Dispose(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = Value::Void();
    return true;
}

static bool IsListName(const char* name) {
    return name && strncmp(name, "System.Collections.Generic.List`1", 33) == 0;
}

// ---------------------------------------------------------------------------
// System.Collections.Generic.Dictionary<K,V>
// ---------------------------------------------------------------------------

static DictionaryObject* AsDict(const Value& v) {
    if (v.tag != VT_OBJECT || !v.data.obj) return 0;
    return (DictionaryObject*)v.data.obj;
}

static bool Dict_Ensure(DictionaryObject* d, u32 capacity) {
    if (capacity <= d->capacity) return true;
    u32 cap = d->capacity ? d->capacity : 8;
    while (cap < capacity) cap *= 2;
    DictEntry* fresh = (DictEntry*)MemAlloc(cap * sizeof(DictEntry));
    if (!fresh) return false;
    memset(fresh, 0, cap * sizeof(DictEntry));
    for (u32 i = 0; i < d->capacity; ++i) {
        if (!d->entries[i].used) continue;
        u32 j = 0;
        while (j < cap && fresh[j].used) ++j;
        if (j < cap) fresh[j] = d->entries[i];
    }
    MemFree(d->entries);
    d->entries = fresh;
    d->capacity = cap;
    return true;
}

static s32 Dict_Find(Interpreter& in, DictionaryObject* d, const Value& key) {
    if (!d) return -1;
    for (u32 i = 0; i < d->capacity; ++i) {
        if (d->entries[i].used && ValueEqual(in, d->entries[i].key, key)) return (s32)i;
    }
    return -1;
}

static bool Dict_Ctor(Interpreter& in, const MemberRefInfo& member, u32 argc, Value* a, Value* ret) {
    (void)argc; (void)a;
    DictionaryObject* d = (DictionaryObject*)in.TrackAllocation(MemAlloc(sizeof(DictionaryObject)));
    if (!d) return false;
    memset(d, 0, sizeof(DictionaryObject));
    d->type_id = member.decl_id;
    const TypeInfo& t = in.Metadata().Type(member.decl_id);
    if (t.generic_arg_count >= 1) d->key_type = in.Metadata().GenericArg(t.generic_arg_start);
    if (t.generic_arg_count >= 2) d->value_type = in.Metadata().GenericArg(t.generic_arg_start + 1);
    if (ret) *ret = Value::ObjectRef((Object*)d);
    return true;
}

static bool Dict_Add(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    if (!d || Dict_Find(in, d, a[1]) >= 0 || !Dict_Ensure(d, d->count + 1)) return false;
    for (u32 i = 0; i < d->capacity; ++i) {
        if (d->entries[i].used) continue;
        d->entries[i].key = in.CopyValue(a[1]);
        d->entries[i].value = in.CopyValue(a[2]);
        d->entries[i].used = 1;
        ++d->count;
        if (ret) *ret = Value::Void();
        return true;
    }
    return false;
}

static bool Dict_SetItem(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    if (!d || !Dict_Ensure(d, d->count + 1)) return false;
    s32 at = Dict_Find(in, d, a[1]);
    if (at < 0) {
        for (u32 i = 0; i < d->capacity; ++i) {
            if (!d->entries[i].used) { at = (s32)i; break; }
        }
        if (at < 0) return false;
        d->entries[at].key = in.CopyValue(a[1]);
        d->entries[at].used = 1;
        ++d->count;
    }
    d->entries[at].value = in.CopyValue(a[2]);
    if (ret) *ret = Value::Void();
    return true;
}

static bool Dict_GetItem(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    s32 at = Dict_Find(in, d, a[1]);
    if (at < 0) return false;
    if (ret) *ret = d->entries[at].value;
    return true;
}

static bool Dict_TryGetValue(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    s32 at = Dict_Find(in, d, a[1]);
    bool found = at >= 0;
    if (a[2].tag == VT_BYREF && a[2].data.ref) {
        *a[2].data.ref = found ? d->entries[at].value : Value::Null();
    }
    return RetBool(ret, found);
}

static bool Dict_ContainsKey(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    return RetBool(ret, Dict_Find(in, AsDict(a[0]), a[1]) >= 0);
}

static bool Dict_ContainsValue(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    if (!d) return RetBool(ret, false);
    for (u32 i = 0; i < d->capacity; ++i) {
        if (d->entries[i].used && ValueEqual(in, d->entries[i].value, a[1])) return RetBool(ret, true);
    }
    return RetBool(ret, false);
}

static bool Dict_GetCount(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    return RetI32(ret, d ? (s32)d->count : 0);
}

static bool Dict_GetKeys(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    u32 key_type = d ? d->key_type : 0;
    Value arr = in.NewArrayValue(key_type, d ? d->count : 0);
    if (arr.tag == VT_ARRAY && arr.data.array && d) {
        u32 j = 0;
        for (u32 i = 0; i < d->capacity && j < d->count; ++i) {
            if (!d->entries[i].used) continue;
            arr.data.array->Elements()[j++] = in.CopyValue(d->entries[i].key);
        }
    }
    if (ret) *ret = arr;
    return true;
}

static bool Dict_GetValues(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    u32 value_type = d ? d->value_type : 0;
    Value arr = in.NewArrayValue(value_type, d ? d->count : 0);
    if (arr.tag == VT_ARRAY && arr.data.array && d) {
        u32 j = 0;
        for (u32 i = 0; i < d->capacity && j < d->count; ++i) {
            if (!d->entries[i].used) continue;
            arr.data.array->Elements()[j++] = in.CopyValue(d->entries[i].value);
        }
    }
    if (ret) *ret = arr;
    return true;
}

static bool Dict_Clear(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    if (d) {
        for (u32 i = 0; i < d->capacity; ++i) d->entries[i].used = 0;
        d->count = 0;
    }
    if (ret) *ret = Value::Void();
    return true;
}

static bool Dict_Remove(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DictionaryObject* d = AsDict(a[0]);
    s32 at = Dict_Find(in, d, a[1]);
    if (at >= 0) {
        d->entries[at].used = 0;
        d->entries[at].key = Value::Null();
        d->entries[at].value = Value::Null();
        --d->count;
        return RetBool(ret, true);
    }
    return RetBool(ret, false);
}

static bool IsDictName(const char* name) {
    return name && strncmp(name, "System.Collections.Generic.Dictionary`2", 39) == 0;
}

// ---------------------------------------------------------------------------
// System.Array enumeration
// ---------------------------------------------------------------------------

static bool Array_GetEnumerator(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_ARRAY || !a[0].data.array) return false;
    u32 object_type = in.Metadata().FindType("System.Object");
    Object* e = in.AllocObjectWithSlots(object_type, 2);
    if (!e) return false;
    e->Fields()[0] = a[0];
    e->Fields()[1] = Value::I4(-1);
    if (ret) *ret = Value::ObjectRef(e);
    return true;
}

static bool ArrayEnumerator_MoveNext(Interpreter&, Object* e, Value* ret) {
    Value arr = e->Fields()[0];
    if (arr.tag != VT_ARRAY || !arr.data.array) return RetBool(ret, false);
    s32 i = Interpreter::ToI32(e->Fields()[1]) + 1;
    e->Fields()[1] = Value::I4(i);
    return RetBool(ret, i >= 0 && (u32)i < arr.data.array->length);
}

static bool ArrayEnumerator_Current(Interpreter&, Object* e, Value* ret) {
    Value arr = e->Fields()[0];
    if (arr.tag != VT_ARRAY || !arr.data.array) return false;
    s32 i = Interpreter::ToI32(e->Fields()[1]);
    if (i < 0 || (u32)i >= arr.data.array->length) return false;
    if (ret) *ret = arr.data.array->Elements()[i];
    return true;
}

bool Interpreter::DictionarySet(const Value& dict, const Value& key, const Value& value) {
    DictionaryObject* d = (dict.tag == VT_OBJECT && dict.data.obj)
        ? (DictionaryObject*)dict.data.obj : 0;
    if (!d) return false;
    if (!Dict_Ensure(d, d->count + 1)) return false;
    s32 at = Dict_Find(*this, d, key);
    if (at < 0) {
        for (u32 i = 0; i < d->capacity; ++i) {
            if (!d->entries[i].used) { at = (s32)i; break; }
        }
        if (at < 0) return false;
        d->entries[at].key = CopyValue(key);
        d->entries[at].used = 1;
        ++d->count;
    }
    d->entries[at].value = CopyValue(value);
    return true;
}

// ---------------------------------------------------------------------------
// System.Delegate / System.Type
// ---------------------------------------------------------------------------

static DelegateObject* AsDelegate(const Value& v) {
    return (v.tag == VT_DELEGATE) ? v.data.del : 0;
}

static bool Delegate_Combine(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DelegateObject* left = AsDelegate(a[0]);
    DelegateObject* right = AsDelegate(a[1]);
    if (!left && !right) { if (ret) *ret = Value::Null(); return true; }
    if (!left) { if (ret) *ret = a[1]; return true; }
    if (!right) { if (ret) *ret = a[0]; return true; }
    Value out = in.NewDelegate(left->type_id, left->method_id, left->target,
                               (left->flags & 1u) != 0);
    if (out.tag != VT_DELEGATE || !out.data.del) return false;
    DelegateObject* tail = out.data.del;
    for (DelegateObject* n = left->next; n; n = n->next) {
        Value one = in.NewDelegate(n->type_id, n->method_id, n->target, (n->flags & 1u) != 0);
        if (one.tag != VT_DELEGATE || !one.data.del) return false;
        tail->next = one.data.del;
        tail = tail->next;
    }
    for (DelegateObject* n = right; n; n = n->next) {
        Value one = in.NewDelegate(n->type_id, n->method_id, n->target, (n->flags & 1u) != 0);
        if (one.tag != VT_DELEGATE || !one.data.del) return false;
        tail->next = one.data.del;
        tail = tail->next;
    }
    if (ret) *ret = out;
    return true;
}

static bool DelegateMatches(const DelegateObject* a, const DelegateObject* b) {
    if (!a || !b) return false;
    if (a->method_id != b->method_id) return false;
    if ((a->flags & 1u) != (b->flags & 1u)) return false;
    if (a->target.tag == VT_NULL || b->target.tag == VT_NULL) {
        return a->target.tag == b->target.tag;
    }
    return a->target.data.obj == b->target.data.obj;
}

static bool Delegate_Remove(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    DelegateObject* src = AsDelegate(a[0]);
    DelegateObject* remove = AsDelegate(a[1]);
    if (!src) { if (ret) *ret = Value::Null(); return true; }
    if (!remove) { if (ret) *ret = a[0]; return true; }
    Value head = Value::Null();
    DelegateObject* tail = 0;
    for (DelegateObject* n = src; n; n = n->next) {
        bool matched = false;
        for (DelegateObject* r = remove; r && !matched; r = r->next) {
            matched = DelegateMatches(n, r);
        }
        if (matched) continue;
        Value one = in.NewDelegate(n->type_id, n->method_id, n->target, (n->flags & 1u) != 0);
        if (one.tag != VT_DELEGATE || !one.data.del) return false;
        if (head.tag == VT_NULL) { head = one; tail = one.data.del; }
        else { tail->next = one.data.del; tail = tail->next; }
    }
    if (ret) *ret = head;
    return true;
}

static bool Type_GetTypeFromHandle(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 token = 0;
    if (a[0].tag == VT_I4) token = (u32)a[0].data.i32;
    else if ((a[0].tag == VT_STRUCT || a[0].tag == VT_OBJECT) && a[0].data.obj) {
        token = (u32)a[0].data.obj->Fields()[0].data.i32;
    }
    u32 table = token >> 24;
    if (table == 0x7F) { // generic method parameter (MVAR)
        u32 idx = token & 0xFFFFFF;
        u32 tid = idx ? in.GenericArgType(idx - 1) : 0;
        return RetI32(ret, (s32)tid);
    }
    if (table == 0x7E) { // generic type parameter (VAR)
        return RetI32(ret, 0);
    }
    u32 tid = token ? in.Metadata().ResolveTypeToken(token) : 0;
    return RetI32(ret, (s32)tid);
}

static bool Type_GetName(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 tid = (u32)Interpreter::ToI32(a[0]);
    const char* name = (tid && tid <= in.Metadata().TypeCount())
        ? in.Metadata().Type(tid).full_name : "";
    if (ret) *ret = in.NewStringAscii(name ? name : "");
    return true;
}

static bool Type_GetIsEnum(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 tid = (u32)Interpreter::ToI32(a[0]);
    bool is_enum = tid && tid <= in.Metadata().TypeCount() && in.Metadata().Type(tid).is_enum;
    return RetBool(ret, is_enum);
}

static bool Type_GetIsValueType(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 tid = (u32)Interpreter::ToI32(a[0]);
    bool is_value = tid && tid <= in.Metadata().TypeCount() && in.Metadata().Type(tid).is_value_type;
    return RetBool(ret, is_value);
}

static bool Native_Void(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetVoid(ret);
}
static bool Native_Null(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = Value::Null();
    return true;
}
static bool Object_NewFromCtor(Interpreter& in, const MemberRefInfo& member, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewObjectValue(member.decl_id);
    return true;
}
static bool StringWriter_ToString(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = (a[0].tag == VT_OBJECT) ? a[0].data.obj : 0;
    if (o) {
        Value v = o->Fields()[0];
        if (v.tag == VT_STRING) { if (ret) *ret = v; return true; }
    }
    if (ret) *ret = in.NewStringAscii("");
    return true;
}
static bool StringWriter_Write(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = (a[0].tag == VT_OBJECT) ? a[0].data.obj : 0;
    if (o && a[1].tag == VT_STRING) o->Fields()[0] = a[1];
    return RetVoid(ret);
}
static bool StringReader_ReadToEnd(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewStringAscii("");
    return true;
}

static bool Enum_GetValues(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 tid = (u32)Interpreter::ToI32(a[0]);
    if (!tid || tid > in.Metadata().TypeCount()) return false;
    const TypeInfo& t = in.Metadata().Type(tid);
    if (!t.is_enum) return false;
    u32 count = 0;
    for (u32 i = 0; i < t.field_count; ++i) {
        const FieldInfo& f = in.Metadata().Field(t.field_start + i);
        if (f.flags & FIELD_FLAG_LITERAL) {
            if (strcmp(f.name, "value__") != 0) ++count;
        }
    }
    Value arr = in.NewArrayValue(tid, count);
    if (arr.tag == VT_ARRAY && arr.data.array) {
        u32 j = 0;
        for (u32 i = 0; i < t.field_count && j < count; ++i) {
            u32 fid = t.field_start + i;
            const FieldInfo& f = in.Metadata().Field(fid);
            if (!(f.flags & FIELD_FLAG_LITERAL) || strcmp(f.name, "value__") == 0) continue;
            s64 value = 0;
            if (!in.Metadata().FieldConstant(fid, &value)) continue;
            arr.data.array->Elements()[j++] = Value::I4((s32)value);
        }
    }
    if (ret) *ret = arr;
    return true;
}

static bool Interlocked_CompareExchange(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_BYREF || !a[0].data.ref) return false;
    Value current = *a[0].data.ref;
    bool equal;
    if (current.tag == VT_OBJECT || current.tag == VT_NULL ||
        a[2].tag == VT_OBJECT || a[2].tag == VT_NULL) {
        equal = current.data.obj == a[2].data.obj;
    } else if (current.tag == VT_R4 || current.tag == VT_R8 ||
               a[2].tag == VT_R4 || a[2].tag == VT_R8) {
        equal = Interpreter::ToF64(current) == Interpreter::ToF64(a[2]);
    } else {
        equal = Interpreter::ToI64(current) == Interpreter::ToI64(a[2]);
    }
    if (equal) *a[0].data.ref = a[1];
    if (ret) *ret = current;
    return true;
}

static u32 PrimitiveElementSize(Interpreter& in, u32 type_id) {
    if (!type_id) return 0;
    const TypeInfo& t = in.Metadata().Type(type_id);
    const char* n = t.full_name;
    if (!n) return t.size;
    if (strcmp(n, "System.Boolean") == 0 || strcmp(n, "System.Byte") == 0 ||
        strcmp(n, "System.SByte") == 0) return 1;
    if (strcmp(n, "System.Char") == 0 || strcmp(n, "System.Int16") == 0 ||
        strcmp(n, "System.UInt16") == 0) return 2;
    if (strcmp(n, "System.Int32") == 0 || strcmp(n, "System.UInt32") == 0 ||
        strcmp(n, "System.Single") == 0) return 4;
    if (strcmp(n, "System.Int64") == 0 || strcmp(n, "System.UInt64") == 0 ||
        strcmp(n, "System.Double") == 0) return 8;
    if (t.is_enum && t.base_id) return PrimitiveElementSize(in, t.base_id);
    return t.size ? t.size : 4;
}

static bool RuntimeHelpers_InitializeArray(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_ARRAY || !a[0].data.array) return false;
    ArrayObject* arr = a[0].data.array;
    u32 token = 0;
    if (a[1].tag == VT_I4) token = (u32)a[1].data.i32;
    else if ((a[1].tag == VT_STRUCT || a[1].tag == VT_OBJECT) && a[1].data.obj) {
        token = (u32)a[1].data.obj->Fields()[0].data.i32;
    }
    u32 field_id = token ? in.Metadata().ResolveFieldToken(token) : 0;
    u32 size = 0;
    const u8* data = field_id ? in.Metadata().FieldRvaData(field_id, &size) : 0;
    if (!data || !arr->element_type) return false;
    u32 elem_size = PrimitiveElementSize(in, arr->element_type);
    if (!elem_size) return false;
    u32 count = arr->length;
    if (count * elem_size > size) count = size / elem_size;
    const TypeInfo& et = in.Metadata().Type(arr->element_type);
    for (u32 i = 0; i < count; ++i) {
        const u8* p = data + i * elem_size;
        if (strcmp(et.full_name, "System.Single") == 0) {
            u32 bits = ReadU32LE(p);
            f32 f;
            memcpy(&f, &bits, 4);
            arr->Elements()[i] = Value::R4(f);
        } else if (strcmp(et.full_name, "System.Double") == 0) {
            u64 bits = ReadU64LE(p);
            f64 f;
            memcpy(&f, &bits, 8);
            arr->Elements()[i] = Value::R8(f);
        } else if (strcmp(et.full_name, "System.Boolean") == 0) {
            arr->Elements()[i] = Value::Bool(p[0] != 0);
        } else if (elem_size == 1) {
            arr->Elements()[i] = Value::I4(p[0]);
        } else if (elem_size == 2) {
            arr->Elements()[i] = Value::I4((s32)ReadU16LE(p));
        } else if (elem_size == 8) {
            arr->Elements()[i] = Value::I8((s64)ReadU64LE(p));
        } else {
            arr->Elements()[i] = Value::I4((s32)ReadU32LE(p));
        }
    }
    return RetVoid(ret);
}

static bool Exception_Ctor(Interpreter& in, const MemberRefInfo& member, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewObjectValue(member.decl_id);
    return true;
}

static f32 g_rewired_axis_x = 0.0f;
static f32 g_rewired_axis_y = 0.0f;
static u32 g_rewired_buttons = 0;
static u32 g_rewired_buttons_prev = 0;

void SetRewiredInputState(f32 axis_x, f32 axis_y, u32 buttons) {
    g_rewired_axis_x = axis_x;
    g_rewired_axis_y = axis_y;
    g_rewired_buttons_prev = g_rewired_buttons;
    g_rewired_buttons = buttons;
}

static bool Rewired_GetAxis(Interpreter&, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    if (argc < 2) return RetF32(ret, 0.0f);
    s32 axis = Interpreter::ToI32(a[1]);
    return RetF32(ret, axis == 0 ? g_rewired_axis_x : g_rewired_axis_y);
}
static bool Rewired_ButtonBit(Value* a, u32* bit) {
    s32 button = Interpreter::ToI32(a[1]);
    if (button < 0 || button >= 32) return false;
    *bit = 1u << button;
    return true;
}
static bool Rewired_GetButton(Interpreter&, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    if (argc < 2) return RetBool(ret, false);
    u32 bit = 0;
    return RetBool(ret, Rewired_ButtonBit(a, &bit) && (g_rewired_buttons & bit) != 0);
}
static bool Rewired_GetButtonDown(Interpreter&, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    if (argc < 2) return RetBool(ret, false);
    u32 bit = 0;
    return RetBool(ret, Rewired_ButtonBit(a, &bit) &&
                        (g_rewired_buttons & bit) != 0 &&
                        (g_rewired_buttons_prev & bit) == 0);
}
static bool Rewired_GetButtonUp(Interpreter&, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    if (argc < 2) return RetBool(ret, false);
    u32 bit = 0;
    return RetBool(ret, Rewired_ButtonBit(a, &bit) &&
                        (g_rewired_buttons & bit) == 0 &&
                        (g_rewired_buttons_prev & bit) != 0);
}

// Minimal Rewired runtime shim. Cuphead's initialization only needs the
// player handles to exist before input is polled through the native bridge.
static bool Rewired_ReInput_IsReady(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetBool(ret, true);
}
static bool Rewired_ReInput_Players(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    u32 t = in.Metadata().FindType("Rewired.ReInput/PlayerHelper");
    if (!t) t = in.Metadata().FindType("Rewired.ReInput.PlayerHelper");
    if (!t) {
        if (ret) *ret = Value::Null();
        return true;
    }
    Object* o = in.AllocObjectWithSlots(t, 4);
    if (ret) *ret = o ? Value::ObjectRef(o) : Value::Null();
    return true;
}
static bool Rewired_PlayerHelper_GetPlayer(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    u32 t = in.Metadata().FindType("Rewired.Player");
    if (!t) {
        if (ret) *ret = Value::Null();
        return true;
    }
    Object* o = in.AllocObjectWithSlots(t, 8);
    if (o) {
        s32 id = argc >= 2 ? Interpreter::ToI32(a[1]) : 0;
        o->Fields()[0] = Value::I4(id);
    }
    if (ret) *ret = o ? Value::ObjectRef(o) : Value::Null();
    return true;
}
static bool Rewired_Player_GetId(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = (a[0].tag == VT_OBJECT) ? a[0].data.obj : 0;
    return RetI32(ret, o ? o->Fields()[0].data.i32 : 0);
}
static bool GogGalaxyManager_IsInitialized(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    // No Galaxy runtime is present on Xbox 360; online/achievement backends
    // are a later platform task.
    return RetBool(ret, false);
}
static bool JsonUtility_ToJson(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewStringAscii("{}");
    return true;
}
static bool JsonUtility_FromJson(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    u32 t = in.GenericArgType(0);
    if (ret) *ret = t ? in.NewObjectValue(t) : Value::Null();
    return true;
}
static bool String_Replace(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {    if (a[0].tag != VT_STRING) {
        if (ret) *ret = Value::Null();
        return true;
    }
    u32 n = StringLength(a[0]);
    const u16* src = StringChars(a[0]);
    if (a[1].tag == VT_STRING && a[2].tag == VT_STRING) {
        u32 m = StringLength(a[1]);
        u32 r = StringLength(a[2]);
        const u16* old = StringChars(a[1]);
        const u16* rep = StringChars(a[2]);
        if (m == 0) {
            if (ret) *ret = a[0];
            return true;
        }
        Buffer<u16> out;
        u32 i = 0;
        while (i < n) {
            bool match = (i + m <= n);
            if (match) {
                for (u32 j = 0; j < m; ++j) {
                    if (src[i + j] != old[j]) { match = false; break; }
                }
            }
            if (match) {
                for (u32 j = 0; j < r; ++j) out.PushBack(rep[j]);
                i += m;
            } else {
                out.PushBack(src[i]);
                ++i;
            }
        }
        if (ret) *ret = in.NewStringUtf16(out.Size() ? out.Data() : 0, out.Size());
        return true;
    }
    if (a[1].tag == VT_I4 && a[2].tag == VT_I4) {
        u16 oldc = (u16)a[1].data.i32;
        u16 newc = (u16)a[2].data.i32;
        u16* out = (u16*)MemAlloc((n + 1) * 2);
        if (!out) return false;
        for (u32 i = 0; i < n; ++i) out[i] = (src[i] == oldc) ? newc : src[i];
        if (ret) *ret = in.NewStringUtf16(out, n);
        MemFree(out);
        return true;
    }
    if (ret) *ret = a[0];
    return true;
}

struct PrefEntry {
    char key[128];
    char value[512];
    bool used;
};
static PrefEntry g_prefs[32];

static s32 PrefFind(const char* key) {
    if (!key) return -1;
    for (u32 i = 0; i < 32; ++i) {
        if (g_prefs[i].used && strcmp(g_prefs[i].key, key) == 0) return (s32)i;
    }
    return -1;
}
static void PrefStore(const char* key, const char* value) {
    if (!key) return;
    s32 slot = PrefFind(key);
    if (slot < 0) {
        for (u32 i = 0; i < 32; ++i) {
            if (!g_prefs[i].used) { slot = (s32)i; break; }
        }
    }
    if (slot < 0) return;
    PrefEntry& e = g_prefs[slot];
    strncpy(e.key, key, sizeof(e.key) - 1);
    e.key[sizeof(e.key) - 1] = 0;
    strncpy(e.value, value ? value : "", sizeof(e.value) - 1);
    e.value[sizeof(e.value) - 1] = 0;
    e.used = true;
}
static bool PlayerPrefs_HasKey(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char buf[128];
    strncpy(buf, k ? k : "", sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = 0;
    return RetBool(ret, PrefFind(buf) >= 0);
}
static bool PlayerPrefs_GetString(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char buf[128];
    strncpy(buf, k ? k : "", sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = 0;
    s32 i = PrefFind(buf);
    if (i >= 0) {
        if (ret) *ret = in.NewStringUtf8(g_prefs[i].value);
    } else if (argc >= 2) {
        if (ret) *ret = a[1];
    } else {
        if (ret) *ret = in.NewStringAscii("");
    }
    return true;
}
static bool PlayerPrefs_SetString(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char keybuf[128];
    strncpy(keybuf, k ? k : "", sizeof(keybuf) - 1);
    keybuf[sizeof(keybuf) - 1] = 0;
    PrefStore(keybuf, in.StringUtf8(a[1]));
    return RetVoid(ret);
}
static bool PlayerPrefs_GetInt(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char buf[128];
    strncpy(buf, k ? k : "", sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = 0;
    s32 i = PrefFind(buf);
    if (i >= 0) return RetI32(ret, atoi(g_prefs[i].value));
    return RetI32(ret, argc >= 2 ? Interpreter::ToI32(a[1]) : 0);
}
static bool PlayerPrefs_SetInt(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char keybuf[128];
    strncpy(keybuf, k ? k : "", sizeof(keybuf) - 1);
    keybuf[sizeof(keybuf) - 1] = 0;
    char val[64];
    sprintf(val, "%d", Interpreter::ToI32(a[1]));
    PrefStore(keybuf, val);
    return RetVoid(ret);
}
static bool PlayerPrefs_GetFloat(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char buf[128];
    strncpy(buf, k ? k : "", sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = 0;
    s32 i = PrefFind(buf);
    if (i >= 0) return RetF32(ret, (f32)atof(g_prefs[i].value));
    return RetF32(ret, argc >= 2 ? Interpreter::ToF32(a[1]) : 0.0f);
}
static bool PlayerPrefs_SetFloat(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char keybuf[128];
    strncpy(keybuf, k ? k : "", sizeof(keybuf) - 1);
    keybuf[sizeof(keybuf) - 1] = 0;
    char val[64];
    sprintf(val, "%.9g", Interpreter::ToF32(a[1]));
    PrefStore(keybuf, val);
    return RetVoid(ret);
}
static bool PlayerPrefs_DeleteKey(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const char* k = in.StringUtf8(a[0]);
    char buf[128];
    strncpy(buf, k ? k : "", sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = 0;
    s32 i = PrefFind(buf);
    if (i >= 0) g_prefs[i].used = false;
    return RetVoid(ret);
}
static bool PlayerPrefs_DeleteAll(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    for (u32 i = 0; i < 32; ++i) g_prefs[i].used = false;
    return RetVoid(ret);
}

static const NativeEntry kEntries[] = {
    { "UnityEngine.Vector2", ".ctor", Vec2_Ctor },
    { "UnityEngine.Vector2", "get_x", Vec2_GetX },
    { "UnityEngine.Vector2", "get_y", Vec2_GetY },
    { "UnityEngine.Vector2", "set_x", Vec2_SetX },
    { "UnityEngine.Vector2", "set_y", Vec2_SetY },
    { "UnityEngine.Vector2", "get_zero", Vec2_Zero },
    { "UnityEngine.Vector2", "get_one", Vec2_One },
    { "UnityEngine.Vector2", "get_left", Vec2_Left },
    { "UnityEngine.Vector2", "get_right", Vec2_Right },
    { "UnityEngine.Vector2", "get_up", Vec2_Up },
    { "UnityEngine.Vector2", "get_down", Vec2_Down },
    { "UnityEngine.Vector2", "get_normalized", Vec2_Normalized },
    { "UnityEngine.Vector2", "Distance", Vec2_Distance },
    { "UnityEngine.Vector2", "Dot", Vec2_Dot },
    { "UnityEngine.Vector2", "op_Addition", Vec2_Add },
    { "UnityEngine.Vector2", "op_Subtraction", Vec2_Sub },
    { "UnityEngine.Vector2", "op_Multiply", Vec2_Mul },
    { "UnityEngine.Vector2", "op_Division", Vec2_Div },
    { "UnityEngine.Vector2", "get_magnitude", Vec2_Magnitude },
    { "UnityEngine.Vector2", "get_sqrMagnitude", Vec2_SqrMagnitude },
    { "UnityEngine.Vector2", "Lerp", Vec2_Lerp },
    { "UnityEngine.Vector2", "op_Implicit", Vec2_ImplicitV3 },
    { "UnityEngine.Vector3", "get_x", Vec3_GetX },
    { "UnityEngine.Vector3", "get_y", Vec3_GetY },
    { "UnityEngine.Vector3", "get_z", Vec3_GetZ },
    { "UnityEngine.Mathf", "Lerp", Mathf_Lerp },
    { "UnityEngine.Mathf", "LerpUnclamped", Mathf_LerpUnclamped },
    { "UnityEngine.Mathf", "Clamp", Mathf_Clamp },
    { "UnityEngine.Mathf", "Clamp01", Mathf_Clamp01 },
    { "UnityEngine.Mathf", "Abs", Mathf_Abs },
    { "UnityEngine.Mathf", "Sin", Mathf_Sin },
    { "UnityEngine.Mathf", "Cos", Mathf_Cos },
    { "UnityEngine.Mathf", "Pow", Mathf_Pow },
    { "UnityEngine.Mathf", "Sqrt", Mathf_Sqrt },
    { "UnityEngine.Mathf", "Floor", Mathf_Floor },
    { "UnityEngine.Mathf", "Ceil", Mathf_Ceil },
    { "UnityEngine.Mathf", "Round", Mathf_Round },
    { "UnityEngine.Mathf", "Min", Mathf_Min },
    { "UnityEngine.Mathf", "Max", Mathf_Max },
    { "UnityEngine.Mathf", "Sign", Mathf_Sign },
    { "UnityEngine.Mathf", "Approximately", Mathf_Approximately },
    { "System.Math", "Abs", Math_Abs },
    { "System.Math", "Sin", Math_Sin },
    { "System.Math", "Cos", Math_Cos },
    { "System.Math", "Pow", Math_Pow },
    { "System.Math", "Sqrt", Math_Sqrt },
    { "System.Math", "Floor", Math_Floor },
    { "System.Math", "Ceiling", Math_Ceiling },
    { "System.Math", "Round", Math_Round },
    { "System.Object", ".ctor", Object_ctor },
    { "System.Object", "ToString", Object_ToString },
    { "System.Object", "Equals", Object_Equals },
    { "System.Object", "GetHashCode", Object_GetHashCode },
    { "System.Object", "ReferenceEquals", Object_ReferenceEquals },
    { "System.String", "Concat", String_Concat2 },
    { "System.String", "get_Length", String_Length },
    { "System.String", "get_Chars", String_Chars },
    { "System.String", "IsNullOrEmpty", String_IsNullOrEmpty },
    { "System.String", "op_Equality", String_Equality },
    { "System.String", "op_Inequality", String_Inequality },
    { "System.String", "Substring", String_Substring1 },
    { "System.String", "ToUpper", String_ToUpper },
    { "System.String", "ToLower", String_ToLower },
    { "System.String", "Trim", String_Trim },
    { "System.String", "Contains", String_Contains },
    { "System.String", "StartsWith", String_StartsWith },
    { "System.String", "EndsWith", String_EndsWith },
    { "System.String", "IndexOf", String_IndexOf },
    { "System.Char", "IsUpper", Char_IsUpper },
    { "System.Char", "IsLower", Char_IsLower },
    { "System.Char", "IsDigit", Char_IsDigit },
    { "System.Char", "IsLetter", Char_IsLetter },
    { "System.Char", "IsWhiteSpace", Char_IsWhiteSpace },
    { "System.Char", "ToLower", Char_ToLower },
    { "System.Char", "ToUpper", Char_ToUpper },
    { "System.Array", "GetEnumerator", Array_GetEnumerator },
    { "System.Delegate", "Combine", Delegate_Combine },
    { "System.Delegate", "Remove", Delegate_Remove },
    { "System.MulticastDelegate", "CombineImpl", Delegate_Combine },
    { "System.Type", "GetTypeFromHandle", Type_GetTypeFromHandle },
    { "System.Type", "get_Name", Type_GetName },
    { "System.Type", "get_FullName", Type_GetName },
    { "System.Type", "get_IsEnum", Type_GetIsEnum },
    { "System.Type", "get_IsValueType", Type_GetIsValueType },
    { "System.Enum", "GetValues", Enum_GetValues },
    { "System.Enum", "ToString", Object_ToString },
    { "System.Threading.Interlocked", "CompareExchange", Interlocked_CompareExchange },
    { "System.Runtime.CompilerServices.RuntimeHelpers", "InitializeArray", RuntimeHelpers_InitializeArray },
    { "System.Exception", ".ctor", Exception_Ctor },
    { "System.ArgumentException", ".ctor", Exception_Ctor },
    { "System.ArgumentNullException", ".ctor", Exception_Ctor },
    { "System.InvalidOperationException", ".ctor", Exception_Ctor },
    { "System.NotSupportedException", ".ctor", Exception_Ctor },
    { "System.NullReferenceException", ".ctor", Exception_Ctor },
    { "System.IndexOutOfRangeException", ".ctor", Exception_Ctor },
    { "System.InvalidCastException", ".ctor", Exception_Ctor },
    { "System.FormatException", ".ctor", Exception_Ctor },
    { "System.OverflowException", ".ctor", Exception_Ctor },
    { "System.IO.IOException", ".ctor", Exception_Ctor },
    { "System.Collections.Generic.KeyNotFoundException", ".ctor", Exception_Ctor },
    { "System.Xml.Serialization.XmlSerializer", ".ctor", Object_NewFromCtor },
    { "System.Xml.Serialization.XmlSerializer", "Serialize", Native_Void },
    { "System.Xml.Serialization.XmlSerializer", "Deserialize", Native_Null },
    { "System.IO.StringWriter", ".ctor", Object_NewFromCtor },
    { "System.IO.StringWriter", "ToString", StringWriter_ToString },
    { "System.IO.StringWriter", "Write", StringWriter_Write },
    { "System.IO.StringWriter", "Dispose", Native_Void },
    { "System.IO.StringReader", ".ctor", Object_NewFromCtor },
    { "System.IO.StringReader", "ReadToEnd", StringReader_ReadToEnd },
    { "System.IO.StringReader", "Dispose", Native_Void },
    { "Rewired.Player", "GetAxis", Rewired_GetAxis },
    { "Rewired.Player", "GetButton", Rewired_GetButton },
    { "Rewired.Player", "GetButtonDown", Rewired_GetButtonDown },
    { "Rewired.Player", "GetButtonUp", Rewired_GetButtonUp },
    { "Rewired.Player", "get_id", Rewired_Player_GetId },
    { "Rewired.ReInput", "get_isReady", Rewired_ReInput_IsReady },
    { "Rewired.ReInput", "get_players", Rewired_ReInput_Players },
    { "Rewired.ReInput/PlayerHelper", "GetPlayer", Rewired_PlayerHelper_GetPlayer },
    { "Rewired.ReInput", "add_ControllerConnectedEvent", Native_Void },
    { "Rewired.ReInput", "remove_ControllerConnectedEvent", Native_Void },
    { "Rewired.ReInput", "add_ControllerDisconnectedEvent", Native_Void },
    { "Rewired.ReInput", "remove_ControllerDisconnectedEvent", Native_Void },
    { "Rewired.ReInput", "add_ControllerPreDisconnectEvent", Native_Void },
    { "Rewired.ReInput", "remove_ControllerPreDisconnectEvent", Native_Void },
    { "Galaxy.Api.IAuthListener", ".ctor", Native_Void },
    { "GogGalaxyManager", "IsInitialized", GogGalaxyManager_IsInitialized },
    { "Galaxy.Api.GogGalaxyManager", "IsInitialized", GogGalaxyManager_IsInitialized },
    { "UnityEngine.JsonUtility", "ToJson", JsonUtility_ToJson },
    { "UnityEngine.JsonUtility", "FromJson", JsonUtility_FromJson },
    { "System.String", "Replace", String_Replace },
    { "UnityEngine.PlayerPrefs", "HasKey", PlayerPrefs_HasKey },
    { "UnityEngine.PlayerPrefs", "GetString", PlayerPrefs_GetString },
    { "UnityEngine.PlayerPrefs", "SetString", PlayerPrefs_SetString },
    { "UnityEngine.PlayerPrefs", "GetInt", PlayerPrefs_GetInt },
    { "UnityEngine.PlayerPrefs", "SetInt", PlayerPrefs_SetInt },
    { "UnityEngine.PlayerPrefs", "GetFloat", PlayerPrefs_GetFloat },
    { "UnityEngine.PlayerPrefs", "SetFloat", PlayerPrefs_SetFloat },
    { "UnityEngine.PlayerPrefs", "DeleteKey", PlayerPrefs_DeleteKey },
    { "UnityEngine.PlayerPrefs", "DeleteAll", PlayerPrefs_DeleteAll },
    { "UnityEngine.PlayerPrefs", "Save", Native_Void },
};

bool Interpreter::InvokeNative(const MemberRefInfo& member, u32 arg_count, Value* args, Value* ret) {
    if (ret) *ret = Value::Void();
    {
        const TypeInfo& mt = metadata_.Type(member.decl_id);
        const char* tn = mt.full_name ? mt.full_name : "";
        if (strncmp(tn, "System.Nullable`1", 16) == 0) {
            if (strcmp(member.name, ".ctor") == 0) {
                Object* o = AllocObjectWithSlots(member.decl_id, 2);
                if (!o) return false;
                Value v = (arg_count >= 2) ? args[1] : (arg_count ? args[0] : Value::Null());
                o->Fields()[0] = v;
                o->Fields()[1] = Value::I4(v.tag != VT_NULL ? 1 : 0);
                if (ret) *ret = Value::StructRef(o);
                return true;
            }
            if (strcmp(member.name, "get_HasValue") == 0) {
                Object* o = (arg_count && args[0].tag == VT_STRUCT) ? args[0].data.obj : 0;
                if (ret) *ret = Value::Bool(o && o->Fields()[1].data.i32 != 0);
                return true;
            }
            if (strcmp(member.name, "get_Value") == 0 || strcmp(member.name, "GetValueOrDefault") == 0) {
                Object* o = (arg_count && args[0].tag == VT_STRUCT) ? args[0].data.obj : 0;
                if (ret) *ret = o ? o->Fields()[0] : Value::Null();
                return true;
            }
        }
        if (strncmp(tn, "AbstractLevelState`2", 20) == 0 && strcmp(member.name, ".ctor") == 0) {
            return RetVoid(ret);
        }
        if (strncmp(tn, "AbstractLevelProperties`3", 24) == 0 && strcmp(member.name, ".ctor") == 0) {
            u32 def = mt.generic_type_id ? mt.generic_type_id : member.decl_id;
            if (arg_count >= 2) {
                u32 f = metadata_.FindField(def, "TotalHealth");
                if (f) SetField(f, args[0], args[1]);
                f = metadata_.FindField(def, "<CurrentHealth>k__BackingField");
                if (f) SetField(f, args[0], args[1]);
            }
            if (arg_count >= 3) {
                u32 f = metadata_.FindField(def, "goalTimes");
                if (f) SetField(f, args[0], args[2]);
            }
            if (arg_count >= 4) {
                u32 f = metadata_.FindField(def, "states");
                if (f) SetField(f, args[0], args[3]);
            }
            return RetVoid(ret);
        }
        if ((strncmp(tn, "AbstractLevelProperties`3", 24) == 0 ||
             strncmp(tn, "AbstractLevelState`2", 20) == 0) &&
            (strncmp(member.name, "add_", 4) == 0 || strncmp(member.name, "remove_", 7) == 0)) {
            return RetVoid(ret);
        }
    }
    if (arg_count >= 1 && args[0].tag == VT_DELEGATE && args[0].data.del &&
        strcmp(member.name, "Invoke") == 0) {
        return InvokeDelegate(args[0].data.del, arg_count, args, ret);
    }
    const TypeInfo& t = metadata_.Type(member.decl_id);
    if (arg_count >= 1 && (args[0].tag == VT_OBJECT || args[0].tag == VT_STRUCT) &&
        args[0].data.obj) {
        Object* recv = args[0].data.obj;
        Value first = recv->Fields()[0];
        if (first.tag == VT_ARRAY) {
            const char* n = member.name;
            if (strcmp(n, "MoveNext") == 0) return ArrayEnumerator_MoveNext(*this, recv, ret);
            if (strcmp(n, "get_Current") == 0) return ArrayEnumerator_Current(*this, recv, ret);
            if (strcmp(n, "Dispose") == 0) return RetVoid(ret);
            if (strcmp(n, "Reset") == 0) {
                recv->Fields()[1] = Value::I4(-1);
                return RetVoid(ret);
            }
        }
    }
    if (IsListName(t.full_name)) {
        const char* n = member.name;
        if (strcmp(n, ".ctor") == 0) return List_Ctor(*this, member, arg_count, args, ret);
        if (strcmp(n, "Add") == 0) return List_Add(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Count") == 0) return List_GetCount(*this, member, arg_count, args, ret);
        if (strcmp(n, "ToArray") == 0) return List_ToArray(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Item") == 0) return List_GetItem(*this, member, arg_count, args, ret);
        if (strcmp(n, "set_Item") == 0) return List_SetItem(*this, member, arg_count, args, ret);
        if (strcmp(n, "Clear") == 0) return List_Clear(*this, member, arg_count, args, ret);
        if (strcmp(n, "Contains") == 0) return List_Contains(*this, member, arg_count, args, ret);
        if (strcmp(n, "IndexOf") == 0) return List_IndexOf(*this, member, arg_count, args, ret);
        if (strcmp(n, "RemoveAt") == 0) return List_RemoveAt(*this, member, arg_count, args, ret);
        if (strcmp(n, "Remove") == 0) return List_Remove(*this, member, arg_count, args, ret);
        if (strcmp(n, "Sort") == 0) return List_Sort(*this, member, arg_count, args, ret);
        if (strcmp(n, "GetEnumerator") == 0) return List_GetEnumerator(*this, member, arg_count, args, ret);
    }
    if (strstr(t.full_name, "List`1") && strstr(t.full_name, "Enumerator")) {
        const char* n = member.name;
        if (strcmp(n, "MoveNext") == 0) return List_Enumerator_MoveNext(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Current") == 0) return List_Enumerator_Current(*this, member, arg_count, args, ret);
        if (strcmp(n, "Dispose") == 0) return List_Enumerator_Dispose(*this, member, arg_count, args, ret);
    }
    if (IsDictName(t.full_name)) {
        const char* n = member.name;
        if (strcmp(n, ".ctor") == 0) return Dict_Ctor(*this, member, arg_count, args, ret);
        if (strcmp(n, "Add") == 0) return Dict_Add(*this, member, arg_count, args, ret);
        if (strcmp(n, "set_Item") == 0) return Dict_SetItem(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Item") == 0) return Dict_GetItem(*this, member, arg_count, args, ret);
        if (strcmp(n, "TryGetValue") == 0) return Dict_TryGetValue(*this, member, arg_count, args, ret);
        if (strcmp(n, "ContainsKey") == 0) return Dict_ContainsKey(*this, member, arg_count, args, ret);
        if (strcmp(n, "ContainsValue") == 0) return Dict_ContainsValue(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Count") == 0) return Dict_GetCount(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Keys") == 0) return Dict_GetKeys(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Values") == 0) return Dict_GetValues(*this, member, arg_count, args, ret);
        if (strcmp(n, "Clear") == 0) return Dict_Clear(*this, member, arg_count, args, ret);
        if (strcmp(n, "Remove") == 0) return Dict_Remove(*this, member, arg_count, args, ret);
    }
    if (strstr(t.full_name, "GCFreePredicateList")) {
        const char* n = member.name;
        if (strcmp(n, ".ctor") == 0) {
            Object* o = AllocObjectWithSlots(member.decl_id, 8);
            if (ret) *ret = o ? Value::ObjectRef(o) : Value::Null();
            return o != 0;
        }
        if (strcmp(n, "Add") == 0 || strcmp(n, "Clear") == 0 || strcmp(n, "Remove") == 0) {
            return RetVoid(ret);
        }
        if (strcmp(n, "get_Count") == 0) return RetI32(ret, 0);
        if (strcmp(n, "Contains") == 0) return RetBool(ret, false);
    }
    for (u32 i = 0; i < CH360_ARRAY_COUNT(kEntries); ++i) {
        if (strcmp(kEntries[i].type, t.full_name) == 0 &&
            strcmp(kEntries[i].name, member.name) == 0) {
            bool ok = kEntries[i].fn(*this, member, arg_count, args, ret);
            if (!ok) LogError("native", "%s::%s returned false", t.full_name, member.name);
            return ok;
        }
    }
    if (strncmp(t.full_name, "UnityEngine.", 12) == 0) {
        bool ok = InvokeUnityNative(*this, member, arg_count, args, ret);
        if (!ok) LogError("native", "%s::%s returned false (unity)", t.full_name, member.name);
        return ok;
    }
    LogError("native", "unimplemented native method %s::%s (argc=%u)",
             t.full_name, member.name, arg_count);
    return false;
}

} // namespace ch360
