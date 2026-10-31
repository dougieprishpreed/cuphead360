#include "app/app.h"
#include "core/log.h"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static bool ArgEquals(const char* a, const char* b) {
    return a && b && strcmp(a, b) == 0;
}

int main(int argc, char** argv) {
    ch360::AppConfig cfg;
    bool explicit_mode = false;
    for (int i = 1; i < argc; ++i) {
        const char* a = argv[i];
        if (ArgEquals(a, "--headless")) {
            cfg.headless = true;
        } else if (ArgEquals(a, "--windowed")) {
            cfg.headless = false;
        } else if (ArgEquals(a, "--mute")) {
            cfg.mute = true;
        } else if (ArgEquals(a, "--frames") && i + 1 < argc) {
            cfg.frames = (u32)strtoul(argv[++i], 0, 10);
        } else if (ArgEquals(a, "--seed") && i + 1 < argc) {
            cfg.seed = (u32)strtoul(argv[++i], 0, 10);
        } else if (ArgEquals(a, "--log") && i + 1 < argc) {
            cfg.log_path = argv[++i];
        } else if (ArgEquals(a, "--metadata") && i + 1 < argc) {
            cfg.metadata_path = argv[++i];
        } else if (ArgEquals(a, "--inspect")) {
            cfg.command = "inspect";
        } else if (ArgEquals(a, "--logic")) {
            cfg.command = "logic";
        } else if (ArgEquals(a, "--unity")) {
            cfg.command = "unity";
            explicit_mode = true;
        } else if (ArgEquals(a, "--stack")) {
            cfg.command = "stack";
            explicit_mode = true;
        } else if (ArgEquals(a, "--motor")) {
            cfg.command = "motor";
            explicit_mode = true;
            cfg.frames = 180;
        } else if (ArgEquals(a, "--motorplay")) {
            cfg.command = "motorplay";
            explicit_mode = true;
            cfg.frames = 240;
        } else if (ArgEquals(a, "--level")) {
            cfg.command = "level";
            explicit_mode = true;
        } else if (ArgEquals(a, "--levelplay")) {
            cfg.command = "levelplay";
            explicit_mode = true;
            cfg.frames = 240;
        } else if (ArgEquals(a, "--play")) {
            cfg.command = "play";
            explicit_mode = true;
        } else if (ArgEquals(a, "--manual")) {
            cfg.auto_input = false;
        } else if (ArgEquals(a, "--scene") && i + 1 < argc) {
            cfg.scene_path = argv[++i];
            if (!explicit_mode) cfg.command = "scene";
        } else if (ArgEquals(a, "--screenshot") && i + 1 < argc) {
            cfg.screenshot_path = argv[++i];
        } else if (ArgEquals(a, "--anim-time") && i + 1 < argc) {
            cfg.anim_time = (f32)atof(argv[++i]);
        } else if (ArgEquals(a, "--command") && i + 1 < argc) {
            cfg.command = argv[++i];
        } else if (ArgEquals(a, "--selftest")) {
            cfg.command = "selftest";
        } else if (ArgEquals(a, "--speed") && i + 1 < argc) {
            cfg.speed = (f32)atof(argv[++i]);
        } else if (ArgEquals(a, "--help")) {
            printf("Cuphead360 development build\n");
            printf("  --headless | --windowed\n");
            printf("  --frames N\n");
            printf("  --seed N\n");
            printf("  --log PATH\n");
            printf("  --metadata PATH\n");
            printf("  --inspect\n");
            printf("  --logic\n");
            printf("  --scene PATH\n");
            printf("  --unity\n");
            printf("  --stack\n");
            printf("  --motor\n");
            printf("  --motorplay\n");
            printf("  --level\n");
            printf("  --levelplay\n");
            printf("  --play\n");
            printf("  --manual\n");
            printf("  --screenshot PATH\n");
            printf("  --anim-time SECONDS\n");
            printf("  --selftest\n");
            printf("  --speed N\n");
            return 0;
        } else {
            printf("unknown argument: %s\n", a);
            return 2;
        }
    }
    return ch360::AppMain(cfg);
}
