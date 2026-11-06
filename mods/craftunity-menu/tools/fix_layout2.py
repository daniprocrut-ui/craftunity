from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

BTN_X, BTN_Y = "150", ["-165", "-99", "-33", "33", "99"]
CENTER_X = BTN_X

NEWS_SOURCE = "Новости сервера\\nОбновления модпака подтягиваются при запуске, вход по нику синхронизирован с лаунчером."


def set_field(block: str, key: str, value: str) -> str:
    return re.sub(rf"^(\s*{key} = ).*$", rf"\g<1>{value}", block, count=1, flags=re.MULTILINE)


def main() -> None:
    text = P.read_text(encoding="utf-8")
    parts = re.split(r"(?m)^(vanilla_button \{)$", text)
    header = parts[0]
    blocks = []
    for i in range(1, len(parts), 2):
        blocks.append(parts[i] + parts[i + 1])
    if len(blocks) != 5:
        raise SystemExit(f"ожидалось 5 кнопок, найдено {len(blocks)}")

    new_blocks = []
    for idx, block in enumerate(blocks):
        block = set_field(block, "x", BTN_X)
        block = set_field(block, "y", BTN_Y[idx])
        block = set_field(block, "width", "420")
        block = set_field(block, "height", "46")
        new_blocks.append(block)

    text = header + "".join(new_blocks)

    # «Новости сервера» — по центру под кнопками, текст главного блока — по левому краю колонки.
    element_blocks = re.split(r"(?m)^(element \{)$", text)
    out = [element_blocks[0]]
    for i in range(1, len(element_blocks), 2):
        blk = "element {" + element_blocks[i + 1]
        if "anchor_point = mid-centered" in blk and "text_v2" in blk:
            blk = set_field(blk, "y", "190")
            if "Новости сервера" not in blk:
                blk = re.sub(r"^(\s*source = ).*$", rf"\g<1>{NEWS_SOURCE}", blk, count=1, flags=re.MULTILINE)
        out.append(blk)
    text = "".join(out)

    P.write_text(text, encoding="utf-8")
    print("layout centered")


if __name__ == "__main__":
    main()
