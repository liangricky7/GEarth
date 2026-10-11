import json
import subprocess
import sys
import tarfile
import threading
from pathlib import Path

from dotenv import dotenv_values

from ops.run_collection import reserve_seed, run_one
from ops.gcp.deploy import bundle_source, cloud_config, write_config
from ops.compute_config import compute_environment, spark_a_forwards


def test_reserved_seed_is_not_reused_after_collector_restart(tmp_path):
    state = tmp_path / "state.json"
    assert reserve_seed(state, 100) == 100
    # A different CLI default cannot reset previously allocated experiments.
    assert reserve_seed(state, 900) == 101
    assert json.loads(state.read_text()) == {"next_seed": 102}


def test_child_failure_remains_a_failure():
    result = run_one([sys.executable, "-c", "raise SystemExit(7)"], threading.Event(),
                     read_health=lambda: {"run_id": "fake", "run_status": "running"}, poll_seconds=0.01)
    assert result["exit_code"] == 7
    assert not result["interrupted"]
    assert result["health"]["run_id"] == "fake"


def test_shutdown_waits_for_child_to_save_its_state(tmp_path):
    ready, saved = tmp_path / "ready", tmp_path / "saved"
    script = """import signal,time,sys
from pathlib import Path
def finish(*args):
    Path(sys.argv[2]).write_text('saved')
    sys.exit(0)
signal.signal(signal.SIGTERM,finish)
Path(sys.argv[1]).touch()
while True: time.sleep(.01)
"""
    stop = threading.Event()
    def check():
        if ready.exists():
            stop.set()
        return None
    result = run_one([sys.executable, "-c", script, str(ready), str(saved)], stop,
                     read_health=check, poll_seconds=0.01)
    assert result["interrupted"]
    assert result["exit_code"] == 0
    assert saved.read_text() == "saved"


def test_cloud_config_reaches_spark_b_without_the_laptop_reverse_tunnel(tmp_path):
    config = {"DATABASE_URL": "postgresql://private", "FAULTLINE_SHARE_KEY": "do-not-copy", "AMD_API_KEY": "do-not-copy"}
    for prefix in ["SPARK_A", "SPARK_A_CITIZEN", "SPARK_B", "SPARK_B_CITIZEN"]:
        config.update({prefix + "_API_KEY": "punctuation\\'${secret}#", prefix + "_MODEL": "model", prefix + "_CAPACITY": "4"})
    config.update({"SPARK_B_BASE_URL": "http://100.119.198.81:18002/v1", "SPARK_B_TAILSCALE_HTTP_HOST": "100.119.198.81",
                   "SPARK_B_CITIZEN_BASE_URL": "http://100.119.198.81:18004/v1", "SPARK_B_CITIZEN_TAILSCALE_HTTP_HOST": "100.119.198.81"})
    exported = cloud_config(config)
    assert "FAULTLINE_SHARE_KEY" not in exported
    assert "AMD_API_KEY" not in exported
    path = tmp_path / "config.env"
    write_config(path, exported)
    assert dotenv_values(path, interpolate=False) == exported
    assert path.stat().st_mode & 0o777 == 0o600
    _, _, providers, capacity = compute_environment(exported, "spark")
    assert capacity == 16
    assert spark_a_forwards(providers) == [(18001, 8000), (18003, 8001)]
    assert exported["SPARK_B_BASE_URL"] == config["SPARK_B_BASE_URL"]
    assert exported["SPARK_B_CITIZEN_BASE_URL"] == config["SPARK_B_CITIZEN_BASE_URL"]


def test_source_bundle_excludes_credentials_runtime_and_unrelated_untracked_files(tmp_path):
    subprocess.run(["git", "init", "-q", str(tmp_path)], check=True)
    (tmp_path / "app.txt").write_text("version1")
    (tmp_path / ".env.local").write_text("SECRET=private")
    (tmp_path / "unrelated.txt").write_text("unrelated work")
    subprocess.run(["git", "-C", str(tmp_path), "add", "app.txt"], check=True)
    subprocess.run(["git", "-C", str(tmp_path), "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-qm", "initial"], check=True)
    (tmp_path / "app.txt").write_text("version2")
    destination = tmp_path / "bundle.tar.gz"
    source = bundle_source(tmp_path, destination)
    with tarfile.open(destination) as archive:
        assert archive.getnames() == ["app.txt", ".deployment.json"]
        assert archive.extractfile("app.txt").read() == b"version2"
        assert json.load(archive.extractfile(".deployment.json")) == source
    assert source["dirty"]


def test_cloud_config_exports_enabled_amd_for_hybrid_citizen_decisions():
    config = {"DATABASE_URL": "postgresql://private", "FAULTLINE_SHARE_KEY": "not-cloud-config"}
    for prefix in ("SPARK_A", "SPARK_A_CITIZEN", "SPARK_B", "SPARK_B_CITIZEN"):
        config.update({prefix + "_API_KEY": "private", prefix + "_MODEL": "model", prefix + "_CAPACITY": "4"})
    config.update({"SPARK_B_BASE_URL": "http://100.119.198.81:18002/v1", "SPARK_B_TAILSCALE_HTTP_HOST": "100.119.198.81",
                   "SPARK_B_CITIZEN_BASE_URL": "http://100.119.198.81:18004/v1", "SPARK_B_CITIZEN_TAILSCALE_HTTP_HOST": "100.119.198.81",
                   "AMD_ENABLED": "true", "AMD_BASE_URL": "https://example.test/v1", "AMD_API_KEY": "private-amd",
                   "AMD_MODEL": "Qwen/Qwen3-4B", "AMD_CAPACITY": "1", "AMD_DECISION_GROUP": "citizen"})
    exported = cloud_config(config)
    mode, _, providers, capacity = compute_environment(exported, "auto")
    assert mode == "hybrid" and capacity == 17
    assert {provider.name for provider in providers} == {"spark_a", "spark_b", "spark_a_citizen", "spark_b_citizen", "amd"}
    assert all(provider.capacity == 4 for provider in providers if provider.name.startswith("spark"))
    amd = next(provider for provider in providers if provider.name == "amd")
    assert amd.serves("citizen") and not amd.serves("institution") and amd.capacity == 1
    assert "FAULTLINE_SHARE_KEY" not in exported


def test_operational_overlay_preserves_base_engine_and_records_provenance(tmp_path):
    subprocess.run(["git", "init", "-q", str(tmp_path)], check=True)
    (tmp_path / "engine.txt").write_text("collecting-engine")
    (tmp_path / "ops").mkdir()
    (tmp_path / "ops/check_amd.py").write_text("old-check")
    subprocess.run(["git", "-C", str(tmp_path), "add", "."], check=True)
    subprocess.run(["git", "-C", str(tmp_path), "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-qm", "initial"], check=True)
    base = tmp_path / "base.tar.gz"
    original = bundle_source(tmp_path, base)
    (tmp_path / "engine.txt").write_text("future-engine")
    (tmp_path / "ops/check_amd.py").write_text("new-check")
    destination = tmp_path / "overlay.tar.gz"
    source = bundle_source(tmp_path, destination, base)
    with tarfile.open(destination) as archive:
        assert archive.extractfile("engine.txt").read() == b"collecting-engine"
        assert archive.extractfile("ops/check_amd.py").read() == b"new-check"
    assert source["base_source_sha256"] == original["source_sha256"]
    assert source["source_sha256"] != original["source_sha256"] and source["dirty"]
    assert source["operations_commit"] == source["commit"] == original["commit"]
