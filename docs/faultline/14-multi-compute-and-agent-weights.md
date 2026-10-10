# Shared compute, unequal agent endowments and scheduling integration

## Current topology

Jayden's laptop remains the coordinator, simulation engine, storage client and observer host. It submits inference over HTTP to model servers rather than running a separate world on each GPU. Jayden can reach Spark A; Ricky operates Spark B and Kennedy can access it. Team members can reach the authenticated AMD endpoint. Team access to AMD shares one GPU and one generation slot; it does not create additional compute capacity.

Spark B is supported by the code but remains disabled locally until Jayden has a reachable endpoint and a privately supplied key. Ricky's access to it does not establish reachability from Jayden's laptop. No Spark model server, AMD generation limit, Netplan or cloud networking was changed in this implementation.

## Pool configuration

The launcher keeps each enabled provider's model, credentials and capacity. Omit `--capacity` to use the sum of configured capacities, subject to a global ceiling of 384 and each backend's ceiling of 128. Those ceilings are input limits, not demonstrated sustainable capacity. An explicit `--capacity` is a total in-flight cap.

The initial operating configuration remains 64 slots for Spark A and 1 for AMD. Spark B starts at 64 when enabled. That would permit 129 in-flight requests across all three backends, once Spark B is connected. AMD stays at one because its current server permits one generation at a time.

Add these values to the ignored `.env.local` after obtaining Spark B's connection details:

```dotenv
SPARK_B_ENABLED=true
SPARK_B_BASE_URL=http://127.0.0.1:18002/v1
SPARK_B_MODEL=REPLACE_WITH_ACTUAL_SERVED_MODEL
SPARK_B_API_KEY=REPLACE_WITH_PRIVATELY_SUPPLIED_KEY
SPARK_B_CAPACITY=64
```

Loopback HTTP assumes an operator has already opened a private SSH forward from this laptop to the second Spark's model port. Alternatively, configure an authenticated HTTPS model endpoint reachable from Jayden's laptop. Ken's shared-laptop Tailscale HTTP relay is supported with an exact host pin; see [his server and relay setup](15-ken-spark-b-relay.md). The launcher manages only its existing Spark A forward; it does not open a Spark B tunnel automatically. Do not expose a raw unauthenticated model port or guess another machine's connection details.

```bash
.venv/bin/python ops/run_stage1.py --world --live --compute hybrid --storage local --population 1100
```

This includes enabled Sparks plus AMD. `--compute spark` includes enabled Sparks without AMD. `--compute amd` uses AMD alone, without Spark SSH. Rule and saved-run modes make no inference requests. Only one coordinator should own a live world. Coordinate independent teammate GPU tests with the operator: per-worker admission limits do not enforce a global limit across multiple laptops.

The existing provider pool chooses the available backend with the lowest fraction of occupied slots and rotates ties. A failed backend enters a 30-second cooldown. Active requests to other providers continue. The worker returns explicit fallback results on model errors; a later eligible agent turn can use a healthy backend. Requests keep backend/model attribution. This is capacity-based routing, not a claim that all models behave equivalently or have equal throughput.

## Population and speed

Simulation residents share model weights. Adding a resident stores a profile and world state; it does not load another copy of an LLM. Population, concurrent requests, tokens/second and simulation ticks are different quantities.

Existing short representative-prompt tests on Spark A measured about 3.15 valid decisions/second at 64 concurrent requests. At 128, throughput was about 3.18/second while p95 latency increased from roughly 24 to 48 seconds. Raising concurrency alone did not materially raise throughput. These results exclude evolving prompts and integrated storage, and do not establish sustained capacity. Reuse [the 64-slot evidence](evidence/world-spark-benchmark-high.json) and [the larger sweep](evidence/world-spark-benchmark-upper.json); no new coverage qualification was run.

For 70 institutions taking turns every 120 wall-clock seconds and residents every 600 seconds, average inference demand is:

```text
required decisions/second = 70 / 120 + (population - 70) / 600
planning population = 70 + floor((0.8 × measured accepted decisions/second - 70 / 120) × 600)
```

The 0.8 multiplier reserves 20% of measured throughput for variability. Use the aggregate accepted rate from integrated runs, including invalid results and outages, rather than adding advertised hardware performance. Requests can still wait longer during bursts.

| Population | Average demand at existing cadence |
| --- | --- |
| 1,100 | 2.30 decisions/second |
| 2,500 | 4.63 decisions/second |
| 5,000 | 8.80 decisions/second |

Keep 1,100 as the provisional live population. 2,500 is a candidate after the second Spark joins and aggregate accepted throughput supports it with headroom; it is not yet qualified. The 100,000 population input ceiling is a world-state limit, not a promise of timely AI turns for every resident. No maximum agent count has been demonstrated.

Live scheduling now applies completed results and refills free slots between tick boundaries. A fast AMD response can free its slot without waiting two seconds for the next tick or for a slow Spark request. The engine still advances at tick boundaries and commits every five ticks. No unbounded queue of jobs is introduced; due identities remain in world state until admitted.

Reducing `--interval` advances economic time faster but does not accelerate model generation or change wall-clock decision eligibility. Replies older than 90 simulation ticks are still rejected, along with elapsed deadlines and changed agent intent/preconditions; extremely short live intervals can therefore increase rejection. Use short intervals for rule-only experiments, and retain two-second ticks for the provisional live setting. Accelerated live time needs a separate calibrated staleness/cadence policy before use.

## Agent weights

`AgentEndowments` generates seeded, bounded profiles independently for fictional residents. These are experiment parameters representing unequal starting conditions, opportunities and preferences. They do not measure human value, inherit real demographic attributes or train different neural-network weights.

| Dimension | Distribution and effect |
| --- | --- |
| Wealth | Clipped lognormal weight, 0.25–4. Country cash is allocated proportionally within the original 2,000 minor units per resident budget. Integer remainders are assigned deterministically. Wealth is an initial condition, not a recurring income multiplier. |
| Productivity | Uniform 0.6–1.4. Scales labor output and relative business wage shares. Health, wellbeing and effort remain separate factors. |
| Bargaining power | Uniform 0.75–1.25. Changes the share of a funded business wage pool; does not increase production or create money. Public salaries remain governed by the existing funded salary rule. |
| Personal priorities | Five independent positive draws normalized to sum to one: security, wealth, health, belonging and mobility. The live model receives definitions and these relative priorities alongside traits, goals, budgets, memories and available actions. These preferences guide the prompt; they do not force a particular model choice. |

Different dimensions use different seeded hash inputs, so wealth does not automatically imply productivity or bargaining power. Profiles for the same resident ordinal are paired across countries to reduce initial-distribution confounding. At unequal country sizes, the additional resident changes the sampled set and proportional cash allocation. Geography, government policy, country-specific personality traits and subsequent choices remain additional differences.

The scenario hash includes `endowments_v1`. Existing saved agents without weights deserialize to neutral productivity/bargaining values and equal preference weights; their historical balances are preserved. Migration retains the resident's profile. The scheduler ignores endowment values, preserves its institution/resident allocation and excludes duplicate pending agents. Voting/approval remains unweighted by wealth.

Weights appear in individual observations, agent list/detail responses and saved checkpoints. Automatic notes report the initial-weight version, current cash range, and descriptive cash/health/wellbeing outcomes for initial-wealth cohorts. Model backend counts remain separate. These reports describe one synthetic run; they do not establish that a government or a weight caused an outcome. Offline rule choices remain the existing deterministic action selection; preference-guided thinking applies to live model turns.

## Jayden's next Trello task

[4. Jayden — Schedule agent decisions without growing queues](https://trello.com/c/N9Fgq3Wm) is the earliest outstanding assigned task. Existing scheduling/API work was reviewed, brought together with main and AMD changes, then extended for inference completion between ticks. The integration PR is the remaining review step before main contains the work. Teammate-owned worker/storage review tasks remain separate.

Verification at this revision:

- 58 C# tests pass, including admission limits, duplicate exclusion, independent completion, cancellation, resident fairness, agent profile reproducibility, old checkpoint defaults, wage/output effects and cash/goods conservation.
- 39 Python tests pass, including three-provider configuration, explicit total caps, AMD-only mode, missing Spark B credentials, provider routing, cooldown, validation and cancellation. Worker tests use a fresh provider pool and explicitly release cooldown before testing post-failure recovery; production cooldown remains enabled.
- The local synthetic worker integration used API port 5081 and worker port 8100. Eight synthetic decisions completed within the first 150 ms tick with only three pending slots, demonstrating slot refill between ticks. A completed run ended with zero pending work. A separate shutdown discarded three pending requests, committed zero pending work and applied no canceled decisions.
- Both synthetic integration runs automatically generated observation reports, and the completed run retained backend attribution and complete event history. These are scheduler fixtures, not real GPU inference or capacity evidence. See [the recorded lifecycle check](evidence/multi-compute-scheduler.json).

Repeat only this local lifecycle check, with ports 5081 and 8100 free:

```bash
dotnet build Faultline.sln --nologo -v quiet
.venv/bin/python ops/check_scheduler_integration.py
```

Existing observer port 5080 and the permanent share URL were left running during integration verification. Code on this branch is not deployed to the existing saved-run observer by pushing the branch.
