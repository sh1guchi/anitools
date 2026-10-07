from __future__ import annotations

from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path
import subprocess
import shutil
import sys
import os


# ================= НАСТРОЙКИ =================
CUT_SECONDS = 1.0
BITRATE = "256k"
OUTDIR = Path("audio_fixed")

# сколько файлов кодировать одновременно
# лучше = числу ФИЗИЧЕСКИХ ядер CPU
WORKERS = 6

EXTS = {
    ".mka", ".m4a", ".aac", ".mp3", ".ac3",
    ".dts", ".flac", ".wav", ".ogg", ".opus"
}
# ============================================


def find_ffmpeg() -> str:
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        print("❌ ffmpeg не найден в PATH")
        sys.exit(1)
    return ffmpeg


def process_file(src: Path, ffmpeg: str) -> tuple[str, bool]:
    dst = OUTDIR / f"{src.stem}.mka"

    if dst.exists():
        return (src.name, True)

    cmd = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-i", str(src),
        "-ss", str(CUT_SECONDS),
        "-c:a", "aac",
        "-b:a", BITRATE,
        "-vn",
        str(dst),
    ]

    p = subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True
    )

    if p.returncode != 0:
        if dst.exists():
            dst.unlink(missing_ok=True)
        return (src.name, False)

    return (src.name, True)


def main() -> None:
    ffmpeg = find_ffmpeg()

    OUTDIR.mkdir(exist_ok=True)

    files = [
        f for f in Path(".").iterdir()
        if f.is_file() and f.suffix.lower() in EXTS
    ]

    if not files:
        print("⚠️ Аудиофайлы не найдены")
        return

    print(f"Найдено файлов : {len(files)}")
    print(f"Потоков       : {WORKERS}")
    print(f"Обрезка       : {CUT_SECONDS} сек")
    print(f"Битрейт       : {BITRATE}")
    print(f"Выход         : ./{OUTDIR}\n")

    ok = fail = 0

    with ProcessPoolExecutor(max_workers=WORKERS) as pool:
        futures = {
            pool.submit(process_file, f, ffmpeg): f
            for f in files
        }

        for fut in as_completed(futures):
            name, success = fut.result()
            if success:
                print(f"✔ {name}")
                ok += 1
            else:
                print(f"✖ {name}")
                fail += 1

    print(f"\nГотово. OK={ok} FAIL={fail}")


if __name__ == "__main__":
    main()
