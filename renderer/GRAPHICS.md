# Graphics controls

The GEarth viewer retains the supplied renderer's procedural globe, starfield,
shadows, HDR lighting, bloom, FXAA, route arcs, cargo meshes, ocean, cockpit and
ship views. Build from this repository; there are no checked-in executables.

```powershell
renderer/build/Release/renderer.exe
renderer/build/Release/renderer.exe --scene plane
renderer/build/Release/renderer.exe --scene ship
renderer/build/Release/renderer.exe --mesh renderer/assets/materials.glb
```

Without a pipeline or replay input, the globe is explicitly labeled **DEMO**.
Its routes and moving vehicles are visual fixtures. Use the [integrated
launcher](README.md) to see simulation data.

Drag the left mouse outside the panels to orbit and use the wheel to zoom.
Country buttons focus the camera. The lighting panel controls clouds, atmosphere,
stars, exposure, shadows, FXAA, night view, city lights and bloom. H hides panels;
Escape closes the viewer.

In plane and ship views, hold the right mouse button to look around. Release it
to use the controls; R resets your head. Escape releases mouse capture first.
The ship follows the rendered swell. Ocean motion and vehicle cameras are
illustrative; they are not a physical reconstruction of a shipment's trip.

The bundled cockpit and ship models load on first use. `--plane-model PATH` and
`--ship-model PATH` accept replacement GLB/glTF assets. Keep the copied `models`,
`assets`, `shaders` and SDL DLL beside the executable. Model scale, offset and yaw
can be adjusted in the lighting panel. Mesh inspection supports static indexed
GLB/glTF with PNG/JPEG or BC7 DDS materials, alpha masks, blending, emissive maps,
and metallic/roughness or specular/glossiness materials. Skinning, animation,
sparse geometry, Draco/KTX2 and other material extensions remain unsupported.

## Bistro environment

From the repository root, fetch the pinned Niagara Bistro assets once, then open
the scene at its original scale with its authored camera:

```powershell
node renderer/tools/fetch_bistro.mjs
renderer/build/Release/renderer.exe --environment artifacts/local/bistro/bistro.gltf
```

The download is about 2.15 GB. Each file is checked against its pinned Git blob
hash; rerunning the command verifies and reuses completed downloads. The large
assets stay under ignored `artifacts/local/`; source and license notices accompany
them. Build the renderer again after updating to the Bistro branch.

Hold the right mouse button to look around and use WASD to move, Q/E to descend
or ascend, and Shift to move faster. Release the button to use lighting controls;
R restores the authored camera. H hides controls. This is a static environment
preview, independent of the simulation replay.

DDS textures remain GPU-compressed, select existing mip levels up to 2048 pixels,
and have a combined 1.5 GiB upload budget. Only 2D BC7/DX10 DDS is supported; other
formats fail explicitly. The GPU must support BC compression. Camera and shadow
visibility tests avoid drawing surfaces wholly outside their respective views.
Sun shadows follow the camera; point/spot lights use imported positions and
ranges but do not yet cast shadows. Glass uses approximate alpha blending;
indirect lighting and environment reflections are analytic approximations,
not ray tracing or HDR image-based lighting. Animated props remain in their
static pose. Scene lights and exposure can be adjusted in the lighting panel.

## Legacy replay inputs

These remain available for older recorded files. New GEarth runs should use the
pipeline launcher so their full entity state, weekly frames and events stay together.

```powershell
py -3.11 renderer/tools/history_to_replay.py history.ndjson.gz run.replay
renderer/build/Release/renderer.exe --replay run.replay
py -3.11 renderer/tools/snapshot_to_replay.py snapshot.csv snapshot.replay
renderer/build/Release/renderer.exe --replay snapshot.replay
py -3.11 renderer/tools/neon_to_events.py tagged-export.json events.json
renderer/build/Release/renderer.exe --events events.json
```

The event viewer supports dispatch, reroute, delivery, failure, policy, diplomacy
and other recorded event inspection. Resolved offers and accepted peace clear
those overlays. Replay geography must match the ten GEarth capitals. Vehicle
choice and movement between recorded points remain illustrative.

See [assets/CREDITS.md](assets/CREDITS.md) and [SOURCE_ORIGINS.md](SOURCE_ORIGINS.md)
for required attribution and library licenses. [OCEAN_RENDERING_DESIGN.md](OCEAN_RENDERING_DESIGN.md)
describes the water model.
