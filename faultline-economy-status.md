# Faultline economy: status, code review and next steps

Integration note (October 10): [current plan and design](faultline-current-plan-and-design.md) is the maintained combined specification. This document preserves the earlier brief or economy decisions and analysis. Main now includes weekly labor-market scheduling, scoped Qwen Spark/AMD compute and resident endowments; deferred banking and calibration decisions remain deferred.

**Author:** Ricky Liang (finance and persistence lane), written with Claude
**Date:** October 10, 2026 (America/New_York)
**Branch:** `ricky/labor-market` at `eabf0bd`, clean
**Deadline:** Sunday, October 11, 2026, 10:00 a.m. EDT

This file covers the project context, the decisions made in Ricky's economy sessions, where the code stands, a refactor and performance review of the current branch, and the next steps. No code was changed for this review. Every refactor listed below is a proposal that needs a yes before it goes in.

---

## 1. Project context

Faultline is an observable AI society. Individual agents live in ten fictional countries. They work, buy necessities, run businesses, trade across borders, govern, negotiate and live with the consequences. Viewers watch and inspect; they don't control anything. The primary hackathon track is **Best AI**.

**Team:** Jayden Hong, Kennedy Nguyen, Ricky Liang, Ahmad Hijazi.

**Stack**

| Part | What it is |
|---|---|
| Engine | C# / .NET 10. `backend/Faultline.Core` holds the rules, `Faultline.Api` the run loop, HTTP API and observer, and `Faultline.Tests` the xUnit tests |
| Worker | Python FastAPI (`agents/worker.py`). Builds prompts, calls the model, validates `{choice, reason}` |
| Model | vLLM serving `openai/gpt-oss-120b` on one DGX Spark. Model downgrades are being evaluated by the team |
| Storage | Neon Postgres over HTTPS, or local JSON files under `.data/runs` |
| Observer | Static page served by the API (`wwwroot/index.html`, `observer.js`) |

**How a run works**

1. `SocietyFactory.Create` builds the world. The default has 1,100 agents: 10 countries, each with a government, a bank, a newsroom and 12 firms; the rest are residents.
2. The coordinator advances one tick per day. Each day runs production, health, doctor visits and household shopping. Every 7th tick runs the weekly economy: the labor market, payroll, taxes, welfare, public care, discretionary spending and metrics.
3. Each week, every institution and entrepreneur gets one decision, plus one eighth of citizens on rotation. The engine builds a numbered menu of legal actions; the model picks one with a short reason. Results land before the weekly payroll (the weekly barrier). A decision made on a view older than 7 days expires.
4. Money and goods are conserved. `AssertConservation` runs every tick and after every applied decision.
5. Checkpoints and events are saved every 5 ticks. `ops/check_world.py` runs 12 integrity checks on a finished run.

**Local commands** (the system `dotnet` only has SDK 9, so use the one in `~/.dotnet`):

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
```

```bash
dotnet test backend/Faultline.Tests
```

```bash
.venv/bin/python ops/run_stage1.py --world --storage local --population 1100 --ticks 365 --interval 0
```

---

## 2. Branches

| Branch | State |
|---|---|
| `main` | Has the earlier economic-loop merge (`0833da5`) |
| `ricky/labor-market` | **Current.** Labor market, entrepreneurs, quality of life, competing firms, service sectors and the prompt-size speedup (change A). Pushed. This is the build the team's current live simulation run uses |
| `labor-market-potential-bank` | One commit (`6363e82 add bank`) on top of `ricky/labor-market`: the full banking module (19 files, +719/−264). On hold until the current simulation run is analyzed |
| `ricky/finance`, `ricky/agent-payments` | Earlier finance work, already folded into the branches above |

---

## 3. What the economy does on `ricky/labor-market`

- **Time:** 1 tick = 1 day. The weekly economy runs every 7 ticks.
- **Needs:** every resident uses 1 food and 1 energy a day, bought automatically up to a target stock. Purchases are spread across sellers, with weight falling with the fourth power of the price ratio. Health falls 1 a day, more with illness or unmet needs.
- **Health care:** below their health threshold, residents visit the cheapest clinic with a doctor free. Public healthcare pays for the sickest from a weekly budget.
- **Sectors:**

  | Sector | Occupation | Firms per country |
  |---|---|---|
  | Food | farmer | 3 |
  | Energy | energy worker | 2 |
  | Tools | factory worker | 2 |
  | Entertainment (service) | service worker | 2 |
  | Medicine | researcher | 1 |
  | Clinic (service) | doctor | 1 |
  | Freight | dockworker | 1 |

  Teachers and soldiers work for the government.
- **Labor market:**
  - Salaries are fixed and set by employer offers; pay differences by occupation come from a table.
  - Firms hire, lay off, offer overtime (workers must consent with effort 2) and adjust wage offers on autopilot between owner turns.
  - After 14 days unemployed, anyone can take an unskilled job. Skilled workers keep their trade and return when a matching job pays 10% more.
  - A firm short of cash cuts pay 10%, then lays off staff. Four weeks in distress closes it.
- **Entrepreneurs:**
  - Each owns exactly one business. They're paid from profit: up to their former wage, then half of the rest. Cash above 8 weeks of payroll is paid to them as a dividend.
  - Citizens with savings can start a firm in their own trade or an entertainment venue when the market shows an opportunity. The new firm launches 10% under the cheapest competitor.
  - If the business closes, the owner goes back to being a citizen.
- **Prices:** reviewed weekly, moving in proportion to shortage or glut by up to 20% a week. Prices are clamped between half and three times the base price.
- **Quality of life:** scored from outcomes with diminishing returns: needs met 0.35, health 0.20, comfort 0.20, security 0.15, leisure 0.10.
  - Households buy tools and entertainment while the next unit adds more quality of life than the savings it uses.
  - A sharp fall in quality of life withdraws support from the leadership; deep misery does too.
- **Government:**
  - Collects income tax, sales tax and profit tax, and pays public salaries, welfare, unemployment benefit and public healthcare.
  - There are five profiles (market democracy, social democracy, planned economy, dictatorship, monarchy) with different policy delays and authority rules. Planned economies own every firm and fix prices.
- **Trade and diplomacy:** imports travel physically along routes with escrow, tariffs and freight fees. The world also has elections, agreements, border closures and abstract conflict.
- **Credit (legacy, replaced on the bank branch):** firms can request a loan and the bank institution can approve it. Each loan is one bullet repayment after 60 days. In practice almost no loans happen.

---

## 4. Decision log

These are in order. Quoted text is Ricky's own wording.

### 4.1 Finance and payments (first session)

- Ricky's lane is finance and persistence. War is "a stretch, last."
- Agents can pay each other (`pay`) and buy goods on demand (`buy_goods`), with conservation checks. Finance tests were written for these.
- Goal: "simulate a full economy of people paying each other for goods and services, money being generated by our agents doing their respective jobs, money paid to government in tax, government returning the value of the tax into public goods and services."
- "ignore banks for the time being, i want to first be able to have a realistic economy pre banking."
- **Standing rule:** "DO NOT MAKE ANY CODE CHANGES FOR NOW, REMEMBER REQUESTED CHANGES BUT QUESTIONS NEED TO BE ANSWERED BEFORE CHANGES GO THROUGH." Design questions get answered first, then changes go in.

### 4.2 Entrepreneurs and the labor market

- Salaries are fixed for business and government employees.
- There's an entrepreneur role, one-to-one with a business. Entrepreneurs have their own needs and stop being entrepreneurs if the business fails. Anyone can start a business related to their previous occupation by investing money.
- A new business needs an edge over incumbents, and businesses can sell at different prices.
- Ricky took over Jayden's engine side while Jayden was unavailable.

### 4.3 Kennedy's labor-market run (`world_20261010_183127_70b725`)

- Budgets and shortages improved: needs met rose to 98–99% in the democracies, and treasuries were funded.
- The economy then sank into a slow slump. Unemployment reached 21–55%. There were zero startups, closures, pay cuts and job switches, and firms and banks hoarded cash.
- Eight divergences from reality were found and matched to how real economies avoid them. The biggest: fixed demand (only tools absorbed extra income), money stuck inside firms, a single firm per sector behaving like a crowd, nobody able to afford a startup, and firms that never got into distress.

### 4.4 Redesign that was built (now committed as `eabf0bd`)

The redesign added:
- Quality of life per citizen, scored from outcomes with diminishing returns.
- Entertainment as a services sector that turns money into quality of life.
- Owner dividends.
- Two or three firms per sector.
- Proportional price response.
- Approval that follows quality of life.
- Overtime that the employer offers and the worker accepts.
- Weekly forced decisions with expiry (institutions every week, citizens on an 8-week rotation).
- Scarcer doctors, with skilled workers able to step down into unskilled work and come back.
- Price-weighted choice of seller.

Deferred: product quality ("premium vs cheap") goes to a later update.

### 4.5 Calibration veto

- Five fixes were proposed for the year-long collapse. Ricky vetoed everything except #3 (production targets and pricing), which stayed.
- The vetoed items were: #1 welfare defaults, #2 startup cost and sector refill, #4 staffing hysteresis, and #5 a second researcher.
- **Consequence, accepted by Ricky:** the run "fully" goes first, to see all the problems before the next update. In the rule-mode year probe, 8 of 10 countries collapse to health 0, including both planned economies. The year test `MoneyKeepsCirculatingForAFullYear` fails with "cinder government+business cash ratio 0.02". This failure is known and accepted.

### 4.6 Speed

The cost model comes from Spark benchmarks: each call costs about 0.21 s plus about 0.2 ms per prompt token, with 64 calls in flight.

| Option | Decision |
|---|---|
| **A. Smaller prompts** (rules note in the cached system prompt, per-role trimming, compact menus, reasons of 100 characters or less) | **Approved and built.** Prompts went from about 2,300 tokens to about 680 per agent plus a 950-token cached prefix. A year went from about 2.9 h to an estimated 80–90 min |
| **B. Fewer calls** (rule-based newsrooms, less frequent state firms and entrepreneurs, citizens every 12 weeks) | **Declined.** Don't propose cutting who decides |
| **C. Smaller model** (e.g. gpt-oss-20b) | "in the future most likely." Model downgrades are under way |
| **D. Shorter day interval** | Advised against: the 2 s interval only matters once the model stops being the bottleneck |

### 4.7 Running on the Spark

The Spark wasn't reachable from Ricky's machine. Ricky: "scrape us running the sim, someone else will be doing it." The team's current live run uses `ricky/labor-market`.

### 4.8 Banking design (built on `labor-market-potential-bank`, on hold)

**Ricky's decisions:**
- Would-be entrepreneurs and businesses can borrow.
- Savings sit in the bank and earn interest. Lending is funded by real deposits, so bank runs are possible.
- There's a banker occupation for jobs. "The overall decisionmaking is still something i need to think about."
- Deposits use an automatic weekly sweep ("for time sake").
- The government insures deposits, varying by government type.
- One bank per country for now. A second bank is possible "if we see an increase in feasible models."
- Banks are owner-run.
- A run happens only when people choose to withdraw: "see what happens as the sims go."
- Default liability was left to Claude, who chose **limited liability**: the firm owes the loan, and the owner keeps their savings.

**What was built:**
- The bank is a firm in a new "bank" sector with an owner (state-owned in planned economies).
- Residents keep 2 weeks of pay in cash, and the rest is swept into deposits.
- The bank keeps a 10% reserve. Loans are paid back weekly over 26 weeks, and 3 missed payments close the borrowing firm.
- Deposit insurance by government type:

  | Government | Insured per depositor |
  |---|---|
  | Market democracy | 50,000 |
  | Social democracy | All |
  | Planned economy | All |
  | Monarchy | 10,000 |
  | Dictatorship | None |

- Residents choose whether to keep their savings in the bank.
- An insolvent bank is taken over by the state.
- Each banker can manage 10 loans.
- The owner sets the deposit rate (1/3/5%), the loan rate (6/10/14%) and a lending standard (strict, normal or loose).

**Results:**

| Check | Result |
|---|---|
| .NET tests | 52/53 (only the known year test failed) |
| Python tests | 15/15 |
| Rule-mode 365-day run | All 12 `check_world` checks passed |
| Health at the end of the rule-mode year, without vs with banking | Fjord 63 → 81, Dune 0 → 39, Ionia 0 → 39 |
| Countries collapsing | 8 without banking, 6 with |

**Known quirks to settle when banking comes back:**
- War costs are still paid into the bank.
- With one bank per country there's no competition for deposits.
- A loss-making rate setting is allowed (for example a 5% deposit rate with a 6% loan rate).
- The rules note grows by about 250 cached tokens.

---

## 5. Known problems on the current branch

1. **Year-long collapse (calibration).** In rule mode, 8 of 10 countries reach health 0 within a year, and the year test fails. This is accepted until the live run has been analyzed. The traced causes are in 4.5: medicine runs out with one researcher, clinic staff are fired and rehired in a loop, a closed sector never reopens, and welfare drains demand.
2. **Planned economies collapse.** The state props up firms until its treasury is empty.
3. **War costs flow to the bank.** Declaring conflict (5,000) and weekly upkeep (1,000) are paid to `{country}_bank`. Separately, the dictatorship authority check reads the bank's cash. Both need a decision before or during the bank merge.
4. **Entrepreneur menus are long,** about 30 options with imports. Ahmad should know this for the worker.
5. **Product quality** is deferred.
6. **The observer hasn't been visually checked** since the labor-market changes.

---

## 6. Refactor and performance review

### 6.1 Measurements

These were taken on Ricky's Mac with a Release build, a scratch benchmark outside the repo, and 28 days of rule-driven economy.

**Engine time per phase:**

| Population | Daily economy | Weekly economy | Observe the whole weekly cohort | Copy and serialize the world |
|---|---|---|---|---|
| 1,100 | 27 ms per tick | 26 ms | 145 ms | 24 ms |
| 3,000 | 64 ms per tick | 33 ms | 154 ms | 18 ms |
| 6,000 | **262 ms per tick** | 55 ms | 276 ms | 29 ms |

**Checkpoint size** at 1,100 agents after 4 weeks of decisions:

| Part | Size |
|---|---|
| **Whole checkpoint** | **5.3 MB** |
| Agents | 3.9 MB |
| – of which `Agent.LastDecision` | **2.6 MB** |
| `World.Decisions` (last 256) | 1.1 MB |
| Events (hot window of 512) | 0.2 MB |

**Reading these numbers:**
- At 1,100 agents the engine isn't the bottleneck. The model is, and the 2 s day interval is far above 27 ms.
- The daily economy grows faster than the population. Doubling from 3,000 to 6,000 agents makes it 4× slower. That only matters if model downgrades make much larger worlds feasible.
- The checkpoint is the real cost today. It's saved every 5 ticks, grows as more agents get their first decision, and goes to Neon as one HTTP request. With full coverage it should reach about 6–7 MB at 1,100 agents. This is an estimate; it hasn't been checked against Neon's request limits.
- The full test suite takes about 9 s. The year test alone takes 8 s.

### 6.2 Findings, ranked

| # | Finding | Evidence | Proposed change | Behaviour change | Effort |
|---|---|---|---|---|---|
| R1 | **Checkpoint bloat.** Every agent keeps a full copy of its last decision, including the whole observation (about 2.5 KB each), and `World.Decisions` keeps another 256 copies | 2.6 MB of 5.3 MB is `LastDecision` | Store `LastDecision` without `Job.Observation`. The observer already falls back to the `decisions` window to inspect an observation | None to the simulation. The observer can only show the observation for the last 256 decisions | Small |
| R2 | **Each commit does the same work several times.** `Commit` round-trips the whole world through JSON (`Wire.Copy`) for the API copy. `store.Save` serializes it again. `LocalStore.Save` also reloads and parses the previous checkpoint to check its revision | `Coordinator.cs` Commit, `Store.cs` LocalStore.Save | Serialize once per commit and reuse the string. LocalStore keeps the last revision in memory | None | Small |
| R3 | **Household shopping scans every agent.** `SocietyEconomy.Sellers` walks all agents each time it's called: twice per resident per day, plus every doctor visit (`OpenClinic`) | Micro-benchmark: 0.037 ms per call at 6,000 agents, the main cause of the superlinear daily cost | Build one per-day index of firms by country and sector in `Day` (and for `Week`), and rebuild it when a firm starts or closes | None | Medium |
| R4 | **Eight hand-written firm queries.** Eight places repeat `Role == "business" && CountryId == … && Specialty == …` instead of using `SocietyLabor.Firms` | `SocietyEconomy.cs:298, 348, 359`, `SocietyLabor.cs:169, 200`, `SocietyTrade.cs:31`, `SocietyPayments.cs:79`, plus the `Firms` helper itself | Route them through `Firms`, so R3's index speeds them all up | None | Small, best done with R3 |
| R5 | **Staff lookups scan every agent.** `Staff()` and `Workers()` are called repeatedly per firm per day (production, service capacity) and per week (autopilot twice, layoffs twice, matching, payroll, price review) | 0.022 ms per call; part of the daily cost | Group staff by employer in the same index as R3 | None | Small, with R3 |
| R6 | **Rules note can drift from the constants.** `RulesNote` writes the numbers out by hand (8 weeks, 10%, 14 days, 20%, 5,000/1,000, 7 days) | `SocietyEngine.cs` | Build the note from the constants with string interpolation | None, while the values are unchanged | Small |
| R7 | **Unused state fields** are serialized into every checkpoint and snapshot: `SocietyState.InstitutionCadenceTicks`, `CitizenCadenceTicks`, `MeasuredDecisionsPerSecond`, and `Nation.TurnedAwayPatients` | No references outside their declarations | Delete them | None | Trivial |
| R8 | **Memory append-and-trim is duplicated** | `SocietyEngine.cs:194`, `SocietyPayments.cs:47` | One `Remember(agent, text)` helper | None | Trivial |
| R9 | **Stage-1 code path still ships** (about 700 lines): `Engine.cs`, `WorldFactory.cs`, `ActionValidator.cs`, the stage-1 loop and `SelectAgents` in `Coordinator`, `World.StageOneSnapshot`, `MedicineRoutePlan`, `SchedulerCursor`, the stage-1 prompt and JSON-schema path in `worker.py`, `ops/check_stage1.py`, and the stage-1 tests | Only reached when `FAULTLINE_WORLD` isn't `true` | Delete it if no one demos stage 1 any more. **Keep `RoutePlanner`**, which trade uses | None to the world sim; breaks stage-1 demos | Medium, needs the team to agree |
| R10 | **Legacy credit on this branch** (`request_loan`, `approve_loan`, `Credit.DueMinor`/`DueTick`, `SettleCredit`) | The bank branch replaces all of it | **Don't touch it here.** It disappears when the bank branch merges | — | — |

**Checked and not worth doing:**
- **Hashing:** `SocietyFactory.Sample` uses SHA-256 per random draw, but it measured 0.26 µs per call, about 5 ms per tick at 6,000 agents. Replacing it would also change every seeded result.
- **Menu rebuild:** `Validate` rebuilds the whole menu for each decision. It costs little at about 260 decisions a week.
- **Decision cuts:** reducing how often agents decide was declined in 4.6, so none of the proposals above reduce model calls.

### 6.3 Suggested order

1. **Now, if the live run shows slow commits or Neon errors:** R1 and R2. Together they cut the saved data roughly in half and remove redundant work, with no change to behaviour.
2. **Trivial cleanup, any time:** R7 and R8.
3. **Before running more than about 3,000 agents:** R3, R4 and R5 together as one change.
4. **When the bank branch merges:** R10 resolves itself. Also decide where war costs go and what the dictatorship authority check reads (section 5, item 3).
5. **When the team agrees:** R9.

Every item should pass the full test suite, keep the rule-mode year output identical to before, and pass `check_world` on a 365-day run. R1–R8 shouldn't change any simulated number, so an identical year-probe result is the check.

**Merge note:** the bank branch edits `SocietyEconomy`, `SocietyLabor`, `SocietyEngine`, `SocietyActions` and `World`. R3–R5 touch the same files. Merging the bank branch first and refactoring after avoids a painful rebase. R1, R2, R7 and R8 barely overlap with it.

---

## 7. Next steps

### Before the deadline (Oct 11, 10:00 a.m. EDT)

1. **Analyze the team's live run** of `ricky/labor-market` when it finishes. Compare it with run `70b725` and the rule-mode year probe:
   - Unemployment, startups and closures, job switches, prices at the floor, the health and quality-of-life trend, and which countries collapse.
   - How the model's choices differ from rule mode.
2. **Decide calibration** from that run: reinstate any of the vetoed #1, #2, #4 and #5, or fix those chains differently.
3. **Optionally apply R1 and R2** if the run shows checkpoint or Neon pressure.
4. **Visually check the observer** against the run.
5. **Update the design docs** (`faultline-current-plan-and-design.md`, `08-world-runbook.md`) with whatever ships.

### After the run (next update)

1. **Bring banking back:** re-test `labor-market-potential-bank` on top of any calibration changes, settle the quirks in 4.8, then merge.
2. **Decide who makes bank decisions long-term** (Ricky's open question).
3. **A second bank per country** if a smaller model makes more agents affordable.
4. **Product quality** (premium vs cheap goods).
5. **Benchmark gpt-oss-20b** with real prompts (`ops/benchmark_spark.py`) before switching models (option C).
6. **Performance refactors** R3–R5 if the population grows.
7. **Stage-1 removal** (R9) if the team agrees.

---

## 8. Open questions for Ricky

1. Which refactors should go in now: R1 and R2 only, the trivial R7 and R8 as well, or wait until after the run?
2. Is stage 1 still demoed by anyone? This decides whether R9 is safe.
3. Where should war spending go once the bank is a real lender: the government only, or soldiers' wages?
4. Should the dictatorship authority check keep reading bank cash? With banking, that cash is depositors' reserves.

---

## 9. Working agreements

- Design questions are answered before economy changes go in. Requested changes are remembered until approved.
- Stay on `ricky/labor-market`; no new branches unless asked. The bank work was moved by Ricky.
- Commit and push only when asked.
- Never put credentials, API keys or connection strings in docs, the board or chat.
- Don't propose cutting who makes decisions (speed option B was declined).
- War and conflict features are a stretch goal.
