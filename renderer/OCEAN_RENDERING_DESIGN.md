# Cloud-free cinematic globe: implementation handoff

## Goal and scope

Implement a cloud-free version of the user's reference: deep blue oceans, a broad broken-up sun reflection, directional daylight with a dark hemisphere, natural land colors, and a thin cyan atmospheric edge. Preserve the ten fictional countries, their labels, borders, ports, demo ships/planes, and procedural starfield.

This is a design document, not an implemented feature. The preceding implementation turn only inspected files; it did not change renderer code. Inspect the current checkout before editing because the user has continued improving terrain.

The reference's white swirls are clouds. Do not reproduce them as giant ocean waves or foam. Clouds stay disabled by default. Target the ocean gloss and lighting rather than an exact cloud-covered Earth reproduction.

Do not add FFT, geometric wave displacement, cube-sphere geometry, quadtree LOD, tessellation, mesh shaders, DLSS, full atmospheric scattering, SSR, or new dependencies for this milestone. These are unnecessary for the current orbital view.

## Current implementation observed

Paths below are relative to this document and the renderer project root.

| File | Current behavior and integration point |
| --- | --- |
| `src/procedural_world.cpp` | Seeded 3D noise; seven octaves; land/sea threshold `SeaLevel=.035f`; country assignment; colored terrain; ocean depth colors; land relief normals; thin dark borders; `world_self_test()`. Preserve geography and route generation. |
| `src/procedural_world.h` | `ProceduralWorld` contains color pixels, normal pixels, country IDs, and country metadata. Ocean country ID is 255. |
| `src/vk_engine.cpp` | `init_globe()` generates a 4096×2048 map and uploads sRGB color plus linear normal textures. Initializes six globe/traffic materials. `update_scene()` fills the scene uniform. `update_globe()` adds an optional atmosphere sphere at radius 1.025. |
| `src/vk_engine.h` | Clouds and atmosphere currently default off; stars default on. Existing lighting/atmosphere UI can be reused. |
| `src/vk_types.h` and `shaders/scene_data.glsl` | Matching scene uniform, currently 208 bytes. Keep CPU/GLSL layout synchronized. |
| `shaders/mesh.frag` | Existing GGX lighting, normal maps, shadow sampling, atmosphere/cloud branches, and an old blue-color ocean heuristic behind `materialFlags.w`. Current globe initialization does not enable that flag. |
| `shaders/material.glsl` | Existing color, metal/roughness, and normal samplers. Roughness uses green; metallic uses blue (`.bg`). |
| `shaders/space.vert` | Existing procedural star brightness. Preserve its stable direction and camera behavior. |
| `tests/smoke.py` | Rendering, resize, traffic, star stability, imported-material and malformed-asset checks. |

Current globe lighting starts at sun direction `(-.45,.65,1)`, intensity 2, ambient .48. This generous ambient fill reduces day/night contrast. Main camera depth is reversed; preserve existing depth conventions.

## Implementation sequence

### 1. Give water an explicit material mask

Do not infer water from blue RGB: fictional country colors and later terrain changes can invalidate that heuristic.

Smallest integration: generate an RGBA material texture alongside the existing color and normal textures and bind it through the existing `metalRoughTex` sampler for the globe only.

- R: ocean coverage, 1 for ocean and 0 for land.
- G: roughness, initial land value .85 and water .24.
- B: metallic, always 0 for land and water.
- A: 1.

Build the mask from exactly the existing sea-level classification, not a second noise evaluation or a changed threshold. Initialize every channel. Use `VK_FORMAT_R8G8B8A8_UNORM`, not sRGB; generate mipmaps using the existing texture path. Filtering may create fractional coverage at the shore, which is suitable for blending material properties.

Add the pixel vector to `ProceduralWorld`, the GPU image to `VulkanEngine`, upload it in `init_globe()`, bind it only for the globe material, and destroy it alongside the existing globe textures. Keep imported glTF materials on their existing resources and interpretation.

Set the globe's `materialFlags.w=1` to identify this custom material. Read mask R only inside that branch. Replace the old blue-color heuristic. Set the globe roughness factor to 1 if G stores the final roughness; avoid multiplying .24 by the old .95 unintentionally.

### 2. Natural terrain and ocean color

Preserve elevations, continent shapes, country assignment, capitals, ports, routes, and existing land relief normals.

Replace the dominant flat country palette with terrain colors derived from existing elevation plus latitude: muted green lowlands, restrained tan dry regions, gray/brown highlands, and limited pale peaks. These are artistic biome cues, not a climate simulation. Country colors remain available for labels/UI; optionally retain only a very subtle country tint in the surface color.

Retain country boundaries, but tune their contrast against the natural terrain so they remain visible without looking like thick black cracks. Keep names and markers readable on both hemispheres.

Use deep navy/blue offshore and restrained teal near the existing coastline. Keep the shallow band narrow; avoid outlining every continent in bright cyan. These values are authored sRGB texture colors; the existing sRGB texture format performs conversion to linear space for lighting.

### 3. Water shading and animated micro-detail

Reuse the current GGX BRDF and HDR → tone mapping → final output pipeline. Water is a dielectric, not metallic. Start with water F0 around .02 and roughness .20–.30; land retains its current dielectric behavior. These are tuning ranges, not validated final values.

For this orbital view, keep the sphere geometry smooth and perturb only the water shading normal. Use a few continuous 3D sine components evaluated on the undisplaced globe position. This avoids texture UV seams and requires neither FFT nor an extra normal texture.

Suggested wave field and derivative:

```text
h(p,t) = sum(a_i * sin(k_i dot p + omega_i*t + phase_i))
g(p,t) = sum(a_i * k_i * cos(k_i dot p + omega_i*t + phase_i))
g_tangent = g - n * dot(n,g)
n_water = normalize(n - g_tangent)
```

Here `n` is the smooth radial normal and `p` is a stable position in globe-local coordinates. Choose a small fixed set of nonparallel wave vectors. Keep slopes subtle; these are stylized unresolved ripples, not meter-scale ocean simulation. Add a `shortcut:` comment recording that limitation and the close-up condition for upgrading.

Keep this field attached to the globe, not the screen or camera. In the current static identity-transform globe, local and world coordinates coincide; document that assumption or pass local position if rotation is introduced. Do not sample a displaced position recursively.

Blend the existing land relief normal and water normal by ocean coverage, then normalize. Land relief must remain intact. Prevent perturbations from producing bright glints on the night hemisphere by gating direct light with the smooth radial day/night orientation as well as the shading normal.

Use screen-space derivatives of phase to attenuate frequencies smaller than a pixel. Otherwise moving highlights can sparkle and alias badly at orbital distances. Smoothly fade unresolved waves toward the underlying smooth water normal; do not sharply toggle detail by distance.

Expose time through one appended `vec4 waterSettings` in both scene uniform definitions (for example x=visual time seconds; reserve remaining components at zero unless needed). Update the size assertion from 208 to 224 bytes and all explicit layout assumptions. Existing allocation/descriptor ranges using `sizeof(GPUSceneData)` should follow automatically. Reuse the engine's existing elapsed/simulation clock after inspecting its semantics; smoke mode must stay deterministic. Keep time finite and bounded without visible resets.

The broad sun reflection should come from GGX roughness and lighting, not a painted white spot. A restrained blue grazing-angle fill may approximate atmospheric reflection, gated to the lit side; label it an artistic approximation rather than environment-based lighting. Avoid adding a second full-strength specular lobe or washing the entire ocean white.

### 4. Directional light and thin atmosphere

Lower globe-only ambient fill, initially trying .08–.15, and tune sunlight/exposure against the existing tone mapper. Leave imported-mesh inspection defaults alone. Pick a sun direction with a visible terminator and a sun reflection on visible ocean; verify the actual generated geography rather than assuming a particular coast faces the camera.

Reuse the existing optional atmosphere material and shell. Enable atmosphere by default for the globe while leaving clouds off. Reduce shell thickness from the existing 1.025 if needed; start around 1.008–1.015 and assess at multiple zoom levels.

Replace the current uniformly lit rim with a view-angle rim weighted by sunlight:

```text
rim = pow(1 - clamp(dot(radialNormal, viewDirection), 0, 1), exponent)
day = smoothstep(negativeSmallValue, positiveValue, dot(radialNormal, sunDirection))
alpha = rim * day * strength
```

Use a restrained blue/cyan color and tune exponent/strength visually. This is a cheap atmospheric limb approximation, not physical scattering. Reuse the transparent pass with depth test enabled and depth writes disabled, respecting reversed depth. Confirm the opaque globe occludes the shell's far side and that the rim does not obscure vehicles or labels. Keep existing blending conventions consistent; do not feed premultiplied color to a straight-alpha pipeline.

### 5. Presentation and packaging

Keep the procedural starfield. Reduce its brightness only if it competes with the planet; preserve its fixed directions when orbiting/zooming. Existing star-visibility test thresholds may need a justified adjustment if brightness intentionally changes; do not remove stability checks.

Reuse existing lighting controls. Do not add an ocean settings panel for this milestone. Keep traffic explicitly labeled DEMO and preserve the existing country list and focus controls.

After validation, refresh the packaged executable and compiled shaders if the user is expected to run the prebuilt launch configuration. Never overwrite a running executable by force. `F5 → Run globe (prebuilt)` uses `runtime/renderer.exe`; building source alone does not update that executable. `Build and debug renderer` uses the project build output.

## Verification and acceptance

1. Extend `world_self_test()` to check material-map size, deterministic generation, ocean-mask agreement with country ID 255, zero metallic, and initialized channel values. Preserve existing geography/route checks. Do not change the generation seed or country IDs to make a check pass.
2. Compile shaders and C++ with the updated uniform layout. Run the existing self-test and full smoke suite with Vulkan synchronization validation. Confirm imported mesh materials, shadow toggles, resize, and resource cleanup still work.
3. Capture deterministic frames at two times. Verify water detail moves while continents, borders, stars, and the camera stay stable. Traffic movement alone is not evidence that water animation works.
4. Visually inspect a sun-facing ocean, the terminator, both poles, and the longitude seam while orbiting. Check wide and closer zooms. Require no visible mapping seam, polar swirl, crawling highlights, broad cyan halo, or light leaking through the night side.
5. Compare clouds-off captures against the reference's lighting qualities: deep blue water, a broad localized glint, subdued land, dark hemisphere, and thin illuminated rim. Do not expect the cloud-free image to reproduce the reference's white texture.
6. Confirm all ten countries are still identifiable through borders, labels, and UI; ships and planes remain legible. Check the atmosphere does not cover their markers.
7. Record build/test results and any unverified GPU behavior in `VALIDATION.md`; update README defaults only after the implemented result is verified.

Example PowerShell commands from the renderer root (use an existing configured build directory when appropriate):

```powershell
cmake -S . -B build
cmake --build build --config Debug --parallel 8
ctest --test-dir build -C Debug --output-on-failure
python tests/smoke.py build/Debug/renderer.exe --output build/ocean-checks
```

Do not consider a successful compile sufficient: the acceptance criterion is the captured and inspected image plus passing regression checks.

## Future FFT decision

Revisit FFT only if the camera approaches ships closely enough for wave crests and geometric silhouettes to matter. At that point evaluate one local FFT patch, actual displacement, normals derived consistently from the displaced surface, and ship buoyancy sampling. A cube-sphere/LOD system is a separate decision. Triplanar vector blending can soften or distort wave patterns and does not guarantee an artifact-free global ocean. None of these are prerequisites for the current visual target.

## Suggested prompt for the VS Code agent

> Implement OCEAN_RENDERING_DESIGN.md in this renderer. First inspect the current files and preserve my existing terrain improvements. Complete the cloud-free ocean material, natural terrain treatment, directional lighting, and thin atmospheric rim using the existing Vulkan pipeline. Keep geography, country IDs, demo traffic, and star stability intact. Do not implement FFT or add dependencies. Follow the document's validation and packaging steps, inspect actual captures, and report what passed and what remains unverified.
