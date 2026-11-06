from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

# Кнопки: уже (300), левее центра (x=-70 относительно центра экрана), шаг 54.
BTN_X = "-70"
BTN_W = "300"
BTN_H = "40"
BTN_YS = ["-150", "-96", "-42", "12", "66"]

# Блок «Новости сервера» — под кнопками, по той же оси.
NEWS_Y = "130"
NEWS_H = "40"


def set_field(block: str, key: str, value: str) -> str:
    return re.sub(rf"^(\s*{key} = ).*$", rf"\g<1>{value}", block, count=1, flags=re.MULTILINE)


def main() -> None:
    text = P.read_text(encoding="utf-8")

    parts = re.split(r"(?m)^(vanilla_button \{)$", text)
    out = [parts[0]]
    btn_index = 0
    for i in range(1, len(parts), 2):
        blk = "vanilla_button {" + parts[i + 1]
        blk = set_field(blk, "x", BTN_X)
        blk = set_field(blk, "y", BTN_YS[btn_index])
        blk = set_field(blk, "width", BTN_W)
        blk = set_field(blk, "height", BTN_H)
        out.append(blk)
        btn_index += 1
    text = "".join(out)

    parts = re.split(r"(?m)^(element \{)$", text)
    out = [parts[0]]
    for i in range(1, len(parts), 2):
        blk = "element {" + parts[i + 1]
        if "anchor_point = mid-centered" in blk:
            blk = set_field(blk, "x", BTN_X)
            blk = set_field(blk, "y", NEWS_Y)
            blk = set_field(blk, "width", BTN_W)
            blk = set_field(blk, "height", NEWS_H)
        elif "anchor_point = top-left" in blk:
            # Логотип и описание — правее, ближе к колонке контента.
            blk = set_field(blk, "x", "560")
            if "element_type = image" in blk:
                blk = set_field(blk, "y", "150")
                blk = set_field(blk, "width", "320")
                blk = set_field(blk, "height", "48")
            else:
                blk = set_field(blk, "y", "200")
                blk = set_field(blk, "width", "320")
                blk = set_field(blk, "height", "150")
        out.append(blk)
    text = "".join(out)

    P.write_text(text, encoding="utf-8")
    print("layout updated")


if __name__ == "__main__":
    main()
