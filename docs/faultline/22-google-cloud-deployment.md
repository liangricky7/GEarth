# Continuous collection on Google Cloud

The CPU VM runs the .NET world engine, Python decision worker, and observer. Both Sparks run the existing Qwen inference services. Neon stores world checkpoints, ordered events, and run reports. The deployment does not use Gemini API credits or require a GPU VM.

## Current deployment

- Project: `project-73e10704-566a-4d04-886` (My First Project).
- Instance: `faultline-sim`, zone `us-central1-a`, Ubuntu 24.04, `e2-medium`, 40 GB persistent boot disk.
- Public observer: the instance's external IP, served by Nginx on port 80. The inference worker and engine listen on loopback; only observer GET/HEAD traffic is forwarded publicly.
- The VM joins Jayden's existing Tailscale network. Spark A is reached at `hackathon@100.70.23.23` using the existing pinned SSH host keys.
- One managed SSH connection forwards Spark A's model ports 8000/8001. Spark B is reached directly over Tailscale through Ken's existing relay at `100.119.198.81:18002/18004`. All four catalogs must pass before the service starts. The observer IP is reserved as `faultline-observer-ip` so it survives VM stop/start.

The cloud service disables inference HTTP keepalive across SSH relays. A remote protocol disconnect is retried once within the original job timeout; persistent failures still produce explicit fallbacks. This prevents a stale relay connection from immediately putting a healthy model into its 30-second cooldown. The first live deployment exposed this failure pattern and remains recorded as a separate, interrupted experiment.

Spark B currently depends on Ken's existing PC relay. Keeping that PC relay and both Sparks available remains necessary. The VM must not use Spark A's loopback 18002/18004 reverse tunnel, which currently passes through Jayden's laptop. Moving the coordinator to the VM removes Jayden's laptop from the collection path; it does not make the physical inference machines or Ken's relay available when powered off. Netplan is untouched on all machines.

## Repeated experiments

`ops/run_collection.py` builds once and repeatedly launches finite worlds with `--exit-on-complete`. The service defaults to 1,100 agents, 365 daily ticks, two-second minimum tick intervals, live Spark decisions, and Neon persistence. Real run duration also depends on inference and storage.

Before each attempt, the collector durably reserves a new seed. A crash or service restart never reuses a reserved seed. Each experiment has a manifest under `/var/lib/faultline/collection/` recording its seed, settings, observed run ID, source commit and source archive hash, timestamps, exit code, and outcome. A manifest left `running` after an abrupt reboot represents an interrupted attempt and must not be interpreted as completed. World events and notes remain in Neon.

The collector backs off after failed runs and waits when less than 1 GiB of disk remains. A successful HTTP health response with `status=paused` is a simulation failure, not evidence that data collection is progressing. Inspect ticks and run status as well as the service process. A completed saved run can be inspected but is not currently resumed; after a restart the collector starts a new experiment from a new seed.

## Deploy a reviewed version

From the local checkout, after Google Cloud CLI authentication:

```bash
.venv/bin/python ops/gcp/deploy.py \
  --project project-73e10704-566a-4d04-886
```

The deployer packages tracked source as it currently exists plus deployment files. It excludes credentials, runtime data, build outputs, and unrelated untracked documents. It records both the Git commit and a hash of the exact packaged source, including local tracked edits. Review the working tree before deploying. Private Neon and Spark credentials are filtered from `.env.local` and copied separately via encrypted SCP. The original local config stays unchanged, and the old share supervisor is not configured on the VM.

The installer builds a separate release before switching `/opt/faultline/current`. Persistent state lives outside releases at `/var/lib/faultline`, and logs at `/var/log/faultline`. A deployment stops the previous service gracefully and does not auto-start a new live run: verify all four model catalogs, then start it explicitly. Prefer deployments between completed experiments; engine updates cannot hot-swap into an active world.

```bash
gcloud compute ssh faultline-sim --project=project-73e10704-566a-4d04-886 \
  --zone=us-central1-a \
  --command='sudo systemctl start faultline.service'
```

First installation also requires approving the VM's Tailscale device sign-in. No Spark model containers, model weights, Netplan settings, or GPU drivers are changed by this deployment.

## Operate

```bash
# On the VM
sudo systemctl status faultline.service
sudo journalctl -u faultline.service -n 40 --no-pager
curl -fsS http://127.0.0.1:5080/api/health
sudo tail -n 20 /var/log/faultline/api.log
sudo systemctl stop faultline.service
sudo systemctl start faultline.service
```

The systemd service starts after a VM reboot, checks Spark readiness, and retries service failures. Tailscale must remain authenticated. Its device key lifetime is separate from Google Cloud's credit expiry. SSH uses existing host-key verification; it never disables certificate or host verification.

Develop features locally while this release collects data. Use a separate Neon branch/database for development when testing persistence changes. Deploy a reviewed new release for a new experiment batch; do not change the collection schema underneath an active old release.

Google Cloud trial credits cover eligible Compute Engine usage, but expire or stop resources when the trial ends. Check VM, disk, public IP, and egress charges in Cloud Billing. A billing budget alert is a notification, not a hard spending cap. Do not upgrade the billing account as part of this setup without the account owner's explicit instruction. Stop the VM when collection is no longer needed; its disk still incurs storage charges and should retain needed manifests until backed up.
