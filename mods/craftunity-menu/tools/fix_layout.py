from __future__ import annotations

import re
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

YS = ["-170", "-104", "-38", "28", "94"]

NEWS = '''element {
  source_mode = local
  source = Новости сервера\\nОбновления модпака подтягиваются при запуске, вход по нику синхронизирован с лаунчером.
  interactable = false
  shadow = false
  case_mode = none
  enable_scrolling = false
  auto_line_wrapping = true
  remove_html_breaks = false
  parse_markdown = true
  element_type = text_v2
  instance_identifier = 6f2a4e7b-931c-4a53-8d28-bf6e1b5a8815-1793724000000
  appearance_delay = no_delay
  appearance_delay_seconds = 1.0
  fade_in_v2 = no_fading
  fade_in_speed = 1.0
  fade_out = no_fading
  fade_out_speed = 1.0
  base_opacity = 1.0
  auto_sizing = true
  auto_sizing_base_screen_width = 1920
  auto_sizing_base_screen_height = 1080
  sticky_anchor = false
  anchor_point = mid-centered
  x = 130
  y = 190
  width = 420
  height = 90
  stretch_x = false
  stretch_y = false
  stay_on_screen = true
  element_loading_requirement_container_identifier = 7a3b5f8c-a42d-4b64-9e39-c07f2c6b9926-1793724000000
  [loading_requirement_container_meta:7a3b5f8c-a42d-4b64-9e39-c07f2c6b9926-1793724000000] = [groups:][instances:]
  enable_parallax = false
  parallax_intensity_v2 = 0.5
  invert_parallax = false
  animated_offset_x = 0
  animated_offset_y = 0
  load_once_per_session = false
  in_editor_color = #FFC800FF
  layer_hidden_in_editor = false
}'''


def main() -> None:
    s = P.read_text(encoding="utf-8")
    s = s.replace("  x = 150\n  y = 150\n  width = 400\n  height = 111",
                  "  x = 150\n  y = 170\n  width = 300\n  height = 83")
    s = s.replace("  auto_line_wrapping = false\n", "  auto_line_wrapping = true\n")
    s = s.replace("  x = 152\n  y = 290\n  width = 290\n  height = 130",
                  "  x = 152\n  y = 300\n  width = 300\n  height = 150")

    parts = s.split("vanilla_button {\n")
    if len(parts) != 6:
        raise SystemExit(f"неожидаемое число кнопок: {len(parts) - 1}")
    rebuilt = [parts[0]]
    for idx, block in enumerate(parts[1:]):
        block = re.sub(r"  x = 130\n  y = -?\d+\n  width = 320\n  height = 44\n",
                       f"  x = 130\n  y = {YS[idx]}\n  width = 420\n  height = 46\n",
                       block, count=1)
        rebuilt.append(block)
    s = "vanilla_button {\n".join(rebuilt).rstrip()
    s = s + "\n\n" + NEWS + "\n"
    P.write_text(s, encoding="utf-8")
    print("updated", P)


if __name__ == "__main__":
    main()
