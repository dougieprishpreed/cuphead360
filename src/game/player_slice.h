#ifndef CH360_GAME_PLAYER_SLICE_H
#define CH360_GAME_PLAYER_SLICE_H

#include "core/containers.h"
#include "core/types.h"
#include "engine/scene.h"
#include "platform/input.h"
#include "runtime/value.h"

namespace ch360 {

class Interpreter;

// A small playable vertical slice. Movement is driven by the original
// LevelPlayerMotor/VelocityManager IL where possible; jump/dash, ground
// collision and animation selection are native scaffolding for this slice.
class PlayerSlice {
public:
    PlayerSlice();
    ~PlayerSlice();

    bool Init(Scene& scene, Interpreter& interp);
    void Update(Scene& scene, Interpreter& interp, f32 dt, const InputState& input,
                bool auto_input, u32 frame);
    void ApplyCamera(Scene& scene);

    bool Initialized() const { return initialized_; }
    f32 X() const { return x_; }
    f32 Y() const { return y_; }
    f32 VX() const { return vx_; }
    f32 VY() const { return vy_; }
    bool Grounded() const { return grounded_; }
    u32 DashCount() const { return dash_count_; }
    const char* StateName() const;

private:
    u32 PickFrame(const Buffer<u32>& frames, f32 t) const;
    void SetAnimState(const char* prefix);

    bool initialized_;
    u32 player_object_;
    u32 player_component_;
    u32 vm_type_;
    u32 vm_total_;
    u32 f_move_;
    u32 f_dash_;
    u32 f_hit_;
    u32 f_vertical_dash_;
    u32 f_y_;
    u32 f_max_y_;
    u32 f_y_ease_;
    Value velocity_manager_;

    Buffer<u32> idle_frames_;
    Buffer<u32> run_frames_;
    Buffer<u32> jump_frames_;
    Buffer<u32> dash_frames_;

    f32 x_;
    f32 y_;
    f32 vx_;
    f32 vy_;
    f32 anim_time_;
    f32 dash_timer_;
    f32 dash_cooldown_;
    f32 jump_hold_timer_;
    f32 ground_y_;
    f32 half_height_;
    s32 facing_;
    bool grounded_;
    bool dashing_;
    bool jump_held_;
    u32 dash_count_;
};

} // namespace ch360

#endif // CH360_GAME_PLAYER_SLICE_H
