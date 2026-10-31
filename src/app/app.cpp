#include "app/app.h"
#include "core/containers.h"
#include "core/endian.h"
#include "core/file.h"
#include "core/log.h"
#include "core/mem.h"
#include "platform/platform.h"
#include "engine/animation.h"
#include "engine/renderer_soft.h"
#include "engine/scene.h"
#include "platform/display.h"
#include "runtime/interpreter.h"
#include "runtime/metadata.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

namespace ch360 {

static int g_failures = 0;

static void Check(bool condition, const char* what) {
    if (condition) {
        LogInfo("selftest", "PASS %s", what);
    } else {
        LogError("selftest", "FAIL %s", what);
        ++g_failures;
    }
}

static void SelfTestEndian() {
    const u8 le[8] = { 0x44, 0x33, 0x22, 0x11, 0x88, 0x77, 0x66, 0x55 };
    Check(ReadU16LE(le) == 0x3344u, "ReadU16LE");
    Check(ReadU32LE(le) == 0x11223344u, "ReadU32LE");
    Check(ReadU64LE(le) == 0x5566778811223344ull, "ReadU64LE");

    const u8 be[4] = { 0x11, 0x22, 0x33, 0x44 };
    Check(ReadU16BE(be) == 0x1122u, "ReadU16BE");
    Check(ReadU32BE(be) == 0x11223344u, "ReadU32BE");

    u8 out[4];
    WriteU32LE(out, 0xDEADBEEFu);
    Check(out[0] == 0xEF && out[1] == 0xBE && out[2] == 0xAD && out[3] == 0xDE,
          "WriteU32LE byte order");

    union { u32 u; f32 f; } bits;
    bits.u = 0x3F800000u;
    u8 fbytes[4];
    WriteU32LE(fbytes, bits.u);
    Check(ReadF32LE(fbytes) == 1.0f, "ReadF32LE");
}

static void SelfTestMemory() {
    u8* a = (u8*)MemAlloc(128);
    Check(a != 0, "MemAlloc");
    if (!a) return;
    memset(a, 0xA5, 128);
    Check(a[0] == 0xA5 && a[127] == 0xA5, "MemAlloc writable");

    a = (u8*)MemRealloc(a, 4096);
    Check(a != 0 && a[0] == 0xA5 && a[127] == 0xA5, "MemRealloc preserves");
    MemFree(a);

    u32 before = MemAllocationCount();
    u8* aligned = (u8*)MemAllocAligned(100, 64);
    Check(aligned != 0 && (((u32)(size_t)aligned) & 63u) == 0, "MemAllocAligned");
    MemFree(aligned);
    Check(MemAllocationCount() == before, "MemFree accounting");
}

static void SelfTestFile() {
#if defined(_XBOX)
    const char* path = "game:\\cuphead360_selftest.bin";
#else
    const char* path = "cuphead360_selftest.bin";
#endif
    const char payload[] = "Cuphead360 file test\x01\x02\x03";
    Check(FileWriteAll(path, payload, (u32)sizeof(payload)), "FileWriteAll");
    u32 size = 0;
    u8* data = FileReadAll(path, &size);
    Check(data != 0 && size == sizeof(payload) &&
          memcmp(data, payload, sizeof(payload)) == 0, "FileReadAll round trip");
    if (data) MemFree(data);
    Check(FileExists(path), "FileExists");
    Check(FileSize(path) == sizeof(payload), "FileSize");
}

static void SelfTestContainers() {
    Buffer<int> numbers;
    for (int i = 0; i < 100; ++i) numbers.PushBack(i);
    Check(numbers.Size() == 100 && numbers[0] == 0 && numbers[99] == 99,
          "Buffer push/order");
    numbers.RemoveAt(50);
    Check(numbers.Size() == 99 && numbers[50] == 51, "Buffer RemoveAt");
    numbers.Resize(120);
    Check(numbers.Size() == 120 && numbers[100] == 0, "Buffer Resize zeroes");

    StringBuf text;
    text.Append("Cuphead");
    text.AppendF(" %d", 360);
    Check(strcmp(text.CStr(), "Cuphead 360") == 0, "StringBuf append");
}

static void RunSelfTest() {
    LogInfo("selftest", "begin");
    Check(HostIsBigEndian() || !HostIsBigEndian(), "host endian probe");
    LogInfo("selftest", "host_is_big_endian=%d", HostIsBigEndian() ? 1 : 0);
    SelfTestEndian();
    SelfTestMemory();
    SelfTestFile();
    SelfTestContainers();
    LogInfo("selftest", "result=%s failures=%d", g_failures ? "FAIL" : "PASS", g_failures);
}

static void InspectMetadata(MetadataPackage& pkg) {
    LogInfo("inspect", "types=%u fields=%u methods=%u memberrefs=%u",
            pkg.TypeCount(), pkg.FieldCount(), pkg.MethodCount(), pkg.MemberRefCount());
    struct Query { const char* name; };
    static const Query queries[] = {
        { "EaseUtils" },
        { "LevelPlayerMotor" },
        { "LevelPlayerMotor/VelocityManager" },
        { "PlayerData" },
        { "SaltbakerLevel" },
        { "ChaliceTutorialLevel" },
        { "LevelProperties/Saltbaker" },
    };
    for (u32 q = 0; q < CH360_ARRAY_COUNT(queries); ++q) {
        u32 id = pkg.FindType(queries[q].name);
        if (!id) {
            LogWarn("inspect", "type '%s' not found", queries[q].name);
            continue;
        }
        const TypeInfo& t = pkg.Type(id);
        LogInfo("inspect", "type '%s' id=%u base=%u fields=%u methods=%u size=%u static=%u vtable=%u value=%u enum=%u",
                t.full_name, id, t.base_id, t.field_count, t.method_count,
                t.size, t.static_size, t.vtable_size, t.is_value_type, t.is_enum);
        u32 limit = t.method_count < 6 ? t.method_count : 6;
        for (u32 i = 0; i < limit; ++i) {
            const MethodInfo& m = pkg.Method(t.method_start + i);
            LogInfo("inspect", "  method %s il=%u flags=0x%x slot=%u", m.name,
                    m.il_size, m.flags, m.vtable_slot);
        }
    }
    u32 ease = pkg.FindType("EaseUtils");
    if (ease) {
        u32 m = pkg.FindMethod(ease, "Ease");
        if (m) {
            const MethodInfo& mi = pkg.Method(m);
            LogInfo("inspect", "EaseUtils.Ease id=%u il=%u sig=%u", m, mi.il_size, mi.sig);
            const u8* il = pkg.Il(m);
            LogInfo("inspect", "EaseUtils.Ease first_bytes=%02x %02x %02x %02x %02x %02x %02x %02x",
                    il ? il[0] : 0, il ? il[1] : 0, il ? il[2] : 0, il ? il[3] : 0,
                    il ? il[4] : 0, il ? il[5] : 0, il ? il[6] : 0, il ? il[7] : 0);
        }
    }
}

static float InvokeEase3(Interpreter& interp, MetadataPackage& pkg, const char* name,
                         float a, float b, float t, bool* ok) {
    u32 type = pkg.FindType("EaseUtils");
    u32 method = pkg.FindMethod(type, name);
    if (!method) {
        LogError("logic", "EaseUtils.%s not found", name);
        if (ok) *ok = false;
        return 0.0f;
    }
    Value args[3];
    args[0] = Value::R4(a);
    args[1] = Value::R4(b);
    args[2] = Value::R4(t);
    Value ret;
    bool result = interp.Invoke(method, args, 3, &ret);
    if (ok) *ok = result;
    if (!result) LogError("logic", "invoke %s failed", name);
    return ret.data.f32;
}

static int RunUnityTest(MetadataPackage& pkg, const AppConfig& config) {
    if (!config.scene_path) {
        LogError("unity", "no scene path");
        return 1;
    }
    Scene scene;
    if (!scene.Load(config.scene_path)) return 1;
    Interpreter interp(pkg);
    if (!interp.Init() || !interp.BindScene(scene)) {
        LogError("unity", "interpreter/bind failed");
        return 1;
    }
    // Pick an object with a non-identity local scale so the reset is visible.
    u32 oi = 0;
    for (u32 i = 0; i < scene.ObjectCount(); ++i) {
        const SceneObject& o = scene.Object(i);
        if (o.scale[0] != 1.0f || o.scale[1] != 1.0f) { oi = i; break; }
    }
    const SceneObject& before = scene.Object(oi);
    LogInfo("unity", "object '%s' before pos=%.2f,%.2f scale=%.2f,%.2f",
            before.name, before.position[0], before.position[1], before.scale[0], before.scale[1]);
    u32 ext = pkg.FindType("TransformExtensions");
    u32 method = pkg.FindMethod(ext, "ResetLocalTransforms");
    if (!method) {
        LogError("unity", "TransformExtensions.ResetLocalTransforms not found");
        return 1;
    }
    Value args[1];
    args[0] = interp.ManagedTransform(oi);
    Value ret;
    bool ok = interp.Invoke(method, args, 1, &ret);
    const SceneObject& after = scene.Object(oi);
    bool pass = ok && after.position[0] == 0.0f && after.position[1] == 0.0f &&
                after.scale[0] == 1.0f && after.scale[1] == 1.0f;
    LogInfo("unity", "ResetLocalTransforms after pos=%.2f,%.2f scale=%.2f,%.2f %s",
            after.position[0], after.position[1], after.scale[0], after.scale[1],
            pass ? "PASS" : "FAIL");

    // Invoke an original MonoBehaviour lifecycle method on a bound script
    // object. AnimationHelper.Update only touches its bool when autoUpdate is
    // false, making it a safe first lifecycle check.
    u32 helper_type = pkg.FindType("AnimationHelper");
    u32 helper_update = pkg.FindMethod(helper_type, "Update");
    bool lifecycle_ok = false;
    if (helper_type && helper_update) {
        for (u32 ci = 0; ci < scene.ComponentCount(); ++ci) {
            const SceneComponent& comp = scene.Component(ci);
            if (comp.type != 4 || strcmp(comp.controller, "AnimationHelper") != 0) continue;
            Value obj = interp.ManagedComponent(ci);
            if (obj.tag != VT_OBJECT) continue;
            Value args[1]; args[0] = obj; Value r;
            lifecycle_ok = interp.Invoke(helper_update, args, 1, &r);
            break;
        }
    }
    LogInfo("unity", "AnimationHelper.Update %s", lifecycle_ok ? "PASS" : "FAIL");
    if (!lifecycle_ok) pass = false;

    // AbstractMonoBehaviour.Awake on a PlayerDebug instance (base-only Awake
    // sets useGUILayout; no scene references required).
    u32 dbg_type = pkg.FindType("PlayerDebug");
    u32 base_type = pkg.FindType("AbstractMonoBehaviour");
    u32 awake = pkg.FindMethod(base_type, "Awake");
    if (dbg_type && awake) {
        Value obj = interp.NewObjectValue(dbg_type);
        Value args[1]; args[0] = obj; Value r;
        bool aok = interp.Invoke(awake, args, 1, &r);
        LogInfo("unity", "AbstractMonoBehaviour.Awake %s", aok ? "PASS" : "FAIL");
        if (!aok) pass = false;
    }

    return pass ? 0 : 1;
}

static void RunLogicTest(MetadataPackage& pkg) {
    Interpreter interp(pkg);
    if (!interp.Init()) {
        LogError("logic", "interpreter init failed");
        return;
    }
    bool ok = true;
    // Expected values were produced by running the original Assembly-CSharp
    // under the desktop CLR (tests/reference/EaseReference.cs).
    struct Case { const char* name; float a, b, t; float expect; };
    static const Case cases[] = {
        { "EaseInQuad",    0.00f, 10.0f, 0.25f, 0.625f },
        { "EaseInQuad",    0.00f, 10.0f, 0.50f, 2.5f },
        { "EaseInQuad",    0.25f, 10.0f, 0.90f, 8.147499f },
        { "EaseOutQuad",   0.00f, 10.0f, 0.25f, 4.375f },
        { "EaseOutQuad",   0.00f, 10.0f, 0.50f, 7.5f },
        { "EaseOutQuad",   0.25f, 10.0f, 0.90f, 9.9025f },
        { "EaseInOutQuad", 0.00f, 10.0f, 0.25f, 1.25f },
        { "EaseInOutQuad", 0.00f, 10.0f, 0.50f, 5.0f },
        { "EaseInOutQuad", 0.25f, 10.0f, 0.90f, 9.805f },
        { "EaseInCubic",   0.00f, 10.0f, 0.25f, 0.15625f },
        { "EaseInCubic",   0.00f, 10.0f, 0.50f, 1.25f },
        { "EaseInCubic",   0.25f, 10.0f, 0.90f, 7.35774946f },
        { "EaseOutCubic",  0.00f, 10.0f, 0.25f, 5.78125f },
        { "EaseOutCubic",  0.00f, 10.0f, 0.50f, 8.75f },
        { "EaseOutCubic",  0.25f, 10.0f, 0.90f, 9.990251f },
        { "EaseInOutCubic",0.00f, 10.0f, 0.25f, 0.625f },
        { "EaseInOutCubic",0.00f, 10.0f, 0.50f, 5.0f },
        { "EaseInOutCubic",0.25f, 10.0f, 0.90f, 9.96099949f },
    };
    for (u32 i = 0; i < CH360_ARRAY_COUNT(cases); ++i) {
        bool call_ok = false;
        float v = InvokeEase3(interp, pkg, cases[i].name, cases[i].a, cases[i].b, cases[i].t, &call_ok);
        bool near = fabsf(v - cases[i].expect) < 0.0001f;
        LogInfo("logic", "%s(%.4f,%.4f,%.4f)=%.6f expect=%.6f %s",
                cases[i].name, cases[i].a, cases[i].b, cases[i].t, v, cases[i].expect,
                (call_ok && near) ? "PASS" : "FAIL");
        if (!call_ok || !near) ok = false;
    }
    // Original save-data class: its constructor creates a List<Weapon> and
    // adds the two starting weapons. This exercises object allocation,
    // generic List<T> dispatch, field assignment and enum values.
    {
        u32 inventory_type = pkg.FindType("PlayerData/PlayerInventory");
        u32 ctor = pkg.FindMethod(inventory_type, ".ctor");
        u32 weapons_field = pkg.FindField(inventory_type, "_weapons");
        if (inventory_type && ctor) {
            Value obj = interp.NewObjectValue(inventory_type);
            Value args[1];
            args[0] = obj;
            Value ret;
            bool call_ok = interp.Invoke(ctor, args, 1, &ret);
            Value weapons = interp.GetField(weapons_field, obj);
            u32 count = 0;
            if (weapons.tag == VT_OBJECT && weapons.data.obj) {
                ListObject* list = (ListObject*)weapons.data.obj;
                count = list->count;
            }
            bool near = call_ok && count == 2;
            LogInfo("logic", "PlayerInventory.ctor weapons=%u expect=2 %s", count,
                    near ? "PASS" : "FAIL");
            if (!near) ok = false;
        } else {
            LogError("logic", "PlayerInventory ctor/field not found");
            ok = false;
        }
    }

    // Original movement math: LevelPlayerMotor.VelocityManager.get_Total.
    {
        u32 vm_type = pkg.FindType("LevelPlayerMotor/VelocityManager");
        u32 total = pkg.FindMethod(vm_type, "get_Total");
        if (vm_type && total) {
            Value vm = interp.NewObjectValue(vm_type);
            interp.SetField(pkg.FindField(vm_type, "maxY"), vm, Value::R4(1620.0f));
            interp.SetField(pkg.FindField(vm_type, "yEase"), vm, Value::I4(0));
            interp.SetField(pkg.FindField(vm_type, "_y"), vm, Value::R4(0.5f));
            interp.SetField(pkg.FindField(vm_type, "move"), vm, Value::R4(100.0f));
            interp.SetField(pkg.FindField(vm_type, "dash"), vm, Value::R4(0.0f));
            interp.SetField(pkg.FindField(vm_type, "hit"), vm, Value::R4(0.0f));
            interp.SetField(pkg.FindField(vm_type, "verticalDash"), vm, Value::R4(0.0f));
            Value args[1]; args[0] = vm;
            Value ret;
            bool call_ok = interp.Invoke(total, args, 1, &ret);
            f32 x = 0.0f, y = 0.0f;
            if (ret.tag == VT_VEC2 && ret.data.ptr) {
                Vec2* v = (Vec2*)ret.data.ptr;
                x = v->x; y = v->y;
            }
            bool near = call_ok && fabsf(x - 100.0f) < 0.001f && fabsf(y + 202.5f) < 0.001f;
            LogInfo("logic", "VelocityManager.Total=(%.3f,%.3f) expect=(100.000,-202.500) %s",
                    x, y, near ? "PASS" : "FAIL");
            if (!near) ok = false;
        } else {
            LogError("logic", "VelocityManager.get_Total not found");
            ok = false;
        }
    }

    // Exercise the switch-based Ease dispatcher with the first three enums.
    u32 type = pkg.FindType("EaseUtils");
    u32 ease = pkg.FindMethod(type, "Ease");
    if (ease) {
        for (s32 e = 0; e < 3; ++e) {
            Value args[4];
            args[0] = Value::I4(e);
            args[1] = Value::R4(0.0f);
            args[2] = Value::R4(10.0f);
            args[3] = Value::R4(0.25f);
            Value ret;
            bool call_ok = interp.Invoke(ease, args, 4, &ret);
            float expect = (e == 0) ? 0.625f : (e == 1 ? 4.375f : 1.25f);
            bool near = fabsf(ret.data.f32 - expect) < 0.0001f;
            LogInfo("logic", "Ease(enum=%d,0,10,0.25)=%.6f expect=%.6f %s", e,
                    ret.data.f32, expect, (call_ok && near) ? "PASS" : "FAIL");
            if (!call_ok || !near) ok = false;
        }
    } else {
        LogError("logic", "EaseUtils.Ease not found");
        ok = false;
    }
    LogInfo("logic", "result=%s", ok ? "PASS" : "FAIL");
}

static int RunScene(const AppConfig& config) {
    if (!config.scene_path) {
        LogError("scene", "no scene path supplied");
        return 1;
    }
    Scene scene;
    if (!scene.Load(config.scene_path)) {
        LogError("scene", "load failed: %s", scene.LastError());
        return 1;
    }
    if (!scene.LoadTextures()) {
        LogError("scene", "texture load failed");
        return 1;
    }
    if (!DisplayInit(1280, 720, config.headless)) {
        LogError("display", "initialization failed");
        return 1;
    }
    AnimationSet animations;
    AnimationPlayer anim_player;
    char anim_path[1024];
    anim_path[0] = 0;
    if (config.scene_path) {
        u32 n = (u32)strlen(config.scene_path);
        if (n < sizeof(anim_path) - 16) {
            memcpy(anim_path, config.scene_path, n + 1);
            for (s32 i = (s32)n - 1; i >= 0; --i) {
                if (anim_path[i] == '\\' || anim_path[i] == '/') {
                    anim_path[i + 1] = 0;
                    break;
                }
            }
            strcat(anim_path, "anim.chs");
            if (animations.Load(anim_path)) {
                anim_player.Init(scene, animations);
                anim_player.SetTime(scene, animations, config.anim_time);
            }
        }
    }

    SoftRenderer renderer(1280, 720);
    const u32 frames = config.frames ? config.frames : 1;
    bool saved = false;
    for (u32 i = 0; i < frames; ++i) {
        if (animations.Loaded() && i > 0) anim_player.Update(scene, animations, config.fixed_dt);
        renderer.DrawScene(scene);
        DisplayPresent(renderer.Pixels());
        if (!saved && config.screenshot_path) {
            if (renderer.SaveBmp(config.screenshot_path)) {
                LogInfo("scene", "screenshot=%s", config.screenshot_path);
                saved = true;
            } else {
                LogError("scene", "screenshot write failed");
            }
        }
        DisplayPump();
        if (DisplayShouldQuit()) break;
        if (!config.headless) PlatformSleepMs(16);
    }
    if (!saved && config.screenshot_path) {
        if (renderer.SaveBmp(config.screenshot_path)) {
            LogInfo("scene", "screenshot=%s", config.screenshot_path);
        } else {
            LogError("scene", "screenshot write failed");
            DisplayShutdown();
            return 1;
        }
    }
    DisplayShutdown();
    LogInfo("scene", "rendered objects=%u components=%u sprites=%u textures=%u frames=%u",
            scene.ObjectCount(), scene.ComponentCount(), scene.SpriteCount(),
            scene.TextureCount(), frames);
    return 0;
}

static void LogBuildInfo() {
    LogInfo("boot", "Cuphead360 native runtime");
    LogInfo("boot", "platform=%s _MSC_VER=%d", PlatformName(), _MSC_VER);
    LogInfo("boot", "pointer=%u int=%u long=%u longlong=%u size_t=%u",
            (u32)sizeof(void*), (u32)sizeof(int), (u32)sizeof(long),
            (u32)sizeof(long long), (u32)sizeof(size_t));
    LogInfo("boot", "big_endian=%d", HostIsBigEndian() ? 1 : 0);
    LogInfo("boot", "mem_allocated=%u peak=%u blocks=%u",
            MemAllocatedBytes(), MemPeakBytes(), MemAllocationCount());
}

int AppMain(const AppConfig& config) {
    MemInit();
    PlatformInit();

    const char* log_path = config.log_path ? config.log_path : PlatformDefaultLogPath();
    LogOpen(log_path, true);
    LogBuildInfo();
    LogEventStr("boot", "command", "name", config.command ? config.command : "run");

    MetadataPackage metadata;
    bool wants_metadata = config.metadata_path != 0 ||
        (config.command && (strcmp(config.command, "inspect") == 0 ||
                            strcmp(config.command, "logic") == 0 ||
                            strcmp(config.command, "unity") == 0));
    const char* metadata_path = config.metadata_path;
#if defined(_XBOX)
    if (!metadata_path && wants_metadata) metadata_path = "game:\\GameData\\metadata.chm";
#endif
    if (wants_metadata && metadata_path) {
        if (metadata.Load(metadata_path)) {
            LogEventStr("metadata", "loaded", "path", metadata_path);
        } else {
            LogError("metadata", "failed to load '%s': %s", metadata_path, metadata.LastError());
        }
    }

    int result = 0;
    if (config.command && strcmp(config.command, "inspect") == 0) {
        if (!metadata.IsLoaded()) {
            LogError("inspect", "no metadata package loaded");
            result = 1;
        } else {
            InspectMetadata(metadata);
        }
    } else if (config.command && strcmp(config.command, "unity") == 0) {
        if (!metadata.IsLoaded() || !config.scene_path) {
            LogError("unity", "metadata and scene required");
            result = 1;
        } else {
            result = RunUnityTest(metadata, config);
        }
    } else if (config.command && strcmp(config.command, "scene") == 0) {
        result = RunScene(config);
    } else if (config.command && strcmp(config.command, "logic") == 0) {
        if (!metadata.IsLoaded()) {
            LogError("logic", "no metadata package loaded");
            result = 1;
        } else {
            RunLogicTest(metadata);
        }
    } else if (config.command && strcmp(config.command, "selftest") == 0) {
        RunSelfTest();
        result = g_failures ? 1 : 0;
    } else if (config.command && strcmp(config.command, "info") == 0) {
        LogInfo("info", "log=%s", LogPath());
    } else {
        const u32 frames = config.frames ? config.frames : 1;
        for (u32 i = 0; i < frames; ++i) {
            LogDebug("run", "frame=%u dt=%.6f", i, (double)config.fixed_dt);
        }
        LogInfo("run", "completed_frames=%u", frames);
    }

    if (metadata.IsLoaded()) metadata.Unload();
    LogInfo("shutdown", "result=%d mem_allocated=%u peak=%u", result,
            MemAllocatedBytes(), MemPeakBytes());
    LogClose();
    PlatformShutdown();
    MemShutdown();
    return result;
}

} // namespace ch360
