# Next steps (working notes, Oct 10–11 2026)

Living list of what is decided, open and blocked. Edited as we discuss; nothing here is built until signed off.
Deadline: Sun Oct 11, 10am EDT.

## Pipeline (agreed process)

Each step: run a sim with the current patch, analyze where it strayed from reality, then build the fix
patch plus the next major change together on a new branch. A branch is only cut once the previous sim has
finished and its fix is known, so each starts from the last analyzed state.

| Step | Branch | Contains | Waits for |
|---|---|---|---|
| 0 | `main` (91a6f68) | Public services batch, banks paused | Spark rebuild, then run 0 |
| 1 | `ricky/banks-on` (staged, uncommitted) | Run 0 fixes + banks back on | Run 0 analysis |
| 2 | `gearth/phase-1-resources` | Run 1 fixes + Phase 1 (resources and trading) | Run 1 analysis |
| 3 | `gearth/phase-2-infrastructure` | Run 2 fixes + Phase 2 (infrastructure, builders) | Run 2 analysis |
| 4 | `gearth/phase-3-war` | Run 3 fixes + Phase 3 (war, unrest, relations); set x, y, n, m from runs | Run 3 analysis |

Timing: a LIVE 365-day run took about 30 minutes (world_20261011_012640_1b2cb2), so a cycle is about
1–1.5 hours with analysis and building. Five cycles before 10am is tight; Phase 3 is the one to trim.

Pre-build (Oct 11, 1am): the three phases are being built in parallel on three machines from `origin/main`
(1bf3630, now GEarth) using the handoffs in `docs/handoff/phase-{1,2,3}-*.md`: a frozen shared contract
(`PhaseContracts.cs`, partial `Nation`), one logic file per phase and assigned shared-file hunks. Each is
rebased onto the pipeline branch when its turn comes; merge order 1 -> 2 -> 3.

## Blocked right now

- [x] Spark rebuild on `main` (91a6f68: public services batch, banks paused). Confirmed Oct 11 ~04:05 UTC:
      `world_20261011_033639_b47aba` (started 03:36 UTC, Neon storage) has 10 school firms, 2 planners
      (cinder, haven) issuing `plan_set`, no loans, and every bank still at its starting 376000 cash.
- [ ] Redeploy `agents/worker.py` on the Spark (planner `values` for free-number quotas). Haven's planner
      sent the same quotas all year (724/227/16/752/14/470), which suggests the values are not getting through.
- [x] Analyze the no-bank run (`033639_b47aba`, 365 days, live Qwen, completed; baseline `023722_07d393`, pre-batch, 204 days):
  - Fixed: health. Market-country average 65–75 (baseline 0–53 at day 204); under 30 is 1–5 per country.
  - Owners chose `set_price +10%` on all 4,662 turns. Every market firm is at the 3× cap (food 120,
    clinic visit 600), and wages stayed at the table (around 1,200 a week), while food plus energy cost
    1,365 a week. Result: 30–40% of residents priced out of food every week, median citizen cash 0.73 WLD
    (baseline 26), Gini 0.59–0.70, and comfort 0.05–0.09.
  - Governments only adjusted tax (366) or funded services (53). 8 of 10 went down to a 0–0.5% income tax.
    The pattern matches choosing the first menu option (`-100` is listed first; at the floor, `+25`).
    The food subsidy and school levers were on the menu but never chosen.
  - School: 0 enrollments all year. The unemployed cannot pay 2,000, and the employed (309 could) would
    have to quit.
  - Planned economies have the best QoL (needs met 0.95–1.0) because they are not price-capped at 3×, but
    their treasuries drain. Cinder ran from 4,528 to about 0 by day 224 (its tax had been cut to 0.5%);
    Haven ran from 4,528 to 422 at a 25% tax.
  - Entry is blocked: 5 startups and 10 closures (mostly entertainment); unemployment 13–30% while firms
    sit on 300–1,000 WLD.
- [ ] Decisions from the no-bank run (proposed, **open**):
  - N1 Prices (**agreed**: a + b): owners see their sales, unsold stock, profit, competitor prices and what
    their last price change did; customers priced out count as lost sales in ReviewPrice; no +10% option at the cap.
  - N2 Tax spiral: not menu share. Every tax choice gave "cut to boost the economy" as its reason, including 95
    of the +25 picks ("Boost economy with tax cut"), so the model misreads `change_basis_points=25` as a cut.
    At temperature 0, every government repeats the same choice. **Agreed (all)**: render it as "income tax
    0.25% → 0.50% (raise)"; one tax option; tax only at a budget turn every 4 weeks; shuffle (seeded); log the
    chosen index.
  - N3 School (**agreed**): graduates are paid +15% (the firm keeps the rest of the +25% output); part-time
    study is half days for 56 days at half pay and half output, keeping the job; the enroll option shows
    tuition, lost pay, the raise and the payback weeks.
  - N4 Treasury at 0 (**agreed**: 1, 2, 5): wage arrears, with strikes after 2 weeks; capitation, welfare and
    benefits stop; the government sees how many weeks the treasury will last and what happens at 0.
    Not chosen: an import stop or an explicit approval penalty (approval still falls through quality of life).
  - N5 Money sinks: firm buffer 8 → 4 weeks, or firm cash goes on deposit once banks are on.
  - Unemployment (**parked**, come back later): no separate fix yet. 147 of 161 unemployed are service workers (median 78 days without
    work), after entertainment closed when spending collapsed, so it follows N1. Re-measure after N1 and banks.
  - Why citizens are broke (market countries, day 365): firms hold 37% of the cash, paused banks 29%,
    treasuries 17%, citizens 8% (median 0.56 WLD). A wage of 11–13/week is below food plus energy at the cap
    (13.65/week).
- [ ] Bank review against real banking (**agreed Oct 11**, building on `ricky/banks-on`; B5 deferred):
  - B1 Firm cash is never deposited (only residents' cash is swept), so the largest cash pool (firm buffers,
    37%) funds no lending. Households hold almost nothing to deposit. **Agreed**: lending is funded by households and
    private firms (cash above 2 weeks of payroll is deposited); the treasury stays out. Replaces N5.
  - B2 Student loans make households borrowers, which reverses "no household loans". **Agreed**: student loans as a second
    loan type next to startup loans.
  - B3 Limited liability with a 10% down payment: a founder can lose at most the down payment. Real small
    business loans need a personal guarantee. **Agreed**: the owner's savings cover the
    shortfall and anything left becomes the owner's personal debt to the bank.
  - B4 Artificial bank runs: citizens at temperature 0 with near-identical views could all withdraw in the
    same week (the governments' tax pattern shows this). **Agreed**: plain-words option text, the shuffle,
    and citizen temperature 0.5.
  - B5 Rates do not respond to inflation (prices tripled; the loan rate stays 10% a year, so real rates are
    negative). **Deferred**; recheck once N1 has fixed the price ring.
  - B6 Planned economies: the state bank holds deposits it never lends while the treasury drains. **Agreed**:
    the state bank lends to the planned treasury before wages fall into arrears.
  - B7 War costs are paid into the domestic bank, giving its owner free equity and dividends. **Agreed**: pay
    them to the soldiers as wages.
  - B8 Deposit insurance limit applies to each withdrawal, not to each depositor (an existing ponytail note).
    **Agreed**: track insured totals per depositor.
- [x] **Built (Oct 11, uncommitted on `ricky/banks-on`)**: N1 (a, b), N2, N3, N4 (1, 2, 5), B1–B4, B6–B8, on top of the
      bank un-pause. 143/143 .NET and 193 Python tests pass. Design doc section 5.11. The v2 event schema now lists
      every event type the engine emits (`plan_set`, `enrolled` and others were already missing).
      Checked with a no-agent-decision year against the pre-change build: market median savings and deposits up,
      food prices near the old levels (the first version of the lost-sales rule drove food to the floor; it now counts
      only shoppers a 20% lower price would win back).
- [ ] Spark: rebuild from `ricky/banks-on` and redeploy `agents/worker.py` (citizen temperature 0.5, planner values).
- [ ] Banks back on: the pause revert is staged on local branch `ricky/banks-on` (133/133 .NET, 192 Python
      tests pass). Do not commit or merge until the no-bank run has been seen. Then run again with banks.
- Older open items: delete 6 stale Neon test fork branches (needs approval); key rotation (Ricky).

## Phase 1: Resources & trading (decided)

Goal: goods need raw materials as well as energy, countries hold different amounts, so countries must trade
to keep quality of life up. Raw materials raise QoL only indirectly, through the goods made from them;
that is what creates the demand for them.

Current sectors. Goods: food, energy, tools, medicine. Services: entertainment, clinic, school, freight, bank.
Energy is power (electricity and fuel): households use 1 a day and every goods firm uses it as an input.

| # | Decision | Status |
|---|---|---|
| H1 | Goods sectors that reasonably need raw materials require them; services need only energy and current inputs | agreed |
| H2 / R7 | Extraction sectors (mine for ore, well for fuel): output = labor x country concentration, unskilled jobs, about 8 per 100 workers taken from service work (24 to 16), scaled by endowment | agreed |
| H3 / R6 | Firms import automatically (exists); the planned state imports for its firms; governments act through trade agreements and tariffs, plus a strategic reserve when war arrives | agreed |
| H4 | Raw materials raise QoL only through the goods made from them | agreed |
| R1 | Two raw materials: ore and fuel. Food stays tied to land fertility (FoodMultiplier) | agreed |
| R2 | Inputs: energy uses fuel (replaces flat EnergyMultiplier); tools use ore and energy; medicine uses energy and tools; food uses energy; clinic uses medicine; entertainment and school use a little energy | agreed |
| R3 | Endowment range per raw material | agreed: 0.25–2.5 multiplier, no zeros; 3 rich, 4 middling, 3 poor per material, set against each other so each country is rich in something |
| R4 | No depletion (deferred, noted as a known simplification) | agreed |
| R5 | Shipping = small fixed fee per shipment + per-unit cost x distance; keep tariffs | agreed |
| T1 | Prices pass on input costs: marginal cost includes inputs at landed cost (price, shipping, tariff), not only wages | agreed |
| T2 | Tariff becomes a government lever (steps like tax) instead of a fixed 5% | **open**: default no (stays 5%, 0 with a trade agreement) unless Ricky says yes |

## Phase 2: Infrastructure (decided; I2, I6, I7, I8 on defaults unless Ricky changes them)

- Governments spend tax money on infrastructure (roads, bridges, etc.) that boosts productivity of some
  sectors or all of them.
- New public job: builder. Each project needs a minimum number of builders and some number of ticks.
- Deferred earlier: builder public job (now part of this phase).

| # | Decision | Status |
|---|---|---|
| I1 | Builders work for a construction firm the government contracts (state-owned in planned economies), not a civil-service job; idle between projects like real construction. Builder is an unskilled job. Observe in runs: ticks a project waits for builders (old-build run had 7–33 unemployed per market country, 0 in planned ones) | agreed |
| I2 | Catalog: roads (+5% all goods), power grid (+15% energy), irrigation (+15% food), mine rail (+15% ore and fuel), port (-25% shipping cost, -1 day transit), hospital (+50% clinic capacity); Phase 3 adds anti-missile turret | default (not yet confirmed) |
| I3 | Boosts sized to evidence (public capital output elasticity about 0.1–0.2): +5–15%, not +50% | agreed |
| I4 | Each project needs builders (3–6), ticks and materials (ore, tools, energy); paid weekly as work progresses, not all upfront | agreed: roads 28, irrigation 28, grid 35, mine rail 35, port 42, hospital 42 ticks; observe and tune in runs |
| I5 | Degradation agreed. Proposal: the engine tracks a condition 0–100 per project (like Stocks); it loses 2 a week; weekly upkeep (1% of build cost, paid automatically to the construction firm, needs its builders) restores it; boost = full x condition/100; government can cut upkeep to save money (set_maintenance) | agreed |
| I6 | One of each type per country, up to level 3, smaller gain each level | default (not yet confirmed) |
| I7 | Paid from the treasury only; no government borrowing (bonds deferred) | default (not yet confirmed) |
| I8 | Builders: about 3 per 100 workers to start, taken from factory work (25 to 22) | default (not yet confirmed) |
| I9 | Planned economies have no unemployed: the state construction firm takes workers from the largest state firm for the project's length | agreed |

## Phase 3: War, unrest, relations (in discussion; stretch goal)

Existing: `declare_conflict` (tension >= 60, costs 5000 + 1000 a week, closes borders), `Tension`,
`TradeAgreement`, peace and trade offers.

- Missiles: hit any country, destroying one infrastructure (random, weighted by its cost). Soldiers and
  anti-missile turrets (an infrastructure) reduce the chance of damage.
- Civil unrest for n ticks when average QoL < x; afterwards, if soldiers + m x missiles < y, revolution:
  the country dissolves and its resources, people, infrastructure and land go to its neighbors.
- Country's main mission: keep average QoL >= x. It trades for what it needs and goes to war as it
  nears the threshold.
- Relations: rise with trade routes; fall toward an aggressor much bigger than its victim; low relations
  mean more refused trades and more war (by each country's aptitude for war); gifts improve relations.
- Open: what does a country gain by conquering one with no military? Claude to propose.
- Sizing: revolution and dissolution is the largest single piece; probably the first thing to cut.

| # | Decision | Status |
|---|---|---|
| W1 | Missiles damage infrastructure (lower its condition, weighted by build cost); only anti-missile turrets reduce the hit chance. Soldiers do not stop missiles | default (not yet confirmed) |
| W2 | Soldiers decide ground war (strength = soldiers + m x missiles) and keep internal order; missiles do not count toward suppressing unrest | agreed |
| W3 | Unrest after n ticks below QoL x: democracies hold an early election (leader falls); autocracies face revolution if soldiers (paid ones only) < y; revolution changes the government type (ChangeGovernment exists). Dissolution into neighbors only for a country conquered in war, and cut first | agreed (H10 paid soldiers only, H11 election or government change) |
| W4 | QoL >= x is the government's mission and shown in its view; war is a choice, incentivized indirectly: the view shows raw materials it needs but cannot buy (refused, embargoed, unaffordable) and who holds them. Rule default: trade first, war only when trade is blocked and it is stronger | agreed |
| W5 | War gains: each week the stronger side wins it takes tribute (cash and raw materials) from the weaker; peace offers carry terms (reparations or ceding part of a raw-material endowment). No annexation | agreed |
| W6 | Relations -100..100 (reuse Tension): + from trade volume and gifts; - for every third country toward an aggressor, scaled by size ratio; drift toward 0. Low relations: refused trades, tariffs or embargo | default (not yet confirmed) |
| W7 | Aptitude for war by government type (dictatorship highest, democracies lowest), shown in the government's view and used by the rule default | default (not yet confirmed) |
| W9 | Winning: each war week is a contest, P(win week) = own strength / (own + enemy) (standard contest function). Week's winner takes tribute; loser's soldiers are wounded (health drop, too sick to fight until they recover; no agents removed). A side with no fit soldiers for 2 weeks surrenders and the winner's peace terms are imposed. Otherwise wars end by accepted peace. War weariness lowers both sides' security QoL and approval | agreed |
| W8 | Defenseless countries are protected by relations, not by rules: third countries with high relations can join a war or embargo the aggressor | agreed |

## Gaps found after the phase walkthrough (proposed defaults, need sign-off)

| # | Gap | Proposed default | Status |
|---|---|---|---|
| G1 | Who builds missiles and at what cost | Government orders from the construction firm: 2 builders, 7 ticks, ore + tools + energy, about 10k each; kept in a stockpile; launching uses one | agreed |
| G2 | Turret effect | Infrastructure type with one level: cuts missile hit chance by 30% (missiles must still land) | agreed |
| G3 | Changing soldier numbers | Government recruit/discharge in steps of 2 at the table wage (1,400); recruits come from the unemployed first | agreed |
| G4 | Gifts | Cash or raw materials; relations gain scales with gift value vs the recipient's weekly spending, capped at +15 per gift | agreed |
| G5 | Tribute and cession size | Weekly tribute: 10% of the loser's weekly tax income and 10% of its raw-material stock. Cession: winner takes 25% of the loser's concentration in one raw material | agreed |
| G6 | Planned economies | Planner quotas cover ore, fuel and construction; the state imports raw materials | agreed |
| G7 | Branch base | One branch per major change, chained (see Pipeline) | agreed: see Pipeline |
| G8 | Knock-on work | Population minimum rises again (new extraction and construction firms per country); RulesNote, worker prompt, observer and analytics need the new events; design doc update; one rule-mode year per phase before Spark | noted |
| N1 | Numbers x (unrest QoL), n (unrest ticks), y (soldiers to stop revolution), m (soldiers per missile) | Set after Phase 1 and 2 runs | later |

## Deferred

- 6c aspiration (a desire for more beyond QoL), revolution (until Phase 3), resource depletion.
- Soldiers left alone until Phase 3.
