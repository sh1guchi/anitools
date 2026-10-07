"""Иконка anitools (src/Anitools.App/Assets/anitools.ico): как шапка окна — пурпурная полоса «▌» и «A» на тёмном.

Запуск: python3 tools/make_icon.py [путь к Inter Bold]. Нужен Pillow.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "Anitools.App" / "Assets" / "anitools.ico"
FONT = sys.argv[1] if len(sys.argv) > 1 else "/usr/share/fonts/opentype/inter/Inter-Bold.otf"
BACKGROUND = (21, 18, 27, 255)  # RegionColor тёмной темы
ACCENT = (168, 85, 247, 255)  # #A855F7
SIZE = 256


def draw() -> Image.Image:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    d.rounded_rectangle((8, 8, SIZE - 8, SIZE - 8), radius=48, fill=BACKGROUND)
    d.rounded_rectangle((44, 52, 72, SIZE - 52), radius=8, fill=ACCENT)
    font = ImageFont.truetype(FONT, 164)
    left, top, right, bottom = d.textbbox((0, 0), "A", font=font)
    x = 88 + (SIZE - 88 - 36 - (right - left)) / 2 - left
    y = (SIZE - (bottom - top)) / 2 - top
    d.text((x, y), "A", font=font, fill=(255, 255, 255, 255))
    return image


def main() -> None:
    OUT.parent.mkdir(parents=True, exist_ok=True)
    draw().save(OUT, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"Записано {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
