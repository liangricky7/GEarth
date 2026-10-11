import hashlib
import json
from pathlib import Path

import pytest

from ops.export_renderer import CONTRACT, export_run


def test_kenny_fixture_matches_the_published_frame_schema():
    import jsonschema
    root = Path(__file__).resolve().parents[1] / "renderer"
    schema = json.loads((root / "frame.schema.json").read_text())
    frame = json.loads((root / "fixtures/week-1.json").read_text())
    jsonschema.Draft202012Validator(schema).validate(frame)
    assert frame["fixture"]["synthetic"]
    assert not frame["fixture"]["complete_population"]


def fake_api(gap=False):
    def envelope(**kwargs):
        return {"schema_version": CONTRACT, "run_id": "run", **kwargs}
    heads = [{"run_id": "run", "revision": 1, "tick": 0, "through_event_sequence": 1},
             {"run_id": "run", "revision": 3, "tick": 7, "through_event_sequence": 3},
             {"run_id": "run", "revision": 5, "tick": 14, "through_event_sequence": 5}]
    def get(path):
        if path == "/manifest":
            return envelope(checkpoint_revision=3, status="running", captured_from_opening=True)
        if path.startswith("/frames?"):
            return envelope(frames=heads, has_more=False)
        if path.startswith("/frames/"):
            head = next(h for h in heads if h["revision"] == int(path.split("/")[-1]))
            return envelope(**{k: v for k, v in head.items() if k != "run_id"}, snapshot={"agents": []})
        from urllib.parse import parse_qs, urlsplit
        params = parse_qs(urlsplit(path).query)
        after = int(params["after_seq"][0]); through = int(params["through_seq"][0])
        seq = after + (2 if gap else 1)
        return envelope(events=[{"run_id": "run", "seq": seq}], next_after_seq=seq,
                        through_event_sequence=through)
    return get


def test_export_pins_live_revision_hashes_files_and_finishes_manifest_last(tmp_path):
    output = tmp_path / "export"
    result = export_run("https://sim.example", "run", output, fake_api())
    assert result["frame_count"] == 2
    assert result["through_revision"] == 3
    assert not result["run_complete"]
    assert json.loads((output / "manifest.json").read_text()) == result
    for item in result["files"]:
        content = (output / item["path"]).read_bytes()
        assert hashlib.sha256(content).hexdigest() == item["sha256"]
        assert len(content) == item["bytes"]
    assert not (output / "frame-000000000005.json").exists()
    with pytest.raises(FileExistsError):
        export_run("https://sim.example", "run", output, fake_api())


def test_missing_events_cannot_produce_an_importable_export(tmp_path):
    output = tmp_path / "incomplete"
    with pytest.raises(ValueError, match="Noncontiguous"):
        export_run("https://sim.example", "run", output, fake_api(gap=True))
    assert not (output / "manifest.json").exists()


@pytest.mark.parametrize("api", ["https://user:secret@example.test", "https://example.test/?secret=1", "file:///tmp/test"])
def test_api_credentials_are_not_accepted(tmp_path, api):
    with pytest.raises(ValueError, match="origin"):
        export_run(api, "run", tmp_path / "unsafe", fake_api())
