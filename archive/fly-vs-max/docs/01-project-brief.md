# Project brief

Historical/supporting document. Use [10 — execution roadmap](10-implementation-roadmap.md) and [11–13 — current contracts](11-implementation-contract.md) for build decisions. Earlier schedules, inputs, data quotas, and learning recipes below do not add requirements to that plan.

Revision note: [document 08](08-research-roadmap.md) and [document 09](09-fixed-circuit-controller.md) are the current plan. This brief records the original EA F1 proposal. The real F1-driver comparison remains the objective; simulator, interface, and named lap reference are conditional choices.

## Pitch

Can a neural controller built from a fruit fly's wiring diagram learn to drive an F1 simulator? We translate racing telemetry into network inputs, learn a mapping from neural activity to a virtual gamepad, and measure progress toward a real-world Verstappen lap time.

The scientific resource is a connectome: a structural wiring map. It does not supply a pretrained racing policy or uniquely determine neural dynamics. Google describes more than 166,000 neurons and 125 million synaptic connections in the male CNS map. Our software dynamics, observation encoding, and control mapping will be engineering choices. [Google Research](https://research.google/blog/a-connectomics-milestone-mapping-the-complete-male-fruit-fly-brain/)

## Confirmed constraints and assumptions

| Item | Status |
| --- | --- |
| Team | Four people, confirmed |
| Game distribution | Steam, confirmed; exact F1 edition and patch unknown |
| Game hardware | Windows, confirmed; likely RTX 3060 laptop; RAM, VRAM, CPU, and Windows version unverified |
| Model hardware | Exclusive access to an NVIDIA DGX Spark over the internet, confirmed; actual configuration and latency unverified |
| Dataset | MaleCNS from the supplied Google article, confirmed |
| Opponent | Published real-world Verstappen lap, confirmed |
| Event duration | Unknown; schedule provisionally assumes 36 build hours |
| Platform | Windows with Steam, confirmed |
| Mode | Solo dry Time Trial proposed, subject to edition support and resolution of automated-input/leaderboard questions |

EA F1 is conditional on the [anti-cheat and permitted-use assessment](06-ea-anticheat.md). Being alone on track does not establish that automated input is accepted or that results remain local.

## Scope

The MVP learns steering, throttle, and brake on one fixed circuit with one fixed car/setup. Use automatic gears and a declared, fixed assist configuration. Inputs are telemetry plus a reference driving path recorded by the team. A simple spectator view shows the game, current controls, network activity, valid lap results, and the target.

Learning all tracks, overtaking, race strategy, pit stops, wet weather, screen-only perception, and biologically realistic whole-CNS emulation are outside the weekend scope. Training all synapses from scratch is a later research direction. A full race duration comparison is also outside scope: the initial benchmark is one flying lap.

## Success ladder

| Level | Acceptance criterion |
| --- | --- |
| Integration | Live telemetry and three analog controls work; stop/release works reliably |
| Closed loop | Fly-derived policy completes a measured section autonomously |
| MVP | At least 3 valid autonomous flying laps in 5 predeclared attempts, under fixed settings |
| Learning result | Frozen trained policy improves completion or valid-lap pace over its initial version in equal-budget evaluation |
| Stretch | At least one valid autonomous lap below the declared real-world target, with the full attempt set reported |

An attempt ends in a valid lap, invalid lap, intervention, crash, timeout, or reset. Failed attempts remain in the denominator. If only the section milestone is reached, show that result accurately rather than presenting a full-lap claim.

## Proposed benchmark

Start with Austria / Red Bull Ring, subject to game availability and the team's preference. A short circuit reduces iteration time. Proposed reference: Verstappen's **1:04.391** in 2023 Austrian GP qualifying, established in the FIA classification. This is a qualifying lap, not a race lap or a claimed circuit record. [FIA classification](https://www.fia.com/events/fia-formula-one-world-championship/season-2023/austrian-grand-prix/qualifying-classification)

Display: **“EA F1 lap vs. Verstappen's 2023 Austrian qualifying time — cross-simulator comparison.”**

Game physics, car generation, assists, conditions, and Time Trial behavior differ from the real event. Matching the numeric time does not establish equivalent driving ability. Preserve this context beside the result. If a race-session lap is preferred, select and source it before evaluation; do not change the target after seeing results.

## Demo story

1. Explain the actual wiring map and show what is retained in this controller.
2. Show an early attempt and the trained controller using identical settings.
3. Run one live attempt with visible steering, pedals, and neural activity.
4. Show valid-lap rate, best and median valid time, and the real-world reference.
5. Reveal the network control experiments, including negative results.

Recorded runs must be labeled as recordings. Neural activity graphics must use actual sampled states. If only a subgraph fits, name it and show the retained neuron/edge counts.
