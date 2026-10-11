# Resource and banking consistency decisions

Design review and implementation, 2026-10-11, on `ricky/post-bank-patch-1`,
built from `bc553a3` after rebasing onto phase 1 resources at `177639c`.
The decisions reached through the skeptical two-agent review are implemented.
Local validation covers the acceptance cases below; aggregate realism still needs
a fresh model run. Nothing in this follow-up has been pushed or deployed.

## Constraints

- Preserve the reversible substitute for death: hunger reduces output by 99%, and
  eating restores capacity. Preserve the approved small-firm owner-exit restriction.
- Production requires physical inputs, labor and delivered stock. Credit cannot
  create resources or bypass routes, delivery time, tariffs or reserve constraints.
- Base cash plus cargo escrow remains conserved. Deposits and broad money may change
  through lending, interest, repayment and other explicitly recorded bank operations.
- Keep existing borrower standards, capital/reserve ratios and banker capacity.
  Apply the same production and settlement rules to market and planned economies.
- No new central bank, automatic reserve creation, discretionary planned-economy
  penalty or complete bank-resolution/accrual-accounting system in this release.

## 1. One payment contract, with explicit cash operations

Domestic payments use cash and deposits through `SocietyEconomy.Transfer`.
Remove pre-payment cash draws from payroll and domestic shopping. Government
menus and their execution must use the same payment-capacity predicate, including
investment, startup grants and hiring bonuses. Revalidate at execution.

Keep the existing cash targets, but automatic buffer top-ups are best effort.
Deposit excess cash first, then allocate a bounded budget of reserves above the
required reserve floor across eligible top-up requests. Top-ups never invoke
insurance or record unmet withdrawals; lacking notes does not prevent domestic
deposit payments. Allocation should be deterministic and proportionate rather
than rewarding the first account in iteration order.

An explicit choice to withdraw savings remains a real withdrawal request. It can
exhaust reserves, record a shortfall and invoke the existing insurance policy.
Insurance transfers existing government cash and claims; it does not mint reserves.

**Why the initial proposal was rejected:** removing `Draw` from purchases alone
leaves the weekly sweep manufacturing the same artificial bank run. A probe with
positive bank equity of 55,200 and zero reserves still paid 4,200 in insurance
solely to refill routine buffers. Its cash and goods conservation checks passed.

## 2. Keep cash-backed goods escrow, split domestic fees

Before dispatch, require both sufficient cash availability for the goods principal
and sufficient total payment capacity for principal, domestic freight and tariff.
Withdraw only the principal shortfall, without invoking insurance. Put that cash
in cargo escrow, then settle freight and tariff domestically using cash/deposits.
Validate stock, route, prices and capacities before mutation; commit all movements
as one simulation operation with their normal audit records.

Delivery releases escrow to the seller; failure returns it to the buyer. Freight
and tariff retain their existing sunk-cost treatment on failed delivery. No new
foreign trade is granted merely because a bank has created deposits.

**Why:** a probe rejected a 75-principal shipment with 27 in domestic fees despite
75 cash being available and 57,200 in spendable funds. Requiring cash for the fees
adds a restriction unrelated to the chosen foreign-settlement constraint. Replacing
escrow wholesale with unrestricted deposits would remove that reserve constraint
and require a larger settlement design.

## 3. Existing closed-firm accounts become liquidation estates

Closing stops production, hiring and decisions, and returns the owner to their old
trade immediately. The existing firm account stays in its original country as a
durable estate; do not introduce another account identity or reopen its role.

Keep immediate recovery and assignment of any remaining guaranteed debt to the
owner. Save the resulting credit IDs on the estate. Cash, deposits, late sales
proceeds and refunds first repay the remaining principal of those linked credits,
then go to the beneficiary. Use a principal-only recovery operation: reduce the
same credit asset, retire created credit once where applicable, and never charge
a second copy of the owner's debt. If the owner already repaid it, late proceeds
belong to the owner. Repeated closure/recovery must be idempotent.

Handle cargo on both sides: a closing buyer can receive goods or refunds, and a
closing seller can receive payment. Do not finalize an estate until its shipments
are terminal and its remaining assets have an explicit disposition. A migrated
beneficiary receives only what can settle across banks; unpaid assets remain
recoverable in the original-country estate.

Expose residual goods as dedicated discounted domestic offers to eligible buyers,
including firms consuming raw inputs. Integrate these offers with acquisition and
input-cost quotes. Do not broaden `Firms()` or re-enroll estates in production,
employment, price reviews or model scheduling. Household raw-material restrictions
continue to apply. Retain unsold stock as accessible estate inventory rather than
silently destroying it or handing industrial inputs to non-trading citizens.

**Why alternatives failed:** delaying debt assignment until shipments finish hides
the guarantee while an owner starts another business. Assigning debt again after
each delivery risks double recovery. Paying all estate proceeds to the owner first
bypasses creditors. An isolated probe delivered five ore into a defunct account;
another left 127,200 in deposits there. Conservation alone did not detect the loss
of economic accessibility.

## 4. Use measured operating cash surplus, not a claim of accrual profit

Name the current signal operating cash surplus and state that it includes cash and
deposit payments. Measure operating receipts less input purchases, freight,
tariffs, interest and actual wages. Separate financing principal and owner
distributions; record subsidies explicitly rather than guessing their meaning.
Keep the owner's weekly draw conservative under the existing distribution rule.

Wage-review eligibility uses a saved window of up to four completed payroll weeks.
Use observed weeks for a new firm, never invented zero weeks. Review after actual
payroll measurement, with any increase effective the following week. Positive
average surplus qualifies the firm; the affordable increase remains bounded by
that average surplus and current funds after reserving the next normal payroll.
Retain each firm's pending cost-of-living reference when a raise is skipped or partial.
Owner distributions also reserve the next payroll, including any newly approved
raise; passing the affordability check must not leave that payroll unfunded.

**Why:** a single sampled week can repeatedly coincide with purchases of roughly
two weeks of imported inputs, suppressing catch-up despite sustainable receipts.
Three measured weeks plus an estimated bill would mix actual outcomes with guessed
overtime, sick pay and work refusals. Full accrual accounting would require opening
inventory costs, input credits/work in progress, output stock, in-transit goods and
liquidation losses; an incomplete version would create new contradictions.

This remains conservative cash-flow accounting. It does not measure earned profit
or establish that every inventory timing difference has been eliminated.

## 5. State continuation and migration must preserve claims explicitly

Separate bank insolvency from reserve illiquidity in observations. Continue existing
nominal deposits and domestic settlement after state takeover under an explicit
simulation state guarantee. Freeze lending under the existing capital constraints.
Suspend deposit-interest credits while equity is nonpositive, assessed after loan
collections; expose that suspension rather than silently promising the quoted rate.
Record the capital shortfall and guarantee exposure. Do not inject reserves or
pretend that the bank has been recapitalized. Foreign settlement and actual cash
withdrawals remain constrained, with profile-specific cash insurance unchanged.

This is a disclosed simplification, not a full resolution regime. It protects
nominal domestic claims without funding a new stream of interest deposits while
capital is exhausted. Recovery through actual loan income can restore equity.

Migration must preflight complete settlement of personal savings and the fee, then
revalidate before changing country. If savings cannot settle, migration is currently
unavailable; do not zero an unpaid deposit. This avoids introducing a multi-bank
personal-account system in this release. Estate claims remain in their saved accounts.

For authority gates, monarchy counts business cash plus deposits at the existing
threshold, explicitly a liquid-asset proxy rather than a solvency test. Preserve
dictatorship's existing reserve threshold as an explicitly reserve-sensitive
banking-liquidity proxy. Changing that political rule is a separate calibration
decision; it must not happen through indiscriminate replacement of cash checks.

Bank operating wages still require reserve cash: banks do not issue deposits to
pay their own expenses against equity in this model. The continuation policy is
not a promise that every bank operating expense can settle without reserves.
Ownerless-firm borrowing is not offered by the current business-loan action.

## Integration fixes

The annual resource tests exposed two additional interactions. Essential-sector
recovery now starts its existing 14-day clock when the last producer closes,
rather than waiting for the following weekly review. State firms retain working
capital for both payroll and physical inputs needed for feasible output, valued
at available domestic or landed replacement quotes. Forecasting does not supply
inputs, create money, or alter productivity; purchases and production still obey
the normal stock, funding and route constraints.

Estate assets remain in the beneficiary's estimated net wealth, with guaranteed
owner debt counted once. Bank-ledger rows retain credit and causal identifiers,
and terminal cargo settlement keeps its shipment context. Claim bookkeeping is
excluded from economic-transfer totals to avoid counting cash deposits or insured
withdrawals twice. Capture-only causal fields are confined to the audit stream so
enabling analytics cannot change ordinary simulation state.

## Acceptance cases

1. A solvent low-reserve bank supports domestic wages and purchases without routine
   insurance or run flags; explicit withdrawals still produce the intended shortfall.
2. Equivalent domestic balances in notes or deposits offer and execute the same
   government actions. Cash-only operations retain reserve checks.
3. Principal cash plus deposit-funded domestic fees permits an import; inadequate
   principal reserves rejects it without stock, escrow or fee mutation.
4. Buyer closure, seller closure, delivery failure and checkpoint/resume preserve
   all cargo, cash and claims and settle them to the correct estate.
5. Owner repayment before late estate proceeds cannot repay principal twice; debt
   assignment and recovery preserve bank assets and credit-retirement counters.
6. Estate ore/fuel remains purchasable by eligible production firms. Estate accounts
   remain outside labor, production and decision-maker counts.
7. A migrated estate beneficiary cannot cause an unfunded cross-bank transfer or
   erased asset. A personal migrant with unwithdrawable savings cannot lose the claim.
8. Shift identical inventory purchases within the same four-week operating window:
   wage eligibility agrees when current liquidity is equal. Cash-loss firms do not
   qualify. Partial raises retain the remaining catch-up.
9. Nonpositive-equity banks do not create new loan or interest deposits; existing
   domestic claims remain usable under the recorded continuation policy.
10. Every path preserves base cash, goods and bank balance-sheet identities. Keep
    cash history reconstructible and deposit movements in `bank_ledger`, with
    integral money fields, causal links and additive saved-state defaults.

Local validation: 29 resource/bank consistency cases, the full C# suite (including
both annual economic tests), and 192 Python tests covering agents, analytics and
operations. Annual calibration thresholds were preserved. Wage fixtures now use
funded operating receipts; audit persistence checks traverse byte-bounded pages.

A fresh compatible model run follows these checks. Do not resume a pre-resource
world without an explicit migration. Passing mechanics tests does not establish
realistic aggregate prices, consumption or market/planned outcomes.
