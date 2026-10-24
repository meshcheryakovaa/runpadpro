"""Real HTTP integration checks. Runs with Python stdlib on Linux and Windows."""
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

root = Path(__file__).resolve().parents[1]
dotnet = os.environ.get("DOTNET_EXE", "dotnet")
server = root / "src/Runpad.Server/bin/Release/net10.0/Runpad.Server.dll"
with socket.socket() as probe:
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
base = f"http://127.0.0.1:{port}"
admin = secrets.token_hex(32)

def request(path, token=None, method="GET", body=None, status=200, headers=None):
    h = dict(headers or {})
    if token:
        h["Authorization"] = "Bearer " + token
    if body is not None and not isinstance(body, bytes):
        body = json.dumps(body).encode()
        h["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=body, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            code, data = response.status, response.read()
    except urllib.error.HTTPError as error:
        code, data = error.code, error.read()
    assert code == status, f"{method} {path}: expected {status}, got {code}: {data[:300]}"
    if data and data[:1] in (b"{", b"["):
        return json.loads(data)
    return data

with tempfile.TemporaryDirectory(prefix="runpad-hub-test-") as temp:
    env = {**os.environ, "RUNPAD_ADMIN_TOKEN": admin, "RUNPAD_SERVER_DATA": temp,
           "ASPNETCORE_URLS": base, "DOTNET_ENVIRONMENT": "Production"}
    log = open(Path(temp) / "server.log", "w+", encoding="utf-8")
    def start():
        process = subprocess.Popen([dotnet, str(server)], env=env, stdout=log, stderr=log)
        for _ in range(100):
            try:
                request("/health")
                return process
            except (urllib.error.URLError, ConnectionError):
                if process.poll() is not None:
                    log.seek(0)
                    raise RuntimeError(log.read())
                time.sleep(0.1)
        process.terminate()
        raise TimeoutError("Server did not become healthy")
    process = start()
    try:
        assert b"RUNPAD PRO" in request("/")
        request("/api/admin/devices", status=401)
        request("/api/admin/devices", "incorrect", status=401)
        a = request("/api/admin/devices", admin, "POST", {"name": "PC01", "classroom": "43"})
        b = request("/api/admin/devices", admin, "POST", {"name": "PC02", "classroom": "43"})
        request(f"/api/device/{a['deviceId']}/commands?target=user", b["token"], status=401)
        heartbeat = {"name": "PC01", "classroom": "43", "component": "user", "modules": [], "user": "student"}
        request(f"/api/device/{a['deviceId']}/heartbeat", a["token"], "POST", heartbeat, 204)
        devices = request("/api/admin/devices", admin)
        assert len(devices) == 2 and all("token" not in d and "tokenHash" not in d for d in devices)
        for url in ["file:///C:/Windows/System32/cmd.exe", "javascript:alert(1)"]:
            request("/api/admin/commands", admin, "POST", {"deviceIds": [a["deviceId"]], "kind": "open-url", "payload": {"url": url}}, 400)
        added = request("/api/admin/commands", admin, "POST", {"deviceIds": [a["deviceId"]], "kind": "message", "payload": {"text": "Тест"}})
        command_id = added[0]["id"]
        commands = request(f"/api/device/{a['deviceId']}/commands?target=user", a["token"])
        assert len(commands) == 1 and commands[0]["id"] == command_id
        assert request(f"/api/device/{a['deviceId']}/commands?target=user", a["token"]) == [], "Delivery lease must prevent immediate duplicates"
        request(f"/api/device/{b['deviceId']}/commands/{command_id}/ack", b["token"], "POST", {"ok": True}, 400)
        request(f"/api/device/{a['deviceId']}/commands/{command_id}/ack", a["token"], "POST", {"ok": True}, 204)
        request("/api/admin/commands", admin, "POST", {"deviceIds": [a["deviceId"]], "kind": "shutdown", "payload": {}})
        assert not request(f"/api/device/{a['deviceId']}/commands?target=user", a["token"])
        assert len(request(f"/api/device/{a['deviceId']}/commands?target=system", a["token"])) == 1
        boundary = "runpad-test-boundary"
        content = b"sample teaching file"
        multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="lesson.txt"\r\nContent-Type: text/plain\r\n\r\n'.encode() + content + f'\r\n--{boundary}--\r\n'.encode())
        uploaded = request("/api/admin/files", admin, "POST", multipart, headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        request(f"/api/device/{a['deviceId']}/files/{uploaded['fileId']}", a["token"], status=404)
        request("/api/admin/commands", admin, "POST", {"deviceIds": [a["deviceId"]], "kind": "file", "payload": uploaded})
        assert request(f"/api/device/{a['deviceId']}/files/{uploaded['fileId']}", a["token"]) == content
        request(f"/api/device/{b['deviceId']}/files/{uploaded['fileId']}", b["token"], status=404)
        state = Path(temp, "state.json").read_text(encoding="utf-8-sig")
        assert a["token"] not in state and admin not in state
        process.terminate(); process.wait(timeout=15)
        process = start()
        assert len(request("/api/admin/devices", admin)) == 2, "Device enrollment must survive restart"
        request("/api/admin/devices/" + a["deviceId"], admin, "DELETE", status=204)
        request(f"/api/device/{a['deviceId']}/commands?target=user", a["token"], status=401)
        print("PASS: hub authentication, enrollment, command leases, ACK ownership, file grants, persistence, revocation")
    finally:
        if process.poll() is None:
            process.terminate(); process.wait(timeout=15)
        log.close()
