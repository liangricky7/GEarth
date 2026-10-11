# Reinforcement learning from scratch

Status: candidate only, October 9, 2026. The user reopened training-method selection; this proposal is NOT the accepted implementation specification. If selected, this proposal would replace demonstration initialization, ridge fitting, expert losses, and human-correction training. See document 16 for the comparison before choosing. Document 10 owns the schedule, 11 owns runtime/scoring, and 12 owns graph construction. No settings in this candidate take precedence until the team selects and qualifies the method. PPO is a worked candidate, not a settled choice over SAC.

## Meaning of from scratch

Initialize the driving actor and critic with new random weights. No human/AI demonstrations, F3/F1 starter data, pretrained driving checkpoint, expert loss, or teacher speed/braking schedule. The agent creates experience by controlling the selected car. Human calibration, resetting, and stopping are allowed; their records never supply learning targets.

The connectome remains a structural prior. Numeric telemetry, known track geometry, automatic gears, declared assists, action limits, rewards, and curriculum are engineered support. The network does not read prose tutorials. Describe the result as a connectome-derived controller learning driving from scratch with telemetry and track geometry. It is not screen-only, biologically faithful learning, or random initialization of every fly connection.

Keep the graph, mappings, dynamics, and output selection fixed. Train the action head, exploration parameters, and a separate critic. Transient neural activity differs from saved parameter learning. No raw-observation bypass enters the fly actor.

## Remove demonstration dependencies

- Obtain an ordered closed route from track geometry, preferably a centerline derived from verified boundaries. A locates accessible assets/API fields; C checks axes, direction, lap wrap, parallel sections, and legal progress. The exact source remains unverified. Do not quietly import an expert racing line or driving traces. If only an AI racing line is accessible, explicitly resolve and disclose that different prior before use.
- Retain document 11's 21 feature meanings/order, versioned as `route21-scratch-v1`. Proposed fixed scales: sine/cosine and previous actions unchanged; forward velocity /100 m/s; lateral velocity /30 m/s; lateral error /10 m; each lookahead coordinate /its lookahead distance; dt /0.04 s; telemetry age /0.2 s. Clip scaled continuous values to [-5,5] only after rejecting invalid raw telemetry. These are unqualified engineering scales.
- Calibrate 128 readout-feature means/stds using at most 2,500 valid ticks from the untrained policy after runtime qualification. During calibration use raw pooled states with no optimizer updates. Proposed std floor: 1e-6; reject apparent features dominated by numerical noise. Freeze these scales, reset the graph, and start fresh training collection. Calibration is not an expert dataset or a PPO batch.
- Missing geometry or dead features block training. More random driving cannot repair unavailable observations. Do not fabricate maps or scaling statistics.

## Curriculum

One car, track, physics, setup, and assist configuration. Initially change only task endpoints and action caps. Each stage records start procedure, task length, timeout, success rule, action limits, and reward hash. Stage changes occur between episodes and require fresh PPO data.

| Stage | Task | Initial proposal |
| --- | --- | --- |
| 1 | Legal forward progress and a straight section | Throttle cap 0.25; full braking; common calibrated steering limits |
| 2 | First manageable corner, including approach and exit | Throttle cap 0.5 |
| 3 | Connected corners, extending toward a lap | Throttle cap 0.8 |
| 4 | Valid full laps, then pace improvement | Full throttle range and frozen final conditions |

A throttle cap is not a speed governor: sustained low throttle can still reach high speed. Qualify task lengths/caps and log changes. The actor selects all steering/pedal commands; automatic braking or path tracking would be a separate disclosed controller condition.

Do not assume corner spawning exists. Start with the supported standard reset and progressively longer prefixes from that location. Adapt the first task to the actual start if it lacks a straight. Optional section resets require verified position, vehicle conditions, graph reset, and progress accounting. Manual positioning occurs before arming and receives no learning loss.

Proposed advancement gate: 4/5 successful deterministic development attempts on the unchanged task. This is a practical threshold, not statistical proof. A schedule milestone is not permission to advance a failing policy. Retest earlier tasks before champion promotion after a stage change. Reward values across stages are not directly comparable.

## Initial learner: PPO without demonstrations

Retain PPO as a first implementation proposal to limit integration changes, not as a claim of best sample efficiency. PPO requires fresh experience from its behavior policy; archived records cannot become the next on-policy batch. [Primary algorithm explanation](https://spinningup.openai.com/en/latest/algorithms/ppo.html)

| Setting | Unqualified starting proposal |
| --- | --- |
| Actor mean | Random `128 → 64 tanh → 2`, linear unbounded output |
| Critic | Separate random `128 → 64 tanh → 1` |
| Initialization | Seed 2026; orthogonal hidden gain sqrt(2), actor output gain 0.01, critic output gain 1; zero biases |
| Exploration | Learned state-independent Gaussian log std; initial std [0.3, 0.5]; constrain log std to [-3,0] |
| Execution | Original sample → clip [-1,1] → stage throttle cap/common steering slew → game |
| Collection | Minimum 2,048 eligible transitions; then finish the current attempt within its declared timeout |
| Optimization | Up to 3 passes, minibatch 128 including final smaller batch; separate Adam actor/critic rates 0.0003; gradient norm cap 0.5 |
| Returns | Gamma 0.999 at 25 Hz; GAE lambda 0.99; normalize batch advantages, no reward normalization |
| Constraints | PPO ratio clip 0.2; mean old-to-new Gaussian KL stop at 0.01 after each actor minibatch |
| Losses | PPO actor objective, entropy coefficient 0.001, separate critic MSE, zero expert loss |

Actor mean: 8,386 parameters plus two exploration parameters; critic: 8,321. This replaces the 258-coefficient imitation readout. No positive-throttle teacher bias is supplied. Do not reuse the old tiny fixed exploration settings.

Inspect actual issued controls: clipping/slew/caps may erase exploration or cause chatter. Verify untrained practice produces movement and meaningful steering variation. If not, diagnose the transform and make one documented exploration/cap change. Correlated noise or action repetition requires a new probability/timing contract before adoption.

Compute joint Gaussian log probability for the original unbounded sample, never for its clipped control. Save old mean/std and include both changes in KL. Entropy measures the latent distribution, not effective variation at the wheels. Freeze policy, feature scaling, stage, and action transform throughout each batch.

Use a pinned inspected PPO implementation/reference. Importing saved rollouts on Spark requires an explicit buffer/trainer integration; stock `learn()` does not provide remote file training automatically. Qualify local CPU head updates between attempts if remote overhead dominates. [Implementation guidance](https://stable-baselines3.readthedocs.io/en/master/modules/ppo.html)

## Rewards and transition boundaries

Section reward proposal: new legal progress / declared section length, plus +1 once at verified section completion. Full-lap task: new legal progress / circuit length, plus +1 only for the designated eligible valid full flying lap. Initial partial laps earn no finish bonus. Explicit extra failure/time coefficients start at zero; termination and discounting supply consequences. Log every component. This is a completion-first reward, not a guarantee of optimal lap time.

Use ordered unwrapped progress and a per-episode high-water mark. Reverse/forward motion cannot earn the same metres twice. Check displacement against simulator time/motion. Reject teleports or ambiguous projection rather than clipping jumps into rewards. Cuts/invalidation end the episode and stop reward. Never reward raw speed, throttle, survival, or resets alone.

Each transition links `z_t`, original action sample, issued control, interval reward, `z_next`, behavior-policy/stage/config IDs, old log probability/value, termination flags, and loss masks. Compute `z_next` from the actual next decision observation and previous issued action, preserving recurrent history. Never use the next episode's reset observation as its predecessor's successor. Cache fixed-graph features for head updates; do not shuffle raw observations through the graph or reset at storage chunks/intermediate line crossings.

- Success, driving failure, stuck condition, and declared task deadline are terminal: no bootstrap, no GAE trace across the boundary. Declare startup grace before classifying an untrained stationary car as stuck.
- An artificial collection cutoff is a truncation: bootstrap only from trustworthy successor features, then stop the GAE trace. Prefer completing the current attempt after the minimum batch quota so training can observe success.
- Infrastructure faults/human stops are not driving penalties. Exclude the affected transition and later rows. Keep the earlier clean prefix only if its final successor is known, using a truncation there; otherwise exclude the incomplete prefix. Human commands receive no learning loss.
- Evaluation, manual calibration, and graph-scaling calibration never enter PPO. Tag sources separately.

Before updates, test reward and masks on success at two speeds, partial failure, waiting, reverse/repeat progress, illegal cut, partial-line crossing, teleport, stale telemetry, human stop, and collection cutoff. Check discounted targets separately from raw totals. Faster otherwise identical successes should have higher discounted return. These checks do not prove the absence of all reward exploits.

## Learning continuity and preserving progress

Maintain a continuing **learner** and a separately evaluated **champion**. Save the untrained checkpoint and each stage champion. Two early failed deterministic evaluations do not trigger abandonment or automatic learner rollback: discovery may take many updates. Reject invalid/nonfinite updates; restore learner/optimizer/exploration state when measured regression justifies it.

Collect → close/hash/transfer → update → export/verify parity → collect fresh experience. Replace weights only in IDLE. Older successes are diagnostic records, not reusable PPO samples. Revisit earlier tasks through fresh current-policy practice at batch boundaries.

Evaluate at stage boundaries and every 10 updates or 30 elapsed minutes, whichever comes first. Use three deterministic attempts per candidate/incumbent under identical task conditions; rank success count, median completion time, then median legal progress if neither succeeds. Retain the champion for mixed results. Before promotion, check fixed earlier tasks too. These tests reduce regression risk without guaranteeing future behavior.

Changing route, normalization, graph, mappings, dynamics, or cadence requires a new feature version, affected-cache rebuild, and requalification. A stage change preserves features but changes environment/configuration and invalidates batch eligibility.

## Evidence and limits

The comparison actor is a random `21 → 64 tanh → 2` MLP, with a separate value head and the same PPO settings, tasks, observations, and limits. Agents collect their own on-policy data. Predeclare budgets and compare matched-step checkpoints for efficiency claims; disclose unequal budgets. This does not establish a special benefit from biological wiring, which would require additional matched-graph experiments.

Report learning curves, legal progress, task success, valid lap times when available, cumulative transitions, and wall time, separated by stage. Primary deliverable: honest evidence of learning and the best autonomous task achieved. A valid full lap and ultimately a published professional time remain increasingly ambitious targets.

At real time and 25 Hz, 100,000 decisions take about 1.11 hours and 1,000,000 about 11.11 hours before resets, updates, transfers, and evaluation. These are arithmetic bounds, not convergence predictions. One game instance is assumed; Spark does not accelerate its experience generation by itself.

AssettoCorsaGym supports studying from-scratch RL and reports benefits from demonstrations for its tested controllers. It does not establish weekend convergence for this fly architecture or F1 car. [Research](https://assetto-corsa-gym.github.io/)

If learning stalls, inspect rewards, observations, exploration, and resets, then change one variable. Narrow the task if needed. The fallback is a labeled progress/section demo or documented unsuccessful experiment. Do not silently switch to demonstration training or a pretrained driver.
