from __future__ import annotations

import json
import os
import shutil
from pathlib import Path

from . import config

SERVER_FOLDER = "craftunity"
LINKS_NAME = "dropbox.json"


def dropbox_root() -> Path | None:
    """Папка синхронизации Dropbox на этом компьютере, если она есть."""
    info = Path(os.environ.get("LOCALAPPDATA", "")) / "Dropbox" / "info.json"
    try:
        data = json.loads(info.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        data = {}
    for key in ("personal", "business"):
        path = (data.get(key) or {}).get("path")
        if path and Path(path).exists():
            return Path(path)
    fallback = Path.home() / "Dropbox"
    return fallback if fallback.exists() else None


def server_dir(root: Path | None = None) -> Path | None:
    base = root if root is not None else dropbox_root()
    return base / SERVER_FOLDER if base else None


def settings_payload(manifest: dict) -> dict:
    return {
        "schema": manifest.get("schema"),
        "updated_at": manifest.get("updated_at", ""),
        "message": manifest.get("message", ""),
        "game": manifest.get("game") or {},
        "server": manifest.get("server") or {},
        "jvm": manifest.get("jvm") or {},
        "mods": manifest.get("mods") or [],
        "min_launcher": manifest.get("min_launcher", ""),
        "heartbeat_url": manifest.get("heartbeat_url", ""),
    }


def links_path() -> Path:
    return config.data_dir() / "admin" / LINKS_NAME


def configured_links() -> dict:
    data = config.load_json(links_path(), {})
    return data if isinstance(data, dict) else {}


def launcher_url() -> str:
    """Постоянная ссылка Dropbox на сборку лаунчера, если она прописана в профиле."""
    return str(configured_links().get("launcher_url") or "").strip()


def publish(publish_dir: Path, manifest: dict, root: Path | None = None) -> Path | None:
    """Кладёт подписанный манифест и настройки в папку сервера; None — если Dropbox не найден."""
    if root is not None and not Path(root).exists():
        return None
    target = server_dir(root)
    if target is None:
        return None
    source = Path(publish_dir)
    body = source / "manifest.json"
    signature = source / "manifest.json.sig"
    if not body.exists() or not signature.exists():
        return None
    target.mkdir(parents=True, exist_ok=True)
    shutil.copy2(body, target / "manifest.json")
    shutil.copy2(signature, target / "manifest.json.sig")
    payload = json.dumps(settings_payload(manifest), ensure_ascii=False, indent=2)
    (target / "settings.json").write_text(payload, encoding="utf-8")
    try:
        config.save_json(config.data_dir() / "admin" / "manifest.json", manifest)
    except OSError:
        pass
    return target
