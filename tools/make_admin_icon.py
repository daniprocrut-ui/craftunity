from __future__ import annotations

import struct
import zlib
from pathlib import Path

SIZE = 256


def blend(a: tuple, b: tuple, t: float) -> tuple:
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def inside_rounded(x: int, y: int, radius: int) -> bool:
    if radius <= x < SIZE - radius and 0 <= y < SIZE:
        return True
    if radius <= y < SIZE - radius and 0 <= x < SIZE:
        return True
    for cx, cy in (
        (radius, radius),
        (SIZE - radius - 1, radius),
        (radius, SIZE - radius - 1),
        (SIZE - radius - 1, SIZE - radius - 1),
    ):
        if (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2:
            return True
    return False


def point_in_polygon(px: float, py: float, polygon: list[tuple]) -> bool:
    inside = False
    count = len(polygon)
    for index in range(count):
        x1, y1 = polygon[index]
        x2, y2 = polygon[(index + 1) % count]
        if (y1 > py) != (y2 > py):
            x_cross = (x2 - x1) * (py - y1) / (y2 - y1) + x1
            if px < x_cross:
                inside = not inside
    return inside


LEFT_LEG = [(122, 56), (142, 56), (98, 200), (72, 200)]
RIGHT_LEG = [(114, 56), (134, 56), (184, 200), (158, 200)]
CROSSBAR = [(88, 136), (168, 136), (168, 160), (88, 160)]


def build_pixels() -> list[list[tuple]]:
    top = (251, 191, 36)
    bottom = (249, 115, 22)
    rows = []
    for y in range(SIZE):
        row = []
        for x in range(SIZE):
            if not inside_rounded(x, y, 52):
                row.append((0, 0, 0, 0))
                continue
            t = (x / SIZE) * 0.4 + (y / SIZE) * 0.6
            color = blend(top, bottom, t)
            if 36 <= x < 84 and 36 <= y < 84:
                color = blend(color, (255, 255, 255), 0.28)
            dark = (32, 22, 7)
            if (
                point_in_polygon(x, y, LEFT_LEG)
                or point_in_polygon(x, y, RIGHT_LEG)
                or point_in_polygon(x, y, CROSSBAR)
            ):
                color = dark
            row.append((color[0], color[1], color[2], 255))
        rows.append(row)
    return rows


def write_ico(path: Path, rows: list[list[tuple]]) -> None:
    pixels = bytearray()
    for y in range(SIZE - 1, -1, -1):
        for x in range(SIZE):
            r, g, b, a = rows[y][x]
            pixels += bytes((b, g, r, a))
    mask = bytes(SIZE * SIZE // 8)
    header = struct.pack("<IiiHHIIiiII", 40, SIZE, SIZE * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    image = header + bytes(pixels) + mask
    directory = struct.pack("<HHH", 0, 1, 1)
    entry = struct.pack("<BBBBHHII", 0, 0, 0, 0, 1, 32, len(image), 6 + 16)
    path.write_bytes(directory + entry + image)


def write_png(path: Path, rows: list[list[tuple]]) -> None:
    raw = bytearray()
    for row in rows:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )
    path.write_bytes(png)


if __name__ == "__main__":
    rows = build_pixels()
    root = Path(__file__).resolve().parent.parent
    write_ico(root / "icon_admin.ico", rows)
    write_png(root / "icon_admin.png", rows)
    print("icon_admin.ico, icon_admin.png")
