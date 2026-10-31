#ifndef CH360_APP_APP_H
#define CH360_APP_APP_H

#include "core/types.h"

namespace ch360 {

struct AppConfig {
    AppConfig()
        : command("run"), log_path(0), metadata_path(0), scene_path(0),
          screenshot_path(0), frames(1), headless(true), mute(true),
          seed(1), fixed_dt(1.0f / 60.0f), speed(0.0f), anim_time(0.5f),
          auto_input(true), probe_fields(false), combat_test(false) {}

    const char* command;   // "run", "selftest", "info", "inspect", "logic", "scene"
    const char* log_path;  // null => platform default
    const char* metadata_path; // null => target default
    const char* scene_path;
    const char* screenshot_path;
    u32 frames;
    bool headless;
    bool mute;
    u32 seed;
    f32 fixed_dt;
    f32 speed;             // 0 = as fast as possible in headless mode
    f32 anim_time;         // initial animation sample for screenshots
    bool auto_input;       // scripted playable-slice input
    bool probe_fields;     // print runtime gameplay fields after a play harness
    bool combat_test;      // place the player in line with the tutorial target
};

int AppMain(const AppConfig& config);

} // namespace ch360

#endif // CH360_APP_APP_H
