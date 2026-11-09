# Cuphead360's Xbox 360 build on the open xSDK (github.com/xenosworks/xsdk)
# and vulkan4xenon. console/xsdk/build.sh writes the shaders and configures
# this with xsdk's toolchain file.
cmake_minimum_required(VERSION 3.22)
project(cuphead360 C CXX ASM)

if(NOT XSDK)
    message(FATAL_ERROR "Configure with -DCMAKE_TOOLCHAIN_FILE=<xsdk>/cmake/xsdk.cmake")
endif()

set(VULKAN4XENON "" CACHE PATH "vulkan4xenon checkout, with build/libvulkan4xenon.a built")
set(CH360_SHADERS "" CACHE PATH "the shaders' folder: ch360_shaders.c/.h (build.sh)")
set(CH360_PLAYER_C "" CACHE PATH "the translated player (work/managed/c, tools/build_managed.py --stage wasm,c)")
set(CH360_RUNTIME "" CACHE PATH "the wasm runtime and w2c2 headers: a Spire360 checkout")
set(CH360_GIT_COMMIT "" CACHE STRING "Source commit for the log's first line")
set(CH360_TITLE_ID "0x43483630" CACHE STRING "The XEX's title ID ('CH60')")

get_filename_component(CONSOLE "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)

# A title runs in 32-bit mode, where a record form sets CR0 from its result's
# low word: GCC's record forms of 64-bit values get a 64-bit compare after
# them (record_forms.py; a change to it needs a clean build).
set(CMAKE_C_COMPILER_LAUNCHER python3 "${CMAKE_CURRENT_LIST_DIR}/record_forms.py")
set(CMAKE_CXX_COMPILER_LAUNCHER python3 "${CMAKE_CURRENT_LIST_DIR}/record_forms.py")

# char is signed and no strict aliasing (w2c2's C expects both); no fused
# multiply-adds, so floats match the PC build's.
set(COMMON_FLAGS -O2 -fsigned-char -fno-strict-aliasing -ffp-contract=off -ffunction-sections -fdata-sections)

# The translated player and its runtime (w2c2's C, the wasm runtime on xsdk's
# POSIX calls), started by ch360_imports.c.
file(GLOB PLAYER_SOURCES "${CH360_PLAYER_C}/*.c")
add_library(ch360_player STATIC ${PLAYER_SOURCES}
    "${CH360_RUNTIME}/runtime/src/srt_core.c" "${CH360_RUNTIME}/runtime/src/srt_wasi.c"
    "${CH360_RUNTIME}/runtime/src/srt_wasi_p2.c" "${CH360_RUNTIME}/runtime/src/srt_plat_posix.c"
    "${CONSOLE}/ch360_imports.c" "${CONSOLE}/ch360_ogg.c" "${CONSOLE}/ch360_pack.c")
# console/w2c2/w2c2_base.h comes first: Spire360's copy with 64-bit memory
# accesses as two 32-bit ones.
target_include_directories(ch360_player PRIVATE "${CONSOLE}/w2c2"
    "${CH360_RUNTIME}/runtime/include" "${CH360_RUNTIME}/runtime/src" "${CH360_RUNTIME}/third_party/w2c2/w2c2"
    "${CH360_PLAYER_C}" "${CONSOLE}")
target_compile_definitions(ch360_player PRIVATE W2C2_HOST_MEMORY)
# No unwind tables for the C: nothing unwinds through it (the runtime traps with longjmp; an uncaught
# exception ends the title either way), and they were 10 MB of the image the console keeps in memory.
target_compile_options(ch360_player PRIVATE ${COMMON_FLAGS} -std=gnu99 -w -fno-asynchronous-unwind-tables -fno-unwind-tables)
# The functions that rarely run (the C stage's split with --cold-os: tools/hot_functions.py's list of those sampled
# over every level, the rest in <file>_cold.c) compiled for size: 12-16% smaller, the console keeping all code
# resident. Off by default: the first build with it stopped at start-up on the console (hw31).
file(GLOB PLAYER_COLD "${CH360_PLAYER_C}/*_cold.c")
set_source_files_properties(${PLAYER_COLD} PROPERTIES COMPILE_OPTIONS "-Os")

add_executable(cuphead360 "${CONSOLE}/xsdk/main.cpp" "${CONSOLE}/xsdk/big_alloc.c" "${CONSOLE}/xsdk/probe64.c"
    "${CONSOLE}/xsdk/probe64_raw.S" "${CH360_SHADERS}/ch360_shaders.c")
target_include_directories(cuphead360 PRIVATE "${CONSOLE}" "${CH360_SHADERS}")
target_include_directories(cuphead360 SYSTEM PRIVATE
    "${VULKAN4XENON}/include" "${VULKAN4XENON}/third_party/vulkan-headers/include")
target_compile_options(cuphead360 PRIVATE ${COMMON_FLAGS})
set_source_files_properties("${CONSOLE}/xsdk/main.cpp" PROPERTIES COMPILE_OPTIONS
    "-std=gnu++11;-Wall;-Wextra;-Wno-unused-parameter;-Wno-missing-field-initializers;-Wno-unused-function")
set_source_files_properties("${CH360_SHADERS}/ch360_shaders.c" PROPERTIES COMPILE_OPTIONS "-std=gnu99;-w")
set_source_files_properties("${CONSOLE}/xsdk/big_alloc.c" "${CONSOLE}/xsdk/probe64.c" PROPERTIES COMPILE_OPTIONS
    "-std=gnu99;-Wall;-Wextra")
if(CH360_GIT_COMMIT)
    target_compile_definitions(cuphead360 PRIVATE CH360_GIT_COMMIT="${CH360_GIT_COMMIT}")
endif()
# The 64-bit test build (build.sh with CH360_TEST64=1: cuphead360_test.xex): its own log, the player started
# with --test64 (Test64.cs logs every result and stops before the saves).
option(CH360_TEST64 "the 64-bit test build" OFF)
if(CH360_TEST64)
    target_compile_definitions(cuphead360 PRIVATE CH360_TEST64)
endif()

target_link_libraries(cuphead360 PRIVATE ch360_player "${VULKAN4XENON}/build/libvulkan4xenon.a")
set_property(TARGET cuphead360 APPEND PROPERTY LINK_DEPENDS "${VULKAN4XENON}/build/libvulkan4xenon.a")
target_link_options(cuphead360 PRIVATE "-Wl,--gc-sections,--no-warn-execstack,-Map,${CMAKE_CURRENT_BINARY_DIR}/cuphead360.map")
# Big blocks from the kernel's pages, given back whole (big_alloc.c).
target_link_options(cuphead360 PRIVATE "-Wl,--wrap=malloc,--wrap=free,--wrap=realloc,--wrap=calloc")
# The runtime's waits, sleeps and file calls pass through main.cpp, which marks
# and counts them in the log's heartbeat.
target_link_options(cuphead360 PRIVATE "-Wl,--wrap=srt_clock_wait_until,--wrap=srt_plat_sleep_ns"
    "-Wl,--wrap=srt_plat_open,--wrap=srt_plat_read,--wrap=srt_plat_seek")
# The player's memory growth makes room from textures first (main.cpp).
target_link_options(cuphead360 PRIVATE "-Wl,--wrap=srt_plat_commit")
xsdk_add_xex(cuphead360 TITLE_ID ${CH360_TITLE_ID} NAME "Cuphead360")
