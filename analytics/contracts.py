"""Versioned contracts survive a project rename without rewriting audit evidence.

The namespace is a product label. Contract kind and version determine the
wire shape; all existing structural, cursor and checksum validation still runs.
"""
import re


def contract_matches(value, expected):
    if not isinstance(value, str):
        return False
    namespace, separator, contract = value.partition(".")
    return bool(separator and re.fullmatch(r"[a-z][a-z0-9_-]*", namespace)
                and contract == expected.partition(".")[2])
