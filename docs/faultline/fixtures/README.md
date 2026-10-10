# Synthetic handoff fixtures

All files in this directory are design examples, not recordings of a running application. Show FIXTURE in the viewer whenever using them. Population counts represent the configured reference world; model decision coverage starts at zero and is not a benchmark result.

- `world-snapshot.json`: projection at tick 8 / sequence 2. The seeded convoy has reached Aster Port in this illustrative scene; the direct link has just closed. This location is illustrative, not evidence that a model dispatched it.
- `timeline.ndjson`: connected, ordered event history. Events 1–2 precede the snapshot; events 3–4 demonstrate a synthetic business response and its route effect. After fetching the snapshot, consume only events with seq > 2.
- `agent-action.json`: model-output shape for a cheapest-route decision, without trusted request/identity fields.
- `border-event.json`: the same event as sequence 2 in the timeline.
- `invalid-negative-loan.json`: must fail action schema validation.
- `forbidden-role-action.json`: schema-valid proposal that the engine must reject because a citizen cannot control a border.

Typed event payloads in this sample:

| Type | Fields |
| --- | --- |
| `world_initialized` | scenario_id, population, mode |
| `border_changed` | edge_id, open, expires_tick |
| `action_accepted` | decision_id, worker_id, model_revision, observation_tick, action |
| `route_changed` | previous_edge_ids, new_edge_ids, objective, planned_remaining_travel_ticks, planned_remaining_cost_minor |

The route effect starts at Aster Port, so it excludes the already-traversed farm link. Planned remaining cost is 500 minor units and time is 11 ticks. These differ from the full origin-to-destination cheapest route's 600 minor units and 14 ticks. Loans, receipts, and other event payloads become typed implementation DTOs under the same envelope; add fixtures when implementing them.

Run `python3 docs/faultline/validate_design.py` from the repository root. Its checks cover these design artifacts only. Use a standards-compliant schema library for the production application.
