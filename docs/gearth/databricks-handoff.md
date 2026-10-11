# Databricks handoff

For the developer taking over the Databricks challenge. Ricky Liang owns the Neon side and the simulation data; you own everything inside Databricks. Section 6 is the contract between you: what you need from Ricky and what Ricky needs from you. Settle it first.

**Deadline:** Sunday, October 11, 2026, 10:00 a.m. EDT. The team's feature freeze is 6:00 a.m. Sunday.

**Background:** read [the design](../../gearth-design.md), especially:
- §1, the product.
- §5, the economy.
- §8.4, the verbose history.
- §12.4, the Databricks plan.

---

## 1. What GEarth is, in one paragraph

GEarth is an observable AI society.
- **The world:** about 1,100 agents in ten fictional countries under five government types: market democracy, social democracy, planned economy, dictatorship and monarchy.
- **What agents do:** they work, shop, run businesses, pay taxes, trade, migrate, go to war and elect leaders.
- **How decisions are made:** a C# engine owns money, goods and time. LLMs (Qwen3-8B for institutions, Qwen3-4B for citizens) pick each agent's strategic choices from an engine-built menu, with a short reason.
- **Time:** one tick is one simulated day, and a run is usually a year (365 ticks).
- **The core question:** how do different government types handle the same world? Databricks is where we answer it across many runs.

## 2. What the Databricks track needs

Read the official challenge text yourself; the version in our notes is second-hand. Our reading of it is:
- **Not a log dump.** Databricks must prepare the data and compute something: a cross-run comparison with real sample counts and an actionable conclusion.
- **No hand-built dashboards over raw Neon.** A dashboard made directly from Neon data doesn't count as Databricks work.
- **Real usage.** Local MLflow alone isn't Databricks usage.
- **Viewable without a login.** Judges shouldn't need a Databricks login to see the result. Cache the final report (Ricky's write-back, §6), and keep the notebook and dashboard available for sponsor review.

**The pitch to aim for:** "Databricks turns hundreds of simulated societies into evidence about which institutions survive shocks, and why."

## 3. Data available

### 3.1 Verbose history files (best data, available now)

A run started with `--verbose` writes `.data/history/<run_id>.ndjson.gz` on the machine that ran it. Gzipped NDJSON reads directly with `spark.read.json`, or line by line in pandas. Two sample files are on Ricky's machine; ask for them.

**Line types:**
- **`header` (first line):**
  - `run_id`, `seed`, `mode` (`RULE` or `LIVE`), `initial_cash_minor`.
  - The column lists `agent_fields`, `nation_fields`, `flow_fields` and `shipment_fields`.
  - `countries`, with each country's `government_id`, latitude and longitude.
  - `locations`, `routes` and `government_profiles`.
- **One `day` line per tick (0…N),** with `tick`, `status` and:
  - **`agents`:** one row per agent:
    - `id`, `role`, `country`, `location`, `occupation`, `employer`
    - `cash`, `health`, `wellbeing`
    - `specialty`, `price`, `stock`, `staff`
  - **`nations`:** one row per country:
    - `id`, `government`, `residents`, `unemployed`
    - `approval`, `government_cash`, `gini`, `unmet_needs`
    - `wars`, `stock` (a map of good to units)
  - **`flows`:** money movements summed per day as `[from, to, kind, amount, count]`. There are 26 kinds, such as `wage`, `purchase`, `sales_tax`, `welfare`, `dividend`, `import` and `migration_fee`. IDs starting `cargo_` are shipments holding escrow.
  - **`shipments`:** cargo in transit.
  - **`events`:** every event since the previous line ([§3.3](#33-event-types-worth-knowing)).
- **`end` (last line):** anything after the last day, plus the final status.

**Two facts to rely on:**
- Each agent's daily cash change equals the sum of its flows; a test enforces this.
- Total cash plus escrow is constant.

**Size:** about 17 MB gzipped (99 MB raw) for a 1,100-agent year. A rule-mode year takes about 10 seconds to generate.

### 3.2 Neon (available now, JSONB)

Schema `gearth`:

| Table | Key | Contents |
|---|---|---|
| `stage1_runs` | `run_id` | `revision`, `state` (JSON, not JSONB, so key order survives: the whole world's latest checkpoint), `updated_at`. Includes old Stage 1 runs; filter with `state->'society' IS NOT NULL`. Use `json_each`/`json_array_elements` on it, not the `jsonb_` functions |
| `stage1_events` | `(run_id, seq)` | `envelope` (JSONB event, schema in [`contracts/event.v2.schema.json`](contracts/event.v2.schema.json)) |
| `run_notes` | `run_id` | `through_seq`, `notes` (JSONB automatic run report) |

**Useful fields in `stage1_runs.state`:**
- **Run:** `mode`, `seed`, `status`, `tick`.
- **Society:** `society.government_rotation`.
- **Per country (`society.nations.<id>`), the latest values of:**
  - `government_id`, `approval`, `gini_cash`, `unemployed`
  - `taxes_collected_minor`, `welfare_paid_minor`, `wages_paid_minor`
  - `businesses_started`, `businesses_closed`
  - `history`

**Checkpoints are overwritten,** so the run row only has the final state. History over time comes from events (weekly per-country numbers) or from the verbose files (daily).

```sql
SELECT run_id, state->>'mode' AS mode, (state->>'seed')::int AS seed,
       (state->'society'->>'government_rotation')::int AS rotation,
       state->>'status' AS status, (state->>'tick')::int AS tick
FROM gearth.stage1_runs WHERE state->'society' IS NOT NULL;

SELECT run_id, seq, (envelope->>'tick')::int AS tick, envelope->>'event_type' AS type,
       envelope->>'source' AS source, envelope->'entity_ids'->>0 AS entity, envelope->'payload' AS payload
FROM gearth.stage1_events;
```

**Neon holds** the team's live runs, including:
- Kennedy's labor-market run `world_20261010_183127_70b725`.
- The earlier 1,100-agent run `world_20261010_125750_6d3812`, on an older engine.

Ask Ricky which runs are official.

### 3.3 Event types worth knowing

| Event | Grain | Payload highlights |
|---|---|---|
| `economy_cycle` | Country per week | `wages_paid_minor`, `sales_tax_minor`, `profit_tax_minor`, `government_income_minor`, `government_spending_minor`, `unmet_needs_last_cycle`, `fulfilled_needs_last_cycle`, `doctor_visits`, `public_doctor_visits`, `education_factor`, `unemployed`, `production_units` |
| `labor_market` | Country per week | `hires`, `layoffs`, `job_switches`, `pay_cuts`, `unemployed`, `dividends_minor`, `overtime_offers`, `active_businesses`, `wage_offers` |
| `action_accepted` / `action_rejected` | Per decision | Agent = `entity_ids[0]`; `action.action_type`, `action.params`, `action.reason` (the model's explanation), `model`, `latency_ms`, `code`. The envelope `source` is `model`, `rule` or `rule_fallback` |
| `policy_proposed` / `policy_enacted` / `policy_blocked` | Per government policy | Action, parameters, delay, institution rule |
| `election_result` | Per election | `approval`, `replaced`, `mechanism` |
| `business_started` / `business_closed` | Per firm | Sector, owner, investment, reason, workers laid off, cash recovered |
| `agent_migrated` | Per migrant | `from`, `to`, occupation |
| `shipment_dispatched` / `_delivered` / `_failed` / `_rerouted` | Per shipment | Good, quantity, route plan, escrow |
| `conflict_started`, `conflict_costs`, `diplomatic_*`, `border_changed` | Per country pair | War and diplomacy |

**Cause links:** every event has `cause_ids`, which link it to the events that caused it, so causal chains can be traced.

**Units:** money is in minor units (100 = 1 WLD).

### 3.4 Planned Neon tables (Ricky, on request)

Ricky can move the verbose history into Neon as relational tables, so you read clean tables instead of JSON:
- `runs`: one row per run, with seed, rotation, mode, population, models and status.
- `nation_days`.
- `agent_days`.
- `flows`.

Agree the columns with Ricky before he builds them (§6). Until then, use §3.1 and §3.2.

### 3.5 Generating more runs

| Run type | How to get it | What it's good for | Cost |
|---|---|---|---|
| Rule mode | Anyone with the repo, sequentially | Calibration, sensitivity and baselines | Free; about 10 s per year |
| Live (LLM) | Shared GPUs, coordinated through Jayden and Kennedy | The AI story | Scarce: a year takes hours |

To make rule-mode runs:
```bash
.venv/bin/python ops/run_stage1.py --world --storage local --verbose --ticks 365 --interval 0 --exit-on-complete --seed 101 --government-rotation 0
```

**What the run flags control:**
- `--government-rotation 0–4` rotates the five government types over fixed geography. Five rotations of one seed give each country each government type exactly once.
- `--seed` changes initial conditions.

**Rule-mode decisions are a deterministic hash of agent and day, not reasoning.** Rule runs are a baseline; the AI story needs the live runs.

Economic constants (welfare levels, startup cost and so on) are code constants, not flags. A parameter sweep needs Ricky to expose them first.

## 4. Getting data into Databricks

Pick one, in this order of preference:

1. **Lakehouse Federation to Neon.**
   - Create a PostgreSQL connection with `CREATE CONNECTION … TYPE postgresql` and a foreign catalog over Neon's database. Neon is then queried in place, which tells the Neon + Databricks story best.
   - Use Neon's **direct** host (without `-pooler`), port 5432, and SSL.
   - It needs a workspace and compute that support Unity Catalog federation. Confirm your workspace tier.
2. **JDBC from a notebook.** Use `spark.read.format("postgresql")` or JDBC with the same credentials, then write Delta tables.
3. **File upload.** Put `.ndjson.gz` files in a Unity Catalog Volume and read them with `spark.read.json`. This has no Neon dependency, so start here tonight if credentials aren't ready yet.

**Credentials:** keep them in a Databricks secret scope (`dbutils.secrets`). Never put them in notebooks, git, chat or screenshots.

**Suggested layering:**
- **Bronze:** raw lines and events.
- **Silver:**
  - typed `runs`, `nation_days`, `agent_days`, `flows` and `decisions`,
  - weekly `economy_cycle` and `labor_market` tables.
- **Gold:** per-run outcomes and per-government comparisons.

Deduplicate events on `(run_id, seq)`.

## 5. Features that win the track

Ranked by value for the effort. Do 1–3 at minimum, then add more as time allows.

| # | Feature | What it shows | Databricks pieces |
|---|---|---|---|
| 1 | **Government scorecard across rotation runs** | With country held fixed, which government type keeps health, employment, quality of life and budgets up, and which collapses first. Report means, spread and sample counts | Delta gold tables, Databricks SQL, AI/BI dashboard |
| 2 | **Collapse early warning** | Predict whether a country collapses by year end from its first 8 weeks (unemployment, government cash, unmet needs, doctor visits). Feature importance gives the actionable lesson, such as "a treasury below X by week 6 predicts collapse" | MLflow experiment tracking; a simple model (logistic regression or gradient boosting) |
| 3 | **AI decision audit** | Use an LLM to label every model reason (for example hoarding, price gouging, austerity, stimulus). Compare model vs. rule behavior and what agents chose in the weeks before a collapse | `ai_query` / AI Functions in SQL over `action_accepted` |
| 4 | **Genie space for judges** | Judges ask plain-English questions, such as "which government had the lowest unemployment in week 20?" | Genie over the gold tables |
| 5 | **Causal chains** | Follow `cause_ids` from a shock (border closure, war, policy) through failed shipments, shortages and closures to health outcomes | SQL recursive queries or GraphFrames |
| 6 | **Write-back to Neon** | Push findings into a Neon table so the live observer shows "Databricks insight" next to the simulation. This ties the Neon and Databricks tracks together | JDBC write, using the writer role from §6 |
| 7 | **Money-flow analysis** | Where money goes in each government type: wages vs. taxes vs. dividends vs. welfare, and how inequality evolves | Gold aggregates over `flows`; a Sankey-style dashboard |

**Rules for honest results:**
- **Freeze metric definitions before looking at results.** [Design §11.2](../../gearth-design.md#112-metrics-explicit-denominators) lists the denominators.
- **Keep failed and partial runs,** and flag them.
- **Say "in this simulator",** never claims about real governments.
- **Report LLM and rule runs separately.**
- **Show the model assignment;** it's a condition of the experiment.

**Demo flow, 30–45 seconds:**
1. The dashboard scorecard.
2. One early-warning finding.
3. One audited decision linked to its consequence.
4. The same insight showing up in the GEarth observer through the write-back.

## 6. Contract between you and Ricky

### You need from Ricky (Neon side)

| Item | Notes |
|---|---|
| Read-only Neon credentials | A dedicated role (for example `databricks_reader`) with `SELECT` on schema `gearth`. Host (direct, not pooled), database and user, delivered through a private channel only. Store them in a Databricks secret scope |
| Writer role for results | `INSERT`/`UPDATE` on one results table only (for example `gearth.analysis_results`), so Databricks can never touch simulation data |
| List of official runs | Which run IDs count, their mode (LIVE or RULE), seed, rotation and models. Ideally a `gearth.runs` table |
| The verbose history files | Uploaded or shared for the runs you analyze, until they are in Neon |
| Relational daily tables (optional) | `nation_days`, `agent_days`, `flows` and `runs` in Neon, built to the columns you request |
| Government rotation in the history header | The verbose header has the seed but not the rotation; until it's added, read `state->'society'->>'government_rotation'` from the checkpoint |
| Neon network settings | Whether IP allow-listing is on (it needs Databricks' egress IPs) |
| Neon branches | Each what-if timeline is a Neon branch with its own host (design 12.3). `gearth.timelines` on the default branch lists them (branch ID, parent run, fork day, change); `gearth.checkpoints` has each checkpoint's LSN. The run ID is the same on every branch, so the same SQL compares timelines. Ask Ricky for read access to branch endpoints |

### Ricky needs from you

| Item | Notes |
|---|---|
| Your data request | Which grain (daily or weekly), which fields, and which tables you want in Neon, as early as possible, so Ricky builds them once |
| Workspace details | Tier (Free Edition, trial or company) and whether federation, Genie and AI Functions are available; egress IPs if Neon allow-listing is on |
| Run plan | How many runs, which seeds and rotations, rule vs. live. Live runs must be booked with Jayden and Kennedy, because the GPUs are shared |
| Write-back schema | What the observer should display: for example `run_id`, `country`, `finding`, `metric`, `value`, `computed_at`, `notebook_url`. Ricky adds the table and the observer panel |
| A cached report | A small JSON or Markdown summary of final results, so the observer and the submission don't depend on a Databricks login |
| Links for submission | The notebook and dashboard URLs, plus a 2–3 sentence description of what Databricks computed |
| Timing | When your pipeline is stable, so new runs aren't generated mid-analysis |

### Rules for both

- **Credentials:** never in chat, git, Trello, screenshots or notebooks. Use secret scopes and `.env.local`.
- **Neon stays the operational source of truth.** Databricks reads from it and writes only to its results table.
- **Schema changes:** tell the other person before changing a table or column the other side depends on.

## 7. Suggested timeline (times in EDT)

| By | Milestone |
|---|---|
| Sat 11 p.m. | Contract in §6 agreed; credentials or files received; bronze tables loaded from at least one history file |
| Sun 2 a.m. | Silver tables; rule-mode rotation batch (5 rotations × a few seeds) ingested; feature 1 gold table |
| Sun 4 a.m. | Dashboard for feature 1; feature 2 or 3 working; write-back table populated |
| Sun 6 a.m. | Freeze: no new features; live-run data added if available |
| Sun 8:30 a.m. | Cached report, links and description handed to the team for submission |

**Fallback order if time runs short:** drop 7, then 5, then 4. Keep 1 and either 2 or 3, plus the write-back if Neon credentials are ready.
