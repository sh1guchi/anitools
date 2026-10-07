from __future__ import annotations

from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path
import subprocess
import shutil
import sys


# ================= НАСТРОЙКИ =================
BITRATE = "256k"                  # битрейт выходного аудио AAC
OUTDIR = Path("converted")        # папка для готовых файлов

# в какой формат перекодировать: "m4a" (AAC), "mp3", "flac", "wav", "opus", "ogg"
OUT_FORMAT = "mka"

# сколько каналов на выходе (2 = стерео; 5.1/7.1 будет сведено в стерео)
# None — оставить как в исходнике
CHANNELS = 2

# чинить битые таймстампы исходника (Non-monotonic DTS): наложения обрезаются,
# дыры заполняются тишиной, синхрон с видео сохраняется
FIX_TIMESTAMPS = True

# сколько файлов кодировать одновременно
WORKERS = 8

# какие аудио-файлы брать (расширенная поддержка форматов)
EXTS = {
    ".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma",
    ".ogg", ".oga", ".opus", ".ape", ".alac", ".aiff",
    ".aif", ".aifc", ".ac3", ".dts", ".amr", ".mka",
    ".wv", ".tta", ".mpc", ".spx", ".caf", ".dsf", ".dff",
}
# ============================================


# карта: формат вывода -> (расширение, аргументы кодека для ffmpeg)
CODEC_MAP: dict[str, tuple[str, list[str]]] = {
    "m4a":  (".m4a",  ["-c:a", "aac", "-profile:a", "aac_low", "-b:a", BITRATE]),
    "aac":  (".m4a",  ["-c:a", "aac", "-profile:a", "aac_low", "-b:a", BITRATE]),
    "mka":  (".mka",  ["-c:a", "aac", "-profile:a", "aac_low", "-b:a", BITRATE]),
    "mp3":  (".mp3",  ["-c:a", "libmp3lame", "-b:a", BITRATE]),
    "opus": (".opus", ["-c:a", "libopus", "-b:a", BITRATE]),
    "ogg":  (".ogg",  ["-c:a", "libvorbis", "-b:a", BITRATE]),
    "flac": (".flac", ["-c:a", "flac"]),                       # без потерь
    "wav":  (".wav",  ["-c:a", "pcm_s16le"]),                  # без потерь
}


def find_ffmpeg() -> str:
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        print("❌ ffmpeg не найден в PATH")
        sys.exit(1)
    return ffmpeg


def process_file(src: Path, ffmpeg: str, ext: str, codec_args: list[str]) -> tuple[str, bool, str]:
    """
    Перекодируем аудио-файл в выбранный формат.
    Метаданные (теги) и обложку по возможности переносим.
    Возвращаем (имя, успех, причина_ошибки).
    """
    dst = OUTDIR / f"{src.stem}{ext}"

    # не перезаписываем уже готовый файл
    if dst.exists():
        return (src.name, True, "")

    cmd = [
        ffmpeg,
        "-hide_banner",
        "-nostdin",
        "-i", str(src),

        # только аудио-дорожка (без видео-потоков, кроме обложки ниже)
        "-map", "0:a",

        # выбранный кодек и его параметры
        *codec_args,
    ]

    # выравниваем таймстампы по реальному числу сэмплов
    if FIX_TIMESTAMPS:
        cmd += ["-af", "aresample=async=1"]

    # сводим в нужное число каналов (например 5.1 -> стерео)
    if CHANNELS:
        cmd += ["-ac", str(CHANNELS)]

    cmd += [
        # переносим теги/метаданные
        "-map_metadata", "0",
    ]

    # обложку как attached_pic понимают mp4/m4a/mp3 и т.п., но не Matroska (mka)
    if ext != ".mka":
        cmd += [
            "-map", "0:v?",
            "-c:v", "copy",          # обложка (album art) как есть, если есть
            "-disposition:v", "attached_pic",
        ]

    cmd += [str(dst)]

    p = subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",     # ffmpeg пишет UTF-8; не полагаемся на cp1251 системы
        errors="replace",     # не падаем на нестандартных байтах в выводе
    )

    if p.returncode != 0:
        if dst.exists():
            dst.unlink(missing_ok=True)
        # последняя содержательная строка вывода ffmpeg как причина
        lines = [ln.strip() for ln in (p.stdout or "").splitlines() if ln.strip()]
        reason = lines[-1] if lines else f"код возврата {p.returncode}"
        return (src.name, False, reason)

    return (src.name, True, "")


def main() -> None:
    ffmpeg = find_ffmpeg()

    fmt = OUT_FORMAT.lower()
    if fmt not in CODEC_MAP:
        print(f"❌ Неизвестный формат вывода: {OUT_FORMAT}")
        print(f"   Доступно: {', '.join(sorted(CODEC_MAP))}")
        sys.exit(1)

    ext, codec_args = CODEC_MAP[fmt]

    OUTDIR.mkdir(exist_ok=True)

    files = [
        f for f in Path(".").iterdir()
        if f.is_file() and f.suffix.lower() in EXTS
    ]

    if not files:
        print("⚠️ Аудио-файлы не найдены")
        return

    print(f"Найдено файлов : {len(files)}")
    print(f"Потоков        : {WORKERS}")
    print(f"Формат вывода  : {fmt} ({ext})")
    print(f"Каналы         : {CHANNELS or 'как в исходнике'}")
    if any(a == "-b:a" for a in codec_args):
        print(f"Аудио битрейт  : {BITRATE}")
    else:
        print("Аудио битрейт  : без потерь")
    print(f"Выход          : ./{OUTDIR}\n")

    ok = fail = 0

    with ProcessPoolExecutor(max_workers=WORKERS) as pool:
        futures = {
            pool.submit(process_file, f, ffmpeg, ext, codec_args): f
            for f in files
        }

        for fut in as_completed(futures):
            name, success, reason = fut.result()
            if success:
                print(f"✔ {name}")
                ok += 1
            else:
                print(f"✖ {name}  —  {reason}")
                fail += 1

    print(f"\nГотово. OK={ok} FAIL={fail}")


if __name__ == "__main__":
    main()