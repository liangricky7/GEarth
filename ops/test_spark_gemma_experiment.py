import json
from types import SimpleNamespace

import pytest

from ops import spark_gemma_experiment as experiment


@pytest.mark.parametrize("count,menu", [(0, []), (2, ["0: hold"]),
                                         (True, ["0: hold"]), (1, ["1: hold"])])
def test_archived_or_inconsistent_action_menus_are_rejected(count, menu):
    samples = [{"candidate_count": count, "payload": {"messages": [
        {"role": "user", "content": json.dumps({"available_actions": menu})}]}}]
    with pytest.raises(RuntimeError, match="invalid action menu"):
        experiment.validate_samples(samples)


def test_numbered_menu_matches_candidate_count():
    experiment.validate_samples([{"candidate_count": 2, "payload": {"messages": [
        {"role": "system", "content": "Choose one action."},
        {"role": "user", "content": json.dumps({"available_actions": ["0: hold", "1: work effort=1"]})}]}}])


def container(identity="original", role="citizen", running=False, label="qwen-roles"):
    return {"Id": identity, "Name": f"/faultline-qwen-{role}",
            "State": {"Running": running},
            "Config": {"Labels": {"io.faultline.workload": label},
                       "Cmd": ["serve", "--served-model-name", "Qwen/Qwen3-4B"]}}


def rollback_record(path, identity="original", running=True, created=None):
    record = {"restored": False, "created": created or [], "originals": [
        {"id": identity, "name": "faultline-qwen-citizen", "role": "citizen",
         "running": running, "port": 8001, "model": "Qwen/Qwen3-4B"}]}
    (path / "rollback.json").write_text(json.dumps(record))


@pytest.mark.parametrize("action", [
    {"choice": True, "reason": "Work."},
    {"choice": 2, "reason": "Work."},
    {"choice": 0, "reason": " "},
    {"choice": 0, "reason": "Work.", "extra": 1},
])
def test_invalid_output_does_not_count_as_throughput(action):
    with pytest.raises(ValueError):
        experiment.validate_choice({"choices": [{"message": {"content": json.dumps(action)}}]}, 2)


def test_restore_rejects_recreated_original(tmp_path, monkeypatch):
    rollback_record(tmp_path)
    calls = []
    monkeypatch.setattr(experiment, "inspect", lambda _: container("recreated"))
    monkeypatch.setattr(experiment, "docker", lambda *args: calls.append(args))
    with pytest.raises(RuntimeError, match="identity changed"):
        experiment.restore(tmp_path, None)
    assert not calls
    assert not json.loads((tmp_path / "rollback.json").read_text())["restored"]


def test_restore_does_not_remove_an_unowned_experiment_container(tmp_path, monkeypatch):
    rollback_record(tmp_path, created=["replacement"])
    calls = []
    monkeypatch.setattr(experiment, "inspect", lambda _: container("replacement"))
    monkeypatch.setattr(experiment, "docker", lambda *args: calls.append(args))
    with pytest.raises(RuntimeError, match="ownership mismatch"):
        experiment.restore(tmp_path, None)
    assert not calls


def test_restore_releases_experiment_memory_before_starting_original(tmp_path, monkeypatch):
    rollback_record(tmp_path, created=["trial"])
    credential_dir = tmp_path / "faultline-qwen"
    credential_dir.mkdir()
    (credential_dir / "citizen.env").write_text("VLLM_API_KEY=test-private")
    calls = []
    trial = {"Config": {"Labels": {experiment.LABEL_KEY: experiment.LABEL_VALUE}}}
    monkeypatch.setattr(experiment, "inspect", lambda identity: trial if identity == "trial" else container())
    monkeypatch.setattr(experiment, "docker", lambda *args: calls.append(args))
    monkeypatch.setattr(experiment, "ready", lambda *args: calls.append(("ready", args[0])))
    monkeypatch.setattr(experiment, "private_write", lambda path, value, owner: path.write_text(value))
    experiment.restore(tmp_path, SimpleNamespace(pw_dir=str(tmp_path)))
    assert calls == [("rm", "-f", "trial"), ("start", "original"), ("ready", "original")]
    assert json.loads((tmp_path / "rollback.json").read_text())["restored"]


def test_restore_preserves_an_original_that_was_already_stopped(tmp_path, monkeypatch):
    rollback_record(tmp_path, running=False)
    calls = []
    monkeypatch.setattr(experiment, "inspect", lambda _: container())
    monkeypatch.setattr(experiment, "docker", lambda *args: calls.append(args))
    monkeypatch.setattr(experiment, "private_write", lambda path, value, owner: path.write_text(value))
    experiment.restore(tmp_path, None)
    assert not calls
