#ifndef CH360_RUNTIME_INTERPRETER_H
#define CH360_RUNTIME_INTERPRETER_H

#include "core/containers.h"
#include "runtime/metadata.h"
#include "runtime/object.h"
#include "runtime/value.h"

namespace ch360 {

class Scene;

struct TypeRuntime {
    u32 total_slots;
    u32 static_slots;
    Value* static_values;
    u32 vtable_size;
    u32* vtable;
    u8 initialized;
    u8 initializing;
    u8 reserved0;
    u8 reserved1;
    u32 cctor_method;
};

typedef bool (*NativeFn)(class Interpreter&, const MemberRefInfo&, u32 arg_count,
                         Value* args, Value* ret);

class Interpreter {
public:
    explicit Interpreter(MetadataPackage& metadata);
    ~Interpreter();

    bool Init();
    void Shutdown();

    MetadataPackage& Metadata() { return metadata_; }

    bool Invoke(u32 method_id, Value* args, u32 arg_count, Value* ret);
    bool InvokeMemberRef(const MemberRefInfo& member, u32 arg_count, Value* args, Value* ret);

    Value NewObjectValue(u32 type_id);
    Object* AllocObject(u32 type_id);
    Object* AllocObjectWithSlots(u32 type_id, u32 slots);
    void* TrackAllocation(void* p);
    Value CopyValue(const Value& v);
    Value NewStructValue(u32 type_id);
    Value NewArrayValue(u32 element_type_id, u32 length);
    Value NewDictionaryValue(u32 type_id);
    bool DictionarySet(const Value& dict, const Value& key, const Value& value);
    Value NewStringUtf16(const u16* chars, u32 length);
    Value NewStringUtf8(const char* text);
    Value NewStringAscii(const char* text);
    Value NewVec2(f32 x, f32 y);
    Value NewVec3(f32 x, f32 y, f32 z);
    Value NewDelegate(u32 type_id, u32 method_id, Value target, bool is_static);

    bool IsAssignable(u32 derived_type, u32 base_type) const;
    u32 ObjectTypeId(const Value& v) const;
    u32 StringTypeId() const { return string_type_id_; }
    u32 Int32TypeId() const { return int32_type_id_; }
    u32 SingleTypeId() const { return single_type_id_; }
    u32 BoolTypeId() const { return bool_type_id_; }

    const char* TypeName(u32 id) const;
    const char* StringUtf8(const Value& v, u32* out_len = 0) const;

    void SetTrace(bool enabled) { trace_ = enabled; }
    bool Trace() const { return trace_; }
    u32 LastError() const { return last_error_; }

    // Static helpers for native bindings.
    static s32 ToI32(const Value& v);
    static u32 ToU32(const Value& v);
    static s64 ToI64(const Value& v);
    static f32 ToF32(const Value& v);
    static f64 ToF64(const Value& v);
    static bool ToBool(const Value& v);

    // Native method registration (implemented in native.cpp / unity_bind.cpp).
    bool InvokeNative(const MemberRefInfo& member, u32 arg_count, Value* args, Value* ret);

    // Binds a loaded scene to managed UnityEngine.GameObject/Transform/
    // Component objects. Native Unity methods use these mappings.
    bool BindScene(Scene& scene);
    Scene* BoundScene() const { return scene_; }
    Value ManagedGameObject(u32 object_index) const;
    Value ManagedTransform(u32 object_index) const;
    Value ManagedComponent(u32 component_index) const;
    u32 ManagedObjectIndexFor(const Value& v) const;
    u32 ManagedComponentIndexFor(const Value& v) const;
    void AddManagedBinding(Object* obj, u32 object_index, u32 component_index);
    u32 GameObjectTypeId() const { return gameobject_type_id_; }
    u32 TransformTypeId() const { return transform_type_id_; }
    u32 ComponentTypeId() const { return component_type_id_; }
    u32 MonoBehaviourTypeId() const { return monobehaviour_type_id_; }

    // Runtime field access used by native bindings and the engine.
    Value GetField(u32 field_id, const Value& instance, bool* ok = 0);
    void SetField(u32 field_id, const Value& instance, const Value& value);
    bool GetExternalField(const MemberRefInfo& member, const Value& instance, Value* out);
    bool SetExternalField(const MemberRefInfo& member, const Value& instance, const Value& value);

    // Current generic method instantiation (MethodSpec) context, used by
    // native Unity methods such as GetComponent<T>/FindObjectsOfType<T>.
    u32 GenericArgCount() const { return generic_arg_count_; }
    u32 GenericArgType(u32 index) const;

    // Static type initialization / vtable construction.
    bool EnsureTypeInitialized(u32 type_id);
    const TypeRuntime& RuntimeType(u32 type_id) const;
    TypeRuntime& MutableTypeRuntime(u32 type_id);

private:
    bool Fail(const char* message, u32 method_id = 0);
    bool BuildRuntimeTypes();
    void BuildFieldSlots(u32 type_id, u32 base_slots, bool* visiting);
    bool BuildVtable(u32 type_id, bool* visiting);
    u32 FieldSlot(u32 field_id) const;
    bool IsStaticField(u32 field_id) const;

    Value DefaultValue(u32 sig_index) const;
    void StoreValue(Value* dest, const Value& v);
    bool BoxValue(const Value& v, Value* out);
    bool UnboxValue(const Value& boxed, u32 target_type, Value* out);
    bool IsDelegateType(u32 type_id) const;
    bool InvokeDelegate(DelegateObject* d, u32 arg_count, Value* args, Value* ret);

    bool Execute(u32 method_id, const MethodInfo& method, const MethodSigInfo& sig,
                 Value* args, u32 arg_count, Value* ret);
    bool InvokeVirtual(u32 method_id, u32 arg_count, Value* args, Value* ret);

    MetadataPackage& metadata_;
    Buffer<TypeRuntime> runtime_types_; // index 0 unused
    Buffer<u32> field_slots_;           // 1-based field id
    Buffer<u32> field_static_;          // 1 if static
    Buffer<Object*> objects_;
    bool trace_;
    u32 last_error_;
    u32 current_method_id_;
    u32 generic_arg_start_;
    u32 generic_arg_count_;

    u32 string_type_id_;
    u32 object_type_id_;
    u32 int32_type_id_;
    u32 single_type_id_;
    u32 bool_type_id_;
    u32 void_type_id_;
    u32 array_type_id_;
    u32 exception_type_id_;
    u32 mathf_type_id_;
    u32 gameobject_type_id_;
    u32 transform_type_id_;
    u32 component_type_id_;
    u32 monobehaviour_type_id_;
    u32 sprite_renderer_type_id_;
    u32 animator_type_id_;

    Scene* scene_;
    Buffer<Value> managed_gameobjects_;
    Buffer<Value> managed_transforms_;
    Buffer<Value> managed_components_;

    struct ManagedBinding {
        Object* obj;
        u32 object_index;
        u32 component_index;
    };
    Buffer<ManagedBinding> bindings_;

    friend struct NativeContext;
};

bool InvokeUnityNative(Interpreter& interp, const MemberRefInfo& member,
                       u32 arg_count, Value* args, Value* ret);

// Feeds the minimal Rewired action bridge used by PlayerInput.
void SetRewiredInputState(f32 axis_x, f32 axis_y, u32 buttons);

} // namespace ch360

#endif // CH360_RUNTIME_INTERPRETER_H
