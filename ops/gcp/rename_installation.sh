#!/usr/bin/env bash
# Preserve the existing OS account, runtime data and private configuration.
# Use only during the coordinated cutover, after a graceful simulation stop.
set -euo pipefail
previous=${1:?previous lowercase installation name required}
mode=${2:-plan}
if [[ ! "$previous" =~ ^[a-z][a-z0-9_-]{0,31}$ || "$previous" == gearth ]]; then exit 2; fi
if [[ "$mode" != plan && "$mode" != apply ]]; then exit 2; fi
for base in /opt /var/lib /var/log /home; do
  if [[ -e "$base/gearth" ]]; then
    echo "Target $base/gearth already exists; refusing to combine installations."; exit 2
  fi
  test -d "$base/$previous"
done
if [[ "$mode" == plan ]]; then
  echo "Plan: retain UID/GID; rename home, release, data and log directories; update configuration keys."
  echo "Apply only with both services stopped, then install and verify the new GEarth release."
  exit 0
fi
test "$(id -u)" = 0
for unit in "$previous.service" "$previous-control.service"; do
  if systemctl is-active --quiet "$unit"; then
    echo "Service $unit is active; stop gracefully before migrating."; exit 2
  fi
done
if pgrep -u "$previous" >/dev/null; then
  echo "Previous account still has processes; wait for them to exit."; exit 2
fi
usermod --login gearth --home /home/gearth --move-home "$previous"
groupmod --new-name gearth "$previous"
for base in /opt /var/lib /var/log; do mv "$base/$previous" "$base/gearth"; done
for config in /var/lib/gearth/config.env /var/lib/gearth/control.env; do
  if [[ -f "$config" ]]; then
    python3 "$(dirname "$0")/../rebrand_env.py" "$config" --source-prefix "${previous^^}" --apply
  fi
done
# Repair operational path-valued configuration without printing secrets.
python3 - "$previous" <<'PY'
from pathlib import Path
import sys
previous = sys.argv[1]
for name in ('config.env', 'control.env'):
    path = Path('/var/lib/gearth') / name
    if path.is_file():
        text = path.read_text()
        for base in ('/opt/', '/var/lib/', '/var/log/', '/home/'):
            text = text.replace(base + previous + '/', base + 'gearth/')
        path.write_text(text)
PY
systemctl disable "$previous.service" "$previous-control.service" 2>/dev/null || true
echo "Persistent installation migrated. Install the new release; retain old units for rollback until verification passes."
