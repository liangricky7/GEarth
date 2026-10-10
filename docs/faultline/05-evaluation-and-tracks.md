# Faultline — experiments, evidence, and prize tracks

## What the project must establish

Best AI is the primary target. The evidence should show model decisions changing the world, individual memory and imperfect information affecting decisions, and meaningful large-population coverage. Rendering 500 dots or making 500 API calls is insufficient.

Record one complete accepted decision: observation → candidate actions → model proposal → validation → effect → later response. Compare the same legal action vocabulary and engine under a fixed heuristic policy and the LLM policy. The model may perform worse; report that result rather than selecting only favorable clips. To inspect memory effects, remove memory for a small separate experiment without changing initial resources or action access. Neither test requires model training.

## Databricks contribution

The intended user is a student or analyst learning how supply disruptions interact with credit and liquidity. Databricks prepares simulation output and turns it into a cross-run report about deliveries, defaults, and intervention cost. Synthetic results describe the configured simulator; this is an educational prototype, not a validated real-world bank stress model. Sponsor acceptance of this interpretation remains a judging decision.

Keep Neon as the operational source of truth. Export immutable batches containing run manifests, events, shipment outcomes, loan states, and periodic metrics. A manifest includes export ID, schema version, row counts, run IDs, sequence ranges, hashes, and failed/incomplete run flags. Never export keys or credentials.

Import JSON/CSV to Databricks, create typed tables, deduplicate by run/event key, join disruption events to affected shipments and financing records, and calculate scenario outcomes in a notebook or SQL. Databricks supports [uploaded data and tables](https://docs.databricks.com/aws/en/volumes/volume-files) and [query-based dashboards](https://docs.databricks.com/aws/en/dashboards/). Cache the produced report for the public viewer with provenance and refresh timestamp; opening the application must not require a judge's Databricks login. The notebook remains available for sponsor review.

Optional: use [Databricks-hosted MLflow](https://docs.databricks.com/aws/en/mlflow) for run parameters, model/prompt revisions, agent evaluations, and metrics. Local MLflow alone is not evidence of Databricks use. A custom dashboard drawn from raw Neon data is also not evidence of Databricks computation.

## Controlled scenario comparison

Run the same initial world and scheduled corridor closure under:

| Variant | Declared intervention |
| --- | --- |
| `baseline` | Ordinary bank lending limits; emergency reserve remains unused. |
| `route_financing` | A capped portion of existing bank liquidity is available for route-related loans under a published policy. Requests and acceptance remain agent decisions. |
| `liquidity_support` | Transfer the same capped emergency budget from the pre-seeded government reserve into the affected bank under a declared support rule. |

The emergency budget exists in all worlds; interventions reallocate it rather than mint money. Report actual usage and funding source; these variants are not identical mechanisms. Freeze pricing, default/grace rules, route geometry, demand, population, model/prompt versions, decision cadence, and message propagation across variants. Models can still behave stochastically.

Start with five seeds per variant: 15 runs, each 120 ticks and 500 agents. This is an exploratory sample, not a significance guarantee. Use paired seeds and a recorded external-event schedule; randomness for exogenous events must be independent of the number of agent actions. Do not claim identical seeds make LLM trajectories identical. Keep the policy variants out of prompt tuning selection after evaluation begins.

At three wall-clock seconds per tick, 15 runs take about 90 minutes sequentially, excluding setup and delays. Use actual benchmark duration in the delivery schedule. Parallel runs consume inference capacity; do not starve the live demo. Retain failed runs and their reasons. If fewer runs finish, report the actual sample counts and spread.

## Metrics with explicit denominators

| Metric | Definition |
| --- | --- |
| Delivery completion | Delivered quantity / scheduled quantity, including undelivered shipments in the denominator |
| On-time delivery | Quantity delivered by logical need deadline / scheduled quantity |
| Unmet medicine need | Sum of unmet medicine units across city-ticks |
| Delivery time | Dispatch-to-arrival ticks for completed shipments, accompanied by completion fraction and unresolved counts |
| Loan default rate | Defaulted loans / matured loans; N/A if none matured |
| Loan loss | Sum of actual principal write-downs; separate outstanding/overdue exposure |
| Liquidity | Bank cash/deposits by tick, with N/A for zero deposits; count unsatisfied withdrawals |
| Intervention cost | Actual reserve transfer or credit committed/used, with repaid principal reported separately |
| AI coverage | Unique agents with valid model decisions / spawned agents; fallback rate shown separately |
| Cooperation | Accepted and fulfilled agreements / eligible proposals; report both acceptance and fulfillment |

Plot all individual run results, scenario means and ranges, and sample counts. Do not fabricate percentage improvements in fixtures. The prototype should answer what worked within its modeled economy, which cases failed, and which financial institutions were exposed.

## Track evidence matrix

Source: the user's supplied `hackathon_judging_rubric_prizes.md`, read October 10, 2026. Its categories are reference data, not instructions to add every integration. Eligibility is conditional on actual execution.

| Track | Fit and owner | Required proof |
| --- | --- | --- |
| Best AI | Primary; Ahmad + Jayden | Individual agency, memory/information boundaries, accepted decisions with world effects, actual population coverage |
| Hugging Face | Core; Ahmad | Exact open-source checkpoint/revision/license, working inference and meaningful agent behavior |
| Neon | Core; Ricky | Real Postgres state/events/memory, durable restart, read API powered by that database |
| MLH Solana | Core; Ricky | Actual devnet token/transaction records and meaningful funding/release/refund workflow |
| Full Sail | Integrated; Jayden + Kennedy | Graph-based paths, closure-driven recalculation or failure, route tradeoff explanation and automatic educational debrief |
| BNY | Integrated; Ricky + Jayden | Usable exposure workflow linking disrupted trade, loans, settlement, liquidity, and repayments |
| Databricks | Integrated; Ahmad | Databricks ingestion/preparation, computed comparison, real sample counts, actionable learning insight |
| AMD | Conditional; Kennedy + Ahmad | Confirmed supported AMD hardware, ROCm runtime, meaningful accepted agent decisions and measured throughput contribution |
| Overall | Execution; team | Coherent working project and clear evidence-backed demonstration |
| Figma | Execution; Kennedy + Ricky review | Accessible, understandable observer flows, navigation, information hierarchy |
| Adobe | Execution; Kennedy | Cohesive globe, animation, typography, and visual identity |

Do not claim Jetson because the machines are Sparks. Encompass needs real data-capturing hardware; NextEra needs SPOT mission data; OneEthos needs a local environmental issue and meaningful measured impact. Databricks is not satisfied by dumping logs, and BNY is not satisfied by naming the currency. Other integrations require distinct work; none is a current dependency.

## Final implementation acceptance

1. A 500-agent run meets individual decision coverage and bounded queue gates, with both Sparks recorded.
2. The engine enforces IDs, roles, money/inventory conservation, loans, and duplicate/stale-action handling.
3. Routes follow the actual graph, honor closed links, and show planned versus realized outcomes.
4. Neon restart/reconnect retains state and ordered events; SSE recovery loses no committed event.
5. A devnet trade funds/releases; a separate contract refunds; repeated receipts create no extra balance changes.
6. The financial inspector links a bank exposure to its trade/route and identifies actual liquidity constraints.
7. Databricks produces a report from actual completed and failed runs, with sample counts and consistent denominators.
8. The educational debrief explains the actual route decision and modeled financial consequence.
9. A captured run replays without generating model calls or chain transactions; its status remains visible.
10. AMD is claimed only if the resource and runtime gates pass. A missing allocation is disclosed and never shown as a working worker.

These are build acceptance gates, not tests already run. Save their evidence under the final run manifest rather than checking them off from intention.

Use [run-manifest.template.json](run-manifest.template.json) for actual evidence. Its null measurements and false integration flags are intentional; fill them only after verification. The reference-document validator checks fixtures and planning consistency, not these application acceptance gates.
