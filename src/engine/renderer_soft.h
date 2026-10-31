#ifndef CH360_ENGINE_RENDERER_SOFT_H
#define CH360_ENGINE_RENDERER_SOFT_H

#include "core/containers.h"
#include "core/types.h"
#include "engine/scene.h"

namespace ch360 {

class SoftRenderer {
public:
    struct DrawItem {
        s32 layer;
        s32 order;
        u32 object_index;
        u32 component_index;
    };

    SoftRenderer(u32 width, u32 height);
    ~SoftRenderer();

    u32 Width() const { return width_; }
    u32 Height() const { return height_; }
    u32* Pixels() { return pixels_; }

    void Clear(f32 r, f32 g, f32 b, f32 a);
    void DrawScene(const Scene& scene);
    bool SaveBmp(const char* path) const;

private:
    void DrawSprite(const SceneSprite& sprite, const SceneTexture& texture,
                    f32 x, f32 y, f32 w, f32 h, const f32 color[4],
                    bool flip_x, bool flip_y);

    u32 width_;
    u32 height_;
    u32* pixels_;
};

} // namespace ch360

#endif // CH360_ENGINE_RENDERER_SOFT_H
