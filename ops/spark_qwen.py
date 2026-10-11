import argparse
import json
import os
import pwd
import secrets
import signal
import socket
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

MODELS = (
    ("institution", "Qwen/Qwen3-8B", 8000, "0.30", "16"),
    ("citizen", "Qwen/Qwen3-4B", 8001, "0.45", "64"),
)
LABEL = "io.gearth.workload=qwen-roles"
ORIGINAL = "vllm_node"
RESTORE_SCRIPT = "/workspace/exec-script.sh"
MANIFEST = Path(__file__).with_name("qwen-models.json")


def docker(*arguments):
    result = subprocess.run(["docker", *arguments], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"Docker {arguments[0]} failed (exit {result.returncode})")
    return result.stdout.strip()


def inspect(name):
    result = subprocess.run(["docker", "inspect", name], capture_output=True, text=True)
    if result.returncode:
        return None
    return json.loads(result.stdout)[0]


def original_workload_running():
    probe = (
        "import os\n"
        "from pathlib import Path\n"
        "for path in Path('/proc').glob('[0-9]*/cmdline'):\n"
        "    if int(path.parent.name) == os.getpid():\n"
        "        continue\n"
        "    try:\n"
        "        command = path.read_bytes()\n"
        "    except (OSError, UnicodeError):\n"
        "        continue\n"
        "    if b'serve\\x00openai/gpt-oss-120b\\x00' in command:\n"
        "        print('yes')\n"
        "        break\n"
        "else:\n"
        "    print('no')\n"
    )
    return docker("exec", ORIGINAL, "python3", "-c", probe) == "yes"


def private_write(path, text, owner):
    path.write_text(text)
    path.chmod(0o600)
    os.chown(path, owner.pw_uid, owner.pw_gid)


def snapshot_path(model, revision):
    return (
        Path("/root/.cache/huggingface/hub")
        / f"models--{model.replace('/', '--')}"
        / "snapshots"
        / revision
    )


def process_record(pid):
    try:
        path = Path("/proc") / str(pid)
        fields = (path / "stat").read_text().rsplit(") ", 1)[1].split()
        return {
            "pid": int(pid),
            "parent": int(fields[1]),
            "state": fields[0],
            "start": fields[19],
            "arguments": (path / "cmdline").read_bytes().split(b"\x00"),
        }
    except (OSError, ValueError, IndexError):
        return None


def original_recipe_processes():
    records = {}
    roots = []
    for path in Path("/proc").iterdir():
        if path.name.isdigit():
            record = process_record(path.name)
            if record is not None:
                records[record["pid"]] = record
                args = record["arguments"]
                if (
                    b"/home/admin/vllm/spark-vllm-docker/run-recipe.py" in args
                    and b"openai-gpt-oss-120b" in args
                    and b"--solo" in args
                    and b"-c" not in args
                ):
                    roots.append(record["pid"])
    ordered = list(roots)
    seen = set(roots)
    for parent in ordered:
        for pid, record in records.items():
            if record["parent"] == parent and pid not in seen:
                seen.add(pid)
                ordered.append(pid)
    return [records[pid] for pid in ordered]


def pause_original_recipes(state, owner):
    record_file = state / "paused-gpt-oss-launchers.json"
    paused = json.loads(record_file.read_text()) if record_file.exists() else []
    recorded = {(entry["pid"], entry["start"]) for entry in paused}
    # Rescan after stopping parents so children cannot keep launching containers.
    for _ in range(5):
        stopped = False
        for entry in original_recipe_processes():
            current = process_record(entry["pid"])
            if current is None or current["start"] != entry["start"]:
                continue
            identity = (entry["pid"], entry["start"])
            if current["state"] in ("T", "t"):
                continue
            if identity not in recorded:
                paused.append({"pid": entry["pid"], "start": entry["start"]})
                recorded.add(identity)
                private_write(record_file, json.dumps(paused), owner)
            try:
                os.kill(entry["pid"], signal.SIGSTOP)
            except ProcessLookupError:
                continue
            stopped = True
        if not stopped:
            break
        time.sleep(0.1)
    for entry in paused:
        current = process_record(entry["pid"])
        if (
            current is not None
            and current["start"] == entry["start"]
            and current["state"] not in ("T", "t", "Z")
        ):
            raise RuntimeError("The GPT-OSS launcher did not pause; reinstall stopped")
    print(
        f"Paused {len(paused)} GPT-OSS launcher processes for Qwen installation.",
        flush=True,
    )


def resume_original_recipes(state):
    record_file = state / "paused-gpt-oss-launchers.json"
    if not record_file.exists():
        return
    for entry in json.loads(record_file.read_text()):
        current = process_record(entry["pid"])
        if current is not None and current["start"] == entry["start"]:
            try:
                os.kill(entry["pid"], signal.SIGCONT)
            except ProcessLookupError:
                pass
    record_file.unlink()


def validate_cached_tokenizers(state, prepared):
    probe = """import json, sys
from transformers import AutoConfig, AutoTokenizer
for model, snapshot in json.loads(sys.argv[1]).items():
    AutoConfig.from_pretrained(snapshot, local_files_only=True)
    tokenizer = AutoTokenizer.from_pretrained(snapshot, local_files_only=True)
    if not tokenizer.encode('Hello from GEarth'):
        raise RuntimeError('Cached tokenizer returned no tokens')
    print('Verified cached tokenizer: ' + model, flush=True)
"""
    snapshots = {
        model: str(snapshot_path(model, prepared["revisions"][model]))
        for _, model, *_ in MODELS
    }
    docker(
        "run",
        "--rm",
        "--network",
        "none",
        "-e",
        "HF_HUB_OFFLINE=1",
        "-e",
        "TRANSFORMERS_OFFLINE=1",
        "-v",
        f"{state / 'cache'}:/root/.cache/huggingface:ro",
        "--entrypoint",
        "python3",
        prepared["image"],
        "-c",
        probe,
        json.dumps(snapshots),
    )
    print("Both cached tokenizers verified offline.", flush=True)


def serving_arguments(image, state, role, model, port, memory, sequences, revision):
    snapshot = str(snapshot_path(model, revision))
    return [
        "run",
        "-d",
        "--name",
        f"gearth-qwen-{role}",
        "--label",
        LABEL,
        "--gpus",
        "all",
        "--ipc=host",
        "--restart",
        "unless-stopped",
        "--env-file",
        str(state / f"{role}.env"),
        "-v",
        f"{state / 'cache'}:/root/.cache/huggingface",
        "-p",
        f"127.0.0.1:{port}:8000",
        "--entrypoint",
        "vllm",
        image,
        "serve",
        snapshot,
        "--served-model-name",
        model,
        "--tokenizer",
        snapshot,
        "--revision",
        revision,
        "--tokenizer-revision",
        revision,
        "--host",
        "0.0.0.0",
        "--port",
        "8000",
        "--dtype",
        "bfloat16",
        "--gpu-memory-utilization",
        memory,
        "--max-model-len",
        "8192",
        "--max-num-seqs",
        sequences,
        "--max-num-batched-tokens",
        "8192",
        "--enable-prefix-caching",
        "--attention-backend",
        "FLASHINFER",
    ]


def prepare(state, owner, image):
    if inspect(image) is None:
        print(f"Pulling Spark runtime {image}", flush=True)
        docker("pull", "--platform", "linux/arm64", image)
    details = json.loads(docker("image", "inspect", image))[0]
    if details["Architecture"] != "arm64":
        raise RuntimeError("An ARM64 Spark image is required")
    image = details["Id"]
    probe = (
        "import json, torch, vllm; from transformers import AutoConfig; "
        "from vllm.model_executor.models.registry import ModelRegistry; "
        "assert 'GB10' in torch.cuda.get_device_name(0); "
        "AutoConfig.for_model('qwen3'); "
        "assert 'Qwen3ForCausalLM' in ModelRegistry.get_supported_archs(); "
        "print(json.dumps({'gpu':torch.cuda.get_device_name(0),'vllm':vllm.__version__}))"
    )
    runtime = json.loads(
        docker(
            "run",
            "--rm",
            "--gpus",
            "all",
            "--entrypoint",
            "python3",
            image,
            "-c",
            probe,
        ).splitlines()[-1]
    )
    print(f"Runtime verified: {runtime['gpu']}, vLLM {runtime['vllm']}", flush=True)
    cache = state / "cache"
    cache.mkdir(exist_ok=True)
    catalog = json.loads(MANIFEST.read_text())
    revisions = {entry["model"]: entry["revision"] for entry in catalog.values()}
    if set(revisions) != {model for _, model, *_ in MODELS}:
        raise RuntimeError(
            "Manifest must contain exactly the selected Qwen3 model pair"
        )
    downloader = """import json, sys
from huggingface_hub import snapshot_download
revisions = json.loads(sys.argv[1])
for model, revision in revisions.items():
    snapshot_download(model, revision=revision)
print(json.dumps(revisions))
"""
    print(
        "Downloading the 8B and 4B checkpoints; the existing server stays running.",
        flush=True,
    )
    revisions = json.loads(
        docker(
            "run",
            "--rm",
            "-v",
            f"{cache}:/root/.cache/huggingface",
            "--entrypoint",
            "python3",
            image,
            "-c",
            downloader,
            json.dumps(revisions),
        ).splitlines()[-1]
    )
    for role, *_ in MODELS:
        key_file = state / f"{role}.env"
        if not key_file.exists():
            private_write(
                key_file, f"VLLM_API_KEY={secrets.token_urlsafe(48)}\n", owner
            )
    prepared = {
        "image": image,
        "runtime": runtime,
        "revisions": revisions,
        "prepared_at": datetime.now(timezone.utc).isoformat(),
    }
    private_write(state / "prepared.json", json.dumps(prepared), owner)
    print("Preparation complete. No model server was replaced.", flush=True)


def api_key(state, role):
    return (state / f"{role}.env").read_text().strip().split("=", 1)[1]


def model_request(port, key, payload=None):
    endpoint = "chat/completions" if payload is not None else "models"
    request = Request(
        f"http://127.0.0.1:{port}/v1/{endpoint}",
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    with urlopen(request, timeout=60) as response:
        return json.load(response)


def wait_ready(state, role, model, port, timeout=900):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        container = inspect(f"gearth-qwen-{role}")
        if (
            container is None
            or not container["State"]["Running"]
            or container.get("RestartCount", 0) > 1
        ):
            raise RuntimeError(f"The {role} model container exited")
        try:
            models = model_request(port, api_key(state, role))
            if model in [entry["id"] for entry in models["data"]]:
                return
        except (OSError, ValueError, KeyError):
            pass
        print(f"Waiting for {model} readiness", flush=True)
        time.sleep(15)
    raise RuntimeError(f"The {role} model did not become ready")


def validate_server(state, role, model, port):
    try:
        model_request(port, "incorrect-key")
    except HTTPError as error:
        if error.code != 401:
            raise RuntimeError(f"The {role} endpoint did not return 401") from None
    else:
        raise RuntimeError(f"The {role} endpoint allowed unauthenticated access")
    result = model_request(
        port,
        api_key(state, role),
        {
            "model": model,
            "messages": [
                {
                    "role": "user",
                    "content": "Choose the only available action, hold. Return exactly a JSON object with choice 0 and a short nonempty reason.",
                }
            ],
            "max_tokens": 128,
            "temperature": 0,
            "chat_template_kwargs": {"enable_thinking": False},
            "response_format": {"type": "json_object"},
        },
    )
    action = json.loads(result["choices"][0]["message"]["content"])
    if (
        set(action) != {"choice", "reason"}
        or type(action["choice"]) is not int
        or action["choice"] != 0
        or not isinstance(action["reason"], str)
        or not 1 <= len(action["reason"].strip()) <= 240
    ):
        raise RuntimeError(f"The {role} endpoint failed JSON decision validation")


def rollback(state):
    original_id = json.loads((state / "rollback.json").read_text())["container_id"]
    original = inspect(ORIGINAL)
    if original_id is not None and (original is None or original["Id"] != original_id):
        raise RuntimeError(
            "Original container identity changed; automatic restoration stopped"
        )
    for role, *_ in MODELS:
        name = f"gearth-qwen-{role}"
        container = inspect(name)
        if container is not None:
            if (
                container["Config"].get("Labels", {}).get("io.gearth.workload")
                != "qwen-roles"
            ):
                raise RuntimeError("Replacement container ownership does not match")
            docker("rm", "-f", name)
    if original_id is not None:
        if not original["State"]["Running"]:
            docker("start", ORIGINAL)
        if not original_workload_running():
            docker("exec", "-d", ORIGINAL, "bash", RESTORE_SCRIPT)
    resume_original_recipes(state)
    (state / "connection.env").unlink(missing_ok=True)
    print(
        "Fresh-install containers removed."
        if original_id is None
        else "Original GPT-OSS launch restored; allow time for loading.",
        flush=True,
    )


def recover(state, owner, original_id, provider="SPARK_A"):
    saved = json.loads((state / "rollback.json").read_text())
    if saved.get("container_id") is None:
        raise RuntimeError("Recovery requires an existing migration rollback record")
    original = inspect(ORIGINAL)
    if (
        original is None
        or original["Id"] != original_id
        or not original["State"]["Running"]
        or original.get("Config", {}).get("Image") != "vllm-node-mxfp4"
    ):
        raise RuntimeError(
            "Current original container does not match the inspected ID and image"
        )
    if not original_workload_running():
        raise RuntimeError(
            "Current original container is not running the expected GPT-OSS workload"
        )
    docker("exec", ORIGINAL, "test", "-f", RESTORE_SCRIPT)
    for role, *_ in MODELS:
        container = inspect(f"gearth-qwen-{role}")
        if container is not None and (
            container.get("Config", {}).get("Labels", {}).get("io.gearth.workload")
            != "qwen-roles"
        ):
            raise RuntimeError("Replacement container ownership does not match")
    prepared = json.loads((state / "prepared.json").read_text())
    validate_cached_tokenizers(state, prepared)
    current = inspect(ORIGINAL)
    if (
        current is None
        or current["Id"] != original_id
        or not current["State"]["Running"]
    ):
        raise RuntimeError(
            "Original container changed during recovery checks; no saved state changed"
        )
    backup = state / ("rollback.before-recovery-" + str(time.time_ns()) + ".json")
    private_write(backup, json.dumps(saved), owner)
    private_write(
        state / "rollback.json", json.dumps({"container_id": original_id}), owner
    )
    print(
        "Verified current GPT-OSS container; prior rollback record backed up.",
        flush=True,
    )
    rollback(state)
    launch(state, owner, provider=provider, expected_original_id=original_id)


def reinstall(state, owner, provider="SPARK_A"):
    prepared = json.loads((state / "prepared.json").read_text())
    validate_cached_tokenizers(state, prepared)
    for role, *_ in MODELS:
        api_key(state, role)
        container = inspect(f"gearth-qwen-{role}")
        if container is not None and (
            container.get("Config", {}).get("Labels", {}).get("io.gearth.workload")
            != "qwen-roles"
        ):
            raise RuntimeError("Replacement container ownership does not match")
    pending_cleanup = False
    try:
        pause_original_recipes(state, owner)
        original = inspect(ORIGINAL)
        if original is not None:
            original_image = original.get("Config", {}).get("Image")
            if original_image not in ("vllm-node-mxfp4", "vllm-node-mxfp4:latest"):
                raise RuntimeError(
                    f"Reinstall found original image {original_image!r}; container preserved"
                )
            # docker cp also works when the preserved container is stopped.
            with tempfile.TemporaryDirectory() as temporary:
                docker(
                    "cp",
                    f"{original['Id']}:{RESTORE_SCRIPT}",
                    str(Path(temporary) / "restore.sh"),
                )
        replacements = []
        for role, *_ in MODELS:
            container = inspect(f"gearth-qwen-{role}")
            if container is not None:
                if (
                    container.get("Config", {})
                    .get("Labels", {})
                    .get("io.gearth.workload")
                    != "qwen-roles"
                ):
                    raise RuntimeError(
                        "Replacement ownership changed; reinstall stopped"
                    )
                replacements.append(container["Id"])
        current = inspect(ORIGINAL)
        original_id = original["Id"] if original is not None else None
        if (current["Id"] if current is not None else None) != original_id:
            raise RuntimeError(
                "Another launcher is still replacing vllm_node; reinstall stopped"
            )
        rollback_file = state / "rollback.json"
        if rollback_file.exists():
            backup = state / (
                "rollback.before-reinstall-" + str(time.time_ns()) + ".json"
            )
            private_write(backup, rollback_file.read_text(), owner)
        private_write(rollback_file, json.dumps({"container_id": original_id}), owner)
        pending_cleanup = True
        (state / "connection.env").unlink(missing_ok=True)
        for container_id in replacements:
            docker("rm", "-f", container_id)
        if original is not None and original["State"]["Running"]:
            docker("stop", original_id)
        print(
            "Reinstalling both Qwen servers from cached weights; original container preserved.",
            flush=True,
        )
        pending_cleanup = (
            False  # start_models handles failures after server startup begins.
        )
        start_models(state, owner, prepared, provider)
    except BaseException:
        try:
            if pending_cleanup:
                rollback(state)
        finally:
            resume_original_recipes(state)
        raise


def launch(state, owner, fresh=False, provider="SPARK_A", expected_original_id=None):
    prepared = json.loads((state / "prepared.json").read_text())
    validate_cached_tokenizers(state, prepared)
    original = inspect(ORIGINAL)
    if expected_original_id is not None and (
        original is None or original["Id"] != expected_original_id
    ):
        raise RuntimeError(
            "Original container changed before launch; deployment stopped"
        )
    if fresh:
        if original is not None and original["State"]["Running"]:
            raise RuntimeError(
                "Fresh installation refuses to stop a running original container"
            )
        with socket.socket() as connection:
            connection.settimeout(2)
            if connection.connect_ex(("127.0.0.1", 8000)) == 0:
                raise RuntimeError("Port 8000 is already occupied")
    else:
        if original is None or not original["State"]["Running"]:
            raise RuntimeError("Expected running GPT-OSS container was not found")
        if not original_workload_running():
            raise RuntimeError(
                "The original container is not the expected GPT-OSS workload"
            )
        docker("exec", ORIGINAL, "test", "-f", RESTORE_SCRIPT)
    for role, *_ in MODELS:
        if inspect(f"gearth-qwen-{role}") is not None:
            raise RuntimeError(
                "A replacement container already exists; inspect it before relaunching"
            )
    with socket.socket() as connection:
        connection.settimeout(2)
        if connection.connect_ex(("127.0.0.1", 8001)) == 0:
            raise RuntimeError("Port 8001 is already occupied")
    private_write(
        state / "rollback.json",
        json.dumps({"container_id": None if fresh else original["Id"]}),
        owner,
    )
    if not fresh:
        docker("stop", expected_original_id or ORIGINAL)
    start_models(state, owner, prepared, provider)


def start_models(state, owner, prepared, provider):
    try:
        for role, model, port, memory, sequences in MODELS:
            docker(
                *serving_arguments(
                    prepared["image"],
                    state,
                    role,
                    model,
                    port,
                    memory,
                    sequences,
                    prepared["revisions"][model],
                )
            )
            wait_ready(state, role, model, port)
            validate_server(state, role, model, port)
            print(f"Verified {model} on loopback port {port}", flush=True)
        write_connection(state, owner, provider)
    except BaseException:
        rollback(state)
        raise


def write_connection(state, owner, provider):
    connection = {
        "SPARK_A_ENABLED": "true",
        "SPARK_A_MODEL": "Qwen/Qwen3-8B",
        "SPARK_A_BASE_URL": "http://127.0.0.1:18001/v1",
        "SPARK_A_DECISION_GROUP": "institution",
        "SPARK_A_CAPACITY": "4",
        "SPARK_A_API_KEY": api_key(state, "institution"),
        "SPARK_A_CITIZEN_ENABLED": "true",
        "SPARK_A_CITIZEN_MODEL": "Qwen/Qwen3-4B",
        "SPARK_A_CITIZEN_BASE_URL": "http://127.0.0.1:18003/v1",
        "SPARK_A_CITIZEN_DECISION_GROUP": "citizen",
        "SPARK_A_CITIZEN_CAPACITY": "8",
        "SPARK_A_CITIZEN_API_KEY": api_key(state, "citizen"),
    }
    if provider == "SPARK_B":
        connection = {
            key.replace("SPARK_A", "SPARK_B"): value
            for key, value in connection.items()
        }
        connection["SPARK_B_BASE_URL"] = "http://127.0.0.1:18002/v1"
        connection["SPARK_B_CITIZEN_BASE_URL"] = "http://127.0.0.1:18004/v1"
    private_write(
        state / "connection.env",
        "".join(f"{key}={value}\n" for key, value in connection.items()),
        owner,
    )
    print(
        f"Both models verified. Private connection file: {state / 'connection.env'}",
        flush=True,
    )


def resume(state, owner, fresh=False, provider="SPARK_A"):
    prepared = json.loads((state / "prepared.json").read_text())
    saved = json.loads((state / "rollback.json").read_text())
    original_id = saved["container_id"]
    if fresh != (original_id is None):
        raise RuntimeError("Resume mode does not match the saved deployment")
    original = inspect(ORIGINAL)
    if original_id is not None and (original is None or original["Id"] != original_id):
        raise RuntimeError("Original container identity changed; resume stopped")
    if original_id is None and original is not None and original["State"]["Running"]:
        raise RuntimeError("Fresh resume refuses to stop a running original container")
    existing = {}
    for role, model, port, memory, sequences in MODELS:
        name = f"gearth-qwen-{role}"
        container = inspect(name)
        existing[role] = container
        if container is None:
            with socket.socket() as connection:
                connection.settimeout(2)
                if connection.connect_ex(("127.0.0.1", port)) == 0:
                    raise RuntimeError(
                        f"Port {port} is occupied without its expected container"
                    )
            continue
        arguments = serving_arguments(
            prepared["image"],
            state,
            role,
            model,
            port,
            memory,
            sequences,
            prepared["revisions"][model],
        )
        config = container.get("Config", {})
        ports = container.get("HostConfig", {}).get("PortBindings", {})
        cache = any(
            mount.get("Source") == str(state / "cache")
            and mount.get("Destination") == "/root/.cache/huggingface"
            for mount in container.get("Mounts", [])
        )
        if (
            config.get("Labels", {}).get("io.gearth.workload") != "qwen-roles"
            or container.get("Image") != prepared["image"]
            or config.get("Entrypoint") != ["vllm"]
            or config.get("Cmd") != arguments[arguments.index(prepared["image"]) + 1 :]
            or f"VLLM_API_KEY={api_key(state, role)}" not in config.get("Env", [])
            or ports != {"8000/tcp": [{"HostIp": "127.0.0.1", "HostPort": str(port)}]}
            or not cache
        ):
            raise RuntimeError(
                f"Existing {role} container does not match the prepared deployment"
            )
    validate_cached_tokenizers(state, prepared)
    if original_id is not None and original["State"]["Running"]:
        docker("stop", ORIGINAL)
    try:
        for role, model, port, memory, sequences in MODELS:
            container = existing[role]
            if container is None:
                docker(
                    *serving_arguments(
                        prepared["image"],
                        state,
                        role,
                        model,
                        port,
                        memory,
                        sequences,
                        prepared["revisions"][model],
                    )
                )
            elif not container["State"]["Running"]:
                docker("start", f"gearth-qwen-{role}")
            print(f"Resuming {model} on loopback port {port}", flush=True)
            wait_ready(state, role, model, port)
            validate_server(state, role, model, port)
            print(f"Verified {model} on loopback port {port}", flush=True)
    except BaseException:
        rollback(state)
        raise
    write_connection(state, owner, provider)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "action",
        choices=(
            "prepare",
            "launch",
            "resume",
            "recover",
            "reinstall",
            "deploy",
            "rollback",
            "status",
        ),
    )
    parser.add_argument("--image", default="vllm/vllm-openai:qwen38")
    parser.add_argument("--fresh", action="store_true")
    parser.add_argument(
        "--original-id", help="Inspected original container ID for guarded recovery"
    )
    parser.add_argument("--provider", choices=("SPARK_A", "SPARK_B"), default="SPARK_A")
    options = parser.parse_args()
    if os.geteuid() != 0 or not os.environ.get("SUDO_USER"):
        raise RuntimeError("Run with sudo from the Spark operator's SSH terminal")
    owner = pwd.getpwnam(os.environ["SUDO_USER"])
    state = Path(owner.pw_dir) / "gearth-qwen"
    state.mkdir(mode=0o700, exist_ok=True)
    state.chmod(0o700)
    os.chown(state, owner.pw_uid, owner.pw_gid)
    if options.action == "deploy":
        prepare(state, owner, options.image)
        launch(state, owner, options.fresh, options.provider)
    elif options.action == "prepare":
        prepare(state, owner, options.image)
    elif options.action == "launch":
        launch(state, owner, options.fresh, options.provider)
    elif options.action == "resume":
        resume(state, owner, options.fresh, options.provider)
    elif options.action == "recover":
        if not options.original_id or options.fresh:
            raise RuntimeError("Recovery requires --original-id and migration mode")
        recover(state, owner, options.original_id, options.provider)
    elif options.action == "reinstall":
        reinstall(state, owner, options.provider)
    elif options.action == "rollback":
        rollback(state)
    else:
        for role, model, port, *_ in MODELS:
            try:
                ready = model in [
                    entry["id"]
                    for entry in model_request(port, api_key(state, role))["data"]
                ]
            except (OSError, ValueError, KeyError):
                ready = False
            print(
                json.dumps({"role": role, "model": model, "port": port, "ready": ready})
            )


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError, KeyError) as error:
        print(f"Qwen setup stopped: {type(error).__name__}: {error}", file=sys.stderr)
        sys.exit(1)
