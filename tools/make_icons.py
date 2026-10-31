from __future__ import annotations

import struct
import zlib
from pathlib import Path

SIZE = 256
SIZES = (256, 128, 64, 48, 32, 16)


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


def draw_launcher() -> list[list[tuple]]:
    top = (110, 231, 168)
    bottom = (124, 92, 255)
    rows = []
    for y in range(SIZE):
        row = []
        for x in range(SIZE):
            if not inside_rounded(x, y, 52):
                row.append((0, 0, 0, 0))
                continue
            t = (x / SIZE) * 0.45 + (y / SIZE) * 0.55
            color = blend(top, bottom, t)
            if 40 <= x < 92 and 40 <= y < 92:
                color = blend(color, (255, 255, 255), 0.35)
            if 92 <= x < 104 and 40 <= y < 92:
                color = blend(color, (10, 14, 20), 0.25)
            if (
                (74 <= x < 182 and 62 <= y < 92)
                or (74 <= x < 104 and 62 <= y < 194)
                or (74 <= x < 182 and 164 <= y < 194)
                or (74 <= x < 168 and 92 <= y < 164 and 104 <= x < 134)
            ):
                color = (8, 12, 18)
            row.append((color[0], color[1], color[2], 255))
        rows.append(row)
    return rows


ADMIN_LEFT_LEG = [(122, 56), (142, 56), (98, 200), (72, 200)]
ADMIN_RIGHT_LEG = [(114, 56), (134, 56), (184, 200), (158, 200)]
ADMIN_CROSSBAR = [(88, 136), (168, 136), (168, 160), (88, 160)]


def draw_admin() -> list[list[tuple]]:
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
            if (
                point_in_polygon(x, y, ADMIN_LEFT_LEG)
                or point_in_polygon(x, y, ADMIN_RIGHT_LEG)
                or point_in_polygon(x, y, ADMIN_CROSSBAR)
            ):
                color = (32, 22, 7)
            row.append((color[0], color[1], color[2], 255))
        rows.append(row)
    return rows


def downscale(rows: list[list[tuple]], target: int) -> list[list[tuple]]:
    if target == SIZE:
        return rows
    factor = SIZE // target
    result = []
    for ty in range(target):
        row = []
        for tx in range(target):
            r = g = b = a = 0
            for dy in range(factor):
                for dx in range(factor):
                    pr, pg, pb, pa = rows[ty * factor + dy][tx * factor + dx]
                    r += pr * pa
                    g += pg * pa
                    b += pb * pa
                    a += pa
            if a:
                row.append((r // a, g // a, b // a, a // (factor * factor)))
            else:
                row.append((0, 0, 0, 0))
        result.append(row)
    return result


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
        + chunk(b"IHDR", struct.pack(">IIBBBBB", len(rows), len(rows), 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )
    path.write_bytes(png)


def icon_image(rows: list[list[tuple]], size: int) -> bytes:
    pixels = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            r, g, b, a = rows[y][x]
            pixels += bytes((b, g, r, a))
    mask_row = ((size + 31) // 32) * 4
    mask = bytes(mask_row * size)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    return header + bytes(pixels) + mask


def write_ico(path: Path, rows: list[list[tuple]]) -> None:
    images = [(size, icon_image(downscale(rows, size), size)) for size in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = bytearray()
    payload = bytearray()
    for size, image in images:
        dimension = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(image), offset)
        payload += image
        offset += len(image)
    path.write_bytes(header + bytes(entries) + bytes(payload))


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    launcher = draw_launcher()
    admin = draw_admin()
    write_ico(root / "icon.ico", launcher)
    write_png(root / "icon.png", downscale(launcher, 256))
    write_ico(root / "icon_admin.ico", admin)
    write_png(root / "icon_admin.png", downscale(admin, 256))
    print("icons:", ", ".join(str(size) for size in SIZES), "px")
