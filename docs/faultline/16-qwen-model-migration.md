# Qwen3 institution and citizen models

Integration update (October 10): see the [maintained specification](../../faultline-current-plan-and-design.md) and [integration evidence](evidence/main-integration.json). Weekly cohorts supersede historical wall-clock eligibility fields; current agent summaries expose `decision_maker` and `next_attempt_due_tick`. Spark A's Qwen pair and AMD Qwen3-4B completed real decisions; Spark B and AMD's second model remain unverified. Earlier measurements below retain their original scope.

For the complete matching model setup across both Sparks and AMD, use the [all-node setup guide](18-all-node-qwen-setup.md). That guide supersedes this document's interim AMD 4B-only routing and adds a fresh Spark B installer. The shared manifest now pins checkpoint revisions for every node.

## Selected models

| Decision group | Model | Roles |
| --- | --- | --- |
| Institutions | [Qwen/Qwen3-8B](https://huggingface.co/Qwen/Qwen3-8B) | Governments, businesses, banks and media |
| Citizens | [Qwen/Qwen3-4B](https://huggingface.co/Qwen/Qwen3-4B) | Residents, including doctors, teachers, farmers and soldiers |

The user selected this pair on October 10, 2026, replacing the Qwen3.8-27B proposal. Both models support non-thinking operation. Preferences, productivity, wealth and bargaining power remain independently seeded simulation parameters.

Routing uses agent role, not occupation, wealth or government type: Stage 2 reads `self.role`, Stage 1 reads `agent.role`. The citizen group includes the `resident` alias used in probes. Other named roles select institutions. Missing roles are rejected when all enabled providers are scoped.

A provider's `DECISION_GROUP` is `citizen`, `institution` or `all`. Existing configurations default to `all`. The selected deployment explicitly scopes the 8B server to institutions and both Spark 4B and AMD to citizens. Saturation or cooldown does not spill into the other group. An unrestricted provider remains eligible for either group, so do not enable one when enforcing this experiment.

## Current status

Spark A still serves GPT-OSS-120B. Its SSH account requires a sudo password for Docker. The deployment script is staged at `/home/hackathon/faultline-qwen-setup.py`. Spark models and `.env.local` have not been changed by this preparation.

AMD already serves Qwen3-4B. A fresh citizen decision passed validation in 942.12 ms, and incorrect authentication returned 401. This single smoke check is not a simulation or throughput benchmark. The private receipt is `.data/qwen-citizen-probe.json`.

Spark B remains disabled pending Ken's server and relay. The fixed website continues showing the existing saved run until the coordinator is restarted for a new run.

## Spark A deployment

| Server | Spark loopback port | Managed laptop tunnel | GPU memory fraction | Initial worker slots |
| --- | --- | --- | --- | --- |
| Qwen3-8B institutions | 8000 | 18001 | 0.30 | 4 |
| Qwen3-4B citizens | 8001 | 18003 | 0.45 | 8 |

These are independent BF16 model servers sharing one GPU. Memory fractions are allocation budgets, not percentages of compute. The initial request slots are probe settings, not measured throughput. Both use prefix caching, 8,192-token context limits and short JSON output with thinking disabled.

The script checks the installed ARM64 image for GB10, Transformers Qwen3 support and vLLM's Qwen3 implementation. It records the image digest and model revisions. Passing imports still requires successful model startup and inference. See [vLLM Docker deployment](https://docs.vllm.ai/en/latest/deployment/docker/).

From Jayden's existing Spark SSH terminal, run:

```bash
sudo python3 ~/faultline-qwen-setup.py deploy
```

Enter any sudo password only in that terminal. This command prepares and launches both servers. Preparation downloads both public checkpoints while the old server stays running and generates separate private bearer keys under `~/faultline-qwen/`.

Launch verifies the original GPT-OSS container and restart script, conflicting container names and port 8001. It stops only `vllm_node`, preserves that container, starts the replacements and checks authentication, model identity and JSON decisions. If startup or validation fails, it attempts to restore the original container and existing launch script. It does not modify Netplan, Tailscale, Open WebUI, Docker permissions or the NVIDIA driver.

Separate operations for diagnosis:

```bash
sudo python3 ~/faultline-qwen-setup.py prepare
sudo python3 ~/faultline-qwen-setup.py launch
sudo python3 ~/faultline-qwen-setup.py status
sudo python3 ~/faultline-qwen-setup.py rollback
```

Allow time for downloads and first loading. Once both validate, the script writes private `~/faultline-qwen/connection.env`. Download it over SSH and import only its provider settings into ignored `.env.local`, preserving Neon and sharing credentials. Never print or commit the connection file. Rollback removes it and requires restoring the laptop's matching GPT-OSS configuration. Original-model restoration takes time and is not a guarantee of immediate readiness.

## Configuration after readiness

The private connection file includes keys. Its nonsecret settings are:

```dotenv
SPARK_A_ENABLED=true
SPARK_A_MODEL=Qwen/Qwen3-8B
SPARK_A_BASE_URL=http://127.0.0.1:18001/v1
SPARK_A_DECISION_GROUP=institution
SPARK_A_CAPACITY=4
SPARK_A_CITIZEN_ENABLED=true
SPARK_A_CITIZEN_MODEL=Qwen/Qwen3-4B
SPARK_A_CITIZEN_BASE_URL=http://127.0.0.1:18003/v1
SPARK_A_CITIZEN_DECISION_GROUP=citizen
SPARK_A_CITIZEN_CAPACITY=8
AMD_DECISION_GROUP=citizen
```

The launcher manages both Spark A forwards in one SSH connection. It sums enabled request slots subject to the global ceiling and rejects world configurations missing either group before launching. AMD's enabled flag and one-slot capacity remain governed by existing configuration.

Ken can later add replicas using `SPARK_B_*` and `SPARK_B_CITIZEN_*`: each needs a verified model ID, scope, capacity, private key and reachable endpoint. Follow the [relay guide](15-ken-spark-b-relay.md), with a separate port for the citizen model. This migration script expects Spark A's existing GPT-OSS container; it is not a fresh Spark B installer.

## Validation and observations

Targeted tests cover role routing, capacity and cooldown isolation, missing roles, legacy unrestricted providers, private deployment credentials and guarded rollback. Checkpoint metadata retains provider scope and reads older records without it. The observer displays model assignments. Every simulation's automatic notes record configured assignments and observed model/backend decisions.

Before increasing slots, probe fixed citizen and institution choices, then run a small world simulation with its normal observation notes. Measure accepted decisions per second, invalid decisions, timeouts and latency. Old GPT-OSS concurrency results do not establish Qwen capacity. No coverage qualification rerun is part of this change.

Compare government outcomes using matched model assignments, scenarios, seeds and profiles. The smaller citizen model can affect behavior and must be recorded as an experimental condition. Rationales are attributed explanations, not proof of private motives.
