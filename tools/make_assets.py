"""Draws openbox's images: the package logos in OpenBox/Assets and the built-in apps' tile icons
in OpenBox/Assets/Apps. Run it after changing a drawing here; the images are checked in.

    python3 tools/make_assets.py
"""
import os
import re

from PIL import Image, ImageDraw

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "OpenBox", "Assets")
BACKGROUND = (17, 17, 20, 255)
WHITE = (255, 255, 255, 255)
SUPERSAMPLE = 4


def canvas(width, height, background=(0, 0, 0, 0)):
    image = Image.new("RGBA", (width * SUPERSAMPLE, height * SUPERSAMPLE), background)
    return image, ImageDraw.Draw(image)


def finish(image, width, height, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    image.resize((width, height), Image.LANCZOS).save(path)


def draw_mark(draw, cx, cy, size, background, color=WHITE):
    """The openbox mark: a box with its lid lifted off."""
    s = size * SUPERSAMPLE
    stroke = max(1, round(s * 0.09))
    left, right = cx * SUPERSAMPLE - s / 2, cx * SUPERSAMPLE + s / 2
    top = cy * SUPERSAMPLE - s * 0.22
    bottom = cy * SUPERSAMPLE + s / 2
    radius = s * 0.16
    # The box: open at the top.
    draw.rounded_rectangle([left, top, right, bottom], radius=radius, outline=color, width=stroke,
                           corners=(False, False, True, True))
    draw.rectangle([left + stroke, top - 1, right - stroke, top + stroke + 1], fill=background)
    # The lid, floating above it.
    lid_top = cy * SUPERSAMPLE - s / 2
    draw.rounded_rectangle([left - s * 0.06, lid_top, right + s * 0.06, lid_top + stroke],
                           radius=stroke / 2, fill=color)


def logo(width, height, mark_size, path, background=BACKGROUND):
    image, draw = canvas(width, height, background)
    draw_mark(draw, width / 2, height / 2, mark_size, background)
    finish(image, width, height, path)


def package_logos():
    for scale in (100, 200, 400):
        f = scale / 100
        logo(round(50 * f), round(50 * f), 26 * f, f"{ROOT}/StoreLogo.scale-{scale}.png")
        logo(round(150 * f), round(150 * f), 60 * f, f"{ROOT}/Square150x150Logo.scale-{scale}.png")
        logo(round(44 * f), round(44 * f), 24 * f, f"{ROOT}/Square44x44Logo.scale-{scale}.png")
        logo(round(310 * f), round(150 * f), 60 * f, f"{ROOT}/Wide310x150Logo.scale-{scale}.png")
        logo(round(620 * f), round(300 * f), 96 * f, f"{ROOT}/SplashScreen.scale-{scale}.png")
    for size in (16, 24, 32, 48, 256):
        logo(size, size, size * 0.6, f"{ROOT}/Square44x44Logo.targetsize-{size}.png")
        logo(size, size, size * 0.6, f"{ROOT}/Square44x44Logo.altform-unplated_targetsize-{size}.png",
             background=(0, 0, 0, 0))


def youtube_icon(path):
    """A white play button: the tile itself is YouTube red."""
    size = 256
    image, draw = canvas(size, size)
    k = SUPERSAMPLE
    w, h = 200 * k, 140 * k
    x, y = (size * k - w) / 2, (size * k - h) / 2
    draw.rounded_rectangle([x, y, x + w, y + h], radius=40 * k, fill=WHITE)
    cx, cy = size * k / 2 + 6 * k, size * k / 2
    draw.polygon([(cx - 26 * k, cy - 32 * k), (cx - 26 * k, cy + 32 * k), (cx + 34 * k, cy)], fill=(255, 0, 51, 255))
    finish(image, size, size, path)


# X's mark, from the SVG on x.com (a 24x24 box): the outline of the wide stroke, then its hole.
X_MARK = ("M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835"
          "L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z")


def path_polygons(d):
    """The polygons of an SVG path made only of straight lines (M, L, H, V and z, either case)."""
    tokens = re.findall(r"[MmLlHhVvZz]|-?\d*\.?\d+", d)
    polygons, points = [], []
    x = y = 0.0
    command = None
    i = 0
    while i < len(tokens):
        if tokens[i].isalpha():
            command = tokens[i]
            i += 1
            if command in "Zz":
                polygons.append(points)
                x, y = points[0]
                points = []
                continue
        relative = command.islower()
        kind = command.upper()
        if kind in "ML":
            dx, dy = float(tokens[i]), float(tokens[i + 1])
            i += 2
            x, y = (x + dx, y + dy) if relative else (dx, dy)
            # After a move, more pairs are lines.
            command = "l" if relative else "L"
        elif kind == "H":
            x = x + float(tokens[i]) if relative else float(tokens[i])
            i += 1
        elif kind == "V":
            y = y + float(tokens[i]) if relative else float(tokens[i])
            i += 1
        points.append((x, y))
    return polygons


def x_icon(path):
    """X's mark in white: the tile itself is black."""
    size = 256
    image, draw = canvas(size, size)
    k = SUPERSAMPLE
    scale = 144 / 24 * k
    offset = (size * k - 24 * scale) / 2
    outline, hole = path_polygons(X_MARK)
    for points, fill in ((outline, WHITE), (hole, (0, 0, 0, 0))):
        draw.polygon([(offset + px * scale, offset + py * scale) for px, py in points], fill=fill)
    finish(image, size, size, path)


def store_icon(path):
    """A shopping bag."""
    size = 256
    image, draw = canvas(size, size)
    k = SUPERSAMPLE
    stroke = 16 * k
    body = [56 * k, 92 * k, 200 * k, 212 * k]
    draw.rounded_rectangle(body, radius=22 * k, outline=WHITE, width=stroke)
    # The handle: the top half of a ring above the bag.
    draw.arc([92 * k, 44 * k, 164 * k, 132 * k], start=180, end=360, fill=WHITE, width=stroke)
    finish(image, size, size, path)


if __name__ == "__main__":
    package_logos()
    youtube_icon(f"{ROOT}/Apps/youtube.png")
    x_icon(f"{ROOT}/Apps/x.png")
    store_icon(f"{ROOT}/Apps/store.png")
    logo(256, 256, 150, f"{ROOT}/Apps/openbox.png", background=(0, 0, 0, 0))
