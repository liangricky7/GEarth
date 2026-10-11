"""Export pinned, committed weekly renderer frames and ordered events without database credentials."""
import argparse
import hashlib
import json
import urllib.parse
import urllib.request
from pathlib import Path

CONTRACT = "gearth.renderer.v1"


def export_run(api, run_id, output, fetch=None):
    parsed = urllib.parse.urlsplit(api)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc or parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("API must be an HTTP(S) origin without credentials, query or fragment")
    root = api.rstrip("/") + "/api/runs/" + urllib.parse.quote(run_id, safe="") + "/renderer"

    def read(path):
        if fetch:
            value = fetch(path)
        else:
            with urllib.request.urlopen(root + path, timeout=30) as response:
                value = json.load(response)
        if value.get("schema_version") != CONTRACT or value.get("run_id") != run_id:
            raise ValueError("Renderer contract or run identity mismatch")
        return value

    metadata = read("/manifest")
    output = Path(output)
    output.mkdir(parents=True, exist_ok=False)
    files = []

    def write(name, value):
        content = (json.dumps(value, separators=(",", ":"), ensure_ascii=False) + "\n").encode()
        (output / name).write_bytes(content)
        files.append({"path": name, "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)})

    revision = sequence = count = 0
    more = True
    pin = metadata["checkpoint_revision"]
    through = 0
    last_frame = None
    while more:
        page = read(f"/frames?after_revision={revision}&limit=20")
        if page["has_more"] and not page["frames"]:
            raise ValueError("Frame cursor did not advance")
        for head in page["frames"]:
            if head["revision"] > pin:
                more = False
                break
            if head["run_id"] != run_id or head["revision"] <= revision:
                raise ValueError("Out-of-order renderer frame")
            frame = read(f"/frames/{head['revision']}")
            if (frame["revision"] != head["revision"] or frame["tick"] != head["tick"]
                    or frame["through_event_sequence"] != head["through_event_sequence"]):
                raise ValueError("Renderer frame does not match index")
            through = frame["through_event_sequence"]
            if through < sequence:
                raise ValueError("Event sequence moved backwards")
            while sequence < through:
                events = read(f"/events?after_seq={sequence}&through_seq={through}&limit=500")
                expected = sequence + 1
                if events["through_event_sequence"] != through or not events["events"]:
                    raise ValueError("Incomplete event history")
                for event in events["events"]:
                    if event["run_id"] != run_id or event["seq"] != expected or expected > through:
                        raise ValueError("Noncontiguous event history")
                    expected += 1
                if events["next_after_seq"] != expected - 1:
                    raise ValueError("Invalid event cursor")
                write(f"events-{sequence + 1:012d}-{expected - 1:012d}.json", events)
                sequence = expected - 1
            write(f"frame-{head['revision']:012d}.json", frame)
            last_frame = frame
            revision = head["revision"]
            count += 1
        else:
            more = page["has_more"]
            continue
        break
    if count == 0:
        raise ValueError("No captured weekly frames; historical weeks cannot be reconstructed from the latest state")
    manifest = {**metadata, "export_schema_version": "gearth.renderer-export.v1", "files": files,
                "frame_count": count, "through_revision": revision, "through_event_sequence": through,
                "run_complete": metadata["status"] == "completed"
                    and last_frame.get("frame_kind") == "terminal"
                    and last_frame["tick"] == metadata.get("checkpoint_tick")}
    # Written last; interrupted directories deliberately have no importable manifest.
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run_id")
    parser.add_argument("output", type=Path)
    parser.add_argument("--api", default="http://127.0.0.1:5080")
    options = parser.parse_args()
    result = export_run(options.api, options.run_id, options.output)
    print(f"Exported {result['frame_count']} renderer frames through revision {result['through_revision']}")


if __name__ == "__main__":
    main()
