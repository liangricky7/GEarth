# Faultline integration handoff

Updated October 10, 2026 (America/New_York). The maintained specification is [current plan and design](../../faultline-current-plan-and-design.md); the earlier branch comparison is [design vs. current](../../faultline-design-vs-current.md). This supersedes the local handoff written before Qwen completed deployment. That original handoff was preserved privately under ignored `.data/private-config/`.

Main combines Ricky's daily labor-market engine and weekly cohorts with Jayden's compute pool, Qwen setup/recovery, seeded endowments, bounded cancellable queue, API metadata, observer consistency, private relays, automatic reports and sharing supervisor. The full banking branch remains deliberately separate.

The previous live simulation is stopped. Local observer/worker and Spark SSH forwards were stopped; remote model services were left available. Building or merging does not restart a simulation or deploy new model servers. Another teammate's Spark workload is not managed by this integration.

Verified earlier in this session: Spark A serves authenticated Qwen3-8B on loopback 8000 and Qwen3-4B on 8001. Laptop configuration enables 4 institution slots, 8 citizen slots and 1 shared AMD Qwen3-4B slot. AMD's health and actual decisions passed. Spark B and AMD's second model remain unverified. Private configuration is in ignored `.env.local`; credentials are never part of this handoff or Git.

The world now uses simulated weeks rather than wall-clock cadences. Every institution/entrepreneur decides weekly; citizens rotate over eight weeks. Owned businesses act through entrepreneurs, and state businesses decide themselves. Both queues drain before payroll and before completion. Role-specific capacity protects citizen slots while institutions are queued. Shutdown cancels pending jobs, clears queued jobs, commits stopped state and exports notes.

Endowment profiles preserve wealth, productivity and preferences. Initial cash is redistributed within each country's existing two-week-pay budget. Productivity affects goods production. The newer fixed-salary rule takes precedence over old revenue-share payroll: bargaining values remain metadata. The observer keeps backend/model attribution, committed ages, decision-maker counts and the available observation window.

Known limits: upstream `MoneyKeepsCirculatingForAFullYear` remains a visible calibration failure; the old hybrid run accumulated HTTP-error fallbacks. Neither a maximum population nor a full live-year time is verified for the merged scheduler. Banking/calibration changes remain deferred per the design. Browser layout has not been visually inspected. Saved checkpoints support inspection, not execution resumption.

[Integration evidence](evidence/main-integration.json) records exact checks; [synthetic lifecycle evidence](evidence/multi-compute-scheduler.json) checks admission, completion, cancellation and notes without real inference. The previous full-population qualification was stopped at the user's request and was not repeated.

To start later, after an operator chooses to run:

```bash
.venv/bin/python ops/run_stage1.py --world --live --compute hybrid --storage neon
```

See [world runbook](08-world-runbook.md), [Qwen installer guide](18-all-node-qwen-setup.md), [AMD guide](12-amd-compute.md) and [Spark B relay guide](15-ken-spark-b-relay.md). The permanent observer address and private configuration remain unchanged. Unrelated archive, traffic and other local project documents were excluded from the integration.
