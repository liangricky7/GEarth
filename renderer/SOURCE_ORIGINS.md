# Source origins

Faultline's Vulkan foundation is adapted from [vkguide.dev](https://vkguide.dev), pinned to [vulkan-guide / all-chapters-2, dcf72a8b](https://github.com/vblanco20-1/vulkan-guide/tree/dcf72a8b3cf93e27b917639a012be1b4b24b5e7d). The initial scaffold used an earlier personal renderer; its additional implementations were replaced on October 11, 2026. This describes the current source, not when the original project was first written.

## Tutorial foundation

- `vk_descriptors.*`: tutorial descriptor layouts, writers and pools, with checked Vulkan errors.
- `vk_initializers.*`: the tutorial functions used by this renderer. Dynamic rendering also accepts zero color attachments for the shadow pass.
- `vk_images.*`: tutorial barriers, blits and mip generation, with explicit depth aspects and a one-pixel minimum for rectangular mip chains.
- `vk_pipelines.*`: tutorial single-color-target pipeline builder. Faultline supplies depth bias directly for its shadow pass. Shader loading uses a checked SPIR-V reader and a batch of modules whose lifetime covers pipeline creation.
- `vk_engine_bootstrap.cpp` and `vk_engine_resources.cpp`: chapter-6 setup and upload functions, adapted to Faultline's cleanup, optional anisotropy, per-image presentation semaphores, VMA host flushes and transfer barriers. Images are 2D; unused volume-texture support is removed.
- Scene-node, mesh, material and vertex interfaces retain the tutorial's structure. The optional tangent is the standard glTF `TANGENT` attribute; normal textures require an additional material binding.

## Faultline implementations

- `vk_loader.cpp`: replacement static glTF/GLB importer. Resources are indexed independently of display names, images are decoded once per color-space role, and only owned GPU images enter scene cleanup. Asset validation and Faultline's AO baking/cache are retained. The filter-enum conversions follow vkguide.
- Normal mapping uses authored glTF tangents where available and reconstructs a tangent frame from position/UV derivatives otherwise. The old CPU tangent generator and its build entries are removed. Missing or degenerate UVs fall back to the geometric normal.
- `fxaa.frag`: a compact directional filter on display-encoded color, following the general [FXAA method described by Timothy Lottes](https://developer.download.nvidia.com/assets/gamedev/files/sdk/11/FXAA_WhitePaper.pdf). It replaces the previous quality-preset edge-search shader; its filtering is not pixel-identical to that shader.
- `fullscreen.vert`: an oversized triangle generated from the vertex index, shared by post-processing and sky rendering.
- `tonemap.frag`: exposure, bloom and sun glare followed by the published [Narkowicz ACES fit](https://knarkowicz.wordpress.com/2016/01/06/aces-filmic-tone-mapping-curve/) (CC0 option) and sRGB encoding. Unused Reinhard, bypass and debug modes are removed.
- Procedural world generation, globe/traffic, replay/event presentation, ocean scenes, AO baking and their shaders remain Faultline work.
- Ocean waves follow the spectrum model of [gasgiant/FFT-Ocean](https://github.com/gasgiant/FFT-Ocean) (MIT, Copyright (c) 2020 Ivan Pensionerov), re-implemented; no files were copied.

The source comparison covered the earlier renderer's top-level `src/` and `shaders/` files and vkguide's shared code, chapters 1–6 and shaders. Common tutorial code and standard Vulkan/glTF declarations intentionally remain. Text similarity is not a measurement of authorship.

## Licenses

- `LICENSE.txt`: vkguide.dev's MIT license, retained with the tutorial-derived code.
- `LICENSE`: MIT license for this project's code.
- SDL, fmt, fastgltf, GLM, VMA, stb_image and vk-bootstrap come from the exact vkguide commit above. CMake downloads and hash-checks that snapshot under `build/_deps`; their license notices remain with the downloaded sources. The renderer's newer ImGui sources and embedded license remain under `third_party/imgui`. Model and texture credits are in `assets/CREDITS.md`.
