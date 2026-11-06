from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

LOGO_X = 260
LOGO_Y = -300


def blocks(text: str) -> list[tuple[int, int, str]]:
    out = []
    for m in re.finditer(r"(?m)^(\w[\w ]*) \{", text):
        start = m.start()
        depth = 0
        i = m.end() - 1
        while i < len(text):
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        out.append((start, i + 1, m.group(1)))
    return out


def set_field(block: str, key: str, value) -> str:
    return re.sub(rf"^(\s*{key} = ).*$", lambda m: f"{m.group(1)}{value}", block, count=1, flags=re.MULTILINE)


def main() -> None:
    text = P.read_text(encoding="utf-8")
    out = []
    cursor = 0
    for start, end, kind in blocks(text):
        chunk = text[start:end]
        new = chunk
        if kind == "element" and "element_type = image" in chunk:
            new = set_field(chunk, "anchor_point", "mid-centered")
            new = set_field(new, "x", LOGO_X)
            new = set_field(new, "y", LOGO_Y)
        out.append(text[cursor:start])
        out.append(new)
        cursor = end
    out.append(text[cursor:])
    P.write_text("".join(out), encoding="utf-8")
    print("logo centered above buttons")


if __name__ == "__main__":
    main()
