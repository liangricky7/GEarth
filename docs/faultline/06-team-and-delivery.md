# Faultline — four-person delivery plan

## Deadline and access

Deadline: **Sunday, October 11, 2026, 10:00 a.m. America/New_York (EDT)**. Planning began Saturday around 5:12 a.m., giving approximately 28 hours 48 minutes. All times below are local EDT. Finish the submission package at 9:30 a.m.; 10:00 is the deadline, not the start of uploading.

Jayden and Kennedy are the only SSH-capable teammates, using Tailscale. Kennedy handles initial serving setup and Jayden can recover either Spark. Ahmad works through authenticated model/agent APIs, and Ricky through database/RPC/API interfaces. AMD credits remain uncertain; verify allocation before assigning deployment hours to it.

## Assignment based on the supplied resumes

These are recommendations drawn from self-reported experience, not independent evaluations of skill. Do not include contact details or resume PDFs in the public project repository/submission. None of the resumes establishes production Solana-program or Databricks experience; time-box both platform smoke tests early.

| Person | Primary lane and concrete deliverables | Why this fits | Review/backup |
| --- | --- | --- | --- |
| **Jayden Hong** | Authoritative C# world engine/API; tick lifecycle; local information and role/resource checks; routing; scenario script; coordinator integration | CARTS LLM-to-SQL workflows, deterministic planning, ASP.NET APIs, persistent multi-agent assistant, telemetry | Ricky reviews persistence/finance; Ahmad reviews observations/actions; Kennedy is SSH recovery backup |
| **Ahmad Hijazi** | Python agent worker; memory/prompts; inference router and benchmark; valid-action evaluation; Databricks exports/notebook/comparison | Self-hosted vLLM verification, agentic workflows, JSON evidence pipelines, Python/Pandas, evaluation and real-time application experience | Jayden integrates worker contract; Kennedy executes remote deployment changes |
| **Ricky Liang** | Neon schema/migrations; finance module and reconciliation; TypeScript Solana outbox worker and escrow; financial read projections; integration checklist | Production PostgreSQL/API integrity, idempotency/version checks, full-stack delivery and CI leadership | Jayden owns calls into finance module; Ahmad consumes export schema; Kennedy consumes projections |
| **Kennedy Nguyen** | React/Three.js globe; routes/convoys; event/agent inspectors; LIVE/REPLAY labels; remote compute bootstrap and recovery runbook | Vulkan/OpenGL/Unity rendering, GPU profiling, entity systems, prior DGX Spark work, hackathon presentation | Ricky helps establish React scaffold; Ahmad supplies serving configuration; Jayden is second SSH owner |

Kennedy's graphics expertise supports the visual model, but a new native Vulkan engine would add packaging risk. Use a browser globe and focused rendering features. Ricky spends at most 30–45 minutes helping establish the React shell, then hands over ownership. Ahmad writes launch/config requests so remote work remains reproducible even without his own SSH access.

Assignments were informed by privately supplied resumes. Resume files and personal contact details are not included in this repository.

## Dependency rules

Agree on action/event/projection contracts before implementation branches diverge. Jayden owns shared schema changes; a breaking change needs affected owners to update fixtures and adapters in the same integration. Each owner can begin with synthetic fixtures without waiting for remote compute or another service.

The engine calls Ricky's finance functions inside its own transaction; Ricky's adapter reports receipts rather than independently changing balances. Ahmad receives due jobs and returns proposals; he never changes the world directly. Kennedy receives snapshot/events and does not re-derive accounting or logistics rules in the browser.

Keep one integrated main branch. Use `codex/` for branches created by Codex, but human branch names need not follow that prefix. Merge runnable slices every two to three hours. Ricky owns the integration checklist; each teammate runs the checks for their module. No member is responsible for every test or every sponsor pitch.

## Milestones

| Time | Shared gate | Parallel owner work |
| --- | --- | --- |
| **Sat 5:30–7:00 a.m.** | Contracts agreed; first model action from each Spark; database and Solana devnet connectivity checked | Jayden: engine skeleton and snapshot; Ahmad: prompt/schema and gateway; Ricky: Neon migration and devnet smoke; Kennedy: inspect/start existing serving, fixture globe |
| **Sat 7:00–10:00 a.m.** | 50-agent model → validator → Neon → viewer loop; preliminary AMD allocation decision | Jayden: tick/observations/routes; Ahmad: worker queues/memory; Ricky: reservations and finance module, escrow compile/local test; Kennedy: live event feed and actor inspection |
| **Sat 10:00 a.m.–noon** | 200 then 500-agent load measurements; closure changes a route; one loan agreement changes balances | Jayden: graph/crisis rules; Ahmad: concurrency/cadence measurements; Ricky: financial projection and devnet escrow funding; Kennedy: route/convoy rendering and finance view |
| **Sat noon–3:00 p.m.** | Complete primary crisis run; real devnet release and separate refund; 500-agent coverage | Jayden/Ricky: conservation and receipt recovery; Ahmad: fallback/invalid/stale tracking; Kennedy: cause links, route explanation, worker status |
| **Sat 3:00–6:00 p.m.** | Full observer experience; restart/reconnect; Databricks imports actual run data | Jayden: saved replay/recovery; Ahmad: Databricks transformation and report; Ricky: settlement fault cases and sponsor evidence; Kennedy: educational debrief and accessibility |
| **Sat 6:00–10:00 p.m.** | Controlled experiment batch starts; first scenario comparison | Ahmad owns runs/analytics; Jayden reviews simulation fidelity; Ricky audits finance metrics; Kennedy records polished demonstration paths |
| **Sat 10:00 p.m.–Sun 2:00 a.m.** | Finish batch, failures included; capture full measured replay; stabilize | Fix cross-service failures and resource bottlenecks. Rotate recovery duty between Jayden/Kennedy; protect rest windows once their lane gates pass |
| **Sun 2:00–6:00 a.m.** | Regression and sponsor evidence; feature freeze at 6:00 | No new tracks or new model families. Finish outstanding accepted features and resolve severe defects |
| **Sun 6:00–8:30 a.m.** | Rehearsal, reconnect/restart drill, evidence/replay freeze | Verify claimed integrations, archive configuration/results, write submission and source acknowledgements |
| **Sun 8:30–9:30 a.m.** | Final ready-to-submit package | Three-minute demo/video, project description, screenshots, repository/runbook, sponsor entries and checked access links |
| **Sun 9:30–10:00 a.m.** | Submission buffer | Upload, verify submission confirmation, and handle platform issues |

Milestones are integration gates, not invitations to wait until that time to integrate. If work starts later, compress optional polish and overlap independent lanes; retain the Sunday freeze/buffer. Use measured simulation duration to schedule experiment batches.

## Failure and scope decisions

- **Spark serving fails by 7:00 a.m.:** Kennedy/Jayden recover existing compatible service or swap to the smaller qualified model while Ahmad and Ricky continue with labeled fixtures. Kennedy can resume the fixture viewer when remote setup is handed to Jayden. This is temporary integration mode, not proof of AI.
- **AMD unallocated at 10:00 a.m.:** keep its disabled adapter slot and stop deployment work. Revisit only on an actual allocation change. Do not submit AMD on placeholder integration.
- **Solana program cannot pass funding/release/refund gates by 3:00 p.m.:** pair Ricky with Jayden for a bounded repair. A real transfer fallback is disclosed as reduced scope; the escrow requirement remains incomplete until implemented or explicitly revised by the team.
- **Databricks access/import fails by 6:00 p.m.:** Ahmad verifies the workspace/compute path before promising a sponsor submission. Keep the export/analysis design; local analytics alone does not fulfill Databricks.
- **500-agent throughput fails:** optimize cadence/context and slower world pacing using measured coverage; do not rename scripted citizens as LLM agents. Share the actual limitation and protect the society design.
- **Deadline pressure:** remove nuclear visuals, voices, extra currencies, custom shaders, physical database branching, extra sponsor integrations, and the 1,000-agent stretch first. Preserve the agreed world/finance/routing/analytics scope; report any required feature that remains incomplete.

## Final handoff ownership

Jayden: engine version, scenario config/hash, run start/recovery commands, routing invariants. Ahmad: exact model/prompt versions, capacity/coverage results, experiment manifests and Databricks report. Ricky: database migration version, accounting checks, devnet mint/program/accounts, verified signatures and settlement runbook. Kennedy: browser build, captured replay, screenshots/video, launch/tunnel instructions and viewing accessibility.

The final package states which integrations work, actual population and coverage, measured outcomes, and remaining limits. It never replaces observed failures with planned success. The documents define the build; their completion does not mean the project is already implemented.
