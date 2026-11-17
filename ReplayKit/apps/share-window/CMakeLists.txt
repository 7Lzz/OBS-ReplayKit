cmake_minimum_required(VERSION 3.28)
project(replaykit_share LANGUAGES CXX RC)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# the static runtime, so the window needs nothing installed beside it and keeps running while an update replaces other files
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

set(REPLAYKIT_VERSION "" CACHE STRING "the release version stamped into the exe")
set(REPLAYKIT_ICON_DIR "" CACHE PATH "the folder of replaykit.ico and share/obs-swirl-mask.png")
set(REPLAYKIT_SHARE_MOTION "" CACHE FILEPATH "ui/_share-motion.html, the shader body of the moving theme ground")
if(NOT REPLAYKIT_VERSION OR NOT REPLAYKIT_ICON_DIR OR NOT REPLAYKIT_SHARE_MOTION)
    message(FATAL_ERROR "build thru build.ps1, which passes the version, the icon folder and the share motion shader")
endif()

# the moving theme ground is one shader body that the card editor runs as glsl, so its timing and its body are read from that page partial and written out as hlsl for fxc
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${REPLAYKIT_SHARE_MOTION}")
file(READ "${REPLAYKIT_SHARE_MOTION}" SHARE_MOTION_HTML)
if(NOT SHARE_MOTION_HTML MATCHES "<script([^>]*)>(.*)</script>")
    message(FATAL_ERROR "${REPLAYKIT_SHARE_MOTION} holds no shader script")
endif()
set(SHARE_MOTION_TAG "${CMAKE_MATCH_1}")
set(SHARE_MOTION_BODY "${CMAKE_MATCH_2}")
foreach(name IN ITEMS turn shine loop)
    if(NOT SHARE_MOTION_TAG MATCHES " data-${name}-seconds=\"([1-9][0-9]*)\"")
        message(FATAL_ERROR "the shader script of ${REPLAYKIT_SHARE_MOTION} needs a whole number of seconds in data-${name}-seconds")
    endif()
    set(SHARE_MOTION_${name} "${CMAKE_MATCH_1}")
endforeach()
if(NOT SHARE_MOTION_TAG MATCHES " data-mask-range=\"(0\\.[0-9]+)\"")
    message(FATAL_ERROR "the shader script of ${REPLAYKIT_SHARE_MOTION} needs the swirl mask range as a fraction in data-mask-range")
endif()
set(SHARE_MOTION_MASK_RANGE "${CMAKE_MATCH_1}")
math(EXPR SHARE_MOTION_TURN_REST "${SHARE_MOTION_loop} % ${SHARE_MOTION_turn}")
math(EXPR SHARE_MOTION_SHINE_REST "${SHARE_MOTION_loop} % ${SHARE_MOTION_shine}")
if(SHARE_MOTION_TURN_REST OR SHARE_MOTION_SHINE_REST)
    message(FATAL_ERROR "data-loop-seconds must hold a whole number of turns and of shine slots, or the motion jumps where the clock wraps")
endif()
# static, since a global hlsl const without it is a constant buffer value that nothing fills and reads as 0
file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/share_motion.hlsl"
    CONTENT [=[static const float SwirlTurnSeconds = @SHARE_MOTION_turn@.0;
static const float ShineInterval = @SHARE_MOTION_shine@.0;
static const float SwirlLoopSeconds = @SHARE_MOTION_loop@.0;
static const float SwirlMaskRange = @SHARE_MOTION_MASK_RANGE@;
@SHARE_MOTION_BODY@]=] @ONLY)

# the shaders are compiled here with warnings as errors, so a shader mistake fails the build instead of drawing wrong on the stream
find_program(REPLAYKIT_FXC fxc REQUIRED)
set(SHARE_SHADER_HEADERS)
foreach(shader IN ITEMS "VsMain|vs_4_0" "PsMain|ps_4_0" "PsGround|ps_4_0")
    string(REPLACE "|" ";" shader "${shader}")
    list(GET shader 0 entry)
    list(GET shader 1 profile)
    set(header "${CMAKE_CURRENT_BINARY_DIR}/shader_${entry}.h")
    add_custom_command(OUTPUT "${header}"
        COMMAND "${REPLAYKIT_FXC}" /nologo /WX /O3 /T ${profile} /E ${entry} /Vn kShader${entry} /I "${CMAKE_CURRENT_BINARY_DIR}" /Fh "${header}" "${CMAKE_CURRENT_SOURCE_DIR}/src/share.hlsl"
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/src/share.hlsl" "${CMAKE_CURRENT_BINARY_DIR}/share_motion.hlsl"
        VERBATIM)
    list(APPEND SHARE_SHADER_HEADERS "${header}")
endforeach()
if(NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/protocol/share_window_values.hpp")
    message(FATAL_ERROR "protocol/share_window_values.hpp is missing; build.ps1 writes it thru scripts/sync_shared.ps1")
endif()
# the version resource takes four numbers
string(REPLACE "." ";" REPLAYKIT_VERSION_PARTS "${REPLAYKIT_VERSION}")
list(LENGTH REPLAYKIT_VERSION_PARTS REPLAYKIT_VERSION_COUNT)
while(REPLAYKIT_VERSION_COUNT LESS 4)
    list(APPEND REPLAYKIT_VERSION_PARTS 0)
    math(EXPR REPLAYKIT_VERSION_COUNT "${REPLAYKIT_VERSION_COUNT} + 1")
endwhile()
string(REPLACE ";" "," REPLAYKIT_VERSION_COMMA "${REPLAYKIT_VERSION_PARTS}")

# sources are listed by hand so a new file is a reviewed change
add_executable(ReplayKitShare WIN32
    src/main.cpp
    src/app.cpp
    src/audio_out.cpp
    src/card.cpp
    src/card_video.cpp
    src/control_pipe.cpp
    src/log.cpp
    src/renderer.cpp
    src/security.cpp
    src/shared_state.cpp
    src/window.cpp
    share-window.rc
    share-window.manifest
    ${SHARE_SHADER_HEADERS}
)
target_include_directories(ReplayKitShare PRIVATE
    "${CMAKE_CURRENT_BINARY_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/protocol"
)
target_compile_definitions(ReplayKitShare PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
    REPLAYKIT_VERSION="${REPLAYKIT_VERSION}" REPLAYKIT_VERSION_COMMA=${REPLAYKIT_VERSION_COMMA} SHARE_MOTION_LOOP_SECONDS=${SHARE_MOTION_loop}.0)
set_source_files_properties(share-window.rc PROPERTIES
    INCLUDE_DIRECTORIES "${REPLAYKIT_ICON_DIR}"
    OBJECT_DEPENDS "${REPLAYKIT_ICON_DIR}/share/obs-swirl-mask.png")
# the same sources make the same exe (no link time stamp, the pdb named without its folder), since an update ends a running window whose exe changed
target_compile_options(ReplayKitShare PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/W4 /permissive- /utf-8 /EHsc /Zi /Brepro>)
target_link_options(ReplayKitShare PRIVATE /DEBUG /OPT:REF /OPT:ICF /Brepro /PDBALTPATH:%_PDB%)
target_link_libraries(ReplayKitShare PRIVATE d3d11 dxgi windowscodecs ole32 advapi32 user32 avrt winmm dwmapi mfplat mfreadwrite mfuuid)
