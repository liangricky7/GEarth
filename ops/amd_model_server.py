import asyncio
import json
import os
import secrets
import time
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel, Field

MODEL = os.environ.get("AMD_MODEL", "Qwen/Qwen3-4B")
KEY_FILE = Path(os.environ.get("AMD_KEY_FILE", "/workspace/faultline-amd/api-key"))
state = {"ready": False, "active": False}


def load_model():
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer

    if not torch.cuda.is_available() or not torch.version.hip:
        raise RuntimeError("An AMD ROCm GPU is required")
    state["tokenizer"] = AutoTokenizer.from_pretrained(MODEL)
    state["model"] = AutoModelForCausalLM.from_pretrained(
        MODEL,
        torch_dtype=torch.bfloat16,
        device_map="cuda:0",
        attn_implementation="sdpa",
    ).eval()
    state["gpu"] = torch.cuda.get_device_name(0)
    state["rocm"] = torch.version.hip
    state["ready"] = True


@asynccontextmanager
async def lifespan(application):
    state["key"] = KEY_FILE.read_text().strip()
    if len(state["key"]) < 32:
        raise RuntimeError("A strong API key is required")
    await asyncio.to_thread(load_model)
    yield


app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)


def authorize(authorization: str = Header(default="")):
    if not secrets.compare_digest(
        authorization, f"Bearer {state.get('key', '')}"
    ) or not state.get("key"):
        raise HTTPException(401, "Unauthorized")


class Message(BaseModel):
    role: str
    content: str = Field(max_length=100000)


class CompletionRequest(BaseModel):
    model: str
    messages: list[Message] = Field(min_length=1, max_length=32)
    max_tokens: int = Field(default=256, ge=1, le=512)
    temperature: float = Field(default=0, ge=0, le=2)
    stream: bool = False
    response_format: dict | None = None


@app.get("/health", dependencies=[Depends(authorize)])
def health():
    return {
        "ready": state["ready"],
        "model": MODEL,
        "gpu": state.get("gpu"),
        "rocm": state.get("rocm"),
        "active_requests": int(state["active"]),
        "capacity": 1,
    }


@app.get("/v1/models", dependencies=[Depends(authorize)])
def models():
    return {
        "object": "list",
        "data": [{"id": MODEL, "object": "model", "owned_by": "faultline-amd"}],
    }


def generate(request):
    import torch

    tokenizer = state["tokenizer"]
    model = state["model"]
    prompt = tokenizer.apply_chat_template(
        [message.model_dump() for message in request.messages],
        tokenize=False,
        add_generation_prompt=True,
        enable_thinking=False,
    )
    inputs = tokenizer(prompt, return_tensors="pt").to(model.device)
    input_tokens = inputs.input_ids.shape[1]
    if input_tokens > 8192:
        raise HTTPException(413, "Prompt exceeds 8192 tokens")
    with torch.inference_mode():
        output = model.generate(
            **inputs,
            max_new_tokens=request.max_tokens,
            do_sample=False,
            pad_token_id=tokenizer.eos_token_id,
        )
    content = tokenizer.decode(
        output[0][input_tokens:], skip_special_tokens=True
    ).strip()
    if request.response_format and request.response_format.get("type") == "json_object":
        try:
            if not isinstance(json.loads(content), dict):
                raise ValueError()
        except ValueError:
            raise HTTPException(502, "Model did not return a JSON object") from None
    output_tokens = output.shape[1] - input_tokens
    return {
        "id": f"amd-{secrets.token_hex(8)}",
        "object": "chat.completion",
        "created": int(time.time()),
        "model": MODEL,
        "choices": [
            {
                "index": 0,
                "message": {"role": "assistant", "content": content},
                "finish_reason": "stop"
                if output_tokens < request.max_tokens
                else "length",
            }
        ],
        "usage": {
            "prompt_tokens": input_tokens,
            "completion_tokens": output_tokens,
            "total_tokens": input_tokens + output_tokens,
        },
    }


@app.post("/v1/chat/completions", dependencies=[Depends(authorize)])
async def completions(request: CompletionRequest):
    if request.model != MODEL or request.stream:
        raise HTTPException(400, "Unsupported model or streaming request")
    if state["active"]:
        raise HTTPException(429, "AMD server at capacity")
    state["active"] = True
    task = asyncio.create_task(asyncio.to_thread(generate, request))
    task.add_done_callback(finish_generation)
    return await asyncio.shield(task)


def finish_generation(task):
    state["active"] = False
    if not task.cancelled():
        task.exception()
