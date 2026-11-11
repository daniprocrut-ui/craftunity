from __future__ import annotations

import argparse
import json
import os
import shutil
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


def apply_update_in_process() -> bool:
    """Подменяет скачанный exe без перезапуска сервера — для запуска из C#-лаунчера.

    C#-окно держит python.exe живым всё время, поэтому старый путь (schedule_apply)
    не подходит: скрипт подмены не сможет заменить занятый файл. Здесь замена идёт
    в самом процессе, а C# перезапустит окно после закрытия.
    """
    if not updater.is_frozen():
        return False
    info = updater.pending_info()
    if not info:
        updater.discard_pending()
        return False
    if updater.is_installer_url(str(info.get("url") or "")):
        if not updater.run_installer(updater.pending_exe_path(info)):
            return False
        updater.pending_meta_path().unlink(missing_ok=True)
        return True
    try:
        exe = Path(sys.executable).resolve()
        updater._swap_files(exe, updater.pending_exe_path(info))
    except OSError as exc:
        updater._log_update(f"Не удалось подменить exe: {exc}")
        return False
    updater.pending_meta_path().unlink(missing_ok=True)
    updater.mark_applied(str(info.get("version") or ""))
    return True


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
    if os.environ.get("CRAFTUNITY_HEADLESS") == "1":
        if apply_update_in_process():
            server.ENGINE.request_restart = True
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


def webview2_available() -> bool:
    """Есть ли рантайм Edge WebView2: без него окно webview остаётся пустым."""
    if os.name != "nt":
        return True
    try:
        import winreg

        keys = (
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"),
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"),
            (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"),
        )
        for root, path in keys:
            try:
                with winreg.OpenKey(root, path) as key:
                    version = str(winreg.QueryValueEx(key, "pv")[0] or "").strip()
                if version and version != "0.0.0.0":
                    return True
            except OSError:
                continue
    except Exception:
        return True
    return False


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


def browser_profile_dir() -> Path:
    folder = config.data_dir() / "window"
    try:
        folder.mkdir(parents=True, exist_ok=True)
    except OSError:
        pass
    return folder


def launchable_browser(url: str) -> bool:
    """Окно браузера без вкладок и адресной строки, со своим профилем."""
    if os.name != "nt":
        return False
    profile = "--user-data-dir=" + str(browser_profile_dir())
    args = ["--app=" + url, profile, "--no-first-run", "--no-default-browser-check"]
    candidates: list[str] = []
    for base in (os.environ.get("ProgramFiles"), os.environ.get("ProgramFiles(x86)")):
        if not base:
            continue
        candidates += [
            os.path.join(base, r"Google\Chrome\Application\chrome.exe"),
            os.path.join(base, r"Microsoft\Edge\Application\msedge.exe"),
            os.path.join(base, r"BraveSoftware\Brave-Browser\Application\brave.exe"),
            os.path.join(base, r"Vivaldi\Application\vivaldi.exe"),
            os.path.join(base, r"Yandex\YandexBrowser\Application\browser.exe"),
        ]
    local = os.environ.get("LOCALAPPDATA") or ""
    if local:
        candidates += [
            os.path.join(local, r"Google\Chrome\Application\chrome.exe"),
            os.path.join(local, r"Microsoft\Edge\Application\msedge.exe"),
            os.path.join(local, r"BraveSoftware\Brave-Browser\Application\brave.exe"),
            os.path.join(local, r"Yandex\YandexBrowser\Application\browser.exe"),
            os.path.join(local, r"Programs\Opera GX\opera.exe"),
            os.path.join(local, r"Programs\Opera\opera.exe"),
        ]
    for path in candidates:
        if not os.path.exists(path):
            continue
        try:
            import subprocess

            subprocess.Popen(
                [path, *args],
                close_fds=True,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            return True
        except OSError:
            continue
    return False

def _com_init() -> None:
    """pywebview требует инициализированный COM в текущем потоке (иначе CO_E_NOTINITIALIZED)."""
    if os.name != "nt":
        return
    try:
        import ctypes

        ctypes.windll.ole32.CoInitialize(None)
    except Exception:
        pass


def run_window(url: str) -> bool:
    if not webview2_available():
        return False
    try:
        import webview  # type: ignore
    except Exception:
        return False
    _com_init()
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
    parser.add_argument("--uninstall-cleanup", action="store_true", help="Служебный флаг деинсталлятора: убрать временные файлы и остановить сервер")
    args = parser.parse_args()

    config.seed_from_portable()

    if getattr(args, "uninstall_cleanup", False):
        try:
            import shutil as _shutil
            from pathlib import Path as _Path
            for _name in ("updates", "admin"):
                _shutil.rmtree(config.data_dir() / _name, ignore_errors=True)
            (config.data_dir() / "launcher.lock").unlink(missing_ok=True)
        except Exception:
            pass
        return 0


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
            if launchable_browser(url):
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
        if not args.browser:
            if run_window(url):
                return 0
            if launchable_browser(url):
                while True:
                    time.sleep(1)
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
