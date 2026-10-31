#include "engine/scene.h"
#include "core/endian.h"
#include "core/file.h"
#include "core/log.h"
#include "core/mem.h"

#include <math.h>
#include <string.h>

namespace ch360 {

static const u32 kSceneMagic = 0x43534843u; // "CHSC"
static const u32 kSceneHeaderWords = 32;

static void SplitDirectory(const char* path, char* out, u32 out_size) {
    out[0] = 0;
    if (!path) return;
    u32 n = (u32)strlen(path);
    if (n >= out_size) n = out_size - 1;
    memcpy(out, path, n);
    out[n] = 0;
    for (s32 i = (s32)n - 1; i >= 0; --i) {
        if (out[i] == '\\' || out[i] == '/') {
            out[i] = 0;
            return;
        }
    }
    out[0] = 0;
}

Scene::Scene()
    : file_(0), file_size_(0), last_error_("not loaded"), strings_(0),
      strings_size_(0) {
    directory_[0] = 0;
}

Scene::~Scene() {
    Unload();
}

bool Scene::Fail(const char* message) {
    last_error_ = message;
    LogError("scene", "%s", message);
    return false;
}

const char* Scene::Str(u32 off) const {
    if (!strings_ || off >= strings_size_) return "";
    return (const char*)(strings_ + off);
}

const SceneSprite& Scene::Sprite(u32 id) const {
    static SceneSprite empty;
    if (id == 0 || id > sprites_.Size()) return empty;
    return sprites_[id - 1];
}

const SceneTexture& Scene::Texture(u32 id) const {
    static SceneTexture empty;
    if (id == 0 || id > textures_.Size()) return empty;
    return textures_[id - 1];
}

const SceneCamera* Scene::MainCamera() const {
    if (cameras_.Empty()) return 0;
    return &cameras_[0];
}

void Scene::Unload() {
    for (u32 i = 0; i < textures_.Size(); ++i) {
        if (textures_[i].owned) MemFree(textures_[i].owned);
    }
    textures_.Reset();
    objects_.Reset();
    components_.Reset();
    sprites_.Reset();
    cameras_.Reset();
    MemFree(file_);
    file_ = 0;
    file_size_ = 0;
    strings_ = 0;
    strings_size_ = 0;
    last_error_ = "not loaded";
}

bool Scene::Load(const char* path) {
    Unload();
    SplitDirectory(path, directory_, sizeof(directory_));
    file_ = FileReadAll(path, &file_size_);
    if (!file_) return Fail("cannot read scene file");
    if (file_size_ < kSceneHeaderWords * 4) return Fail("scene file too small");
    if (ReadU32LE(file_) != kSceneMagic) return Fail("bad scene magic");
    if (ReadU32LE(file_ + 4) != 1) return Fail("unsupported scene version");

    u32 go_count = ReadU32LE(file_ + 12);
    u32 sprite_count = ReadU32LE(file_ + 16);
    u32 texture_count = ReadU32LE(file_ + 20);
    u32 camera_count = ReadU32LE(file_ + 24);
    u32 component_count = ReadU32LE(file_ + 28);
    u32 strings_off = ReadU32LE(file_ + 32);
    u32 gos_off = ReadU32LE(file_ + 36);
    u32 sprites_off = ReadU32LE(file_ + 40);
    u32 textures_off = ReadU32LE(file_ + 44);
    u32 cameras_off = ReadU32LE(file_ + 48);
    u32 components_off = ReadU32LE(file_ + 52);
    u32 strings_size = ReadU32LE(file_ + 56);

    if (strings_off > file_size_ || strings_size > file_size_ - strings_off) return Fail("bad string section");
    if (go_count != 0 && (gos_off > file_size_ || go_count > (file_size_ - gos_off) / 64)) return Fail("bad object section");
    if (sprite_count != 0 && (sprites_off > file_size_ || sprite_count > (file_size_ - sprites_off) / 64)) return Fail("bad sprite section");
    if (texture_count != 0 && (textures_off > file_size_ || texture_count > (file_size_ - textures_off) / 24)) return Fail("bad texture section");
    if (camera_count != 0 && (cameras_off > file_size_ || camera_count > (file_size_ - cameras_off) / 64)) return Fail("bad camera section");
    if (component_count != 0 && (components_off > file_size_ || component_count > (file_size_ - components_off) / 48)) return Fail("bad component section");

    strings_ = file_ + strings_off;
    strings_size_ = strings_size;

    if (!objects_.Resize(go_count)) return Fail("out of memory for objects");
    for (u32 i = 0; i < go_count; ++i) {
        const u8* p = file_ + gos_off + i * 64;
        SceneObject& o = objects_[i];
        o.id = ReadU32LE(p + 0);
        o.parent = ReadU32LE(p + 4);
        o.name = Str(ReadU32LE(p + 8));
        o.flags = ReadU32LE(p + 12);
        for (u32 j = 0; j < 3; ++j) o.position[j] = ReadF32LE(p + 16 + j * 4);
        for (u32 j = 0; j < 4; ++j) o.rotation[j] = ReadF32LE(p + 28 + j * 4);
        for (u32 j = 0; j < 3; ++j) o.scale[j] = ReadF32LE(p + 44 + j * 4);
        o.component_start = ReadU32LE(p + 56);
        o.component_count = ReadU32LE(p + 60);
        o.world_x = o.position[0];
        o.world_y = o.position[1];
        o.world_rotation = 0.0f;
        o.world_scale_x = o.scale[0];
        o.world_scale_y = o.scale[1];
    }

    if (!components_.Resize(component_count)) return Fail("out of memory for components");
    for (u32 i = 0; i < component_count; ++i) {
        const u8* p = file_ + components_off + i * 48;
        SceneComponent& c = components_[i];
        c.type = ReadU32LE(p + 0);
        c.flags = ReadU32LE(p + 4);
        c.sprite_id = ReadU32LE(p + 8);
        c.controller_off = ReadU32LE(p + 12);
        c.controller = Str(c.controller_off);
        c.sorting_order = ReadS32LE(p + 16);
        c.sorting_layer = ReadS32LE(p + 20);
        for (u32 j = 0; j < 4; ++j) c.color[j] = ReadF32LE(p + 24 + j * 4);
    }

    if (!sprites_.Resize(sprite_count)) return Fail("out of memory for sprites");
    for (u32 i = 0; i < sprite_count; ++i) {
        const u8* p = file_ + sprites_off + i * 64;
        SceneSprite& s = sprites_[i];
        s.name = Str(ReadU32LE(p + 0));
        s.texture_id = ReadU32LE(p + 4);
        s.texture_width = ReadU32LE(p + 8);
        s.texture_height = ReadU32LE(p + 12);
        for (u32 j = 0; j < 4; ++j) s.rect[j] = ReadF32LE(p + 16 + j * 4);
        s.pivot[0] = ReadF32LE(p + 32);
        s.pivot[1] = ReadF32LE(p + 36);
        s.pixels_per_unit = ReadF32LE(p + 40);
        s.world_width = ReadF32LE(p + 44);
        s.world_height = ReadF32LE(p + 48);
        s.offset[0] = ReadF32LE(p + 52);
        s.offset[1] = ReadF32LE(p + 56);
    }

    if (!textures_.Resize(texture_count)) return Fail("out of memory for textures");
    for (u32 i = 0; i < texture_count; ++i) {
        const u8* p = file_ + textures_off + i * 24;
        SceneTexture& t = textures_[i];
        t.name = Str(ReadU32LE(p + 0));
        t.file = Str(ReadU32LE(p + 4));
        t.width = ReadU32LE(p + 8);
        t.height = ReadU32LE(p + 12);
        t.format = ReadU32LE(p + 16);
        t.pixels = 0;
        t.owned = 0;
        t.data_size = 0;
    }

    if (!cameras_.Resize(camera_count)) return Fail("out of memory for cameras");
    for (u32 i = 0; i < camera_count; ++i) {
        const u8* p = file_ + cameras_off + i * 64;
        SceneCamera& c = cameras_[i];
        c.go_id = ReadU32LE(p + 0);
        c.flags = ReadU32LE(p + 4);
        c.orthographic_size = ReadF32LE(p + 8);
        c.field_of_view = ReadF32LE(p + 12);
        c.near_clip = ReadF32LE(p + 16);
        c.far_clip = ReadF32LE(p + 20);
        for (u32 j = 0; j < 4; ++j) c.background[j] = ReadF32LE(p + 24 + j * 4);
        c.depth = ReadF32LE(p + 40);
        for (u32 j = 0; j < 3; ++j) c.position[j] = ReadF32LE(p + 44 + j * 4);
    }

    ComputeWorldTransforms();
    LogInfo("scene", "loaded %s objects=%u components=%u sprites=%u textures=%u cameras=%u",
            path, objects_.Size(), components_.Size(), sprites_.Size(),
            textures_.Size(), cameras_.Size());
    last_error_ = "ok";
    return true;
}

bool Scene::LoadTextures() {
    if (!file_) return false;
    for (u32 i = 0; i < textures_.Size(); ++i) {
        SceneTexture& t = textures_[i];
        if (!t.file || !t.file[0]) continue;
        char path[1024];
        if (directory_[0]) {
            _snprintf(path, sizeof(path) - 1, "%s/%s", directory_, t.file);
        } else {
            _snprintf(path, sizeof(path) - 1, "%s", t.file);
        }
        path[sizeof(path) - 1] = 0;
        u32 size = 0;
        u8* data = FileReadAll(path, &size);
        if (!data) {
            LogError("scene", "missing texture %s", path);
            return false;
        }
        if (size < 16 || memcmp(data, "CHTX", 4) != 0) {
            MemFree(data);
            LogError("scene", "bad texture header %s", path);
            return false;
        }
        u32 w = ReadU32LE(data + 4);
        u32 h = ReadU32LE(data + 8);
        u32 fmt = ReadU32LE(data + 12);
        if (fmt != 1 || size < 16 + w * h * 4u) {
            MemFree(data);
            LogError("scene", "bad texture format %s", path);
            return false;
        }
        t.owned = data;
        t.pixels = data + 16;
        t.width = w;
        t.height = h;
        t.data_size = size - 16;
    }
    return true;
}

void Scene::ComputeWorldTransforms() {
    // Iterate until parents are resolved (scenes are shallow).
    for (u32 pass = 0; pass < 8; ++pass) {
        for (u32 i = 0; i < objects_.Size(); ++i) {
            SceneObject& o = objects_[i];
            if (o.parent == 0) {
                o.world_x = o.position[0];
                o.world_y = o.position[1];
                o.world_rotation = 0.0f;
                o.world_scale_x = o.scale[0];
                o.world_scale_y = o.scale[1];
                continue;
            }
            const SceneObject* p = 0;
            for (u32 j = 0; j < objects_.Size(); ++j) {
                if (objects_[j].id == o.parent) { p = &objects_[j]; break; }
            }
            if (!p) continue;
            f32 px = p->world_x;
            f32 py = p->world_y;
            f32 ca = cosf(p->world_rotation);
            f32 sa = sinf(p->world_rotation);
            o.world_x = px + (o.position[0] * ca - o.position[1] * sa) * p->world_scale_x;
            o.world_y = py + (o.position[0] * sa + o.position[1] * ca) * p->world_scale_y;
            o.world_rotation = p->world_rotation;
            o.world_scale_x = o.scale[0] * p->world_scale_x;
            o.world_scale_y = o.scale[1] * p->world_scale_y;
        }
    }
}

} // namespace ch360
