from __future__ import annotations

import json
import threading
import time
from pathlib import Path

from . import config
from .util import LauncherError, download, http_json, sha1_file, sha256_file

MODS_STATE = "installed_mods.json"
ICONS_DIR = "icons"


def _icon_key(project_id: str) -> str:
    raw = str(project_id or "")
    return "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in raw)


def icons_dir() -> Path:
    path = config.data_dir() / ICONS_DIR
    path.mkdir(parents=True, exist_ok=True)
    return path


def icon_local(project_id: str) -> Path | None:
    """Локальная копия иконки проекта, если она уже скачана."""
    key = _icon_key(project_id)
    if not key:
        return None
    for suffix in (".png", ".webp", ".jpg", ".ico"):
        path = icons_dir() / (key + suffix)
        try:
            if path.exists() and path.stat().st_size > 0:
                return path
        except OSError:
            continue
    return None


def _fetch_icon(url: str, target: Path, timeout: float = 3.0) -> bool:
    """Быстрая загрузка иконки: одна попытка, короткий таймаут, без ретраев."""
    import urllib.error
    import urllib.request

    from .util import ssl_context

    part = target.with_name(target.name + ".part")
    request = urllib.request.Request(url, headers={"User-Agent": "CraftunityLauncher"})
    try:
        with urllib.request.urlopen(request, timeout=timeout, context=ssl_context()) as response:
            data = response.read(1024 * 512)
        if not data:
            return False
        part.write_bytes(data)
        part.replace(target)
        return True
    except (urllib.error.URLError, urllib.error.HTTPError, OSError, ValueError):
        try:
            part.unlink(missing_ok=True)
        except OSError:
            pass
        return False


def cache_icon(project_id: str, url: str, fallback_url: str = "") -> Path | None:
    """Скачивает иконку в профиль, чтобы карточки не зависели от интернета."""
    existing = icon_local(project_id)
    if existing is not None:
        return existing
    key = _icon_key(project_id)
    if not key:
        return None
    for candidate in (url, fallback_url):
        candidate = str(candidate or "")
        if not candidate.startswith(("http://", "https://")):
            continue
        suffix = ".webp" if ".webp" in candidate.lower() else ".png"
        target = icons_dir() / (key + suffix)
        if _fetch_icon(candidate, target):
            try:
                if target.exists() and target.stat().st_size:
                    return target
            except OSError:
                pass
    return None

CONTENT_TYPES = {
    "mod": "mods",
    "resourcepack": "resourcepacks",
    "shader": "shaderpacks",
}

CONTENT_TITLES = {
    "mod": "Мод",
    "resourcepack": "Ресурспак",
    "shader": "Шейдер",
}


def icon_hd(project_id: str, fallback: str = "") -> str:
    """Оригинальная иконка проекта из CDN Modrinth (обычно 400–512 px)."""
    project_id = str(project_id or "")
    if project_id and all(ch.isalnum() for ch in project_id):
        return f"https://cdn.modrinth.com/data/{project_id}/icon.png"
    return fallback


def cover_image(project_type: str, project_id: str, icon_url: str = "", gallery=None) -> str:
    """Иконка проекта для карточки.

    Раньше ресурспакам и шейдерам подставлялся первый скриншот галереи — на карточке
    оказывалась случайная фотография. Теперь у всех разделов оригинальная иконка проекта,
    а скриншот берётся только если иконки нет вовсе.
    """
    icon = icon_hd(project_id, icon_url)
    if icon:
        return icon
    for shot in gallery or []:
        url = shot.get("url") if isinstance(shot, dict) else shot
        if url:
            return str(url)
    return ""


def content_dir(kind: str = "mod") -> Path:
    folder = CONTENT_TYPES.get(kind)
    if not folder:
        raise LauncherError("Неизвестный тип контента.")
    path = config.game_dir() / folder
    path.mkdir(parents=True, exist_ok=True)
    return path


def mods_dir(loader: str = "fabric", version: str = "") -> Path:
    return content_dir("mod")


def _state_path() -> Path:
    return config.data_dir() / MODS_STATE


def installed() -> list[dict]:
    data = config.load_json(_state_path())
    return list(data.get("mods") or [])


def _save(items: list[dict]) -> None:
    config.save_json(_state_path(), {"mods": items})


def _facets(game_version: str, loader: str, kind: str = "mod") -> str:
    facets = [[f"project_type:{kind}"]]
    if game_version:
        facets.append([f"versions:{game_version}"])
    if kind == "mod" and loader in ("fabric", "forge", "quilt", "neoforge"):
        facets.append([f"categories:{loader}"])
    return json.dumps(facets, separators=(",", ":"))


def search(
    query: str,
    game_version: str,
    loader: str,
    limit: int = 20,
    offset: int = 0,
    sort: str = "relevance",
    kind: str = "mod",
) -> dict:
    import urllib.parse

    if kind not in CONTENT_TYPES:
        kind = "mod"
    allowed = {"relevance", "downloads", "newest", "updated", "follows"}
    if sort not in allowed:
        sort = "relevance"
    params = {
        "query": query or "",
        "limit": str(limit),
        "offset": str(offset),
        "index": sort,
        "facets": _facets(game_version, loader, kind),
    }
    url = f"{config.MODRINTH_API}/search?" + urllib.parse.urlencode(params)
    data = http_json(url, timeout=25)
    hits = []
    for hit in data.get("hits", []):
        hits.append(
            {
                "id": hit.get("project_id"),
                "slug": hit.get("slug"),
                "title": hit.get("title"),
                "description": hit.get("description"),
                "author": hit.get("author"),
                "downloads": hit.get("downloads", 0),
                "icon": hit.get("icon_url"),
                "icon_hd": cover_image(
                    hit.get("project_type") or kind,
                    hit.get("project_id"),
                    hit.get("icon_url") or "",
                    hit.get("gallery"),
                ),
                "categories": hit.get("categories", []),
            }
        )
    return {"total": data.get("total_hits", 0), "hits": hits}


def project_versions(project_id: str, game_version: str, loader: str, kind: str = "mod") -> list[dict]:
    import urllib.parse

    base = f"{config.MODRINTH_API}/project/{project_id}/version"
    params: dict[str, str] = {}
    if game_version:
        params["game_versions"] = json.dumps([game_version])
    if kind == "mod" and loader:
        params["loaders"] = json.dumps([loader])
    url = base + ("?" + urllib.parse.urlencode(params) if params else "")
    versions = http_json(url, timeout=25)
    if versions:
        return versions
    if kind != "mod":
        # ресурспаки и шейдеры часто не указывают версию игры — берём последнюю сборку
        fallback = http_json(base, timeout=25)
        return fallback or []
    return []


def _pick_file(version: dict) -> dict | None:
    for file in version.get("files", []):
        if file.get("primary"):
            return file
    files = version.get("files") or []
    return files[0] if files else None


DISABLED_SUFFIX = ".disabled"


def _disabled_path(path: Path) -> Path:
    return path.with_name(path.name + DISABLED_SUFFIX)


def is_disabled(path: Path) -> bool:
    return path.name.lower().endswith(DISABLED_SUFFIX)


def _enabled_path(path: Path) -> Path:
    name = path.name[: -len(DISABLED_SUFFIX)] if is_disabled(path) else path.name
    return path.with_name(name)


def set_enabled(project_id: str = "", kind: str = "", filename: str = "", enabled: bool = True) -> dict:
    """Включает или выключает файл, переименовывая его в .disabled и обратно."""
    if kind and kind not in CONTENT_TYPES:
        raise LauncherError("Неизвестный тип контента.")
    target: Path | None = None
    tracked = None
    if project_id:
        for item in installed():
            if item.get("project_id") == project_id:
                tracked = item
                candidate = Path(item.get("path") or "")
                target = candidate if candidate.exists() else _disabled_path(candidate)
                break
        if target is None:
            raise LauncherError("Запись не найдена в установленных.")
    else:
        if kind not in CONTENT_TYPES:
            raise LauncherError("Неизвестный тип контента.")
        safe = Path(filename or "").name
        if not safe:
            raise LauncherError("Не указан файл.")
        folder = content_dir(kind)
        base = _enabled_path(folder / safe)
        target = base if base.exists() else _disabled_path(base)
        for item in installed():
            if Path(item.get("path") or "").name in (base.name, target.name):
                tracked = item
                break
    if target is None or not target.exists():
        raise LauncherError("Файл не найден.")
    if enabled and is_disabled(target):
        new_path = _enabled_path(target)
        target.rename(new_path)
        target = new_path
    elif not enabled and not is_disabled(target):
        new_path = _disabled_path(target)
        target.rename(new_path)
        target = new_path
    if tracked is not None:
        tracked["path"] = str(target)
        tracked["disabled"] = not enabled
        _upsert(tracked)
    return {"filename": target.name, "path": str(target), "disabled": is_disabled(target)}


def _installed_slugs() -> dict[str, dict]:
    return {item.get("project_id"): item for item in installed()}


def _upsert(item: dict) -> None:
    items = [entry for entry in installed() if entry.get("project_id") != item["project_id"]]
    items.append(item)
    _save(items)


def _drop(project_id: str) -> None:
    _save([entry for entry in installed() if entry.get("project_id") != project_id])


def install_project(
    project_id: str,
    game_version: str,
    loader: str,
    with_dependencies: bool = True,
    kind: str = "mod",
) -> dict:
    if kind not in CONTENT_TYPES:
        kind = "mod"
    existing = _installed_slugs().get(project_id)
    versions = project_versions(project_id, game_version, loader, kind)
    if not versions:
        raise LauncherError(
            f"У этого проекта нет сборки под Minecraft {game_version}."
            if kind != "mod"
            else "У этого мода нет сборки под выбранную версию и загрузчик."
        )
    version = versions[0]
    file = _pick_file(version)
    if not file:
        raise LauncherError("У версии проекта нет файла для скачивания.")

    if (
        existing
        and existing.get("filename") == file["filename"]
        and Path(existing.get("path") or "").exists()
    ):
        if (
            existing.get("game_version") != game_version
            or existing.get("loader") != loader
            or existing.get("kind") != kind
        ):
            existing.update(
                {
                    "game_version": game_version,
                    "loader": loader,
                    "kind": kind,
                    "version": version.get("version_number"),
                }
            )
            _upsert(existing)
        return existing

    target = content_dir(kind) / file["filename"]
    download(file["url"], target, file.get("hashes", {}).get("sha1"), file.get("size"))
    if existing:
        old_path = Path(existing.get("path") or "")
        if old_path and old_path != target:
            old_path.unlink(missing_ok=True)
    try:
        project = http_json(f"{config.MODRINTH_API}/project/{project_id}", timeout=20)
    except LauncherError:
        project = {"slug": project_id, "title": project_id, "icon_url": ""}
    item = {
        "project_id": project_id,
        "slug": project.get("slug") or project_id,
        "title": project.get("title") or project_id,
        "description": project.get("description") or "",
        "version": version.get("version_number"),
        "filename": file["filename"],
        "path": str(target),
        "sha1": file.get("hashes", {}).get("sha1"),
        "game_version": game_version,
        "loader": loader,
        "kind": kind,
        "installed_at": int(time.time()),
        "dependencies": [],
        "icon": (project.get("icon_url") or ""),
        "icon_hd": cover_image(
            project.get("project_type") or kind,
            project_id,
            project.get("icon_url") or "",
            project.get("gallery"),
        ),
    }
    deps = []
    dep_projects = []
    if with_dependencies and kind == "mod":
        for dep in version.get("dependencies", []):
            if dep.get("dependency_type") != "required" or not dep.get("project_id"):
                continue
            try:
                result = install_project(dep["project_id"], game_version, loader, with_dependencies=True)
                deps.append(result.get("filename"))
                dep_projects.append(
                    {
                        "project_id": dep["project_id"],
                        "title": result.get("title") or dep["project_id"],
                        "filename": result.get("filename"),
                    }
                )
            except LauncherError:
                continue
    item["dependencies"] = deps
    item["dependency_projects"] = dep_projects
    cache_icon(project_id, item.get("icon") or "", item.get("icon_hd") or "")
    _upsert(item)
    if kind == "shader":
        try:
            install_project("iris", game_version, loader, with_dependencies=True, kind="mod")
            item["needs_iris"] = True
        except LauncherError:
            item["needs_iris"] = False
    return item


def sync_for_version(game_version: str, loader: str) -> dict:
    updated: list[str] = []
    removed: list[str] = []
    failed: list[dict] = []
    for item in list(installed()):
        project_id = item.get("project_id")
        path = Path(item.get("path") or "")
        kind = item.get("kind") or "mod"
        if not project_id or kind != "mod":
            continue
        if (
            item.get("game_version") == game_version
            and item.get("loader") == loader
            and path.exists()
        ):
            continue
        try:
            versions = project_versions(project_id, game_version, loader, kind)
        except LauncherError as exc:
            failed.append({"project": project_id, "error": str(exc)})
            continue
        if not versions:
            path.unlink(missing_ok=True)
            _drop(project_id)
            removed.append(item.get("slug") or project_id)
            continue
        try:
            result = install_project(project_id, game_version, loader, with_dependencies=True, kind=kind)
            if result.get("filename") != item.get("filename"):
                updated.append(result.get("slug") or project_id)
        except LauncherError as exc:
            failed.append({"project": project_id, "error": str(exc)})
    return {"updated": updated, "removed": removed, "failed": failed}


def remove_project(project_id: str) -> dict:
    items = installed()
    keep = []
    removed = None
    for item in items:
        if item.get("project_id") == project_id:
            removed = item
            Path(item.get("path", "")).unlink(missing_ok=True)
        else:
            keep.append(item)
    _save(keep)
    if not removed:
        raise LauncherError("Запись не найдена в установленных.")
    return removed


def install_curseforge(project_id: str, game_version: str, loader: str) -> dict:
    """Ставит мод с CurseForge: берём самый свежий файл под нужную версию игры."""
    from . import curseforge

    key = project_id if str(project_id).startswith("cf:") else f"cf:{project_id}"
    files = curseforge.project_files(key, game_version, loader)
    if not files:
        raise LauncherError(
            "У этого мода на CurseForge нет файла под выбранную версию игры или автор запретил сторонние загрузки."
        )
    file = files[0]
    existing = _installed_slugs().get(key)
    target = content_dir("mod") / file["filename"]
    if existing and existing.get("filename") == file["filename"] and Path(existing.get("path") or "").exists():
        return existing
    download(file["url"], target, file.get("hashes", {}).get("sha1") or None, file.get("size") or None)
    if existing:
        old_path = Path(existing.get("path") or "")
        if old_path and old_path != target:
            old_path.unlink(missing_ok=True)
    try:
        info = curseforge.project(key)
    except LauncherError:
        info = {}
    item = {
        "project_id": key,
        "slug": info.get("slug") or key,
        "title": info.get("name") or file["filename"],
        "description": info.get("summary") or "",
        "version": file.get("version"),
        "filename": file["filename"],
        "path": str(target),
        "kind": "mod",
        "game_version": game_version,
        "loader": loader,
        "icon_url": ((info.get("logo") or {}).get("thumbnailUrl") or ""),
    }
    cache_icon(key, item["icon_url"])
    _upsert(item)
    return item


def install(project_id: str, game_version: str, loader: str, kind: str = "mod") -> dict:
    """Ставит контент из любого каталога: cf:<id> — CurseForge, иначе Modrinth."""
    if str(project_id).startswith("cf:"):
        return install_curseforge(project_id, game_version, loader)
    return install_project(project_id, game_version, loader, kind=kind)


_warm_lock = threading.Lock()
_warming = False


def warm_icons(limit: int = 200) -> None:
    """Догружает иконки установленных модов в фоне, чтобы не тормозить интерфейс."""
    global _warming
    with _warm_lock:
        if _warming:
            return
        _warming = True

    def work() -> None:
        global _warming
        try:
            done = 0
            for item in installed():
                if done >= limit:
                    break
                project_id = str(item.get("project_id") or "")
                if not project_id or project_id.startswith("file:"):
                    continue
                if icon_local(project_id) is not None:
                    continue
                cache_icon(project_id, item.get("icon") or "", item.get("icon_hd") or "")
                done += 1
        finally:
            with _warm_lock:
                _warming = False

    threading.Thread(target=work, daemon=True).start()


def local_project_info(project_id: str) -> dict:
    """Карточка для файлового мода: показывает, что за файл лежит в профиле."""
    wanted = str(project_id or "")
    for item in installed_all().get("items") or []:
        if str(item.get("project_id") or "") != wanted:
            continue
        path = Path(str(item.get("path") or ""))
        size = 0
        digest = ""
        try:
            if path.exists():
                size = path.stat().st_size
                digest = sha256_file(path)
        except OSError:
            pass
        title = item.get("title") or path.stem or wanted
        project_type = item.get("kind") or "mod"
        return {
            "id": wanted,
            "slug": path.stem or wanted,
            "title": title,
            "description": item.get("description")
            or "Файл мода, установленный из модпака сервера или добавленный вручную.",
            "body": "Файл лежит в профиле игры.\n\n"
            f"Имя: {path.name or '—'}\n"
            f"Размер: {size} байт\n"
            f"sha256: {digest or '—'}",
            "icon": item.get("icon") or "",
            "icon_hd": item.get("icon_hd") or "",
            "downloads": 0,
            "followers": 0,
            "categories": ["локальный файл"],
            "project_type": project_type,
            "license": "",
            "authors": [],
            "gallery": [],
            "versions": [
                {
                    "version": item.get("version") or "файл",
                    "game_versions": [item.get("game_version") or ""],
                    "loaders": [item.get("loader") or ""],
                }
            ],
            "source": "file",
        }
    raise LauncherError("Файл не найден среди установленных.")


def manifest_file_name(entry: dict) -> str:
    raw = Path(str(entry.get("filename") or "")).name
    safe = "".join(ch for ch in raw if ch.isalnum() or ch in "._- ()[]")
    if not safe:
        return ""
    return safe if safe.lower().endswith(".jar") else safe + ".jar"


def install_manifest_file(entry: dict, game_version: str, loader: str) -> dict:
    """Ставит мод, который администратор добавил в модпак файлом."""
    url = str(entry.get("url") or "").strip()
    safe = manifest_file_name(entry)
    if not url.startswith(("http://", "https://")) or not safe:
        raise LauncherError("В записи модпака нет ссылки на файл мода.")
    project_id = f"file:{safe}"
    target = content_dir("mod") / safe
    expected = str(entry.get("sha256") or "").strip().lower()
    existing = _installed_slugs().get(project_id)
    if target.exists() and existing and (not expected or sha256_file(target) == expected):
        return existing
    download(url, target, None, entry.get("size") or None)
    if expected and sha256_file(target) != expected:
        target.unlink(missing_ok=True)
        raise LauncherError(f"Файл {safe} скачался с ошибкой: контрольная сумма не совпала.")
    item = {
        "project_id": project_id,
        "slug": safe,
        "title": str(entry.get("title") or Path(safe).stem),
        "version": str(entry.get("version") or "файл"),
        "filename": safe,
        "path": str(target),
        "kind": "mod",
        "game_version": game_version,
        "loader": loader,
    }
    _upsert(item)
    return item


def sync_manifest_mods(manifest: dict, game_version: str, loader: str) -> list[dict]:
    results = []
    wanted = manifest.get("mods") or []
    for entry in wanted:
        if str(entry.get("url") or "").strip() and manifest_file_name(entry):
            try:
                results.append(install_manifest_file(entry, game_version, loader))
            except LauncherError as exc:
                results.append({"project_id": f"file:{manifest_file_name(entry)}", "error": str(exc)})
            continue
        project_id = entry.get("project_id") or entry.get("slug")
        if not project_id:
            continue
        try:
            results.append(install(project_id, game_version, loader))
        except LauncherError as exc:
            results.append({"project_id": project_id, "error": str(exc)})
    return results


def installed_versions() -> dict:
    return {"mods": installed()}


def team_members(team_id: str) -> list[dict]:
    """Авторы проекта из команды Modrinth: владельцы идут первыми."""
    if not team_id:
        return []
    try:
        members = http_json(f"{config.MODRINTH_API}/team/{team_id}/members", timeout=20) or []
    except LauncherError:
        return []
    authors = []
    for member in members:
        user = member.get("user") or {}
        username = user.get("username")
        if not username:
            continue
        authors.append(
            {
                "username": username,
                "name": user.get("name") or username,
                "avatar": user.get("avatar_url") or "",
                "role": member.get("role") or "",
                "url": f"https://modrinth.com/user/{username}",
            }
        )
    authors.sort(key=lambda item: 0 if item["role"] == "owner" else 1)
    return authors[:6]


def project_info(project_id: str) -> dict:
    if str(project_id).startswith("cf:"):
        from . import curseforge

        return curseforge.project_info(project_id)
    project = http_json(f"{config.MODRINTH_API}/project/{project_id}", timeout=25)
    try:
        versions = http_json(f"{config.MODRINTH_API}/project/{project_id}/version?limit=8", timeout=25) or []
    except LauncherError:
        versions = []
    gallery = []
    for shot in project.get("gallery") or []:
        url = shot.get("url")
        if url:
            gallery.append(
                {
                    "url": url,
                    "title": shot.get("title") or "",
                    "description": shot.get("description") or "",
                }
            )
    slug = project.get("slug") or project_id
    project_type = project.get("project_type") or "mod"
    kind_path = {"resourcepack": "resourcepack", "shader": "shader", "modpack": "modpack"}.get(project_type, "mod")
    return {
        "id": project.get("id") or project_id,
        "slug": slug,
        "title": project.get("title") or project_id,
        "description": project.get("description") or "",
        "body": project.get("body") or "",
        "icon": project.get("icon_url") or "",
        "icon_hd": icon_hd(project.get("id") or project_id, project.get("icon_url") or ""),
        "downloads": project.get("downloads", 0),
        "followers": project.get("followers", 0),
        "categories": project.get("categories", []),
        "project_type": project_type,
        "license": (project.get("license") or {}).get("name") or "",
        "authors": team_members(project.get("team") or ""),
        "page_url": f"https://modrinth.com/{kind_path}/{slug}",
        "gallery": gallery,
        "versions": [
            {
                "version": version.get("version_number"),
                "game_versions": version.get("game_versions", []),
                "loaders": version.get("loaders", []),
                "date": version.get("date_published"),
            }
            for version in versions[:8]
        ],
    }


def dependents(filename: str) -> list[dict]:
    """Кто из установленных модов требует этот файл как зависимость."""
    wanted = Path(str(filename or "")).name
    if not wanted:
        return []
    result = []
    for item in installed():
        if item.get("disabled"):
            continue
        deps = item.get("dependencies") or []
        if any(Path(str(dep)).name == wanted for dep in deps):
            result.append(
                {
                    "project_id": item.get("project_id"),
                    "title": item.get("title") or item.get("slug") or wanted,
                }
            )
    return result


def dependents_for(project_id: str = "", kind: str = "", filename: str = "") -> list[dict]:
    """Зависимые моды для записи по project_id или по файлу."""
    target = Path(str(filename or "")).name
    if not target and project_id:
        for item in installed():
            if item.get("project_id") == project_id:
                target = Path(item.get("path") or "").name
                break
    if not target:
        return []
    items = dependents(target)
    return [entry for entry in items if entry.get("project_id") != project_id]


def _project_by_filename(filename: str, game_version: str, loader: str) -> dict | None:
    """Ищет проект в Modrinth, у которого есть именно этот файл."""
    stem = Path(str(filename or "")).stem
    if not stem:
        return None
    query = stem.split("+")[0].split("-")[0].replace("_", " ").strip()
    if not query:
        return None
    try:
        found = search(query, game_version, loader, limit=12, kind="mod")
    except LauncherError:
        return None
    wanted = Path(str(filename)).name
    for hit in found.get("hits") or []:
        project_id = str(hit.get("id") or hit.get("slug") or "")
        if not project_id:
            continue
        try:
            versions = project_versions(project_id, game_version, loader)
        except LauncherError:
            continue
        for version in versions:
            for item in version.get("files") or []:
                if Path(str(item.get("filename") or "")).name == wanted:
                    return {
                        "project_id": project_id,
                        "title": hit.get("title") or project_id,
                        "filename": wanted,
                    }
    return None


def missing_projects(project_id: str, game_version: str = "", loader: str = "") -> list[dict]:
    """Зависимости конкретного мода, которых нет среди установленных.

    Возвращает записи вида {"project_id", "title"}: по ним можно поставить
    недостающее одним нажатием.
    """
    entry = next((item for item in installed() if item.get("project_id") == project_id), None)
    if not entry:
        return []
    present = {Path(item.get("path") or "").name for item in installed_all()["items"]}
    known = {
        Path(str(dep.get("filename") or "")).name: dep
        for dep in entry.get("dependency_projects") or []
        if dep.get("filename")
    }
    result = []
    for dep_file in entry.get("dependencies") or []:
        filename = Path(str(dep_file)).name
        if not filename or filename in present:
            continue
        dep = known.get(filename) or {}
        dep_id = dep.get("project_id")
        title = dep.get("title") or ""
        if not dep_id and game_version:
            found = _project_by_filename(filename, game_version, loader)
            if found:
                dep_id = found["project_id"]
                title = found.get("title") or title
                entry.setdefault("dependency_projects", []).append(found)
        if not dep_id:
            continue
        if any(item.get("project_id") == dep_id for item in result):
            continue
        result.append({"project_id": dep_id, "title": title or dep_id})
    if result:
        _upsert(entry)
    return result


def install_dependencies(project_id: str, game_version: str, loader: str) -> list[dict]:
    """Доставляет недостающие зависимости одного мода."""
    installed_items = []
    for dep in missing_projects(project_id):
        try:
            installed_items.append(install_project(dep["project_id"], game_version, loader, with_dependencies=True))
        except LauncherError as exc:
            installed_items.append({"project_id": dep["project_id"], "error": str(exc)})
    return installed_items


def installed_all() -> dict:
    """Установленный контент: отслеживаемые записи + файлы, положенные вручную."""
    tracked = installed()
    changed = False
    icon_budget = 8
    for item in tracked:
        project_id = str(item.get("project_id") or "")
        if project_id and not project_id.startswith("file:") and not item.get("icon_hd"):
            item["icon_hd"] = icon_hd(project_id, item.get("icon") or "")
            changed = True
        if not project_id or project_id.startswith("file:"):
            continue
        if item.get("icon") and item.get("description"):
            if icon_local(project_id) is None and icon_budget > 0:
                icon_budget -= 1
                cache_icon(project_id, item.get("icon") or "", item.get("icon_hd") or "")
            continue
        try:
            project = http_json(f"{config.MODRINTH_API}/project/{project_id}", timeout=10)
        except LauncherError:
            continue
        icon = project.get("icon_url") or ""
        if icon and not item.get("icon"):
            item["icon"] = icon
            changed = True
        if icon and icon_budget > 0 and icon_local(project_id) is None:
            icon_budget -= 1
            cache_icon(project_id, item.get("icon") or icon, item.get("icon_hd") or icon)
        description = project.get("description") or ""
        if description and not item.get("description"):
            item["description"] = description
            changed = True
    by_file = {
        Path(entry.get("path") or "").name: entry
        for entry in tracked
        if entry.get("path") and not str(entry.get("project_id") or "").startswith("file:")
    }
    for entry in tracked:
        deps = entry.get("dependencies") or []
        if not deps or entry.get("dependency_projects"):
            continue
        restored = []
        for dep_file in deps:
            name = Path(str(dep_file)).name
            owner = by_file.get(name)
            if not owner:
                continue
            restored.append(
                {
                    "project_id": owner.get("project_id"),
                    "title": owner.get("title") or owner.get("slug") or name,
                    "filename": name,
                }
            )
        if restored:
            entry["dependency_projects"] = restored
            changed = True
    if changed:
        _save(tracked)
    known = {str(Path(item.get("path", "")).resolve()) for item in tracked if item.get("path")}
    items = [dict(item, kind=item.get("kind") or "mod") for item in tracked]
    for kind, folder in CONTENT_TYPES.items():
        for path in content_dir(kind).iterdir():
            if path.suffix.lower() not in (".jar", ".zip", ".disabled"):
                continue
            if str(path.resolve()) in known:
                continue
            disabled = is_disabled(path)
            if disabled:
                path = _enabled_path(path)
            items.append(
                {
                    "project_id": f"file:{kind}:{path.name}",
                    "slug": path.stem,
                    "title": path.stem,
                    "filename": path.name,
                    "path": str(path),
                    "kind": kind,
                    "untracked": True,
                    "disabled": disabled,
                    "installed_at": int(path.stat().st_mtime),
                }
            )
    items.sort(key=lambda item: item.get("installed_at", 0), reverse=True)
    present = {Path(item.get("path") or "").name for item in items}
    for entry in items:
        deps = entry.get("dependencies") or []
        missing = [str(dep) for dep in deps if Path(str(dep)).name not in present]
        if missing:
            entry["missing_dependencies"] = missing
    warm_icons()
    return {"items": items}


def remove_local_file(kind: str, filename: str) -> dict:
    if kind not in CONTENT_TYPES:
        raise LauncherError("Неизвестный тип контента.")
    safe = Path(filename).name
    target = content_dir(kind) / safe
    if not target.exists():
        raise LauncherError("Файл не найден.")
    target.unlink()
    for item in list(installed()):
        if Path(item.get("path") or "").name == safe:
            _drop(item.get("project_id"))
    return {"removed": safe}


def install_local_upload(data: bytes, filename: str, kind: str) -> dict:
    if kind not in ("mod", "resourcepack", "shader"):
        raise LauncherError("Загрузить вручную можно мод, ресурспак или шейдер.")
    if not data:
        raise LauncherError("Пустой файл.")
    if len(data) > 128 * 1024 * 1024:
        raise LauncherError("Файл больше 128 МБ.")
    if not data.startswith(b"PK"):
        raise LauncherError("Файл повреждён: ожидается .jar или .zip.")
    suffix = ".jar" if kind == "mod" else ".zip"
    fallback = "mod.jar" if kind == "mod" else "pack.zip"
    name = Path(filename or "").name or fallback
    if not name.lower().endswith(suffix):
        if kind == "mod":
            raise LauncherError("Для модов нужен файл .jar.")
        name = Path(name).stem + suffix
    safe = "".join(ch for ch in name if ch.isalnum() or ch in "._- ()[]") or fallback
    target = content_dir(kind) / safe
    target.write_bytes(data)
    return {
        "project_id": f"file:{kind}:{safe}",
        "title": Path(safe).stem,
        "filename": safe,
        "path": str(target),
        "kind": kind,
        "untracked": True,
    }
