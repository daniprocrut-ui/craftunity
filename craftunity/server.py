from __future__ import annotations

import json
import mimetypes
import os
import secrets
import socketserver
import threading
import time
import urllib.parse
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from . import auth, catalog, config, files, holidays, installer, manifest, mods, skins, state, translate
from .launch import Engine
from .util import LauncherError, human_size

ENGINE = Engine()
API_TOKEN = secrets.token_urlsafe(24)
ADMIN_TOKENS: set[str] = set()
ADMIN_PASSWORD = os.environ.get("CRAFTUNITY_ADMIN_PASSWORD", "craftunity")
CLIENTS_FILE = "admin/clients.json"


def _clients_path() -> Path:
    path = config.data_dir() / "admin" / "clients.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def _read_clients() -> dict:
    return config.load_json(_clients_path(), {"clients": {}})


def _write_clients(data: dict) -> None:
    config.save_json(_clients_path(), data)


def _overview() -> dict:
    settings = config.load_settings()
    account = state.current_account()
    manifest_data = ENGINE.manifest
    return {
        "launcher_version": config.LAUNCHER_VERSION,
        "instance_id": state.instance_id(),
        "settings": settings,
        "account": account,
        "manifest": manifest_data,
        "manifest_error": ENGINE.manifest_error,
        "manifest_source": ENGINE.manifest_source,
        "manifest_fetched_at": ENGINE.manifest_fetched_at,
        "status": ENGINE.snapshot(),
        "installed_mods": len(mods.installed()),
        "holiday": holidays.active(),
        "skin": skins.local_skin(),
        "game_dir": str(config.game_dir()),
        "java": settings.get("java_path") or installer.detect_java(),
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "CraftunityLauncher/" + config.LAUNCHER_VERSION
    mode = "client"

    # ------------------------------------------------------------------ utils

    def log_message(self, fmt: str, *args) -> None:  # noqa: A003
        return

    def _send(self, payload, status: int = 200, content_type: str = "application/json") -> None:
        if isinstance(payload, (dict, list)):
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            content_type = "application/json; charset=utf-8"
        elif isinstance(payload, str):
            body = payload.encode("utf-8")
        else:
            body = payload
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _error(self, message: str, status: int = 400) -> None:
        self._send({"ok": False, "error": message}, status)

    def _read_json(self) -> dict:
        length = int(self.headers.get("Content-Length") or 0)
        if not length:
            return {}
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode("utf-8"))
        except ValueError:
            return {}

    def _read_body(self, limit: int = 4 * 1024 * 1024) -> bytes:
        length = int(self.headers.get("Content-Length") or 0)
        if length > limit:
            raise LauncherError("Файл слишком большой.")
        return self.rfile.read(length) if length else b""

    def _cookie(self, name: str) -> str:
        raw = self.headers.get("Cookie") or ""
        for chunk in raw.split(";"):
            if "=" in chunk:
                key, value = chunk.strip().split("=", 1)
                if key == name:
                    return value
        return ""

    def _is_admin(self) -> bool:
        return self._cookie("craftunity_admin") in ADMIN_TOKENS

    # ------------------------------------------------------------------- HTTP

    def do_GET(self) -> None:  # noqa: N802
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        try:
            if path in ("/", "/index.html"):
                return self._static("index.html")
            if path == "/admin" or path.startswith("/admin/"):
                if path == "/admin/login":
                    return self._static("admin_login.html")
                return self._static("admin.html")
            if path.startswith("/static/"):
                return self._static(path[len("/static/"):])
            if path == "/api/state":
                return self._send(ENGINE.snapshot())
            if path == "/api/overview":
                return self._send(_overview())
            if path == "/api/mods/installed":
                items = mods.installed_all()["items"]
                return self._send({"mods": items, "items": items})
            if path.startswith("/api/icon/"):
                project_id = urllib.parse.unquote(path.rsplit("/", 1)[-1])
                cached = mods.icon_local(project_id)
                if cached is None:
                    return self._error("Иконка не найдена", 404)
                content_type = mimetypes.guess_type(str(cached))[0] or "image/png"
                return self._send(cached.read_bytes(), content_type=content_type)
            if path.startswith("/api/project/"):
                project_id = urllib.parse.unquote(path.rsplit("/", 1)[-1])
                if project_id.startswith("cf:"):
                    if not project_id[3:].isdigit():
                        return self._error("Некорректный проект.", 400)
                elif project_id.startswith("file:"):
                    return self._send({"ok": True, "project": mods.local_project_info(project_id)})
                elif not project_id or not all(ch.isalnum() or ch in "-_" for ch in project_id):
                    return self._error("Некорректный проект.", 400)
                return self._send({"ok": True, "project": mods.project_info(project_id)})
            if path == "/api/files/list":
                query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
                dest = (query.get("dest") or ["game"])[0]
                return self._send({"ok": True, **files.list_files(dest)})
            if path == "/api/skin/current":
                return self._send({"local": skins.local_skin()})
            if path == "/api/versions":
                return self._versions()
            if path == "/api/manifest.json":
                publish = config.data_dir() / "admin" / "publish" / "manifest.json"
                if not publish.exists():
                    return self._error("Манифест ещё не опубликован.", 404)
                return self._send(publish.read_bytes(), content_type="application/json")
            if path == "/api/manifest.json.sig":
                publish = config.data_dir() / "admin" / "publish" / "manifest.json.sig"
                if not publish.exists():
                    return self._error("Подпись не найдена.", 404)
                return self._send(publish.read_bytes(), content_type="text/plain")
            if path == "/api/admin/manifest":
                if not self._is_admin():
                    return self._error("Требуется вход в админ-панель.", 401)
                return self._send({"manifest": manifest.manifest_for_admin()})
            if path == "/api/admin/clients":
                if not self._is_admin():
                    return self._error("Требуется вход в админ-панель.", 401)
                clients = list((_read_clients().get("clients") or {}).values())
                clients.sort(key=lambda item: item.get("last_seen", 0), reverse=True)
                return self._send({"clients": clients})
            if path == "/api/admin/session":
                return self._send({"authenticated": self._is_admin()})
            return self._error("Не найдено", 404)
        except LauncherError as exc:
            return self._error(str(exc), 400)
        except Exception as exc:  # noqa: BLE001
            return self._error(f"Внутренняя ошибка: {exc}", 500)

    def do_POST(self) -> None:  # noqa: N802
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        try:
            if path == "/api/account/offline":
                data = self._read_json()
                account = state.set_offline_account((data.get("username") or "").strip())
                return self._send({"ok": True, "account": account})
            if path == "/api/account/ely":
                data = self._read_json()
                account = auth.ely_authenticate((data.get("username") or "").strip(), data.get("password") or "")
                return self._send({"ok": True, "account": account})
            if path == "/api/account/logout":
                state.logout()
                return self._send({"ok": True})
            if path == "/api/install":
                threading.Thread(target=_safe_prepare, kwargs={"deep": False}, daemon=True).start()
                return self._send({"ok": True, "started": True})
            if path == "/api/repair":
                threading.Thread(target=_safe_prepare, kwargs={"deep": True}, daemon=True).start()
                return self._send({"ok": True, "started": True, "deep": True})
            if path == "/api/launch":
                return self._send({"ok": True, **ENGINE.launch()})
            if path == "/api/stop":
                return self._send({"ok": True, **ENGINE.kill()})
            if path == "/api/mods/search":
                data = self._read_json()
                settings = config.load_settings()
                manifest_game = ((ENGINE.manifest or {}).get("game") or {})
                version = manifest_game.get("version") or settings["game_version"]
                loader = "fabric"
                kind = data.get("kind") or "mod"
                result = catalog.search_all(
                    data.get("query") or "",
                    version,
                    loader,
                    int(data.get("limit") or 20),
                    int(data.get("offset") or 0),
                    data.get("sort") or "relevance",
                    kind,
                )
                return self._send(result)
            if path == "/api/mods/install":
                data = self._read_json()
                settings = config.load_settings()
                manifest_game = ((ENGINE.manifest or {}).get("game") or {})
                version = manifest_game.get("version") or settings["game_version"]
                loader = "fabric"
                item = mods.install(
                    data.get("project_id") or "",
                    version,
                    loader,
                    kind=data.get("kind") or "mod",
                )
                return self._send({"ok": True, "mod": item})
            if path == "/api/content/upload":
                query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
                kind = (query.get("kind") or ["resourcepack"])[0]
                name = urllib.parse.unquote((query.get("name") or ["pack.zip"])[0])
                body = self._read_body(128 * 1024 * 1024)
                item = mods.install_local_upload(body, name, kind)
                return self._send({"ok": True, "item": item})
            if path == "/api/content/toggle":
                data = self._read_json()
                result = mods.set_enabled(
                    project_id=str(data.get("project_id") or ""),
                    kind=str(data.get("kind") or ""),
                    filename=str(data.get("filename") or ""),
                    enabled=bool(data.get("enabled")),
                )
                return self._send({"ok": True, **result})
            if path == "/api/content/remove-file":
                data = self._read_json()
                removed = mods.remove_local_file(data.get("kind") or "", data.get("filename") or "")
                return self._send({"ok": True, **removed})
            if path == "/api/files/upload":
                query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
                dest = (query.get("dest") or ["game"])[0]
                name = urllib.parse.unquote((query.get("name") or ["file.bin"])[0])
                overwrite = (query.get("overwrite") or ["0"])[0] in ("1", "true", "yes")
                body = self._read_body(files.MAX_FILE_BYTES + 1024)
                item = files.save_file(dest, name, body, overwrite)
                return self._send({"ok": True, "file": item})
            if path == "/api/translate":
                data = self._read_json()
                text = str(data.get("text") or "")
                if len(text) > 12000:
                    text = text[:12000]
                target = str(data.get("target") or "ru")[:8]
                translated = translate.translate(text, target)
                return self._send({"ok": True, "text": translated})
            if path == "/api/files/delete":
                data = self._read_json()
                removed = files.delete_file(data.get("dest") or "game", data.get("name") or "")
                return self._send({"ok": True, **removed})
            if path == "/api/mods/remove":
                data = self._read_json()
                removed = mods.remove_project(data.get("project_id") or "")
                return self._send({"ok": True, "mod": removed})
            if path == "/api/skin/upload":
                body = self._read_body(2 * 1024 * 1024)
                model = self.headers.get("X-Skin-Model", "classic")
                local = skins.apply_local(body, model)
                return self._send({"ok": True, "skin": local})
            if path == "/api/skin/ely":
                body = self._read_body(2 * 1024 * 1024)
                model = self.headers.get("X-Skin-Model", "classic")
                result = skins.upload_ely_skin(body, model)
                return self._send({"ok": True, **result})
            if path == "/api/settings":
                data = self._read_json()
                settings = config.save_settings(data)
                return self._send({"ok": True, "settings": settings})
            if path == "/api/curseforge/check":
                from . import curseforge

                data = self._read_json()
                key = str(data.get("key") or "").strip()
                try:
                    curseforge.check_key(key)
                except LauncherError as exc:
                    return self._send({"ok": False, "error": str(exc)})
                config.save_settings({"curseforge_key": key})
                return self._send({"ok": True, "saved": True})
            if path == "/api/manifest/refresh":
                manifest_data = ENGINE.refresh_manifest()
                return self._send({"ok": bool(manifest_data), "manifest": manifest_data, "error": ENGINE.manifest_error})
            if path == "/api/open":
                data = self._read_json()
                _open_folder(data.get("target") or "game")
                return self._send({"ok": True})
            if path == "/api/open-url":
                data = self._read_json()
                _open_url(data.get("url") or "")
                return self._send({"ok": True})
            if path == "/api/heartbeat":
                data = self._read_json()
                clients = _read_clients()
                key = data.get("instance_id") or "unknown"
                clients.setdefault("clients", {})[key] = {
                    **data,
                    "last_seen": int(time.time()),
                }
                _write_clients(clients)
                return self._send({"ok": True})
            if path == "/api/admin/login":
                if auth_disabled():
                    return self._send({"ok": True})
                data = self._read_json()
                if not secrets.compare_digest(str(data.get("password") or ""), ADMIN_PASSWORD):
                    return self._error("Неверный пароль.", 403)
                token = secrets.token_urlsafe(24)
                ADMIN_TOKENS.add(token)
                self.send_response(200)
                body = json.dumps({"ok": True}).encode()
                self.send_header("Set-Cookie", f"craftunity_admin={token}; Path=/; HttpOnly; SameSite=Strict")
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            if path == "/api/admin/logout":
                ADMIN_TOKENS.discard(self._cookie("craftunity_admin"))
                return self._send({"ok": True})
            if path == "/api/admin/manifest":
                if not self._is_admin():
                    return self._error("Требуется вход в админ-панель.", 401)
                data = self._read_json()
                saved = manifest.save_admin_manifest(data.get("manifest") or manifest.DEFAULT_MANIFEST)
                return self._send({"ok": True, "manifest": saved})
            if path == "/api/admin/publish":
                if not self._is_admin():
                    return self._error("Требуется вход в админ-панель.", 401)
                current = manifest.manifest_for_admin()
                saved = manifest.save_admin_manifest(current)
                return self._send({"ok": True, "manifest": saved})
            return self._error("Не найдено", 404)
        except LauncherError as exc:
            return self._error(str(exc), 400)
        except Exception as exc:  # noqa: BLE001
            return self._error(f"Внутренняя ошибка: {exc}", 500)

    # --------------------------------------------------------------- handlers

    def _versions(self) -> None:
        try:
            data = ENGINE.version_manifest() if hasattr(ENGINE, "version_manifest") else None
        except Exception:
            data = None
        if not data:
            data = installer.Installer().version_manifest()
        release = data.get("latest", {})
        versions = [v["id"] for v in data.get("versions", []) if v.get("type") == "release"][:60]
        return self._send({"latest": release, "versions": versions})

    def _static(self, name: str) -> None:
        safe = Path(name).name
        root = config.resource_path("ui")
        target = root / safe
        if not target.exists():
            return self._error("Файл не найден", 404)
        content_type = mimetypes.guess_type(str(target))[0] or "application/octet-stream"
        if content_type.startswith("text/") or content_type in ("application/javascript", "application/json"):
            content_type += "; charset=utf-8"
        return self._send(target.read_bytes(), content_type=content_type)


def auth_disabled() -> bool:
    return os.environ.get("CRAFTUNITY_ADMIN_NO_PASSWORD") == "1"


def _safe_prepare(deep: bool = False) -> None:
    try:
        ENGINE.prepare(force=False, deep=deep)
    except LauncherError as exc:
        ENGINE._fail(str(exc))  # noqa: SLF001
    except Exception as exc:  # noqa: BLE001
        ENGINE._fail(f"Ошибка установки: {exc}")  # noqa: SLF001


ALLOWED_URL_HOSTS = {
    "account.ely.by",
    "ely.by",
    "www.ely.by",
    "craftunity.mcsh.io",
    "modrinth.com",
    "www.modrinth.com",
    "curseforge.com",
    "www.curseforge.com",
    "legacy.curseforge.com",
    "console.curseforge.com",
}


def _open_url(url: str) -> None:
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != "https" or (parsed.hostname or "") not in ALLOWED_URL_HOSTS:
        raise LauncherError("Ссылка не разрешена.")
    import webbrowser

    webbrowser.open(url)


def _open_folder(target: str) -> None:
    import subprocess
    import sys

    mapping = {
        "game": config.game_dir(),
        "mods": config.game_dir() / "mods",
        "logs": config.game_dir() / "logs",
        "data": config.data_dir(),
        "admin": config.data_dir() / "admin" / "publish",
    }
    path = mapping.get(target, config.game_dir())
    path.mkdir(parents=True, exist_ok=True)
    if sys.platform == "win32":
        os.startfile(str(path))  # type: ignore[attr-defined]
    else:
        subprocess.Popen(["xdg-open", str(path)])


class LocalHTTPServer(ThreadingHTTPServer):
    """Локальный сервер без обратного резолва имени.

    Штатный server_bind() зовёт socket.getfqdn(), а тот тянет encodings.idna,
    которого нет в сборке PyInstaller — получался LookupError: unknown encoding: idna.
    """

    daemon_threads = True

    def server_bind(self) -> None:
        socketserver.TCPServer.server_bind(self)
        host, port = self.server_address[:2]
        self.server_name = str(host)
        self.server_port = int(port)


def serve(port: int = 0, mode: str = "client", host: str = "127.0.0.1") -> tuple[ThreadingHTTPServer, int]:
    Handler.mode = mode
    server = LocalHTTPServer((host, port), Handler)
    server.daemon_threads = True
    actual = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, actual
