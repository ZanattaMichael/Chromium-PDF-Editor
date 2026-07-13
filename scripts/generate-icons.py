#!/usr/bin/env python3
"""Generates the extension's toolbar icons (16/48/128 px PNGs).

Run once before loading the unpacked extension:
    python3 scripts/generate-icons.py
Only the Python standard library is required.
"""
import os
import struct
import zlib

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "extension", "icons")


def make_png(path: str, size: int) -> None:
    """A rounded red document tile with text lines and a black redaction bar."""
    rows = bytearray()
    radius = max(2, size // 8)
    for y in range(size):
        rows.append(0)  # PNG row filter byte
        for x in range(size):
            cx = min(x, size - 1 - x)
            cy = min(y, size - 1 - y)
            in_corner = cx < radius and cy < radius and \
                (cx - radius) ** 2 + (cy - radius) ** 2 > radius * radius
            if in_corner:
                rows += b"\x00\x00\x00\x00"
            elif x > size * 0.62 and y < size * 0.38 and \
                    (x - size * 0.62) > (size * 0.38 - y) * 0.9:
                rows += b"\xff\xe0\xdb\xff"  # page-fold corner
            elif size >= 48 and size * 0.30 < y < size * 0.42 and size * 0.18 < x < size * 0.60:
                rows += b"\xff\xff\xff\xff"  # text line
            elif size >= 48 and size * 0.50 < y < size * 0.62 and size * 0.18 < x < size * 0.70:
                rows += b"\x11\x11\x11\xff"  # redaction bar
            elif size >= 48 and size * 0.70 < y < size * 0.82 and size * 0.18 < x < size * 0.55:
                rows += b"\xff\xff\xff\xff"  # text line
            else:
                rows += b"\xb3\x26\x1e\xff"  # base red

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + \
            struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + \
        chunk(b"IDAT", zlib.compress(bytes(rows))) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    for size in (16, 48, 128):
        target = os.path.join(OUT_DIR, f"icon{size}.png")
        make_png(target, size)
        print(f"wrote {os.path.relpath(target)}")


if __name__ == "__main__":
    main()
