from __future__ import annotations

import re
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
SYS = ROOT / "mods" / "craftunity-menu" / "src" / "main" / "resources" / "craftunitymenu" / "assets" / "craftunity"
OUT = ROOT / "mods" / "craftunity-menu" / "preview" / "title_screen_preview.png"
LAYOUT = ROOT / "mods" / "craftunity-menu" / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

TEXT = (0xE9, 0xEE, 0xFB)
MUTED = (0x8B, 0x95, 0xB2)
ACCENT = (0xFB, 0x92, 0x3C)
BTN_TEXT = (0xFF, 0xE7, 0xD1)
FONT = "C:/Windows/Fonts/seguisb.ttf"

LABELS = {
    "mc_titlescreen_singleplayer_button": "Одиночная игра",
    "mc_titlescreen_multiplayer_button": "Сетевая игра",
    "mc_titlescreen_options_button": "Настройки...",
    "mc_titlescreen_realms_button": "Realms",
    "mc_titlescreen_quit_button": "Выход из игры",
}


def prop(block: str, key: str) -> str:
    m = re.search(rf"^\s*{key} = ?(.*)$", block, re.MULTILINE)
    return m.group(1).strip() if m else ""


def blocks(src: str, kind: str) -> list[str]:
    return re.findall(rf"^{kind} \{{\n(.*?)^\}}", src, re.MULTILINE | re.DOTALL)


def font(path: str, size: int) -> ImageFont.FreeTypeFont:
    try:
        return ImageFont.truetype(path, size)
    except OSError:
        return ImageFont.load_default(size)


def scale_for(canvas: tuple[int, int]) -> float:
    base_w, base_h = 1920, 1080
    return min(canvas[0] / base_w, canvas[1] / base_h)


def nine_slice(tex: Image.Image, size: tuple[int, int], border: int) -> Image.Image:
    w, h = size
    b = max(1, min(border, (w - 1) // 2, (h - 1) // 2))
    out = Image.new("RGBA", size, (0, 0, 0, 0))
    tw, th = tex.size
    out.paste(tex.crop((0, 0, b, b)), (0, 0))
    out.paste(tex.crop((tw - b, 0, tw, b)), (w - b, 0))
    out.paste(tex.crop((0, th - b, b, th)), (0, h - b))
    out.paste(tex.crop((tw - b, th - b, tw, th)), (w - b, h - b))
    mid_h = Image.new("RGBA", (w - 2 * b, b), (0, 0, 0, 0))
    left = tex.crop((b, 0, tw - b, b)).resize((1, b), Image.BILINEAR)
    mid_h = Image.new("RGBA", (w - 2 * b, b), (0, 0, 0, 0))
    for x in range(w - 2 * b):
        mid_h.paste(left, (x, 0))
    out.paste(mid_h, (b, 0))
    out.paste(mid_h, (b, h - b))
    mid_v = Image.new("RGBA", (b, h - 2 * b), (0, 0, 0, 0))
    for y in range(h - 2 * b):
        mid_v.paste(tex.crop((0, b, b, th - b)).resize((b, 1), Image.BILINEAR), (0, y))
    out.paste(mid_v, (0, b))
    out.paste(mid_v, (w - b, b))
    out.paste(tex.crop((b, b, tw - b, th - b)).resize((w - 2 * b, h - 2 * b), Image.BILINEAR), (b, b))
    return out


def place(img: Image.Image, el: Image.Image, anchor: str, x: float, y: float, width: float, height: float,
          canvas: tuple[int, int], scale: float) -> None:
    w, h = canvas
    cx, cy = w / 2, h / 2
    if anchor.startswith("top"):
        ay = 0
    elif anchor.startswith("mid"):
        ay = cy
    else:
        ay = h
    if anchor.endswith("centered"):
        ax = cx
    elif anchor.endswith("left"):
        ax = 0
    else:
        ax = w
    pos_x = int(round(ax + x * scale - width * scale / 2))
    pos_y = int(round(ay + y * scale - height * scale / 2))
    size = (max(1, int(round(width * scale))), max(1, int(round(height * scale))))
    img.alpha_composite(el.resize(size, Image.LANCZOS), (pos_x, pos_y))


def render(canvas: tuple[int, int]) -> Image.Image:
    src = LAYOUT.read_text(encoding="utf-8")
    scale = scale_for(canvas)
    bg = Image.open(SYS / "background.png").convert("RGB")
    img = bg.resize(canvas, Image.LANCZOS).convert("RGBA")

    for block in blocks(src, "element"):
        kind = prop(block, "element_type")
        anchor = prop(block, "anchor_point")
        x, y = float(prop(block, "x")), float(prop(block, "y"))
        w, h = float(prop(block, "width")), float(prop(block, "height"))
        if kind == "image":
            path = prop(block, "source").split("assets/craftunity/")[-1]
            tex = Image.open(SYS / path).convert("RGBA")
            place(img, tex, anchor, x, y, w, h, canvas, scale)
        elif kind == "player_entity_v2":
            from player_stub import build_player

            avatar = build_player((max(1, int(round(w * scale))), max(1, int(round(h * scale)))))
            place(img, avatar, anchor, x, y, w, h, canvas, scale)
        elif kind == "text_v2":
            body = prop(block, "source").replace("\\n", "\n")
            body = body.replace('{"placeholder":"playername"}', "Player")
            tw = int(round(w * scale))
            th = int(round(h * scale))
            f = font(FONT, int(round(15 * scale)))
            probe = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
            lines = []
            for paragraph in body.split("\n"):
                current = ""
                for word in paragraph.split():
                    candidate = f"{current} {word}".strip()
                    if probe.textlength(candidate, font=f) <= tw or not current:
                        current = candidate
                    else:
                        lines.append(current)
                        current = word
                lines.append(current)
            layer = Image.new("RGBA", (tw, max(1, th)), (0, 0, 0, 0))
            d = ImageDraw.Draw(layer)
            ty = 0
            step = int(round(20 * scale))
            for line in lines:
                if ty + step > th:
                    break
                d.text((0, ty), line, font=f, fill=(0xCD, 0xD6, 0xEA, 235))
                ty += step
            place(img, layer, anchor, x, y, w, h, canvas, scale)

    for block in blocks(src, "vanilla_button"):
        ident = prop(block, "instance_identifier")
        anchor = prop(block, "anchor_point")
        x, y = float(prop(block, "x")), float(prop(block, "y"))
        w, h = float(prop(block, "width")), float(prop(block, "height"))
        normal = Image.open(SYS / "btn_normal.png").convert("RGBA")
        hover = Image.open(SYS / "btn_hover.png").convert("RGBA")
        accent = Image.open(SYS / "btn_focus.png").convert("RGBA")
        accent_hover = Image.open(SYS / "btn_focus_hover.png").convert("RGBA")
        primary = ident == "mc_titlescreen_singleplayer_button"
        tex = accent if primary else normal
        btn = tex.resize((max(1, int(round(w * scale))), max(1, int(round(h * scale)))), Image.LANCZOS)
        label = LABELS.get(ident, "")
        d = ImageDraw.Draw(btn)
        f = font(FONT, max(9, int(round(10 * scale))))
        tw = d.textlength(label, font=f)
        color = BTN_TEXT if primary else TEXT
        d.text(((btn.width - tw) / 2, (btn.height - 12 * scale) / 2), label, font=f, fill=color + (255,))
        place(img, btn, anchor, x, y, w, h, canvas, scale)

    return img.convert("RGB")


def main() -> None:
    OUT.parent.mkdir(parents=True, exist_ok=True)
    render((1920, 1080)).save(OUT, optimize=True)
    render((1280, 720)).save(OUT.with_name("title_screen_preview_small.png"), optimize=True)
    print("saved", OUT)


if __name__ == "__main__":
    main()
