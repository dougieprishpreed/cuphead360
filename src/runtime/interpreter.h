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
    const MetadataPackage& Metadata() const { return metadata_; }

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
    bool IsLiveObject(const Object* obj) const;
    u32 StringTypeId() const { return string_type_id_; }
    u32 Int32TypeId() const { return int32_type_id_; }
    u32 SingleTypeId() const { return single_type_id_; }
    u32 BoolTypeId() const { return bool_type_id_; }

    const char* TypeName(u32 id) const;
    const char* StringUtf8(const Value& v, u32* out_len = 0) const;

    void SetTrace(bool enabled) { trace_ = enabled; }
    bool Trace() const { return trace_; }
    u32 CurrentMethodId() const { return current_method_id_; }
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
    bool BindScene(Scene& scene, bool run_script_ctors = false);
    Scene* BoundScene() const { return scene_; }
    Value ManagedGameObject(u32 object_index) const;
    Value ManagedTransform(u32 object_index) const;
    Value ManagedComponent(u32 component_index) const;
    u32 ManagedObjectIndexFor(const Value& v) const;
    u32 ManagedComponentIndexFor(const Value& v) const;
    void AddManagedBinding(Object* obj, u32 object_index, u32 component_index);
    bool IsComponentAwake(u32 component_index) const;
    void MarkComponentAwake(u32 component_index);
    bool AppendRuntimeGameObject(const Value& name, Value* out_go);
    bool AppendRuntimeComponent(u32 object_index, u32 scene_type, const char* controller,
                                u32* out_component_index, Value* out_component);
    bool CloneGameObject(u32 template_object_index, Value* out_go);
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

    // Coroutine scheduler for original IEnumerator state machines.
    bool StartCoroutine(const Value& owner, const Value& enumerator, Value* handle,
                        u32 parent_entry = 0);
    bool StopAllCoroutines(const Value& owner);
    bool StopCoroutine(const Value& owner, const Value& target);
    void StopCoroutinesForGameObject(u32 object_index, bool descendants);
    void StopCoroutinesForComponent(u32 component_index);
    void UpdateCoroutines(f32 dt);

    struct CoroutineInfo {
        const char* iterator_type;
        const char* owner_type;
        const char* last_yield;
        s32 state;
        u8 running;
        u8 completed;
        u8 stopped;
        u8 orphaned;
        u8 faulted;
        u8 wait_kind;
        u32 last_step_pass;
        const char* fault_site;
    };
    u32 CoroutineCount() const { return coroutines_.Size(); }
    u32 CoroutinePass() const { return coroutine_pass_; }
    bool GetCoroutineInfo(u32 index, CoroutineInfo* out) const;
    bool GetCoroutineInfo(const Value& handle, CoroutineInfo* out) const;
    bool IsCoroutineRunning(const Value& handle) const;

    enum LedgerCategory {
        LEDGER_FAULT = 0,
        LEDGER_UNSUPPORTED = 1,
        LEDGER_NOTE = 2
    };
    void SetRootContext(u32 iterator_type, u32 owner_type, const char* callback);
    void ClearRootContext();
    void RecordFaultSite(const char* site, const char* detail = 0);
    void RecordUnsupportedSite(const char* site, const char* detail = 0);
    void RecordNoteSite(const char* site, const char* detail = 0);
    u32 PrintLedgerSummary(u32 limit = 32);
    u32 LedgerSummaryGroupCount() const { return last_summary_groups_; }
    u32 LedgerRecordCount() const { return ledger_.Size(); }
    u32 LedgerObservationCount() const;
    u32 LedgerSiteCount(LedgerCategory category, const char* site) const;
    u32 LedgerDroppedCount() const { return ledger_dropped_; }
    u32 LedgerAllocationLossCount() const { return ledger_allocation_loss_; }
    u32 LedgerPushFailureCount() const { return ledger_push_failed_; }
    u32 LedgerAncestryTruncationCount() const { return ledger_ancestry_truncated_; }
    bool FirstLedgerSite(LedgerCategory category, char* out, u32 out_size) const;
    bool FirstLedgerSitePrefix(LedgerCategory category, const char* prefix,
                               char* out, u32 out_size) const;
    bool FirstLedgerSiteForRoot(LedgerCategory category, u32 iterator_type,
                                u32 owner_type, char* out, u32 out_size) const;

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
    bool OwnerActive(u32 owner_object_index) const;
    u32 InactiveAncestor(u32 owner_object_index) const;
    bool EntryMatchesTarget(u32 entry_id, const Value& target) const;
    bool EntryActive(u32 entry_id) const;
    bool EntryChainActive(u32 entry_id) const;
    void StopEntry(u32 entry_id);
    void FaultEntry(u32 entry_id, const char* site, const char* detail);
    bool ChargeStep(u32 entry_id);
    bool StepEntry(u32 entry_id, f32 dt, bool timer_tick);
    bool StepEntryImpl(u32 entry_id, f32 dt, bool timer_tick);
    f32 ReadCoroutineTimeScale();
    struct LedgerRecord;
    void RecordLedger(LedgerCategory category, const char* native_site,
                      u32 managed_site, const char* detail);
    void RecordStrictPrefixes(const LedgerRecord& record);
    const char* LedgerSite(const LedgerRecord& record,
                           char* scratch, u32 scratch_size) const;

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
    u32 generic_arg_types_[32];
    u32 generic_arg_type_count_;

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
    Buffer<u8> awake_flags_;
    mutable Buffer<char> utf8_scratch_;
    Buffer<char*> runtime_names_;

    struct ManagedBinding {
        Object* obj;
        u32 object_index;
        u32 component_index;
    };
    Buffer<ManagedBinding> bindings_;

    struct CoroutineEntry {
        Value enumerator;
        Value owner;
        Value handle;
        u32 parent_entry;
        u32 wait_entry;
        u32 owner_object_index;
        f32 wait;
        char last_yield[96];
        char fault_site[160];
        u8 active;
        u8 completed;
        u8 stopped;
        u8 orphaned;
        u8 faulted;
        u8 wait_kind;
        u16 reserved0;
        u32 timer_prepared_pass;
        u32 last_step_pass;
        u32 skip_pass;
    };
    Buffer<CoroutineEntry> coroutines_;

    struct RootContext {
        u32 iterator_type;
        u32 owner_type;
        char callback[128];
        u8 kind;
        u8 reserved0;
        u8 reserved1;
        u8 reserved2;
    };
    RootContext root_context_;

    struct LedgerRecord {
        u32 frame;
        u32 count;
        u32 managed_site;
        u32 ancestor_count;
        u32 ancestors[32];
        char native_site[160];
        char detail[192];
        u32 root_iterator_type;
        u32 root_owner_type;
        char root_callback[128];
        u8 category;
        u8 ancestors_truncated;
        u8 reserved0;
        u8 reserved1;
    };
    struct RootScope {
        Interpreter& interp;
        RootContext saved;
        bool owns;
        RootScope(Interpreter& owner, u32 method_id, u32 arg_count, Value* args);
        ~RootScope();
    };
    struct StepRootScope {
        Interpreter& interp;
        RootContext saved;
        StepRootScope(Interpreter& owner, u32 iterator_type, u32 owner_type);
        ~StepRootScope();
    };
    Buffer<LedgerRecord> ledger_;
    Buffer<u32> method_stack_;
    u32 coroutine_frame_;
    u32 coroutine_pass_;
    u32 ledger_dropped_;
    u32 ledger_allocation_loss_;
    u32 ledger_push_failed_;
    u32 ledger_ancestry_truncated_;
    u32 last_summary_groups_;
    u32 time_scale_member_ref_;
    u32 sync_step_depth_;
    u32 step_budget_pass_;
    u32 step_budget_count_;
    u32 current_entry_;
    u32 budget_exhausted_pass_;
    bool time_scale_resolved_;
    bool coroutine_in_pass_;
    bool ledger_printed_;

    friend struct NativeContext;
};

void SetStrictFaultPrefixes(const char* prefixes);
bool LastRunStrictFaultMatched();

bool InvokeUnityNative(Interpreter& interp, const MemberRefInfo& member,
                       u32 arg_count, Value* args, Value* ret);

// Feeds the minimal Rewired action bridge used by PlayerInput.
void SetRewiredInputState(f32 axis_x, f32 axis_y, u32 buttons);

} // namespace ch360

#endif // CH360_RUNTIME_INTERPRETER_H
