# GEarth renderer

The native C++20 / Vulkan viewer plays completed GEarth simulations from the
existing `gearth.renderer.v1` export. Run the simulation, export its recorded
states and events, then open that recording to animate it on the renderer's own
clock. No running simulation or API connection is needed during file playback.

## Build

On Windows, install Visual Studio's Desktop Development with C++ workload,
CMake 3.26+, the Vulkan SDK, and Node 22+. Open a new terminal after installing
the SDK so `VULKAN_SDK` is available.

```powershell
cmake -S renderer -B renderer/build
cmake --build renderer/build --config Release --parallel 8
```

CMake downloads the exact pinned dependency sources into `build/_deps` on the
first configuration (about 110 MB, plus simdjson). Internet access is required
for that first build; later builds reuse the local copies. The snapshot's SHA-256
is checked before extraction. Only the selected libraries are built, not the
upstream tutorial applications. ImGui remains in `third_party` because this
renderer uses a newer version than the snapshot.

Library licenses accompany their sources; model/texture credits are included.
The build copies shaders, assets, models and SDL's DLL beside the executable.
No npm install is needed for the launcher.

The viewer requires a Vulkan 1.3 GPU and a desktop surface, including for hidden
smoke captures. This port has been exercised on Windows; other platforms have
not been qualified. Use `--exe PATH` for a different build location.

## Open a simulation

After the simulation finishes, export it with the existing pipeline, package it
once, then open the resulting recording file:

```powershell
py -3.11 ops/export_renderer.py YOUR_RUN_ID artifacts/local/my-replay --api http://127.0.0.1:5080
node renderer/recording.mjs artifacts/local/my-replay artifacts/local/my-replay.gearth
node renderer/run.mjs --export artifacts/local/my-replay.gearth
```

If you receive a `.gearth` file, only the last command is needed. That one file
contains the checkpoints, events and inspector data. It opens directly without
unpacking or creating a temporary copy. The original export folder also remains
supported with `--export FOLDER`.
For the saved local full-year RULE recording (days 0-365, including all agents):

```powershell
node renderer/run.mjs --export artifacts/local/renderer-year365/year-365.gearth
```

Playback starts at the first recorded day. The full year plays in **60 seconds**
by default. Change **Replay length** to play the recording over 10-600 seconds.
Recordings repeat with **Loop** enabled. Use **Pause/Play**, **Restart**, and
**Day** to seek. With Loop disabled, playback stops at the end; Play restarts it.
The recorded run's `completed` status describes the simulation, not playback.

### Create a new 365-day recording on Windows

For a local RULE run with 1,100 agents, open PowerShell at the repository root:

```powershell
$env:GEARTH_WORLD = 'true'
$env:GEARTH_LIVE = 'false'
$env:GEARTH_STORAGE = 'local'
$env:GEARTH_POPULATION = '1100'
$env:GEARTH_TICKS = '365'
$env:GEARTH_INTERVAL_MS = '0'
$env:GEARTH_PORT = '5087'
$env:GEARTH_RUNS_DIRECTORY = "$PWD/artifacts/local/renderer-recordings/runs"
dotnet run --project backend/GEarth.Api --no-launch-profile
```

Use a fresh terminal with no saved-run/resume settings. Fresh runs capture renderer
history automatically; `--verbose` and analytics capture are not required. Zero
interval removes the deliberate wait between simulation days; it does not set
the renderer's playback speed. This runs without AI model services.

Leave that terminal running. Once the simulation finishes, use a second terminal:

```powershell
$health = Invoke-RestMethod 'http://127.0.0.1:5087/api/health'
if ($health.run_status -ne 'completed' -or $health.tick -ne 365) { throw 'Wait for the 365-day run to finish' }
$recording = "artifacts/local/renderer-recordings/$($health.run_id)"
py -3.11 ops/export_renderer.py $health.run_id $recording --api http://127.0.0.1:5087
if ($LASTEXITCODE -ne 0) { throw 'Export failed' }
node renderer/recording.mjs $recording "$recording.gearth"
if ($LASTEXITCODE -ne 0) { throw 'Recording packaging failed' }
node renderer/run.mjs --export "$recording.gearth"
```

After export completes, the API can be stopped with Ctrl+C. Keep the `.gearth`
file for later playback. For an AI-driven recording, use the configured live
simulation with a 365-tick limit, then export its completed run the same way.

The launcher verifies file hashes, run identity, ordering and complete event
coverage when packaging; it verifies the hashes on every open, and the native
parser checks the run, sequence and state coverage. Raw frames are preserved, and
complete entity lists replace earlier lists. A packed file has no temporary
handoff. Folder/API handoffs are removed when the launcher exits. Interrupted
exports without a complete manifest cannot open.

## Optional API connection

To follow a simulation while it is running, use its run ID from `/api/runs` or
the observer:

```powershell
node renderer/run.mjs --run YOUR_RUN_ID --api http://127.0.0.1:5080
```

For a remote server, substitute its HTTPS origin. The server must serve the
renderer endpoints in this repository; this port does not deploy or restart the
shared cloud simulation. A server on an older release will report an HTTP error.

The launcher downloads captured history, starts the viewer, and polls every five
seconds until the run completes, stops or fails. New weekly and terminal states
appear without restarting the viewer. Closing the viewer stops its local polling;
it does not stop the simulation. Connection errors are shown in the viewer while
the last valid state remains available, and polling retries automatically.

Use **Play**, **Day**, and **Replay length** to replay; **Follow incoming data** follows
the newest recorded state. One tick remains one day, including partial final weeks.
The nation table and night lights use the last authoritative checkpoint at or
before the selected day. The inspector includes every agent, banks, loans,
shipments, metrics, routes, relationships, policies, countries, locations and raw
events. Select a record by its stable ID; money remains in integer minor units
(100 per WLD), with cash and bank deposits separate.

Cargo starts from the recorded shipment state at each checkpoint. Dispatch,
reroute, delivery and failure events update it between checkpoints. Movement and
ship/plane choices are illustrative; they are never measurements or inputs to the
simulation. Peace responses clear conflict overlays and resolved offers. The
world's ten capital coordinates must match the current GEarth scenario; mismatched
geography is rejected. Display names come from the run.

Older runs without archived weeks open through `/current`. They are explicitly
labeled as having a history gap, and show the latest snapshot without inventing
missing weeks. Do not interpret a recorded `LIVE` mode as a live model connection
from the viewer.

The pipeline's API, persistence rules and frame schema are described in
[PIPELINE.md](PIPELINE.md). The public contract and backend capture cadence are
unchanged. `stream.mjs` is only the local handoff to the native process.

## Graphics and earlier replay formats

Run `renderer/build/Release/renderer.exe` for the labeled demo globe. The original
mesh viewer, ocean, cockpit, ship, lighting and camera controls remain available;
see [GRAPHICS.md](GRAPHICS.md). Legacy `--events` and `--replay` inputs and their
Python converters remain supported. History conversion validates completion and
replaces its output atomically, so invalid input cannot overwrite a good replay.

Model and texture credits are in [assets/CREDITS.md](assets/CREDITS.md), with source
origins in [SOURCE_ORIGINS.md](SOURCE_ORIGINS.md). Preserve these credits when
redistributing the renderer or sharing footage.

## Tests

Recorded results and local artifact locations are in [VALIDATION.md](VALIDATION.md).

```powershell
$env:GEARTH_RENDERER_EXE = (Resolve-Path renderer/build/Release/renderer.exe).Path
$env:GEARTH_RENDERER_GPU_TESTS = '1'
npm.cmd run test:renderer
ctest --test-dir renderer/build -C Release --output-on-failure
py -3.11 renderer/tests/smoke.py renderer/build/Release/renderer.exe --output artifacts/local/renderer-smoke
py -3.11 renderer/tools/history_to_replay.py --self-test
py -3.11 renderer/tools/snapshot_to_replay.py --self-test
py -3.11 renderer/tools/neon_to_events.py --self-test
py -3.11 -m pytest ops/test_export_renderer.py -q
dotnet test backend/GEarth.Tests/GEarth.Tests.csproj --filter FullyQualifiedName~Renderer
```

Without `GEARTH_RENDERER_EXE`, Node runs transport/bridge checks and explicitly
skips the native integration test. The live reconnect GPU test additionally needs
`GEARTH_RENDERER_GPU_TESTS=1`; it opens a hidden window and serves a local fixture
API that drops a request before publishing new frames. The exporter tests need pytest and jsonschema.
The native `--check-stream DIRECTORY --tick DAY` command checks the actual parser
and cargo reconstruction without creating a window or GPU device.

For an end-to-end hidden GPU capture using either source:

```powershell
node renderer/run.mjs --export artifacts/local/my-replay --smoke --capture artifacts/local/gearth.ppm
```

The immediate workload is the default 1,100-agent simulation. The native viewer
retains recorded snapshots and events in memory, and reloads them at published
checkpoint boundaries. Long/large runs need separate memory and performance
qualification; the event parser currently rejects more than 250,000 events.
