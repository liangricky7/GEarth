#!/usr/bin/env bash
# Compute Engine Ubuntu 24.04 startup: dependencies only; no secrets in metadata.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq python3 python3-venv python3-pip curl ca-certificates git openssh-client nginx libicu74 libssl3t64 libgssapi-krb5-2 zlib1g
if ! id gearth >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash gearth
fi
mkdir -p /opt/gearth /opt/dotnet /var/lib/gearth /var/log/gearth
chown gearth:gearth /opt/gearth /var/lib/gearth /var/log/gearth
if [ ! -x /opt/dotnet/dotnet ]; then
  curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh -o /tmp/gearth-dotnet-install.sh
  bash /tmp/gearth-dotnet-install.sh --version 10.0.203 --install-dir /opt/dotnet
fi
ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
if ! command -v tailscale >/dev/null 2>&1; then
  curl --fail --silent --show-error --location https://tailscale.com/install.sh -o /tmp/gearth-tailscale-install.sh
  bash /tmp/gearth-tailscale-install.sh
fi
systemctl enable --now tailscaled
touch /var/lib/gearth/bootstrap-ready
