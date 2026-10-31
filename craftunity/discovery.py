from __future__ import annotations

import json
import socket
import time

BEACON_PORT = 30123
SERVICE_NAME = "craftunity-manifest"


def lan_ip() -> str:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.connect(("8.8.8.8", 80))
        return sock.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        sock.close()


def discover(timeout: float = 2.5, port: int = BEACON_PORT) -> str | None:
    """Слушает UDP-маяк админки и возвращает адрес манифеста, если он найден."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind(("", port))
    except OSError:
        sock.close()
        return None
    sock.settimeout(0.4)
    deadline = time.time() + timeout
    try:
        while time.time() < deadline:
            try:
                data, _address = sock.recvfrom(4096)
            except socket.timeout:
                continue
            except OSError:
                return None
            try:
                payload = json.loads(data.decode("utf-8", "replace"))
            except ValueError:
                continue
            if not isinstance(payload, dict) or payload.get("service") != SERVICE_NAME:
                continue
            url = payload.get("url")
            if isinstance(url, str) and url.startswith(("http://", "https://")):
                return url
    finally:
        sock.close()
    return None


class Beacon:
    """Шлёт широковещательный UDP-пакет с адресом манифеста."""

    def __init__(self, url_factory, port: int = BEACON_PORT, interval: float = 3.0) -> None:
        self.url_factory = url_factory
        self.port = port
        self.interval = interval
        self._stop = False
        self._thread = None
        self._sock: socket.socket | None = None

    def start(self) -> None:
        if self._thread:
            return
        self._stop = False
        import threading

        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def _loop(self) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        self._sock = sock
        try:
            while not self._stop:
                try:
                    payload = {
                        "service": SERVICE_NAME,
                        "version": 1,
                        "url": self.url_factory(),
                        "name": "Craftunity",
                    }
                    sock.sendto(json.dumps(payload).encode("utf-8"), ("255.255.255.255", self.port))
                except OSError:
                    pass
                deadline = time.time() + self.interval
                while not self._stop and time.time() < deadline:
                    time.sleep(0.2)
        finally:
            sock.close()
            self._sock = None

    def stop(self) -> None:
        self._stop = True
        if self._thread:
            self._thread.join(timeout=1.5)
            self._thread = None
