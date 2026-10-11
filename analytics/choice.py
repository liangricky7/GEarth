"""Match original fixed/bounded-integer menus without rewriting offered options."""
from .export import canonical

INT32_MIN = -(2 ** 31)
INT32_MAX = 2 ** 31 - 1


def bounded_spec(value):
    return isinstance(value, dict) and "min" in value and "max" in value


def int32(value):
    return isinstance(value, int) and not isinstance(value, bool) and INT32_MIN <= value <= INT32_MAX


def candidate_matches(candidate, action):
    """Mirror SocietyActions.Matches; fixed JSON values keep their exact types."""
    if not isinstance(candidate, dict) or not isinstance(action, dict) or candidate.get("action_type") != action.get("action_type"):
        return False
    offered, chosen = candidate.get("params"), action.get("params")
    if not isinstance(offered, dict) or not isinstance(chosen, dict) or offered.keys() != chosen.keys():
        return False
    for key, value in offered.items():
        actual = chosen[key]
        if bounded_spec(value):
            if not int32(value["min"]) or not int32(value["max"]) or not int32(actual) or not value["min"] <= actual <= value["max"]:
                return False
        elif canonical(value) != canonical(actual):
            return False
    return True


def matching_indices(options, action):
    return [index for index, option in enumerate(options) if candidate_matches(option, action)] if isinstance(options, list) else []


def parameter_contract(candidate):
    params = candidate.get("params") if isinstance(candidate, dict) else None
    return "bounded_integer" if isinstance(params, dict) and any(bounded_spec(value) for value in params.values()) else "fixed"
