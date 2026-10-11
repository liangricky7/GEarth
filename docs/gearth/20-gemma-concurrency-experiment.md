# Gemma 4 E4B concurrency and replica experiment

The experiment completed on Spark A on October 10, 2026. All 960 timed
responses passed the action validator. Results are saved in the
[benchmark receipt](evidence/gemma-concurrency-20261010.json).

| Concurrent requests | Qwen3-4B, one instance | Gemma 4 E4B, one instance | Gemma 4 E4B, two instances |
| --- | ---: | ---: | ---: |
| 1 | 0.94 | 0.84 | 0.84 |
| 4 | 3.56 | 3.16 | 1.64 |
| 8 | 6.87 | 6.42 | 3.24 |
| 16 | 11.45 | 10.38 | 6.25 |
| 32 | 17.20 | 18.62 | 10.03 |

Values are valid agent decisions per second. At 32 concurrent requests,
single-instance Gemma was 8.3% faster than Qwen in this short sweep. At the
production citizen concurrency of eight, Gemma was 6.6% slower. Two Gemma
instances on the same GPU were 46.2% slower than one Gemma instance at 32.
Increasing concurrency helped both single-instance models more than adding
a second Gemma replica on the same Spark. This does not establish a sustained
hardware maximum or a full-simulation speedup.

At 32 concurrent requests, median / 95th percentile response latency was
1.65 / 1.97 seconds for Qwen, 1.58 / 1.88 seconds for one Gemma instance,
and 2.98 / 3.68 seconds for two Gemma instances. Raising Qwen concurrency
from eight to 32 increased isolated throughput by 2.50 times, while increasing
median latency from 1.15 to 1.65 seconds. Concurrency measures simultaneous
model requests, not the total population of the simulation.

The benchmark used an isolated GPU memory allocation with Qwen3-8B stopped.
Production serves both institution and citizen models together, so its
concurrency should be qualified under that combined load before applying
the benchmark's 32-request setting. No provider settings were changed. Both
original Qwen servers were restored and their authenticated model catalogs
verified. Experiment ports 8002 and 8003 are closed; the simulation remains
stopped.

The staged script is `/home/hackathon/faultline-gemma-test.py`. It uses
`ops/spark_gemma_experiment.py` and 32 citizen prompts generated from the saved
1,000-agent world through the engine's `SocietyEngine.Observe` and the production
worker's prompt builder. The prompts
contain simulation observations and no provider credentials.

Saved decision records omit their candidate menus, so they cannot be directly
replayed as prompts. Regenerate complete menus from the checkpoint instead:

```bash
.venv/bin/python ops/export_benchmark_prompts.py .data/runs/RUN_ID.json
```

The exporter and staged script both validate candidate counts against the
numbered action menu before any Docker operation. The current 32 prompts were
generated at day 104 and each has 10-17 actions.

Run after stopping the laptop simulation, in the Spark A SSH terminal:

```bash
sudo python3 ~/faultline-gemma-test.py test
```

Enter the sudo password in that terminal. Preparation pulls the pinned ARM64
runtime, finishes downloading the pinned Gemma weights, and checks the GPU,
model architectures, cached configurations and tokenizers before stopping any
existing model server. Download and startup can take several minutes.

The script preserves the exact IDs of the labeled Qwen institution and citizen
containers, stops them for isolation, and executes three trials:

1. Qwen3-4B in the experimental runtime with one server.
2. Gemma 4 E4B in the same runtime with one server.
3. Gemma 4 E4B with two independent servers on the same Spark GPU.

Each trial sweeps 1, 4, 8, 16 and 32 concurrent requests. Each level processes
the same 64 requests, after warming all 32 prompts on each replica. The total
GPU memory budget stays at 0.60: one server receives 0.60, or two servers receive
0.30 each. Weights use BF16, context is limited to 8,192 tokens, prefix caching
is enabled, thinking is disabled and responses have a 512-token limit. Gemma's
image and audio inputs are disabled. No multimodal inputs are sent.

It records accepted JSON decisions per second, median and 95th percentile
latency, and errors. A decision counts only if it chooses an available action
and passes the world contract's reason/schema checks. Higher load levels stop
after errors. Benchmark endpoints are authenticated and bound to loopback
ports 8002 and 8003. Requests are distributed across the two replicas.

Results save to `~/faultline-gemma-experiment/results.json`. The script removes
only its recorded, labeled experiment containers and restarts the original
Qwen containers by ID, then verifies their model catalogs. It does this after
both success and ordinary failure. The laptop simulation does not restart
automatically. If the process is killed before cleanup, recover with:

```bash
sudo python3 ~/faultline-gemma-test.py restore
```

Restoration refuses changed Qwen container identities or an experiment
container with a missing ownership label. Failed server logs are saved
privately in the experiment directory. Provider keys, permanent server
definitions, the existing Qwen cache, and laptop configuration are preserved.

Pinned artifacts:

- Gemma model: `google/gemma-4-E4B-it`, revision `ee0ef6023621cff504d758262d4e04895a5af4a2`.
- ARM64 runtime: `vllm/vllm-openai@sha256:2970dd3c8fd00bccbc663345f9b4d87b35c491f4b13a2a4cdef60fe5c015f6e2`.
- This digest was resolved from `vllm/vllm-openai:gemma4-cu130`, the tag in [NVIDIA's Spark playbook](https://github.com/NVIDIA/dgx-spark-playbooks/blob/main/nvidia/vllm/README.md).
- Qwen revision comes from Spark A's existing private preparation record.

These are short inference tests, not a sustained population qualification.
Output validity is not decision quality. Energy consumption and end-to-end
simulation/persistence speed are not measured. A faster Gemma trial does not
by itself prove lower electricity use. Unrelated GPU workloads can influence
the results. Spark B and AMD remain on their existing models during this test.
