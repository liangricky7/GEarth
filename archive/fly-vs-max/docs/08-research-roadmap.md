# Research update and execution roadmap

Status: research evidence, reviewed October 5, 2026; no implementation. [10 — roadmap](10-implementation-roadmap.md) supersedes all schedules and build priorities here; documents 11–13 specify the implementation contracts. The original goal remains a fly-derived controller chasing a **real F1 driver's lap time**, with Hamilton or Verstappen as candidate references. Completing a lap against a local baseline is an engineering milestone, not a replacement for that goal.

## Findings that change the plan

### 1. A repeated course is a useful simplification

The proposed task is one car, one circuit, one setup, and fixed conditions. The controller can learn where the corners are instead of discovering a new track every run. However, a timed recording of pedal and steering inputs can diverge when speed, starting position, or latency changes. The design therefore uses distance along the lap plus live state feedback. This is our engineering proposal, not an established capability of the released connectome.

### 2. The Beat Saber clip is inspiration, not a verified model specification

A likely match is [Lyra Bubbles' X post](https://x.com/_lyraaaa_/status/2097527368919470162). The original post could not be retrieved directly during this research. No creator-authenticated source repository, checkpoint, training protocol, or controlled evaluation for that exact experiment was verified. Secondary summaries and mirrors are not sufficient to determine whether the fly network learned reactive control, generated a rehearsed sequence, or relied on additional controller logic.

Consequently, do not claim the clip establishes that an untrained connectome can play rhythm games or drive. It motivates a testable experiment: can the fly-derived network learn a repeatable route and correct deviations? If the user provides the exact post or code, audit what enters the network, what learns, and what actually moves the sabers before adopting its method.

For a scientific starting point, [Shiu et al.](https://www.nature.com/articles/s41586-024-07763-9) provide a published computational sensorimotor model and link [their implementation](https://github.com/philshiu/Drosophila_brain_model). That work is a model reference, not a MaleCNS racing checkpoint. Retain the user's MaleCNS dataset choice rather than silently substituting a different fly reconstruction.

### 3. Original Assetto Corsa has a substantially closer technical fit

[AssettoCorsaGym](https://assetto-corsa-gym.github.io/) is a published autonomous-racing project with code and demonstration datasets. Its initial published cars include a Formula 3 car and GT/road cars; those demonstrations are not F1-car expert demonstrations. The [repository](https://github.com/dasGringuen/assetto_corsa_gym) lists Red Bull Ring and Silverstone among supported track configurations, plus an example trained policy for Monza/GT3. Use these to verify the integration before claiming support for a different car.

The framework's [Windows guide](https://github.com/dasGringuen/assetto_corsa_gym/blob/main/INSTALL.md) uses vJoy for controls, a Python game plugin, and Custom Shaders Patch for resets. This is not the earlier vgamepad/ViGEm proposal. Its extra components and older Python/CUDA setup instructions are integration risks. Keep game/plugin dependencies isolated from the modern Spark training environment.

The [paper](https://proceedings.neurips.cc/paper_files/paper/2024/file/b91ea2ba1b6521940e204b7b81d5ef47-Paper-Datasets_and_Benchmarks_Track.pdf) reports 25 Hz actions in its experiments and an interface tested up to 100 Hz. Those measurements do not establish our fly-network latency. Appendix B also explicitly directs research/commercial users to obtain developer permission.

Use **Assetto Corsa (the original Steam game)** for this investigation, not Competizione or EVO; their environments are not interchangeable.

### 4. Public-demo permission is a concrete Assetto Corsa dependency

The [posted base-game EULA](https://store.steampowered.com/eula/244210_eula_0) explicitly requires written permission for public, educational, training, and other listed uses. A hackathon demo needs this addressed. Research code being MIT-licensed does not grant rights to the simulator. This route is technically promising but is not cleared by an offline launch or an existing community project.

Contact route: [Kunos / Assetto Corsa contact page](https://assettocorsa.gg/contact-us/). No inquiry has been sent. A draft is included below. This finding does not prevent continuing simulator-independent design work.

## Simulator decision

Update October 9: the team selected original Assetto Corsa. The alternatives below are retained as contingency research, not open primary choices. The car, adapter qualification, content access, and permitted demo use remain unresolved.

| Route | When to choose it | What remains unresolved |
| --- | --- | --- |
| Original Assetto Corsa + AssettoCorsaGym | Best investigated technical candidate for the actual F1-driver challenge, if permission and integration are resolved | Public-demo permission, game/DLC access, Windows setup, specific F1-car support |
| TORCS | Open-source integration fallback and independent controller-development route | A verified F1-like car and appropriate real circuit for the headline benchmark |
| EA F1 | Only if exact edition's input compatibility and permitted use are resolved | Earlier Javelin/input questions remain |
| Trackmania | If the team explicitly changes the challenge to a Trackmania time | Different physics/course; does not preserve the real F1 benchmark |

Do not keep switching environments throughout the weekend. Resolve these branches before or during a short initial integration window. If neither the F1-capable route nor a suitable fallback configuration is available, report that the F1 comparison is unfulfilled while still demonstrating controller progress.

## Concrete car, circuit, and target proposal

Investigate **Red Bull Ring GP + Ferrari SF70H** in original Assetto Corsa. The official [Red Pack](https://store.steampowered.com/app/493190/) includes the circuit; the [Ferrari 70th Anniversary Pack](https://store.steampowered.com/app/675590/Assetto_Corsa_Ferrari_70th_Anniversary_Pack/) includes the 2017 SF70H. Purchase/access, control configuration, and its behavior through the wrapper have not been tested. A supported track does not imply a tested car.

Choose a named primary target before scored evaluation, with other targets displayed separately:

| Driver and event | Session | Time | Source |
| --- | --- | --- | --- |
| Lewis Hamilton, Austria 2017 | Fastest race lap, lap 69 | **1:07.411** | [Formula 1 result](https://www.formula1.com/en/results/2017/races/967/austria/fastest-laps) |
| Lewis Hamilton, Austria 2017 | Qualifying Q3 | **1:04.424** | [FIA classification](https://www.fia.com/events/fia-formula-one-world-championship/season-2017/session-classifications-11) |
| Max Verstappen, Austria 2023 | Qualifying Q3 | **1:04.391** | [FIA classification](https://www.fia.com/events/fia-formula-one-world-championship/season-2023/austrian-grand-prix/qualifying-classification) |

The first target is 3.020 seconds slower numerically than the last, making it a less demanding time threshold. This does not compare driver ability: the laps differ in year, car, session, and conditions. Hamilton's qualifying target is only 0.033 seconds above the proposed Verstappen qualifying target.

The SF70H is a Ferrari, while Hamilton's 2017 lap was driven in a Mercedes. Label the demo accordingly: **“Fly-derived controller in simulated Ferrari SF70H at Red Bull Ring vs. Hamilton's published 2017 race-lap time.”** Include simulator, assists, car, conditions, and session distinctions. Do not describe this as beating Hamilton in equivalent machinery. Preserve the circuit's full GP layout, standard start line, official timing, track-limit enforcement, and fixed physics/configuration.

## Work packages, in dependency order

All estimates are planning allocations, not runtime promises. Event duration is still unconfirmed; reserve the final quarter for evaluation/demo preparation.

| ID | Owner | Task | Acceptance evidence |
| --- | --- | --- | --- |
| P0 | A + D | Resolve simulator use/access and exact car/track; review event pre-work rules | Written decision with versions and benchmark manifest |
| P1 | A | Validate a supplied/simple controller on the selected simulator | Repeatable state/control exchange; correct signs; stop and reset verified |
| P2 | B | Import official MaleCNS data; benchmark sparse dynamics on both machines | IDs/counts/hashes; peak memory; input sensitivity; latency distribution |
| P3 | C | Capture target-car demonstrations and path geometry | Inspect two pilot laps; use document 10's small session-separated starter dataset and add targeted recordings |
| P4 | B + C | Fit frozen-network readout on Spark | Baseline comparison on held-out episodes; complete portable checkpoint |
| P5 | A + C | Drive one section, then a full lap; add corrective demonstrations | Autonomous progress logs; intervention data clearly separated |
| P6 | C | Improve pace with small, bounded changes | Better valid time without collapsing completion rate |
| P7 | D + team | Freeze candidates; score five attempts per condition | All successes/failures, best/median times, target delta, latency, video |

Use document 10's single provisional 36-hour schedule and protected final eight hours; this research note does not set a second allocation. If event rules permit preparation, resolving access and reading dependencies before the event avoids consuming the first build window; do not implement early without checking those rules.

Keep simulator integration moving with a simple conventional controller while B works on the graph. The simple controller is an engineering baseline, not part of the claimed fly controller. If the full fly policy cannot run locally, qualify local optimizations, a changed cadence, or an explicitly reduced graph. Remote inference is outside the weekend critical path; see documents 10–11. Never silently replace the fly policy with an ordinary model.

## Final integration research notes

- **Skip optional camera setup.** The AssettoCorsaGym Windows guide separates screen-capture support from its core plugin/control setup. Its optional image path introduces another interpreter/process and dependencies. Our telemetry-only design does not need that path. Pin the adapter revision and qualify only the needed plugin, controls, reset, and logging capabilities. The embedded game interpreter and modern model environment remain separate. [Maintainer guide](https://github.com/dasGringuen/assetto_corsa_gym/blob/main/INSTALL.md)
- **Use the Spark stack deliberately.** NVIDIA documents an ARM64 processor and container support. Record a working supported environment from the actual Spark instead of transplanting the older racing repository's x86 dependency pins. Test the required sparse operation, not just GPU detection. [NVIDIA system overview](https://docs.nvidia.com/dgx/dgx-spark/system-overview.html)
- **Small-head training is a separate workload.** Stable-Baselines3 notes that its non-image PPO workload is often suited to CPU. Benchmark our small learner rather than assuming GPU execution is beneficial; the sparse fly graph remains a different measurement. Document 13 specifies the permitted local head-update fallback. [PPO implementation guidance](https://stable-baselines3.readthedocs.io/en/master/modules/ppo.html)
- **Existing racing data is not target-car expertise.** The project's published cars include F317, Miata, and GT3. Use those records for format/integration investigations only unless explicitly changing the experiment; capture SF70H or other chosen target-car actions separately. [Project data description](https://assetto-corsa-gym.github.io/)

These findings change setup priorities, not the unresolved simulator permission/compatibility status. No Windows/Spark setup was exercised, no dataset tables imported, and no external messages sent.

## Draft Kunos inquiry — not sent

> We are four students preparing a hackathon demonstration of a controller based on an open fruit-fly connectome. We would like written permission to use original Assetto Corsa and legitimately purchased DLC for a public educational demo, including recording gameplay. The proposed setup is a local single-car session, the published AssettoCorsaGym plugin and vJoy interface, and a model trained on telemetry/demonstration data. We intend no public leaderboard submissions, multiplayer automation, distribution of game assets, or changes to game protections. Our candidate content is the Ferrari SF70H at Red Bull Ring GP, with results clearly labeled as a cross-simulator comparison against published F1 lap times. Could you confirm permitted use and any licensing requirements? Event/date/location: [fill in]. Organizer/demo audience: [fill in].

## Research limits

No model, simulator, driver, remote service, or dataset has been installed or executed. No claims about actual game compatibility, full-brain performance, or hitting an F1 target are validated yet. The next deliverable after design is a measured integration prototype, followed by a recorded closed-loop driving experiment.
