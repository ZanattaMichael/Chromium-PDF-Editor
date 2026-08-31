#!/usr/bin/env python3
"""Generates the extension's toolbar and store icons (16/48/128 px PNGs).

Run once before loading the unpacked extension:
    python3 scripts/generate-icons.py
Only the Python standard library is required.

The mark is a white page with a folded corner on a red tile, carrying a black
redaction bar -- the one feature that distinguishes this editor from every other
PDF tool. Shapes are defined once in a 128x128 design space and rendered by
supersampling (each output pixel averages ss*ss coverage samples), which is what
gives the curves and the tile's rounded corners their antialiased edges.
"""
import os
import struct
import zlib

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "extension", "icons")

TILE_TOP, TILE_BOT = (0xC9, 0x37, 0x2C), (0x9E, 0x1B, 0x13)
SHEET_TOP, SHEET_BOT = (0xFF, 0xFF, 0xFF), (0xF1, 0xF3, 0xF5)
FOLD = (0xC3, 0xCA, 0xD1)
LINE = (0xAE, 0xB6, 0xBE)
BAR = (0x11, 0x13, 0x17)
SHADOW = (0x3A, 0x07, 0x03)

# size -> (supersample factor, draw the page detail). At 16px the fold and the
# third line turn to mush, so that size gets a larger page and two fat bars.
SIZES = ((128, 4, True), (48, 8, True), (16, 16, False))


def lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def rounded_rect(x, y, w, h, r):
    """Returns a point-in-rounded-rectangle test in design space."""
    def inside(px, py):
        if not (x <= px <= x + w and y <= py <= y + h):
            return False
        dx, dy = min(px - x, x + w - px), min(py - y, y + h - py)
        if dx < r and dy < r:
            return (dx - r) ** 2 + (dy - r) ** 2 <= r * r
        return True
    return inside


def box_blur(mask, w, radius, passes=3):
    """Separable box blur; repeated passes approximate a gaussian well enough
    for the page's drop shadow."""
    buf = mask
    for _ in range(passes):
        mid = [0.0] * (w * w)
        for y in range(w):
            row = y * w
            for x in range(w):
                lo, hi = max(0, x - radius), min(w - 1, x + radius)
                mid[row + x] = sum(buf[row + i] for i in range(lo, hi + 1)) / (hi - lo + 1)
        out = [0.0] * (w * w)
        for x in range(w):
            for y in range(w):
                lo, hi = max(0, y - radius), min(w - 1, y + radius)
                out[y * w + x] = sum(mid[i * w + x] for i in range(lo, hi + 1)) / (hi - lo + 1)
        buf = out
    return buf


def shapes(detailed):
    tile = rounded_rect(2, 2, 124, 124, 28)
    if detailed:
        body = rounded_rect(28, 24, 72, 82, 8)
        # The dog-ear: clip the top-right corner along x - y = 56, then paint the
        # folded flap as the triangle just inside that cut.
        sheet = lambda px, py: body(px, py) and (px - py) <= 56
        fold = lambda px, py: px >= 80 and py <= 44 and (px - py) <= 56
        bars = [(rounded_rect(40, 52, 38, 7, 3.5), LINE),
                (rounded_rect(40, 82, 30, 7, 3.5), LINE),
                (rounded_rect(40, 65, 48, 12, 2), BAR)]
    else:
        sheet = rounded_rect(20, 16, 88, 96, 9)
        fold = lambda px, py: False
        bars = [(rounded_rect(34, 38, 60, 14, 2), LINE),
                (rounded_rect(34, 60, 60, 22, 2), BAR)]
    return tile, sheet, fold, bars


def render(size, ss, detailed):
    """Returns PNG-ready RGBA rows (each prefixed with a zero filter byte)."""
    w = size * ss
    k = size / 128.0                              # design units -> output pixels
    tile, sheet, fold, bars = shapes(detailed)

    sheet_mask = [0.0] * (w * w)
    for py in range(w):
        y = (py + 0.5) / (ss * k)
        for px in range(w):
            if sheet((px + 0.5) / (ss * k), y):
                sheet_mask[py * w + px] = 1.0
    shadow = box_blur(sheet_mask, w, max(1, int(2.0 * ss * k)))
    drop = int(round(2.5 * ss * k))

    acc = [[0.0, 0.0, 0.0, 0.0] for _ in range(size * size)]
    for py in range(w):
        y = (py + 0.5) / (ss * k)
        for px in range(w):
            x = (px + 0.5) / (ss * k)
            r = g = b = a = 0.0

            def over(col, alpha):                 # source-over compositing
                nonlocal r, g, b, a
                if alpha <= 0:
                    return
                r = col[0] * alpha + r * (1 - alpha)
                g = col[1] * alpha + g * (1 - alpha)
                b = col[2] * alpha + b * (1 - alpha)
                a = alpha + a * (1 - alpha)

            if tile(x, y):
                over(lerp(TILE_TOP, TILE_BOT, min(1.0, max(0.0, (y - 2) / 124))), 1.0)
                sy = py - drop
                if 0 <= sy < w:                   # shadow stays inside the tile
                    over(SHADOW, shadow[sy * w + px] * 0.42)
            if sheet(x, y):
                over(lerp(SHEET_TOP, SHEET_BOT, min(1.0, max(0.0, (y - 24) / 82))), 1.0)
                if fold(x, y):
                    over(FOLD, 1.0)
                for hits, col in bars:
                    if hits(x, y):
                        over(col, 1.0)

            cell = acc[(py // ss) * size + (px // ss)]
            cell[0] += r * a
            cell[1] += g * a
            cell[2] += b * a
            cell[3] += a

    n = float(ss * ss)
    rows = bytearray()
    for i, cell in enumerate(acc):
        if i % size == 0:
            rows.append(0)                        # PNG row filter byte
        alpha = cell[3] / n
        if alpha <= 0.0001:
            rows += b"\x00\x00\x00\x00"
            continue
        rows += bytes((int(round(min(255, cell[0] / cell[3]))),
                       int(round(min(255, cell[1] / cell[3]))),
                       int(round(min(255, cell[2] / cell[3]))),
                       int(round(min(255, alpha * 255)))))
    return bytes(rows)


def write_png(path, size, rows):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + \
            struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + \
        chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    for size, ss, detailed in SIZES:
        target = os.path.join(OUT_DIR, f"icon{size}.png")
        write_png(target, size, render(size, ss, detailed))
        print(f"wrote {os.path.relpath(target)}")


if __name__ == "__main__":
    main()
