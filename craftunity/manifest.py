from __future__ import annotations

import base64
import json
import os
import time
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
from cryptography.exceptions import InvalidSignature

from . import config
from .util import LauncherError, canonical_json, ssl_context

MANIFEST_SCHEMA = 1

DEFAULT_MANIFEST = {
    "schema": MANIFEST_SCHEMA,
    "version": "26.3-craftunity.1",
    "updated_at": "",
    "message": "Добро пожаловать на Craftunity!",
    "game": {
        "version": config.DEFAULT_GAME_VERSION,
        "loader": config.DEFAULT_LOADER,
        "loader_version": "",
    },
    "server": {
        "host": config.DEFAULT_SERVER_HOST,
        "port": config.DEFAULT_SERVER_PORT,
        "auto_join": True,
    },
    "jvm": {"args": ["-Xmx4G", "-XX:+UseG1GC"], "java": ""},
    "mods": [],
    "min_launcher": "1.0.0",
    "heartbeat_url": "",
    "launcher": {
        "version": "",
        "url": "",
        "sha256": "",
        "notes": "",
        "mandatory": False,
    },
}


def public_key_path() -> Path:
    return config.resource_path("keys", "craftunity_manifest_pub.pem")


def admin_private_key_path() -> Path:
    return config.data_dir() / "admin" / "manifest_private.pem"


def ensure_admin_keys() -> Path:
    path = admin_private_key_path()
    if path.exists():
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    key = Ed25519PrivateKey.generate()
    path.write_bytes(
        key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
    )
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass
    path.with_name("craftunity_manifest_pub.pem").write_bytes(
        key.public_key().public_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PublicFormat.SubjectPublicKeyInfo,
        )
    )
    return path


def _load_private(path: Path | None = None) -> Ed25519PrivateKey:
    key_path = path or ensure_admin_keys()
    try:
        return serialization.load_pem_private_key(key_path.read_bytes(), password=None)  # type: ignore[return-value]
    except (OSError, ValueError) as exc:
        raise LauncherError(f"Не удалось прочитать приватный ключ {key_path}") from exc


def _load_public(path: Path | None = None) -> Ed25519PublicKey:
    key_path = path or public_key_path()
    if not key_path.exists():
        raise LauncherError(
            "В лаунчере отсутствует публичный ключ манифеста (keys/craftunity_manifest_pub.pem)."
        )
    try:
        return serialization.load_pem_public_key(key_path.read_bytes())  # type: ignore[return-value]
    except (OSError, ValueError) as exc:
        raise LauncherError("Публичный ключ манифеста повреждён.") from exc


def sign_manifest(manifest: dict, key_path: Path | None = None) -> tuple[bytes, bytes]:
    key = _load_private(key_path)
    body = canonical_json(manifest)
    signature = key.sign(body)
    return body, base64.b64encode(signature)


def verify_manifest(body: bytes, signature_b64: bytes | str) -> dict:
    try:
        signature = base64.b64decode(signature_b64, validate=True)
    except Exception as exc:
        raise LauncherError("Подпись манифеста повреждена.") from exc
    try:
        _load_public().verify(signature, body)
    except InvalidSignature as exc:
        raise LauncherError(
            "Манифест лаунчера не подписан ключом Craftunity. Запуск остановлен."
        ) from exc
    try:
        manifest = json.loads(body.decode("utf-8"))
    except ValueError as exc:
        raise LauncherError("Манифест лаунчера не является корректным JSON.") from exc
    if manifest.get("schema") != MANIFEST_SCHEMA:
        raise LauncherError("Лаунчер устарел: неизвестная версия схемы манифеста.")
    return manifest


def cache_path() -> Path:
    return config.data_dir() / "manifest_cache.json"


def save_cached_manifest(body: bytes, signature: bytes | str, url: str) -> None:
    text = signature.decode("ascii", "replace") if isinstance(signature, bytes) else str(signature)
    payload = {
        "url": url,
        "fetched_at": int(time.time()),
        "body": body.decode("utf-8"),
        "signature": text.strip(),
    }
    try:
        config.save_json(cache_path(), payload)
    except OSError:
        pass


def load_cached_manifest() -> tuple[dict, dict] | None:
    data = config.load_json(cache_path())
    body = data.get("body")
    signature = data.get("signature")
    if not body or not signature:
        return None
    try:
        manifest = verify_manifest(str(body).encode("utf-8"), signature)
    except LauncherError:
        return None
    return manifest, {"fetched_at": int(data.get("fetched_at") or 0), "url": data.get("url") or ""}


def fetch_manifest(url: str, timeout: int = 20, verify: bool = True, sig_url: str = "") -> dict:
    body_url = url
    signature_url = sig_url or (url + ".sig")
    request_body = _raw(body_url, timeout)
    if not verify:
        try:
            return json.loads(request_body.decode("utf-8"))
        except ValueError as exc:
            raise LauncherError("Манифест лаунчера не является корректным JSON.") from exc
    signature = _raw(signature_url, timeout).strip()
    manifest = verify_manifest(request_body, signature)
    save_cached_manifest(request_body, signature, url)
    return manifest


def fallback_urls(primary: str = "") -> list[str]:
    """Резервные адреса того же манифеста (зеркала), кроме основного."""
    result: list[str] = []
    for url in config.MANIFEST_FALLBACKS:
        if isinstance(url, str) and url.startswith(("http://", "https://")) and url != primary and url not in result:
            result.append(url)
    return result


def _raw(url: str, timeout: int) -> bytes:
    import urllib.error
    import urllib.request

    request = urllib.request.Request(url)
    request.add_header("User-Agent", "CraftunityLauncher/1.0")
    try:
        with urllib.request.urlopen(request, timeout=timeout, context=ssl_context()) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        raise LauncherError(f"Манифест недоступен ({url}): HTTP {exc.code}") from exc
    except urllib.error.URLError as exc:
        raise LauncherError(f"Манифест недоступен ({url}): {exc.reason}") from exc


def _server_manifest() -> dict | None:
    """Опубликованный манифест из папки сервера, если его подпись сходится."""
    from . import dropbox

    target = dropbox.server_dir()
    if target is None:
        return None
    body_path = target / "manifest.json"
    sig_path = target / "manifest.json.sig"
    if not body_path.exists() or not sig_path.exists():
        return None
    try:
        return verify_manifest(body_path.read_bytes(), sig_path.read_text(encoding="utf-8").strip())
    except (LauncherError, OSError):
        return None


def published_manifest() -> dict | None:
    """Манифест, который сейчас раздаётся клиентам, — копия в папке сервера."""
    return _server_manifest()


def _mine() -> dict | None:
    store = config.data_dir() / "admin" / "manifest.json"
    if not store.exists():
        return None
    try:
        return json.loads(store.read_text(encoding="utf-8"))
    except ValueError:
        return None


def manifest_for_admin() -> dict:
    """Берём более свежий манифест: локальный или уже опубликованный в папке сервера."""
    local = _mine()
    remote = _server_manifest()
    if remote is not None:
        if local is None or str(remote.get("updated_at") or "") > str(local.get("updated_at") or ""):
            try:
                config.save_json(config.data_dir() / "admin" / "manifest.json", remote)
            except OSError:
                pass
            return remote
        return local
    if local is not None:
        return local
    manifest = json.loads(json.dumps(DEFAULT_MANIFEST))
    manifest["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    save_admin_manifest(manifest)
    return manifest


def _version_key(value: str) -> tuple:
    parts = []
    for chunk in str(value or "").replace("-", ".").split("."):
        parts.append(int(chunk) if chunk.isdigit() else 0)
    return tuple(parts)


def save_admin_manifest(manifest: dict) -> dict:
    from . import dropbox

    link = dropbox.launcher_url()
    if link:
        block = dict(manifest.get("launcher") or {})
        block["url"] = link
        manifest["launcher"] = block
    remote = _server_manifest()
    if remote:
        published = remote.get("launcher") or {}
        incoming = manifest.get("launcher") or {}
        if _version_key(published.get("version")) > _version_key(incoming.get("version")):
            merged = dict(incoming)
            merged.update({key: value for key, value in published.items() if key in ("version", "url", "mirrors", "sha256", "notes")})
            manifest["launcher"] = merged
    manifest["schema"] = MANIFEST_SCHEMA
    manifest["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    store = config.data_dir() / "admin" / "manifest.json"
    config.save_json(store, manifest)
    body, signature = sign_manifest(manifest)
    publish = config.data_dir() / "admin" / "publish"
    publish.mkdir(parents=True, exist_ok=True)
    (publish / "manifest.json").write_bytes(body)
    (publish / "manifest.json.sig").write_bytes(signature)
    return manifest


def manifest_summary(manifest: dict) -> dict:
    game = manifest.get("game") or {}
    return {
        "version": manifest.get("version"),
        "game_version": game.get("version"),
        "loader": game.get("loader"),
        "loader_version": game.get("loader_version") or "auto",
        "message": manifest.get("message"),
        "updated_at": manifest.get("updated_at"),
        "mods": len(manifest.get("mods") or []),
        "server": (manifest.get("server") or {}).get("host"),
    }
