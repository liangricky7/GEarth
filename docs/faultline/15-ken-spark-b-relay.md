# Ken's Spark B model server and Tailscale relay

The selected deployment has since changed to Qwen3-8B for institutions and Qwen3-4B for citizens. Follow the [all-node Qwen setup](18-all-node-qwen-setup.md) for new servers. The GPT-OSS command below remains a historical record of Spark A's inspected launch, not the current target for Spark B.

Spark A's live process arguments were inspected read-only on October 10, 2026. Spark A was already running when this project began; Faultline did not originally launch that server.

## Confirmed Spark A setup

- Server: vLLM inside the custom `vllm-node-mxfp4` Docker image, container `vllm_node`.
- Model and served ID: `openai/gpt-oss-120b`, MXFP4 quantization.
- Server port: `8000`.
- API authentication: bearer API key. The worker sends `Authorization: Bearer <provider-specific key>` on each inference request.
- Observed recipe process: `python3 /home/admin/vllm/spark-vllm-docker/run-recipe.py openai-gpt-oss-120b --solo`.

For the initial second Spark, use the same model if the same compatible runtime is available. That reduces model differences when comparing government or agent behavior. It is still an independent replica, not one model sharded across Sparks.

The following is the observed server command with its private API key replaced by an environment variable. It belongs inside the same custom runtime; these custom MXFP4 flags are not a verified command for an arbitrary standard vLLM installation.

```bash
: "${SPARK_B_API_KEY:?Set a new private API key first}"
vllm serve openai/gpt-oss-120b \
  --tool-call-parser openai \
  --reasoning-parser openai_gptoss \
  --enable-auto-tool-choice \
  --gpu-memory-utilization 0.8 \
  --enable-prefix-caching \
  --load-format fastsafetensors \
  --quantization mxfp4 \
  --mxfp4-backend CUTLASS \
  --mxfp4-layers moe,qkv,o,lm_head \
  --attention-backend FLASHINFER \
  --kv-cache-dtype fp8 \
  --max-num-batched-tokens 8192 \
  --host 0.0.0.0 \
  --port 8000 \
  --api-key "$SPARK_B_API_KEY"
```

Set a new strong, nonempty `SPARK_B_API_KEY` privately before launching. Do not reuse Spark A's key or put the value in the repository, a chat response or logs. Do not run this command with an unset variable. The host flag above describes Spark A; for a new host-networked server accessed exclusively by SSH, bind the model server to `127.0.0.1` instead. With a bridge-networked Docker container, bind inside the container and publish only the Spark's loopback port. Keep the model endpoint reachable from the Spark's own SSH session without public exposure.

If the custom runtime or recipe is absent, first identify an ARM64/GB10-compatible setup using [NVIDIA's Spark vLLM workflow](https://build.nvidia.com/spark/vllm/overview). Copying only the server command does not install the image, runtime or model. Ken's agent should verify the software before trying custom flags. No server was launched or changed on either Spark by this documentation update.

## Ken's laptop relay

Ken proposes sharing laptop `kenpc`, Tailscale IP `100.119.198.81`, and forwarding its port `18002` to Spark B's port `8000`.

On Ken's laptop, replace `SPARK_B_SSH_TARGET` below with the actual `username@host` for his distinct Spark:

```bash
ssh -N -o ExitOnForwardFailure=yes \
  -o ServerAliveInterval=30 -o ServerAliveCountMax=3 \
  -L 100.119.198.81:18002:127.0.0.1:8000 SPARK_B_SSH_TARGET
```

This explicitly listens on Ken's Tailscale interface. A forward bound only to `127.0.0.1:18002` cannot be reached from Jayden's laptop. Keep Ken's laptop awake and this SSH process running. If his Spark uses a nonstandard SSH port, add `-p PORT`.

Share `kenpc` with Jayden and have Jayden accept the device invitation. Device sharing does not automatically share machines behind it; the forwarded service is hosted on the shared laptop. Host firewall and tailnet access rules must permit Jayden to reach TCP port 18002. No subnet routing or Netplan changes are required for this relay.

Tailscale encrypts traffic between the laptops with WireGuard; SSH encrypts the relay-to-Spark segment. See [Tailscale encryption](https://tailscale.com/docs/concepts/tailscale-encryption) and [device sharing](https://tailscale.com/docs/features/sharing). The model service still requires its bearer key.

## Faultline configuration

The provider loader now accepts private Tailscale HTTP only when the exact numerical host is explicitly pinned. It checks that the pinned address is in `100.64.0.0/10` and that the URL hostname matches it. This is configuration validation, not a live verification of a Tailscale route or endpoint identity. Operators must verify the shared device. Other remote HTTP, credentials embedded in a URL, query strings and fragments remain rejected. Model clients ignore environment proxy settings so these requests do not go through an inherited HTTP proxy.

After device sharing, service readiness and private key transfer, set these values in Jayden's ignored `.env.local`:

```dotenv
SPARK_B_ENABLED=true
SPARK_B_BASE_URL=http://100.119.198.81:18002/v1
SPARK_B_TAILSCALE_HTTP_HOST=100.119.198.81
SPARK_B_MODEL=openai/gpt-oss-120b
SPARK_B_API_KEY=REPLACE_WITH_PRIVATELY_SUPPLIED_KEY
SPARK_B_CAPACITY=64
```

The `/v1` suffix is required for this model API. Start with a small inference probe before using the provisional 64-slot budget; Spark B's actual integrated capacity is unverified. Coordinate any simultaneous workloads on that GPU.

Before enabling the backend, Jayden should verify:

1. `kenpc` is visible and reachable through Tailscale.
2. An unauthenticated request to `http://100.119.198.81:18002/v1/models` returns 401.
3. An authenticated request returns `openai/gpt-oss-120b`.
4. A short chat-completion request passes Faultline's decision validator.

At this revision the relay address and model are planned values from Ken's message. Faultline has not connected to Spark B or enabled it in `.env.local`. The provider change passes 48 Python tests, including exact host pinning, rejection of other addresses, mandatory remote credentials and preservation of the relay setting through the launcher. This check makes no GPU calls and does not repeat coverage qualification.
