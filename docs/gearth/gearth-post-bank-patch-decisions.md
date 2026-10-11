# Post-bank patch decisions — October 11, 2026

Source: Ricky's signed-off decisions supplied by Jayden, following run `world_20261011_055151_b0315c`. Base: `ricky/post-bank-patch-1`, commit `6447724` (`in progress patch`).

The run showed a market slump: owners chose price increases 87% of the time, food reached 2.4× base, wages stayed flat, unemployment and hunger rose, and treasuries and banks accumulated money.

## Accepted changes

- Entry drops the national quality-of-life gate. A gap requires a sales-weighted average price at least 1.5× unit cost, unmet demand (priced out, turned away, less than seven days of stock, or at least 80% service capacity), and an idle worker able to staff the firm. An empty sector always counts. Schools use occupied seats with fixed tuition. Prompt examples use placeholder actions.
- Cash shortages cause layoffs down to a minimum crew of one unless the owner works, before a 10% pay cut. Every four weeks profitable firms pass half the food-and-energy cost-of-living rise into pay.
- A small non-bank firm (at most one employee) closes when its owner goes hungry and unpaid for two consecutive weeks and firm cash plus deposits cannot cover another week of payroll plus the owner's former table wage. Larger firms use the distress rule. Jayden explicitly retained this partial-commit restriction after reviewing the annual calibration result on October 11, 2026; it narrows the original signed-off owner-exit wording.
- Private bank equity is 15% of the old opening seed; the remainder becomes the owner's deposit. Planned state banks keep their original capital. Lending standards stay unchanged; rejected freight loans lacked revenue.
- Government budget levers are integer ranges showing the current level, including unemployment benefit 0–900. Conflict choices explain their effects and require a failed offer between the pair.
- Planned-economy realism remains deferred to `gearth/phase-1-resources`. Add a productivity knob only if planned economies still lead after that run.

## Follow-up analysis

In the next banks-on run, inspect entry, food prices relative to wages, layoffs and pay reviews, owner exits, bank-owner savings, fiscal choices, and conflict decisions. These changes restore entry, wage catch-up, exit, bank capital and fiscal response; their effect on live model behavior still needs measurement.

The broad owner-exit alternative produced 19 unemployed residents out of 94 (20.2%) in Jade after a rule-only year, above the existing test's 20% bound. Jayden chose to retain the small-firm restriction. The test bound remains unchanged, and the broad alternative is not part of this patch.

## Local verification

- `dotnet test GEarth.sln --no-restore`: 187 passed, including the full-year calibration and daily money/goods conservation.
- `.venv/bin/python -m pytest agents -q`: 80 passed.
- `npm run test:observer`: 9 passed; the recorded live/stopped-run integration check was skipped without its configured API.
- `.venv/bin/python docs/gearth/validate_design.py` and `git diff --check`: passed.

Live inference and cloud deployment were not part of this local verification.
