from __future__ import annotations

import hashlib
import json
import os
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


def download(url: str, dest: Path, timeout: int = 120) -> Path:
    from .util import LauncherError, ssl_context

    dest.parent.mkdir(parents=True, exist_ok=True)
    part = dest.with_name(dest.name + ".part")
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
        part.unlink(missing_ok=True)
        raise LauncherError(f"Не удалось сохранить обновление: {exc}") from exc
    return dest


def stage(block: dict, timeout: int = 120) -> Path:
    """Скачивает новый exe рядом с профилем и проверяет контрольную сумму."""
    from .util import LauncherError

    version = str(block.get("version") or "").strip()
    url = str(block.get("url") or "").strip()
    expected = str(block.get("sha256") or "").strip().lower()
    if not url or not version:
        raise LauncherError("В манифесте нет данных об обновлении лаунчера.")
    target = pending_exe_path()
    download(url, target, timeout=timeout)
    actual = _sha256(target)
    if expected and actual != expected:
        target.unlink(missing_ok=True)
        raise LauncherError("Контрольная сумма обновления не совпала — файл удалён.")
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


def discard_pending() -> None:
    pending_exe_path().unlink(missing_ok=True)
    pending_meta_path().unlink(missing_ok=True)


def cleanup_old() -> None:
    if not is_frozen():
        return
    exe = Path(sys.executable)
    for candidate in (exe.with_name(exe.name + ".old"), update_dir() / ".." / (exe.name + ".old")):
        try:
            if candidate.exists():
                candidate.unlink()
        except OSError:
            pass


def _swap_files(exe: Path, fresh: Path) -> None:
    old = exe.with_name(exe.name + ".old")
    try:
        old.unlink(missing_ok=True)
    except OSError:
        pass
    os.replace(exe, old)
    try:
        os.replace(fresh, exe)
    except OSError:
        os.replace(old, exe)
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
    args = [str(exe), *(relaunch_args or ["--updated"])]
    try:
        subprocess.Popen(args, cwd=str(exe.parent), close_fds=True)
    except OSError as exc:
        _log_update(f"Не удалось перезапустить лаунчер: {exc}")
        return False
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
