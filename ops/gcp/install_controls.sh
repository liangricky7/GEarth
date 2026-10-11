#!/usr/bin/env bash
# Existing release must contain the pause hooks. Configure HTTPS and the team service.
set -euo pipefail
hostname=${1:?HTTPS hostname required}
private_config=${2:?private control configuration required}
if [[ ! "$hostname" =~ ^[a-z0-9][a-z0-9.-]+$ ]]; then exit 2; fi
target=$(readlink -f /opt/gearth/current)
grep -q 'new SimulationPause(' "$target/backend/GEarth.Api/Coordinator.cs"
test -f "/etc/letsencrypt/live/$hostname/fullchain.pem"
mkdir -p /var/lib/gearth/control
chown gearth:gearth /var/lib/gearth/control
chmod 700 /var/lib/gearth/control
install -m 600 -o gearth -g gearth "$private_config" /var/lib/gearth/control.env
visudo -cf "$target/ops/gcp/control-sudoers"
install -m 440 "$target/ops/gcp/control-sudoers" /etc/sudoers.d/gearth-control
install -m 644 "$target/ops/gcp/gearth-control.service" /etc/systemd/system/gearth-control.service
# Static assets stay available while Reset restarts the engine.
cat > /etc/nginx/sites-available/default <<EOF
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name _;
    location /.well-known/acme-challenge/ { root /var/www/certbot; }
    location / { return 301 https://$hostname\$request_uri; }
}
server {
    listen 443 ssl;
    listen [::]:443 ssl;
    server_name $hostname;
    ssl_certificate /etc/letsencrypt/live/$hostname/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$hostname/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    client_max_body_size 4k;
    add_header X-Content-Type-Options nosniff always;
    add_header Referrer-Policy same-origin always;
    location /api/control/ {
        proxy_pass http://127.0.0.1:5090;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_read_timeout 10s;
    }
    location /api/ {
        limit_except GET HEAD { deny all; }
        proxy_pass http://127.0.0.1:5080;
        proxy_set_header Host \$host;
        proxy_read_timeout 60s;
    }
    location / {
        limit_except GET HEAD { deny all; }
        root /opt/gearth/current/backend/GEarth.Api/wwwroot;
        try_files \$uri \$uri/ /index.html;
        add_header Cache-Control no-cache;
    }
}
EOF
nginx -t
mkdir -p /etc/letsencrypt/renewal-hooks/deploy
printf '#!/bin/sh\n/usr/bin/systemctl reload nginx\n' > /etc/letsencrypt/renewal-hooks/deploy/gearth-nginx
chmod 755 /etc/letsencrypt/renewal-hooks/deploy/gearth-nginx
systemctl daemon-reload
systemctl enable --now gearth-control.service certbot.timer
systemctl reload nginx
rm -f "$private_config"
echo 'HTTPS team controls installed.'
