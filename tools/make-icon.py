# Draws src/BatchPad.App/BatchPad.ico: a prompt chevron and cursor on a warm tile, rendered separately per size.
# Run with: py tools/make-icon.py [preview.png]
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SCALE = 8
TOP_LEFT = (0xF5, 0x9E, 0x0B)
BOTTOM_RIGHT = (0xDC, 0x26, 0x26)
CHEVRON = (0xFF, 0xFF, 0xFF)
CURSOR = (0x1F, 0x1B, 0x4B)


def render(size):
    big = size * SCALE
    margin = 0 if size <= 24 else round(big * 0.04)
    tile = big - 2 * margin

    gradient = Image.new("RGB", (size, size))
    pixels = gradient.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * size - 2)
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(TOP_LEFT, BOTTOM_RIGHT))
    gradient = gradient.resize((big, big), Image.BILINEAR)
    mask = Image.new("L", (big, big))
    ImageDraw.Draw(mask).rounded_rectangle((margin, margin, margin + tile - 1, margin + tile - 1), radius=round(tile * 0.22), fill=255)
    image = Image.new("RGBA", (big, big))
    image.paste(gradient, mask=mask)

    draw = ImageDraw.Draw(image)
    # Small sizes need strokes of at least ~2 px to stay readable.
    stroke = max(tile * 0.12, 2.1 * SCALE if size <= 24 else 0)

    def at(u, v):
        return margin + u * tile, margin + v * tile

    chevron = [at(0.25, 0.28), at(0.50, 0.50), at(0.25, 0.72)]
    draw.line(chevron, fill=CHEVRON, width=round(stroke), joint="curve")
    for x, y in (chevron[0], chevron[2]):
        draw.ellipse((x - stroke / 2, y - stroke / 2, x + stroke / 2, y + stroke / 2), fill=CHEVRON)

    left, bottom = at(0.56, 0.72)
    right, _ = at(0.78, 0.72)
    draw.rounded_rectangle((left - stroke / 2, bottom - stroke / 2, right + stroke / 2, bottom + stroke / 2), radius=stroke / 2, fill=CURSOR)

    return image.resize((size, size), Image.LANCZOS)


def main():
    repo = Path(__file__).resolve().parent.parent
    images = [render(size) for size in SIZES]
    largest = images[-1]
    largest.save(repo / "src" / "BatchPad.App" / "BatchPad.ico", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    if len(sys.argv) > 1:
        sheet = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES), 256), (240, 240, 240, 255))
        x = 0
        for image in images:
            sheet.paste(image, (x, 0), image)
            x += image.width + 10
        sheet.save(sys.argv[1])


main()
