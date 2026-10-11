"""Bundle reviewed source and deploy it to an existing Compute Engine VM."""

import argparse
import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
from pathlib import Path

from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from ops.compute_config import compute_environment


def bundle_source(root, destination, base_archive=None):
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
    additions = ["ops/run_collection.py", "ops/test_run_collection.py", "docs/gearth/22-google-cloud-deployment.md"]
    additions += [str(path.relative_to(root)) for path in (root / "ops/gcp").glob("*") if path.is_file()]
    names = sorted({name for name in tracked + additions if name and (root / name).is_file()
                    and not any(part in {".git", ".data", ".logs", ".venv", "node_modules", "bin", "obj", "__pycache__"}
                                for part in Path(name).parts)
                    and not name.startswith(".env")})
    digest = hashlib.sha256()
    # Read each file once: the recorded hash describes exactly what goes into the archive.
    files = [(name, (root / name).read_bytes()) for name in names]
    base_source = None
    if base_archive:
        # Operational changes can preserve the exact engine already collecting data.
        # Refuse archives whose recorded contents do not match their source hash.
        with tarfile.open(base_archive) as archive:
            base_source = json.load(archive.extractfile(".deployment.json"))
            base_files = {}
            for entry in archive.getmembers():
                if entry.name == ".deployment.json":
                    continue
                path = Path(entry.name)
                if (not entry.isfile() or path.is_absolute() or ".." in path.parts or entry.name.startswith(".env")
                        or any(part in {".git", ".data", ".logs", ".venv", "node_modules", "bin", "obj", "__pycache__"} for part in path.parts)):
                    raise ValueError("Unsafe base source archive")
                if entry.name in base_files:
                    raise ValueError("Repeated base source archive path")
                base_files[entry.name] = archive.extractfile(entry).read()
        original = hashlib.sha256()
        for name, content in sorted(base_files.items()):
            original.update(name.encode() + b"\0" + hashlib.sha256(content).digest())
        if original.hexdigest() != base_source["source_sha256"]:
            raise ValueError("Base source archive hash mismatch")
        for name, content in files:
            if name.startswith("ops/gcp/") or name in {"ops/check_amd.py", "ops/test_run_collection.py", "docs/gearth/22-google-cloud-deployment.md", "backend/GEarth.Api/SimulationPause.cs", "backend/GEarth.Api/wwwroot/controls.js", "backend/GEarth.Api/wwwroot/controls.css", "backend/GEarth.Tests/SimulationPauseTests.cs"}:
                base_files[name] = content
        # Preserve the running economy/model code and insert only reviewed pause/UI hooks.
        from ops.gcp.control_patch import pause_hooks, control_panel
        for name, patch in [("backend/GEarth.Api/Coordinator.cs", pause_hooks), ("backend/GEarth.Api/wwwroot/index.html", control_panel)]:
            if name in base_files:
                base_files[name] = patch(base_files[name].decode()).encode()
        files = sorted(base_files.items())
    for name, content in files:
        digest.update(name.encode() + b"\0" + hashlib.sha256(content).digest())
    source = {
        "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
        "dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root)),
        "source_sha256": digest.hexdigest(),
    }
    if base_source:
        source.update({"operations_commit": source["commit"], "commit": base_source["commit"],
                       "dirty": True, "base_source_sha256": base_source["source_sha256"]})
    with tarfile.open(destination, "w:gz") as archive:
        for name, content in files + [(".deployment.json", json.dumps(source, indent=2).encode())]:
            info = tarfile.TarInfo(name)
            info.size = len(content)
            info.mode = 0o644
            archive.addfile(info, io.BytesIO(content))
    return source


def cloud_config(config):
    result = {key: value for key, value in config.items()
              if value is not None and (key == "DATABASE_URL" or key.startswith(("SPARK_A_", "SPARK_B_")))}
    if not result.get("DATABASE_URL"):
        raise ValueError("Neon DATABASE_URL is required")
    if str(config.get("GEARTH_EXPERIMENTS_ENABLED", "false")).lower() == "true":
        for key in ("NEON_API_KEY", "NEON_PROJECT_ID"):
            if not config.get(key):
                raise ValueError("Timeline experiments require NEON_API_KEY and NEON_PROJECT_ID")
            result[key] = config[key]
        result["GEARTH_EXPERIMENTS_ENABLED"] = "true"
    for prefix in ("SPARK_A", "SPARK_A_CITIZEN", "SPARK_B", "SPARK_B_CITIZEN"):
        for suffix in ("_MODEL", "_API_KEY"):
            if not result.get(prefix + suffix):
                raise ValueError(prefix + suffix + " is required")
        result[prefix + "_ENABLED"] = "true"
    for prefix, port in [("SPARK_A", 18001), ("SPARK_A_CITIZEN", 18003)]:
        result[prefix + "_BASE_URL"] = f"http://127.0.0.1:{port}/v1"
        result.pop(prefix + "_TAILSCALE_HTTP_HOST", None)
    for prefix in ("AMD", "AMD_CITIZEN"):
        enabled = config.get(prefix + "_ENABLED", "false").lower() == "true"
        result[prefix + "_ENABLED"] = str(enabled).lower()
        if enabled:
            for suffix in ("_BASE_URL", "_MODEL", "_API_KEY", "_CAPACITY", "_DECISION_GROUP"):
                if config.get(prefix + suffix) is not None:
                    result[prefix + suffix] = config[prefix + suffix]
    # Spark B stays on its existing pinned Tailscale relay, reached directly
    # from the VM. Spark A's reverse ports currently depend on Jayden's laptop.
    compute_environment(result, "auto")
    return result


def write_config(path, config):
    # python-dotenv single-quoted values: escape literal slash and quote, preserve punctuation.
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as handle:
        for key, value in config.items():
            escaped = value.replace("\\", "\\\\").replace("'", "\\'")
            handle.write(f"{key}='{escaped}'\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", required=True)
    parser.add_argument("--zone", default="us-central1-a")
    parser.add_argument("--instance", default="gearth-sim")
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--config", type=Path, default=ROOT / ".env.local")
    parser.add_argument("--base-archive", type=Path, help="Preserve this engine release; replace cloud operations only")
    parser.add_argument("--after-current", action="store_true", help="Wait for the active experiment before switching releases")
    parser.add_argument("--start", action="store_true", help="Start the verified collector after installation")
    options = parser.parse_args()
    configured = dotenv_values(options.config, interpolate=False)
    if options.base_archive and str(configured.get("GEARTH_EXPERIMENTS_ENABLED", "false")).lower() == "true":
        parser.error("Timeline experiments require a full engine release; omit --base-archive.")
    directory = ROOT / ".data/gcp"
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / "source.tar.gz"
    private = directory / "config.env"
    known_hosts = directory / "spark-known-hosts"
    keys = subprocess.check_output(["ssh-keygen", "-F", "100.70.23.23", "-f", str(Path.home() / ".ssh/known_hosts")])
    known_hosts.write_bytes(keys)
    source = bundle_source(ROOT, archive, options.base_archive)
    write_config(private, cloud_config(configured))
    print("Prepared source", source["source_sha256"], flush=True)
    if options.prepare_only:
        return
    common = ["--project=" + options.project, "--zone=" + options.zone, "--quiet"]
    # Credential values remain in a private local file and encrypted SCP, never CLI arguments.
    subprocess.run(["gcloud", "compute", "scp", str(archive), str(private), str(known_hosts),
                    str(ROOT / "ops/gcp/install_release.sh"), options.instance + ":~/", *common], check=True)
    command = "sudo bash ~/install_release.sh ~/source.tar.gz ~/config.env " + source["source_sha256"]
    if options.after_current:
        # The VM owns the wait, even if the uploading laptop disconnects.
        command = ("sudo systemd-run --unit=gearth-deploy-" + source["source_sha256"][:12]
                   + " --wait --collect /bin/bash ~/install_release.sh ~/source.tar.gz ~/config.env "
                   + source["source_sha256"] + " after-current")
    if options.start:
        if not options.after_current:
            command += " immediate"
        command += " true"
    subprocess.run(["gcloud", "compute", "ssh", options.instance, *common, "--command=" + command], check=True)
    subprocess.run(["gcloud", "compute", "ssh", options.instance, *common,
                    "--command=rm -f ~/config.env ~/source.tar.gz ~/install_release.sh ~/spark-known-hosts"], check=True)
    private.unlink()
    print("Installed source", source["source_sha256"], flush=True)


if __name__ == "__main__":
    main()
