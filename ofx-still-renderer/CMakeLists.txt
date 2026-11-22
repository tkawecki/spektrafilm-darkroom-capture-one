cmake_minimum_required(VERSION 3.24)
project(SpektraStillRenderer LANGUAGES CXX)

set(OFX_SOURCE "${CMAKE_CURRENT_SOURCE_DIR}/../upstream-ofx" CACHE PATH "Public OFX source checkout")
set(OFX_BUILD "${CMAKE_CURRENT_SOURCE_DIR}/../build-ofx-harness" CACHE PATH "Built upstream Vulkan harness")
set(GENERATED_DIR "${OFX_BUILD}/generated")
set(HARNESS_RESOURCES "${OFX_BUILD}/Release")
find_package(Vulkan REQUIRED)

add_executable(SpektraStillRenderer main.cpp
    "${OFX_SOURCE}/src/SpektraVulkanRenderer.cpp"
    "${GENERATED_DIR}/SpektraGeneratedProfileCurves.cpp")
target_compile_features(SpektraStillRenderer PRIVATE cxx_std_17)
target_compile_definitions(SpektraStillRenderer PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_include_directories(SpektraStillRenderer PRIVATE "${OFX_SOURCE}/src" "${GENERATED_DIR}")
target_link_libraries(SpektraStillRenderer PRIVATE Vulkan::Vulkan)
if(MSVC)
    set_property(TARGET SpektraStillRenderer PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

add_custom_command(TARGET SpektraStillRenderer POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_directory
        "${HARNESS_RESOURCES}/shaders" "$<TARGET_FILE_DIR:SpektraStillRenderer>/shaders"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
        "${HARNESS_RESOURCES}/SpektraHanatos2025Spectra.f32"
        "${HARNESS_RESOURCES}/SpektraOutputGamutCompression.f32"
        "$<TARGET_FILE_DIR:SpektraStillRenderer>"
    VERBATIM)
