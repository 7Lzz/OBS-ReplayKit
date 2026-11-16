cmake_minimum_required(VERSION 3.28)
project(replaykit_share LANGUAGES CXX RC)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# the static runtime, so the window needs nothing installed beside it and keeps running while an update replaces other files
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

set(REPLAYKIT_VERSION "" CACHE STRING "the release version stamped into the exe")
set(REPLAYKIT_ICON_DIR "" CACHE PATH "the folder of replaykit.ico")
if(NOT REPLAYKIT_VERSION OR NOT REPLAYKIT_ICON_DIR)
    message(FATAL_ERROR "build thru build.ps1, which passes the version and the icon folder")
endif()
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
    src/control_pipe.cpp
    src/log.cpp
    src/renderer.cpp
    src/security.cpp
    src/shared_state.cpp
    src/window.cpp
    share-window.rc
    share-window.manifest
)
target_include_directories(ReplayKitShare PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/protocol"
)
target_compile_definitions(ReplayKitShare PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX
    REPLAYKIT_VERSION="${REPLAYKIT_VERSION}" REPLAYKIT_VERSION_COMMA=${REPLAYKIT_VERSION_COMMA})
set_source_files_properties(share-window.rc PROPERTIES INCLUDE_DIRECTORIES "${REPLAYKIT_ICON_DIR}")
# the same sources make the same exe (no link time stamp, the pdb named without its folder), since an update ends a running window whose exe changed
target_compile_options(ReplayKitShare PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/W4 /permissive- /utf-8 /EHsc /Zi /Brepro>)
target_link_options(ReplayKitShare PRIVATE /DEBUG /OPT:REF /OPT:ICF /Brepro /PDBALTPATH:%_PDB%)
target_link_libraries(ReplayKitShare PRIVATE d3d11 dxgi d3dcompiler windowscodecs ole32 advapi32 user32 avrt winmm)
