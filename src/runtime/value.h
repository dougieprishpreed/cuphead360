#ifndef CH360_RUNTIME_VALUE_H
#define CH360_RUNTIME_VALUE_H

#include "core/types.h"

namespace ch360 {

// Runtime value tags. Primitive tags mirror the CLI element types closely
// enough that conversions are explicit and testable.
enum ValueTag {
    VT_NULL = 0,
    VT_VOID,
    VT_BOOL,
    VT_CHAR,
    VT_I1,
    VT_U1,
    VT_I2,
    VT_U2,
    VT_I4,
    VT_U4,
    VT_I8,
    VT_U8,
    VT_R4,
    VT_R8,
    VT_NATIVEINT,
    VT_NATIVEUINT,
    VT_STRING,
    VT_OBJECT,
    VT_ARRAY,
    VT_STRUCT,
    VT_DELEGATE,
    VT_BYREF,
    VT_VEC2,
    VT_VEC3,
    VT_QUAT,
    VT_COLOR,
    VT_RECT
};

struct Object;
struct ArrayObject;
struct StringObject;
struct DelegateObject;

struct Value {
    u32 tag;
    union {
        s32 i32;
        u32 u32;
        s64 i64;
        u64 u64;
        f32 f32;
        f64 f64;
        void* ptr;
        Object* obj;
        ArrayObject* array;
        StringObject* str;
        DelegateObject* del;
        Value* ref;
    } data;

    Value() : tag(VT_NULL) { data.u64 = 0; }

    static Value Null() { return Value(); }
    static Value Void() { Value v; v.tag = VT_VOID; return v; }
    static Value Bool(bool x) { Value v; v.tag = VT_BOOL; v.data.i32 = x ? 1 : 0; return v; }
    static Value Char(u16 x) { Value v; v.tag = VT_CHAR; v.data.u32 = x; return v; }
    static Value I1(s8 x) { Value v; v.tag = VT_I1; v.data.i32 = x; return v; }
    static Value U1(u8 x) { Value v; v.tag = VT_U1; v.data.u32 = x; return v; }
    static Value I2(s16 x) { Value v; v.tag = VT_I2; v.data.i32 = x; return v; }
    static Value U2(u16 x) { Value v; v.tag = VT_U2; v.data.u32 = x; return v; }
    static Value I4(s32 x) { Value v; v.tag = VT_I4; v.data.i32 = x; return v; }
    static Value U4(u32 x) { Value v; v.tag = VT_U4; v.data.u32 = x; return v; }
    static Value I8(s64 x) { Value v; v.tag = VT_I8; v.data.i64 = x; return v; }
    static Value U8(u64 x) { Value v; v.tag = VT_U8; v.data.u64 = x; return v; }
    static Value R4(f32 x) { Value v; v.tag = VT_R4; v.data.f32 = x; return v; }
    static Value R8(f64 x) { Value v; v.tag = VT_R8; v.data.f64 = x; return v; }
    static Value NativeInt(s32 x) { Value v; v.tag = VT_NATIVEINT; v.data.i32 = x; return v; }
    static Value NativeUInt(u32 x) { Value v; v.tag = VT_NATIVEUINT; v.data.u32 = x; return v; }
    static Value ObjectRef(Object* o) { Value v; v.tag = VT_OBJECT; v.data.obj = o; return v; }
    static Value StringRef(StringObject* s) { Value v; v.tag = VT_STRING; v.data.str = s; return v; }
    static Value ArrayRef(ArrayObject* a) { Value v; v.tag = VT_ARRAY; v.data.array = a; return v; }
    static Value StructRef(Object* o) { Value v; v.tag = VT_STRUCT; v.data.obj = o; return v; }
    static Value DelegateRef(DelegateObject* d) { Value v; v.tag = VT_DELEGATE; v.data.del = d; return v; }
    static Value ByRef(Value* p) { Value v; v.tag = VT_BYREF; v.data.ref = p; return v; }
    static Value Vec2Ref(void* p) { Value v; v.tag = VT_VEC2; v.data.ptr = p; return v; }

    bool IsNull() const { return tag == VT_NULL; }
    bool IsReference() const {
        return tag == VT_STRING || tag == VT_OBJECT || tag == VT_ARRAY ||
               tag == VT_STRUCT || tag == VT_DELEGATE || tag == VT_BYREF;
    }
};

inline s32 ValueAsI32(const Value& v) { return v.data.i32; }
inline u32 ValueAsU32(const Value& v) { return v.data.u32; }
inline s64 ValueAsI64(const Value& v) { return v.data.i64; }
inline u64 ValueAsU64(const Value& v) { return v.data.u64; }
inline f32 ValueAsF32(const Value& v) { return v.data.f32; }
inline f64 ValueAsF64(const Value& v) { return v.data.f64; }
inline bool ValueAsBool(const Value& v) { return v.data.i32 != 0; }

} // namespace ch360

#endif // CH360_RUNTIME_VALUE_H
