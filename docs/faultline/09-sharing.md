# Sharing the observer

Permanent share address: https://diarockets-faultline.finebear64.chatgpt.site

The hosted Site forwards the observer UI and read API to the laptop running Faultline. Its URL remains the same across observer restarts, tunnel reconnects, and Site deployments. The old randomly assigned lhr.life addresses are no longer the links to distribute.

The simulation and Spark inference still run through Jayden’s laptop. Keep it awake, online, and running the observer. Closing the lid can interrupt access. While the host is unavailable, the permanent address shows a reconnecting page and retries automatically.

## Start and reconnect

The normal launcher starts the share supervisor when both FAULTLINE_SHARE_URL and FAULTLINE_SHARE_KEY are configured in the ignored .env.local file. An already running supervisor is reused.

To supervise sharing independently of the observer launcher:

```bash
.venv/bin/python ops/share_site.py
```

For one connection update:

```bash
.venv/bin/python ops/share_site.py --once
```

Run only one updater at a time. The supervisor checks every 30 seconds, recreates failed SSH tunnels, and updates the Site’s stored upstream after checking observer health. It only manages its own localhost.run SSH process; it does not modify Spark networking or Netplan.

## Configuration and access

FAULTLINE_SHARE_URL is the permanent Site origin. FAULTLINE_SHARE_KEY is the private updater credential; it must match the hosted SHARE_UPDATE_KEY secret. Keep it out of chat, Git, and browser code. Teammates need only the public URL to observe runs.

The Site permits the observer’s GET routes. Its connection update endpoint requires authentication and accepts only HTTPS localhost.run tunnel origins. It never forwards visitor cookies or authorization headers to the laptop. Site source and deployment identity are retained in the local .data/sites/faultline-share checkout and the Site’s source repository.

Local sharing state is in .data/share-site.json. Logs are in .logs/share-supervisor.log and .logs/share-ssh.log. These files are ignored by Git.
