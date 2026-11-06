from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

NAME = 'source = {"placeholder":"playername"}'
STATUS = "source = Craftunity 3 · 26.2"
HINT = "source = Настройки и профиль — в игре"


def fix_pairs(text: str) -> str:
    """Склеивает мохибейк, возникший при чтении UTF-8 как cp1251, обратно в юникод."""
    out = []
    for line in text.split("\n"):
        if any(ord(ch) in (0x0402, 0x0412, 0x0403, 0x2019, 0x2020) for ch in line) and "=" in line:
            key, _, value = line.partition("=")
            try:
                repaired = value.encode("cp1251", "strict").decode("utf-8", "strict")
            except (UnicodeEncodeError, UnicodeDecodeError):
                out.append(line)
                continue
            line = f"{key}= {repaired}"
        out.append(line)
    return "\n".join(out)


def main() -> None:
    raw = P.read_bytes()
    text = raw.decode("utf-8-sig")
    text = fix_pairs(text)

    text = re.sub(r"^\s*source = Ник: \{\"placeholder\":\"playername\"\}\n  align = center$",
                  NAME, text, flags=re.MULTILINE)
    text = text.replace(NAME + "\n  align = center", NAME)
    text = text.replace("source = Craftunity 3 · 26.2 ", STATUS)
    P.write_text(text, encoding="utf-8")

    for line in text.split("\n"):
        if line.strip().startswith("source = "):
            print(line.strip()[:90])


if __name__ == "__main__":
    main()
