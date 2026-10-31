from __future__ import annotations

import os
import time
from pathlib import Path

from . import config
from .util import LauncherError

DESTINATIONS = {
    "game": "",
    "config": "config",
    "mods": "mods",
    "resourcepacks": "resourcepacks",
    "shaderpacks": "shaderpacks",
    "saves": "saves",
}

DESTINATION_TITLES = {
    "game": "Корень игры",
    "config": "config",
    "mods": "mods",
    "resourcepacks": "resourcepacks",
    "shaderpacks": "shaderpacks",
    "saves": "saves",
}

MAX_FILE_BYTES = 256 * 1024 * 1024


def dest_dir(dest: str) -> Path:
    if dest not in DESTINATIONS:
        raise LauncherError("Неизвестная папка для файлов.")
    path = config.game_dir() / DESTINATIONS[dest]
    path.mkdir(parents=True, exist_ok=True)
    return path


def _safe_name(filename: str) -> str:
    name = Path(filename or "").name.strip()
    if not name or name in (".", ".."):
        raise LauncherError("Некорректное имя файла.")
    cleaned = "".join(ch for ch in name if ch.isalnum() or ch in " ._-()[]{}!@#$%+=,")
    cleaned = cleaned.strip(" .")
    if not cleaned:
        raise LauncherError("Некорректное имя файла.")
    return cleaned


def list_files(dest: str) -> dict:
    folder = dest_dir(dest)
    items = []
    for path in folder.iterdir():
        if not path.is_file():
            continue
        stat = path.stat()
        items.append(
            {
                "name": path.name,
                "size": stat.st_size,
                "modified": int(stat.st_mtime),
            }
        )
    items.sort(key=lambda item: item["modified"], reverse=True)
    return {"dest": dest, "title": DESTINATION_TITLES[dest], "path": str(folder), "files": items}


def save_file(dest: str, filename: str, data: bytes, overwrite: bool = False) -> dict:
    folder = dest_dir(dest)
    name = _safe_name(filename)
    if not data:
        raise LauncherError("Файл пустой.")
    if len(data) > MAX_FILE_BYTES:
        raise LauncherError("Файл больше 256 МБ.")
    target = folder / name
    if target.exists() and not overwrite:
        raise LauncherError(f"Файл «{name}» уже есть в папке {DESTINATION_TITLES[dest]}.")
    tmp = target.with_suffix(target.suffix + ".part")
    tmp.write_bytes(data)
    os.replace(tmp, target)
    return {
        "name": name,
        "size": len(data),
        "modified": int(time.time()),
        "dest": dest,
        "path": str(target),
    }


def delete_file(dest: str, filename: str) -> dict:
    folder = dest_dir(dest)
    target = folder / _safe_name(filename)
    if not target.exists():
        raise LauncherError("Файл не найден.")
    target.unlink()
    return {"removed": target.name, "dest": dest}
