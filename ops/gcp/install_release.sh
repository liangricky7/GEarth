#!/usr/bin/env bash
# Run as root on the VM after uploading a source archive and the private config.
set -euo pipefail
archive=${1:?source archive required}
config=${2:?private config required}
release=${3:?release digest required}
activation=${4:-immediate}
start_collector=${5:-false}
if [[ "$activation" != immediate && "$activation" != after-current ]]; then
  exit 2
fi
if [[ ! "$release" =~ ^[a-f0-9]{64}$ ]]; then
  exit 2
fi
test -f /var/lib/gearth/bootstrap-ready
target="/opt/gearth/releases/$release"
if [[ "$(readlink -f /opt/gearth/current 2>/dev/null || true)" == "$target" ]]; then
  echo "This source release is already active; prepare a distinct release before replacing it."
  exit 2
fi
mkdir -p "$target"
tar -xzf "$archive" -C "$target"
# Prepare private configuration without changing the running collector's config.
if [[ -L "$target/.env.local" ]]; then
  rm "$target/.env.local"
fi
install -m 600 -o gearth -g gearth "$config" "$target/.env.local"
ln -sfn /var/lib/gearth "$target/.data"
ln -sfn /var/log/gearth "$target/.logs"
mkdir -p /home/gearth/.ssh
chmod 700 /home/gearth/.ssh
chown -R gearth:gearth "$target" /home/gearth/.ssh
install -m 600 -o gearth -g gearth "$(dirname "$config")/spark-known-hosts" /home/gearth/.ssh/known_hosts
sudo -u gearth python3 -m venv "$target/.venv"
sudo -u gearth "$target/.venv/bin/python" -m pip install -q -r "$target/agents/requirements.txt"
cd "$target"
sudo -u gearth env DOTNET_ROOT=/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 /opt/dotnet/dotnet build GEarth.sln --nologo -v quiet
sudo -u gearth "$target/.venv/bin/python" ops/gcp/check_compute.py
if sudo -u gearth "$target/.venv/bin/python" -c 'from dotenv import dotenv_values; c=dotenv_values(".env.local",interpolate=False); raise SystemExit(0 if c.get("AMD_ENABLED", "false").lower()=="true" else 1)'; then
  sudo -u gearth "$target/.venv/bin/python" ops/check_amd.py --output /var/lib/gearth/amd-cloud-connection.json
fi
if [[ "$activation" == after-current ]]; then
  "$target/.venv/bin/python" ops/gcp/wait_for_experiment.py
fi
# Switch only after a successful build. Persistent state survives release changes.
systemctl stop gearth.service 2>/dev/null || true
systemctl stop gearth-control.service 2>/dev/null || true
install -m 600 -o gearth -g gearth "$config" /var/lib/gearth/config.env.new
mv -f /var/lib/gearth/config.env.new /var/lib/gearth/config.env
rm "$target/.env.local"
ln -sfn /var/lib/gearth/config.env "$target/.env.local"
ln -sfn "$target" /opt/gearth/current
install -m 644 "$target/ops/gcp/gearth.service" /etc/systemd/system/gearth.service
# HTTPS installation owns the Nginx configuration after team controls are enabled.
if [[ ! -f /var/lib/gearth/control.env ]]; then
  install -m 644 "$target/ops/gcp/nginx.conf" /etc/nginx/sites-available/default
else
  install -m 644 "$target/ops/gcp/gearth-control.service" /etc/systemd/system/gearth-control.service
  visudo -cf "$target/ops/gcp/control-sudoers"
  install -m 440 "$target/ops/gcp/control-sudoers" /etc/sudoers.d/gearth-control
fi
nginx -t
systemctl daemon-reload
systemctl enable gearth.service
systemctl reload nginx
if [[ -f /var/lib/gearth/control.env ]]; then
  systemctl enable --now gearth-control.service
fi
if [[ "$start_collector" == true ]]; then
  systemctl start gearth.service
  echo "Release installed; verified collector started."
else
  echo "Release installed; authenticate Tailscale and verify compute access before starting gearth.service."
fi
