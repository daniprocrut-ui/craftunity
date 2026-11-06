from __future__ import annotations

import hashlib
import os
import subprocess
import sys
from pathlib import Path
from urllib.parse import quote

from . import config
from .util import LauncherError

SITE_BASE = "https://daniprocrut-ui.github.io/craftunity"
PACK_DIR = ("docs", "pack")
MAX_SIZE = 128 * 1024 * 1024


def _setting_path() -> Path:
    return config.data_dir() / "admin" / "site.json"


def _looks_like_site(path: Path) -> bool:
    try:
        return (path / "docs" / "manifest.json").exists() and (path / ".git").exists()
    except OSError:
        return False


def remember_site(path: Path) -> None:
    try:
        config.save_json(_setting_path(), {"repo": str(path)})
    except OSError:
        pass


def site_dir() -> Path | None:
    """Папка репозитория, из которого публикуется сайт (docs/ и .git)."""
    candidates: list[Path] = []
    env = os.environ.get("CRAFTUNITY_SITE")
    if env:
        candidates.append(Path(env))
    saved = config.load_json(_setting_path(), {})
    if saved.get("repo"):
        candidates.append(Path(saved["repo"]))
    for start in (Path(sys.executable).resolve().parent, Path.cwd()):
        candidates.append(start)
        candidates.extend(start.parents)
    for candidate in candidates:
        if _looks_like_site(candidate):
            remember_site(candidate)
            return candidate
    return None


def safe_name(filename: str) -> str:
    raw = Path(str(filename or "")).name
    cleaned = "".join(ch for ch in raw if ch.isalnum() or ch in "._- ()[]")
    if not cleaned:
        return ""
    return cleaned if cleaned.lower().endswith(".jar") else cleaned + ".jar"


def public_url(name: str) -> str:
    return f"{SITE_BASE}/pack/{quote(name)}"


def store_file(source: Path) -> dict:
    """Кладёт .jar в docs/pack сайта и возвращает запись для манифеста."""
    name = safe_name(source.name)
    if not name:
        raise LauncherError("Не удалось разобрать имя файла мода.")
    try:
        data = source.read_bytes()
    except OSError as exc:
        raise LauncherError(f"Не удалось прочитать файл: {exc}") from exc
    if not data:
        raise LauncherError("Файл пустой.")
    if len(data) > MAX_SIZE:
        raise LauncherError("Файл больше 128 МБ.")
    if not data.startswith(b"PK"):
        raise LauncherError("Это не .jar: файл повреждён или это не мод.")
    site = site_dir()
    if site is None:
        raise LauncherError(
            "Не нашёл папку репозитория сайта (docs/manifest.json рядом с .git). "
            "Укажите её в переменной CRAFTUNITY_SITE."
        )
    target = site.joinpath(*PACK_DIR) / name
    target.parent.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256(data).hexdigest()
    if not target.exists() or hashlib.sha256(target.read_bytes()).hexdigest() != digest:
        target.write_bytes(data)
    return {
        "filename": name,
        "sha256": digest,
        "size": len(data),
        "url": public_url(name),
        "path": str(target),
        "repo": str(site),
    }


def push(site: Path | None = None) -> str:
    """Коммитит и пушит файлы модпака, чтобы они стали доступны на сайте."""
    root = site or site_dir()
    if root is None:
        raise LauncherError("Не нашёл папку репозитория сайта.")

    def git(*args: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            ["git", *args],
            cwd=str(root),
            capture_output=True,
            text=True,
            timeout=600,
        )

    add = git("add", "docs/pack")
    if add.returncode != 0:
        raise LauncherError(f"git add не сработал: {(add.stderr or add.stdout).strip()}")
    if git("diff", "--cached", "--quiet").returncode == 0:
        return "Файлы модпака уже в репозитории"
    commit = git("commit", "-m", "pack: моды из админки")
    if commit.returncode != 0:
        raise LauncherError(f"git commit не сработал: {(commit.stderr or commit.stdout).strip()}")
    pushed = git("push", "origin", "HEAD")
    if pushed.returncode != 0:
        raise LauncherError(f"git push не сработал: {(pushed.stderr or pushed.stdout).strip()}")
    return "Файлы модпака загружены на сайт"
