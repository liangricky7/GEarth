"""Offline report and reconciliation using disk-backed state, no model calls."""
from __future__ import annotations

import argparse
import collections
import datetime as dt
import json
import sqlite3
import tempfile
from pathlib import Path

from .export import canonical
from .transform import typed_record, agent_facts
from .choice import candidate_matches, matching_indices, parameter_contract
from .validate import file_sha256, iter_records, read_manifest, validate

ANALYSIS_VERSION = "1.2.0"


def build_report(manifest_path, *, max_agents=100, work_directory=None):
    """Bounded report: full evidence remains in audit and Delta, preview limited.

    SQLite retains one account row, one latest agent row and all pending decision
    joins on disk. Never collects a complete ledger/year of snapshots in RAM.
    """
    validate(manifest_path)
    manifest = read_manifest(manifest_path)
    run = manifest["run"]
    counts = collections.Counter()
    total_transfer = 0
    transaction_count = 0
    fallback_count = 0
    missing_context = 0
    choice_errors = 0
    accepted_without_offered_index = 0
    bounded_choices = 0
    ambiguous_choices = 0
    transfer_kinds = {}
    feature_metadata = {}
    services_snapshots = 0
    countries = {}
    evidence = []
    daily_counts = {"agent_snapshots": 0, "escrow_snapshots": 0, "mismatches": 0, "unknown_openings": 0}
    daily_examples = []
    daily_evidence = []
    with tempfile.TemporaryDirectory(prefix="faultline-analysis-", dir=work_directory) as temp:
        db = sqlite3.connect(str(Path(temp) / "checks.sqlite"))
        db.executescript("""
        PRAGMA journal_mode=OFF;
        CREATE TABLE accounts(id TEXT PRIMARY KEY, opening INTEGER, net INTEGER NOT NULL DEFAULT 0,
                              latest INTEGER, sequence INTEGER, account_type TEXT);
        CREATE TABLE agents(id TEXT PRIMARY KEY, sequence INTEGER, tick INTEGER, role TEXT, country TEXT,
                            cash INTEGER, health REAL, wellbeing REAL, eligible INTEGER, facts TEXT);
        CREATE TABLE decisions(id TEXT PRIMARY KEY, request TEXT, result TEXT, request_seq INTEGER, result_seq INTEGER);
        CREATE TABLE transactions(id TEXT PRIMARY KEY, hash TEXT);
        """)

        def account(account_id):
            if not isinstance(account_id, str) or not account_id:
                raise ValueError("missing account identifier")
            db.execute("INSERT OR IGNORE INTO accounts(id) VALUES (?)", (account_id,))

        def daily_check(account_id, actual, record, account_type):
            opening, net = db.execute("SELECT opening,net FROM accounts WHERE id=?", (account_id,)).fetchone()
            daily_counts["escrow_snapshots" if account_type == "escrow" else "agent_snapshots"] += 1
            ref = {"run_id": run["run_id"], "sequence": record["sequence"], "kind": record["kind"]}
            if len(daily_evidence) < 2:
                daily_evidence.append(ref)
            if opening is None:
                daily_counts["unknown_openings"] += 1
            elif actual != opening + net:
                daily_counts["mismatches"] += 1
                if len(daily_examples) < 20:
                    daily_examples.append({"account_id": account_id, "tick": record["tick"], "frame_kind": record["payload"].get("frame_kind"),
                        "expected_minor": opening + net, "actual_minor": actual, "evidence_ref": ref})

        for record in iter_records(manifest_path, manifest):
            kind = record["kind"]
            payload = record["payload"]
            _, row = typed_record(record)
            counts[kind] += 1
            if len(evidence) < 20:
                evidence.append({"run_id": run["run_id"], "sequence": record["sequence"], "kind": kind})
            if kind == "run_metadata":
                feature_metadata = {key: payload.get(key) for key in ("capture_contract_version", "features", "active_features", "banking_paused", "economy_configuration")}
            elif kind == "opening_balance":
                account(row["account_id"])
                existing = db.execute("SELECT opening FROM accounts WHERE id=?", (row["account_id"],)).fetchone()[0]
                if existing is not None:
                    raise ValueError("duplicate opening balance")
                db.execute("UPDATE accounts SET opening=?,account_type=? WHERE id=?", (row["balance_minor"], row["account_type"], row["account_id"]))
                agent = payload.get("agent")
                if isinstance(agent, dict):
                    # Opening balances supply the exact tick-zero latest state for a zero-day run.
                    db.execute("INSERT OR IGNORE INTO agents VALUES (?,?,?,?,?,?,?,?,?,?)", (row["account_id"], record["sequence"], 0,
                               agent.get("role"), agent.get("country_id", agent.get("countryId")), row["balance_minor"],
                               agent.get("health"), agent.get("wellbeing"), agent.get("decision_maker", agent.get("decisionMaker")), canonical(agent_facts(agent)).decode()))
                    db.execute("UPDATE accounts SET latest=?,sequence=? WHERE id=?", (row["balance_minor"], record["sequence"], row["account_id"]))
            elif kind == "event" and payload.get("event_type") == "business_started":
                creation = payload.get("payload", {})
                if creation.get("opening_cash_minor") == 0 and isinstance(creation.get("account_id"), str):
                    account(creation["account_id"])
                    db.execute("UPDATE accounts SET opening=0,account_type='agent' WHERE id=? AND opening IS NULL", (creation["account_id"],))
            elif kind == "transaction":
                # A second transaction ID could otherwise count money twice even with distinct audit sequences.
                if db.execute("SELECT 1 FROM transactions WHERE id=?", (row["transaction_id"],)).fetchone():
                    raise ValueError("duplicate transaction ID")
                db.execute("INSERT INTO transactions VALUES (?,?)", (row["transaction_id"], row["record_sha256"]))
                for account_id, amount in ((row["sender_account_id"], -row["amount_minor"]), (row["recipient_account_id"], row["amount_minor"])):
                    account(account_id)
                    db.execute("UPDATE accounts SET net=net+? WHERE id=?", (amount, account_id))
                total_transfer += row["amount_minor"]
                transaction_count += 1
                transfer_kind = row["transfer_kind"] or "unknown"
                bucket = transfer_kinds.setdefault(transfer_kind, {"kind": transfer_kind, "count": 0, "amount_minor": 0, "evidence_refs": []})
                bucket["count"] += 1
                bucket["amount_minor"] += row["amount_minor"]
                if len(bucket["evidence_refs"]) < 2:
                    bucket["evidence_refs"].append({"run_id": run["run_id"], "sequence": row["sequence"], "kind": "transaction"})
            elif kind == "agent_day":
                account(row["agent_id"])
                daily_check(row["agent_id"], row["cash_minor"], record, "agent")
                db.execute("UPDATE accounts SET latest=?,sequence=? WHERE id=?", (row["cash_minor"], row["sequence"], row["agent_id"]))
                db.execute("INSERT OR REPLACE INTO agents VALUES (?,?,?,?,?,?,?,?,?,?)", (row["agent_id"], row["sequence"], row["tick"], row["role"],
                           row["country_id"], row["cash_minor"], row["health"], row["wellbeing"], row["decision_maker"], canonical(agent_facts(payload)).decode()))
                services_snapshots += int("is_student" in payload and "hungry" in payload and "qol_parts_points" in payload)
            elif kind == "nation_day":
                country = row["country_id"]
                if country:
                    # A finite country-day map (5*365 expected), selecting final same-tick revision.
                    countries[(country, row["tick"])] = {"country_id": country, "tick": row["tick"], "sequence": row["sequence"],
                        "metrics": {key: value for key, value in payload.items() if isinstance(value, (int, float, bool)) or value is None},
                        "public_services": {key: payload.get(key) for key in ("plan", "qol_parts", "needed_since", "planning_view", "last_policy_params")},
                        "evidence_ref": {"run_id": run["run_id"], "sequence": row["sequence"], "kind": kind}}
                for escrow in payload.get("escrow_accounts", []):
                    account(escrow["account_id"])
                    # New cargo accounts have zero cash before their funding transfer,
                    # as specified by SocietyTrade.Dispatch. Existing cargo openings
                    # retain their explicitly captured balance.
                    db.execute("UPDATE accounts SET opening=0 WHERE id=? AND opening IS NULL", (escrow["account_id"],))
                    daily_check(escrow["account_id"], escrow["balance_minor"], record, "escrow")
                    db.execute("UPDATE accounts SET latest=?,sequence=?,account_type='escrow' WHERE id=?",
                               (escrow["balance_minor"], row["sequence"], escrow["account_id"]))
            elif kind in {"decision_request", "decision_result"}:
                decision_id = row["decision_id"]
                db.execute("INSERT OR IGNORE INTO decisions(id) VALUES (?)", (decision_id,))
                column = "request" if kind == "decision_request" else "result"
                existing = db.execute(f"SELECT {column} FROM decisions WHERE id=?", (decision_id,)).fetchone()[0]
                if existing is not None:
                    raise ValueError("duplicate decision request/result ID")
                db.execute(f"UPDATE decisions SET {column}=?,{column}_seq=? WHERE id=?", (canonical(payload).decode(), row["sequence"], decision_id))
                fallback_count += int(kind == "decision_result" and row["fallback"])
            if sum(counts.values()) % 10000 == 0:
                db.commit()

        for request_json, result_json in db.execute("SELECT request,result FROM decisions"):
            request = json.loads(request_json) if request_json else None
            result = json.loads(result_json) if result_json else None
            if request is None or not isinstance(request.get("job", {}).get("observation"), dict) or not isinstance(request.get("job", {}).get("candidates"), list):
                missing_context += 1
                continue
            if result:
                index = result.get("selected_option_index")
                options = request["job"]["candidates"]
                action = (result.get("result") or {}).get("action") or {}
                matches = matching_indices(options, action)
                if len(matches) > 1:
                    ambiguous_choices += 1
                if index is None and result.get("accepted") is True:
                    accepted_without_offered_index += 1
                if index is not None:
                    if isinstance(index, bool) or not isinstance(index, int) or not 0 <= index < len(options):
                        choice_errors += 1
                    else:
                        selected = options[index]
                        if not candidate_matches(selected, action):
                            choice_errors += 1
                        elif parameter_contract(selected) == "bounded_integer":
                            bounded_choices += 1
                if "selected_params" in result and canonical(result["selected_params"]) != canonical(action.get("params")):
                    choice_errors += 1
                if "selection_match_count" in result:
                    match_count = result["selection_match_count"]
                    if isinstance(match_count, bool) or not isinstance(match_count, int) or match_count != len(matches):
                        choice_errors += 1
                if request.get("menu_parameter_contract") == "fixed_or_bounded_integer.v1" and index is not None and len(matches) != 1:
                    choice_errors += 1

        # New escrow accounts have an authoritative zero opening balance. This is an
        # engine contract, not guessed from an agent's missing opening endowment.
        db.execute("UPDATE accounts SET opening=0 WHERE opening IS NULL AND account_type='escrow'")
        # Completed escrow legs net back to zero, but cannot infer final escrow balance
        # from ledger alone. Missing snapshots remain unknown.
        missing_final = db.execute("SELECT COUNT(*) FROM accounts WHERE latest IS NULL").fetchone()[0]
        missing_opening = db.execute("SELECT COUNT(*) FROM accounts WHERE opening IS NULL").fetchone()[0]
        mismatch = db.execute("SELECT COUNT(*) FROM accounts WHERE opening IS NOT NULL AND latest IS NOT NULL AND latest != opening+net").fetchone()[0]
        sums = db.execute("SELECT SUM(opening),SUM(latest),SUM(net) FROM accounts").fetchone()
        mismatches = [{"account_id": account_id, "opening_minor": opening, "net_minor": net, "latest_minor": latest}
                      for account_id, opening, net, latest in db.execute("SELECT id,opening,net,latest FROM accounts WHERE opening IS NOT NULL AND latest IS NOT NULL AND latest != opening+net LIMIT 20")]
        agents = [{"agent_id": agent_id, "tick": tick, "role": role, "country_id": country, "cash_minor": cash,
                   "health": health, "wellbeing": wellbeing, "decision_maker": bool(eligible) if eligible is not None else None,
                   "public_services": json.loads(facts) if facts else {},
                   "evidence_ref": {"run_id": run["run_id"], "sequence": sequence, "kind": "agent_day" if tick else "opening_balance"}}
                  for agent_id, sequence, tick, role, country, cash, health, wellbeing, eligible, facts in db.execute("SELECT * FROM agents ORDER BY id LIMIT ?", (max_agents,))]
        agent_count = db.execute("SELECT COUNT(*) FROM agents").fetchone()[0]
        resident_count = db.execute("SELECT COUNT(*) FROM agents WHERE role IN ('citizen','entrepreneur','planner')").fetchone()[0]
        services_summary = {"latest_agent_count": agent_count, "resident_count": resident_count,
                            "denominator": "resident agents with role citizen, entrepreneur or planner"}
        for name, field in (("students", "is_student"), ("graduates", "graduated"), ("job_seekers", "seeking_better_job"), ("hungry", "hungry")):
            known = observed = 0
            for facts, in db.execute("SELECT facts FROM agents WHERE role IN ('citizen','entrepreneur','planner')"):
                value = (json.loads(facts) if facts else {}).get(field)
                if isinstance(value, bool):
                    known += 1
                    observed += int(value)
            services_summary[name] = {"observed_count": observed, "eligible_known_count": known, "missing_count": resident_count - known}
        decision_pending = db.execute("SELECT COUNT(*) FROM decisions WHERE result IS NULL").fetchone()[0]
        check_status = "fail" if mismatch else "unknown" if missing_opening or missing_final else "pass"
        checks = [
            {"name": "manifest_integrity", "status": "pass", "details": {"records": manifest["record_count"]}, "evidence_refs": []},
            {"name": "ledger_account_reconciliation", "status": check_status,
             "details": {"mismatched_accounts": mismatch, "missing_openings": missing_opening, "missing_final_balances": missing_final,
                         "examples": mismatches}, "evidence_refs": []},
            {"name": "daily_account_reconciliation", "status": "fail" if daily_counts["mismatches"] else "unknown" if daily_counts["unknown_openings"] else "pass",
             "details": {**daily_counts, "examples": daily_examples, "basis": "opening plus running ledger net at each snapshot audit sequence"},
             "evidence_refs": daily_evidence + [row["evidence_ref"] for row in daily_examples]},
            {"name": "cash_plus_escrow_conservation", "status": "unknown" if missing_opening or missing_final else "pass" if sums[0] == sums[1] == run.get("initial_cash_minor") and sums[2] == 0 else "fail",
             "details": {"opening_minor": sums[0], "latest_minor": sums[1], "ledger_net_minor": sums[2], "initial_cash_minor": run.get("initial_cash_minor")}, "evidence_refs": []},
            {"name": "offered_choice_index", "status": "fail" if choice_errors else "unknown" if missing_context or decision_pending or accepted_without_offered_index or not counts["decision_request"] else "pass",
             "details": {"invalid_indices": choice_errors, "missing_context": missing_context, "unfinished_requests": decision_pending,
                         "accepted_without_offered_index": accepted_without_offered_index, "request_count": counts["decision_request"],
                         "result_count": counts["decision_result"], "bounded_integer_choices": bounded_choices, "ambiguous_matches": ambiguous_choices,
                         "matching_rule": "exact fixed JSON params and inclusive int32 bounded values; original menu unchanged"}, "evidence_refs": []},
            {"name": "daily_flow_reconciliation", "status": "unknown", "details": {"reason": "Daily aggregate flow records are not part of audit.v1; individual transfers retained."}, "evidence_refs": []},
        ]
        db.close()
    missing = list(manifest["completeness"].get("missing_data", []))
    if missing_final:
        missing.append("final_account_balances")
    if missing_opening:
        missing.append("opening_account_balances")
    if missing_context:
        missing.append("decision_context")
    return {"schema_version": "faultline.analytics-report.v1", "analysis_version": ANALYSIS_VERSION,
            "run_id": run["run_id"], "through_revision": run["through_revision"], "through_sequence": run["through_sequence"],
            "source_manifest_sha256": file_sha256(manifest_path), "generated_at": dt.datetime.now(dt.timezone.utc).isoformat(),
            "completeness": {"snapshot_complete": True, "run_complete": run["status"] == "completed", "run_status": run["status"],
                             "missing_data": missing, "metadata_unknown": manifest["completeness"].get("metadata_unknown", []),
                             "capture_complete": manifest["completeness"].get("capture_complete"),
                             "synthetic": manifest.get("provenance", {}).get("synthetic"), "source_mode": run.get("source")},
            "summary": {"record_count": manifest["record_count"], "counts_by_kind": dict(counts), "transaction_count": transaction_count,
                        "transfer_minor": total_transfer, "agent_count": agent_count, "decision_result_count": counts["decision_result"],
                        "fallback_count": fallback_count, "agent_preview_limit": max_agents},
            "checks": checks, "agents": agents, "countries": [countries[key] for key in sorted(countries)],
            "government_comparison": {"status": "insufficient_evidence", "reason": "A single run cannot establish matched-seed government comparisons.", "sample_count": 1},
            "public_services": {"metadata": feature_metadata, "snapshot_summary": services_summary,
                "coverage": {"agent_snapshot_count": counts["agent_day"], "agent_snapshots_with_public_services_fields": services_snapshots,
                             "status": "pass" if services_snapshots == counts["agent_day"] and services_snapshots else "partial" if services_snapshots else "unknown"},
                "transfers_by_kind": [transfer_kinds[key] for key in sorted(transfer_kinds)]},
            "evidence_refs": evidence}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest")
    parser.add_argument("output")
    parser.add_argument("--max-agents", type=int, default=100)
    args = parser.parse_args()
    if not 0 <= args.max_agents <= 50000:
        parser.error("--max-agents must be 0..50000")
    report = build_report(args.manifest, max_agents=args.max_agents)
    Path(args.output).write_bytes(canonical(report) + b"\n")
    print(f"Wrote report for {report['run_id']}; {len(report['checks'])} explicit checks")


if __name__ == "__main__":
    main()
