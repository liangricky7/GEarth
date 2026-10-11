# Implementation contract and readiness checks

Status: design only. This document resolves implementation details left open in the earlier drafts. It takes precedence for interfaces, timing, training updates, and acceptance checks. [10 — roadmap](10-implementation-roadmap.md) controls the build sequence; [08 — research](08-research-roadmap.md) controls source evidence and simulator caveats. Nothing here establishes tested hardware performance.

[12 — fly-network specification](12-fly-network-specification.md) now defines the proposed graph construction, artificial input/output mapping, dynamics, and readout recipe. It supersedes the open network choices in older drafts; its qualification results remain unmeasured.

[13 — hybrid learning](13-hybrid-learning-plan.md) supersedes imitation-only adaptation: demonstrations initialize the controller, RL provides autonomous practice, and human corrections remain available. Its behavior-policy provenance and rollout-boundary rules apply to RL; the supervised cycle below remains the demonstration/correction path.

Use the [run-manifest template](run-manifest.template.json) to collect configuration decisions. It is a planning artifact, not executable configuration; `null` and `false` readiness fields deliberately expose unfinished decisions. Implementation should add a validator before treating a completed manifest as runnable.

## Decisions we can settle now

| Topic | Version 1 decision |
| --- | --- |
| Placement | Simulator, inference, controls, and watchdog on Windows; graph preparation and training on remote Spark |
| Learning | Fixed graph and mappings; demonstration initialization, RL on the action head, optional human corrections; document 13 defines their combination |
| Updates | Replace checkpoints between episodes; for proposed PPO, collect/train/evaluate in versioned rounds and reject stale-policy data from the next on-policy batch |
| Inputs | Numeric telemetry and a known reference route; no camera pipeline |
| Outputs | Steering and signed longitudinal demand; positive means throttle, negative means brake |
| Cadence | Start with 25 Hz / 40 ms; supersedes the earlier 20 Hz proposal; qualify on hardware |
| Episode reset | Clear recurrent state, previous action, route projection, and input buffers together |
| Simulator reset | Manual reset is acceptable for initialization and supervised practice; unattended RL requires qualified automatic resets |
| Comparison | Final fly policy, saved imitation-only fly policy if distinct, and one conventional feedback baseline; common data/settings/action limits. Replay baselines are deferred by document 10. |
| Deadline fallback | Optimize local execution, then explicitly change cadence or graph scope; remote driving is a separate measured experiment |
| Target | Configured integer milliseconds and cited driver/session; never hard-code a driver/time into evaluation logic |

Choose the implementation language/framework after a minimal Windows/Spark sparse-operation smoke test. Proposed split: Python application/model code plus only the native/plugin pieces required by the chosen simulator. Pin working versions separately on each machine. Do not introduce a web backend, orchestration service, or distributed training requirement for this prototype.

## Module boundaries

These signatures describe behavior, not finished APIs or a promised simulator capability.

| Module | Contract | Owner |
| --- | --- | --- |
| Simulator adapter | `read_latest()` yields canonical raw state; `apply_controls()` issues calibrated controls; `release_controls()` neutralizes them | A |
| Feature builder | `build(state, route, previous_action)` yields ordered features or a specific rejection reason | C |
| Graph core | `reset()` initializes state; `step(features)` returns pooled features and updates internal state once per control tick | B |
| Readout | `predict(neural_features)` returns two finite normalized action requests | C |
| Control owner | Bounds/slews requests, validates age and episode, writes controls, records issue time | A |
| Episode manager | Owns mode, checkpoint identity, start/end events, and authority to arm | A + D |
| Recorder/evaluator | Writes append-only events, joins observations/actions, determines attempt outcomes | D |
| Trainer/exporter | Reads completed versioned data snapshots; produces an immutable candidate bundle | B + C |

Only one process owns automated controller writes. The inference worker must not hold the only release-control path: its crash must leave the independent control owner able to neutralize inputs. A physical human controller may take over in correction mode; first release the automated device so the two cannot fight. Validate device arbitration on the actual simulator.

Do not connect every module to every other module. The display consumes logs/status and never decides actions. Spark receives completed episode files and returns candidate files; it is not required for an active lap.

The qualified small-head training fallback in document 13 may run locally between episodes if remote setup/transfer dominates. This is a recorded training-location change, not remote control of the game or concurrent training on the active driving policy.

## One concrete non-fly comparison

Use a small learned feedback controller: the same 21 normalized observations feed a `21 → 64 tanh → 2` MLP, followed by the identical clipping, longitudinal mapping, slew limit, and watchdog. It receives no extra route-speed schedule, hidden game state, or future observation. This is a separate named condition and never a bypass inside the fly policy.

Fit on the same initial expert sessions and loss-weighting rules as fly imitation. Use document 12's shared MLP fitting recipe. Record training/evaluation cost and report failures even if it never completes a lap; do not quietly replace it with a stronger pretrained controller. Compare the two initial imitation policies on common data. Comparing the final mixed fly policy with this baseline evaluates the full training procedures if their experience differs, not an isolated causal benefit from the fly wiring.

This choice makes a minimal comparison implementable without building a hand-tuned racing-line/speed planner. It does not guarantee that this baseline drives well. Timed replay, distance replay, and a classical path-tracking controller remain optional later comparisons under document 10's budget.

## Canonical observations and actions

Version every record with `schema_version`, `run_id`, `episode_id`, and a monotonic local sequence number. Log simulator session identity when available. Use SI units and distinguish unavailable values from zero.

Coordinate convention: car forward is positive longitudinal velocity; car left is positive lateral velocity; positive heading error means the reference tangent lies left of the car heading. Positive lateral deviation means the car lies left of the reference path. Positive normalized steering means left. Each simulator adapter must convert from its native axes/signs; check with a hand-driven turn.

The initial policy feature vector has **21 values**, in this saved order:

| Index | Feature |
| --- | --- |
| 0–1 | Sine/cosine of projected lap phase |
| 2–3 | Longitudinal/lateral velocity, m/s |
| 4 | Signed lateral deviation, m |
| 5–6 | Sine/cosine of heading error |
| 7–16 | Forward/left coordinates of reference points 10, 25, 50, 100, and 150 m ahead, in that order |
| 17–18 | Previous final issued steering and signed longitudinal demand |
| 19–20 | Elapsed control interval and telemetry age, seconds |

No gear, yaw rate, target time, or reference-speed schedule enters version 1. These may be separately versioned experiments if evidence warrants them. Automatic gear selection and DRS/ERS settings belong in the simulator manifest. Reference points come from training data; they are disclosed prior track knowledge.

Store raw features before normalization. Fit scaling statistics on training episodes only; retain bounded sine/cosine and action values in their declared ranges. Store exact scaling, epsilon, and any clipping bounds in the checkpoint. Reject nonfinite/missing required inputs before normalization. Do not use clipping to conceal impossible positions or bad units.

Bounded action request: `{steer, longitudinal}` in [-1, 1]. Raw model outputs and document 13's Gaussian samples may be outside that range and are logged separately. The control owner clips both raw coordinates, applies a configured steering slew limit, and maps `throttle=max(longitudinal,0)`, `brake=max(-longitudinal,0)`. It logs raw, bounded, final issued, and game-observed controls separately. Pick the slew limit during calibration and freeze it for all comparisons; do not invent an unmeasured universal value.

Human recordings may contain simultaneous throttle/brake, which this action representation cannot reproduce. Keep raw inputs and mark overlapping samples. Exclude their supervised labels initially while still replaying the observed sequence for recurrent state; represent previous longitudinal demand as previous throttle minus brake and label the overlap as a lossy projection in metadata. If overlap is frequent or necessary for driving, revise the action contract and retrain before proceeding. Do not silently label such samples as exact imitation targets.

## Timing and synchronization

Run one ordered inference stream per episode. At each 40 ms tick, select the newest coherent state, construct one observation, update the network using the configured number of internal updates, and issue at most one action. Use a latest-state slot rather than a growing queue. Do not perform catch-up bursts after a stall.

Each action echoes observation sequence, episode, and checkpoint IDs. Drop results for a past episode or replaced checkpoint. Reusing a telemetry sample across ticks is explicit in logs; recurrent dynamics still advance once per policy tick. Cadence changes require replay/retraining and a new configuration version.

Measure timing with the laptop's monotonic clock. Track receipt time per required state field and true capture time when it is trustworthy in that clock domain. If source capture time is unavailable, label freshness as receipt-based and measure transport behavior; do not report it as exact simulator-state age. Never subtract Spark timestamps from laptop monotonic timestamps.

Provisional performance gates: p95 observation-build through action issue below 30 ms, p99 below 40 ms, measured over a representative run with the game rendering. Record stale fields and missed ticks separately. These are acceptance targets, not estimates.

Initial watchdog parameters: poll approximately every 20 ms; neutralize and terminate when required telemetry or the last accepted action exceeds 200 ms, on an explicit fault, or on human stop. Action expiry is measured from its source observation's local build time, not from a late arrival. A discarded late result cannot refresh the watchdog. Verify release timing on the actual host; proposed observed release bound is 250 ms from loss of healthy updates. Windows scheduling is not a hard real-time guarantee.

For demonstration labels, pair the state available at a decision time with the teacher command issued at that time, carrying the previous command as history. Measure actuator delay separately. Do not blindly shift labels to later simulator-observed controls, which can accidentally teach delayed or future information. If requested human controls cannot be captured, declare the game-observed proxy and validate its lag before bulk data collection.

## Episode lifecycle and lap scoring

```text
IDLE -> READY -> RUNNING -> TERMINATED -> IDLE
                  |
                  +-> CORRECTION -> TERMINATED
```

- **IDLE:** automated controls released. Checkpoint replacement and manual game reset permitted.
- **READY:** settings/checkpoint validated, fresh telemetry available, graph and feature state initialized. Remain neutral until explicit start.
- **RUNNING:** one fixed policy and configuration. Log all steps, including the lead-in/out-lap.
- **CORRECTION:** human has taken over; automated controls remain released. Continue recording observations/human actions. This episode cannot produce an autonomous scored success; restart before autonomous testing resumes.
- **TERMINATED:** controls released, final reason recorded, episode file closed. Recovery of a service never re-arms automatically.

On arming, initialize recurrent state to zero and previous action to neutral. Use the same initialization in training replay. Record and replay the entire lead-in so a flying lap begins with the same kind of recurrent history. Crossing the finish line alone does not reset the network. Never treat a saved corner snippet as if it began with the correct hidden state; retain its parent episode and replay the prefix.

A final scored attempt contains its preparation/lead-in and **one designated full flying lap**. Use the first eligible flying lap after the agreed lead-in, not the fastest of an unlimited session. Fix lead-in allowance and timeout before comparison. Any human control after autonomous start terminates that attempt. Manual positioning/reset beforehand is allowed and logged. Development section trials are separate from final flying-lap attempts.

End reasons include `valid_finish`, `invalid_lap`, `intervention`, `crash_or_stuck`, `timeout`, `stale_telemetry`, `policy_fault`, and `operator_abort`. Keep all attempts in the denominator. Lap validity must be established by configured simulator signals/rules, including penalties carried from the lead-in; ambiguous evidence produces an unverified result, not a success.

## Adaptation cycle: drive, correct, train, compare

1. C selects a specific failure to address; D records checkpoint, location, state, and failure reason.
2. A/C record new human recoveries or improved demonstrations. Mark policy-driven versus human-labeled intervals. A failed policy's actions are not automatically correct labels.
3. Close the episodes and transfer a complete dataset snapshot to Spark. Write files to temporary names, then mark complete; verify hashes before consumption.
4. Keep original successful training episodes plus new corrective episodes. Initial fitting proposal: equal total loss weight for the original and correction pools when both exist, normalized within each pool. With only the original pool, assign it the full loss weight. This is a tuning starting point, not a proven optimum. Do not let long straights overwhelm braking examples.
5. Replay ordered episodes through the frozen graph and fit a candidate readout. Unlabeled policy intervals can supply recurrent history, but receive no imitation loss.
6. Validate offline and export a complete immutable bundle. Test compatibility and recorded-sequence parity locally before driving it.
7. Compare candidate and incumbent on the same small development protocol. Swap only in IDLE, with neutral controls. Keep a last-known-working bundle locally.

Use development trials for all corrections and candidate decisions. Held-out test episodes never become corrective training data. Group continuous sessions and their derived snippets into one split. Additional recovery episodes join the training pool; validation stays fixed unless a documented new dataset version is created.

For the initial small dataset, document 10 proposes five independent recording sessions: three training, one development, and one untouched offline test. This supersedes an approximate percentage split as the startup procedure. More data is driven by observed coverage gaps; the small initial test set supports limited evidence, not a broad generalization claim.

Initially refit the small readout on the accumulated dataset rather than requiring incremental optimizer updates. This is still adaptation, and makes rollback/reproduction simpler. Keep input normalization, route, graph, mappings, dynamics, and output selection frozen during this loop. Changing any of them invalidates cached raw neural features and requires complete replay of the affected dataset plus a fresh compatibility check. The cache key includes all those hashes, feature schema, cadence, episode content, and reset rules. Document 12 also specifies training-derived scaling of the sampled neural features: freeze it during adaptation; changing only that scaling requires reprocessing cached raw features and refitting readouts, not recomputing the recurrent trajectory.

Candidate promotion uses **three comparable development attempts per version** initially. A candidate is eligible only if it passes all interface checks and does not increase observed valid-lap failures. Among eligible versions, prefer more valid finishes; if tied with valid laps, lower median valid time; if both have no valid finishes, greater median legal progress. Treat mixed/tied outcomes as inconclusive and retain the incumbent or collect more evidence. These tiny samples guide development and do not establish statistical superiority. Final reporting uses the separate five-attempt protocol.

Spark or internet unavailable: continue local inference/data collection with the incumbent; queue completed episodes. A half-uploaded dataset or partial checkpoint is never usable. Avoid adding a network service when simple file transfer is sufficient.

## First checks to implement

| Check | Evidence required before proceeding |
| --- | --- |
| Signs/units | Known manual left turn and pedal commands agree with canonical telemetry/action signs and ranges |
| Stop path | Kill the inference worker and disconnect telemetry separately; observe neutral controls within the calibrated bound and no automatic restart |
| Stale result | Feed an old episode/action result; no control change occurs |
| Route geometry | Replay a full lap, start-line crossing, and a nearby parallel track section; no unexplained progress jumps |
| Recurrent reset | Replay the same episode twice from reset; results match within fixed tolerance; continuous lap wrap does not reset state |
| Data alignment | Inspect a short recorded braking event with teacher issue times and game response plotted together |
| Label/split integrity | No current action leaks into observations, policy actions are not corrective labels, parent sessions do not cross splits |
| Graph contribution | Perturb observations and disable recurrent propagation separately; record whether pooled features respond |
| Export parity | Same episode on both hosts, comparing normalized features, pooled activity, and final issued controls against predeclared tolerances |
| Model update | Reject incompatible/truncated bundles; apply a valid bundle only in IDLE; restore the incumbent successfully |
| Scoring | Example valid, cut, intervention, timeout, and missing-validity runs yield the expected results and denominator |

Write small automated checks where records and model behavior allow it; perform device/game behavior checks on the actual host. These checks target failures that could invalidate all subsequent training.

Also kill the control-owner process during a controlled low-speed test and observe the device behavior. An independent owner protects against an inference-worker crash, not its own crash. If automatic neutralization is not demonstrated for that fault, document the limitation, retain an operator-accessible game stop, and prohibit unattended runs until a qualified release path exists. Do not claim that the 250 ms target covers untested process/device failure modes.

## Decisions still requiring evidence

| Unresolved item | Owner | How to close it | Blocks |
| --- | --- | --- | --- |
| Selected Assetto Corsa: access/permitted demo use and exact version | A + D | Team selected the simulator October 9; confirm access/use evidence and version from document 08 | Qualified simulator integration |
| Build duration and available demonstration driver | Team | Confirm schedule and driver/input device | Time budget and data plan |
| Laptop capacity and full-graph execution | B | Hardware inventory and concurrent game/model benchmark | Accepted cadence/full-graph deployment |
| Native telemetry fields, age, and lap validity | A | Map actual adapter fields, log examples, check UI | Canonical features and scored claims |
| Human takeover and input capture | A + C | Controlled pilot with actual controller/wheel | Corrective labels |
| Source-schema audit and graph qualification | B | Apply document 12's explicit recipe; verify retention, mapping reachability, responsiveness, and bounded dynamics candidates | Feature cache and trained policy |
| Target, start procedure, timeout, car setup | C + D | Manifest frozen before scored evaluation | Comparable attempts |
| Steering limit, numeric tolerances, software versions | A + B | Measured calibration and portability checks | Accepted deployment bundle |
| Pause, focus loss, and simulator-clock stalls | A | Specify available detection signals and test pause/resume/focus changes on the chosen game | Reliable live control |
| Crash/stuck detection and legal-progress measurement | A + D | Define speed/progress window, allowed startup grace, invalidation behavior, and timeout; replay example failures | Comparable failure and candidate-promotion results |
| Correction-to-candidate turnaround | B + C | Time one complete upload, graph replay, fit, export, and local validation cycle | Realistic number of adaptation rounds |
| Demonstration quality and path beyond teacher pace | C | Measure teacher times against the target; choose faster demonstrations or a bounded improvement experiment with remaining time | Credible route to the headline time |

An unresolved item is not permission to silently guess a value in production code. Use labeled placeholders in configuration, validate required fields at startup, and fail with the exact missing decision. Work on independent components while evidence is gathered.

## Audit follow-ups before finalizing the build plan

The overall architecture is specified, but the following operational details must not be lost among configuration placeholders:

- **Pause/resume:** wall-clock ticks can continue while a game is paused and fresh packets may repeat a frozen simulator state. Receipt freshness alone does not detect this. For the initial real-time adapter, a detected pause or loss of control focus releases inputs and ends the attempt; resume requires a new episode. Use available simulator clock/session signals and verify them. If reliable automated detection is unavailable, require operator stop before pausing and document that limitation. A future stepped simulator needs an explicit simulation-time scheduling contract rather than inheriting wall-clock behavior.
- **Failure detection:** specify startup grace and the observation window before classifying slow motion as stuck. Measure legal progress along the ordered route and stop accumulating it on invalidation; resets, teleports, or reverse/forward oscillations must not improve a candidate's score. Actual thresholds depend on the selected car/track and require pilot logs.
- **Adaptation budget:** measure end-to-end turnaround, including graph feature replay and transfer, rather than timing readout fitting alone. Reuse compatible cached episode features; process only new completed episodes when the feature pipeline is unchanged. Choose the number and size of correction rounds from measured turnaround and the event clock.
- **Faster-than-teacher plan:** before promising the target, record the teacher's best valid time and gap. If a teacher already beats the target, imitation remains a plausible route to test. If all teachers are slower, reserve explicit time for a faster teacher or optimization and report that this is an unproven experiment. Successful error correction alone does not establish a route to professional pace.

## Additional performance questions to qualify

These are proposed experiments, not established defects or automatic changes to `route21-v1`. Plan their procedures now and measure them during integration. Simulator selection/access, source-schema inspection, and local inference qualification remain the first gates.

| Question | Why it matters | Bounded qualification and decision | Owner |
| --- | --- | --- | --- |
| Does the actor observe enough vehicle state? | Similar speed/position/heading can require different actions with different yaw motion, grip, or vehicle condition. Short recurrent memory may not resolve the ambiguity. | Log available yaw rate and condition fields as diagnostics without adding them to actor inputs. Inspect repeated failures and matched approach states. Standardize controllable conditions first; only introduce a versioned feature change when evidence identifies missing information. Rebuild affected feature caches and retrain. | A + C |
| Is route lookahead long enough for braking? | The proposed farthest reference point is 150 m. At 80 m/s that represents only 1.875 seconds of travel; that arithmetic does not establish adequate braking distance. Lap phase can support memorized braking but does not prove robust anticipation. | Measure teacher braking onset, approach speed, route curvature, and stopping response in the target car. If needed, compare one longer-range feature variant or a separately declared speed-planning variant. Keep training/deployment features and baseline information consistent. | C |
| Do resets produce comparable vehicle conditions? | Fuel, tyres, damage, temperatures, and preparation can change across attempts even when the starting location matches. | Inventory supported reset/condition controls, fix the lead-in, log available conditions, and record tolerances or limitations before comparing candidates. Match incumbent/candidate conditions and alternate their order when feasible. Do not silently remove poor attempts after seeing their outcomes. | A + D |
| Can we collect enough practice in the available time? | A remote accelerator does not shorten real-time driving, resets, or candidate evaluation. A small training head may fit quickly while experience collection dominates. | Time a complete pilot round. Estimate round time as collection plus reset overhead, transfer, training/export, local validation, and development evaluation. At the proposed 25 Hz, 10,000 transitions alone require 400 seconds of uninterrupted real-time driving. Choose batch sizes and the maximum number of rounds from measured costs; reserve the existing final evaluation window. | B + C |
| Does the game compete with the neural model for laptop resources? | Graph execution alone can pass while simultaneous rendering, recording, and inference cause missed deadlines or allocation failures. | Qualify the exact intended demo workload over representative driving, tracking memory headroom, inference tails, and missed ticks. Freeze graphics/recording settings with the accepted configuration. Apply the documented execution/cadence/graph-scope fallbacks if it fails. | A + B |
| Is the fly condition competitive with simpler controls? | Good offline action error does not establish useful closed-loop control or an advantage from fly wiring. | Bring up the planned replay/feedback baselines early under common conditions. Compare section completion, valid laps, time, and execution cost. Use these to separate adapter/feature failures from model failures; use document 12's matched-graph comparison if claiming a wiring advantage. | C + D |

Keep simultaneous experiments limited: change one major component at a time, save the reason and parent configuration, and evaluate before combining changes. Nominate C to maintain the accepted training configuration, with B owning graph revisions and A owning adapter/feature-interface compatibility. Everyone develops against the same recorded schema and fixture episodes; only the designated publisher promotes an immutable candidate bundle through the existing checks.

Close these items through the same manifest and acceptance checks; do not start separate competing design documents.
