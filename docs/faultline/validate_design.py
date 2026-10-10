"""Validate Faultline reference documents and fixtures, not application behavior.

Uses the controlled JSON Schema subset present in these two reference schemas.
This is not a general-purpose JSON Schema implementation or production validator.
"""

from pathlib import Path
import heapq
import json
import re
from datetime import datetime
from zoneinfo import ZoneInfo

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def load(path):
    return json.loads((HERE / path).read_text())


def valid(value, schema):
    types = {
        "object": lambda x: isinstance(x, dict),
        "array": lambda x: isinstance(x, list),
        "string": lambda x: isinstance(x, str),
        "integer": lambda x: isinstance(x, int) and not isinstance(x, bool),
        "boolean": lambda x: isinstance(x, bool),
        "null": lambda x: x is None,
    }
    kind = schema.get("type")
    if kind and not any(types[k](value) for k in ([kind] if isinstance(kind, str) else kind)):
        return False
    if "const" in schema and value != schema["const"]:
        return False
    if "enum" in schema and value not in schema["enum"]:
        return False
    if isinstance(value, dict):
        if not set(schema.get("required", [])).issubset(value):
            return False
        properties = schema.get("properties", {})
        if schema.get("additionalProperties") is False and set(value) - set(properties):
            return False
        if not all(valid(value[k], spec) for k, spec in properties.items() if k in value):
            return False
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0) or len(value) > schema.get("maxLength", float("inf")):
            return False
    if isinstance(value, int) and not isinstance(value, bool):
        if value < schema.get("minimum", float("-inf")):
            return False
    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0) or len(value) > schema.get("maxItems", float("inf")):
            return False
        if schema.get("uniqueItems") and len({json.dumps(x, sort_keys=True) for x in value}) != len(value):
            return False
        if "items" in schema and not all(valid(x, schema["items"]) for x in value):
            return False
    if "oneOf" in schema and sum(valid(value, branch) for branch in schema["oneOf"]) != 1:
        return False
    return True


def shortest(routes, origin, destination, weight):
    queue = [(0, origin, [])]
    best = {}
    while queue:
        cost, node, path = heapq.heappop(queue)
        if node in best:
            continue
        best[node] = cost
        if node == destination:
            return cost, path
        for edge in routes:
            if edge["open"] and edge["from"] == node:
                heapq.heappush(queue, (cost + edge[weight], edge["to"], path + [edge["id"]]))
    raise AssertionError("No route")


def main():
    json_paths = list(HERE.rglob("*.json"))
    for path in json_paths:
        json.loads(path.read_text())
    action = load("contracts/action.schema.json")
    event = load("contracts/event.schema.json")
    assert valid(load("fixtures/agent-action.json"), action)
    assert not valid(load("fixtures/invalid-negative-loan.json"), action)
    forbidden = load("fixtures/forbidden-role-action.json")
    assert valid(forbidden["proposal"], action)
    assert "_citizen_" in forbidden["agent_id"] and forbidden["proposal"]["action_type"] == "set_border"
    assert forbidden["expected_rejection"] == "ROLE_FORBIDDEN"
    assert valid(load("fixtures/border-event.json"), event)

    timeline = [json.loads(line) for line in (HERE / "fixtures/timeline.ndjson").read_text().splitlines()]
    seen = set()
    for expected_seq, row in enumerate(timeline, 1):
        assert valid(row, event) and row["seq"] == expected_seq
        assert set(row["cause_ids"]).issubset(seen)
        seen.add(row["event_id"])
    assert timeline[1] == load("fixtures/border-event.json")

    scenario = load("scenario.demo.json")
    population = scenario["population"]
    assert sum(population.values()) == 500
    assert population == {"government": 5, "business": 10, "bank": 5, "media": 5, "citizen": 475}
    countries = {c["id"] for c in scenario["countries"]}
    locations = {p["id"] for p in scenario["locations"]}
    assert len(countries) == 5 and len(locations) == 15
    assert all(p["country_id"] in countries for p in scenario["locations"])
    edges = {r["id"]: r for r in scenario["routes"]}
    assert len(edges) == len(scenario["routes"])
    for route in edges.values():
        assert route["from"] in locations and route["to"] in locations
        assert route["controller_country_id"] in countries
        assert route["travel_ticks"] > 0 and route["cost_minor"] >= 0 and 0 <= route["risk_score"] <= 1
    for disruption in scenario["external_events"]:
        assert disruption["edge_id"] in edges
        assert 0 <= disruption["tick"] < disruption["expires_tick"] < scenario["duration_ticks"]
    deadline = datetime.fromisoformat(scenario["deadline_local"]).astimezone(ZoneInfo("America/New_York"))
    assert deadline.weekday() == 6 and deadline.hour == 10
    assert deadline.date().isoformat() == "2026-10-11"

    trade = scenario["seeded_contract"]
    assert trade["quantity"] * trade["unit_price_minor"] == trade["escrow_amount_minor"]
    assert trade["quantity"] == scenario["city_demand"]["medicine_quantity"]
    routes = scenario["routes"]
    fastest, path = shortest(routes, trade["origin"], trade["destination"], "travel_ticks")
    assert fastest == 8 and "r_direct" in path
    closed_routes = [dict(r, open=False) if r["id"] == "r_direct" else r for r in routes]
    detour, path = shortest(closed_routes, trade["origin"], trade["destination"], "travel_ticks")
    assert detour == 12 and "r_direct" not in path
    cheapest, cheap_path = shortest(closed_routes, trade["origin"], trade["destination"], "cost_minor")
    assert cheapest == 600 and "r_land_a" in cheap_path
    assert sum(edges[e]["cost_minor"] for e in path) == 1700
    assert sum(edges[e]["travel_ticks"] for e in cheap_path) == 14
    effect = timeline[3]["payload"]
    assert sum(edges[e]["cost_minor"] for e in effect["new_edge_ids"]) == effect["planned_remaining_cost_minor"]
    assert sum(edges[e]["travel_ticks"] for e in effect["new_edge_ids"]) == effect["planned_remaining_travel_ticks"]

    currency = scenario["currency"]
    cash = currency["initial_cash_minor_per_role"]
    total_cash = sum(population[role] * amount for role, amount in cash.items())
    total_cash += currency["initial_cash_overrides"][trade["buyer_id"]] - cash["business"]
    assert total_cash == 2137000
    buyer_cash = currency["initial_cash_overrides"][trade["buyer_id"]] - trade["initial_deposit_minor"] + trade["loan_principal_minor"] - trade["escrow_amount_minor"]
    bank_cash = cash["bank"] + trade["initial_deposit_minor"] - trade["loan_principal_minor"]
    assert buyer_cash == 1500 and bank_cash == 90500
    assert buyer_cash < 1700 and buyer_cash >= 600
    world = load("fixtures/world-snapshot.json")
    assert world["run"]["mode"] == "FIXTURE"
    assert world["agents_summary"]["unique_model_deciding"] == 0
    bank = world["banks"][0]
    assert bank["cash_minor"] == bank_cash
    assert bank["assets_minor"] == bank["cash_minor"] + bank["principal_receivable_minor"]
    assert bank["equity_minor"] == bank["assets_minor"] - bank["deposits_minor"]
    assert all(n["accepted_model_decisions"] == 0 for n in world["compute"])

    manifest = load("run-manifest.template.json")
    assert manifest["status"] == "template_not_measured"
    assert manifest["population_actual"] is None and not manifest["model_workers"][2]["enabled"]
    docs = [ROOT / "README.md", ROOT / "faultline-design.md", *HERE.rglob("*.md")]
    for doc in docs:
        for raw in re.findall(r"\]\(([^\n)]+)\)", doc.read_text()):
            target = raw.strip("<>")
            if target.startswith(("https://", "http://", "#")):
                continue
            target = target.split("#", 1)[0]
            assert (doc.parent / target).exists(), f"Broken link in {doc.name}: {target}"
    print(f"PASS: {len(json_paths)} JSON artifacts; valid/invalid action fixtures; connected event timeline; 500-agent counts; graph references; route alternatives; seeded accounting; Sunday deadline; {len(docs)} Markdown files' local links.")
    print("Not verified: model serving, agent coverage/latency, application behavior, Neon/Solana/Databricks integration, AMD allocation.")


if __name__ == "__main__":
    main()
