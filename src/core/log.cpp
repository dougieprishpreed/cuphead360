#include "core/log.h"
#include "core/file.h"
#include "platform/platform.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

namespace ch360 {

static const int kLineMax = 2048;
static const int kPathMax = 512;

static void* s_file = 0; // HANDLE, kept void* so this header stays platform-neutral
static bool s_mirror = true;
static char s_path[kPathMax];
static u32 s_lines = 0;

static const char* LevelName(LogLevel level) {
    switch (level) {
        case LOG_DEBUG: return "DEBUG";
        case LOG_INFO:  return "INFO";
        case LOG_WARN:  return "WARN";
        case LOG_ERROR: return "ERROR";
        case LOG_FATAL: return "FATAL";
        default:        return "?";
    }
}

static void EmitLine(const char* line) {
    if (s_mirror) {
        PlatformDebugOutput(line);
        PlatformDebugOutput("\n");
    }
    if (s_file) {
        FileWriteHandle(s_file, line, (u32)strlen(line));
        FileWriteHandle(s_file, "\n", 1);
        LogFlush();
    }
    ++s_lines;
}

bool LogOpen(const char* path, bool mirror_to_debugger) {
    LogClose();
    s_mirror = mirror_to_debugger;
    s_path[0] = 0;
    s_lines = 0;
    if (path && path[0]) {
        strncpy(s_path, path, kPathMax - 1);
        s_path[kPathMax - 1] = 0;
        s_file = FileOpenWrite(path);
        if (!s_file) {
            s_path[0] = 0;
            PlatformDebugOutput("[log] could not open log file: ");
            PlatformDebugOutput(path);
            PlatformDebugOutput("\n");
            return false;
        }
    }
    return true;
}

void LogClose() {
    if (s_file) {
        FileClose(s_file);
        s_file = 0;
    }
}

void LogFlush() {
    if (s_file) FileFlush(s_file);
}

const char* LogPath() { return s_path; }

static void LogV(LogLevel level, const char* category, const char* fmt, va_list ap) {
    char body[kLineMax];
    _vsnprintf(body, kLineMax - 1, fmt, ap);
    body[kLineMax - 1] = 0;

    char line[kLineMax + 64];
    _snprintf(line, CH360_ARRAY_COUNT(line) - 1, "[%s] [%s] %s", LevelName(level),
              category ? category : "-", body);
    line[CH360_ARRAY_COUNT(line) - 1] = 0;
    EmitLine(line);
}

void LogPrintf(LogLevel level, const char* category, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    LogV(level, category, fmt, ap);
    va_end(ap);
}

void LogDebug(const char* c, const char* fmt, ...) {
    va_list ap; va_start(ap, fmt); LogV(LOG_DEBUG, c, fmt, ap); va_end(ap);
}
void LogInfo(const char* c, const char* fmt, ...) {
    va_list ap; va_start(ap, fmt); LogV(LOG_INFO, c, fmt, ap); va_end(ap);
}
void LogWarn(const char* c, const char* fmt, ...) {
    va_list ap; va_start(ap, fmt); LogV(LOG_WARN, c, fmt, ap); va_end(ap);
}
void LogError(const char* c, const char* fmt, ...) {
    va_list ap; va_start(ap, fmt); LogV(LOG_ERROR, c, fmt, ap); va_end(ap);
}
void LogFatal(const char* c, const char* fmt, ...) {
    va_list ap; va_start(ap, fmt); LogV(LOG_FATAL, c, fmt, ap); va_end(ap);
}

void LogEvent(const char* category, const char* event_name) {
    LogPrintf(LOG_INFO, category, "event=%s", event_name);
}

void LogEventInt(const char* category, const char* event_name, const char* key, s64 value) {
    LogPrintf(LOG_INFO, category, "event=%s %s=%lld", event_name, key, value);
}

void LogEventStr(const char* category, const char* event_name, const char* key, const char* value) {
    LogPrintf(LOG_INFO, category, "event=%s %s=%s", event_name, key, value ? value : "");
}

void LogAssertFail(const char* file, int line, const char* expression) {
    LogPrintf(LOG_FATAL, "assert", "%s(%d): assertion failed: %s", file, line, expression);
    PlatformDebugBreak();
}

} // namespace ch360
