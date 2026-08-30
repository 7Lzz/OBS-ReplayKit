cmake_minimum_required(VERSION 3.28)
project(replaykit-tray LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(CMAKE_AUTOMOC ON)

find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets)

add_library(replaykit-tray MODULE replaykit.cpp)

target_include_directories(replaykit-tray PRIVATE
    "${OBS_HEADERS_DIR}/libobs"
    "${OBS_HEADERS_DIR}/frontend/api"
)

target_link_libraries(replaykit-tray PRIVATE
    Qt6::Core
    Qt6::Gui
    Qt6::Widgets
    ws2_32
    ole32
    shell32
    "${OBS_IMPLIB_DIR}/obs.lib"
    "${OBS_IMPLIB_DIR}/obs-frontend-api.lib"
)

set_target_properties(replaykit-tray PROPERTIES
    PREFIX ""
    OUTPUT_NAME "replaykit"
)
