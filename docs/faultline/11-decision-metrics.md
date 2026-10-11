# Decision measurements

New world runs record each admitted request ID and its applied or canceled outcome. The checker reconciles `admitted = applied + pending + canceled`, rejects duplicate outcomes and compares source totals with the committed counters.

Model responses, accepted AI actions, rejected actions and fallbacks are separate counts. Timeouts are a subset of failures; an `EXPIRED_PROPOSAL` rejection is not a model timeout. HTTP errors retain their origin and status without response bodies or exception messages.

Worker duration, coordinator round-trip duration and committed decision age remain distinct. Snapshot latency covers the latest 200 positive worker durations from all sources; the report labels its full-run model-source sample separately.

## Check locally

```sh
python -m pytest agents/test_worker.py ops/test_decision_metrics.py -q
dotnet test Faultline.sln
python ops/check_decision_metrics.py --output artifacts/local/decision-metrics/integration.json
```

The integration check uses a controlled worker on local ports 5080/8100, then stops a second run and reads its saved checkpoint. It makes no Spark or Neon calls. Existing processes on those ports must be stopped first.

For a completed or stopped run, `python ops/check_world.py --request-id REQUEST_ID` follows one request. For downloaded archives:

```sh
python ops/decision_metrics.py --events events.json --snapshot snapshot.json --request-id REQUEST_ID --output report.json
```

Legacy runs lack admission/cancellation records. Their applied totals can reconcile, but the report leaves dispatch totals unknown. Abrupt process termination preserves only the last durable checkpoint; it cannot claim graceful cancellation after that boundary.

## Verification and integration

See [recorded results](evidence/decision-metrics.json). The focused branch builds on Jayden’s `stage2-scheduling-api` at `f60d36f`. Its combined candidate with main at `0833da5` passes 41 Python tests, 68 C# tests and the full API/storage/request/cancellation check. That candidate preserves Ricky’s newly merged economic changes. Main integration still requires the scheduling dependency; this task does not deploy the observer or qualify capacity.
