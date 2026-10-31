#include "engine/animation.h"
#include "engine/scene.h"
#include "core/endian.h"
#include "core/file.h"
#include "core/log.h"
#include "core/mem.h"

#include <math.h>
#include <string.h>

namespace ch360 {

static const u32 kAnimMagic = 0x4E414843u; // "CHAN"

AnimationSet::AnimationSet()
    : file_(0), file_size_(0), last_error_("not loaded"), strings_(0), strings_size_(0) {}

AnimationSet::~AnimationSet() { Unload(); }

void AnimationSet::Unload() {
    clips_.Reset();
    frames_.Reset();
    states_.Reset();
    controllers_.Reset();
    MemFree(file_);
    file_ = 0;
    file_size_ = 0;
    strings_ = 0;
    strings_size_ = 0;
    last_error_ = "not loaded";
}

bool AnimationSet::Load(const char* path) {
    Unload();
    file_ = FileReadAll(path, &file_size_);
    if (!file_) { last_error_ = "cannot read animation file"; return false; }
    if (file_size_ < 32 * 4) { last_error_ = "animation file too small"; return false; }
    if (ReadU32LE(file_) != kAnimMagic) { last_error_ = "bad animation magic"; return false; }
    if (ReadU32LE(file_ + 4) != 1) { last_error_ = "unsupported animation version"; return false; }
    u32 clip_count = ReadU32LE(file_ + 12);
    u32 frame_count = ReadU32LE(file_ + 16);
    u32 state_count = ReadU32LE(file_ + 20);
    u32 controller_count = ReadU32LE(file_ + 24);
    u32 strings_off = ReadU32LE(file_ + 28);
    u32 clips_off = ReadU32LE(file_ + 32);
    u32 frames_off = ReadU32LE(file_ + 36);
    u32 states_off = ReadU32LE(file_ + 40);
    u32 ctrls_off = ReadU32LE(file_ + 44);
    u32 strings_size = ReadU32LE(file_ + 48);
    if (strings_off > file_size_ || strings_size > file_size_ - strings_off) {
        last_error_ = "bad animation string section";
        return false;
    }
    if (clip_count && (clips_off > file_size_ || clip_count > (file_size_ - clips_off) / 32)) {
        last_error_ = "bad animation clip section";
        return false;
    }
    if (frame_count && (frames_off > file_size_ || frame_count > (file_size_ - frames_off) / 16)) {
        last_error_ = "bad animation frame section";
        return false;
    }
    if (state_count && (states_off > file_size_ || state_count > (file_size_ - states_off) / 24)) {
        last_error_ = "bad animation state section";
        return false;
    }
    if (controller_count && (ctrls_off > file_size_ || controller_count > (file_size_ - ctrls_off) / 32)) {
        last_error_ = "bad animation controller section";
        return false;
    }
    strings_ = file_ + strings_off;
    strings_size_ = strings_size;
    if (!clips_.Resize(clip_count) || !frames_.Resize(frame_count) ||
        !states_.Resize(state_count) || !controllers_.Resize(controller_count)) {
        last_error_ = "out of memory for animation data";
        return false;
    }
    for (u32 i = 0; i < clip_count; ++i) {
        const u8* p = file_ + clips_off + i * 32;
        AnimClip& c = clips_[i];
        u32 name_off = ReadU32LE(p + 0);
        c.name = name_off < strings_size_ ? (const char*)(strings_ + name_off) : "";
        c.sample_rate = ReadF32LE(p + 4);
        c.loop = ReadU32LE(p + 8);
        c.frame_start = ReadU32LE(p + 12);
        c.frame_count = ReadU32LE(p + 16);
        c.curve_count = ReadU32LE(p + 20);
    }
    for (u32 i = 0; i < frame_count; ++i) {
        const u8* p = file_ + frames_off + i * 16;
        AnimFrame& f = frames_[i];
        f.time = ReadF32LE(p + 0);
        f.sprite_id = ReadU32LE(p + 4);
        f.target_go_id = ReadU32LE(p + 8);
        f.curve = ReadU32LE(p + 12);
    }
    for (u32 i = 0; i < state_count; ++i) {
        const u8* p = file_ + states_off + i * 24;
        AnimState& s = states_[i];
        u32 name_off = ReadU32LE(p + 0);
        s.name = name_off < strings_size_ ? (const char*)(strings_ + name_off) : "";
        s.clip = ReadU32LE(p + 4);
        s.loop = ReadU32LE(p + 8);
        s.speed = ReadF32LE(p + 12);
    }
    for (u32 i = 0; i < controller_count; ++i) {
        const u8* p = file_ + ctrls_off + i * 32;
        AnimController& c = controllers_[i];
        u32 name_off = ReadU32LE(p + 0);
        c.name = name_off < strings_size_ ? (const char*)(strings_ + name_off) : "";
        c.default_state = ReadU32LE(p + 4);
        c.state_start = ReadU32LE(p + 8);
        c.state_count = ReadU32LE(p + 12);
    }
    last_error_ = "ok";
    LogInfo("anim", "loaded %s clips=%u frames=%u states=%u controllers=%u",
            path, clip_count, frame_count, state_count, controller_count);
    return true;
}

const AnimClip& AnimationSet::Clip(u32 id) const {
    static AnimClip empty;
    if (id >= clips_.Size()) return empty;
    return clips_[id];
}
const AnimFrame& AnimationSet::Frame(u32 index) const {
    static AnimFrame empty;
    if (index >= frames_.Size()) return empty;
    return frames_[index];
}
const AnimState& AnimationSet::State(u32 id) const {
    static AnimState empty;
    if (id >= states_.Size()) return empty;
    return states_[id];
}
const AnimController& AnimationSet::Controller(u32 id) const {
    static AnimController empty;
    if (id >= controllers_.Size()) return empty;
    return controllers_[id];
}

u32 AnimationSet::FindController(const char* name) const {
    if (!name || !name[0]) return 0xFFFFFFFFu;
    for (u32 i = 0; i < controllers_.Size(); ++i) {
        if (strcmp(controllers_[i].name, name) == 0) return i;
    }
    return 0xFFFFFFFFu;
}

AnimationPlayer::AnimationPlayer() {}

bool AnimationPlayer::Init(Scene& scene, const AnimationSet& anim) {
    instances_.Reset();
    targets_.Reset();
    // Map every GameObject to its first SpriteRenderer component.
    for (u32 i = 0; i < scene.ObjectCount(); ++i) {
        const SceneObject& o = scene.Object(i);
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene.ComponentCount()) break;
            if (scene.Component(ci).type == 1) {
                Target t;
                t.go_id = o.id;
                t.component = ci;
                targets_.PushBack(t);
                break;
            }
        }
    }
    for (u32 i = 0; i < scene.ObjectCount(); ++i) {
        const SceneObject& o = scene.Object(i);
        for (u32 c = 0; c < o.component_count; ++c) {
            u32 ci = o.component_start + c;
            if (ci >= scene.ComponentCount()) break;
            const SceneComponent& comp = scene.Component(ci);
            if (comp.type != 2) continue;
            u32 ctrl = anim.FindController(comp.controller);
            if (ctrl == 0xFFFFFFFFu) continue;
            const AnimController& ac = anim.Controller(ctrl);
            AnimInstance inst;
            inst.go_id = o.id;
            inst.controller_id = ctrl;
            inst.state_id = ac.default_state;
            inst.time = 0.0f;
            instances_.PushBack(inst);
        }
    }
    LogInfo("anim", "controllers=%u targets=%u", instances_.Size(), targets_.Size());
    return true;
}

void AnimationPlayer::SetTime(Scene& scene, const AnimationSet& anim, f32 seconds) {
    for (u32 i = 0; i < instances_.Size(); ++i) instances_[i].time = seconds;
    Apply(scene, anim);
}

void AnimationPlayer::Update(Scene& scene, const AnimationSet& anim, f32 dt) {
    for (u32 i = 0; i < instances_.Size(); ++i) instances_[i].time += dt;
    Apply(scene, anim);
}

void AnimationPlayer::Apply(Scene& scene, const AnimationSet& anim) {
    Best best[128];
    for (u32 i = 0; i < instances_.Size(); ++i) {
        const AnimInstance& inst = instances_[i];
        const AnimController& ctrl = anim.Controller(inst.controller_id);
        u32 sid = ctrl.state_start + inst.state_id;
        if (inst.state_id >= ctrl.state_count) sid = ctrl.state_start;
        const AnimState& state = anim.State(sid);
        const AnimClip& clip = anim.Clip(state.clip);
        if (clip.frame_count == 0) continue;
        f32 duration = 0.0f;
        for (u32 f = 0; f < clip.frame_count; ++f) {
            f32 ft = anim.Frame(clip.frame_start + f).time;
            if (ft > duration) duration = ft;
        }
        duration += 1.0f / (clip.sample_rate > 0.0f ? clip.sample_rate : 24.0f);
        f32 t = inst.time * (state.speed > 0.0f ? state.speed : 1.0f);
        if (state.loop || clip.loop) {
            if (duration > 0.0f) t = fmodf(t, duration);
        }
        if (t < 0.0f) t = 0.0f;

        u32 best_count = 0;
        for (u32 f = 0; f < clip.frame_count; ++f) {
            const AnimFrame& fr = anim.Frame(clip.frame_start + f);
            if (fr.time > t) continue;
            // One winner per target GameObject.
            u32 b = 0;
            for (; b < best_count; ++b) if (best[b].go_id == fr.target_go_id) break;
            if (b == best_count && best_count < CH360_ARRAY_COUNT(best)) {
                best[b].go_id = fr.target_go_id;
                best[b].component = 0xFFFFFFFFu;
                best[b].sprite = 0;
                best[b].time = -1.0f;
                ++best_count;
            }
            if (b < best_count && fr.time >= best[b].time) {
                best[b].sprite = fr.sprite_id;
                best[b].time = fr.time;
            }
        }
        for (u32 b = 0; b < best_count; ++b) {
            if (best[b].sprite == 0) continue;
            for (u32 ti = 0; ti < targets_.Size(); ++ti) {
                if (targets_[ti].go_id == best[b].go_id) {
                    scene.MutableComponent(targets_[ti].component).sprite_id = best[b].sprite;
                    break;
                }
            }
        }
    }
}

} // namespace ch360
