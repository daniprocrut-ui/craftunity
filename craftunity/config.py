from __future__ import annotations

import json
import os
import sys
from pathlib import Path

APP_NAME = "Craftunity"
LAUNCHER_VERSION = "1.5.12"

GITHUB_REPO = "daniprocrut-ui/craftunity"
DEFAULT_MANIFEST_URL = "https://daniprocrut-ui.github.io/craftunity/manifest.json"
DEFAULT_MANIFEST_SIG_URL = "https://raw.githubusercontent.com/daniprocrut-ui/craftunity/main/docs/manifest.json.sig"
MANIFEST_REFRESH_SECONDS = 60
HEARTBEAT_URL = "https://craftunity-heartbeat.netlify.app"
LEGACY_MANIFEST_URLS = (
    "https://craftunity.mcsh.io/launcher/manifest.json",
    "https://daniprocrut-ui.github.io/craftunity/manifest.json",
    "https://www.dropbox.com/scl/fi/ipn0oz4wn0sdl8z9k1cyj/manifest.json?rlkey=knsrqdlptkx2k8fkxxen2o4wl&dl=1",
)
MANIFEST_FALLBACKS: list[str] = [
    "https://raw.githubusercontent.com/daniprocrut-ui/craftunity/main/docs/manifest.json",
    "https://cdn.jsdelivr.net/gh/daniprocrut-ui/craftunity@main/docs/manifest.json",
]
DEFAULT_SERVER_HOST = "craftunity3.mcsh.io"
DEFAULT_SERVER_PORT = 25565
DEFAULT_GAME_VERSION = "1.20.1"
DEFAULT_LOADER = "fabric"
DEFAULT_MEMORY_MB = 4096

VERSION_MANIFEST_URL = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
FABRIC_META = "https://meta.fabricmc.net/v2"
AUTHLIB_INJECTOR_LATEST = "https://authlib-injector.yushi.moe/artifact/latest.json"
ELY_AUTH_ROOT = "https://authserver.ely.by/api/authlib-injector"
ELY_SKIN_PROFILE = "https://authserver.ely.by/api/users/profiles/minecraft/{name}"
ELY_SKIN_UPLOAD = "https://authserver.ely.by/api/mojang/profile/skin"
MODRINTH_API = "https://api.modrinth.com/v2"


def app_root() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


def resource_path(*parts: str) -> Path:
    base = Path(getattr(sys, "_MEIPASS", app_root()))
    return base.joinpath(*parts)


def data_dir() -> Path:
    override = os.environ.get("CRAFTUNITY_HOME")
    if override:
        path = Path(override)
    elif sys.platform == "win32":
        path = Path(os.environ.get("APPDATA", str(Path.home()))) / "Craftunity"
    else:
        path = Path.home() / ".craftunity"
    path.mkdir(parents=True, exist_ok=True)
    return path


def game_dir() -> Path:
    path = data_dir() / "game"
    path.mkdir(parents=True, exist_ok=True)
    return path


def cache_dir() -> Path:
    path = data_dir() / "cache"
    path.mkdir(parents=True, exist_ok=True)
    return path


def settings_path() -> Path:
    return data_dir() / "settings.json"


def accounts_path() -> Path:
    return data_dir() / "accounts.json"


def state_path() -> Path:
    return data_dir() / "state.json"


def settings_defaults() -> dict:
    return {
        "manifest_url": DEFAULT_MANIFEST_URL,
        "manifest_sig_url": DEFAULT_MANIFEST_SIG_URL,
        "heartbeat_url": "",
        "server_host": DEFAULT_SERVER_HOST,
        "server_port": DEFAULT_SERVER_PORT,
        "game_version": DEFAULT_GAME_VERSION,
        "loader": DEFAULT_LOADER,
        "loader_version": "",
        "memory_mb": DEFAULT_MEMORY_MB,
        "java_path": "",
        "auto_java": True,
        "auto_join": True,
        "close_on_launch": False,
        "show_snapshots": False,
        "theme": "dark",
        "content_view": "tiles",
        "reduce_motion": False,
        "accept_manifest": True,
        "curseforge_key": "",
        "holiday_theme": True,
    }


def _merge(defaults: dict, loaded: dict) -> dict:
    result = dict(defaults)
    for key, value in (loaded or {}).items():
        if key in result and value is not None:
            result[key] = value
    return result


def load_json(path: Path, default: dict | None = None) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return dict(default or {})


def save_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, path)


PORTABLE_FILE = "craftunity.json"


def portable_path() -> Path:
    return app_root() / PORTABLE_FILE


def seed_from_portable() -> None:
    """Первый запуск: берём адрес манифеста и сервер из craftunity.json рядом с exe."""
    source = portable_path()
    if not source.exists() or settings_path().exists():
        return
    data = load_json(source)
    if not isinstance(data, dict):
        return
    allowed = set(settings_defaults())
    patch = {key: value for key, value in data.items() if key in allowed}
    if patch:
        save_settings(patch)


def load_settings() -> dict:
    settings = _merge(settings_defaults(), load_json(settings_path()))
    current = settings.get("manifest_url")
    if current in LEGACY_MANIFEST_URLS and DEFAULT_MANIFEST_URL not in LEGACY_MANIFEST_URLS:
        settings["manifest_url"] = DEFAULT_MANIFEST_URL
        if DEFAULT_MANIFEST_SIG_URL and not settings.get("manifest_sig_url"):
            settings["manifest_sig_url"] = DEFAULT_MANIFEST_SIG_URL
    return settings


def save_settings(patch: dict) -> dict:
    settings = _merge(load_settings(), patch)
    save_json(settings_path(), settings)
    return settings
