# Lookback native code: the present hook and the only apps it may be injected into.
# Build with native/build.ps1 (Visual Studio 2022, x64). Output: native/out/bin/<Config>/.
cmake_minimum_required(VERSION 3.24)
project(lookback_native LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
  message(FATAL_ERROR "The Lookback hook and test apps are Windows/MSVC only.")
endif()
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Build x64 only (the recorder and the test apps are 64-bit).")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Hook and test apps land in one folder; the injector looks for them there.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin/$<CONFIG>)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin/$<CONFIG>)

add_compile_options(/W4 /WX /permissive- /utf-8 /Zc:__cplusplus /EHsc /guard:cf /sdl)
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
add_link_options(/GUARD:CF /DYNAMICBASE /NXCOMPAT)

add_subdirectory(hook)
add_subdirectory(testapps)
