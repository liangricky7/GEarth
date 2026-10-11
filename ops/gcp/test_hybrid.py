import io
import json
from urllib.error import HTTPError

import pytest

from ops.gcp.check_compute import check_http_catalog
from ops.gcp.wait_for_experiment import wait_for_boundary


@pytest.mark.parametrize("status", [401, 200, 403])
def test_amd_readiness_requires_model_and_application_auth(monkeypatch, status):
    calls = []

    def open_url(request, timeout):
        calls.append(request)
        if len(calls) == 1:
            assert request.headers["Authorization"] == "Bearer private-test-key"
            return io.BytesIO(json.dumps({"data": [{"id": "model"}]}).encode())
        assert isinstance(request, str)
        if status != 200:
            raise HTTPError(request, status, "test", {}, None)
        return io.BytesIO(b"{}")

    monkeypatch.setattr("urllib.request.urlopen", open_url)
    config = {"AMD_BASE_URL": "https://example.test/v1", "AMD_API_KEY": "private-test-key", "AMD_MODEL": "model"}
    if status == 401:
        check_http_catalog("AMD", config)
    else:
        with pytest.raises(RuntimeError):
            check_http_catalog("AMD", config)
    assert len(calls) == 2


def test_activation_waits_for_completed_manifest_without_interrupting_world(tmp_path):
    (tmp_path / "state.json").write_text(json.dumps({"next_seed": 101}))
    manifest = tmp_path / "experiment-100.json"
    manifest.write_text(json.dumps({"status": "running"}))
    stops = []

    def pause(_):
        assert not stops
        manifest.write_text(json.dumps({"status": "completed"}))

    wait_for_boundary(tmp_path, lambda: True, lambda: stops.append(True), pause)
    assert stops == [True]
