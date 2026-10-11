import pytest

from ops.compute_config import compute_environment, spark_a_forwards


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


def test_launcher_preserves_explicit_tailscale_relay_pin():
    config = {
        **configuration(),
        "SPARK_B_BASE_URL": "http://100.119.198.81:18002/v1",
        "SPARK_B_TAILSCALE_HTTP_HOST": "100.119.198.81",
    }
    _, environment, providers, capacity = compute_environment(config)
    assert environment["SPARK_B_TAILSCALE_HTTP_HOST"] == "100.119.198.81"
    assert providers[1].base_url == config["SPARK_B_BASE_URL"]
    assert capacity == 129


def test_two_models_on_one_spark_preserve_role_assignments_and_forward_ports():
    config = {
        "SPARK_A_MODEL": "Qwen/Qwen3-8B",
        "SPARK_A_DECISION_GROUP": "institution",
        "SPARK_A_API_KEY": "leaders-private",
        "SPARK_A_CAPACITY": "4",
        "SPARK_A_CITIZEN_ENABLED": "true",
        "SPARK_A_CITIZEN_API_KEY": "citizens-private",
        "SPARK_A_CITIZEN_CAPACITY": "8",
    }
    _, environment, providers, capacity = compute_environment(config)
    assert [item.decision_group for item in providers] == ["institution", "citizen"]
    assert [item.model for item in providers] == ["Qwen/Qwen3-8B", "Qwen/Qwen3-4B"]
    assert capacity == 12
    assert environment["SPARK_A_CITIZEN_DECISION_GROUP"] == "citizen"
    assert spark_a_forwards(providers) == [(18001, 8000), (18003, 8001)]
    _, _, amd_providers, _ = compute_environment({**configuration(), **config}, "amd")
    assert [item.name for item in amd_providers] == ["amd"]
    assert spark_a_forwards(amd_providers) == []


def test_world_rejects_partial_role_configuration():
    with pytest.raises(ValueError, match="citizen decisions"):
        compute_environment({"SPARK_A_DECISION_GROUP": "institution"})


def test_custom_local_endpoint_is_not_replaced_by_managed_forward():
    _, _, providers, _ = compute_environment(
        {"SPARK_A_BASE_URL": "http://localhost:19000/v1"}
    )
    assert spark_a_forwards(providers) == []


def test_all_three_nodes_share_model_pair_with_independent_role_slots():
    config = {
        "SPARK_A_ENABLED": "false",
        "SPARK_B_ENABLED": "false",
        "AMD_ENABLED": "false",
    }
    for name, url in [
        ("SPARK_A", "http://127.0.0.1:18001/v1"),
        ("SPARK_B", "http://127.0.0.1:18002/v1"),
        ("AMD", "https://amd.example/v1"),
    ]:
        for suffix, model, group in [
            ("", "Qwen/Qwen3-8B", "institution"),
            ("_CITIZEN", "Qwen/Qwen3-4B", "citizen"),
        ]:
            prefix = name + suffix
            config.update(
                {
                    f"{prefix}_ENABLED": "true",
                    f"{prefix}_MODEL": model,
                    f"{prefix}_DECISION_GROUP": group,
                    f"{prefix}_BASE_URL": url,
                    f"{prefix}_API_KEY": "private",
                    f"{prefix}_CAPACITY": "1",
                }
            )
    _, environment, providers, capacity = compute_environment(config)
    assert capacity == 6
    assert len(providers) == 6
    assert all(
        provider.model
        == (
            "Qwen/Qwen3-4B" if provider.decision_group == "citizen" else "Qwen/Qwen3-8B"
        )
        for provider in providers
    )
    _, amd_environment, amd_providers, capacity = compute_environment(config, "amd")
    assert len(amd_providers) == capacity == 2
    assert not any(provider.name.startswith("spark") for provider in amd_providers)
    assert amd_environment["SPARK_A_CITIZEN_ENABLED"] == "false"
