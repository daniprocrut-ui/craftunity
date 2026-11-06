from __future__ import annotations

import os
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

TEST_HOME = Path(tempfile.gettempdir()) / "craftunity-heartbeat-check"
os.environ["CRAFTUNITY_HOME"] = str(TEST_HOME)

from craftunity import config, launch  # noqa: E402

FAILED: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    print(("  OK  " if condition else " FAIL ") + name + (f" — {detail}" if detail and not condition else ""))
    if not condition:
        FAILED.append(name)


def main() -> int:
    if TEST_HOME.exists():
        shutil.rmtree(TEST_HOME, ignore_errors=True)
    TEST_HOME.mkdir(parents=True, exist_ok=True)

    calls: list[float] = []

    def fake_discover(timeout: float = 2.5, port: int = 0) -> str | None:
        calls.append(timeout)
        return "http://192.168.0.108:8765/launcher/manifest.json" if len(calls) < 3 else None

    launch.discovery.discover = fake_discover

    config.save_settings({"manifest_url": "https://daniprocrut-ui.github.io/craftunity/manifest.json", "heartbeat_url": ""})
    engine = launch.Engine()
    check("адрес берётся из маяка", engine._heartbeat_target() == "http://192.168.0.108:8765/heartbeat", engine._heartbeat_target())
    check("маяк спрошен один раз", len(calls) == 1, str(len(calls)))
    engine._heartbeat_target()
    check("результат кэширован", len(calls) == 1, str(len(calls)))

    config.save_settings({"heartbeat_url": "http://192.168.1.10:8765/heartbeat"})
    engine2 = launch.Engine()
    check("настройка важнее маяка", engine2._heartbeat_target() == "http://192.168.1.10:8765/heartbeat")

    config.save_settings({"heartbeat_url": ""})
    launch.discovery.discover = lambda timeout=2.5, port=0: None
    engine3 = launch.Engine()
    check("без маяка github.io не используется", engine3._heartbeat_target() == "", engine3._heartbeat_target())

    sent: list[str] = []

    def fake_post(url: str, payload: dict) -> bool:
        sent.append(url)
        return url.endswith("192.168.0.108:8765/heartbeat")

    config.save_settings({"heartbeat_url": "http://192.168.1.10:8765/heartbeat"})
    launch.discovery.discover = fake_discover
    engine4 = launch.Engine()
    engine4._post_heartbeat = fake_post
    engine4._heartbeat({"version": "1.20.1", "loader": "fabric"})
    check("при ошибке пробует адрес из маяка", sent == ["http://192.168.1.10:8765/heartbeat", "http://192.168.0.108:8765/heartbeat"], str(sent))

    print()
    if FAILED:
        print("Провалено: " + ", ".join(FAILED))
        return 1
    print("Все проверки heartbeat пройдены")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
