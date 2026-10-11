"""Bounded, token-budgeted microbatch serving on the existing ROCm runtime."""

import asyncio
import json
import os
import secrets
import time
import threading
from collections import deque
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path

from fastapi import Depends, FastAPI, HTTPException
from pydantic import BaseModel, Field

try:
    from ops import amd_model_server as base
except ModuleNotFoundError:
    import amd_model_server as base

tokenizer_lock = threading.RLock()


class Tuning(BaseModel):
    batch_size: int = Field(default=8, ge=1, le=64)
    capacity: int = Field(default=8, ge=1, le=128)
    batch_tokens: int = Field(default=32768, ge=8192, le=131072)
    wait_ms: int = Field(default=15, ge=0, le=100)
    utilization_target_percent: int = Field(default=90, ge=50, le=95)


def gpu_busy_percent():
    sensor = base.state.get("gpu_busy_file") or os.environ.get("AMD_GPU_BUSY_FILE")
    if not sensor:
        return None
    try:
        value = int(Path(sensor).read_text().strip())
        return value if 0 <= value <= 100 else None
    except (OSError, ValueError):
        return None


class GpuPacer:
    """Yield between decode steps without changing logits or generated tokens.

    A synchronized work/idle budget targets averaged utilization. The three
    percentage-point margin allows for sampling and unpaced bookkeeping;
    individual GPU kernels are not subject to a hardware utilization ceiling.
    """

    def __init__(self, target_percent, synchronize, read_busy=gpu_busy_percent,
                 clock=time.monotonic, sleep=time.sleep):
        self.fraction = (target_percent - 3) / 100
        self.synchronize, self.read_busy = synchronize, read_busy
        self.clock, self.sleep = clock, sleep
        self.last = clock()
        self.next_sample = self.last
        self.idle_seconds = 0.0
        self.high_samples = 0

    def __call__(self, input_ids, scores):
        self.synchronize()
        now = self.clock()
        work = max(0, now - self.last)
        delay = work * (1 / self.fraction - 1)
        if now >= self.next_sample:
            busy = self.read_busy()
            self.next_sample = now + 0.5
            if busy is not None and busy > 95:
                self.high_samples += 1
                # Additional backoff when the sensor reports saturation.
                delay = max(delay, 0.05)
        if delay:
            self.sleep(delay)
            self.idle_seconds += delay
        self.last = self.clock()
        return scores


@dataclass
class Pending:
    request: base.CompletionRequest
    prompt: str
    tokens: int
    future: asyncio.Future
    created: float


def prepare_locked(request):
    tokenizer = base.state["models"][request.model]["tokenizer"]
    prompt = tokenizer.apply_chat_template(
        [message.model_dump() for message in request.messages],
        tokenize=False, add_generation_prompt=True, enable_thinking=False,
    )
    tokens = len(tokenizer(prompt, add_special_tokens=False)["input_ids"])
    if tokens + request.max_tokens > 8192:
        raise HTTPException(413, "Prompt and output exceed 8192 tokens")
    return prompt, tokens


def prepare(request):
    with tokenizer_lock:
        return prepare_locked(request)


def response_for(request, content, input_tokens, output_tokens, stopped):
    if request.response_format:
        kind = request.response_format.get("type")
        if kind == "json_schema":
            raise HTTPException(400, "Use json_object; schema decoding is not supported")
        if kind == "json_object":
            try:
                if not isinstance(json.loads(content), dict):
                    raise ValueError()
            except ValueError:
                raise HTTPException(502, "Model did not return a JSON object") from None
    return {
        "id": f"amd-{secrets.token_hex(8)}", "object": "chat.completion",
        "created": int(time.time()), "model": request.model,
        "choices": [{"index": 0, "message": {"role": "assistant", "content": content},
                     "finish_reason": "stop" if stopped else "length"}],
        "usage": {"prompt_tokens": input_tokens, "completion_tokens": output_tokens,
                  "total_tokens": input_tokens + output_tokens},
    }


def generate_batch(items):
    import torch
    from transformers import LogitsProcessorList

    selected = base.state["models"][items[0].request.model]
    tokenizer, model = selected["tokenizer"], selected["model"]
    with tokenizer_lock:
        tokenizer.padding_side = "left"
        if tokenizer.pad_token_id is None:
            tokenizer.pad_token = tokenizer.eos_token
        inputs = tokenizer([item.prompt for item in items], padding=True,
                           add_special_tokens=False, return_tensors="pt").to(model.device)
    width = inputs.input_ids.shape[1]
    target = scheduler.tuning.utilization_target_percent if scheduler else 90
    pacer = GpuPacer(target, lambda: torch.cuda.synchronize(model.device))
    with torch.inference_mode():
        output = model.generate(**inputs, max_new_tokens=items[0].request.max_tokens,
                                do_sample=False, pad_token_id=tokenizer.pad_token_id,
                                use_cache=True, logits_processor=LogitsProcessorList([pacer]))
    if scheduler:
        scheduler.pacing_idle_seconds += pacer.idle_seconds
        scheduler.utilization_backoffs += pacer.high_samples
    rows = output[:, width:].tolist()
    eos = model.generation_config.eos_token_id
    eos_ids = set(eos if isinstance(eos, list) else [eos])
    results = []
    for item, row in zip(items, rows, strict=True):
        end = next((index for index, token in enumerate(row) if token in eos_ids), None)
        count = len(row) if end is None else end + 1
        with tokenizer_lock:
            content = tokenizer.decode(row[:count], skip_special_tokens=True).strip()
        try:
            results.append(response_for(item.request, content, item.tokens, count, end is not None))
        except HTTPException as error:
            results.append(error)
    return results


def memory_snapshot():
    import torch

    free, total = torch.cuda.mem_get_info()
    return {"free_gib": round(free / 2**30, 2), "total_gib": round(total / 2**30, 2),
            "allocated_gib": round(torch.cuda.memory_allocated() / 2**30, 2),
            "reserved_gib": round(torch.cuda.memory_reserved() / 2**30, 2),
            "peak_allocated_gib": round(torch.cuda.max_memory_allocated() / 2**30, 2)}


class BatchScheduler:
    def __init__(self, tuning, generate=generate_batch, prepare_request=prepare):
        self.tuning, self.generate, self.prepare = tuning, generate, prepare_request
        self.pending = deque()
        self.wakeup = asyncio.Event()
        self.active = 0
        self.admitted = 0
        self.completed = 0
        self.failed = 0
        self.batches = 0
        self.largest_batch = 0
        self.oom_splits = 0
        self.accepting = True
        self.runner = None
        self.memory = {}
        self.pacing_idle_seconds = 0.0
        self.utilization_backoffs = 0

    async def submit(self, request):
        if not self.accepting:
            raise HTTPException(503, "Server is draining")
        if self.admitted >= self.tuning.capacity:
            raise HTTPException(429, "AMD server at capacity")
        self.admitted += 1
        future = asyncio.get_running_loop().create_future()
        item = None
        try:
            prompt, tokens = await asyncio.to_thread(self.prepare, request)
            if not self.accepting:
                raise HTTPException(503, "Server is draining")
            item = Pending(request, prompt, tokens, future, time.monotonic())
            self.pending.append(item)
            self.wakeup.set()
            return await future
        finally:
            if item is None:
                self.admitted -= 1
            elif item in self.pending:
                self.pending.remove(item)
                self.admitted -= 1

    def take_batch(self):
        first = self.pending.popleft()
        batch = [first]
        longest = first.tokens
        for item in list(self.pending):
            if len(batch) >= self.tuning.batch_size:
                break
            if (item.request.model, item.request.max_tokens) != (first.request.model, first.request.max_tokens):
                continue
            width = max(longest, item.tokens) + first.request.max_tokens
            if width * (len(batch) + 1) > self.tuning.batch_tokens:
                continue
            self.pending.remove(item)
            batch.append(item)
            longest = max(longest, item.tokens)
        return batch

    async def execute(self, batch):
        try:
            return await asyncio.to_thread(self.generate, batch)
        except Exception as error:
            if "out of memory" not in str(error).lower():
                return [HTTPException(503, "GPU generation failed") for _ in batch]
            import torch
            await asyncio.to_thread(torch.cuda.empty_cache)
            self.oom_splits += 1
            if len(batch) == 1:
                return [HTTPException(503, "GPU memory budget exceeded")]
            self.tuning.batch_size = min(self.tuning.batch_size, max(1, len(batch) // 2))
            midpoint = len(batch) // 2
            return await self.execute(batch[:midpoint]) + await self.execute(batch[midpoint:])

    async def run(self):
        while self.accepting or self.pending:
            if not self.pending:
                self.wakeup.clear()
                await self.wakeup.wait()
                if not self.pending:
                    continue
            await asyncio.sleep(self.tuning.wait_ms / 1000)
            if not self.pending:
                continue
            batch = self.take_batch()
            self.active = len(batch)
            self.batches += 1
            self.largest_batch = max(self.largest_batch, len(batch))
            try:
                results = await self.execute(batch)
                for item, result in zip(batch, results, strict=True):
                    failed = isinstance(result, Exception)
                    self.failed += int(failed)
                    self.completed += int(not failed)
                    if not item.future.done():
                        if failed:
                            item.future.set_exception(result)
                        else:
                            item.future.set_result(result)
            finally:
                self.admitted -= len(batch)
                self.active = 0

    async def close(self):
        self.accepting = False
        self.wakeup.set()
        if self.runner:
            await self.runner


TUNING_FILE = Path(os.environ.get("AMD_TUNING_FILE", "/workspace/gearth-amd/batch-tuning.json"))
scheduler = None


@asynccontextmanager
async def lifespan(application):
    global scheduler
    base.state["key"] = base.KEY_FILE.read_text().strip()
    if len(base.state["key"]) < 32:
        raise RuntimeError("A strong API key is required")
    await asyncio.to_thread(base.load_model)
    import torch
    properties = torch.cuda.get_device_properties(0)
    # DRM card numbers can change after a driver reset. PCI identity follows
    # the GPU actually selected by the model's ROCm context.
    detected = (f"/sys/bus/pci/devices/{properties.pci_domain_id:04x}:"
                f"{properties.pci_bus_id:02x}:{properties.pci_device_id:02x}.0/gpu_busy_percent")
    configured = os.environ.get("AMD_GPU_BUSY_FILE")
    base.state["gpu_busy_file"] = detected if Path(detected).exists() else configured
    tuning = Tuning.model_validate_json(TUNING_FILE.read_text()) if TUNING_FILE.exists() else Tuning()
    scheduler = BatchScheduler(tuning)
    scheduler.runner = asyncio.create_task(scheduler.run())
    yield
    await scheduler.close()


app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)


@app.get("/health", dependencies=[Depends(base.authorize)])
async def health():
    memory = await asyncio.to_thread(memory_snapshot)
    return {"ready": base.state["ready"], "model": base.MODEL,
            "gpu": base.state.get("gpu"), "rocm": base.state.get("rocm"),
            "serving": "transformers_microbatch", "capacity": scheduler.tuning.capacity,
            "active_requests": scheduler.active, "queued_requests": len(scheduler.pending),
            "admitted_requests": scheduler.admitted, "tuning": scheduler.tuning.model_dump(),
            "completed_requests": scheduler.completed, "failed_requests": scheduler.failed,
            "batches": scheduler.batches, "largest_batch": scheduler.largest_batch,
            "oom_splits": scheduler.oom_splits, "memory": memory,
            "gpu_busy_percent": gpu_busy_percent(),
            "gpu_sensor_path": base.state.get("gpu_busy_file"),
            "pacing_idle_seconds": round(scheduler.pacing_idle_seconds, 3),
            "utilization_backoffs": scheduler.utilization_backoffs,
            "models": [{"id": model, "revision": base.state["models"][model].get("revision")}
                       for model in base.model_ids()]}


@app.get("/v1/models", dependencies=[Depends(base.authorize)])
def models():
    return base.models()


@app.post("/v1/chat/completions", dependencies=[Depends(base.authorize)])
async def completions(request: base.CompletionRequest):
    if request.model not in base.model_ids() or request.stream:
        raise HTTPException(400, "Unsupported model or streaming request")
    if request.response_format and request.response_format.get("type") == "json_schema":
        raise HTTPException(400, "Use json_object; schema decoding is not supported")
    return await scheduler.submit(request)


@app.post("/admin/tuning", dependencies=[Depends(base.authorize)])
async def tune(settings: Tuning):
    if settings.capacity < settings.batch_size:
        raise HTTPException(400, "Capacity must be at least batch size")
    TUNING_FILE.parent.mkdir(parents=True, exist_ok=True)
    temporary = TUNING_FILE.with_suffix(".tmp")
    temporary.write_text(settings.model_dump_json() + "\n")
    temporary.replace(TUNING_FILE)
    scheduler.tuning = settings
    return settings.model_dump()
