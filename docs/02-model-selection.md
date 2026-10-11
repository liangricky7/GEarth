# Local model choice for Crawl

## Recommendation

Start with [Qwen3.5-9B](https://huggingface.co/Qwen/Qwen3.5-9B), an Apache-2.0-licensed, post-trained model. Run it in non-thinking mode for short suggestions. Its job is to pick an appealing place from a short, verified list and explain the tradeoff between group travel time, cost, and preferences. Do not fine-tune during the 36-hour build.

The [DGX Spark](https://www.nvidia.com/content/dam/en-zz/solutions/support/dgx-spark/DGX-Spark-Quick-Start-Guide.pdf) has 128 GB of unified memory, enough capacity for a 9B model. Actual response time on the team's device and network remains unmeasured. NVIDIA provides a [Spark vLLM serving workflow](https://build.nvidia.com/spark/vllm/instructions) with an OpenAI-compatible API; use its compatible ARM64 container and test the endpoint before integration.

## Fallbacks and additions

| Model | Use when | Decision |
| --- | --- | --- |
| [Qwen3.5-9B](https://huggingface.co/Qwen/Qwen3.5-9B) | Default next-stop selection and explanation | Start here. |
| [Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B) | 9B latency is too high during a live group session | Keep as a swap-in fallback. |
| [Qwen3-Reranker-0.6B](https://huggingface.co/Qwen/Qwen3-Reranker-0.6B) | Free-text search returns many weakly relevant eligible places | Add only if search quality needs it. |

An embedding model is unnecessary for the target loop if Google Places search supplies the candidate set. Larger generation models add serving and latency risk without solving the hard constraints.

## Model contract

Input: a group preference summary plus at most roughly 10–20 eligible candidates, each with a stable place ID and verified fields. Output: a structured object such as `{ "place_id": "...", "reason": "..." }`. The backend rejects a `place_id` absent from the input list and falls back to a deterministic top candidate if inference fails. Never let the model manufacture a new place, route time, price, or vote.

Use `chat_template_kwargs: {"enable_thinking": false}` in the chat request as shown by the [Qwen model card](https://huggingface.co/Qwen/Qwen3.5-9B). Keep the answer short, and benchmark the complete remote request from the demo client before locking the model choice.
