# Same Qwen models on Spark A, Spark B and AMD

## Shared model contract

Every node serves the same two Hugging Face checkpoints:

| Group | Model | Pinned Hugging Face revision |
| --- | --- | --- |
| Institutions | `Qwen/Qwen3-8B` | `b968826d9c46dd6066d109eabc6255188de91218` |
| Citizens | `Qwen/Qwen3-4B` | `1cfa9a7208912126459214e8b04321603b3df60c` |

These revisions were read from the public Hugging Face repositories on October 10, 2026. They are stored in `ops/qwen-models.json`. Both installers download directly from Hugging Face at these revisions. Keep this manifest identical on all machines.

All nodes use BF16 weights, non-thinking generation, temperature zero, at most 512 output tokens and an 8,192-token total context budget. The worker validates the same candidate-action contract for every response. Governments, businesses, banks and media use 8B; residents of every occupation use 4B. Routing does not depend on wealth or government type.

The Sparks use vLLM with continuous batching; AMD uses the ROCm PyTorch runtime with Transformers. The model pair and revisions match across platforms. Serving kernels and JSON decoding differ, so identical weights do not guarantee identical output or throughput.

## Balancing population and simulation speed

Agent identities do not each load a model. Thousands of profiles share these six model replicas. The laptop executes the authoritative economy, trade, conflict, resource accounting and world updates. GPUs generate strategic decisions asynchronously; routine behavior continues between decisions.

Begin with the existing 1,100-agent scenario and modest concurrency: 4 institution slots and 8 citizen slots per Spark, plus 1 slot per model on AMD. With all three nodes ready, this gives 26 permitted concurrent requests, not 26 agents or a measured GPU utilization percentage. Increase slots only when a short probe improves accepted decisions per second without increasing invalid responses or latency.

Give residents longer decision intervals than institutions, stagger requests and retain one pending request per identity. Population, simulation ticks per second and AI decision frequency are separate controls. Increasing population without increasing decision frequency permits more simulated people; accelerating world ticks without timely model turns can leave policies stale. Tune these together using observed latency and the existing automatic run notes.

The initial Spark memory budgets are 0.30 for 8B and 0.45 for 4B. They allocate memory, not compute percentages. Both model servers share each Spark's GPU bandwidth. AMD accepts one simultaneous generation per model, with no Transformer continuous batching. Maximum population and final slot counts remain unmeasured for this model pair. This setup does not rerun coverage qualification.

## Files to share

Generate the portable bundle from the project directory:

```bash
.venv/bin/python ops/build_qwen_bundle.py
```

The default output is `~/Downloads/faultline-qwen-setup.zip`. It contains `README.md`, `spark_qwen.py`, `qwen-models.json`, `amd_model_server.py`, `amd-requirements.txt` and `faultline-qwen-amd.ipynb`. It contains no credentials. The Markdown file you are reading is its README.

## Spark A: replace the existing GPT-OSS workload

The prepared script and manifest have been copied to Jayden's Spark home directory. In its existing SSH terminal:

```bash
sudo python3 ~/faultline-qwen-setup.py deploy
```

Sudo requires the Spark account's password. Enter it only in that terminal. The script pulls the ARM64 runtime `vllm/vllm-openai:qwen38`, a runtime used in [NVIDIA's Spark instructions](https://build.nvidia.com/spark/vllm/instructions). The container name does not select a model: the actual downloaded and served weights are the Qwen3 8B and 4B revisions above. Runtime checks verify GB10 and Qwen3 support before replacement.

To explicitly reuse Spark A's existing image instead:

```bash
sudo python3 ~/faultline-qwen-setup.py deploy --image vllm-node-mxfp4
```

That image must pass the same Qwen3 compatibility checks. The old GPT-OSS quantization flags are not reused. Downloads happen before the old server stops. Migration preserves `vllm_node` and records its existing restart script; failed replacement startup triggers an attempted restoration. Netplan, Tailscale, Open WebUI, Docker permissions and the driver remain outside this migration.

Success prints `Both models verified` and creates private `~/faultline-qwen/connection.env`. The new model ports are loopback 8000 for 8B and 8001 for 4B. The laptop launcher manages forwards to 18001 and 18003. Download and merge the generated provider settings into ignored `.env.local`, preserving database and website credentials. Do not print or commit the connection file.

Jayden's preparation completed, but launch stopped with `Docker top failed (exit 1)` before replacing the original server. The patched installer checks the original workload inside its container and returns only a boolean, keeping process arguments private. It has been copied to Spark A. Resume the prepared deployment without repeating downloads:

```bash
sudo python3 ~/faultline-qwen-setup.py launch
```

This is still a pending launch, not a verified deployment. Eleven focused installer tests pass, including the process-probe and rollback checks.

If launch reports that a replacement container already exists, use the updated installer's recovery action:

```bash
sudo python3 ~/faultline-qwen-setup.py resume
```

Resume requires the saved preparation and rollback state. It validates existing containers against the prepared image, exact serving arguments, private key, cache mount, ownership label and loopback ports before changing anything. Matching running models are retained; missing models are started and both APIs are verified before writing the connection file. A startup failure attempts the saved rollback. For a fresh Spark B deployment, use `resume --fresh --provider SPARK_B`. Nineteen focused installer tests pass, including rejection of mismatched partial deployments.

## Spark B: fresh installation for Ken

Unzip the shared bundle locally. Copy both files to Spark B, substituting its actual SSH target:

```bash
scp spark_qwen.py qwen-models.json USER@SPARK_B:~/
ssh USER@SPARK_B
sudo python3 ~/spark_qwen.py deploy --fresh --provider SPARK_B
```

Run the scp command from the extracted bundle directory. This fresh mode refuses to stop an existing workload and checks both model ports. It downloads the same checkpoints and runtime, generates keys local to this Spark, and starts independent 8B and 4B servers. Failed startup removes only its labeled replacement containers.

The generated private connection file uses `SPARK_B_*` and `SPARK_B_CITIZEN_*` settings. It initially gives loopback laptop URLs 18002 and 18004. For Ken's proposed Tailscale relay, run on Ken's laptop:

```bash
ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -o ServerAliveCountMax=3 \
  -L 100.119.198.81:18002:127.0.0.1:8000 \
  -L 100.119.198.81:18004:127.0.0.1:8001 USER@SPARK_B
```

Jayden must have accepted Ken's device share and be permitted to reach both ports. On Jayden's laptop, import the generated private Spark B keys and use:

```dotenv
SPARK_B_ENABLED=true
SPARK_B_MODEL=Qwen/Qwen3-8B
SPARK_B_DECISION_GROUP=institution
SPARK_B_CAPACITY=4
SPARK_B_BASE_URL=http://100.119.198.81:18002/v1
SPARK_B_TAILSCALE_HTTP_HOST=100.119.198.81
SPARK_B_CITIZEN_ENABLED=true
SPARK_B_CITIZEN_MODEL=Qwen/Qwen3-4B
SPARK_B_CITIZEN_DECISION_GROUP=citizen
SPARK_B_CITIZEN_CAPACITY=8
SPARK_B_CITIZEN_BASE_URL=http://100.119.198.81:18004/v1
SPARK_B_CITIZEN_TAILSCALE_HTTP_HOST=100.119.198.81
```

Keep Ken's laptop awake and the SSH process running. The relay remains unverified until both APIs respond from Jayden's laptop. See the [relay guide](15-ken-spark-b-relay.md) for existing access details.

## AMD: both models on the existing ROCm instance

Upload only `faultline-qwen-amd.ipynb` from the bundle into AMD Jupyter and open it as a notebook. It embeds the server, manifest and dependency file, so separate source-file uploads are unnecessary. Use the existing PyTorch ROCm image and no sudo.

1. Run the installer cell and check the status cell until the exit code is zero. It installs application dependencies and preserves the installed ROCm PyTorch.
2. Run the start cell. It stops only the old Faultline server in `/workspace/faultline-amd` on port 8180, reuses the private key and loads both pinned models. At least 28 GiB free GPU memory is required before loading. The known W7900D has 48 GiB total, but current free memory must still pass this check.
3. Repeat the health cell until `ready: true`. The health output lists both model revisions. Run the probe cell to verify both IDs, revisions, authentication and short JSON decisions.
4. Reuse the existing HTTPS tunnel to port 8180. On a fresh instance, set `EXPOSE_ENDPOINT=True` in the exposure cell and run it. The command filters inherited proxy variables, as required by the working tunnel setup.
5. Put the current HTTPS origin in `AMD_ORIGIN`, then run the connection cell. Download its private `faultline-amd-connection.env` file. Never open or share its contents in chat.

The existing AMD origin is `https://rc-e6bf41d9fc5e6b18.radeon.firstdg.ai`; verify it remains current. Reusing the active tunnel preserves it, while creating a new tunnel may assign another address.

Both models use the same authenticated HTTPS `/v1` API. The exported file supplies `AMD_MODEL=Qwen/Qwen3-8B` with institution scope, `AMD_CITIZEN_MODEL=Qwen/Qwen3-4B` with citizen scope, separate provider names and one slot each. Both provider entries share this instance's API key and endpoint; they reserve different model slots.

## Coordinator activation and validation

Import only verified provider configuration into ignored `.env.local`. Every enabled 8B entry must have institution scope; every 4B entry must have citizen scope. Disable unverified nodes. In particular, the older AMD 4B-only configuration cannot serve as the new AMD 8B institution entry until its upgrade succeeds.

First check model catalogs, bearer authentication and one valid decision for each configured model. The AMD notebook includes these checks. The Spark installer includes them locally; verify the relay separately before enabling Spark B on the coordinator. A model ID change in `.env.local` alone does not change a running server.

After the small probes pass, start the normal live coordinator with `--compute hybrid`. Keep the existing automatic observation notes for each simulation. They record configured scopes and observed backend/model decisions. The global queue remains bounded by enabled capacity, and a saturated or unavailable scoped provider does not send work to the other model group.

At this revision the routing, portable installers and notebook are prepared and tested locally. Spark A's privileged migration, Spark B deployment and AMD dual-model loading have not been completed or benchmarked. Spark A still runs GPT-OSS, and the live AMD probe exercised its existing 4B-only server. No new simulation was run for this setup.

## Operator diagnosis

On either Spark, use the same script name and run `status` for readiness or `rollback` to undo that script's deployment. For example:

```bash
sudo python3 ~/spark_qwen.py status
sudo python3 ~/spark_qwen.py rollback
```

Spark A's staged filename is `faultline-qwen-setup.py`. Its rollback attempts to restart the preserved GPT-OSS launch; Spark B fresh rollback removes only the new labeled containers. Restore matching laptop provider settings after rollback. Inspect private local container logs if startup fails, without posting credentials.

If AMD loading fails, inspect its notebook health/log cell before restarting. Its original single-model setup remains documented in [AMD compute integration](12-amd-compute.md). Model/runtime differences remain part of the experiment even when checkpoint revisions match.

Primary model references: [Qwen3-8B](https://huggingface.co/Qwen/Qwen3-8B), [Qwen3-4B](https://huggingface.co/Qwen/Qwen3-4B).
