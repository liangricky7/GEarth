# Faultline: current plan and design

Updated October 10, 2026 (America/New_York), combining the labor-market engine from main with Jayden's compute, endowment, scheduling, observer and deployment work. This is the maintained specification. [Design comparison](faultline-design-vs-current.md) records the earlier branch comparison; [economy decisions](faultline-economy-status.md) records the rationale and deferred work. Historical documents and benchmarks describe their original engine, not this integrated version.

## 1. Product and status

Faultline is an observable AI society for KnightHacks 2026. Individual agents live in ten fictional countries under five institutional profiles. They work, shop, own businesses, govern, trade, borrow, communicate and respond to crises. The C# engine owns money, goods, state and time. Shared model servers choose bounded actions for separate agent identities; an agent is not a separately loaded model.

The default world contains 1,100 agents. One tick is one simulated day. Daily activity runs between strategic turns; payroll, labor matching and reviews run weekly. The observer displays country outcomes, relationships, routes, shipments, loans, agent profiles, accepted choices, inference errors and automatic run notes.

Implemented: the labor market, entrepreneurs, competing firms, quality of life, health and clinics, public services, taxes, trade, diplomacy, elections, conflict, internal accounting, local/Neon persistence, weekly scheduling, compact prompts, shared compute routing, seeded resident profiles, observer consistency and automatic reports.

Banking currently means legacy working-capital loans. Full deposits, interest, insurance and bank runs remain on `labor-market-potential-bank` and are deliberately deferred. Databricks analysis and Solana settlement are not implemented; Solana is a mega stretch goal. Saved checkpoints can be inspected, but an interrupted world cannot yet resume execution.

The simulation was stopped at the user's request. Merging or building does not restart it or replace any remote model server.

## 2. Geography, population and government

The countries are Aster, Beryl, Cinder, Dune, Ember, Fjord, Grove, Haven, Ionia and Jade. Profiles repeat across two sets of five: market democracy, social democracy, single-party planned economy, personal dictatorship and absolute monarchy. These synthetic institutions are not calibrated representations of real countries.

Every country starts with a government, bank, newsroom and 12 firms: three food, two energy, two tools, two entertainment, one medicine, one clinic and one freight. That is 150 institutional agents across ten countries. At 1,100 agents the remaining 950 are residents, initially 95 per country. Market-country firm owners are resident entrepreneurs, not additional population; planned-country firms are state owned.

An entrepreneur controls one firm and receives its strategic turns. An owned firm does not also receive separate turns; a state firm does. Closed firms do not decide. Population and the number of decision makers are therefore different denominators. Business startups create additional firm records, so total agent records can grow beyond the initial population without creating new residents. The factory currently supports 160–100,000 initial agents; this is a software range, not an inference or storage capacity guarantee.

Geography and food/energy multipliers repeat across the profile pairs. Directed routes include travel time, cost, risk, availability and a controlling country. Controlled experiments must rotate profiles over fixed geography and resources (`--government-rotation 0–4`) and hold model assignment constant.

## 3. Identity, endowments and observations

Agents retain IDs, role, country, occupation, goals, traits, memories, cash, inventory, last attempt, last accepted model turn and decision counts. Residents also track employer, salary, unemployment duration, health, doctor threshold, household policy, quality of life, overtime consent and any owned business.

Seeded `endowments_v1` profiles give residents wealth weights (0.25–4), productivity (0.6–1.4), bargaining (0.75–1.25) and normalized preferences for security, wealth, health, belonging and mobility. Profiles match resident ordinal IDs across countries; repeating the seed reproduces them. New worlds redistribute each country's existing resident cash budget by wealth weight without changing its total. This preserves the labor engine's starting budget based on two weeks of occupation pay, rather than replacing it with the old flat 2,000 minor units per person.

Productivity weights multiply goods output. Preferences appear in observations and the shared instruction text. Salaries remain fixed agreements in the labor-market design: bargaining weights are retained as profile metadata and do not reintroduce revenue-share wages. Wealth is an initial allocation, not a recurring income multiplier. Equal voting and weekly eligibility do not depend on wealth.

Role-specific observations contain relevant self, household/business, country, market, loan and public-event information. Governments get the full country, neighbors and weekly budget; banks and newsrooms omit irrelevant market blocks. Memories are limited to the last three, truncated to 100 characters. Other agents' private balances and memories are not exposed.

The engine builds the candidate menu. The worker renders compact numbered options and asks for `{choice, reason}` with a requested explanation of at most 100 characters. The validator retains compatibility with reasons up to 240 characters. The choice maps to an exact action and parameters; the engine checks role, money, goods, dependencies, expiry and replay before applying it. Shared rules lead the system prompt to support prefix caching. Qwen requests disable thinking; GPT-OSS requests use low reasoning effort.

## 4. Current economy and labor market

All amounts are integer minor units; 100 minor units = 1 simulated WLD. Cash plus shipment escrow and goods are conserved and asserted every day. This ledger is internal, without blockchain settlement.

Daily activity includes production, household food/energy purchases and consumption, health wear and illness, doctor visits, and quality-of-life updates. Every seventh day runs labor matching, firm reviews, payroll, taxes, owner payments, welfare, unemployment benefits, public healthcare and discretionary spending.

| Sector | Firms per country | Occupation | Base price (minor units) |
| --- | ---: | --- | ---: |
| Food | 3 | Farmer | 40 |
| Energy | 2 | Energy worker | 25 |
| Tools | 2 | Factory worker | 100 |
| Entertainment | 2 | Service worker | 60 per session |
| Medicine | 1 | Researcher | 120 |
| Clinic | 1 | Doctor | 450 per visit |
| Freight | 1 | Dockworker | Shipment fee |

Food, energy, tools and medicine are goods; clinic visits and entertainment are services. Teachers and soldiers work for government. Starting job shares per 100 remaining workers are farmer 21, energy 6, factory 25, service 20, dock 2, teacher 9, soldier 6 and unemployed 9. Essential occupations are placed first, including researcher and doctor. Allocation rounds remaining positions, so these are weights rather than an exact population census.

Weekly base salaries are farmer 1,000; service worker 1,100; factory worker 1,300; soldier 1,400; dockworker 1,450; teacher 1,750; energy worker 2,100; researcher 2,500; doctor 6,000. Education is abstracted into occupation pay and productivity; there is no separate education progression system.

Firms hire against headcount targets, prioritizing unemployed workers and then workers who gain at least 10% pay. After 14 days unemployed, workers can take unskilled work; doctors and researchers may switch related skilled roles. Public workers remain in the public sector. Salary agreements are fixed. A payroll shortfall triggers 10% pay cuts, layoffs and distress; four distress weeks can close a market firm.

Entrepreneurs own and work in exactly one firm. They receive draws from profit and dividends above an eight-week payroll buffer. Citizens may start firms when they can fund the investment and see a market opportunity; new firms begin 10% below the cheapest incumbent. Failed owners return to citizen roles. Weekly review can refill an empty viable sector. Quality differentiation remains deferred.

Owner price choices move ±10%. Autopilot reviews respond proportionally to shortage or glut, up to 20% per week, clamped to 0.5–3 times base. Planned economies fix prices at base. Household demand is spread across stocked sellers with weight `(cheapest/price)^4`; the cheapest seller does not automatically get every buyer.

Residents consume one food and one energy per day and restock under their standing household policy. Weekly tools and entertainment purchases compare marginal quality-of-life gains with depleted savings. Quality of life (0–100) weighs needs 0.35, health 0.20, comfort 0.20, security 0.15 and leisure 0.10, with diminishing returns; it moves one quarter toward its target each day. Approval follows outcomes.

Health loses one point per day, with a 1% daily illness chance costing 30. Unmet needs and overtime add costs. A clinic visit restores 40 and consumes medicine. Health scales output from 25% to 100%; wellbeing adds a milder output factor. Public healthcare buys visits for the sickest residents within its budget. Funded, healthy teachers determine an education factor of 0.7–1.0.

Effort 2 means consent to employer-offered overtime: one quarter more hours at 1.5 times the hourly pay, with costs to health and leisure. Effort 0 means striking and forgoing pay. Overtime is not an unconditional worker-controlled output or wage multiplier.

Taxes include wages at profile rates, household sales tax (default 5%), business profit tax (20%), import tariffs without agreements (5%) and migration fees. Governments fund public salaries, weekly welfare, unemployment benefit, healthcare and investment. Weekly welfare defaults by profile are 0/100/150/0/50; unemployment benefits start at 300 in market democracy, 400 in social democracy and zero elsewhere. The planned economy owns firms and covers shortfalls or collects cash above the payroll buffer; other displayed ownership percentages do not cause partial state transfers.

Trade uses funded internal escrow and actual cargo movement. Up to two shipments may be in flight per good, in half/one/two-population sizes. Transport fees go to domestic freight firms, with government fallback. Diplomacy affects borders, tariffs, routes and conflict. Conflict costs 5,000 upfront and 1,000 weekly, currently paid into the domestic bank; this destination remains an open design question. Migration costs 100 and migrants arrive unemployed.

Policy delays remain 0–3 days, elections every 100 days, crisis on days 40–90, diplomatic proposals expire after 60 days and border closures last 15 days. These old tick durations have not been rescaled; doing so requires a design decision, not an integration guess.

Legacy business loans accrue 5% interest and mature after 60 days, with bank-agent approval. The full banking branch remains outside this integration.

## 5. Weekly scheduling and shared compute

Every government, entrepreneur, bank, newsroom and state firm gets one weekly turn. Citizens are deterministically distributed across eight weekly cohorts, so each gets one turn every eight simulated weeks. No extra immediate retry is scheduled after a failed turn. Dynamic ownership and closures determine who decides at dispatch time.

The coordinator queues the week's cohort, admits bounded jobs, and observes the current world when dispatching. Role-specific capacities reserve institution and citizen slots; shared slots count once. An institution backlog can be bypassed to fill available citizen slots without dropping queued institutions. The engine drains the week's queue and in-flight decisions before weekly payroll. Cancellation interrupts that barrier, discards unfinished results and records a stopped checkpoint and notes. The final cohort is drained before completion.

The bounded decision queue retains duplicate exclusion, cancellation, maximum-pending measurements and prompt reuse of freed slots. Proposals older than seven simulated days expire; wall-clock request deadlines also apply.

| Compute source | Verified service at integration | Configured local concurrency |
| --- | --- | ---: |
| Spark A institutions | `Qwen/Qwen3-8B`, authenticated loopback port 8000 | 4 |
| Spark A citizens | `Qwen/Qwen3-4B`, authenticated loopback port 8001 | 8 |
| AMD cloud | `Qwen/Qwen3-4B`, authenticated HTTPS, shared roles | 1 |
| Spark B | Installer and private relay support prepared; deployment not verified | Disabled |
| AMD second model | Dual-model notebook prepared; not deployed/verified | Disabled |

The laptop runs one authoritative world. Spark and AMD supply inference. Local admission currently totals 13, drawn from enabled providers rather than the historical default of 64. AMD is available for overflow across roles. The provider pool reports active, completed and failed requests, scoped routing, cooldown and backend attribution. Provider HTTP clients ignore inherited proxies. URLs and private keys belong in ignored configuration, never in Git or public reports.

Spark A's pair uses pinned Hugging Face revisions recorded in `ops/qwen-models.json`. The installer validates cached tokenizers offline, serves weights/tokenizers from local snapshots, and verifies both APIs. Guarded recovery preserves the original container; clean reinstall pauses known competing GPT-OSS launchers, preserves restoration state and replaces only owned Qwen containers. See [Qwen setup](docs/faultline/18-all-node-qwen-setup.md). Remote driver, Netplan and Docker-permission changes are outside this setup.

### Capacity and timing

The old 120-second institution / 600-second citizen formula no longer describes this scheduler. The earlier estimate of roughly 1,500 agents was for that earlier engine and cannot be a maximum for the weekly world.

Weekly demand is `non-citizen decision makers + citizens / 8`, subject to cohort variation and ownership changes. At the initial 1,100-person world there are about 150 non-citizen decision makers and 854 ordinary citizens, or about 257 calls per simulated week. Faster compute shortens the weekly barrier; a larger population generally takes longer to advance simulated time rather than silently losing its turns.

No maximum population or full live-year completion time has been qualified for this integration. Prior successful latency measurements used larger prompts and the old scheduler; the stopped hybrid run also accumulated HTTP-error fallbacks. Neither proves sustained capacity of the new compact-prompt weekly system. The 100,000-agent software limit is not a supported operating target. Validation uses synthetic inference and local rule-mode runs without restarting paid/remote live workloads.

## 6. Persistence, API and observer

Local files or Neon over HTTPS store a whole-world JSON checkpoint every five ticks, plus ordered event rows. A save serializes once for persistence and the read-only API copy. Checkpoints retain a recent event window, 256 recent decisions and each agent's slim last-decision summary. Full observations may disappear from the recent window; the observer says when they are unavailable.

Events include labor matching, business startup/closure, payment, purchases, shipment state, policy, diplomacy, conflict and run lifecycle. The engine checks money/goods conservation independently of model explanations.

Read API: `/api/health`, `/api/runs`, snapshots, ordered events, paginated/searchable agents, agent profiles, structured notes and Markdown notes. Agent lists include revision/sequence/time metadata, decision-maker status, weights and model ages. Compute summaries include configured providers, capacity, pending by role, queued agents, coverage by role, throughput and citizen rotation. Eligibility is a weekly queue; future turns are described in simulated ticks rather than invented wall-clock deadlines.

Observer requests are canceled/replaced when run, search, page or agent selection changes. Late replies cannot mix runs. Policy strings and action objects both render. Decision profiles prefer retained full observations over the slim copy. Coverage should be compared with actual decision makers, including entrepreneurs and excluding owner-controlled/closed firms.

Every completed, stopped or paused run produces observations with event references, behavior counts by role and source, country outcomes, inequalities, example attributed reasoning and inference errors. Reports separate model, rule and fallback actions and avoid causal claims about real governments.

The permanent observer address remains `https://diarockets-faultline.finebear64.chatgpt.site/`. Its reconnect supervisor forwards read-only traffic when the laptop app is running. This integration preserves the address and configuration; it does not republish the website.

## 7. Operation and verification

Requirements: .NET 10 matching `global.json`, Python 3.12+, and Node for observer tests. Private Spark/AMD/Neon credentials stay in ignored `.env.local`.

```bash
.venv/bin/python ops/run_stage1.py --world --storage local
.venv/bin/python ops/run_stage1.py --world --live --compute hybrid --storage neon
```

These are operator commands, not evidence that the integration restarted a simulation. Add `--ticks 365` for a full simulated year; the default is 360 ticks. Population defaults to 1,100, interval to two seconds, and live concurrency is derived from enabled provider capacities. `--capacity` can lower global admission. The old `--institution-seconds` and `--resident-seconds` flags are removed because turns now follow simulated weeks.

Keep Stage 1 by omitting `--world`. Inspect an existing checkpoint without inference with `--run-id ID`. Ctrl+C or SIGTERM stops the launcher and its child services. The observer stays available after completion unless `--exit-on-complete` is supplied.

```bash
dotnet test Faultline.sln
.venv/bin/python -m pytest agents ops/test_amd_model_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q
npm run test:observer
.venv/bin/python docs/faultline/validate_design.py
.venv/bin/python ops/check_scheduler_integration.py
```

The integrated checks report 68 passing C# cases and the one known full-year failure, 121 passing Python cases, five passing offline observer cases and one optional real-API browser case skipped. A local 365-day rule fixture passed all 12 accounting/event/persistence checks and the Stage 2 API checks; synthetic weekly scheduling and cancellation checks also passed. These do not establish live throughput or realistic calibration.

Integration verification records exact results in [merge evidence](docs/faultline/evidence/main-integration.json). The historical reference validator covers Stage 1 contracts and fixtures, not economy calibration or sponsor completion.

Known calibration gap: `MoneyKeepsCirculatingForAFullYear` fails in the upstream labor-market design. Earlier year runs show severe health and demand collapse. The comparison document records that calibration items #1, #2, #4 and #5 were deliberately vetoed pending live-run analysis; price responsiveness (#3) was retained. The merge must not hide the failing test or introduce those deferred calibration changes.

## 8. Delivery and outstanding decisions

Ricky implemented the economy, labor market, quality of life, persistence refactors and deferred banking work. Jayden implemented shared compute, endowments, deployment, scheduling integration and observer consistency. Kennedy operates live runs and visualization; Ahmad works on inference/evaluation. Availability is a team snapshot, not an exclusive ownership claim.

Remaining work: explain HTTP-error fallbacks in the old hybrid run, analyze the teammate's labor-market run, decide economy calibration and tick-duration rescaling, inspect the updated observer visually, measure the integrated weekly system, deploy Spark B/AMD's second model if needed, and run controlled comparisons before Databricks analysis. Full banking and Solana remain deferred. Do not mix historical evidence with integrated live qualification.

See [README](README.md), [world runbook](docs/faultline/08-world-runbook.md), [compute setup](docs/faultline/18-all-node-qwen-setup.md), [current handoff](docs/faultline/19-model-handoff.md), and the source under `backend/`, `agents/` and `ops/`.
