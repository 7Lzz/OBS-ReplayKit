cmake_minimum_required(VERSION 3.28)
project(replaykit-tray LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(CMAKE_AUTOMOC ON)

find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets Svg)

add_library(replaykit-tray MODULE replaykit.cpp)

target_include_directories(replaykit-tray PRIVATE
    "${OBS_HEADERS_DIR}/libobs"
    "${OBS_HEADERS_DIR}/frontend/api"
)

target_link_libraries(replaykit-tray PRIVATE
    Qt6::Core
    Qt6::Gui
    Qt6::Widgets
    Qt6::Svg
    ws2_32
    ole32
    shell32
    dwmapi
    "${OBS_IMPLIB_DIR}/obs.lib"
    "${OBS_IMPLIB_DIR}/obs-frontend-api.lib"
)

set_target_properties(replaykit-tray PROPERTIES
    PREFIX ""
    OUTPUT_NAME "replaykit"
)

if(MSVC)
    target_compile_options(replaykit-tray PRIVATE /Zi)
    target_link_options(replaykit-tray PRIVATE /DEBUG:FULL /OPT:REF /OPT:ICF)
endif()
