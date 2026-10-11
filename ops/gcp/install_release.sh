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
test -f /var/lib/faultline/bootstrap-ready
target="/opt/faultline/releases/$release"
if [[ "$(readlink -f /opt/faultline/current 2>/dev/null || true)" == "$target" ]]; then
  echo "This source release is already active; prepare a distinct release before replacing it."
  exit 2
fi
mkdir -p "$target"
tar -xzf "$archive" -C "$target"
# Prepare private configuration without changing the running collector's config.
if [[ -L "$target/.env.local" ]]; then
  rm "$target/.env.local"
fi
install -m 600 -o faultline -g faultline "$config" "$target/.env.local"
ln -sfn /var/lib/faultline "$target/.data"
ln -sfn /var/log/faultline "$target/.logs"
mkdir -p /home/faultline/.ssh
chmod 700 /home/faultline/.ssh
chown -R faultline:faultline "$target" /home/faultline/.ssh
install -m 600 -o faultline -g faultline "$(dirname "$config")/spark-known-hosts" /home/faultline/.ssh/known_hosts
sudo -u faultline python3 -m venv "$target/.venv"
sudo -u faultline "$target/.venv/bin/python" -m pip install -q -r "$target/agents/requirements.txt"
cd "$target"
sudo -u faultline env DOTNET_ROOT=/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 /opt/dotnet/dotnet build Faultline.sln --nologo -v quiet
sudo -u faultline "$target/.venv/bin/python" ops/gcp/check_compute.py
if sudo -u faultline "$target/.venv/bin/python" -c 'from dotenv import dotenv_values; c=dotenv_values(".env.local",interpolate=False); raise SystemExit(0 if c.get("AMD_ENABLED", "false").lower()=="true" else 1)'; then
  sudo -u faultline "$target/.venv/bin/python" ops/check_amd.py --output /var/lib/faultline/amd-cloud-connection.json
fi
if [[ "$activation" == after-current ]]; then
  "$target/.venv/bin/python" ops/gcp/wait_for_experiment.py
fi
# Switch only after a successful build. Persistent state survives release changes.
systemctl stop faultline.service 2>/dev/null || true
install -m 600 -o faultline -g faultline "$config" /var/lib/faultline/config.env.new
mv -f /var/lib/faultline/config.env.new /var/lib/faultline/config.env
rm "$target/.env.local"
ln -sfn /var/lib/faultline/config.env "$target/.env.local"
ln -sfn "$target" /opt/faultline/current
install -m 644 "$target/ops/gcp/faultline.service" /etc/systemd/system/faultline.service
install -m 644 "$target/ops/gcp/nginx.conf" /etc/nginx/sites-available/default
nginx -t
systemctl daemon-reload
systemctl enable faultline.service
systemctl reload nginx
if [[ "$start_collector" == true ]]; then
  systemctl start faultline.service
  echo "Release installed; verified collector started."
else
  echo "Release installed; authenticate Tailscale and verify compute access before starting faultline.service."
fi
