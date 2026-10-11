# Post-bank follow-up

This completes the follow-up to `world_20261011_070814_922b2a`. Changes are local on
`ricky/post-bank-patch-1`, rebased onto the latest fetched `gearth/phase-1-resources`
at `177639c`. The resource implementation is in the branch history, followed by the
post-bank patch and follow-up fixes. They have not been deployed or validated in a new live run.

## Changes

- Government/planner decisions select an action first, then supply only that action's
  integer fields. Numeric requests use constrained JSON schemas where supported,
  strict local validation, and one repair attempt inside the original deadline.
  No values are clamped or defaulted. Logs distinguish missing fields, invalid
  types and out-of-range values without retaining private response bodies.
- Each firm retains its own cost-of-living reference. Skipped or partly affordable
  raises remain pending at later profitable reviews even if prices stop rising.
  Owners see measured operating cash surplus, input replacement costs and
  income-constrained buyers. Wage eligibility uses up to four actual payroll weeks;
  raises apply the next week, whose payroll is retained before owner distributions.
  Entry uses current input and landed costs.
- Production now requires ore, fuel, energy and intermediate inputs from phase 1.
  Extraction follows endowments; quantity and distance determine freight costs.
  Planned economies face these same physical constraints. No discretionary
  productivity penalty has been added to planned economies.
- New loans create equal deposits and loan assets. Principal repayment retires
  money; interest contributes to bank income. Domestic payments transfer deposits;
  cross-bank payments settle with reserves. Lending is limited by simplified 10%
  capital and 10% reserve constraints, banker capacity and the existing borrower tests.
  Legacy cash-funded loans remain readable. Imports still require cash escrow.
- Domestic wages, shopping and government spending use deposits directly.
  Automatic cash-buffer top-ups allocate spare reserves proportionally without
  insurance or bank-run flags. Import principal remains cash-backed; domestic
  freight and tariffs can use deposits. Migration preserves unwithdrawable savings
  by remaining unavailable until they can fully settle.
- Closed firms retain fixed-country liquidation estates for residual inventory,
  deliveries and refunds. Proceeds first retire linked outstanding principal, then
  pay the beneficiary; eligible firms can buy discounted industrial inputs.
  Essential-sector recovery starts its existing clock at closure. State firms
  budget physical input purchases alongside payroll under normal funding rules.
- Nonpositive-equity banks suspend deposit interest and retain the existing lending
  constraints. State continuation explicitly guarantees nominal domestic claims
  without recapitalizing the bank or creating reserves.
- Base cash plus cargo escrow remains conserved. Broad money can grow through
  lending. Cash history and transactions retain their existing cash-only contract;
  additive `bank_ledger` records capture deposit movements separately, including
  export and Databricks import projections. Economic transfer summaries include
  deposit payments while cash reconciliation excludes them.
- Unemployment uses employed plus available job seekers as its denominator.
  Active owners count as employed; full-time students, work refusals and people
  too sick to work are nonparticipants. Agent snapshots record disposable income,
  real income and worked days. Reports distinguish liquid-assets, income and
  estimated net-wealth inequality. Historical decision roles are recorded at the turn.
- Purchases rejected because quoted stock or price changed receive one refreshed
  menu within the original deadline. The selected quantity and price are never
  silently rewritten, and a role change prevents refreshing an obsolete decision.

## Preserved concepts

Hunger's 99% output penalty remains the simulation's reversible substitute for
death: agents stay present, become nearly ineffective, and recover by satisfying
the need. No permanent agent removal was introduced. The previously approved
small-firm restriction on hungry-owner exit also remains.

## Validation and limits

Local verification on 2026-10-11: `dotnet test GEarth.sln --no-restore`
passed 248 tests; `.venv/bin/python -m pytest agents analytics
ops/test_amd_model_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q`
passed 192 tests. `git diff --check` also passed.

The C# suite includes full-year rule-only calibration and resource conservation;
focused regressions cover credit creation, repayments, deposit settlement,
lending limits, skipped wage catch-up, input costs, historical roles and fresh
purchase menus. Python tests exercise bounded numeric repair, unsupported-schema
fallback, exporter projections and cash reconciliation. The 29 new consistency
cases cover reserve-limited top-ups, escrow fees, late cargo and estate recovery,
migration, wage timing, state input funding and bank continuation. Annual
calibration thresholds were not loosened. The decisions and rejected alternatives
are recorded in [resource-bank-consistency-decisions.md](resource-bank-consistency-decisions.md).

These checks verify mechanics, not realistic aggregate outcomes. A new matched-seed
model run is still needed to verify government/planner numeric success, price and
real-income paths, and the market/planned comparison. Larger cash holdings in a
planned economy are not by themselves a failure: they must be interpreted with
real consumption, needs and production constraints. Net wealth excludes unvalued
land and fixed capital; deposits are nominal claims, not insured purchasing power.
Do not directly pool old funded-lending runs with new deposit-creation runs; capture
metadata records the changed accounting and resource conditions.
Bank operating wages remain cash-funded, and operating surplus is conservative
cash-flow accounting rather than accrual profit. Start the next round with a fresh
resource-enabled world; resuming a pre-resource world requires explicit migration.
