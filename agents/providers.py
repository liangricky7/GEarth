import os
from dataclasses import dataclass, field
from ipaddress import ip_address, ip_network
from urllib.parse import urlsplit


@dataclass(frozen=True)
class ModelProvider:
    name: str
    model: str
    base_url: str
    capacity: int
    api_key: str = field(repr=False)


def tailscale_http_allowed(address, environment, prefix):
    configured_host = environment.get(f"{prefix}_TAILSCALE_HTTP_HOST", "")
    if not configured_host or configured_host != address.hostname:
        return False
    try:
        return ip_address(configured_host) in ip_network("100.64.0.0/10")
    except ValueError:
        return False


def load_providers(environment=None, capacity=4):
    environment = os.environ if environment is None else environment
    providers = []
    for name, prefix, default_model, default_url, default_capacity in (
        (
            "spark_a",
            "SPARK_A",
            "openai/gpt-oss-120b",
            "http://127.0.0.1:18001/v1",
            capacity,
        ),
        ("spark_b", "SPARK_B", "openai/gpt-oss-120b", "", capacity),
        ("amd", "AMD", "Qwen/Qwen3-4B", "", 1),
    ):
        if (
            environment.get(
                f"{prefix}_ENABLED", "true" if name == "spark_a" else "false"
            ).lower()
            != "true"
        ):
            continue
        base_url = environment.get(f"{prefix}_BASE_URL", default_url).rstrip("/")
        address = urlsplit(base_url)
        if (
            address.scheme not in {"http", "https"}
            or not address.hostname
            or address.username
            or address.password
            or address.query
            or address.fragment
            or (
                address.scheme == "http"
                and address.hostname not in {"127.0.0.1", "localhost", "::1"}
                and not tailscale_http_allowed(address, environment, prefix)
            )
        ):
            raise ValueError(
                f"{prefix}_BASE_URL must use HTTPS, loopback HTTP, or explicitly pinned Tailscale HTTP without embedded credentials"
            )
        api_key = environment.get(f"{prefix}_API_KEY", "")
        if not api_key and (
            name != "spark_a"
            or address.hostname not in {"127.0.0.1", "localhost", "::1"}
        ):
            raise ValueError(f"{prefix}_API_KEY is required")
        limit = int(environment.get(f"{prefix}_CAPACITY", default_capacity))
        if not 1 <= limit <= 128:
            raise ValueError(f"{prefix}_CAPACITY must be between 1 and 128")
        providers.append(
            ModelProvider(
                name,
                environment.get(f"{prefix}_MODEL", default_model),
                base_url,
                min(limit, capacity),
                api_key,
            )
        )
    if not providers:
        raise ValueError("Enable at least one model provider")
    return providers


class ProviderPool:
    def __init__(self, providers, cooldown_seconds=30):
        self.providers = providers
        self.cooldown_seconds = cooldown_seconds
        self.active = {provider.name: 0 for provider in providers}
        self.completed = {provider.name: 0 for provider in providers}
        self.failures = {provider.name: 0 for provider in providers}
        self.unavailable_until = {provider.name: 0.0 for provider in providers}
        self.cursor = 0

    def acquire(self, now):
        candidates = [
            (index, provider)
            for index, provider in enumerate(self.providers)
            if self.active[provider.name] < provider.capacity
            and self.unavailable_until[provider.name] <= now
        ]
        if not candidates:
            return None
        index, provider = min(
            candidates,
            key=lambda item: (
                self.active[item[1].name] / item[1].capacity,
                (item[0] - self.cursor) % len(self.providers),
            ),
        )
        self.cursor = (index + 1) % len(self.providers)
        self.active[provider.name] += 1
        return provider

    def release(self, provider, now, failed=False, unavailable=False, cancelled=False):
        self.active[provider.name] -= 1
        if failed:
            self.failures[provider.name] += 1
        elif not cancelled:
            self.completed[provider.name] += 1
        if unavailable:
            self.unavailable_until[provider.name] = now + self.cooldown_seconds

    def snapshot(self, now):
        return [
            {
                "name": provider.name,
                "model": provider.model,
                "capacity": provider.capacity,
                "active_requests": self.active[provider.name],
                "completed_requests": self.completed[provider.name],
                "failed_requests": self.failures[provider.name],
                "cooldown_seconds": round(
                    max(0, self.unavailable_until[provider.name] - now), 1
                ),
            }
            for provider in self.providers
        ]
