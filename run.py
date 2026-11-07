from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
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
    if not current or updater.just_applied():
        return
    result = updater.check(current)
    if result.get("state") != "staged":
        return
    process = getattr(server.ENGINE, "process", None)
    if process is not None and process.poll() is None:
        return
    if getattr(server.ENGINE, "status", "") in ("installing", "launching"):
        return
    if updater.schedule_apply():
        clear_lock()
        os._exit(0)
    if updater.apply_pending(["--updated"]):
        clear_lock()
        os._exit(0)


def pack_signature(manifest: dict | None) -> str:
    items = sorted(
        str(entry.get("project_id") or entry.get("slug") or "")
        for entry in ((manifest or {}).get("mods") or [])
    )
    return "|".join(part for part in items if part)


def sync_pack_mods(tracker: dict) -> None:
    """Доставляет моды модпака в фоне, когда список в манифесте изменился."""
    from craftunity import mods as mods_module

    manifest = server.ENGINE.manifest
    if not manifest:
        return
    signature = pack_signature(manifest)
    if not signature or signature == tracker.get("value"):
        return
    process = getattr(server.ENGINE, "process", None)
    if process is not None and process.poll() is None:
        return
    if getattr(server.ENGINE, "status", "") in ("starting", "installing", "launching", "running"):
        return
    game = manifest.get("game") or {}
    version = game.get("version") or config.load_settings().get("game_version")
    result = mods_module.sync_manifest_mods(manifest, version, "fabric")
    failed = [item for item in result if item.get("error")]
    tracker["value"] = signature
    server.ENGINE._log(f"[mods] модпак обновлён в фоне: {len(result)} мод(ов), ошибок {len(failed)}")


def startup_update_check(timeout: float = 8.0) -> None:
    """Проверяет обновление сразу при запуске, не дожидаясь минутного цикла."""
    finished = threading.Event()

    def work() -> None:
        try:
            server.ENGINE.refresh_manifest()
            maybe_apply_update()
        except Exception:
            pass
        finally:
            finished.set()

    threading.Thread(target=work, daemon=True).start()
    finished.wait(timeout)


def temp_problem() -> str:
    """Проверяет, сможет ли распаковка сборки работать во временной папке."""
    folder = os.environ.get("TEMP") or os.environ.get("TMP") or ""
    if not folder:
        return "Windows не сообщает путь к временной папке (%TEMP%)."
    try:
        probe = os.path.join(folder, "craftunity-write-test.tmp")
        with open(probe, "w", encoding="utf-8") as handle:
            handle.write("ok")
        os.remove(probe)
    except OSError:
        return f"Нет прав на запись во временную папку:\n{folder}"
    try:
        free = shutil.disk_usage(folder).free
    except OSError:
        return ""
    if free < 200 * 1024 * 1024:
        return f"Мало места для распаковки (свободно {free // (1024 * 1024)} МБ) во временной папке:\n{folder}"
    return ""


def show_problem(title: str, text: str) -> None:
    """Показывает понятное окно вместо системной ошибки LoadLibrary."""
    if os.environ.get("CRAFTUNITY_HEADLESS") == "1":
        print(text, file=sys.stderr)
        return
    if os.name != "nt":
        print(text, file=sys.stderr)
        return
    try:
        import ctypes

        ctypes.windll.user32.MessageBoxW(None, text, title, 0x10)
    except Exception:
        print(text, file=sys.stderr)


def _window_flags() -> tuple[int, object]:
    if os.name != "nt":
        return 0, None
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    return flags, startup


def _detached(command: list[str]) -> bool:
    flags, startup = _window_flags()
    try:
        subprocess.Popen(
            command,
            close_fds=True,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=flags,
            startupinfo=startup,
        )
        return True
    except (OSError, ValueError):
        return False


def open_native_window(url: str) -> bool:
    """Окно на tkinter + WebView2, если WebView2 на машине доступен и не заблокирован."""
    if os.name != "nt":
        return False
    try:
        from craftunity import native_window
    except Exception:
        return False
    if not native_window.available():
        return False
    if not getattr(sys, "frozen", False):
        return bool(native_window.run(url))
    return _detached([sys.executable, "--native-window", url])


def open_native_window_blocking(url: str) -> bool:
    """Запускает окно WebView2 в текущем процессе (для режима --native-window)."""
    try:
        from craftunity import native_window
    except Exception:
        return False
    return native_window.run(url)


def run_window(url: str) -> bool:
    """Интерфейс открывается в отдельном окне: WebView2, браузер-app или вкладка браузера."""
    if open_native_window(url):
        return True
    if launchable_browser(url):
        return True
    try:
        return bool(webbrowser.open(url))
    except Exception:
        return False


def browser_ok(url: str) -> bool:
    return bool(url)


def launchable_browser(url: str) -> bool:
    """Открывает интерфейс окном браузера без вкладок и адресной строки."""
    if os.name != "nt":
        return False
    candidates: list[tuple[str, list[str]]] = []
    for base in (os.environ.get("ProgramFiles"), os.environ.get("ProgramFiles(x86)")):
        if not base:
            continue
        candidates += [
            (os.path.join(base, r"Google\Chrome\Application\chrome.exe"), ["--app=" + url]),
            (os.path.join(base, r"Microsoft\Edge\Application\msedge.exe"), ["--app=" + url]),
            (os.path.join(base, r"BraveSoftware\Brave-Browser\Application\brave.exe"), ["--app=" + url]),
            (os.path.join(base, r"Vivaldi\Application\vivaldi.exe"), ["--app=" + url]),
            (os.path.join(base, r"Yandex\YandexBrowser\Application\browser.exe"), ["--app=" + url]),
        ]
    local = os.environ.get("LOCALAPPDATA") or ""
    if local:
        candidates += [
            (os.path.join(local, r"Google\Chrome\Application\chrome.exe"), ["--app=" + url]),
            (os.path.join(local, r"Microsoft\Edge\Application\msedge.exe"), ["--app=" + url]),
            (os.path.join(local, r"BraveSoftware\Brave-Browser\Application\brave.exe"), ["--app=" + url]),
            (os.path.join(local, r"Yandex\YandexBrowser\Application\browser.exe"), ["--app=" + url]),
            (os.path.join(local, r"Programs\Opera GX\opera.exe"), ["--app=" + url]),
            (os.path.join(local, r"Programs\Opera\opera.exe"), ["--app=" + url]),
        ]
    for path, args in candidates:
        if not os.path.exists(path):
            continue
        if _detached([path, *args]):
            return True
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description="Craftunity Launcher")
    parser.add_argument("--admin", action="store_true", help="Запустить админ-панель")
    parser.add_argument("--port", type=int, default=0, help="Порт локального API (0 — автоматически)")
    parser.add_argument("--host", default="127.0.0.1", help="Адрес прослушивания (для админки на VPS: 0.0.0.0)")
    parser.add_argument("--browser", action="store_true", help="Открыть в браузере вместо окна")
    parser.add_argument("--prepare-keys", action="store_true", help="Создать ключи подписи и выйти")
    parser.add_argument("--updated", action="store_true", help="Служебный флаг: лаунчер перезапущен после обновления")
    parser.add_argument("--native-window", default="", help="Служебный флаг: открыть окно WebView2 на указанный адрес")
    parser.add_argument("--no-native", action="store_true", help="Не использовать окно WebView2: открыть интерфейс в браузере")
    args = parser.parse_args()

    config.seed_from_portable()

    if args.native_window:
        return 0 if open_native_window_blocking(args.native_window) else 1

    if not args.prepare_keys and getattr(sys, "frozen", False):
        trouble = temp_problem()
        if trouble:
            show_problem(
                "Craftunity Launcher",
                trouble
                + "\n\nДобавьте папку во исключения антивируса Microsoft Defender\n"
                "или используйте сборку «без распаковки» (папка Craftunity) с сайта.",
            )
            return 1

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
    try:
        updater.cleanup_temp()
    except Exception:
        pass

    if os.environ.get("CRAFTUNITY_SINGLE") == "1":
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

    startup_update_check()

    # Манифест, heartbeat, модпак и тихое обновление: при старте и далее раз в минуту
    mods_tracker = {"value": ""}

    def _presence_loop():
        # первый проход сразу: лаунчер сообщает о себе, не дожидаясь минуты
        first = True
        while True:
            try:
                server.ENGINE.refresh_manifest()
                server.ENGINE.report_presence()
            except Exception:
                pass
            if first:
                first = False
                continue
            try:
                maybe_apply_update()
            except Exception:
                pass
            try:
                sync_pack_mods(mods_tracker)
            except Exception:
                pass
            try:
                updater.cleanup_old(attempts=1, delay=0)
            except Exception:
                pass
            time.sleep(max(15, int(config.MANIFEST_REFRESH_SECONDS)))

    threading.Thread(target=_presence_loop, daemon=True).start()

    try:
        if os.environ.get("CRAFTUNITY_HEADLESS") == "1":
            print(url)
            while True:
                time.sleep(1)
        if not args.browser and not args.no_native and getattr(sys, "frozen", False):
            if open_native_window(url):
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
