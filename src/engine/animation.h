#ifndef CH360_ENGINE_ANIMATION_H
#define CH360_ENGINE_ANIMATION_H

#include "core/containers.h"
#include "core/types.h"

namespace ch360 {

class Scene;

struct AnimClip {
    const char* name;
    f32 sample_rate;
    u32 loop;
    u32 frame_start;
    u32 frame_count;
    u32 curve_count;
};

struct AnimFrame {
    f32 time;
    u32 sprite_id;    // 1-based scene sprite id
    u32 target_go_id; // GameObject id in the scene
    u32 curve;
};

struct AnimState {
    const char* name;
    u32 clip;
    u32 loop;
    f32 speed;
};

struct AnimController {
    const char* name;
    u32 default_state;
    u32 state_start;
    u32 state_count;
};

struct AnimInstance {
    u32 go_id;
    u32 controller_id;
    u32 state_id;
    f32 time;
};

class AnimationSet {
public:
    AnimationSet();
    ~AnimationSet();

    bool Load(const char* path);
    void Unload();
    bool Loaded() const { return file_ != 0; }

    u32 ClipCount() const { return clips_.Size(); }
    u32 FrameCount() const { return frames_.Size(); }
    u32 StateCount() const { return states_.Size(); }
    u32 ControllerCount() const { return controllers_.Size(); }

    const AnimClip& Clip(u32 id) const;
    const AnimFrame& Frame(u32 index) const;
    const AnimState& State(u32 id) const;
    const AnimController& Controller(u32 id) const;

    u32 FindController(const char* name) const;
    const char* LastError() const { return last_error_; }

private:
    u8* file_;
    u32 file_size_;
    const char* last_error_;
    const u8* strings_;
    u32 strings_size_;
    Buffer<AnimClip> clips_;
    Buffer<AnimFrame> frames_;
    Buffer<AnimState> states_;
    Buffer<AnimController> controllers_;
};

// Drives the default state of every Animator in a scene. Transitions and
// parameters are layered on once the original scripts call into Animator.
class AnimationPlayer {
public:
    AnimationPlayer();

    bool Init(Scene& scene, const AnimationSet& anim);
    void SetTime(Scene& scene, const AnimationSet& anim, f32 seconds);
    void Update(Scene& scene, const AnimationSet& anim, f32 dt);

private:
    struct Target {
        u32 go_id;
        u32 component;
    };
    struct Best {
        u32 go_id;
        u32 component;
        u32 sprite;
        f32 time;
    };

    void Apply(Scene& scene, const AnimationSet& anim);

    Buffer<AnimInstance> instances_;
    Buffer<Target> targets_;
};

} // namespace ch360

#endif // CH360_ENGINE_ANIMATION_H
