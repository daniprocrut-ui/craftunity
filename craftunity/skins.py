from __future__ import annotations

import base64
import json
import mimetypes
import os
import urllib.request
import uuid
from pathlib import Path

from . import config, state
from .util import USER_AGENT, LauncherError, http_json, ssl_context

MAX_SKIN_BYTES = 1024 * 1024


def skin_cache_dir() -> Path:
    path = config.data_dir() / "skins"
    path.mkdir(parents=True, exist_ok=True)
    return path


def save_local_skin(data: bytes, model: str = "classic") -> dict:
    if len(data) > MAX_SKIN_BYTES:
        raise LauncherError("Файл скина больше 1 МБ.")
    if not _is_png(data):
        raise LauncherError("Скин должен быть PNG-файлом 64×64 (или 64×32).")
    name = f"{uuid.uuid4().hex}.png"
    path = skin_cache_dir() / name
    path.write_bytes(data)
    meta = {"file": name, "model": model, "uploaded_at": int(os.path.getmtime(path))}
    config.save_json(skin_cache_dir() / "local.json", meta)
    account = state.current_account()
    if account:
        account["skin_file"] = name
        account["skin_model"] = model
        accounts = state.load_accounts()
        accounts["accounts"][account["type"]] = account
        state.save_accounts(accounts)
    return {"file": name, "model": model, "data_url": data_url(data)}


def _is_png(data: bytes) -> bool:
    return data[:8] == b"\x89PNG\r\n\x1a\n"


def data_url(data: bytes) -> str:
    return "data:image/png;base64," + base64.b64encode(data).decode("ascii")


def local_skin() -> dict | None:
    meta_path = skin_cache_dir() / "local.json"
    if not meta_path.exists():
        return None
    meta = config.load_json(meta_path)
    path = skin_cache_dir() / str(meta.get("file", ""))
    if not path.exists():
        return None
    return {
        "model": meta.get("model", "classic"),
        "source": "local",
        "data_url": data_url(path.read_bytes()),
    }


def _fetch_bytes(url: str, headers: dict | None = None, timeout: int = 25) -> bytes:
    request = urllib.request.Request(url)
    request.add_header("User-Agent", USER_AGENT)
    for key, value in (headers or {}).items():
        request.add_header(key, value)
    with urllib.request.urlopen(request, timeout=timeout, context=ssl_context()) as response:
        return response.read()


def remote_skin(username: str) -> dict | None:
    if not username:
        return None
    try:
        profile = http_json(config.ELY_SKIN_PROFILE.format(name=username), timeout=20)
    except LauncherError:
        return None
    skins = (profile or {}).get("skins") or {}
    skin = skins.get("skin") or {}
    url = skin.get("url")
    if not url:
        return None
    try:
        data = _fetch_bytes(url)
    except Exception:
        return None
    return {
        "source": "ely.by",
        "model": skin.get("model", "classic") or "classic",
        "data_url": data_url(data),
        "url": url,
    }


def fetch_skin_bytes(username: str) -> bytes | None:
    import urllib.parse

    if not username:
        return None
    try:
        return _fetch_bytes(f"https://skinsystem.ely.by/skins/{urllib.parse.quote(username)}", timeout=6)
    except Exception:
        return None


def upload_ely_skin(data: bytes, model: str = "classic") -> dict:
    """ely.by не публикует API загрузки скина — загрузка идёт через сайт аккаунта."""
    raise LauncherError(
        "ely.by не принимает скин через API. Откройте account.ely.by/skins и загрузите скин там — "
        "он применится к аккаунту и появится в игре. Локальный скин можно применить кнопкой рядом."
    )


def apply_local(data: bytes, model: str) -> dict:
    return save_local_skin(data, model)
