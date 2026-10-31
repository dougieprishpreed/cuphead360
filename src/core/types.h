#ifndef CH360_CORE_TYPES_H
#define CH360_CORE_TYPES_H

// Cuphead360 portable foundation types.
//
// Production code targets the Xbox 360 XDK compiler (Visual Studio 2010,
// PowerPC, C++03) and Visual Studio 2010 x86. Do not introduce C++11-only
// syntax or library facilities here.

#if defined(_XBOX)
#define CH360_PLATFORM_XBOX360 1
#elif defined(_WIN32)
#define CH360_PLATFORM_WIN32 1
#else
#error "Cuphead360 supports Win32 and Xbox 360 only"
#endif

#if defined(_MSC_VER)
#define CH360_COMPILER_MSVC 1
#endif

typedef unsigned char      u8;
typedef signed char        s8;
typedef unsigned short     u16;
typedef signed short       s16;
typedef unsigned int       u32;
typedef signed int         s32;
typedef unsigned __int64   u64;
typedef signed __int64     s64;
typedef float              f32;
typedef double             f64;

typedef unsigned int       uint;
typedef unsigned long      ulong;

#if defined(_XBOX)
typedef unsigned int       ch360_size_t;
#else
typedef unsigned int       ch360_size_t;
#endif

#define CH360_ARRAY_COUNT(a) (sizeof(a) / sizeof((a)[0]))

// A 32-bit handle/index that is invalid when equal to CH360_INVALID_INDEX.
#define CH360_INVALID_INDEX 0xFFFFFFFFu

namespace ch360 {

inline u32 MinU32(u32 a, u32 b) { return a < b ? a : b; }
inline u32 MaxU32(u32 a, u32 b) { return a > b ? a : b; }
inline s32 MinS32(s32 a, s32 b) { return a < b ? a : b; }
inline s32 MaxS32(s32 a, s32 b) { return a > b ? a : b; }
inline f32 MinF32(f32 a, f32 b) { return a < b ? a : b; }
inline f32 MaxF32(f32 a, f32 b) { return a > b ? a : b; }
inline f32 ClampF32(f32 v, f32 lo, f32 hi) { return v < lo ? lo : (v > hi ? hi : v); }
inline s32 ClampS32(s32 v, s32 lo, s32 hi) { return v < lo ? lo : (v > hi ? hi : v); }

// Rounds a byte count up to the given power-of-two alignment.
inline u32 AlignUp(u32 value, u32 alignment) {
    return (value + alignment - 1u) & ~(alignment - 1u);
}

inline bool IsPowerOfTwo(u32 v) { return v != 0 && (v & (v - 1u)) == 0; }

} // namespace ch360

#endif // CH360_CORE_TYPES_H
