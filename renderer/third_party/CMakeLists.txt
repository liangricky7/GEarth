include(FetchContent)
# Preserve vkguide's exact dependency revisions, including its fastgltf changes.
FetchContent_Declare(gearth_vendor_snapshot
    URL https://codeload.github.com/vblanco20-1/vulkan-guide/tar.gz/dcf72a8b3cf93e27b917639a012be1b4b24b5e7d
    URL_HASH SHA256=f2cb329b989ff0c2d839e296c91e443bbaf207c8b7cdd0b53e26583386cac5dd
    SOURCE_SUBDIR _download_only)
FetchContent_MakeAvailable(gearth_vendor_snapshot)
set(vendor "${gearth_vendor_snapshot_SOURCE_DIR}/third_party")

set(SDL_TEST OFF CACHE BOOL "" FORCE)
set(SDL_TESTS OFF CACHE BOOL "" FORCE)
set(SDL_SHARED ON CACHE BOOL "" FORCE)
set(SDL_STATIC OFF CACHE BOOL "" FORCE)
add_subdirectory("${vendor}/SDL" SDL EXCLUDE_FROM_ALL)
add_subdirectory("${vendor}/fmt" fmt EXCLUDE_FROM_ALL)
set(FASTGLTF_ENABLE_DEPRECATED_EXT ON CACHE BOOL "" FORCE)
add_subdirectory("${vendor}/fastgltf" fastgltf EXCLUDE_FROM_ALL)
add_library(glm INTERFACE)
target_include_directories(glm INTERFACE "${vendor}/glm")
add_library(vma INTERFACE)
target_include_directories(vma INTERFACE "${vendor}/vma")
add_library(stb_image INTERFACE)
target_include_directories(stb_image INTERFACE "${vendor}/stb_image")
add_library(vkbootstrap STATIC "${vendor}/vkbootstrap/VkBootstrap.cpp")
target_include_directories(vkbootstrap PUBLIC "${vendor}/vkbootstrap")
target_link_libraries(vkbootstrap PUBLIC Vulkan::Vulkan ${CMAKE_DL_LIBS})
add_library(imgui STATIC imgui/imgui.cpp imgui/imgui_draw.cpp imgui/imgui_widgets.cpp
    imgui/imgui_tables.cpp imgui/imgui_impl_sdl2.cpp imgui/imgui_impl_vulkan.cpp)
target_include_directories(imgui PUBLIC imgui)
target_link_libraries(imgui PUBLIC Vulkan::Vulkan SDL2::SDL2)
