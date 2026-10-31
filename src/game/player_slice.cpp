#include "game/player_slice.h"
#include "engine/renderer_soft.h"
#include "core/log.h"
#include "runtime/interpreter.h"

#include <math.h>
#include <string.h>

namespace ch360 {

static const f32 kMoveSpeed = 490.0f;
static const f32 kJumpSpeed = 1250.0f;
static const f32 kGravity = -3600.0f;
static const f32 kTerminalY = -1620.0f;
static const f32 kDashSpeed = 1100.0f;
static const f32 kDashTime = 0.30f;
static const f32 kDashCooldown = 0.45f;
static const f32 kGroundY = -280.0f;
static const f32 kHalfHeight = 95.0f;

PlayerSlice::PlayerSlice()
    : initialized_(false), player_object_(0xFFFFFFFFu), player_component_(0xFFFFFFFFu),
      vm_type_(0), vm_total_(0), f_move_(0), f_dash_(0), f_hit_(0),
      f_vertical_dash_(0), f_y_(0), f_max_y_(0), f_y_ease_(0),
      x_(0), y_(0), vx_(0), vy_(0), anim_time_(0), dash_timer_(0),
      dash_cooldown_(0), jump_hold_timer_(0), ground_y_(kGroundY),
      half_height_(kHalfHeight), facing_(1), grounded_(false), dashing_(false),
      jump_held_(false), dash_count_(0) {}

PlayerSlice::~PlayerSlice() {}

bool PlayerSlice::Init(Scene& scene, Interpreter& interp) {
    player_object_ = scene.FindObjectByName("Player");
    if (player_object_ == 0xFFFFFFFFu) return false;
    const SceneObject& o = scene.Object(player_object_);
    for (u32 c = 0; c < o.component_count; ++c) {
        u32 ci = o.component_start + c;
        if (ci < scene.ComponentCount() && scene.Component(ci).type == 1) {
            player_component_ = ci;
            break;
        }
    }
    if (player_component_ == 0xFFFFFFFFu) return false;
    x_ = o.world_x;
    y_ = o.world_y;
    // Collect animation frames by sprite-name prefix.
    for (u32 i = 0; i < scene.SpriteCount(); ++i) {
        const SceneSprite& sp = scene.Sprite(i + 1);
        if (strncmp(sp.name, "cuphead_idle_", 13) == 0) idle_frames_.PushBack(i + 1);
        else if (strncmp(sp.name, "cuphead_run_", 12) == 0) run_frames_.PushBack(i + 1);
        else if (strncmp(sp.name, "cuphead_jump_", 13) == 0) jump_frames_.PushBack(i + 1);
        else if (strncmp(sp.name, "cuphead_dash_", 13) == 0) dash_frames_.PushBack(i + 1);
    }
    // Original movement math object.
    MetadataPackage& md = interp.Metadata();
    vm_type_ = md.FindType("LevelPlayerMotor/VelocityManager");
    if (vm_type_) {
        vm_total_ = md.FindMethod(vm_type_, "get_Total");
        f_move_ = md.FindField(vm_type_, "move");
        f_dash_ = md.FindField(vm_type_, "dash");
        f_hit_ = md.FindField(vm_type_, "hit");
        f_vertical_dash_ = md.FindField(vm_type_, "verticalDash");
        f_y_ = md.FindField(vm_type_, "_y");
        f_max_y_ = md.FindField(vm_type_, "maxY");
        f_y_ease_ = md.FindField(vm_type_, "yEase");
        velocity_manager_ = interp.NewObjectValue(vm_type_);
        interp.SetField(f_max_y_, velocity_manager_, Value::R4(1620.0f));
        interp.SetField(f_y_ease_, velocity_manager_, Value::I4(0));
        interp.SetField(f_move_, velocity_manager_, Value::R4(0.0f));
        interp.SetField(f_dash_, velocity_manager_, Value::R4(0.0f));
        interp.SetField(f_hit_, velocity_manager_, Value::R4(0.0f));
        interp.SetField(f_vertical_dash_, velocity_manager_, Value::R4(0.0f));
        interp.SetField(f_y_, velocity_manager_, Value::R4(0.0f));
    }
    initialized_ = true;
    LogInfo("play", "player object=%u component=%u idle=%u run=%u jump=%u dash=%u",
            player_object_, player_component_, idle_frames_.Size(), run_frames_.Size(),
            jump_frames_.Size(), dash_frames_.Size());
    return true;
}

const char* PlayerSlice::StateName() const {
    if (dashing_) return "Dash";
    if (!grounded_) return "Jump";
    if (fabsf(vx_) > 20.0f) return "Run";
    return "Idle";
}

u32 PlayerSlice::PickFrame(const Buffer<u32>& frames, f32 t) const {
    if (frames.Empty()) return 0;
    u32 i = (u32)(t * 12.0f) % frames.Size();
    return frames[i];
}

void PlayerSlice::SetAnimState(const char* prefix) {
    (void)prefix;
}

void PlayerSlice::Update(Scene& scene, Interpreter& interp, f32 dt,
                         const InputState& input, bool auto_input, u32 frame) {
    if (!initialized_) return;
    f32 axis = input.axis_x;
    bool jump = input.jump_pressed;
    bool jump_held = input.jump_held;
    bool dash = input.dash_pressed;
    if (auto_input) {
        // Deterministic test timeline: run, jump, land, dash.
        axis = 0.0f; jump = false; jump_held = false; dash = false;
        if (frame < 70) axis = 1.0f;
        else if (frame < 90) axis = 1.0f;
        else if (frame < 130) axis = -1.0f;
        else if (frame < 170) axis = 1.0f;
        if (frame == 30 || frame == 125) jump = true;
        if (frame >= 30 && frame < 55) jump_held = true;
        if (frame == 80) dash = true;
        if (frame == 150) dash = true;
    }

    // Use the original VelocityManager.Total for the horizontal target.
    f32 target_vx = axis * kMoveSpeed;
    if (vm_type_ && vm_total_) {
        interp.SetField(f_move_, velocity_manager_, Value::R4(axis * kMoveSpeed));
        Value args[1]; args[0] = velocity_manager_;
        Value ret;
        if (interp.Invoke(vm_total_, args, 1, &ret) && ret.tag == VT_VEC2 && ret.data.ptr) {
            target_vx = ((Vec2*)ret.data.ptr)->x;
        }
    }

    if (dash_timer_ <= 0.0f) {
        f32 blend = dt * 12.0f;
        if (blend > 1.0f) blend = 1.0f;
        vx_ += (target_vx - vx_) * blend;
    }
    if (axis != 0.0f) facing_ = axis > 0.0f ? 1 : -1;

    if (dash && dash_cooldown_ <= 0.0f && dash_timer_ <= 0.0f) {
        dash_timer_ = kDashTime;
        dash_cooldown_ = kDashCooldown;
        dashing_ = true;
        ++dash_count_;
        vx_ = kDashSpeed * (f32)facing_;
        vy_ = 0.0f;
    }
    if (dash_timer_ > 0.0f) {
        dash_timer_ -= dt;
        if (dash_timer_ <= 0.0f) dashing_ = false;
    }
    if (dash_cooldown_ > 0.0f) dash_cooldown_ -= dt;

    if (grounded_ && jump && dash_timer_ <= 0.0f) {
        vy_ = kJumpSpeed;
        grounded_ = false;
        jump_hold_timer_ = 0.16f;
    }
    jump_held_ = jump_held;
    if (!grounded_ && jump_held_ && jump_hold_timer_ > 0.0f && vy_ < 0.0f) {
        vy_ += -900.0f * dt;
        jump_hold_timer_ -= dt;
    }

    if (!grounded_) {
        vy_ += kGravity * dt;
        if (vy_ < kTerminalY) vy_ = kTerminalY;
    }
    x_ += vx_ * dt;
    y_ += vy_ * dt;

    // Ground collision (the tutorial's floor; native AABB slice).
    if (y_ - half_height_ <= ground_y_ + 0.01f) {
        y_ = ground_y_ + half_height_;
        vy_ = 0.0f;
        grounded_ = true;
        dash_timer_ = 0.0f;
        dashing_ = false;
    } else {
        grounded_ = false;
    }
    if (x_ < -900.0f) { x_ = -900.0f; vx_ = 0.0f; }
    if (x_ > 900.0f) { x_ = 900.0f; vx_ = 0.0f; }

    // Write the transform and animation.
    SceneObject& o = scene.MutableObject(player_object_);
    o.position[0] = x_;
    o.position[1] = y_;
    o.position[2] = 0.0f;
    scene.ComputeWorldTransforms();
    anim_time_ += dt;
    const Buffer<u32>* frames = &idle_frames_;
    if (dashing_) frames = &dash_frames_;
    else if (!grounded_) frames = &jump_frames_;
    else if (fabsf(vx_) > 20.0f) frames = &run_frames_;
    u32 sprite = PickFrame(*frames, anim_time_);
    if (sprite == 0) sprite = PickFrame(idle_frames_, anim_time_);
    if (sprite) scene.MutableComponent(player_component_).sprite_id = sprite;
    if (vx_ < -1.0f) scene.MutableComponent(player_component_).flags |= 2; // flipX
    else if (vx_ > 1.0f) scene.MutableComponent(player_component_).flags &= ~2u;
}

void PlayerSlice::ApplyCamera(Scene& scene) {
    f32 cam_x = x_;
    f32 cam_y = y_ + 35.0f;
    if (cam_y < -120.0f) cam_y = -120.0f;
    scene.SetMainCameraPosition(cam_x, cam_y);
}

} // namespace ch360
