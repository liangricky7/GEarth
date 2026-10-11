# Traffic Flow Lab — data, evaluation, and expansion

Research checked October 10, 2026. The sources below support the planning direction; no local LSU scenario has been downloaded, calibrated, or implemented yet.

## Evidence available for LSU

| Source | Confirmed public material | Limit |
| --- | --- | --- |
| [2015 LSU manual traffic control study](https://rosap.ntl.bts.gov/view/dot/31271/dot_31271_DS1.pdf) | Fall 2012 observations at five Baton Rouge intersections across four events; three intersections had observed manual control. Published geometry, timing summaries, throughput, and simulation analyses. | Historic, localized evidence. Raw videos and complete time series were not found linked; full Traffic Signal Inventory sheets were explicitly omitted. Not every observation has been established as postgame. |
| [LSU parking and traffic page](https://lsusports.net/gameday/parking) | Links to 2026 pregame and postgame maps and parking-zone information. | Routing context, not departure counts or queue measurements. |
| [LSU game-day controlled signals](https://services.arcgis.com/KYvXadMcgf0K1EzK/ArcGIS/rest/services/LSU_Game_Day_Controlled_Signals/FeatureServer) | Publicly indexed GIS service listing BRPD and ATMC controlled-signal layers. | Live layer queries and current accuracy remain unverified. |
| [Baton Rouge signal catalog](https://catalog-old.data.gov/dataset/traffic-signal-80af8) | Signal location/attribute dataset with listed CSV and JSON resources. | Locations alone do not determine phase programs, offsets, or manual control decisions. |
| [DOTD monitoring](https://www.dotd.louisiana.gov/about/office-of-project-delivery/planning/data-collection-management-systems/traffic-monitoring/) and [AADT metadata](https://gis.dotd.la.gov/metadata/Transportation/AADT.html) | Traffic-count resources and annual daily traffic context. | Exact game-date hourly coverage near LSU and accessible exports have not been verified. Annual averages do not reconstruct a departure surge. |

The study's manually controlled intersections were Nicholson/Roosevelt, Nicholson/Lee, and Stanford/Perkins. It records 21 hours 49 minutes of Baton Rouge manual control, not 21 hours 49 minutes of downloadable training data. Its authors also modeled adaptive/manual behavior; it does not establish that fixed timing is the current game-day baseline.

Use current geography with current restrictions for an explicitly LSU-inspired experiment, or reconstruct a historic scenario against historic measurements. Do not mix a current map and old throughput figures and label the result a validated reconstruction of present-day traffic.

## Reusable scenario packages

Represent LSU as data consumed by the engine. A scenario package contains:

- Network geometry, lanes, connections, speed assumptions, boundaries, and source dates.
- Intersection movements, phase order, permitted transitions, and clearance/service constraints.
- Intended origin/destination departures over time, vehicle classes, and background demand.
- Capacity disruptions and their timing; a shared routing/closure response if applicable.
- Warm-up, evaluation interval, demand end time, drain limit, and random seeds.
- Evaluation objectives and observation/action schema versions.
- Provenance for each input: observed, derived, or assumed; calibration observations held separate from test observations.

Changing a scenario should not require editing the learner or renderer. SUMO's network conversion tools can import OSM and several engineering formats, but imported geometry and generated phases still need review. A road map does not supply the missing travel demand. [Network import](https://eclipse.dev/sumo/docs/Networks/Import/).

## Evaluation contract

Compare fixed timing, a gap-responsive controller, and a max-pressure or another strong queue-responsive strategy when it can be implemented under the same action restrictions. Then compare the frozen learned policy. If a reference algorithm has broader phase freedom, report that difference or constrain it for the paired test. RESCO provides benchmark scenarios and baseline implementations useful for an independent sanity check. [RESCO](https://github.com/Pi-Star-Lab/RESCO).

Freeze evaluation scenarios before final tuning. Use the same network, scheduled demand, seeds, phase constraints, routing behavior, and observation access across the primary paired comparisons. Run multiple independent seeds; begin with at least five evaluation seeds per scenario if runtime permits. Report each paired outcome, mean change, spread, and regressions rather than a single favorable run.

Maintain a demand ledger: scheduled, awaiting insertion, currently active, completed, unfinished, and removed/teleported vehicles. Waiting outside the network must count. SUMO can delay insertion when road capacity is insufficient; these vehicles cannot be excluded from the traffic burden. [Vehicle insertion](https://eclipse.dev/sumo/docs/Simulation/VehicleInsertion.html).

Disable gridlock teleporting for scored runs and log collisions, removals, invalid routes, and other teleport events. Do not count teleport-assisted exits as successful demand completion. If a run cannot drain before its declared limit, report it as unfinished. SUMO's default gridlock behavior can move stuck vehicles forward, which would confound the comparison. [Teleport behavior](https://eclipse.dev/sumo/docs/Simulation/Why_Vehicles_are_teleporting.html).

Report:

| Metric | Meaning and treatment |
| --- | --- |
| Total queued vehicle-seconds | Primary traffic burden, including insertion queues; not just vehicles that finished. |
| Completion and unfinished demand | Finished fraction and pending/active vehicles at matched checkpoints. |
| Door-to-exit duration | Scheduled departure to completion, including insertion delay. Report completed-trip averages and p95 alongside censoring/unfinished counts. |
| Clearance time | Time from the final scheduled departure until all intended demand completes, or explicitly “not cleared.” |
| Approach service | Delay and long-unserved periods by approach; identify who benefits and who regresses. |
| Experiment validity | Policy overrides, broken routes, collision/removal/teleport events, and schema/version checks. |

Do not translate a completed-trip p95 into a network-wide p95 when many trips remain unfinished. A provisional MVP fairness target is no increase in total queued vehicle-seconds on any designated neighborhood approach across the paired evaluation suite, no increase in the worst approach's time-averaged queue, and no mandatory service violation against the strongest compatible baseline; declare the approaches and target before scored runs. Report seed-level variation and other approach regressions even if the suite-level target passes.

First success gate: valid accounting and constraints plus lower total queued vehicle-seconds in the predefined held-out event suite, with no fairness-target regression. This is a test target, not a predicted result. No fixed improvement percentage is promised.

## Proving expansion rather than assuming it

| Test | What changes | What the result establishes |
| --- | --- | --- |
| Same corridor, fresh random seeds | Realizations of known demand patterns | Repeatability within the trained family. |
| Same corridor, held-out demand | Different surge shape, origin balance, or background volume | Generalization to new demand. |
| Same corridor, ordinary traffic | Balanced daytime demand and commuter peaks | Whether an event-trained policy transfers to everyday use. |
| Same corridor, mixed-demand retraining | Train on several demand families and evaluate each separately | Breadth after retraining, not zero-shot transfer. |
| New road network, frozen weights | Geometry or movement structure changes | Only a valid shared representation permits this test; otherwise record incompatibility. |
| New network, fine-tuning or full retraining | Local data and control rules added | Reusability of the platform and adaptation cost. |
| More transport modes | Pedestrians, cycles, buses, priority vehicles | Broader modeling only after mode-specific service and objective checks. |

Use RESCO as a separate research testbed if an LSU package cannot be sufficiently grounded in time. Its repository describes scenarios from Cologne, Luxembourg, and Salt Lake City. Verify the chosen scenario's license, demand provenance, and compatibility locally before incorporating it. Never present a benchmark-city run as an LSU run.

## Expansion roadmap

**Next after LSU:** train and test on the same corridor under normal traffic, commuter peaks, and event surges. Compare a single mixed-demand controller with scenario-specific controllers. This directly tests whether broader training compromises performance on any family.

**Next network:** import a different corridor, review its phase mapping, and report performance before and after retraining. A fixed input/output policy is not automatically portable to different lane or phase counts. Normalized per-movement features, action masking, shared local policies, and neighbor/graph representations become justified once this incompatibility appears.

**More modes:** model people, bicycles, and transit explicitly, replacing car-count-only optimization with declared person-delay and service objectives. SUMO supports pedestrian modeling, but that capability alone does not supply calibrated pedestrian behavior or a multimodal policy. [Pedestrian modeling](https://eclipse.dev/sumo/docs/Simulation/Pedestrians.html).

**More traffic systems:** freeway ramp metering, route assignment, parking-lot release schedules, and emergency priority require different actions and often different models. Retain scenario ingestion, experiment orchestration, and reporting; add domain-specific control adapters. “All types of traffic” is a platform roadmap, not a credible claim for one signal controller.

Real operations would additionally need current detector data, local timing/control rules, calibrated demand, practitioner review, and a measured shadow evaluation against actual operations. The near-term product remains a simulator-backed experiment and planning tool.
