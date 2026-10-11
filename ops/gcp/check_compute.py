"""Require all four Spark catalogs and every enabled AMD catalog before collection."""

import json
import shlex
import subprocess
import sys
import urllib.request
from urllib.error import HTTPError
from pathlib import Path

from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from ops.compute_config import compute_environment
SCRIPT = """
import json,sys,urllib.request
config=json.load(sys.stdin)
for prefix,port in [('SPARK_A',8000),('SPARK_A_CITIZEN',8001)]:
    request=urllib.request.Request(f'http://127.0.0.1:{port}/v1/models',headers={'Authorization':'Bearer '+config[prefix+'_API_KEY']})
    try:
        with urllib.request.urlopen(request,timeout=15) as response:
            models=[item['id'] for item in json.load(response)['data']]
        if config[prefix+'_MODEL'] not in models:
            raise RuntimeError('Configured model absent')
        print(prefix+' ready',flush=True)
    except Exception as error:
        print(prefix+' unavailable: '+type(error).__name__,flush=True)
        sys.exit(1)
"""


def check_http_catalog(prefix, config):
    url = config[prefix + "_BASE_URL"].rstrip("/") + "/models"
    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + config[prefix + "_API_KEY"]})
    with urllib.request.urlopen(request, timeout=15) as response:
        models = [item["id"] for item in json.load(response)["data"]]
    if config[prefix + "_MODEL"] not in models:
        raise RuntimeError("Configured model absent")
    if prefix.startswith("AMD"):
        try:
            with urllib.request.urlopen(url, timeout=15):
                raise RuntimeError("AMD endpoint accepts unauthenticated access")
        except HTTPError as error:
            if error.code != 401:
                raise RuntimeError("AMD authentication check failed") from None


def main():
    config = dotenv_values(ROOT / ".env.local", interpolate=False)
    mode, _, providers, _ = compute_environment(config, "auto")
    # Pass only model IDs and bearer keys through encrypted SSH stdin; never arguments or logs.
    payload = {prefix + suffix: config[prefix + suffix]
               for prefix in ("SPARK_A", "SPARK_A_CITIZEN")
               for suffix in ("_MODEL", "_API_KEY")}
    result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                             "-o", "StrictHostKeyChecking=yes", "hackathon@100.70.23.23",
                             "python3 -c " + shlex.quote(SCRIPT)],
                            input=json.dumps(payload), text=True, timeout=90)
    if result.returncode:
        return result.returncode
    prefixes = ["SPARK_B", "SPARK_B_CITIZEN"] + [provider.name.upper() for provider in providers if provider.name.startswith("amd")]
    for prefix in prefixes:
        try:
            check_http_catalog(prefix, config)
            print(prefix + " ready", flush=True)
        except Exception as error:
            print(prefix + " unavailable: " + type(error).__name__, flush=True)
            return 1
    print("Compute mode: " + mode, flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
