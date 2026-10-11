import pytest
from analytics.contracts import contract_matches


@pytest.mark.parametrize("value,expected", [
    ("previous_project.run-export.v1", True),
    ("gearth.run-export.v1", True),
    ("previous_project.run-export.v2", False),
    ("previous_project.audit.v1", False),
    (".run-export.v1", False),
    ("bad/prefix.run-export.v1", False),
    (None, False),
])
def test_project_namespace_does_not_change_contract_kind_or_version(value, expected):
    assert contract_matches(value, "gearth.run-export.v1") is expected
