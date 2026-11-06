from __future__ import annotations

import http.server
import os
import shutil
import socketserver
import sys
import tempfile
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

TEST_HOME = Path(tempfile.gettempdir()) / "craftunity-sig-url-check"
os.environ["CRAFTUNITY_HOME"] = str(TEST_HOME)

from craftunity import config, manifest  # noqa: E402
from craftunity.util import LauncherError  # noqa: E402

FAILED: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    print(("  OK  " if condition else " FAIL ") + name + (f" — {detail}" if detail and not condition else ""))
    if not condition:
        FAILED.append(name)


def main() -> int:
    if TEST_HOME.exists():
        shutil.rmtree(TEST_HOME, ignore_errors=True)
    TEST_HOME.mkdir(parents=True, exist_ok=True)

    manifest.public_key_path = lambda: config.data_dir() / "admin" / "craftunity_manifest_pub.pem"
    saved = manifest.save_admin_manifest(dict(manifest.DEFAULT_MANIFEST))
    publish = config.data_dir() / "admin" / "publish"
    body = (publish / "manifest.json").read_bytes()
    signature = (publish / "manifest.json.sig").read_bytes()

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self) -> None:  # noqa: N802
            if self.path == "/manifest.json":
                payload = body
            elif self.path == "/signature":
                payload = signature
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, *args) -> None:
            pass

    with socketserver.TCPServer(("127.0.0.1", 0), Handler) as httpd:
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        body_url = f"http://127.0.0.1:{port}/manifest.json"
        sig_url = f"http://127.0.0.1:{port}/signature"

        try:
            manifest.fetch_manifest(body_url)
            check("без отдельного адреса подписи не скачивается", False, "манифест принят")
        except LauncherError:
            check("без отдельного адреса подписи не скачивается", True)

        try:
            loaded = manifest.fetch_manifest(body_url, sig_url=sig_url)
            check("подпись по отдельному адресу проверяется", loaded.get("schema") == manifest.MANIFEST_SCHEMA)
            check("версия совпадает", loaded.get("version") == saved.get("version"))
        except LauncherError as exc:
            check("подпись по отдельному адресу проверяется", False, str(exc))

        cached = manifest.load_cached_manifest()
        check("манифест попал в кэш", bool(cached))

        httpd.shutdown()

    print()
    if FAILED:
        print("Провалено: " + ", ".join(FAILED))
        return 1
    print("Проверка отдельного адреса подписи пройдена")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
