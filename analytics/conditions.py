"""Explicit experimental conditions; legacy omissions remain unknown."""
import hashlib
import re
from .export import canonical


def comparison_conditions(metadata):
    unknown = []
    if not isinstance(metadata.get("seed"), int) or isinstance(metadata.get("seed"), bool):
        unknown.append("seed_pairing_key")
    for key in ("mode", "tick_limit", "initial_population", "engine_commit", "worker_capacity", "tick_interval_ms",
                "capture_contract_version", "features", "active_features", "banking_paused", "economy_configuration"):
        if metadata.get(key) is None:
            unknown.append(key)
    if not isinstance(metadata.get("engine_commit"), str) or not re.fullmatch(r"[0-9a-fA-F]{40}", metadata["engine_commit"]):
        unknown.append("engine_commit_valid_sha")
    features = metadata.get("features")
    if not isinstance(features, dict) or any(not isinstance(features.get(key), str) or not features[key] for key in ("public_services", "bounded_integer_choices")):
        unknown.append("feature_versions")
    capture_version = metadata.get("capture_contract_version")
    if capture_version in ("gearth.economy_capture.v2", "gearth.economy_capture.v3", "gearth.economy_capture.v4"):
        war = capture_version in ("gearth.economy_capture.v3", "gearth.economy_capture.v4")
        if not isinstance(features, dict) or any(not isinstance(features.get(key), str) or not features[key]
                for key in ("infrastructure", "part_time_school", "wage_arrears", "price_response", "currency_trade") + (("war_diplomacy",) if war else ())):
            unknown.append("economy_v2_feature_versions")
        expanded = metadata.get("economy_configuration")
        if not isinstance(expanded, dict) or any(not isinstance(expanded.get(key), dict) or not expanded[key]
                for key in ("labor", "infrastructure", "currency_trade") + (("war_diplomacy",) if war else ())):
            unknown.append("economy_v2_configuration_sections")
        if not isinstance(expanded, dict) or not isinstance(expanded.get("school"), dict) or expanded["school"].get("part_time_course_days") is None:
            unknown.append("part_time_course_configuration")
    if capture_version == "gearth.economy_capture.v4":
        if not isinstance(features, dict) or any(not isinstance(features.get(key), str) or not features[key]
                for key in ("nutrition_recovery", "clinic_feedback", "loan_provisions", "infrastructure_repairs")):
            unknown.append("economy_v4_feature_versions")
        expanded = metadata.get("economy_configuration")
        if not isinstance(expanded, dict) or any(not isinstance(expanded.get(key), dict) or not expanded[key]
                for key in ("nutrition_recovery", "clinic_feedback", "loan_provisions")):
            unknown.append("economy_v4_configuration_sections")
        if not isinstance(expanded, dict) or not isinstance(expanded.get("health"), dict) or expanded["health"].get("starvation_days") is None:
            unknown.append("starvation_days_configuration")
        infrastructure = expanded.get("infrastructure") if isinstance(expanded, dict) else None
        if not isinstance(infrastructure, dict) or any(infrastructure.get(key) is None for key in (
                "maintenance_basis", "repair_max_condition_per_week", "repair_builders_required",
                "repair_cost_fraction_per_condition", "repair_materials_basis")):
            unknown.append("infrastructure_repairs_configuration")
    active = metadata.get("active_features")
    if not isinstance(active, list) or not active or any(not isinstance(value, str) or not value for value in active):
        unknown.append("active_feature_set")
    if not isinstance(metadata.get("banking_paused"), bool):
        unknown.append("banking_state")
    config = metadata.get("economy_configuration")
    if not isinstance(config, dict) or any(not isinstance(config.get(key), dict) or not config[key] for key in ("school", "health", "quality_of_life", "planning", "banking", "accounting")):
        unknown.append("economy_configuration_sections")
    if isinstance(config, dict) and isinstance(config.get("banking"), dict) and config["banking"].get("paused") != metadata.get("banking_paused"):
        unknown.append("banking_flag_configuration_agreement")
    providers = metadata.get("compute_providers")
    if metadata.get("mode") != "RULE" and (not isinstance(providers, list) or not providers or any(not isinstance(value, dict) or not isinstance(value.get("model"), str) or not value["model"].strip() for value in providers)):
        unknown.append("model_assignment")
    facts = {key: metadata.get(key) for key in ("mode", "tick_limit", "initial_population", "engine_commit", "worker_capacity",
        "tick_interval_ms", "capture_contract_version", "features", "banking_paused", "economy_configuration", "compute_providers")}
    facts["active_features"] = sorted(set(active)) if isinstance(active, list) and all(isinstance(value, str) for value in active) else active
    # Seed is the pairing key; rotation is the treatment, not a matching condition.
    return {"known": not unknown, "unknown": sorted(set(unknown)), "facts": facts,
            "signature": hashlib.sha256(canonical(facts)).hexdigest()}
