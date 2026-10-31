#ifndef CH360_RUNTIME_OBJECT_H
#define CH360_RUNTIME_OBJECT_H

#include "core/types.h"
#include "runtime/value.h"

namespace ch360 {

struct Vec2 { f32 x, y; };
struct Vec3 { f32 x, y, z; };
struct Vec4 { f32 x, y, z, w; };

// Every heap object starts with the same header. Field slots are Values;
// metadata field offsets are not used for managed field access (all slots are
// 8 bytes and host-endian, which keeps the interpreter correct on PPC).
struct Object {
    u32 type_id;
    u32 flags;
    u32 slots;      // actual allocated Value slots
    u32 reserved;
    Value* Fields() { return (Value*)(this + 1); }
    const Value* Fields() const { return (const Value*)(this + 1); }
};

struct ArrayObject {
    u32 type_id;
    u32 length;
    u32 element_type;
    u32 reserved;
    Value* Elements() { return (Value*)(this + 1); }
    const Value* Elements() const { return (const Value*)(this + 1); }
    // Returns the storage size for an array with `n` elements.
    static u32 SizeFor(u32 n) { return (u32)sizeof(ArrayObject) + n * (u32)sizeof(Value); }
};

struct StringObject {
    u32 type_id;
    u32 length;
    u16* Chars() { return (u16*)(this + 1); }
    const u16* Chars() const { return (const u16*)(this + 1); }
    static u32 SizeFor(u32 chars) { return (u32)sizeof(StringObject) + chars * 2u; }
};

struct DelegateObject {
    u32 type_id;
    u32 method_id;
    u32 flags;       // bit 0: static target
    Value target;
    DelegateObject* next; // multicast chain
};

// Native BCL List<T>. It is a real managed object (type_id is the generic
// instance) with a back-to-front Value store. The interpreter recognizes it
// by its generic base type name and dispatches its methods natively.
struct ListObject {
    u32 type_id;
    u32 count;
    u32 capacity;
    u32 element_type;
    Value* items;
};

// Native BCL Dictionary<K,V> placeholder layout. Entries are kept in a
// simple open array; the game's dictionaries are small.
struct DictEntry {
    Value key;
    Value value;
    u32 used;
    u32 reserved;
};

struct DictionaryObject {
    u32 type_id;
    u32 count;
    u32 key_type;
    u32 value_type;
    DictEntry* entries;
    u32 capacity;
    u32 reserved;
};

inline Object* AsObject(Value v) { return v.data.obj; }
inline ArrayObject* AsArray(Value v) { return v.data.array; }
inline StringObject* AsString(Value v) { return v.data.str; }

} // namespace ch360

#endif // CH360_RUNTIME_OBJECT_H
