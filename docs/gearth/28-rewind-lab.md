# GEarth rewind lab

The observer and saved-data browser link to `/rewind.html`. This page is a **UI preview**, not an implementation of broader experiment interventions.

## Flow

1. Choose a saved simulation from the existing read-only run catalog.
2. Choose an actual opening, complete-week, or terminal checkpoint. All checkpoint pages are loaded; missing weeks are never invented. A save marker without a renderer frame is explicitly unavailable.
3. Select a country from the cards. Country policy, seller prices and commercial bank settings come from that exact renderer frame, with run/revision/tick identity checked.
4. Edit wages, ore/fuel/food/energy prices, country productivity, commercial bank rates and lending standards, government, income/sales tax, welfare, unemployment benefit, clinic funding and food subsidy.
5. Preview the unchanged and proposed timelines and download a `gearth.scenario-draft.v1` configuration. The comparison list describes metrics to examine after implementation; it contains no simulated outcomes.

Sample mode is explicit and independent of database reads. It offers ten illustrative countries, 52 complete weeks and the terminal day 365 (partial week 53). It never appears as a saved simulation or published Databricks result.

## Units and semantics

- Monetary controls display WLD, converted from 100 minor units per WLD. Seller price baselines are the simple mean of active country sellers, not a sales-weighted inflation index.
- Wage changes are a proposed multiplier for existing salaries and wage offers. 100% is a neutral multiplier, not an assertion about historical salary levels.
- Commercial bank rates use the engine's discrete choices: loans 6/10/14% per year, deposits 1/3/5% per year. A loan-rate override proposes changing new-loan pricing, not rewriting existing credit terms.
- Missing historical fields remain **Not recorded**. Default editor values are labeled draft values and are excluded from changes unless explicitly edited. Known zero values remain zero.
- The lab proposes one-time overrides. Planned-economy fixed prices, policy delays, price clamps and later agent decisions must be accounted for by future engine integration.

## Existing real execution

The existing authenticated experiment editor remains unchanged. It supports one country government or income-tax intervention, or one resident health or productivity intervention. Its pinned preview and Neon history requirements still apply. Viewing a captured renderer frame alone does not prove its Neon LSN remains forkable.

The new lab performs only GET requests. Exported scenario JSON is a draft, not an accepted experiment API request. No simulation, inference, Neon branch, Databricks job or production service restart is triggered by this page.

## Verification

`npm run test:rewind` covers historical values and units, all weekly pages and gaps, stale frames after navigation, missing values, validation, identity mismatches, and the sample preview's absence of network writes. Observer, controls, existing experiments and saved-data regression tests remain relevant.
