import json
import io
import os
from contextlib import redirect_stdout
from pathlib import Path
from types import SimpleNamespace

import pytest

from ops import spark_qwen


@pytest.mark.parametrize("running", [True, False])
def test_original_probe_ignores_itself_and_keeps_process_arguments_private(
    monkeypatch, running
):
    command = (
        b"vllm\x00serve\x00openai/gpt-oss-120b\x00--api-key\x00private-test-key\x00"
    )

    def inaccessible():
        raise OSError("Process exited")

    entries = [
        SimpleNamespace(
            parent=SimpleNamespace(name=str(os.getpid())), read_bytes=lambda: command
        ),
        SimpleNamespace(parent=SimpleNamespace(name="1"), read_bytes=inaccessible),
        SimpleNamespace(
            parent=SimpleNamespace(name=str(os.getpid() + 1)),
            read_bytes=lambda: command if running else b"sleep\x00infinity\x00",
        ),
    ]
    monkeypatch.setattr(Path, "glob", lambda path, pattern: entries)
    output = io.StringIO()

    def docker(*arguments):
        assert arguments[:4] == ("exec", "vllm_node", "python3", "-c")
        with redirect_stdout(output):
            exec(compile(arguments[4], "container_probe", "exec"), {})
        return output.getvalue().strip()

    monkeypatch.setattr(spark_qwen, "docker", docker)
    assert spark_qwen.original_workload_running() is running
    assert output.getvalue() == ("yes\n" if running else "no\n")


def test_prepare_checks_runtime_and_downloads_before_replacing_any_workload(
    tmp_path, monkeypatch, capsys
):
    calls = []

    def docker(*arguments):
        calls.append(arguments)
        if arguments[:2] == ("image", "inspect"):
            return json.dumps([{"Architecture": "arm64", "Id": "sha256:pinned"}])
        compile(arguments[arguments.index("-c") + 1], "container_code", "exec")
        if "--gpus" in arguments:
            return json.dumps({"gpu": "NVIDIA GB10", "vllm": "compatible"})
        return json.dumps({"Qwen/Qwen3-8B": "rev8", "Qwen/Qwen3-4B": "rev4"})

    monkeypatch.setattr(spark_qwen, "docker", docker)
    monkeypatch.setattr(spark_qwen, "inspect", lambda name: {})
    owner = SimpleNamespace(pw_uid=os.getuid(), pw_gid=os.getgid())
    spark_qwen.prepare(tmp_path, owner, "existing-image")
    assert all(arguments[0] != "stop" for arguments in calls)
    prepared = json.loads((tmp_path / "prepared.json").read_text())
    assert prepared["image"] == "sha256:pinned"
    assert prepared["revisions"]["Qwen/Qwen3-8B"] == "rev8"
    output = capsys.readouterr().out
    for role in ("citizen", "institution"):
        key_file = tmp_path / f"{role}.env"
        assert key_file.stat().st_mode & 0o777 == 0o600
        assert spark_qwen.api_key(tmp_path, role) not in output


def test_prepare_rejects_wrong_architecture_without_stopping_server(
    tmp_path, monkeypatch
):
    calls = []

    def docker(*arguments):
        calls.append(arguments)
        return json.dumps([{"Architecture": "amd64", "Id": "wrong"}])

    monkeypatch.setattr(spark_qwen, "docker", docker)
    monkeypatch.setattr(spark_qwen, "inspect", lambda name: {})
    with pytest.raises(RuntimeError, match="ARM64"):
        spark_qwen.prepare(tmp_path, None, "wrong-image")
    assert len(calls) == 1


@pytest.mark.parametrize("role,model,port,memory,sequences", spark_qwen.MODELS)
def test_launch_commands_keep_api_keys_private_and_publish_loopback_only(
    role, model, port, memory, sequences
):
    arguments = spark_qwen.serving_arguments(
        "pinned-image",
        Path("/private/state"),
        role,
        model,
        port,
        memory,
        sequences,
        "pinned-revision",
    )
    assert arguments[arguments.index("-p") + 1] == f"127.0.0.1:{port}:8000"
    assert "--api-key" not in arguments
    assert arguments[arguments.index("--env-file") + 1] == f"/private/state/{role}.env"
    assert arguments[arguments.index("--revision") + 1] == "pinned-revision"
    assert "--privileged" not in arguments


def test_rollback_refuses_changed_original_identity(tmp_path, monkeypatch):
    (tmp_path / "rollback.json").write_text(json.dumps({"container_id": "original"}))
    monkeypatch.setattr(spark_qwen, "inspect", lambda name: {"Id": "different"})
    monkeypatch.setattr(
        spark_qwen, "docker", lambda *args: pytest.fail("No container may be changed")
    )
    with pytest.raises(RuntimeError, match="identity changed"):
        spark_qwen.rollback(tmp_path)


def test_rollback_refuses_unowned_replacement_container(tmp_path, monkeypatch):
    (tmp_path / "rollback.json").write_text(json.dumps({"container_id": "original"}))
    original = {"Id": "original", "State": {"Running": False}}
    monkeypatch.setattr(
        spark_qwen,
        "inspect",
        lambda name: original if name == "vllm_node" else {"Config": {"Labels": {}}},
    )
    monkeypatch.setattr(
        spark_qwen, "docker", lambda *args: pytest.fail("No container may be changed")
    )
    with pytest.raises(RuntimeError, match="ownership"):
        spark_qwen.rollback(tmp_path)


def test_rollback_restores_original_launch_and_removes_stale_connection(
    tmp_path, monkeypatch
):
    (tmp_path / "rollback.json").write_text(json.dumps({"container_id": "original"}))
    (tmp_path / "connection.env").write_text("stale")
    original = {"Id": "original", "State": {"Running": False}}
    owned = {"Config": {"Labels": {"io.faultline.workload": "qwen-roles"}}}
    monkeypatch.setattr(
        spark_qwen, "inspect", lambda name: original if name == "vllm_node" else owned
    )
    calls = []

    def docker(*arguments):
        calls.append(arguments)
        return ""

    monkeypatch.setattr(spark_qwen, "docker", docker)
    monkeypatch.setattr(spark_qwen, "original_workload_running", lambda: False)
    spark_qwen.rollback(tmp_path)
    assert ("start", "vllm_node") in calls
    assert ("exec", "-d", "vllm_node", "bash", "/workspace/exec-script.sh") in calls
    assert not (tmp_path / "connection.env").exists()


def test_launch_failure_restores_original_server(tmp_path, monkeypatch):
    (tmp_path / "prepared.json").write_text(
        json.dumps(
            {
                "image": "pinned",
                "revisions": {"Qwen/Qwen3-8B": "rev8", "Qwen/Qwen3-4B": "rev4"},
            }
        )
    )
    original = {"Id": "original", "State": {"Running": True}}
    monkeypatch.setattr(
        spark_qwen, "inspect", lambda name: original if name == "vllm_node" else None
    )
    monkeypatch.setattr(spark_qwen, "private_write", lambda *args: None)
    connection = SimpleNamespace(
        connect_ex=lambda address: 1, settimeout=lambda seconds: None
    )

    class AvailablePort:
        def __enter__(self):
            return connection

        def __exit__(self, *args):
            return False

    monkeypatch.setattr(spark_qwen.socket, "socket", AvailablePort)
    calls = []

    def docker(*arguments):
        calls.append(arguments)
        return ""

    def unavailable(*args):
        raise RuntimeError("Model startup failed")

    restored = []
    monkeypatch.setattr(spark_qwen, "docker", docker)
    monkeypatch.setattr(spark_qwen, "original_workload_running", lambda: True)
    monkeypatch.setattr(spark_qwen, "wait_ready", unavailable)
    monkeypatch.setattr(spark_qwen, "rollback", lambda state: restored.append(state))
    with pytest.raises(RuntimeError, match="startup failed"):
        spark_qwen.launch(tmp_path, None)
    assert ("stop", "vllm_node") in calls
    assert restored == [tmp_path]
    assert not (tmp_path / "connection.env").exists()


def test_fresh_rollback_only_removes_owned_replacements(tmp_path, monkeypatch):
    (tmp_path / "rollback.json").write_text(json.dumps({"container_id": None}))
    owned = {"Config": {"Labels": {"io.faultline.workload": "qwen-roles"}}}
    monkeypatch.setattr(
        spark_qwen, "inspect", lambda name: None if name == "vllm_node" else owned
    )
    calls = []
    monkeypatch.setattr(spark_qwen, "docker", lambda *args: calls.append(args))
    spark_qwen.rollback(tmp_path)
    assert len(calls) == 2
    assert all(arguments[:2] == ("rm", "-f") for arguments in calls)


@pytest.fixture
def partial_deployment(tmp_path, monkeypatch):
    prepared = {
        "image": "sha256:pinned",
        "revisions": {"Qwen/Qwen3-8B": "rev8", "Qwen/Qwen3-4B": "rev4"},
    }
    (tmp_path / "prepared.json").write_text(json.dumps(prepared))
    (tmp_path / "rollback.json").write_text(json.dumps({"container_id": "original"}))
    for role, *_ in spark_qwen.MODELS:
        (tmp_path / f"{role}.env").write_text(f"VLLM_API_KEY={role}-private-key\n")
    arguments = spark_qwen.serving_arguments(
        "sha256:pinned", tmp_path, *spark_qwen.MODELS[0], "rev8"
    )
    institution = {
        "Image": "sha256:pinned",
        "State": {"Running": True},
        "Config": {
            "Labels": {"io.faultline.workload": "qwen-roles"},
            "Entrypoint": ["vllm"],
            "Cmd": arguments[arguments.index("sha256:pinned") + 1 :],
            "Env": ["VLLM_API_KEY=institution-private-key"],
        },
        "HostConfig": {
            "PortBindings": {"8000/tcp": [{"HostIp": "127.0.0.1", "HostPort": "8000"}]}
        },
        "Mounts": [
            {
                "Source": str(tmp_path / "cache"),
                "Destination": "/root/.cache/huggingface",
            }
        ],
    }
    original = {"Id": "original", "State": {"Running": True}}
    containers = {"vllm_node": original, "faultline-qwen-institution": institution}
    monkeypatch.setattr(spark_qwen, "inspect", lambda name: containers.get(name))

    class AvailablePort:
        def __enter__(self):
            return SimpleNamespace(
                settimeout=lambda value: None, connect_ex=lambda address: 1
            )

        def __exit__(self, *args):
            return False

    monkeypatch.setattr(spark_qwen.socket, "socket", AvailablePort)
    owner = SimpleNamespace(pw_uid=os.getuid(), pw_gid=os.getgid())
    return tmp_path, owner, containers


def test_resume_keeps_matching_running_model_and_starts_missing_citizen(
    partial_deployment, monkeypatch, capsys
):
    state, owner, _ = partial_deployment
    calls = []
    verified = []
    monkeypatch.setattr(spark_qwen, "docker", lambda *args: calls.append(args))
    monkeypatch.setattr(spark_qwen, "wait_ready", lambda *args: None)
    monkeypatch.setattr(
        spark_qwen, "validate_server", lambda state, role, *args: verified.append(role)
    )
    spark_qwen.resume(state, owner)
    assert calls[0] == ("stop", "vllm_node")
    assert len(calls) == 2
    assert calls[1][calls[1].index("--name") + 1] == "faultline-qwen-citizen"
    assert verified == ["institution", "citizen"]
    assert (state / "connection.env").stat().st_mode & 0o777 == 0o600
    assert "private-key" not in capsys.readouterr().out


@pytest.mark.parametrize(
    "mismatch", ["owner", "image", "key", "port", "command", "original"]
)
def test_resume_rejects_mismatched_partial_deployment_before_mutations(
    partial_deployment, monkeypatch, mismatch
):
    state, owner, containers = partial_deployment
    container = containers["faultline-qwen-institution"]
    if mismatch == "owner":
        container["Config"]["Labels"] = {}
    elif mismatch == "image":
        container["Image"] = "sha256:changed"
    elif mismatch == "key":
        container["Config"]["Env"] = ["VLLM_API_KEY=wrong-key"]
    elif mismatch == "port":
        container["HostConfig"]["PortBindings"]["8000/tcp"][0]["HostIp"] = "0.0.0.0"
    elif mismatch == "command":
        container["Config"]["Cmd"][1] = "other/model"
    else:
        containers["vllm_node"]["Id"] = "changed"
    monkeypatch.setattr(
        spark_qwen, "docker", lambda *args: pytest.fail("No container may be changed")
    )
    with pytest.raises(RuntimeError, match="match|identity"):
        spark_qwen.resume(state, owner)


def test_resume_failure_attempts_saved_rollback(partial_deployment, monkeypatch):
    state, owner, _ = partial_deployment
    restored = []
    monkeypatch.setattr(spark_qwen, "docker", lambda *args: "")

    def fail_readiness(*args):
        raise RuntimeError("Model failed to load")

    monkeypatch.setattr(spark_qwen, "wait_ready", fail_readiness)
    monkeypatch.setattr(spark_qwen, "rollback", lambda path: restored.append(path))
    with pytest.raises(RuntimeError, match="failed to load"):
        spark_qwen.resume(state, owner)
    assert restored == [state]
    assert not (state / "connection.env").exists()
