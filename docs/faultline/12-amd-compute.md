# AMD compute integration

## Current status

The project now supports Spark-only, AMD-only, and hybrid inference. The AMD adapter is implemented and tested locally; live AMD inference has not yet been verified or enabled. The existing observer continues to serve its saved run.

The open instance is `rgapi-hackathon-22895-c1f396c5`, using the Radeon Cloud ROCm PyTorch image. Its notebook reported an AMD Radeon Pro W7900D with 48 GiB VRAM, PyTorch `2.13.0+rocm10.0.0`, and HIP `7.15.26333`. It had no vLLM or Transformers installation when inspected. The notebook dependency installation completed successfully with exit code `0`. A Qwen3-4B weight download was started in a background process; completion has not yet been verified. No SSH connection is available for this instance. Native browser automation was unreliable for subsequent notebook actions and file uploads. The connected browser profile returned HTTP 403 for the same notebook URL; it lacks the signed-in AMD session. The remaining cloud setup needs the prepared notebook to run in an authenticated session.

## Architecture

The authoritative simulation still runs on Jayden's laptop. Its local authenticated worker reserves a slot on one model provider for each decision. Each provider has its own model, API key, endpoint, concurrency limit, and counters. Available providers are selected by their occupied fraction, with rotating tie breaks. A failed HTTP request or timeout places that provider in a 30-second cooldown. Other available providers remain eligible for new requests. Failed jobs return an explicit fallback; they are not silently retried on another model.

The worker applies the existing action validator to all responses. The engine records backend and model attribution in decision events and checkpoints. Run observations count accepted decisions by backend and model, keeping fallback results separate. Mixed-model comparisons also record model assignment as a confound.

The AMD server uses Qwen3-4B with Transformers on ROCm, with thinking disabled. It initially accepts one generation at a time. This establishes an additional inference source; it does not qualify an agent limit or a throughput improvement. Transformers generation does not provide continuous batching. A later optimized server can replace it behind the same API contract after measurements justify a higher limit.

## Cloud setup

Upload `ops/faultline-amd-setup.ipynb` to the AMD Jupyter file browser and open it. A copy is also saved in `~/Downloads/faultline-amd-setup/` for convenient upload.

1. Run the dependency cell. It preserves the installed ROCm PyTorch runtime.
2. Run the installation-status cell until it reports exit code `0`.
3. Run the server-start cell once. It writes the server and creates a private API-key file, then starts a loopback server on port `8180`. Model weights download from Hugging Face.
4. Run the health cell until it reports `ready: true` and the AMD GPU model. No credential is printed.
5. Review and approve endpoint exposure before enabling `EXPOSE_ENDPOINT`. The platform tunnel supplies HTTPS but adds no authentication; this server checks a bearer key on every route. It exposes only the model server, not Jupyter or the workspace.
6. Enter the tunnel address in `AMD_ORIGIN`, run the connection-file cell, and download its file to the laptop. Merge the resulting values into the ignored `.env.local`. Never commit the connection file or paste its key into chat.

The server can also be started from a cloud terminal after writing its private key file:

```sh
python -m pip install -r amd-requirements.txt
python -m uvicorn amd_model_server:app --host 127.0.0.1 --port 8180 --no-access-log
```

Rerun the health cell if the cloud process exits. Do not start duplicate model-server processes. The instance and tunnel need to remain running for future AMD requests.

## Laptop configuration and verification

The new variables in `.env.example` are `AMD_ENABLED`, `AMD_BASE_URL`, `AMD_MODEL`, `AMD_API_KEY`, and `AMD_CAPACITY`. Keep AMD disabled until this check passes:

```sh
.venv/bin/python ops/check_amd.py
```

It checks the model catalog, verifies that unauthenticated access is rejected, and sends one fictional resident decision through the actual worker validator. It writes observations to `.data/amd-connection.json` and `.data/amd-connection.md`. It does not start a simulation or run a coverage test. This check has not yet been run against the cloud instance.

After verification, set `AMD_ENABLED=true`. The launcher then selects hybrid mode automatically. Explicit selection is also available:

```sh
.venv/bin/python ops/run_stage1.py --live --world --compute hybrid --storage neon
.venv/bin/python ops/run_stage1.py --live --world --compute amd --storage neon
.venv/bin/python ops/run_stage1.py --live --world --compute spark --storage neon
```

These commands start new simulations. Each run retains the automatic observation notes. AMD-only mode does not open a Spark SSH tunnel. Saved-run inspection requires no model endpoint. The global `--capacity` still bounds the entire decision queue; backend limits do not raise it automatically. The launcher also caps live world admission at the sum of enabled backend slots, so an AMD-only world with the initial single-slot server admits one pending decision at a time. The default AMD limit is one, while the Spark limit remains configurable separately.

The provider module also accepts `SPARK_B_*` settings for a future worker deployment. The current launcher deliberately leaves Spark B disabled until its endpoint and tunnel setup are supplied.

## Validation

- 27 Python checks passed, including provider routing, independent capacity, cooldown recovery, credential validation, cancellation, and cloud-route authentication.
- 37 C# checks passed, including backend attribution in run observations.
- All prepared notebook code cells compile locally. GPU loading and generation are pending cloud verification.
- No new simulation, coverage test, Spark server change, or Netplan change was made.

## Primary references

- [Radeon Cloud tunnel guide](https://amd-aim.github.io/radeon-cloud-docs/guides/tunnel/)
- [Radeon Cloud SSH guide](https://amd-aim.github.io/radeon-cloud-docs/guides/ssh/)
- [Qwen3-4B model and non-thinking mode](https://huggingface.co/Qwen/Qwen3-4B)
