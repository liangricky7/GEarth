import argparse
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def cell(kind, source):
    result = {
        "cell_type": kind,
        "id": hashlib.sha256((kind + source).encode()).hexdigest()[:12],
        "metadata": {},
        "source": source.splitlines(keepends=True),
    }
    if kind == "code":
        result.update({"execution_count": None, "outputs": []})
    return result


def notebook():
    server = (ROOT / "ops/amd_model_server.py").read_text()
    manifest = (ROOT / "ops/qwen-models.json").read_text()
    requirements = (ROOT / "ops/amd-requirements.txt").read_text()
    install = f"""import json, os, secrets, subprocess, sys, time
from pathlib import Path
setup_dir = Path('/workspace/faultline-amd')
setup_dir.mkdir(exist_ok=True)
(setup_dir / 'amd_model_server.py').write_text({server!r})
(setup_dir / 'qwen-models.json').write_text({manifest!r})
(setup_dir / 'amd-requirements.txt').write_text({requirements!r})
install_log = (setup_dir / 'install.log').open('w')
setup_process = subprocess.Popen([sys.executable, '-m', 'pip', 'install', '-r', str(setup_dir / 'amd-requirements.txt')], stdout=install_log, stderr=subprocess.STDOUT)
print('Dependency installer started:', setup_process.pid)
"""
    status = """print('Installer exit code:', setup_process.poll())
print((setup_dir / 'install.log').read_text()[-2000:])
"""
    start = """import signal, socket
if setup_process.poll() != 0:
    raise RuntimeError('Wait for installer exit code 0 before starting')
old_processes = []
for process_path in Path('/proc').iterdir():
    if not process_path.name.isdigit():
        continue
    try:
        arguments = (process_path / 'cmdline').read_bytes().decode().split(chr(0))
        working_dir = (process_path / 'cwd').resolve()
        if 'amd_model_server:app' in arguments and '8180' in arguments and working_dir == setup_dir:
            old_processes.append(int(process_path.name))
    except (OSError, UnicodeError):
        continue
for pid in old_processes:
    os.kill(pid, signal.SIGTERM)
deadline = time.monotonic() + 60
while time.monotonic() < deadline:
    with socket.socket() as connection:
        connection.settimeout(1)
        if connection.connect_ex(('127.0.0.1', 8180)) != 0:
            break
    time.sleep(1)
else:
    raise RuntimeError('Port 8180 remains occupied; inspect the previous process')
key_path = setup_dir / 'api-key'
if not key_path.exists():
    descriptor = os.open(key_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'w') as handle:
        handle.write(secrets.token_urlsafe(48))
key_path.chmod(0o600)
server_environment = {**os.environ, 'AMD_DUAL_MODELS': 'true', 'AMD_MODEL': 'Qwen/Qwen3-8B', 'AMD_MODEL_MANIFEST': str(setup_dir / 'qwen-models.json'), 'AMD_KEY_FILE': str(key_path)}
server_log = (setup_dir / 'server.log').open('w')
server_process = subprocess.Popen([sys.executable, '-m', 'uvicorn', 'amd_model_server:app', '--host', '127.0.0.1', '--port', '8180', '--no-access-log'], cwd=setup_dir, env=server_environment, stdout=server_log, stderr=subprocess.STDOUT, start_new_session=True)
print('Both Hugging Face models are loading. PID:', server_process.pid)
"""
    health = """import urllib.request, urllib.error
api_key = key_path.read_text().strip()
def local_request(path, payload=None, key=api_key):
    request = urllib.request.Request('http://127.0.0.1:8180' + path, data=json.dumps(payload).encode() if payload is not None else None, headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)
try:
    health_result = local_request('/health')
    print(health_result)
except OSError:
    print('Still loading or failed. Process exit:', server_process.poll())
    print((setup_dir / 'server.log').read_text()[-2000:])
"""
    probe = """health_result = local_request('/health')
if not health_result['ready']:
    raise RuntimeError('Wait for ready true')
expected = json.loads((setup_dir / 'qwen-models.json').read_text())
reported = {entry['id']: entry['revision'] for entry in health_result['models']}
if reported != {entry['model']: entry['revision'] for entry in expected.values()}:
    raise RuntimeError('Loaded model revisions do not match the shared manifest')
try:
    local_request('/v1/models', key='incorrect-key')
except urllib.error.HTTPError as error:
    if error.code != 401:
        raise
else:
    raise RuntimeError('Authentication check failed')
for group, entry in expected.items():
    body = {'model': entry['model'], 'messages': [{'role': 'user', 'content': 'Choose the only available action, hold. Return exactly a JSON object with choice 0 and a short nonempty reason.'}], 'max_tokens': 128, 'temperature': 0, 'chat_template_kwargs': {'enable_thinking': False}, 'response_format': {'type': 'json_object'}}
    started = time.perf_counter()
    result = local_request('/v1/chat/completions', body)
    action = json.loads(result['choices'][0]['message']['content'])
    if set(action) != {'choice', 'reason'} or type(action['choice']) is not int or action['choice'] != 0 or not isinstance(action['reason'], str) or not 1 <= len(action['reason'].strip()) <= 240:
        raise RuntimeError('Decision validation failed for ' + group)
    print(group, entry['model'], 'valid decision in', round(time.perf_counter() - started, 2), 'seconds')
print('Both pinned models and authentication verified')
"""
    expose = """EXPOSE_ENDPOINT = False
if EXPOSE_ENDPOINT:
    subprocess.run(['/var/run/secrets/frp-self-service/install'], check=True)
    tunnel = str(Path.home() / '.local/bin/rc-tunnel')
    tunnel_environment = {key: value for key, value in os.environ.items() if key.lower() not in ('http_proxy', 'https_proxy', 'all_proxy')}
    result = subprocess.run([tunnel, 'expose', '--port', '8180'], env=tunnel_environment, capture_output=True, text=True, check=True)
    print(result.stdout)
else:
    print('Reuse the existing HTTPS tunnel to port 8180, or enable exposure for a fresh instance')
"""
    connection = """from urllib.parse import urlsplit
from IPython.display import FileLink, display
AMD_ORIGIN = ''
address = urlsplit(AMD_ORIGIN)
if address.scheme != 'https' or not address.hostname or not address.hostname.endswith('.radeon.firstdg.ai') or address.username or address.password or address.query or address.fragment or address.path not in ('', '/'):
    raise ValueError('Set AMD_ORIGIN to the HTTPS tunnel origin')
values = {'AMD_ENABLED': 'true', 'AMD_MODEL': 'Qwen/Qwen3-8B', 'AMD_DECISION_GROUP': 'institution', 'AMD_CAPACITY': '1', 'AMD_BASE_URL': AMD_ORIGIN.rstrip('/') + '/v1', 'AMD_API_KEY': api_key, 'AMD_CITIZEN_ENABLED': 'true', 'AMD_CITIZEN_MODEL': 'Qwen/Qwen3-4B', 'AMD_CITIZEN_DECISION_GROUP': 'citizen', 'AMD_CITIZEN_CAPACITY': '1', 'AMD_CITIZEN_BASE_URL': AMD_ORIGIN.rstrip('/') + '/v1', 'AMD_CITIZEN_API_KEY': api_key}
connection_path = Path.cwd() / 'faultline-amd-connection.env'
descriptor = os.open(connection_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(descriptor, 'w') as handle:
    handle.write(''.join(key + '=' + value + '\\n' for key, value in values.items()))
connection_path.chmod(0o600)
display(FileLink(connection_path.name))
print('Private connection file created; do not print its contents')
"""
    cells = [
        cell(
            "markdown",
            "# Faultline: the same Qwen3 model pair on AMD\n\nRun cells in order. This preserves ROCm PyTorch and uses no sudo. It replaces only the model server in `/workspace/faultline-amd` on port 8180. Models download from Hugging Face at the same revisions as both Sparks.\n",
        ),
        cell("code", install),
        cell("code", status),
        cell(
            "markdown",
            "## Start both models\n\nThis stops the prior Faultline model server before loading the new pair. GPU loading needs at least 28 GiB free memory and can take several minutes.\n",
        ),
        cell("code", start),
        cell("code", health),
        cell("code", probe),
        cell(
            "markdown",
            "## Endpoint and private configuration\n\nReuse the existing tunnel when upgrading this instance. For a fresh instance, enable the exposure cell. Set AMD_ORIGIN below to the tunnel's HTTPS origin, then download the generated connection file without sharing its contents.\n",
        ),
        cell("code", expose),
        cell("code", connection),
    ]
    for entry in cells:
        if entry["cell_type"] == "code":
            compile("".join(entry["source"]), "notebook_cell", "exec")
    return {
        "cells": cells,
        "metadata": {
            "kernelspec": {
                "display_name": "Python 3",
                "language": "python",
                "name": "python3",
            }
        },
        "nbformat": 4,
        "nbformat_minor": 5,
    }


def build(destination):
    destination.mkdir(parents=True, exist_ok=True)
    prepared = json.dumps(notebook(), indent=2) + "\n"
    (ROOT / "ops/faultline-qwen-amd.ipynb").write_text(prepared)
    for name in (
        "spark_qwen.py",
        "qwen-models.json",
        "amd_model_server.py",
        "amd-requirements.txt",
        "faultline-qwen-amd.ipynb",
    ):
        shutil.copy2(ROOT / "ops" / name, destination / name)
    shutil.copy2(
        ROOT / "docs/faultline/18-all-node-qwen-setup.md", destination / "README.md"
    )
    archive = shutil.make_archive(str(destination), "zip", destination)
    print(f"Setup bundle: {archive}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output", type=Path, default=Path.home() / "Downloads/faultline-qwen-setup"
    )
    build(parser.parse_args().output)
