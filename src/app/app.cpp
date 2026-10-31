#include "app/app.h"
#include "core/containers.h"
#include "core/endian.h"
#include "core/file.h"
#include "core/log.h"
#include "core/mem.h"
#include "platform/platform.h"

#include <stdio.h>
#include <string.h>

namespace ch360 {

static int g_failures = 0;

static void Check(bool condition, const char* what) {
    if (condition) {
        LogInfo("selftest", "PASS %s", what);
    } else {
        LogError("selftest", "FAIL %s", what);
        ++g_failures;
    }
}

static void SelfTestEndian() {
    const u8 le[8] = { 0x44, 0x33, 0x22, 0x11, 0x88, 0x77, 0x66, 0x55 };
    Check(ReadU16LE(le) == 0x3344u, "ReadU16LE");
    Check(ReadU32LE(le) == 0x11223344u, "ReadU32LE");
    Check(ReadU64LE(le) == 0x5566778811223344ull, "ReadU64LE");

    const u8 be[4] = { 0x11, 0x22, 0x33, 0x44 };
    Check(ReadU16BE(be) == 0x1122u, "ReadU16BE");
    Check(ReadU32BE(be) == 0x11223344u, "ReadU32BE");

    u8 out[4];
    WriteU32LE(out, 0xDEADBEEFu);
    Check(out[0] == 0xEF && out[1] == 0xBE && out[2] == 0xAD && out[3] == 0xDE,
          "WriteU32LE byte order");

    union { u32 u; f32 f; } bits;
    bits.u = 0x3F800000u;
    u8 fbytes[4];
    WriteU32LE(fbytes, bits.u);
    Check(ReadF32LE(fbytes) == 1.0f, "ReadF32LE");
}

static void SelfTestMemory() {
    u8* a = (u8*)MemAlloc(128);
    Check(a != 0, "MemAlloc");
    if (!a) return;
    memset(a, 0xA5, 128);
    Check(a[0] == 0xA5 && a[127] == 0xA5, "MemAlloc writable");

    a = (u8*)MemRealloc(a, 4096);
    Check(a != 0 && a[0] == 0xA5 && a[127] == 0xA5, "MemRealloc preserves");
    MemFree(a);

    u32 before = MemAllocationCount();
    u8* aligned = (u8*)MemAllocAligned(100, 64);
    Check(aligned != 0 && (((u32)(size_t)aligned) & 63u) == 0, "MemAllocAligned");
    MemFree(aligned);
    Check(MemAllocationCount() == before, "MemFree accounting");
}

static void SelfTestFile() {
#if defined(_XBOX)
    const char* path = "game:\\cuphead360_selftest.bin";
#else
    const char* path = "cuphead360_selftest.bin";
#endif
    const char payload[] = "Cuphead360 file test\x01\x02\x03";
    Check(FileWriteAll(path, payload, (u32)sizeof(payload)), "FileWriteAll");
    u32 size = 0;
    u8* data = FileReadAll(path, &size);
    Check(data != 0 && size == sizeof(payload) &&
          memcmp(data, payload, sizeof(payload)) == 0, "FileReadAll round trip");
    if (data) MemFree(data);
    Check(FileExists(path), "FileExists");
    Check(FileSize(path) == sizeof(payload), "FileSize");
}

static void SelfTestContainers() {
    Buffer<int> numbers;
    for (int i = 0; i < 100; ++i) numbers.PushBack(i);
    Check(numbers.Size() == 100 && numbers[0] == 0 && numbers[99] == 99,
          "Buffer push/order");
    numbers.RemoveAt(50);
    Check(numbers.Size() == 99 && numbers[50] == 51, "Buffer RemoveAt");
    numbers.Resize(120);
    Check(numbers.Size() == 120 && numbers[100] == 0, "Buffer Resize zeroes");

    StringBuf text;
    text.Append("Cuphead");
    text.AppendF(" %d", 360);
    Check(strcmp(text.CStr(), "Cuphead 360") == 0, "StringBuf append");
}

static void RunSelfTest() {
    LogInfo("selftest", "begin");
    Check(HostIsBigEndian() || !HostIsBigEndian(), "host endian probe");
    LogInfo("selftest", "host_is_big_endian=%d", HostIsBigEndian() ? 1 : 0);
    SelfTestEndian();
    SelfTestMemory();
    SelfTestFile();
    SelfTestContainers();
    LogInfo("selftest", "result=%s failures=%d", g_failures ? "FAIL" : "PASS", g_failures);
}

static void LogBuildInfo() {
    LogInfo("boot", "Cuphead360 native runtime");
    LogInfo("boot", "platform=%s _MSC_VER=%d", PlatformName(), _MSC_VER);
    LogInfo("boot", "pointer=%u int=%u long=%u longlong=%u size_t=%u",
            (u32)sizeof(void*), (u32)sizeof(int), (u32)sizeof(long),
            (u32)sizeof(long long), (u32)sizeof(size_t));
    LogInfo("boot", "big_endian=%d", HostIsBigEndian() ? 1 : 0);
    LogInfo("boot", "mem_allocated=%u peak=%u blocks=%u",
            MemAllocatedBytes(), MemPeakBytes(), MemAllocationCount());
}

int AppMain(const AppConfig& config) {
    MemInit();
    PlatformInit();

    const char* log_path = config.log_path ? config.log_path : PlatformDefaultLogPath();
    LogOpen(log_path, true);
    LogBuildInfo();
    LogEventStr("boot", "command", "name", config.command ? config.command : "run");

    int result = 0;
    if (config.command && strcmp(config.command, "selftest") == 0) {
        RunSelfTest();
        result = g_failures ? 1 : 0;
    } else if (config.command && strcmp(config.command, "info") == 0) {
        LogInfo("info", "log=%s", LogPath());
    } else {
        const u32 frames = config.frames ? config.frames : 1;
        for (u32 i = 0; i < frames; ++i) {
            LogDebug("run", "frame=%u dt=%.6f", i, (double)config.fixed_dt);
        }
        LogInfo("run", "completed_frames=%u", frames);
    }

    LogInfo("shutdown", "result=%d mem_allocated=%u peak=%u", result,
            MemAllocatedBytes(), MemPeakBytes());
    LogClose();
    PlatformShutdown();
    MemShutdown();
    return result;
}

} // namespace ch360
