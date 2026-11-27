add_library(lookback_hook SHARED hook.cpp capture_d3d11.cpp capture_d3d12.cpp hook_internal.h)
set_target_properties(lookback_hook PROPERTIES OUTPUT_NAME lookback-hook)
target_include_directories(lookback_hook PRIVATE ${CMAKE_SOURCE_DIR}/include)
# d3d11/d3d12 entry points are resolved with GetProcAddress, so loading the
# hook never pulls a runtime the app does not already use.
target_link_libraries(lookback_hook PRIVATE dxgi)
