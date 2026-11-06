from __future__ import annotations

import html as html_lib
import os
import re
from urllib.parse import urlencode

from . import config
from .util import LauncherError, http_json

API = "https://api.curseforge.com/v1"
GAME_ID = 432
CLASS_IDS = {"mod": 6, "resourcepack": 12, "shader": 6552}
LOADER_TYPES = {"forge": 1, "fabric": 4, "quilt": 5, "neoforge": 6}
SORT_FIELDS = {"relevance": 2, "downloads": 6, "newest": 11, "updated": 3, "follows": 2}
TAGS = re.compile(r"<[^>]+>")
VERSION_TAG = re.compile(r"^\d+(\.\d+)*$")


def plain_text(raw: str) -> str:
    """Описание CurseForge приходит HTML — приводим к обычному тексту."""
    text = str(raw or "")
    text = re.sub(r"(?i)<br\s*/?>", "\n", text)
    text = re.sub(r"(?i)</(p|div|li|tr|h\d)>", "\n", text)
    text = TAGS.sub("", text)
    text = html_lib.unescape(text)
    text = re.sub(r"[ \t]+\n", "\n", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()


def description(project_id: str) -> str:
    key = str(project_id).replace("cf:", "")
    try:
        data = _get(f"/mods/{key}/description")
    except LauncherError:
        return ""
    return plain_text(data.get("data") or "")


def latest_versions(project_id: str, limit: int = 8) -> list[dict]:
    key = str(project_id).replace("cf:", "")
    try:
        data = _get(f"/mods/{key}/files", {"pageSize": max(1, min(int(limit), 50))})
    except LauncherError:
        return []
    files = list(data.get("data") or [])
    files.sort(key=lambda item: str(item.get("fileDate") or ""), reverse=True)
    versions: list[dict] = []
    for item in files[:limit]:
        tags = [str(tag) for tag in (item.get("gameVersions") or [])]
        versions.append(
            {
                "version": item.get("displayName") or item.get("fileName") or "",
                "game_versions": [tag for tag in tags if VERSION_TAG.match(tag)],
                "loaders": [tag for tag in tags if not VERSION_TAG.match(tag)],
            }
        )
    return versions


def api_key() -> str:
    env = os.environ.get("CRAFTUNITY_CURSEFORGE_KEY", "").strip()
    if env:
        return env
    return str(config.load_settings().get("curseforge_key") or "").strip()


def available() -> bool:
    return bool(api_key())


def check_key(key: str) -> None:
    """Проверяет ключ одним запросом; при отказе бросает понятную ошибку."""
    value = str(key or "").strip()
    if not value:
        raise LauncherError("Ключ пустой.")
    url = f"{API}/mods/search?gameId={GAME_ID}&classId=6&pageSize=1"
    try:
        http_json(url, headers={"x-api-key": value}, timeout=20)
    except LauncherError as exc:
        text = str(exc)
        if "HTTP 401" in text or "HTTP 403" in text:
            raise LauncherError(
                "CurseForge отклонил ключ. Скопируйте его целиком из console.curseforge.com."
            ) from exc
        raise


def _get(path: str, params: dict | None = None) -> dict:
    key = api_key()
    if not key:
        raise LauncherError("CurseForge выключен: в настройках не задан ключ API.")
    url = f"{API}{path}"
    if params:
        url += "?" + urlencode({k: v for k, v in params.items() if v not in (None, "")})
    return http_json(url, headers={"x-api-key": key, "Accept": "application/json"}, timeout=25)


def project_info(project_id: str) -> dict:
    """Данные проекта в том же виде, что отдаёт Modrinth, для окна карточки."""
    data = project(project_id)
    if not data:
        raise LauncherError("Проект на CurseForge не найден.")
    logo = data.get("logo") or {}
    links = data.get("links") or {}
    authors = [
        {
            "username": str(author.get("name") or ""),
            "url": links.get("sourceUrl") or "",
            "role": "автор",
        }
        for author in (data.get("authors") or [])
        if author.get("name")
    ]
    gallery = [
        {
            "url": str(shot.get("thumbnailUrl") or shot.get("url") or ""),
            "title": str(shot.get("title") or ""),
            "description": str(shot.get("description") or ""),
        }
        for shot in (data.get("screenshots") or [])
        if shot.get("thumbnailUrl") or shot.get("url")
    ]
    return {
        "id": f"cf:{data.get('id')}",
        "slug": data.get("slug") or "",
        "title": data.get("name") or "CurseForge мод",
        "description": data.get("summary") or "",
        "body": description(project_id) or (data.get("summary") or ""),
        "icon": logo.get("thumbnailUrl") or "",
        "icon_hd": logo.get("thumbnailUrl") or "",
        "downloads": data.get("downloadCount") or 0,
        "followers": 0,
        "categories": [str(c.get("name") or "") for c in (data.get("categories") or [])],
        "project_type": "mod",
        "license": "",
        "authors": authors,
        "gallery": gallery,
        "versions": latest_versions(project_id),
        "source": "curseforge",
        "site_url": links.get("websiteUrl") or "",
        "page_url": links.get("websiteUrl")
        or f"https://www.curseforge.com/minecraft/mc-mods/{data.get('slug') or ''}",
    }


def _hit(mod: dict) -> dict:
    mod_id = mod.get("id")
    logo = mod.get("logo") or {}
    links = mod.get("links") or {}
    authors = [str(a.get("name") or "").strip() for a in (mod.get("authors") or [])]
    author = ", ".join([name for name in authors if name])
    return {
        "id": f"cf:{mod_id}",
        "project_id": f"cf:{mod_id}",
        "slug": mod.get("slug") or str(mod_id),
        "title": mod.get("name") or "CurseForge мод",
        "description": mod.get("summary") or "",
        "author": author,
        "author_url": links.get("sourceUrl") or "",
        "downloads": mod.get("downloadCount") or 0,
        "icon": logo.get("thumbnailUrl") or "",
        "icon_hd": logo.get("thumbnailUrl") or "",
        "categories": [str(c.get("name") or "") for c in (mod.get("categories") or [])],
        "source": "curseforge",
        "project_type": "mod",
        "versions": mod.get("latestFilesIndexes") or [],
    }


def search(
    query: str,
    game_version: str,
    loader: str,
    limit: int = 20,
    offset: int = 0,
    sort: str = "relevance",
    kind: str = "mod",
) -> dict:
    if kind not in CLASS_IDS:
        kind = "mod"
    params = {
        "gameId": GAME_ID,
        "classId": CLASS_IDS[kind],
        "searchFilter": query or "",
        "gameVersion": game_version,
        "index": offset,
        "pageSize": max(1, min(int(limit), 50)),
        "sortField": SORT_FIELDS.get(sort, 2),
        "sortOrder": "desc",
    }
    loader_type = LOADER_TYPES.get(loader)
    if kind == "mod" and loader_type:
        params["modLoaderType"] = loader_type
    data = _get("/mods/search", params)
    hits = [_hit(mod) for mod in (data.get("data") or [])]
    total = (data.get("pagination") or {}).get("totalCount") or len(hits)
    return {"hits": hits, "total_hits": total, "offset": offset, "limit": params["pageSize"]}


def project(mod_id: str) -> dict:
    key = str(mod_id).replace("cf:", "")
    data = _get(f"/mods/{key}")
    return data.get("data") or {}


def project_files(mod_id: str, game_version: str, loader: str, kind: str = "mod") -> list[dict]:
    key = str(mod_id).replace("cf:", "")
    params = {"gameVersion": game_version, "pageSize": 50}
    loader_type = LOADER_TYPES.get(loader)
    if kind == "mod" and loader_type:
        params["modLoaderType"] = loader_type
    data = _get(f"/mods/{key}/files", params)
    files = list(data.get("data") or [])
    files.sort(key=lambda item: str(item.get("fileDate") or ""), reverse=True)
    result: list[dict] = []
    for item in files:
        versions = [str(v) for v in (item.get("gameVersions") or [])]
        if game_version and versions and game_version not in versions:
            continue
        url = str(item.get("downloadUrl") or "").strip()
        if not url:
            continue
        hash_value = ""
        for entry in item.get("hashes") or []:
            if str(entry.get("algo")) == "1":
                hash_value = str(entry.get("value") or "")
                break
        result.append(
            {
                "url": url,
                "filename": item.get("fileName") or f"curseforge-{key}.jar",
                "size": item.get("fileLength") or 0,
                "version": item.get("displayName") or item.get("fileName") or "",
                "hashes": {"sha1": hash_value.lower()},
                "date": item.get("fileDate") or "",
            }
        )
    return result
