# Post-infrastructure run review: e1efff

Reviewed October 11, 2026. Source: `/Users/rickyliang/Downloads/world_20261011_113650_e1efff-observations.md`, LIVE seed20261031, day252, through event93533. Source code inspected at local HEAD `7d28121`; this checkout includes later partial Phase3 work. The observations do not identify their deployed commit, so code mechanisms below must be checked against the run manifest before making causal claims about this run. No production code was changed or simulations launched for this review.

## Recommendation

Do not reduce every country's resources as the first fix. Correct misleading metrics, investigate the hunger/output feedback and sovereign-credit accounting, and measure physical supply separately from affordability. Then test targeted scarcity using matched countries and seeds. Planned economies should be able to succeed or fail through the same physical constraints, with transparent financial and administrative costs; forcing their score below100 would not establish realism.

## What the run actually establishes

- Planned Cinder/Haven show100% **rounded food-and-energy needs met in the latest weekly cycle**. This is not overall wellbeing, economic efficiency or unanimous support. Their reported support is80%/84%; they accumulated107/11 unmet unit-days earlier in the run. Whole-percent formatting can display a fraction below100 as100.
- Eight other countries mostly meet97–99% of needs; Dune is the exception at65%, treasury0, support2%, liquid-wealth Gini0.649 and10,433 cumulative unmet needs. Ionia's latest98% conceals6,532 accumulated unmet needs. Endpoints obscure recovery and persistent deprivation.
- Planned liquid-wealth Ginis0.404/0.424 exceed most peers. The run does not show universally equal wealth under planning.
-1,845 shipments dispatched,1,790 arrived,0failed. Delivery success alone cannot establish excess resources: it says nothing about households who cannot afford goods, idle firms, stock-days or unused capacity.
-142/360 government decisions were fund_services (39.4%). One quoted explanation claims this supports construction, whereas the action pays clinics.28 infrastructure actions were selected; the report does not report completed projects, construction delays, utilization or marginal output.
-7 diplomatic offers received0responses;609 decisions were rejected by current preconditions. These are actionable observability/decision issues, not proof of failed inference: the report recorded0fallbacks.
- Conserved base money does not establish sustainable credit or a realistic economy. It also does not mean loans are free money: deposit creation and principal retirement are already explicitly modeled.

## Why planned countries can reach100 here

The metric requires only one food and one energy per resident per day (`SocietyEconomy.Day`, approximately lines155–180). Once those two streams are covered, additional variation in services, choice, health, waiting time, quality, housing or fiscal sustainability cannot change this particular score. FAO distinguishes availability, access, utilization and stability; two goods consumed in a single week cover a much narrower concept.

The code combines several advantages for planned provision:

1. Producer prices begin at sector base prices; planned firms skip market price review (`SocietyFactory`, `SocietyLabor.Week`).
2. Planned unemployed residents are automatically assigned to eligible state firms (`SocietyLabor.Match`).
3. The treasury pools state-firm surplus and fills wage/input funding shortages (`SocietyEconomy.Week`).
4. The treasury can borrow automatically from its bank before wages go unpaid (`BorrowForTreasury`).
5. Planned firms avoid market distress layoffs/closure (`SocietyLabor.Payroll`), though unpaid state wages can still produce arrears and strikes.
6. Planner quotas are supported by exact sector stock/sales/capacity observations and routine purchasing/imports. Planning is not a complete physical allocation problem: workers, inputs and trade still impose limits, but there is little explicit information delay, internal logistics or administrative friction.

Each feature has a plausible institutional interpretation. The problem is the combination of strong protection, thin measures of welfare and limited visibility of financing/inefficiency. There is no evidence here of a literal unconditional100% assignment to planned countries. Their physical inputs can run short and wages can go unpaid.

Do not add arbitrary waste or corruption to planned economies just to make them lose. If administrative capacity, information delays, service queues, quality variation or implementation failures are introduced, represent mechanisms that can affect public and private institutions, with differences tied to organization and decisions.

## Priority findings and concrete fixes

### R1 — Severe hunger/output feedback can produce an artificial collapse

**Code-confirmed mechanism; Dune causality unproven.** `SocietyEconomy.HungerOutputFactor=0.01`; capacity multiplies by this factor whenever Hungry. Production is integer-valued, so a hungry small crew can fall to zero output. Reduced output undermines firm sales and wage funding, making food less affordable. Daily production occurs before that day's household meal. Farm-worker food/in-kind provisions mitigate one leg of the loop, but energy, extraction and other workers remain exposed. Dune's outcome is consistent with this trap but the report cannot establish the chain.

**Fix:** replace the one-meal cliff with cumulative nutrition debt and gradual deterioration/recovery. Separate short-lived fatigue from prolonged undernutrition; calibrate parameters, do not assert a particular replacement multiplier is medically established. Carry fractional production across days so small positive productive effort is not repeatedly rounded away. Make emergency food relief an explicit funded flow rather than conjuring food. Classify each missed meal as empty shelf, unaffordable, delivery delay or access restriction.

**Proof test:** same household/workforce, one missed meal then restored food; output should recover without a persistent mechanical lock. Prolonged shortage should still hurt. Check a whole supply chain, not only the multiplier function.

### R2 — Treasury lending can conceal distress and enable debt rollover

**Code-confirmed accounting gap; occurrence in this run unknown.** `SocietyBank.Collect` increments missed payments but triggers default consequences only for business and student loans. Treasury debt remains active. `Equity` counts all active principal at face value. `BorrowForTreasury` can enlarge an existing treasury loan and recalculate its installment without rejecting an already delinquent borrower. Capital/reserve constraints do bound lending; this is not literally unlimited money creation. Nevertheless, repeatedly refinancing a delinquent treasury while carrying its debt at par can overstate bank health and delay a fiscal crisis.

**Fix:** expose missed treasury payments, nonperforming sovereign exposure and consolidated public debt. Define explicit restructuring, provisioning/haircut and new-lending policy for delinquent treasuries. A recapitalization or forgiveness must transfer/recognize a loss somewhere. Avoid repeatedly restarting debt amortization without showing the maturity extension. Keep legitimate emergency finance possible under a stated rule.

**Proof test:** unfundable treasury for many weeks: missed payments must affect provisioning, lending eligibility or a recorded restructuring. No invisible evergreen principal. Check conservation and bank balance-sheet identity throughout.

The planned countries' treasury cash552.59/776.05WLD does not answer this question; need treasury debt, firm balances, bank equity/reserves and fiscal flows. IMF's SOE guidance similarly stresses public-sector balance sheets and fiscal risks rather than treasury cash alone.

### R3 — Maintenance toggle can dominate continuous maintenance

**Code-confirmed incentive; not demonstrated as a repeated strategy here.** Every week projects lose2condition. One funded upkeep payment of1%project cost resets condition to100, regardless of previous condition. Therefore skipped upkeep can be caught up at the same charge as one normal week, with no material bill proportional to accumulated damage. Worker constraints and lost interim output remain real, so intermittent upkeep is not universally optimal—but the repair discount is structural.

**Fix:** split routine maintenance from restoration. Routine upkeep prevents ordinary deterioration or restores a small bounded amount; accumulated damage takes proportionate money, materials and builder time. Specify an explicit repair project/action and avoid resetting a ruined asset instantly. This would change the previously retained Phase2 model and should be signed off as a new infrastructure decision, not smuggled into Phase3.

**Proof test:** compare continuous versus skipped-then-resumed maintenance over a fixed horizon, including lost output, actual treasury transfers, builder days and materials. Restoration effort must depend on accumulated damage. Only one maintenance action is in this report, so do not claim agents exploited the cycle repeatedly.

### R4 — Government type is confounded with geography and fiscal defaults

**Code-confirmed comparison problem.** Default government profiles and food/energy productivity arrays repeat every five countries. With default rotation, planned Cinder/Haven both get food1.0/energy1.25, while dictator Dune/Ionia both get food0.55/energy1.6. Dictators also start with10%income tax and no welfare/unemployment benefit; planned states with25%tax and welfare150minor. The observed ranking therefore cannot isolate effects of political/economic organization. Raw-material endowments differ further.

**Fix:** use existing government rotation when creating matched scenarios; cover every regime in every geography with identical seeds/initial resident profiles where feasible. Separate ownership, fiscal policy, electoral authority and geography in reporting. A government-label change on an existing checkpoint is not enough if it leaves incompatible firm ownership or institutions.

**Proof test:** all five rotations across multiple seeds, then one-parameter interventions. Report regime-by-geography outcomes and distributions rather than one winner per regime.

### R5 — Stockpiles lack carrying costs and food spoilage

**Model omission in inspected goods/inventory paths; run magnitude unknown.** Initial producer buffers are large, physical storage is unrestricted, and no general food spoilage/storage-capacity cost was found. A temporary glut can create a durable safety buffer whose maintenance is effectively free. This weakens recurring scarcity and may protect countries even when current production is inefficient.

**Fix:** first report stock-days and inventory age. Then introduce food perishability and priced storage capacity/handling where useful; durable ore/tools should have different treatment. Avoid one universal decay rate for all goods. Track destroyed inventory through the existing LostGoods accounting.

**Proof test:** a finite stockpile cannot feed a population indefinitely without appropriate production/imports; output/conservation stays correct with spoilage. This is an economic design extension, not a proven cause of the two planned100% entries.

### R6 — Score and report labels obscure actual wellbeing and support

**Code-confirmed.** Needs-met counts food+energy only and prints whole percentages. `nation.Approval` is50%binary leadership support +50%average wellbeing; the report labels it Support. Thus a displayed support80% is not literally80%of people endorsing the leader. Support updates also rely on large weekly QoL changes or very low levels, which may miss cumulative gradual deterioration.

**Fix:** label the existing metric “basic food/energy coverage”; show separate food, energy, health, comfort, security, deprivation duration and population percentiles. Report actual supporter share separately from a composite approval index. Use rolling trends and duration below reasonable thresholds for retrospective support rather than only a ten-point weekly change. Show fiscal sustainability separately; don't subtract arbitrary points from a needs metric for debt.

### R7 — Government decisions show semantic confusion and possible action cycling

**Observed semantic mismatch; cycling unproven.**142fund_services selections dominate360government turns. In one explicit example the explanation says construction even though funds go to clinics. We do not know how often the actions alternated levels or whether health outcomes improved.

**Fix:** name/describe the choice as clinic funding; show recipient, current spend, utilization, untreated illness, forecast budget effect and last change's result. Flag contradictory explanations in evaluation but validate actual action parameters. Inspect the time series of changes before adding cooldowns or removing useful control. Highlight pending diplomatic offers in a dedicated decision section:7offers/0responses deserve investigation, but expired/blocked/ignored histories are needed to diagnose why.

### R8 — Price adjustment and model price choices may fight each other

**Code-confirmed dual controllers; harmful oscillation unproven.**2,953set_price selections dominate entrepreneurs' choices, while `ReviewPrice` also changes prices weekly in market economies. Prices are globally clamped between0.5x and3xbase price. A resource shock may push replacement cost beyond the allowed price, creating unfundable production instead of transparent scarcity pricing or subsidy/rationing choices.

**Fix:** log model-selected price, automatic adjustment and resulting sales/margin separately. Define an explicit owner policy (manual or delegated adjustment) so the automatic controller does not silently override strategy. Test unavoidable input costs above the price cap; redesign nominal bounds if needed, preserving deliberate price-control choices as policies. Do not remove constraints without testing household affordability.

## Scarcity experiment proposal

Uniform resource cuts are a poor first intervention: the run already contains severe deprivation, and the binding cause may be money/distribution, not physical global supply. Resource “stocks,” replenishing extraction productivity, extraction capacity and total exhaustible reserves are different knobs. Current Endowment mostly scales output productivity; cutting it is not modeling gradual depletion.

Recommended order:

1. Measure weekly physical balances by good: starting stock + production + imports - household/industrial use - exports - losses; record stock-days, capacity utilization, unmet input demand and affordability failures. Include imports and exports at the world level only once.
2. Diagnose/run corrections R1/R2 and improve R6 reporting before changing total supply.
3. Test **illustrative**, uncalibrated ore/fuel output scalars1.0,0.9,0.8 separately; hold food productivity and starting household cash fixed. Allow enough time for initial buffers to wash out. Do not bundle storage, finance, hunger and scarcity changes into one treatment.
4. Test geographic concentration while preserving total world supply. Some countries can depend on imports; producers must have exportable surplus, routes and settlement capacity. This creates a role for trade without making the whole world arithmetically unable to meet essential demand.
5. Use identical seeds/profile rotations for controls and treatments. Evaluate final-month consumption plus entire-run hunger duration, tenth-percentile wellbeing, prices/wages, productivity, inventories, defaults/arrears and consolidated public debt.
6. Keep near-universal basic provision achievable when policies, supply chains and financing work. Scarcity should make choices consequential through investment, trade and distribution; it should not force permanent mass deprivation as the only route to interesting gameplay.

No defensible production cut percentage can be inferred from this document alone. It does not include output/consumption balances, inventories, utilization or debt histories.

## Evidence still needed from e1efff

- Deployed commit and full checkpoint/event/audit data.
- Country/firm weekly food and energy availability, prices, cash/deposits, hunger cause, wage arrears and output lost to hunger.
- Planned firm subsidies/surplus, quota/output/stock/utilization and public-sector consolidated budget.
- Treasury loan draws, repayments, missed installments; bank sovereign exposure, equity and settlement reserves.
- Infrastructure started/completed/stalled, builder shortages, material delays, upkeep paid and condition history.
- Government fund_services values over time, diplomatic offer lifetimes and opportunities to respond.
- The609rejections by action and elapsed observation age; accepted start_business counts are proposals, not necessarily completed firms, so don't infer a creation bug from122choices versus81openings.

## Primary external references

- FAO, food-security dimensions: https://www.fao.org/4/j0710e/j0710e.htm
- IMF, Managing Fiscal Risks from State-Owned Enterprises: https://www.elibrary.imf.org/view/journals/001/2020/213/article-A001-en.xml
- IMF, SOE Stress Test Tool: https://www.imf.org/en/topics/fiscal-policies/fiscal-risks/fiscal-risks-toolkit/fiscal-risks-toolkit-soe-st

These support the choice of realism dimensions and fiscal diagnostics. They do not establish that a particular simulation run's outcome had any one cause. Findings above distinguish observed outcomes, inspected code mechanisms and untested hypotheses. No fresh dynamic counterexample tests were run for this review.
