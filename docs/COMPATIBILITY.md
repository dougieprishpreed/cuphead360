# Xbox 360 compatibility notes

## Verified toolchain facts

Verified by compiling and running the existing Spire360 `hello` XEX on this
machine (Xenia report available locally):

- XDK compiler: `_MSC_VER=1600` (Visual Studio 2010, PowerPC).
- `short` 2, `int` 4, `long` 4, `long long` 8, pointer 4, `size_t` 4,
  `wchar_t` 2.
- Xbox 360 is big-endian (first byte of `0x11223344` is `0x11`).
- 64-bit arithmetic works; note the XDK/x86 difference in signed remainder
  for negative operands observed in the reference hello test.
- `_byteswap_ulong` and `__loadwordbytereverse` work, including unaligned
  `lwbrx` loads on this toolchain/emulator.
- `VirtualAlloc` reserve/commit works; ~491 MB free physical reported.
- D3D9 device creation at 1280x720 works.
- XInput works.
- Writing `game:\...` works under Xenia.

## Language subset

Production code targets C++03:

- no `auto`, `nullptr`, range-for, lambdas, `std::unique_ptr`, variadic
  templates, or C++11 library facilities;
- no exceptions/RTTI in shared code unless verified on the XDK;
- fixed-width types from `src/core/types.h`;
- explicit little-endian/big-endian loads for serialized data;
- avoid x86 intrinsics and unaligned multi-byte host accesses.

## Compiler checks

Every significant change must build both targets. The XDK compiler is the
authority; a clean MSVC x86 build is necessary but not sufficient.

## Known platform constraints

- 512 MB shared memory; assets must stream.
- 32-bit address space; reserve large regions carefully.
- Big-endian CPU; all original data is little-endian.
- No virtual memory assumptions; allocation failure must be handled.
- Thread scheduling differs from Windows; shared code must not assume
  desktop thread priorities or timing.
- GPU is Xenos/D3D9-class; shader translation is required for effects that
  cannot be expressed directly.

## Current compile status

See [STATUS.md](STATUS.md) for the current summary and
[VERIFICATION.md](VERIFICATION.md) for versioned execution evidence.
