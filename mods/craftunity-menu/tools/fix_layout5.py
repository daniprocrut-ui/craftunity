from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"


def index_blocks(text: str) -> list[tuple[int, int, str]]:
    """Возвращает (начало, конец, тип) для каждого блока верхнего уровня вида `тип { ... }`."""
    result = []
    for match in re.finditer(r"(?m)^(\w[\w ]* \{)", text):
        start = match.start()
        depth = 0
        i = match.end() - 1
        while i < len(text):
            ch = text[i]
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        block_type = match.group(1).split(" {")[0]
        result.append((start, i + 1, block_type))
    return result


def set_x(block: str, value: int) -> str:
    return re.sub(r"^(\s*x = ).*$", rf"\g<1>{value}", block, count=1, flags=re.MULTILINE)


def main() -> None:
    text = P.read_text(encoding="utf-8")
    blocks = index_blocks(text)

    pieces = []
    cursor = 0
    changes = []
    for start, end, kind in blocks:
        chunk = text[start:end]
        new = chunk
        if kind == "element" and "element_type = image" in chunk:
            new = set_x(chunk, 1410)
            label = "logo"
        elif kind == "element" and "element_type = player_entity_v2" in chunk:
            new = set_x(chunk, 0)
            label = "skin"
        elif kind in ("element", "vanilla_button") and "anchor_point = mid-centered" in chunk:
            new = set_x(chunk, -330)
            label = "button/side"
        else:
            label = "other"
        if new != chunk:
            changes.append(label)
        pieces.append((start, cursor, new))
        cursor = end

    out = []
    cursor = 0
    for start, _, new in pieces:
        out.append(text[cursor:start])
        out.append(new)
        cursor = start + len(new) if False else start + len(new)
        # сдвигаем курсор по исходной длине блока
        cursor = start + len(new) - (len(new) - len(new))
    # проще пересобрать заново
    out = []
    cursor = 0
    for start, end, kind in blocks:
        chunk = text[start:end]
        new = chunk
        if kind == "element" and "element_type = image" in chunk:
            new = set_x(chunk, 1410)
        elif kind == "element" and "element_type = player_entity_v2" in chunk:
            new = set_x(chunk, 0)
        elif kind in ("element", "vanilla_button") and "anchor_point = mid-centered" in chunk:
            new = set_x(chunk, -330)
        out.append(text[cursor:start])
        out.append(new)
        cursor = end
    out.append(text[cursor:])
    P.write_text("".join(out), encoding="utf-8")
    print("changes:", changes)


if __name__ == "__main__":
    main()
