from __future__ import annotations

import re
import threading

from . import curseforge, mods
from .util import LauncherError

CLEAN = re.compile(r"[^0-9a-zа-яё]+")


def _key(text: str) -> str:
    return CLEAN.sub("", str(text or "").lower())


def _same_mod(left: dict, right: dict) -> bool:
    """Один и тот же мод в двух каталогах: совпадает название и автор."""
    left_title = _key(left.get("title"))
    right_title = _key(right.get("title"))
    if not left_title or left_title != right_title:
        return False
    left_author = _key(left.get("author"))
    right_author = _key(right.get("author"))
    if left_author and right_author:
        if left_author == right_author:
            return True
        return left_author in right_author or right_author in left_author
    return True


def search_all(
    query: str,
    game_version: str,
    loader: str,
    limit: int = 20,
    offset: int = 0,
    sort: str = "relevance",
    kind: str = "mod",
) -> dict:
    """Поиск сразу по Modrinth и CurseForge.

    Мод, который есть в обоих каталогах, показывается один раз — вариант с Modrinth.
    """
    results: dict[str, object] = {}

    def run_modrinth() -> None:
        try:
            results["modrinth"] = mods.search(
                query, game_version, loader, limit=limit, offset=offset, sort=sort, kind=kind
            )
        except LauncherError as exc:
            results["modrinth_error"] = str(exc)

    def run_curseforge() -> None:
        if not curseforge.available():
            return
        try:
            results["curseforge"] = curseforge.search(
                query, game_version, loader, limit=limit, offset=offset, sort=sort, kind=kind
            )
        except LauncherError as exc:
            results["curseforge_error"] = str(exc)

    workers = [threading.Thread(target=run_modrinth, daemon=True)]
    if curseforge.available():
        workers.append(threading.Thread(target=run_curseforge, daemon=True))
    for worker in workers:
        worker.start()
    for worker in workers:
        worker.join(timeout=30)

    modrinth = results.get("modrinth") or {"hits": [], "total_hits": 0}
    curse = results.get("curseforge") or {"hits": [], "total_hits": 0}

    seen: list[dict] = []
    merged: list[dict] = []
    for hit in modrinth.get("hits") or []:
        item = dict(hit)
        item["source"] = "modrinth"
        merged.append(item)
        seen.append(item)
    dropped = 0
    for hit in curse.get("hits") or []:
        if any(_same_mod(hit, other) for other in seen):
            dropped += 1
            continue
        item = dict(hit)
        merged.append(item)
        seen.append(item)

    payload = {
        "hits": merged[: max(1, int(limit))],
        "total_hits": int(modrinth.get("total_hits") or 0) + int(curse.get("total_hits") or 0),
        "offset": offset,
        "limit": limit,
        "curseforge": {
            "enabled": curseforge.available(),
            "found": len(curse.get("hits") or []),
            "duplicates": dropped,
        },
    }
    payload["total"] = payload["total_hits"]
    error = results.get("modrinth_error") or results.get("curseforge_error")
    if error:
        payload["warning"] = str(error)
    return payload
