#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
🔥 Hardsub — вшивает .ass в видео (hevc_nvenc)

Берёт из текущей папки пары «видео + .ass с тем же именем»
и жарит хардсаб в папку ./Hardsub.

Запуск:  python hardsub.py
Нужен ffmpeg/ffprobe в PATH и NVENC-совместимая карта.
"""

import os
import re
import sys
import json
import time
import subprocess
from pathlib import Path

# ═══════════════════════════════════════════════
#   НАСТРОЙКИ
# ═══════════════════════════════════════════════
VIDEO_EXTS = (".mkv", ".mp4", ".avi", ".m2ts", ".ts")
OUT_DIR    = Path("Hardsub")
FONTS_DIR  = Path("Fonts")   # если есть — подхватится для рендера ass

ENCODE_ARGS = [
    "-c:v", "hevc_nvenc",
    "-tune", "hq",
    "-multipass", "fullres",
    "-rc", "vbr",
    "-cq", "17",
    "-qmin", "1",
    "-qmax", "51",
    "-bufsize", "80M",
    "-tier", "high",
    "-pix_fmt", "yuv420p10le",
]

# ═══════════════════════════════════════════════
#   ЦВЕТА (ANSI) — RGB для Windows Terminal
# ═══════════════════════════════════════════════
os.system("")  # включает VT-последовательности в cmd
class C:
    reset  = "\x1b[0m"
    bold   = "\x1b[1m"
    dim    = "\x1b[2m"
    red    = "\x1b[38;2;255;95;95m"
    green  = "\x1b[38;2;80;215;135m"
    yellow = "\x1b[38;2;255;195;75m"
    cyan   = "\x1b[38;2;75;205;215m"
    purple = "\x1b[38;2;155;115;255m"
    white  = "\x1b[38;2;215;215;215m"

def info(m): print(f"   {C.cyan}>{C.reset} {m}")
def ok(m):   print(f"   {C.green}✓{C.reset} {m}")
def warn(m): print(f"   {C.yellow}!{C.reset} {m}")
def err(m):  print(f"   {C.red}x{C.reset} {m}")

def banner():
    print(f"""
   {C.purple}{C.bold}▌{C.reset} {C.white}{C.bold}HARDSUB{C.reset}   {C.dim}hevc_nvenc · cq17 · 10bit{C.reset}
   {C.purple}{C.bold}▌{C.reset} {C.dim}видео + .ass с тем же именем → ./Hardsub{C.reset}

   {C.dim}{'─' * 46}{C.reset}
""")

def fmt_time(sec):
    if not sec or sec != sec or sec == float("inf"):
        return "--:--"
    m, s = divmod(int(sec), 60)
    return f"{m:02d}:{s:02d}"

# ═══════════════════════════════════════════════
#   ПОИСК ПАР
# ═══════════════════════════════════════════════
def find_pairs():
    pairs = []
    for f in sorted(Path(".").iterdir()):
        if f.is_file() and f.suffix.lower() in VIDEO_EXTS:
            ass = f.with_suffix(".ass")
            if ass.exists():
                pairs.append((f, ass))
    return pairs

def get_duration(path):
    try:
        out = subprocess.run(
            ["ffprobe", "-v", "quiet", "-print_format", "json", "-show_format", str(path)],
            capture_output=True, timeout=30,
        )
        return float(json.loads(out.stdout).get("format", {}).get("duration", 0))
    except Exception:
        return 0.0

# Экранирование пути для фильтра subtitles (спецсимволы filtergraph)
def escape_filter(p):
    s = str(p).replace("\\", "/")
    for ch in ("\\", ":", "'", "[", "]", ",", ";"):
        s = s.replace(ch, "\\" + ch)
    return s

# ═══════════════════════════════════════════════
#   КОДИРОВАНИЕ
# ═══════════════════════════════════════════════
def render_progress(name, cur, total, speed, eta):
    pct = cur / total if total > 0 else 0
    filled = round(24 * min(pct, 1.0))
    bar = f"{C.green}{'█' * filled}{C.reset}{C.dim}{'░' * (24 - filled)}{C.reset}"
    sp = f"{speed:.2f}x" if speed else "  ...  "
    sys.stdout.write(
        f"\r   {C.dim}{name[:28]:<28}{C.reset}  {bar}  {C.bold}{pct * 100:5.1f}%{C.reset}"
        f"  {C.yellow}{sp}{C.reset}  {C.dim}ETA {fmt_time(eta)}{C.reset}   "
    )
    sys.stdout.flush()

def hardsub(video, ass, out_path, duration):
    vf = f"subtitles={escape_filter(ass)}"
    if FONTS_DIR.is_dir():
        vf += f":fontsdir={escape_filter(FONTS_DIR)}"

    cmd = [
        "ffmpeg", "-hide_banner", "-y",
        "-v", "error",
        "-i", str(video),
        "-map", "0:v:0", "-map", "0:a?",
        "-vf", vf,
        *ENCODE_ARGS,
        "-c:a", "copy",
        "-progress", "pipe:1", "-nostats",
        str(out_path),
    ]

    start = time.time()
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    cur = 0.0
    for line in proc.stdout:
        line = line.strip()
        m = re.match(r"out_time_us=(\d+)", line)
        if m:
            cur = int(m.group(1)) / 1_000_000
        elif line.startswith("out_time_ms="):
            try: cur = int(line.split("=")[1]) / 1_000_000
            except ValueError: pass
        if line.startswith(("out_time", "progress=")):
            elapsed = time.time() - start
            speed = cur / elapsed if elapsed > 0 else 0
            eta = (duration - cur) / speed if speed > 0 and duration else 0
            render_progress(video.name, cur, duration, speed, eta)

    proc.wait()
    sys.stdout.write("\n")
    if proc.returncode != 0:
        tail = proc.stderr.read().strip().splitlines()
        raise RuntimeError(tail[-1] if tail else f"ffmpeg exit {proc.returncode}")

# ═══════════════════════════════════════════════
#   MAIN
# ═══════════════════════════════════════════════
def main():
    banner()

    pairs = find_pairs()
    if not pairs:
        warn("Пар «видео + .ass» в текущей папке не нашлось.")
        return

    print(f"   {C.purple}{C.bold}Найдено пар: {len(pairs)}{C.reset}\n")
    for i, (v, _) in enumerate(pairs, 1):
        exists = (OUT_DIR / v.name).exists()
        mark = f"  {C.yellow}(уже есть в Hardsub — будет перезаписан){C.reset}" if exists else ""
        print(f"   {C.cyan}{i:<3}{C.reset} {C.white}{v.name}{C.reset}{mark}")

    ans = input(f"\n   {C.purple}?{C.reset} Погнали? [Enter — да / n — отмена]  ").strip().lower()
    if ans == "n":
        info("Отменено.")
        return

    OUT_DIR.mkdir(exist_ok=True)
    print()

    done, errors = 0, []
    total_start = time.time()
    for i, (video, ass) in enumerate(pairs, 1):
        print(f"   {C.dim}[{i}/{len(pairs)}]{C.reset} {C.white}{C.bold}{video.name}{C.reset}")
        duration = get_duration(video)
        out_path = OUT_DIR / video.name
        # .part ПЕРЕД расширением — иначе ffmpeg не определит контейнер по ".part"
        tmp_path = OUT_DIR / (video.stem + ".part" + video.suffix)
        t0 = time.time()
        try:
            hardsub(video, ass, tmp_path, duration)
            tmp_path.replace(out_path)  # атомарно: недожаренные файлы не путаются с готовыми
            ok(f"{video.name}  {C.dim}{fmt_time(time.time() - t0)}{C.reset}")
            done += 1
        except KeyboardInterrupt:
            print()
            warn("Прервано. Недоделанный .part удалён.")
            tmp_path.unlink(missing_ok=True)
            break
        except Exception as e:
            err(f"{video.name}: {e}")
            errors.append(video.name)
            tmp_path.unlink(missing_ok=True)
        print()

    print(f"   {C.dim}{'─' * 46}{C.reset}")
    if errors:
        print(f"   {C.yellow}{C.bold}готово с ошибками{C.reset}  {C.green}✓ {done}{C.reset}  {C.red}x {len(errors)}{C.reset}")
        for name in errors:
            print(f"   {C.red}x{C.reset} {name}")
    else:
        print(f"   {C.green}{C.bold}все файлы готовы{C.reset}  ({done})")
    print(f"   {C.dim}🕐  Общее время: {fmt_time(time.time() - total_start)}  ·  папка: {OUT_DIR}{C.reset}\n")

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print()
        info("Выход.")
