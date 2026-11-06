# builds input-overlay from the upstream source build.ps1 unpacks (IO_SRC) the way replaykit.dll is built, against the headers and import libs of the oldest obs line, instead of thru the obs plugin template whose bootstrap fetches its own obs sources; the flags are the ones that template sets on windows
cmake_minimum_required(VERSION 3.28)
project(input-overlay LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Qt6 REQUIRED COMPONENTS Core Widgets)

add_compile_options(/W3 /utf-8 /MP /Gy /d2FH4-)
add_compile_definitions(UNICODE _UNICODE _CRT_SECURE_NO_WARNINGS _CRT_NONSTDC_NO_WARNINGS)
add_link_options(/OPT:REF /OPT:ICF /INCREMENTAL:NO /Brepro)

# libuiohooks own cmake file asks for cmake 4.2, and on windows it is these files built into a shared library
set(UIOHOOK_DIR "${IO_SRC}/deps/libuiohook")
add_library(uiohook SHARED
    "${UIOHOOK_DIR}/src/logger.c"
    "${UIOHOOK_DIR}/src/windows/dispatch_event.c"
    "${UIOHOOK_DIR}/src/windows/input_helper.c"
    "${UIOHOOK_DIR}/src/windows/input_hook.c"
    "${UIOHOOK_DIR}/src/windows/monitor_helper.c"
    "${UIOHOOK_DIR}/src/windows/post_event.c"
    "${UIOHOOK_DIR}/src/windows/system_properties.c"
    "${UIOHOOK_DIR}/src/windows/unused_functions.c"
)
set_target_properties(uiohook PROPERTIES C_STANDARD 17 C_STANDARD_REQUIRED ON WINDOWS_EXPORT_ALL_SYMBOLS ON)
target_include_directories(uiohook PUBLIC "${UIOHOOK_DIR}/include" PRIVATE "${UIOHOOK_DIR}/src" "${UIOHOOK_DIR}/src/windows")
target_link_libraries(uiohook PRIVATE Advapi32)

set(IO "${IO_SRC}/src")
add_library(input-overlay MODULE
    "${IO}/input_overlay.cpp"
    "${IO}/sources/input_source.cpp"
    "${IO}/hook/sdl_gamepad.cpp"
    "${IO}/hook/gamepad_hook_helper.cpp"
    "${IO}/hook/uiohook_helper_win.cpp"
    "${IO}/gui/io_settings_dialog.cpp"
    "${IO}/gui/io_settings_dialog.hpp"
    "${IO}/util/obs_util.cpp"
    "${IO}/util/overlay.cpp"
    "${IO}/util/config.cpp"
    "${IO}/util/input_filter.cpp"
    "${IO}/util/window_helper_win.cpp"
    "${IO}/util/element/element.cpp"
    "${IO}/util/element/element_texture.cpp"
    "${IO}/util/element/element_button.cpp"
    "${IO}/util/element/element_mouse_wheel.cpp"
    "${IO}/util/element/element_trigger.cpp"
    "${IO}/util/element/element_analog_stick.cpp"
    "${IO}/util/element/element_gamepad_id.cpp"
    "${IO}/util/element/element_mouse_movement.cpp"
    "${IO}/util/element/element_dpad.cpp"
    "${IO}/network/remote_connection.cpp"
    "${IO}/network/mg.cpp"
    "${IO}/network/websocket_server.cpp"
    "${IO_SRC}/deps/common/input_data.cpp"
    "${IO_SRC}/deps/mongoose/mongoose.c"
)

# moc and uic only for the plugin, with its one Q_OBJECT header listed: left to find it from the includes, the generated moc was sometimes compiled before it was written
set_target_properties(input-overlay PROPERTIES AUTOMOC ON AUTOUIC ON)

target_compile_definitions(input-overlay PRIVATE
    GIT_BRANCH="${IO_VERSION}"
    GIT_COMMIT_HASH="source release"
    BUILD_TIME="ReplayKit build"
)

target_include_directories(input-overlay PRIVATE
    "${IO}"
    "${IO_SRC}/deps/sdl3/include"
    "${IO_SRC}/deps/common"
    "${IO_SRC}/deps/json11"
    "${IO_SRC}/deps/mongoose"
    "${OBS_HEADERS_DIR}/libobs"
    "${OBS_HEADERS_DIR}/frontend/api"
)

target_link_libraries(input-overlay PRIVATE
    Qt6::Core
    Qt6::Widgets
    uiohook
    "${IO_SRC}/deps/sdl3/bin/x64/SDL3.lib"
    ws2_32
    iphlpapi
    "${OBS_IMPLIB_DIR}/obs.lib"
    "${OBS_IMPLIB_DIR}/obs-frontend-api.lib"
)
