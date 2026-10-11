"""Bounded, immutable finished-run HTTP export (stdlib only)."""
from __future__ import annotations

import argparse
import collections
import datetime as dt
import gzip
import hashlib
import json
import os
from pathlib import Path
from urllib.parse import quote, urlencode, urlparse
from urllib.request import Request, urlopen

FORMAT = "faultline.run-export.v1"
TERMINAL = {"completed", "stopped", "failed"}
MAX_ROW_BYTES = 2 * 1024 * 1024
DEFAULT_CHUNK_BYTES = 64 * 1024 * 1024
KINDS = {"opening_balance", "agent_day", "nation_day", "transaction", "decision_request", "decision_result", "action_effect", "event", "run_metadata"}


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")


def integer(value, name, minimum=0):
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise ValueError(f"{name} must be an integer >= {minimum}")
    return value


def record_check(record, run_id, revision, through_sequence):
    if record.get("run_id") != run_id:
        raise ValueError("record run_id mismatch")
    integer(record.get("sequence"), "sequence", 1)
    integer(record.get("tick"), "tick")
    integer(record.get("revision"), "revision")
    if record["sequence"] > through_sequence or record["revision"] > revision:
        raise ValueError("record exceeds pinned final cursor")
    if record.get("kind") not in KINDS or not isinstance(record.get("payload"), dict):
        raise ValueError("unsupported record kind or payload")
    if record["kind"] == "event" and record["payload"].get("event_type") == "timeline_forked":
        raise ValueError("faultline.run-export.v1 does not support Neon fork timelines; stable branch/timeline identity is required")
    # Reject lossy floating money at all nesting depths; money can be negative in deltas.
    def monetary_leaves(node, key):
        if node is None:
            return
        if isinstance(node, dict):
            for value in node.values():
                monetary_leaves(value, key)
        elif isinstance(node, list):
            for value in node:
                monetary_leaves(value, key)
        elif isinstance(node, bool) or not isinstance(node, int):
            raise ValueError(f"{key} must contain integer minor units")
    def money_check(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key.lower().endswith("minor") and value is not None:
                    monetary_leaves(value, key)
                money_check(value)
        elif isinstance(node, list):
            for value in node:
                money_check(value)
    money_check(record["payload"])


def export_run(base_url, run_id, destination, *, token=None, page_size=1000, chunk_rows=10000, chunk_bytes=DEFAULT_CHUNK_BYTES, fetch=None):
    """Export pinned terminal snapshot; no credentials enter files or error messages.

    fetch(path, params) injectable for offline fixtures. At most one page is retained.
    An interrupted export deliberately has no manifest and is never importable.
    """
    if not 1 <= page_size <= 2000 or chunk_rows < 1 or chunk_bytes < 1:
        raise ValueError("page_size must be 1..2000 and chunk_rows/chunk_bytes positive")
    parsed = urlparse(base_url)
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("API URL must not contain credentials, query or fragment")
    if parsed.scheme not in {"http", "https"}:
        raise ValueError("API URL must use HTTP(S)")
    root = Path(destination)
    root.mkdir(parents=True, exist_ok=False)
    endpoint = "/api/runs/" + quote(run_id, safe="") + "/analytics/"
    if fetch is None:
        def fetch(path, params):
            request = Request(base_url.rstrip("/") + path + ("?" + urlencode(params) if params else ""))
            if token:
                request.add_header("Authorization", "Bearer " + token)
            try:
                with urlopen(request, timeout=120) as response:
                    return json.load(response)
            except Exception:
                raise RuntimeError("analytics HTTP request failed; check API connectivity and authorization") from None
    metadata = fetch(endpoint + "export-metadata", {})
    if metadata.get("run_id") != run_id or metadata.get("status") not in TERMINAL:
        raise ValueError("only completed/stopped/failed committed snapshots can be exported")
    if any(metadata.get(key) is not True for key in ("analytics_enabled", "audit_available", "audit_from_tick_zero")):
        raise ValueError("v1 export requires authoritative audit captured from tick zero")
    if metadata.get("audit_schema_version") != 1:
        raise ValueError("unsupported operational audit schema")
    revision = integer(metadata.get("through_revision"), "through_revision")
    through = integer(metadata.get("through_sequence"), "through_sequence")
    if through == 0:
        raise ValueError("empty audit is not a complete run export")
    integer(metadata.get("through_event_sequence"), "through_event_sequence")
    counts = collections.Counter()
    files = []
    after = 0
    output = None
    rows = 0
    uncompressed_bytes = 0
    first = None
    previous_revision = 0

    def finish_chunk():
        nonlocal output, rows, uncompressed_bytes
        if output is None:
            return
        output.close()
        path = root / name
        digest = hashlib.sha256()
        with path.open("rb") as data:
            for block in iter(lambda: data.read(1024 * 1024), b""):
                digest.update(block)
        files.append({"path": name, "sha256": digest.hexdigest(), "bytes": path.stat().st_size,
                      "uncompressed_bytes": uncompressed_bytes, "row_count": rows, "first_sequence": first, "last_sequence": after})
        output = None
        rows = 0
        uncompressed_bytes = 0

    try:
        while after < through:
            page = fetch(endpoint + "audit", {"after": after, "limit": page_size,
                          "through_revision": revision, "through_sequence": through})
            if any(page.get(key) != value for key, value in (("run_id", run_id), ("through_revision", revision), ("through_sequence", through))):
                raise ValueError("audit response changed pinned snapshot")
            records = page.get("records")
            if not isinstance(records, list) or not records or len(records) > page_size:
                raise ValueError("empty/oversized audit page before final cursor")
            for record in records:
                record_check(record, run_id, revision, through)
                if record["sequence"] != after + 1 or record["revision"] < previous_revision:
                    raise ValueError("audit sequence gap/duplicate or revision regression")
                line = canonical(record) + b"\n"
                if len(line) > MAX_ROW_BYTES:
                    raise ValueError("audit row exceeds the 2 MiB bounded-record limit")
                # Gzip input is unsplittable in Spark; bound uncompressed size as
                # well as rows. A single valid row can exceed a smaller custom cap.
                if output is not None and uncompressed_bytes + len(line) > chunk_bytes:
                    finish_chunk()
                if output is None:
                    name = f"audit-{len(files):06d}.ndjson.gz"
                    output = gzip.GzipFile(filename=str(root / name), mode="wb", mtime=0)
                    first = record["sequence"]
                output.write(line)
                rows += 1
                uncompressed_bytes += len(line)
                after = record["sequence"]
                previous_revision = record["revision"]
                counts[record["kind"]] += 1
                if rows >= chunk_rows:
                    finish_chunk()
            if page.get("next_after") != after or page.get("has_more") != (after < through):
                raise ValueError("invalid audit pagination cursor")
        finish_chunk()
    finally:
        if output:
            output.close()
    unknown = metadata.get("metadata_unknown", [])
    missing = ["audit_before_capture_start"] if metadata.get("audit_from_tick_zero") is False else []
    fixture = metadata.get("source", {}).get("fixture")
    manifest = {"schema_version": FORMAT, "record_schema_version": "faultline.audit.v1", "run": metadata,
                "created_at": dt.datetime.now(dt.timezone.utc).isoformat(), "files": files,
                "record_count": after, "counts_by_kind": dict(sorted(counts.items())),
                "completeness": {"snapshot_complete": True, "run_complete": metadata["status"] == "completed",
                                 "capture_complete": metadata.get("audit_from_tick_zero"),
                                 "missing_data": missing, "metadata_unknown": unknown},
                "provenance": {"source": "committed operational audit", "cursor": "run revision and audit sequence",
                               "synthetic": metadata.get("synthetic", fixture), "source_mode": metadata.get("source")}}
    target = root / "manifest.json"
    with target.open("xb") as stream:
        stream.write(canonical(manifest) + b"\n")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_id")
    parser.add_argument("destination", help="new directory; existing exports cannot be overwritten")
    parser.add_argument("--api", default="http://localhost:5080")
    parser.add_argument("--token-env", help="environment variable containing optional API bearer token")
    parser.add_argument("--page-size", type=int, default=1000)
    parser.add_argument("--chunk-rows", type=int, default=10000)
    parser.add_argument("--chunk-bytes", type=int, default=DEFAULT_CHUNK_BYTES, help="uncompressed bytes per gzip chunk (default 64 MiB)")
    args = parser.parse_args()
    manifest = export_run(args.api, args.run_id, args.destination, token=os.environ.get(args.token_env) if args.token_env else None,
                          page_size=args.page_size, chunk_rows=args.chunk_rows, chunk_bytes=args.chunk_bytes)
    print(f"Exported {manifest['record_count']} audit rows in {len(manifest['files'])} files")


if __name__ == "__main__":
    main()
