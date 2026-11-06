from __future__ import annotations

from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

INFO = "Сервер Craftunity · версия 26.2\\nЗаходите в игру через Craftunity Launcher — ник и скины синхронизированы.\\nОбновления модпака подтягиваются при запуске."
NEWS = "Новости сервера\\nОбновления модпака подтягиваются при запуске, вход по нику синхронизирован с лаунчером."


def main() -> None:
    text = P.read_text(encoding="utf-8-sig")
    lines = text.split("\n")
    sources = [i for i, line in enumerate(lines) if line.startswith("  source = ") and "logo.png" not in line]
    if len(sources) != 2:
        raise SystemExit(f"ожидалось 2 текстовых source, найдено {len(sources)}")
    lines[sources[0]] = f"  source = {INFO}"
    lines[sources[1]] = f"  source = {NEWS}"
    P.write_text("\n".join(lines), encoding="utf-8")
    print("fixed text sources")


if __name__ == "__main__":
    main()
