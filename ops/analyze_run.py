"""Export one finished run, analyze it in Databricks, then publish its cached report."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from analytics.export import export_run
from analytics.validate import validate
from analytics.browse import read_bundle_manifest, safe_path, validate_bundle


class DatabricksCli:
    def __init__(self, executable, profile):
        self.executable = executable
        self.profile = profile

    def call(self, *arguments):
        result = subprocess.run(
            [self.executable, *map(str, arguments), "--profile", self.profile, "--output", "json"],
            capture_output=True, text=True, timeout=180,
        )
        if result.returncode:
            # Never echo remote responses or authentication configuration.
            raise RuntimeError(f"Databricks {arguments[0]} command failed; inspect the workspace job or CLI authentication.")
        return json.loads(result.stdout) if result.stdout.strip() else None


def run_analysis(run_id, destination, *, api, cli, storage, dotnet="dotnet", timeout=1200, analytics_browse=False):
    destination = Path(destination).resolve()
    export_run(api, run_id, destination)
    integrity = validate(destination / "manifest.json")
    manifest_hash = integrity["source_manifest_sha256"]
    bundle = destination / "gearth-analytics.zip"
    with zipfile.ZipFile(bundle, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted((ROOT / "analytics").glob("*.py")):
            archive.write(path, "analytics/" + path.name)
    bundle_hash = hashlib.sha256(bundle.read_bytes()).hexdigest()
    user = cli.call("current-user", "me")["userName"]
    if not isinstance(user, str) or "/" in user or "\\" in user:
        raise RuntimeError("Databricks returned an invalid user workspace path.")
    deployment = hashlib.sha256(bundle.read_bytes())
    notebooks = sorted((ROOT / "analytics/notebooks").glob("*.py"))
    for notebook in notebooks:
        deployment.update(notebook.name.encode())
        deployment.update(b"\0")
        deployment.update(notebook.read_bytes())
    workspace = f"/Workspace/Users/{user}/GEarth/Analytics/{deployment.hexdigest()[:16]}"
    volume = "/Volumes/workspace/gearth_bronze/run_exports"
    remote_export = f"{volume}/{run_id}-{manifest_hash[:12]}"
    remote_bundle = f"{volume}/_code/{bundle_hash}.zip"
    cli.call("workspace", "mkdirs", workspace)
    cli.call("fs", "mkdir", "dbfs:" + volume + "/_code")
    # Code paths are content-addressed; only identical artifacts are overwritten.
    cli.call("fs", "cp", bundle, "dbfs:" + remote_bundle, "--overwrite")
    for notebook in notebooks:
        cli.call("workspace", "import", workspace + "/" + notebook.stem,
                 "--file", notebook, "--format", "SOURCE", "--language", "PYTHON", "--overwrite")
    cli.call("fs", "mkdir", "dbfs:" + remote_export)
    manifest = json.loads((destination / "manifest.json").read_text())
    for entry in manifest["files"]:
        cli.call("fs", "cp", destination / entry["path"], "dbfs:" + remote_export + "/" + entry["path"], "--overwrite")
    # Publish the finalized manifest last, after all required chunks exist.
    cli.call("fs", "cp", destination / "manifest.json", "dbfs:" + remote_export + "/manifest.json", "--overwrite")
    job = {
        "run_name": "GEarth finished-run analysis",
        "timeout_seconds": timeout,
        "tasks": [{"task_key": "import_finished_run", "max_retries": 0, "notebook_task": {
            "notebook_path": workspace + "/01_import_finished_run",
            "base_parameters": {"export_directory": remote_export, "library_zip": remote_bundle,
                                "publish_browse": "true" if analytics_browse else "false"},
        }}],
    }
    job_path = destination / "databricks-job.json"
    job_path.write_text(json.dumps(job))
    submitted = cli.call("jobs", "submit", "--json", "@" + str(job_path), "--no-wait")
    job_id = submitted["run_id"]
    (destination / "databricks-run.json").write_text(json.dumps({"run_id": job_id, "simulation_run_id": run_id}))
    print(f"Databricks analysis submitted: {job_id}", flush=True)
    deadline = time.monotonic() + timeout + 60
    while time.monotonic() < deadline:
        state = cli.call("jobs", "get-run", job_id)
        lifecycle = state.get("state", {}).get("life_cycle_state")
        if lifecycle in {"TERMINATED", "SKIPPED", "INTERNAL_ERROR"}:
            if state.get("state", {}).get("result_state") != "SUCCESS":
                raise RuntimeError(f"Databricks run {job_id} did not succeed; report was not published.")
            break
        time.sleep(5)
    else:
        raise RuntimeError(f"Databricks run {job_id} is still pending; inspect it before retrying. No report published.")
    report_path = destination / "report.json"
    cli.call("fs", "cp", "dbfs:" + remote_export + "/report-" + manifest_hash[:12] + ".json", report_path)
    publish_arguments = []
    expected_catalog = None
    if analytics_browse:
        browse_directory = destination / "analytics-browse"
        browse_directory.mkdir()
        remote_browse = "dbfs:" + remote_export + "/browse-" + manifest_hash[:12]
        # Validate the small manifest before any remote path is used for downloads.
        cli.call("fs", "cp", remote_browse + "/browse-manifest.json", browse_directory / "browse-manifest.json")
        browse_manifest, _ = read_bundle_manifest(browse_directory)
        expected = {"run_id": run_id, "through_revision": manifest["run"]["through_revision"],
            "through_sequence": manifest["run"]["through_sequence"], "source_manifest_sha256": manifest_hash}
        if any(browse_manifest[key] != value for key, value in expected.items()):
            raise ValueError("Downloaded browse manifest has conflicting source provenance")
        for dataset in browse_manifest["datasets"]:
            for chunk in dataset.get("chunks", []):
                path = safe_path(browse_directory, chunk["path"])
                path.parent.mkdir(parents=True, exist_ok=True)
                cli.call("fs", "cp", remote_browse + "/" + chunk["path"], path)
        downloaded_report = json.loads(report_path.read_text())
        expected_catalog = downloaded_report.get("dataset_catalog")
        if expected_catalog is None:
            raise ValueError("Downloaded report has no actual Databricks dataset catalog")
        validate_bundle(browse_directory, expected=expected, expected_catalog=expected_catalog)
        publish_arguments = ["--analytics-browse", str(browse_directory)]
    environment = os.environ.copy()
    environment["GEARTH_STORAGE"] = storage
    publication = subprocess.run(
        [dotnet, "run", "--project", str(ROOT / "backend/GEarth.Api"), "--no-build", "--",
         "--publish-analytics-report", str(report_path), "--source-manifest", str(destination / "manifest.json"), *publish_arguments],
        cwd=ROOT, env=environment, capture_output=True, text=True, timeout=180,
    )
    if publication.returncode:
        raise RuntimeError("Report download succeeded but publication failed; check storage configuration and source manifest. No website report verified.")
    from urllib.parse import quote
    from urllib.request import urlopen
    with urlopen(api.rstrip("/") + "/api/runs/" + quote(run_id, safe="") + "/analytics/report", timeout=30) as response:
        cached = json.load(response)
    if cached.get("run_id") != run_id or cached.get("source_manifest_sha256") != manifest_hash:
        raise RuntimeError("Website returned a different report; publication not verified.")
    if analytics_browse:
        if cached.get("dataset_catalog") != expected_catalog:
            raise RuntimeError("Website report dataset catalog differs; publication not verified.")
        with urlopen(api.rstrip("/") + "/api/runs/" + quote(run_id, safe="") + "/analytics/datasets", timeout=30) as response:
            datasets = json.load(response)
        if (datasets.get("run_id") != run_id or datasets.get("status") != "available"
                or datasets.get("dataset_catalog") != expected_catalog
                or datasets.get("bundle_manifest_sha256") != expected_catalog["bundle_manifest_sha256"]):
            raise RuntimeError("Website dataset cache differs or is unavailable; publication not verified.")
    print(f"Verified cached report for {run_id}" + (" and full dataset cache" if analytics_browse else ""), flush=True)
    return cached


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_id")
    parser.add_argument("destination", help="New export directory; preserve it if a Databricks job fails.")
    parser.add_argument("--api", default="http://127.0.0.1:5080")
    parser.add_argument("--profile", default="gearth")
    parser.add_argument("--storage", choices=("neon", "local"), required=True)
    parser.add_argument("--databricks", default=shutil.which("databricks") or "databricks")
    parser.add_argument("--dotnet", default=shutil.which("dotnet") or "dotnet")
    parser.add_argument("--analytics-browse", action="store_true", help="Export actual Databricks rows and publish their verified read-only cache on this observer host.")
    parser.add_argument("--timeout", type=int, default=1200)
    args = parser.parse_args()
    if not 60 <= args.timeout <= 3600:
        parser.error("--timeout must be 60..3600 seconds")
    try:
        run_analysis(args.run_id, args.destination, api=args.api,
                     cli=DatabricksCli(args.databricks, args.profile), storage=args.storage,
                     dotnet=args.dotnet, timeout=args.timeout, analytics_browse=args.analytics_browse)
    except (RuntimeError, ValueError, OSError, EOFError, subprocess.TimeoutExpired) as error:
        print(f"Analysis stopped: {str(error) if isinstance(error, (RuntimeError, ValueError)) else type(error).__name__}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
