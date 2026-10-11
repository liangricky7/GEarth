"""Plan or apply a data-preserving PostgreSQL schema rename during cutover."""
import argparse
import json
import os
import re
from urllib.parse import urlparse
from urllib.request import Request, urlopen


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r"[a-z][a-z0-9_]{0,62}", value):
        raise ValueError("Use a lowercase SQL identifier of at most 63 characters")
    return '"' + value + '"'


def migration_sql(source, target="gearth"):
    """One atomic operation; retries are harmless, collisions fail closed."""
    original, renamed = identifier(source), identifier(target)
    if source == target:
        raise ValueError("Source and target must differ")
    return f"""DO $migration$
    BEGIN
      IF to_regnamespace('{source}') IS NULL THEN
        IF to_regnamespace('{target}') IS NOT NULL
           AND to_regclass('{target}.stage1_runs') IS NOT NULL THEN RETURN;
        END IF;
        RAISE EXCEPTION 'Source application schema is missing';
      END IF;
      IF to_regnamespace('{target}') IS NOT NULL THEN
        RAISE EXCEPTION 'Target schema already exists; refusing to split or replace data';
      END IF;
      IF to_regclass('{source}.stage1_runs') IS NULL
         OR to_regclass('{source}.stage1_events') IS NULL THEN
        RAISE EXCEPTION 'Source is not an application checkpoint schema';
      END IF;
      ALTER SCHEMA {original} RENAME TO {renamed};
    END $migration$;"""


def query(database_url, sql, parameters=()):
    uri = urlparse(database_url)
    if uri.scheme not in {"postgres", "postgresql"} or not uri.hostname or not uri.hostname.endswith(".neon.tech"):
        raise ValueError("DATABASE_URL must be a Neon PostgreSQL connection")
    # Direct connection for schema operations; application traffic may stay pooled.
    connection = database_url.replace(uri.hostname, uri.hostname.replace("-pooler", ""), 1)
    endpoint = "https://api." + uri.hostname.split(".", 1)[1] + "/sql"
    request = Request(endpoint, data=json.dumps({"query": sql, "params": list(parameters)}).encode(),
                      headers={"Content-Type": "application/json", "Neon-Connection-String": connection})
    try:
        with urlopen(request, timeout=120) as response:
            return json.load(response)["rows"]
    except Exception:
        raise RuntimeError("Schema operation failed; connection details suppressed") from None


def inventory(database_url, schema):
    identifier(schema)
    rows = query(database_url, "SELECT c.oid::text AS object_id,c.relname AS name,c.relkind AS kind "
                 "FROM pg_class c JOIN pg_namespace n ON c.relnamespace=n.oid "
                 "WHERE n.nspname=$1 ORDER BY c.relname", [schema])
    result = []
    for row in rows:
        entry = dict(row)
        if row["kind"] == "r":
            table = identifier(schema) + "." + identifier(row["name"])
            fingerprint = query(database_url, f"SELECT COUNT(*)::text AS rows,"
                f"COALESCE(bit_xor(('x'||substr(md5(row(t.*)::text),1,16))::bit(64)::bigint),0)::text AS fingerprint FROM {table} t")[0]
            entry.update(fingerprint)
        result.append(entry)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-schema", required=True)
    parser.add_argument("--target-schema", default="gearth")
    parser.add_argument("--apply", action="store_true", help="only after stopping all application writers")
    args = parser.parse_args()
    sql = migration_sql(args.source_schema, args.target_schema)
    if not args.apply:
        print(sql)
        return
    connection = os.environ["DATABASE_URL"]
    before = inventory(connection, args.source_schema)
    if not before:
        if inventory(connection, args.target_schema):
            print("Migration already applied")
            return
        raise RuntimeError("No source data or migrated target exists")
    query(connection, sql)
    after = inventory(connection, args.target_schema)
    if before != after:
        raise RuntimeError("Schema renamed but preservation verification differs; keep writers stopped")
    print(json.dumps({"target_schema": args.target_schema, "preserved_objects": len(after),
                      "tables": [row for row in after if row["kind"] == "r"]}, sort_keys=True))


if __name__ == "__main__":
    main()
