import threading
import time
from concurrent.futures import ThreadPoolExecutor

from ops import share_site


def test_overlapping_reconnections_do_not_replace_each_others_tunnel(monkeypatch, tmp_path):
    monkeypatch.setattr(share_site, "STATE", tmp_path / "share-site.json")
    gate = threading.Lock()
    active = 0
    peak = 0

    def reconnect(client, url, key):
        nonlocal active, peak
        with gate:
            active += 1
            peak = max(peak, active)
        time.sleep(0.03)
        with gate:
            active -= 1
        return "connected"

    monkeypatch.setattr(share_site, "reconnect_locked", reconnect)
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda _: share_site.reconnect(None, "url", "key"), range(2)))
    assert results == ["connected", "connected"]
    assert peak == 1
