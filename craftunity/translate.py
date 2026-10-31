from __future__ import annotations

import hashlib
import json
import urllib.parse
import urllib.request

from . import config
from .util import LauncherError, ssl_context

GOOGLE_URL = "https://translate.googleapis.com/translate_a/single"
GOOGLE_DICT_URL = "https://clients5.google.com/translate_a/t"
MYMEMORY_URL = "https://api.mymemory.translated.net/get"

MAX_CHUNK = 1200
CACHE_LIMIT = 400


def _cache_path():
    return config.data_dir() / "translations.json"


def _load_cache() -> dict:
    return config.load_json(_cache_path(), {})


def _save_cache(key: str, value: str) -> None:
    cache = _load_cache()
    cache[key] = value
    if len(cache) > CACHE_LIMIT:
        for old_key in list(cache)[: len(cache) - CACHE_LIMIT]:
            cache.pop(old_key, None)
    try:
        config.save_json(_cache_path(), cache)
    except OSError:
        pass


def _chunks(text: str, limit: int) -> list[str]:
    paragraphs = [part.strip() for part in text.splitlines() if part.strip()]
    chunks: list[str] = []
    current = ""
    for paragraph in paragraphs:
        while len(paragraph) > limit:
            chunks.append(paragraph[:limit])
            paragraph = paragraph[limit:]
        if current and len(current) + len(paragraph) + 2 > limit:
            chunks.append(current)
            current = paragraph
        else:
            current = f"{current}\n{paragraph}" if current else paragraph
    if current:
        chunks.append(current)
    return chunks or [text[:limit]]


def _fetch_json(url: str, timeout: int) -> dict | list:
    with urllib.request.urlopen(url, timeout=timeout, context=ssl_context()) as response:
        return json.loads(response.read().decode("utf-8"))


def _google_gtx(text: str, target: str, timeout: int) -> str:
    parts = []
    for chunk in _chunks(text, 4000):
        query = urllib.parse.urlencode(
            {"client": "gtx", "sl": "auto", "tl": target, "dt": "t", "q": chunk}
        )
        data = _fetch_json(f"{GOOGLE_URL}?{query}", timeout)
        segments = data[0] if isinstance(data, list) and data else []
        parts.append("".join(segment[0] for segment in segments if segment and segment[0]))
    return "\n".join(parts)


def _google_dict(text: str, target: str, timeout: int) -> str:
    parts = []
    for chunk in _chunks(text, 1500):
        query = urllib.parse.urlencode({"client": "dict-chrome-ex", "sl": "auto", "tl": target, "q": chunk})
        data = _fetch_json(f"{GOOGLE_DICT_URL}?{query}", timeout)
        if isinstance(data, list):
            parts.append(" ".join(str(item) for item in data if isinstance(item, str)))
        elif isinstance(data, dict) and data.get("sentences"):
            parts.append(" ".join(segment.get("trans", "") for segment in data["sentences"]))
        else:
            raise LauncherError("Непонятный ответ переводчика.")
    return "\n".join(parts)


def _mymemory(text: str, target: str, timeout: int) -> str:
    parts = []
    for chunk in _chunks(text, 450):
        query = urllib.parse.urlencode({"q": chunk, "langpair": f"en|{target}"})
        data = _fetch_json(f"{MYMEMORY_URL}?{query}", timeout)
        translated = (data.get("responseData") or {}).get("translatedText") if isinstance(data, dict) else ""
        if not translated:
            raise LauncherError("MyMemory не вернул перевод.")
        parts.append(translated)
    return "\n".join(parts)


PROVIDERS = (_google_gtx, _google_dict, _mymemory)


def translate(text: str, target: str = "ru", timeout: int = 20) -> str:
    text = (text or "").strip()
    if not text:
        return ""
    if target == "en" and not any("а" <= ch.lower() <= "я" for ch in text[:200]):
        return text
    key = hashlib.sha1(f"{target}:{text}".encode("utf-8")).hexdigest()
    cache = _load_cache()
    if key in cache:
        return cache[key]
    error: Exception | None = None
    for provider in PROVIDERS:
        try:
            result = provider(text, target, timeout)
        except Exception as exc:  # noqa: BLE001
            error = exc
            continue
        if result.strip():
            _save_cache(key, result.strip())
            return result.strip()
    raise LauncherError(f"Перевод временно недоступен: {error}")