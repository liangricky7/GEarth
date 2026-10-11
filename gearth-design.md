# GEarth design

The single design document for GEarth, merged on October 10, 2026 (America/New_York) from every design and status document in the repository, and checked against the code on `main` at `47e380b`. It describes what is built, what is planned, and why each major decision was made. Setup runbooks stay as separate operational guides; [section 15](#15-document-map) lists them along with the documents this one replaces.

**Team:** Jayden Hong, Kennedy Nguyen, Ricky Liang and Ahmad Hijazi.
**Event:** KnightHacks 2026.
**Deadline:** Sunday, October 11, 2026, 10:00 a.m. EDT.
**Primary track:** Best AI.

## Contents

1. [Product](#1-product)
2. [Status](#2-status)
3. [World, countries and governments](#3-world-countries-and-governments)
4. [Agents](#4-agents)
5. [Economy](#5-economy)
6. [Trade, routes, diplomacy and conflict](#6-trade-routes-diplomacy-and-conflict)
7. [Scheduling and compute](#7-scheduling-and-compute)
8. [Persistence, API, observer and history](#8-persistence-api-observer-and-history)
9. [Contracts and validation](#9-contracts-and-validation)
10. [Operation and verification](#10-operation-and-verification)
11. [Evaluation and sponsor tracks](#11-evaluation-and-sponsor-tracks)
12. [Deferred and planned designs](#12-deferred-and-planned-designs)
13. [Decision log](#13-decision-log)
14. [Known problems and open questions](#14-known-problems-and-open-questions)
15. [Document map](#15-document-map)

---

## 1. Product

GEarth is an observable AI society. Individual AI agents live in ten fictional countries. They work, shop, own businesses, govern, trade, borrow, communicate and respond to crises. Viewers inspect countries, agents, decisions and consequences; they do not control the world. Selecting an agent or event reveals what it knew, what it chose and why, and what the engine recorded as a result.

**Pitch:** "A living AI society where hundreds of autonomous agents govern, trade, negotiate, and respond to crises — and you can trace how individual decisions reshape the world."

**Audience:** curious visitors and students learning how decisions propagate through societies, logistics and finance. The app opens on a running or recorded world, with no sign-in, wallet or player faction. Viewing controls (selection, replay, camera) are allowed. Scenario creation, restarts and policy changes belong to operator scripts.

**Principles**
- **The engine owns reality.** The C# engine owns money, goods, time and state. Models choose bounded actions from an engine-built menu; they cannot create money, invent prices or routes, or read another agent's private state.
- **Every effect is traceable.** Every effect links to its cause events. Explanations are attributed model statements, not proof of motive.
- **Fiction, not prediction.** Countries and institutions are fictional configurations. Outcomes describe this simulator, not real political systems.
- **No faked results.** Fallbacks, rejections and failures stay visible. Rule fallbacks never count as model decisions, and a missed target is disclosed rather than hidden.

**Demonstration (target, three minutes)**
1. Show the population, the inference sources that are actually running, and real decision coverage.
2. Follow a disruption (border closure, shortage or conflict) and a response: a reroute, a policy change or a business decision. Inspect the information the agent had.
3. Show the economy's response: unemployment, prices, health, firm startups and closures, and the government budget.
4. Compare government types on the same geography (rotation runs), with the analysis if Databricks is ready.
5. Follow one citizen. State what is live, what is recorded, and what remains unvalidated.

Prepare a captured run, because live decisions differ between runs. Recorded runs are labeled REPLAY and synthetic fixtures FIXTURE.

---

## 2. Status

| Area | Built and verified | Not built or not verified |
|---|---|---|
| World engine | C#/.NET 10; 10 countries; 1,100 agents by default (software range 170–100,000); one tick = one day | No maximum population qualified for live inference |
| Economy | Labor market, fixed salaries, entrepreneurs, 13 competing firms in 8 sectors per country, quality of life with a steep health penalty, too sick to work, always-staffed clinics with capitation, pharmacy medicine, hunger penalty, automatic input and food imports, startup grants, food and tuition subsidies, school and graduates, planned-economy quotas set by a planner, tax steps, overtime by consent, wage/sales/profit tax, welfare, unemployment and sick benefit ([5.10](#510-public-services-batch)) | Live behavior of the new levers; product quality |
| Trade and politics | Physical shipments with escrow, freight firms, tariffs, routes; diplomacy, borders, conflict, migration, elections, five government profiles | Tick-based political durations not rescaled to days |
| Banking | Full bank (deposits, interest, insurance, bank runs) merged, but **temporarily paused** (`SocietyBank.Paused`) so the public-services batch can be measured alone | Re-enable by reverting the pause commit, then compare runs |
| Agents and prompts | Seeded traits and endowments; role-specific observations; compact numbered menus; `{choice, reason}` answers | Behavioral effect of traits and endowments not evaluated |
| Scheduling | Weekly cohorts (institutions weekly, citizens every 8 weeks); weekly barrier before payroll; bounded, cancellable queue | Sustained live throughput of the weekly system |
| Compute | Spark A: Qwen3-8B (institutions) + Qwen3-4B (citizens), verified; AMD Qwen3-4B, verified | Spark B; AMD's second model |
| Persistence | Local files or Neon over HTTPS; week-boundary checkpoints plus ordered events; saved-run inspection; Neon branching timelines (`ops/fork_run.py`) | Resuming an interrupted run in place |
| History for the renderer | `--verbose` writes a day-by-day state, money-flow and event file; served by the API | Kennedy's renderer consuming it |
| Observer | Read-only API and static observer; automatic run notes; permanent share address | Browser layout not visually inspected since the labor-market changes |
| Databricks | — | Not implemented (planned) |
| Solana | Design only | Mega stretch goal |

**Evidence so far**
- **Stage 1 (50 agents, 16 ticks, GPT-OSS-120B):**
  - 64 model decisions: 63 accepted, 1 rejected as stale, 0 fallbacks.
  - Cash conserved.
  - The medicine route detoured after a border closure.
  - Latency: 6.2 s median, 8.0 s p95.
- **First 1,100-agent live run (`world_20261010_125750_6d3812`, older engine):**
  - 1,881 accepted model decisions.
  - 977 of 1,100 identities covered.
  - 82 shipments delivered.
  - Cash and goods conserved.
  - Laptop sleep interrupted it, so capacity remains unqualified.
- **Kennedy's labor-market run (`world_20261010_183127_70b725`):**
  - Budgets and shortages improved, with needs 98–99% met in the democracies.
  - Then a slump set in: 21–55% unemployment, no startups or closures, and firms hoarding cash.
  - This drove the redesign in [13.4](#134-redesign-after-kennedys-run).
- **Rule-mode 365-day run before the public-services batch:** health reached 0 in 7 of 10 countries and both planned treasuries emptied.
- **The same run after the batch (in-process, banking paused):** all 10 countries keep average health 77–85 and quality of life 64–81, with exact conservation; unemployment 0–20% (highest in monarchies, which pay no benefit). `MoneyKeepsCirculatingForAFullYear` passes.
- **A stopped hybrid live run** used both Qwen models but accumulated HTTP-error fallbacks; the cause is still unexplained.

---

## 3. World, countries and governments

### 3.1 Geography

There are ten fictional countries: Aster, Beryl, Cinder, Dune, Ember, Fjord, Grove, Haven, Ionia and Jade.
- **Locations and routes:** each country has locations on a directed route graph. Each route has travel time, cost, risk, an open/closed state and a controlling country.
- **Resources:** food and energy multipliers differ by country.
- **Repeated geography:** geography and resources repeat across two sets of five countries, so each government profile appears twice on different land.

### 3.2 Government profiles

Political structure and economic policy are modeled as explicit rules, not personalities. A prompt saying "you are a democracy" does not create elections; the engine enforces authority, delays and budgets.

| Profile | Countries (rotation 0) | Authority rule | Policy delay | Wage tax | Weekly welfare | Weekly unemployment benefit | Ownership |
|---|---|---|---|---|---|---|---|
| Market democracy | Aster, Fjord | Elected; acts while approval ≥ 45% | 3 days | 15% | 0 | 300 | Private, entrepreneur-owned |
| Social democracy | Beryl, Grove | Elected; acts while approval ≥ 45% | 2 days | 30% | 100 | 400 | Private, entrepreneur-owned |
| Single-party planned economy | Cinder, Haven | Central executive | 1 day | 25% | 150 | 0 | State owns every firm |
| Personal dictatorship | Dune, Ionia | Acts while bank cash ≥ 100 × population (elite-support proxy) | 0 days | 10% | 0 | 0 | Private, entrepreneur-owned |
| Absolute monarchy | Ember, Jade | Acts while at least two businesses hold more than 1,000 | 1 day | 20% | 50 | 0 | Private, entrepreneur-owned |

**Defaults and changes**
- **Every country starts with:**
  - a 5% sales tax,
  - a 20% profit tax,
  - public healthcare of 50 per resident per week.
- **Who changes policy:** governments change these through their own decisions, subject to the policy delay and authority rule.
- **Units:** all amounts are minor units (100 = 1 simulated WLD).

**The planned economy** is the only profile with state control. The state:
- owns every firm,
- pays the public pay table,
- fixes prices at base,
- places every unemployed resident in a state firm,
- takes firm cash above four weeks of payroll and covers payroll shortfalls,
- allows no private startups.

**Labels:**
- The code calls the "socialist democracy" profile **social democracy**. It is a market economy with high taxes and benefits, and no reason was recorded for the rename.
- Ember and Jade are absolute monarchies. A constitutional monarchy would be a different configuration.
- No profile is given an automatic tendency to win, cooperate, attack or become corrupt.

**Controlled comparisons:**
- `--government-rotation 0–4` rotates profiles over fixed geography, so institutional effects can be separated from land and route advantages.
- Comparisons hold the model assignment, seed and scenario constant.

### 3.3 Institutions and population

Every country starts with 16 institutions: a government, a bank, a newsroom and 13 firms (including the state school). The smallest world is 170 agents.

| Sector | Firms | Occupation | Base price |
|---|---:|---|---:|
| Food | 3 | Farmer | 40 |
| Energy | 2 | Energy worker | 25 |
| Tools | 2 | Factory worker | 100 |
| Entertainment | 2 | Service worker | 60 per session |
| Medicine | 1 | Researcher | 120 |
| Clinic | 1 | Doctor | 200 per visit, plus a weekly public capitation |
| School | 1 (state-owned) | Teacher | 2,000 tuition per 4-week course |
| Freight | 1 | Dockworker | Shipment fee |

**Population at 1,100 agents:**
- 160 are institutions across the ten countries.
- 940 are residents, 94 per country (in planned economies one is the planner).
- In market countries, firm owners are resident entrepreneurs, not extra population. Planned-economy firms are state owned.

**Starting jobs (weights per 100 workers):**
- **Private sector:** farmer 21, energy worker 6, factory worker 25, service worker 24, dockworker 2.
- **Public:** teacher 5 (at the state school), soldier 6, and in planned economies one planner (2,500 a week).
- **Unemployed:** 9.
- Researchers and doctors are placed first.

**Firm records can grow:** new businesses add firm records during a run, and closed firms stay as `defunct` records.

**Two denominators:** population is not the same as the number of decision makers:
- **Decide for themselves:** entrepreneurs.
- **Act only through their owner:** owner-run firms.
- **Decide for themselves:** state firms.
- **Don't decide:** closed firms.

---

## 4. Agents

### 4.1 Identity and state

Every agent has:
- **Identity:** an ID, role, country, location, occupation, goal and seeded traits.
- **Holdings:** cash and inventory.
- **History:** memories (the last 5 are stored), decision counts, last attempt, and last accepted model decision.

Residents also track:
- **Work:** employer, salary, unemployment duration and overtime consent.
- **Health:** health, doctor threshold and household policy.
- **Wellbeing:** quality of life.
- **Business:** any owned business.

All identities share model weights. An agent is not a separately loaded model; its distinct state and observations make it an individual.

### 4.2 Endowments (`endowments_v1`)

Seeded profiles give residents unequal starting conditions. The same seed and resident ordinal reproduce them.

| Dimension | Distribution | Effect |
|---|---|---|
| Wealth | Clipped lognormal, 0.25–4 | Redistributes each country's existing starting cash budget (two weeks of occupation pay) without changing its total. Initial condition only |
| Productivity | Uniform 0.6–1.4 | Multiplies goods output |
| Bargaining | Uniform 0.75–1.25 | Metadata only; fixed salaries supersede the old revenue-share wage rule |
| Priorities | Five weights summing to 1: security, wealth, health, belonging, mobility | Shown to the model in observations; guides but does not force choices |

Voting and approval are not weighted by wealth. Profiles are paired by ordinal across countries to reduce distribution confounding.

### 4.3 Roles and decisions

| Role | Goal | Model decisions |
|---|---|---|
| Citizen | Keep their own quality of life high | Household stock target; work effort (0 strike, 1 normal, 2 accept offered overtime); doctor threshold (0/50/70/85); visit a doctor now (health below 70); enroll in school (an employee leaves their job); seek a better job (any raise for 28 days); express support; migrate (arrives unemployed); pay a local resident, government or employer; buy extra food or energy; start any business where residents feel a gap and the market shows one |
| Entrepreneur | Keep their one business profitable while meeting their own needs | Citizen choices plus, for the business: price ±10%, wage ±10% (applies to current staff), headcount −1/+1/+2, imports in three sizes |
| Government | Meet public needs with a funded budget | Income tax in steps of ±0.25/0.5/1 point (0–60%), sales tax (0/5/10%), weekly welfare (0/100/200), unemployment benefit (0/300/600), clinic capitation (0/50/150 per resident), productivity investment, and when they apply: startup grants for an empty essential sector, food subsidy (0/25/50/75%), tuition subsidy (0/50/100%), student stipend, graduate hiring bonus (0/100/200); borders, trade agreements, conflict and peace, payments; all through the profile's policy delay. Sees last week's budget, the last policy's effect, the treasury in weeks of spending, hunger and quality of life by part |
| Planner (planned economy) | Meet needs without shortages or waste | Every 4 weeks, one `set_plan` with a free weekly quota per sector (food, energy, tools, medicine, clinic visits, entertainment). Sees each sector's quota, sales, stock or queue, workers and capacity |
| State firm (planned economy) | Supply its sector | Imports; prices and wages are fixed by the state |
| Owned firm | — | No turns of its own; acts through its entrepreneur |
| Bank | Lend safely | Approve requested loans (legacy credit) |
| Newsroom | Inform the public | Publish an engine-generated country report |

Routine life between turns follows standing policies and engine rules:
- shopping, consumption and production,
- hiring, payroll and price drift.

### 4.4 Observations and prompts

**What an agent sees**
- **Contents:** relevant self, household or business, country, market, loan and public-event information.
- **Memories:** at most the last three, each truncated to 100 characters.
- **By role:**
  - Governments see their full country, their neighbors and last week's budget.
  - Banks and newsrooms don't get irrelevant market blocks.
- **Never shown:** other agents' private balances and memories.

**How the model answers**
- **Menu:** the engine builds the candidate menu, and the worker renders it as compact numbered options.
- **Answer:** the model returns `{choice, reason}`, with the reason asked to be at most 100 characters (the validator accepts up to 240). The choice maps to an exact action with parameters. An option can carry free integer fields (`{"min","max","default"}`, shown as `<integer MIN-MAX, now N>`); the model then adds `"values"` with an integer for each, and both the worker and the engine check the range. Rule-driven choices use the defaults.
- **Caching:** the shared rules text (`RulesNote`, built from the engine's constants) leads the system prompt, so the model server can cache it as a prefix.
- **Model settings:**
  - Qwen requests disable thinking.
  - GPT-OSS requests use low reasoning effort.
  - Temperature is 0 and output is capped at 512 tokens.

**Why compact prompts:** prompts were about 2,300 tokens per agent. Moving the rules into the cached prefix, trimming by role and compacting menus brought them to about 680 tokens plus a 950-token cached prefix. Before the scheduler and model changes, that cut an estimated live year from about 2.9 h to 80–90 min ([13.6](#136-speed)).

---

## 5. Economy

All amounts are integers. Cash plus shipment escrow, and goods held plus goods in transit, are conserved and asserted every day and after every applied decision. The ledger is internal, with no blockchain.

### 5.1 Time

- **1 tick = 1 simulated day.** A run defaults to 360 ticks; use `--ticks 365` for a year.
- **Daily:**
  - production,
  - household purchases and consumption of food and energy,
  - health wear and illness,
  - doctor visits,
  - quality-of-life updates.
- **Weekly (every 7th tick):**
  - labor matching, firm reviews and price reviews,
  - payroll and taxes, owner payments and dividends,
  - welfare, unemployment benefit, public healthcare and discretionary spending,
  - metrics.

### 5.2 Labor market

- **Weekly base salaries:**
  - farmer 1,000
  - service worker 1,100
  - factory worker 1,300
  - soldier 1,400
  - dockworker 1,450
  - teacher 1,750
  - energy worker 2,100
  - researcher 2,500
  - doctor 6,000

  - planner 2,500

  Occupations differ in pay from day one; school ([5.10](#510-public-services-batch)) adds a graduate's output boost.
- **Salaries are fixed agreements.** Employers set wage offers; the occupation table sets the differences between jobs.
- **Hiring:** firms hire toward a headcount target. They take unemployed workers first (graduates first), then workers who would gain at least 10% in pay (any raise while seeking a better job). Students are not hired.
- **Shortage pay:** in a shortage, farms, power plants, pharma and clinics raise wage offers as fast as prices may rise (up to 20% a week, to twice the table wage); other firms by 5% a week, to 1.5×.
- **Changing trades:**
  - After 14 days unemployed, anyone can take unskilled work.
  - Skilled workers (doctors, researchers) keep their trade and return when a matching job pays more.
  - Public workers stay in the public sector.
- **Distress and closure:**
  - A payroll shortfall triggers 10% pay cuts, then layoffs, and counts as a distress week.
  - Four distress weeks close a market firm.
  - Weekly review can refill an empty viable sector.
- **Overtime by consent:**
  - The employer offers overtime, and a worker on effort 2 accepts.
  - It means a quarter more hours at 1.5× hourly pay, costing health and leisure.
  - Effort 0 is a strike: no pay.

### 5.3 Entrepreneurs

- **One firm each:** an entrepreneur owns and works in exactly one firm.
- **Pay from profit:**
  - First, up to their former wage.
  - Then half of the remaining profit.
  - Then cash above an eight-week payroll buffer, as a dividend.
- **Starting a business:**
  - A citizen with savings can start a firm in any sector except banking and freight.
  - **Gap check:** the option appears only where residents feel the shortfall (the national quality-of-life part the sector serves is below 60% of its best: food and energy → needs, clinic and medicine → health, tools and entertainment → comfort, school → security) **and** the market shows it (no firm, high prices, empty shelves or customers turned away). An empty sector always counts.
  - A clinic, pharma or school needs someone qualified to work in it: the founder or a resident free to hire. Owners work in their firm only in a trade they could be hired for.
  - It costs 8 weeks of the sector's table wage for a goods firm and 4 weeks for a service.
  - The new firm launches 10% below the cheapest incumbent.
- **Failure:** if the firm closes, the owner returns to a citizen role and takes the remaining cash.

### 5.4 Prices and demand

- **Who sets prices:**
  - Owners move price by ±10% per decision.
  - Between owner turns, a weekly autopilot moves prices in proportion to shortage or glut, by up to 20% a week.
  - Prices are clamped to 0.5–3× base. Planned economies fix prices at base.
- **Where households buy:** demand is spread across stocked sellers with weight `(cheapest / price)^4`, so the cheapest seller does not take every buyer.

### 5.5 Households and quality of life

- **Daily needs:** residents consume one food and one energy a day and restock up to their household stock target.
- **Weekly extras:** they buy tools and entertainment while the next unit adds more quality of life than the savings it uses.
- **Quality of life (0–100):**
  - parts, with diminishing returns:
    - needs up to 35
    - health: `20 × h/100 − 50 × ((100 − h)/100)³`, so +20 at full health, about +13 at 70, +4 at 50, −11 at 30 and −50 at 0
    - comfort up to 20
    - security up to 15
    - free time up to 10
  - the total is clamped to 0–100; residents see their own parts, and countries publish the average of each part (0–1)
  - moves a quarter of the way toward its target each day.
- **Approval:** follows quality of life. A sharp fall or deep misery withdraws support from the leadership.

### 5.6 Health and public services

- **Health wear:** health falls 1 every 4 days. A 1% daily illness costs 30, and unmet needs and overtime add further costs. Acute illness is the main reason to see a doctor, about 6 visits per resident a year.
- **Clinics:** a clinic visit restores 40 and uses one medicine. Residents visit the cheapest clinic with a free doctor once below their threshold, or now with `visit_doctor`. With no visit possible they buy medicine at the pharmacy (+15).
- **Too sick to work:** below health 30 a resident produces nothing and is not paid for those days; sick pay equals the unemployment benefit where one is paid, and a clinic sees them free if they cannot pay.
- **Effect on work:** health scales output from 25% to 100%, and wellbeing adds a milder output factor.
- **Public healthcare:** the per-resident services budget is paid weekly to clinics as a capitation, shared by the doctors working, so a visit costs 200 instead of 450 and the clinic covers a doctor's pay.
- **Always staffed:** a clinic never lays off its last doctor; an idle essential firm takes back someone from the trade; with no clinic left, a doctor opens one on a startup grant.

### 5.7 Government finance

- **Income:** wage tax (by profile), sales tax (default 5%, inside the price), profit tax (20%), tariffs (5% without a trade agreement) and migration fees.
- **Spending:** public salaries (soldiers, the planner), weekly welfare, unemployment and sick benefit, clinic capitation, startup grants, food and tuition subsidies, the student stipend, hiring bonuses and investment.
- **State firms:** the state collects firm cash above four weeks of payroll and covers shortfalls, for every firm in a planned economy and for the school everywhere. Other displayed public-ownership percentages cause no transfers.

### 5.8 Legacy credit

Businesses can request a working-capital loan, and the bank agent approves it. Loans accrue 5% interest and mature after 60 days as repaid or defaulted. In practice almost no loans happen. The full bank design is in [12.1](#121-banking-built-on-hold).

### 5.9 Old tick durations (open)

These were never rescaled when a tick became a day:
- policy delays of 0–3 days,
- elections every 100 days,
- the scripted crisis on days 40–90,
- diplomatic proposals that expire after 60 days,
- border closures that last 15 days,
- loans that mature after 60 days.

Rescaling them is a design decision ([14](#14-known-problems-and-open-questions)).

### 5.10 Public services batch

Built on October 11, 2026 after the hosted live run `world_20261011_004205_8e1892`, in which health fell to 0 in every market country: a clinic laid off its only doctor, energy-poor countries lost their power plants and with them all food output, and governments flipped tax rates weekly while their treasuries grew. Banking is paused while this batch is measured alone.

- **Health and work:** decay, the health curve, too sick to work and pharmacy medicine ([5.5](#55-households-and-quality-of-life), [5.6](#56-health-and-public-services)).
- **Hunger:** a resident who missed their last meal produces 99% less until they eat again. Farm workers eat first from their employer's stock, paying cash or, when broke, owing it from the next wage. Governments see hungry and priced-out residents and the share of working days lost.
- **Essential sectors** (food, energy, clinic, medicine) never stay without workers: an idle firm takes back someone from the trade, and a sector still empty after 14 days gets a startup grant (in a planned economy the state opens a firm). Governments can also grant one themselves (`subsidize_startup`).
- **Imports:** a firm with no domestic supplier imports its input from the cheapest foreign seller, one shipment at a time; when a country's food shelves hold under three days of meals, farms import food that lands cheaper than their price (the planned state always). Run notes count automatic and agent-ordered shipments separately.
- **Food subsidy:** offered when at least 10% of residents went hungry with food on the shelves; the treasury pays 25, 50 or 75% of households' food and farms still receive the full price.
- **School:** a state-owned school in every country (private schools allowed through the gap check). A citizen who is not studying or graduated enrolls for a fixed 2,000 tuition; an employee leaves their job to do so. The course lasts 28 days without pay (a stipend if offered). Graduates produce 25% more (tunable) and are hired first. Each working teacher takes 5 students; the state school grows when classes fill and keeps its teachers when they don't. The old national education factor is gone.
- **Planned economies:** a planner sets each sector's weekly quota every four weeks with free numbers; state firms make or serve their share at fixed prices, so mistakes show as queues or unsold stock rather than as a built-in penalty. Without a model the rule default raises a quota 10% after shortages and lowers it 10% after gluts.
- **Taxes:** income tax moves in ±0.25/0.5/1-point steps; the government sees what its last policy did to the weekly budget.

---

## 6. Trade, routes, diplomacy and conflict

**Routes**
- **Pathfinding:** routes come from Dijkstra over the directed graph. Closed links and links without border permission are excluded before searching.
- **Objectives:**
  - fastest: travel ticks
  - cheapest: cost
  - cautious: travel ticks + 10 × risk
- **No route:**
  - An import can't be dispatched without a path.
  - A shipment whose next leg closes reroutes by the cautious objective, or waits where it is if no route exists.
  - At the 60-day deadline it fails and the buyer is refunded.
  - Shipments stop moving while the two countries are at war.
- **Animation:** follows real graph nodes, never a direct line between unrelated points.

**Imports**
- **Funding:** imports use funded internal escrow. The buyer's payment is held with the shipment, released to the seller on delivery, or refunded on failure (deadline 60 days).
- **Sizes and limits:** three sizes (half, one or two times population), with up to two shipments in flight per good per buyer.
- **Freight fee:**
  - The whole route's fee is charged at dispatch.
  - It goes to the buyer country's freight firm, which employs dockworkers, or to the government if there is none.
- **Tariff:** 5% without a trade agreement.

**Diplomacy and conflict**
- **Diplomacy:** governments propose and answer trade agreements and peace, and close borders.
- **Conflict:**
  - It is abstract: borders close, inventory is lost, and upkeep is paid. There is no territorial conquest.
  - Declaring conflict costs 5,000 up front plus 1,000 a week, currently paid into the domestic bank. That destination is an open question.
  - War features are a stretch goal.
- **Migration:** costs 100, paid to the destination government. Migrants arrive unemployed.
- **Elections:** follow the profile's authority rules.

---

## 7. Scheduling and compute

### 7.1 Weekly cohorts

- **Weekly turns:** every government, entrepreneur, bank, newsroom and state firm gets one turn each week.
- **Citizen rotation:** citizens are split deterministically into 8 cohorts, so each decides once every 8 weeks (about 12% a week).
- **No retries:** a failed turn is not retried immediately.
- **Dispatch:** ownership changes and closures are resolved when a job is dispatched.

**The coordinator's cycle**
1. Queue the week's cohort.
2. Admit bounded jobs. Each job observes the world at the moment it is dispatched.
3. Before the weekly payroll, drain the queue and every in-flight decision. This is the weekly barrier: decisions land before the payroll they affect.
4. Before completion, drain the final cohort.

**Expiry:** proposals older than 7 simulated days expire, and wall-clock request deadlines also apply.

**Capacity and cancellation:** the bounded `DecisionQueue`:
- excludes duplicate agents,
- reserves institution and citizen slots by role, so an institution backlog can't starve citizens,
- refills freed slots between ticks.

Cancellation discards unfinished results and commits a stopped checkpoint and notes.

**Weekly demand** is roughly `non-citizen decision makers + citizens / 8`: about 150 + 854/8 ≈ 257 calls a week at 1,100 agents. Faster compute shortens the barrier. A larger population slows simulated time rather than silently dropping turns.

### 7.2 Models

| Group | Model | Pinned revision | Roles |
|---|---|---|---|
| Institutions | `Qwen/Qwen3-8B` | `b968826d9c46dd6066d109eabc6255188de91218` | Governments, firms, banks, newsrooms |
| Citizens | `Qwen/Qwen3-4B` | `1cfa9a7208912126459214e8b04321603b3df60c` | Residents of every occupation, entrepreneurs' personal choices |

**Serving settings:**
- BF16 weights, no thinking, temperature 0.
- At most 512 output tokens and an 8,192-token context.
- Revisions are pinned in `ops/qwen-models.json`.

**Routing:** depends on role only (not occupation, wealth or government). A provider scoped to one group never spills into the other. The model assignment is recorded per decision and treated as an experimental condition.

**History:**
- The prototype used `openai/gpt-oss-120b` (MXFP4) on Spark A.
- On October 10 the team replaced it with a smaller Qwen pair to raise decision throughput.
- A Qwen3.8-27B proposal was rejected in favor of 8B/4B.

### 7.3 Compute sources

| Source | Status | Local concurrency |
|---|---|---:|
| Spark A, Qwen3-8B (institutions) | Verified, authenticated loopback port 8000, laptop tunnel 18001 | 4 |
| Spark A, Qwen3-4B (citizens) | Verified, loopback port 8001, laptop tunnel 18003 | 8 |
| AMD cloud (Radeon Pro W7900D, ROCm), Qwen3-4B | Verified, authenticated HTTPS, one generation at a time | 1 |
| Spark B (both models) | Fresh installer and Ken's Tailscale relay prepared; not verified | Disabled |
| AMD second model (8B) | Dual-model notebook prepared; not deployed | Disabled |

**Where things run:**
- **Jayden's laptop:** the one authoritative world, worker, observer and storage client.
- **The GPUs:** inference only.
- **Admission:** the sum of enabled provider capacities (13 now); `--capacity` can lower it.
- **Ceilings:** per backend 128, global 384. These are input limits, not measured capacity.

**Provider pool:**
- **Routing:** picks the provider with the lowest occupied fraction, rotating ties.
- **Failures:** a failed provider cools down for 30 seconds, and the job returns an explicit fallback.
- **Attribution:** each decision records the backend and model that served it.

**Security:**
- **No secrets in the repo:** model keys, URLs and the Neon connection string live in ignored `.env.local`.
- **Isolation:**
  - The worker never receives database credentials.
  - Browsers never call model servers.
  - Model HTTP clients ignore inherited proxies.
  - Remote HTTP is accepted only for an exactly pinned Tailscale host.

**Capacity is unqualified.** Old Spark benchmarks (GPT-OSS at 64 slots: about 3.15 decisions/s, with 128 slots adding nothing but latency) and the old wall-clock cadence formulas don't describe the weekly Qwen system. Neither a maximum population nor the duration of a full live year has been measured.

Setup details live in the runbooks listed in [section 15](#15-document-map).

---

## 8. Persistence, API, observer and history

### 8.1 Storage

- **Backends:** `--storage local` writes `.data/runs/`. `--storage neon` uses Neon Postgres over HTTPS, because a direct Postgres connection was refused from the laptop.
- **Neon tables:**
  - `gearth.stage1_runs`: the whole-world JSON checkpoint plus revision. The column is `json`, not `jsonb`: jsonb reorders object keys, and a forked timeline must reload agents and countries in the order the engine iterates them. Older tables are converted on startup.
  - `gearth.stage1_events`: one row per event.
  - `gearth.run_notes`.
  - `gearth.checkpoints`: the Neon LSN of each saved checkpoint, for branching (12.3).
  - `gearth.timelines`: the registry of forked timelines (12.3).
- **Checkpoints:**
  - Saved at each week boundary (after the week's decisions drain, before payroll: ticks 6, 13, 20, …), at the start and at the end, in one statement guarded by the expected revision. Nothing is in flight at a week boundary, so a run resumes from it exactly.
  - Identical repeated saves are idempotent.
  - Transient failures retry 3 times, then the run pauses. Neon storage never silently falls back to local.
- **Save cost and window:**
  - Each save serializes the world once, for both the store and the API's read-only copy.
  - The checkpoint keeps the last 512 events, the last 256 decisions, and each agent's last decision without its observation (a slim copy that roughly halved checkpoint size).
  - The full event log is stored separately.
- **Saved runs:** `--run-id` loads one for inspection. Forks resume a saved run on its own Neon branch ([12.3](#123-neon-branching-timelines)); resuming an interrupted run in place is not implemented.

### 8.2 Read API (all read-only)

| Endpoint | Returns |
|---|---|
| `GET /api/health` | Status (starting/ok/paused), storage, current run and tick |
| `GET /api/runs` | Saved run IDs and the current run |
| `GET /api/runs/{id}/snapshot` | Committed world: countries, locations, nations, government profiles, routes, relationships, policies, shipments, loans, agent summary, metrics, compute |
| `GET /api/runs/{id}/events?after_seq&through_seq&limit` | Ordered event pages, at most 500 |
| `GET /api/runs/{id}/agents?country&role&q&offset&limit` | Filtered agent summaries, at most 100 per page, with revision/sequence metadata, decision-maker status, endowments and model ages |
| `GET /api/runs/{id}/agents/{agentId}` | Agent state, last decision and recent decisions |
| `GET /api/runs/{id}/notes`, `/notes.md` | Automatic run observations as JSON or downloadable Markdown |
| `GET /api/runs/{id}/history` | Verbose runs only: the day-by-day history file ([8.4](#84-verbose-history-for-the-renderer)) |

### 8.3 Observer, notes and sharing

**Observer** (`backend/GEarth.Api/wwwroot`): a static read-only page.
- **What it shows:** countries, routes, events, agent search and profiles, compute and coverage.
- **Request handling:** requests are cancelled when the run or selection changes, so late replies can't mix runs.
- **Event formats:** renders both action shapes: strings from policy events and objects from decisions.
- **Missing observations:** when an observation is no longer in the 256-decision window, the page says so.

**Automatic run notes** are written for completed, stopped and paused runs. They cover:
- behavior by role and source,
- country outcomes and inequality,
- example attributed reasoning,
- model/backend counts and inference errors,
- event references.

They keep model, rule and fallback actions separate and avoid causal claims about real governments.

**Sharing:** the permanent address is configured in `GEARTH_SHARE_URL`; Jayden is handling its hosted URL rename. A supervisor reconnects its tunnel to the laptop, which must stay awake and online.

**Target observer (Kennedy):** a globe with route arcs and convoy motion, an event feed, an inspector, and Society/Logistics/Finance views. It separates "information available then" from "what the engine recorded", labels rumors as unverified, and supports keyboard access and reduced motion.

### 8.4 Verbose history for the renderer

`--verbose` (`GEARTH_VERBOSE=true`) writes `.data/history/<run_id>.ndjson.gz` for Kennedy's tick-by-tick renderer. It's served gzip-encoded by `/api/runs/{id}/history`, so a browser `fetch(...).text()` decompresses it automatically. The file is complete when the run ends.

**One JSON object per line:**
- **`header`:** run ID, seed, initial cash, countries, locations, routes, government profiles, and the column names for each row type.
- **One `day` line per tick (0 to N):**
  - **`agents`:** id, role, country, location, occupation, employer, cash, health, wellbeing, specialty, price, stock, staff.
  - **`nations`:** id, government, residents, unemployed, approval, government cash, Gini, unmet needs, wars, stock per good.
  - **`shipments`:** in-transit cargo with its current route edge.
  - **`flows`:** who paid whom, as `[from, to, kind, amount, count]`, summed per day.
  - **`events`:** every event since the previous line.
- **`end`:** anything after the last day, plus the final status.

**Flow kinds:**
- **Everyday:** purchase, wage, sales_tax, income_tax, welfare, unemployment_benefit.
- **Owners and firms:** profit_tax, owner_draw, dividend, state_surplus, state_subsidy, business_founding, closure_payout, liquidation_sale.
- **Trade:** shipping, tariff.
- **Agent actions and policy:** payment, migration_fee, loan, loan_repayment, public_investment, war_declaration, war_upkeep.
- **Escrow:** import_escrow, import, import_refund. The shipment's cargo ID stands in as the holder, so money visibly travels buyer → shipment → seller (or back to the buyer).

**Why daily state rather than logging every transaction:**
- **No accounting replay:** the renderer needs state. Logging transactions would force it to re-implement the accounting to know anyone's cash.
- **Size:** about 2–4 million events a year (over 1 GB) versus about 99 MB uncompressed (17 MB gzipped) for a 1,100-agent year.
- **Instant seeking:** any day can be read directly.
- **Exact by construction:** each agent's daily cash change equals its recorded flows, which a test asserts.

**Verified on a 365-day rule-mode run:**
- zero cash mismatches across all agents and days,
- cash plus escrow equals the initial total,
- all 20,816 events present with no duplicates.

---

## 9. Contracts and validation

**Contract files** ([`docs/gearth/contracts/`](docs/gearth/contracts/)):
- **`event.v2.schema.json`:** the event envelope:
  - `schema_version`, `event_id`, `run_id`, `tick`, `seq`, `event_type`, `source`, `entity_ids`, `cause_ids`, `payload`.
  - Sources: `scenario`, `model`, `rule`, `rule_fallback`, `external_receipt`.
- **`decision-choice.v2.schema.json`:** the model's `{choice, reason}` answer.
- **`action.schema.json` and `event.schema.json`:** the Stage 1 contracts.

Synthetic fixtures live in [`docs/gearth/fixtures/`](docs/gearth/fixtures/), and recorded evidence in [`docs/gearth/evidence/`](docs/gearth/evidence/). `docs/gearth/validate_design.py` checks the historical Stage 1 contracts and fixtures.

**Two validators**
- **Worker (Python):** checks the answer's syntax and that the choice is one of the offered options.
- **Engine (C#):** re-checks against the latest state:
  - role, funds, goods and dependencies,
  - expiry and replay.

  Rejections are recorded with reason codes (for example `CURRENT_PRECONDITION_FAILED`, `ROLE_FORBIDDEN`, `EXPIRED_PROPOSAL`) and have no state effect.

**Failures:**
- **Unavailable model:** produces an explicit `rule_fallback` hold.
- **Unsupported claims:** a newsroom or government claim without a verified reference stays an attributed, unverified statement. Stage 1 caught a government overgeneralizing its own shortage.

---

## 10. Operation and verification

**Requirements**
- .NET 10 SDK matching `global.json` (10.0.203).
- Python 3.12+.
- Node, for observer tests only.
- Private settings in `.env.local`, copied from `.env.example`.

**Run**
```bash
python3.12 -m venv .venv
.venv/bin/python -m pip install -r agents/requirements.txt
.venv/bin/python ops/run_stage1.py --world --storage local
```
That starts rule mode, with no credentials needed. Open http://127.0.0.1:5080.

```bash
.venv/bin/python ops/run_stage1.py --world --live --compute hybrid --storage neon --ticks 365
```
That starts live inference across the enabled Sparks and AMD; `--compute spark` and `--compute amd` select one source.

**World flags:**
- `--population` (default 1,100), `--ticks` (default 360), `--interval` (default 2 s).
- `--seed`, `--government-rotation 0–4`.
- `--capacity` (lowers total admission).
- `--verbose` (history file), `--exit-on-complete`.

**Other modes:**
- `--run-id ID` inspects a saved run without inference.
- Omitting `--world` runs the original Stage 1 world.
- Ctrl+C stops the launcher and its child processes.

**Tests and checks**
```bash
dotnet test GEarth.sln
.venv/bin/python -m pytest -q
npm run test:observer
.venv/bin/python ops/check_world.py
.venv/bin/python ops/check_stage2_api.py
.venv/bin/python ops/check_scheduler_integration.py
```

**Earlier integration results (historical; superseded by [GEarth rename verification](docs/gearth/evidence/gearth-rename-20261011.json)):**
- **C#:** 69 of 70 pass. Only the known year-long calibration test, `MoneyKeepsCirculatingForAFullYear`, fails.
- **Python:** 121 of 121 pass.
- **Observer:** 5 offline tests pass.

**What the checkers cover:**
- `check_world.py`, run on a served completed world:
  - money and goods conservation,
  - durable ordered events with causes,
  - checkpoint readback,
  - pagination and request bounds,
  - read-only assets.

  `--require-model` and `--require-full-coverage` turn on qualification checks.
- **Not covered:** browser layout has not been visually inspected.

---

## 11. Evaluation and sponsor tracks

### 11.1 What the project must show (Best AI)

- **Model decisions change the world:**
  - A complete chain: observation → menu → model choice → validation → effect → later response.
  - Real population coverage, with fallbacks shown separately.
- **Information matters:** individual memory and imperfect information affect choices.
- **Honest comparisons:**
  - The same engine and menu under a rule policy versus the model policy, reported even if the model does worse.
  - A small memory-ablation run, if time allows.

### 11.2 Metrics (explicit denominators)

| Metric | Definition |
|---|---|
| AI coverage | Unique agents with an accepted model decision / decision makers (owned and closed firms excluded); fallback rate separate |
| Unemployment | Unemployed citizens / citizens at the weekly review, by country |
| Business churn | Startups (agent decision vs. sector refill) and closures, with investment and cash recovered |
| Health access | Private and public doctor visits per week; residents below 60 health |
| Fiscal balance | Government income vs. spending per week; treasury; share of public payroll paid |
| Delivery completion | Delivered quantity / dispatched quantity, failures included |
| Loan default rate | Defaulted / matured loans; N/A if none matured |
| Cooperation | Accepted and fulfilled agreements / proposals |
| Inequality | Gini of cash, under the declared formula |

Report individual runs, means and ranges, and sample counts, including failed runs. Never invent improvements.

### 11.3 Controlled comparison

- **Runs:** five government-rotation runs (`--government-rotation 0–4`) on the same seed give each country each profile once.
- **Seeds:** repeat with paired seeds as time allows.
- **What to freeze:** profile parameters, prices, model assignment and scenario, before results are seen.
- **Seeds don't fix trajectories:** the same seed fixes initial conditions, not LLM trajectories.

### 11.4 Track matrix

| Track | Fit | Status and required proof |
|---|---|---|
| Best AI | Primary | Individual agency, information boundaries, accepted decisions with effects, real coverage. Built; needs a clean live run |
| Hugging Face | Core | Pinned open checkpoints (Qwen3-8B/4B) serving real decisions. Verified |
| AMD | Conditional | Real ROCm inference serving accepted decisions. Qwen3-4B verified; throughput contribution not measured |
| Neon | Core | Neon must play a meaningful role in how the app works. Checkpoints and events, and every what-if timeline is a Neon branch forked at an exact checkpoint LSN ([12.3](#123-neon-branching-timelines)). Working from the command line |
| Databricks | Integrated | Analysis over real run data, not a log dump ([12.4](#124-databricks-analysis-planned)). Not started |
| Full Sail | Integrated | Graph routes, closure-driven reroutes, route tradeoff explanation. Routing built; explanation/debrief not |
| BNY | Deferred | Financial exposure workflow; depends on the banking branch |
| Solana (MLH) | Mega stretch | Devnet escrow ([12.2](#122-solana-settlement-mega-stretch)). Not started |
| Overall, Figma, Adobe | Execution | Coherent, accessible, polished observer and demo |

Not claimed:
- **Jetson:** the machines are Sparks.
- **Encompass, NextEra, OneEthos:** no matching hardware or data.

---

## 12. Deferred and planned designs

### 12.1 Banking (built, on hold)

The full bank is on `labor-market-potential-bank` (commit `6363e82`). It waits until the pre-banking economy is analyzed and calibrated.

**Ricky's decisions:**
- **Borrowers:** would-be entrepreneurs and businesses can borrow.
- **Deposits:**
  - Savings earn interest, and deposits fund lending, so bank runs are possible.
  - An automatic weekly sweep keeps 2 weeks of pay in cash and deposits the rest.
  - Residents choose whether to keep savings in the bank.
- **Ownership and staff:**
  - One owner-run bank per country (state-owned in planned economies).
  - A banker occupation, with each banker managing 10 loans.
- **Bank runs:** happen only when people choose to withdraw.
- **Default:** limited liability. The firm owes the loan, and the owner keeps their savings.

**Mechanics:**
- **Reserve:** 10%.
- **Loans:** repaid weekly over 26 weeks; 3 missed payments close the borrowing firm.
- **Owner settings:**
  - deposit rate: 1/3/5%
  - loan rate: 6/10/14%
  - lending standard: strict/normal/loose
- **Insolvency:** an insolvent bank is taken over by the state.

**Deposit insurance per depositor:**
- market democracy 50,000
- social democracy: all
- planned economy: all
- monarchy 10,000
- dictatorship: none

**Results (rule mode):** health at the end of the year improved:
- Fjord 63 → 81
- Dune 0 → 39
- Ionia 0 → 39

Collapsing countries fell from 8 to 6.

**To settle before merging:**
- War costs still pay into the bank.
- The dictatorship authority check reads bank cash, which becomes depositors' reserves.
- One bank per country means no competition for deposits.
- Loss-making rate settings are allowed.
- The rules note grows by about 250 cached tokens.
- Who makes bank decisions long term.

**The BNY finance inspector** would link a bank's exposure to disrupted shipments, repayments and liquidity.

### 12.2 Solana settlement (mega stretch)

**Design only.** A custom SPL token, WLD, on devnet, with backend custody:
- Ordinary transfers stay internal and are backed by a per-run reserve.
- Major trade escrow moves real tokens into a program-derived vault. They are released on an engine delivery attestation, or refunded after an on-chain deadline.

**Database states:** DRAFT → RESERVED → FUNDING_PENDING → FUNDED → RELEASE/REFUND_PENDING → RELEASED/REFUNDED.

**Reliability:**
- An outbox and a receipt inbox make settlement idempotent.
- A lost response triggers a lookup of the original signature, never a blind resubmit.

**Proof required:** one funded and released trade, plus a separate refund.

### 12.3 Neon branching timelines

**Why:** for the Neon track, Neon should make the app work in a way plain Postgres can't. Every what-if timeline is a Neon branch.

**How it works:**
- **Checkpoints with an address:** each week-boundary checkpoint records the Neon LSN right after it commits (`gearth.checkpoints`). Checkpoints in a fast run are milliseconds apart, so the LSN, not a timestamp, picks the fork point.
- **Fork:** `ops/fork_run.py` creates a branch of the default branch at that LSN (point-in-time branching), with its own compute endpoint. The branch holds the run exactly as it was at that checkpoint, with its full event history, copied on write and instant at any size. Neon's history is the snapshot store; nothing is copied.
- **One change:** the forked run resumes on the branch with one country's government type changed (`government_changed` and `timeline_forked` events record it). Leaving the change out makes a control fork.
- **Registry:** `gearth.timelines` on the default branch records each timeline's branch, parent run, fork day, LSN and change.
- **Comparison:** the same SQL against each branch's endpoint compares timelines; the run ID stays the same on every branch.
- **Determinism:** an unchanged control fork reproduces its parent exactly (every agent's cash and every daily flow, verified on Neon over days 14–28), so any difference in a changed fork comes from the change. Three things had to hold for that: completed decisions apply in dispatch order (the pending queue is an ordered dictionary), new firms, cargo, loans and policies are numbered by their own counter rather than the event sequence, and checkpoints are stored as `json` (8.1). `ForkTests` guards the engine side.

**Usage:**
```
.venv/bin/python ops/fork_run.py RUN_ID --day 14 --country aster --government dictatorship --ticks 360 [--verbose --live --interval 0 --exit-on-complete]
.venv/bin/python ops/fork_run.py --list
```
`--day` forks at the last week boundary at or before that day. Unknown options pass through to `run_stage1.py`. A verbose fork writes `.data/history/<timeline>.ndjson.gz` starting at the fork day.

**Approved choices (B1–B4):**

| # | Choice | Decision |
|---|---|---|
| B1 | Fork-day snapshot source | Neon point-in-time branching at the checkpoint's LSN; limited to the project's restore window (6 hours today) |
| B2 | Fork points | Checkpoints on week boundaries (every 7 days instead of 5), so a fork starts with no half-finished week |
| B3 | What a fork changes | One country's government type first; more levers later |
| B4 | How a fork starts | `ops/fork_run.py`; an observer button if time |

**Needs:** `DATABASE_URL`, `NEON_API_KEY` and `NEON_PROJECT_ID` in `.env.local`. Child branches inherit roles, so only the host changes.

**Limits and next steps:**
- Forks are only possible within the restore window; raising it in the project settings extends it.
- Each branch keeps its compute endpoint until deleted; Neon plans cap the number of branches.
- Only one run at a time locally (port 5080), so forks run one after another. Live forks share GPU capacity.
- Next: per-day tables in Neon (N2) so branches can be compared day by day in SQL, an observer view of timelines, and more levers for B3.

### 12.4 Databricks analysis (planned)

Neon is the operational source of truth; Databricks answers what tends to happen across many runs, not within one. Handoff for the Databricks developer: [databricks-handoff.md](docs/gearth/databricks-handoff.md).

**Candidate analyses:**
1. **Government types across rotation runs:** which profile keeps health, employment and budgets up, with country held constant.
2. **Cheap calibration:** hundreds of seeded rule-mode years (about 10 s each) to find which settings separate surviving from collapsing countries.
3. **Model behavior:**
   - model vs. rule vs. fallback choices,
   - which choices preceded collapse,
   - LLM labeling of stored reasons.
4. **Causal chains through `cause_ids`:** for example embargo → failed shipment → shortage → clinic closure.
5. **Demo:** dashboards, Genie natural-language questions, and MLflow runs (government, seed, outcome).
6. **Write-back:** findings saved to Neon for the observer to show.

**Mechanics:**
- **Exports:** immutable batches with a manifest (run IDs, sequence ranges, row counts, hashes, failed-run flags), or Lakehouse Federation reading Neon directly.
- **Never export:** credentials.
- **Viewing:** reports are cached for viewers, so judges don't need a Databricks login.
- **Dependency:** daily history (8.4, N2).

### 12.5 Other deferred work

- **Product quality:** premium vs. cheap goods.
- **A second bank per country:** if more agents become affordable.
- **War features:** a stretch goal.
- **Messaging and social relationships:** trust and generosity are currently inert traits.
- **Out of scope for a one-year run:** births, deaths and education progression.
- **Stage 1 removal:** about 700 lines; kept because it costs no runtime.
- **Larger populations:** the per-country agent index keeps the daily economy near-linear (6,000 agents: 44 ms a day, down from 304). Beyond that, profiling.

---

## 13. Decision log

The reasons behind the current design, in order.

### 13.1 Scope: observable society, not a game

- **Viewers only observe.** Interventions belong to operator scripts, so outcomes stay attributable to agents and rules.
- **Fictional countries.** They avoid implying that model behavior represents real populations.

### 13.2 Engine authority

- **Models only propose:** they choose from engine-built menus, and the engine re-validates every choice.
- **Conservation is asserted continuously.** A fluent model explanation can't create money, and claims stay attributed.

### 13.3 Pre-banking economy first

The goal is "a full economy of people paying each other for goods and services, money being generated by our agents doing their respective jobs, money paid to government in tax, government returning the value of the tax into public goods and services." Banks were deferred until that economy is realistic. Salaries are fixed agreements, and entrepreneurs own exactly one business.

### 13.4 Redesign after Kennedy's run

Kennedy's run stagnated: high unemployment, no churn, and cash stuck in firms. It was traced to unrealistic mechanics:
- fixed demand,
- money trapped in firms,
- one firm per sector acting like a crowd,
- unaffordable startups,
- firms that never got into distress.

The redesign added:
- quality of life with diminishing returns,
- entertainment as a service sector,
- owner dividends,
- 2–3 competing firms per sector,
- proportional price response,
- approval that follows quality of life,
- overtime by consent,
- scarcer doctors with skill step-down,
- price-weighted seller choice,
- weekly decisions with expiry.

### 13.5 Time as days, decisions as weeks

- **One tick is one day,** giving pay, shopping and health realistic rhythms.
- **Weekly scheduling** replaced wall-clock cadences (institutions every 120 s, residents every 600 s), so decisions land before the payroll they affect.
- **No cuts to who decides:** cutting who makes decisions to save time (speed option B) was declined.

### 13.6 Speed

| Option | Decision |
|---|---|
| A. Smaller prompts (cached rules, role trimming, compact menus, short reasons) | Built: ~2,300 → ~680 tokens plus a cached prefix |
| B. Fewer decision makers | Declined |
| C. Smaller model | Done: GPT-OSS-120B → Qwen3-8B/4B by role |
| D. Shorter day interval | Not useful while the model is the bottleneck |

### 13.7 Calibration veto

- **The proposals:** five fixes were proposed for the year-long collapse:
  1. welfare defaults
  2. startup cost and sector refill
  3. production targets and pricing
  4. staffing hysteresis
  5. a second researcher
- **The decision:** only #3 was kept. The rest wait for analysis of a live run, so the full set of problems is visible first.
- **The cost, accepted:** the year test stays red.

### 13.8 Refactors (no behavior change)

- **What changed:**
  - a per-country agent index (daily economy about 4× faster at 1,100 agents, 7× at 6,000),
  - slim `LastDecision` in checkpoints,
  - one serialization per save,
  - a memory helper,
  - unused fields removed,
  - `RulesNote` built from constants.
- **How it was checked:** fingerprint runs (1,100 agents × 365 days, 6,000 × 56) were byte-identical before and after.

### 13.9 Neon, Databricks and Solana

- **Solana:** pushed to a mega stretch goal.
- **Neon:** must be more than storage, which led to branching timelines (12.3).
- **Databricks:** its value is cross-run analysis, which needs daily history.
- **Order:** verbose history came first; Neon branching followed (12.3).

### 13.10 Verbose history

- **Format:** daily state snapshots plus summed money flows plus events, chosen over logging every transaction ([8.4](#84-verbose-history-for-the-renderer)).
- **Flows:** every money movement goes through one labeled transfer function, so the renderer can show giver and recipient.
- **Off by default:** normal runs are unaffected.

---

## 14. Known problems and open questions

**Known problems**
1. **Year-long collapse (addressed, awaiting a live run).** Before the public-services batch, 7–8 of 10 countries reached health 0 within a rule-mode year. The batch ([5.10](#510-public-services-batch)) keeps all ten healthy in rule mode with banking paused; a Spark run without banks, then one with banks, should confirm it under model decisions.
2. **Planned treasuries still drain slowly** (about 0.6k a day in a rule-mode year with banking on): households' savings pile up in the state bank while the state covers firm losses. The planner's quotas and, later, bank lending should be watched for this.
3. **Monarchies idle up to a fifth of their factory workers** in a decision-free year: they pay no unemployment benefit, so the jobless cannot buy household goods.
4. **The hybrid live run's HTTP-error fallbacks** are unexplained.
5. **No qualified live capacity** for the weekly Qwen system.
6. **Observer layout** hasn't been visually checked since the labor-market changes.
7. **Entrepreneur menus** are long (about 30 options with imports).

**Open questions**
1. **Tick durations:** should policy delays, elections, crisis timing, proposal expiry and loan maturity be rescaled to days?
2. **War spending:** where should it go: government only, or soldiers' wages, instead of the bank?
3. **Dictatorship authority:** what should the check read instead of bank cash once banking returns?
4. **Calibration:** which of the vetoed fixes come back after the live run?
5. **Bank decisions:** who makes them long term?
6. **Databricks:** who owns it, and is workspace access confirmed?

**Working agreements**
- **Design first:** design questions are answered before economy changes go in, and requested changes are remembered until approved.
- **Git:** commit and push only when asked.
- **Credentials:** never in docs, the board or chat.
- **Scope:** don't propose cutting who decides. War is a stretch goal; Solana is a mega stretch goal.

---

## 15. Document map

**This document replaced** the following design and status documents, which were removed (they remain in git history):
- `gearth-current-plan-and-design.md`, the previous maintained specification.
- `gearth-full-design.md`, the compiled planning history. Its inlined schemas and fixtures are the files in `docs/gearth/contracts/` and `fixtures/`.
- `gearth-project-brief.md`.
- `gearth-economy-status.md`.
- `gearth-design-vs-current.md`.
- `docs/gearth/`:
  - 01 product and demo
  - 02 architecture and contracts
  - 03 compute and agents
  - 04 economy and routing
  - 05 evaluation and tracks
  - 06 team and delivery
  - 07 Stage 1 runbook
  - 10 Stage 2 review
  - 11 observer consistency
  - 14 multi-compute and agent weights
  - 16 Qwen model migration
  - 19 model handoff

**These operational guides stay separate.** They hold setup steps, not design:
- [README](README.md): quick start.
- [World runbook](docs/gearth/08-world-runbook.md): running and inspecting worlds.
- [Sharing](docs/gearth/09-sharing.md): the permanent observer address and supervisor.
- [AMD setup](docs/gearth/12-amd-compute.md) and [AMD teammate connection](docs/gearth/13-amd-teammate-connection.md).
- [Spark B relay](docs/gearth/15-ken-spark-b-relay.md).
- [Databricks handoff](docs/gearth/databricks-handoff.md): data, features and the Neon contract for the Databricks developer.
- [All-node Qwen setup](docs/gearth/18-all-node-qwen-setup.md): installers, recovery and rollback.
- [Contracts](docs/gearth/contracts/), [fixtures](docs/gearth/fixtures/) and [evidence](docs/gearth/evidence/).

**Team ownership (snapshot, not exclusive):**
- **Jayden:** compute pool, Qwen deployment, endowments, scheduler integration, observer consistency, sharing.
- **Ricky:** economy, labor market, quality of life, persistence and refactors, verbose history, banking branch, Neon.
- **Kennedy:** live runs, the renderer and visualization, Spark B.
- **Ahmad:** inference and evaluation; Databricks was his lane.
