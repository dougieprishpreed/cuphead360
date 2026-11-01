#include "app/app.h"
#include "core/log.h"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

#if defined(_WIN32)
#include <windows.h>

static LONG WINAPI Cuphead360CrashFilter(EXCEPTION_POINTERS* info) {
    if (!info || !info->ExceptionRecord || !info->ContextRecord) return EXCEPTION_CONTINUE_SEARCH;
    const EXCEPTION_RECORD* er = info->ExceptionRecord;
    const CONTEXT* ctx = info->ContextRecord;
    ch360::LogError("crash", "unhandled exception code=%08lx address=%p",
             (unsigned long)er->ExceptionCode, (void*)er->ExceptionAddress);
#if defined(_M_IX86)
    ch360::LogError("crash", "eip=%08lx esp=%08lx ebp=%08lx",
             (unsigned long)ctx->Eip, (unsigned long)ctx->Esp, (unsigned long)ctx->Ebp);
    const unsigned long* sp = (const unsigned long*)ctx->Esp;
    if (sp && !IsBadReadPtr(sp, 16 * sizeof(unsigned long))) {
        for (u32 i = 0; i < 16; ++i) {
            ch360::LogError("crash", "stack[%02u]=%08lx", i, sp[i]);
        }
    }
#elif defined(_M_X64)
    ch360::LogError("crash", "rip=%016llx rsp=%016llx rbp=%016llx",
             (unsigned long long)ctx->Rip, (unsigned long long)ctx->Rsp,
             (unsigned long long)ctx->Rbp);
#else
    (void)ctx;
#endif
    ch360::LogFlush();
    return EXCEPTION_CONTINUE_SEARCH;
}
#endif

static bool ArgEquals(const char* a, const char* b) {
    return a && b && strcmp(a, b) == 0;
}

int main(int argc, char** argv) {
#if defined(_WIN32)
    SetUnhandledExceptionFilter(Cuphead360CrashFilter);
#endif
    ch360::AppConfig cfg;
    bool explicit_mode = false;
    bool explicit_frames = false;
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
            explicit_frames = true;
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
            if (!explicit_frames) cfg.frames = 180;
        } else if (ArgEquals(a, "--motorplay")) {
            cfg.command = "motorplay";
            explicit_mode = true;
            if (!explicit_frames) cfg.frames = 240;
        } else if (ArgEquals(a, "--level")) {
            cfg.command = "level";
            explicit_mode = true;
        } else if (ArgEquals(a, "--levelplay")) {
            cfg.command = "levelplay";
            explicit_mode = true;
            if (!explicit_frames) cfg.frames = 240;
        } else if (ArgEquals(a, "--play")) {
            cfg.command = "play";
            explicit_mode = true;
        } else if (ArgEquals(a, "--manual")) {
            cfg.auto_input = false;
        } else if (ArgEquals(a, "--probe-fields")) {
            cfg.probe_fields = true;
        } else if (ArgEquals(a, "--combat-test")) {
            cfg.combat_test = true;
        } else if (ArgEquals(a, "--scene") && i + 1 < argc) {
            cfg.scene_path = argv[++i];
            if (!explicit_mode) cfg.command = "scene";
        } else if (ArgEquals(a, "--screenshot") && i + 1 < argc) {
            cfg.screenshot_path = argv[++i];
        } else if (ArgEquals(a, "--anim-time") && i + 1 < argc) {
            cfg.anim_time = (f32)atof(argv[++i]);
        } else if (ArgEquals(a, "--command") && i + 1 < argc) {
            cfg.command = argv[++i];
            explicit_mode = true;
        } else if (ArgEquals(a, "--strict-faults") && i + 1 < argc) {
            cfg.strict_fault_prefixes = argv[++i];
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
            printf("  --strict-faults PREFIX[,PREFIX...]\n");
            return 0;
        } else {
            printf("unknown argument: %s\n", a);
            return 2;
        }
    }
    return ch360::AppMain(cfg);
}
