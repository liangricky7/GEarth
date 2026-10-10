# Faultline — economy, transport, and Solana

## Small economy with explicit rules

Use three goods: food, energy, and medicine. Citizen needs, producer capacity, transportation, prices, and loan deadlines create constraints for agent decisions. All values are fictional and versioned in [scenario.demo.json](scenario.demo.json). Tick duration has no claimed relationship to actual macroeconomic time.

Seed 500 individual accounts and a fixed initial money supply. Currency uses integer minor units; 100 minor units equal one WLD. Consumption, production, account transfers, inventory reservations, and repayments run in code. Agents choose among offers and actions; they do not invent balances or prices.

Every five ticks, a business can produce its configured output if its required energy/input inventory exists. Citizens consume configured food/energy units every five ticks. If inventory is unavailable, accumulate unmet need; do not allow negative stock. Citizens choose purchases from posted offers, bounded by cash and inventory. Medicine is an institutional shipment good used to satisfy a city's configured need ledger.

For the first build, update local posted prices every five ticks using a capped rule: `next_price = clamp(round(current_price * (1 + 0.1 * imbalance)), min_price, max_price)`, where `imbalance = clamp((unmet_demand - available_stock) / max(1, target_stock), -1, 1)`. Compute from a prior-period snapshot; trades within that period use the quoted price. This is a declared synthetic pricing rule, not model-generated market truth.

Legitimacy and trust are bounded scores. Unmet need reduces government support by a configured amount; verified fulfilled promises increase directed trust, while violated agreements decrease it. Agents observe the relevant scores and choose reactions. Protests are individual actions, aggregated in the viewer. Migration changes location when an eligible path exists, after its modeled travel time; it is not an instantaneous teleport.

## Banking and lending

Citizens can place/withdraw deposits. A deposit transfers existing citizen cash into bank cash and creates an equal bank liability. A withdrawal reverses both, limited by available bank liquidity. Queue unfulfilled withdrawals; do not silently delete the liability.

Banks lend existing cash from their own liquid pool. An accepted loan credits borrower cash, reduces bank cash, and creates a principal receivable. Interest is a contractual future amount, not newly minted money. Require borrower request plus bank acceptance of the exact amount, rate, maturity, and collateral/eligibility policy; an approval alone does not transfer funds. Offers expire after five ticks and reference entity versions.

Repayment transfers borrower cash to bank cash; reduce outstanding principal first, recognizing paid interest as income. An unpaid loan after its due tick is overdue; after a five-tick grace period, apply the configured loss/write-down and mark default. The introductory scenario uses unsecured loans and a declared recovery assumption. No dynamic credit model is required.

Required balances: `bank_assets = liquid_cash + loan_principal_receivable`; `bank_equity = bank_assets - deposit_liabilities`, adjusted for realized losses and paid interest. Cash is not total assets. Liquidity ratio is cash/deposits when deposits are nonzero; report N/A otherwise. Money conservation tracks liquid balances plus locked escrow, excluding receivables and deposit claims to prevent double counting.

The finance inspector links a bank's borrower exposure to shipment IDs, destinations, route disruption, repayment dates, and settlement status. The useful workflow is identifying which disrupted deliveries threaten repayment and whether liquidity is sufficient for queued withdrawals. This is the BNY contribution, not merely displaying a token balance.

## Routes that affect outcomes

The transport graph uses explicit locations and directed links, with travel ticks, cost in minor units, risk score, and open status. The reference world has 15 nodes; six participate in the primary medicine corridor. Keep fictional geography visually coherent on the globe. Paths follow actual graph nodes; route animation must not draw an unrelated direct line.

Use Dijkstra with nonnegative weights; these weighted shortest-path mechanics are documented by [NetworkX](https://networkx.org/documentation/stable/reference/algorithms/shortest_paths.html). A C# implementation can use a priority queue without a Python graph service. Three policy scores are fastest=`travel_ticks`, cheapest=`cost_minor`, cautious=`travel_ticks + 10 * risk_score`. Treat these as declared objectives; capacity and time-window optimization are later extensions.

Exclude closed links and links lacking valid border permission before searching. Initially limit each edge to one new convoy departure per tick; FIFO queues handle conflicts. Convoys already inside an edge complete that leg when it closes; departures after closure cannot enter. At the next node, recompute using current permissions and the business's chosen objective. If no path exists, keep inventory in the shipment at its current node and emit `route_unavailable`.

Origin stock is reserved exactly once before dispatch. Charge each leg's modeled transport cost when it begins, to a transport-sector account; a borrower without funds waits and can request financing. A crisis can invalidate the remaining route. Record previous/new edge IDs, objective, calculated time/cost/risk, and cause event. The viewer explains why the direct route was excluded and what the alternative costs.

At destination, decrement shipment inventory and increment destination inventory once, producing a delivery event. Late delivery still physically arrives, but the trade contract determines whether it earns payment. Avoid a route cost claim that ignores queues or later financing delays; distinguish planned from realized arrival time.

## Currency and chain boundary

Use a custom SPL token, WLD, on Solana devnet; devnet SOL covers fees. Devnet assets have no financial value. [Solana token quickstart](https://solana.com/docs/tokens/quickstart).

Ordinary citizen and bank transactions remain internal ledger transfers backed by a per-run custodial token reserve. Major trade escrow moves actual tokens from that reserve into a program-controlled vault and then into the seller's receiving account, or back to the reserve on refund. All these accounts remain under the demo backend's custody. Agents have individual beneficial balances; they do not independently control signing keys. No end-user wallet setup is needed.

Bootstrap the full run supply into the reserve. Internal loans, deposits, and purchases change beneficial claims without moving tokens between chain accounts. Before escrow funding, reserve the buyer's internal claim; the reserve custodian signs on its behalf. On release, reduce its locked claim and credit the seller once after confirmation. The seller's receiving-account tokens remain part of controlled reserves backing internal claims, and cannot be counted again as extra money. Reconcile liquid plus locked claims against reserve, receiving-account, and unresolved-escrow token holdings. This is a custodial settlement prototype, not a claim that all microtransactions or bank loans settle individually on-chain.

Only the engine authorizes spend after role/resource checks. The settlement worker holds demo signing authority outside prompts and model workers. Record the custody mapping and reserves; do not call internal transfers individually settled on-chain.

## Escrow protocol

Ricky owns a small Solana program plus TypeScript adapter. [Program-derived addresses](https://solana.com/docs/core/pda) can identify a trade state and control its token vault. Bind escrow to `(run_id, trade_id)` and immutable buyer/seller agent IDs, reserve/refund account, seller receiving account, mint, amount, delivery deadline in chain time, reserve authority, and engine attestor. The program authenticates the reserve authority; the engine enforces the buyer's beneficial funds and authorization. Exact accounts and program ID are filled in only after deployment.

| Operation | Validation and effect |
| --- | --- |
| `create_and_fund` | Reserve custodian signs for an engine-authorized buyer reservation; unique trade ID; correct mint, amount, and vault; transfer reserve tokens to vault atomically. State becomes FUNDED. |
| `attest_delivery` | Configured engine attestor signs; funded/unresolved trade; delivery before the on-chain deadline; release the amount to the fixed seller and mark RELEASED. |
| `refund_expired` | On-chain clock has passed deadline; funded/unresolved trade; return funds to the fixed reserve/refund account, restore the buyer claim after confirmation, and mark REFUNDED. |

The program verifies accounts and signers, rejects duplicate settlement and wrong destinations, and applies one terminal outcome. Use [atomic transactions](https://solana.com/docs/core/transactions) for related instructions. Choose a generous wall-clock deadline per run, separate from the simulation's logical need deadline, so slow inference does not unexpectedly expire escrow. For the demo refund test, create a separate short-lived contract rather than waiting out the main trade.

The engine is the delivery oracle. The chain enforces attested settlement, not independent verification of a shipment's physical arrival. Agents cannot attest their own success. Late delivery after a refund cannot release payment, and an already released escrow cannot refund.

Database states: DRAFT → RESERVED → FUNDING_PENDING → FUNDED → RELEASE_PENDING/REFUND_PENDING → RELEASED/REFUNDED. Errors retain their operation ID and last known chain state for reconciliation. Do not dispatch until funding confirmation. Funding failure releases the internal reservation only after proving no funded vault exists. Delivery can be physically recorded while release remains pending; seller spendable cash increases only after the receipt is applied.

The coordinator writes reservations/outbox records in one Neon transaction. The worker submits and stores the signature before retrying. A lost RPC response triggers signature/account lookup, not blind re-submission with a new identity. Chain confirmation returns through a receipt inbox and is applied once at a tick boundary. Maintain a separate external receipt log for recovery; the UI clearly labels pending versus confirmed.

## Replay and scenario branches

Recorded replay performs no signing or new transactions. Counterfactual runs clone initial state under a new run ID and use fresh custody/escrow accounts, with reserves funded before start. A Neon branch cannot undo Solana history. Ordinary run IDs and copied initial state are sufficient; physical Neon database branching is optional and must not become a deadline dependency.

## Verification

Check money/inventory conservation, deposit/loan accounting, double withdrawal, duplicate dispatch/delivery, disconnected routes, closure during a leg, depleted transport funds, wrong signer/mint, escrow replay, timeout refund, and lost-receipt recovery. At least one devnet trade must complete funding and release, and a separate trade must prove refund. A plain transfer fallback can support a reduced Solana claim but does not count as completed escrow.
