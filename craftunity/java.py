from __future__ import annotations

import shutil
import zipfile
from pathlib import Path

from . import config
from .installer import java_major
from .util import LauncherError, download

ADOPTIUM_URL = (
    "https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse"
)


def runtime_dir() -> Path:
    path = config.data_dir() / "runtime"
    path.mkdir(parents=True, exist_ok=True)
    return path


def java_exe(root: Path) -> Path:
    return root / "bin" / "java.exe"


def cached_runtime(major: int) -> str:
    exe = java_exe(runtime_dir() / f"jre-{major}")
    if exe.exists() and java_major(str(exe)) >= major:
        return str(exe)
    return ""


def ensure_runtime(major: int, progress) -> str:
    """Скачивает Temurin JRE нужной версии в папку лаунчера и возвращает путь к java."""
    existing = cached_runtime(major)
    if existing:
        return existing

    archive = config.cache_dir() / f"temurin-jre-{major}.zip"
    url = ADOPTIUM_URL.format(major=major)

    def report(done: int, total: int) -> None:
        total = total or 1
        progress("java", done, total, f"Скачиваю Java {major} ({done // (1024 * 1024)} МБ)")

    progress("java", 0, 1, f"Скачиваю Java {major}…")
    if archive.exists() and archive.stat().st_size < 1024 * 1024:
        archive.unlink(missing_ok=True)
    download(url, archive, None, None, progress=report)

    tmp = runtime_dir() / f"jre-{major}-tmp"
    if tmp.exists():
        shutil.rmtree(tmp, ignore_errors=True)
    tmp.mkdir(parents=True, exist_ok=True)
    try:
        with zipfile.ZipFile(archive) as bundle:
            bundle.extractall(tmp)
        found = next(iter(tmp.glob("*/bin/java.exe")), None) or next(iter(tmp.glob("**/bin/java.exe")), None)
        if not found:
            raise LauncherError("В скачанной Java не найден java.exe.")
        root = found.parent.parent
        target = runtime_dir() / f"jre-{major}"
        if target.exists():
            shutil.rmtree(target, ignore_errors=True)
        shutil.move(str(root), str(target))
    except zipfile.BadZipFile as exc:
        raise LauncherError("Архив Java повреждён, попробуйте ещё раз.") from exc
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        archive.unlink(missing_ok=True)

    exe = java_exe(runtime_dir() / f"jre-{major}")
    if not exe.exists():
        raise LauncherError("Не удалось установить Java. Установите Java вручную и укажите путь в настройках.")
    if java_major(str(exe)) < major:
        raise LauncherError("Скачанная Java не подходит по версии. Укажите путь к Java вручную.")
    return str(exe)
