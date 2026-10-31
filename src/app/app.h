#ifndef CH360_APP_APP_H
#define CH360_APP_APP_H

#include "core/types.h"

namespace ch360 {

struct AppConfig {
    AppConfig()
        : command("run"), log_path(0), frames(1), headless(true), mute(true),
          seed(1), fixed_dt(1.0f / 60.0f), speed(0.0f) {}

    const char* command;   // "run", "selftest", "info"
    const char* log_path;  // null => platform default
    u32 frames;
    bool headless;
    bool mute;
    u32 seed;
    f32 fixed_dt;
    f32 speed;             // 0 = as fast as possible in headless mode
};

int AppMain(const AppConfig& config);

} // namespace ch360

#endif // CH360_APP_APP_H
