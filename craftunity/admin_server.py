from __future__ import annotations

import json
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Callable

from . import config

LOCK = threading.Lock()


def publish_dir() -> Path:
    path = config.data_dir() / "admin" / "publish"
    path.mkdir(parents=True, exist_ok=True)
    return path


def clients_path() -> Path:
    path = config.data_dir() / "admin" / "clients.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def read_clients() -> list[dict]:
    with LOCK:
        data = config.load_json(clients_path(), {"clients": {}})
    items = list((data.get("clients") or {}).values())
    items.sort(key=lambda item: item.get("last_seen", 0), reverse=True)
    return items


def heartbeat_config_path() -> Path:
    path = config.data_dir() / "admin" / "heartbeat.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def read_heartbeat_config() -> dict:
    data = config.load_json(heartbeat_config_path(), {})
    return {
        "url": str(data.get("url") or "").strip(),
        "token": str(data.get("token") or "").strip(),
    }


def save_heartbeat_config(url: str, token: str) -> dict:
    payload = {"url": (url or "").strip(), "token": (token or "").strip()}
    config.save_json(heartbeat_config_path(), payload)
    return payload


def read_remote_clients(timeout: int = 12) -> list[dict]:
    """Клиенты с внешнего приёмника heartbeat; пустой список, если он недоступен."""
    from .util import http_json

    settings = read_heartbeat_config()
    if not settings["url"]:
        return []
    base = settings["url"].rstrip("/")
    # В настройках часто указан .../heartbeat — для чтения клиентов нужен путь /clients
    if base.endswith("/heartbeat"):
        base = base[: -len("/heartbeat")]
    url = base if base.endswith("/clients") else base + "/clients"
    if settings["token"]:
        url += ("&" if "?" in url else "?") + "token=" + urllib.parse.quote(settings["token"])
    try:
        payload = http_json(url, timeout=timeout)
    except Exception:
        return []
    clients = payload.get("clients") if isinstance(payload, dict) else None
    if not isinstance(clients, list):
        return []
    normalized = []
    for item in clients:
        if not isinstance(item, dict):
            continue
        entry = dict(item)
        entry["username"] = str(entry.get("username") or "").strip()
        entry["icon"] = str(entry.get("icon") or "")
        normalized.append(entry)
    return normalized



def clients_for_admin() -> list[dict]:
    """Объединяет записи приёмника и локального файла, оставляя самую свежую по клиенту."""
    merged: dict[str, dict] = {}
    for source in (read_clients(), read_remote_clients()):
        for item in source:
            key = str(item.get("instance_id") or "")
            if not key:
                continue
            current = merged.get(key)
            if current is None or int(item.get("last_seen") or 0) >= int(current.get("last_seen") or 0):
                merged[key] = item
    items = list(merged.values())
    items.sort(key=lambda item: item.get("last_seen", 0), reverse=True)
    return items


def delete_local_clients(instance_ids: list[str]) -> int:
    """Убирает клиентов из локального файла админки."""
    keys = {str(key) for key in instance_ids if key}
    if not keys:
        return 0
    with LOCK:
        data = config.load_json(clients_path(), {"clients": {}})
        clients = data.get("clients") or {}
        removed = [key for key in keys if key in clients]
        for key in removed:
            clients.pop(key, None)
        if removed:
            config.save_json(clients_path(), data)
    return len(removed)


def delete_remote_clients(instance_ids: list[str], timeout: int = 12) -> tuple[int, str]:
    """Убирает клиентов из внешнего приёмника heartbeat.

    Возвращает (сколько удалено, текст ошибки). Удалением занимается сам
    приёмник: он знает, что лежит в текущем хранилище.
    """
    from .util import http_json

    keys = [str(key) for key in instance_ids if key]
    if not keys:
        return 0, ""
    settings = read_heartbeat_config()
    if not settings["url"]:
        return 0, ""
    base = settings["url"].rstrip("/")
    if base.endswith("/heartbeat"):
        base = base[: -len("/heartbeat")]
    url = base if base.endswith("/clients") else base + "/clients"
    header = {"X-Token": settings["token"]} if settings["token"] else {}
    try:
        payload = http_json(url, payload={"instance_ids": keys}, method="DELETE", timeout=timeout, headers=header)
    except Exception as exc:
        return 0, str(exc)
    try:
        return int((payload or {}).get("deleted") or 0), ""
    except (TypeError, ValueError):
        return 0, ""


def delete_clients(instance_ids: list[str]) -> tuple[int, str]:
    """Удаляет клиентов локально и во внешнем приёмнике; возвращает (удалено, ошибка)."""
    removed = delete_local_clients(instance_ids)
    remote, error = delete_remote_clients(instance_ids)
    return max(removed, remote), error


def save_heartbeat(payload: dict) -> None:
    if not isinstance(payload, dict):
        return
    key = str(payload.get("instance_id") or "unknown")[:64]
    with LOCK:
        data = config.load_json(clients_path(), {"clients": {}})
        clients = data.setdefault("clients", {})
        previous = clients.get(key) or {}
        clients[key] = {
            "instance_id": key,
            "username": str(payload.get("username") or previous.get("username") or "")[:32],
            "version": str(payload.get("version") or "")[:32],
            "loader": str(payload.get("loader") or "")[:32],
            "icon": str(payload.get("icon") or previous.get("icon") or "")[:512],
            "started": int(payload.get("started") or previous.get("started") or 0),
            "game_started": int(payload.get("game_started") or 0),
            "in_game": bool(payload.get("in_game")),
            "last_seen": int(time.time()),
            "first_seen": previous.get("first_seen") or int(time.time()),
        }
        config.save_json(clients_path(), data)


class _Handler(BaseHTTPRequestHandler):
    server_version = "CraftunityManifest/1.0"
    on_log: Callable[[str], None] | None = None

    def log_message(self, fmt: str, *args) -> None:  # noqa: A003
        if self.on_log:
            self.on_log(self.address_string() + " " + (fmt % args))

    def _send(self, body: bytes, content_type: str = "application/json", status: int = 200) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, payload: dict, status: int = 200) -> None:
        self._send(json.dumps(payload, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8", status)

    def do_OPTIONS(self) -> None:  # noqa: N802
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()

    def do_GET(self) -> None:  # noqa: N802
        path = urllib.parse.urlparse(self.path).path.rstrip("/") or "/"
        root = publish_dir()
        if path in ("/manifest.json", "/launcher/manifest.json", "/api/manifest.json"):
            target = root / "manifest.json"
            if not target.exists():
                return self._json({"error": "Манифест ещё не опубликован"}, 404)
            return self._send(target.read_bytes())
        if path in ("/manifest.json.sig", "/launcher/manifest.json.sig", "/api/manifest.json.sig"):
            target = root / "manifest.json.sig"
            if not target.exists():
                return self._json({"error": "Подпись не найдена"}, 404)
            return self._send(target.read_bytes(), "text/plain; charset=utf-8")
        if path == "/":
            html = (
                "<!doctype html><meta charset='utf-8'><title>Craftunity</title>"
                "<body style='font-family:Segoe UI;background:#0b0d12;color:#e8ecf6;padding:40px'>"
                "<h2>Craftunity manifest server</h2>"
                "<p>Манифест: <code>/launcher/manifest.json</code><br>"
                "Подпись: <code>/launcher/manifest.json.sig</code></p></body>"
            )
            return self._send(html.encode("utf-8"), "text/html; charset=utf-8")
        return self._json({"error": "Не найдено"}, 404)

    def do_POST(self) -> None:  # noqa: N802
        path = urllib.parse.urlparse(self.path).path.rstrip("/")
        if path in ("/heartbeat", "/api/heartbeat"):
            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length) if length else b"{}"
            try:
                payload = json.loads(raw.decode("utf-8"))
            except ValueError:
                return self._json({"error": "bad json"}, 400)
            save_heartbeat(payload)
            return self._json({"ok": True})
        return self._json({"error": "Не найдено"}, 404)


class ManifestServer:
    def __init__(self, host: str = "127.0.0.1", port: int = 8765, on_log: Callable[[str], None] | None = None) -> None:
        self.host = host
        self.port = port
        self.on_log = on_log
        self._httpd: ThreadingHTTPServer | None = None
        self._thread: threading.Thread | None = None

    @property
    def running(self) -> bool:
        return bool(self._httpd)

    def start(self) -> str:
        if self._httpd:
            return self.url
        handler = type("Handler", (_Handler,), {"on_log": self.on_log})
        httpd = ThreadingHTTPServer((self.host, self.port), handler)
        httpd.daemon_threads = True
        self._httpd = httpd
        self.port = httpd.server_address[1]
        self._thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        self._thread.start()
        return self.url

    def stop(self) -> None:
        if not self._httpd:
            return
        httpd, self._httpd = self._httpd, None
        httpd.shutdown()
        httpd.server_close()

    @property
    def url(self) -> str:
        host = "127.0.0.1" if self.host in ("0.0.0.0", "") else self.host
        return f"http://{host}:{self.port}/launcher/manifest.json"
