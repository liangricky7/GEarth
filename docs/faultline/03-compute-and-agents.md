# Faultline — compute and individual agents

## Actual resource boundary

| Resource | Evidence and use |
| --- | --- |
| Two DGX Sparks | User-confirmed access through Tailscale; Jayden and Kennedy alone have SSH access. Provision independent inference replicas. |
| Hugging Face credits | User reports $20 of credits. Eligible services, expiration, and account balance are unverified. Consider supported hosted models for bounded prompt comparisons and demo fallback after checking credit eligibility. |
| AMD ROCm | Sponsor option; credits and usable machine are not confirmed. Activate a worker only after inventory and smoke tests. |
| Backend host | Still to be chosen. It must reach Neon and private inference endpoints. |
| Models and runtime | The first Spark has a working authenticated vLLM API serving `openai/gpt-oss-120b` with MXFP4. Second-node configuration and sustained throughput remain unverified. |

The 500-agent plan depends on the two confirmed Sparks; AMD adds capacity. A public hosting provider cannot automatically reach a laptop's tailnet. The simplest demo topology is a coordinator on a team machine connected to Tailscale and a locally served viewer. Remote public viewing needs a separately configured gateway and is not a deadline dependency.

Kennedy owns compute bootstrap, Jayden is the SSH backup, and Ahmad supplies model configurations and tests inference through the backend gateway. Ricky and Ahmad need no SSH permissions. Passwords, tokens, and private keys do not belong in documents or fixtures.

## Qualification and networking

On each Spark, record GPU/memory, CPU architecture, driver/runtime versions, running model services, free storage, and container images. DGX ARM64/Blackwell-compatible containers are required; generic x86 CUDA wheels are not a verified deployment. NVIDIA provides a [Spark-specific vLLM workflow](https://build.nvidia.com/spark/vllm/overview). AMD documents [vLLM on supported ROCm hardware](https://rocm.docs.amd.com/projects/ai-ecosystem/en/latest/inference/vllm.html). Use each platform's runtime and independent replicas; do not shard one model across NVIDIA and AMD.

For services bound to loopback, an SSH-capable teammate can establish tunnels on the backend host:

```text
ssh -N -L 18001:127.0.0.1:8000 <spark-a-alias>
ssh -N -L 18002:127.0.0.1:8000 <spark-b-alias>
```

Aliases are unresolved placeholders. `SPARK_A_BASE_URL=http://127.0.0.1:18001/v1` and `SPARK_B_BASE_URL=http://127.0.0.1:18002/v1` apply only to that tunnel host. Containers need the host's reachable address instead of their own loopback. Alternatively bind to a restricted Tailscale interface with authentication. Browser clients never call these nodes directly.

Private configuration records endpoints, model IDs, capacities, timeout, and enabled flags. The AMD worker defaults disabled. Benchmark environment variables are separate from signing/database credentials; workers receive neither.

Hugging Face credits are optional hosted capacity. Running downloaded public model weights on the Sparks does not consume HF inference credits. If the reported credits cover Inference Providers, use [HF-routed requests](https://huggingface.co/docs/inference-providers/pricing) so eligible usage is billed to the HF account; custom provider keys bill the provider separately. Keep any hosted fallback behind the same backend gateway, apply a bounded request/token budget, and record model/provider provenance. Hosted prompt tests can compare behavior; they do not measure Spark throughput. No hosted calls have been made and no credits have been spent by this project.

### First Spark smoke test — October 10, 2026

SSH access was verified through Tailscale as `hackathon`. User-provided hardware output identifies NVIDIA GB10, driver 580.95.05, and driver-reported CUDA compatibility 13.0. Docker lists an existing `vllm_node` container using image `vllm-node-mxfp4`, plus a healthy Open WebUI container. This Spark account needs `sudo` for Docker access; the AMD root-account instruction does not apply to it.

A read-only process inspection identifies `vllm serve openai/gpt-oss-120b`, MXFP4 quantization, GPU memory utilization configured at 0.8, and an API listening on port 8000. Authenticated `/v1/models` returned that model ID. One `/v1/chat/completions` request with low reasoning effort, temperature 0, and a 256-token output cap returned exactly `{"status":"ok"}` in 0.619 seconds; the response reported 77 prompt tokens and 23 completion tokens. Credentials were used in memory and were not printed or saved in the repository. No service configuration was changed.

This establishes that one existing model API responds. It does not establish action-schema compliance, sustained concurrency, 500-agent coverage, second-Spark readiness, or recovery behavior. Reuse this service for the first agent prototype and measure the planned workload before deciding whether a smaller model is needed. CPU architecture, available host memory/storage, vLLM version, container digest, and model revision still need recording.

A subsequent [small synthetic agent probe](evidence/first-spark-model-probe-20261010.json) tested four citizen/business cases twice: food affordability, an affordable medicine detour, and urgent delivery. All eight responses passed the controlled action-schema validator, selected a supplied legal candidate, cited only known event IDs, and chose the expected action. Prompts used 586–673 tokens; completions used 75–144 reported tokens, with low reasoning effort, temperature 0, and a 256-token cap. Four sequential requests took 7.329 seconds (0.546 valid decisions/second); four concurrent requests took 3.540 seconds (1.130 valid decisions/second), with maximum request latency 3.539 seconds. These are short batch measurements, not sustained throughput or a representative quality evaluation; only two roles were sampled, and cache warming and other workloads were uncontrolled. No model service or network configuration was changed.

Keep the working 120B service for the prototype. Larger concurrency sweeps and the second Spark remain necessary before choosing the final deployment. If the combined workload misses the target, compare a smaller citizen model on the second Spark while retaining 120B for institutions. Model routing must be recorded per agent/decision so that hardware and quality comparisons account for differing models.

## Models and action output

Qualify [Qwen3-4B](https://huggingface.co/Qwen/Qwen3-4B) and [Qwen3-8B](https://huggingface.co/Qwen/Qwen3-8B), Apache-2.0 models on Hugging Face. Start with non-thinking decisions using the supported `enable_thinking=false` template option. Save model/tokenizer revisions, serving container digest, precision, and sampling parameters for each worker. These candidates remain untested on the actual devices.

Initial budgets: target about 700 input tokens, hard cap 1,024; 160 output tokens for citizens and 256 for institutions; 2,048 total context allocation. Pick supported precision from the runtime. Do not assume an NVIDIA quantized checkpoint runs unchanged on Radeon. Select the candidate with the best validated decision throughput, not the largest checkpoint that fits memory.

[vLLM structured outputs](https://docs.vllm.ai/en/latest/features/structured_outputs/) can constrain JSON; current documentation uses `structured_outputs` or supported JSON-schema response formatting. Map the pinned runtime to [the action schema](contracts/action.schema.json). Independently validate syntax and engine semantics. Disable verbose reasoning; store short stated rationales as explanations, not proof.

## Individual identity and information

Each agent has distinct goals, holdings, need, personality parameters, local relationships, and action history. Shared model weights do not mean shared memory. Seed citizen attributes and connections individually.

Observations contain owned balances, visible local prices, received messages, relevant public events, and a legal candidate menu. Use at most eight relevant memories, six received messages, and four outgoing social neighbors per citizen. Retain full provenance in Neon while compacting the prompt. On overflow, trim old low-priority memories before pending obligations and unmet needs. No vector database or separate summarizer is needed for the deadline.

Messages propagate along permitted social edges on later ticks. Governments see public/country aggregates, not private citizen states. A rumor remains unverified; message text cannot become a trusted engine instruction.

## Scheduling for 500 agents

There are 25 institutions and 475 citizens. Start institution decisions every five ticks and citizen decisions every 60 ticks. Stagger due times by stable ID hash. Relevant events wake affected agents with a ten-tick cooldown; coalesce repeated events and allow only one outstanding request per identity.

At three wall-clock seconds per tick, baseline demand is `25/15 + 475/180 ≈ 4.31 requests/second`, before crisis bursts. This is a workload estimate, not measured capacity. Target combined capacity of six valid decisions/second and institution p95 response time under 12 seconds. Measure prefill and queueing as well as output tokens.

Use priority queues with aging: urgent institutions, due institutions, affected citizens, then ordinary citizens. Reserve at least 30% of dispatch slots for ordinary citizens while their backlog exists. Start concurrency sweeps at 1, 2, 4, 8, and 16 per node; stop when useful throughput plateaus or latency fails. A 15-second request deadline includes queue delay; permit at most one retry inside that deadline. Unhealthy workers circuit-break. Never turn retries into simultaneous competing actions from one identity.

Any healthy replica can handle any identity. Distribute institutional requests across both Sparks; route citizens to the least-loaded node. If AMD becomes available, assign meaningful citizen/business decisions and record their effects. Compare matched request workloads with/without AMD. If the models differ, hardware throughput and decision-quality comparisons are separate experiments.

## Measurement and gates

Scale tests: 50 → 200 → 500 agents. Try 1,000 only after 500 passes. Record raw completions and accepted decisions/second, valid-action rate, p50/p95 response time, queue age/depth, tokens, timeouts, per-worker accepted decisions, and unique model-deciding identities. Memory reservation is not evidence of useful GPU computation.

The 500-agent run passes when every identity records at least one valid model decision during 120 ticks, at least 95% record one in each 60-tick window, institutional latency meets target, and the queue remains bounded. `hold` counts only if genuinely returned by the model; fallback holds do not count. Display coverage and fallback percentage.

If capacity is insufficient, tune prompt size, serving, cadence, or wall-clock tick duration while retaining 500 identities and reporting the actual cadence. A smaller debugging world is not the final population target. If the final target cannot be met, disclose the actual result.

By Saturday 7:00 a.m., get a valid action from both Sparks. By noon, record the first 500-agent load result. Check AMD allocation by Saturday 10:00 a.m.; if unavailable, stop setup work until allocation status changes. Missing AMD removes the AMD submission claim, not the core society.

## Handoff

Kennedy records launch/tunnel recovery instructions; Ahmad records model/prompt hashes and capacities; Jayden records backend location and coordinator recovery. Capture one full measured run for a labeled replay if connectivity fails.
