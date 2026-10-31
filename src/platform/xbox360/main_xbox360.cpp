#include "app/app.h"
#include "core/log.h"

#include <xtl.h>

// Xbox 360 entry point. The XDK console application CRT calls main().
void __cdecl main() {
    ch360::AppConfig cfg;
    cfg.command = "run";
    cfg.frames = 1;
    cfg.headless = true;
    cfg.mute = true;
    cfg.log_path = "game:\\cuphead360.log";
    ch360::AppMain(cfg);
}
