#include "runtime/interpreter.h"
#include "core/endian.h"
#include "core/log.h"
#include "core/mem.h"

#include <math.h>
#include <string.h>

namespace ch360 {

static const u32 kMaxStack = 256;
static const u32 kMaxCallArgs = 64;

static ValueTag TagForSig(const MetadataPackage& md, const SigInfo& s) {
    switch (s.kind) {
        case SIG_VOID: return VT_VOID;
        case SIG_BOOL: return VT_BOOL;
        case SIG_CHAR: return VT_CHAR;
        case SIG_I1: return VT_I1;
        case SIG_U1: return VT_U1;
        case SIG_I2: return VT_I2;
        case SIG_U2: return VT_U2;
        case SIG_I4: return VT_I4;
        case SIG_U4: return VT_U4;
        case SIG_I8: return VT_I8;
        case SIG_U8: return VT_U8;
        case SIG_R4: return VT_R4;
        case SIG_R8: return VT_R8;
        case SIG_STRING: return VT_STRING;
        case SIG_OBJECT: return VT_OBJECT;
        case SIG_CLASS: return VT_OBJECT;
        case SIG_VALUETYPE: {
            const TypeInfo& t = md.Type(s.type_id);
            return t.is_value_type ? VT_STRUCT : VT_OBJECT;
        }
        case SIG_GENERICINST: {
            const TypeInfo& t = md.Type(s.type_id);
            return t.is_value_type ? VT_STRUCT : VT_OBJECT;
        }
        case SIG_SZARRAY:
        case SIG_ARRAY: return VT_ARRAY;
        case SIG_BYREF: return VT_BYREF;
        case SIG_PTR: return VT_NATIVEINT;
        case SIG_NATIVEINT: return VT_NATIVEINT;
        case SIG_NATIVEUINT: return VT_NATIVEUINT;
        case SIG_TYPEDBYREF: return VT_STRUCT;
        default: return VT_OBJECT;
    }
}

static bool IsNumericTag(u32 tag) {
    return tag == VT_BOOL || tag == VT_CHAR || tag == VT_I1 || tag == VT_U1 ||
           tag == VT_I2 || tag == VT_U2 || tag == VT_I4 || tag == VT_U4 ||
           tag == VT_I8 || tag == VT_U8 || tag == VT_R4 || tag == VT_R8 ||
           tag == VT_NATIVEINT || tag == VT_NATIVEUINT;
}

static f64 NumF64(const Value& v) {
    switch (v.tag) {
        case VT_R4: return (f64)v.data.f32;
        case VT_R8: return v.data.f64;
        case VT_I8: return (f64)v.data.i64;
        case VT_U8: return (f64)v.data.u64;
        case VT_U4: case VT_U2: case VT_U1: case VT_NATIVEUINT:
            return (f64)v.data.u32;
        default: return (f64)v.data.i32;
    }
}

static s64 NumI64(const Value& v) {
    switch (v.tag) {
        case VT_R4: return (s64)v.data.f32;
        case VT_R8: return (s64)v.data.f64;
        case VT_U4: case VT_U2: case VT_U1: case VT_NATIVEUINT:
            return (s64)v.data.u32;
        case VT_U8: return (s64)v.data.u64;
        case VT_I8: return v.data.i64;
        default: return (s64)v.data.i32;
    }
}

static Value ConvertTag(const Value& v, u32 tag) {
    switch (tag) {
        case VT_BOOL: return Value::Bool(NumI64(v) != 0);
        case VT_CHAR: return Value::Char((u16)(NumI64(v) & 0xFFFF));
        case VT_I1: return Value::I1((s8)NumI64(v));
        case VT_U1: return Value::U1((u8)NumI64(v));
        case VT_I2: return Value::I2((s16)NumI64(v));
        case VT_U2: return Value::U2((u16)NumI64(v));
        case VT_I4: return Value::I4((s32)NumI64(v));
        case VT_U4: return Value::U4((u32)NumI64(v));
        case VT_I8: return Value::I8(NumI64(v));
        case VT_U8: return Value::U8((u64)NumI64(v));
        case VT_R4: return Value::R4((f32)NumF64(v));
        case VT_R8: return Value::R8(NumF64(v));
        case VT_NATIVEINT: return Value::NativeInt((s32)NumI64(v));
        case VT_NATIVEUINT: return Value::NativeUInt((u32)NumI64(v));
        default: return v;
    }
}

static Value MakeNumeric(u32 tag, f64 f, s64 i, bool isFloat) {
    if (isFloat) {
        return tag == VT_R8 ? Value::R8(f) : Value::R4((f32)f);
    }
    if (tag == VT_I8) return Value::I8(i);
    if (tag == VT_U8) return Value::U8((u64)i);
    if (tag == VT_NATIVEINT) return Value::NativeInt((s32)i);
    if (tag == VT_NATIVEUINT) return Value::NativeUInt((u32)i);
    return Value::I4((s32)i);
}

static bool IsTrue(const Value& v) {
    if (v.tag == VT_NULL) return false;
    if (v.tag == VT_OBJECT || v.tag == VT_STRING || v.tag == VT_ARRAY ||
        v.tag == VT_DELEGATE || v.tag == VT_STRUCT) {
        return v.data.obj != 0;
    }
    if (v.tag == VT_R4) return v.data.f32 != 0.0f;
    if (v.tag == VT_R8) return v.data.f64 != 0.0;
    if (v.tag == VT_I8) return v.data.i64 != 0;
    if (v.tag == VT_U8) return v.data.u64 != 0;
    return v.data.i32 != 0;
}

Interpreter::Interpreter(MetadataPackage& metadata)
    : metadata_(metadata), trace_(false), last_error_(0),
      current_method_id_(0), generic_arg_start_(0), generic_arg_count_(0),
      string_type_id_(0), object_type_id_(0), int32_type_id_(0),
      single_type_id_(0), bool_type_id_(0), void_type_id_(0),
      array_type_id_(0), exception_type_id_(0), mathf_type_id_(0),
      gameobject_type_id_(0), transform_type_id_(0), component_type_id_(0),
      monobehaviour_type_id_(0), sprite_renderer_type_id_(0), animator_type_id_(0),
      scene_(0) {}

Interpreter::~Interpreter() {
    Shutdown();
}

bool Interpreter::Fail(const char* message, u32 method_id) {
    last_error_ = method_id;
    if (method_id) {
        const MethodInfo& m = metadata_.Method(method_id);
        LogError("interp", "%s (method %s.%s id=%u)", message,
                 metadata_.Type(m.decl_id).full_name, m.name, method_id);
    } else {
        LogError("interp", "%s", message);
    }
    return false;
}

const char* Interpreter::TypeName(u32 id) const {
    return metadata_.Type(id).full_name;
}

const TypeRuntime& Interpreter::RuntimeType(u32 type_id) const {
    static TypeRuntime empty;
    if (type_id == 0 || type_id > metadata_.TypeCount()) return empty;
    return runtime_types_[type_id];
}

TypeRuntime& Interpreter::MutableTypeRuntime(u32 type_id) {
    static TypeRuntime empty;
    if (type_id == 0 || type_id > metadata_.TypeCount()) return empty;
    return runtime_types_[type_id];
}

bool Interpreter::BuildRuntimeTypes() {
    u32 n = metadata_.TypeCount();
    if (!runtime_types_.Resize(n + 1)) return false;
    memset(runtime_types_.Data(), 0, (n + 1) * sizeof(TypeRuntime));
    if (!field_slots_.Resize(metadata_.FieldCount() + 1)) return false;
    if (!field_static_.Resize(metadata_.FieldCount() + 1)) return false;
    memset(field_slots_.Data(), 0, (metadata_.FieldCount() + 1) * sizeof(u32));
    memset(field_static_.Data(), 0, (metadata_.FieldCount() + 1) * sizeof(u32));

    bool* visiting = (bool*)MemAlloc((n + 1) * sizeof(bool));
    if (!visiting) return false;
    memset(visiting, 0, (n + 1) * sizeof(bool));
    for (u32 id = 1; id <= n; ++id) {
        if (!runtime_types_[id].reserved0) {
            BuildFieldSlots(id, 0, visiting);
        }
    }
    MemFree(visiting);

    for (u32 id = 1; id <= n; ++id) {
        TypeRuntime& rt = runtime_types_[id];
        if (rt.static_slots) {
            rt.static_values = (Value*)MemAlloc(rt.static_slots * sizeof(Value));
            if (!rt.static_values) return false;
            for (u32 i = 0; i < rt.static_slots; ++i) rt.static_values[i] = Value::Null();
        }
        const TypeInfo& t = metadata_.Type(id);
        for (u32 mid = t.method_start; mid < t.method_start + t.method_count; ++mid) {
            const MethodInfo& m = metadata_.Method(mid);
            if (strcmp(m.name, ".cctor") == 0) {
                rt.cctor_method = mid;
                break;
            }
        }
    }

    bool* vvisiting = (bool*)MemAlloc((n + 1) * sizeof(bool));
    if (!vvisiting) return false;
    memset(vvisiting, 0, (n + 1) * sizeof(bool));
    for (u32 id = 1; id <= n; ++id) {
        if (!runtime_types_[id].reserved1) BuildVtable(id, vvisiting);
    }
    MemFree(vvisiting);

    string_type_id_ = metadata_.FindType("System.String");
    object_type_id_ = metadata_.FindType("System.Object");
    int32_type_id_ = metadata_.FindType("System.Int32");
    single_type_id_ = metadata_.FindType("System.Single");
    bool_type_id_ = metadata_.FindType("System.Boolean");
    void_type_id_ = metadata_.FindType("System.Void");
    array_type_id_ = metadata_.FindType("System.Array");
    exception_type_id_ = metadata_.FindType("System.Exception");
    mathf_type_id_ = metadata_.FindType("UnityEngine.Mathf");
    gameobject_type_id_ = metadata_.FindType("UnityEngine.GameObject");
    transform_type_id_ = metadata_.FindType("UnityEngine.Transform");
    component_type_id_ = metadata_.FindType("UnityEngine.Component");
    monobehaviour_type_id_ = metadata_.FindType("UnityEngine.MonoBehaviour");
    sprite_renderer_type_id_ = metadata_.FindType("UnityEngine.SpriteRenderer");
    animator_type_id_ = metadata_.FindType("UnityEngine.Animator");
    return true;
}

void Interpreter::BuildFieldSlots(u32 type_id, u32 base_slots, bool* visiting) {
    if (type_id == 0 || type_id > metadata_.TypeCount() || visiting[type_id]) return;
    TypeRuntime& rt = runtime_types_[type_id];
    if (rt.reserved0) return;
    visiting[type_id] = true;
    const TypeInfo& t = metadata_.Type(type_id);
    if (t.base_id) {
        BuildFieldSlots(t.base_id, 0, visiting);
        base_slots = runtime_types_[t.base_id].total_slots;
    }
    u32 inst = 0;
    u32 stat = 0;
    for (u32 fid = t.field_start; fid < t.field_start + t.field_count; ++fid) {
        const FieldInfo& f = metadata_.Field(fid);
        if (f.flags & FIELD_FLAG_LITERAL) continue;
        if (f.flags & FIELD_FLAG_STATIC) {
            field_slots_[fid] = stat++;
            field_static_[fid] = 1;
        } else {
            field_slots_[fid] = base_slots + inst++;
            field_static_[fid] = 0;
        }
    }
    rt.total_slots = base_slots + inst;
    rt.static_slots = stat;
    rt.reserved0 = 1;
    visiting[type_id] = false;
}

bool Interpreter::BuildVtable(u32 type_id, bool* visiting) {
    if (type_id == 0 || type_id > metadata_.TypeCount()) return true;
    TypeRuntime& rt = runtime_types_[type_id];
    if (rt.reserved1) return true;
    if (visiting[type_id]) return true;
    visiting[type_id] = true;
    const TypeInfo& t = metadata_.Type(type_id);
    if (t.base_id) BuildVtable(t.base_id, visiting);
    if (t.vtable_size == 0) {
        rt.vtable = 0;
        rt.vtable_size = 0;
        rt.reserved1 = 1;
        visiting[type_id] = false;
        return true;
    }
    u32* vt = (u32*)MemAlloc(t.vtable_size * sizeof(u32));
    if (!vt) {
        visiting[type_id] = false;
        return false;
    }
    for (u32 i = 0; i < t.vtable_size; ++i) vt[i] = 0;
    if (t.base_id) {
        TypeRuntime& br = runtime_types_[t.base_id];
        for (u32 i = 0; i < br.vtable_size && i < t.vtable_size; ++i) vt[i] = br.vtable[i];
    }
    for (u32 mid = t.method_start; mid < t.method_start + t.method_count; ++mid) {
        const MethodInfo& m = metadata_.Method(mid);
        if (m.vtable_slot != 0xFFFFFFFFu && m.vtable_slot < t.vtable_size) {
            vt[m.vtable_slot] = mid;
        }
    }
    rt.vtable = vt;
    rt.vtable_size = t.vtable_size;
    rt.reserved1 = 1;
    visiting[type_id] = false;
    return true;
}

bool Interpreter::Init() {
    if (!metadata_.IsLoaded()) return false;
    if (!BuildRuntimeTypes()) return Fail("failed to build runtime types");
    return true;
}

void Interpreter::Shutdown() {
    for (u32 i = 0; i < objects_.Size(); ++i) {
        // Objects are allocated as one block; free the header block.
        MemFree(objects_[i]);
    }
    objects_.Reset();
    for (u32 i = 1; i <= metadata_.TypeCount(); ++i) {
        TypeRuntime& rt = runtime_types_[i];
        if (rt.static_values) MemFree(rt.static_values);
        if (rt.vtable) MemFree(rt.vtable);
        rt.static_values = 0;
        rt.vtable = 0;
    }
    runtime_types_.Reset();
    field_slots_.Reset();
    field_static_.Reset();
}

Object* Interpreter::AllocObject(u32 type_id) {
    u32 slots = runtime_types_[type_id].total_slots;
    if (type_id && type_id <= metadata_.TypeCount()) {
        const TypeInfo& t = metadata_.Type(type_id);
        if (strcmp(t.full_name, "UnityEngine.Rect") == 0) slots = 4;
        else if (strcmp(t.full_name, "UnityEngine.Color") == 0) slots = 4;
        else if (strcmp(t.full_name, "UnityEngine.Quaternion") == 0) slots = 4;
        else if (strcmp(t.full_name, "UnityEngine.Vector4") == 0) slots = 4;
        else if (strcmp(t.full_name, "UnityEngine.Bounds") == 0) slots = 6;
    }
    return AllocObjectWithSlots(type_id, slots);
}

Object* Interpreter::AllocObjectWithSlots(u32 type_id, u32 slots) {
    if (type_id == 0 || type_id > metadata_.TypeCount()) return 0;
    u32 bytes = (u32)sizeof(Object) + slots * (u32)sizeof(Value);
    Object* o = (Object*)MemAlloc(bytes);
    if (!o) return 0;
    memset(o, 0, bytes);
    o->type_id = type_id;
    for (u32 i = 0; i < slots; ++i) o->Fields()[i] = Value::Null();
    objects_.PushBack(o);
    return o;
}

void* Interpreter::TrackAllocation(void* p) {
    if (p) objects_.PushBack((Object*)p);
    return p;
}

Value Interpreter::NewObjectValue(u32 type_id) {
    Object* o = AllocObject(type_id);
    const TypeInfo& t = metadata_.Type(type_id);
    return t.is_value_type ? Value::StructRef(o) : Value::ObjectRef(o);
}

Value Interpreter::NewStructValue(u32 type_id) {
    Object* o = AllocObject(type_id);
    return Value::StructRef(o);
}

Value Interpreter::NewDictionaryValue(u32 type_id) {
    DictionaryObject* d = (DictionaryObject*)TrackAllocation(MemAlloc(sizeof(DictionaryObject)));
    if (!d) return Value::Null();
    memset(d, 0, sizeof(DictionaryObject));
    d->type_id = type_id;
    const TypeInfo& t = metadata_.Type(type_id);
    if (t.generic_arg_count >= 2) {
        d->key_type = metadata_.GenericArg(t.generic_arg_start);
        d->value_type = metadata_.GenericArg(t.generic_arg_start + 1);
    }
    return Value::ObjectRef((Object*)d);
}

Value Interpreter::NewArrayValue(u32 element_type_id, u32 length) {
    if (length > 64u * 1024u * 1024u) return Value::Null();
    u32 bytes = ArrayObject::SizeFor(length);
    ArrayObject* a = (ArrayObject*)MemAlloc(bytes);
    if (!a) return Value::Null();
    memset(a, 0, bytes);
    a->type_id = 0;
    a->length = length;
    a->element_type = element_type_id;
    for (u32 i = 0; i < length; ++i) a->Elements()[i] = Value::Null();
    return Value::ArrayRef(a);
}

Value Interpreter::NewStringUtf16(const u16* chars, u32 length) {
    u32 bytes = StringObject::SizeFor(length);
    StringObject* s = (StringObject*)MemAlloc(bytes);
    if (!s) return Value::Null();
    s->type_id = string_type_id_;
    s->length = length;
    for (u32 i = 0; i < length; ++i) s->Chars()[i] = chars[i];
    return Value::StringRef(s);
}

Value Interpreter::NewStringUtf8(const char* text) {
    if (!text) return Value::Null();
    u32 n = (u32)strlen(text);
    u32 cap = n + 1;
    u16* out = (u16*)MemAlloc(cap * 2);
    if (!out) return Value::Null();
    u32 oi = 0;
    u32 i = 0;
    while (i < n) {
        u8 c = (u8)text[i++];
        u32 cp = c;
        if ((c & 0x80) == 0) {
            cp = c;
        } else if ((c & 0xE0) == 0xC0 && i < n) {
            cp = ((c & 0x1F) << 6) | ((u8)text[i++] & 0x3F);
        } else if ((c & 0xF0) == 0xE0 && i + 1 < n) {
            cp = ((c & 0x0F) << 12) | (((u8)text[i] & 0x3F) << 6) | ((u8)text[i + 1] & 0x3F);
            i += 2;
        } else if ((c & 0xF8) == 0xF0 && i + 2 < n) {
            cp = ((c & 0x07) << 18) | (((u8)text[i] & 0x3F) << 12) |
                 (((u8)text[i + 1] & 0x3F) << 6) | ((u8)text[i + 2] & 0x3F);
            i += 3;
        } else {
            cp = 0xFFFD;
        }
        if (cp <= 0xFFFF) {
            out[oi++] = (u16)cp;
        } else {
            cp -= 0x10000;
            out[oi++] = (u16)(0xD800 | (cp >> 10));
            out[oi++] = (u16)(0xDC00 | (cp & 0x3FF));
        }
    }
    Value v = NewStringUtf16(out, oi);
    MemFree(out);
    return v;
}

Value Interpreter::NewStringAscii(const char* text) {
    if (!text) return Value::Null();
    u32 n = (u32)strlen(text);
    u16* out = (u16*)MemAlloc((n + 1) * 2);
    if (!out) return Value::Null();
    for (u32 i = 0; i < n; ++i) out[i] = (u16)(u8)text[i];
    Value v = NewStringUtf16(out, n);
    MemFree(out);
    return v;
}

Value Interpreter::NewDelegate(u32 type_id, u32 method_id, Value target, bool is_static) {
    DelegateObject* d = (DelegateObject*)MemAlloc(sizeof(DelegateObject));
    if (!d) return Value::Null();
    d->type_id = type_id;
    d->method_id = method_id;
    d->flags = is_static ? 1u : 0u;
    d->target = target;
    d->next = 0;
    return Value::DelegateRef(d);
}

const char* Interpreter::StringUtf8(const Value& v, u32* out_len) const {
    if (out_len) *out_len = 0;
    if (v.tag != VT_STRING || !v.data.str) return "";
    return ""; // UTF-8 conversion is provided lazily by native helpers.
}

u32 Interpreter::ObjectTypeId(const Value& v) const {
    if (v.tag == VT_OBJECT || v.tag == VT_STRUCT) return v.data.obj ? v.data.obj->type_id : 0;
    if (v.tag == VT_STRING) return string_type_id_;
    if (v.tag == VT_ARRAY) return array_type_id_;
    if (v.tag == VT_DELEGATE) return v.data.del ? v.data.del->type_id : 0;
    return 0;
}

u32 Interpreter::GenericArgType(u32 index) const {
    if (index >= generic_arg_count_) return 0;
    u32 sig_index = metadata_.MethodSpecArg(generic_arg_start_ + index);
    if (!sig_index) return 0;
    const SigInfo& sig = metadata_.Sig(sig_index);
    if (sig.type_id) return sig.type_id;
    if (sig.kind == SIG_SZARRAY || sig.kind == SIG_ARRAY) {
        // Arrays still carry their element type in type_id via the builder.
        return sig.type_id;
    }
    return 0;
}

bool Interpreter::IsDelegateType(u32 type_id) const {
    u32 guard = 0;
    while (type_id && guard++ < 64) {
        const TypeInfo& t = metadata_.Type(type_id);
        if (strcmp(t.full_name, "System.Delegate") == 0 ||
            strcmp(t.full_name, "System.MulticastDelegate") == 0) {
            return true;
        }
        if (t.name && (strcmp(t.name, "Action") == 0 || strcmp(t.name, "Func`1") == 0)) {
            return true;
        }
        type_id = t.base_id;
    }
    return false;
}

bool Interpreter::InvokeDelegate(DelegateObject* d, u32 arg_count, Value* args, Value* ret) {
    if (!d) return false;
    bool first = true;
    for (DelegateObject* cur = d; cur; cur = cur->next) {
        if (!cur->method_id) continue;
        const MethodInfo& m = metadata_.Method(cur->method_id);
        bool is_static = (m.flags & METHOD_FLAG_STATIC) != 0;
        Value call_args[kMaxCallArgs];
        u32 argc = arg_count;
        if (is_static) {
            for (u32 i = 1; i < arg_count; ++i) call_args[i - 1] = args[i];
            argc = arg_count - 1;
        } else {
            call_args[0] = cur->target;
            for (u32 i = 1; i < arg_count; ++i) call_args[i] = args[i];
        }
        Value r = Value::Void();
        if (!Invoke(cur->method_id, call_args, argc, &r)) return false;
        if (first && ret) *ret = r;
        first = false;
    }
    return true;
}

bool Interpreter::IsAssignable(u32 derived_type, u32 base_type) const {
    if (derived_type == 0 || base_type == 0) return derived_type == base_type;
    if (derived_type == base_type) return true;
    if (base_type == object_type_id_) return true;
    u32 cur = derived_type;
    u32 guard = 0;
    while (cur && guard++ < 128) {
        const TypeInfo& t = metadata_.Type(cur);
        if (cur == base_type) return true;
        if (t.base_id == 0) break;
        cur = t.base_id;
    }
    // Interfaces.
    cur = derived_type;
    guard = 0;
    while (cur && guard++ < 128) {
        const TypeInfo& t = metadata_.Type(cur);
        for (u32 i = 0; i < t.interface_count; ++i) {
            if (metadata_.InterfaceId(t.interface_start + i) == base_type) return true;
        }
        if (t.base_id == 0) break;
        cur = t.base_id;
    }
    return false;
}

Value Interpreter::DefaultValue(u32 sig_index) const {
    const SigInfo& s = metadata_.Sig(sig_index);
    ValueTag tag = TagForSig(metadata_, s);
    Value v;
    v.tag = tag;
    if (tag == VT_STRUCT) {
        // Caller replaces this with an allocated struct where storage is
        // required; locals use a null struct as "default zero".
        v.tag = VT_NULL;
    }
    return v;
}

Value Interpreter::NewVec2(f32 x, f32 y) {
    Vec2* v = (Vec2*)MemAlloc(sizeof(Vec2));
    if (!v) return Value::Null();
    v->x = x; v->y = y;
    TrackAllocation(v);
    return Value::Vec2Ref(v);
}

Value Interpreter::NewVec3(f32 x, f32 y, f32 z) {
    Vec3* v = (Vec3*)MemAlloc(sizeof(Vec3));
    if (!v) return Value::Null();
    v->x = x; v->y = y; v->z = z;
    TrackAllocation(v);
    Value out; out.tag = VT_VEC3; out.data.ptr = v; return out;
}

Value Interpreter::CopyValue(const Value& v) {
    if (v.tag == VT_VEC2 && v.data.ptr) {
        const Vec2* src = (const Vec2*)v.data.ptr;
        return NewVec2(src->x, src->y);
    }
    if (v.tag == VT_VEC3 && v.data.ptr) {
        const Vec3* src = (const Vec3*)v.data.ptr;
        return NewVec3(src->x, src->y, src->z);
    }
    if (v.tag != VT_STRUCT || !v.data.obj) return v;
    Object* src = v.data.obj;
    Object* dst = AllocObject(src->type_id);
    if (!dst) return Value::Null();
    u32 slots = runtime_types_[src->type_id].total_slots;
    for (u32 i = 0; i < slots; ++i) StoreValue(&dst->Fields()[i], src->Fields()[i]);
    return Value::StructRef(dst);
}

void Interpreter::StoreValue(Value* dest, const Value& v) {
    if (!dest) return;
    if (v.tag == VT_STRUCT) {
        *dest = CopyValue(v);
    } else {
        *dest = v;
    }
}

u32 Interpreter::FieldSlot(u32 field_id) const {
    if (field_id == 0 || field_id >= field_slots_.Size()) return 0;
    return field_slots_[field_id];
}

bool Interpreter::IsStaticField(u32 field_id) const {
    if (field_id == 0 || field_id >= field_static_.Size()) return false;
    return field_static_[field_id] != 0;
}

Value Interpreter::GetField(u32 field_id, const Value& instance, bool* ok) {
    if (ok) *ok = false;
    const FieldInfo& f = metadata_.Field(field_id);
    if (field_id == 0) return Value::Null();
    if (IsStaticField(field_id)) {
        TypeRuntime& rt = runtime_types_[f.decl_id];
        u32 slot = FieldSlot(field_id);
        if (!rt.static_values || slot >= rt.static_slots) return Value::Null();
        if (ok) *ok = true;
        return rt.static_values[slot];
    }
    Object* o = 0;
    if (instance.tag == VT_OBJECT || instance.tag == VT_STRUCT) o = instance.data.obj;
    if (!o) return Value::Null();
    u32 slot = FieldSlot(field_id);
    if (slot >= runtime_types_[o->type_id].total_slots) return Value::Null();
    if (ok) *ok = true;
    return o->Fields()[slot];
}

static Value DerefValue(const Value& v) {
    if (v.tag == VT_BYREF && v.data.ref) return *v.data.ref;
    return v;
}

bool Interpreter::GetExternalField(const MemberRefInfo& member, const Value& instance, Value* out) {
    Value obj = DerefValue(instance);
    const TypeInfo& t = metadata_.Type(member.decl_id);
    const char* n = member.name;
    if (strcmp(t.full_name, "UnityEngine.Vector2") == 0 && obj.tag == VT_VEC2 && obj.data.ptr) {
        const Vec2* v = (const Vec2*)obj.data.ptr;
        if (strcmp(n, "x") == 0) { if (out) *out = Value::R4(v->x); return true; }
        if (strcmp(n, "y") == 0) { if (out) *out = Value::R4(v->y); return true; }
    }
    if (strcmp(t.full_name, "UnityEngine.Vector3") == 0 && obj.tag == VT_VEC3 && obj.data.ptr) {
        const Vec3* v = (const Vec3*)obj.data.ptr;
        if (strcmp(n, "x") == 0) { if (out) *out = Value::R4(v->x); return true; }
        if (strcmp(n, "y") == 0) { if (out) *out = Value::R4(v->y); return true; }
        if (strcmp(n, "z") == 0) { if (out) *out = Value::R4(v->z); return true; }
    }
    return false;
}

bool Interpreter::SetExternalField(const MemberRefInfo& member, const Value& instance, const Value& value) {
    Value obj = DerefValue(instance);
    const TypeInfo& t = metadata_.Type(member.decl_id);
    const char* n = member.name;
    if (strcmp(t.full_name, "UnityEngine.Vector2") == 0 && obj.tag == VT_VEC2 && obj.data.ptr) {
        Vec2* v = (Vec2*)obj.data.ptr;
        if (strcmp(n, "x") == 0) { v->x = (f32)NumF64(value); return true; }
        if (strcmp(n, "y") == 0) { v->y = (f32)NumF64(value); return true; }
    }
    if (strcmp(t.full_name, "UnityEngine.Vector3") == 0 && obj.tag == VT_VEC3 && obj.data.ptr) {
        Vec3* v = (Vec3*)obj.data.ptr;
        if (strcmp(n, "x") == 0) { v->x = (f32)NumF64(value); return true; }
        if (strcmp(n, "y") == 0) { v->y = (f32)NumF64(value); return true; }
        if (strcmp(n, "z") == 0) { v->z = (f32)NumF64(value); return true; }
    }
    return false;
}

void Interpreter::SetField(u32 field_id, const Value& instance, const Value& value) {
    const FieldInfo& f = metadata_.Field(field_id);
    if (field_id == 0) return;
    if (IsStaticField(field_id)) {
        TypeRuntime& rt = runtime_types_[f.decl_id];
        u32 slot = FieldSlot(field_id);
        if (rt.static_values && slot < rt.static_slots) StoreValue(&rt.static_values[slot], value);
        return;
    }
    Object* o = (instance.tag == VT_OBJECT || instance.tag == VT_STRUCT) ? instance.data.obj : 0;
    if (!o) return;
    u32 slot = FieldSlot(field_id);
    if (slot < runtime_types_[o->type_id].total_slots) StoreValue(&o->Fields()[slot], value);
}

bool Interpreter::EnsureTypeInitialized(u32 type_id) {
    if (type_id == 0 || type_id > metadata_.TypeCount()) return true;
    TypeRuntime& rt = runtime_types_[type_id];
    if (rt.initialized || rt.initializing) return true;
    const TypeInfo& t = metadata_.Type(type_id);
    rt.initializing = 1;
    if (t.base_id) EnsureTypeInitialized(t.base_id);
    if (rt.cctor_method) {
        if (!Invoke(rt.cctor_method, 0, 0, 0)) {
            rt.initializing = 0;
            return false;
        }
    }
    rt.initialized = 1;
    rt.initializing = 0;
    return true;
}

bool Interpreter::Invoke(u32 method_id, Value* args, u32 arg_count, Value* ret) {
    if (ret) *ret = Value::Void();
    if (method_id == 0 || method_id > metadata_.MethodCount()) return Fail("bad method id");
    const MethodInfo& m = metadata_.Method(method_id);
    const MethodSigInfo& sig = metadata_.MethodSig(m.sig);
    u32 want = sig.param_count + ((sig.flags & 0x20) ? 1u : 0u);
    if (arg_count != want) {
        return Fail("argument count mismatch", method_id);
    }
    if ((m.flags & METHOD_FLAG_STATIC) && !(sig.flags & 0x20)) {
        if (!EnsureTypeInitialized(m.decl_id)) {
            const TypeInfo& dt = metadata_.Type(m.decl_id);
            LogWarn("interp", "type initializer failed for %s (called by %s)",
                    dt.full_name, m.name);
        }
    }
    if (m.il_size == 0 || !metadata_.Il(method_id)) {
        // Abstract or native-only method. Native dispatch is handled by
        // callers for external methods; this is an error for game methods.
        return Fail("method has no IL", method_id);
    }
    return Execute(method_id, m, sig, args, arg_count, ret);
}

bool Interpreter::InvokeMemberRef(const MemberRefInfo& member, u32 arg_count, Value* args, Value* ret) {
    if (ret) *ret = Value::Void();
    if (member.kind == 1 && member.resolved_id) {
        return Invoke(member.resolved_id, args, arg_count, ret);
    }
    return InvokeNative(member, arg_count, args, ret);
}

bool Interpreter::InvokeVirtual(u32 method_id, u32 arg_count, Value* args, Value* ret) {
    if (arg_count == 0) return Fail("callvirt without this", method_id);
    if (args[0].tag == VT_DELEGATE && args[0].data.del) {
        return InvokeDelegate(args[0].data.del, arg_count, args, ret);
    }
    if (args[0].tag == VT_NULL) {
        if (current_method_id_ && current_method_id_ <= metadata_.MethodCount()) {
            const MethodInfo& caller = metadata_.Method(current_method_id_);
            LogError("interp", "null callvirt caller=%s.%s id=%u",
                     metadata_.Type(caller.decl_id).full_name, caller.name, current_method_id_);
        }
        return Fail("callvirt on null receiver", method_id);
    }
    u32 type = ObjectTypeId(args[0]);
    const MethodInfo& base = metadata_.Method(method_id);
    if (type && base.vtable_slot != 0xFFFFFFFFu) {
        TypeRuntime& rt = runtime_types_[type];
        if (base.vtable_slot < rt.vtable_size && rt.vtable && rt.vtable[base.vtable_slot]) {
            method_id = rt.vtable[base.vtable_slot];
        }
    }
    if (method_id > metadata_.MethodCount()) return Fail("bad virtual method");
    const MethodInfo& m = metadata_.Method(method_id);
    if (m.il_size == 0) {
        // External/native override: use the memberref path when possible.
        return Fail("virtual method has no IL", method_id);
    }
    return Invoke(method_id, args, arg_count, ret);
}

bool Interpreter::Execute(u32 method_id, const MethodInfo& method, const MethodSigInfo& sig,
                          Value* args, u32 arg_count, Value* ret) {
    const u8* code = metadata_.Il(method_id);
    if (!code) return Fail("missing IL", method_id);
    u32 code_size = method.il_size;
    u32 saved_method = current_method_id_;
    current_method_id_ = method_id;

    Value* arg_slots = (Value*)MemAlloc((arg_count ? arg_count : 1) * sizeof(Value));
    if (!arg_slots) return Fail("out of memory for args", method_id);
    for (u32 i = 0; i < arg_count; ++i) arg_slots[i] = CopyValue(args[i]);

    u32 local_count = 0;
    Value* locals = 0;
    u32 local_sig_id = method.local_sig;
    if (local_sig_id) {
        const LocalSigInfo& ls = metadata_.LocalSig(local_sig_id);
        local_count = ls.var_count;
        locals = (Value*)MemAlloc((local_count ? local_count : 1) * sizeof(Value));
        if (!locals) { MemFree(arg_slots); return Fail("out of memory for locals", method_id); }
        for (u32 i = 0; i < local_count; ++i) {
            locals[i] = DefaultValue(metadata_.LocalVarSig(ls.var_start + i));
        }
    }

    Value stack[kMaxStack];
    u32 sp = 0;
    u32 pc = 0;
    bool returned = false;
    bool ok = true;
    Value result = Value::Void();
    u8 last_op = 0;
    u32 last_pc = 0;

#define PUSH(v) do { if (sp >= kMaxStack) { ok = Fail("evaluation stack overflow", method_id); goto done; } stack[sp++] = (v); } while (0)
#define POP(dst) do { if (sp == 0) { ok = Fail("evaluation stack underflow", method_id); goto done; } (dst) = stack[--sp]; } while (0)
#define POP2(b, a) do { Value _b, _a; POP(_b); POP(_a); (b) = _b; (a) = _a; } while (0)
#define BRANCH(off) do { pc = (u32)((s32)pc + (s32)(off)); } while (0)

    while (pc < code_size && !returned) {
        if (trace_) {
            LogDebug("il", "method=%u pc=%u op=%02x", method_id, pc, code[pc]);
        }
        u8 op = code[pc++];
        last_op = op;
        last_pc = pc - 1;
        switch (op) {
            case 0x00: break; // nop
            case 0x01: break; // break
            case 0x02: case 0x03: case 0x04: case 0x05:
                PUSH(CopyValue(arg_slots[op - 0x02])); break;
            case 0x06: case 0x07: case 0x08: case 0x09:
                PUSH(CopyValue(locals[op - 0x06])); break;
            case 0x0A: case 0x0B: case 0x0C: case 0x0D: {
                Value v; POP(v); StoreValue(&locals[op - 0x0A], v); break;
            }
            case 0x0E: { u32 i = code[pc++]; PUSH(CopyValue(arg_slots[i])); break; }
            case 0x0F: { u32 i = code[pc++]; PUSH(Value::ByRef(&arg_slots[i])); break; }
            case 0x10: { u32 i = code[pc++]; Value v; POP(v); StoreValue(&arg_slots[i], v); break; }
            case 0x11: { u32 i = code[pc++]; PUSH(CopyValue(locals[i])); break; }
            case 0x12: { u32 i = code[pc++]; PUSH(Value::ByRef(&locals[i])); break; }
            case 0x13: { u32 i = code[pc++]; Value v; POP(v); StoreValue(&locals[i], v); break; }
            case 0x14: PUSH(Value::Null()); break;
            case 0x15: PUSH(Value::I4(-1)); break;
            case 0x16: case 0x17: case 0x18: case 0x19: case 0x1A:
            case 0x1B: case 0x1C: case 0x1D: case 0x1E:
                PUSH(Value::I4((s32)(op - 0x16))); break;
            case 0x1F: { s8 v = (s8)code[pc++]; PUSH(Value::I4(v)); break; }
            case 0x20: { s32 v = ReadS32LE(code + pc); pc += 4; PUSH(Value::I4(v)); break; }
            case 0x21: { s64 v = ReadS64LE(code + pc); pc += 8; PUSH(Value::I8(v)); break; }
            case 0x22: { f32 v = ReadF32LE(code + pc); pc += 4; PUSH(Value::R4(v)); break; }
            case 0x23: { f64 v = ReadF64LE(code + pc); pc += 8; PUSH(Value::R8(v)); break; }
            case 0x25: { if (sp == 0) { ok = Fail("dup underflow", method_id); goto done; } PUSH(CopyValue(stack[sp - 1])); break; }
            case 0x26: { Value v; POP(v); (void)v; break; }
            case 0x27: { // jmp
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 target = metadata_.ResolveMethodToken(token);
                ok = Invoke(target, arg_slots, arg_count, ret);
                returned = true;
                break;
            }
            case 0x28: case 0x6F: { // call / callvirt
                u32 token = ReadU32LE(code + pc); pc += 4;
                bool virt = (op == 0x6F);
                u32 target = metadata_.ResolveMethodToken(token);
                const MemberRefInfo* mr = metadata_.MemberRefForToken(token);
                const MethodSpecInfo* spec = 0;
                if ((token >> 24) == 0x2B) {
                    spec = &metadata_.MethodSpec(token & 0xFFFFFF);
                    if (!target && spec->memberref_id) {
                        mr = &metadata_.MemberRef(spec->memberref_id);
                    }
                }
                u32 argc = 0;
                const MethodSigInfo* ms = 0;
                if (target) {
                    ms = &metadata_.MethodSig(metadata_.Method(target).sig);
                    argc = ms->param_count + ((ms->flags & 0x20) ? 1u : 0u);
                } else if (mr) {
                    ms = &metadata_.MethodSig(mr->sig_index);
                    argc = ms->param_count + ((ms->flags & 0x20) ? 1u : 0u);
                } else {
                    ok = Fail("unresolved call token", method_id);
                    goto done;
                }
                if (argc > kMaxCallArgs) { ok = Fail("too many call args", method_id); goto done; }
                Value call_args[kMaxCallArgs];
                for (u32 i = argc; i > 0; --i) POP(call_args[i - 1]);
                Value call_ret = Value::Void();
                u32 saved_arg_start = generic_arg_start_;
                u32 saved_arg_count = generic_arg_count_;
                if (spec) {
                    generic_arg_start_ = spec->arg_start;
                    generic_arg_count_ = spec->arg_count;
                }
                bool call_ok;
                if (virt) {
                    if (target) call_ok = InvokeVirtual(target, argc, call_args, &call_ret);
                    else call_ok = InvokeNative(*mr, argc, call_args, &call_ret);
                } else {
                    if (target) call_ok = Invoke(target, call_args, argc, &call_ret);
                    else call_ok = InvokeNative(*mr, argc, call_args, &call_ret);
                }
                generic_arg_start_ = saved_arg_start;
                generic_arg_count_ = saved_arg_count;
                ok = call_ok;
                if (!ok) goto done;
                if (ms->return_sig) {
                    const SigInfo& rs = metadata_.Sig(ms->return_sig);
                    if (rs.kind != SIG_VOID) PUSH(CopyValue(call_ret));
                }
                break;
            }
            case 0x2A: { // ret
                if (sig.return_sig) {
                    const SigInfo& rs = metadata_.Sig(sig.return_sig);
                    if (rs.kind != SIG_VOID) POP(result);
                }
                returned = true;
                break;
            }
            case 0x2B: { s8 off = (s8)code[pc++]; BRANCH(off); break; }
            case 0x2C: { s8 off = (s8)code[pc++]; Value v; POP(v); if (!IsTrue(v)) BRANCH(off); break; }
            case 0x2D: { s8 off = (s8)code[pc++]; Value v; POP(v); if (IsTrue(v)) BRANCH(off); break; }
            case 0x2E: case 0x2F: case 0x30: case 0x31:
            case 0x32: case 0x33: case 0x34: case 0x35:
            case 0x36: case 0x37: {
                s8 off = (s8)code[pc++];
                Value b, a; POP2(b, a);
                s64 x = NumI64(a), y = NumI64(b);
                bool take = false;
                switch (op) {
                    case 0x2E: take = x == y; break;
                    case 0x2F: take = x >= y; break;
                    case 0x30: take = x > y; break;
                    case 0x31: take = x <= y; break;
                    case 0x32: take = x < y; break;
                    case 0x33: take = x != y; break;
                    case 0x34: take = (u64)x >= (u64)y; break;
                    case 0x35: take = (u64)x > (u64)y; break;
                    case 0x36: take = (u64)x <= (u64)y; break;
                    case 0x37: take = (u64)x < (u64)y; break;
                }
                if (take) BRANCH(off);
                break;
            }
            case 0x38: { s32 off = ReadS32LE(code + pc); pc += 4; BRANCH(off); break; }
            case 0x39: { s32 off = ReadS32LE(code + pc); pc += 4; Value v; POP(v); if (!IsTrue(v)) BRANCH(off); break; }
            case 0x3A: { s32 off = ReadS32LE(code + pc); pc += 4; Value v; POP(v); if (IsTrue(v)) BRANCH(off); break; }
            case 0x3B: case 0x3C: case 0x3D: case 0x3E:
            case 0x3F: case 0x40: case 0x41: case 0x42:
            case 0x43: case 0x44: {
                s32 off = ReadS32LE(code + pc); pc += 4;
                Value b, a; POP2(b, a);
                s64 x = NumI64(a), y = NumI64(b);
                bool take = false;
                switch (op) {
                    case 0x3B: take = x == y; break;
                    case 0x3C: take = x >= y; break;
                    case 0x3D: take = x > y; break;
                    case 0x3E: take = x <= y; break;
                    case 0x3F: take = x < y; break;
                    case 0x40: take = x != y; break;
                    case 0x41: take = (u64)x >= (u64)y; break;
                    case 0x42: take = (u64)x > (u64)y; break;
                    case 0x43: take = (u64)x <= (u64)y; break;
                    case 0x44: take = (u64)x < (u64)y; break;
                }
                if (take) BRANCH(off);
                break;
            }
            case 0x45: { // switch
                u32 n = ReadU32LE(code + pc); pc += 4;
                Value idx; POP(idx);
                s32 i = (s32)NumI64(idx);
                if (i >= 0 && (u32)i < n) {
                    // switch targets are relative to the end of the table.
                    u32 target = ReadU32LE(code + pc + (u32)i * 4);
                    pc = pc + n * 4 + target;
                } else {
                    pc += n * 4;
                }
                break;
            }
            case 0x46: case 0x47: case 0x48: case 0x49: case 0x4A: case 0x4B:
            case 0x4C: case 0x4D: case 0x4E: case 0x4F: case 0x50: {
                Value addr; POP(addr);
                if (addr.tag != VT_BYREF || !addr.data.ref) { ok = Fail("ldind on non-byref", method_id); goto done; }
                Value v = *addr.data.ref;
                if (op == 0x46) v = ConvertTag(v, VT_I1);
                else if (op == 0x47) v = ConvertTag(v, VT_U1);
                else if (op == 0x48) v = ConvertTag(v, VT_I2);
                else if (op == 0x49) v = ConvertTag(v, VT_U2);
                else if (op == 0x4A) v = ConvertTag(v, VT_I4);
                else if (op == 0x4B) v = ConvertTag(v, VT_U4);
                else if (op == 0x4C) v = ConvertTag(v, VT_I8);
                else if (op == 0x4D) v = ConvertTag(v, VT_NATIVEINT);
                else if (op == 0x4E) v = ConvertTag(v, VT_R4);
                else if (op == 0x4F) v = ConvertTag(v, VT_R8);
                PUSH(v);
                break;
            }
            case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57: {
                Value v; POP(v); Value addr; POP(addr);
                if (addr.tag != VT_BYREF || !addr.data.ref) { ok = Fail("stind on non-byref", method_id); goto done; }
                u32 tag = VT_I4;
                if (op == 0x51) tag = VT_OBJECT;
                else if (op == 0x52) tag = VT_I1;
                else if (op == 0x53) tag = VT_I2;
                else if (op == 0x54) tag = VT_I4;
                else if (op == 0x55) tag = VT_I8;
                else if (op == 0x56) tag = VT_R4;
                else if (op == 0x57) tag = VT_R8;
                if (tag != VT_OBJECT) v = ConvertTag(v, tag);
                StoreValue(addr.data.ref, v);
                break;
            }
            case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D:
            case 0x5E: case 0x5F: case 0x60: case 0x61: {
                Value b, a; POP2(b, a);
                bool useFloat = (a.tag == VT_R4 || a.tag == VT_R8 || b.tag == VT_R4 || b.tag == VT_R8);
                bool wide = (a.tag == VT_I8 || a.tag == VT_U8 || b.tag == VT_I8 || b.tag == VT_U8);
                u32 outTag = useFloat ? ((a.tag == VT_R8 || b.tag == VT_R8) ? VT_R8 : VT_R4)
                                      : (wide ? VT_I8 : VT_I4);
                Value r;
                if (useFloat) {
                    f64 x = NumF64(a), y = NumF64(b), z = 0;
                    switch (op) {
                        case 0x58: z = x + y; break;
                        case 0x59: z = x - y; break;
                        case 0x5A: z = x * y; break;
                        case 0x5B: z = x / y; break;
                        case 0x5C: z = x / y; break;
                        case 0x5D: z = fmod(x, y); break;
                        case 0x5E: z = fmod(x, y); break;
                        default: break;
                    }
                    r = MakeNumeric(outTag, z, 0, true);
                } else {
                    s64 x = NumI64(a), y = NumI64(b), z = 0;
                    switch (op) {
                        case 0x58: z = x + y; break;
                        case 0x59: z = x - y; break;
                        case 0x5A: z = x * y; break;
                        case 0x5B: z = x / y; break;
                        case 0x5C: z = (s64)((u64)x / (u64)y); break;
                        case 0x5D: z = x % y; break;
                        case 0x5E: z = (s64)((u64)x % (u64)y); break;
                        case 0x5F: z = x & y; break;
                        case 0x60: z = x | y; break;
                        case 0x61: z = x ^ y; break;
                        default: break;
                    }
                    r = MakeNumeric(outTag, 0, z, false);
                }
                PUSH(r);
                break;
            }
            case 0x62: case 0x63: case 0x64: {
                Value b, a; POP2(b, a);
                s64 x = NumI64(a);
                s32 y = (s32)NumI64(b);
                s64 z = (op == 0x62) ? (x << y) : (op == 0x63 ? (x >> y) : (s64)((u64)x >> y));
                PUSH(MakeNumeric((a.tag == VT_I8 ? VT_I8 : VT_I4), 0, z, false));
                break;
            }
            case 0x65: { Value a; POP(a); if (a.tag == VT_R4) PUSH(Value::R4(-a.data.f32)); else if (a.tag == VT_R8) PUSH(Value::R8(-a.data.f64)); else if (a.tag == VT_I8) PUSH(Value::I8(-a.data.i64)); else PUSH(Value::I4(-a.data.i32)); break; }
            case 0x66: { Value a; POP(a); PUSH(Value::I4(~a.data.i32)); break; }
            case 0x67: case 0x68: case 0x69: case 0x6A: case 0x6B: case 0x6C: case 0x6D: case 0x6E: {
                Value a; POP(a);
                static const u32 tags[] = { VT_I1, VT_I2, VT_I4, VT_I8, VT_R4, VT_R8, VT_U4, VT_U8 };
                PUSH(ConvertTag(a, tags[op - 0x67]));
                break;
            }
            case 0x70: { // cpobj
                u32 tok = ReadU32LE(code + pc); pc += 4;
                Value src; POP(src); Value dst; POP(dst);
                if (dst.tag == VT_BYREF && dst.data.ref) StoreValue(dst.data.ref, src);
                break;
            }
            case 0x71: { // ldobj
                u32 tok = ReadU32LE(code + pc); pc += 4;
                (void)tok;
                Value addr; POP(addr);
                if (addr.tag == VT_BYREF && addr.data.ref) PUSH(CopyValue(*addr.data.ref));
                else PUSH(Value::Null());
                break;
            }
            case 0x72: { // ldstr
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 len = 0;
                const u16* chars = metadata_.UserString(token, &len);
                if (!chars) { ok = Fail("bad ldstr token", method_id); goto done; }
                PUSH(NewStringUtf16(chars, len));
                break;
            }
            case 0x73: { // newobj
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 ctor_id = metadata_.ResolveMethodToken(token);
                const MemberRefInfo* mr = metadata_.MemberRefForToken(token);
                if (!ctor_id) {
                    if (mr) {
                        // Native constructor (List<T>, String, Action, etc.).
                        const MethodSigInfo& ms = metadata_.MethodSig(mr->sig_index);
                        u32 explicit_count = ms.param_count;
                        Value cargs[kMaxCallArgs];
                        for (u32 i = explicit_count; i > 0; --i) POP(cargs[i - 1]);
                        if (IsDelegateType(mr->decl_id)) {
                            Value target = explicit_count > 0 ? cargs[0] : Value::Null();
                            Value ftn = explicit_count > 1 ? cargs[1] : Value::Null();
                            u32 mid = (ftn.tag == VT_DELEGATE && ftn.data.del)
                                ? ftn.data.del->method_id
                                : ((ftn.tag == VT_I4) ? (u32)ftn.data.i32 : 0);
                            bool is_static = (target.tag == VT_NULL);
                            PUSH(NewDelegate(mr->decl_id, mid, target, is_static));
                            break;
                        }
                        // The constructor has no 'this' on the IL stack yet, so
                        // synthesize a null this.
                        Value synth[kMaxCallArgs];
                        synth[0] = Value::Null();
                        for (u32 i = 0; i < explicit_count; ++i) synth[i + 1] = cargs[i];
                        Value cr = Value::Void();
                        ok = InvokeNative(*mr, explicit_count + 1, synth, &cr);
                        if (!ok) goto done;
                        PUSH(cr);
                    } else {
                        ok = Fail("unresolved newobj token", method_id);
                        goto done;
                    }
                    break;
                }
                const MethodInfo& ctor = metadata_.Method(ctor_id);
                const MethodSigInfo& ms = metadata_.MethodSig(ctor.sig);
                if (IsDelegateType(ctor.decl_id)) {
                    u32 dargc = ms.param_count;
                    if (dargc > kMaxCallArgs) { ok = Fail("too many delegate ctor args", method_id); goto done; }
                    Value da[kMaxCallArgs];
                    for (u32 i = dargc; i > 0; --i) POP(da[i - 1]);
                    Value target = dargc > 0 ? da[0] : Value::Null();
                    Value ftn = dargc > 1 ? da[1] : Value::Null();
                    u32 mid = (ftn.tag == VT_DELEGATE && ftn.data.del)
                        ? ftn.data.del->method_id
                        : ((ftn.tag == VT_I4) ? (u32)ftn.data.i32 : 0);
                    bool is_static = (target.tag == VT_NULL);
                    PUSH(NewDelegate(ctor.decl_id, mid, target, is_static));
                    break;
                }
                u32 argc = ms.param_count + ((ms.flags & 0x20) ? 1u : 0u);
                Value cargs[kMaxCallArgs];
                for (u32 i = argc; i > 1; --i) POP(cargs[i - 1]);
                Value obj = NewObjectValue(ctor.decl_id);
                cargs[0] = obj;
                Value cr = Value::Void();
                ok = Invoke(ctor_id, cargs, argc, &cr);
                if (!ok) goto done;
                PUSH(obj);
                break;
            }
            case 0x74: case 0x75: { // castclass / isinst
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 target = metadata_.ResolveTypeToken(token);
                Value v; POP(v);
                bool isType = (v.tag == VT_NULL) || IsAssignable(ObjectTypeId(v), target) ||
                              (target == string_type_id_ && v.tag == VT_STRING) ||
                              (target == array_type_id_ && v.tag == VT_ARRAY);
                if (!isType && v.tag == VT_ARRAY && v.data.array && target) {
                    const TypeInfo& tt = metadata_.Type(target);
                    if (tt.element_id) {
                        isType = v.data.array->element_type == tt.element_id ||
                                 IsAssignable(v.data.array->element_type, tt.element_id);
                    }
                }
                if (op == 0x75) {
                    PUSH(isType ? v : Value::Null());
                } else {
                    if (!isType) { ok = Fail("castclass failed", method_id); goto done; }
                    PUSH(v);
                }
                break;
            }
            case 0x76: { Value a; POP(a); PUSH(Value::R4((f32)(u32)NumI64(a))); break; }
            case 0x79: case 0xA5: { // unbox / unbox.any
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 target = metadata_.ResolveTypeToken(token);
                Value box; POP(box);
                if (box.tag == VT_NULL) { ok = Fail("unbox null", method_id); goto done; }
                if (box.tag == VT_OBJECT || box.tag == VT_STRUCT) {
                    Object* o = box.data.obj;
                    if (o && o->type_id != target && !IsAssignable(o->type_id, target)) {
                        ok = Fail("unbox type mismatch", method_id);
                        goto done;
                    }
                    if (op == 0xA5) {
                        if (o && runtime_types_[o->type_id].total_slots > 0) PUSH(CopyValue(o->Fields()[0]));
                        else PUSH(Value::Null());
                    } else {
                        if (o && runtime_types_[o->type_id].total_slots > 0) PUSH(Value::ByRef(&o->Fields()[0]));
                        else PUSH(Value::Null());
                    }
                } else {
                    // Primitive and enum values are stored unboxed in this
                    // runtime. Accept them for value-type targets so code such
                    // as Enum.GetValues() consumers can unbox normally.
                    const TypeInfo& tt = metadata_.Type(target);
                    if (tt.is_value_type || tt.is_enum) {
                        PUSH(op == 0xA5 ? CopyValue(box) : box);
                    } else {
                        ok = Fail("unbox of non-object", method_id);
                        goto done;
                    }
                }
                break;
            }
            case 0x7A: ok = Fail("managed exception throw is not implemented yet", method_id); goto done;
            case 0x7B: { // ldfld
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 fid = metadata_.ResolveFieldToken(token);
                Value obj; POP(obj);
                if (obj.tag == VT_NULL) { ok = Fail("ldfld on null", method_id); goto done; }
                if (fid) {
                    PUSH(CopyValue(GetField(fid, obj)));
                } else {
                    const MemberRefInfo* mr = metadata_.MemberRefForToken(token);
                    Value v;
                    if (!mr || !GetExternalField(*mr, obj, &v)) {
                        ok = Fail("unresolved external field", method_id);
                        goto done;
                    }
                    PUSH(v);
                }
                break;
            }
            case 0x7C: { // ldflda
                u32 fid = metadata_.ResolveFieldToken(ReadU32LE(code + pc)); pc += 4;
                Value obj; POP(obj);
                Object* o = (obj.tag == VT_OBJECT || obj.tag == VT_STRUCT) ? obj.data.obj : 0;
                if (!o) { ok = Fail("ldflda on null", method_id); goto done; }
                u32 slot = FieldSlot(fid);
                if (slot >= runtime_types_[o->type_id].total_slots) {
                    const FieldInfo& ff = metadata_.Field(fid);
                    const TypeInfo& ot = metadata_.Type(o->type_id);
                    LogError("interp", "bad field slot field=%s id=%u slot=%u type=%s total=%u",
                             ff.name, fid, slot, ot.full_name,
                             runtime_types_[o->type_id].total_slots);
                    ok = Fail("bad field slot", method_id);
                    goto done;
                }
                PUSH(Value::ByRef(&o->Fields()[slot]));
                break;
            }
            case 0x7D: { // stfld
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 fid = metadata_.ResolveFieldToken(token);
                Value val; POP(val); Value obj; POP(obj);
                if (fid) {
                    SetField(fid, obj, CopyValue(val));
                } else {
                    const MemberRefInfo* mr = metadata_.MemberRefForToken(token);
                    if (!mr || !SetExternalField(*mr, obj, val)) {
                        ok = Fail("unresolved external field store", method_id);
                        goto done;
                    }
                }
                break;
            }
            case 0x7E: { // ldsfld
                u32 fid = metadata_.ResolveFieldToken(ReadU32LE(code + pc)); pc += 4;
                const FieldInfo& f = metadata_.Field(fid);
                EnsureTypeInitialized(f.decl_id);
                PUSH(CopyValue(GetField(fid, Value::Null())));
                break;
            }
            case 0x7F: { // ldsflda
                u32 fid = metadata_.ResolveFieldToken(ReadU32LE(code + pc)); pc += 4;
                const FieldInfo& f = metadata_.Field(fid);
                EnsureTypeInitialized(f.decl_id);
                TypeRuntime& rt = runtime_types_[f.decl_id];
                u32 slot = FieldSlot(fid);
                if (!rt.static_values || slot >= rt.static_slots) { ok = Fail("bad static field", method_id); goto done; }
                PUSH(Value::ByRef(&rt.static_values[slot]));
                break;
            }
            case 0x80: { // stsfld
                u32 fid = metadata_.ResolveFieldToken(ReadU32LE(code + pc)); pc += 4;
                const FieldInfo& f = metadata_.Field(fid);
                EnsureTypeInitialized(f.decl_id);
                Value val; POP(val);
                SetField(fid, Value::Null(), CopyValue(val));
                break;
            }
            case 0x81: { // stobj
                u32 tok = ReadU32LE(code + pc); pc += 4;
                (void)tok;
                Value val; POP(val); Value dst; POP(dst);
                if (dst.tag == VT_BYREF && dst.data.ref) StoreValue(dst.data.ref, CopyValue(val));
                break;
            }
            case 0x8C: { // box
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 target = metadata_.ResolveTypeToken(token);
                Value v; POP(v);
                Object* o = AllocObject(target);
                if (!o) { ok = Fail("box allocation failed", method_id); goto done; }
                if (runtime_types_[target].total_slots > 0) StoreValue(&o->Fields()[0], v);
                PUSH(Value::ObjectRef(o));
                break;
            }
            case 0x8D: { // newarr
                u32 token = ReadU32LE(code + pc); pc += 4;
                u32 elem = metadata_.ResolveTypeToken(token);
                Value len; POP(len);
                u32 n = (u32)NumI64(len);
                if (n > 16u * 1024u * 1024u) { ok = Fail("array too large", method_id); goto done; }
                PUSH(NewArrayValue(elem, n));
                break;
            }
            case 0x8E: { // ldlen
                Value a; POP(a);
                if (a.tag != VT_ARRAY || !a.data.array) { ok = Fail("ldlen on non-array", method_id); goto done; }
                PUSH(Value::NativeUInt(a.data.array->length));
                break;
            }
            case 0x8F: { // ldelema
                u32 token = ReadU32LE(code + pc); pc += 4;
                (void)token;
                Value idx; POP(idx); Value arr; POP(arr);
                if (arr.tag != VT_ARRAY || !arr.data.array) { ok = Fail("ldelema on non-array", method_id); goto done; }
                u32 i = (u32)NumI64(idx);
                if (i >= arr.data.array->length) { ok = Fail("array index out of range", method_id); goto done; }
                PUSH(Value::ByRef(&arr.data.array->Elements()[i]));
                break;
            }
            case 0x90: case 0x91: case 0x92: case 0x93: case 0x94:
            case 0x95: case 0x96: case 0x97: case 0x98: case 0x99: case 0x9A: {
                Value idx; POP(idx); Value arr; POP(arr);
                if (arr.tag != VT_ARRAY || !arr.data.array) { ok = Fail("ldelem on non-array", method_id); goto done; }
                u32 i = (u32)NumI64(idx);
                if (i >= arr.data.array->length) { ok = Fail("array index out of range", method_id); goto done; }
                Value v = arr.data.array->Elements()[i];
                static const u32 tags[] = { VT_I1, VT_U1, VT_I2, VT_U2, VT_I4, VT_U4, VT_I8, VT_I8, VT_R4, VT_R8, VT_OBJECT };
                if (op != 0x9A) v = ConvertTag(v, tags[op - 0x90]);
                PUSH(v);
                break;
            }
            case 0x9B: case 0x9C: case 0x9D: case 0x9E: case 0x9F:
            case 0xA0: case 0xA1: case 0xA2: {
                Value v; POP(v); Value idx; POP(idx); Value arr; POP(arr);
                if (arr.tag != VT_ARRAY || !arr.data.array) { ok = Fail("stelem on non-array", method_id); goto done; }
                u32 i = (u32)NumI64(idx);
                if (i >= arr.data.array->length) { ok = Fail("array index out of range", method_id); goto done; }
                StoreValue(&arr.data.array->Elements()[i], v);
                break;
            }
            case 0xA3: case 0xA4: { // ldelem / stelem (typed)
                u32 token = ReadU32LE(code + pc); pc += 4;
                (void)token;
                if (op == 0xA3) {
                    Value idx; POP(idx); Value arr; POP(arr);
                    if (arr.tag != VT_ARRAY || !arr.data.array) { ok = Fail("ldelem on non-array", method_id); goto done; }
                    u32 i = (u32)NumI64(idx);
                    if (i >= arr.data.array->length) { ok = Fail("array index out of range", method_id); goto done; }
                    PUSH(CopyValue(arr.data.array->Elements()[i]));
                } else {
                    Value v; POP(v); Value idx; POP(idx); Value arr; POP(arr);
                    if (arr.tag != VT_ARRAY || !arr.data.array) { ok = Fail("stelem on non-array", method_id); goto done; }
                    u32 i = (u32)NumI64(idx);
                    if (i >= arr.data.array->length) { ok = Fail("array index out of range", method_id); goto done; }
                    StoreValue(&arr.data.array->Elements()[i], v);
                }
                break;
            }
            case 0xC2: { u32 tok = ReadU32LE(code + pc); pc += 4; (void)tok; Value v; POP(v); PUSH(v); break; }
            case 0xC3: break;
            case 0xC6: { u32 tok = ReadU32LE(code + pc); pc += 4; (void)tok; Value v; POP(v); PUSH(v); break; }
            case 0xD0: { u32 tok = ReadU32LE(code + pc); pc += 4; PUSH(Value::I4((s32)tok)); break; }
            case 0xD1: { Value a; POP(a); PUSH(ConvertTag(a, VT_U2)); break; }
            case 0xD2: { Value a; POP(a); PUSH(ConvertTag(a, VT_U1)); break; }
            case 0xD3: { Value a; POP(a); PUSH(ConvertTag(a, VT_NATIVEINT)); break; }
            case 0xD4: case 0xD5: { Value a; POP(a); PUSH(ConvertTag(a, op == 0xD4 ? VT_NATIVEINT : VT_NATIVEUINT)); break; }
            case 0xD6: case 0xD7: case 0xD8: case 0xD9: case 0xDA: case 0xDB: {
                Value b, a; POP2(b, a);
                s64 x = NumI64(a), y = NumI64(b);
                s64 z = (op == 0xD6 || op == 0xD7) ? x + y : (op == 0xD8 || op == 0xD9) ? x * y : x - y;
                PUSH(Value::I4((s32)z));
                break;
            }
            case 0xDC: ok = Fail("endfinally outside exception region", method_id); goto done;
            case 0xDD: { s32 off = ReadS32LE(code + pc); pc += 4; BRANCH(off); break; }
            case 0xDE: { s8 off = (s8)code[pc++]; BRANCH(off); break; }
            case 0xDF: { Value v; POP(v); Value addr; POP(addr); if (addr.tag == VT_BYREF && addr.data.ref) *addr.data.ref = v; break; }
            case 0xE0: { Value a; POP(a); PUSH(ConvertTag(a, VT_NATIVEUINT)); break; }
            case 0xFE: {
                u8 op2 = code[pc++];
                switch (op2) {
                    case 0x01: case 0x02: case 0x03: case 0x04: case 0x05: {
                        Value b, a; POP2(b, a);
                        bool r = false;
                        if (a.tag == VT_NULL || b.tag == VT_NULL) {
                            bool an = a.tag == VT_NULL, bn = b.tag == VT_NULL;
                            if (op2 == 0x01) r = an && bn;
                            else if (op2 == 0x02) r = !an && bn;
                            else if (op2 == 0x04) r = an && !bn;
                        } else if (a.tag == VT_R4 || a.tag == VT_R8 || b.tag == VT_R4 || b.tag == VT_R8) {
                            f64 x = NumF64(a), y = NumF64(b);
                            if (op2 == 0x01) r = x == y;
                            else if (op2 == 0x02 || op2 == 0x03) r = x > y;
                            else if (op2 == 0x04 || op2 == 0x05) r = x < y;
                        } else {
                            s64 x = NumI64(a), y = NumI64(b);
                            if (op2 == 0x01) r = x == y;
                            else if (op2 == 0x02) r = x > y;
                            else if (op2 == 0x03) r = (u64)x > (u64)y;
                            else if (op2 == 0x04) r = x < y;
                            else if (op2 == 0x05) r = (u64)x < (u64)y;
                        }
                        PUSH(Value::Bool(r));
                        break;
                    }
                    case 0x06: case 0x07: { // ldftn / ldvirtftn
                        u32 token = ReadU32LE(code + pc); pc += 4;
                        u32 mid = metadata_.ResolveMethodToken(token);
                        Value target = Value::Null();
                        bool is_static = true;
                        if (op2 == 0x07) {
                            // ldvirtftn pops the object reference it resolves
                            // through; the delegate ctor receives the original
                            // (duplicated) reference as its target.
                            POP(target);
                            is_static = false;
                        }
                        PUSH(NewDelegate(0, mid, target, is_static));
                        break;
                    }
                    case 0x09: { u32 i = ReadU16LE(code + pc); pc += 2; PUSH(CopyValue(arg_slots[i])); break; }
                    case 0x0A: { u32 i = ReadU16LE(code + pc); pc += 2; PUSH(Value::ByRef(&arg_slots[i])); break; }
                    case 0x0B: { u32 i = ReadU16LE(code + pc); pc += 2; Value v; POP(v); StoreValue(&arg_slots[i], v); break; }
                    case 0x0C: { u32 i = ReadU16LE(code + pc); pc += 2; PUSH(CopyValue(locals[i])); break; }
                    case 0x0D: { u32 i = ReadU16LE(code + pc); pc += 2; PUSH(Value::ByRef(&locals[i])); break; }
                    case 0x0E: { u32 i = ReadU16LE(code + pc); pc += 2; Value v; POP(v); StoreValue(&locals[i], v); break; }
                    case 0x12: pc += 1; break;
                    case 0x13: case 0x14: case 0x1E: break;
                    case 0x15: { // initobj
                        u32 token = ReadU32LE(code + pc); pc += 4;
                        u32 tid = metadata_.ResolveTypeToken(token);
                        Value addr; POP(addr);
                        if (addr.tag != VT_BYREF || !addr.data.ref) { ok = Fail("initobj on non-byref", method_id); goto done; }
                        const TypeInfo& ti = metadata_.Type(tid);
                        if (strcmp(ti.full_name, "UnityEngine.Vector2") == 0) {
                            *addr.data.ref = NewVec2(0.0f, 0.0f);
                        } else if (strcmp(ti.full_name, "UnityEngine.Vector3") == 0) {
                            *addr.data.ref = NewVec3(0.0f, 0.0f, 0.0f);
                        } else if (ti.is_value_type &&
                                   (runtime_types_[tid].total_slots > 0 ||
                                    strcmp(ti.full_name, "UnityEngine.Rect") == 0 ||
                                    strcmp(ti.full_name, "UnityEngine.Color") == 0 ||
                                    strcmp(ti.full_name, "UnityEngine.Quaternion") == 0 ||
                                    strcmp(ti.full_name, "UnityEngine.Vector4") == 0 ||
                                    strcmp(ti.full_name, "UnityEngine.Bounds") == 0)) {
                            Object* o = AllocObject(tid);
                            if (o) *addr.data.ref = Value::StructRef(o);
                        } else {
                            *addr.data.ref = Value::Null();
                        }
                        break;
                    }
                    case 0x16: { u32 tok = ReadU32LE(code + pc); pc += 4; (void)tok; break; }
                    case 0x17: case 0x18: { Value n; POP(n); Value addr; POP(addr); (void)n; (void)addr; break; }
                    case 0x19: pc += 1; break;
                    case 0x1A: ok = Fail("rethrow is not implemented yet", method_id); goto done;
                    case 0x1C: { u32 token = ReadU32LE(code + pc); pc += 4; u32 tid = metadata_.ResolveTypeToken(token); PUSH(Value::I4((s32)metadata_.Type(tid).size)); break; }
                    case 0x1D: break;
                    default: ok = Fail("unsupported two-byte opcode", method_id); goto done;
                }
                break;
            }
            default:
                ok = Fail("unsupported IL opcode", method_id);
                goto done;
        }
    }

done:
    current_method_id_ = saved_method;
    if (!ok) {
        LogError("interp", "execute failed %s.%s id=%u pc=%u op=%02x",
                 metadata_.Type(method.decl_id).full_name, method.name,
                 method_id, last_pc, last_op);
    }
    if (ok && returned && ret) *ret = CopyValue(result);
    if (locals) MemFree(locals);
    MemFree(arg_slots);
    return ok;
#undef PUSH
#undef POP
#undef POP2
#undef BRANCH
}

static s32 ToI32Static(const Value& v) { return (s32)NumI64(v); }
s32 Interpreter::ToI32(const Value& v) { return ToI32Static(v); }
u32 Interpreter::ToU32(const Value& v) { return (u32)NumI64(v); }
s64 Interpreter::ToI64(const Value& v) { return NumI64(v); }
f32 Interpreter::ToF32(const Value& v) { return (f32)NumF64(v); }
f64 Interpreter::ToF64(const Value& v) { return NumF64(v); }
bool Interpreter::ToBool(const Value& v) { return IsTrue(v); }

} // namespace ch360
