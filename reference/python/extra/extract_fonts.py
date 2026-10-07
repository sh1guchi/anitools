#!/usr/bin/env python3
"""
Извлекает шрифты из всех видео-контейнеров в текущей папке и пакует в fonts.zip
Зависимости: mkvtoolnix (mkvmerge, mkvextract) — https://mkvtoolnix.download/windows.html
Запуск: python extract_fonts.py
"""

import json
import os
import subprocess
import zipfile
from pathlib import Path

VIDEO_EXTENSIONS = {".mkv", ".mp4", ".webm", ".avi", ".mov", ".matroska"}
FONT_EXTENSIONS  = {".ttf", ".otf", ".woff", ".woff2", ".eot"}
FONT_MIME_PREFIX = "font"
OUTPUT_ZIP       = "fonts.zip"


def get_attachments(video_path: Path) -> list[dict]:
    """Возвращает список вложений через mkvmerge -J."""
    try:
        result = subprocess.run(
            ["mkvmerge", "-J", str(video_path)],
            capture_output=True, text=True, check=True,
            encoding="utf-8", errors="replace"  # fix: Windows cp1251 → utf-8
        )
        data = json.loads(result.stdout)
        return data.get("attachments", [])
    except (subprocess.CalledProcessError, json.JSONDecodeError, FileNotFoundError) as e:
        print(f"  ⚠️  mkvmerge не смог прочитать файл: {e}")
        return []


def is_font(attachment: dict) -> bool:
    mime = attachment.get("content_type", "").lower()
    name = attachment.get("file_name", "").lower()
    return (
        FONT_MIME_PREFIX in mime
        or Path(name).suffix in FONT_EXTENSIONS
    )


def extract_font(video_path: Path, att_id: int, out_path: Path) -> bool:
    """Извлекает одно вложение через mkvextract."""
    try:
        subprocess.run(
            ["mkvextract", str(video_path), "attachments", f"{att_id}:{out_path}"],
            capture_output=True, check=True
        )
        return True
    except subprocess.CalledProcessError as e:
        print(f"  ❌ Ошибка извлечения (id={att_id}): {e.stderr.strip()}")
        return False


def main():
    cwd = Path.cwd()
    videos = [f for f in cwd.iterdir() if f.suffix.lower() in VIDEO_EXTENSIONS]

    if not videos:
        print("❌ Видеофайлы не найдены в текущей папке.")
        input("\nНажми Enter для выхода...")
        return

    fonts: dict[str, bytes] = {}  # имя файла → содержимое (дедупликация)

    for video in sorted(videos):
        print(f"\n📦 {video.name}")
        attachments = get_attachments(video)
        font_attachments = [a for a in attachments if is_font(a)]

        if not font_attachments:
            print("  — шрифтов нет")
            continue

        for att in font_attachments:
            name = att["file_name"]
            att_id = att["id"]

            if name in fonts:
                print(f"  ⏭️  уже есть: {name}")
                continue

            tmp_path = cwd / f"__tmp_font_{att_id}__"
            if extract_font(video, att_id, tmp_path):
                fonts[name] = tmp_path.read_bytes()
                tmp_path.unlink()
                print(f"  ✅ {name}")
            elif tmp_path.exists():
                tmp_path.unlink()

    if not fonts:
        print("\n❌ Шрифты не найдены ни в одном видео.")
        input("\nНажми Enter для выхода...")
        return

    zip_path = cwd / OUTPUT_ZIP
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, data in fonts.items():
            zf.writestr(name, data)

    print(f"\n✅ Упаковано {len(fonts)} шрифтов → {OUTPUT_ZIP}")
    input("\nНажми Enter для выхода...")


if __name__ == "__main__":
    main()
