from agents.providers import load_providers


def compute_environment(config, mode="auto", capacity=None, world=True):
    if mode == "auto":
        mode = (
            "hybrid"
            if config.get("AMD_ENABLED", "false").lower() == "true"
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
            "AMD_ENABLED": str(mode in {"amd", "hybrid"}).lower(),
        }
    )
    environment.setdefault("SPARK_A_BASE_URL", "http://127.0.0.1:18001/v1")
    environment.setdefault("SPARK_A_CAPACITY", "64" if world else "4")
    environment.setdefault("SPARK_B_CAPACITY", "64" if world else "4")
    ceiling = capacity if capacity is not None else (384 if world else 4)
    providers = load_providers(environment, capacity=ceiling)
    for provider in providers:
        prefix = provider.name.upper()
        environment.update(
            {
                f"{prefix}_BASE_URL": provider.base_url,
                f"{prefix}_MODEL": provider.model,
                f"{prefix}_CAPACITY": str(provider.capacity),
                f"{prefix}_API_KEY": provider.api_key,
            }
        )
    effective_capacity = min(ceiling, sum(provider.capacity for provider in providers))
    return mode, environment, providers, effective_capacity
