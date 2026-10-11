"""Rename environment keys without changing values, comments or secret quoting."""
import argparse
import os
from pathlib import Path
import re
import tempfile


def rename_keys(text, source_prefix):
    if not re.fullmatch(r"[A-Z][A-Z0-9]*", source_prefix) or source_prefix == "GEARTH":
        raise ValueError("Supply the previous uppercase environment prefix")
    keys = re.findall(r"^\s*(?:export\s+)?([A-Z][A-Z0-9_]*)=", text, re.M)
    replacements = {key: "GEARTH_" + key[len(source_prefix) + 1:] for key in keys if key.startswith(source_prefix + "_")}
    if any(target in keys for target in replacements.values()):
        raise ValueError("New and previous keys both exist; resolve the configuration collision first")
    return re.sub(r"^(\s*(?:export\s+)?)([A-Z][A-Z0-9_]*)(=)",
                  lambda m: m[1] + replacements.get(m[2], m[2]) + m[3], text, flags=re.M)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file")
    parser.add_argument("--source-prefix", required=True)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    path = Path(args.file)
    text = path.read_text()
    renamed = rename_keys(text, args.source_prefix)
    if not args.apply:
        print("Configuration rename prepared; use --apply to update keys. Values are never printed.")
        return
    # Resolve an existing configuration symlink, preserving it and file permissions.
    path = path.resolve()
    original = path.stat()
    name = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, prefix=".gearth-env-", delete=False) as temporary:
            temporary.write(renamed)
            name = temporary.name
        os.chmod(name, original.st_mode & 0o777)
        if (os.stat(name).st_uid, os.stat(name).st_gid) != (original.st_uid, original.st_gid):
            os.chown(name, original.st_uid, original.st_gid)
        os.replace(name, path)
    finally:
        if name and os.path.exists(name):
            os.unlink(name)
    print("Environment keys updated; values preserved")


if __name__ == "__main__":
    main()
