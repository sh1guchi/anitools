from __future__ import annotations

from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path
import re
import sys


# ================= НАСТРОЙКИ =================
SHIFT_SECONDS = 1.0
OUTDIR = Path("subs_fixed")

# сколько файлов обрабатывать одновременно
WORKERS = 6

EXTS = {".srt", ".ass", ".ssa"}
# ============================================


def shift_time_srt(time_str: str, shift: float) -> str:
    """Сдвигает временную метку SRT формата HH:MM:SS,mmm"""
    h, m, rest = time_str.split(":")
    s, ms = rest.split(",")
    total_ms = (int(h) * 3600 + int(m) * 60 + int(s)) * 1000 + int(ms)
    total_ms += int(shift * 1000)
    if total_ms < 0:
        total_ms = 0
    h_new = total_ms // 3600000
    m_new = (total_ms % 3600000) // 60000
    s_new = (total_ms % 60000) // 1000
    ms_new = total_ms % 1000
    return f"{h_new:02}:{m_new:02}:{s_new:02},{ms_new:03}"


def shift_time_ass(time_str: str, shift: float) -> str:
    """Сдвигает временную метку ASS формата H:MM:SS.cc"""
    h, m, rest = time_str.split(":")
    s, cs = rest.split(".")
    total_cs = (int(h) * 3600 + int(m) * 60 + int(s)) * 100 + int(cs)
    total_cs += int(shift * 100)
    if total_cs < 0:
        total_cs = 0
    h_new = total_cs // 360000
    m_new = (total_cs % 360000) // 6000
    s_new = (total_cs % 6000) // 100
    cs_new = total_cs % 100
    return f"{h_new}:{m_new:02}:{s_new:02}.{cs_new:02}"


def process_srt(src: Path, dst: Path, shift: float) -> None:
    text = src.read_text(encoding="utf-8-sig", errors="replace")

    def replace_ts(m: re.Match) -> str:
        return f"{shift_time_srt(m.group(1), shift)} --> {shift_time_srt(m.group(2), shift)}"

    text = re.sub(
        r"(\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2},\d{3})",
        replace_ts,
        text
    )
    dst.write_text(text, encoding="utf-8")


def process_ass(src: Path, dst: Path, shift: float) -> None:
    text = src.read_text(encoding="utf-8-sig", errors="replace")

    def replace_ts(m: re.Match) -> str:
        return f"Dialogue: {m.group(1)}{shift_time_ass(m.group(2), shift)},{shift_time_ass(m.group(3), shift)},{m.group(4)}"

    text = re.sub(
        r"Dialogue: (\d+,)(\d:\d{2}:\d{2}\.\d{2}),(\d:\d{2}:\d{2}\.\d{2}),(.*)",
        replace_ts,
        text
    )
    dst.write_text(text, encoding="utf-8")


def process_file(src: Path) -> tuple[str, bool]:
    try:
        dst = OUTDIR / src.name
        ext = src.suffix.lower()

        if ext == ".srt":
            process_srt(src, dst, SHIFT_SECONDS)
        elif ext in {".ass", ".ssa"}:
            process_ass(src, dst, SHIFT_SECONDS)

        return (src.name, True)
    except Exception as e:
        return (src.name, False)


def main() -> None:
    OUTDIR.mkdir(exist_ok=True)

    files = [
        f for f in Path(".").iterdir()
        if f.is_file() and f.suffix.lower() in EXTS
    ]

    if not files:
        print("⚠️ Файлы субтитров не найдены")
        return

    print(f"Найдено файлов : {len(files)}")
    print(f"Потоков       : {WORKERS}")
    print(f"Сдвиг         : +{SHIFT_SECONDS} сек")
    print(f"Выход         : ./{OUTDIR}\n")

    ok = fail = 0

    with ProcessPoolExecutor(max_workers=WORKERS) as pool:
        futures = {pool.submit(process_file, f): f for f in files}

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
