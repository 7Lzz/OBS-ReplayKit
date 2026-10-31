cmake_minimum_required(VERSION 3.28)
project(replaykit LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(CMAKE_AUTOMOC ON)

find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets Svg)

# sources are listed by hand so a new file is a reviewed change and nothing stray ends up in the dll
set(REPLAYKIT_SOURCES
    src/plugin.cpp
    src/overlays/bongo.cpp
    src/overlays/bongo_input.cpp
    src/overlays/bongo_model.cpp

    src/app/frontend_events.cpp
    src/app/ipc_commands.cpp
    src/app/main_window_filter.cpp
    src/app/clip_buffer.cpp
    src/app/capture_context.cpp
    src/app/obs_actions.cpp
    src/app/obs_menu.cpp
    src/app/sdk_commands.cpp
    src/app/settings_poll.cpp

    src/chrome/caption_track_filter.cpp
    src/chrome/title_keybind_badge.cpp

    src/core/app_icon.cpp
    src/core/app_version.cpp
    src/core/async.cpp
    src/core/cef_host.cpp
    src/core/crash_reporter.cpp
    src/core/dev_log.cpp
    src/core/draw_util.cpp
    src/core/helper_client.cpp
    src/core/idle_memory.cpp
    src/core/ipc_pipe.cpp
    src/core/json_lite.cpp
    src/core/keybind_label.cpp
    src/core/main_window.cpp
    src/core/open_clips_hotkey.cpp
    src/core/process_group.cpp
    src/core/state.cpp
    src/core/theme.cpp
    src/core/window_state.cpp

    src/layout/controls_dock.cpp
    src/layout/ui_layout.cpp

    src/notify/notify_button.cpp
    src/notify/notify_panel.cpp
    src/notify/notify_tip.cpp

    src/shell/shell.cpp
    src/shell/shell_account.cpp
    src/shell/shell_badge.cpp
    src/shell/shell_curtain.cpp
    src/shell/shell_frame.cpp
    src/shell/shell_grip.cpp
    src/shell/shell_history.cpp
    src/shell/shell_layout.cpp
    src/shell/shell_nav_button.cpp
    src/shell/shell_obs_nav.cpp
    src/shell/shell_obs_settings.cpp
    src/shell/shell_hotkey_menu.cpp
    src/shell/shell_quick_menu.cpp
    src/shell/shell_page.cpp
    src/shell/shell_projector.cpp
    src/shell/shell_projector_bar.cpp
    src/shell/shell_rail.cpp
    src/shell/shell_state.cpp
    src/shell/shell_storage.cpp
    src/shell/shell_storage_modal.cpp
    src/shell/shell_modal_card.cpp
    src/shell/shell_notify_reader.cpp
    src/shell/shell_title_bar.cpp
    src/shell/shell_title_button.cpp
    src/shell/shell_views.cpp
    src/shell/shell_win_button.cpp

    src/tray/tray_menu.cpp

    src/ui/anchored_panel.cpp
    src/ui/popup_host.cpp
    src/ui/confirm_dialog.cpp

    src/windows/cef_host_window.cpp
    src/windows/clips_window.cpp
    src/windows/obs_settings.cpp
    src/windows/settings_window.cpp
    src/windows/setup_window.cpp
)

add_library(replaykit MODULE ${REPLAYKIT_SOURCES})

target_include_directories(replaykit PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/third_party/obs-browser"
    "${OBS_HEADERS_DIR}/libobs"
    "${OBS_HEADERS_DIR}/frontend/api"
)

# every source starts with the prelude include, so the shared headers are compiled once instead of per file
target_precompile_headers(replaykit PRIVATE src/prelude.hpp)

target_link_libraries(replaykit PRIVATE
    Qt6::Core
    Qt6::Gui
    Qt6::Widgets
    Qt6::Svg
    ws2_32
    ole32
    shell32
    dwmapi
    iphlpapi
    psapi
    "${OBS_IMPLIB_DIR}/obs.lib"
    "${OBS_IMPLIB_DIR}/obs-frontend-api.lib"
)

set_target_properties(replaykit PROPERTIES
    PREFIX ""
    OUTPUT_NAME "replaykit"
)

if(MSVC)
    target_compile_options(replaykit PRIVATE /Zi)
    target_link_options(replaykit PRIVATE /DEBUG:FULL /OPT:REF /OPT:ICF)
endif()
