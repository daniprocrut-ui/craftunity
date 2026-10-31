from __future__ import annotations

import hashlib
import json
import os
import platform
import shutil
import socket
import ssl
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Callable

USER_AGENT = "CraftunityLauncher/1.0.0 (Windows; +https://craftunity.mcsh.io)"

_SSL_CONTEXT: ssl.SSLContext | None = None


def ssl_context() -> ssl.SSLContext:
    """Общий контекст HTTPS: проверка сертификатов остаётся включённой.

    На Windows встроенное хранилище иногда содержит просроченные промежуточные
    сертификаты (из-за этого падал ely.by), поэтому используем бандл certifi.
    """
    global _SSL_CONTEXT
    if _SSL_CONTEXT is not None:
        return _SSL_CONTEXT
    try:
        import certifi

        _SSL_CONTEXT = ssl.create_default_context(cafile=certifi.where())
    except Exception:
        _SSL_CONTEXT = ssl.create_default_context()
    return _SSL_CONTEXT


class LauncherError(Exception):
    """Human readable error that the UI can show as-is."""


def http_json(
    url: str,
    headers: dict | None = None,
    timeout: int = 30,
    method: str = "GET",
    payload: dict | bytes | None = None,
    content_type: str = "application/json",
) -> dict:
    data = None
    if payload is not None:
        if isinstance(payload, bytes):
            data = payload
        else:
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("User-Agent", USER_AGENT)
    request.add_header("Accept", "application/json")
    if data is not None:
        request.add_header("Content-Type", content_type)
    for key, value in (headers or {}).items():
        request.add_header(key, value)
    try:
        with urllib.request.urlopen(request, timeout=timeout, context=ssl_context()) as response:
            raw = response.read()
    except urllib.error.HTTPError as exc:
        detail = ""
        try:
            detail = exc.read().decode("utf-8", "replace")[:400]
        except Exception:
            pass
        raise LauncherError(f"{url} -> HTTP {exc.code}: {detail or exc.reason}") from exc
    except urllib.error.URLError as exc:
        raise LauncherError(f"Нет связи с {url}: {exc.reason}") from exc
    if not raw:
        return {}
    try:
        return json.loads(raw.decode("utf-8"))
    except ValueError as exc:
        raise LauncherError(f"Некорректный JSON от {url}") from exc


def sha1_file(path: Path) -> str:
    digest = hashlib.sha1()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 256), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 256), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(
    url: str,
    dest: Path,
    sha1: str | None = None,
    size: int | None = None,
    progress: Callable[[int, int], None] | None = None,
    attempts: int = 3,
) -> Path:
    last_error: LauncherError | None = None
    for attempt in range(attempts):
        try:
            return _download_once(url, dest, sha1, size, progress)
        except LauncherError as exc:
            last_error = exc
            if "HTTP 404" in str(exc) or "HTTP 403" in str(exc):
                raise
            time.sleep(1.2 * (attempt + 1))
    assert last_error is not None
    raise last_error


def _download_once(
    url: str,
    dest: Path,
    sha1: str | None,
    size: int | None,
    progress: Callable[[int, int], None] | None,
) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists() and sha1 and sha1_file(dest) == sha1:
        return dest
    tmp = dest.with_suffix(dest.suffix + ".part")
    request = urllib.request.Request(url)
    request.add_header("User-Agent", USER_AGENT)
    try:
        with urllib.request.urlopen(request, timeout=60, context=ssl_context()) as response, tmp.open("wb") as handle:
            total = int(response.headers.get("Content-Length") or size or 0)
            done = 0
            while True:
                chunk = response.read(1024 * 128)
                if not chunk:
                    break
                handle.write(chunk)
                done += len(chunk)
                if progress:
                    progress(done, total)
    except urllib.error.HTTPError as exc:
        tmp.unlink(missing_ok=True)
        raise LauncherError(f"Не удалось скачать {url}: HTTP {exc.code}") from exc
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        tmp.unlink(missing_ok=True)
        reason = getattr(exc, "reason", exc)
        raise LauncherError(f"Не удалось скачать {url}: {reason}") from exc
    if sha1:
        actual = sha1_file(tmp)
        if actual != sha1:
            tmp.unlink(missing_ok=True)
            raise LauncherError(f"Файл повреждён (SHA1 {actual[:8]}…, ожидался {sha1[:8]}…): {url}")
    os.replace(tmp, dest)
    return dest


def extract_natives(jar: Path, target: Path) -> None:
    target.mkdir(parents=True, exist_ok=True)
    try:
        with zipfile.ZipFile(jar) as archive:
            for info in archive.infolist():
                name = info.filename
                if name.startswith("META-INF/") or name.endswith("/"):
                    continue
                if not name.lower().endswith((".dll", ".so", ".dylib", ".jnilib")):
                    continue
                out = target / Path(name).name
                with archive.open(info) as source, out.open("wb") as destination:
                    shutil.copyfileobj(source, destination)
    except zipfile.BadZipFile as exc:
        raise LauncherError(f"Битый архив библиотеки: {jar.name}") from exc


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def os_name() -> str:
    if sys_platform() == "win32":
        return "windows"
    if sys_platform() == "darwin":
        return "osx"
    return "linux"


def sys_platform() -> str:
    return os.environ.get("CRAFTUNITY_OS") or platform.system().lower() and platform.system().lower().replace("darwin", "darwin") or "windows"


def platform_token() -> str:
    system = platform.system().lower()
    return {"windows": "windows", "darwin": "osx", "linux": "linux"}.get(system, "windows")


def rules_allow(rules: list | None) -> bool:
    if not rules:
        return True
    allowed = False
    for rule in rules:
        action = rule.get("action")
        match = True
        os_rule = rule.get("os")
        if os_rule:
            if os_rule.get("name") and os_rule["name"] != platform_token():
                match = False
            if os_rule.get("arch") and os_rule["arch"] not in (platform.machine().lower(), "x86_64", "amd64"):
                match = False
            if os_rule.get("version") and os_rule["version"] not in platform.version():
                match = False
        features = rule.get("features")
        if features:
            match = False
        if match:
            allowed = action == "allow"
    return allowed


def maven_to_path(coordinate: str) -> str:
    group, artifact, version = coordinate.split(":")[:3]
    classifier = ""
    parts = coordinate.split(":")
    if len(parts) >= 4:
        classifier = "-" + parts[3]
    filename = f"{artifact}-{version}{classifier}.jar"
    return "/".join(group.split(".")) + f"/{artifact}/{version}/{filename}"


def human_size(value: int) -> str:
    size = float(value)
    for unit in ("Б", "КБ", "МБ", "ГБ"):
        if size < 1024 or unit == "ГБ":
            return f"{size:.0f} {unit}" if unit == "Б" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} ГБ"


def canonical_json(payload: dict) -> bytes:
    return json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
