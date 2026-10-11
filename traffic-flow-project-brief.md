# Traffic Flow Lab — working project brief

Planning proposal, October 10, 2026. Working name only. No traffic simulator, trained policy, Spark benchmark, or verified performance improvement has been implemented in this workspace.

## The idea

Traffic Flow Lab lets a planner build a traffic scenario, compare control strategies, and investigate which strategy works under changing demand. One DGX Spark produces simulation experience; the other trains a controller. A web app displays synchronized baseline and learned-control runs, explains measured outcomes, and lets the audience change the scenario.

**Pitch:** “We train traffic controllers in a simulated road network, then test whether they reduce congestion without shifting the burden onto other streets. Our first case study is traffic around LSU after a football game.”

The broader product is a reusable traffic-control experimentation platform. LSU gives the first demonstration a recognizable problem and useful public context. It does not define the platform's permanent boundaries.

## Problem and intended user

A traffic strategy that works for an ordinary afternoon may behave differently when demand becomes strongly directional, a road loses capacity, or several parking areas empty together. Clearing one intersection can create a queue at the next. Reducing the average wait can also conceal a badly underserved side street.

The initial intended users are transportation researchers, event operations teams, and traffic planners comparing proposed strategies. Their decision is: **“Which controller or timing plan performs best for this network and these assumptions, and where does it fail?”** These are intended users, not validated customers. A useful follow-up is to have a transportation practitioner inspect the assumptions, baselines, and decision report.

The product returns a reproducible comparison, including the demand assumptions, model checkpoint, baseline, uncertainty across runs, and affected approaches. The first prototype runs entirely in software and evaluates simulated outcomes.

## What the AI does

A small reinforcement-learning policy observes traffic state and chooses among configured signal actions. It learns by interacting with the simulator. It is evaluated against fixed timing and demand-responsive controllers. An LLM is unnecessary for the control loop; any future natural-language explanation must be grounded in logged actions and measurements.

Start with green-phase duration decisions along a small, fixed corridor. Routing, road closures, and postgame restrictions are scenario inputs. Changes to those inputs apply equally to every compared controller.

## First demonstration

1. Open a small LSU-area corridor with two or three controlled intersections and its connecting roads. Confirm geometry before settling the exact boundary.
2. Select an LSU-inspired departure surge with background neighborhood traffic. Label the demand as assumed unless independently measured.
3. Start baseline and learned-control runs using the same demand, starting conditions, and random seed.
4. Show moving vehicles, signal phases, queue lengths, total delay, unfinished demand, and approach-level service.
5. Let a judge increase the departure surge or select a validated lane-capacity disruption. Re-run the paired comparison from a common initial state.
6. Show where each strategy improved or regressed and identify assumptions that limit the result.

The memorable moment is coordinated behavior: a controller may hold traffic upstream because the downstream street is full. The app can highlight that measured relationship without pretending to expose the neural network's internal reasoning.

## Two-Spark design

| Component | Initial role |
| --- | --- |
| Spark A | Independent headless simulation workers, local policy inference, experience collection, and separate frozen-checkpoint evaluation runs. |
| Spark B | GPU policy training, versioned checkpoints, training metrics, and experiment tracking. |
| Web app | Scenario selection, synchronized visualization, metric comparison, and downloadable experiment summaries. |

SUMO's microscopic simulation mainly runs on a CPU core per instance. Multiple independent instances can produce experience concurrently; this is different from splitting one simulation across GPUs. The proposed design uses ordinary network transfers rather than distributed model training. Benchmark the deployment before claiming any two-Spark speedup. A small controller may also underutilize Spark B's GPU. [SUMO FAQ](https://eclipse.dev/sumo/docs/FAQ.html).

## A path beyond LSU

| Stage | Expansion | Evidence required |
| --- | --- | --- |
| 1 | LSU-inspired event surge on one corridor | Valid simulation; paired comparisons; held-out departure patterns. |
| 2 | Ordinary afternoon, commuter peaks, and directional surges on that corridor | A mixed-demand controller or explicit specialist selection; results for each demand family. |
| 3 | Another event venue or urban corridor | Import and review a new scenario; test transfer separately from retraining. |
| 4 | Pedestrians, bicycles, buses, and emergency priority | Additional observations, service constraints, and person-level objectives. |
| 5 | Freeway merges, ramps, and network routing | New control adapters, decision spaces, and suitable baselines. |

**Reusable software does not imply universal learned weights.** Demand changes on the same roads are an easier first test than transfer to a different intersection layout. The current fixed-corridor policy is allowed to be network-specific. Future normalized movement representations and graph-based controllers are research options, not MVP requirements.

## Positioning and boundaries

The broad concept has substantial existing overlap. [SUMO-RL](https://github.com/LucasAlegre/sumo-rl) already supports learning traffic-signal policies in simulation. [Aimsun's football-event tutorial](https://docs.aimsun.com/next/26.0.0/Tutorials/3_Traffic_Management/3_Traffic_Management.html) covers stadium-exit congestion and strategy comparisons. [Yunex SCOOT](https://us.yunextraffic.com/portfolio/adaptive-technologies/scoot/) provides demand-adaptive control. An interactive interface, an LSU case study, broader traffic coverage, and two Sparks do not by themselves establish originality or a market advantage. Choose a specific experimentally demonstrated improvement or validated user workflow before treating this as a differentiated product. See the [competitive assessment](docs/traffic/03-competitive-assessment.md).

A credible hackathon outcome is a functioning experiment platform plus an honestly evaluated policy. Improvement is a hypothesis. A learned policy that loses to the strongest baseline must be presented as such. The first build does not establish real-world deployment readiness or complete coverage of postgame pedestrians and vehicle behavior.

Implementation details: [architecture and MVP](docs/traffic/01-architecture-and-mvp.md). Evidence and expansion: [data and evaluation](docs/traffic/02-data-evaluation-and-expansion.md).
