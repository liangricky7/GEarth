from agents.providers import load_providers


def spark_a_forwards(providers):
    endpoints = {
        "spark_a": ("http://127.0.0.1:18001/v1", 18001, 8000),
        "spark_a_citizen": ("http://127.0.0.1:18003/v1", 18003, 8001),
    }
    return [
        (endpoints[provider.name][1], endpoints[provider.name][2])
        for provider in providers
        if provider.name in endpoints
        and provider.base_url == endpoints[provider.name][0]
    ]


def compute_environment(config, mode="auto", capacity=None, world=True):
    if mode == "auto":
        mode = (
            "hybrid"
            if any(
                config.get(key, "false").lower() == "true"
                for key in ("AMD_ENABLED", "AMD_CITIZEN_ENABLED")
            )
            else "spark"
        )
    use_sparks = mode in {"spark", "hybrid"}
    environment = {
        key: value
        for key, value in config.items()
        if key.startswith(("SPARK_A_", "SPARK_B_", "AMD_"))
    }
    environment.update(
        {
            "SPARK_A_ENABLED": str(
                use_sparks and config.get("SPARK_A_ENABLED", "true").lower() == "true"
            ).lower(),
            "SPARK_B_ENABLED": str(
                use_sparks and config.get("SPARK_B_ENABLED", "false").lower() == "true"
            ).lower(),
            "SPARK_A_CITIZEN_ENABLED": str(
                use_sparks
                and config.get("SPARK_A_CITIZEN_ENABLED", "false").lower() == "true"
            ).lower(),
            "SPARK_B_CITIZEN_ENABLED": str(
                use_sparks
                and config.get("SPARK_B_CITIZEN_ENABLED", "false").lower() == "true"
            ).lower(),
            "AMD_ENABLED": str(
                mode in {"amd", "hybrid"}
                and config.get("AMD_ENABLED", "true").lower() == "true"
            ).lower(),
            "AMD_CITIZEN_ENABLED": str(
                mode in {"amd", "hybrid"}
                and config.get("AMD_CITIZEN_ENABLED", "false").lower() == "true"
            ).lower(),
        }
    )
    environment.setdefault("SPARK_A_BASE_URL", "http://127.0.0.1:18001/v1")
    environment.setdefault("SPARK_A_CAPACITY", "64" if world else "4")
    environment.setdefault("SPARK_B_CAPACITY", "64" if world else "4")
    environment.setdefault("SPARK_A_CITIZEN_CAPACITY", "64" if world else "4")
    environment.setdefault("SPARK_B_CITIZEN_CAPACITY", "64" if world else "4")
    ceiling = capacity if capacity is not None else (384 if world else 4)
    providers = load_providers(environment, capacity=ceiling)
    if world and any(provider.decision_group != "all" for provider in providers):
        for group in ("citizen", "institution"):
            if not any(provider.serves(group) for provider in providers):
                raise ValueError(
                    f"World simulation requires a provider for {group} decisions"
                )
    for provider in providers:
        prefix = provider.name.upper()
        environment.update(
            {
                f"{prefix}_BASE_URL": provider.base_url,
                f"{prefix}_MODEL": provider.model,
                f"{prefix}_CAPACITY": str(provider.capacity),
                f"{prefix}_API_KEY": provider.api_key,
                f"{prefix}_DECISION_GROUP": provider.decision_group,
            }
        )
    effective_capacity = min(ceiling, sum(provider.capacity for provider in providers))
    return mode, environment, providers, effective_capacity
