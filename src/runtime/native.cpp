#include "runtime/interpreter.h"
#include "core/log.h"
#include "core/mem.h"

#include <math.h>
#include <string.h>

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
    Vec2* x = AsVec2(a[0]);
    if (!x) return false;
    if (ret) *ret = in.NewVec3(x->x, x->y, 0.0f);
    return true;
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

static bool ValueEqual(const Value& a, const Value& b) {
    if (a.tag == VT_STRING || b.tag == VT_STRING) return StringEquals(a, b);
    if (a.tag == VT_NULL || b.tag == VT_NULL) return a.data.obj == b.data.obj;
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

static bool List_Contains(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    if (!list) return RetBool(ret, false);
    for (u32 i = 0; i < list->count; ++i) {
        if (ValueEqual(list->items[i], a[1])) return RetBool(ret, true);
    }
    return RetBool(ret, false);
}

static bool List_IndexOf(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
    if (!list) return RetI32(ret, -1);
    for (u32 i = 0; i < list->count; ++i) {
        if (ValueEqual(list->items[i], a[1])) return RetI32(ret, (s32)i);
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
        if (ValueEqual(list->items[i], a[1])) {
            for (u32 j = i; j + 1 < list->count; ++j) list->items[j] = list->items[j + 1];
            --list->count;
            return RetBool(ret, true);
        }
    }
    return RetBool(ret, false);
}

static bool List_GetEnumerator(Interpreter& in, const MemberRefInfo& member, u32, Value* a, Value* ret) {
    ListObject* list = AsList(a[0]);
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

static const NativeEntry kEntries[] = {
    { "UnityEngine.Vector2", ".ctor", Vec2_Ctor },
    { "UnityEngine.Vector2", "get_x", Vec2_GetX },
    { "UnityEngine.Vector2", "get_y", Vec2_GetY },
    { "UnityEngine.Vector2", "set_x", Vec2_SetX },
    { "UnityEngine.Vector2", "set_y", Vec2_SetY },
    { "UnityEngine.Vector2", "get_zero", Vec2_Zero },
    { "UnityEngine.Vector2", "get_one", Vec2_One },
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
};

bool Interpreter::InvokeNative(const MemberRefInfo& member, u32 arg_count, Value* args, Value* ret) {
    if (ret) *ret = Value::Void();
    const TypeInfo& t = metadata_.Type(member.decl_id);
    if (IsListName(t.full_name)) {
        const char* n = member.name;
        if (strcmp(n, ".ctor") == 0) return List_Ctor(*this, member, arg_count, args, ret);
        if (strcmp(n, "Add") == 0) return List_Add(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Count") == 0) return List_GetCount(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Item") == 0) return List_GetItem(*this, member, arg_count, args, ret);
        if (strcmp(n, "set_Item") == 0) return List_SetItem(*this, member, arg_count, args, ret);
        if (strcmp(n, "Clear") == 0) return List_Clear(*this, member, arg_count, args, ret);
        if (strcmp(n, "Contains") == 0) return List_Contains(*this, member, arg_count, args, ret);
        if (strcmp(n, "IndexOf") == 0) return List_IndexOf(*this, member, arg_count, args, ret);
        if (strcmp(n, "RemoveAt") == 0) return List_RemoveAt(*this, member, arg_count, args, ret);
        if (strcmp(n, "Remove") == 0) return List_Remove(*this, member, arg_count, args, ret);
        if (strcmp(n, "GetEnumerator") == 0) return List_GetEnumerator(*this, member, arg_count, args, ret);
    }
    if (strstr(t.full_name, "List`1") && strstr(t.full_name, "Enumerator")) {
        const char* n = member.name;
        if (strcmp(n, "MoveNext") == 0) return List_Enumerator_MoveNext(*this, member, arg_count, args, ret);
        if (strcmp(n, "get_Current") == 0) return List_Enumerator_Current(*this, member, arg_count, args, ret);
        if (strcmp(n, "Dispose") == 0) return List_Enumerator_Dispose(*this, member, arg_count, args, ret);
    }
    for (u32 i = 0; i < CH360_ARRAY_COUNT(kEntries); ++i) {
        if (strcmp(kEntries[i].type, t.full_name) == 0 &&
            strcmp(kEntries[i].name, member.name) == 0) {
            return kEntries[i].fn(*this, member, arg_count, args, ret);
        }
    }
    if (strncmp(t.full_name, "UnityEngine.", 12) == 0) {
        return InvokeUnityNative(*this, member, arg_count, args, ret);
    }
    LogError("native", "unimplemented native method %s::%s (argc=%u)",
             t.full_name, member.name, arg_count);
    return false;
}

} // namespace ch360
