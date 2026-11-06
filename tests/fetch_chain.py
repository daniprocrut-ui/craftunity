from __future__ import annotations

import http.server
import importlib.util
import json
import os
import shutil
import socketserver
import sys
import tempfile
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

TEST_HOME = Path(tempfile.gettempdir()) / "craftunity-chain-check"
os.environ["CRAFTUNITY_HOME"] = str(TEST_HOME)

from craftunity import config, launch, manifest  # noqa: E402

FAILED: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    print(("  OK  " if condition else " FAIL ") + name + (f" — {detail}" if detail and not condition else ""))
    if not condition:
        FAILED.append(name)


def load_release_module():
    spec = importlib.util.spec_from_file_location("craftunity_release", ROOT / "tools" / "release.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    if TEST_HOME.exists():
        shutil.rmtree(TEST_HOME, ignore_errors=True)
    TEST_HOME.mkdir(parents=True, exist_ok=True)

    manifest.public_key_path = lambda: config.data_dir() / "admin" / "craftunity_manifest_pub.pem"
    manifest.save_admin_manifest(dict(manifest.DEFAULT_MANIFEST))
    publish = config.data_dir() / "admin" / "publish"

    handler = lambda *args, **kwargs: http.server.SimpleHTTPRequestHandler(  # noqa: E731
        *args, directory=str(publish), **kwargs
    )
    with socketserver.TCPServer(("127.0.0.1", 0), handler) as httpd:
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        mirror = f"http://127.0.0.1:{port}/manifest.json"

        check("зеркало отдаёт подписанный манифест", manifest.fetch_manifest(mirror).get("version") == manifest.DEFAULT_MANIFEST["version"])

        config.save_settings({"manifest_url": "http://127.0.0.1:9/manifest.json", "accept_manifest": True})
        config.DEFAULT_MANIFEST_URL = "http://127.0.0.1:9/default.json"
        config.MANIFEST_FALLBACKS = [mirror]

        engine = launch.Engine()
        fetched = engine.refresh_manifest(force=True)
        check("манифест подтянулся с зеркала", bool(fetched))
        check("источник помечен как зеркало", engine.manifest_source == "mirror", engine.manifest_source)
        check("ошибки не осталось", engine.manifest_error == "")
        check("основной адрес не перезаписан", config.load_settings()["manifest_url"] == "http://127.0.0.1:9/manifest.json")

        check("адрес не дублируется в зеркалах", manifest.fallback_urls(mirror) == [])
        check("зеркал ровно одно", len(manifest.fallback_urls("https://example.org/m.json")) == 1)

        httpd.shutdown()

    config.MANIFEST_FALLBACKS = []

    def serve_dir(directory: Path):
        handler = lambda *args, **kwargs: http.server.SimpleHTTPRequestHandler(  # noqa: E731
            *args, directory=str(directory), **kwargs
        )
        server = socketserver.TCPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        return server, server.server_address[1]

    def write_manifest(directory: Path, build: str, launcher_version: str, updated: str) -> None:
        directory.mkdir(parents=True, exist_ok=True)
        data = json.loads(json.dumps(manifest.DEFAULT_MANIFEST))
        data["version"] = build
        data["updated_at"] = updated
        data["launcher"] = {
            "version": launcher_version,
            "url": "http://127.0.0.1:9/x.exe",
            "sha256": "0" * 64,
            "notes": "",
            "mandatory": False,
        }
        body, signature = manifest.sign_manifest(data)
        (directory / "manifest.json").write_bytes(body)
        (directory / "manifest.json.sig").write_bytes(signature)

    stale_dir = TEST_HOME / "stale"
    fresh_dir = TEST_HOME / "fresh"
    write_manifest(stale_dir, "26.1", "1.0.0", "2026-01-01T00:00:00Z")
    write_manifest(fresh_dir, "26.3", "9.9.9", "2026-10-31T12:00:00Z")
    stale_srv, stale_port = serve_dir(stale_dir)
    fresh_srv, fresh_port = serve_dir(fresh_dir)
    config.save_settings({"manifest_url": f"http://127.0.0.1:{stale_port}/manifest.json"})
    config.DEFAULT_MANIFEST_URL = f"http://127.0.0.1:{fresh_port}/manifest.json"

    engine2 = launch.Engine()
    picked = engine2.refresh_manifest(force=True) or {}
    check(
        "устаревшее зеркало не выигрывает у свежего",
        (picked.get("launcher") or {}).get("version") == "9.9.9",
        str((picked.get("launcher") or {}).get("version")),
    )
    check("источник — свежий адрес", engine2.manifest_source == "mirror", engine2.manifest_source)
    stale_srv.shutdown()
    fresh_srv.shutdown()

    legacy = dict(config.settings_defaults())
    legacy["manifest_url"] = config.LEGACY_MANIFEST_URLS[0]
    config.save_json(config.settings_path(), legacy)
    config.DEFAULT_MANIFEST_URL = "https://user.github.io/repo/manifest.json"
    check("старый адрес заменяется новым", config.load_settings()["manifest_url"] == config.DEFAULT_MANIFEST_URL)

    release = load_release_module()
    tmp_config = TEST_HOME / "config_copy.py"
    shutil.copy2(ROOT / "craftunity" / "config.py", tmp_config)
    release.CONFIG_PATH = tmp_config
    release.set_source_constants("someone/craftunity")
    text = tmp_config.read_text(encoding="utf-8")
    compile(text, "config_copy.py", "exec")
    values = {}
    exec(compile(text, "config_copy.py", "exec"), values)
    check("в config.py записан репозиторий", values["GITHUB_REPO"] == "someone/craftunity")
    check(
        "адрес манифеста указывает на Pages",
        values["DEFAULT_MANIFEST_URL"] == "https://someone.github.io/craftunity/manifest.json",
        values["DEFAULT_MANIFEST_URL"],
    )
    check("два зеркала", len(values["MANIFEST_FALLBACKS"]) == 2, str(values["MANIFEST_FALLBACKS"]))
    check("json-зеркало на jsDelivr", "cdn.jsdelivr.net/gh/someone/craftunity@main/docs/manifest.json" in values["MANIFEST_FALLBACKS"][1])
    check("raw-зеркало идёт первым", values["MANIFEST_FALLBACKS"][0].startswith("https://raw.githubusercontent.com/"))
    check("legacy-адрес остался в истории", values["LEGACY_MANIFEST_URLS"][0] == "https://craftunity.mcsh.io/launcher/manifest.json")

    print()
    if FAILED:
        print("Провалено: " + ", ".join(FAILED))
        return 1
    print("Все проверки цепочки манифеста пройдены")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
