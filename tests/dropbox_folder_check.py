from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

TEST_HOME = Path(tempfile.gettempdir()) / "craftunity-dropbox-folder-check"
os.environ["CRAFTUNITY_HOME"] = str(TEST_HOME)

from craftunity import config, dropbox, manifest  # noqa: E402

FAILED: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    print(("  OK  " if condition else " FAIL ") + name + (f" — {detail}" if detail and not condition else ""))
    if not condition:
        FAILED.append(name)


def main() -> int:
    if TEST_HOME.exists():
        shutil.rmtree(TEST_HOME, ignore_errors=True)
    TEST_HOME.mkdir(parents=True, exist_ok=True)

    manifest.public_key_path = lambda: config.data_dir() / "admin" / "craftunity_manifest_pub.pem"
    saved = manifest.save_admin_manifest(dict(manifest.DEFAULT_MANIFEST))
    publish_dir = config.data_dir() / "admin" / "publish"

    fake_dropbox = TEST_HOME / "Dropbox"
    fake_dropbox.mkdir(parents=True, exist_ok=True)

    target = dropbox.publish(publish_dir, saved, root=fake_dropbox)
    check("папка сервера найдена", target is not None, str(target))
    if target is None:
        print("Провалено: публикация не выполнена")
        return 1

    check("это папка craftunity", target.name == dropbox.SERVER_FOLDER, str(target))
    for name in ("manifest.json", "manifest.json.sig", "settings.json"):
        check(f"{name} на месте", (target / name).exists())

    body = (target / "manifest.json").read_bytes()
    signature = (target / "manifest.json.sig").read_text(encoding="utf-8")
    verified = manifest.verify_manifest(body, signature)
    check("подпись в папке сервера верна", verified.get("version") == saved.get("version"))

    settings = json.loads((target / "settings.json").read_text(encoding="utf-8"))
    check("настройки повторяют манифест", settings.get("server") == saved.get("server"))
    check("настройки содержат игру", settings.get("game") == saved.get("game"))

    empty = dropbox.publish(publish_dir, saved, root=TEST_HOME / "нет-такого")
    check("без папки Dropbox возвращается None", empty is None)

    print()
    if FAILED:
        print("Провалено: " + ", ".join(FAILED))
        return 1
    print("Публикация в папку сервера Dropbox работает")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
