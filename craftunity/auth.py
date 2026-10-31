from __future__ import annotations

import json
import time
import uuid
from pathlib import Path

from . import config, state
from .util import LauncherError, download, http_json, sha256_file

CLIENT_TOKEN_KEY = "ely_client_token"


def _client_token() -> str:
    data = config.load_json(config.state_path())
    token = data.get(CLIENT_TOKEN_KEY)
    if not token:
        token = str(uuid.uuid4())
        data[CLIENT_TOKEN_KEY] = token
        config.save_json(config.state_path(), data)
    return token


def _ely_server_message(message: str) -> str:
    index = message.find('{"')
    if index < 0:
        return ""
    try:
        data = json.loads(message[index:])
    except ValueError:
        return ""
    if not isinstance(data, dict):
        return ""
    return str(data.get("errorMessage") or data.get("error") or "").strip()


def ely_authenticate(username: str, password: str) -> dict:
    username = (username or "").replace("\u00a0", " ").strip()
    password = (password or "").replace("\u00a0", " ")
    if not username or not password.strip():
        raise LauncherError("Укажите логин и пароль ely.by.")
    payload = {
        "username": username,
        "password": password,
        "clientToken": _client_token(),
        "agent": {"name": "Minecraft", "version": 1},
        "requestUser": False,
    }
    try:
        data = http_json(
            f"{config.ELY_AUTH_ROOT}/authserver/authenticate",
            payload=payload,
            method="POST",
            timeout=30,
        )
    except LauncherError as exc:
        message = str(exc)
        detail = _ely_server_message(message)
        lowered = detail.lower()
        if "totp" in lowered or "two factor" in lowered or "2fa" in lowered:
            raise LauncherError(
                "На аккаунте ely.by включена двухфакторная защита — вход из лаунчера она не пропускает. "
                "Отключите 2FA в настройках account.ely.by или входите оффлайн-ником."
            ) from exc
        if "confirm" in lowered or "activat" in lowered or "verif" in lowered:
            raise LauncherError(
                f"ely.by: {detail}. Подтвердите e-mail в личном кабинете account.ely.by."
            ) from exc
        if "HTTP 401" in message or "HTTP 403" in message:
            reason = f"ely.by: {detail.rstrip(".")}. " if detail else ""
            raise LauncherError(
                reason
                + "Логин — ник ely.by или e-mail, пароль — от аккаунта ely.by (не от Minecraft). "
                "Аккаунт создаётся на account.ely.by; если пароль точно верный, проверьте "
                "подтверждение почты и раскладку клавиатуры."
            ) from exc
        if "HTTP 400" in message:
            raise LauncherError(
                f"ely.by отклонил запрос{': ' + detail if detail else ''}. Проверьте логин и пароль."
            ) from exc
        raise
    profile = data.get("selectedProfile") or {}
    if not data.get("accessToken") or not profile.get("name"):
        raise LauncherError("ely.by не выдал сессию. Проверьте логин, пароль и подтверждение почты.")
    account = state.set_ely_account(profile, data["accessToken"], data.get("clientToken") or _client_token())
    account["skin_model"] = account.get("skin_model", "classic")
    return account


def ely_refresh(account: dict) -> dict | None:
    token = account.get("access_token")
    if not token:
        return None
    try:
        data = http_json(
            f"{config.ELY_AUTH_ROOT}/authserver/refresh",
            payload={"accessToken": token, "clientToken": account.get("client_token") or _client_token()},
            method="POST",
            timeout=20,
        )
    except LauncherError:
        return None
    if data.get("accessToken"):
        account["access_token"] = data["accessToken"]
        if data.get("selectedProfile"):
            account["uuid"] = data["selectedProfile"].get("id", account.get("uuid"))
            account["username"] = data["selectedProfile"].get("name", account.get("username"))
        accounts = state.load_accounts()
        accounts["accounts"]["ely"] = account
        state.save_accounts(accounts)
        return account
    return None


def ely_validate(account: dict) -> bool:
    token = account.get("access_token")
    if not token:
        return False
    try:
        http_json(
            f"{config.ELY_AUTH_ROOT}/authserver/validate",
            payload={"accessToken": token, "clientToken": account.get("client_token") or _client_token()},
            method="POST",
            timeout=15,
        )
        return True
    except LauncherError:
        return bool(ely_refresh(account))


def ensure_authlib_injector() -> Path:
    target_dir = config.data_dir() / "libs"
    target_dir.mkdir(parents=True, exist_ok=True)
    meta_path = target_dir / "authlib-injector.json"
    jar: Path | None = None
    if meta_path.exists():
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
            candidate = target_dir / f"authlib-injector-{meta['version']}.jar"
            if candidate.exists() and sha256_file(candidate) == meta.get("sha256"):
                jar = candidate
        except (ValueError, KeyError):
            jar = None
    if jar is None:
        info = http_json(config.AUTHLIB_INJECTOR_LATEST, timeout=30)
        version = info.get("version") or str(int(time.time()))
        jar = target_dir / f"authlib-injector-{version}.jar"
        download(info["download_url"], jar)
        meta_path.write_text(
            json.dumps({"version": version, "sha256": sha256_file(jar)}), encoding="utf-8"
        )
    return jar


def ely_profile_skin(username: str) -> dict:
    data = http_json(config.ELY_SKIN_PROFILE.format(name=username), timeout=20)
    return data or {}
