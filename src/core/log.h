#ifndef CH360_CORE_LOG_H
#define CH360_CORE_LOG_H

#include "core/types.h"

namespace ch360 {

enum LogLevel {
    LOG_DEBUG = 0,
    LOG_INFO,
    LOG_WARN,
    LOG_ERROR,
    LOG_FATAL
};

// Opens the log file. If path is null/empty, only debug output is used.
bool LogOpen(const char* path, bool mirror_to_debugger);
void LogClose();
void LogFlush();

// Always appends a newline if the formatted text does not end with one.
void LogPrintf(LogLevel level, const char* category, const char* fmt, ...);

void LogDebug(const char* category, const char* fmt, ...);
void LogInfo(const char* category, const char* fmt, ...);
void LogWarn(const char* category, const char* fmt, ...);
void LogError(const char* category, const char* fmt, ...);
void LogFatal(const char* category, const char* fmt, ...);

// Structured "key=value" records, one line per record.
void LogEvent(const char* category, const char* event_name);
void LogEventInt(const char* category, const char* event_name, const char* key, s64 value);
void LogEventStr(const char* category, const char* event_name, const char* key, const char* value);

void LogAssertFail(const char* file, int line, const char* expression);

const char* LogPath();

} // namespace ch360

#define CH360_ASSERT(expr) \
    do { if (!(expr)) { ::ch360::LogAssertFail(__FILE__, __LINE__, #expr); } } while (0)

#define CH360_LOG_INFO(cat, ...)  ::ch360::LogInfo(cat, __VA_ARGS__)
#define CH360_LOG_WARN(cat, ...)  ::ch360::LogWarn(cat, __VA_ARGS__)
#define CH360_LOG_ERROR(cat, ...) ::ch360::LogError(cat, __VA_ARGS__)

#endif // CH360_CORE_LOG_H
