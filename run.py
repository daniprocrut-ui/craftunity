from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
import urllib.request
import webbrowser
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent))

from craftunity import config  # noqa: E402
from craftunity import manifest as manifest_module  # noqa: E402
from craftunity import server, state, updater  # noqa: E402


def wait_ready(port: int, timeout: float = 8.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/api/state", timeout=1):
                return True
        except Exception:
            time.sleep(0.1)
    return False


LOCK_NAME = "launcher.lock"


def lock_path() -> Path:
    return config.data_dir() / LOCK_NAME


def is_alive(url: str) -> bool:
    try:
        with urllib.request.urlopen(url.rstrip("/") + "/api/state", timeout=1):
            return True
    except Exception:
        return False


def existing_instance() -> str | None:
    try:
        data = json.loads(lock_path().read_text(encoding="utf-8"))
        port = int(data.get("port") or 0)
    except (OSError, ValueError):
        return None
    if not port:
        return None
    url = f"http://127.0.0.1:{port}/"
    return url if is_alive(url) else None


def write_lock(port: int) -> None:
    try:
        lock_path().write_text(json.dumps({"pid": os.getpid(), "port": port}), encoding="utf-8")
    except OSError:
        pass


def clear_lock() -> None:
    try:
        data = json.loads(lock_path().read_text(encoding="utf-8"))
        if int(data.get("pid") or 0) == os.getpid():
            lock_path().unlink(missing_ok=True)
    except (OSError, ValueError):
        pass


def notify_running(url: str) -> None:
    if os.environ.get("CRAFTUNITY_HEADLESS") == "1":
        print("Craftunity уже запущен:", url)
        return
    if os.name == "nt":
        try:
            import ctypes

            ctypes.windll.user32.MessageBoxW(
                None,
                "Craftunity уже запущен.\nЗакройте окно лаунчера, чтобы открыть новое.",
                "Craftunity",
                0x40,
            )
            return
        except Exception:
            pass
    webbrowser.open(url)


def wait_for_previous(timeout: float = 8.0) -> None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            data = json.loads(lock_path().read_text(encoding="utf-8"))
            port = int(data.get("port") or 0)
            pid = int(data.get("pid") or 0)
        except (OSError, ValueError):
            return
        if pid == os.getpid():
            return
        if port and is_alive(f"http://127.0.0.1:{port}/"):
            time.sleep(0.25)
            continue
        try:
            lock_path().unlink(missing_ok=True)
        except OSError:
            pass
        return


def maybe_apply_update() -> None:
    current = server.ENGINE.manifest
    if not current:
        return
    result = updater.check(current)
    if result.get("state") != "staged" or not updater.should_block(current):
        return
    process = getattr(server.ENGINE, "process", None)
    if process is not None and process.poll() is None:
        return
    if updater.apply_pending(["--updated"]):
        os._exit(0)


def run_window(url: str) -> bool:
    try:
        import webview  # type: ignore
    except Exception:
        return False
    try:
        window = webview.create_window(
            "Craftunity Launcher",
            url,
            width=1180,
            height=760,
            min_size=(980, 640),
            background_color="#0b0d12",
        )
        webview.start()
        return True
    except Exception:
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description="Craftunity Launcher")
    parser.add_argument("--admin", action="store_true", help="Запустить админ-панель")
    parser.add_argument("--port", type=int, default=0, help="Порт локального API (0 — автоматически)")
    parser.add_argument("--host", default="127.0.0.1", help="Адрес прослушивания (для админки на VPS: 0.0.0.0)")
    parser.add_argument("--browser", action="store_true", help="Открыть в браузере вместо окна")
    parser.add_argument("--prepare-keys", action="store_true", help="Создать ключи подписи и выйти")
    parser.add_argument("--updated", action="store_true", help="Служебный флаг: лаунчер перезапущен после обновления")
    args = parser.parse_args()

    config.seed_from_portable()

    if args.prepare_keys:
        path = manifest_module.ensure_admin_keys()
        public = path.with_name("craftunity_manifest_pub.pem")
        print(f"private: {path}")
        print(f"public:  {public}")
        return 0

    if args.admin:
        admin_port = args.port or 8765
        httpd, port = server.serve(admin_port, mode="admin", host=args.host)
        url = f"http://127.0.0.1:{port}/admin"
        if not wait_ready(port):
            print("Сервер не запустился", file=sys.stderr)
            return 1
        print(f"Craftunity Admin: {url}")
        print(f"Публичный манифест: http://127.0.0.1:{port}/api/manifest.json")
        if not args.browser:
            if run_window(url):
                return 0
        webbrowser.open(url)
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            httpd.shutdown()
        return 0

    if args.updated:
        wait_for_previous()
    if updater.apply_pending(["--updated"]):
        return 0
    updater.cleanup_old()

    existing = existing_instance()
    if existing:
        notify_running(existing)
        return 0

    httpd, port = server.serve(args.port, mode="client", host=args.host)
    write_lock(port)
    url = f"http://127.0.0.1:{port}/"
    if not wait_ready(port):
        clear_lock()
        print("Локальный API не запустился", file=sys.stderr)
        return 1

    # Манифест, heartbeat и тихое обновление: при старте и далее каждые 5 минут
    def _presence_loop():
        while True:
            try:
                server.ENGINE.refresh_manifest()
                server.ENGINE.report_presence()
            except Exception:
                pass
            try:
                maybe_apply_update()
            except Exception:
                pass
            time.sleep(300)

    threading.Thread(target=_presence_loop, daemon=True).start()

    try:
        if os.environ.get("CRAFTUNITY_HEADLESS") == "1":
            print(url)
            while True:
                time.sleep(1)
        if not args.browser and run_window(url):
            return 0
        webbrowser.open(url)
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        httpd.shutdown()
    finally:
        clear_lock()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
