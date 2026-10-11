# Training and evaluation

Historical/supporting document. Use [10 — execution roadmap](10-implementation-roadmap.md) and [11–13 — current contracts](11-implementation-contract.md) for build decisions. Earlier schedules, inputs, data quotas, and learning recipes below do not add requirements to that plan.

Current training strategy: [document 13](13-hybrid-learning-plan.md) combines demonstrations, autonomous RL, and optional corrections. It supersedes this earlier draft's treatment of RL as only a late pace experiment.

Revision note: [document 09](09-fixed-circuit-controller.md) provides learning rationale; [document 11](11-implementation-contract.md) controls the adaptation and scoring contract; [document 08](08-research-roadmap.md) lists candidate F1 references. A local human baseline is a milestone, not a replacement for the F1 goal.

## Why demonstration learning first

Treat the chosen game as a real-time environment until measured otherwise. At the proposed 25 actions per second, one million transitions would take about 11.1 hours of uninterrupted driving, before resets or training. Spark improves model compute and memory, but a single game instance still limits fresh driving experience. Cold-start reinforcement learning remains a poor weekend default.

Instead, collect human demonstrations, train the fly-network readout offline, test briefly in-game, then add recovery data from the mistakes. This is an engineering recommendation; neither successful transfer nor faster driving is guaranteed. Published connectome-based control research supports studying this architecture, but its simulated-fly locomotion results do not establish racing performance. [Fly-connectomic Graph Model paper](https://arxiv.org/abs/2602.17997)

## Data plan

1. Fix circuit, car, setup, game version, and assists before recording.
2. Collect an initial 10–20 clean laps plus slower demonstrations and gentle recovery examples. Adapt this budget to event length and driver availability.
3. Record raw telemetry and timestamped physical-controller inputs together where possible. Distinguish the driver's requested input from game-reported controls after filtering/assists.
4. Measure input-to-telemetry delay with controlled input changes. Align observations with the subsequent driving command rather than learning an accidental lag.
5. Split by complete laps or sessions into train/validation/test; never randomly split neighboring frames. Build the reference path and normalization using training data only.

Game-reported current steering/throttle/brake are action labels or diagnostics, not same-step input features. Only the prior issued action is allowed in the policy input, preventing a trivial label leak.

## Training stages

| Stage | Work | Exit condition |
| --- | --- | --- |
| A: sanity | Replay ordered observations through fixed network; inspect activity and latency | Stable, input-sensitive states and compatible data |
| B: imitation | Cache pooled recurrent features; fit a small readout to steering and longitudinal demand | Held-out command error improves over constant output |
| C: closed loop | Run conservative section/lap trials; collect interventions and recovery demonstrations | Sustained progress and repeatable valid laps |
| D: pace | Tune a small set of gains or fine-tune readout with a capped online budget | Pace improves without collapsing validity |

Reset recurrent state at the same episode boundary in training and deployment; replay the lead-in before scored flying laps. A lap boundary alone should not reset a continuously driving policy. Cache features by graph, preprocessing, dynamics, and episode-boundary hashes so incompatible runs cannot mix.

Start with a readout producing steering and signed longitudinal demand; map positive demand to throttle and negative demand to brake. A tiny two-head model with bounded outputs is sufficient as a first candidate. This intentionally simplifies simultaneous braking/throttle; document that limitation.

Balance braking examples during fitting so straight-line throttle does not dominate the loss. Validation command error is only a debugging metric: small errors can accumulate into crashes in closed loop. Do not select a checkpoint solely on imitation loss.

During recovery collection, a human takes over and records a correction. Tag those segments; mixed-control runs are training data, never autonomous evaluation results. Train on remote Spark while a frozen checkpoint runs locally; transfer new checkpoints only between episodes. If remote inference is selected instead, schedule training outside evaluation sessions to avoid contention. Cached demonstrations can support multiple seeds and baseline fits without consuming additional game time; replay is supervised training, not new environmental interaction.

## Additional experiments enabled by Spark

Prioritize retaining the full sparse graph, evaluating several readout seeds, and running fair baselines. Once the fixed-network controller works, a second experiment may train input adapters or per-population gains through a short recurrent unroll while preserving graph topology. Measure gradient support, stability, memory, and held-out driving outcomes before scaling to trainable edge weights. This is a distinct model condition and must have its own checkpoint and report. More memory alone does not justify training every connection from scratch or adopting a biophysical simulator during the weekend.

## Optional online optimization

Only attempt this after stable laps. Prefer bounded searches over a few readout/control parameters before introducing a full RL stack. Change one policy version at a time, retain the best validated checkpoint, and cap the wall-clock budget.

If using rewards, reward forward distance along the path minus time and smoothness costs, penalize invalidation and crashes, and give a valid-completion bonus. Use game elapsed time, not process speed. Guard distance increments against teleports, lap wraps, resets, and reverse-then-forward farming using ordered progress/high-water marks. Stop progress rewards after invalidation. Log termination reason separately from reward.

PPO or another online algorithm is a later option if enough interaction time remains; the design does not depend on achieving RL convergence over the weekend.

## Evaluation protocol

Freeze the target, settings, checkpoint, preprocessing, graph, and controller calibration before scored attempts. Read `target_ms` and its driver/session/source from the selected run manifest. One candidate is **64,391 ms**, Verstappen's 2023 Austrian qualifying lap; document 08 contains alternatives, not interchangeable results. [FIA result](https://www.fia.com/events/fia-formula-one-world-championship/season-2023/austrian-grand-prix/qualifying-classification)

Use a consistent automated lead-in where feasible; score only a complete flying lap with game-confirmed validity. Exclude out-laps from lap-time comparisons. A reset, flashback, cut, intervention, or missing validity evidence prevents a scored success. Preserve evidence for both the completed lap and any next-lap invalidation carried over from the lead-in.

Run five predeclared attempts per final condition as the initial budget. Record all failures; report best and median valid lap time, valid-lap fraction, and how many valid laps contribute to the median. If there are none, report no valid time and maximum completed distance. Use more attempts if time allows, with equal budgets for comparisons.

Compute `delta_ms = game_lap_ms - target_ms`. A negative value beats the numeric reference. `100 * delta_ms / target_ms` gives the percentage gap. Require a positive configured target and use integer milliseconds for comparisons and display formatting separately.

A single published total time cannot define Verstappen's position or time at every point on the circuit. Do not fabricate a live ghost or corner-by-corner delta from it. Show completed-lap delta to Max; live path-relative delta may instead use a separately identified recorded team lap.

## Does the fly graph contribute?

| Condition | What it tests |
| --- | --- |
| Human demonstration driver | Data quality and attainable pace under these game settings |
| Small conventional policy on the same observations | Whether fly-derived features add value over a straightforward learned controller |
| Fixed fly graph + trained readout | Primary proposal |
| Graph with randomized connections + retrained readout | Whether this wiring helps beyond recurrence and model size |
| Fly activity removed or shuffled at inference | Whether the deployed policy depends on neural features; diagnostic only |

Give trained baselines the same data splits, inputs, action limits, and evaluation settings; report architecture and parameter counts. Keep randomized graph size, approximate degree/weight statistics, dynamics, and fitting budget comparable. Removing features at inference creates distribution shift, so failure alone does not prove the biological topology is special. A weekend pilot supports observations, not broad neuroscience claims.

## Saved evidence

Each evaluated run should retain a manifest with game/patch/protocol, track, car/year, setup, weather, assists, damage/track-limit settings, DRS/ERS behavior, control rate, graph hashes and counts, model seed/checkpoint, training split, hardware, latency statistics, and attempt results. Save raw packets, compact policy/action logs, and video of the best valid attempt. Retain dataset and reused-code attribution separately.
