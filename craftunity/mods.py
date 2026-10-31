from __future__ import annotations

import json
import threading
import time
from pathlib import Path

from . import config
from .util import LauncherError, download, http_json, sha1_file

MODS_STATE = "installed_mods.json"

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
    """Крупная картинка для карточки.

    У ресурспаков и шейдеров иконка отдаётся только в 96 px и при увеличении мылит,
    поэтому берём первый скриншот галереи (350 px). У модов остаётся оригинальная иконка.
    """
    if project_type in ("resourcepack", "shader"):
        for shot in gallery or []:
            url = shot.get("url") if isinstance(shot, dict) else shot
            if url:
                return str(url)
    return icon_hd(project_id, icon_url)


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
    if with_dependencies and kind == "mod":
        for dep in version.get("dependencies", []):
            if dep.get("dependency_type") != "required" or not dep.get("project_id"):
                continue
            try:
                result = install_project(dep["project_id"], game_version, loader, with_dependencies=True)
                deps.append(result.get("filename"))
            except LauncherError:
                continue
    item["dependencies"] = deps
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


def sync_manifest_mods(manifest: dict, game_version: str, loader: str) -> list[dict]:
    results = []
    wanted = manifest.get("mods") or []
    for entry in wanted:
        project_id = entry.get("project_id") or entry.get("slug")
        if not project_id:
            continue
        try:
            results.append(install_project(project_id, game_version, loader, True))
        except LauncherError as exc:
            results.append({"project_id": project_id, "error": str(exc)})
    return results


def installed_versions() -> dict:
    return {"mods": installed()}


def project_info(project_id: str) -> dict:
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
    return {
        "id": project.get("id") or project_id,
        "slug": project.get("slug") or project_id,
        "title": project.get("title") or project_id,
        "description": project.get("description") or "",
        "body": project.get("body") or "",
        "icon": project.get("icon_url") or "",
        "icon_hd": icon_hd(project.get("id") or project_id, project.get("icon_url") or ""),
        "downloads": project.get("downloads", 0),
        "followers": project.get("followers", 0),
        "categories": project.get("categories", []),
        "project_type": project.get("project_type") or "mod",
        "license": (project.get("license") or {}).get("name") or "",
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


def installed_all() -> dict:
    """Установленный контент: отслеживаемые записи + файлы, положенные вручную."""
    tracked = installed()
    changed = False
    for item in tracked:
        project_id = str(item.get("project_id") or "")
        if project_id and not project_id.startswith("file:") and not item.get("icon_hd"):
            item["icon_hd"] = icon_hd(project_id, item.get("icon") or "")
            changed = True
        if item.get("icon") or not project_id or project_id.startswith("file:"):
            continue
        try:
            project = http_json(f"{config.MODRINTH_API}/project/{project_id}", timeout=10)
        except LauncherError:
            continue
        icon = project.get("icon_url") or ""
        if icon:
            item["icon"] = icon
            changed = True
    if changed:
        _save(tracked)
    known = {str(Path(item.get("path", "")).resolve()) for item in tracked if item.get("path")}
    items = [dict(item, kind=item.get("kind") or "mod") for item in tracked]
    for kind, folder in CONTENT_TYPES.items():
        for path in content_dir(kind).iterdir():
            if path.suffix.lower() not in (".jar", ".zip"):
                continue
            if str(path.resolve()) in known:
                continue
            items.append(
                {
                    "project_id": f"file:{kind}:{path.name}",
                    "slug": path.stem,
                    "title": path.stem,
                    "filename": path.name,
                    "path": str(path),
                    "kind": kind,
                    "untracked": True,
                    "installed_at": int(path.stat().st_mtime),
                }
            )
    items.sort(key=lambda item: item.get("installed_at", 0), reverse=True)
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
    if kind not in ("resourcepack", "shader"):
        raise LauncherError("Загрузить вручную можно ресурспак или шейдер.")
    if not data:
        raise LauncherError("Пустой файл.")
    if len(data) > 128 * 1024 * 1024:
        raise LauncherError("Файл больше 128 МБ.")
    if not data.startswith(b"PK"):
        raise LauncherError("Нужен файл .zip (ресурспак или архив шейдера).")
    name = Path(filename or "").name or "pack.zip"
    if not name.lower().endswith(".zip"):
        name += ".zip"
    safe = "".join(ch for ch in name if ch.isalnum() or ch in "._- ()[]") or "pack.zip"
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
