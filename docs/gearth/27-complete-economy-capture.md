# Capture the current economy fields

Fresh normal runs use `gearth.economy_capture.v3`, adding war/diplomacy coverage to the prior `gearth.economy_capture.v2` economy fields. The audit format remains `gearth.audit.v1`; the feature contract records which engine state was captured. Existing v2 exports and older 19-dataset browse bundles remain readable without claiming missing war history.

## Where the added information goes

Neon’s world snapshots retain the engine state as JSON. With analytics enabled, daily audit payloads and action-effect before/after payloads also retain these fields. Databricks preserves the original JSON and exposes these projections:

| Information | Dataset / columns |
|---|---|
| Part-time schooling | `silver.agent_daily.part_time_student` |
| Unpaid wages and owner income | `arrears_minor`, `arrears_weeks`, `unpaid_hungry_weeks` in `silver.agent_daily` |
| Deposit insurance | `silver.agent_daily.insured_minor`; `bank_failures`, `defaulted_loans`, `deposit_insurance_paid_minor` in `silver.country_daily` |
| Price response | `price_change_tick`, `price_change_pct`, `units_before_price_change`, `profit_before_price_change_minor`, `price_before_price_change_minor` in `silver.agent_daily` |
| Inventory | `silver.agent_daily.inventory_json`, plus original payload JSON |
| Infrastructure | `silver.country_daily.infrastructure_json` and project rows in `silver.infrastructure_daily` |
| Maintenance, materials and builders | `maintenance_funded`, `construction_materials_used_json`, `borrowed_builders_json` in `silver.country_daily` |
| Currency and trade balances | `currency_index`, `trade_net_week_minor`, `trade_gross_week_minor` in `silver.country_daily` |
| Resources | `endowment_json`, `resource_imports_json` in `silver.country_daily` |
| Soldier pay and war weariness | `military_paid_tick`, `military_pay_minor`, `war_weariness` in `silver.agent_daily` |
| War and leadership state | `missiles`, `war_weeks`, `no_fit_soldier_weeks`, `war_weariness`, `unrest_since_tick`, `tax_mark_minor`, `war_accounting_initialized`, `government_id`, `leadership_term`, `next_election_tick` in `silver.country_daily` |
| Diplomatic relationships | `silver.country_daily.relationships_json`: original per-run relationship state, including score, war status, aggressor and peace terms |
| Military capacity | `silver.country_daily.military_json`: soldiers, fit/paid soldiers, strength, salary and missile defense |
| Public mission and unrest | `silver.country_daily.mission_json`: actual quality of life, its threshold and unrest timing |

Infrastructure rows retain run, country, day, frame kind and project identity, project attributes, original JSON and source audit/export pins. The engine records `progress_days`, `required_days` and `progress_fraction` from its remaining construction schedule, with the explicit `engine_remaining_schedule_ticks` basis. Payment amounts remain separate from work progress.

Main’s phase-three war additions are declared as `war_diplomacy=phase3_v1` in capture v3. Their original structured state is retained as JSON; military construction uses the existing infrastructure project dataset, so the catalog still contains 20 datasets. The explorer separates war/military coverage from diplomacy, elections and unrest. A v2 run does not gain complete war coverage merely because its export can still be imported.

## Deploy, capture and import

1. Deploy the updated engine, API and analytics importer together. Use the existing Neon connection; these fields are inside existing JSON audit/world records and do not require a Neon column migration.
2. For a directly deployed API, set `GEARTH_ANALYTICS=true` **before starting a fresh run**. Fresh normal `ops/run_stage1.py --world` runs capture analytics by default; `--no-analytics` explicitly opts out. Saved-run inspection and forks do not start new analytics capture.
3. Keep weekly database saves and daily audit records. The final partial week must also be saved. Do not try to enable capture halfway through an old run and call its history complete.
4. Build the updated API. On the observer host, configure persistent `GEARTH_ANALYTICS_BROWSE_DIRECTORY` and run the authenticated operator workflow:

   ```sh
   python ops/analyze_run.py RUN_ID NEW_EXPORT_DIRECTORY \
     --api OBSERVER_URL --storage neon --profile gearth --analytics-browse
   ```

5. Confirm a successful import receipt, matching run/source hashes, the 20-dataset catalog and complete field coverage before publishing. Open `/data.html?run_id=RUN_ID` to compare original records, typed columns and stored JSON.

The operator must run on the observer host or its shared cache volume. Publishing on a laptop does not populate a remote observer’s filesystem cache. See [analytics operations](20-analytics-pipeline.md) and [explorer interpretation](26-run-data-explorer.md).

## Older runs

Adding columns to Delta does not create missing historical facts. Older payloads remain unchanged; newly projected fields are null when absent. The per-run JSON presence inventory distinguishes a missing key from explicit null or recorded zero/false. A legacy run without infrastructure records has no verified project observations; zero project rows alone does not prove complete infrastructure capture.

No central-bank feature is claimed by this capture upgrade. Commercial bank settings are separate. Fork comparisons and 50,000-agent capacity remain separate work.

This document describes the operator procedure. It does not establish that production has deployed or that any particular run has passed a live import.
