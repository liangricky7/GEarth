# Simulator selection and revised MVP

Historical/supporting document. Use [10 — execution roadmap](10-implementation-roadmap.md) and [11–13 — current contracts](11-implementation-contract.md) for build decisions. Earlier schedules, inputs, data quotas, and learning recipes below do not add requirements to that plan.

Status: earlier comparison, research checked October 4, 2026. [Document 08](08-research-roadmap.md) supersedes the recommendation below after investigating Assetto Corsa and preserving the real F1-driver objective. This document retains useful Trackmania/TORCS research. No implementation or installation.

## Earlier fallback recommendation

TORCS was proposed as a fallback because custom AI drivers are an intended feature. Its robot interface removes the requirement to use a virtual controller. However, its car/circuit suitability for the named real F1 benchmark was not established. Current planning investigates Assetto Corsa first, conditionally, rather than silently changing that benchmark to a local human time.

## Comparison

| Option | Technical evidence | Unresolved issue | Planning position |
| --- | --- | --- | --- |
| EA F1 on Windows | Documented telemetry; virtual input remains unverified | Anti-cheat compatibility and autonomous-use permission | Conditional alternative |
| Trackmania 2020 + TMRL | Existing community RL environment, virtual gamepad, remote training architecture | Publisher terms and exact game/plugin compatibility | Strong technical alternative, not blanket permission |
| Trackmania Nations Forever + TMInterface/Linesight | Existing tool-assisted driving and learning projects | Different edition/toolchain; publisher permission not established here | Research alternative |
| TORCS | Official robot-driver interface, Windows support, open source | Build/bridge effort and model performance | Recommended default |

### Trackmania is technically credible

[TMRL](https://github.com/trackmania-rl/tmrl) provides a Trackmania 2020 environment with analog actions and a design that separates rollout workers from training. Its [installation guide](https://github.com/trackmania-rl/tmrl/blob/master/readme/Install.md) uses Openplanet and a Windows virtual-gamepad driver. That architecture fits the laptop/remote-Spark arrangement, although Spark package compatibility and a recurrent fly policy would still need integration.

For the older Nations/United Forever games, [TMInterface](https://donadigo.com/tminterface/) supports scripted inputs, save states, and speed modification. Its author classifies runs as tool-assisted and excludes them from legitimate public leaderboards. [Linesight](https://linesight-rl.github.io/linesight/build/html/) specifically trains agents in Nations Forever and also forbids submitting its tool-assisted runs to official leaderboards. Do not mix these tools with Trackmania 2020 instructions or assume old Python interfaces work with current TMInterface versions.

These are community projects. Their existence establishes engineering precedent, not publisher authorization. Ubisoft's current US terms, section 1.3, restrict unpermitted use of Services/Content for training or fine-tuning models. The Trackmania code also lists macros and botting clients among forbidden conduct. No applicable research exception was verified. [Ubisoft terms](https://www.ubisoft.com/legal/documents/termsofuse/en-US), [Trackmania code](https://doc.trackmania.com/general/code-of-conduct/)

### Offline is not a general clearance mechanism

Offline operation can remove live multiplayer participation; it does not automatically remove software checks, change the publisher's rules, or prevent later result uploads. The EA-specific findings remain in [document 06](06-ea-anticheat.md). A local tool-assisted Trackmania experiment is distinct from leaderboard cheating, but that distinction alone does not establish an exception to the published terms.

### Why TORCS addresses this concern

The [official TORCS site](https://torcs.sourceforge.net/) explicitly supports custom AI drivers, Windows, and headless batch races. Its [robot tutorial](https://torcs.sourceforge.net/api/robot_tutorial.html) describes a C++ plugin that receives simulation state and returns steering, throttle, brake, and gear commands. It requires a development build, not merely a prebuilt game. This is a documented extension path rather than an attempt to get automation past a commercial game's protection system.

## Revised architecture — proposed, not tested

`TORCS robot plugin → local state/action bridge → fly network → local bridge → TORCS controls`

The game and fly policy initially run on Windows. The remote Spark receives episode datasets and trains versioned checkpoints, which are deployed between episodes. A small C++ plugin translates simulator state to the existing normalized observation contract; a local IPC bridge connects it to the Python model. The model remains responsible for driving decisions.

Keep the full sparse MaleCNS rate network and learned readout proposal. Add no conventional steering bypass in the primary condition. A simple hand-coded driver is a separate integration baseline, never the claimed fly controller.

Define stepping explicitly: choose a control interval in simulation time, measure callback behavior, and decide whether the simulator advances while inference is outstanding. Log simulation elapsed time separately from wall-clock runtime. On bridge failure, terminate the episode and clear controls. Do not carry the EA 20 Hz assumptions over without measurement.

Headless mode provides an avenue for batch experiments, but acceleration and parallelism are not yet measured. Running the simulator on Spark could later remove internet latency from training rollouts; ARM64 builds, dependencies, and capacity must first be verified. The MVP does not depend on that extension.

## Benchmark change

For the first demo, use one shipped circuit/car and compare the fly with a team member and a conventional controller under identical settings. Preserve the target of 3 completed valid laps in 5 attempts, using explicitly defined simulator-specific off-track and intervention rules.

Trackmania courses and arbitrary TORCS tracks cannot meaningfully inherit Verstappen's 64.391-second Austrian target. Keep that comparison as a later milestone only if the team obtains an appropriate, legitimately usable circuit layout and car. Verify distance, layout, start convention, and physics assumptions; continue labeling any real-world comparison as cross-simulator. Do not spend the weekend recreating the Red Bull Ring merely to preserve the title.

## Revised first gates and ownership

1. **A, integration:** obtain a working Windows development build and sample robot; verify state, controls, lap timing, and restart behavior.
2. **A/B:** connect a local dummy policy through the bridge, then measure the full fly graph; reject stale actions and test process failure.
3. **B/C:** establish portable Spark training and checkpoint parity; collect initial demonstrations or a clearly labeled teacher-controller dataset.
4. **C/D:** freeze a local benchmark and evaluate fly, human, and conventional-controller conditions; retain failed attempts.

Timebox the first build/bridge gate to the opening few hours. Failure is an integration issue to reassess, not a reason to switch silently back to unverified commercial-game automation. The rest of the four-person schedule and graph-control experiments remain applicable.
