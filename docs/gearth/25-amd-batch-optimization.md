# AMD batch optimization — October 11, 2026

The existing Radeon Pro W7900D endpoint now serves Qwen3-4B with token-budgeted Transformers microbatches. Following the owner's request for GPU headroom, the shared simulation worker has **32 AMD citizen slots**, alongside two 32-slot Spark citizen providers and two 16-slot Spark institution providers. Its total configured capacity is **128**. The optimization keeps the cached model weights, ROCm runtime, bearer authentication, public tunnel URL and loopback port 8180.

## Citizen generation budget

Citizen/resident world decisions now request at most 128 output tokens instead of 512, with a brief reason of at most 40 characters instead of 100. Numeric-value requests for those roles use the same 128-token budget and retain their schema, bounds validation and bounded repair. Institutions and entrepreneurs retain 512 tokens. Qwen thinking was already disabled on all providers; this change shortens requested explanations rather than changing a supported reasoning-effort level. Existing validators still accept their original reason bounds to avoid new failures solely from a longer explanation.

The worker-only update passed 104 focused worker, provider and AMD batch tests. Deployment preserved the same world at day 223, its existing pause and the 128-slot provider pool. No new simulation or throughput benchmark was started. A smaller output limit is not a proportional speedup: generation already stops before its limit when a response is complete, and institution work may dominate total run duration.

## Current headroom policy

Generation pacing targets **90% average utilization**. Each decode step synchronizes the selected GPU and inserts idle time according to measured work duration, with a three-point margin and additional backoff when a sensor sample exceeds 95%. Batch size and admission are 32, the padded token budget is 65,536, and collection time is 20 ms. Utilization targets above 95 are rejected. The utilization sensor follows the model GPU's PCI identity because DRM card numbers changed during deployment.

This is **not a strict instantaneous utilization ceiling**. Both raw sensor readings and the cloud dashboard still caught brief 100% readings in the paced confirmation test. Individual GPU kernels can saturate the device before the next pacing interval; software pacing limits average duty and cannot guarantee that every dashboard sample stays below 95%. The observed dashboard also showed 81% during load. No failures were observed in the short tests; this is not a guarantee of future agent reliability.

| Current public HTTPS test | Valid choices | Choices/second | p95 latency | Minimum free VRAM |
| --- | --- | --- | --- | --- |
| 32 concurrent requests, three waves | 96/96 | 2.484 | 14.18 seconds | 29.67 GiB |
| 32 concurrent requests, six waves | 192/192 | 2.770 | 12.78 seconds | 29.56 GiB |

Both tests reported zero OOM splits. Full worker validation passed 16/16 actions, including eight numeric-range actions. Focused automated checks passed 75 tests. Compared with the previous 64-slot public test, the six-wave test traded throughput for shorter tail latency and more free VRAM. See [the headroom receipt](evidence/amd-headroom-20261011.json) for the tests and live verification.

The owner accepts brief utilization spikes; keeping the simulation progressing is the priority. The existing worker bounds each decision by its timeout and original deadline, applies a 30-second provider cooldown after transport errors/timeouts, and returns explicit fallback holds. The engine also bounds worker calls by the decision deadline, retries capacity responses only within that deadline, and converts unavailable inference into attributed fallback decisions. The AMD server limits queue reservations and splits OOM batches, and its supervisor restarts crashed processes. These guards reduce overload and prevent ordinary inference failures from blocking simulation progress; they do not cover every possible infrastructure failure. Live verification reached day 154 in the same saved world, with 74 AMD decisions and zero failures across all five providers after the worker change.

## Earlier maximum-throughput sweep

The sweep used 32 recorded fictional citizen observations and the current choice-only prompt, with up to 512 generated tokens per request. Each result required a valid available choice, bounded reason and completed generation.

| Test | Requests | Valid choices/second | p95 latency |
| --- | --- | --- | --- |
| Single-request baseline | 32/32 valid | 1.268 | 1.13 seconds |
| Cloud loopback, concurrency 32 | 96/96 valid | 2.898 | 16.53 seconds |
| Cloud loopback, concurrency 48 | 144/144 valid | 3.005 | 19.87 seconds |
| Cloud loopback, concurrency 64 | 192/192 valid | 3.336 | 22.31 seconds |
| Production HTTPS, concurrency 64 | 192/192 valid | 3.152 | 22.05 seconds |

The dashboard showed 98–100% GPU utilization under load. Production retained at least 18.83 GiB free VRAM in sampled health readings and reported no OOM splits. The loopback sweep temporarily kept the original server resident as a rollback option; production runs one model server. A separate worker probe accepted 16/16 full actions, including eight forced numeric-range actions using the two-stage validator. Focused automated checks passed 71 tests.

These are short load tests, not proof of the absolute hardware maximum or a sustained simulation throughput guarantee. Batch throughput comes with higher per-request latency. Numeric-range actions can require an additional model request. The single-request baseline measures the same model/runtime and fixtures; it is not a historical old-server benchmark. GPU utilization falls when the simulation has no pending decisions.

Credential-free measurements are in [the receipt](evidence/amd-batch-20261011.json). Detailed request receipts and the dashboard screenshot remain under ignored `.data/amd-optimization/`.

## Serving and routing

`ops/amd_batch_server.py` groups compatible model/output-limit requests for a 20 ms collection window, uses left padding and KV caching, and currently admits at most 32 requests. A padded input-plus-output token budget of 65,536 limits each batch. Input plus requested output is bounded to 8,192 tokens per request. The scheduler serializes GPU generation and tokenizer access, tracks reservations through cancellation, and splits a batch after an OOM. Failed responses remain explicit errors and pass through the existing worker validation/fallback behavior.

The implementation retains the original server's greedy generation behavior. It supports `json_object`; the worker's existing fallback handles unsupported `json_schema` before validating numeric fields. All routes, including `/health` and `/admin/tuning`, require the existing bearer key. Health includes queue, batch, failure and memory counters. Authenticated tuning persists bounded settings in `batch-tuning.json`.

The cloud files live in `/workspace/gearth-amd-batch`. `amd_cloud_control.py supervise` restarts the production server after a crash. Its private `production-launch.json` and `rollback-launch.json` preserve launch environments; never publish them. The original `/workspace/faultline-amd` source and key remain available. The existing tunnel process still targets port 8180.

The GCP worker was drained using the engine's pause acknowledgement and replaced with the same application and credentials under `gearth-worker-amd.service`. That unit follows the main simulation service lifecycle. `/var/lib/gearth/config.env` and the owner's ignored `.env.local` now specify `AMD_CAPACITY=32`. The worker reserves AMD slots using the existing occupancy-based provider selection. The transient worker was restarted while drained to apply the smaller capacity. The stale public tunnel was reconnected with its original domain prefix; its URL and bearer authentication are unchanged.

The running world retains its startup engine admission cap of 97 and snapshot labels showing the original AMD capacity. The worker actually has 128 slots and AMD has 32. A new run derives engine admission and provider labels from the updated configuration. Preserving the live world avoids restarting its simulation state solely to refresh those labels.

## Recovery

Pause and drain the shared simulation before changing server capacity or rolling back. To restart the optimized endpoint, first verify the existing supervisor and server PIDs; start only one supervisor using `python /workspace/gearth-amd-batch/amd_cloud_control.py supervise` in the cloud environment. Keep the tunnel and key unchanged.

Private `production-launch.before-pacing.json`, `amd_batch_server.before-pacing.py` and `batch-tuning.before-pacing.json` preserve the initial batch deployment. The GCP `worker-before-pacing.json` and `config-before-pacing.env` preserve the earlier 64-slot routing. Keep those environment files private. Returning to that earlier configuration would remove the requested headroom policy.

For a rollback, stop that supervisor and its child, then launch the original arguments, working directory and environment from the private `rollback-launch.json`. Restore the GCP worker configuration from `/var/lib/gearth/amd-optimization/worker-before.json` and `config-before.env` before resuming. Those files contain credentials and must stay private. The cutover helper automatically restarts the original server if the initial batch deployment fails readiness.

`ops/gcp/tune_amd_capacity.py` is a one-time deployment helper for a drained worker. Do not rerun it blindly: its backup paths and transient systemd unit are deployment-specific. Verify authenticated AMD health, worker provider limits and successful live decisions after any capacity change.
