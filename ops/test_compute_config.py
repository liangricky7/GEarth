import pytest

from ops.compute_config import compute_environment


def configuration():
    return {
        "SPARK_A_API_KEY": "first-private",
        "SPARK_A_CAPACITY": "64",
        "SPARK_B_ENABLED": "true",
        "SPARK_B_BASE_URL": "http://127.0.0.1:18002/v1",
        "SPARK_B_API_KEY": "second-private",
        "SPARK_B_CAPACITY": "64",
        "AMD_ENABLED": "true",
        "AMD_BASE_URL": "https://amd.example/v1",
        "AMD_API_KEY": "amd-private",
        "AMD_CAPACITY": "1",
    }


def test_auto_sums_three_configured_backends_without_disabling_spark_b():
    mode, environment, providers, capacity = compute_environment(configuration())
    assert mode == "hybrid"
    assert capacity == 129
    assert [provider.name for provider in providers] == ["spark_a", "spark_b", "amd"]
    assert environment["SPARK_B_ENABLED"] == "true"
    assert providers[1].api_key == "second-private"


def test_explicit_capacity_bounds_total_and_each_backend():
    _, _, providers, capacity = compute_environment(configuration(), capacity=8)
    assert capacity == 8
    assert [provider.capacity for provider in providers] == [8, 8, 1]


def test_amd_only_never_requires_spark_configuration():
    config = configuration()
    config.pop("SPARK_B_API_KEY")
    _, environment, providers, capacity = compute_environment(config, "amd")
    assert [provider.name for provider in providers] == ["amd"]
    assert capacity == 1
    assert environment["SPARK_A_ENABLED"] == "false"
    assert environment["SPARK_B_ENABLED"] == "false"


def test_missing_second_spark_credentials_fail_before_launching_processes():
    config = configuration()
    config.pop("SPARK_B_API_KEY")
    with pytest.raises(ValueError, match="SPARK_B_API_KEY"):
        compute_environment(config)


def test_spark_mode_honors_disabled_first_spark_and_excludes_amd():
    _, _, providers, capacity = compute_environment(
        {**configuration(), "SPARK_A_ENABLED": "false"}, "spark"
    )
    assert [provider.name for provider in providers] == ["spark_b"]
    assert capacity == 64
