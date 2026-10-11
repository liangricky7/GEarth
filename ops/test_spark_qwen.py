import io
import json
import os
import signal
import sys
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
    assert arguments[arguments.index("--tokenizer-revision") + 1] == "pinned-revision"
    assert arguments[arguments.index("--served-model-name") + 1] == model
    snapshot = str(spark_qwen.snapshot_path(model, "pinned-revision"))
    assert arguments[arguments.index("serve") + 1] == snapshot
    assert arguments[arguments.index("--tokenizer") + 1] == snapshot
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
    monkeypatch.setattr(spark_qwen, "validate_cached_tokenizers", lambda *args: None)
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
    monkeypatch.setattr(spark_qwen, "validate_cached_tokenizers", lambda *args: None)
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


def test_tokenizer_preflight_uses_offline_cache_without_gpu(tmp_path, monkeypatch):
    prepared = {
        "image": "pinned-image",
        "revisions": {"Qwen/Qwen3-8B": "rev8", "Qwen/Qwen3-4B": "rev4"},
    }
    calls = []
    monkeypatch.setattr(spark_qwen, "docker", lambda *args: calls.append(args))
    spark_qwen.validate_cached_tokenizers(tmp_path, prepared)
    arguments = calls[0]
    assert "--gpus" not in arguments
    assert "--env-file" not in arguments
    assert arguments[arguments.index("--network") + 1] == "none"
    assert arguments[arguments.index("-v") + 1].endswith("huggingface:ro")
    probe = arguments[arguments.index("-c") + 1]
    compile(probe, "preflight", "exec")
    checked = []

    def from_pretrained(snapshot, local_files_only):
        assert local_files_only
        checked.append(snapshot)
        return SimpleNamespace(encode=lambda value: [1])

    monkeypatch.setitem(
        sys.modules,
        "transformers",
        SimpleNamespace(
            AutoConfig=SimpleNamespace(from_pretrained=from_pretrained),
            AutoTokenizer=SimpleNamespace(from_pretrained=from_pretrained),
        ),
    )
    monkeypatch.setattr(sys, "argv", ["probe", arguments[-1]])
    exec(compile(probe, "preflight", "exec"), {})
    assert checked == [
        str(spark_qwen.snapshot_path("Qwen/Qwen3-8B", "rev8")),
        str(spark_qwen.snapshot_path("Qwen/Qwen3-8B", "rev8")),
        str(spark_qwen.snapshot_path("Qwen/Qwen3-4B", "rev4")),
        str(spark_qwen.snapshot_path("Qwen/Qwen3-4B", "rev4")),
    ]


@pytest.mark.parametrize("action", ["launch", "resume"])
def test_tokenizer_preflight_failure_preserves_running_workloads(
    partial_deployment, monkeypatch, action
):
    state, owner, _ = partial_deployment

    def unavailable(*args):
        raise RuntimeError("Cached tokenizer failed")

    monkeypatch.setattr(spark_qwen, "validate_cached_tokenizers", unavailable)
    monkeypatch.setattr(
        spark_qwen, "docker", lambda *args: pytest.fail("No mutation allowed")
    )
    with pytest.raises(RuntimeError, match="Cached tokenizer failed"):
        getattr(spark_qwen, action)(state, owner)


@pytest.fixture
def recreated_original(tmp_path, monkeypatch):
    (tmp_path / "rollback.json").write_text(json.dumps({"container_id": "old-id"}))
    (tmp_path / "prepared.json").write_text(json.dumps({"image": "pinned"}))
    original = {
        "Id": "current-id",
        "State": {"Running": True},
        "Config": {"Image": "vllm-node-mxfp4"},
    }
    containers = {
        "vllm_node": original,
        "faultline-qwen-institution": {
            "Config": {"Labels": {"io.faultline.workload": "qwen-roles"}}
        },
    }
    monkeypatch.setattr(spark_qwen, "inspect", lambda name: containers.get(name))
    monkeypatch.setattr(spark_qwen, "original_workload_running", lambda: True)
    monkeypatch.setattr(spark_qwen, "validate_cached_tokenizers", lambda *args: None)
    calls = []
    monkeypatch.setattr(spark_qwen, "docker", lambda *args: calls.append(args))
    owner = SimpleNamespace(pw_uid=os.getuid(), pw_gid=os.getgid())
    return tmp_path, owner, containers, calls


def test_recovery_backs_up_stale_identity_and_relaunches_verified_original(
    recreated_original, monkeypatch
):
    state, owner, _, calls = recreated_original
    actions = []

    def rollback(path):
        assert (
            json.loads((path / "rollback.json").read_text())["container_id"]
            == "current-id"
        )
        actions.append("rollback")

    def launch(path, owner, **options):
        assert options["expected_original_id"] == "current-id"
        assert options["provider"] == "SPARK_A"
        actions.append("launch")

    monkeypatch.setattr(spark_qwen, "rollback", rollback)
    monkeypatch.setattr(spark_qwen, "launch", launch)
    spark_qwen.recover(state, owner, "current-id")
    assert actions == ["rollback", "launch"]
    assert calls == [("exec", "vllm_node", "test", "-f", spark_qwen.RESTORE_SCRIPT)]
    backups = list(state.glob("rollback.before-recovery-*.json"))
    assert len(backups) == 1
    assert json.loads(backups[0].read_text()) == {"container_id": "old-id"}
    assert backups[0].stat().st_mode & 0o777 == 0o600


@pytest.mark.parametrize(
    "failure", ["id", "image", "workload", "owner", "cache", "changed"]
)
def test_recovery_rejects_unverified_original_before_state_changes(
    recreated_original, monkeypatch, failure
):
    state, owner, containers, _ = recreated_original
    if failure == "id":
        containers["vllm_node"]["Id"] = "different"
    elif failure == "image":
        containers["vllm_node"]["Config"]["Image"] = "other-image"
    elif failure == "workload":
        monkeypatch.setattr(spark_qwen, "original_workload_running", lambda: False)
    elif failure == "owner":
        containers["faultline-qwen-institution"]["Config"]["Labels"] = {}
    else:

        def preflight(*args):
            if failure == "cache":
                raise RuntimeError("Cached tokenizer failed")
            containers["vllm_node"]["Id"] = "changed-during-probe"

        monkeypatch.setattr(spark_qwen, "validate_cached_tokenizers", preflight)
    monkeypatch.setattr(
        spark_qwen,
        "private_write",
        lambda *args: pytest.fail("No saved state may change"),
    )
    monkeypatch.setattr(
        spark_qwen, "rollback", lambda *args: pytest.fail("No rollback allowed")
    )
    monkeypatch.setattr(
        spark_qwen, "launch", lambda *args: pytest.fail("No launch allowed")
    )
    with pytest.raises(RuntimeError):
        spark_qwen.recover(state, owner, "current-id")
    assert json.loads((state / "rollback.json").read_text()) == {
        "container_id": "old-id"
    }


def test_recovery_launch_rejects_container_changed_after_cleanup(
    recreated_original, monkeypatch
):
    state, owner, containers, calls = recreated_original
    containers["vllm_node"]["Id"] = "changed"
    with pytest.raises(RuntimeError, match="changed before launch"):
        spark_qwen.launch(state, owner, expected_original_id="current-id")
    assert calls == []


@pytest.fixture
def reinstall_state(recreated_original, monkeypatch):
    state, owner, containers, calls = recreated_original
    containers["faultline-qwen-institution"]["Id"] = "failed-qwen-id"
    for role, *_ in spark_qwen.MODELS:
        (state / f"{role}.env").write_text(f"VLLM_API_KEY={role}-private-key\n")
    events = []
    monkeypatch.setattr(
        spark_qwen, "pause_original_recipes", lambda *args: events.append("pause")
    )
    monkeypatch.setattr(
        spark_qwen, "resume_original_recipes", lambda *args: events.append("resume")
    )
    monkeypatch.setattr(
        spark_qwen,
        "original_workload_running",
        lambda: pytest.fail("Reinstall does not require old model startup"),
    )
    return state, owner, containers, calls, events


@pytest.mark.parametrize("original_present", [True, False])
def test_reinstall_uses_cached_weights_and_current_original_without_stale_id(
    reinstall_state, monkeypatch, original_present
):
    state, owner, containers, calls, events = reinstall_state
    if not original_present:
        del containers["vllm_node"]
    verified = []
    monkeypatch.setattr(spark_qwen, "wait_ready", lambda *args: None)
    monkeypatch.setattr(
        spark_qwen, "validate_server", lambda state, role, *args: verified.append(role)
    )
    prepared = {
        "image": "pinned",
        "revisions": {"Qwen/Qwen3-8B": "rev8", "Qwen/Qwen3-4B": "rev4"},
    }
    (state / "prepared.json").write_text(json.dumps(prepared))
    spark_qwen.reinstall(state, owner)
    assert events == ["pause"]  # Keep competing recipe paused while Qwen serves.
    assert verified == ["institution", "citizen"]
    assert json.loads((state / "rollback.json").read_text()) == {
        "container_id": "current-id" if original_present else None
    }
    backup = next(state.glob("rollback.before-reinstall-*.json"))
    assert json.loads(backup.read_text()) == {"container_id": "old-id"}
    assert ("rm", "-f", "failed-qwen-id") in calls
    if original_present:
        assert ("stop", "current-id") in calls
        assert calls[0][:2] == ("cp", "current-id:/workspace/exec-script.sh")
    else:
        assert all(call[0] != "stop" for call in calls)
    assert all(call[0] not in ("pull", "build") for call in calls)
    assert (state / "connection.env").stat().st_mode & 0o777 == 0o600


@pytest.mark.parametrize("failure", ["foreign", "image", "changed"])
def test_reinstall_preserves_unknown_containers_and_resumes_launcher_on_error(
    reinstall_state, monkeypatch, failure
):
    state, owner, containers, calls, events = reinstall_state
    if failure == "foreign":
        containers["faultline-qwen-citizen"] = {"Config": {"Labels": {}}}
    elif failure == "image":
        containers["vllm_node"]["Config"]["Image"] = "other-workload"
    else:
        original_checks = 0

        def inspect(name):
            nonlocal original_checks
            if name == "vllm_node":
                original_checks += 1
                if original_checks == 2:
                    return {"Id": "recreated-during-check"}
            return containers.get(name)

        monkeypatch.setattr(spark_qwen, "inspect", inspect)
    with pytest.raises(RuntimeError):
        spark_qwen.reinstall(state, owner)
    assert all(call[0] not in ("rm", "stop", "start", "run") for call in calls)
    assert events == ([] if failure == "foreign" else ["pause", "resume"])
    assert json.loads((state / "rollback.json").read_text()) == {
        "container_id": "old-id"
    }


def test_reinstall_stop_failure_attempts_restoration(reinstall_state, monkeypatch):
    state, owner, _, _, events = reinstall_state

    def docker(*args):
        if args[0] == "stop":
            raise RuntimeError("Docker stop failed")
        return ""

    monkeypatch.setattr(spark_qwen, "docker", docker)
    monkeypatch.setattr(spark_qwen, "rollback", lambda *args: events.append("rollback"))
    with pytest.raises(RuntimeError, match="Docker stop failed"):
        spark_qwen.reinstall(state, owner)
    assert events == ["pause", "rollback", "resume"]


def test_pause_records_only_newly_stopped_processes_without_arguments(
    tmp_path, monkeypatch
):
    entries = {
        11: {"pid": 11, "start": "100", "state": "S", "arguments": [b"secret"]},
        12: {"pid": 12, "start": "101", "state": "T", "arguments": []},
    }
    monkeypatch.setattr(
        spark_qwen, "original_recipe_processes", lambda: list(entries.values())
    )
    monkeypatch.setattr(spark_qwen, "process_record", lambda pid: entries.get(pid))
    monkeypatch.setattr(spark_qwen.time, "sleep", lambda value: None)
    signals = []

    def kill(pid, value):
        signals.append((pid, value))
        entries[pid]["state"] = "T" if value == signal.SIGSTOP else "S"

    monkeypatch.setattr(spark_qwen.os, "kill", kill)
    owner = SimpleNamespace(pw_uid=os.getuid(), pw_gid=os.getgid())
    spark_qwen.pause_original_recipes(tmp_path, owner)
    record = tmp_path / "paused-gpt-oss-launchers.json"
    assert json.loads(record.read_text()) == [{"pid": 11, "start": "100"}]
    assert "secret" not in record.read_text()
    assert record.stat().st_mode & 0o777 == 0o600
    spark_qwen.resume_original_recipes(tmp_path)
    assert signals == [(11, signal.SIGSTOP), (11, signal.SIGCONT)]
    assert not record.exists()


def test_resume_does_not_signal_reused_pid(tmp_path, monkeypatch):
    (tmp_path / "paused-gpt-oss-launchers.json").write_text(
        json.dumps([{"pid": 11, "start": "100"}])
    )
    monkeypatch.setattr(spark_qwen, "process_record", lambda pid: {"start": "200"})
    monkeypatch.setattr(
        spark_qwen.os, "kill", lambda *args: pytest.fail("Do not signal a reused PID")
    )
    spark_qwen.resume_original_recipes(tmp_path)


def test_recipe_process_selection_excludes_other_models_and_includes_descendants(
    monkeypatch,
):
    recipe = b"/home/admin/vllm/spark-vllm-docker/run-recipe.py"
    entries = {
        11: {
            "pid": 11,
            "parent": 1,
            "arguments": [b"python3", recipe, b"openai-gpt-oss-120b", b"--solo"],
        },
        12: {
            "pid": 12,
            "parent": 1,
            "arguments": [b"python3", recipe, b"other-model", b"--solo"],
        },
        13: {"pid": 13, "parent": 11, "arguments": [b"bash", b"launch-cluster.sh"]},
        14: {"pid": 14, "parent": 13, "arguments": [b"docker", b"exec"]},
    }
    monkeypatch.setattr(
        Path, "iterdir", lambda path: [Path(str(pid)) for pid in entries]
    )
    monkeypatch.setattr(spark_qwen, "process_record", lambda pid: entries[int(pid)])
    assert [entry["pid"] for entry in spark_qwen.original_recipe_processes()] == [
        11,
        13,
        14,
    ]
