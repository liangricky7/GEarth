# Four-person hackathon plan

Historical draft: [document 10](10-implementation-roadmap.md) supersedes this schedule, mandatory experiments, and fallback priorities. Use documents 11–13 for interfaces, network, and learning. The older EA-oriented schedule below is retained for history, not as an additional or alternative execution checklist.

This is a provisional 36-hour schedule, not the confirmed event timetable. Before the event, check its pre-work rules; keep current work to research and design unless those rules permit more.

## Ownership

| Teammate | Primary responsibility | First deliverable |
| --- | --- | --- |
| A: game integration | Windows host, UDP, virtual controller, stop/reset behavior | Recorded telemetry and verified analog-control loop |
| B: connectome | Spark environment, MaleCNS import, network dynamics, latency/memory | Reproducible graph and measured Spark inference |
| C: learning | Demonstrations, path features, readout, baselines | Trained checkpoint and held-out evaluation |
| D: experience/evaluation | Dashboard, benchmark, run manifests, pitch/video | Visible run status and credible results table |

One person owns the game laptop at a time. A and C schedule driving/data collection blocks; A and B benchmark full-graph local inference alongside the game and verify Spark-to-laptop checkpoint parity. B and C can train on Spark while a frozen checkpoint drives locally. Test internet inference only as an alternative; do not make the core demo depend on unmeasured latency. D can develop against explicitly labeled recorded data. No parallel game instances are assumed.

## Milestones and decision gates

Before committing the weekend to EA integration, resolve the [automated-input question](06-ea-anticheat.md) for the exact edition and mode. This is a project recommendation based on the user's anti-cheat concern, not a claimed published EA research-approval procedure. Keep simulator-independent model and data design moving while that remains open.

| Elapsed time | Target | Decision if blocked |
| --- | --- | --- |
| 0–3 h | Identify edition/protocol; verify controller/telemetry; establish Spark access and compatible environment | Timebox controller or package issues; report EA integration blocked if unresolved |
| 3–7 h | Capture demonstration path; benchmark full graph locally and on Spark; check checkpoint parity | If local inference too slow, qualify remote path; if neither works, disclose graph reduction or narrower scope |
| 7–13 h | Fit readout; conventional baseline; first closed-loop section | Fix observation/control alignment and recovery data before expanding model complexity |
| 13–22 h | Complete laps; refine recovery and controller calibration | Reduce speed and focus on a repeatable section if full laps remain unreliable |
| 22–29 h | Freeze candidate; compare baselines; optimize pace if stable | Prioritize validity and evidence over additional RL |
| 29–33 h | Execute scored attempts; capture demo; finalize result labels | Use the best achieved milestone; disclose failures |
| 33–36 h | Rehearse; protect working setup; package artifacts | Show labeled recording if live run fails |

For a 24-hour event, cut online optimization and advanced visuals first. For a 48-hour event, add recovery data and repeated evaluations before increasing biological complexity. Account for breaks and avoid scheduling unattended training that requires manual resets.

## Risks and responses

| Risk | Early evidence | Planned response |
| --- | --- | --- |
| Automated control not approved or permission unclear | No applicable EA guidance for the specific experiment | Seek written clarification; keep a simulator with explicit agent support as a fallback candidate |
| Game rejects virtual controller | Controller visible to OS but no calibrated in-game response | Resolve in first gate; do not spend the weekend training against an untested interface |
| Protocol mismatch | Implausible values, packet-length failures | Pin correct schema and validate against game UI |
| Spark package/kernel incompatibility | ARM64 package unavailable or sparse kernel fails | Use verified supported stack; keep implementation simple and benchmark early |
| Network jitter/disconnection | Interrupted transfers or expired actions in optional remote mode | Default to local inference; cached checkpoints/data; local watchdog for remote experiments |
| Resource contention | Frame drops, latency tails, memory exhaustion | Reduce game rendering load; benchmark inference placement; reduce graph explicitly only if required |
| Fixed network produces useless features | Saturation, no input sensitivity, readout fails | Adjust documented dynamics/mappings, then compare to ordinary baseline |
| Policy only memorizes clean laps | Good imitation error, immediate off-path failure | Record recovery examples and repeat closed-loop validation |
| Too few driving samples | Most wall time spent resetting | Short section curriculum; manual reset discipline; no cold-start RL |
| Misleading fly claim | Controller works despite disconnected graph | Audit data path and run graph controls; label baseline/hybrid modes |
| Real-world target dominates the pitch | Fast game time mistaken for real driving equivalence | Keep cross-simulator label visible and report settings |

## Decisions still needed

| Decision | Owner | Needed by |
| --- | --- | --- |
| Exact EA F1 edition, patch, and Steam account access | A | Before implementation |
| EA guidance for autonomous inputs and whether selected mode uploads results | A/D | Before committing to EA-specific implementation |
| Windows version, CPU, RAM, VRAM, free disk | A/B | Before runtime design is finalized |
| Spark memory/software/access window; internet latency if remote inference needed | A/B | Before choosing live inference placement; internet access already confirmed |
| Event build hours and permitted preparation | Team | Before scheduling |
| Austria and qualifying target vs. a race-session lap | Team/D | Before data collection |
| Actual full graph vs. explicitly reduced graph | B/team | After measured inference gate |
| Car, assists, setup, DRS/ERS handling | A/C | Before demonstrations |
| Available human demonstration driver/controller | C | Before demonstrations |

No software, drivers, game modifications, or datasets have been installed by this planning work. The next step is resolving these decisions and turning the gate criteria into implementation tickets.
