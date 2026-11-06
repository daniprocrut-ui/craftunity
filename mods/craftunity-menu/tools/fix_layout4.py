from __future__ import annotations

import re
import uuid
from pathlib import Path

P = Path(__file__).resolve().parents[1] / "src" / "main" / "resources" / "craftunitymenu" / "customization" / "craftunity_title_screen.txt"

STAMP = "1793724000000"
S = f"-{STAMP}"


def uid(n: int) -> str:
    return str(uuid.uuid5(uuid.NAMESPACE_URL, f"craftunity/preview/{n}"))


def set_field(block: str, key: str, value: str) -> str:
    return re.sub(rf"^(\s*{key} = ).*$", rf"\g<1>{value}", block, count=1, flags=re.MULTILINE)


def common_tail(n: int) -> str:
    return (
        f"  appearance_delay = no_delay\n"
        f"  appearance_delay_seconds = 1.0\n"
        f"  fade_in_v2 = no_fading\n"
        f"  fade_in_speed = 1.0\n"
        f"  fade_out = no_fading\n"
        f"  fade_out_speed = 1.0\n"
        f"  base_opacity = 1.0\n"
        f"  auto_sizing = true\n"
        f"  auto_sizing_base_screen_width = 1920\n"
        f"  auto_sizing_base_screen_height = 1080\n"
        f"  sticky_anchor = false\n"
        f"  stretch_x = false\n"
        f"  stretch_y = false\n"
        f"  stay_on_screen = true\n"
        f"  element_loading_requirement_container_identifier = {uid(n)}-{STAMP}\n"
        f"  [loading_requirement_container_meta:{uid(n)}-{STAMP}] = [groups:][instances:]\n"
        f"  enable_parallax = false\n"
        f"  parallax_intensity_v2 = 0.5\n"
        f"  invert_parallax = false\n"
        f"  animated_offset_x = 0\n"
        f"  animated_offset_y = 0\n"
        f"  load_once_per_session = false\n"
        f"  in_editor_color = #FFC800FF\n"
        f"  layer_hidden_in_editor = false\n"
    )


def gen(n: int) -> str:
    """Новая раскладка: слева скин + ник, справа колонка кнопок и настроек."""
    parts = []

    # 1. Логотип
    parts.append(
        "element {\n"
        "  source = [source:local]/config/fancymenu/assets/craftunity/logo.png\n"
        "  repeat_texture = false\n"
        "  nine_slice_texture = false\n"
        "  nine_slice_texture_border_x = 5\n"
        "  nine_slice_texture_border_y = 5\n"
        "  image_tint = #FFFFFF\n"
        "  restart_animated_on_menu_load = false\n"
        "  element_type = image\n"
        f"  instance_identifier = {uid(1)}-{STAMP}\n"
        + common_tail(2) +
        "  anchor_point = top-left\n"
        "  x = 152\n"
        "  y = 132\n"
        "  width = 320\n"
        "  height = 48\n"
        "}\n"
    )

    # 2. Модель игрока
    parts.append(
        "element {\n"
        "  copy_client_player = true\n"
        "  playername = \n"
        "  auto_skin = true\n"
        "  auto_cape = true\n"
        "  slim = false\n"
        "  skin_source = \n"
        "  cape_source = \n"
        "  parrot = false\n"
        "  is_baby = false\n"
        "  player_pose = standing\n"
        "  body_movement = true\n"
        "  showname = false\n"
        "  follow_mouse = false\n"
        "  head_follows_mouse = true\n"
        "  body_follows_mouse = false\n"
        "  head_x_rot_advanced_mode = false\n"
        "  head_y_rot_advanced_mode = false\n"
        "  head_z_rot_advanced_mode = false\n"
        "  body_x_rot_advanced_mode = false\n"
        "  body_y_rot_advanced_mode = false\n"
        "  body_z_rot_advanced_mode = false\n"
        "  left_arm_x_rot_advanced_mode = false\n"
        "  left_arm_y_rot_advanced_mode = false\n"
        "  left_arm_z_rot_advanced_mode = false\n"
        "  right_arm_x_rot_advanced_mode = false\n"
        "  right_arm_y_rot_advanced_mode = false\n"
        "  right_arm_z_rot_advanced_mode = false\n"
        "  left_leg_x_rot_advanced_mode = false\n"
        "  left_leg_y_rot_advanced_mode = false\n"
        "  left_leg_z_rot_advanced_mode = false\n"
        "  right_leg_x_rot_advanced_mode = false\n"
        "  right_leg_y_rot_advanced_mode = false\n"
        "  right_leg_z_rot_advanced_mode = false\n"
        "  element_type = player_entity_v2\n"
        f"  instance_identifier = {uid(3)}-{STAMP}\n"
        + common_tail(4) +
        "  anchor_point = mid-centered\n"
        "  x = -300\n"
        "  y = -60\n"
        "  width = 140\n"
        "  height = 280\n"
        "}\n"
    )

    # 3. Ник под скином
    parts.append(
        "element {\n"
        "  source_mode = local\n"
        '  source = {"placeholder":"playername"}\n'
        "  interactable = false\n"
        "  shadow = false\n"
        "  case_mode = none\n"
        "  enable_scrolling = false\n"
        "  auto_line_wrapping = false\n"
        "  remove_html_breaks = false\n"
        "  parse_markdown = false\n"
        "  element_type = text_v2\n"
        f"  instance_identifier = {uid(5)}-{STAMP}\n"
        + common_tail(6) +
        "  anchor_point = mid-centered\n"
        "  x = -300\n"
        "  y = 92\n"
        "  width = 220\n"
        "  height = 22\n"
        "}\n"
    )

    # 4. Подпись статуса под ником
    parts.append(
        "element {\n"
        "  source_mode = local\n"
        "  source = Craftunity 3 · 26.2\n"
        "  interactable = false\n"
        "  shadow = false\n"
        "  case_mode = none\n"
        "  enable_scrolling = false\n"
        "  auto_line_wrapping = false\n"
        "  remove_html_breaks = false\n"
        "  parse_markdown = false\n"
        "  element_type = text_v2\n"
        f"  instance_identifier = {uid(7)}-{STAMP}\n"
        + common_tail(8) +
        "  anchor_point = mid-centered\n"
        "  x = -300\n"
        "  y = 118\n"
        "  width = 220\n"
        "  height = 18\n"
        "}\n"
    )
    return "".join(parts)


def main() -> None:
    text = P.read_text(encoding="utf-8")
    head = text.split("element {\n", 1)[0]
    body = gen(0)

    # Кнопки: колонка справа от центра, компактные.
    buttons = []
    labels = [
        ("singleplayer", "-160", "btn_focus", "btn_focus_hover", "single"),
        ("multiplayer", "-108", "btn_normal", "btn_hover", "second"),
        ("options", "-56", "btn_normal", "btn_hover", "third"),
        ("realms", "-4", "btn_normal", "btn_hover", "fourth"),
        ("quit", "48", "btn_normal", "btn_hover", "fifth"),
    ]
    for i, (name, y, normal, hover, _) in enumerate(labels, start=10):
        buttons.append(
            "vanilla_button {\n"
            f"  button_element_executable_block_identifier = {uid(i)}-{STAMP}\n"
            f"  [executable_block:{uid(i)}-{STAMP}][type:generic] = [executables:]\n"
            f"  backgroundnormal = [source:local]/config/fancymenu/assets/craftunity/{normal}.png\n"
            f"  backgroundhovered = [source:local]/config/fancymenu/assets/craftunity/{hover}.png\n"
            "  restartbackgroundanimations = true\n"
            "  nine_slice_custom_background = false\n"
            "  nine_slice_border_x = 5\n"
            "  nine_slice_border_y = 5\n"
            "  navigatable = true\n"
            f"  widget_active_state_requirement_container_identifier = {uid(i + 1)}-{STAMP}\n"
            f"  [loading_requirement_container_meta:{uid(i + 1)}-{STAMP}] = [groups:][instances:]\n"
            "  is_template = false\n"
            "  template_apply_width = false\n"
            "  template_apply_height = false\n"
            "  template_apply_posx = false\n"
            "  template_apply_posy = false\n"
            "  template_apply_opacity = false\n"
            "  template_apply_visibility = false\n"
            "  template_apply_label = false\n"
            "  template_share_with = buttons\n"
            "  nine_slice_slider_handle = false\n"
            "  nine_slice_slider_handle_border_x = 5\n"
            "  nine_slice_slider_handle_border_y = 5\n"
            "  element_type = vanilla_button\n"
            f"  instance_identifier = mc_titlescreen_{name}_button\n"
            + common_tail(i + 2) +
            "  anchor_point = mid-centered\n"
            "  x = 150\n"
            f"  y = {y}\n"
            "  width = 300\n"
            "  height = 40\n"
            "  is_hidden = false\n"
            "  automated_button_clicks = 0\n"
            "}\n"
        )

    # Блок настроек: язык и профиль — текстовая подсказка под кнопками.
    buttons.append(
        "element {\n"
        "  source_mode = local\n"
        "  source = Настройки и профиль — в игре\n"
        "  interactable = false\n"
        "  shadow = false\n"
        "  case_mode = none\n"
        "  enable_scrolling = false\n"
        "  auto_line_wrapping = true\n"
        "  remove_html_breaks = false\n"
        "  parse_markdown = false\n"
        "  element_type = text_v2\n"
        f"  instance_identifier = {uid(40)}-{STAMP}\n"
        + common_tail(41) +
        "  anchor_point = mid-centered\n"
        "  x = 150\n"
        "  y = 110\n"
        "  width = 300\n"
        "  height = 40\n"
        "}\n"
    )

    P.write_text(head + body + "".join(buttons), encoding="utf-8")
    print("layout rebuilt")


if __name__ == "__main__":
    main()
