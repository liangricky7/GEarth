import argparse
import json
import os
import pwd
import secrets
import socket
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

MODELS = (
    ("institution", "Qwen/Qwen3-8B", 8000, "0.30", "16"),
    ("citizen", "Qwen/Qwen3-4B", 8001, "0.45", "64"),
)
LABEL = "io.faultline.workload=qwen-roles"
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


def serving_arguments(image, state, role, model, port, memory, sequences, revision):
    return [
        "run",
        "-d",
        "--name",
        f"faultline-qwen-{role}",
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
        model,
        "--revision",
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
        container = inspect(f"faultline-qwen-{role}")
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
        name = f"faultline-qwen-{role}"
        container = inspect(name)
        if container is not None:
            if (
                container["Config"].get("Labels", {}).get("io.faultline.workload")
                != "qwen-roles"
            ):
                raise RuntimeError("Replacement container ownership does not match")
            docker("rm", "-f", name)
    if original_id is not None:
        if not original["State"]["Running"]:
            docker("start", ORIGINAL)
        if not original_workload_running():
            docker("exec", "-d", ORIGINAL, "bash", RESTORE_SCRIPT)
    (state / "connection.env").unlink(missing_ok=True)
    print(
        "Fresh-install containers removed."
        if original_id is None
        else "Original GPT-OSS launch restored; allow time for loading.",
        flush=True,
    )


def launch(state, owner, fresh=False, provider="SPARK_A"):
    prepared = json.loads((state / "prepared.json").read_text())
    original = inspect(ORIGINAL)
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
        if inspect(f"faultline-qwen-{role}") is not None:
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
        docker("stop", ORIGINAL)
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
    except BaseException:
        rollback(state)
        raise
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "action", choices=("prepare", "launch", "deploy", "rollback", "status")
    )
    parser.add_argument("--image", default="vllm/vllm-openai:qwen38")
    parser.add_argument("--fresh", action="store_true")
    parser.add_argument("--provider", choices=("SPARK_A", "SPARK_B"), default="SPARK_A")
    options = parser.parse_args()
    if os.geteuid() != 0 or not os.environ.get("SUDO_USER"):
        raise RuntimeError("Run with sudo from the Spark operator's SSH terminal")
    owner = pwd.getpwnam(os.environ["SUDO_USER"])
    state = Path(owner.pw_dir) / "faultline-qwen"
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
