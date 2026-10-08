"""Иконка anitools (src/Anitools.App/Assets/anitools.ico): как плитка слева сверху в окне — фиолетовый градиент
(#b394ff → #7b4ff2 → #5f37d4, как .brand-mark у Anime Uploader) и белая «A».

Запуск: python3 tools/make_icon.py [путь к Inter Bold]. Нужен Pillow.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "Anitools.App" / "Assets" / "anitools.ico"
FONT = sys.argv[1] if len(sys.argv) > 1 else "/usr/share/fonts/opentype/inter/Inter-Bold.otf"
STOPS = [(0.0, (0xB3, 0x94, 0xFF)), (0.6, (0x7B, 0x4F, 0xF2)), (1.0, (0x5F, 0x37, 0xD4))]
SIZE = 256


def color(t: float) -> tuple[int, int, int, int]:
    for (t0, c0), (t1, c1) in zip(STOPS, STOPS[1:]):
        if t <= t1:
            k = (t - t0) / (t1 - t0)
            return tuple(round(a + (b - a) * k) for a, b in zip(c0, c1)) + (255,)
    return STOPS[-1][1] + (255,)


def draw() -> Image.Image:
    # градиент по диагонали сверху слева вниз направо
    gradient = Image.new("RGBA", (SIZE, SIZE))
    pixels = gradient.load()
    for y in range(SIZE):
        for x in range(SIZE):
            pixels[x, y] = color((x + y) / (2 * (SIZE - 1)))
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle((8, 8, SIZE - 8, SIZE - 8), radius=64, fill=255)
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    image.paste(gradient, (0, 0), mask)
    d = ImageDraw.Draw(image)
    font = ImageFont.truetype(FONT, 150)
    left, top, right, bottom = d.textbbox((0, 0), "A", font=font)
    x = (SIZE - (right - left)) / 2 - left
    y = (SIZE - (bottom - top)) / 2 - top
    d.text((x, y), "A", font=font, fill=(255, 255, 255, 255))
    return image


def main() -> None:
    OUT.parent.mkdir(parents=True, exist_ok=True)
    draw().save(OUT, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"Записано {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
