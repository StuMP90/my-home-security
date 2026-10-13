"""Generates the app icon and MSIX/Store logos from code (no binary design source to maintain).

A shield with a camera lens in it. Drawn at 4x and scaled down so the curves stay smooth.

Usage: python3 tools/make_icons.py   (requires Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
BG = (24, 28, 38, 255)
ACCENT = (64, 156, 230, 255)
LENS = (18, 22, 30, 255)
GLINT = (220, 226, 236, 255)
SCALE = 4


def glyph(size: int, pad_ratio: float = 0.0, transparent: bool = False) -> Image.Image:
    if pad_ratio:
        img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        pad = int(size * pad_ratio)
        img.alpha_composite(glyph(size - 2 * pad, transparent=transparent), (pad, pad))
        return img
    big = size * SCALE
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if not transparent:
        d.rounded_rectangle((0, 0, big - 1, big - 1), radius=big // 6, fill=BG)

    def p(x: float, y: float) -> tuple[float, float]:
        return (x * big, y * big)

    # Shield: flat top, straight sides, curving to a point at the bottom.
    shield = [p(0.20, 0.18), p(0.50, 0.10), p(0.80, 0.18), p(0.80, 0.40)]
    steps = 24
    for i in range(1, steps + 1):  # right side curve down to the point
        t = i / steps
        shield.append(p(0.80 - 0.30 * (t ** 1.8), 0.40 + 0.50 * t))
    for i in range(1, steps + 1):  # and back up the left side
        t = i / steps
        shield.append(p(0.50 - 0.30 * (1 - (1 - t) ** 1.8), 0.90 - 0.50 * t))
    d.polygon(shield, fill=ACCENT)

    # Camera lens.
    cx, cy, r = 0.50 * big, 0.46 * big, 0.17 * big
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=LENS)
    r2 = r * 0.55
    d.ellipse((cx - r2, cy - r2, cx + r2, cy + r2), outline=ACCENT, width=max(1, int(r * 0.16)))
    g = r * 0.16
    gx, gy = cx - r * 0.38, cy - r * 0.38
    d.ellipse((gx - g, gy - g, gx + g, gy + g), fill=GLINT)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    assets = ROOT / "src" / "MyHomeSecurity.App" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    glyph(256).save(assets / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    glyph(256).save(assets / "app.png")

    linux = ROOT / "packaging" / "linux"
    linux.mkdir(parents=True, exist_ok=True)
    glyph(256).save(linux / "myhomesecurity.png")

    msix = ROOT / "packaging" / "msix" / "Images"
    msix.mkdir(parents=True, exist_ok=True)
    glyph(50).save(msix / "StoreLogo.png")
    glyph(44).save(msix / "Square44x44Logo.png")
    glyph(44, transparent=True).save(msix / "Square44x44Logo.targetsize-44_altform-unplated.png")
    glyph(150, pad_ratio=0.18).save(msix / "Square150x150Logo.png")
    wide = Image.new("RGBA", (310, 150), BG)
    wide.alpha_composite(glyph(150, pad_ratio=0.18), (80, 0))
    wide.save(msix / "Wide310x150Logo.png")

    # Microsoft Store listing logos (Partner Center: 300x300 required, 150x150 and 71x71).
    store = ROOT / "packaging" / "store"
    store.mkdir(parents=True, exist_ok=True)
    for size in (300, 150, 71):
        glyph(size).save(store / f"StoreLogo-{size}x{size}.png")


if __name__ == "__main__":
    main()
