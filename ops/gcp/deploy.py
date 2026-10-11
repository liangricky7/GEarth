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


def bundle_source(root, destination):
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
    additions = ["ops/run_collection.py", "ops/test_run_collection.py", "docs/faultline/22-google-cloud-deployment.md"]
    additions += [str(path.relative_to(root)) for path in (root / "ops/gcp").glob("*") if path.is_file()]
    names = sorted({name for name in tracked + additions if name and (root / name).is_file()
                    and not any(part in {".git", ".data", ".logs", ".venv", "node_modules", "bin", "obj", "__pycache__"}
                                for part in Path(name).parts)
                    and not name.startswith(".env")})
    digest = hashlib.sha256()
    # Read each file once: the recorded hash describes exactly what goes into the archive.
    files = [(name, (root / name).read_bytes()) for name in names]
    for name, content in files:
        digest.update(name.encode() + b"\0" + hashlib.sha256(content).digest())
    source = {
        "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
        "dirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root)),
        "source_sha256": digest.hexdigest(),
    }
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
    for prefix in ("SPARK_A", "SPARK_A_CITIZEN", "SPARK_B", "SPARK_B_CITIZEN"):
        for suffix in ("_MODEL", "_API_KEY"):
            if not result.get(prefix + suffix):
                raise ValueError(prefix + suffix + " is required")
        result[prefix + "_ENABLED"] = "true"
    for prefix, port in [("SPARK_A", 18001), ("SPARK_A_CITIZEN", 18003)]:
        result[prefix + "_BASE_URL"] = f"http://127.0.0.1:{port}/v1"
        result.pop(prefix + "_TAILSCALE_HTTP_HOST", None)
    # Spark B stays on its existing pinned Tailscale relay, reached directly
    # from the VM. Spark A's reverse ports currently depend on Jayden's laptop.
    compute_environment(result, "spark")
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
    parser.add_argument("--instance", default="faultline-sim")
    parser.add_argument("--prepare-only", action="store_true")
    options = parser.parse_args()
    directory = ROOT / ".data/gcp"
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / "source.tar.gz"
    private = directory / "config.env"
    known_hosts = directory / "spark-known-hosts"
    keys = subprocess.check_output(["ssh-keygen", "-F", "100.70.23.23", "-f", str(Path.home() / ".ssh/known_hosts")])
    known_hosts.write_bytes(keys)
    source = bundle_source(ROOT, archive)
    write_config(private, cloud_config(dotenv_values(ROOT / ".env.local", interpolate=False)))
    print("Prepared source", source["source_sha256"], flush=True)
    if options.prepare_only:
        return
    common = ["--project=" + options.project, "--zone=" + options.zone, "--quiet"]
    # Credential values remain in a private local file and encrypted SCP, never CLI arguments.
    subprocess.run(["gcloud", "compute", "scp", str(archive), str(private), str(known_hosts),
                    str(ROOT / "ops/gcp/install_release.sh"), options.instance + ":~/", *common], check=True)
    command = "sudo bash ~/install_release.sh ~/source.tar.gz ~/config.env " + source["source_sha256"]
    subprocess.run(["gcloud", "compute", "ssh", options.instance, *common, "--command=" + command], check=True)
    subprocess.run(["gcloud", "compute", "ssh", options.instance, *common,
                    "--command=rm -f ~/config.env ~/source.tar.gz ~/install_release.sh ~/spark-known-hosts"], check=True)
    private.unlink()
    print("Installed source", source["source_sha256"], flush=True)


if __name__ == "__main__":
    main()
