"""Draws openbox's images: the package logos in OpenBox/Assets and the built-in apps' tile icons
in OpenBox/Assets/Apps. Run it after changing a drawing here; the images are checked in.

    python3 tools/make_assets.py
"""
import os

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
    store_icon(f"{ROOT}/Apps/store.png")
    logo(256, 256, 150, f"{ROOT}/Apps/openbox.png", background=(0, 0, 0, 0))
