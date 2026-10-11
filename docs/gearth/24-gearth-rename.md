# GEarth rename and deployment

Names now use **GEarth**, `gearth` paths and schema names, and `GEARTH_` environment variables.
The solution is `GEarth.sln`; API assembly is `backend/GEarth.Api/bin/Debug/net10.0/GEarth.Api.dll`.
The maintained design is `gearth-design.md`.

## Operational database cutover

1. Stop the simulation gracefully and wait for the saved checkpoint. Stop every app and fork writer using the branch being migrated.
2. Keep a Neon recovery branch and the current release/configuration for rollback.
3. Run `python3 ops/migrate_gearth.py --source-schema PREVIOUS_SCHEMA` to inspect the SQL.
4. Set `DATABASE_URL` privately to that branch's connection. Add `--apply` to perform the atomic rename and verify table counts, content fingerprints and object IDs.
5. Rename each application's configuration keys to `GEARTH_`, preserving their values. Install the new release and its `gearth.service` and `gearth-control.service` units.
6. Verify saved run loading, audit export, cached reports and a new weekly save before resuming normal traffic.

Migrate each existing fork branch separately. A rename of the parent does not rewrite existing child branches.
The database itself remains `neondb`, so its connection URL does not change. PostgreSQL schema moves preserve rows, indexes and foreign keys.
Startup refuses an unmigrated checkpoint schema instead of silently creating an empty second database.
Rollback uses the inverse schema rename with all writers stopped, then the retained release/configuration.

`python3 ops/rebrand_env.py CONFIG_FILE --source-prefix PREVIOUS_PREFIX` previews a key rename without printing credentials.
Add `--apply` only on the retained configuration; values, quoting, permissions and ownership are preserved.
On the Ubuntu VM, run `bash ops/gcp/rename_installation.sh PREVIOUS_NAME` to inspect the installation plan.
With every old service/process stopped, use `apply` to retain the UID/GID while moving the existing home, data, logs and release directories.
Run the new release installer before restarting. After verification, remove the previous service units and sudoers entry retained for rollback.

## Databricks

Rename the three Unity Catalog schemas to `gearth_bronze`, `gearth_silver` and `gearth_gold` using the schema update API.
Preserve and verify the schema/table/volume IDs. Recreate derived views with the updated schema references.
Use `/Volumes/workspace/gearth_bronze/run_exports` and the GEarth notebook folder; update any job notebook and widget paths.
Reference: [Databricks schema management](https://docs.databricks.com/aws/en/schemas/manage-schema).

## Historical evidence

Recorded audit payloads, manifests, checksums, source commits and prior-run evidence are immutable historical observations.
They are not rewritten to imply that older runs used the renamed release. Prior branded v1 contracts remain readable;
contract kind/version and full data validation still apply. New exports/reports use `gearth` contract labels.
The observer returns the current report label without changing its persisted source evidence.

Jayden will rename the earlier hosted Site URL. Until then, configure its actual address in `GEARTH_SHARE_URL`;
do not invent a new working URL. The cloud observer address is unchanged by a product rename.
