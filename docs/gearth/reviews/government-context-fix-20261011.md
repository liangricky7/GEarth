# Government context overflow fix

October 11, 2026. Institution servers retain their existing 8,192-token context and 512-token output allowance.

Government choice prompts previously measured 8,292–9,413 input tokens and were rejected before generation. The worker returned fallback holds and placed healthy providers in a 30-second outage cooldown.

The worker now uses a government-specific rules prefix: a brief economic/banking overview followed by the engine's exact government, infrastructure, planning, trade and war rules. Full country observations and the action menu are retained. Repeated same-schema observation records are encoded with shared fields, column names and ordered rows; no record or field is discarded. The same representation is used for numeric-values requests. Citizen and other institution prompts retain their existing rules.

Context-related HTTP 400/422 responses are categorized as `model_context_exceeded`, remain counted as failures, and do not quarantine the provider. Private server response bodies are not logged.

## Validation

- 94 worker/provider tests pass, including preservation of records, menu/parameter validation, citizen behavior, and context-error cooldown handling.
- Actual Spark B Qwen3-8B inference returned valid actions for all 20 saved government jobs: all 10 countries at ticks 0 and 48.
- New choice inputs measured 5,521–6,495 tokens; maximum input plus 512 output is 7,007, leaving 1,185 tokens below the server limit.
- Results: `docs/gearth/evidence/government-context-fix-20261011.json`.
- The saved government cases had fixed-parameter menus; they did not exercise a live numeric-values request. Numeric-values representation and range validation are covered by tests.

This validates removal of the reproduced context failures. It does not establish a whole-simulation speedup or guarantee arbitrarily large later-state observations fit. A future context overflow is now explicitly classified without treating the model server as down.
