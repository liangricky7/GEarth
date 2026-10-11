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
| 2a | `gearth/post-resource-patch-1` | Every sim patch and fix after Phase 1 until infrastructure goes in (starts with the run-speed changes P1–P2) | Resource run analysis |
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

### Resources run fixes (run `092034_dabda2`, day 125; signed off Oct 11, built on `gearth/post-resource-patch-1`)

Findings: market prices about 2x base (owners raise prices +10% 77% of the time, against 23% for cuts), so planned citizens have twice the real income;
market treasuries hold 40% of cash, and every enacted policy is `fund_services`; every country refounds every sector
(mine and medicine close-and-refound loops); 18 new energy firms on a glut; beryl's bank hired 5 bankers with no loans
and failed; imports are 40% of entrepreneur choices and 576 of 621 rejections.

| # | Decision | Status |
|---|---|---|
| P1 | Shoppers lean hard away from prices above 1.3x base (extra 8th power of reference/price; 1.3 and 8 arbitrary, tune after runs); each `set_price` option shows expected weekly sales and margin if competitors hold | built |
| P2 | Government hoarding: (a) welfare and unemployment benefit indexed to the full cost-of-living rise every 28 days (never cut; levers' ranges stretch to include the indexed level); (b) a lever set to its current level is a hold (no policy; `unchanged_lever` on the accepted event). (c) lever effect text and (d) surplus rule wait for the phase 2 infrastructure run | a, b built; c built as D1 (ranges replaced by moves); d deferred |
| P4 | An empty mine, well or pharma is a gap (and gets an essential grant) only if imports do not land below 1.5x a local firm's unit cost; food, energy and tools always keep a local seller (households only buy at home) | built |
| P5 | Gap demand is market-wide: buyers turned away at least a tenth of sales (not any one seller turning someone away) | built |
| P6 | Bank: another banker only when loans fill 80% of banker capacity; deposit-rate options capped at loan rate x loans / deposits | built |
| P7 | Import options: cheapest landed seller per good and size, with landed cost and cheapest local price; an order fills what the seller still has | built |
| P8 | Planned economies: fix pricing first, revisit the productivity knob after the next run | agreed |

Day 216 of the same run: dune and ionia (poor farmland, rich ore) ran trade deficits of about 5.5k WLD each under one
currency and fell into depression (33–39 unemployed, needs met 59–70%). Planned states exported at decree prices and
re-imported the same goods (cinder: 10.2k ore out, 10.4k in); haven's treasury was about 5 weeks from running out.

| # | Decision | Status |
|---|---|---|
| R1 | No import option when the landed cost does not beat the cheapest local seller (no goods crossing a border both ways) | built |
| R2 | A planned state firm exports at the cheapest market price abroad (world value, never below its decree) and only stock above a week of its quota. Pricing at the buyer's own market price never landed below a local seller, so cinder could not pay for fuel and collapsed in the rule-only year | built |
| R3 | Raw materials worth more (higher ore/fuel prices or more uses) | deferred: phase 2 infrastructure consumes ore |
| R4 | Per-country currency index (a): moves weekly by up to 5% x the trade balance's share of trade (0.5–2, tune after runs); applied as its single-money equivalent, an import surcharge and export rebate through each treasury, so exporters keep their own price. Planned economies keep a fixed official rate of 1 (non-convertible; a floating index pinned cinder at 0.5 and starved it of imports). Watch: the net surcharge piles up in deficit treasuries (rule-only year: dune/ionia treasuries +12–17%), which ties to P2(d) | built |

## Phase 2: Infrastructure (decided; I2, I6, I7, I8 on defaults unless Ricky changes them)

- Governments spend tax money on infrastructure (roads, bridges, etc.) that boosts productivity of some
  sectors or all of them.
- New public job: builder. Each project needs a minimum number of builders and some number of ticks.
- Deferred earlier: builder public job (now part of this phase).

| # | Decision | Status |
|---|---|---|
| I1 | Builders work for a construction firm the government contracts (state-owned in planned economies), not a civil-service job; idle between projects like real construction. Builder is an unskilled job. Observe in runs: ticks a project waits for builders (old-build run had 7–33 unemployed per market country, 0 in planned ones) | superseded by C1: state contractor everywhere |
| I2 | Catalog: roads (+5% all goods), power grid (+15% energy), irrigation (+15% food), mine rail (+15% ore and fuel), port (-25% transit time per level-1 port; see C4), hospital (+50% clinic capacity); Phase 3 adds anti-missile turret | default (not yet confirmed) |
| I3 | Boosts sized to evidence (public capital output elasticity about 0.1–0.2): +5–15%, not +50% | agreed |
| I4 | Each project needs builders (3–6), ticks and materials (ore, tools, energy); paid weekly as work progresses, not all upfront | agreed: roads 28, irrigation 28, grid 35, mine rail 35, port 42, hospital 42 ticks; observe and tune in runs |
| I5 | Degradation agreed. Proposal: the engine tracks a condition 0–100 per project (like Stocks); it loses 2 a week; weekly upkeep (1% of build cost, paid automatically to the construction firm, needs its builders) restores it; boost = full x condition/100; government can cut upkeep to save money (set_maintenance) | agreed |
| I6 | One of each type per country, up to level 3, smaller gain each level | default (not yet confirmed) |
| I7 | Paid from the treasury only; no government borrowing (bonds deferred) | default (not yet confirmed) |
| I8 | Builders: about 3 per 100 workers to start, taken from factory work (25 to 22) | superseded by C5: no starting crew |
| I9 | Planned economies have no unemployed: the state construction firm takes workers from the largest state firm for the project's length | agreed |

### Infrastructure review fixes (Oct 11, after merging main into `gearth/phase-2-infrastructure`)

Review finding: infrastructure almost never got built. In a probe where every country ordered irrigation and a
hospital on day 56, 1 of 20 projects finished in 140 days. Five of eight market contractors closed. Planned contractors
deadlocked: they had no cash for materials, the state settlement swept them and their crews struck. Idle market
contractors' owners went hungry all year. After the fixes, 19 of 20 projects finished within 8 weeks and the
twentieth a week later.

| # | Fix | Status |
|---|---|---|
| C1 | The construction contractor is state-owned in every country, like the school. It never closes, nobody can found one, and the rules run it, not an agent | built |
| C2 | A generous advance: half the project cost is paid when it is ordered. Installments resume once work passes it. While builders are missing, the contractor raises its wage offer 10% a week, up to 2x the table wage, and recruits from unskilled jobs outside the essential sectors | built |
| C3 | Before each payroll the treasury tops the contractor up to a week of its builders' pay. With no project under way, it takes back anything above two weeks of pay | built |
| C4 | A port cuts import transit time on every leg (25% at level 1) instead of freight fees. The fees go to the country's own freight firm, so cutting them gained the country nothing | built |
| C5 | No starting builders: the seeded crew was laid off in week one in market countries and sat idle all year in planned ones | built |
| — | Optional, not approved: index contract prices to costs; upkeep 1%/week (52%/yr) against about 2–4%/yr in reality | open |

Watch in the next run: whether governments order projects, weeks a project waits for builders, which sectors lose
workers to the contractor, and contractor top-ups against treasury cash.

### Post-resource run fixes (run `102658_3098da` on main `3726bd0`, day 118; signed off Oct 11, built on `gearth/phase-2-infrastructure`)

Findings: price ratchet fixed (793 cuts against 576 raises) and treasuries down to 12–30% of national cash, but:
governments froze (156 of 165 `fund_services` picks re-chose the level in force; taxes, welfare and benefits never moved
while ember sat at 25–28% unemployment with no benefit and market treasuries grew every week); food-poor ionia and dune
sold food at 4–5x the price imports landed for; 732 of 1,369 owner price moves chose a worse forecast margin than
holding (197 sold less *and* earned less); founders entered in herds (4 food firms in aster in one day, 4 unstaffed
mines in grove), and market unemployment rose from about 1 to 20–28 per country.

| # | Fix | Status |
|---|---|---|
| D1 | Government levers (tax ±2 points, unemployment benefit ±300, welfare ±50, sales tax ±2 points, services ±50) are a cut and a raise from the level in force, each with its weekly cost or revenue; holding is the hold option. `country.problems` names joblessness ≥10% (and a missing benefit), hungry residents, and a treasury idling over 8 weeks of spending or running out within 12 | built |
| D2 | A unit's cost is the cheaper of making it here or importing a week's sales of it, in the entry test, the cost push, price forecasts and the owner's view | built |
| D3 | No price option forecast to sell no more and earn less than holding; each option states its margin against now. A firm's first week has no empty-shelf price rise | built |
| D4 | A market that took a newcomer in the past week stays closed to founders; food's priced-out signal needs a tenth of residents (as for the food subsidy), not one shopper | built (trial) |
| D5 | Remove `pay` from planned state firms' menus (147 gifts of 10 WLD to random residents and their own government) | built |
| D6 | A closed firm's late receipts are still paid out as they arrive, but `estate_settled` is emitted once a week per estate (and when wound up), not per sale (was 1,149 events) | built |

Watch: planned treasuries fall about 185 WLD a week (cinder 5,399 to 3,732 since day 42), pointing at arrears around
day 250; planned economies still lead on needs met, comfort and approval, so P8's productivity knob waits for that.
D2 cost: the entry test checks demand and an idle worker before the margin, and prices imports once per market, so
a citizen menu got slightly faster (1.4 to 1.2 ms) and a day costs about 2 ms more (15.9 to 18.0 ms, best of 4), from
each goods firm's weekly price review pricing one import.

## Phase 3: War, unrest, relations

The narrowed E1–E13 review consensus is implemented on the integrated economy. Continuation starts from
`main` at `7d28121` on `feat/phase-3-completion`, as requested October 11. The historical pipeline and
review evidence remain above and in [phase-3-review.md](phase-3-review.md). Current checks and limitations
are in [phase-3-war.md](phase-3-war.md). Local implementation does not establish live calibration.

Retained decisions: route-based embargoes with third-country routing, weekly unrest, ordinary wound
treatment and funded infrastructure upkeep, uncapped approved tribute/cession shares, and existing
recruitment/arrears rules. No dissolution, annexation, forced election defeat, truce or new repair system.

| # | Decision | Status |
|---|---|---|
| W1 | Missile hits remove 50 condition from one cost-weighted completed project; only turrets reduce the hit chance. Ordinary funded upkeep can restore the damage | implemented; retained E5 model choice |
| W2 | Soldiers decide ground war (strength = soldiers + m x missiles) and keep internal order; missiles do not count toward suppressing unrest | agreed |
| W3 | Weekly checks after 28 days below QoL 40: democracies call an early election decided by ordinary approval; autocracies need recently paid, non-striking soldiers covering 5% of residents. Revolutions change only ownership-compatible profiles and cancel pending leadership policies; planned conversion emits explicit deferral | implemented; narrowed E3/E11 |
| W4 | QoL >= x is the government's mission and shown in its view; war is a choice, incentivized indirectly: the view shows raw materials it needs but cannot buy (refused, embargoed, unaffordable) and who holds them. Rule default: trade first, war only when trade is blocked and it is stronger | agreed |
| W5 | Each probabilistic weekly winner takes cash/raw tribute; all wars expose peace with no terms, 25% treasury reparations or 25% raw endowment cession. Clear pending offers at war start/end, reject stale responses, and never settle twice | implemented; E4/E6/E9/E12 |
| W6 | Scores -100..100 with legacy tension loading; deliveries/gifts raise scores, third countries condemn aggressors, peaceful scores drift toward zero. Score <= -60 closes direct routes; third-country routing remains possible, but war blocks bilateral trade | implemented; narrowed E7/E13 |
| W7 | Readiness: dictatorship .8, monarchy .6, planned .5, market democracy .2, social democracy .15; elected-versus-elected multiplier .25. Rules require inaccessible inputs and superior strength; LIVE bypasses this heuristic only | implemented default; calibration pending |
| W9 | Winning: each war week is a contest, P(win week) = own strength / (own + enemy) (standard contest function). Week's winner takes tribute; loser's soldiers are wounded (health drop, too sick to fight until they recover; no agents removed). A side with no fit soldiers for 2 weeks surrenders and the winner's peace terms are imposed. Otherwise wars end by accepted peace. War weariness lowers both sides' security QoL and approval | agreed |
| W8 | Defenseless countries are protected by relations, not by rules: third countries with high relations can join a war or embargo the aggressor | agreed |

## Phase walkthrough defaults retained after review

| # | Gap | Proposed default | Status |
|---|---|---|---|
| G1 | Who builds missiles and at what cost | Government orders from the construction firm: 2 builders, 7 ticks, ore + tools + energy, about 10k each; kept in a stockpile; launching uses one | agreed |
| G2 | Turret effect | Infrastructure type with one level: cuts missile hit chance by 30% (missiles must still land) | agreed |
| G3 | Changing soldier numbers | Government recruit/discharge in steps of 2 at the table wage (1,400); recruits come from the unemployed first | agreed |
| G4 | Gifts | Cash or exact treasury-owned/purchased raw materials; compensate private suppliers, prequote full funding, report actual amount/value; relations gain capped at +15 and may round to zero | implemented; E10 |
| G5 | Tribute and cession size | Weekly tribute: 10% of the loser's weekly tax income and 10% of its raw-material stock. Cession: winner takes 25% of the loser's concentration in one raw material | agreed |
| G6 | Planned economies | Planner quotas cover ore, fuel and construction; the state imports raw materials | agreed |
| G7 | Branch base | Continue current integrated main on a new focused branch, commit incrementally | current user request; supersedes historical chained base |
| G8 | Knock-on work | Population minimum rises again (new extraction and construction firms per country); RulesNote, worker prompt, observer and analytics need the new events; design doc update; one rule-mode year per phase before Spark | noted |
| N1 | Numbers x, n, y, m | 40 QoL, 28 days checked weekly, 5% recently paid soldiers, 3 strength per missile | implemented defaults; not live-calibrated |

E8 completion: preserve every positive staffed consumer forecast gap after own inventory and incoming cargo.
Show domestic seller stock/prices and affordable quantity with a shared domestic budget; show each foreign
seller's exportable stock, one-unit access blocker, affordable quantity and full-gap escrow/funding quote.
Foreign offers are independent alternatives using the same funds, not quantities to add. A partial shortage
with any buying access remains visible but does not by itself satisfy the rule-war blocked-access test.
Government observations also expose military salary, upkeep, construction commitments and multiwar tribute.

## Run speed: lock and scheduler review (Oct 11, branch `gearth/post-resource-patch-1`)

Run b0315c took about 41 minutes for 365 ticks (about 47 s per simulated week). With inference stubbed, the
C# engine, locks and scheduler finish the same 1,100-agent year in 22 s, so they are not the bottleneck. The
time goes to the 8B institution pool (66% of decisions, 32 slots) before each weekly barrier; the 4B citizen
pool finishes in one wave. Tick pacing (2 s) already overlaps inference.

| # | Change | Status |
|---|---|---|
| P1 | Upload the Neon week-boundary checkpoint in the background while the next tick dispatches decisions | built |
| P2 | World-run worker timeout 60 s -> 20 s; run notes report worker latency p50/p95/max and timeouts per backend | timeout reverted after run a24105; latency notes retained |
| P3 | Raise 8B concurrency (16 -> 24-32 per Spark) | rejected: serving frozen |
| P4 | FP8 8B weights | rejected: serving frozen |
| P5 | Move entrepreneurs to 4B | rejected: entrepreneurs keep 8B |
| P6 | Entrepreneurs decide every 2 weeks (rotation like citizens) | open |
| P7 | Let stragglers spill past the weekly barrier | rejected |

Run `world_20261011_101952_a24105` exposed a timeout/admission cascade: at day 49 there were 452 accepted model decisions and 1,486 fallbacks. Model timeouts triggered provider cooldowns, and HTTP 429 admissions were immediately counted as fallback holds. Restore the 60-second world budget (planner and ranged-value turns make 2-3 model calls), and retry busy-worker admissions with backoff within the unchanged 65-second proposal deadline. Authentication and other HTTP errors remain explicit failures; cancellation abandons the request. Verify the replacement run using fallback categories and model coverage, not just elapsed run time.

## Deferred

- 6c aspiration (a desire for more beyond QoL), ownership-changing revolution, resource depletion.
