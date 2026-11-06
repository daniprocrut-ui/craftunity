from __future__ import annotations

import hashlib
import http.server
import json
import os
import socketserver
import sys
import tempfile
import threading
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

TEST_HOME = Path(tempfile.gettempdir()) / "craftunity-updater-check"
os.environ["CRAFTUNITY_HOME"] = str(TEST_HOME)

from craftunity import config, manifest, updater  # noqa: E402

FAILED: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    print(("  OK  " if condition else " FAIL ") + name + (f" — {detail}" if detail and not condition else ""))
    if not condition:
        FAILED.append(name)


PAYLOAD = b"MZ fake launcher payload " + b"x" * 4096


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/launcher.exe":
            self.send_response(200)
            self.send_header("Content-Length", str(len(PAYLOAD)))
            self.end_headers()
            self.wfile.write(PAYLOAD)
            return
        self.send_error(404)

    def log_message(self, *args) -> None:
        pass


def main() -> int:
    if TEST_HOME.exists():
        for item in TEST_HOME.rglob("*"):
            if item.is_file():
                item.unlink()
    updater.discard_pending()

    with socketserver.TCPServer(("127.0.0.1", 0), Handler) as httpd:
        port = httpd.server_address[1]
        threading.Thread(target=httpd.serve_forever, daemon=True).start()
        exe_url = f"http://127.0.0.1:{port}/launcher.exe"
        good_sha = hashlib.sha256(PAYLOAD).hexdigest()

        check("проверка версий", updater.is_newer("1.1.0", "1.0.0") and not updater.is_newer("1.0.0", "1.0.9"))
        check("короткая версия сравнивается", updater.is_newer("1.2", "1.1.9"))

        current = config.LAUNCHER_VERSION
        stale = {"launcher": {"version": current, "url": exe_url, "sha256": good_sha}}
        check("та же версия не обновляется", updater.available(stale) is None)

        fresh = {
            "version": "26.3-craftunity.9",
            "min_launcher": "1.0.0",
            "launcher": {
                "version": "9.9.9",
                "url": exe_url,
                "sha256": good_sha,
                "notes": "тест",
                "mandatory": False,
            },
        }
        block = updater.available(fresh)
        check("новая версия найдена", bool(block) and block.get("version") == "9.9.9")
        check("не блокирует запуск", not updater.should_block(fresh))

        broken = json.loads(json.dumps(fresh))
        broken["launcher"]["sha256"] = "0" * 64
        try:
            updater.stage(broken["launcher"])
            check("битая сумма отклонена", False, "файл принят")
        except Exception:
            check("битая сумма отклонена", not updater.pending_exe_path().exists())

        result = updater.check(fresh)
        check("обновление скачано", result.get("state") == "staged" and updater.pending_exe_path().exists())
        check("метаданные записаны", (updater.pending_info() or {}).get("version") == "9.9.9")

        pending = updater.pending_exe_path()
        pending.write_bytes(b"corrupted")
        check("испорченный файл не считается готовым", updater.pending_info() is None)
        updater.discard_pending()

        mandatory = json.loads(json.dumps(fresh))
        mandatory["launcher"]["mandatory"] = True
        check("принудительное обновление блокирует", updater.should_block(mandatory))

        hard = json.loads(json.dumps(fresh))
        hard["min_launcher"] = "9.9.9"
        check("min_launcher блокирует", updater.should_block(hard))

        check("в исходниках подмена не выполняется", updater.apply_pending() is False)

        httpd.shutdown()

    check("после применения стоял бы карантин", not updater.just_applied())
    updater.mark_applied("9.9.9")
    check("сразу после перезапуска карантин активен", updater.just_applied())
    check("в маркере записана версия", (config.load_json(updater.applied_path()).get("version")) == "9.9.9")
    check("старый маркер не блокирует", not updater.just_applied(seconds=0))
    updater.applied_path().unlink(missing_ok=True)

    import types

    fake_dir = TEST_HOME / "fakeapp"
    fake_dir.mkdir(parents=True, exist_ok=True)
    fake_exe = fake_dir / "Craftunity_launcher.exe"
    fake_exe.write_bytes(b"MZ")
    (fake_dir / "Craftunity_launcher.exe.old").write_bytes(b"old")
    (updater.update_dir() / "Craftunity_launcher.exe.old").write_bytes(b"old2")
    real_sys = updater.sys
    real_frozen = updater.is_frozen
    updater.sys = types.SimpleNamespace(executable=str(fake_exe))
    updater.is_frozen = lambda: True
    try:
        removed = updater.cleanup_old(attempts=1, delay=0)
        check("старые копии удалены", removed == 2, str(removed))
        check("рядом с exe .old нет", not (fake_dir / "Craftunity_launcher.exe.old").exists())
        check("в папке update .old нет", not (updater.update_dir() / "Craftunity_launcher.exe.old").exists())
    finally:
        updater.sys = real_sys
        updater.is_frozen = real_frozen

    doc = manifest.DEFAULT_MANIFEST
    check("в схеме манифеста есть блок launcher", isinstance(doc.get("launcher"), dict))
    check("пустой блок не считается обновлением", updater.available(doc) is None)

    check("в исходниках фоновая подмена не запускается", updater.schedule_apply() is False)
    check(
        "скрипт обновления ждёт выхода и подменяет exe",
        "move /y" in updater.APPLY_BATCH and "--updated" in updater.APPLY_BATCH,
    )
    check("скрипт обновления удаляет себя", 'del "%~f0"' in updater.APPLY_BATCH)

    print()
    if FAILED:
        print("Провалено: " + ", ".join(FAILED))
        return 1
    print("Все проверки обновления пройдены")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
