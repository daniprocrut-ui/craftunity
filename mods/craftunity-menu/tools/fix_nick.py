from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"


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


def main() -> None:
    text = P.read_text(encoding="utf-8")
    out = []
    cursor = 0
    for start, end, kind in blocks(text):
        chunk = text[start:end]
        new = chunk
        if kind == "element" and 'placeholder":"playername"' in chunk:
            new = re.sub(r"^  x = .*$", "  x = 0", chunk, count=1, flags=re.MULTILINE)
            new = re.sub(r"^  y = .*$", "  y = 118", new, count=1, flags=re.MULTILINE)
            new = re.sub(r"^  width = .*$", "  width = 220", new, count=1, flags=re.MULTILINE)
        out.append(text[cursor:start])
        out.append(new)
        cursor = end
    out.append(text[cursor:])
    P.write_text("".join(out), encoding="utf-8")
    print("nick moved under skin")


if __name__ == "__main__":
    main()
