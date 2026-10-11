#!/usr/bin/env python3
"""CLI entry point for the bounded finished-run exporter."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from analytics.export import main

if __name__ == "__main__":
    main()
