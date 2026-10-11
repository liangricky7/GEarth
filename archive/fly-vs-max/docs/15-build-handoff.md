# Build handoff and remaining evidence

Planning handoff, October 5, 2026. No game integration, dataset import, model training, or hardware qualification has been performed. This is a navigation/checklist document, not another implementation specification.

## Read these in order

1. [10 — roadmap](10-implementation-roadmap.md): priorities, owners, time limits, and stop rules.
2. [11 — implementation contract](11-implementation-contract.md): observations, controls, timing, lifecycle, comparison, and scoring.
3. [12 — network specification](12-fly-network-specification.md): exact proposed graph, mappings, dynamics, readout, and diagnostics.
4. [13 — hybrid learning](13-hybrid-learning-plan.md): demonstrations, concrete RL pilot, reward, boundary masks, evaluation, and rollback.
5. [Run manifest](run-manifest.template.json): fill accepted values and evidence hashes; it is deliberately not executable configuration yet.

Read [08](08-research-roadmap.md) for simulator/source evidence and [14](14-fly-biology-and-model-review.md) for the biological explanation. Documents 01–07 are supporting history. Document 09 is a short architecture rationale. Do not combine all their old schedules or experiment suggestions into new requirements.

## What is settled on paper

- Preserve the original objective: a fly-derived controller chasing a predeclared published professional lap time, explicitly cross-simulator. Section control and lap completion are interim milestones, not substitutes for that claim.
- One car/circuit, numeric observations, local real-time controls, fixed fly-derived graph, trainable output head, demonstrations followed by a bounded RL pilot and optional corrections.
- Two Spark operators suffice. Use complete files and immutable bundles, not remote per-action calls. Qualify a small-head CPU update fallback if it saves measured turnaround.
- Start with one graph candidate, a small inspected dataset, and one direct-feature MLP comparison. Other architectures/biological claims require separate time and evidence.
- Freeze at the provisional hour 28 of 36 and preserve eight hours for scoring, video, rehearsal, and submission. Confirm actual duration before adopting these times.
- Evaluate the final fly checkpoint, the imitation checkpoint if different, and the simple non-fly controller. Five attempts per distinct condition; keep all outcomes and the initial offline test data separate.

## Decisions requiring people or access, not more model research

| Missing fact | Owner | Required decision/evidence |
| --- | --- | --- |
| Event duration and pre-work/submission rules | Team / D | Confirmed event rules and revised schedule if needed |
| Selected Assetto Corsa: version, permitted use, game/DLC access, fallback | A + D | Simulator choice confirmed October 9; exact content/use evidence still needed; document 08's unsent inquiry is available |
| Laptop specifications and Spark access window | A + B | CPU/RAM/VRAM/storage, operating system, working authorized access |
| Available demonstrator and input device | C | Named driver, recording block, demonstrated target-car inputs |
| Exact benchmark and vehicle conditions | C + D | Primary source/time and frozen car/layout/setup/assists/lead-in/validity rules |

These remain open. Do not interpret this handoff as permission to assume them, purchase content, message developers, or begin work outside event rules. Work on independent modules when a dependency is unresolved.

## Implementation tickets and proof of done

| Order / owner | Deliverable | Evidence that closes it |
| --- | --- | --- |
| 1 / A + D | Minimal adapter and recorder | Manual turn/braking log; correct signs; control/stop/reset demonstrated; pause and stale-data behavior known |
| 2 / B, alongside ticket 1 | Source audit and sparse graph | Actual schemas and label mapping, IDs/counts/hashes, memory report, hand-checked edge direction and normalization |
| 3 / A + B | Local runtime qualification | Representative game-plus-model timing/memory run, perturbation response, reset replay, cross-host parity, release-path fault tests |
| 4 / C + D | Initial dataset and route | Two pilot laps inspected; session split recorded; route wrap/parallel-segment check; teacher time; immutable snapshot |
| 5 / C + B | Imitation policy and one comparison model | Saved initial checkpoints, no label leakage, offline validation, autonomous connected-section attempts |
| 6 / C + A | Improvement loop | Targeted corrections; eligible RL pilot only after boundary/reward checks; complete export/compare/rollback cycle within its time cap |
| 7 / D + team | Scored demonstration | Frozen settings/checkpoints, five attempts per distinct condition, result table, backup video, successful cold-start rehearsal |

Tickets 2 and 4 can overlap qualified parts of integration. No ticket's name implies it has been completed. Keep an experiment card with parent version, one proposed change, budget, observed result, and promotion decision. Hash changes that affect neural features invalidate the appropriate caches.

## Source review that informed this handoff

The latest primary-source review confirmed public MaleCNS bulk tables, the adapter's optional camera path, the Spark architecture/environment distinction, and PPO customization/CPU guidance. References and implications are in documents 08, 12, and 13. These establish design choices and dependencies, not hardware compatibility or racing performance. Actual Arrow schemas, accepted software versions, car controls, resets, and lap validity still require the selected environment.

## Planning completion audit

| Requirement | Evidence in the current docs | Status |
| --- | --- | --- |
| Research the fly and relevant integration/training methods | Primary-source notes in 08 and 14; source/method links in 12–13 | Completed as research; specific simulator/model remains untested |
| Make the weekend scope realistic without abandoning the target | 10's milestones, time caps, fallback labels, protected evaluation | Specified |
| Make modules and data flow implementable | 11's ordered features/actions, lifecycle, baseline, timing and provenance; 12's graph recipe | Specified, pending hardware/data qualification |
| Define learning and protect prior advances | 13's pilot configuration, original-sample probabilities, transition masks, rewards, connected-corner checks and rollback | Specified, pending pilot evidence |
| Prevent conflicting old instructions | README precedence, history banners, revised 08–09, matching 10–13 and manifest | Reviewed |
| Make unresolved work concrete | Owner/evidence tables above and in 11; accepted manifest fields remain null until measured/confirmed | Explicitly handed off |
| Verify documentation | Link/fence/JSON checks plus manual review of scope, math, boundaries and matching defaults | Performed during this planning pass |

The planning stopping point is reached when this audit holds. The next confidence gain requires the people/access decisions above and actual integration, graph import, recordings, or driving. Filling unmeasured values with guesses or adding further optional experiments would not close those gaps.

Final documentation checks: all 16 Markdown files passed local-link, fence, and whitespace checks; the manifest parsed and its schedule, feature dimensions, split counts, pilot settings, and intentionally unqualified flags agreed with the specifications. A synthetic arithmetic illustration with uniform progress gave discounted returns of approximately 0.646 for a 70-second finish, 0.568 for an 80-second finish, 0.333 for half a lap in 35 seconds followed by failure, and zero for no progress. This checks those illustrative orderings only; actual telemetry, termination, reward code, and learning still require the planned pilot tests.
