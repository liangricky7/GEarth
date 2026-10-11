"""Reversible, single-Spark Qwen/Gemma concurrency and replica experiment.

Run with sudo on Spark A after stopping the laptop simulation. The sibling
gemma-benchmark-prompts.json file is exported by the laptop using its real
worker prompts. No provider settings or existing container definitions change.
"""

import argparse
import concurrent.futures
import fcntl
import json
import math
import os
import pwd
import secrets
import signal
import socket
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

IMAGE = "vllm/vllm-openai@sha256:2970dd3c8fd00bccbc663345f9b4d87b35c491f4b13a2a4cdef60fe5c015f6e2"
GEMMA = "google/gemma-4-E4B-it"
GEMMA_REVISION = "ee0ef6023621cff504d758262d4e04895a5af4a2"
QWEN = "Qwen/Qwen3-4B"
LABEL_KEY = "io.faultline.experiment"
LABEL_VALUE = "gemma-e4b"
SAMPLES = Path(__file__).with_name("gemma-benchmark-prompts.json")


def docker(*arguments):
    result = subprocess.run(["docker", *arguments], capture_output=True, text=True)
    if result.returncode:
        # Container output may include authentication configuration.
        raise RuntimeError(f"Docker {arguments[0]} failed; inspect the private log")
    return result.stdout.strip()


def inspect(identity):
    result = subprocess.run(["docker", "inspect", identity], capture_output=True, text=True)
    return json.loads(result.stdout)[0] if result.returncode == 0 else None


def private_write(path, value, owner):
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w") as handle:
        temporary.chmod(0o600)
        handle.write(value)
    os.chown(temporary, owner.pw_uid, owner.pw_gid)
    temporary.replace(path)


def request(port, key, payload=None):
    endpoint = "chat/completions" if payload is not None else "models"
    req = Request(
        f"http://127.0.0.1:{port}/v1/{endpoint}",
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    with urlopen(req, timeout=60) as response:
        return json.load(response)


def validate_choice(body, candidate_count):
    choice = json.loads(body["choices"][0]["message"]["content"])
    if set(choice) != {"choice", "reason"}:
        raise ValueError("INVALID_SCHEMA")
    if type(choice["choice"]) is not int or not 0 <= choice["choice"] < candidate_count:
        raise ValueError("UNAVAILABLE_ACTION")
    if not isinstance(choice["reason"], str) or not 1 <= len(choice["reason"].strip()) <= 240:
        raise ValueError("INVALID_REASON")


def payload_for(sample, model):
    return {**sample["payload"], "model": model,
            "chat_template_kwargs": {"enable_thinking": False}}


def validate_samples(samples):
    if not isinstance(samples, list) or not samples:
        raise RuntimeError("Benchmark prompt file must contain a nonempty list")
    for index, sample in enumerate(samples):
        try:
            count = sample["candidate_count"]
            payload = sample["payload"]
            messages = payload["messages"]
            menu = json.loads(next(message["content"] for message in messages
                                   if message["role"] == "user"))["available_actions"]
            if type(count) is not int or count < 1 or not isinstance(menu, list) or len(menu) != count:
                raise ValueError("Missing or inconsistent action menu")
            if not all(isinstance(action, str) and action.startswith(f"{number}:")
                       for number, action in enumerate(menu)):
                raise ValueError("Invalid numbered action menu")
        except (KeyError, TypeError, ValueError, StopIteration) as error:
            raise RuntimeError(f"Benchmark prompt {index} has an invalid action menu") from error


def ready(identity, port, key, model, seconds=900):
    deadline = time.monotonic() + seconds
    previous_report = 0
    while time.monotonic() < deadline:
        details = inspect(identity)
        if details is None or not details["State"]["Running"]:
            raise RuntimeError("Model server exited during startup")
        try:
            if model in [item["id"] for item in request(port, key)["data"]]:
                return
        except (OSError, ValueError, KeyError):
            pass
        if time.monotonic() - previous_report > 30:
            print(f"Waiting for {model} on loopback port {port}", flush=True)
            previous_report = time.monotonic()
        time.sleep(2)
    raise RuntimeError("Model startup timed out")


def check_original(details, expected):
    if details is None or details["Name"] != f"/faultline-qwen-{expected}":
        raise RuntimeError("Expected Qwen container is missing")
    if details["Config"].get("Labels", {}).get("io.faultline.workload") != "qwen-roles":
        raise RuntimeError("Qwen ownership label mismatch")
    model = "Qwen/Qwen3-8B" if expected == "institution" else QWEN
    if model not in details["Config"]["Cmd"]:
        raise RuntimeError("Qwen model identity mismatch")
    return details["Id"]


def sweep(model, ports, key, samples, concurrencies, waves):
    # Warm each replica before timing, then use the same fixed prompt set.
    for port in ports:
        for sample in samples:
            validate_choice(request(port, key, payload_for(sample, model)), sample["candidate_count"])
    rows = []
    for concurrency in concurrencies:
        started = time.perf_counter()
        def one(index):
            sample = samples[index % len(samples)]
            begin = time.perf_counter()
            try:
                body = request(ports[index % len(ports)], key, payload_for(sample, model))
                validate_choice(body, sample["candidate_count"])
                return {"valid": True, "seconds": time.perf_counter() - begin,
                        "usage": body.get("usage", {})}
            except (OSError, ValueError, KeyError, TypeError) as error:
                return {"valid": False, "seconds": time.perf_counter() - begin,
                        "error": type(error).__name__}
        # Fixed request count gives each concurrency level the same work.
        attempts = max(32, max(concurrencies) * waves)
        with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
            records = list(pool.map(one, range(attempts)))
        elapsed = time.perf_counter() - started
        latencies = sorted(record["seconds"] for record in records)
        valid = sum(record["valid"] for record in records)
        row = {"concurrency": concurrency, "attempts": attempts, "valid": valid,
               "elapsed_seconds": round(elapsed, 3), "valid_per_second": round(valid / elapsed, 3),
               "p50_seconds": round(latencies[len(latencies) // 2], 3),
               "p95_seconds": round(latencies[math.ceil(len(latencies) * .95) - 1], 3),
               "errors": sorted({record["error"] for record in records if not record["valid"]})}
        rows.append(row)
        print(json.dumps({"model": model, "replicas": len(ports), **row}), flush=True)
        if valid < attempts:
            break  # Avoid escalating load after a failed level.
    return rows


def restore(state, owner):
    record_path = state / "rollback.json"
    if not record_path.exists():
        return
    record = json.loads(record_path.read_text())
    errors = []
    # Only exact IDs created by this experiment may be removed.
    for identity in record["created"]:
        try:
            details = inspect(identity)
            if details is not None:
                if details["Config"].get("Labels", {}).get(LABEL_KEY) != LABEL_VALUE:
                    raise RuntimeError("Experiment container ownership mismatch")
                docker("rm", "-f", identity)
        except RuntimeError as error:
            errors.append(str(error))
    # Remove experiment memory allocations before restarting either Qwen.
    if errors:
        raise RuntimeError("; ".join(errors))
    for original in record["originals"]:
        details = inspect(original["name"])
        if check_original(details, original["role"]) != original["id"]:
            raise RuntimeError("Qwen identity changed; automatic restoration stopped")
        if original["running"] and not details["State"]["Running"]:
            docker("start", original["id"])
    for original in record["originals"]:
        if original["running"]:
            key = (Path(owner.pw_dir) / "faultline-qwen" / f"{original['role']}.env").read_text().strip().split("=", 1)[1]
            ready(original["id"], original["port"], key, original["model"])
    record["restored"] = True
    private_write(record_path, json.dumps(record, indent=2), owner)
    print("Original Qwen servers restored and verified.", flush=True)


def experiment(state, owner, options):
    if not SAMPLES.exists():
        raise RuntimeError("Benchmark prompt file is missing")
    samples = json.loads(SAMPLES.read_text())
    validate_samples(samples)
    previous = state / "rollback.json"
    if previous.exists() and not json.loads(previous.read_text()).get("restored"):
        raise RuntimeError("Restore the preceding experiment before starting another")
    for port in (8002, 8003):
        with socket.socket() as sock:
            if sock.connect_ex(("127.0.0.1", port)) == 0:
                raise RuntimeError(f"Experiment port {port} is occupied")
    print("Downloading pinned ARM64 runtime and Gemma weights; Qwen stays available during preparation.", flush=True)
    docker("pull", "--platform", "linux/arm64", options.image)
    image = json.loads(docker("image", "inspect", options.image))[0]
    if image["Architecture"] != "arm64":
        raise RuntimeError("An ARM64 image is required")
    image_id = image["Id"]
    qwen_state = Path(owner.pw_dir) / "faultline-qwen"
    prepared = json.loads((qwen_state / "prepared.json").read_text())
    qwen_revision = prepared["revisions"][QWEN]
    qwen_snapshot = f"/qwen-cache/hub/models--Qwen--Qwen3-4B/snapshots/{qwen_revision}"
    gemma_snapshot = f"/root/.cache/huggingface/hub/models--google--gemma-4-E4B-it/snapshots/{GEMMA_REVISION}"
    cache = state / "cache"
    cache.mkdir(exist_ok=True)
    mounts = ["-v", f"{cache}:/root/.cache/huggingface", "-v", f"{qwen_state / 'cache'}:/qwen-cache:ro"]
    probe = """import json,sys,torch,vllm
from huggingface_hub import snapshot_download
from transformers import AutoConfig,AutoTokenizer
from vllm.model_executor.models.registry import ModelRegistry
assert 'GB10' in torch.cuda.get_device_name(0)
archs=ModelRegistry.get_supported_archs()
assert 'Gemma4ForConditionalGeneration' in archs and 'Qwen3ForCausalLM' in archs
snapshot_download(sys.argv[1],revision=sys.argv[2])
for path in sys.argv[3:]:
    AutoConfig.from_pretrained(path,local_files_only=True)
    tokenizer=AutoTokenizer.from_pretrained(path,local_files_only=True)
    assert tokenizer.encode('Choose a household action')
print(json.dumps({'vllm':vllm.__version__,'gpu':torch.cuda.get_device_name(0)}))
"""
    runtime = json.loads(docker("run", "--rm", "--gpus", "all", *mounts, "--entrypoint", "python3", image_id,
                                "-c", probe, GEMMA, GEMMA_REVISION, qwen_snapshot, gemma_snapshot).splitlines()[-1])
    print("Runtime and cached tokenizers verified.", flush=True)
    originals = []
    for role, port, model in (("institution", 8000, "Qwen/Qwen3-8B"), ("citizen", 8001, QWEN)):
        name = f"faultline-qwen-{role}"
        details = inspect(name)
        identity = check_original(details, role)
        originals.append({"name": name, "id": identity, "running": details["State"]["Running"],
                          "role": role, "port": port, "model": model})
    record = {"originals": originals, "created": [], "restored": False}
    key = secrets.token_urlsafe(48)
    private_write(state / "server.env", f"VLLM_API_KEY={key}\nHF_HUB_OFFLINE=1\n", owner)
    private_write(state / "rollback.json", json.dumps(record, indent=2), owner)
    evidence = {"measured_at": datetime.now(timezone.utc).isoformat(), "runtime": runtime,
                "image": options.image, "gemma_revision": GEMMA_REVISION, "qwen_revision": qwen_revision,
                "total_gpu_memory_budget": .60, "prompt_count": len(samples), "prompt_source": "fresh citizen observations generated from the saved 1000-agent world",
                "trials": [], "limitations": ["Short load sweep; not sustained capacity qualification.",
                "Measures inference and output validity, not decision quality or end-to-end simulation speed.",
                "Does not measure energy consumption. Other users on this Spark can affect results.",
                "Qwen baseline is re-served in the same runtime and memory budget as Gemma."]}
    try:
        for original in originals:
            if check_original(inspect(original["name"]), original["role"]) != original["id"]:
                raise RuntimeError("Qwen identity changed before the experiment")
            if original["running"]:
                docker("stop", original["id"])
        for model, snapshot, replicas in ((QWEN, qwen_snapshot, 1), (GEMMA, gemma_snapshot, 1), (GEMMA, gemma_snapshot, 2)):
            current_ids = []
            ports = [8002 + index for index in range(replicas)]
            for index, port in enumerate(ports):
                name = f"faultline-gemma-bench-{secrets.token_hex(4)}"
                identity = docker("run", "-d", "--name", name, "--label", f"{LABEL_KEY}={LABEL_VALUE}",
                    "--gpus", "all", "--ipc=host", "--env-file", str(state / "server.env"), *mounts,
                    "-p", f"127.0.0.1:{port}:8000", "--entrypoint", "vllm", image_id,
                    "serve", snapshot, "--served-model-name", model, "--tokenizer", snapshot,
                    "--host", "0.0.0.0", "--port", "8000", "--dtype", "bfloat16",
                    "--gpu-memory-utilization", str(.60 / replicas), "--max-model-len", "8192",
                    "--max-num-seqs", "64", "--max-num-batched-tokens", "8192", "--enable-prefix-caching",
                    *( ["--limit-mm-per-prompt", '{"image":0,"audio":0}'] if model == GEMMA else [] ))
                record["created"].append(identity)
                private_write(state / "rollback.json", json.dumps(record, indent=2), owner)
                current_ids.append(identity)
                ready(identity, port, key, model)
            # Authentication is required even on loopback.
            for port in ports:
                try:
                    request(port, "")
                except HTTPError as error:
                    if error.code != 401:
                        raise RuntimeError("Unexpected unauthenticated status") from None
                else:
                    raise RuntimeError("Experiment endpoint did not enforce authentication")
            rows = sweep(model, ports, key, samples, options.concurrency, options.waves)
            evidence["trials"].append({"model": model, "replicas": replicas, "results": rows})
            private_write(state / "results.json", json.dumps(evidence, indent=2) + "\n", owner)
            for identity in current_ids:
                docker("rm", "-f", identity)
    finally:
        for identity in record["created"]:
            if inspect(identity) is not None:
                log = subprocess.run(["docker", "logs", "--tail", "200", identity], capture_output=True, text=True)
                private_write(state / f"server-{identity[:12]}.log", log.stdout + log.stderr, owner)
        restore(state, owner)
    print(f"Experiment complete. Results: {state / 'results.json'}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["test", "restore"])
    parser.add_argument("--image", default=IMAGE)
    parser.add_argument("--concurrency", nargs="+", type=int, default=[1, 4, 8, 16, 32])
    parser.add_argument("--waves", type=int, default=2)
    options = parser.parse_args()
    if not options.concurrency or any(not 1 <= number <= 64 for number in options.concurrency) or not 1 <= options.waves <= 4:
        parser.error("Use concurrency 1-64 and waves 1-4")
    if os.geteuid() != 0 or not os.environ.get("SUDO_USER"):
        raise RuntimeError("Run with sudo from the Spark account's SSH terminal")
    owner = pwd.getpwnam(os.environ["SUDO_USER"])
    state = Path(owner.pw_dir) / "faultline-gemma-experiment"
    state.mkdir(exist_ok=True)
    os.chown(state, owner.pw_uid, owner.pw_gid)
    state.chmod(0o700)
    with (state / "experiment.lock").open("w") as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
        if options.action == "restore":
            restore(state, owner)
        else:
            experiment(state, owner, options)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, KeyError, KeyboardInterrupt) as error:
        print(f"Experiment stopped: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
