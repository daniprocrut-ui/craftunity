from __future__ import annotations

import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from craftunity import manifest  # noqa: E402
from craftunity.admin_server import ManifestServer  # noqa: E402


def main() -> int:
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
    data = manifest.manifest_for_admin()
    manifest.save_admin_manifest(data)
    server = ManifestServer("127.0.0.1", port)
    url = server.start()
    print(url, flush=True)
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        server.stop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
