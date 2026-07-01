cmake_minimum_required(VERSION 3.22.1)

project(hoshidicts_c_api LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Link runtime libraries statically to avoid DLL dependencies
if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "Clang" AND WIN32))
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -static")
    set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -static")
endif()

# Build hoshidicts as a static library first
set(ZSTD_BUILD_PROGRAMS OFF CACHE BOOL "")
set(ZSTD_BUILD_TESTS OFF CACHE BOOL "")
set(ZSTD_BUILD_SHARED OFF CACHE BOOL "")
set(ZSTD_BUILD_STATIC ON CACHE BOOL "")
set(LIBDEFLATE_BUILD_SHARED_LIB OFF CACHE BOOL "")
set(LIBDEFLATE_BUILD_GZIP OFF CACHE BOOL "")

add_subdirectory(../hoshidicts ${CMAKE_BINARY_DIR}/hoshidicts)

add_library(hoshidicts_c_api SHARED
    hoshidicts_c_api.cpp
)

target_include_directories(hoshidicts_c_api PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../hoshidicts/include
)

target_link_libraries(hoshidicts_c_api PRIVATE
    hoshidicts
)

if(WIN32)
    target_link_libraries(hoshidicts_c_api PRIVATE ws2_32)
endif()

target_compile_definitions(hoshidicts_c_api PRIVATE
    HOSHIDICTS_C_API_EXPORTS
)

# On Windows, produce .dll; on Linux/macOS, produce .so/.dylib
set_target_properties(hoshidicts_c_api PROPERTIES
    OUTPUT_NAME hoshidicts_c_api
    PREFIX ""
)
