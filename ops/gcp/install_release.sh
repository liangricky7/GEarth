#!/usr/bin/env bash
# Run as root on the VM after uploading a source archive and the private config.
set -euo pipefail
archive=${1:?source archive required}
config=${2:?private config required}
release=${3:?release digest required}
if [[ ! "$release" =~ ^[a-f0-9]{64}$ ]]; then
  exit 2
fi
test -f /var/lib/faultline/bootstrap-ready
target="/opt/faultline/releases/$release"
mkdir -p "$target"
tar -xzf "$archive" -C "$target"
install -m 600 -o faultline -g faultline "$config" /var/lib/faultline/config.env
ln -sfn /var/lib/faultline/config.env "$target/.env.local"
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
# Switch only after a successful build. Persistent state survives release changes.
systemctl stop faultline.service 2>/dev/null || true
ln -sfn "$target" /opt/faultline/current
install -m 644 "$target/ops/gcp/faultline.service" /etc/systemd/system/faultline.service
install -m 644 "$target/ops/gcp/nginx.conf" /etc/nginx/sites-available/default
nginx -t
systemctl daemon-reload
systemctl enable faultline.service
systemctl reload nginx
echo "Release installed; authenticate Tailscale and verify Spark access before starting faultline.service."
