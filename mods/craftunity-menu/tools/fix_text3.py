from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"


def main() -> None:
    text = P.read_text(encoding="utf-8")
    text = re.sub(r"^  source = Ник: .*$", '  source = {"placeholder":"playername"}', text, flags=re.MULTILINE)
    text = re.sub(r"^  source = +", "  source = ", text, flags=re.MULTILINE)
    P.write_text(text, encoding="utf-8")
    for line in text.split("\n"):
        if line.strip().startswith("source = "):
            print(repr(line.strip()[:80]))


if __name__ == "__main__":
    main()
