add_executable(replaykit-host WIN32 "${REPLAYKIT_HOST_DIR}/main.cpp" "${REPLAYKIT_HOST_DIR}/host.manifest"
  "${CMAKE_BINARY_DIR}/frontend/obs.rc")
target_compile_features(replaykit-host PRIVATE cxx_std_17)
target_include_directories(replaykit-host PRIVATE
  "${CMAKE_SOURCE_DIR}/frontend"
  "C:/Program Files (x86)/Windows Kits/NETFXSDK/4.8/Include/um")
target_link_directories(replaykit-host PRIVATE "C:/Program Files (x86)/Windows Kits/NETFXSDK/4.8/Lib/um/x64")
target_link_libraries(replaykit-host PRIVATE shell32 ole32 mscoree)
set_target_properties(replaykit-host PROPERTIES OUTPUT_NAME obs64 RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/host/$<CONFIG>")
add_dependencies(replaykit-host obs-studio obs-browser obs-browser-helper)
