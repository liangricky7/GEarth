# Implementation roadmap: working driver first

Status: revised October 5, 2026; planning only. This document owns execution priority, experiment limits, and schedule, superseding earlier schedules and mandatory experiment lists. Document 11 owns interfaces and scoring, 12 owns the graph recipe, and 13 owns training semantics. Optional work below becomes mandatory only if we make the corresponding scientific claim. Exact event duration remains unconfirmed; the worked schedule assumes 36 elapsed hours and includes buffers, breaks, evaluation, and submission.

## Objective and priority

Aim for a valid autonomous lap below a predeclared published Hamilton or Verstappen time, labeled as a cross-simulator comparison. Maximize the chance of a credible working result and a measured improvement within the weekend; neither the headline result nor optimal reward is guaranteed.

Select candidates by validity/completion first and median valid lap time second, using document 11's development protocol. Also report best valid time against the target. Do not select the fastest invalid lap or select solely by training reward. Never change physics, assists, track, or the primary reference retrospectively to make a trained candidate look successful. Additional published references may be shown as separately labeled context.

Use three explicit milestones, keeping the original objective visible:

1. **Working fly control:** the fly-derived graph is in the action path and the controller drives a meaningful section autonomously.
2. **Reliable complete laps:** at least three valid finishes in five development attempts; this is a modest engineering gate, not proof of reliability.
3. **Headline target:** a valid scored lap beats the predeclared professional reference; disclose all cross-simulator differences and all scored outcomes.

Save and record each milestone. A section demonstration or a fallback circuit is a partial result, never a claim that the original time target was achieved.

## Scope and ownership

Required: one simulator/car/circuit, telemetry, local control and independent stop, one fly-graph recipe, demonstration initialization, targeted corrections, one simple comparison controller, versioned checkpoints, complete logs, and final scoring/video. Plan a bounded RL pilot after meaningful section control; keep the mixed-learning objective while making its deployment contingent on evidence.

Excluded from the weekend's critical path: camera input, remote per-action inference, multiple tracks, all-synapse training, anatomy-informed remapping, web backend, distributed training, several replay baselines, and elaborate brain animation. The game, model, and logger must run together before display polish begins.

| Person | Primary ownership | First handoff |
| --- | --- | --- |
| A | Windows adapter, calibrated controls, stop/reset, runtime | Correct short recorded drive and controllable car |
| B, Spark access | Source audit, graph, environments, latency/parity | Qualified fixed feature generator |
| C, Spark access | Route, demonstration driver, readout, learning | First fly policy and one conventional feedback baseline |
| D | Recorder, manifest, scoring, evidence, presentation | Episode log and attempt results; later a minimal status display |

A and C book exclusive laptop blocks. B and C share one training queue and immutable artifacts; four individual Spark accounts are unnecessary. D works against recorded fixtures when the laptop is occupied. Schedule operator breaks; manual-reset collection is attended work. Files plus hashes are sufficient for transfer. The demo must keep running locally if internet access fails.

## Before the build clock

Check event pre-work rules. Current work remains planning; install, code, or collect data early only if allowed.

- Confirm event length/submission rules, game access, laptop specifications, and the two Spark operators.
- Resolve simulator access/permitted use from document 08. Assetto Corsa remains conditional, not an already selected or approved game. Offline launch alone does not settle automation questions.
- Choose one primary simulator and one fallback before adapter work. Confirm how the fallback changes the target claim; TORCS on an unmatched car/track preserves only the controller experiment.
- Name the demonstration driver. Freeze car, full layout, primary reference, assists, vehicle conditions, lead-in, lap validity, and attempt timeout in the manifest before scored comparisons.
- Assign artifact publisher and module owners. Use the existing document 11 schema and recorded examples; do not build competing interfaces.

If no qualified simulator choice exists, keep work simulator-independent and report that dependency. Do not assume an integration can be made permissible or functional in four hours.

## Worked 36-hour schedule

| Hours | Main deliverable | Decision at the boundary |
| --- | --- | --- |
| 0–4 | Observe, command, stop, manually reset; source import progressing | Switch once to the preselected fallback if appropriate; no continuing unbounded primary-adapter debugging |
| 4–10 | Small clean dataset, route, qualified graph, simple baseline | Only a measured local graph proceeds to training/deployment; record any scope/cadence change |
| 10–14 | First fly-controlled section and imitation checkpoint | Diagnose the first failure; no feature expansion or large training sweep |
| 14–18 | Targeted corrections and one qualified RL update cycle if eligible | Continue RL only if the bounded pilot completes and there is budget for evaluation |
| 18–24 | Improve completion; repeat the better-qualified learning route | By hour 24, decide whether the last tuning window targets pace or completion |
| 24–28 | At most two targeted candidate changes, with reserved comparison time | Freeze model, conditions, and artifacts at hour 28 |
| 28–33 | Five scored attempts per required distinct condition and backup video | Keep every outcome; no training from scored attempts |
| 33–36 | Cold-start rehearsal, pitch, submission, contingency | Preserve the frozen working setup |

These are elapsed windows, not a requirement for continuous work by all four people. Adapt the schedule to confirmed hours before implementation: protect the final roughly 20–25% for evaluation/submission, remove optional experiments first, and do not simply compress every activity. A shorter event may support no useful RL rounds; an extended event buys more data and comparisons before more architecture.

## Gate 1: a trustworthy loop

A verifies axes/units, requested versus observed controls, manual reset, input ownership, and an independent neutral-control stop. Use document 11's stale-data, pause/focus, and re-arming rules. C/D inspect a short braking event and a manual turn in the log. B audits actual MaleCNS schemas and prepares sparse data with provenance; never assume source column names.

Manual resets are acceptable for the first driver and supervised practice. Automatic reset development earns time only after a driver works or when a documented existing adapter makes it inexpensive. Qualify it before unattended RL. Do not require arbitrary simulator-state snapshots.

## Gate 2: enough data and a usable graph

Record two pilot laps and inspect timestamps, controls, validity, and route projection before bulk collection. Then aim initially for five independently restarted recording sessions of one or two laps each: three training, one development, and one untouched offline test session. This is a small startup dataset, not a sufficiency claim; session-level isolation overrides the earlier approximate 70/15/15 ratio. Do not split consecutive snippets of one session across pools. Add targeted training sessions as failures identify missing coverage, rather than treating 10–20 laps as a prerequisite to first training. Do not discard unsuccessful demonstration sessions without recording their inclusion/label decisions.

Derive the reference route and normalization from training sessions only. Inspect lap wrap and adjacent track sections. Establish the teacher's valid time and gap to the chosen reference; a slower teacher means imitation alone has no demonstrated route to the headline goal.

B starts with document 12's default dynamics and two internal updates, testing representative input responsiveness and full-loop latency while the game runs. Try one or four updates only when latency or measured response justifies it; changed dynamics require fresh replay. At most one additional dynamics candidate is allowed before first driving. The four listed settings are a diagnostic menu, not a mandatory sweep.

If full-graph runtime fails, allow one focused repair block of at most two hours within this phase, not added after it. First lower rendering load or resolve a demonstrated sparse-execution problem. A changed cadence or reduced graph must pass checks and have a recorded recipe, retained counts, and limitations before use; reduction is not an automatic proven rescue. Do not spend the remainder of the event implementing remote driving. If no fly graph qualifies, preserve the conventional control prototype and report the fly objective as blocked.

C implements one conventional feedback baseline using the same permitted observations/action rules. No timed or distance-indexed replay controller is required this weekend. D builds the log-based results table before visual effects.

## Gate 3: first fly control

Cache ordered neural features; fit the fixed-graph ridge readout using document 12's small regularization set. Use one graph candidate first. Export the complete bundle, verify recorded-sequence parity, and drive a straight, braking zone, then connected corners. Keep the primary fly actor free of raw-input or reference-speed bypasses.

When control fails, check signs, label timing, route geometry, scaling, recurrent initialization, and feature responsiveness first. If these work but the linear readout does not, allow one small MLP experiment with a pinned training configuration and a comparison budget. Do not combine new features, mapping, dynamics, and readout in the same candidate.

Save the imitation-only checkpoint and record a short section demo immediately. Graph structure, perturbation response, local execution, and provenance checks remain required. Synthetic memory benchmarks, transmitter sensitivity experiments, and randomized-graph comparisons are deferred unless diagnosing a specific failure or making claims that require them.

## Gate 4: measured improvement

Use document 13's mixed strategy. Pick the first repeatable failure, record a few valid human recovery examples when available, and evaluate a corrected candidate while retaining prior expert coverage. Do not refit a mature RL actor from scratch without a separate comparison.

After meaningful autonomous section control and correct reward/reset/rollout handling, allocate at most four person-hours of C's work to qualifying the RL integration, within hours 14–18 or an earlier eligible window. Reuse a qualified implementation where practical; do not build a new RL framework. The gate is a complete collect/train/export/compare/rollback cycle, not just declining training loss. B supports packaging as needed without interrupting essential runtime work.

If the pilot is blocked or two comparable RL candidates show no development improvement, park RL for the weekend and use targeted demonstration/correction rounds. Two rounds are a resource decision, not a scientific verdict on RL. If RL improves outcomes and turnaround leaves time for comparison, continue it; keep human correction available. Never claim RL improvement when it was not demonstrated.

Every experiment card records one hypothesis, parent checkpoint, changed component, fixed conditions, wall-clock/attempt budget, outcome, and keep/rollback decision. C publishes only evaluated candidates. Estimate complete round cost from actual driving, resets, transfer, fit, validation, and comparison, not Spark compute alone. No new candidate starts unless its comparison can finish before freeze.

## Gate 5: pace or completion, then stop changing the system

At hour 24, if the controller has three valid finishes in five development attempts, target the largest measured time loss while preserving completion. Compare combined corner sequences and whole laps; an improved individual split is insufficient. Allow at most two targeted changes in this final tuning window, fewer if measured turnaround requires it.

If full laps are unreliable, use this window for completion and recovery. No new architecture, feature schema, simulator, car setup, or unqualified trainer enters this window. The target remains unfulfilled until a valid scored time beats it.

Keep the professional target gap visible. Faster expert examples and qualified RL are the proposed routes beyond initial teacher pace; neither is promised to close the gap within the event.

## Final evidence and presentation

Required distinct conditions: final fly controller, saved imitation-only fly controller if training changed it, and the one conventional feedback baseline. If final equals imitation, evaluate that checkpoint once and label it accordingly. Run five attempts per condition, one designated flying lap each, using document 11's fixed start/timeout and denominator rules. Calculate the required wall time in advance; if it cannot fit, stop tuning earlier rather than silently shrinking the scored protocol.

Report valid/total attempts, best and median valid lap time, target delta, failure reasons, interventions, and deadline misses. An inconclusive result stays inconclusive. Keep the untouched offline demonstration test separate from these fresh driving attempts. A topology-superiority claim needs additional matched/retrained graph controls; omit that claim if time does not permit them.

Deliver one frozen runnable bundle, manifest and source attribution, logs/results table, short video, and a cold-start rehearsal. Show the comparison label, controller identity, validity, lap time, and target gap. Disclose whether the graph was reduced, whether RL helped, and what was engineered. A polished explanation of an honest partial result is the fallback, not evidence of achieving the professional time.

## Implementation structure

Keep the proposed modules small: simulator adapter, feature builder, graph core, readout, recorder/evaluator, and trainer/exporter. Use the document 11 contracts. Create directories as needed; no service architecture or dashboard framework is required. Store large datasets and checkpoints outside Git with hashes. Keep one short setup/run procedure and one accepted configuration so any teammate can demonstrate the frozen local bundle.
