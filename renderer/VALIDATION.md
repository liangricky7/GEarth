# GEarth renderer integration verification — 2026-10-11

Windows x64, NVIDIA RTX 3060 Laptop GPU, Vulkan SDK 1.4.357.0,
Visual Studio 2026/MSVC 19.51, CMake 4.3.1, Node and Python 3.11.

Bistro follow-up: imported Niagara Bistro at revision
`2bdb6a410f8ebd475d3737e7c8e038ed2b00b02e`. The fetch tool verified every asset
against its Git blob hash and retained source/license notices under ignored
`artifacts/local/bistro`. Release and Debug builds and Release CTest passed.
All 29 graphics smoke tests passed, including BC7 DDS selection over missing PNG
fallbacks, specular/glossiness materials, imported point lights and truncated DDS
rejection. All 14 renderer integration tests, including GPU reconnect, passed.

The full scene loaded 551 meshes, 2,909 mesh instances, 96 local lights and its
authored camera. Selected compressed texture mips used 1,155.9 MiB. A short
180-frame Release run averaged 53.6 FPS at a 1920x1200 capture size, including
smoke-test resizing and capture overhead; this is not a sustained benchmark.
The capture was visually inspected. A separate 24-frame full-scene Debug run
with synchronization validation reported no validation errors or hazards.
Evidence is in `artifacts/local/bistro-inspection` (`bistro.png`,
`bistro-release.log`, `bistro-validation.log`, `smoke.log`, `replay-tests.log`);
fixture captures are in `artifacts/local/bistro-smoke`.
Glass and indirect lighting are approximations, local lights do not cast shadows,
and animation remains static. Other GPUs and operating systems were not tested.

Dependency cleanup: replaced 2,371 bundled library files with a SHA-256-pinned
vkguide source download managed by CMake. Compared the existing library files
with that snapshot (normalizing line endings); all matched. The newer ImGui
copy remains local. CMake downloaded the snapshot and simdjson into the build
directory, and the Release rebuild passed with the old source folders removed.
Release CTest, all 14 renderer integration tests (including GPU reconnect), and
all 26 graphics smoke checks passed. The rebuilt parser confirmed 54 frames,
136,505 events, day 365 and 60-second playback for the existing full-year file.
Graphics captures and logs are in `artifacts/local/renderer-cleanup-smoke`.

Single-file follow-up: `artifacts/local/renderer-year365/year-365.gearth` contains
the same verified full-year export in one 246,792,542-byte file. Packaging preserves
the original JSON and hashes. Direct loading avoids rewriting the folder handoff,
and native parsing overlaps graphics startup. On this machine with warm file
caches, the old folder verification/handoff took 2.99 s and native parsing 3.17 s;
single-file verification took 0.25 s and native parsing 2.80 s. Actual GPU startup
through the loaded scene took 6.07 s with overlapping work (7.36 s before overlap).
These are individual local measurements, not cold-start or cross-machine guarantees.
The native probe confirmed all 54 frames, 136,505 events, day 365 and a default
60-second playback duration. A 24-frame full-file GPU capture was visually checked
at day 14.60 (`single-file.png`), including cargo, agents and Replay length controls.
Recording tests cover corruption, truncation, oversized headers, duplicate entries,
failed publication, existing-output preservation and native cargo reconciliation.
All 14 renderer integration tests (including the GPU reconnect test) and Release
CTest passed after the final changes.

Full-year follow-up: local RULE run `world_20261011_114742_3725b6` completed all
365 ticks with 1,100 initial agents. The complete export is
`artifacts/local/renderer-year365/recording`: 54 checkpoints from day 0 through
365, 136,505 contiguous events, 354 files and 246,806,405 bytes. The existing
exporter's fetch hook read committed local events once to avoid repeated full
archive scans; checkpoint requests still used the API. First and last event
pages matched the API exactly, and every sequence/run identity was checked.
Export hashes were verified by the launcher. Native `--check-stream --tick 365`
confirmed the final state and 31 cargo poses; the full export rendered a 24-frame
GPU smoke capture. Release CTest and all nine client/stream tests passed after
raising the bounded event limit from 100,000 to 250,000. A regression self-check
loads 100,001 events through tick 365. Earlier incomplete attempts remain in the
ignored output directory; only `recording/manifest.json` marks the finished export.
Reading checkpoint files while writing them caused Windows access errors during
two attempts; the successful run was exported after completion. This separate
local-store concurrency issue remains unmodified. No AI model services were used.

Recorded-playback follow-up: rebuilt Release with a 0.25 days/sec default,
Pause/Play, Restart, Loop and an explicit end-of-recording state. Release CTest
and all nine client/stream tests passed. New native checks cover fractional-day
cargo transforms, pause, end, restart, looping and single-snapshot input. A
300-frame GPU capture of the actual export (`playback.png`) was visually checked
at day 7.50 with cargo in transit and the new controls. The full graphics suite
and Debug build below predate this playback-only update.

- Clean Release configuration and Release/Debug builds succeeded. Both CTest
  self-check runs passed, including shipment reroutes, failure, peace responses,
  backward seeking, geometry, cameras and asset handling.
- Nine client/stream tests passed with the actual native executable. They cover
  complete entity replacement, weekly/partial-week timing, hashes, malformed
  identities/revisions, shipment reconciliation and current-only fallback.
- The additional hidden-window reconnect test passed. A local fixture API
  returned HTTP 503, then published the remaining checkpoints. The same native
  process loaded day 15 after retrying; no restart was needed.
- Seven backend renderer tests and six Python exporter tests passed.
- All 26 final Release GPU smoke scenarios passed, covering the globe, cargo
  motion, material import, malformed assets/shaders, legacy event playback,
  geometry rejection, ocean, plane and ship views.
- All three legacy converter self-tests passed. History conversion tests include
  truncated/malformed input, identical source/destination and preserving an
  existing output after failure.

An isolated RULE simulation using the default 1,100-agent population was served
by the real GEarth API at localhost:5087. Run
`world_20261011_110703_e12bc8` completed 15 days, ending with 1,101 agents.
The existing exporter produced four authoritative frames at ticks
`[0, 7, 14, 15]`, through revision 6 and event sequence 5,824.
Both the HTTP launcher and verified offline launcher rendered that data.

The final Debug stream capture reached day 15, showed its partial-week label and
all 1,101 current agents, and rendered the recorded cargo and route overlays.
Vulkan synchronization validation reported no validation errors or hazards.
The driver emitted its existing manifest-version warning; the older validation
environment setting also emitted a deprecation warning.

Local evidence is retained under ignored `artifacts/local/renderer-integration/`:
the export, state handoff, HTTP/offline captures, final `debug.png` and
`debug.log`, and `reconnect-test.log`. The isolated API was stopped after testing.
Graphics regression captures and individual logs are under
`artifacts/local/renderer-smoke/`.

Not qualified: production Neon/deployment, other GPUs/operating systems, or
large/long-run memory and sustained performance. The shared cloud simulation
was not modified. Motion between checkpoints and transport mode remain
illustrative; recorded economic state is preserved without interpolation.
