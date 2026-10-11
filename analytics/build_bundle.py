"""Package tested helpers for a Databricks volume; this does not upload or execute."""
import argparse
from pathlib import Path
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output")
    args = parser.parse_args()
    with zipfile.ZipFile(args.output, "x", compression=zipfile.ZIP_DEFLATED) as bundle:
        for source in sorted(Path(__file__).resolve().parent.glob("*.py")):
            bundle.write(source, "analytics/" + source.name)
    print("Created helper bundle; upload it to run_exports/_code/faultline-analytics.zip")


if __name__ == "__main__":
    main()
