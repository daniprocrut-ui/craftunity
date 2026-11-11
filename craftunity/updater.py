from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

from . import config

PENDING_SUFFIX = ".new"
META_NAME = "pending.json"
CHUNK = 256 * 1024


def update_dir() -> Path:
    path = config.data_dir() / "update"
    path.mkdir(parents=True, exist_ok=True)
    return path


def pending_exe_path() -> Path:
    return update_dir() / (Path(executable_name()).stem + ".exe" + PENDING_SUFFIX)


def pending_meta_path() -> Path:
    return update_dir() / META_NAME


def executable_name() -> str:
    return Path(sys.executable).name if is_frozen() else "Craftunity_launcher.exe"


def is_frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def parse_version(text: str) -> tuple[int, ...]:
    parts: list[int] = []
    for chunk in str(text or "").strip().lstrip("vV").split("."):
        digits = "".join(ch for ch in chunk if ch.isdigit())
        if not digits:
            break
        parts.append(int(digits))
    return tuple(parts)


def version_key(text: str) -> tuple[int, ...]:
    parts = list(parse_version(text))
    while len(parts) < 3:
        parts.append(0)
    return tuple(parts)


def is_newer(candidate: str, current: str) -> bool:
    return version_key(candidate) > version_key(current)


def launcher_block(manifest: dict | None) -> dict:
    block = (manifest or {}).get("launcher")
    return block if isinstance(block, dict) else {}


def available(manifest: dict | None, current: str | None = None) -> dict | None:
    block = launcher_block(manifest)
    version = str(block.get("version") or "").strip()
    url = str(block.get("url") or "").strip()
    if not version or not url:
        return None
    if not is_newer(version, current or config.LAUNCHER_VERSION):
        return None
    return block


def required(manifest: dict | None, current: str | None = None) -> bool:
    minimum = str((manifest or {}).get("min_launcher") or "").strip()
    if not minimum:
        return False
    return is_newer(minimum, current or config.LAUNCHER_VERSION)


def should_block(manifest: dict | None, current: str | None = None) -> bool:
    block = launcher_block(manifest)
    return required(manifest, current) or bool(block.get("mandatory") and available(manifest, current))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for piece in iter(lambda: handle.read(CHUNK), b""):
            digest.update(piece)
    return digest.hexdigest()


def _replace_with_retry(source: Path, dest: Path, attempts: int = 12, delay: float = 0.5) -> None:
    """os.replace, который переживает короткую занятость файла (антивирус, второй процесс)."""
    last: OSError | None = None
    for attempt in range(max(1, attempts)):
        try:
            os.replace(source, dest)
            return
        except OSError as exc:
            last = exc
            if attempt + 1 < attempts:
                time.sleep(delay)
    if last is not None:
        raise last


def _clear_stale_parts(target: Path) -> None:
    """Убирает недокачанные .part прошлых запусков, иначе они блокируют новую загрузку."""
    part = target.with_name(target.name + ".part")
    if not part.exists():
        return
    try:
        with part.open("ab"):
            pass
    except OSError:
        return
    try:
        part.unlink()
    except OSError:
        pass


def download(url: str, dest: Path, timeout: int = 120) -> Path:
    from .util import LauncherError, ssl_context

    dest.parent.mkdir(parents=True, exist_ok=True)
    part = dest.with_name(dest.name + ".part")
    _clear_stale_parts(dest)
    request = urllib.request.Request(url)
    request.add_header("User-Agent", f"CraftunityLauncher/{config.LAUNCHER_VERSION}")
    try:
        with urllib.request.urlopen(request, timeout=timeout, context=ssl_context()) as response, part.open("wb") as handle:
            size = int(response.headers.get("Content-Length") or 0)
            received = 0
            while True:
                piece = response.read(CHUNK)
                if not piece:
                    break
                handle.write(piece)
                received += len(piece)
            if size and received != size:
                raise LauncherError(f"Загрузка оборвалась: {received} из {size} байт")
        os.replace(part, dest)
    except LauncherError:
        part.unlink(missing_ok=True)
        raise
    except urllib.error.HTTPError as exc:
        part.unlink(missing_ok=True)
        raise LauncherError(f"Файл обновления недоступен ({url}): HTTP {exc.code}") from exc
    except urllib.error.URLError as exc:
        part.unlink(missing_ok=True)
        raise LauncherError(f"Файл обновления недоступен ({url}): {exc.reason}") from exc
    except OSError as exc:
        try:
            with part.open("ab"):
                pass
            part.unlink()
        except OSError:
            pass
        raise LauncherError(f"Не удалось сохранить обновление: {exc}") from exc
    return dest


def _candidates(block: dict, primary: str) -> list[str]:
    """Основной адрес сборки и запасные зеркала из того же блока манифеста."""
    result = [primary]
    mirrors = block.get("mirrors")
    if isinstance(mirrors, str):
        mirrors = [mirrors]
    if isinstance(mirrors, list):
        for item in mirrors:
            url = str(item or "").strip()
            if url.startswith(("http://", "https://")) and url not in result:
                result.append(url)
    return result


def stage(block: dict, timeout: int = 120) -> Path:
    """Скачивает новый exe рядом с профилем и проверяет контрольную сумму."""
    from .util import LauncherError

    version = str(block.get("version") or "").strip()
    url = str(block.get("url") or "").strip()
    expected = str(block.get("sha256") or "").strip().lower()
    if not url or not version:
        raise LauncherError("В манифесте нет данных об обновлении лаунчера.")
    target = pending_exe_path()
    last_error: Exception | None = None
    for candidate in _candidates(block, url):
        try:
            download(candidate, target, timeout=timeout)
        except LauncherError as exc:
            last_error = exc
            continue
        actual = _sha256(target)
        if expected and actual != expected:
            target.unlink(missing_ok=True)
            last_error = LauncherError("Контрольная сумма обновления не совпала — файл удалён.")
            continue
        break
    if last_error is not None:
        raise last_error
    actual = _sha256(target)
    meta = {
        "version": version,
        "sha256": actual,
        "url": url,
        "notes": str(block.get("notes") or ""),
        "mandatory": bool(block.get("mandatory")),
        "staged_at": int(time.time()),
    }
    config.save_json(pending_meta_path(), meta)
    return target


def pending_info() -> dict | None:
    """Возвращает данные о готовом к установке обновлении, если файл цел."""
    meta = config.load_json(pending_meta_path())
    target = pending_exe_path()
    if not target.exists() or not meta.get("version"):
        return None
    if meta.get("sha256") and _sha256(target) != str(meta["sha256"]).lower():
        return None
    return meta


def applied_path() -> Path:
    return update_dir() / "applied.json"


def mark_applied(version: str) -> None:
    try:
        config.save_json(applied_path(), {"version": str(version), "at": int(time.time())})
    except OSError:
        pass


def just_applied(seconds: int = 180) -> bool:
    """Защита от цикла: только что перезапустились под эту версию."""
    data = config.load_json(applied_path())
    stamp = int(data.get("at") or 0)
    return bool(stamp) and (time.time() - stamp) < seconds


def discard_pending() -> None:
    pending_exe_path().unlink(missing_ok=True)
    pending_meta_path().unlink(missing_ok=True)


def orphan_copies() -> list[Path]:
    exe = Path(sys.executable)
    found: list[Path] = []
    try:
        for path in exe.parent.glob("*.old"):
            if path.is_file() and path.name.startswith(exe.name):
                found.append(path)
    except OSError:
        pass
    try:
        for path in update_dir().glob("*.old"):
            if path.is_file() and path not in found:
                found.append(path)
    except OSError:
        pass
    return found


def cleanup_temp(keep_seconds: int = 3600) -> int:
    """Убирает папки распаковки _MEI во временном каталоге, оставшиеся от прошлых запусков.

    Onefile-сборка распаковывается в %TEMP%\\_MEIxxxxx и убирает папку после выхода.
    Если процесс упал или его убили, папка остаётся; со временем их становится столько,
    что распаковка нового запуска падает с ошибкой pyi_rth_multiprocessing.
    """
    import tempfile

    if os.name != "nt":
        return 0
    current = getattr(sys, "_MEIPASS", "") or ""
    root = Path(tempfile.gettempdir())
    removed = 0
    try:
        candidates = list(root.glob("_MEI*"))
    except OSError:
        return 0
    now = time.time()
    for folder in candidates:
        try:
            if not folder.is_dir():
                continue
            if current and Path(current).resolve() == folder.resolve():
                continue
            age = now - folder.stat().st_mtime
            if keep_seconds and age < keep_seconds:
                continue
        except OSError:
            continue
        try:
            shutil.rmtree(folder, ignore_errors=False)
            removed += 1
        except OSError:
            continue
    if removed:
        _log_update(f"удалено старых папок распаковки: {removed}")
    return removed


def cleanup_old(attempts: int = 4, delay: float = 1.5) -> int:
    """Удаляет старые копии exe; повторяет попытки, пока файл занят системой."""
    if not is_frozen():
        return 0
    removed = 0
    for attempt in range(max(1, attempts)):
        busy: list[Path] = []
        for path in orphan_copies():
            try:
                path.unlink()
                removed += 1
                _log_update(f"удалена старая копия: {path.name}")
            except OSError:
                busy.append(path)
        if not busy:
            break
        if attempt + 1 < attempts:
            time.sleep(delay)
    return removed


def _swap_files(exe: Path, fresh: Path) -> None:
    """Меняет exe на скачанный; старая копия уносится в папку обновлений, а не рядом с ярлыком."""
    old = update_dir() / (exe.name + ".old")
    try:
        old.unlink(missing_ok=True)
    except OSError:
        pass
    shutil.move(str(exe), str(old))
    try:
        os.replace(fresh, exe)
    except OSError:
        shutil.move(str(old), str(exe))
        raise


def apply_pending(relaunch_args: list[str] | None = None) -> bool:
    """Подменяет текущий exe на скачанный и перезапускает лаунчер. True — нужно выйти."""
    if not is_frozen():
        return False
    info = pending_info()
    if not info:
        discard_pending()
        return False
    exe = Path(sys.executable).resolve()
    fresh = pending_exe_path()
    try:
        _swap_files(exe, fresh)
    except OSError as exc:
        _log_update(f"Не удалось подменить exe: {exc}")
        return False
    pending_meta_path().unlink(missing_ok=True)
    mark_applied(str(info.get("version") or ""))
    args = [str(exe), *(relaunch_args or ["--updated"])]
    try:
        subprocess.Popen(args, cwd=str(exe.parent), close_fds=True)
    except OSError as exc:
        _log_update(f"Не удалось перезапустить лаунчер: {exc}")
        return False
    return True


APPLY_SCRIPT = "apply_update.cmd"

APPLY_BATCH = """@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion
set "TARGET=%~1"
set "FRESH=%~2"
set "OLD=%~3"
set /a TRIES=0
:wait
if not exist "%FRESH%" goto launch
if not exist "%OLD%" (
  move /y "%TARGET%" "%OLD%" >nul 2>&1
)
if exist "%OLD%" (
  move /y "%FRESH%" "%TARGET%" >nul 2>&1
)
if not exist "%FRESH%" goto launch
set /a TRIES+=1
if !TRIES! GEQ 120 goto giveup
ping -n 2 127.0.0.1 >nul
goto wait
:launch
start "" "%TARGET%" --updated
set /a TRIES=0
:cleanup
if not exist "%OLD%" goto done
del /f /q "%OLD%" >nul 2>&1
if not exist "%OLD%" goto done
set /a TRIES+=1
if !TRIES! GEQ 30 goto done
ping -n 2 127.0.0.1 >nul
goto cleanup
:done
del "%~f0" >nul 2>&1
exit /b 0
:giveup
exit /b 1
"""


def schedule_apply() -> bool:
    """Ставит обновление в фоне: лаунчер выходит, скрипт ждёт и подменяет exe.

    Нужен, потому что на Windows нельзя заменить запущенный exe — только переименовать,
    а onefile-сборка держит файл до самого выхода процесса.
    """
    if not is_frozen():
        return False
    info = pending_info()
    if not info:
        discard_pending()
        return False
    exe = Path(sys.executable).resolve()
    fresh = pending_exe_path()
    old = update_dir() / (exe.name + ".old")
    try:
        old.unlink(missing_ok=True)
    except OSError:
        pass
    script = update_dir() / APPLY_SCRIPT
    try:
        script.write_text(APPLY_BATCH, encoding="utf-8")
    except OSError as exc:
        _log_update(f"Не удалось создать скрипт обновления: {exc}")
        return False
    flags = 0
    startup = None
    if os.name == "nt":
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = getattr(subprocess, "SW_HIDE", 0)
    try:
        subprocess.Popen(
            ["cmd", "/c", str(script), str(exe), str(fresh), str(old)],
            cwd=str(exe.parent),
            close_fds=True,
            creationflags=flags,
            startupinfo=startup,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except OSError as exc:
        _log_update(f"Не удалось запустить скрипт обновления: {exc}")
        return False
    _log_update(f"Обновление {info.get('version')} ставится в фоне после выхода лаунчера")
    return True


def _log_update(line: str) -> None:
    try:
        stamp = time.strftime("%Y-%m-%d %H:%M:%S")
        with (update_dir() / "update.log").open("a", encoding="utf-8") as handle:
            handle.write(f"{stamp} {line}\n")
    except OSError:
        pass


def check(manifest: dict | None, current: str | None = None) -> dict:
    """Тихая проверка: скачивает обновление, если оно есть. Ничего не применяет."""
    version = current or config.LAUNCHER_VERSION
    block = available(manifest, version)
    if not block:
        return {"state": "current", "version": version}
    info = pending_info()
    if info and version_key(str(info.get("version"))) >= version_key(str(block.get("version"))):
        return {"state": "staged", "version": version, "next": info.get("version")}
    try:
        stage(block)
    except Exception as exc:  # noqa: BLE001
        _log_update(f"Обновление {block.get('version')} не скачалось: {exc}")
        return {"state": "error", "version": version, "error": str(exc)}
    _log_update(f"Обновление {block.get('version')} готово к установке при следующем запуске")
    return {"state": "staged", "version": version, "next": block.get("version")}


def describe_pending() -> str:
    info = pending_info()
    if not info:
        return ""
    return f"Обновление {info.get('version')} установится при следующем запуске"
