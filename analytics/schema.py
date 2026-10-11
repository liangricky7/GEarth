"""Plan additive nullable projections; never update existing evidence."""


def nullable_additions(existing, incoming):
    """Input maps contain field names and canonical Spark simpleString types."""
    additions = {}
    for name, datatype in incoming.items():
        if name in existing and existing[name] != datatype:
            raise ValueError("Unsafe type change in " + name)
        if name not in existing:
            additions[name] = datatype
    return additions



def economy_backfill_fields(table):
    """Allow only columns introduced by this migration, never old/raw identity.

    Type 'json' means key-presence-aware extraction: missing SQL null, explicit
    captured JSON null literal 'null'. Old projection semantics stay unchanged.
    """
    from .transform import (AGENT_TYPES, COUNTRY_TYPES, REQUIRED_AGENT_V4, REQUIRED_COUNTRY_V4,
        AGENT_JSON_FACTS, COUNTRY_JSON_FACTS, LEGACY_AGENT_JSON_FACTS, LEGACY_COUNTRY_JSON_FACTS)
    if table == "workspace.gearth_silver.agent_daily":
        scalars = (*REQUIRED_AGENT_V4, "estate_unreported_payout_minor", "week_disposable_income_minor", "week_work_days")
        types, json_fields, legacy = AGENT_TYPES, AGENT_JSON_FACTS, LEGACY_AGENT_JSON_FACTS
    elif table == "workspace.gearth_silver.country_daily":
        scalars = (*REQUIRED_COUNTRY_V4, "tax_changed_tick", "living_cost_at_review", "crisis_until_tick", "crisis_good")
        types, json_fields, legacy = COUNTRY_TYPES, COUNTRY_JSON_FACTS, LEGACY_COUNTRY_JSON_FACTS
    else:
        return {}
    return {**{name: (name, types[name]) for name in scalars if name in types},
        **{name + "_json": (name, "json") for name in json_fields if name not in legacy}}
