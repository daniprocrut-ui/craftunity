from __future__ import annotations

import hashlib
import time
import uuid
from pathlib import Path

from . import config


def instance_id() -> str:
    state = config.load_json(config.state_path())
    value = state.get("instance_id")
    if not value:
        value = str(uuid.uuid4())
        state["instance_id"] = value
        config.save_json(config.state_path(), state)
    return value


def load_accounts() -> dict:
    data = config.load_json(config.accounts_path())
    data.setdefault("active", "")
    data.setdefault("accounts", {})
    return data


def save_accounts(data: dict) -> None:
    config.save_json(config.accounts_path(), data)


def offline_uuid(username: str) -> str:
    digest = hashlib.md5(f"OfflinePlayer:{username}".encode("utf-8")).digest()
    return str(uuid.UUID(bytes=digest, version=3))


def current_account() -> dict | None:
    data = load_accounts()
    key = data.get("active")
    if not key:
        return None
    return data["accounts"].get(key)


def set_offline_account(username: str) -> dict:
    if not username or len(username) > 16 or not all(ch.isalnum() or ch == "_" for ch in username):
        from .util import LauncherError

        raise LauncherError("Ник: 3–16 символов, только латиница, цифры и «_».")
    if len(username) < 3:
        from .util import LauncherError

        raise LauncherError("Ник должен содержать минимум 3 символа.")
    account = {
        "type": "offline",
        "username": username,
        "uuid": offline_uuid(username),
        "access_token": "0" * 32,
        "skin_model": "classic",
        "created_at": int(time.time()),
    }
    data = load_accounts()
    data["accounts"]["offline"] = account
    data["active"] = "offline"
    save_accounts(data)
    return account


def set_ely_account(profile: dict, token: str, client_token: str) -> dict:
    account = {
        "type": "ely",
        "username": profile.get("name"),
        "uuid": profile.get("id"),
        "access_token": token,
        "client_token": client_token,
        "skin_model": "classic",
        "created_at": int(time.time()),
    }
    data = load_accounts()
    data["accounts"]["ely"] = account
    data["active"] = "ely"
    save_accounts(data)
    return account


def logout() -> None:
    data = load_accounts()
    data["active"] = ""
    save_accounts(data)


def last_played(version: str) -> None:
    state = config.load_json(config.state_path())
    state["last_version"] = version
    state["last_played"] = int(time.time())
    config.save_json(config.state_path(), state)


def cache_dir(sub: str) -> Path:
    path = config.cache_dir() / sub
    path.mkdir(parents=True, exist_ok=True)
    return path
