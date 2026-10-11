cmake_minimum_required(VERSION 3.26)
if(POLICY CMP0141)
    cmake_policy(SET CMP0141 NEW)
    set(CMAKE_MSVC_DEBUG_INFORMATION_FORMAT Embedded)
endif()
project(gearth_renderer LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(MSVC)
    add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/EHsc>")
endif()
# The pinned SDL/fmt versions predate CMake 4's policy floor.
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
find_package(Vulkan REQUIRED)
find_program(GLSL_VALIDATOR glslangValidator HINTS "$ENV{VULKAN_SDK}/Bin" REQUIRED)
add_subdirectory(third_party)
add_executable(renderer src/main.cpp src/vk_engine.cpp src/globe.cpp src/procedural_world.cpp src/replay.cpp src/event_replay.cpp src/pipeline_replay.cpp src/town.cpp src/ao_bake.cpp src/vk_engine_bootstrap.cpp
    src/vk_engine_resources.cpp src/vk_engine_materials.cpp src/vk_loader.cpp
    src/vk_initializers.cpp src/vk_descriptors.cpp src/vk_pipelines.cpp src/vk_images.cpp)
target_include_directories(renderer PRIVATE src)
target_compile_definitions(renderer PRIVATE GLM_FORCE_DEPTH_ZERO_TO_ONE SDL_MAIN_HANDLED)
target_link_libraries(renderer PRIVATE Vulkan::Vulkan vma glm vkbootstrap SDL2::SDL2
    fmt::fmt stb_image fastgltf::fastgltf fastgltf_simdjson imgui)
file(GLOB shader_includes CONFIGURE_DEPENDS shaders/*.glsl)
file(GLOB shader_sources CONFIGURE_DEPENDS shaders/*.vert shaders/*.frag)
foreach(shader IN LISTS shader_sources)
    get_filename_component(name "${shader}" NAME)
    set(binary "${CMAKE_CURRENT_BINARY_DIR}/shaders/${name}.spv")
    add_custom_command(OUTPUT "${binary}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/shaders"
        COMMAND "${GLSL_VALIDATOR}" -V --target-env vulkan1.2 "${shader}" -o "${binary}"
        DEPENDS "${shader}" ${shader_includes} VERBATIM)
    list(APPEND shader_binaries "${binary}")
endforeach()
add_custom_target(shaders ALL DEPENDS ${shader_binaries})
add_dependencies(renderer shaders)
add_custom_command(TARGET renderer POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_CURRENT_BINARY_DIR}/shaders" "$<TARGET_FILE_DIR:renderer>/shaders"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_CURRENT_SOURCE_DIR}/assets" "$<TARGET_FILE_DIR:renderer>/assets"
    COMMAND ${CMAKE_COMMAND} -E copy_directory_if_different "${CMAKE_CURRENT_SOURCE_DIR}/models" "$<TARGET_FILE_DIR:renderer>/models")
add_custom_target(renderer_resources ALL
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_CURRENT_BINARY_DIR}/shaders" "$<TARGET_FILE_DIR:renderer>/shaders"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_CURRENT_SOURCE_DIR}/assets" "$<TARGET_FILE_DIR:renderer>/assets"
    COMMAND ${CMAKE_COMMAND} -E copy_directory_if_different "${CMAKE_CURRENT_SOURCE_DIR}/models" "$<TARGET_FILE_DIR:renderer>/models"
    DEPENDS renderer shaders)
if(WIN32)
    add_custom_command(TARGET renderer POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_RUNTIME_DLLS:renderer> $<TARGET_FILE_DIR:renderer>
        COMMAND_EXPAND_LISTS)
endif()
enable_testing()
add_test(NAME mesh_geometry COMMAND renderer --self-test)
