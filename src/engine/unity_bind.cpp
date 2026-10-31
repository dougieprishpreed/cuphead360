#include "runtime/interpreter.h"
#include "engine/scene.h"
#include "core/log.h"
#include "core/mem.h"

#include <math.h>
#include <string.h>

namespace ch360 {

static bool RetVoid(Value* ret) { if (ret) *ret = Value::Void(); return true; }
static bool RetBoolV(Value* ret, bool v) { if (ret) *ret = Value::Bool(v); return true; }
static bool RetI32V(Value* ret, s32 v) { if (ret) *ret = Value::I4(v); return true; }
static bool RetF32V(Value* ret, f32 v) { if (ret) *ret = Value::R4(v); return true; }
static bool RetVec3V(Interpreter& in, Value* ret, f32 x, f32 y, f32 z) {
    if (ret) *ret = in.NewVec3(x, y, z);
    return true;
}
static bool RetVec2V(Interpreter& in, Value* ret, f32 x, f32 y) {
    if (ret) *ret = in.NewVec2(x, y);
    return true;
}
static bool RetF32Field(Value* ret, f32 v) { if (ret) *ret = Value::R4(v); return true; }
static bool RetBoolField(Value* ret, bool v) { if (ret) *ret = Value::Bool(v); return true; }

// Extra native-only slots on Rigidbody2D objects.
enum { RB_SLOT_VEL_X = 2, RB_SLOT_VEL_Y = 3, RB_SLOT_ANGULAR = 4, RB_SLOT_BODY = 5,
       RB_SLOT_KINEMATIC = 6, RB_SLOT_SIMULATED = 7, RB_SLOT_FREEZE = 8 };

static const SceneComponent* SceneComponentFor(Interpreter& in, const Value& v) {
    u32 ci = in.ManagedComponentIndexFor(v);
    Scene* scene = in.BoundScene();
    if (!scene || ci >= scene->ComponentCount()) return 0;
    return &scene->Component(ci);
}
static SceneComponent* MutableSceneComponentFor(Interpreter& in, const Value& v) {
    u32 ci = in.ManagedComponentIndexFor(v);
    Scene* scene = in.BoundScene();
    if (!scene || ci >= scene->ComponentCount()) return 0;
    return &scene->MutableComponent(ci);
}

static u32 ManagedObjectIndex(const Interpreter& in, const Value& v) {
    return in.ManagedObjectIndexFor(v);
}

static u32 ManagedComponentIndex(const Interpreter& in, const Value& v) {
    return in.ManagedComponentIndexFor(v);
}

static Value ManagedGameObjectForComponent(Interpreter& in, const Value& c) {
    u32 oi = ManagedObjectIndex(in, c);
    return in.ManagedGameObject(oi);
}

static Value ManagedTransformForObject(Interpreter& in, const Value& go) {
    u32 oi = ManagedObjectIndex(in, go);
    Scene* scene = in.BoundScene();
    if (scene && oi < scene->ObjectCount()) return in.ManagedTransform(oi);
    if (go.tag == VT_OBJECT && go.data.obj) return go.data.obj->Fields()[1];
    return Value::Null();
}

static bool IsSceneObjectIndex(Interpreter& in, u32 oi) {
    Scene* scene = in.BoundScene();
    return scene && oi < scene->ObjectCount();
}

static Value UnboundTransformField(const Value& t, u32 slot) {
    if (t.tag == VT_OBJECT && t.data.obj) return t.data.obj->Fields()[slot];
    return Value::Null();
}

static void SetUnboundTransformField(const Value& t, u32 slot, const Value& v) {
    if (t.tag == VT_OBJECT && t.data.obj) t.data.obj->Fields()[slot] = v;
}

static Value TransformWorldPosition(Interpreter& in, const Value& t) {
    if (t.tag != VT_OBJECT || !t.data.obj) return in.NewVec3(0.0f, 0.0f, 0.0f);
    u32 oi = in.ManagedObjectIndexFor(t);
    Scene* scene = in.BoundScene();
    if (scene && oi < scene->ObjectCount()) {
        const SceneObject& o = scene->Object(oi);
        return in.NewVec3(o.world_x, o.world_y, 0.0f);
    }
    f32 x = 0.0f, y = 0.0f, z = 0.0f;
    Value local = t.data.obj->Fields()[3];
    if (local.tag == VT_VEC3 && local.data.ptr) {
        const Vec3* p = (const Vec3*)local.data.ptr;
        x = p->x; y = p->y; z = p->z;
    }
    Value parent = t.data.obj->Fields()[2];
    if (parent.tag == VT_OBJECT && parent.data.obj) {
        Value pw = TransformWorldPosition(in, parent);
        if (pw.tag == VT_VEC3 && pw.data.ptr) {
            const Vec3* p = (const Vec3*)pw.data.ptr;
            x += p->x; y += p->y; z += p->z;
        }
    }
    return in.NewVec3(x, y, z);
}

static Value ResolveScenePointer(Interpreter& in, const ScriptValue& sv) {
    Scene* scene = in.BoundScene();
    if (!scene || sv.kind != 7) return Value::Null();
    u32 target_id = sv.data0;
    // Component references use the serialized component path id.
    if (sv.data1 == 2) {
        for (u32 ci = 0; ci < scene->ComponentCount(); ++ci) {
            if (scene->Component(ci).source_id == target_id) {
                return in.ManagedComponent(ci);
            }
        }
        // The path id can also be a GameObject; try the component type on it.
        u32 oi = 0xFFFFFFFFu;
        for (u32 i = 0; i < scene->ObjectCount(); ++i) {
            if (scene->Object(i).id == target_id) { oi = i; break; }
        }
        if (oi != 0xFFFFFFFFu) {
            const SceneObject& o = scene->Object(oi);
            for (u32 c = 0; c < o.component_count; ++c) {
                u32 ci = o.component_start + c;
                Value v = in.ManagedComponent(ci);
                if (v.tag == VT_OBJECT && v.data.obj) {
                    if (v.data.obj->type_id == sv.data2 || in.IsAssignable(v.data.obj->type_id, sv.data2)) {
                        return v;
                    }
                }
            }
        }
        return Value::Null();
    }
    u32 oi = 0xFFFFFFFFu;
    for (u32 i = 0; i < scene->ObjectCount(); ++i) {
        if (scene->Object(i).id == target_id) { oi = i; break; }
    }
    if (oi == 0xFFFFFFFFu) return Value::Null();
    if (sv.data1 == 1) return in.ManagedTransform(oi);
    return in.ManagedGameObject(oi);
}

static u32 FindObjectIndexById(const Scene& scene, u32 id) {
    for (u32 i = 0; i < scene.ObjectCount(); ++i) {
        if (scene.Object(i).id == id) return i;
    }
    return 0xFFFFFFFFu;
}

static bool IsNestedSerializableType(const TypeInfo& t) {
    if (t.external || t.is_enum || !t.full_name) return false;
    if (strncmp(t.full_name, "UnityEngine.", 12) == 0) return false;
    if (strncmp(t.full_name, "System.", 7) == 0) return false;
    return true;
}

static bool TypeContainsField(Interpreter& in, u32 type_id, u32 field_id, u32 depth) {
    if (!type_id || depth > 12) return false;
    u32 cur = type_id;
    u32 guard = 0;
    while (cur && guard++ < 64) {
        const TypeInfo& t = in.Metadata().Type(cur);
        for (u32 i = 0; i < t.field_count; ++i) {
            u32 fid = t.field_start + i;
            if (fid == field_id) return true;
            const FieldInfo& f = in.Metadata().Field(fid);
            if (f.flags & (FIELD_FLAG_STATIC | FIELD_FLAG_LITERAL)) continue;
            const SigInfo& fs = in.Metadata().Sig(f.type_sig);
            if ((fs.kind == SIG_CLASS || fs.kind == SIG_VALUETYPE) && fs.type_id) {
                const TypeInfo& ft = in.Metadata().Type(fs.type_id);
                if (IsNestedSerializableType(ft) &&
                    TypeContainsField(in, fs.type_id, field_id, depth + 1)) {
                    return true;
                }
            }
        }
        cur = t.base_id;
    }
    return false;
}

// Finds the object that owns field_id, creating intermediate nested objects
// along the serialized path.
static bool FindFieldOwner(Interpreter& in, const Value& obj, u32 field_id,
                           Value* owner, u32 depth) {
    if (depth > 12 || (obj.tag != VT_OBJECT && obj.tag != VT_STRUCT) || !obj.data.obj) {
        return false;
    }
    Object* o = obj.data.obj;
    u32 cur = o->type_id;
    u32 guard = 0;
    while (cur && guard++ < 64) {
        const TypeInfo& t = in.Metadata().Type(cur);
        for (u32 i = 0; i < t.field_count; ++i) {
            if (t.field_start + i == field_id) { *owner = obj; return true; }
        }
        cur = t.base_id;
    }
    cur = o->type_id;
    guard = 0;
    while (cur && guard++ < 64) {
        const TypeInfo& t = in.Metadata().Type(cur);
        for (u32 i = 0; i < t.field_count; ++i) {
            u32 fid = t.field_start + i;
            const FieldInfo& f = in.Metadata().Field(fid);
            if (f.flags & (FIELD_FLAG_STATIC | FIELD_FLAG_LITERAL)) continue;
            const SigInfo& fs = in.Metadata().Sig(f.type_sig);
            if ((fs.kind == SIG_CLASS || fs.kind == SIG_VALUETYPE) && fs.type_id) {
                const TypeInfo& ft = in.Metadata().Type(fs.type_id);
                if (!IsNestedSerializableType(ft)) continue;
                if (!TypeContainsField(in, fs.type_id, field_id, depth + 1)) continue;
                Value child = in.GetField(fid, obj);
                if (child.tag == VT_NULL) {
                    child = ft.is_value_type ? in.NewStructValue(fs.type_id)
                                             : in.NewObjectValue(fs.type_id);
                    in.SetField(fid, obj, child);
                }
                if (FindFieldOwner(in, child, field_id, owner, depth + 1)) return true;
            }
        }
        cur = t.base_id;
    }
    return false;
}

Value Interpreter::ManagedGameObject(u32 object_index) const {
    if (object_index >= managed_gameobjects_.Size()) return Value::Null();
    return managed_gameobjects_[object_index];
}
Value Interpreter::ManagedTransform(u32 object_index) const {
    if (object_index >= managed_transforms_.Size()) return Value::Null();
    return managed_transforms_[object_index];
}
Value Interpreter::ManagedComponent(u32 component_index) const {
    if (component_index >= managed_components_.Size()) return Value::Null();
    return managed_components_[component_index];
}

bool Interpreter::IsComponentAwake(u32 component_index) const {
    return component_index < awake_flags_.Size() && awake_flags_[component_index] != 0;
}

void Interpreter::MarkComponentAwake(u32 component_index) {
    if (component_index < awake_flags_.Size()) awake_flags_[component_index] = 1;
}

void Interpreter::AddManagedBinding(Object* obj, u32 object_index, u32 component_index) {
    if (!obj) return;
    ManagedBinding b;
    b.obj = obj;
    b.object_index = object_index;
    b.component_index = component_index;
    bindings_.PushBack(b);
}

u32 Interpreter::ManagedObjectIndexFor(const Value& v) const {
    if (v.tag != VT_OBJECT || !v.data.obj) return 0xFFFFFFFFu;
    for (u32 i = 0; i < bindings_.Size(); ++i) {
        if (bindings_[i].obj == v.data.obj) return bindings_[i].object_index;
    }
    return (u32)v.data.obj->Fields()[0].data.i32;
}

u32 Interpreter::ManagedComponentIndexFor(const Value& v) const {
    if (v.tag != VT_OBJECT || !v.data.obj) return 0xFFFFFFFFu;
    for (u32 i = 0; i < bindings_.Size(); ++i) {
        if (bindings_[i].obj == v.data.obj) return bindings_[i].component_index;
    }
    return (u32)v.data.obj->Fields()[1].data.i32;
}

bool Interpreter::BindScene(Scene& scene, bool run_script_ctors) {
    scene_ = &scene;
    managed_gameobjects_.Reset();
    managed_transforms_.Reset();
    managed_components_.Reset();
    bindings_.Reset();
    if (!managed_gameobjects_.Resize(scene.ObjectCount())) return false;
    if (!managed_transforms_.Resize(scene.ObjectCount())) return false;
    if (!managed_components_.Resize(scene.ComponentCount())) return false;
    if (!awake_flags_.Resize(scene.ComponentCount())) return false;
    if (scene.ComponentCount()) memset(awake_flags_.Data(), 0, scene.ComponentCount());

    for (u32 i = 0; i < scene.ObjectCount(); ++i) {
        Object* go = AllocObjectWithSlots(gameobject_type_id_, 2);
        Object* tr = AllocObjectWithSlots(transform_type_id_, 2);
        if (!go || !tr) return false;
        go->Fields()[0] = Value::I4((s32)i);
        tr->Fields()[0] = Value::I4((s32)i);
        go->Fields()[1] = Value::ObjectRef(tr);
        tr->Fields()[1] = Value::ObjectRef(go);
        managed_gameobjects_[i] = Value::ObjectRef(go);
        managed_transforms_[i] = Value::ObjectRef(tr);
        AddManagedBinding(go, i, 0xFFFFFFFFu);
        AddManagedBinding(tr, i, 0xFFFFFFFFu);
    }
    for (u32 oi = 0; oi < scene.ObjectCount(); ++oi) {
        const SceneObject& o = scene.Object(oi);
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene.ComponentCount()) break;
            const SceneComponent& sc = scene.Component(ci);
            if (sc.type == 4) {
                u32 script_type = metadata_.FindType(sc.controller);
                if (script_type) {
                    Object* script = AllocObjectWithSlots(script_type, runtime_types_[script_type].total_slots);
                    if (!script) return false;
                    AddManagedBinding(script, oi, ci);
                    managed_components_[ci] = Value::ObjectRef(script);
                }
                continue;
            }
            u32 type = component_type_id_;
            if (sc.type == 1) type = sprite_renderer_type_id_;
            else if (sc.type == 2) type = animator_type_id_;
            else if (sc.type == 3) type = metadata_.FindType("UnityEngine.Camera");
            else if (sc.type == 5) type = metadata_.FindType("UnityEngine.BoxCollider2D");
            else if (sc.type == 6) type = metadata_.FindType("UnityEngine.CircleCollider2D");
            else if (sc.type == 7) type = metadata_.FindType("UnityEngine.Rigidbody2D");
            if (!type) type = component_type_id_;
            Object* comp = AllocObjectWithSlots(type, sc.type == 7 ? 10 : 4);
            if (!comp) return false;
            comp->Fields()[0] = Value::I4((s32)oi);
            comp->Fields()[1] = Value::I4((s32)ci);
            if (sc.type == 7) {
                comp->Fields()[RB_SLOT_SIMULATED] = Value::I4((sc.flags & 1) ? 1 : 0);
                comp->Fields()[RB_SLOT_KINEMATIC] = Value::I4((sc.flags & 2) ? 1 : 0);
                comp->Fields()[RB_SLOT_FREEZE] = Value::I4((sc.flags & 4) ? 1 : 0);
                comp->Fields()[RB_SLOT_BODY] = Value::I4((s32)((sc.flags >> 8) & 0xFF));
            }
            comp->flags = sc.flags;
            AddManagedBinding(comp, oi, ci);
            managed_components_[ci] = Value::ObjectRef(comp);
        }
    }
    // Run each script's default constructor before applying serialized
    // fields. Unity constructs MonoBehaviours first, so inline field
    // initializers (arrays, lists, dictionaries) exist before deserialization.
    if (run_script_ctors) {
    for (u32 oi = 0; oi < scene.ObjectCount(); ++oi) {
        const SceneObject& o = scene.Object(oi);
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene.ComponentCount()) break;
            const SceneComponent& sc = scene.Component(ci);
            if (sc.type != 4) continue;
            // UI/text constructors depend on Unity's UI base classes and are
            // not needed for gameplay; skipping them avoids log floods and
            // repeated unsupported-constructor failures.
            if (strncmp(sc.controller, "TMPro.", 6) == 0 ||
                strncmp(sc.controller, "UnityEngine.UI.", 15) == 0 ||
                strncmp(sc.controller, "UnityEngine.EventSystems.", 24) == 0) {
                continue;
            }
            u32 script_type = metadata_.FindType(sc.controller);
            if (!script_type) continue;
            u32 ctor = metadata_.FindMethod(script_type, ".ctor");
            if (!ctor) continue;
            Value self = managed_components_[ci];
            if (self.tag != VT_OBJECT || !self.data.obj) continue;
            Value r;
            if (!Invoke(ctor, &self, 1, &r)) {
                LogDebug("unity", "script constructor failed: %s", sc.controller);
            }
        }
    }
    }
    // Apply script field values only after every component has a managed
    // object; serialized PPtrs can target components that appear later in
    // the scene's component table.
    for (u32 ci = 0; ci < scene.ComponentCount(); ++ci) {
        const SceneComponent& sc = scene.Component(ci);
        if (sc.type != 4) continue;
        Value script_value = managed_components_[ci];
        if (script_value.tag != VT_OBJECT || !script_value.data.obj) continue;
        Object* script = script_value.data.obj;
        u32 vstart = sc.sprite_id;
        u32 vcount = (u32)sc.sorting_layer;
        for (u32 vi = 0; vi < vcount && vstart + vi < scene.ScriptValueCount(); ++vi) {
            const ScriptValue& sv = scene.ScriptValueAt(vstart + vi);
            Value val = Value::Null();
            if (sv.kind == 1) val = Value::I4((s32)sv.data0);
            else if (sv.kind == 2) {
                f32 f; memcpy(&f, &sv.data0, 4); val = Value::R4(f);
            } else if (sv.kind == 3) val = Value::Bool(sv.data0 != 0);
            else if (sv.kind == 4) {
                f32 x, y; memcpy(&x, &sv.data0, 4); memcpy(&y, &sv.data1, 4);
                val = NewVec2(x, y);
            } else if (sv.kind == 5) {
                f32 x, y, z; memcpy(&x, &sv.data0, 4); memcpy(&y, &sv.data1, 4); memcpy(&z, &sv.data2, 4);
                val = NewVec3(x, y, z);
            } else if (sv.kind == 6) {
                const u8* str = scene.ScriptStrings();
                if (str && sv.data0 + sv.data1 <= scene.ScriptStringsSize()) {
                    char* tmp = (char*)MemAlloc(sv.data1 + 1);
                    if (!tmp) return false;
                    memcpy(tmp, str + sv.data0, sv.data1);
                    tmp[sv.data1] = 0;
                    val = NewStringUtf8(tmp);
                    MemFree(tmp);
                }
            } else if (sv.kind == 7) {
                val = ResolveScenePointer(*this, sv);
            } else if (sv.kind == 8) {
                val = Value::Null();
            } else if (sv.kind == 9) {
                const FieldInfo& f = metadata_.Field(sv.field_id);
                const SigInfo& fs = metadata_.Sig(f.type_sig);
                u32 elem = 0;
                if (fs.kind == SIG_SZARRAY || fs.kind == SIG_ARRAY) {
                    elem = metadata_.Type(fs.type_id).element_id;
                } else if (fs.kind == SIG_GENERICINST) {
                    const TypeInfo& gt = metadata_.Type(fs.type_id);
                    if (gt.generic_arg_count) elem = metadata_.GenericArg(gt.generic_arg_start);
                } else {
                    elem = sv.data2;
                }
                val = NewArrayValue(elem, sv.data0);
            } else {
                continue;
            }

            if (sv.parent_field == 0) {
                SetField(sv.field_id, Value::ObjectRef(script), val);
            } else if (sv.parent_field == sv.field_id) {
                Value arr = GetField(sv.field_id, Value::ObjectRef(script));
                if (arr.tag == VT_ARRAY && arr.data.array && sv.index < arr.data.array->length) {
                    arr.data.array->Elements()[sv.index] = CopyValue(val);
                }
            } else {
                Value script_obj = Value::ObjectRef(script);
                Value owner = script_obj;
                Value parent = Value::Null();
                if (FindFieldOwner(*this, script_obj, sv.parent_field, &owner, 0)) {
                    parent = GetField(sv.parent_field, owner);
                } else {
                    parent = GetField(sv.parent_field, script_obj);
                }
                if (parent.tag == VT_NULL || parent.tag == VT_VOID) {
                    const FieldInfo& pf = metadata_.Field(sv.parent_field);
                    const SigInfo& ps = metadata_.Sig(pf.type_sig);
                    if (ps.type_id) {
                        const TypeInfo& pt = metadata_.Type(ps.type_id);
                        parent = pt.is_value_type ? NewStructValue(ps.type_id) : NewObjectValue(ps.type_id);
                        SetField(sv.parent_field, owner, parent);
                    }
                }
                if (parent.tag == VT_OBJECT || parent.tag == VT_STRUCT) {
                    SetField(sv.field_id, parent, val);
                }
            }
        }
    }

    LogInfo("unity", "bound %u gameobjects, %u components", scene.ObjectCount(), scene.ComponentCount());
    return true;
}

static bool Object_GetName(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (scene && oi < scene->ObjectCount()) {
        if (ret) *ret = in.NewStringUtf8(scene->Object(oi).name);
        return true;
    }
    Object* o = (a[0].tag == VT_OBJECT) ? a[0].data.obj : 0;
    if (o) {
        Value n = o->Fields()[2];
        if (n.tag == VT_STRING) { if (ret) *ret = n; return true; }
        Value go = o->Fields()[1];
        if (go.tag == VT_OBJECT && go.data.obj) {
            n = go.data.obj->Fields()[2];
            if (n.tag == VT_STRING) { if (ret) *ret = n; return true; }
        }
    }
    if (ret) *ret = in.NewStringAscii("");
    return true;
}

static bool GameObject_Ctor(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    Object* go = in.AllocObjectWithSlots(in.GameObjectTypeId(), 10);
    Object* tr = in.AllocObjectWithSlots(in.TransformTypeId(), 10);
    if (!go || !tr) return false;
    go->Fields()[0] = Value::I4(-1);
    go->Fields()[1] = Value::ObjectRef(tr);
    go->Fields()[2] = (argc >= 2 && a[1].tag == VT_STRING) ? a[1] : Value::Null();
    tr->Fields()[0] = Value::I4(-1);
    tr->Fields()[1] = Value::ObjectRef(go);
    tr->Fields()[2] = Value::Null();
    tr->Fields()[3] = in.NewVec3(0.0f, 0.0f, 0.0f);
    tr->Fields()[4] = in.NewVec3(1.0f, 1.0f, 1.0f);
    tr->Fields()[5] = in.NewVec3(0.0f, 0.0f, 0.0f);
    if (ret) *ret = Value::ObjectRef(go);
    return true;
}

static bool GameObject_GetTransform(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (ret) *ret = ManagedTransformForObject(in, a[0]);
    return true;
}

static bool Component_GetGameObject(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (ret) *ret = ManagedGameObjectForComponent(in, a[0]);
    return true;
}

static bool Component_GetTransform(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (ret) *ret = ManagedTransformForObject(in, ManagedGameObjectForComponent(in, a[0]));
    return true;
}

static bool GameObject_GetComponent(Interpreter& in, const MemberRefInfo& member, u32 argc, Value* a, Value* ret) {
    (void)argc;
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) {
        if (ret) *ret = Value::Null();
        return true;
    }
    const MethodSigInfo& ms = in.Metadata().MethodSig(member.sig_index);
    const SigInfo& rs = in.Metadata().Sig(ms.return_sig);
    u32 target = rs.type_id;
    if (argc >= 2 && a[1].tag == VT_I4 && a[1].data.i32 > 0) {
        target = (u32)a[1].data.i32;
    }
    if (rs.kind == SIG_MVAR || rs.kind == SIG_VAR || target == 0) {
        target = in.GenericArgType(0);
    }
    if (target == in.TransformTypeId()) {
        if (ret) *ret = in.ManagedTransform(oi);
        return true;
    }
    const SceneObject& o = scene->Object(oi);
    for (u32 c = 0; c < o.component_count; ++c) {
        u32 ci = o.component_start + c;
        if (ci >= scene->ComponentCount()) break;
        Value v = in.ManagedComponent(ci);
        if (v.tag != VT_OBJECT || !v.data.obj) continue;
        u32 vt = v.data.obj->type_id;
        bool is_component_base = (target == in.ComponentTypeId() ||
                                  target == in.MonoBehaviourTypeId());
        if (in.IsAssignable(vt, target) || vt == target || is_component_base) {
            if (ret) *ret = v;
            return true;
        }
    }
    if (target) {
        const TypeInfo& tt = in.Metadata().Type(target);
        // Unity UI components are not part of the compact scene component set.
        // Expose bound managed stand-ins so UI initialization can run.
        if (tt.full_name && strcmp(tt.full_name, "UnityEngine.RectTransform") == 0) {
            if (ret) *ret = in.ManagedTransform(oi);
            return true;
        }
        if (tt.full_name && (strcmp(tt.full_name, "UnityEngine.CanvasGroup") == 0 ||
                             strncmp(tt.full_name, "UnityEngine.UI.", 15) == 0)) {
            Object* ui = in.AllocObjectWithSlots(target, 4);
            if (ui) {
                ui->Fields()[0] = Value::I4((s32)oi);
                ui->Fields()[1] = Value::I4(-1);
                if (ret) *ret = Value::ObjectRef(ui);
                return true;
            }
        }
        // AudioSource is not part of the compact scene component set, but
        // gameplay/Awake code expects one on several prefabs. Expose a
        // managed stand-in bound to the same GameObject.
        if (tt.full_name && strcmp(tt.full_name, "UnityEngine.AudioSource") == 0) {
            Object* src = in.AllocObjectWithSlots(target, 4);
            if (src) {
                src->Fields()[0] = Value::I4((s32)oi);
                src->Fields()[1] = Value::I4(-1);
                if (ret) *ret = Value::ObjectRef(src);
                return true;
            }
        }
    }
    if (ret) *ret = Value::Null();
    return true;
}

static bool IsSceneDescendant(const Scene& scene, u32 object_index, u32 root_id) {
    u32 cur = scene.Object(object_index).id;
    u32 guard = 0;
    while (cur && guard++ < 256) {
        if (cur == root_id) return true;
        u32 idx = FindObjectIndexById(scene, cur);
        if (idx == 0xFFFFFFFFu) return false;
        cur = scene.Object(idx).parent;
    }
    return false;
}

static bool Component_GetComponentsInChildren(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    u32 target = in.GenericArgType(0);
    bool include_inactive = (argc > 1) ? Interpreter::ToBool(a[1]) : false;
    u32 root_oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || root_oi >= scene->ObjectCount() || !target) {
        if (ret) *ret = in.NewArrayValue(target, 0);
        return true;
    }
    u32 root_id = scene->Object(root_oi).id;
    u32 count = 0;
    for (u32 oi = 0; oi < scene->ObjectCount(); ++oi) {
        if (!IsSceneDescendant(*scene, oi, root_id)) continue;
        if (!include_inactive && !(scene->Object(oi).flags & 1u)) continue;
        const SceneObject& o = scene->Object(oi);
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene->ComponentCount()) break;
            Value v = in.ManagedComponent(ci);
            if (v.tag == VT_OBJECT && v.data.obj) {
                u32 vt = v.data.obj->type_id;
                if (in.IsAssignable(vt, target) || vt == target) ++count;
            }
        }
    }
    Value arr = in.NewArrayValue(target, count);
    if (arr.tag == VT_ARRAY && arr.data.array) {
        u32 j = 0;
        for (u32 oi = 0; oi < scene->ObjectCount(); ++oi) {
            if (!IsSceneDescendant(*scene, oi, root_id)) continue;
            if (!include_inactive && !(scene->Object(oi).flags & 1u)) continue;
            const SceneObject& o = scene->Object(oi);
            for (u32 c = 0; c < o.component_count; ++c) {
                u32 ci = o.component_start + c;
                if (ci >= scene->ComponentCount()) break;
                Value v = in.ManagedComponent(ci);
                if (v.tag == VT_OBJECT && v.data.obj) {
                    u32 vt = v.data.obj->type_id;
                    if (in.IsAssignable(vt, target) || vt == target) {
                        if (j < count) arr.data.array->Elements()[j++] = in.CopyValue(v);
                    }
                }
            }
        }
    }
    if (ret) *ret = arr;
    return true;
}

static bool Component_GetComponentInChildren(Interpreter& in, const MemberRefInfo& member, u32 argc, Value* a, Value* ret) {
    Value arr;
    if (!Component_GetComponentsInChildren(in, member, argc, a, &arr)) return false;
    if (arr.tag == VT_ARRAY && arr.data.array && arr.data.array->length > 0) {
        if (ret) *ret = arr.data.array->Elements()[0];
    } else if (ret) {
        *ret = Value::Null();
    }
    return true;
}

static bool Object_FindObjectsOfType(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    u32 target = in.GenericArgType(0);
    Scene* scene = in.BoundScene();
    u32 count = 0;
    if (scene && target) {
        for (u32 ci = 0; ci < scene->ComponentCount(); ++ci) {
            Value v = in.ManagedComponent(ci);
            if (v.tag == VT_OBJECT && v.data.obj &&
                in.IsAssignable(v.data.obj->type_id, target)) {
                ++count;
            }
        }
    }
    Value arr = in.NewArrayValue(target, count);
    if (arr.tag == VT_ARRAY && arr.data.array) {
        u32 j = 0;
        for (u32 ci = 0; ci < scene->ComponentCount(); ++ci) {
            Value v = in.ManagedComponent(ci);
            if (v.tag == VT_OBJECT && v.data.obj &&
                in.IsAssignable(v.data.obj->type_id, target)) {
                if (j < count) arr.data.array->Elements()[j++] = in.CopyValue(v);
            }
        }
    }
    if (ret) *ret = arr;
    return true;
}

static bool GameObject_SetActive(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    bool enabled = Interpreter::ToBool(a[1]);
    if (a[0].tag == VT_OBJECT && a[0].data.obj) {
        a[0].data.obj->flags = (a[0].data.obj->flags & ~1u) | (enabled ? 1u : 0u);
    }
    if (scene && oi < scene->ObjectCount()) {
        SceneObject& o = scene->MutableObject(oi);
        o.flags = (o.flags & ~1u) | (enabled ? 1u : 0u);
    }
    return RetVoid(ret);
}
static bool GameObject_GetActiveSelf(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (scene && oi < scene->ObjectCount()) {
        return RetBoolV(ret, (scene->Object(oi).flags & 1u) != 0);
    }
    return RetBoolV(ret, (a[0].data.obj->flags & 1u) != 0);
}

static bool Behaviour_SetEnabled(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 ci = ManagedComponentIndex(in, a[0]);
    if (a[0].data.obj) {
        bool enabled = Interpreter::ToBool(a[1]);
        a[0].data.obj->flags = (a[0].data.obj->flags & ~1u) | (enabled ? 1u : 0u);
        Scene* scene = in.BoundScene();
        if (scene && ci < scene->ComponentCount()) {
            scene->MutableComponent(ci).flags =
                (scene->Component(ci).flags & ~1u) | (enabled ? 1u : 0u);
        }
    }
    return RetVoid(ret);
}
static bool Behaviour_GetEnabled(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 ci = ManagedComponentIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (scene && ci < scene->ComponentCount()) {
        return RetBoolV(ret, (scene->Component(ci).flags & 1u) != 0);
    }
    return RetBoolV(ret, (a[0].data.obj->flags & 1u) != 0);
}

static bool Transform_GetPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        const SceneObject& o = in.BoundScene()->Object(oi);
        return RetVec3V(in, ret, o.world_x, o.world_y, 0.0f);
    }
    if (ret) *ret = TransformWorldPosition(in, a[0]);
    return true;
}
static bool Transform_SetPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[1].tag != VT_VEC3 || !a[1].data.ptr) return false;
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        const Vec3* p = (const Vec3*)a[1].data.ptr;
        SceneObject& o = const_cast<SceneObject&>(in.BoundScene()->Object(oi));
        o.position[0] = p->x; o.position[1] = p->y; o.position[2] = p->z;
        in.BoundScene()->ComputeWorldTransforms();
        return RetVoid(ret);
    }
    SetUnboundTransformField(a[0], 3, a[1]);
    return RetVoid(ret);
}
static bool Transform_GetLocalPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        const SceneObject& o = in.BoundScene()->Object(oi);
        return RetVec3V(in, ret, o.position[0], o.position[1], o.position[2]);
    }
    Value v = UnboundTransformField(a[0], 3);
    if (v.tag == VT_VEC3) { if (ret) *ret = v; return true; }
    return RetVec3V(in, ret, 0.0f, 0.0f, 0.0f);
}
static bool Transform_SetLocalPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[1].tag != VT_VEC3 || !a[1].data.ptr) return false;
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        const Vec3* p = (const Vec3*)a[1].data.ptr;
        SceneObject& o = const_cast<SceneObject&>(in.BoundScene()->Object(oi));
        o.position[0] = p->x; o.position[1] = p->y; o.position[2] = p->z;
        in.BoundScene()->ComputeWorldTransforms();
        return RetVoid(ret);
    }
    SetUnboundTransformField(a[0], 3, a[1]);
    return RetVoid(ret);
}
static bool Transform_GetLocalScale(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        const SceneObject& o = in.BoundScene()->Object(oi);
        return RetVec3V(in, ret, o.scale[0], o.scale[1], o.scale[2]);
    }
    Value v = UnboundTransformField(a[0], 4);
    if (v.tag == VT_VEC3) { if (ret) *ret = v; return true; }
    return RetVec3V(in, ret, 1.0f, 1.0f, 1.0f);
}
static bool Transform_SetLocalScale(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[1].tag != VT_VEC3 || !a[1].data.ptr) return false;
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        const Vec3* p = (const Vec3*)a[1].data.ptr;
        SceneObject& o = const_cast<SceneObject&>(in.BoundScene()->Object(oi));
        o.scale[0] = p->x; o.scale[1] = p->y; o.scale[2] = p->z;
        in.BoundScene()->ComputeWorldTransforms();
        return RetVoid(ret);
    }
    SetUnboundTransformField(a[0], 4, a[1]);
    return RetVoid(ret);
}
static bool Transform_GetParent(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        u32 parent = in.BoundScene()->Object(oi).parent;
        u32 pi = FindObjectIndexById(*in.BoundScene(), parent);
        if (ret) *ret = pi == 0xFFFFFFFFu ? Value::Null() : in.ManagedTransform(pi);
        return true;
    }
    if (ret) *ret = UnboundTransformField(a[0], 2);
    return true;
}
static bool Transform_SetParent(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) {
        u32 pi = 0xFFFFFFFFu;
        if (a[1].tag == VT_OBJECT && a[1].data.obj) pi = ManagedObjectIndex(in, a[1]);
        SceneObject& o = const_cast<SceneObject&>(in.BoundScene()->Object(oi));
        o.parent = (pi == 0xFFFFFFFFu) ? 0 : in.BoundScene()->Object(pi).id;
        in.BoundScene()->ComputeWorldTransforms();
        return RetVoid(ret);
    }
    SetUnboundTransformField(a[0], 2, a[1]);
    return RetVoid(ret);
}
static bool Transform_GetEuler(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (IsSceneObjectIndex(in, oi)) return RetVec3V(in, ret, 0.0f, 0.0f, 0.0f);
    Value v = UnboundTransformField(a[0], 5);
    if (v.tag == VT_VEC3) { if (ret) *ret = v; return true; }
    return RetVec3V(in, ret, 0.0f, 0.0f, 0.0f);
}
static bool Transform_SetEuler(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    if (!IsSceneObjectIndex(in, oi) && a[1].tag == VT_VEC3) {
        SetUnboundTransformField(a[0], 5, a[1]);
    }
    return RetVoid(ret);
}

static bool Collider2D_GetOffset(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    if (!c) return RetVec2V(in, ret, 0.0f, 0.0f);
    return RetVec2V(in, ret, c->color[2], c->color[3]);
}
static bool Collider2D_SetOffset(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c && a[1].tag == VT_VEC2 && a[1].data.ptr) {
        const Vec2* v = (const Vec2*)a[1].data.ptr;
        c->color[2] = v->x; c->color[3] = v->y;
    }
    return RetVoid(ret);
}
static bool BoxCollider2D_GetSize(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    if (!c) return RetVec2V(in, ret, 0.0f, 0.0f);
    return RetVec2V(in, ret, c->color[0], c->color[1]);
}
static bool BoxCollider2D_SetSize(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c && a[1].tag == VT_VEC2 && a[1].data.ptr) {
        const Vec2* v = (const Vec2*)a[1].data.ptr;
        c->color[0] = v->x; c->color[1] = v->y;
    }
    return RetVoid(ret);
}
static bool CircleCollider2D_GetRadius(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    return RetF32Field(ret, c ? c->color[0] : 0.0f);
}
static bool CircleCollider2D_SetRadius(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c) c->color[0] = a[1].data.f32;
    return RetVoid(ret);
}

// Extra native-only slots on Rigidbody2D objects.
static Object* RigidbodyObject(const Value& v) {
    return (v.tag == VT_OBJECT) ? v.data.obj : 0;
}
static bool Rigidbody2D_GetVelocity(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RigidbodyObject(a[0]);
    if (!o) return RetVec2V(in, ret, 0.0f, 0.0f);
    f32 x = o->Fields()[RB_SLOT_VEL_X].data.f32;
    f32 y = o->Fields()[RB_SLOT_VEL_Y].data.f32;
    return RetVec2V(in, ret, x, y);
}
static bool Rigidbody2D_SetVelocity(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RigidbodyObject(a[0]);
    if (o && a[1].tag == VT_VEC2 && a[1].data.ptr) {
        const Vec2* v = (const Vec2*)a[1].data.ptr;
        o->Fields()[RB_SLOT_VEL_X] = Value::R4(v->x);
        o->Fields()[RB_SLOT_VEL_Y] = Value::R4(v->y);
    }
    return RetVoid(ret);
}
static bool Rigidbody2D_GetPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = in.ManagedObjectIndexFor(a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return RetVec2V(in, ret, 0.0f, 0.0f);
    const SceneObject& o = scene->Object(oi);
    return RetVec2V(in, ret, o.world_x, o.world_y);
}
static bool Rigidbody2D_SetPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = in.ManagedObjectIndexFor(a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return RetVoid(ret);
    if (a[1].tag != VT_VEC2 || !a[1].data.ptr) return RetVoid(ret);
    const Vec2* p = (const Vec2*)a[1].data.ptr;
    SceneObject& o = const_cast<SceneObject&>(scene->Object(oi));
    o.position[0] = p->x; o.position[1] = p->y;
    scene->ComputeWorldTransforms();
    return RetVoid(ret);
}
static bool Rigidbody2D_GetMass(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    return RetF32Field(ret, c ? c->color[0] : 1.0f);
}
static bool Rigidbody2D_SetMass(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c) c->color[0] = a[1].data.f32;
    return RetVoid(ret);
}
static bool Rigidbody2D_GetDrag(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    return RetF32Field(ret, c ? c->color[1] : 0.0f);
}
static bool Rigidbody2D_SetDrag(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c) c->color[1] = a[1].data.f32;
    return RetVoid(ret);
}
static bool Rigidbody2D_GetAngularDrag(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    return RetF32Field(ret, c ? c->color[2] : 0.05f);
}
static bool Rigidbody2D_SetAngularDrag(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c) c->color[2] = a[1].data.f32;
    return RetVoid(ret);
}
static bool Rigidbody2D_GetGravityScale(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    return RetF32Field(ret, c ? c->color[3] : 1.0f);
}
static bool Rigidbody2D_SetGravityScale(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c) c->color[3] = a[1].data.f32;
    return RetVoid(ret);
}
static bool Rigidbody2D_GetAngularVelocity(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RigidbodyObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[RB_SLOT_ANGULAR].data.f32 : 0.0f);
}
static bool Rigidbody2D_SetAngularVelocity(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RigidbodyObject(a[0]);
    if (o) o->Fields()[RB_SLOT_ANGULAR] = Value::R4(a[1].data.f32);
    return RetVoid(ret);
}
static bool Rigidbody2D_GetBodyType(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    return RetI32V(ret, o ? o->Fields()[RB_SLOT_BODY].data.i32 : 0);
}
static bool Rigidbody2D_SetBodyType(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    if (o) o->Fields()[RB_SLOT_BODY] = Value::I4(a[1].data.i32);
    return RetVoid(ret);
}
static bool Rigidbody2D_GetKinematic(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    return RetBoolV(ret, o ? o->Fields()[RB_SLOT_KINEMATIC].data.i32 != 0 : false);
}
static bool Rigidbody2D_SetKinematic(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    if (o) o->Fields()[RB_SLOT_KINEMATIC] = Value::I4(Interpreter::ToBool(a[1]) ? 1 : 0);
    return RetVoid(ret);
}
static bool Rigidbody2D_GetSimulated(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    return RetBoolV(ret, o ? o->Fields()[RB_SLOT_SIMULATED].data.i32 != 0 : true);
}
static bool Rigidbody2D_SetSimulated(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    if (o) o->Fields()[RB_SLOT_SIMULATED] = Value::I4(Interpreter::ToBool(a[1]) ? 1 : 0);
    return RetVoid(ret);
}
static bool Rigidbody2D_GetFreezeRotation(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    return RetBoolV(ret, o ? o->Fields()[RB_SLOT_FREEZE].data.i32 != 0 : false);
}
static bool Rigidbody2D_SetFreezeRotation(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    (void)in;
    Object* o = RigidbodyObject(a[0]);
    if (o) o->Fields()[RB_SLOT_FREEZE] = Value::I4(Interpreter::ToBool(a[1]) ? 1 : 0);
    return RetVoid(ret);
}
static bool Collider2D_GetAttachedRigidbody(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = in.ManagedObjectIndexFor(a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) { if (ret) *ret = Value::Null(); return true; }
    const SceneObject& o = scene->Object(oi);
    for (u32 c = 0; c < o.component_count; ++c) {
        u32 ci = o.component_start + c;
        if (ci >= scene->ComponentCount()) break;
        if (scene->Component(ci).type == 7) {
            if (ret) *ret = in.ManagedComponent(ci);
            return true;
        }
    }
    if (ret) *ret = Value::Null();
    return true;
}

static bool MonoBehaviour_NoOp(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetVoid(ret);
}

struct PhysicsAabb {
    f32 min_x, min_y, max_x, max_y;
};

static bool ColliderAabb(const Scene& scene, u32 oi, const SceneComponent& c, PhysicsAabb* out) {
    const SceneObject& o = scene.Object(oi);
    f32 sx = o.world_scale_x, sy = o.world_scale_y;
    f32 hw = 0.0f, hh = 0.0f, off_x = 0.0f, off_y = 0.0f;
    if (c.type == 5) {
        hw = c.color[0] * 0.5f * fabsf(sx);
        hh = c.color[1] * 0.5f * fabsf(sy);
        off_x = c.color[2] * sx;
        off_y = c.color[3] * sy;
    } else if (c.type == 6) {
        f32 s = fabsf(sx) > fabsf(sy) ? fabsf(sx) : fabsf(sy);
        hw = hh = c.color[0] * 0.5f * s;
        off_x = c.color[2] * sx;
        off_y = c.color[3] * sy;
    } else {
        return false;
    }
    f32 ca = cosf(o.world_rotation), sa = sinf(o.world_rotation);
    f32 cx = o.world_x + off_x * ca - off_y * sa;
    f32 cy = o.world_y + off_x * sa + off_y * ca;
    out->min_x = cx - hw; out->max_x = cx + hw;
    out->min_y = cy - hh; out->max_y = cy + hh;
    return true;
}

static bool SweepBoxVsAabb(f32 ox, f32 oy, f32 hw, f32 hh,
                           f32 dx, f32 dy, f32 dist, const PhysicsAabb& b,
                           f32* out_t, f32* out_nx, f32* out_ny) {
    f32 minx = b.min_x - hw, maxx = b.max_x + hw;
    f32 miny = b.min_y - hh, maxy = b.max_y + hh;
    f32 tmin = 0.0f, tmax = dist;
    f32 nx = 0.0f, ny = 0.0f;
    if (fabsf(dx) < 0.000001f) {
        if (ox < minx || ox > maxx) return false;
    } else {
        f32 t1 = (minx - ox) / dx, t2 = (maxx - ox) / dx;
        f32 n1 = -1.0f, n2 = 1.0f;
        if (t1 > t2) { f32 tt = t1; t1 = t2; t2 = tt; f32 tn = n1; n1 = n2; n2 = tn; }
        if (t1 > tmin) { tmin = t1; nx = n1; ny = 0.0f; }
        if (t2 < tmax) tmax = t2;
        if (tmin > tmax) return false;
    }
    if (fabsf(dy) < 0.000001f) {
        if (oy < miny || oy > maxy) return false;
    } else {
        f32 t1 = (miny - oy) / dy, t2 = (maxy - oy) / dy;
        f32 n1 = -1.0f, n2 = 1.0f;
        if (t1 > t2) { f32 tt = t1; t1 = t2; t2 = tt; f32 tn = n1; n1 = n2; n2 = tn; }
        if (t1 > tmin) { tmin = t1; nx = 0.0f; ny = n1; }
        if (t2 < tmax) tmax = t2;
        if (tmin > tmax) return false;
    }
    if (tmin < 0.0f || tmin > dist) return false;
    *out_t = tmin; *out_nx = nx; *out_ny = ny;
    return true;
}

static bool Physics2D_Cast(Interpreter& in, u32 argc, Value* a, bool box, Value* ret) {
    u32 hit_type = in.Metadata().FindType("UnityEngine.RaycastHit2D");
    Object* hit = in.AllocObjectWithSlots(hit_type ? hit_type : in.ComponentTypeId(), 8);
    if (!hit) return false;
    hit->Fields()[0] = Value::Null();
    hit->Fields()[1] = in.NewVec2(0.0f, 0.0f);
    hit->Fields()[2] = in.NewVec2(0.0f, 0.0f);
    hit->Fields()[3] = Value::R4(0.0f);
    hit->Fields()[4] = Value::R4(0.0f);
    hit->Fields()[5] = Value::Null();
    hit->Fields()[6] = Value::Null();

    Scene* scene = in.BoundScene();
    f32 ox = 0, oy = 0, hw = 0, hh = 0, dist = 0, dx = 0, dy = 0;
    if (box) {
        if (argc < 6 || a[0].tag != VT_VEC2 || a[1].tag != VT_VEC2 || a[3].tag != VT_VEC2) {
            if (ret) *ret = Value::StructRef(hit);
            return true;
        }
        const Vec2* o = (const Vec2*)a[0].data.ptr;
        const Vec2* s = (const Vec2*)a[1].data.ptr;
        const Vec2* d = (const Vec2*)a[3].data.ptr;
        ox = o->x; oy = o->y; hw = s->x * 0.5f; hh = s->y * 0.5f;
        dx = d->x; dy = d->y; dist = a[4].data.f32;
    } else {
        if (argc < 4 || a[0].tag != VT_VEC2 || a[1].tag != VT_VEC2) {
            if (ret) *ret = Value::StructRef(hit);
            return true;
        }
        const Vec2* o = (const Vec2*)a[0].data.ptr;
        const Vec2* d = (const Vec2*)a[1].data.ptr;
        ox = o->x; oy = o->y; dx = d->x; dy = d->y; dist = a[2].data.f32;
    }
    f32 len = sqrtf(dx * dx + dy * dy);
    if (len > 0.000001f) { dx /= len; dy /= len; }
    if (dist <= 0.0f || (fabsf(dx) < 0.000001f && fabsf(dy) < 0.000001f)) {
        if (ret) *ret = Value::StructRef(hit);
        return true;
    }

    f32 best_t = dist, best_nx = 0, best_ny = 0;
    u32 best_ci = 0xFFFFFFFFu, best_oi = 0xFFFFFFFFu;
    if (scene) {
        for (u32 oi = 0; oi < scene->ObjectCount(); ++oi) {
            const SceneObject& o = scene->Object(oi);
            if (o.id >= 1000000u) continue; // player prefab self-colliders
            for (u32 c = 0; c < o.component_count; ++c) {
                u32 ci = o.component_start + c;
                if (ci >= scene->ComponentCount()) break;
                const SceneComponent& sc = scene->Component(ci);
                if (sc.type != 5 && sc.type != 6) continue;
                PhysicsAabb b;
                if (!ColliderAabb(*scene, oi, sc, &b)) continue;
                f32 t = 0, nx = 0, ny = 0;
                if (SweepBoxVsAabb(ox, oy, hw, hh, dx, dy, dist, b, &t, &nx, &ny) && t <= best_t) {
                    best_t = t; best_nx = nx; best_ny = ny; best_ci = ci; best_oi = oi;
                }
            }
        }
    }
    if (best_ci != 0xFFFFFFFFu) {
        hit->Fields()[0] = in.ManagedComponent(best_ci);
        hit->Fields()[1] = in.NewVec2(ox + dx * best_t, oy + dy * best_t);
        hit->Fields()[2] = in.NewVec2(best_nx, best_ny);
        hit->Fields()[3] = Value::R4(best_t);
        hit->Fields()[4] = Value::R4(best_t / dist);
        hit->Fields()[5] = in.ManagedTransform(best_oi);
        const SceneObject& o = scene->Object(best_oi);
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene->ComponentCount()) break;
            if (scene->Component(ci).type == 7) {
                hit->Fields()[6] = in.ManagedComponent(ci);
                break;
            }
        }
    }
    if (ret) *ret = Value::StructRef(hit);
    return true;
}

static bool Physics2D_BoxCast(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    return Physics2D_Cast(in, argc, a, true, ret);
}
static bool Physics2D_Raycast(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    return Physics2D_Cast(in, argc, a, false, ret);
}

struct PhysicsHit {
    f32 t;
    u32 ci;
    u32 oi;
    f32 nx;
    f32 ny;
};

static u32 Physics2D_CollectHits(Interpreter& in, f32 ox, f32 oy, f32 hw, f32 hh,
                                 f32 dx, f32 dy, f32 dist, PhysicsHit* out, u32 max) {
    Scene* scene = in.BoundScene();
    if (!scene || max == 0) return 0;
    f32 len = sqrtf(dx * dx + dy * dy);
    if (len > 0.000001f) { dx /= len; dy /= len; }
    u32 count = 0;
    for (u32 oi = 0; oi < scene->ObjectCount(); ++oi) {
        const SceneObject& o = scene->Object(oi);
        if (o.id >= 1000000u) continue;
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene->ComponentCount()) break;
            const SceneComponent& sc = scene->Component(ci);
            if (sc.type != 5 && sc.type != 6) continue;
            PhysicsAabb b;
            if (!ColliderAabb(*scene, oi, sc, &b)) continue;
            f32 t = 0, nx = 0, ny = 0;
            if (!SweepBoxVsAabb(ox, oy, hw, hh, dx, dy, dist, b, &t, &nx, &ny)) continue;
            if (count < max) {
                PhysicsHit& h = out[count++];
                h.t = t; h.ci = ci; h.oi = oi; h.nx = nx; h.ny = ny;
            }
        }
    }
    for (u32 i = 1; i < count; ++i) {
        PhysicsHit key = out[i];
        s32 j = (s32)i - 1;
        while (j >= 0 && out[j].t > key.t) { out[j + 1] = out[j]; --j; }
        out[j + 1] = key;
    }
    return count;
}

static void Physics2D_FillHit(Interpreter& in, Object* hit, const PhysicsHit& h,
                              f32 ox, f32 oy, f32 dx, f32 dy, f32 dist) {
    Scene* scene = in.BoundScene();
    hit->Fields()[0] = in.ManagedComponent(h.ci);
    hit->Fields()[1] = in.NewVec2(ox + dx * h.t, oy + dy * h.t);
    hit->Fields()[2] = in.NewVec2(h.nx, h.ny);
    hit->Fields()[3] = Value::R4(h.t);
    hit->Fields()[4] = Value::R4(dist > 0.000001f ? h.t / dist : 0.0f);
    hit->Fields()[5] = in.ManagedTransform(h.oi);
    hit->Fields()[6] = Value::Null();
    if (scene) {
        const SceneObject& o = scene->Object(h.oi);
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene->ComponentCount()) break;
            if (scene->Component(ci).type == 7) {
                hit->Fields()[6] = in.ManagedComponent(ci);
                break;
            }
        }
    }
}

static bool Physics2D_NonAlloc(Interpreter& in, u32 argc, Value* a, bool box, Value* ret) {
    u32 hit_type = in.Metadata().FindType("UnityEngine.RaycastHit2D");
    f32 ox = 0, oy = 0, hw = 0, hh = 0, dx = 0, dy = 0, dist = 0;
    Value results = Value::Null();
    if (box) {
        if (argc < 7 || a[0].tag != VT_VEC2 || a[1].tag != VT_VEC2 || a[3].tag != VT_VEC2) {
            return RetI32V(ret, 0);
        }
        const Vec2* o = (const Vec2*)a[0].data.ptr;
        const Vec2* s = (const Vec2*)a[1].data.ptr;
        const Vec2* d = (const Vec2*)a[3].data.ptr;
        ox = o->x; oy = o->y; hw = s->x * 0.5f; hh = s->y * 0.5f;
        dx = d->x; dy = d->y;
        results = a[4];       // BoxCastNonAlloc(..., results, distance, mask)
        dist = a[5].data.f32;
    } else {
        if (argc < 5 || a[0].tag != VT_VEC2 || a[1].tag != VT_VEC2) {
            return RetI32V(ret, 0);
        }
        const Vec2* o = (const Vec2*)a[0].data.ptr;
        const Vec2* d = (const Vec2*)a[1].data.ptr;
        ox = o->x; oy = o->y; dx = d->x; dy = d->y;
        results = a[2];       // RaycastNonAlloc(origin, direction, results, distance, mask)
        dist = a[3].data.f32;
    }
    if (results.tag != VT_ARRAY || !results.data.array || !hit_type) {
        return RetI32V(ret, 0);
    }
    ArrayObject* arr = results.data.array;
    PhysicsHit hits[64];
    u32 max = arr->length < 64 ? arr->length : 64;
    f32 len = sqrtf(dx * dx + dy * dy);
    if (len > 0.000001f) { dx /= len; dy /= len; }
    u32 count = Physics2D_CollectHits(in, ox, oy, hw, hh, dx, dy, dist, hits, max);
    for (u32 i = 0; i < count; ++i) {
        Object* hit = in.AllocObjectWithSlots(hit_type, 8);
        if (!hit) { count = i; break; }
        Physics2D_FillHit(in, hit, hits[i], ox, oy, dx, dy, dist);
        arr->Elements()[i] = Value::StructRef(hit);
    }
    return RetI32V(ret, (s32)count);
}

static bool Physics2D_BoxCastNonAlloc(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    return Physics2D_NonAlloc(in, argc, a, true, ret);
}
static bool Physics2D_RaycastNonAlloc(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    return Physics2D_NonAlloc(in, argc, a, false, ret);
}

static Object* HitObject(const Value& v) {
    Value x = v;
    if (x.tag == VT_BYREF && x.data.ref) x = *x.data.ref;
    return (x.tag == VT_STRUCT || x.tag == VT_OBJECT) ? x.data.obj : 0;
}
static bool Hit_GetCollider(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    if (ret) *ret = h ? h->Fields()[0] : Value::Null();
    return true;
}
static bool Hit_GetPoint(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    if (ret) *ret = h ? h->Fields()[1] : Value::Null();
    return true;
}
static bool Hit_GetNormal(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    if (ret) *ret = h ? h->Fields()[2] : Value::Null();
    return true;
}
static bool Hit_GetDistance(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    return RetF32Field(ret, h ? h->Fields()[3].data.f32 : 0.0f);
}
static bool Hit_GetFraction(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    return RetF32Field(ret, h ? h->Fields()[4].data.f32 : 0.0f);
}
static bool Hit_GetTransform(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    if (ret) *ret = h ? h->Fields()[5] : Value::Null();
    return true;
}
static bool Hit_GetRigidbody(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    if (ret) *ret = h ? h->Fields()[6] : Value::Null();
    return true;
}
static bool Hit_IsTrue(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* h = HitObject(a[0]);
    bool hit = h && h->Fields()[0].tag == VT_OBJECT && h->Fields()[0].data.obj != 0;
    return RetBoolV(ret, hit);
}

static u32 g_rng_state = 0x2545F491u;
static u32 NextRandom() {
    g_rng_state = g_rng_state * 1664525u + 1013904223u;
    return g_rng_state;
}
static bool Random_Range(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_R4 || a[0].tag == VT_R8 || a[1].tag == VT_R4 || a[1].tag == VT_R8) {
        f32 lo = in.ToF32(a[0]);
        f32 hi = in.ToF32(a[1]);
        f32 t = (f32)(NextRandom() & 0xFFFFFFu) / 16777216.0f;
        return RetF32Field(ret, lo + (hi - lo) * t);
    }
    s32 lo = in.ToI32(a[0]);
    s32 hi = in.ToI32(a[1]);
    if (hi <= lo) return RetI32V(ret, lo);
    return RetI32V(ret, lo + (s32)(NextRandom() % (u32)(hi - lo)));
}
static bool Random_Value(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetF32Field(ret, (f32)(NextRandom() & 0xFFFFFFu) / 16777216.0f);
}
static bool Random_InitState(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    g_rng_state = in.ToU32(a[0]) | 1u;
    return RetVoid(ret);
}

static Object* ColorObject(const Value& v) {
    Value x = v;
    if (x.tag == VT_BYREF && x.data.ref) x = *x.data.ref;
    if ((x.tag == VT_STRUCT || x.tag == VT_OBJECT) && x.data.obj) return x.data.obj;
    return 0;
}
static bool Color_Make(Interpreter& in, Value* ret, f32 r, f32 g, f32 b, f32 a) {
    u32 ct = in.Metadata().FindType("UnityEngine.Color");
    Object* o = in.AllocObjectWithSlots(ct, 4);
    if (!o) return false;
    o->Fields()[0] = Value::R4(r);
    o->Fields()[1] = Value::R4(g);
    o->Fields()[2] = Value::R4(b);
    o->Fields()[3] = Value::R4(a);
    if (ret) *ret = Value::StructRef(o);
    return true;
}
static bool Color_Red(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) { return Color_Make(in, ret, 1, 0, 0, 1); }
static bool Color_Green(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) { return Color_Make(in, ret, 0, 1, 0, 1); }
static bool Color_Blue(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) { return Color_Make(in, ret, 0, 0, 1, 1); }
static bool Color_White(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) { return Color_Make(in, ret, 1, 1, 1, 1); }
static bool Color_Black(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) { return Color_Make(in, ret, 0, 0, 0, 1); }
static bool Color_Yellow(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) { return Color_Make(in, ret, 1, 1, 0, 1); }
static bool Color_Clear(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) { return Color_Make(in, ret, 0, 0, 0, 0); }
static bool Color_Ctor(Interpreter& in, const MemberRefInfo& member, u32 argc, Value* a, Value* ret) {
    Object* o = ColorObject(a[0]);
    if (!o && a[0].tag == VT_BYREF && a[0].data.ref) {
        Value s = in.NewStructValue(member.decl_id);
        *a[0].data.ref = s;
        o = ColorObject(s);
    }
    if (!o) o = in.AllocObjectWithSlots(member.decl_id, 4);
    if (o) {
        if (argc >= 2) o->Fields()[0] = Value::R4(a[1].data.f32);
        if (argc >= 3) o->Fields()[1] = Value::R4(a[2].data.f32);
        if (argc >= 4) o->Fields()[2] = Value::R4(a[3].data.f32);
        if (argc >= 5) o->Fields()[3] = Value::R4(a[4].data.f32);
    }
    if (ret) *ret = o ? Value::StructRef(o) : Value::Null();
    return true;
}
static bool Animator_StringToHash(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    u32 h = 2166136261u;
    if (argc >= 1 && a[0].tag == VT_STRING && a[0].data.str) {
        const u16* s = a[0].data.str->Chars();
        for (u32 i = 0; i < a[0].data.str->length; ++i) {
            h ^= (u8)(s[i] & 0xFF);
            h *= 16777619u;
        }
    }
    return RetI32V(ret, (s32)h);
}

static bool Renderer_GetMaterial(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {    u32 ci = ManagedComponentIndex(in, a[0]);
    Value comp = in.ManagedComponent(ci);
    if (comp.tag == VT_OBJECT && comp.data.obj) {
        Object* c = comp.data.obj;
        if (c->Fields()[2].tag == VT_OBJECT) {
            if (ret) *ret = c->Fields()[2];
            return true;
        }
        u32 material_type = in.Metadata().FindType("UnityEngine.Material");
        if (!material_type) material_type = in.ComponentTypeId();
        Object* mat = in.AllocObjectWithSlots(material_type, 8);
        if (!mat) return false;
        c->Fields()[2] = Value::ObjectRef(mat);
        if (ret) *ret = Value::ObjectRef(mat);
        return true;
    }
    if (ret) *ret = Value::Null();
    return true;
}

static f32 g_fixed_dt = 1.0f / 60.0f;
static f32 g_delta = 1.0f / 60.0f;
static f32 g_time = 0.0f;
static f32 g_time_scale = 1.0f;
static u32 g_frame_count = 0;

static bool Time_GetFixedDelta(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetF32Field(ret, g_fixed_dt);
}
static bool Time_GetDelta(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetF32Field(ret, g_delta);
}
static bool Time_GetTime(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetF32Field(ret, g_time);
}
static bool Time_GetTimeScale(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetF32Field(ret, g_time_scale);
}
static bool Time_SetTimeScale(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    g_time_scale = a[0].data.f32;
    return RetVoid(ret);
}
static bool Time_GetFrameCount(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    return RetI32V(ret, (s32)g_frame_count);
}

static bool StringEqualsAscii(const Value& v, const char* text) {
    if (v.tag != VT_STRING || !v.data.str) return false;
    u32 n = (u32)strlen(text);
    if (v.data.str->length != n) return false;
    for (u32 i = 0; i < n; ++i) {
        if (v.data.str->Chars()[i] != (u16)(u8)text[i]) return false;
    }
    return true;
}

static u32 FindMethodInHierarchy(Interpreter& in, u32 type_id, const char* name) {
    u32 cur = type_id;
    u32 guard = 0;
    while (cur && guard++ < 64) {
        u32 m = in.Metadata().FindMethod(cur, name);
        if (m) return m;
        cur = in.Metadata().Type(cur).base_id;
    }
    return 0;
}

static const char* LastPathSegment(const char* path) {
    if (!path) return "";
    const char* slash = strrchr(path, '/');
    return slash ? slash + 1 : path;
}

// Templates included in the converted scene are bound like normal scene
// objects but stay inactive until Object.Instantiate is called. Resources.Load
// resolves the resource path to one of those template objects.
static bool Resources_Load(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    if (argc < 1 || a[0].tag != VT_STRING) {
        if (ret) *ret = Value::Null();
        return true;
    }
    const char* path = in.StringUtf8(a[0]);
    if (!path) path = "";
    if (strcmp(path, "Audio/AudioMixer") == 0) {
        // Audio initialization reads the mixer asset and its serialized
        // groups. Provide a deterministic scaffold until the real audio
        // assets are extracted; this keeps level startup from dereferencing
        // a null mixer while audio playback remains a documented gap.
        u32 mix_type = in.Metadata().FindType("AudioManagerMixer");
        u32 mixer_type = in.Metadata().FindType("UnityEngine.Audio.AudioMixer");
        u32 groups_type = in.Metadata().FindType("AudioManagerMixer/Groups");
        if (!mix_type) {
            if (ret) *ret = Value::Null();
            return true;
        }
        Value mixer = Value::Null();
        if (mixer_type) {
            Object* mo = in.AllocObjectWithSlots(mixer_type, 4);
            if (mo) mixer = Value::ObjectRef(mo);
        }
        Value groups = Value::Null();
        if (groups_type) groups = in.NewObjectValue(groups_type);
        Value mgr = in.NewObjectValue(mix_type);
        if (mgr.tag == VT_OBJECT && mgr.data.obj) {
            u32 f = in.Metadata().FindField(mix_type, "mixer");
            if (f) in.SetField(f, mgr, mixer);
            f = in.Metadata().FindField(mix_type, "audioGroups");
            if (f) in.SetField(f, mgr, groups);
        }
        if (ret) *ret = mgr;
        return true;
    }
    if (strcmp(path, "dialoguer_data_object") == 0) {        u32 wrapper_type = in.Metadata().FindType("DialoguerEditor.DialogueEditorMasterObjectWrapper");
        u32 data_type = in.Metadata().FindType("DialoguerEditor.DialogueEditorMasterObject");
        if (!wrapper_type || !data_type) {
            if (ret) *ret = Value::Null();
            return true;
        }
        Value data = in.NewObjectValue(data_type);
        if (data.tag == VT_NULL) return false;
        u32 ctor = in.Metadata().FindMethod(data_type, ".ctor");
        if (ctor) {
            Value args[1];
            args[0] = data;
            Value r;
            if (!in.Invoke(ctor, args, 1, &r)) return false;
        }
        Value wrapper = in.NewObjectValue(wrapper_type);
        if (wrapper.tag == VT_NULL) return false;
        u32 data_field = in.Metadata().FindField(wrapper_type, "data");
        if (data_field) in.SetField(data_field, wrapper, data);
        if (ret) *ret = wrapper;
        return true;
    }

    const char* name = LastPathSegment(path);
    u32 target = in.GenericArgType(0);
    Scene* scene = in.BoundScene();
    if (!scene || !name[0]) {
        LogInfo("unity", "Resources.Load '%s' -> null (no scene)", path);
        if (ret) *ret = Value::Null();
        return true;
    }
    for (u32 oi = 0; oi < scene->ObjectCount(); ++oi) {
        const SceneObject& o = scene->Object(oi);
        if (strcmp(o.name, name) != 0) continue;
        if (target == in.GameObjectTypeId()) {
            LogInfo("unity", "Resources.Load '%s' -> GameObject '%s'", path, o.name);
            if (ret) *ret = in.ManagedGameObject(oi);
            return true;
        }
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene->ComponentCount()) break;
            Value v = in.ManagedComponent(ci);
            if (v.tag == VT_OBJECT && v.data.obj) {
                u32 vt = v.data.obj->type_id;
                if (!target || vt == target || in.IsAssignable(vt, target)) {
                    LogInfo("unity", "Resources.Load '%s' -> %s on '%s' (target=%u)",
                            path, scene->Component(ci).controller, o.name, target);
                    if (ret) *ret = v;
                    return true;
                }
            }
        }
    }
    LogInfo("unity", "Resources.Load '%s' -> null (template not found, target=%u)", path, target);
    if (ret) *ret = Value::Null();
    return true;
}

static void InvokeAwakeOnTree(Interpreter& in, u32 root_oi) {
    Scene* scene = in.BoundScene();
    if (!scene || root_oi >= scene->ObjectCount()) return;
    u32 root_id = scene->Object(root_oi).id;
    // Root first, then descendants, mirroring the parent-before-child order
    // used by the prefab extractor.
    for (u32 pass = 0; pass < 2; ++pass) {
        for (u32 oi = 0; oi < scene->ObjectCount(); ++oi) {
            bool is_root = (oi == root_oi);
            if (pass == 0 && !is_root) continue;
            if (pass == 1 && is_root) continue;
            if (!is_root && !IsSceneDescendant(*scene, oi, root_id)) continue;
            const SceneObject& o = scene->Object(oi);
            for (u32 c = 0; c < o.component_count; ++c) {
                u32 ci = o.component_start + c;
                if (ci >= scene->ComponentCount()) break;
                if (scene->Component(ci).type != 4) continue;
                if (in.IsComponentAwake(ci)) continue;
                const char* ctrl = scene->Component(ci).controller;
                // UI and Rewired editor UI stacks are not gameplay; their
                // initialization depends on the full Unity UI/Rewired editor
                // runtime and would block level startup.
                if (strncmp(ctrl, "Rewired.UI.", 11) == 0 ||
                    strncmp(ctrl, "Rewired.Integration.", 20) == 0 ||
                    strncmp(ctrl, "UnityEngine.EventSystems.", 24) == 0 ||
                    strncmp(ctrl, "UnityEngine.UI.", 15) == 0 ||
                    strncmp(ctrl, "TMPro.", 6) == 0 ||
                    strncmp(ctrl, "AudioIgnorePauseBehaviour", 25) == 0 ||
                    strncmp(ctrl, "AudioManagerComponent", 21) == 0) {
                    in.MarkComponentAwake(ci);
                    continue;
                }
                Value self = in.ManagedComponent(ci);
                if (self.tag != VT_OBJECT || !self.data.obj) continue;
                u32 awake = FindMethodInHierarchy(in, self.data.obj->type_id, "Awake");
                if (awake) {
                    Value r;
                    bool ok = in.Invoke(awake, &self, 1, &r);
                    if (!ok) {
                        LogWarn("unity", "Awake %s on '%s' failed",
                                scene->Component(ci).controller, o.name);
                    }
                }
                in.MarkComponentAwake(ci);
            }
        }
    }
}

static bool Object_Instantiate(Interpreter& in, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    if (argc < 1 || a[0].tag != VT_OBJECT || !a[0].data.obj) {
        if (ret) *ret = Value::Null();
        return true;
    }
    u32 oi = in.ManagedObjectIndexFor(a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) {
        if (ret) *ret = a[0];
        return true;
    }
    SceneObject& o = scene->MutableObject(oi);
    o.flags |= 1u;
    a[0].data.obj->flags |= 1u;
    InvokeAwakeOnTree(in, oi);
    if (ret) *ret = a[0];
    return true;
}

static bool Object_FindObjectOfType(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    u32 target = in.GenericArgType(0);
    Scene* scene = in.BoundScene();
    if (scene && target) {
        for (u32 ci = 0; ci < scene->ComponentCount(); ++ci) {
            Value v = in.ManagedComponent(ci);
            if (v.tag == VT_OBJECT && v.data.obj) {
                u32 vt = v.data.obj->type_id;
                if (vt == target || in.IsAssignable(vt, target)) {
                    if (ret) *ret = v;
                    return true;
                }
            }
        }
        if (target == in.GameObjectTypeId() && scene->ObjectCount()) {
            if (ret) *ret = in.ManagedGameObject(0);
            return true;
        }
    }
    if (ret) *ret = Value::Null();
    return true;
}
static Object* RectObject(const Value& v) {
    Value x = v;
    if (x.tag == VT_BYREF && x.data.ref) x = *x.data.ref;
    if ((x.tag == VT_STRUCT || x.tag == VT_OBJECT) && x.data.obj) return x.data.obj;
    return 0;
}
static bool Rect_GetX(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[0].data.f32 : 0.0f);
}
static bool Rect_SetX(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) o->Fields()[0] = Value::R4(a[1].data.f32);
    return RetVoid(ret);
}
static bool Rect_GetY(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[1].data.f32 : 0.0f);
}
static bool Rect_SetY(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) o->Fields()[1] = Value::R4(a[1].data.f32);
    return RetVoid(ret);
}
static bool Rect_GetWidth(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[2].data.f32 : 0.0f);
}
static bool Rect_SetWidth(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) o->Fields()[2] = Value::R4(a[1].data.f32);
    return RetVoid(ret);
}
static bool Rect_GetHeight(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[3].data.f32 : 0.0f);
}
static bool Rect_SetHeight(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) o->Fields()[3] = Value::R4(a[1].data.f32);
    return RetVoid(ret);
}
static bool Rect_GetCenter(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    f32 x = o ? o->Fields()[0].data.f32 : 0.0f;
    f32 y = o ? o->Fields()[1].data.f32 : 0.0f;
    f32 w = o ? o->Fields()[2].data.f32 : 0.0f;
    f32 h = o ? o->Fields()[3].data.f32 : 0.0f;
    return RetVec2V(in, ret, x + w * 0.5f, y + h * 0.5f);
}
static bool Rect_GetSize(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetVec2V(in, ret, o ? o->Fields()[2].data.f32 : 0.0f,
                    o ? o->Fields()[3].data.f32 : 0.0f);
}
static bool Rect_GetXMin(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[0].data.f32 : 0.0f);
}
static bool Rect_SetXMin(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) {
        f32 xmax = o->Fields()[0].data.f32 + o->Fields()[2].data.f32;
        o->Fields()[0] = Value::R4(a[1].data.f32);
        o->Fields()[2] = Value::R4(xmax - a[1].data.f32);
    }
    return RetVoid(ret);
}
static bool Rect_GetXMax(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[0].data.f32 + o->Fields()[2].data.f32 : 0.0f);
}
static bool Rect_SetXMax(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) o->Fields()[2] = Value::R4(a[1].data.f32 - o->Fields()[0].data.f32);
    return RetVoid(ret);
}
static bool Rect_GetYMin(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[1].data.f32 : 0.0f);
}
static bool Rect_SetYMin(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) {
        f32 ymax = o->Fields()[1].data.f32 + o->Fields()[3].data.f32;
        o->Fields()[1] = Value::R4(a[1].data.f32);
        o->Fields()[3] = Value::R4(ymax - a[1].data.f32);
    }
    return RetVoid(ret);
}
static bool Rect_GetYMax(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    return RetF32Field(ret, o ? o->Fields()[1].data.f32 + o->Fields()[3].data.f32 : 0.0f);
}
static bool Rect_SetYMax(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o) o->Fields()[3] = Value::R4(a[1].data.f32 - o->Fields()[1].data.f32);
    return RetVoid(ret);
}
static bool Rect_Ctor(Interpreter&, const MemberRefInfo&, u32 argc, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    if (o && argc >= 5) {
        o->Fields()[0] = Value::R4(a[1].data.f32);
        o->Fields()[1] = Value::R4(a[2].data.f32);
        o->Fields()[2] = Value::R4(a[3].data.f32);
        o->Fields()[3] = Value::R4(a[4].data.f32);
    }
    return RetVoid(ret);
}
static bool Rect_Contains(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Object* o = RectObject(a[0]);
    f32 px = 0.0f, py = 0.0f;
    if (a[1].tag == VT_VEC2 && a[1].data.ptr) {
        const Vec2* p = (const Vec2*)a[1].data.ptr;
        px = p->x; py = p->y;
    } else if ((a[1].tag == VT_STRUCT || a[1].tag == VT_OBJECT) && a[1].data.obj) {
        Object* p = a[1].data.obj;
        px = p->Fields()[0].data.f32;
        py = p->Fields()[1].data.f32;
    }
    if (!o) return RetBoolV(ret, false);
    f32 x = o->Fields()[0].data.f32;
    f32 y = o->Fields()[1].data.f32;
    f32 w = o->Fields()[2].data.f32;
    f32 h = o->Fields()[3].data.f32;
    return RetBoolV(ret, px >= x && px <= x + w && py >= y && py <= y + h);
}

static bool Camera_GetMain(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    Scene* scene = in.BoundScene();
    if (scene) {
        for (u32 ci = 0; ci < scene->ComponentCount(); ++ci) {
            Value v = in.ManagedComponent(ci);
            if (v.tag != VT_OBJECT || !v.data.obj) continue;
            const TypeInfo& t = in.Metadata().Type(v.data.obj->type_id);
            if (t.full_name && strcmp(t.full_name, "UnityEngine.Camera") == 0) {
                if (ret) *ret = v;
                return true;
            }
        }
    }
    if (ret) *ret = Value::Null();
    return true;
}
static bool Camera_SetOrthographicSize(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_OBJECT && a[0].data.obj && a[0].data.obj->slots > 2) {
        a[0].data.obj->Fields()[2] = Value::R4(a[1].data.f32);
    }
    Scene* scene = in.BoundScene();
    if (scene && scene->MutableMainCamera()) scene->MutableMainCamera()->orthographic_size = a[1].data.f32;
    return RetVoid(ret);
}
static bool Camera_GetOrthographicSize(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_OBJECT && a[0].data.obj && a[0].data.obj->slots > 2 &&
        a[0].data.obj->Fields()[2].tag == VT_R4) {
        return RetF32V(ret, a[0].data.obj->Fields()[2].data.f32);
    }
    Scene* scene = in.BoundScene();
    return RetF32V(ret, scene && scene->MainCamera() ? scene->MainCamera()->orthographic_size : 360.0f);
}
static bool Camera_SetOrthographic(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    bool on = Interpreter::ToBool(a[1]);
    if (a[0].tag == VT_OBJECT && a[0].data.obj) {
        a[0].data.obj->flags = (a[0].data.obj->flags & ~1u) | (on ? 1u : 0u);
    }
    Scene* scene = in.BoundScene();
    if (scene && scene->MutableMainCamera()) {
        SceneCamera* c = scene->MutableMainCamera();
        c->flags = (c->flags & ~1u) | (on ? 1u : 0u);
    }
    return RetVoid(ret);
}
static bool Camera_GetOrthographic(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_OBJECT && a[0].data.obj) return RetBoolV(ret, (a[0].data.obj->flags & 1u) != 0);
    Scene* scene = in.BoundScene();
    return RetBoolV(ret, scene && scene->MainCamera() && (scene->MainCamera()->flags & 1u));
}
static bool Camera_SetBackgroundColor(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Scene* scene = in.BoundScene();
    SceneCamera* c = scene ? scene->MutableMainCamera() : 0;
    if (c && (a[1].tag == VT_STRUCT || a[1].tag == VT_OBJECT) && a[1].data.obj) {
        Object* col = a[1].data.obj;
        c->background[0] = col->Fields()[0].data.f32;
        c->background[1] = col->Fields()[1].data.f32;
        c->background[2] = col->Fields()[2].data.f32;
        c->background[3] = col->Fields()[3].data.f32;
    }
    return RetVoid(ret);
}
static bool Camera_SetDepth(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    Scene* scene = in.BoundScene();
    if (scene && scene->MutableMainCamera()) scene->MutableMainCamera()->depth = a[1].data.f32;
    return RetVoid(ret);
}
static bool Camera_GetDepth(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    Scene* scene = in.BoundScene();
    return RetF32V(ret, scene && scene->MainCamera() ? scene->MainCamera()->depth : 0.0f);
}
static bool Animator_SetSpeed(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    SceneComponent* c = MutableSceneComponentFor(in, a[0]);
    if (c) c->color[0] = a[1].data.f32;
    return RetVoid(ret);
}
static bool Animator_GetSpeed(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    const SceneComponent* c = SceneComponentFor(in, a[0]);
    return RetF32Field(ret, c ? c->color[0] : 1.0f);
}
static bool MonoBehaviour_StartCoroutine(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = Value::Null();
    return true;
}

static bool Vector3_Ctor(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag == VT_BYREF && a[0].data.ref) {
        *a[0].data.ref = in.NewVec3(a[1].data.f32, a[2].data.f32, a[3].data.f32);
        return RetVoid(ret);
    }
    if (ret) *ret = in.NewVec3(a[1].data.f32, a[2].data.f32, a[3].data.f32);
    return true;
}
static bool Vector3_Zero(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec3(0.0f, 0.0f, 0.0f); return true;
}
static bool Vector3_One(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec3(1.0f, 1.0f, 1.0f); return true;
}
static bool Vector3_Up(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec3(0.0f, 1.0f, 0.0f); return true;
}
static bool Vector3_Right(Interpreter& in, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = in.NewVec3(1.0f, 0.0f, 0.0f); return true;
}
static bool Vector3_Add(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_VEC3 || a[1].tag != VT_VEC3) return false;
    const Vec3* x = (const Vec3*)a[0].data.ptr;
    const Vec3* y = (const Vec3*)a[1].data.ptr;
    if (ret) *ret = in.NewVec3(x->x + y->x, x->y + y->y, x->z + y->z);
    return true;
}
static bool Vector3_Sub(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_VEC3 || a[1].tag != VT_VEC3) return false;
    const Vec3* x = (const Vec3*)a[0].data.ptr;
    const Vec3* y = (const Vec3*)a[1].data.ptr;
    if (ret) *ret = in.NewVec3(x->x - y->x, x->y - y->y, x->z - y->z);
    return true;
}
static bool Vector3_Mul(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_VEC3 || !a[0].data.ptr) return false;
    const Vec3* x = (const Vec3*)a[0].data.ptr;
    f32 s = a[1].data.f32;
    if (ret) *ret = in.NewVec3(x->x * s, x->y * s, x->z * s);
    return true;
}
static bool Vector3_Distance(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_VEC3 || a[1].tag != VT_VEC3 || !a[0].data.ptr || !a[1].data.ptr) return false;
    const Vec3* x = (const Vec3*)a[0].data.ptr;
    const Vec3* y = (const Vec3*)a[1].data.ptr;
    f32 dx = x->x - y->x, dy = x->y - y->y, dz = x->z - y->z;
    return RetF32V(ret, sqrtf(dx * dx + dy * dy + dz * dz));
}
static bool Vector3_ImplicitV2(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {    if (a[0].tag == VT_VEC3 && a[0].data.ptr) {
        const Vec3* x = (const Vec3*)a[0].data.ptr;
        if (ret) *ret = in.NewVec2(x->x, x->y);
        return true;
    }
    if (a[0].tag == VT_VEC2 && a[0].data.ptr) {
        const Vec2* x = (const Vec2*)a[0].data.ptr;
        if (ret) *ret = in.NewVec3(x->x, x->y, 0.0f);
        return true;
    }
    return false;
}
static bool Vector2_ImplicitV3(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_VEC2 || !a[0].data.ptr) return false;
    const Vec2* x = (const Vec2*)a[0].data.ptr;
    if (ret) *ret = in.NewVec3(x->x, x->y, 0.0f);
    return true;
}

bool InvokeUnityNative(Interpreter& in, const MemberRefInfo& member, u32 argc, Value* args, Value* ret) {
    if (ret) *ret = Value::Void();
    const TypeInfo& t = in.Metadata().Type(member.decl_id);
    const char* type = t.full_name;
    const char* n = member.name;

    if (strcmp(type, "UnityEngine.EventSystems.UIBehaviour") == 0) {
        if (strcmp(n, "Awake") == 0 || strcmp(n, "Start") == 0 ||
            strcmp(n, "OnEnable") == 0 || strcmp(n, "OnDisable") == 0) {
            return RetVoid(ret);
        }
    }

    if (strcmp(type, "UnityEngine.AudioSource") == 0) {
        if (strcmp(n, "set_ignoreListenerPause") == 0 || strcmp(n, "set_volume") == 0 ||
            strcmp(n, "set_pitch") == 0 || strcmp(n, "set_loop") == 0 ||
            strcmp(n, "set_playOnAwake") == 0 || strcmp(n, "set_mute") == 0 ||
            strcmp(n, "set_spatialBlend") == 0 || strcmp(n, "set_clip") == 0 ||
            strcmp(n, "set_outputAudioMixerGroup") == 0 || strcmp(n, "Play") == 0 ||
            strcmp(n, "Stop") == 0 || strcmp(n, "Pause") == 0 ||
            strcmp(n, "UnPause") == 0 || strcmp(n, "PlayOneShot") == 0 ||
            strcmp(n, "PlayDelayed") == 0) {
            return RetVoid(ret);
        }
        if (strcmp(n, "get_ignoreListenerPause") == 0 || strcmp(n, "get_loop") == 0 ||
            strcmp(n, "get_playOnAwake") == 0 || strcmp(n, "get_mute") == 0 ||
            strcmp(n, "get_isPlaying") == 0) {
            return RetBoolV(ret, false);
        }
        if (strcmp(n, "get_volume") == 0 || strcmp(n, "get_pitch") == 0 ||
            strcmp(n, "get_spatialBlend") == 0) {
            return RetF32V(ret, 1.0f);
        }
        if (strcmp(n, "get_clip") == 0 || strcmp(n, "get_outputAudioMixerGroup") == 0 ||
            strcmp(n, "get_time") == 0) {
            if (ret) *ret = Value::Null();
            return true;
        }
    }
    if (strcmp(type, "UnityEngine.Audio.AudioMixer") == 0) {
        if (strcmp(n, "GetFloat") == 0) {
            if (argc >= 3 && args[2].tag == VT_BYREF && args[2].data.ref) {
                *args[2].data.ref = Value::R4(0.0f);
            }
            return RetBoolV(ret, true);
        }
        if (strcmp(n, "SetFloat") == 0) return RetVoid(ret);
        if (strcmp(n, "FindMatchingGroups") == 0) {
            if (ret) *ret = in.NewArrayValue(0, 0);
            return true;
        }
    }
    if (strcmp(type, "UnityEngine.Screen") == 0) {
        if (strcmp(n, "get_currentResolution") == 0) {
            u32 rt = in.Metadata().FindType("UnityEngine.Resolution");
            Object* ro = rt ? in.AllocObjectWithSlots(rt, 4) : 0;
            if (ret) *ret = ro ? Value::StructRef(ro) : Value::Null();
            return true;
        }
        if (strcmp(n, "get_fullScreen") == 0) return RetBoolV(ret, false);
        if (strcmp(n, "get_width") == 0) return RetI32V(ret, 1280);
        if (strcmp(n, "get_height") == 0) return RetI32V(ret, 720);
        if (strcmp(n, "SetResolution") == 0) return RetVoid(ret);
    }
    if (strcmp(type, "UnityEngine.Resolution") == 0) {
        if (strcmp(n, "get_width") == 0) return RetI32V(ret, 1280);
        if (strcmp(n, "get_height") == 0) return RetI32V(ret, 720);
        if (strcmp(n, "get_refreshRate") == 0) return RetI32V(ret, 60);
    }
    if (strcmp(type, "UnityEngine.CanvasGroup") == 0) {
        if (strcmp(n, "set_alpha") == 0) {
            if (args[0].tag == VT_OBJECT && args[0].data.obj && args[0].data.obj->slots > 2) {
                args[0].data.obj->Fields()[2] = Value::R4(args[1].data.f32);
            }
            return RetVoid(ret);
        }
        if (strcmp(n, "get_alpha") == 0) {
            if (args[0].tag == VT_OBJECT && args[0].data.obj && args[0].data.obj->slots > 2) {
                return RetF32V(ret, args[0].data.obj->Fields()[2].data.f32);
            }
            return RetF32V(ret, 1.0f);
        }
        if (strcmp(n, "set_interactable") == 0 || strcmp(n, "set_blocksRaycasts") == 0 ||
            strcmp(n, "set_ignoreParentGroups") == 0) return RetVoid(ret);
        if (strcmp(n, "get_interactable") == 0 || strcmp(n, "get_blocksRaycasts") == 0 ||
            strcmp(n, "get_ignoreParentGroups") == 0) return RetBoolV(ret, true);
    }
    if (strcmp(type, "UnityEngine.RectTransform") == 0) {
        if (strcmp(n, "get_sizeDelta") == 0 || strcmp(n, "get_anchoredPosition") == 0 ||
            strcmp(n, "get_anchorMin") == 0 || strcmp(n, "get_anchorMax") == 0 ||
            strcmp(n, "get_offsetMin") == 0 || strcmp(n, "get_offsetMax") == 0) {
            if (ret) *ret = in.NewVec2(0.0f, 0.0f);
            return true;
        }
        if (strcmp(n, "get_pivot") == 0) {
            if (ret) *ret = in.NewVec2(0.5f, 0.5f);
            return true;
        }
        if (strcmp(n, "set_sizeDelta") == 0 || strcmp(n, "set_anchoredPosition") == 0 ||
            strcmp(n, "set_anchorMin") == 0 || strcmp(n, "set_anchorMax") == 0 ||
            strcmp(n, "set_pivot") == 0 || strcmp(n, "set_offsetMin") == 0 ||
            strcmp(n, "set_offsetMax") == 0) return RetVoid(ret);
        if (strcmp(n, "get_rect") == 0) {
            u32 rt = in.Metadata().FindType("UnityEngine.Rect");
            Object* o = rt ? in.AllocObjectWithSlots(rt, 4) : 0;
            if (ret) *ret = o ? Value::StructRef(o) : Value::Null();
            return true;
        }
    }
    if (strncmp(type, "UnityEngine.UI.", 15) == 0) {
        if (strcmp(n, "get_text") == 0) {
            if (ret) *ret = in.NewStringAscii("");
            return true;
        }
        if (strcmp(n, "set_text") == 0 || strcmp(n, "set_font") == 0 ||
            strcmp(n, "set_fontSize") == 0 || strcmp(n, "set_alignment") == 0 ||
            strcmp(n, "set_fontStyle") == 0 || strcmp(n, "set_sprite") == 0 ||
            strcmp(n, "set_material") == 0 || strcmp(n, "SetAllDirty") == 0 ||
            strcmp(n, "set_preserveAspect") == 0 || strcmp(n, "set_type") == 0 ||
            strcmp(n, "set_fillAmount") == 0 || strcmp(n, "set_supportRichText") == 0) {
            return RetVoid(ret);
        }
        if (strcmp(n, "get_fontSize") == 0 || strcmp(n, "get_alignment") == 0 ||
            strcmp(n, "get_fontStyle") == 0 || strcmp(n, "get_type") == 0 ||
            strcmp(n, "get_verticalOverflow") == 0 || strcmp(n, "get_horizontalOverflow") == 0) {
            return RetI32V(ret, 0);
        }
        if (strcmp(n, "get_lineSpacing") == 0 || strcmp(n, "get_pixelsPerUnit") == 0) {
            return RetF32V(ret, 1.0f);
        }
        if (strcmp(n, "get_resizeTextForBestFit") == 0 || strcmp(n, "get_supportRichText") == 0 ||
            strcmp(n, "get_alignByGeometry") == 0) {
            return RetBoolV(ret, false);
        }
        if (strcmp(n, "get_rectTransform") == 0) {
            return GameObject_GetTransform(in, member, argc, args, ret);
        }
        if (strcmp(n, "get_color") == 0) {
            u32 ct = in.Metadata().FindType("UnityEngine.Color");
            Object* o = ct ? in.AllocObjectWithSlots(ct, 4) : 0;
            if (o) {
                o->Fields()[0] = Value::R4(1.0f);
                o->Fields()[1] = Value::R4(1.0f);
                o->Fields()[2] = Value::R4(1.0f);
                o->Fields()[3] = Value::R4(1.0f);
            }
            if (ret) *ret = o ? Value::StructRef(o) : Value::Null();
            return true;
        }
        if (strcmp(n, "set_color") == 0) return RetVoid(ret);
        if (strcmp(n, "get_sprite") == 0 || strcmp(n, "get_material") == 0 ||
            strcmp(n, "get_font") == 0 || strcmp(n, "get_canvasRenderer") == 0) {
            if (ret) *ret = Value::Null();
            return true;
        }
    }
    if (strcmp(type, "UnityEngine.Canvas") == 0) {
        if (strcmp(n, "get_sortingOrder") == 0) return RetI32V(ret, 0);
        if (strcmp(n, "set_sortingOrder") == 0 || strcmp(n, "set_renderMode") == 0 ||
            strcmp(n, "set_worldCamera") == 0 || strcmp(n, "set_overrideSorting") == 0) {
            return RetVoid(ret);
        }
    }
    if (strcmp(type, "UnityEngine.Application") == 0) {
        if (strcmp(n, "set_targetFrameRate") == 0) return RetVoid(ret);
        if (strcmp(n, "get_targetFrameRate") == 0) return RetI32V(ret, 60);
        if (strcmp(n, "get_isPlaying") == 0) return RetBoolV(ret, true);
        if (strcmp(n, "get_isEditor") == 0) return RetBoolV(ret, false);
        if (strcmp(n, "get_platform") == 0) return RetI32V(ret, 2);
        if (strcmp(n, "Quit") == 0) return RetVoid(ret);
        if (strcmp(n, "get_persistentDataPath") == 0 ||
            strcmp(n, "get_dataPath") == 0 ||
            strcmp(n, "get_streamingAssetsPath") == 0) {
            if (ret) *ret = in.NewStringAscii("");
            return true;
        }
    }
    if (strcmp(type, "UnityEngine.QualitySettings") == 0) {
        if (strcmp(n, "get_vSyncCount") == 0) return RetI32V(ret, 0);
        if (strcmp(n, "set_vSyncCount") == 0) return RetVoid(ret);
    }

    if (strcmp(type, "UnityEngine.Object") == 0 || strcmp(type, "UnityEngine.Component") == 0 ||
        strcmp(type, "UnityEngine.Behaviour") == 0 || strcmp(type, "UnityEngine.GameObject") == 0 ||
        strcmp(type, "UnityEngine.MonoBehaviour") == 0 || strcmp(type, "UnityEngine.SpriteRenderer") == 0 ||
        strcmp(type, "UnityEngine.Renderer") == 0 || strcmp(type, "UnityEngine.Animator") == 0 ||
        strcmp(type, "UnityEngine.Camera") == 0 ||
        strcmp(type, "UnityEngine.Collider2D") == 0 || strcmp(type, "UnityEngine.BoxCollider2D") == 0 ||
        strcmp(type, "UnityEngine.CircleCollider2D") == 0 || strcmp(type, "UnityEngine.Rigidbody2D") == 0) {
        if (strcmp(n, "get_gameObject") == 0) return Component_GetGameObject(in, member, argc, args, ret);
        if (strcmp(n, "get_transform") == 0) {
            if (strcmp(type, "UnityEngine.GameObject") == 0) return GameObject_GetTransform(in, member, argc, args, ret);
            return Component_GetTransform(in, member, argc, args, ret);
        }
        if (strcmp(n, "GetComponent") == 0) return GameObject_GetComponent(in, member, argc, args, ret);
        if (strcmp(n, "AddComponent") == 0) {
            u32 target = 0;
            if (argc >= 2 && args[1].tag == VT_I4) target = (u32)args[1].data.i32;
            if (!target) target = in.GenericArgType(0);
            if (!target) {
                if (ret) *ret = Value::Null();
                return true;
            }
            Object* comp = in.AllocObjectWithSlots(target, 8);
            if (comp) {
                comp->Fields()[0] = Value::I4((s32)ManagedObjectIndex(in, args[0]));
                comp->Fields()[1] = Value::I4(-1);
                if (ret) *ret = Value::ObjectRef(comp);
            } else if (ret) {
                *ret = Value::Null();
            }
            return true;
        }
        if (strcmp(n, "Instantiate") == 0) return Object_Instantiate(in, member, argc, args, ret);
        if (strcmp(n, "FindObjectOfType") == 0) return Object_FindObjectOfType(in, member, argc, args, ret);
        if (strcmp(n, "get_material") == 0 || strcmp(n, "get_sharedMaterial") == 0) return Renderer_GetMaterial(in, member, argc, args, ret);
        if (strcmp(n, "get_sortingOrder") == 0) {
            const SceneComponent* c = SceneComponentFor(in, args[0]);
            return RetI32V(ret, c ? c->sorting_order : 0);
        }
        if (strcmp(n, "set_sortingOrder") == 0) {
            SceneComponent* c = MutableSceneComponentFor(in, args[0]);
            if (c) c->sorting_order = Interpreter::ToI32(args[1]);
            return RetVoid(ret);
        }
        if (strcmp(n, "get_sortingLayerID") == 0) {
            const SceneComponent* c = SceneComponentFor(in, args[0]);
            return RetI32V(ret, c ? c->sorting_layer : 0);
        }
        if (strcmp(n, "get_sortingLayer") == 0) {
            const SceneComponent* c = SceneComponentFor(in, args[0]);
            return RetI32V(ret, c ? c->sorting_layer : 0);
        }
        if (strcmp(n, "get_sortingLayerName") == 0) {
            if (ret) *ret = in.NewStringAscii("");
            return true;
        }
        if (strcmp(n, "set_sortingLayerName") == 0 || strcmp(n, "set_sortingLayerID") == 0) {
            return RetVoid(ret);
        }
        if (strcmp(n, "FindObjectsOfType") == 0) return Object_FindObjectsOfType(in, member, argc, args, ret);
        if (strcmp(n, "GetComponentsInChildren") == 0) return Component_GetComponentsInChildren(in, member, argc, args, ret);
        if (strcmp(n, "GetComponentInChildren") == 0) return Component_GetComponentInChildren(in, member, argc, args, ret);
        if (strcmp(n, "SetActive") == 0) return GameObject_SetActive(in, member, argc, args, ret);
        if (strcmp(n, "set_layer") == 0) return RetVoid(ret);
        if (strcmp(n, "get_layer") == 0) return RetI32V(ret, 0);
        if (strcmp(n, "get_activeSelf") == 0) return GameObject_GetActiveSelf(in, member, argc, args, ret);
        if (strcmp(n, "get_name") == 0) return Object_GetName(in, member, argc, args, ret);
        if (strcmp(n, "set_name") == 0) return RetVoid(ret);
        if (strcmp(n, "set_enabled") == 0) return Behaviour_SetEnabled(in, member, argc, args, ret);
        if (strcmp(n, "get_enabled") == 0) return Behaviour_GetEnabled(in, member, argc, args, ret);
        if (strcmp(n, "set_useGUILayout") == 0 || strcmp(n, "get_useGUILayout") == 0) return RetVoid(ret);
        if (strcmp(n, "get_tag") == 0 || strcmp(n, "set_tag") == 0) return RetVoid(ret);
        if (strcmp(n, "StartCoroutine") == 0) return MonoBehaviour_StartCoroutine(in, member, argc, args, ret);
        if (strcmp(n, ".ctor") == 0 && strcmp(type, "UnityEngine.GameObject") == 0) {
            return GameObject_Ctor(in, member, argc, args, ret);
        }
        if (strcmp(n, "StopCoroutine") == 0 || strcmp(n, "StopAllCoroutines") == 0 ||
            strcmp(n, "Invoke") == 0 || strcmp(n, ".ctor") == 0) {
            return MonoBehaviour_NoOp(in, member, argc, args, ret);
        }
        if (strcmp(n, "op_Equality") == 0) return RetBoolV(ret, args[0].data.obj == args[1].data.obj);
        if (strcmp(n, "op_Inequality") == 0) return RetBoolV(ret, args[0].data.obj != args[1].data.obj);
        if (strcmp(n, "op_Implicit") == 0) return RetBoolV(ret, args[0].data.obj != 0);
        if (strcmp(n, "Destroy") == 0 || strcmp(n, "DontDestroyOnLoad") == 0) return RetVoid(ret);
        if (strcmp(type, "UnityEngine.Animator") == 0) {
            if (strcmp(n, "set_speed") == 0) return Animator_SetSpeed(in, member, argc, args, ret);
            if (strcmp(n, "get_speed") == 0) return Animator_GetSpeed(in, member, argc, args, ret);
        }
        if (strcmp(n, "Play") == 0 || strcmp(n, "SetTrigger") == 0 || strcmp(n, "SetBool") == 0 ||
            strcmp(n, "SetInteger") == 0 || strcmp(n, "SetFloat") == 0) {
            return RetVoid(ret);
        }
    }
    if (strcmp(type, "UnityEngine.Camera") == 0) {
        if (strcmp(n, "get_main") == 0) return Camera_GetMain(in, member, argc, args, ret);
        if (strcmp(n, "set_orthographicSize") == 0) return Camera_SetOrthographicSize(in, member, argc, args, ret);
        if (strcmp(n, "get_orthographicSize") == 0) return Camera_GetOrthographicSize(in, member, argc, args, ret);
        if (strcmp(n, "set_orthographic") == 0) return Camera_SetOrthographic(in, member, argc, args, ret);
        if (strcmp(n, "get_orthographic") == 0) return Camera_GetOrthographic(in, member, argc, args, ret);
        if (strcmp(n, "set_backgroundColor") == 0) return Camera_SetBackgroundColor(in, member, argc, args, ret);
        if (strcmp(n, "set_depth") == 0) return Camera_SetDepth(in, member, argc, args, ret);
        if (strcmp(n, "get_depth") == 0) return Camera_GetDepth(in, member, argc, args, ret);
        if (strcmp(n, "set_clearFlags") == 0 || strcmp(n, "set_aspect") == 0 ||
            strcmp(n, "set_fieldOfView") == 0 || strcmp(n, "set_nearClipPlane") == 0 ||
            strcmp(n, "set_farClipPlane") == 0 || strcmp(n, "set_cullingMask") == 0) {
            return RetVoid(ret);
        }
        if (strcmp(n, "get_clearFlags") == 0) return RetI32V(ret, 1);
        if (strcmp(n, "get_aspect") == 0) return RetF32V(ret, 16.0f / 9.0f);
        if (strcmp(n, "get_fieldOfView") == 0) return RetF32V(ret, 60.0f);
        if (strcmp(n, "ScreenToWorldPoint") == 0 || strcmp(n, "WorldToScreenPoint") == 0) {
            if (ret) *ret = in.NewVec3(0.0f, 0.0f, 0.0f);
            return true;
        }
    }
    if (strcmp(type, "UnityEngine.Transform") == 0) {        if (strcmp(n, "get_position") == 0) return Transform_GetPosition(in, member, argc, args, ret);
        if (strcmp(n, "set_position") == 0) return Transform_SetPosition(in, member, argc, args, ret);
        if (strcmp(n, "get_localPosition") == 0) return Transform_GetLocalPosition(in, member, argc, args, ret);
        if (strcmp(n, "set_localPosition") == 0) return Transform_SetLocalPosition(in, member, argc, args, ret);
        if (strcmp(n, "get_localScale") == 0) return Transform_GetLocalScale(in, member, argc, args, ret);
        if (strcmp(n, "set_localScale") == 0) return Transform_SetLocalScale(in, member, argc, args, ret);
        if (strcmp(n, "get_parent") == 0) return Transform_GetParent(in, member, argc, args, ret);
        if (strcmp(n, "set_parent") == 0 || strcmp(n, "SetParent") == 0) return Transform_SetParent(in, member, argc, args, ret);
        if (strcmp(n, "get_localEulerAngles") == 0 || strcmp(n, "get_eulerAngles") == 0) {
            return Transform_GetEuler(in, member, argc, args, ret);
        }
        if (strcmp(n, "set_localEulerAngles") == 0 || strcmp(n, "set_eulerAngles") == 0) {
            return Transform_SetEuler(in, member, argc, args, ret);
        }
        if (strcmp(n, "get_gameObject") == 0) return Component_GetGameObject(in, member, argc, args, ret);
        if (strcmp(n, "get_name") == 0) return Object_GetName(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Collider2D") == 0 || strcmp(type, "UnityEngine.BoxCollider2D") == 0 ||
        strcmp(type, "UnityEngine.CircleCollider2D") == 0) {
        if (strcmp(n, "get_offset") == 0) return Collider2D_GetOffset(in, member, argc, args, ret);
        if (strcmp(n, "set_offset") == 0) return Collider2D_SetOffset(in, member, argc, args, ret);
        if (strcmp(n, "get_attachedRigidbody") == 0) return Collider2D_GetAttachedRigidbody(in, member, argc, args, ret);
        if (strcmp(n, "set_isTrigger") == 0) return RetVoid(ret);
        if (strcmp(n, "get_isTrigger") == 0) return RetBoolV(ret, false);
        if (strcmp(n, "get_shapeCount") == 0) return RetI32V(ret, 1);
    }
    if (strcmp(type, "UnityEngine.BoxCollider2D") == 0) {
        if (strcmp(n, "get_size") == 0) return BoxCollider2D_GetSize(in, member, argc, args, ret);
        if (strcmp(n, "set_size") == 0) return BoxCollider2D_SetSize(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.CircleCollider2D") == 0) {
        if (strcmp(n, "get_radius") == 0) return CircleCollider2D_GetRadius(in, member, argc, args, ret);
        if (strcmp(n, "set_radius") == 0) return CircleCollider2D_SetRadius(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Rigidbody2D") == 0) {
        if (strcmp(n, "get_velocity") == 0) return Rigidbody2D_GetVelocity(in, member, argc, args, ret);
        if (strcmp(n, "set_velocity") == 0) return Rigidbody2D_SetVelocity(in, member, argc, args, ret);
        if (strcmp(n, "get_position") == 0) return Rigidbody2D_GetPosition(in, member, argc, args, ret);
        if (strcmp(n, "set_position") == 0) return Rigidbody2D_SetPosition(in, member, argc, args, ret);
        if (strcmp(n, "get_mass") == 0) return Rigidbody2D_GetMass(in, member, argc, args, ret);
        if (strcmp(n, "set_mass") == 0) return Rigidbody2D_SetMass(in, member, argc, args, ret);
        if (strcmp(n, "get_drag") == 0) return Rigidbody2D_GetDrag(in, member, argc, args, ret);
        if (strcmp(n, "set_drag") == 0) return Rigidbody2D_SetDrag(in, member, argc, args, ret);
        if (strcmp(n, "get_angularDrag") == 0) return Rigidbody2D_GetAngularDrag(in, member, argc, args, ret);
        if (strcmp(n, "set_angularDrag") == 0) return Rigidbody2D_SetAngularDrag(in, member, argc, args, ret);
        if (strcmp(n, "get_gravityScale") == 0) return Rigidbody2D_GetGravityScale(in, member, argc, args, ret);
        if (strcmp(n, "set_gravityScale") == 0) return Rigidbody2D_SetGravityScale(in, member, argc, args, ret);
        if (strcmp(n, "get_angularVelocity") == 0) return Rigidbody2D_GetAngularVelocity(in, member, argc, args, ret);
        if (strcmp(n, "set_angularVelocity") == 0) return Rigidbody2D_SetAngularVelocity(in, member, argc, args, ret);
        if (strcmp(n, "get_bodyType") == 0) return Rigidbody2D_GetBodyType(in, member, argc, args, ret);
        if (strcmp(n, "set_bodyType") == 0) return Rigidbody2D_SetBodyType(in, member, argc, args, ret);
        if (strcmp(n, "get_isKinematic") == 0) return Rigidbody2D_GetKinematic(in, member, argc, args, ret);
        if (strcmp(n, "set_isKinematic") == 0) return Rigidbody2D_SetKinematic(in, member, argc, args, ret);
        if (strcmp(n, "get_simulated") == 0) return Rigidbody2D_GetSimulated(in, member, argc, args, ret);
        if (strcmp(n, "set_simulated") == 0) return Rigidbody2D_SetSimulated(in, member, argc, args, ret);
        if (strcmp(n, "get_freezeRotation") == 0) return Rigidbody2D_GetFreezeRotation(in, member, argc, args, ret);
        if (strcmp(n, "set_freezeRotation") == 0) return Rigidbody2D_SetFreezeRotation(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Resources") == 0) {
        if (strcmp(n, "Load") == 0) return Resources_Load(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Physics2D") == 0) {
        if (strcmp(n, "BoxCast") == 0) return Physics2D_BoxCast(in, member, argc, args, ret);
        if (strcmp(n, "Raycast") == 0) return Physics2D_Raycast(in, member, argc, args, ret);
        if (strcmp(n, "BoxCastNonAlloc") == 0) return Physics2D_BoxCastNonAlloc(in, member, argc, args, ret);
        if (strcmp(n, "RaycastNonAlloc") == 0) return Physics2D_RaycastNonAlloc(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.RaycastHit2D") == 0) {
        if (strcmp(n, "get_collider") == 0) return Hit_GetCollider(in, member, argc, args, ret);
        if (strcmp(n, "get_point") == 0) return Hit_GetPoint(in, member, argc, args, ret);
        if (strcmp(n, "get_normal") == 0) return Hit_GetNormal(in, member, argc, args, ret);
        if (strcmp(n, "get_distance") == 0) return Hit_GetDistance(in, member, argc, args, ret);
        if (strcmp(n, "get_fraction") == 0) return Hit_GetFraction(in, member, argc, args, ret);
        if (strcmp(n, "get_transform") == 0) return Hit_GetTransform(in, member, argc, args, ret);
        if (strcmp(n, "get_rigidbody") == 0) return Hit_GetRigidbody(in, member, argc, args, ret);
        if (strcmp(n, "op_Implicit") == 0 || strcmp(n, "op_True") == 0) return Hit_IsTrue(in, member, argc, args, ret);
        if (strcmp(n, "op_False") == 0) {
            bool hit = Hit_IsTrue(in, member, argc, args, ret) && ret && ret->data.i32 != 0;
            return RetBoolV(ret, !hit);
        }
    }
    if (strcmp(type, "UnityEngine.Time") == 0) {
        if (strcmp(n, "get_fixedDeltaTime") == 0) return Time_GetFixedDelta(in, member, argc, args, ret);
        if (strcmp(n, "get_deltaTime") == 0 || strcmp(n, "get_unscaledDeltaTime") == 0) return Time_GetDelta(in, member, argc, args, ret);
        if (strcmp(n, "get_time") == 0 || strcmp(n, "get_unscaledTime") == 0 || strcmp(n, "get_fixedTime") == 0) return Time_GetTime(in, member, argc, args, ret);
        if (strcmp(n, "get_timeScale") == 0) return Time_GetTimeScale(in, member, argc, args, ret);
        if (strcmp(n, "set_timeScale") == 0) return Time_SetTimeScale(in, member, argc, args, ret);
        if (strcmp(n, "get_frameCount") == 0) return Time_GetFrameCount(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Random") == 0) {
        if (strcmp(n, "Range") == 0) return Random_Range(in, member, argc, args, ret);
        if (strcmp(n, "get_value") == 0) return Random_Value(in, member, argc, args, ret);
        if (strcmp(n, "InitState") == 0) return Random_InitState(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Color") == 0) {
        if (strcmp(n, ".ctor") == 0) return Color_Ctor(in, member, argc, args, ret);
        if (strcmp(n, "get_red") == 0) return Color_Red(in, member, argc, args, ret);
        if (strcmp(n, "get_green") == 0) return Color_Green(in, member, argc, args, ret);
        if (strcmp(n, "get_blue") == 0) return Color_Blue(in, member, argc, args, ret);
        if (strcmp(n, "get_white") == 0) return Color_White(in, member, argc, args, ret);
        if (strcmp(n, "get_black") == 0) return Color_Black(in, member, argc, args, ret);
        if (strcmp(n, "get_yellow") == 0) return Color_Yellow(in, member, argc, args, ret);
        if (strcmp(n, "get_clear") == 0) return Color_Clear(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Debug") == 0) {
        if (strcmp(n, "DrawLine") == 0 || strcmp(n, "DrawRay") == 0 ||
            strcmp(n, "Log") == 0 || strcmp(n, "LogWarning") == 0 ||
            strcmp(n, "LogError") == 0 || strcmp(n, "Assert") == 0) {
            return RetVoid(ret);
        }
    }
    if (strcmp(type, "UnityEngine.Animator") == 0) {
        if (strcmp(n, "StringToHash") == 0) return Animator_StringToHash(in, member, argc, args, ret);
        if (strcmp(n, "set_speed") == 0) return Animator_SetSpeed(in, member, argc, args, ret);
        if (strcmp(n, "get_speed") == 0) return Animator_GetSpeed(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Rect") == 0) {
        if (strcmp(n, ".ctor") == 0) return Rect_Ctor(in, member, argc, args, ret);
        if (strcmp(n, "Contains") == 0) return Rect_Contains(in, member, argc, args, ret);
        if (strcmp(n, "get_x") == 0) return Rect_GetX(in, member, argc, args, ret);
        if (strcmp(n, "set_x") == 0) return Rect_SetX(in, member, argc, args, ret);
        if (strcmp(n, "get_y") == 0) return Rect_GetY(in, member, argc, args, ret);
        if (strcmp(n, "set_y") == 0) return Rect_SetY(in, member, argc, args, ret);
        if (strcmp(n, "get_width") == 0) return Rect_GetWidth(in, member, argc, args, ret);
        if (strcmp(n, "set_width") == 0) return Rect_SetWidth(in, member, argc, args, ret);
        if (strcmp(n, "get_height") == 0) return Rect_GetHeight(in, member, argc, args, ret);
        if (strcmp(n, "set_height") == 0) return Rect_SetHeight(in, member, argc, args, ret);
        if (strcmp(n, "get_center") == 0) return Rect_GetCenter(in, member, argc, args, ret);
        if (strcmp(n, "get_size") == 0) return Rect_GetSize(in, member, argc, args, ret);
        if (strcmp(n, "get_xMin") == 0) return Rect_GetXMin(in, member, argc, args, ret);
        if (strcmp(n, "set_xMin") == 0) return Rect_SetXMin(in, member, argc, args, ret);
        if (strcmp(n, "get_xMax") == 0) return Rect_GetXMax(in, member, argc, args, ret);
        if (strcmp(n, "set_xMax") == 0) return Rect_SetXMax(in, member, argc, args, ret);
        if (strcmp(n, "get_yMin") == 0) return Rect_GetYMin(in, member, argc, args, ret);
        if (strcmp(n, "set_yMin") == 0) return Rect_SetYMin(in, member, argc, args, ret);
        if (strcmp(n, "get_yMax") == 0) return Rect_GetYMax(in, member, argc, args, ret);
        if (strcmp(n, "set_yMax") == 0) return Rect_SetYMax(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Vector3") == 0) {
        if (strcmp(n, ".ctor") == 0) return Vector3_Ctor(in, member, argc, args, ret);
        if (strcmp(n, "get_zero") == 0) return Vector3_Zero(in, member, argc, args, ret);
        if (strcmp(n, "get_one") == 0) return Vector3_One(in, member, argc, args, ret);
        if (strcmp(n, "get_up") == 0) return Vector3_Up(in, member, argc, args, ret);
        if (strcmp(n, "get_right") == 0) return Vector3_Right(in, member, argc, args, ret);
        if (strcmp(n, "op_Addition") == 0) return Vector3_Add(in, member, argc, args, ret);
        if (strcmp(n, "op_Subtraction") == 0) return Vector3_Sub(in, member, argc, args, ret);
        if (strcmp(n, "op_Multiply") == 0) return Vector3_Mul(in, member, argc, args, ret);
        if (strcmp(n, "Distance") == 0) return Vector3_Distance(in, member, argc, args, ret);
        if (strcmp(n, "op_Implicit") == 0) return Vector3_ImplicitV2(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Vector2") == 0) {
        if (strcmp(n, "op_Implicit") == 0) return Vector2_ImplicitV3(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.LayerMask") == 0) {
        if (strcmp(n, "NameToLayer") == 0) return RetI32V(ret, 0);
        if (strcmp(n, "LayerToName") == 0) {
            if (ret) *ret = in.NewStringAscii("Default");
            return true;
        }
    }
    LogError("unity", "unimplemented Unity method %s::%s (argc=%u)", type, n, argc);
    return false;
}

} // namespace ch360
