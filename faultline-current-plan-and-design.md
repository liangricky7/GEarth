# Faultline — Current Plan and Design

**Updated:** October 10, 2026 · America/New_York
**Team:** Jayden Hong, Kennedy Nguyen, Ricky Liang, Ahmad Hijazi
**Primary target:** Best AI
**Delivery deadline:** Sunday, October 11, 2026, 10:00 a.m. EDT

This is the current, self-contained project specification. It supersedes earlier population and two-Spark deployment targets for the next implementation stage. The earlier full design remains available separately as planning history.

The project is an **observable world simulation**. Individual agents live in fictional countries, work, buy necessities, run businesses, finance trade, govern, negotiate, and experience the consequences of their decisions. The audience observes and investigates; it does not need game controls.

The current implementation direction uses **one DGX Spark**, with the largest population that can sustain a useful, measured decision cadence. An absolute maximum agent count has not been established.

---

## 1. Current state: verified, drafted, and planned

The working application and the new world expansion are at different stages. The source additions below are not evidence that the expanded simulation has already run successfully. Repository preparation subsequently fixed two compile errors and passed 15 C# tests plus 5 Python tests, including a 1,000-agent creation check and 120 deterministic economy ticks. This is still not an integrated 1,000-agent model run.

| Component | Current status |
|---|---|
| Stage 1 C# engine and read API | Implemented and tested |
| Stage 1 Python inference worker | Implemented and tested with the existing Spark model |
| Stage 1 observer | Served at `http://127.0.0.1:5080/`; API and assets tested; browser layout inspection remains unverified |
| Neon operational storage | Working checkpoint and event persistence over HTTPS; saved-run reads verified |
| Stage 1 population | 50 individual agents across five countries |
| Stage 1 actions | Hold, local purchase, publication, and medicine route planning |
| Stage 1 logistics | Route planning only; no actual cargo movement in the verified run |
| Spark capacity sweep | Completed from 4 through 128 concurrent requests using representative world-role prompts |
| Expanded world data model | Draft source added for countries, government profiles, occupations, relationships, policies, cargo, credit, and metrics |
| Expanded economy and logistics | Draft source added for production, wages, taxes, welfare, household purchases, services, escrow, shipments, and loan settlement |
| Expanded government and agent actions | Draft source added; compiles with basic core tests; integration and broader behavior tests still required |
| Expanded worker protocol | Draft candidate-index response path added; not yet verified in an integrated world run |
| Expanded coordinator and observer | Still need integration and implementation |
| Sustained large-population coverage | Not yet measured |
| Government comparisons | Experiment design only; no outcome claims established |
| Solana, Databricks, AMD | Planned or conditional; no working integration claimed |

### Last verified live run

Run ID: `stage1_20261010_114011_c648d2`

- 50 agents; 16 ticks.
- 64 model attempts: 63 accepted, one stale-dependency rejection, zero inference fallbacks.
- 49 of 50 agents had an accepted model decision; all 50 generated a decision.
- 123 recorded events, verified against Neon persistence.
- Total cash conserved at 1,237,000 minor units.
- Median model latency: 6.20 seconds. P95: 8.03 seconds.
- A scripted border closure changed the medicine route plan from 8 ticks / 500 minor units to 12 ticks / 1,700 minor units.
- 13 C# tests and 5 Python tests passed for that Stage 1 revision.

The run also exposed limitations: successful purchases selected food but not energy, and a government publication generalized its own empty inventory into an unsupported country-wide claim. The new design supplies public country aggregates and constrains factual publications.

The currently served process still shows that completed Stage 1 run. New world source changes have not replaced it. The Stage 1 test result does not certify the newer draft code.

## 2. Product and demonstration

### Project premise

**What happens when many individual AI agents live under different institutions in a resource-constrained world?**

Faultline makes that question visible through decisions and consequences:

1. Countries have different resource endowments and government institutions.
2. Residents and institutions have individual goals, traits, observations, and memories.
3. Businesses produce goods, hire workers, set bounded prices, and import scarce supplies.
4. Governments choose funded policies and negotiate with other countries.
5. Trade travels through a graph of routes that can close or become unavailable during conflict.
6. Households experience changing income, access to necessities, wellbeing, and political support.
7. The observer explains which decision and institutional rule caused a change.

### Core viewing experience

The viewer should be able to:

- See the world’s countries, active routes, moving cargo, agreements, and conflicts.
- Select a country to inspect its government, treasury, production, shortages, and public support.
- Select an agent to inspect occupation, goals, traits, resources, recent decisions, and memories.
- Follow a shipment from buyer demand through dispatch, route changes, arrival, and payment.
- Follow a loan from request through funding and repayment or default.
- Compare country outcomes over time while seeing their different starting conditions.
- See actual model coverage, pending requests, decision age, failures, and fallback counts.

The first expanded viewer can use a clear two-dimensional world map. A Three.js globe is a later presentation improvement and should not delay a functioning economy.

### Three-minute demonstration outline

1. Introduce the world, population, one-Spark inference, and individual decision coverage.
2. Inspect one household and one government to show different observations and constraints.
3. Follow a resource shortage into an import, a shipment, and a policy response.
4. Show how a border or diplomatic change alters the available route and economic outcome.
5. Compare measured country outcomes, explain institutional assumptions, and show the saved evidence.

Do not script a favorable model decision and present it as spontaneous. Scenario shocks may be scripted, but their source must be visible.

## 3. World geography and population

### Fictional countries on Earth-like geography

The expanded draft has ten countries placed across a schematic global map. These are fictional societies, not simulations of named real countries.

| Country | Initial institutional profile | Broad resource pattern |
|---|---|---|
| Aster | Market democracy | Strong food production; lower energy productivity |
| Beryl | Socialist democracy | Strong food production; energy scarcity exposure |
| Cinder | Single-party planned economy | Balanced food and stronger energy production |
| Dune | Personal dictatorship | Lower food and strong energy production |
| Ember | Absolute monarchy | Moderate food and energy production |
| Fjord | Market democracy | Second geography for the profile |
| Grove | Socialist democracy | Second geography for the profile |
| Haven | Single-party planned economy | Second geography for the profile |
| Ionia | Personal dictatorship | Second geography for the profile |
| Jade | Absolute monarchy | Second geography for the profile |

The draft resource multipliers repeat across the two sets of five countries. That initial assignment is useful for demonstration but **does not isolate government effects**. Controlled experiments must rotate government profiles across fixed geography and resources.

The initial transport graph connects nearby countries and adds links to ensure global connectivity. Directed edges have travel time, shipping cost, risk score, availability, and a controlling country.

### Population composition

Each country initially has seven institutional agents:

- One government.
- Four businesses: food, energy, tools, and medicine.
- One bank.
- One media organization.

Ten countries therefore have **70 institutional agents**. The remaining population consists of individual residents.

For a provisional 1,000-agent run, that means 70 institutions and 930 residents, initially 93 residents per country. Population creation rounds down to an equal allocation across ten countries.

### Resident occupations

The current draft assigns these occupations:

| Occupation | Initial employment relationship |
|---|---|
| Farmer | Food business |
| Energy worker | Energy business |
| Factory worker | Tools business |
| Doctor | Medicine business |
| Teacher | Government |
| Soldier | Government |
| Dockworker | Tools business in the first simplified economy |
| Researcher | Medicine business in the first simplified economy |

Occupations affect employer and production participation. They also shape individual goals. The draft does not yet contain separate education systems, battlefield units, research trees, or detailed port labor. Those roles must not be advertised as fully developed subsystems.

## 4. Individual agent design

### Persistent identity and state

Every agent has its own:

- ID, role, occupation, country, and location.
- Goal and seeded personality traits.
- Cash, inventory, and relevant business or household settings.
- Recent memories and decision history.
- Last attempted and accepted decision times.
- Accepted model-decision count.

Residents additionally track employer, work effort, target household stock, wellbeing, and expressed support for leadership. Businesses track specialty and price.

### Different preferences

Traits are reproducibly sampled from the scenario seed and agent ID:

- **Risk tolerance:** willingness to accept uncertainty.
- **Patience:** tolerance for delayed benefits.
- **Generosity:** weight placed on other agents’ wellbeing.
- **Trust:** willingness to rely on available reports.

These are preferences presented to the model. They do not guarantee distinct behavior. Behavioral differences must be demonstrated through recorded decisions and, if time permits, a controlled trait or memory ablation.

Government institutions and leader personality are separate. A government label does not automatically make its leader aggressive, competent, generous, or corrupt.

### Local information

The observation contains the agent’s own state plus relevant public information:

- Public country stock, shortages, treasury, policy, and institutional rules.
- Relevant relationships and pending diplomatic offers.
- Available import offers and route costs for businesses.
- Requested credit for the relevant bank or borrower.
- A small number of recent public events.

Agents do not receive every other agent’s private balance, memory, or hidden preference.

### Decision protocol

The expanded worker asks the model to return a compact choice:

```json
{"choice": 2, "reason": "Import energy before the local shortage reduces production."}
```

`choice` is the zero-based index of an engine-generated candidate. The worker maps it to the exact action and parameters. The authoritative engine rechecks the current role, budget, resources, and relevant preconditions before applying it.

This reduces output tokens and prevents the model from inventing prices, account IDs, quantities, or action parameters outside the offered choices.

A model explanation is an attributed rationale, not proof that its factual reasoning is correct.

### Routine life between decisions

Model calls choose intentions and standing policies. The engine executes routine consequences between turns:

- Work and production.
- Wages and taxes.
- Household restocking and consumption.
- Funded welfare and public services.
- Cargo movement and loan maturity.

For example, a household can choose a target stock of eight units, after which routine purchases try to maintain that target while respecting cash and available stock. Every individual remains separately represented; routine purchases do not require a fresh model call for every unit.

The observer must distinguish model-selected decisions from rule-executed consequences.

## 5. Government institutions

Political authority and economic policy are separate dimensions. The five profiles combine them into explicit fictional configurations for the demonstration.

### Draft defaults

| Profile | Policy delay | Authority rule | Public ownership parameter | Initial wage tax | Initial welfare per resident/cycle |
|---|---:|---|---:|---:|---:|
| Market democracy | 3 ticks | Synthetic approval threshold of 45% | 0% | 15% | 0 minor units |
| Socialist democracy | 2 ticks | Synthetic approval threshold of 45% | 50% | 30% | 40 minor units |
| Single-party planned economy | 1 tick | Central executive authority | 100% | 25% | 60 minor units |
| Personal dictatorship | 0 ticks | Bank liquidity proxy for elite support | 0% | 10% | 0 minor units |
| Absolute monarchy | 1 tick | At least two sufficiently funded businesses as a council proxy | 25% | 20% | 20 minor units |

These values are tunable simulation assumptions. They are not empirical estimates about political systems.

In the current draft, the public-ownership parameter controls transfers between businesses and treasury. It is a simplified ownership proxy. A complete centrally planned allocation system has not been implemented; the single-party profile still shares much of the common market engine.

### Policy lifecycle

1. Government observes public conditions and available actions.
2. Model selects a legal proposal.
3. Engine records the proposal and institutional delay.
4. At the effective tick, the engine checks authority and funding again.
5. The policy takes effect or receives an explicit blocked event.
6. Subsequent economic cycles reveal its effects.

Only one pending policy per country is intended at a time in the first expanded version.

### Government action vocabulary

- Change tax rate.
- Change welfare amount.
- Change public-service budget.
- Fund a productivity investment.
- Open or temporarily close a controlled border.
- Propose a trade agreement.
- Accept or reject an incoming agreement.
- Declare abstract economic conflict when tension and funding conditions permit.
- Offer or accept peace.

Approval mechanisms are engine rules in this version. There are no separately thinking parliament or council members counted in the population.

### Public support and leadership

The draft approval metric combines expressed resident support and average wellbeing, with equal weights. This is a declared synthetic formula.

Elected profiles hold a simplified review every 100 ticks. Approval below 55% causes a leadership-term change and resampling of leader traits. Country resources and obligations persist.

Separate leader identities, rich campaigning, hereditary succession, coups, and detailed constitutional processes remain future work. The draft monarchy does not yet implement a succession event.

## 6. Economy and public needs

### Goods and currency

The first expanded economy uses four goods:

- Food.
- Energy.
- Tools.
- Medicine.

Money is **simulated WLD**, represented as integer minor units. It is not currently SOL or an on-chain token.

Initial base prices are 50, 20, 100, and 150 minor units respectively. Business decisions can choose bounded prices around the base. This is a simplified price-setting mechanism, not a full order-book or market-clearing model.

### Economic cycle

Every five logical ticks:

1. Apply the declared public-ownership transfer rules.
2. Pay wages from employer balances, capped by available cash.
3. Transfer wage taxes into the treasury.
4. Pay affordable welfare evenly across residents.
5. Produce goods using workforce effort, wellbeing, inputs, resources, and productivity.
6. Fund available medicine-based public services from the treasury.
7. Execute household restocking under each household’s standing policy.
8. Consume food and energy; record unmet needs.
9. Apply any conflict costs and inventory losses.
10. Settle matured loans and update country metrics.

### Production and labor

Food and energy output depend on worker effort, wellbeing, country resource multipliers, and funded productivity improvements. Other production also uses tracked inputs such as energy and tools.

Residents can choose normal work, extra effort, or a strike. Extra effort raises potential wages and output but reduces wellbeing. A strike stops that worker’s wage and production contribution.

Employers cannot pay money they do not have. Production and employment rules need longer-run tests to ensure the economy does not collapse solely from poor calibration.

### Household needs

Households consume one food and one energy unit per economic cycle when available. A missing unit contributes to unmet needs and reduces wellbeing.

Routine purchases are constrained by available money, local stock, and the selected target stock. Buyer ordering rotates across cycles to reduce persistent first-buyer advantage.

### Public services

The current draft uses treasury-funded medicine purchases and consumption as a simple public-service mechanism. Service spending is capped by the available treasury. The mechanism is intentionally simpler than a health system and needs calibration before outcome comparisons.

### Migration

A resident can choose an eligible destination and pay a declared migration cost. Country, location, and employer change while personal cash, goods, and identity persist.

The first version abstracts relocation as an immediate state change. It does not yet simulate passenger travel, housing, citizenship, or migration quotas.

## 7. Trade, routes, and settlement

### Imports

A business can import an available good from a foreign business when:

- The seller has stock.
- The buyer can fund goods, shipping, and tariff.
- The countries are not at war.
- An allowed route exists.
- The buyer does not already have an active import for that good under the first-version limit.

### Shipment lifecycle

1. Remove the quantity from seller inventory.
2. Move the goods price from buyer cash into shipment escrow.
3. Transfer shipping fees and applicable tariffs to declared recipients.
4. Create cargo with a path, location, progress, and delivery deadline.
5. Advance along actual graph edges.
6. If a future edge becomes unavailable, attempt a new permitted path.
7. On arrival, add inventory to the buyer and release escrow to the seller.
8. On deadline failure, refund goods escrow and count abandoned cargo as lost; shipping fees remain spent.

The draft treats shipping charges as prepaid and does not model a separate carrier fleet. Banks temporarily receive the transport-service fee in this simplified ledger. A dedicated logistics agent can replace that abstraction later.

### Pathfinding

Use Dijkstra’s algorithm with declared edge weights:

- Fastest: travel ticks.
- Cheapest: route cost.
- Cautious: travel time plus a risk penalty.

The search excludes closed edges and disallowed conflict boundaries. Route selection and actual realized delivery are separate measurements.

### Trade agreements

An agreement requires an offer and acceptance. Accepted trade agreements remove the draft import tariff between the pair and reduce tension. Successful deliveries also reduce tension slightly.

No agreement should be treated as accepted merely because one government proposed it.

### Abstract conflict

Conflict is modeled through economic consequences:

- An upfront funded cost.
- Recurring funded upkeep.
- Blocked bilateral trade and affected route access.
- Explicit inventory destruction.
- Opportunities to negotiate peace.

The current draft does not include tactical combat, territorial conquest, military pathfinding, or a realistic war model. Initial tensions and a resource-access dispute create opportunities for disagreement, but a war is not guaranteed to occur in every model run.

## 8. Banks and credit

Businesses can request working-capital loans when their cash falls below the declared threshold. A bank agent chooses whether to approve a request.

On approval:

- Principal moves from bank cash to borrower cash.
- A loan records principal, repayment amount, due tick, borrower, and lender.
- No money is created.

The first draft uses a fixed 5% interest amount and a 60-tick term after funding. On maturity, available borrower cash is transferred up to the amount due. The loan becomes repaid or defaulted, with any unpaid amount recorded explicitly.

This first credit model has no deposits, collateral, renegotiation, seniority, or realistic credit underwriting. It supports a basic financial-exposure demonstration and provides a foundation for the BNY story.

## 9. Accounting and validation

The C# engine owns all state changes. The model and Python worker propose actions; neither directly edits balances or world state.

### Cash invariant

```text
sum(agent cash) + sum(active shipment escrow) = initial world cash
```

Taxes, wages, welfare, loans, shipping fees, and tariffs are transfers. They do not mint money.

### Goods invariant

For each good:

```text
inventory held by agents + cargo in transit
  = initial stock + cumulative production
    - cumulative consumption - cumulative recorded loss
```

Goods cannot silently disappear on a failed route or appear on delivery twice.

### Decision checks

Validate:

- Run, request, and actor identity.
- Role eligibility and exact offered action.
- Evidence IDs and bounded explanation length.
- Current money, inventory, authority, and relationship constraints.
- Deadline and observation age.
- Duplicate application.

A broad state-version change should not reject an unrelated action unnecessarily. Current preconditions must still be checked before spending or moving goods.

### Known integration work

The draft needs targeted review of stale public reports, changing candidate lists during inference, in-flight shipment behavior at border changes, and duplicate handling after history retention. These are test requirements, not verified guarantees yet.

## 10. One-Spark architecture

```text
Browser observer
      |
      | read-only snapshots, events, agent profiles
      v
C# authoritative world API and coordinator
      |                           |
      | bounded decision jobs     | checkpoints and ordered events
      v                           v
Python inference worker          Neon PostgreSQL over HTTPS
      |
      | existing OpenAI-compatible model endpoint
      v
One DGX Spark / vLLM / openai/gpt-oss-120b
```

### Existing deployment

- The local machine runs the application, coordinator, and Python worker.
- Tailscale and an SSH tunnel provide access to the existing Spark inference service.
- The Spark supplies shared model inference for many individual agent identities.
- One model is shared; each agent has separate state and observations rather than its own copy of the model weights.
- The second Spark and AMD hardware are outside this stage’s required deployment.

**Netplan must not be modified.** No network reconfiguration is part of the plan.

### Runtime responsibilities

| Component | Responsibility |
|---|---|
| C# core | World state, candidate generation, legality, accounting, economy, diplomacy, route and shipment effects |
| C# coordinator | Clock, scheduling, bounded pending work, result application, checkpoint publication |
| Python worker | Prompt construction, model request, compact response validation, explicit fallback result |
| Neon | Durable operational checkpoints and ordered event history |
| Observer | Display committed state and attributed decisions; no independent accounting |

## 11. Measured Spark capacity and population target

### Completed short sweep

Model: `openai/gpt-oss-120b`, using the existing vLLM service. Each setting used two waves of representative role prompts with the compact choice protocol. Temperature was zero, reasoning effort low, and output budget 512 tokens.

| Concurrent requests | Valid / attempted | Valid decisions/second | P95 response time |
|---:|---:|---:|---:|
| 4 | 8 / 8 | 0.79 | 6.43 s |
| 8 | 16 / 16 | 1.11 | 8.88 s |
| 16 | 32 / 32 | 1.35 | 15.37 s |
| 32 | 64 / 64 | 1.99 | 21.45 s |
| 48 | 96 / 96 | 2.65 | 22.87 s |
| 64 | 127 / 128 | 3.15 | 24.00 s |
| 96 | 192 / 192 | 3.09 | 39.12 s |
| 128 | 256 / 256 | 3.18 | 48.42 s |

Across these short sweeps, 791 of 792 responses passed the benchmark’s validation. This measures response validity, not decision quality or acceptance against a changing world.

Increasing concurrency from 96 to 128 produced only about 3% more throughput while increasing P95 latency by about 24%. The **provisional ceiling for the next integrated test is 96 concurrent requests**, with 48 or 64 as candidates if latency or semantic rejection is better at lower concurrency. A final operating point has not been selected.

These measurements exclude the world coordinator, persistence, full evolving observations, and live action-precondition rejection. They are not a sustained capacity guarantee.

### Population depends on decision cadence

There is no useful single hardware maximum without specifying how frequently agents think. A million stored identities that rarely receive a turn would not demonstrate a million active AI agents.

Provisional operating target:

- 1,000 total agents.
- 70 institutions, each eligible roughly every two minutes.
- 930 residents, each eligible roughly every ten minutes.
- Routine world activity continues between model turns.
- Population increases only after integrated coverage and latency checks pass.

The proposed inference demand is:

```text
70 / 120 seconds + 930 / 600 seconds
  = approximately 2.13 decisions per second
```

That leaves about 31% below the measured 96-request short-sweep rate, before accounting for integrated overhead. It is a **capacity hypothesis**, not a completed 1,000-agent result.

A rough planning formula is:

```text
population budget = institution count
  + floor((measured sustainable rate × usable fraction
           - institution count / institution interval)
          × resident interval)
```

Using 75% of the 96-request short-sweep rate suggests approximately 1,100 total agents under these intervals. The next meaningful scale step is therefore **1,000, then approximately 1,100 if sustained tests pass**. Longer decision intervals could permit a larger population, but that tradeoff must be shown explicitly.

### Scheduler design to implement

- Keep at most one in-flight request per agent.
- Bound global concurrent requests and pending work.
- Apply completed results without waiting for the slowest request in an entire batch.
- Give institutions a faster cadence while preserving resident fairness.
- Prioritize agents that have never had a model turn and agents whose last attempt is oldest.
- Reserve limited capacity for urgent events without starving ordinary residents.
- Record attempted, accepted, rejected, timed-out, and fallback decisions separately.
- Show actual decision age and achieved cadence rather than only a configured interval.
- Drain or explicitly cancel outstanding requests when ending a run.

The draft state contains provisional tick-based defaults. The proposed wall-clock cadences above still need to be wired into the coordinator and verified against actual tick duration.

### Capacity acceptance gate

A population is qualified only after an integrated run demonstrates:

- Every agent gets a turn within the declared coverage window, or missed coverage is reported.
- Pending work remains bounded.
- Decision age does not keep growing under steady demand.
- Accepted model-decision rate is adequate after current-state validation.
- P95 latency remains within the configured deadline.
- Accounting and persistence checks pass throughout.

## 12. Persistence, events, and contracts

### Existing storage

Stage 1 uses Neon tables for run checkpoints and event envelopes. SQL is sent through Neon’s HTTPS endpoint because direct PostgreSQL connectivity was unavailable in the current environment. A local-file option is also available.

Credentials belong in the ignored `.env.local` file and must never appear in documentation, event payloads, exported evidence, or public logs. The Neon CLI is not required for the working application path.

### Expanded storage work

The Stage 1 approach copies and serializes the whole world, including retained history. That must be revised before long, large-population runs.

Target behavior:

- Keep a bounded hot window of decisions, observations, and events in memory.
- Preserve complete ordered events durably outside the hot checkpoint.
- Persist new events incrementally and publish only committed state.
- Retain per-agent lifetime counters even when detailed decisions leave the hot window.
- Support old Stage 1 run reads.
- Distinguish saved-run inspection from true animated replay and interrupted-run resumption.

The draft currently bounds retained decisions and memories, but storage integration and historical event paging still need work.

### Contract versioning

Stage 1 action and event schemas remain version 1. The expanded world uses additional actions and event types and needs version 2 contracts and fixtures before the integration can be considered complete.

Expanded action families include household policy, work, support, migration, pricing, imports, loans, tax, welfare, public services, investment, borders, agreements, conflict, and peace.

Expanded events include economy cycles, policy proposals and outcomes, diplomatic offers and responses, resource crises, elections, shipments, loan funding and settlement, public reports, and conflict costs.

Every event should retain run ID, event ID, sequence, logical tick, source, affected entity IDs, causal references, and a typed payload.

## 13. Observer and measurements

### World view

- Country locations and government labels.
- Open and closed routes.
- Cargo progress and route changes.
- Trade agreements, tension, and active conflict.
- Resource crisis indicators.

### Country comparison view

- Resident population and occupation mix.
- Treasury, wage tax, funded welfare, and public-service spending.
- Production and available market stock.
- Needs fulfillment and cumulative unmet needs.
- Public support and wellbeing.
- Completed trade, active imports, and lost cargo.
- Loan exposure and defaults.
- Household cash inequality over time.

### Agent view

- Identity, occupation, employer, goal, and traits.
- Own cash and goods.
- Standing policy and wellbeing.
- Last decision, reason, result, and memory.
- Actual number of accepted model decisions and decision age.

### Compute view

- Population versus unique accepted model coverage.
- In-flight requests and configured capacity.
- Model latency and accepted throughput.
- Due agents and oldest waiting age.
- Fallbacks and rejection reasons.

Large populations require paginated or filtered agent lists. The browser should not redraw thousands of full agent profiles on every poll.

### Metric definitions

| Metric | Definition |
|---|---|
| Model coverage | Unique agents with at least one accepted model decision / spawned agents |
| Needs met | Fulfilled food and energy needs / required food and energy needs for the period |
| Unmet needs | Missing units summed over residents and consumption periods |
| Household cash Gini | Gini coefficient over resident cash balances; excludes institutions, inventory, and credit claims |
| Trade value | Delivered goods value; country participation totals count a shipment for both trading countries |
| Shipment completion | Delivered shipments or units / all dispatched shipments or units, with the selected denominator declared |
| Loan default rate | Defaulted matured loans / all matured loans; N/A when no loans matured |
| Policy response | Proposal-to-effective or blocked ticks, plus wall-clock time where available |
| Conflict cost | Funded transfers plus separately reported inventory losses; no invented monetary valuation of human harm |
| Decision freshness | Time or ticks since each agent’s last accepted model decision |

## 14. Government-effect experiments

A visual difference between two countries is not sufficient evidence that government caused it.

### Matched experiment

For each tested seed:

1. Hold geography, initial resources, population, occupations, traits, shock timing, and model configuration fixed.
2. Rotate institutional profiles across the same countries.
3. Keep the action vocabulary and declared policy parameters stable within the comparison.
4. Save all runs, including failed and incomplete runs.
5. Compare shortages, delivery outcomes, treasury use, inequality, support, and response time.

Report actual sample sizes, individual outcomes, and spread. Identical seeds make initial conditions reproducible; they do not guarantee identical LLM trajectories.

### Separate AI evaluation

Compare the same engine under a declared heuristic policy and the LLM policy. A separate memory-ablation experiment can test whether memory changes decisions or outcomes.

Do not tune prompts against selected favorable evaluation outcomes and then present the same runs as an independent test.

### Interpretation

The result describes the configured fictional simulation. It does not establish which real-world government is best or predict actual wars or economies.

## 15. Sponsor-track plan

The supplied judging rubric is reference material. A design match is not confirmed eligibility or proof of implementation.

| Track | Intended contribution | Evidence still needed or current limit |
|---|---|---|
| **Best AI** | Individual autonomous decisions with memories, information boundaries, and consequential world effects | Expanded integrated run, large-population coverage, and clear decision-to-outcome evidence |
| **Hugging Face** | Meaningful use of an open checkpoint for agent inference | Exact checkpoint revision/license and final reproducible serving evidence; existing model inference already works |
| **Neon** | Operational state and ordered event persistence | Stage 1 is verified; expanded persistence, recovery, and history behavior need verification |
| **Full Sail** | Educational simulation using graph pathfinding and disruption-driven rerouting | Actual expanded cargo routing, visible route tradeoffs, and an educational debrief |
| **BNY** | Trade disruption linked to working capital, bank exposure, repayment, and liquidity | Integrated financial workflow and useful exposure inspection; a currency label alone is insufficient |
| **Databricks** | Ingest and prepare actual run data, then compare government or intervention outcomes | Real Databricks execution, typed/deduplicated data, computed report, and measured sample counts |
| **Solana** | Meaningful on-chain settlement supporting the simulated economy | Actual devnet transactions and verified receipts; current WLD and escrow are internal only |
| **AMD / ROCm** | A meaningful inference or analysis workload running on supported AMD hardware | Allocation, compatible runtime, and measured execution; access is not yet confirmed |
| **Figma / Adobe / overall execution** | Clear, polished, accessible observer and presentation | Actual interface and submission work; no automatic qualification claim |

The core world does not depend on AMD, Solana, or Databricks being available. These integrations should attach to working simulation data and workflows.

### Solana extension

Keep authoritative simulation accounting separate from asynchronous chain receipts. Use a settlement outbox with idempotent receipt application. A devnet escrow demonstration should show funding, release, and a separate refund case. Do not describe internal escrow as blockchain settlement.

### Databricks extension

Export run manifests, events, country metrics, shipments, and loan outcomes with schema version, run IDs, sequence ranges, row counts, hashes, and incomplete-run flags. In Databricks, type and deduplicate records, join causes to outcomes, and produce a cross-run report. A local chart or a raw log upload alone does not establish this integration.

## 16. Implementation plan from the current state

### Step 1 — Finish the expanded core

- Complete review of the draft society files; compilation now passes.
- Add version 2 action/event contracts.
- Resolve stale-report and current-precondition handling.
- Verify money, goods, cargo, loans, policy delays, and migration invariants.
- Run deterministic multi-cycle scenarios that expose shortages, imports, conflict, and loan maturity.

**Exit:** expanded rules pass meaningful tests without model inference.

### Step 2 — Integrate scheduling and storage

- Add the asynchronous bounded coordinator.
- Make concurrency, population, cadence, deadline, and run duration configurable.
- Keep one in-flight request per agent.
- Implement bounded hot history and durable complete event history.
- Keep Stage 1 saved runs readable.

**Exit:** a rule-driven expanded world persists and serves correctly, with no unbounded queue or history growth.

### Step 3 — Qualify one-Spark population

- Benchmark actual engine-generated observations, including longer government and business candidate lists.
- Compare 48, 64, and 96 concurrent requests under the integrated workload.
- Start at a manageable population, then test the provisional 1,000-agent target.
- Measure full coverage, accepted throughput, latency, stale rejections, and accounting.
- Increase toward approximately 1,100 only if sustained results support it; otherwise lower population or lengthen cadence and report that choice.

**Exit:** the selected population and cadence have recorded integrated evidence.

### Step 4 — Make the world observable

- Add country and government panels.
- Show shipments, agreements, tension, and conflict.
- Add searchable/paginated agents and actual decision-age indicators.
- Show financial exposure and policy consequences.
- Preserve explicit LIVE, RULE, completed, and saved-run labels.

**Exit:** a viewer can follow a real decision through its consequence without reading server logs.

### Step 5 — Run comparisons and attach integrations

- Execute matched government experiments and a declared heuristic baseline.
- Add Databricks analysis if access is available.
- Add actual Solana devnet settlement if the financial core is stable.
- Add AMD only after allocation and a compatible workload are confirmed.

**Exit:** each claimed track has concrete execution evidence.

### Step 6 — Freeze and present

- Preserve a measured complete run and its configuration.
- Finish the runbook and failure/recovery instructions.
- Record a concise demonstration.
- Verify all submission links and claims.
- Finish the submission package before the Sunday 10:00 a.m. EDT deadline, with upload buffer.

## 17. Team ownership

Jayden is currently implementing alone. The following assignments are the handoff plan when teammates join; they are based on the supplied resumes and are not active delegated work.

| Person | Primary ownership |
|---|---|
| **Jayden** | Authoritative C# engine, world rules, coordinator, contracts, routing, and integration |
| **Kennedy** | Observer/map or globe, visualization, profiling, Spark deployment and recovery |
| **Ahmad** | Python worker, prompts, inference benchmarks, scheduling measurements, evaluation, Databricks |
| **Ricky** | Neon persistence, accounting and credit review, Solana settlement, financial projections |

Jayden and Kennedy have SSH access to the Sparks. Other teammates can work through documented APIs and reproducible local fixtures. No remote credentials or resume contact details belong in the project documentation.

## 18. Verification checklist

### Core behavior

- [ ] Deterministic initial population and trait generation.
- [ ] Cash conservation across wages, taxes, welfare, lending, escrow, fees, refunds, and migration.
- [ ] Goods conservation across production, consumption, cargo, arrival, and conflict losses.
- [ ] No negative money or inventory.
- [ ] Correct role permissions and current resource checks.
- [ ] Duplicate requests and repeated receipts cannot apply twice.
- [ ] Government approval delays, authority checks, and funding limits affect outcomes.
- [ ] Graph closures change routes or leave cargo explicitly blocked.
- [ ] Loan maturity produces repayment or a recorded default.

### Integrated operation

- [ ] One-Spark workload meets the declared population and decision cadence.
- [ ] Every agent’s model coverage is measured; fallbacks remain separate.
- [ ] Pending work and hot history remain bounded.
- [ ] Expanded checkpoints and complete events agree with the API.
- [ ] Old Stage 1 runs remain readable.
- [ ] Observer reflects committed state and provides usable agent filtering.
- [ ] Browser layout is visually verified when inspection is authorized.

### Evidence and claims

- [ ] Controlled comparisons rotate government profiles across fixed starting conditions.
- [ ] Failed/incomplete runs remain visible in analysis.
- [ ] Internal currency is labeled simulated; on-chain claims require actual receipts.
- [ ] Every submitted sponsor integration has execution evidence.
- [ ] Netplan remains untouched.

These boxes are intentionally unchecked for the expanded world. Earlier Stage 1 checks remain historical evidence rather than completion of this new scope.

## 19. Repository and evidence map

Paths below are relative to the project directory so this document remains portable.

| Path | Purpose |
|---|---|
| `backend/Faultline.Core/World.cs` | Shared world and agent state |
| `backend/Faultline.Core/Engine.cs` | Verified Stage 1 engine |
| `backend/Faultline.Core/SocietyState.cs` | Draft expanded country, policy, cargo, credit, and metric state |
| `backend/Faultline.Core/SocietyFactory.cs` | Draft ten-country population and route construction |
| `backend/Faultline.Core/SocietyEconomy.cs` | Draft economic cycles and conservation checks |
| `backend/Faultline.Core/SocietyDiplomacy.cs` | Draft government rules, policies, agreements, elections, and conflict |
| `backend/Faultline.Core/SocietyTrade.cs` | Draft physical shipments, paths, and internal escrow |
| `backend/Faultline.Core/SocietyActions.cs` | Draft role-specific available actions |
| `backend/Faultline.Core/SocietyEngine.cs` | Draft observations, validation, and decision effects |
| `backend/Faultline.Core/SocietyProjection.cs` | Expanded snapshot shape; observer integration pending |
| `backend/Faultline.Api/Coordinator.cs` | Existing Stage 1 coordinator; expanded integration pending |
| `agents/worker.py` | Inference worker and draft compact-choice protocol |
| `ops/run_stage1.py` | Existing Stage 1 launcher |
| `ops/check_stage1.py` | Existing Stage 1 verification script |
| `ops/benchmark_spark.py` | One-Spark concurrent request benchmark |
| `docs/faultline/evidence/stage1-smoke-20261010.json` | Verified small live run evidence |
| `docs/faultline/evidence/world-spark-benchmark.json` | 4, 8, 16, and 32 request sweep |
| `docs/faultline/evidence/world-spark-benchmark-high.json` | 48 and 64 request sweep |
| `docs/faultline/evidence/world-spark-benchmark-upper.json` | 96 and 128 request sweep |
| `faultline-full-design.md` | Earlier consolidated design and Stage 1 reference material |

The earlier document contains historical two-Spark, 500-agent, and milestone assumptions. For the next build, this document’s one-Spark scope, measured capacity discussion, and current status take precedence.
