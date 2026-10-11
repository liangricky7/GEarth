"""Require all four Spark model catalogs before starting collection."""

import json
import shlex
import subprocess
import sys
import urllib.request
from pathlib import Path

from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[2]
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


def main():
    config = dotenv_values(ROOT / ".env.local", interpolate=False)
    # Pass only model IDs and bearer keys through encrypted SSH stdin; never arguments or logs.
    payload = {prefix + suffix: config[prefix + suffix]
               for prefix in ("SPARK_A", "SPARK_A_CITIZEN", "SPARK_B", "SPARK_B_CITIZEN")
               for suffix in ("_MODEL", "_API_KEY")}
    result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                             "-o", "StrictHostKeyChecking=yes", "hackathon@100.70.23.23",
                             "python3 -c " + shlex.quote(SCRIPT)],
                            input=json.dumps(payload), text=True, timeout=90)
    if result.returncode:
        return result.returncode
    for prefix in ("SPARK_B", "SPARK_B_CITIZEN"):
        try:
            request = urllib.request.Request(config[prefix + "_BASE_URL"].rstrip("/") + "/models",
                                             headers={"Authorization": "Bearer " + config[prefix + "_API_KEY"]})
            with urllib.request.urlopen(request, timeout=15) as response:
                models = [item["id"] for item in json.load(response)["data"]]
            if config[prefix + "_MODEL"] not in models:
                raise RuntimeError("Configured model absent")
            print(prefix + " ready", flush=True)
        except Exception as error:
            print(prefix + " unavailable: " + type(error).__name__, flush=True)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
