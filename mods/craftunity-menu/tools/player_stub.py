"""Рисует заглушку модели игрока (standing) для превью — только для макета, в jar не попадает."""

from __future__ import annotations

from PIL import Image, ImageDraw

FACE = (0xE8, 0xB8, 0x96)
SHIRT = (0x39, 0x4B, 0x8A)
PANTS = (0x27, 0x2F, 0x52)
SHOE = (0x1A, 0x1E, 0x30)


def _shade(color, k: float):
    return tuple(max(0, min(255, int(c * k))) for c in color)


def build_player(size: tuple[int, int]) -> Image.Image:
    w, h = size
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    unit = h / 32.0
    cx = w / 2

    d.rectangle((cx - 4 * unit, 0, cx + 4 * unit, 8 * unit), fill=FACE)
    d.rectangle((cx - 4 * unit, 0, cx + 4 * unit, 2.2 * unit), fill=_shade(FACE, 0.6))
    for dx in (-2.6, 1.4):
        d.rectangle((cx + dx * unit, 3.4 * unit, cx + (dx + 1.2) * unit, 4.4 * unit), fill=(0x2A, 0x2A, 0x3A))

    body_top = 8 * unit
    d.rectangle((cx - 4 * unit, body_top, cx + 4 * unit, body_top + 12 * unit), fill=SHIRT)
    for side in (-1, 1):
        x0 = cx + (4 * unit if side > 0 else -6 * unit)
        d.rectangle((x0, body_top + unit, x0 + 2 * unit, body_top + 12 * unit), fill=_shade(SHIRT, 0.85))
        d.rectangle((x0, body_top + 12 * unit, x0 + 2 * unit, body_top + 14 * unit), fill=FACE)

    leg_top = body_top + 12 * unit
    for dx in (0, -4 * unit):
        d.rectangle((cx + dx, leg_top, cx + dx + 4 * unit, leg_top + 10 * unit), fill=PANTS)
        d.rectangle((cx + dx, leg_top + 10 * unit, cx + dx + 4 * unit, leg_top + 12 * unit), fill=SHOE)
    return img
