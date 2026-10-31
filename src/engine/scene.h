#ifndef CH360_ENGINE_SCENE_H
#define CH360_ENGINE_SCENE_H

#include "core/containers.h"
#include "core/types.h"

namespace ch360 {

struct SceneObject {
    u32 id;
    u32 parent;
    const char* name;
    u32 flags; // bit 0 active
    f32 position[3];
    f32 rotation[4];
    f32 scale[3];
    u32 component_start;
    u32 component_count;
    // Derived world-space 2D transform.
    f32 world_x;
    f32 world_y;
    f32 world_rotation;
    f32 world_scale_x;
    f32 world_scale_y;
};

struct SceneComponent {
    u32 type; // 1 SpriteRenderer, 2 Animator, 3 Camera
    u32 flags;
    u32 sprite_id;       // 1-based into sprites
    u32 controller_off;
    const char* controller;
    u32 source_id;
    s32 sorting_order;
    s32 sorting_layer;
    f32 color[4];
};

struct SceneSprite {
    const char* name;
    u32 texture_id; // 1-based into textures
    u32 texture_width;
    u32 texture_height;
    f32 rect[4];
    f32 pivot[2];
    f32 pixels_per_unit;
    f32 world_width;
    f32 world_height;
    f32 offset[2];
};

struct SceneTexture {
    const char* name;
    const char* file;
    u32 width;
    u32 height;
    u32 format;
    u8* pixels;
    u8* owned;
    u32 data_size;
};

struct ScriptValue {
    u32 field_id;
    u32 parent_field;
    u32 index;
    u32 kind;
    u32 data0;
    u32 data1;
    u32 data2;
    u32 reserved;
};

struct SceneCamera {
    u32 go_id;
    u32 flags; // bit 0 orthographic
    f32 orthographic_size;
    f32 field_of_view;
    f32 near_clip;
    f32 far_clip;
    f32 background[4];
    f32 depth;
    f32 position[3];
};

class Scene {
public:
    Scene();
    ~Scene();

    bool Load(const char* path);
    void Unload();
    bool LoadTextures();
    void ComputeWorldTransforms();

    bool Loaded() const { return file_ != 0; }
    u32 ObjectCount() const { return objects_.Size(); }
    u32 ComponentCount() const { return components_.Size(); }
    u32 SpriteCount() const { return sprites_.Size(); }
    u32 TextureCount() const { return textures_.Size(); }
    u32 ScriptValueCount() const { return script_values_.Size(); }
    const ScriptValue& ScriptValueAt(u32 index) const { return script_values_[index]; }

    const SceneObject& Object(u32 index) const { return objects_[index]; }
    SceneObject& MutableObject(u32 index) { return objects_[index]; }
    const SceneComponent& Component(u32 index) const { return components_[index]; }
    SceneComponent& MutableComponent(u32 index) { return components_[index]; }
    const SceneSprite& Sprite(u32 id) const;
    const SceneTexture& Texture(u32 id) const;
    const SceneCamera* MainCamera() const;
    SceneCamera* MutableMainCamera();

    const char* LastError() const { return last_error_; }
    const char* Directory() const { return directory_; }
    u32 FindObjectByName(const char* name) const;
    u32 FindSpriteByName(const char* name) const;
    u32 AddRuntimeObject(const SceneObject& object);
    u32 AddRuntimeComponent(const SceneComponent& component);
    void SetMainCameraPosition(f32 x, f32 y);
    const u8* ScriptStrings() const { return script_strings_; }
    u32 ScriptStringsSize() const { return script_strings_size_; }

private:
    bool Fail(const char* message);
    const char* Str(u32 off) const;

    u8* file_;
    u32 file_size_;
    const char* last_error_;
    const u8* strings_;
    u32 strings_size_;
    char directory_[512];

    Buffer<SceneObject> objects_;
    Buffer<SceneComponent> components_;
    Buffer<SceneSprite> sprites_;
    Buffer<SceneTexture> textures_;
    Buffer<SceneCamera> cameras_;
    Buffer<ScriptValue> script_values_;
    const u8* script_strings_;
    u32 script_strings_size_;
};

} // namespace ch360

#endif // CH360_ENGINE_SCENE_H
