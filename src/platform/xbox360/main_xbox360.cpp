#include "app/app.h"
#include "core/log.h"

#include <xtl.h>

// Xbox 360 entry point. The XDK console application CRT calls main().
void __cdecl main() {
    ch360::AppConfig cfg;
    // Development scene-viewer milestone. The logic validation mode is
    // selected by changing this command to "logic"; see docs/HANDOFF.md.
    cfg.command = "scene";
    cfg.scene_path = "game:\\GameData\\scene.chs";
    cfg.frames = 120;
    cfg.headless = false;
    cfg.mute = true;
    cfg.screenshot_path = "game:\\frame.bmp";
    cfg.log_path = "game:\\cuphead360.log";
    ch360::AppMain(cfg);
}
