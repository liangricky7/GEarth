"""Streaming export validation; refuses partial files or unsafe manifest paths."""
from __future__ import annotations

import argparse
import collections
import gzip
import hashlib
import json
import sqlite3
import tempfile
from pathlib import Path, PurePosixPath

from .export import FORMAT, TERMINAL, MAX_ROW_BYTES, integer, record_check, canonical
from .transform import typed_record, validate_capture
from .contracts import contract_matches


def file_sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def safe_file(root, name):
    if not isinstance(name, str) or not name or "\\" in name:
        raise ValueError("unsafe export path")
    path = PurePosixPath(name)
    if path.is_absolute() or any(part in {"..", "."} for part in path.parts) or ":" in name:
        raise ValueError("unsafe export path")
    actual = root / name
    # No symlink may redirect an export outside its immutable upload directory.
    if actual.is_symlink() or root.resolve() not in actual.resolve().parents:
        raise ValueError("unsafe export path or symlink")
    if not actual.is_file() or not name.endswith(".ndjson.gz"):
        raise ValueError("missing gzip NDJSON export chunk")
    return actual


def read_manifest(path):
    manifest_path = Path(path)
    with manifest_path.open(encoding="utf-8") as stream:
        manifest = json.load(stream)
    if not contract_matches(manifest.get("schema_version"), FORMAT) or not contract_matches(manifest.get("record_schema_version"), "gearth.audit.v1"):
        raise ValueError("unsupported export schema")
    run = manifest.get("run", {})
    if not isinstance(run.get("run_id"), str) or not run["run_id"] or run.get("status") not in TERMINAL:
        raise ValueError("manifest requires finished run")
    for key in ("through_revision", "through_sequence", "through_event_sequence"):
        integer(run.get(key), key)
    if run["through_sequence"] == 0 or run.get("audit_schema_version") != 1 or any(run.get(key) is not True for key in ("analytics_enabled", "audit_available", "audit_from_tick_zero")):
        raise ValueError("v1 requires nonempty audit captured from tick zero")
    integer(run.get("tick"), "tick")
    integer(run.get("population"), "population", 1)
    complete = manifest.get("completeness", {})
    if complete.get("snapshot_complete") is not True or not isinstance(complete.get("missing_data"), list):
        raise ValueError("snapshot is incomplete; export again from a committed terminal state")
    if complete.get("run_complete") != (run["status"] == "completed"):
        raise ValueError("run completeness/status disagreement")
    if not isinstance(manifest.get("files"), list):
        raise ValueError("files must be a list")
    integer(manifest.get("record_count"), "record_count", 1)
    counts = manifest.get("counts_by_kind")
    if not isinstance(counts, dict):
        raise ValueError("counts_by_kind must be an object")
    for count in counts.values():
        integer(count, "kind row count", 1)
    if sum(counts.values()) != manifest["record_count"]:
        raise ValueError("manifest kind coverage count mismatch")
    for entry in manifest["files"]:
        if not isinstance(entry, dict):
            raise ValueError("file entry must be an object")
        for key in ("bytes", "row_count", "first_sequence", "last_sequence"):
            integer(entry.get(key), key, 1)
        if "uncompressed_bytes" in entry:
            integer(entry["uncompressed_bytes"], "uncompressed_bytes", 1)
        digest = entry.get("sha256")
        if not isinstance(digest, str) or len(digest) != 64 or any(value not in "0123456789abcdef" for value in digest):
            raise ValueError("file digest must be lowercase SHA256")
    return manifest


def iter_records(manifest_path, manifest=None):
    """Two passes (compressed hash then streamed decode) keep memory bounded."""
    manifest_path = Path(manifest_path)
    manifest = manifest or read_manifest(manifest_path)
    run = manifest["run"]
    after = 0
    last_revision = 0
    counts = collections.Counter()
    names = set()
    for entry in manifest["files"]:
        name = entry.get("path")
        if name in names:
            raise ValueError("duplicate export file")
        names.add(name)
        path = safe_file(manifest_path.parent, name)
        if file_sha256(path) != entry.get("sha256") or path.stat().st_size != entry.get("bytes"):
            raise ValueError("chunk hash/size mismatch")
        rows = 0
        uncompressed_bytes = 0
        first = None
        with gzip.open(path, "rb") as stream:
            while True:
                line = stream.readline(MAX_ROW_BYTES + 1)
                if not line:
                    break
                if len(line) > MAX_ROW_BYTES:
                    raise ValueError("audit row exceeds the 2 MiB bounded-record limit")
                uncompressed_bytes += len(line)
                if not line.strip():
                    raise ValueError("blank audit row")
                record = json.loads(line)
                record_check(record, run["run_id"], run["through_revision"], run["through_sequence"])
                if record["sequence"] != after + 1:
                    raise ValueError("duplicate/conflicting record or sequence gap")
                if record["revision"] < last_revision:
                    raise ValueError("audit revision regression")
                if first is None:
                    first = record["sequence"]
                after = record["sequence"]
                last_revision = record["revision"]
                counts[record["kind"]] += 1
                rows += 1
                yield record
        if rows != entry.get("row_count") or first != entry.get("first_sequence") or after != entry.get("last_sequence"):
            raise ValueError("chunk count/sequence range mismatch")
        if "uncompressed_bytes" in entry and uncompressed_bytes != entry["uncompressed_bytes"]:
            raise ValueError("chunk uncompressed byte count mismatch")
    if after != run["through_sequence"] or after != manifest.get("record_count"):
        raise ValueError("manifest coverage does not reach final committed cursor")
    if dict(counts) != manifest.get("counts_by_kind"):
        raise ValueError("manifest kind counts mismatch")


def validate(manifest_path):
    manifest = read_manifest(manifest_path)
    run = manifest["run"]
    with tempfile.TemporaryDirectory(prefix="gearth-validate-") as temporary:
        db = sqlite3.connect(str(Path(temporary) / "coverage.sqlite"))
        db.executescript("""PRAGMA journal_mode=OFF;
            CREATE TABLE opening(id TEXT PRIMARY KEY, kind TEXT);
            CREATE TABLE days(id TEXT, tick INTEGER, frame TEXT, sequence INTEGER, PRIMARY KEY(id,tick,frame));
            CREATE INDEX day_ticks ON days(tick,frame);
            CREATE TABLE creations(id TEXT PRIMARY KEY,tick INTEGER,sequence INTEGER);
            CREATE TABLE countries(id TEXT PRIMARY KEY);
            CREATE TABLE country_days(id TEXT,tick INTEGER,frame TEXT,PRIMARY KEY(id,tick,frame));
            CREATE TABLE event(seq INTEGER PRIMARY KEY, event_id TEXT UNIQUE);
            CREATE TABLE transactions(id TEXT PRIMARY KEY);
            CREATE TABLE account_activity(id TEXT PRIMARY KEY,sequence INTEGER);
            CREATE TABLE decisions(id TEXT,kind TEXT,PRIMARY KEY(id,kind));
        """)
        metadata_count = 0

        def creation(account_id, tick, sequence):
            prior = db.execute("SELECT tick,sequence FROM creations WHERE id=?", (account_id,)).fetchone()
            if prior is not None and prior[0] != tick:
                raise ValueError("conflicting agent creation tick")
            if prior is None:
                db.execute("INSERT INTO creations VALUES (?,?,?)", (account_id, tick, sequence))
            elif sequence < prior[1]:
                db.execute("UPDATE creations SET sequence=? WHERE id=?", (sequence, account_id))

        try:
            for record in iter_records(manifest_path, manifest):
                kind = record["kind"]
                _, row = typed_record(record)
                validate_capture(record, run.get("capture_contract_version"))
                if kind == "run_metadata":
                    metadata_count += 1
                    if record["sequence"] != 1:
                        raise ValueError("run_metadata must be first and unique")
                    payload = record["payload"]
                    for key in ("capture_contract_version", "features", "active_features", "banking_paused", "economy_configuration"):
                        if canonical(payload.get(key)) != canonical(run.get(key)):
                            raise ValueError("source metadata differs from pinned manifest: " + key)
                elif kind == "opening_balance":
                    db.execute("INSERT INTO opening VALUES (?,?)", (row["account_id"], row["account_type"]))
                    if row["account_type"] == "created_agent":
                        creation_tick = integer(record["payload"].get("creation_tick"), "creation_tick")
                        if creation_tick != row["tick"] or creation_tick > run["tick"] or row["balance_minor"] != 0:
                            raise ValueError("invalid created-agent opening balance/tick")
                        if (db.execute("SELECT 1 FROM account_activity WHERE id=?", (row["account_id"],)).fetchone()
                                or db.execute("SELECT 1 FROM days WHERE id=?", (row["account_id"],)).fetchone()):
                            raise ValueError("created-agent opening must precede transfers and state snapshots")
                        creation(row["account_id"], creation_tick, row["sequence"])
                    country_id = record["payload"].get("agent", {}).get("country_id")
                    if isinstance(country_id, str) and country_id:
                        db.execute("INSERT OR IGNORE INTO countries VALUES (?)", (country_id,))
                elif kind == "agent_day":
                    if row["frame_kind"] not in {"day", "end"} or row["tick"] > run["tick"] or (row["frame_kind"] == "day" and row["tick"] == 0):
                        raise ValueError("invalid daily/final snapshot tick")
                    db.execute("INSERT INTO days VALUES (?,?,?,?)", (row["agent_id"], row["tick"], row["frame_kind"], row["sequence"]))
                    if row["frame_kind"] == "end" and row["tick"] != run["tick"]:
                        raise ValueError("final agent snapshot does not match terminal tick")
                elif kind == "nation_day":
                    if not isinstance(row["country_id"], str) or not row["country_id"] or row["frame_kind"] not in {"day", "end"}:
                        raise ValueError("invalid country snapshot identity/frame")
                    if row["tick"] > run["tick"] or row["frame_kind"] == "day" and row["tick"] == 0 or row["frame_kind"] == "end" and row["tick"] != run["tick"]:
                        raise ValueError("invalid country snapshot tick")
                    db.execute("INSERT INTO country_days VALUES (?,?,?)", (row["country_id"], row["tick"], row["frame_kind"]))
                elif kind == "event":
                    integer(row["event_sequence"], "event sequence", 1)
                    if not isinstance(row["event_id"], str) or not row["event_id"]:
                        raise ValueError("missing event ID")
                    db.execute("INSERT INTO event VALUES (?,?)", (row["event_sequence"], row["event_id"]))
                    event_payload = record["payload"].get("payload", {})
                    if row["event_type"] == "business_started" and event_payload.get("opening_cash_minor") == 0:
                        account_id = event_payload.get("account_id")
                        if not isinstance(account_id, str) or not account_id:
                            raise ValueError("creation event missing authoritative account ID")
                        creation(account_id, row["tick"], row["sequence"])
                elif kind == "transaction":
                    db.execute("INSERT INTO transactions VALUES (?)", (row["transaction_id"],))
                    for account_id in (row["sender_account_id"], row["recipient_account_id"]):
                        db.execute("INSERT OR IGNORE INTO account_activity VALUES (?,?)", (account_id, row["sequence"]))
                elif kind in {"decision_request", "decision_result"}:
                    db.execute("INSERT INTO decisions VALUES (?,?)", (row["decision_id"], kind))
            if metadata_count != 1:
                raise ValueError("missing first run_metadata record")
            count, low, high = db.execute("SELECT COUNT(*),MIN(seq),MAX(seq) FROM event").fetchone()
            expected_events = run["through_event_sequence"]
            if count != expected_events or expected_events and (low != 1 or high != expected_events):
                raise ValueError("event coverage does not reach final event cursor")
            opening_count = db.execute("SELECT COUNT(*) FROM opening WHERE kind='agent'").fetchone()[0]
            initial_population = run.get("initial_population")
            if initial_population is not None and opening_count != initial_population:
                raise ValueError("opening agent count does not match initial population")
            if opening_count == 0:
                raise ValueError("missing opening agent balances")
            end_count = db.execute("SELECT COUNT(*) FROM days WHERE frame='end'").fetchone()[0]
            if end_count != run["population"]:
                raise ValueError("final agent snapshot coverage does not match population")
            known = "SELECT id FROM opening WHERE kind='agent' UNION SELECT id FROM creations"
            if db.execute(f"SELECT COUNT(*) FROM days WHERE id NOT IN ({known})").fetchone()[0]:
                raise ValueError("state row contains unknown agent identity")
            if db.execute(f"SELECT COUNT(*) FROM ({known}) a WHERE NOT EXISTS (SELECT 1 FROM days d WHERE d.id=a.id AND frame='end')").fetchone()[0]:
                raise ValueError("known agent absent from final end snapshots")
            # Initial agents require every daily tick; new agents require every tick
            # from their authoritative creation boundary. Defunct firms stay explicit.
            for agent_id, in db.execute(known):
                initial = db.execute("SELECT 1 FROM opening WHERE id=? AND kind='agent'", (agent_id,)).fetchone() is not None
                first_expected = 1
                if not initial:
                    created_tick, created_sequence = db.execute("SELECT tick,sequence FROM creations WHERE id=?", (agent_id,)).fetchone()
                    first_expected = max(1, created_tick)
                    first_daily_sequence = db.execute("SELECT MIN(sequence) FROM days WHERE tick=? AND frame='day'", (created_tick,)).fetchone()[0]
                    if created_tick > 0 and first_daily_sequence is not None and created_sequence > first_daily_sequence:
                        first_expected = created_tick + 1  # Created after this tick's synchronous day frame.
                count, first_tick = db.execute("SELECT COUNT(*),MIN(tick) FROM days WHERE id=? AND frame='day'", (agent_id,)).fetchone()
                expected = max(0, run["tick"] - first_expected + 1)
                if count != expected or expected and first_tick != first_expected:
                    raise ValueError("missing daily agent snapshot")
            if run["tick"] and db.execute("SELECT COUNT(*) FROM opening o WHERE kind='agent' AND NOT EXISTS (SELECT 1 FROM days d WHERE d.id=o.id AND d.frame='day')").fetchone()[0]:
                raise ValueError("opening agent absent from daily snapshots")
            if db.execute("SELECT COUNT(*) FROM opening o WHERE kind='agent' AND NOT EXISTS (SELECT 1 FROM days d WHERE d.id=o.id AND d.frame='end')").fetchone()[0]:
                raise ValueError("opening agent absent from final snapshots")
            for country_id, in db.execute("SELECT id FROM countries"):
                count = db.execute("SELECT COUNT(*) FROM country_days WHERE id=?", (country_id,)).fetchone()[0]
                if count != run["tick"] + 1:
                    raise ValueError("missing country daily/final snapshot")
        except sqlite3.IntegrityError:
            raise ValueError("duplicate conflicting semantic key in audit") from None
        finally:
            db.close()
    return {"status": "pass", "run_id": manifest["run"]["run_id"], "record_count": manifest["record_count"],
            "source_manifest_sha256": file_sha256(manifest_path)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest")
    args = parser.parse_args()
    print(json.dumps(validate(args.manifest), sort_keys=True))


if __name__ == "__main__":
    main()
