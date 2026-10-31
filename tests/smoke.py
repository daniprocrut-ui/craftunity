from __future__ import annotations

import http.client
import json
import os
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

TEST_HOME = Path(os.environ.get("TEMP", "/tmp")) / "craftunity-smoke"
os.environ["CRAFTUNITY_HOME"] = str(TEST_HOME)
os.environ.setdefault("CRAFTUNITY_ADMIN_PASSWORD", "craftunity")

from craftunity import config, manifest, server  # noqa: E402

source_key = Path(os.environ.get("APPDATA", "")) / "Craftunity" / "admin" / "manifest_private.pem"
if source_key.exists():
    target = config.data_dir() / "admin"
    target.mkdir(parents=True, exist_ok=True)
    (target / "manifest_private.pem").write_bytes(source_key.read_bytes())

FAILED: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    print(("  OK  " if condition else " FAIL ") + name + (f" — {detail}" if detail and not condition else ""))
    if not condition:
        FAILED.append(name)


def request(port: int, method: str, path: str, payload=None, cookie: str = "") -> tuple[int, dict | str, str]:
    body = json.dumps(payload).encode() if payload is not None else None
    request = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=body, method=method)
    if body:
        request.add_header("Content-Type", "application/json")
    if cookie:
        request.add_header("Cookie", cookie)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            raw = response.read().decode("utf-8")
            set_cookie = response.headers.get("Set-Cookie", "")
            try:
                parsed = json.loads(raw)
            except ValueError:
                parsed = raw
            return response.status, parsed, set_cookie
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8")
        try:
            parsed = json.loads(raw)
        except ValueError:
            parsed = raw
        return exc.code, parsed, exc.headers.get("Set-Cookie", "")


def main() -> int:
    httpd, port = server.serve(0, mode="client")
    print(f"Локальный сервер на порту {port}")

    status, page, _ = request(port, "GET", "/")
    check("UI отдаётся", status == 200 and "Craftunity" in str(page))

    status, payload, _ = request(port, "GET", "/api/overview")
    check("GET /api/overview", status == 200 and "settings" in payload)

    status, payload, _ = request(port, "POST", "/api/account/offline", {"username": "CraftTester"})
    check("оффлайн-аккаунт", status == 200 and payload.get("account", {}).get("username") == "CraftTester")
    uuid_value = payload.get("account", {}).get("uuid", "")
    check("offline UUID стабилен", len(uuid_value) == 36)

    status, payload, _ = request(port, "POST", "/api/account/offline", {"username": "x"})
    check("короткий ник отклонён", status == 400)

    status, _, _ = request(port, "POST", "/api/admin/login", {"password": "wrong"})
    check("неверный пароль админки", status == 403)

    status, payload, cookie = request(port, "POST", "/api/admin/login", {"password": os.environ["CRAFTUNITY_ADMIN_PASSWORD"]})
    check("вход в админку", status == 200 and "craftunity_admin" in cookie)

    admin_cookie = cookie.split(";")[0]
    status, payload, _ = request(port, "GET", "/api/admin/manifest", cookie=admin_cookie)
    check("чтение манифеста", status == 200 and "version" in payload.get("manifest", {}))

    new_manifest = dict(payload["manifest"])
    new_manifest["version"] = "26.3-craftunity.2"
    new_manifest["mods"] = [{"project_id": "sodium", "title": "Sodium"}]
    status, payload, _ = request(port, "POST", "/api/admin/manifest", {"manifest": new_manifest}, cookie=admin_cookie)
    check("публикация манифеста", status == 200 and payload["manifest"]["version"] == "26.3-craftunity.2")

    status, manifest_body, _ = request(port, "GET", "/api/manifest.json")
    status_sig, signature, _ = request(port, "GET", "/api/manifest.json.sig")
    check("манифест доступен", status == 200)
    check("подпись доступна", status_sig == 200)
    if status == 200 and status_sig == 200:
        parsed = manifest.verify_manifest(
            json.dumps(manifest_body, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8"),
            str(signature),
        )
        check("подпись Ed25519 проверяется", parsed.get("version") == "26.3-craftunity.2")

    try:
        manifest.fetch_manifest(f"http://127.0.0.1:{port}/api/manifest.json")
        check("клиент принимает манифест", True)
    except Exception as exc:  # noqa: BLE001
        check("клиент принимает манифест", False, str(exc))

    status, payload, _ = request(port, "POST", "/api/heartbeat", {"instance_id": "test-1", "username": "CraftTester", "version": "1.20.1", "loader": "fabric"})
    check("heartbeat принят", status == 200)
    status, payload, _ = request(port, "GET", "/api/admin/clients", cookie=admin_cookie)
    check("клиент виден в админке", status == 200 and any(c.get("instance_id") == "test-1" for c in payload.get("clients", [])))

    status, payload, _ = request(port, "GET", "/api/admin/clients")
    check("клиенты защищены паролем", status == 401)

    httpd.shutdown()
    print()
    if FAILED:
        print("Провалено: " + ", ".join(FAILED))
        return 1
    print("Все проверки пройдены")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
