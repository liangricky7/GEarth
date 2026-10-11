# Traffic Flow Lab — architecture and MVP

Status: proposed implementation, not benchmarked. The remaining hackathon time, Spark access method, installed packages, and inter-node connectivity have not been verified for this project.

## Initial stack and scope

Use SUMO for microscopic traffic simulation and network import, Python for the environment and rollout services, PyTorch for a small policy, and a browser renderer for vehicle and signal traces. Evaluate SUMO-RL as a starting wrapper rather than rebuilding basic traffic-state access. It exposes customizable observations and rewards and examples of traffic-signal learning. Its defaults require inspection for the proposed action and clearance rules. [SUMO-RL](https://github.com/LucasAlegre/sumo-rl).

Qualify everything on the Sparks' ARM64 environment. NVIDIA provides a Spark-specific PyTorch workflow, but it does not verify SUMO, the RL framework, or this distributed rollout design. Pin the versions that actually pass the initial smoke run. [NVIDIA PyTorch workflow](https://build.nvidia.com/spark/pytorch-fine-tune/overview).

MVP: one reviewed corridor, two or three controlled intersections, fixed routes per scenario, and a policy that controls green extensions. Do not make graph transfer, an adversarial scenario generator, camera perception, or citywide optimization dependencies of the first demonstration.

## Process layout

```text
Scenario package + frozen policy version
                   |
                   v
Spark A: independent simulation worker processes
  SUMO + observation/action adapter + local policy inference
                   |
       versioned batches of experience
                   v
Spark B: learner -> checkpoint artifact -> Spark A

Separate evaluation workers on Spark A
  fixed baseline / adaptive baseline / frozen learned policy
                   |
          traces + measurements
                   v
Web backend -> browser comparison and report
```

Run each headless environment in a separate process. Keep simulation stepping and policy inference together on Spark A. Use one batched inference process only if measurements justify it. Avoid a network request to Spark B at every simulation step.

Start with TraCI for easy inspection. Qualify libsumo for headless workers if socket overhead becomes material. The browser consumes recorded or streamed positions; it does not require a native SUMO GUI on either Spark. [TraCI](https://eclipse.dev/sumo/docs/TraCI/index.html), [libsumo](https://github.com/eclipse-sumo/sumo/blob/main/docs/web/docs/Libsumo.md).

## Versioned learning loop

PPO is an initial candidate, subject to a working rollout-buffer integration; a remote data file cannot simply be passed to a stock learning call.

1. Spark B creates policy version k and its preprocessing configuration.
2. Spark A loads k into all training workers at a collection boundary.
3. Workers collect a fixed rollout batch using k, recording observations, sampled actions, action log-probabilities, value predictions, rewards, and episode boundaries.
4. Spark B updates only from that version's batch, using correct bootstrap handling for time limits versus terminal states. It emits k+1.
5. Discard or explicitly account for stale batches. Do not silently mix policy versions in an on-policy update.
6. Publish a checkpoint to evaluation only at a completed update boundary. The judge-facing comparison always uses a frozen version.

Transfers contain compact arrays and manifests. Checkpoints include hashes and observation/action schema versions. Workers acknowledge loaded versions. If the learner becomes unavailable, evaluation can continue with the last validated checkpoint or baseline. Reserve worker capacity for the demo so training does not starve it.

## Observation and decision design

Proposed state per approach: queued vehicles, occupancy relative to lane storage, downstream occupancy, recent arrivals, and elapsed time since service. Add current phase and elapsed green for each controlled intersection. Keep observation definitions identical for policies with equivalent access to traffic information.

Initially use one corridor-level policy with one extend-or-end decision per intersection at a configurable decision interval; five simulated seconds is a starting value to benchmark. Ending a phase advances through a configured phase sequence. This keeps the first experiment focused on timing rather than learning arbitrary phase orders. Configure episode duration and arrival schedules independently of this decision interval.

The environment enforces permitted movements, minimum and maximum green, yellow and all-red clearance, and configured mandatory service phases. Invalid extensions are overridden and logged. These constraints apply to all controllers. Do not assume an RL wrapper automatically models all clearance or pedestrian requirements. SUMO can load explicit signal programs and accept external control. [SUMO signal documentation](https://eclipse.dev/sumo/docs/Simulation/Traffic_Lights.html).

The first vehicle-focused study must state whether pedestrian demand is absent or modeled. Retain configured protected service phases; do not present vehicle-only scores as a complete LSU postgame evaluation. Add explicit pedestrian agents and service metrics in the multimodal phase.

## Learning objective

Start with negative total queued vehicle-seconds across the full experiment, including vehicles awaiting entry. Add a declared penalty for long unserved approaches and unnecessary phase switching. Tune weights on validation scenarios only, then freeze them for evaluation. Report objective weights with the experiment.

Every scenario contains its intended departures before any controller runs. A controller cannot improve its score by suppressing vehicle insertion, changing destinations, or removing difficult vehicles. If a scenario includes closure-induced detours, use the same routing mechanism and information for every controller.

Training reward is a surrogate. Evaluation separately measures demand completion, delay, approach service, and failure events. Reward improvement alone is not proof of useful traffic improvement.

## First implementation gates

| Gate | Deliverable | Decision |
| --- | --- | --- |
| Runtime | Small SUMO scenario and PyTorch policy run on the Sparks; transfers verified | Resolve platform issues before importing LSU geometry. |
| Baselines | Reviewed corridor, fixed controller, adaptive controller, and complete demand ledger | Inspect routes, lane connections, phase mappings, and ordinary congestion first. |
| Throughput | Wall-clock rollout rate at 1, 2, 4, and more independent workers; training/update/transfer times | Choose concurrency from measurements; stop increasing workers when throughput plateaus. |
| Learning | Versioned rollout/update/checkpoint loop and a small sanity experiment | Verify learning integration before large training runs. |
| Evaluation | Paired results on held-out scenarios and seeds | Accept actual results; fix scoring defects before tuning more. |
| Demonstration | Browser comparison, scenario controls, evidence labels, frozen checkpoint, saved replay | Present measured outcomes and disclose any replay. |

These are an ordered build plan, not a promise to fit an unverified 36-hour window. If training cannot improve over adaptive control, the completed baseline comparison and experiment viewer remain useful, but the team must disclose the learning result.

## Compute decision

Record CPU utilization, simulation steps/second, network transfer time, learner time, GPU utilization, and end-to-end collection/update throughput. GPU memory capacity is not a guarantee of faster learning. Small policy networks and CPU simulations may leave substantial Spark capacity unused.

The second Spark gives independent learner resources and separation from simulation/demo work. A later controlled one-machine versus two-machine benchmark can establish a speed benefit. Do not build a new GPU traffic simulator solely to make a utilization graph look impressive; consider it only if measured simulation throughput blocks the research target and fidelity can be preserved.
