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
    return in.ManagedTransform(oi);
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

bool Interpreter::BindScene(Scene& scene) {
    scene_ = &scene;
    managed_gameobjects_.Reset();
    managed_transforms_.Reset();
    managed_components_.Reset();
    bindings_.Reset();
    if (!managed_gameobjects_.Resize(scene.ObjectCount())) return false;
    if (!managed_transforms_.Resize(scene.ObjectCount())) return false;
    if (!managed_components_.Resize(scene.ComponentCount())) return false;

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
                Value parent = GetField(sv.parent_field, Value::ObjectRef(script));
                if (parent.tag == VT_NULL || !parent.data.obj) {
                    const FieldInfo& pf = metadata_.Field(sv.parent_field);
                    const SigInfo& ps = metadata_.Sig(pf.type_sig);
                    if (ps.type_id) {
                        const TypeInfo& pt = metadata_.Type(ps.type_id);
                        parent = pt.is_value_type ? NewStructValue(ps.type_id) : NewObjectValue(ps.type_id);
                        SetField(sv.parent_field, Value::ObjectRef(script), parent);
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
    if (!in.BoundScene() || oi >= in.BoundScene()->ObjectCount()) return RetVoid(ret);
    if (ret) *ret = in.NewStringUtf8(in.BoundScene()->Object(oi).name);
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
    if (!scene || oi >= scene->ObjectCount()) return false;
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
    if (scene && oi < scene->ObjectCount()) {
        // SceneObject flags are read-only through the public API; keep the
        // active bit in the managed object's extra slot for now.
        a[0].data.obj->flags = (a[0].data.obj->flags & ~1u) | (Interpreter::ToBool(a[1]) ? 1u : 0u);
    }
    return RetVoid(ret);
}
static bool GameObject_GetActiveSelf(Interpreter&, const MemberRefInfo&, u32, Value* a, Value* ret) {
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
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    const SceneObject& o = scene->Object(oi);
    return RetVec3V(in, ret, o.world_x, o.world_y, 0.0f);
}
static bool Transform_SetPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    const Value& v = a[1];
    if (v.tag != VT_VEC3 || !v.data.ptr) return false;
    const Vec3* p = (const Vec3*)v.data.ptr;
    SceneObject& o = const_cast<SceneObject&>(scene->Object(oi));
    o.position[0] = p->x;
    o.position[1] = p->y;
    o.position[2] = p->z;
    scene->ComputeWorldTransforms();
    return RetVoid(ret);
}
static bool Transform_GetLocalPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    const SceneObject& o = scene->Object(oi);
    return RetVec3V(in, ret, o.position[0], o.position[1], o.position[2]);
}
static bool Transform_SetLocalPosition(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    if (a[1].tag != VT_VEC3 || !a[1].data.ptr) return false;
    const Vec3* p = (const Vec3*)a[1].data.ptr;
    SceneObject& o = const_cast<SceneObject&>(scene->Object(oi));
    o.position[0] = p->x;
    o.position[1] = p->y;
    o.position[2] = p->z;
    scene->ComputeWorldTransforms();
    return RetVoid(ret);
}
static bool Transform_GetLocalScale(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    const SceneObject& o = scene->Object(oi);
    return RetVec3V(in, ret, o.scale[0], o.scale[1], o.scale[2]);
}
static bool Transform_SetLocalScale(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    if (a[1].tag != VT_VEC3 || !a[1].data.ptr) return false;
    const Vec3* p = (const Vec3*)a[1].data.ptr;
    SceneObject& o = const_cast<SceneObject&>(scene->Object(oi));
    o.scale[0] = p->x;
    o.scale[1] = p->y;
    o.scale[2] = p->z;
    scene->ComputeWorldTransforms();
    return RetVoid(ret);
}
static bool Transform_GetParent(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    u32 parent = scene->Object(oi).parent;
    u32 pi = FindObjectIndexById(*scene, parent);
    if (ret) *ret = pi == 0xFFFFFFFFu ? Value::Null() : in.ManagedTransform(pi);
    return true;
}
static bool Transform_SetParent(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    u32 oi = ManagedObjectIndex(in, a[0]);
    Scene* scene = in.BoundScene();
    if (!scene || oi >= scene->ObjectCount()) return false;
    u32 pi = 0xFFFFFFFFu;
    if (a[1].tag == VT_OBJECT && a[1].data.obj) pi = ManagedObjectIndex(in, a[1]);
    SceneObject& o = const_cast<SceneObject&>(scene->Object(oi));
    o.parent = (pi == 0xFFFFFFFFu) ? 0 : scene->Object(pi).id;
    scene->ComputeWorldTransforms();
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
static bool MonoBehaviour_StartCoroutine(Interpreter&, const MemberRefInfo&, u32, Value*, Value* ret) {
    if (ret) *ret = Value::Null();
    return true;
}

static bool Vector3_Ctor(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
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
static bool Vector3_ImplicitV2(Interpreter& in, const MemberRefInfo&, u32, Value* a, Value* ret) {
    if (a[0].tag != VT_VEC3 || !a[0].data.ptr) return false;
    const Vec3* x = (const Vec3*)a[0].data.ptr;
    if (ret) *ret = in.NewVec2(x->x, x->y);
    return true;
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

    if (strcmp(type, "UnityEngine.Object") == 0 || strcmp(type, "UnityEngine.Component") == 0 ||
        strcmp(type, "UnityEngine.Behaviour") == 0 || strcmp(type, "UnityEngine.GameObject") == 0 ||
        strcmp(type, "UnityEngine.MonoBehaviour") == 0 || strcmp(type, "UnityEngine.SpriteRenderer") == 0 ||
        strcmp(type, "UnityEngine.Renderer") == 0 || strcmp(type, "UnityEngine.Animator") == 0 ||
        strcmp(type, "UnityEngine.Collider2D") == 0 || strcmp(type, "UnityEngine.BoxCollider2D") == 0 ||
        strcmp(type, "UnityEngine.CircleCollider2D") == 0 || strcmp(type, "UnityEngine.Rigidbody2D") == 0) {
        if (strcmp(n, "get_gameObject") == 0) return Component_GetGameObject(in, member, argc, args, ret);
        if (strcmp(n, "get_transform") == 0) {
            if (strcmp(type, "UnityEngine.GameObject") == 0) return GameObject_GetTransform(in, member, argc, args, ret);
            return Component_GetTransform(in, member, argc, args, ret);
        }
        if (strcmp(n, "GetComponent") == 0) return GameObject_GetComponent(in, member, argc, args, ret);
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
        if (strcmp(n, "StopCoroutine") == 0 || strcmp(n, "StopAllCoroutines") == 0 ||
            strcmp(n, "Invoke") == 0 || strcmp(n, ".ctor") == 0) {
            return MonoBehaviour_NoOp(in, member, argc, args, ret);
        }
        if (strcmp(n, "op_Equality") == 0) return RetBoolV(ret, args[0].data.obj == args[1].data.obj);
        if (strcmp(n, "op_Inequality") == 0) return RetBoolV(ret, args[0].data.obj != args[1].data.obj);
        if (strcmp(n, "op_Implicit") == 0) return RetBoolV(ret, args[0].data.obj != 0);
        if (strcmp(n, "Destroy") == 0 || strcmp(n, "DontDestroyOnLoad") == 0) return RetVoid(ret);
        if (strcmp(n, "Play") == 0 || strcmp(n, "SetTrigger") == 0 || strcmp(n, "SetBool") == 0 ||
            strcmp(n, "SetInteger") == 0 || strcmp(n, "SetFloat") == 0) {
            return RetVoid(ret);
        }
    }
    if (strcmp(type, "UnityEngine.Transform") == 0) {
        if (strcmp(n, "get_position") == 0) return Transform_GetPosition(in, member, argc, args, ret);
        if (strcmp(n, "set_position") == 0) return Transform_SetPosition(in, member, argc, args, ret);
        if (strcmp(n, "get_localPosition") == 0) return Transform_GetLocalPosition(in, member, argc, args, ret);
        if (strcmp(n, "set_localPosition") == 0) return Transform_SetLocalPosition(in, member, argc, args, ret);
        if (strcmp(n, "get_localScale") == 0) return Transform_GetLocalScale(in, member, argc, args, ret);
        if (strcmp(n, "set_localScale") == 0) return Transform_SetLocalScale(in, member, argc, args, ret);
        if (strcmp(n, "get_parent") == 0) return Transform_GetParent(in, member, argc, args, ret);
        if (strcmp(n, "set_parent") == 0 || strcmp(n, "SetParent") == 0) return Transform_SetParent(in, member, argc, args, ret);
        if (strcmp(n, "get_localEulerAngles") == 0 || strcmp(n, "get_eulerAngles") == 0) {
            return RetVec3V(in, ret, 0.0f, 0.0f, 0.0f);
        }
        if (strcmp(n, "set_localEulerAngles") == 0 || strcmp(n, "set_eulerAngles") == 0) {
            return RetVoid(ret);
        }
        if (strcmp(n, "get_gameObject") == 0) return Component_GetGameObject(in, member, argc, args, ret);
        if (strcmp(n, "get_name") == 0) return Object_GetName(in, member, argc, args, ret);
    }
    if (strcmp(type, "UnityEngine.Collider2D") == 0 || strcmp(type, "UnityEngine.BoxCollider2D") == 0 ||
        strcmp(type, "UnityEngine.CircleCollider2D") == 0) {
        if (strcmp(n, "get_offset") == 0) return Collider2D_GetOffset(in, member, argc, args, ret);
        if (strcmp(n, "set_offset") == 0) return Collider2D_SetOffset(in, member, argc, args, ret);
        if (strcmp(n, "get_attachedRigidbody") == 0) return Collider2D_GetAttachedRigidbody(in, member, argc, args, ret);
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
    if (strcmp(type, "UnityEngine.Vector3") == 0) {
        if (strcmp(n, ".ctor") == 0) return Vector3_Ctor(in, member, argc, args, ret);
        if (strcmp(n, "get_zero") == 0) return Vector3_Zero(in, member, argc, args, ret);
        if (strcmp(n, "get_one") == 0) return Vector3_One(in, member, argc, args, ret);
        if (strcmp(n, "get_up") == 0) return Vector3_Up(in, member, argc, args, ret);
        if (strcmp(n, "get_right") == 0) return Vector3_Right(in, member, argc, args, ret);
        if (strcmp(n, "op_Addition") == 0) return Vector3_Add(in, member, argc, args, ret);
        if (strcmp(n, "op_Subtraction") == 0) return Vector3_Sub(in, member, argc, args, ret);
        if (strcmp(n, "op_Multiply") == 0) return Vector3_Mul(in, member, argc, args, ret);
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
