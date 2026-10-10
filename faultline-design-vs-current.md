# Faultline: design doc vs. current code

**Date:** October 10, 2026 (America/New_York)
**Code compared:** branch `ricky/labor-market` at `119e478`
**Design doc compared:** `faultline-current-plan-and-design.md`, last changed in `6da1096`

The design doc calls itself the "current, self-contained project specification." It was written before the labor-market work, so most of its economy, scheduling and population sections now describe an older engine.

Main also has a newer economy write-up in `faultline-full-design.md` ("Current economy and labor market", commit `da090c0`). It matches commit `41bb2d3` (one firm per sector, 5% price steps, 60% of spare cash spent on tools), not the current code, and it isn't on this branch. Section 6 lists where it's also out of date.

---

## 1. The project now

Faultline is an observable AI society. 1,100 agents live in ten fictional countries under five government types. They work, shop, run businesses, trade across borders, govern and negotiate. Each decision is one model call choosing from an engine-built menu, and the engine runs daily life between decisions. Money and goods are conserved and checked every tick.

| Part | State on `ricky/labor-market` |
|---|---|
| Engine (C#) | One tick is one day; the weekly economy runs every 7th tick. Seven sectors with 12 competing firms per country. Labor market, entrepreneurs, quality of life, health and clinics, public services, trade, diplomacy, elections and conflict |
| Scheduler | Weekly cohort: every institution and entrepreneur, plus one eighth of citizens on rotation. The run waits for the week's decisions before payroll (weekly barrier). Decisions older than 7 days expire |
| Worker (Python) | The rules text is a shared, cached system prompt. Menus are compact numbered lines. The reply is `{choice, reason}` with a reason of at most 100 characters |
| Storage | Neon over HTTPS or local files. One checkpoint (whole world as JSON) every 5 ticks, plus one row per event |
| Observer | Country metrics, cargo, diplomacy, loans, and searchable agent profiles with their last decision |
| Tests | 52 C# (51 pass; the year-long test fails, which is known and accepted), 15 Python (all pass) |
| Verification | A rule-mode 365-day run (`world_20261010_214907_8458cb`) passed all 12 `check_world` checks |
| Banking | Legacy loans only: 5% interest, due in 60 days, approved by the bank agent. The full banking design is on `labor-market-potential-bank`, on hold |
| Solana, Databricks, AMD | Not implemented. Solana is now a "mega stretch" goal. The Neon and Databricks plan is noted but not started |

## 2. What's been done since the design doc

| When | Commit or branch | What |
|---|---|---|
| Oct 10 | `fd42879`, `162944a` (main lineage) | Agents can pay each other (`pay`) and buy goods on demand (`buy_goods`); fixes to the economic loop. Merged to main |
| Oct 10 | `da090c0` (main) | Economy write-up added to `faultline-full-design.md` |
| Oct 10 | `41bb2d3` | Daily ticks, labor market, entrepreneurs, startups and closures, health and clinics, doctor visits, weekly scheduler |
| Oct 10 | — | Kennedy's live run `70b725` analyzed: slump, 21–55% unemployment, no startups, closures or job switches. Eight divergences from reality identified |
| Oct 10 | `eabf0bd` | Redesign: quality of life, entertainment sector, 2–3 firms per sector, dividends, proportional prices, overtime by consent, approval that follows quality of life, decision expiry, weighted seller choice. Prompt shrink (speed option A). Calibration items #1, #2, #4 and #5 vetoed; #3 kept |
| Oct 10 | `labor-market-potential-bank` | Full banking module (deposits, insurance, owner-run bank, loans), on hold until the current live run is analyzed |
| Oct 10 | `119e478` | Refactor: slimmer checkpoints, one serialization per save, per-country agent index (daily economy 4–7× faster), unused fields removed, rules text built from engine constants. Same simulated results, checked by fingerprint |

The live run now under way on the Spark uses `ricky/labor-market` and is being run by another teammate.

---

## 3. Differences by design section

Each row gives what the design doc says, what the code does now, and why.

### 3.1 Status (§1)

| Design doc | Now | Why |
|---|---|---|
| "Passes 30 C# tests and 14 Python tests" | 52 C# (51 pass) and 15 Python | Tests were added for payments, labor, quality of life, prices and the year-long loop. The failing year test is the accepted calibration gap (§3.13) |
| "Eight resident occupations, four produced goods" | Nine occupations plus the entrepreneur role; four goods plus two services (entertainment, clinic visits) | The labor-market redesign added service workers and services |
| Coordinator uses "wall-clock decision cadences" | Weekly cohort with a weekly barrier | Decisions have to land before the weekly payroll they affect (§3.9) |
| Evidence: rule smoke `419bf6` (120 ticks), interrupted live run `6d3812` | Plus Kennedy's live run `70b725` (420 days) and rule-mode 365-day runs passing all 12 checks | Later runs. None of these is saved under `docs/faultline/evidence` yet |

### 3.2 Population (§3)

| Design doc | Now | Why |
|---|---|---|
| 7 institutions per country (government, 4 businesses, bank, media), 70 in total | 15 per country (government, bank, media, 12 firms), 150 in total | A single firm per sector behaved like a crowd of competitors, which is unrealistic, so most sectors now have 2–3 competing firms. Entertainment, clinic and freight were added |
| 93 residents per country at 1,000 agents | 95 per country at 1,100 agents (950 residents), including 12 entrepreneur owners in each market country | Owned firms are steered by their entrepreneur, a resident with their own household needs |
| Dockworkers work for the tools business; doctors for the medicine business | Dockworkers work for a freight firm; doctors work at a clinic | Freight fees needed a real recipient, and health care became a service |
| No service workers | Service workers staff entertainment venues | Households needed something other than tools to spend extra income on |
| Job shares not specified | Per 100 workers: farmer 21, energy 6, factory 25, service 20, dock 2, teacher 9, soldier 6, unemployed 9 | Sized so each sector's sales roughly cover its payroll |

### 3.3 Agent state and observations (§4)

| Design doc | Now | Why |
|---|---|---|
| Residents track employer, effort, target stock, wellbeing and support | Also salary, days unemployed, health and threshold, quality of life, overtime days, purchases last week, owned business and former occupation | Needed for the labor market, health and quality of life |
| Every role sees the full set of public information | Each role sees only what its choices depend on. Only governments get the full country block, neighbors and budget. Banks and newsrooms get no market block. Memories are cut to the last 3, at most 100 characters each | Prompt shrink (option A): from about 2,300 tokens per call to about 680, plus a cached prefix of about 950 |
| Effort 2 means "extra effort raises wages and output but reduces wellbeing" | Effort 2 means "accept overtime if the employer offers it". Overtime is a quarter more hours at 1.5× pay, and costs free time and some health | Ricky's decision: overtime is offered by the employer and the worker must consent |
| Routine life lists work, wages, restocking, welfare, cargo and loans | Adds health wear and illness, automatic doctor visits, discretionary shopping, hiring and firing, price reviews, startups and closures | New subsystems |

### 3.4 Governments (§5)

| Design doc | Now | Why |
|---|---|---|
| "Socialist democracy" | Named "Social democracy" | Renamed in code; no reason was recorded. It sits alongside Ricky's direction to treat the state-run profile simply as a "planned eco" |
| Public ownership 0 / 50 / 100 / 0 / 25% drives business-treasury transfers | Only the planned economy owns firms: the state takes cash above 4 weeks of payroll and covers shortfalls. The percentage is still shown to governments but changes nothing | Partial public ownership couldn't coexist with entrepreneur-owned firms |
| Welfare per resident per cycle: 0 / 40 / 60 / 0 / 20 | Per citizen per week: 0 / 100 / 150 / 0 / 50 | Converted to weekly amounts when ticks became days. The calibration change to welfare (#1) was vetoed, so these stay |
| No unemployment benefit | Weekly benefit: market democracy 300, social democracy 400, others 0. It's a government policy option (0/300/600) | Unemployment exists now. Run `70b725` showed the benefit decides whether jobless people survive |
| Policy delays of 3/2/1/0/1 "ticks" | The same numbers, but a tick is now a day, so delays are 0–3 days. Elections every 100 days, crisis on days 40–90, proposals expire after 60 days, border closures last 15 days | These durations weren't rescaled when a tick became a day. Not decided yet whether they should be |
| Government actions: tax, welfare, services, investment, borders, agreements, conflict, peace | Adds unemployment benefit, sales tax and government payments | Added with the labor market and agent payments |

### 3.5 Economy (§6)

| Design doc | Now | Why |
|---|---|---|
| Base prices: food 50, energy 20, tools 100, medicine 150 | Food 40, energy 25, tools 100, medicine 120, entertainment 60, clinic visit 450 | Recalibrated against the wage table so each sector's sales cover its payroll |
| Agents pick a price around base (0.8×/1×/1.2×) | Owners move price ±10% per turn; autopilot moves it up to 20% a week in proportion to shortage or glut; clamped to 0.5–3× base. Planned economies fix prices | Prices stuck at the floor in `70b725`. Faster, proportional response was agreed (calibration item #3, kept) |
| "Every five logical ticks" economic cycle | Daily: production, consumption, health, shopping. Weekly: labor market, payroll, taxes, welfare, public care, discretionary spending, metrics | Moved to one tick = one day so health, shopping and pay have realistic rhythms |
| Wages come from employer cash; no labor market | Fixed salaries from employer offers and a pay table; hiring, layoffs, job switches for 10% more, a 14-day wait before taking unskilled work, 10% pay cuts, closure after 4 weeks of distress | Ricky: "make all salaries fixed". The labor market was the core of the redesign |
| No entrepreneurs | One owner per business, paid from profit, with dividends above 8 weeks of payroll. Citizens can start firms when the market shows an opportunity, launching 10% below the cheapest competitor. Failed owners become citizens again | Ricky's design: an entrepreneur is 1:1 with a business, a new business needs an edge, and different prices are allowed |
| Households consume 1 food and 1 energy per cycle | Per day, bought automatically up to the target stock, spread across sellers with weight `(cheapest/price)^4`. A rotating buyer order is kept | Daily ticks, and a realistic spread of customers so the cheapest seller doesn't take everyone |
| No discretionary spending | Weekly tools and entertainment purchases while the next unit adds more quality of life than the savings it uses up | Divergence #1 from `70b725`: demand was fixed, so higher productivity only meant more unemployment |
| Wellbeing as a simple meter | Quality of life (0–100) scored from outcomes with diminishing returns: needs 0.35, health 0.20, comfort 0.20, security 0.15, leisure 0.10. Moves a quarter of the way to its target each day | Ricky's decision: score quality of life from outcomes with diminishing returns |
| Public services = treasury-funded medicine purchases | Public healthcare buys clinic visits for the sickest residents from a weekly budget per resident | Health became a modelled system |
| No health model | Health wears 1 a day, with a 1% daily chance of illness costing 30. Unmet needs and overtime cost health. Clinic visits restore 40. Health scales output from 25% to 100% | Sick workers and doctor demand were needed for a realistic economy |
| No education effect | Teachers' pay and health set an education factor (0.7–1.0) that multiplies output | Gives public payroll a real economic effect |
| No sales or profit tax | 5% sales tax inside household prices, 20% profit tax | Closes the money loop between businesses and government |
| Migration: immediate move with a fee | Unchanged: a 100 fee, and the migrant arrives unemployed | — |

### 3.6 Trade and conflict (§7)

| Design doc | Now | Why |
|---|---|---|
| "Banks temporarily receive the transport-service fee" | Freight fees go to the domestic freight firm, or to the government if there isn't one | Dockworkers needed a real employer, and banks shouldn't collect shipping fees |
| Conflict has "funded" upfront and weekly costs | 5,000 upfront and 1,000 a week, paid **into the country's bank** | Leftover from the old design. Flagged as an open question: where should war spending go? |
| Imports: one active import per good | Up to two shipments in flight per good; three sizes (½×, 1× and 2× population) | Gives importers more realistic choices |

### 3.7 Banks and credit (§8)

| Design doc | Now on this branch | On `labor-market-potential-bank` (on hold) |
|---|---|---|
| Firms request working capital; a bank agent approves; 5% interest, due in 60 ticks | Same, but the term is now 60 days | The bank is an owner-run firm with bankers. Deposits are swept in weekly and earn interest. Insurance depends on government type. Loans are repaid weekly over 26 weeks, and 3 missed payments close the firm. Startup loans for would-be entrepreneurs. Runs happen when residents choose to withdraw |

Banking was deliberately left out of this branch ("first be able to have a realistic economy pre banking") and kept on its own branch until the live run is analyzed. The design decisions are recorded in `faultline-economy-status.md` §4.8.

### 3.8 Architecture (§10)

| Design doc | Now | Why |
|---|---|---|
| The worker sends the observation and the menu as JSON | The rules text sits in the system prompt so vLLM can cache it; menus are compact numbered lines; the reason is capped at 100 characters | Speed option A, approved |
| Gpt-oss-120b on one Spark | Unchanged; smaller models are being evaluated by the team (option C) | — |

### 3.9 Scheduling and capacity (§11)

| Design doc | Now | Why |
|---|---|---|
| Institutions every ~120 s and residents every ~600 s of wall-clock time; a 3/10 institution and 7/10 resident slot pattern; never-decided agents first | Weekly cohort: every government, entrepreneur, bank, newsroom and state firm each week, plus a rotating eighth of citizens. Barrier before the weekly payroll | Decisions must land before the weekly payroll and reviews they affect, and turns should line up with the in-game week rather than wall time |
| Failed or rejected turns are retried after 30 s | No retry; the agent decides again at its next weekly turn | Follows from the weekly cohort |
| Demand ≈ 2.30 decisions/s from cadences | About 260 calls a week. A year was about 2.9 h before option A; the estimate after it is 80–90 min | The estimate uses a cost model fitted to Spark benchmarks (about 0.21 s per call plus 0.2 ms per prompt token). Not yet measured on a full live year |
| Population planning formula | No longer applies: demand scales with the number of institutions plus one eighth of citizens per week | — |
| Fewer calls not discussed | Reducing who decides (option B) was considered and **declined** | Ricky: every agent keeps its turns |

### 3.10 Persistence (§12)

| Design doc | Now | Why |
|---|---|---|
| The checkpoint keeps "each agent's latest observed decision" | Each agent keeps a slim copy of its last decision, without the observation; full observations are kept only for the 256 most recent decisions | The full copies were half the checkpoint (2.6 of 5.3 MB after 4 weeks). Ricky approved keeping only the last 256 |
| Serialization not specified | Each save converts the world to JSON once. Local saves read only the previous revision number | Refactor `119e478` |
| Event types | Adds `business_started`, `business_closed`, `labor_market`, `payment_sent` and `goods_purchased`. `economy_cycle` is now weekly | New systems |

### 3.11 Observer (§13)

| Design doc | Now | Why |
|---|---|---|
| The agent view shows the last decision | Reads it from the recent-decisions window, and says when the observation is no longer kept | Follows from the slim checkpoint |
| Browser layout unverified | Still unverified since the labor-market changes | — |

### 3.12 Experiments (§14)

| Design doc | Now | Why |
|---|---|---|
| Matched experiments rotating government profiles | The rotation exists (`--government-rotation 0–4`) but no comparison has been run | Comes after the live run. It's the planned basis for the Databricks work |

### 3.13 Verification (§18)

| Design doc | Now | Why |
|---|---|---|
| "Production and employment rules need longer-run tests" | A year-long test exists and **fails**: Cinder's government and business cash falls to 2% of its starting level | Calibration fixes #1, #2, #4 and #5 were vetoed until the live run has been analyzed |
| No year-long outcome reported | With no agent decisions, 8 of 10 countries reach health 0 within a year. With random rule-mode choices, all 10 do | The traced causes: medicine runs out with one researcher, clinic staff are fired and rehired in a loop, a closed sector never reopens, and welfare drains demand. Random rule choices also include strikes and skipped doctor visits |

### 3.14 Team (§17)

| Design doc | Now | Why |
|---|---|---|
| "Jayden is currently implementing alone" | Ricky built the labor market, quality of life, refactors and banking while Jayden was away; Kennedy runs live runs; the Spark run is with another teammate | Availability changed during the hackathon |
| Ricky: Neon, accounting, credit, Solana | Ricky: economy engine, Neon and credit. Solana pushed to a mega stretch goal | Ricky's decision |

---

## 4. In the code but not in the design doc

- Labor market and pay table (`SocietyLabor.cs`).
- Entrepreneurs, startups, closures, dividends and the rule that refills empty sectors.
- Competing firms with weighted seller choice.
- Quality of life, discretionary spending, and the entertainment sector.
- Health, illness, clinics, doctor visits and public healthcare.
- Overtime by consent.
- Sales tax, profit tax, unemployment benefit and the education factor.
- Agent-to-agent payments and on-demand purchases.
- A domestic freight firm.
- Weekly cohort scheduling with expiry.
- Shared cached rules prompt and compact menus.
- Per-country agent index and slim checkpoints.

## 5. In the design doc but not in the code

| Item | Status |
|---|---|
| Full banking (deposits, interest, runs, insurance) | Built on `labor-market-potential-bank`, on hold |
| Controlled government-rotation experiments | Not run |
| Databricks analysis | Not started; plan in `faultline-economy-status.md` and the Neon/Databricks discussion |
| Solana devnet settlement | Mega stretch goal |
| Resuming a run after restart | Not implemented; saved runs can only be viewed |
| Leader identities, succession, coups | Future work, as the doc already says |
| Live capacity qualification | No sustained full-coverage live run recorded yet |
| Product quality (premium vs cheap) | Deferred by Ricky |

## 6. Where main's `faultline-full-design.md` economy section is also out of date

That section matches `41bb2d3`. Since then:

| Its text | Now |
|---|---|
| One firm per sector | 2–3 per sector, plus entertainment |
| Prices move 5% a week | Proportional, up to 20% a week |
| Households spend 60% of cash above 1,000 on tools each week | Spending decided by quality-of-life gain, on tools and entertainment |
| Buy "from the cheapest local seller" | Spread across sellers by price |
| Effort 2 costs 1 health and 1 wellbeing per day | Effort 2 means consenting to employer-offered overtime |
| Job shares: factory 46, energy 8, … | Factory 25, service 20, … |
| "Stable economy" calibration evidence | Since the veto, the year collapses in 8 of 10 countries |

## 7. Suggested updates to the design doc

1. Rewrite §3, §5 and §6 from §3.2–3.5 above, or point them at a single maintained economy section.
2. Replace the §11 cadence model with the weekly cohort and the measured cost model.
3. Mark §8 banking as legacy and link the bank branch's design.
4. Update §1 status, test counts and evidence with run `70b725` and the 365-day rule-mode runs.
5. Decide whether durations counted in ticks (policy delays, elections, crisis, expiry) should be rescaled now that a tick is a day.
6. Update §15 sponsor tracks: Solana is a mega stretch goal; Neon and Databricks are the focus.
