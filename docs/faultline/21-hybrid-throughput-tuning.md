# Hybrid concurrency and world tick tuning

Spark A and Spark B each serve Qwen3-8B for institutions and Qwen3-4B for
citizens. AMD's existing Qwen3-4B endpoint keeps one shared generation slot.
The laptop owns the authoritative world state and runs daily economy updates.

The provider pool routes a decision only to an eligible endpoint, favors
dedicated role capacity, and selects the least occupied endpoint relative to
its slot limit. Faster endpoints free slots sooner and therefore receive more
requests. Backend failures cause a cooldown. Increasing the per-provider
limits also raises global admission; population does not set concurrency.

Qualification uses a fresh 1,000-agent world, the same seed, local storage,
28 daily ticks and no artificial tick interval. Both models stay running on
both Sparks. These are short end-to-end checks, not a sustained capacity
maximum. Model decisions can change the subsequent world and number of
requests, so the runs are not identical fixed workloads. Caches also become
warmer between runs.

The live command for maximum tick speed is:

```bash
.venv/bin/python ops/run_stage1.py --world --live --compute hybrid \
  --storage local --population 1000 --interval 0
```

The default interval without this option remains two seconds. Zero interval
removes pacing but preserves the weekly barrier: model turns finish before
the next payroll and weekly review. Removing that barrier would change the
simulation and could expire strategic decisions before they apply.

A separate local CPU profile advanced 120 rule-mode days without inference.
Median `SocietyEngine.Advance` time was 5.30 ms and its 95th percentile was
21.26 ms. Median serialization plus read-only copy time was 26.09 ms at each
five-day checkpoint. These measurements exclude model waiting and are not
live ticks per second. They identify inference as the first tuning target
at this population; increasing GPU concurrency does not distribute the
authoritative economy across GPUs.

Qualification reconciles cash, goods, event sequences and request bounds.
Reports separate model transport/validation fallbacks from world action
rejections, such as a precondition changing after an observation. Higher
concurrency does not bypass either kind of validation. Credentials and
endpoint URLs remain in ignored `.env.local` and are omitted from evidence.
