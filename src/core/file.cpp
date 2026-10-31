#include "core/file.h"
#include "core/log.h"
#include "core/mem.h"
#include "platform/platform.h"

#include <string.h>

#if defined(_XBOX)
#include <xtl.h>
#else
#include <windows.h>
#endif

namespace ch360 {

#if defined(_XBOX)
// xtl.h on this XDK does not define the desktop shell constant.
static const DWORD kInvalidFileAttributes = (DWORD)0xFFFFFFFFu;
#else
static const DWORD kInvalidFileAttributes = INVALID_FILE_ATTRIBUTES;
#endif

void* FileOpenRead(const char* path) {
    if (!path || !path[0]) return 0;
    HANDLE h = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    return (h == INVALID_HANDLE_VALUE) ? 0 : (void*)h;
}

void* FileOpenWrite(const char* path) {
    if (!path || !path[0]) return 0;
    FileEnsureParentDir(path);
    HANDLE h = CreateFileA(path, GENERIC_WRITE, 0, NULL,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    return (h == INVALID_HANDLE_VALUE) ? 0 : (void*)h;
}

void FileClose(void* handle) {
    if (handle) CloseHandle((HANDLE)handle);
}

void FileFlush(void* handle) {
    if (handle) FlushFileBuffers((HANDLE)handle);
}

bool FileWriteHandle(void* handle, const void* data, u32 size) {
    if (!handle || (!data && size)) return false;
    const u8* p = (const u8*)data;
    u32 left = size;
    while (left) {
        DWORD written = 0;
        DWORD chunk = left > 0x100000u ? 0x100000u : (DWORD)left;
        if (!WriteFile((HANDLE)handle, p, chunk, &written, NULL) || written == 0) {
            return false;
        }
        p += written;
        left -= written;
    }
    return true;
}

bool FileReadHandle(void* handle, void* data, u32 size, u32* bytes_read) {
    if (bytes_read) *bytes_read = 0;
    if (!handle || (!data && size)) return false;
    u8* p = (u8*)data;
    u32 left = size;
    while (left) {
        DWORD got = 0;
        DWORD chunk = left > 0x100000u ? 0x100000u : (DWORD)left;
        if (!ReadFile((HANDLE)handle, p, chunk, &got, NULL)) return false;
        if (got == 0) break;
        p += got;
        left -= got;
    }
    if (bytes_read) *bytes_read = size - left;
    return true;
}

u32 FileSizeHandle(void* handle) {
    if (!handle) return 0;
    DWORD high = 0;
    DWORD low = GetFileSize((HANDLE)handle, &high);
    if (low == INVALID_FILE_SIZE && GetLastError() != NO_ERROR) return 0;
    return low; // Game data files and metadata are < 4 GB.
}

bool FileExists(const char* path) {
    if (!path || !path[0]) return false;
    DWORD attrs = GetFileAttributesA(path);
    return attrs != kInvalidFileAttributes;
}

u32 FileSize(const char* path) {
    void* h = FileOpenRead(path);
    if (!h) return 0;
    u32 size = FileSizeHandle(h);
    FileClose(h);
    return size;
}

u8* FileReadAll(const char* path, u32* out_size) {
    if (out_size) *out_size = 0;
    void* h = FileOpenRead(path);
    if (!h) {
        LogError("file", "cannot open '%s'", path ? path : "(null)");
        return 0;
    }
    u32 size = FileSizeHandle(h);
    u8* data = (u8*)MemAlloc(size + 1);
    if (!data) {
        FileClose(h);
        return 0;
    }
    u32 got = 0;
    if (!FileReadHandle(h, data, size, &got) || got != size) {
        LogError("file", "short read '%s': %u of %u", path, got, size);
        MemFree(data);
        FileClose(h);
        return 0;
    }
    FileClose(h);
    data[size] = 0;
    if (out_size) *out_size = size;
    return data;
}

bool FileWriteAll(const char* path, const void* data, u32 size) {
    void* h = FileOpenWrite(path);
    if (!h) {
        LogError("file", "cannot create '%s'", path ? path : "(null)");
        return false;
    }
    bool ok = FileWriteHandle(h, data, size);
    FileFlush(h);
    FileClose(h);
    return ok;
}

static bool CreateOneDir(const char* path) {
    if (!path || !path[0]) return false;
    DWORD attrs = GetFileAttributesA(path);
    if (attrs != kInvalidFileAttributes) {
        return (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }
    return CreateDirectoryA(path, NULL) != 0;
}

bool FileEnsureParentDir(const char* path) {
    if (!path || !path[0]) return false;
    char buf[512];
    u32 len = (u32)strlen(path);
    if (len >= sizeof(buf)) return false;
    memcpy(buf, path, len + 1);

    // Find the last separator.
    int last = -1;
    for (u32 i = 0; i < len; ++i) {
        if (buf[i] == '\\' || buf[i] == '/') last = (int)i;
    }
    if (last <= 0) return true;
    buf[last] = 0;

    // Create each component. Handles "game:\foo\bar" and "C:\foo\bar".
    for (u32 i = 0; buf[i]; ++i) {
        if ((buf[i] == '\\' || buf[i] == '/') && i > 0) {
            char saved = buf[i];
            // Do not terminate on the drive colon's separator (C:\).
            if (!(i == 2 && buf[1] == ':')) {
                buf[i] = 0;
                CreateOneDir(buf);
                buf[i] = saved;
            }
        }
    }
    return CreateOneDir(buf);
}

} // namespace ch360
