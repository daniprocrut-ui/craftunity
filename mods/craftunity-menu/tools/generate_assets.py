from __future__ import annotations

import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[3]
ASSETS = ROOT / "mods" / "craftunity-menu" / "assets"
ICON = ROOT / "icon.png"

BG = (0x09, 0x0B, 0x14)
PANEL = (0x14, 0x1A, 0x2C)
PANEL_2 = (0x1C, 0x23, 0x38)
LINE = (0x2A, 0x33, 0x50)
TEXT = (0xE9, 0xEE, 0xFB)
MUTED = (0x8B, 0x95, 0xB2)
ACCENT = (0xFB, 0x92, 0x3C)
ACCENT_2 = (0xFB, 0xBF, 0x24)
ACCENT_DARK = (0xF9, 0x73, 0x16)
BTN_TEXT = (0x1C, 0x0F, 0x02)

FONT_BOLD = "C:/Windows/Fonts/seguibl.ttf"
FONT_SEMI = "C:/Windows/Fonts/seguisb.ttf"
FONT_REG = "C:/Windows/Fonts/segoeui.ttf"


def font(path: str, size: int) -> ImageFont.FreeTypeFont:
    try:
        return ImageFont.truetype(path, size)
    except OSError:
        return ImageFont.load_default(size)


def noise(size: tuple[int, int], amount: int, seed: int) -> Image.Image:
    rnd = random.Random(seed)
    img = Image.new("L", size)
    img.putdata([rnd.randint(0, amount) for _ in range(size[0] * size[1])])
    return img


def radial_glow(size, center, radius, color, strength=1.0) -> Image.Image:
    w, h = size
    small = (max(1, w // 8), max(1, h // 8))
    layer = Image.new("RGB", small, (0, 0, 0))
    px = layer.load()
    cx, cy = center[0] / 8, center[1] / 8
    r = max(1.0, radius / 8)
    for y in range(small[1]):
        for x in range(small[0]):
            d = math.hypot(x - cx, y - cy) / r
            if d >= 1:
                continue
            k = (1 - d) ** 2 * strength
            px[x, y] = tuple(min(255, int(c * k)) for c in color)
    return layer.resize(size, Image.BICUBIC).filter(ImageFilter.GaussianBlur(radius * 0.35))


def rounded_mask(size, radius) -> Image.Image:
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=radius, fill=255)
    return mask


def vertical_gradient(size, top, bottom) -> Image.Image:
    img = Image.new("RGB", size)
    px = img.load()
    for y in range(size[1]):
        k = y / max(1, size[1] - 1)
        row = tuple(int(top[i] + (bottom[i] - top[i]) * k) for i in range(3))
        for x in range(size[0]):
            px[x, y] = row
    return img


def build_background() -> None:
    """Фон: ровная сетка — левая панель 96..512, контент 512..1824, без разрозненных рамок."""
    size = (1920, 1080)
    img = Image.new("RGB", size, BG)
    img = Image.blend(img, radial_glow(size, (1500, 180), 1200, ACCENT, 0.30), 1.0)
    img = Image.blend(img, radial_glow(size, (260, 1040), 900, ACCENT_2, 0.12), 1.0)

    vignette = Image.new("L", size, 0)
    ImageDraw.Draw(vignette).ellipse(
        (-size[0] * 0.5, -size[1] * 0.6, size[0] * 1.5, size[1] * 1.7), fill=255)
    vignette = vignette.filter(ImageFilter.GaussianBlur(220))
    img = Image.composite(img, Image.new("RGB", size, (4, 6, 13)), vignette)

    overlay = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)

    d.rounded_rectangle((96, 96, 1824, 984), radius=28, fill=(9, 11, 20, 224))
    d.rounded_rectangle((96, 96, 1824, 984), radius=28, outline=(124, 140, 190, 56), width=2)
    d.rounded_rectangle((452, 128, 454, 952), fill=(124, 140, 190, 46))

    img = Image.alpha_composite(img.convert("RGBA"), overlay).convert("RGB")
    img.convert("RGB").save(ASSETS / "background.png", optimize=True)


def _add(img: Image.Image, layer: Image.Image, alpha: float) -> Image.Image:
    return Image.blend(img, Image.composite(layer, img, Image.new("L", img.size, int(255 * alpha))), 1.0)


def _captions(base: Image.Image) -> None:
    img = base.convert("RGBA")
    d = ImageDraw.Draw(img, "RGBA")
    d.text((152, 860), "Сборка 26.2 · Fabric", font=font(FONT_REG, 22), fill=MUTED + (215,))
    return img


def build_logo() -> None:
    """Иконка 64 px + две строки текста, выровненные по её левому краю и вертикали."""
    w, h = 640, 96
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    mark = Image.open(ICON).convert("RGBA").resize((64, 64), Image.LANCZOS)
    img.paste(mark, (0, 16), rounded_mask((64, 64), 16))

    d.text((84, 4), "CRAFTUNITY", font=font(FONT_BOLD, 34), fill=TEXT + (255,))
    d.text((86, 52), "LAUNCHER", font=font(FONT_SEMI, 17), fill=ACCENT + (255,))
    img.save(ASSETS / "logo.png", optimize=True)


def build_button(name: str, top, bottom, border, label_color, radius=18) -> None:
    w, h = 512, 104
    grad = vertical_gradient((w, h), top, bottom).convert("RGBA")
    gloss = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(gloss).rounded_rectangle((0, 0, w - 1, int(h * 0.52)), radius=radius,
                                            fill=(255, 255, 255, 26))
    grad.alpha_composite(gloss)
    d = ImageDraw.Draw(grad)
    d.rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, outline=border + (255,), width=3)
    d.rounded_rectangle((2, 2, w - 3, h - 3), radius=radius - 2, outline=(255, 255, 255, 30), width=2)
    mask = rounded_mask((w, h), radius)
    base = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    base.paste(grad, (0, 0), mask)
    base.save(ASSETS / name, optimize=True)


def build_panel() -> None:
    w, h = 600, 400
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    g = vertical_gradient((w, h), (0x1D, 0x25, 0x3C), (0x10, 0x15, 0x23)).convert("RGBA")
    g.putalpha(234)
    mask = rounded_mask((w, h), 28)
    img.paste(g, (0, 0), mask)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, w - 1, h - 1), radius=28, outline=(124, 140, 190, 70), width=3)
    img.save(ASSETS / "panel.png", optimize=True)

    div = Image.new("RGBA", (400, 6), (0, 0, 0, 0))
    dd = ImageDraw.Draw(div)
    for x in range(400):
        k = x / 399
        col = tuple(int(ACCENT_2[i] + (ACCENT_DARK[i] - ACCENT_2[i]) * k) for i in range(3))
        dd.line((x, 0, x, 5), fill=col + (int(255 * (1 - abs(0.5 - k) * 1.2)),))
    div.save(ASSETS / "divider.png", optimize=True)

    sw, sh = 300, 260
    side = Image.new("RGBA", (sw, sh), (0, 0, 0, 0))
    sg = vertical_gradient((sw, sh), (0x14, 0x1A, 0x2C), (0x0B, 0x0F, 0x1B)).convert("RGBA")
    sg.putalpha(225)
    side.paste(sg, (0, 0), rounded_mask((sw, sh), 22))
    sd = ImageDraw.Draw(side)
    sd.rounded_rectangle((0, 0, sw - 1, sh - 1), radius=22, outline=(124, 140, 190, 55), width=2)
    sd.rounded_rectangle((0, 10, 5, sh - 11), radius=2, fill=ACCENT + (255,))
    side.save(ASSETS / "sidebar.png", optimize=True)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    build_background()
    build_logo()
    build_button("btn_normal.png", (0x1A, 0x21, 0x36), (0x11, 0x16, 0x26), (0x7C, 0x8C, 0xBE), TEXT)
    build_button("btn_hover.png", (0x2A, 0x33, 0x52), (0x17, 0x1D, 0x30), ACCENT, (0xFF, 0xD2, 0xA8))
    build_button("btn_focus.png", (0xFB, 0x92, 0x3C), (0xF9, 0x73, 0x16), (0xFB, 0xBF, 0x24), BTN_TEXT)
    build_button("btn_focus_hover.png", (0xFD, 0xB4, 0x6A), (0xFB, 0x92, 0x3C), (0xFB, 0xBF, 0x24), BTN_TEXT)
    build_panel()
    print("assets written to", ASSETS)


if __name__ == "__main__":
    main()
