#!/usr/bin/env python3
"""
Фикстуры реального вывода ffprobe / ffmpeg / mkvmerge для эталонов и тестов.

Генерирует крошечные тестовые файлы (как в docs/PLAN.md §5.3), прогоняет по ним
программы и сохраняет их вывод в tools/golden_inputs/media/ — дальше gen_golden.py
и C#-тесты работают с этими текстами, сами программы им не нужны.

    python3 tools/capture_media_fixtures.py   # нужны ffmpeg, ffprobe, mkvmerge в PATH

Временная папка в выводе заменяется на {root}. Сами медиафайлы не сохраняются.
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tools" / "golden_inputs" / "media"

ASS = """[Script Info]
ScriptType: v4.00+
PlayResX: 160
PlayResY: 90

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1
Style: Signs,Arial,16,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,8,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.50,0:00:02.00,Signs,,0,0,0,,Надпись
"""

SRT = """1
00:00:00,500 --> 00:00:02,000
Hello
"""

LAVFI_VIDEO = ["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=3"]


def sine(freq: int) -> list[str]:
    return ["-f", "lavfi", "-i", f"sine=f={freq}:d=3"]


def surround() -> list[str]:
    return ["-f", "lavfi", "-t", "3", "-i", "anullsrc=channel_layout=5.1:sample_rate=48000"]


def meta(kind: str, i: int, title: str | None, lang: str | None) -> list[str]:
    out = []
    if title is not None:
        out += [f"-metadata:s:{kind}:{i}", f"title={title}"]
    if lang is not None:
        out += [f"-metadata:s:{kind}:{i}", f"language={lang}"]
    return out


# имя файла → аргументы ffmpeg (входы и всё до выходного файла)
def specs(tmp: Path) -> dict[str, list[str]]:
    (tmp / "signs.ass").write_text(ASS, encoding="utf-8")
    (tmp / "full.srt").write_text(SRT, encoding="utf-8")
    ass, srt = str(tmp / "signs.ass"), str(tmp / "full.srt")
    video = ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]
    return {
        # 3 аудио (aac / flac / ac3 5.1) + надписи и полные; первая аудио — default
        "Test Show - 01.mkv": [
            *LAVFI_VIDEO, *sine(440), *sine(660), *surround(), "-i", ass, "-i", srt,
            "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-map", "4", "-map", "5",
            *video, "-c:a:0", "aac", "-c:a:1", "flac", "-c:a:2", "ac3", "-c:s:0", "ass", "-c:s:1", "srt",
            *meta("a", 0, "AniLibria.TV", "rus"), *meta("a", 1, "Оригинальная", "jpn"), *meta("a", 2, "DEEP", "rus"),
            *meta("s", 0, "Надписи", "rus"), *meta("s", 1, "Full", "eng"),
            "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0",
        ],
        # нет третьей озвучки, субтитры в другом порядке, у одной аудио нет тайтла
        "Test Show - 02.mkv": [
            *LAVFI_VIDEO, *sine(440), *sine(660), "-i", srt, "-i", ass,
            "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-map", "4",
            *video, "-c:a", "aac", "-c:s:0", "srt", "-c:s:1", "ass",
            *meta("a", 0, "AniLibria.TV", "rus"), *meta("a", 1, None, "jpn"),
            *meta("s", 0, "Full", "eng"), *meta("s", 1, "Надписи", "rus"),
        ],
        # подчёркивания в имени, MP4 с mov_text
        "Test_Show_-_03.mp4": [
            *LAVFI_VIDEO, *sine(440), "-i", srt,
            "-map", "0", "-map", "1", "-map", "2",
            *video, "-c:a", "aac", "-c:s", "mov_text",
            *meta("a", 0, "AniLibria.TV", "rus"), *meta("s", 0, "Надписи", "rus"),
        ],
        # QuickTime с двумя именованными аудиодорожками (как экспорт DaVinci)
        "Resolve Export.mov": [
            *LAVFI_VIDEO, *sine(440), *sine(660),
            "-map", "0", "-map", "1", "-map", "2",
            "-c:v", "mpeg4", "-c:a", "pcm_s16le",
            *meta("a", 0, "Dialogue RU", None), *meta("a", 1, "Original JP", None),
        ],
        # без звука
        "Silent Show - 01.mkv": [*LAVFI_VIDEO, "-map", "0", *video],
        # многодорожечный .mka без видео
        "Test Show - 01.mka": [
            *sine(440), *sine(660), "-map", "0", "-map", "1", "-c:a", "flac",
            *meta("a", 0, "AniLibria.TV", "rus"), *meta("a", 1, "Оригинальная", "jpn"),
        ],
        # внешняя озвучка в структуре «Только аудио»
        "1. Test Show - 01.AniLibria.TV.mka": [*sine(440), "-map", "0", "-c:a", "aac", *meta("a", 0, "AniLibria.TV", "rus")],
    }


def stable(suffix: str, data):
    """Случайные UID контейнера и дорожек — постоянными, чтобы фикстуры не менялись от запуска к запуску."""
    if suffix == "mkvmerge.json":
        props = data.get("container", {}).get("properties", {})
        if "segment_uid" in props:
            props["segment_uid"] = "0" * 32
        for track in data.get("tracks", []):
            if "uid" in track.get("properties", {}):
                track["properties"]["uid"] = track["id"] + 1
    return data


def run(cmd: list[str], *, check: bool = True) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", check=check)


def main() -> int:
    for tool in ("ffmpeg", "ffprobe", "mkvmerge"):
        if not shutil.which(tool):
            print(f"нет {tool} в PATH")
            return 1
    OUT.mkdir(parents=True, exist_ok=True)
    versions = {
        "ffmpeg": run(["ffmpeg", "-version"]).stdout.splitlines()[0],
        "mkvmerge": run(["mkvmerge", "--version"]).stdout.strip(),
    }
    with tempfile.TemporaryDirectory(prefix="anitools-media-") as td:
        tmp = Path(td)
        for name, args in specs(tmp).items():
            path = tmp / name
            run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args, str(path)])
            root = str(tmp)
            outputs = {
                "ffprobe.json": run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)]).stdout,
                "ffprobe_titles.json": run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries",
                                            "stream=index:stream_tags=title", "-of", "json", str(path)]).stdout,
                "ffmpeg_i.txt": run(["ffmpeg", "-hide_banner", "-i", str(path)], check=False).stderr,
                "mkvmerge.json": run(["mkvmerge", "-J", str(path)], check=False).stdout,
            }
            for suffix, text in outputs.items():
                text = re.sub(r"@ 0x[0-9a-f]+", "@ 0x0", text.replace(root, "{root}"))  # адреса в логах ffmpeg
                if suffix.endswith(".json"):
                    text = json.dumps(stable(suffix, json.loads(text)), ensure_ascii=False, indent=2) + "\n"
                (OUT / f"{name}.{suffix}").write_text(text, encoding="utf-8", newline="\n")
            print(f"  {name}")
    # Прогресс кодирования (-progress pipe:1 -nostats): с битрейтом и размером
    progress = run(["ffmpeg", "-hide_banner", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=3",
                    "-c:v", "libx264", "-preset", "ultrafast", "-progress", "pipe:1", "-nostats", "-f", "matroska", "-y",
                    "/dev/null" if sys.platform != "win32" else "NUL"]).stdout
    (OUT / "ffmpeg_progress.txt").write_text(progress, encoding="utf-8", newline="\n")
    (OUT / "VERSIONS.txt").write_text("\n".join(versions.values()) + "\n", encoding="utf-8", newline="\n")
    print(f"→ {OUT.relative_to(ROOT).as_posix()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
