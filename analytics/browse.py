"""Actual Delta row-cache export. Distributed staging, bounded streaming chunks.

This module never derives Databricks evidence from offline typed_record projections.
"""
from __future__ import annotations

from datetime import datetime, timezone
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile

from .export import canonical
from .transform import TABLES

MAX_ROWS = 2000
MAX_CHUNK_BYTES = 4 * 1024 * 1024
MAX_ROW_BYTES = 8_000_000
MAX_MANIFEST_BYTES = 16 * 1024 * 1024
MAX_PATHS = 4096
MAX_DEPTH = 4
DATASETS = {"bronze.audit": "workspace.gearth_bronze.audit", **{
    "silver." + name: "workspace.gearth_silver." + name for name in TABLES.values()}, **{
    "silver." + name: "workspace.gearth_silver." + name for name in ("decision_options", "event_edges")}, **{
    "gold." + name: "workspace.gearth_gold." + name for name in (
        "agent_days", "agent_latest", "country_days", "public_service_days", "public_service_transfers", "import_receipts")}}


def safe_path(root, relative, *, check_filesystem=True):
    if not isinstance(relative, str) or not relative or len(relative) > 400 or "\\" in relative or ":" in relative or any(ord(char) < 32 for char in relative):
        raise ValueError("Unsafe browse chunk path")
    parts = PurePosixPath(relative).parts
    if relative.startswith("/") or any(part in ("", ".", "..") for part in relative.split("/")):
        raise ValueError("Unsafe browse chunk path")
    root = Path(root)
    if check_filesystem and root.is_symlink():
        raise ValueError("Browse root is a symlink")
    current = root
    for part in parts:
        current = current / part
        if check_filesystem and current.is_symlink():
            raise ValueError("Browse chunk is a symlink")
    return current


def _sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


class PayloadInventory:
    """Bounded JSON Pointer inventory; arrays are leaves, nested depth is explicit."""
    def __init__(self):
        self.fields = {}
        self.truncated = False
        self.depth_limited = False

    def add(self, payload):
        def visit(value, prefix, depth):
            if not isinstance(value, dict):
                return
            for name, item in value.items():
                path = prefix + "/" + str(name).replace("~", "~0").replace("/", "~1")
                if path not in self.fields:
                    if len(self.fields) >= MAX_PATHS:
                        self.truncated = True
                        continue
                    self.fields[path] = dict(path=path, present_count=0, null_count=0, non_null_count=0, zero_or_false_count=0)
                stats = self.fields[path]
                stats["present_count"] += 1
                stats["null_count" if item is None else "non_null_count"] += 1
                if item is False or (type(item) in (int, float) and item == 0):
                    stats["zero_or_false_count"] += 1
                if isinstance(item, dict):
                    if depth < MAX_DEPTH:
                        visit(item, path, depth + 1)
                    elif item:
                        self.depth_limited = True
        visit(payload, "", 1)

    def result(self):
        return {"payload_fields": sorted(self.fields.values(), key=lambda item: item["path"]),
                "field_coverage_scope": "all_run_rows_bounded_inventory",
                "field_coverage_limit": {"max_depth": MAX_DEPTH, "max_paths": MAX_PATHS,
                    "array_elements": False, "path_inventory_truncated": self.truncated,
                    "depth_limited": self.depth_limited}}


def stream_dataset(rows, root, dataset_id, table_name, columns):
    """Rechunk actual staged Spark JSON lines; holds at most one bounded row."""
    if dataset_id not in DATASETS or DATASETS[dataset_id] != table_name:
        raise ValueError("Dataset is not allowlisted")
    root = Path(root)
    inventory = PayloadInventory()
    chunks = []
    row_count = 0
    stream = None
    chunk_rows = chunk_bytes = 0
    payload_source = None

    def close_chunk():
        nonlocal stream
        if stream is not None:
            stream.close()
            path = root / chunks[-1]["path"]
            chunks[-1].update(row_count=chunk_rows, uncompressed_bytes=chunk_bytes,
                compressed_bytes=path.stat().st_size, sha256=_sha(path))
            stream = None

    try:
        for line in rows:
            if not isinstance(line, bytes) or len(line) > MAX_ROW_BYTES:
                raise ValueError("Databricks browse row exceeds 8 MB")
            row = json.loads(line)
            if not isinstance(row, dict):
                raise ValueError("Browse row must be an object")
            # Rewrite only JSON serialization, never columns or original payload strings.
            encoded = canonical(row) + b"\n"
            if len(encoded) > MAX_ROW_BYTES:
                raise ValueError("Databricks browse row exceeds 8 MB")
            source = "payload_json" if "payload_json" in row else "raw_json" if "raw_json" in row else None
            if source:
                payload_source = source
                payload = row[source]
                if payload is not None:
                    payload = json.loads(payload) if isinstance(payload, str) else payload
                    if source == "raw_json" and isinstance(payload, dict):
                        payload = payload.get("payload", payload)
                    inventory.add(payload)
            if stream is not None and (chunk_rows >= MAX_ROWS or chunk_bytes + len(encoded) > MAX_CHUNK_BYTES):
                close_chunk()
            if stream is None:
                relative = f"{dataset_id}/{len(chunks):06d}.ndjson.gz"
                path = safe_path(root, relative)
                path.parent.mkdir(parents=True, exist_ok=True)
                stream = gzip.GzipFile(filename=str(path), mode="wb", mtime=0)
                chunks.append({"path": relative})
                chunk_rows = chunk_bytes = 0
            stream.write(encoded)
            chunk_rows += 1
            chunk_bytes += len(encoded)
            row_count += 1
        close_chunk()
    finally:
        if stream is not None:
            stream.close()
    return {"dataset_id": dataset_id, "table_name": table_name, "availability": "available",
        "row_count": row_count, "columns": columns, "payload_source": payload_source,
        **inventory.result(), "chunks": chunks}


def catalog(manifest, digest):
    return {**{key: manifest[key] for key in ("run_id", "through_revision", "through_sequence",
        "source_manifest_sha256", "generated_at", "qualification_status")},
        "schema_version": "gearth.dataset-catalog.v1", "bundle_manifest_sha256": digest,
        "datasets": [{key: value for key, value in entry.items() if key != "chunks"} for entry in manifest["datasets"]]}


def read_bundle_manifest(root):
    path = safe_path(root, "browse-manifest.json")
    if path.stat().st_size > MAX_MANIFEST_BYTES:
        raise ValueError("Browse manifest exceeds 16 MiB")
    data = path.read_bytes()
    manifest = json.loads(data)
    validate_manifest(manifest)
    return manifest, hashlib.sha256(data).hexdigest()


def validate_manifest(manifest):
    if manifest.get("schema_version") != "gearth.analytics-browse.v1" or manifest.get("qualification_status") != "pass":
        raise ValueError("Unqualified browse manifest")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", manifest.get("run_id", "")):
        raise ValueError("Invalid browse run ID")
    if not re.fullmatch(r"[0-9a-f]{64}", manifest.get("source_manifest_sha256", "")):
        raise ValueError("Invalid browse source hash")
    for key in ("through_revision", "through_sequence"):
        if type(manifest.get(key)) is not int or manifest[key] < 0:
            raise ValueError("Invalid browse source cursor")
    seen = set()
    paths = set()
    if not isinstance(manifest.get("datasets"), list):
        raise ValueError("Browse datasets must be an array")
    for entry in manifest["datasets"]:
        identity = entry.get("dataset_id")
        if identity not in DATASETS or entry.get("table_name") != DATASETS[identity] or identity in seen:
            raise ValueError("Invalid or duplicate browse dataset")
        seen.add(identity)
        if entry.get("availability", "available") == "unavailable":
            if (entry.get("row_count") is not None or entry.get("chunks") or entry.get("columns") or entry.get("payload_fields")
                    or entry.get("unavailable_reason") != "table_not_present" or entry.get("field_coverage_scope") != "unavailable"):
                raise ValueError("Unavailable dataset cannot assert row counts")
            continue
        if entry.get("availability", "available") != "available" or entry.get("field_coverage_scope") not in ("all_run_rows", "all_run_rows_bounded_inventory"):
            raise ValueError("Unknown browse availability or coverage scope")
        if type(entry.get("row_count")) is not int or entry["row_count"] < 0:
            raise ValueError("Invalid browse row count")
        columns = entry.get("columns")
        if not isinstance(columns, list) or len(columns) > 2048:
            raise ValueError("Invalid bounded browse columns")
        names = set()
        for column in columns:
            name = column.get("name")
            if not isinstance(name, str) or not name or name in names or not isinstance(column.get("data_type"), str) or type(column.get("nullable")) is not bool:
                raise ValueError("Invalid browse column schema")
            names.add(name)
            count = column.get("non_null_count")
            if type(count) is not int or not 0 <= count <= entry["row_count"]:
                raise ValueError("Invalid browse column count")
        if "run_id" not in names:
            raise ValueError("Browse schema lacks run_id")
        fields = entry.get("payload_fields")
        if not isinstance(fields, list) or len(fields) > MAX_PATHS:
            raise ValueError("Invalid bounded payload inventory")
        field_names = set()
        for field in fields:
            path = field.get("path")
            if not isinstance(path, str) or not path.startswith("/") or path in field_names:
                raise ValueError("Invalid payload path")
            field_names.add(path)
            for key in ("present_count", "null_count", "non_null_count", "zero_or_false_count"):
                if type(field.get(key)) is not int or not 0 <= field[key] <= entry["row_count"]:
                    raise ValueError("Invalid payload coverage count")
            if field["present_count"] != field["null_count"] + field["non_null_count"] or field["zero_or_false_count"] > field["non_null_count"]:
                raise ValueError("Invalid payload coverage totals")
        total = 0
        for chunk in entry.get("chunks", []):
            relative = chunk.get("path")
            safe_path(Path("/"), relative, check_filesystem=False)
            if not relative.startswith(identity + "/") or not relative.endswith(".ndjson.gz") or relative in paths:
                raise ValueError("Invalid browse chunk identity")
            paths.add(relative)
            if not re.fullmatch(r"[0-9a-f]{64}", chunk.get("sha256", "")):
                raise ValueError("Invalid browse chunk hash")
            for key in ("row_count", "compressed_bytes", "uncompressed_bytes"):
                if type(chunk.get(key)) is not int or chunk[key] <= 0:
                    raise ValueError("Invalid browse chunk count")
            if chunk["compressed_bytes"] > 8 * 1024 * 1024:
                raise ValueError("Oversized compressed browse chunk")
            if chunk["row_count"] > MAX_ROWS or chunk["uncompressed_bytes"] > (MAX_ROW_BYTES if chunk["row_count"] == 1 else MAX_CHUNK_BYTES):
                raise ValueError("Oversized browse chunk")
            total += chunk["row_count"]
        if total != entry["row_count"]:
            raise ValueError("Browse dataset/chunk count disagreement")
    if seen != set(DATASETS):
        raise ValueError("Browse dataset inventory is incomplete")


def validate_bundle(root, *, expected=None, expected_catalog=None):
    manifest, digest = read_bundle_manifest(root)
    if expected:
        for key in ("run_id", "through_revision", "through_sequence", "source_manifest_sha256"):
            if manifest[key] != expected[key]:
                raise ValueError("Browse source provenance disagreement")
    if expected_catalog is not None and catalog(manifest, digest) != expected_catalog:
        raise ValueError("Browse catalog disagreement")
    for entry in manifest["datasets"]:
        if entry.get("availability") == "unavailable":
            continue
        nonnull = {column["name"]: 0 for column in entry["columns"]}
        inventory = PayloadInventory()
        payload_source = None
        for chunk in entry.get("chunks", []):
            path = safe_path(root, chunk["path"])
            if path.stat().st_size != chunk["compressed_bytes"] or _sha(path) != chunk["sha256"]:
                raise ValueError("Browse compressed hash/size disagreement")
            rows = size = 0
            with gzip.open(path, "rb") as stream:
                while True:
                    line = stream.readline(MAX_ROW_BYTES + 1)
                    if not line:
                        break
                    if len(line) > MAX_ROW_BYTES or not line.endswith(b"\n"):
                        raise ValueError("Oversized or incomplete browse row")
                    row = json.loads(line)
                    if not isinstance(row, dict) or row.get("run_id") != manifest["run_id"]:
                        raise ValueError("Browse row run identity disagreement")
                    if "source_manifest_sha256" in row and row["source_manifest_sha256"] != manifest["source_manifest_sha256"]:
                        raise ValueError("Browse row source identity disagreement")
                    for key, maximum in (("revision", manifest["through_revision"]), ("through_revision", manifest["through_revision"]),
                            ("sequence", manifest["through_sequence"]), ("through_sequence", manifest["through_sequence"])):
                        if key in row and row[key] is not None and (type(row[key]) is not int or row[key] < 0 or row[key] > maximum):
                            raise ValueError("Browse row exceeds source cursor")
                    if set(row) != set(nonnull):
                        raise ValueError("Browse row/schema column disagreement")
                    for name, value in row.items():
                        nonnull[name] += value is not None
                    source = "payload_json" if "payload_json" in row else "raw_json" if "raw_json" in row else None
                    if source:
                        payload_source = source
                        payload = row[source]
                        if payload is not None:
                            payload = json.loads(payload) if isinstance(payload, str) else payload
                            if source == "raw_json" and isinstance(payload, dict):
                                payload = payload.get("payload", payload)
                            inventory.add(payload)
                    rows += 1
                    size += len(line)
                    if rows > chunk["row_count"] or size > chunk["uncompressed_bytes"]:
                        raise ValueError("Browse gzip expansion exceeds manifest")
            if rows != chunk["row_count"] or size != chunk["uncompressed_bytes"]:
                raise ValueError("Browse decompressed count disagreement")
        if any(nonnull[column["name"]] != column["non_null_count"] for column in entry["columns"]):
            raise ValueError("Browse column non-null coverage disagreement")
        if any(entry.get(key) != value for key, value in inventory.result().items()) or entry.get("payload_source") != payload_source:
            raise ValueError("Browse payload coverage disagreement")
    return catalog(manifest, digest)


def export_delta_bundle(spark, source_manifest, manifest_hash, destination, qualification):
    """Run-filter actual tables/views, write distributed JSON, stream local staging.

    Staging/destination must be a UC Volume visible through the driver's filesystem.
    No collect, full list of rows, or toLocalIterator is used.
    """
    from pyspark.sql import functions as F
    run = source_manifest["run"]
    if qualification.get("status") != "pass" or any(qualification.get(key) != value for key, value in {
        "run_id": run["run_id"], "through_revision": run["through_revision"],
        "through_sequence": run["through_sequence"], "source_manifest_sha256": manifest_hash}.items()):
        raise ValueError("Delta qualification/source mismatch")
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".browse-", dir=destination.parent))
    try:
        entries = []
        for identity, table in DATASETS.items():
            if not spark.catalog.tableExists(table):
                entries.append({"dataset_id": identity, "table_name": table, "availability": "unavailable",
                    "unavailable_reason": "table_not_present", "row_count": None, "columns": [], "payload_fields": [],
                    "field_coverage_scope": "unavailable", "chunks": []})
                continue
            frame = spark.table(table).where(F.col("run_id") == run["run_id"])
            if "source_manifest_sha256" in frame.columns and frame.where(
                F.col("source_manifest_sha256").isNull() | (F.col("source_manifest_sha256") != manifest_hash)).limit(1).count():
                raise ValueError("Actual Delta dataset has conflicting source identity: " + identity)
            schema = frame.schema.fields
            if len(schema) > 2048:
                raise ValueError("Actual Delta schema exceeds bounded column inventory")
            counts = frame.agg(*[F.count(F.col("`" + field.name.replace("`", "``") + "`")).alias("c" + str(index))
                for index, field in enumerate(schema)]).first().asDict()
            columns = [{"name": field.name, "data_type": field.dataType.simpleString(), "nullable": field.nullable,
                "non_null_count": counts["c" + str(index)]} for index, field in enumerate(schema)]
            staging = temporary / (".stage-" + identity)
            frame.write.mode("error").option("ignoreNullFields", "false").json(str(staging))
            def lines():
                # Iterating filenames is bounded in rows; Spark owns distributed work.
                for directory, _, filenames in os.walk(staging):
                    for name in sorted(filenames):
                        if name.startswith("part-") and name.endswith(".json"):
                            path = Path(directory) / name
                            if path.is_symlink():
                                raise ValueError("Unsafe staged data file")
                            with path.open("rb") as stream:
                                while True:
                                    line = stream.readline(MAX_ROW_BYTES + 1)
                                    if not line:
                                        break
                                    yield line
            entry = stream_dataset(lines(), temporary, identity, table, columns)
            actual_count = next(column["non_null_count"] for column in columns if column["name"] == "run_id")
            if entry["row_count"] != actual_count:
                raise ValueError("Staged browse rows disagree with actual Delta cardinality: " + identity)
            if identity.startswith("silver."):
                expected_count = qualification["silver"].get(identity.split(".", 1)[1], {}).get("rows")
                if expected_count is not None and entry["row_count"] != expected_count:
                    raise ValueError("Browse rows disagree with qualified Silver cardinality: " + identity)
            elif identity == "bronze.audit" and entry["row_count"] != qualification["bronze"]["rows"]:
                raise ValueError("Browse rows disagree with qualified Bronze cardinality")
            entries.append(entry)
            shutil.rmtree(staging)
        manifest = {"schema_version": "gearth.analytics-browse.v1", "run_id": run["run_id"],
            "through_revision": run["through_revision"], "through_sequence": run["through_sequence"],
            "source_manifest_sha256": manifest_hash, "generated_at": datetime.now(timezone.utc).isoformat(),
            "qualification_status": "pass", "datasets": entries}
        data = canonical(manifest) + b"\n"
        if len(data) > MAX_MANIFEST_BYTES:
            raise ValueError("Browse manifest exceeds 16 MiB")
        (temporary / "browse-manifest.json").write_bytes(data)
        validate_bundle(temporary)
        # UC Volumes use object storage. Do not assume POSIX directory rename is
        # supported or atomic: immutable objects first, qualified manifest last.
        destination.mkdir(parents=True, exist_ok=True)
        for entry in manifest["datasets"]:
            for chunk in entry.get("chunks", []):
                source = safe_path(temporary, chunk["path"])
                relative = entry["dataset_id"] + "/" + chunk["sha256"] + ".ndjson.gz"
                target = safe_path(destination, relative)
                target.parent.mkdir(parents=True, exist_ok=True)
                if target.exists():
                    if target.stat().st_size != chunk["compressed_bytes"] or _sha(target) != chunk["sha256"]:
                        raise ValueError("Immutable browse chunk identity conflict")
                else:
                    created = False
                    try:
                        with source.open("rb") as incoming:
                            with target.open("xb") as outgoing:
                                created = True
                                shutil.copyfileobj(incoming, outgoing, 1024 * 1024)
                    except BaseException:
                        if created:
                            target.unlink(missing_ok=True)
                        raise
                if target.stat().st_size != chunk["compressed_bytes"] or _sha(target) != chunk["sha256"]:
                    raise ValueError("Copied browse chunk integrity disagreement")
                chunk["path"] = relative
        data = canonical(manifest) + b"\n"
        if len(data) > MAX_MANIFEST_BYTES:
            raise ValueError("Browse manifest exceeds 16 MiB")
        current = safe_path(destination, "browse-manifest.json")
        if current.exists():
            old_data = current.read_bytes()
            _, old_digest = read_bundle_manifest(destination)
            archive = safe_path(destination, "manifests/" + old_digest + ".json")
            archive.parent.mkdir(exist_ok=True)
            if archive.exists() and archive.read_bytes() != old_data:
                raise ValueError("Browse archive identity conflict")
            if not archive.exists():
                archive.write_bytes(old_data)
        # Validate exact candidate bytes against copied immutable objects before
        # exposing the current marker. Failed jobs cannot publish observer caches.
        candidate = safe_path(destination, "manifests/" + hashlib.sha256(data).hexdigest() + ".json")
        candidate.parent.mkdir(exist_ok=True)
        if candidate.exists() and candidate.read_bytes() != data:
            raise ValueError("Browse manifest identity conflict")
        if not candidate.exists():
            candidate.write_bytes(data)
        # All candidate rows already passed validation; content-addressed copies
        # preserve exact bytes. Read transport revalidates complete bundles.
        current.write_bytes(data)
        receipt = validate_bundle(destination)
        return receipt
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
