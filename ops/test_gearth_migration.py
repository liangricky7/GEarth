import pytest
from ops.migrate_gearth import migration_sql
from ops.rebrand_env import rename_keys


def test_schema_rename_preserves_data_and_refuses_split_storage():
    sql = migration_sql("previous_project")
    assert 'ALTER SCHEMA "previous_project" RENAME TO "gearth"' in sql
    assert "Target schema already exists" in sql
    assert "stage1_runs" in sql and "stage1_events" in sql
    assert "DROP" not in sql and "UPDATE" not in sql and "DELETE" not in sql


@pytest.mark.parametrize("source", ["production'; DROP DATABASE postgres; --", "auth.users", "", "gearth"])
def test_migration_rejects_unsafe_or_identical_targets(source):
    with pytest.raises(ValueError):
        migration_sql(source)


def test_environment_rename_preserves_secrets_and_quoting():
    old = '# keep comment\nexport PRIOR_SHARE_KEY="literal $() and ="\nPRIOR_WORLD=true\nDATABASE_URL=private\n'
    result = rename_keys(old, "PRIOR")
    assert result == '# keep comment\nexport GEARTH_SHARE_KEY="literal $() and ="\nGEARTH_WORLD=true\nDATABASE_URL=private\n'


def test_environment_rename_refuses_conflicting_existing_keys():
    with pytest.raises(ValueError):
        rename_keys('PRIOR_WORLD=true\nGEARTH_WORLD=false\n', 'PRIOR')
