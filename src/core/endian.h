#ifndef CH360_CORE_ENDIAN_H
#define CH360_CORE_ENDIAN_H

#include "core/types.h"
#include <string.h>

// Explicit endian readers. These never cast serialized bytes to a host
// integer, so they behave identically on little-endian x86 Windows and
// big-endian PowerPC Xbox 360.

namespace ch360 {

inline u16 ReadU16LE(const u8* p) {
    return (u16)((u16)p[0] | ((u16)p[1] << 8));
}

inline u32 ReadU32LE(const u8* p) {
    return (u32)p[0] | ((u32)p[1] << 8) | ((u32)p[2] << 16) | ((u32)p[3] << 24);
}

inline u64 ReadU64LE(const u8* p) {
    u64 lo = ReadU32LE(p);
    u64 hi = ReadU32LE(p + 4);
    return lo | (hi << 32);
}

inline s16 ReadS16LE(const u8* p) { return (s16)ReadU16LE(p); }
inline s32 ReadS32LE(const u8* p) { return (s32)ReadU32LE(p); }
inline s64 ReadS64LE(const u8* p) { return (s64)ReadU64LE(p); }

inline f32 ReadF32LE(const u8* p) {
    u32 bits = ReadU32LE(p);
    f32 value;
    memcpy(&value, &bits, sizeof(value));
    return value;
}

inline f64 ReadF64LE(const u8* p) {
    u64 bits = ReadU64LE(p);
    f64 value;
    memcpy(&value, &bits, sizeof(value));
    return value;
}

inline u16 ReadU16BE(const u8* p) {
    return (u16)(((u16)p[0] << 8) | (u16)p[1]);
}

inline u32 ReadU32BE(const u8* p) {
    return ((u32)p[0] << 24) | ((u32)p[1] << 16) | ((u32)p[2] << 8) | (u32)p[3];
}

inline u64 ReadU64BE(const u8* p) {
    u64 hi = ReadU32BE(p);
    u64 lo = ReadU32BE(p + 4);
    return (hi << 32) | lo;
}

inline void WriteU16LE(u8* p, u16 v) {
    p[0] = (u8)(v & 0xFF);
    p[1] = (u8)((v >> 8) & 0xFF);
}

inline void WriteU32LE(u8* p, u32 v) {
    p[0] = (u8)(v & 0xFF);
    p[1] = (u8)((v >> 8) & 0xFF);
    p[2] = (u8)((v >> 16) & 0xFF);
    p[3] = (u8)((v >> 24) & 0xFF);
}

inline void WriteU64LE(u8* p, u64 v) {
    WriteU32LE(p, (u32)(v & 0xFFFFFFFFu));
    WriteU32LE(p + 4, (u32)(v >> 32));
}

inline void WriteU16BE(u8* p, u16 v) {
    p[0] = (u8)((v >> 8) & 0xFF);
    p[1] = (u8)(v & 0xFF);
}

inline void WriteU32BE(u8* p, u32 v) {
    p[0] = (u8)((v >> 24) & 0xFF);
    p[1] = (u8)((v >> 16) & 0xFF);
    p[2] = (u8)((v >> 8) & 0xFF);
    p[3] = (u8)(v & 0xFF);
}

inline bool HostIsBigEndian() {
    u32 probe = 0x11223344u;
    return *(const u8*)&probe == 0x11;
}

inline u16 HostToLE16(u16 v) { return HostIsBigEndian() ? (u16)((v >> 8) | (v << 8)) : v; }
inline u32 HostToLE32(u32 v) {
    if (!HostIsBigEndian()) return v;
    return ((v & 0x000000FFu) << 24) | ((v & 0x0000FF00u) << 8) |
           ((v & 0x00FF0000u) >> 8)  | ((v & 0xFF000000u) >> 24);
}
inline u64 HostToLE64(u64 v) {
    if (!HostIsBigEndian()) return v;
    return ((u64)HostToLE32((u32)(v & 0xFFFFFFFFu)) << 32) |
           (u64)HostToLE32((u32)(v >> 32));
}

} // namespace ch360

#endif // CH360_CORE_ENDIAN_H
