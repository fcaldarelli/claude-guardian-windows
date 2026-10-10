#!/usr/bin/env python3
"""Generates the app icon: AppIcon.png (1024 px) and AppIcon.ico (16 … 256 px).

A terminal (Claude Code) with the prompt at the top and three sessions below, each with
its own status dot: working (terracotta), waiting for you (amber), idle (grey).
Warm ivory/terracotta palette. Does not use the Claude logo, which is an Anthropic trademark.

Unlike the macOS version there is no ivory "squircle" background: Windows icons
have no common base shape, and without a background the terminal window can be
larger, so it stays readable even at 16 px (File Explorer, taskbar).

Requirements: Python 3 with Pillow (pip install pillow).
Usage: python3 make-icon.py   (writes the files into the same folder)
"""
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

OUT = Path(__file__).resolve().parent
SS = 4                      # supersampling for smooth edges
SIZE = 1024 * SS

# Palette
CARD = (43, 38, 34)            # warm charcoal
CARD_TOP = (58, 51, 45)
PROMPT = (217, 119, 87)        # terracotta
CURSOR = (240, 232, 220)
BAR = (88, 80, 72)
WORKING = (217, 119, 87)       # terracotta
WAITING = (242, 181, 68)       # amber
IDLE = (138, 129, 120)         # warm grey


def s(v):
    return v * SS


def vertical_gradient(size, top, bottom):
    grad = Image.new("RGB", (1, 256))
    for y in range(256):
        f = y / 255
        grad.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * f) for i in range(3)))
    return grad.resize(size, Image.BICUBIC)


def soft_shadow(mask, dy, blur, opacity, color=(40, 28, 18)):
    layer = Image.new("RGBA", mask.size, color + (0,))
    layer.putalpha(mask.point(lambda v: int(v * opacity)))
    return ImageChops.offset(layer, 0, dy).filter(ImageFilter.GaussianBlur(blur))


def build():
    size = (SIZE, SIZE)
    c = SIZE / 2
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))

    # Terminal window
    x0, y0, x1, y1 = c - s(440), c - s(400), c + s(440), c + s(404)
    radius = s(110)
    card_mask = Image.new("L", size, 0)
    ImageDraw.Draw(card_mask).rounded_rectangle((x0, y0, x1, y1), radius=radius, fill=255)
    canvas.alpha_composite(soft_shadow(card_mask, s(14), s(20), 0.35))

    card = vertical_gradient(size, CARD_TOP, CARD).convert("RGBA")
    card.putalpha(card_mask)
    canvas.alpha_composite(card)

    d = ImageDraw.Draw(canvas)
    K = (x1 - x0) / s(632)   # content drawn for the 632 px window of the macOS version

    # Prompt: terracotta chevron + cursor
    arm = s(54 * K)
    width = round(s(32 * K))
    px, py = x0 + s(70 * K) + arm + width / 2, y0 + s(140 * K)   # chevron tip on the right
    d.line([(px - arm, py - arm), (px, py), (px - arm, py + arm)],
           fill=PROMPT + (255,), width=width, joint="curve")
    for (ex, ey) in [(px - arm, py - arm), (px - arm, py + arm), (px, py)]:
        d.ellipse((ex - width / 2, ey - width / 2, ex + width / 2, ey + width / 2), fill=PROMPT + (255,))
    d.rounded_rectangle((px + s(56 * K), py + arm - s(14 * K), px + s(176 * K), py + arm + s(16 * K)), radius=s(10 * K), fill=CURSOR + (255,))

    # Three sessions: status dot + stylized text line
    rows = [
        (y0 + s(300 * K), WORKING, 0.92),
        (y0 + s(400 * K), WAITING, 0.70),
        (y0 + s(500 * K), IDLE, 0.52),
    ]
    dot_r = s(30 * K)
    dot_x = x0 + s(70 * K) + dot_r
    bar_x0 = dot_x + s(66 * K)
    bar_max = (x1 - s(70 * K)) - bar_x0
    for (ry, color, frac) in rows:
        d.ellipse((dot_x - dot_r, ry - dot_r, dot_x + dot_r, ry + dot_r), fill=color + (255,))
        d.rounded_rectangle((bar_x0, ry - s(15 * K), bar_x0 + bar_max * frac, ry + s(15 * K)),
                            radius=s(15 * K), fill=BAR + (255,))

    # Subtle glow on the "working" dot, to suggest activity
    glow = Image.new("L", size, 0)
    gy = rows[0][0]
    ImageDraw.Draw(glow).ellipse((dot_x - s(54 * K), gy - s(54 * K), dot_x + s(54 * K), gy + s(54 * K)), fill=90)
    glow = glow.filter(ImageFilter.GaussianBlur(s(18 * K)))
    halo = Image.new("RGBA", size, WORKING + (0,))
    halo.putalpha(ImageChops.multiply(glow, card_mask))
    under = canvas.copy()
    canvas = Image.alpha_composite(under, halo)
    d = ImageDraw.Draw(canvas)
    d.ellipse((dot_x - dot_r, gy - dot_r, dot_x + dot_r, gy + dot_r), fill=WORKING + (255,))

    return canvas.resize((1024, 1024), Image.LANCZOS)


if __name__ == "__main__":
    icon = build()
    icon.save(OUT / "AppIcon.png")
    # All the sizes used by Windows, from the notification area (16 px) to File Explorer (256 px).
    icon.save(OUT / "AppIcon.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
    print(f"✓ {OUT / 'AppIcon.png'}\n✓ {OUT / 'AppIcon.ico'}")
