# Demonstration learning, RL, and optional corrections

Decision status, October 9: training method is under review. This is the previous hybrid proposal, not a newly confirmed choice. Compare [16 — training options](16-training-options.md) and [17 — from-scratch candidate](17-from-scratch-candidate.md). Do not implement these prior defaults until the method is selected.

Status: proposed October 5, 2026; no implementation. The user has selected a mixed approach. This document replaces the earlier assumption that ongoing adaptation is imitation-only and RL is merely an optional pace experiment. The graph recipe in document 12 and runtime contract in document 11 still apply except where the training-specific changes below are explicit.

Execution revision: [document 10](10-implementation-roadmap.md) owns the realistic schedule and experiment limits. A bounded RL pilot remains planned after meaningful section control; continued RL requires a qualified update cycle and evidence of development benefit. Demonstration/correction learning remains the deployable fallback. Conditional deployment does not change the intended mixed strategy or authorize claims that untested RL worked.

## Intended progression

**Demonstrations establish basic control → RL explores improvements → human corrections address persistent problems → evaluate and retain the best candidate.** These modes can alternate; there is no requirement to remove human help permanently once RL begins.

The architecture keeps the fly-derived graph and observation pipeline fixed initially. Learning changes the action-producing head. Human labels train it to reproduce good actions; RL rewards train it to favor actions with better outcomes. A separate value estimator supports RL training and does not issue controls.

This learning is engineered parameter optimization around a connectome-derived reservoir; it is not a reconstruction of mushroom-body plasticity or dopamine signaling. Neural state changing during driving is transient computation, while saved actor weights store lasting learned changes. [Document 14](14-fly-biology-and-model-review.md) explains the biological distinction.

AssettoCorsaGym provides a relevant simulator precedent for RL with human data and from-scratch comparisons. It does not validate our fly-derived model or establish F1-car performance. [Research project](https://assetto-corsa-gym.github.io/)

## Modes and entry conditions

| Mode | Data | Purpose | Entry condition |
| --- | --- | --- | --- |
| Demonstration initialization | Target-car human/declared-teacher recordings | Establish a usable policy and save an imitation baseline | Reliable recording and a qualified graph |
| Autonomous RL practice | Policy-generated trajectories and rewards | Improve recovery, progress, and eventually pace | Meaningful autonomous section driving; correct reward/termination accounting; repeatable resets |
| Human correction | Flagged takeover or separate recovery demonstrations | Address repeated failures or introduce a better maneuver | A specific failure or discovery worth demonstrating |
| Candidate evaluation | Fixed-policy attempts without exploration | Decide whether to promote or roll back | Complete compatible checkpoint and a fixed development protocol |
| Final evaluation | Frozen candidates and benchmark settings | Report results | End of training; no learning from scored attempts |

RL need not wait for three valid full laps: learning to complete the course can be its first task. It must wait for trustworthy controls, observations, reset behavior, and rewards. Manual resets can support supervised RL sessions; unattended practice requires tested automatic resets. This is a change from the earlier late-RL schedule.

## Keep data sources explicit

Every transition carries `source` (`human_demo`, `human_correction`, `policy_rl`, or `evaluation`), episode ID, policy ID when applicable, schema/feature hashes, and intervention status.

- Human/teacher actions may be used as imitation labels after alignment and quality checks.
- Policy successes and failures belong in RL experience. Failed actions are not expert labels.
- Final evaluation episodes belong in neither training pool.
- Human takeover ends the autonomous RL segment before the first human command. Subsequent human transitions are correction data; do not attribute their rewards or actions to the old policy.
- Preserve the complete observation/action history needed to reconstruct graph state for correction segments, including the preceding autonomous portion. Only the labeled human portion receives imitation loss.

Keep an expert archive and a policy-rollout store with separate usage rules. The same on-disk format can hold both, but their labels and training masks must remain distinct. A successful autonomous discovery remains tagged as policy-generated; do not relabel it as human expertise.

## Initial algorithm proposal

Use the weighted ridge readout from document 12 as the demonstration-only baseline and rollback checkpoint. For the first RL experiment, propose **PPO with an imitation auxiliary loss** on separately sampled expert data. This is a local engineering choice, not a claim that it is the fastest or most sample-efficient racing algorithm.

PPO uses fresh experience from its behavior policy; demonstrations and arbitrary old rollouts are not ordinary on-policy samples. [PPO algorithm documentation](https://spinningup.openai.com/en/latest/algorithms/ppo.html)

The proposed RL actor retains the 128 neural features and a learned two-output mean head, adding a controlled stochastic action distribution for practice. Preserve the qualified deterministic imitation behavior when initializing that mean. Add a small separate value head and its optimizer. The previous count of 258 coefficients describes the imitation readout only, not the whole RL learner. Save the original actor/readout, value head, fixed exploration configuration, optimizer states, reward configuration, and counters in an RL training checkpoint. Exploration scale is fixed within the first pilot, rather than another learned component.

Do not add raw telemetry as a shortcut into the actor. Keep the feature generator, input/output mappings, normalization, and cadence frozen so the existing graph semantics remain intact. A standard recurrent library's LSTM is not automatically our fly graph.

At rollout time the graph consumes ordered history. Store the sampled neural features and their next-step counterparts as well as raw episode records; since the graph is fixed, these features can be reused for head-only updates without training through every neuron. Do not reset graph state at rollout-chunk boundaries, or shuffle raw time steps through the recurrent graph. Any future trainable graph/input dynamics require a new sequence-aware training design.

The actor's sampled action, log probability, and action-transform specification must agree. Record both the policy's sampled action and the final clipped/slew-limited game control. The adapter transformation is part of the environment response; never compute the PPO probability ratio by pretending a clipped/executed command was the original random sample. Specify and test the probability handling before RL rollout collection.

The actor objective combines an RL term and a weighted expert imitation term; the value head has its own return-prediction loss. The imitation weight is not a data percentage. Choose it during pilot experiments: too large can anchor the actor to slower demonstrations; too small can lose useful behavior. Select on driving outcomes and retain an imitation-only checkpoint throughout.

## Concrete pilot recipe, subject to qualification

These are project starting values, not literature-backed racing optima. C may revise them during the bounded pilot, recording one complete new configuration before collecting from it. A configuration is not accepted merely because this document supplies a number.

| Item | First pilot proposal |
| --- | --- |
| Actor | Existing imitation head on the 128 standardized neural features; retain a qualified MLP if it replaced ridge |
| Critic | Separate `128 → 64 tanh → 1` network, no shared trainable layers with actor |
| Exploration | Independent Gaussian samples each tick around the actor's unbounded mean; fixed standard deviations 0.03 steering and 0.08 longitudinal demand |
| Execution | Clip the sampled action to [-1,1], then apply the common adapter/slew limit; deterministic evaluation clips the mean instead |
| Collection | One game instance; target at least 2,048 eligible transitions, then finish the current attempt at its success/failure/declared task timeout. The minimum alone is about 82 seconds of driving at 25 Hz; actual batch size and reset overhead vary. |
| Updates | At most 3 passes over the fresh batch; minibatch 128; normalize advantages over that eligible batch |
| Optimizers | Separate Adam optimizers, actor rate 0.0001 and critic rate 0.0003; gradient norm cap 0.5 |
| Return estimation | Gamma 0.999 and GAE lambda 0.99 at 25 Hz; no reward normalization in the initial pilot |
| PPO constraint | Ratio clip 0.1; stop actor passes when measured mean old-to-new action-distribution KL exceeds 0.01; no entropy bonus or learned variance initially |
| Expert anchor | Actor loss = negative clipped PPO objective + 0.1 × weighted expert action MSE; separately sampled expert minibatch 128; same pool/braking rules as imitation |
| Value fitting | Mean squared error to the declared return targets; critic-only fitting may finish after actor early stopping |
| Reproducibility | Initial seed 2026, exact library versions, all boundary masks and bundle/configuration hashes recorded |

Compute joint log probability by summing the two Gaussian coordinate log probabilities for the original unbounded sample. Never evaluate the executed/clipped action as if it were that sample. Keep rollout samples and old probabilities immutable. Before collection, demonstrate that deterministic actor outputs match the imitation policy after the shared adapter. Inspect clipping frequency and the actual exploration reaching the car; small numerical noise that is entirely removed by clipping/slew is not useful exploration.

Compute KL on the same stored feature states using the old and candidate Gaussian distributions, including mean changes caused by the expert loss. Check after each actor minibatch; early stopping limits further updates but does not undo a bad step or replace driving evaluation. The entropy term is unnecessary for maintaining variance in this fixed-variance first pilot.

Gamma 0.999 retains roughly 0.78 weight after ten seconds and 0.22 after one minute at 25 Hz. GAE controls the estimator's trace separately; these settings do not give a perfect one-minute prediction. Fit the critic on correctly bounded returns before the first actor update, for at most the same three passes. If return targets, value predictions, or gradients are nonfinite, reject the candidate rather than exporting it.

Use a maintained PPO implementation/reference as the starting point, pin its inspected version, and keep the custom work limited to the expert loss, initialization, and file-based rollout handoff. Stable-Baselines3 documents configurable actor/value networks, but its defaults must not silently replace our initialized head. A remote learner consuming saved rollouts is an integration task, not something the stock `learn()` call provides automatically. If that adapter does not fit the four-person-hour pilot, keep the correction fallback. [Policy customization](https://stable-baselines3.readthedocs.io/en/master/guide/custom_policy.html)

Benchmark head training on CPU as well as GPU on Spark; small non-image PPO networks need not benefit from GPU execution. This does not establish where the much larger sparse graph runs fastest. If transfer/setup dominates, a separately qualified local CPU head-update path between episodes is allowed using already cached features and the same algorithm/configuration; graph execution, file provenance, and parity checks still apply. Spark remains the default graph-preparation/replay resource. [PPO implementation guidance](https://stable-baselines3.readthedocs.io/en/master/modules/ppo.html)

## One update round

1. Choose a development candidate and freeze its policy ID, graph/feature pipeline, exploration settings, and reward version.
2. Collect a predeclared batch of autonomous practice trajectories on Windows. Manual or automatic resets begin new episodes and clear graph state appropriately.
3. Close and transfer the batch to Spark. Reward components, actions, old log probabilities, value predictions, terminal/truncation flags, and feature provenance travel with it.
4. Train the candidate on that fresh batch, plus separate expert minibatches for the imitation term. Do not put human transitions into the PPO likelihood-ratio term.
5. Export and validate the candidate, then evaluate with exploration disabled under the existing development protocol.
6. Promote only if results justify it; otherwise restore the incumbent. Preserve the pure imitation checkpoint as an additional fallback.
7. Collect the next RL batch from the selected updated policy. If a batch was generated by an older policy while training ran, archive it but do not silently use it as the next PPO batch.

When the minimum batch quota is reached, finish the current attempt within its predeclared task timeout, include those same-policy rows in this update, and then enter IDLE. Budget up to one extra attempt timeout beyond the quota and record the actual batch size. Do not reset at exactly 2,048 steps: that might repeatedly cut off the eligible full lap and prevent any finish reward. Use variable-length minibatching, including the final smaller minibatch; no duplicated padding or dropped remainder silently changes the data weighting.

If the collection time budget expires before the attempt ends, capture the last clean boundary observation/features, mark an artificial collection cutoff separately from a driving failure, and release controls. Repeated cutoff before a full-lap opportunity fails pilot qualification; increase the predeclared collection budget within the overall experiment cap or park RL. Start the next practice episode with a normal simulator reset and graph reset after IDLE; never hot-swap while the car continues a lap. A data chunk boundary by itself never resets graph state during collection. Every included row belongs to the unchanged behavior policy; unused old-policy surplus never becomes a fresh post-update batch.

For the simplest first implementation, use collect → train → evaluate → collect. Windows can record demonstrations or run a labeled demo during remote training, but those records are not automatically eligible for the next PPO update. This narrows the earlier promise that asynchronous driving data can always be consumed by training.

If the same failure persists over several development rounds, optionally record human correction data, include it in the expert archive, and rerun a candidate update. A substantial standalone imitation correction creates a new policy version: discard stale on-policy batch eligibility and collect fresh RL experience afterward. Do not repeatedly overwrite an improved RL actor with a fresh ridge fit without evaluating the resulting regression.

## Rewards and failure handling

Start with interpretable components: new legal forward progress, elapsed-time cost, invalid/crash/stuck outcomes, and a valid-finish bonus. Record each component separately. For the first completion-focused pilot, propose `delta_new_legal_metres / circuit_length`, plus +1 at a verified eligible full-lap finish. A driving failure ends the episode without a finish bonus or value bootstrap; its extra penalty is initially zero. Elapsed time and failures remain logged components, with zero extra reward coefficients initially. This keeps arbitrary negative penalties from dominating the first progress signal. Discounting already favors earlier otherwise identical progress/finishes; whole-lap evaluation remains the pace objective. These values require the trace checks below and must be frozen before collection. Beating the professional reference is not required to receive finish reward.

Maintain a per-episode high-water mark of validated unwrapped route progress, initialized at the actual start. Reward only progress above the previous high-water mark; reverse/forward motion cannot earn the same metres twice. Gate progress by coherent route projection, plausible measured movement, and lap legality. Clamp no bad teleport into a seemingly valid small step: terminate/mask the fault instead. Limit the scored progress interval to the declared lead-in plus one eligible full lap; completing an initial partial lap earns no finish bonus. Eligibility comes from document 11's start-line/lead-in rules and the simulator validity signals, not progress distance alone. End the training episode at the designated finish or failure, with no graph reset at the intermediate line crossing.

Keep preparation leading to the designated lap in recorded neural history. Any autonomous lead-in receiving RL loss follows the same failure rules; human positioning before arming receives no RL loss. All early driving failures and task timeouts terminate with no bootstrap or finish bonus, including when their explicit penalty is zero. Infrastructure faults and human intervention use the exclusion/bootstrapping rules below, never an invented crash penalty. A failed, reset, or ineligible partial lap must never receive a success bonus.

Before the first update, replay labeled traces for valid completion at two speeds, slow partial progress then failure, immediate failure, stationary timeout, reverse/repeat progress, initial partial-line crossing, illegal cut, teleport, human takeover, and artificial collection cutoff. Confirm component accounting and inspect both undiscounted returns and the configured discounted training targets. In particular, useful legal progress must earn more than zero-progress failures or stationary waiting. Complete valid laps must beat comparable partial failures. Faster otherwise identical full laps tie on undiscounted progress/finish reward and should score higher on discounted return; actual timing breaks the evaluation tie. Compare these quantities separately rather than claiming that the raw reward directly minimizes lap time. If these orderings fail, revise and version the reward before collecting a fresh experiment. Do not infer that these examples prove the absence of every exploit.

Reject progress from teleports/resets, repeated reverse-forward travel, or illegal cuts. Stop accumulating progress after invalidation. Verify that doing nothing, repeatedly crashing early, or exploiting reset behavior cannot systematically score better than useful legal driving. The professional lap-time target is an evaluation threshold, not the only training reward; waiting until the model beats it would provide too little feedback.

Separate driving failures from infrastructure faults. A stale packet, lost focus, or failed controller process is not automatically evidence that the policy selected a bad action. Discard or mask affected transitions as defined by the trainer. For a known crash/invalid-lap terminal, end the return with the declared failure reward (initial extra penalty zero) and no bootstrap. For an artificial collection cutoff, bootstrap only from a valid next state without pretending that the car crashed. For human takeover, exclude the intervention transition and following human segment from PPO; keep the earlier prefix only if its boundary/bootstrapping semantics are valid. Never let reward from the human recovery leak back into the autonomous rollout.

Each transition explicitly links decision-state `z_t`, original policy sample `a_t`, reward observed over its interval, and the next decision-state `z_next`. Construct that next state from the ordered graph after the next observation arrives; never attach a post-reset state to a pre-reset action. The value bootstrap mask and GAE continuation mask are separate:

| Boundary after a valid autonomous transition | Bootstrap from next value? | Carry GAE into following segment? |
| --- | --- | --- |
| Ordinary continuation | Yes | Yes |
| Verified full-lap finish or driving failure/task timeout | No | No |
| Collection batch cutoff while driving continues | Yes, from the valid pre-reset next state | No; the collected batch ends here |
| Human takeover/fault | Only from the last known clean boundary before human influence/fault | No |

For takeover/fault, exclude any action interval crossing the uncertain boundary. Retain a prefix only through a transition with a trustworthy next-state feature vector; bootstrap at that state. If no such boundary can be identified in the current fragment, archive that fragment without PPO use. For retained transitions use `delta = reward + gamma * bootstrap_mask * V(z_next) - V(z_t)` and `advantage = delta + gamma * lambda * continuation_mask * next_advantage`. Do not apply success/failure penalties to masked infrastructure or human intervals. Record which rows were excluded and why; the minimum 2,048-step batch target counts eligible rows, not raw logged ticks.

## Protecting earlier progress and testing downstream effects

A different exit from one corner changes the speed, position, heading, and neural history at the next. Separately, changing shared actor parameters can alter actions elsewhere on the circuit. Neither a low imitation error nor a better isolated corner proves an improved controller. Sequential prediction research documents how a learner's actions change the states it subsequently encounters. [DAgger paper](https://proceedings.mlr.press/v15/ross11a.html)

Use the following development rules:

1. **Preserve the incumbent.** Train a separate candidate and save its complete configuration and lineage. Keep weights fixed throughout each evaluated attempt; changing activity still permits feedback corrections. Never replace the incumbent based only on training reward or an isolated sector record.
2. **Evaluate connected behavior.** Targeted practice must include the approach, modified corner, and following connected corners. Record exit speed, lateral position, heading error, subsequent failures, sector times, and whole-attempt outcome. Once completion is possible, run the entire eligible flying lap under document 11's matching conditions. A slower corner can be acceptable if it improves completion or whole-lap time.
3. **Keep a small regression suite.** Include previously troublesome connected sections and ordinary sections that already work. Predeclare nominal and modestly varied approach conditions within valid car/track bounds, using reproducible resets only if supported. Otherwise use recorded lead-ins and compare measured entry states; do not claim exact simulator-state replay. Reconstruct neural history correctly and do not invent zero-state mid-corner starts. Small-sample outcomes are screening evidence, not proof of reliability.
4. **Retain broad expert coverage.** Sample the imitation loss from earlier successful demonstrations as well as new corrections, stratified by route section so one troublesome corner does not consume every expert minibatch. Preserve the train/development/final split. This can reduce forgetting but must not enforce the demonstrator's exact line as the only successful strategy. Old autonomous rollouts remain ineligible for ordinary fresh-policy PPO updates.
5. **Control update size.** Use declared learning-rate, PPO clipping, and approximate-KL stopping settings. Measure policy change after combined RL and imitation updates, since the auxiliary loss can also move the actor. These are safeguards, not a guarantee against regressions. [PPO documentation](https://spinningup.openai.com/en/latest/algorithms/ppo.html)
6. **Account for delayed outcomes.** Pin discount and advantage settings in seconds at the actual action cadence, not by copying defaults. For example, at 25 Hz a discount of 0.99 weights reward ten seconds later by about 0.081 before other return-estimation effects. This illustration is not a recommended setting. Use appropriate bootstrapping at collection boundaries; ending a rollout chunk does not erase downstream consequences. A value estimator can learn those consequences imperfectly, so connected driving tests remain necessary.
7. **Promote on the agreed objective.** Apply document 11's preliminary development ranking, recording the regression-suite outcomes alongside it. Investigate new crashes/invalidations before promotion; additional laps are warranted when evidence is ambiguous. A single slower split is not an automatic rejection. Keep the incumbent when the result is inconclusive, and keep final scored attempts separate from candidate selection.

Before full laps are possible, use increased legal progress and connected-section completion as explicitly preliminary evidence. Revisit those early promotions when full-lap evaluation becomes available. No finite test set establishes that a new policy can never fail later in a lap.

## Ownership and schedule changes

- **A:** add repeatable resets, takeover arbitration, rollout boundaries, and clean terminal/fault signals.
- **B:** qualify fixed-graph feature replay and local/remote parity; keep graph changes out of the first RL experiment.
- **C:** implement the demonstration baseline, stochastic actor/value heads, PPO plus separate imitation loss, and checkpoint promotion.
- **D:** log reward components and policy lineage; compare imitation-only and mixed-learning results under matching conditions.

Use document 10's single schedule: hours 0–10 qualify integration/data/graph, 10–14 target first fly section control, 14–18 allow corrections and the bounded eligible RL pilot, and 18–24 target completion. Hours 24–28 permit at most two targeted improvements; freeze at hour 28 and protect hours 28–36 for scoring and presentation. Earlier RL entry is allowed if its prerequisites already hold, not as a reason to skip them.

Cap initial RL integration qualification at four person-hours of C's work within the allocated window. If no complete collect/train/export/compare/rollback cycle works within that budget, park RL and continue corrections. If two comparable RL candidates fail to improve development results, park further RL tuning for this weekend; this is a budget decision, not evidence that RL cannot work. Continue only when round cost and measured benefit justify it. Do not introduce a new unqualified trainer in the final tuning window.

If RL tooling or resets are not qualified, continue demonstration/correction learning and explicitly report that autonomous RL improvement was not demonstrated. This preserves a deliverable without mislabeling its training method.

Final conditions should include the saved imitation-only fly policy and final mixed-learning fly policy, plus the planned non-fly comparisons as time permits. If the mixed policy also received extra demonstrations, its advantage is evidence about the combined process, not an isolated causal effect of RL.

## Required RL checks before calling it ready

- Old/new action probabilities match on an unchanged policy, including action transformations.
- The demonstration-initialized actor reproduces qualified deterministic controls within tolerance before exploration is enabled.
- Terminations, artificial cutoffs, infrastructure faults, and human takeover produce the intended return/advantage masks.
- Only current behavior-policy rollouts enter the PPO term; expert data enters its separate loss.
- Graph state survives rollout chunks and resets at actual episode boundaries.
- A short collect/train/export/evaluate cycle completes with recorded wall-clock cost and successful rollback.
- Reward traces rank predefined sensible and exploitative behaviors as intended.

Before implementation of the trainer, encode the pilot recipe, boundary rules, and reward in an explicit training configuration. The manifest distinguishes proposed values from the accepted configuration hash and measured qualification results. No default above is an accepted performance result. Changes must remain within document 10's bounded pilot and must not consume the final evaluation window.
