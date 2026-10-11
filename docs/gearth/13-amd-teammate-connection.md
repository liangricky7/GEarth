# AMD connection guide for teammates and coding agents

This guide lets Kennedy, Ahmad, Ricky, and their coding agents use GEarth's shared AMD model server from their own computers. The simulation runs locally on each teammate's computer; authenticated inference requests go to the existing AMD instance. Tailscale, Spark SSH access, an AMD dashboard login, and Neon are not required for AMD-only testing.

## Current connection

Updated on October 11, 2026. See the [batch deployment and benchmark](25-amd-batch-optimization.md). The existing URL and bearer key are unchanged.

| Setting | Value |
| --- | --- |
| Integration branch | `amd-compute-source` |
| OpenAI-compatible base URL | `https://rc-e6bf41d9fc5e6b18.radeon.firstdg.ai/v1` |
| Model | `Qwen/Qwen3-4B` |
| Hardware | AMD Radeon Pro W7900D, 48 GiB VRAM |
| Authentication | `Authorization: Bearer <AMD_API_KEY>` |
| Shared server capacity | 32 admitted requests, batched across all clients; 90% average utilization target |
| Verified load result | 192/192 valid choices, 2.77 choices/second, 12.78-second p95 at concurrency 32 |

The API key is deliberately absent from this document and Git. Ask Jayden to provide it through an agreed private channel, or to fill in your local `.env.local` securely. Do not paste the key into a GitHub issue, pull request, Trello card, coding-agent conversation, or committed file. A coding agent can read the local configuration without printing its values.

The AMD instance and its tunnel must remain running. Restarting the tunnel can change this model API address; ask Jayden for the latest address if it stops working. The model API address is separate from the permanent observer website link.

## 1. Get the implementation

For a new checkout:

```bash
git clone --branch amd-compute-source https://github.com/liangricky7/GEarth.git gearth-amd
cd gearth-amd
```

If you already have a checkout with ongoing work, create an isolated checkout from inside that repository:

```bash
git fetch origin
git worktree add --detach ../gearth-amd-test origin/amd-compute-source
cd ../gearth-amd-test
```

This integration is currently on `amd-compute-source`; checking out `main` alone does not guarantee the AMD launcher and provider files are present. The isolated checkout is for connection testing. Keep implementation work on your team's normal feature branch and integrate the AMD branch separately when needed, preserving your existing changes.

## 2. Install local dependencies

Use macOS, Linux, or WSL2. The launcher uses Unix process controls; native Windows execution is not supported by this runbook. Install Git, Python 3.12 or newer, and the .NET SDK compatible with `global.json` (`10.0.203`, allowing newer patches in the same SDK feature band).

The examples use Python 3.12:

```bash
python3.12 -m venv .venv
.venv/bin/python -m pip install -r agents/requirements.txt
```

You do not need a local GPU, ROCm, Transformers, or the cloud-only `ops/amd-requirements.txt`. The model is already loaded remotely.

## 3. Configure your private local environment

Create `.env.local` in the repository root if it does not already exist:

```bash
cp -n .env.example .env.local
```

Open it in your editor and add or update these values. Replace the placeholder with the key supplied privately by Jayden; do not overwrite unrelated existing configuration.

```dotenv
SPARK_A_ENABLED=false
SPARK_B_ENABLED=false
AMD_ENABLED=true
AMD_BASE_URL=https://rc-e6bf41d9fc5e6b18.radeon.firstdg.ai/v1
AMD_MODEL=Qwen/Qwen3-4B
AMD_API_KEY=REPLACE_WITH_PRIVATELY_SUPPLIED_KEY
AMD_CAPACITY=1
```

`.env.local` is ignored by Git. Keep it local. No database connection is needed when using `--storage local`.

Always specify `--compute amd` when launching a live simulation without Spark access. The launcher's automatic mode chooses hybrid when AMD is enabled, and hybrid also attempts a Spark SSH connection. Setting `SPARK_A_ENABLED=false` alone does not override that launcher selection.

## 4. Verify one real model decision

Coordinate with Jayden and the group before using the shared GPU. Run this once when no other teammate is running live inference:

```bash
.venv/bin/python ops/check_amd.py
```

This command loads the ignored local configuration, verifies the model catalog, checks that requests without a key return `401`, and asks for one fictional resident decision through GEarth's actual worker and action validator. It does not start a world simulation or rerun the coverage benchmark.

Success prints a receipt with `backend: "amd"`, `model: "Qwen/Qwen3-4B"`, `unauthenticated_status: 401`, a validated action, and its measured latency. It saves credential-free observations to:

- `.data/amd-connection.json`
- `.data/amd-connection.md`

The measured 3.21-second request from Jayden's check is a reference, not a latency guarantee or a qualified agent limit.

## 5. Optional short local simulation

After the connection check passes and the group agrees that the GPU is available:

```bash
.venv/bin/python ops/run_stage1.py --world --live --compute amd --storage local --population 100 --capacity 1 --ticks 20 --interval 2
```

Open `http://127.0.0.1:5080/` on your own computer. The launcher starts your local engine, worker, and observer. AMD-only mode does not open a Spark SSH tunnel. The observer remains available after the run completes; press Ctrl+C to stop the launcher and its child services gracefully.

This is a brief integration run with partial agent coverage. It is not a government comparison, throughput qualification, or full-population coverage test. New runs automatically save behavior and outcome observations under `.data/run-notes/`, and the observer offers a Markdown download. Review those notes when reporting what happened.

For engine or UI development that does not need model inference, use:

```bash
.venv/bin/python ops/run_stage1.py --world --storage local --population 100 --ticks 20
```

That run uses rule choices and consumes no AMD GPU time.

## Shared capacity and API contract

`AMD_CAPACITY=1` in the teammate example limits that local worker. It does not coordinate independent workers on different laptops. The remote server admits 32 requests across everyone; the shared cloud simulation is configured to use that capacity. Additional clients can receive `429` when all slots are occupied. Coordinate separate load tests with the shared simulation operator.

All routes require the bearer key:

| Route | Purpose |
| --- | --- |
| `GET /health` at the origin, without `/v1` | Readiness, GPU, ROCm, and current capacity |
| `GET /v1/models` | Model catalog |
| `POST /v1/chat/completions` | Non-streaming model inference |

For another client, set its OpenAI-compatible Chat Completions base URL to `AMD_BASE_URL`, its model to `AMD_MODEL`, and its bearer credential to `AMD_API_KEY`. This server implements those routes, not the Responses API, embeddings, or streaming. Use at most 512 generated tokens, keep the prompt within 8,192 tokens, and request the configured model. JSON-object output is supported and validated; invalid model output is reported as an error.

The endpoint provides inference, not remote shell or file access. Your coding agent edits and runs the repository on your laptop; it does not need access to the AMD notebook or its workspace.

## Troubleshooting

| Symptom | Next step |
| --- | --- |
| `AMD_API_KEY is required` or `401` | Check the private key in `.env.local` with Jayden, without printing it. |
| `404` from the public tunnel | Confirm the latest AMD address and that the tunnel is active with Jayden. |
| `429` | Another request may be using the single shared slot. Coordinate and try again after it finishes. |
| Timeout or connection failure | Confirm the cloud instance and model server are still running. Inspect local logs without sharing credentials. |
| Launcher tries Spark SSH | Include `--compute amd` explicitly. |
| Launcher asks for `DATABASE_URL` | Include `--storage local` explicitly. |
| Port `5080` or `8100` is already in use | Stop your earlier local GEarth launcher before starting another. |
| .NET build cannot find the SDK | Install an SDK compatible with the repository's `global.json`. |

Jayden's first tunnel inherited an HTTP proxy that rejected the tunnel connection with `403`. The setup notebook now filters proxy variables for `rc-tunnel`. Tunnel diagnostics and restarts belong to the cloud operator; see [AMD compute integration](12-amd-compute.md). Teammates should not start another cloud server or change the Spark, Netplan, model, or tunnel while testing.

## Instructions to give your coding agent

> Read `docs/gearth/13-amd-teammate-connection.md` and inspect the current checkout before changing anything. Use the existing AMD integration from `amd-compute-source`, preserving my feature branch and local work. Configure AMD-only inference through the ignored `.env.local`; if the private API key is missing, ask me to add it locally and do not ask me to paste it into chat. Never print, commit, or transmit credentials. Coordinate shared GPU use before running `ops/check_amd.py` once. Use `--compute amd --storage local --capacity 1` for authorized live testing, and use rule mode for development that does not need inference. Do not rerun the coverage benchmark or change cloud services, Spark networking, or Netplan. Report the connection receipt and review automatic observation notes from any simulation you run. Write clean, legible code without adding inline comments.
