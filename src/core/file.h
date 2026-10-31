#ifndef CH360_CORE_FILE_H
#define CH360_CORE_FILE_H

#include "core/types.h"

namespace ch360 {

void* FileOpenRead(const char* path);
void* FileOpenWrite(const char* path);
void  FileClose(void* handle);
void  FileFlush(void* handle);
bool  FileWriteHandle(void* handle, const void* data, u32 size);
bool  FileReadHandle(void* handle, void* data, u32 size, u32* bytes_read);
u32   FileSizeHandle(void* handle);

bool FileExists(const char* path);
u32  FileSize(const char* path);

// Returns a buffer allocated with MemAlloc (or null). The caller frees it
// with MemFree. The buffer has one extra zero byte.
u8*  FileReadAll(const char* path, u32* out_size);
bool FileWriteAll(const char* path, const void* data, u32 size);

// Creates the parent directories for a path, best effort. Handles both
// '\\' and '/' separators.
bool FileEnsureParentDir(const char* path);

} // namespace ch360

#endif // CH360_CORE_FILE_H
