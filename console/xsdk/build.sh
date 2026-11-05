#!/usr/bin/env bash
# Builds default.xex with the open xSDK toolchain (Linux, or WSL on Windows).
# Defaults: the CTR360-XSDK distro's shared checkouts in /opt/ctr360-xsdk
# (read only: their libraries must be built already), this project's output
# in /opt/cuphead360-xsdk. The translated player comes from work/managed/c
# (tools/build_managed.py --stage wasm,c); the wasm runtime from Spire360
# next to this repository.
#
#   [XSDK=... VULKAN4XENON=... GLSLANG=... XSDK_TOOLCHAIN=... CH360_RUNTIME=...] console/xsdk/build.sh
#
# JOBS compilers at once (default 2; GCC takes over 1 GB on each of the
# player's large files). Output: $CH360_XSDK_BUILD/default.xex (default
# /opt/cuphead360-xsdk/output), copied to work/console/xsdk/ with the
# licences it ships under.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DEPS=/opt/ctr360-xsdk
XSDK="${XSDK:-$DEPS/xsdk}"
VULKAN4XENON="${VULKAN4XENON:-$DEPS/vulkan4xenon}"
GLSLANG="${GLSLANG:-$DEPS/glslang/build-host/StandAlone/glslang}"
XSDK_TOOLCHAIN="${XSDK_TOOLCHAIN:-$DEPS/toolchain}"
CH360_RUNTIME="${CH360_RUNTIME:-$(cd "$ROOT/.." && pwd)/spire360}"
OUT="${CH360_XSDK_BUILD:-/opt/cuphead360-xsdk/output}"
PLAYER="$ROOT/work/managed/c"
JOBS="${JOBS:-2}"
mkdir -p "$OUT"
for f in "$XSDK/build/libxsdk.a" "$XSDK/build/host/libxsc.so" "$VULKAN4XENON/build/libvulkan4xenon.a" "$GLSLANG" \
  "$PLAYER/player.h" "$CH360_RUNTIME/runtime/src/srt_core.c"; do
  [ -e "$f" ] || { echo "missing $f" >&2; exit 1; }
done
echo "xsdk $(git -C "$XSDK" rev-parse --short HEAD), vulkan4xenon $(git -C "$VULKAN4XENON" rev-parse --short HEAD)"

# Shaders: GLSL -> SPIR-V -> Xenos microcode (glslang, then xost), into a C
# source the XEX embeds. Compiled again when a shader changes.
SH="$OUT/shaders"
mkdir -p "$SH"
cp "$ROOT"/console/xsdk/shaders/*.vert "$ROOT"/console/xsdk/shaders/*.frag "$SH/"
(
  cd "$SH"
  files="ch360.vert sprite.frag screen.frag multiply.frag overlay.frag sdf.frag lightray.frag mask.frag"
  stamp="$(cat $files "$VULKAN4XENON/tools/build_shaders.py" | sha256sum)"
  if [ ! -f ch360_shaders.c ] || [ "$(cat stamp 2>/dev/null)" != "$stamp" ]; then
    python3 "$VULKAN4XENON/tools/build_shaders.py" --xost "$XSDK/xenos-toolchain" --glslang "$GLSLANG" \
      --output ch360_shaders --optimize $files
    echo "$stamp" > stamp
  fi
)

COMMIT="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || true)"
if [ -n "$COMMIT" ] &&
  [ -n "$(git -C "$ROOT" -c core.autocrlf=input status --porcelain --untracked-files=no 2>/dev/null)" ]; then
  COMMIT="$COMMIT+"
fi

cmake -S "$ROOT/console/xsdk" -B "$OUT/cmake" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$XSDK/cmake/xsdk.cmake" -DXSDK_TOOLCHAIN="$XSDK_TOOLCHAIN" -DCMAKE_BUILD_TYPE=Release \
  -DVULKAN4XENON="$VULKAN4XENON" -DCH360_SHADERS="$SH" -DCH360_PLAYER_C="$PLAYER" -DCH360_RUNTIME="$CH360_RUNTIME" \
  -DCH360_GIT_COMMIT="$COMMIT"
cmake --build "$OUT/cmake" -j"$JOBS"

cp "$OUT/cmake/cuphead360.xex" "$OUT/default.xex"
DST="$ROOT/work/console/xsdk"
mkdir -p "$DST/licenses"
cp "$OUT/default.xex" "$DST/default.xex"
cp "$XSDK/LICENSE" "$DST/licenses/xsdk.txt"
cp "$XSDK/THIRD_PARTY_NOTICES.md" "$DST/licenses/xsdk-third-party.md" 2>/dev/null || true
cp "$VULKAN4XENON/LICENSE" "$DST/licenses/vulkan4xenon.txt"
cp "$XSDK_TOOLCHAIN"/share/*-licenses/* "$DST/licenses/" 2>/dev/null || true
echo "Built $DST/default.xex ($(stat -c %s "$OUT/default.xex") bytes, source $COMMIT)"
