from __future__ import annotations

import re

from . import config

THEME_NAMES = {
    "dark": "Тёмная",
    "light": "Светлая",
    "midnight": "Полуночная",
    "forest": "Лесная",
    "sunset": "Закатная",
    "ocean": "Океанская",
    "sakura": "Сакура",
    "neon": "Неоновая",
    "graphite": "Графитовая",
    "crimson": "Багровая",
    "coffee": "Кофейная",
    "sand": "Песочная",
    "lavender": "Лавандовая",
    "arctic": "Арктическая",
}

DEFAULT_THEME = "dark"

FALLBACK = {
    "bg": "#090b14",
    "bg_alt": "#141a2c",
    "card": "#1c2338",
    "fg": "#e9eefb",
    "muted": "#8b95b2",
    "accent": "#fb923c",
    "accent_fg": "#1c0f02",
    "border": "#2a3350",
}

VAR_MAP = {
    "bg": "--bg",
    "bg_alt": "--panel",
    "card": "--panel-2",
    "fg": "--text",
    "muted": "--muted",
    "accent": "--accent",
    "accent_fg": "--btn-text",
    "border": "--line",
}

CARD_MAP = {"--card-bg": "card"}


def _hex(value: str) -> tuple[int, int, int] | None:
    text = value.strip().lower()
    if re.fullmatch(r"#[0-9a-f]{3}", text):
        return tuple(int(ch * 2, 16) for ch in text[1:])
    if re.fullmatch(r"#[0-9a-f]{6}", text):
        return tuple(int(text[i:i + 2], 16) for i in (1, 3, 5))
    return None


def _rgb(value: tuple[int, int, int]) -> str:
    return "#%02x%02x%02x" % tuple(max(0, min(255, int(round(channel)))) for channel in value)


def _mix(base: str, other: str, amount: float) -> str:
    left, right = _hex(base), _hex(other)
    if not left or not right:
        return base
    return _rgb(tuple(a + (b - a) * amount for a, b in zip(left, right)))


def _luminance(value: str) -> float:
    channels = _hex(value) or (255, 255, 255)
    total = 0.0
    for channel in channels:
        part = channel / 255
        total += part / 12.92 if part <= 0.03928 else ((part + 0.055) / 1.055) ** 2.4
    return total / 3


def _css_blocks() -> dict[str, dict[str, str]]:
    try:
        text = config.resource_path("ui", "styles.css").read_text(encoding="utf-8")
    except OSError:
        return {}
    blocks: dict[str, dict[str, str]] = {}
    for name, body in re.findall(r'html\[data-theme="([a-z0-9-]+)"\]\s*\{(.*?)\n\}', text, re.S):
        blocks[name] = dict(re.findall(r"(--[a-z0-9-]+):\s*([^;]+);", body))
    return blocks


def theme_ids() -> list[str]:
    found = [name for name in _css_blocks() if name in THEME_NAMES]
    order = [name for name in THEME_NAMES if name in found]
    return order or list(THEME_NAMES)


def theme_name(theme: str) -> str:
    return THEME_NAMES.get(theme, THEME_NAMES[DEFAULT_THEME])


def normalize(theme: str | None) -> str:
    return theme if theme in THEME_NAMES else DEFAULT_THEME


def palette(theme: str | None = None) -> dict[str, str]:
    name = normalize(theme)
    block = _css_blocks().get(name)
    if not block and name != DEFAULT_THEME:
        block = _css_blocks().get(DEFAULT_THEME)
    values = dict(FALLBACK)
    for key, variable in VAR_MAP.items():
        value = (block or {}).get(variable, "").strip()
        if _hex(value):
            values[key] = _rgb(_hex(value))
    card_gradient = (block or {}).get("--card-bg", "")
    first_stop = re.search(r"#[0-9a-fA-F]{3,6}", card_gradient)
    if first_stop and _hex(first_stop.group(0)):
        values["bg_alt"] = _rgb(_hex(first_stop.group(0)))
    if not (block or {}).get("--btn-text") and _hex(values["accent"]):
        values["accent_fg"] = "#101418" if _luminance(values["accent"]) > 0.4 else "#ffffff"
    values["accent_hover"] = _mix(values["accent"], "#ffffff", 0.18)
    values["card_hover"] = _mix(values["card"], values["fg"], 0.07)
    values["sel_bg"] = _mix(values["card"], values["accent"], 0.32)
    values["accent_soft"] = _mix(values["card"], values["accent"], 0.18)
    values["disabled_bg"] = _mix(values["card"], values["bg"], 0.45)
    values["disabled_fg"] = _mix(values["muted"], values["bg"], 0.3)
    values["accent_disabled_bg"] = _mix(values["card"], values["accent"], 0.28)
    values["accent_disabled_fg"] = _mix(values["muted"], values["bg"], 0.2)
    values["danger"] = "#ef4444"
    return values


def launcher_theme() -> str:
    return normalize(config.load_settings().get("theme"))
