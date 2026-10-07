#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mka_muxer.py — вшивание нескольких аудиодорожек в один .mka
by shiguchi

Берёт из папки набор аудиофайлов (по умолчанию .mka озвучки, но также
.mp3/.ac3/.dts/.flac/.aac/.m4a/.opus/.wav), группирует их по номеру серии
и на каждую серию собирает ОДИН .mka, внутри которого все озвучки лежат
отдельными аудиодорожками (муксинг без перекодирования, -c:a copy).

Режимы:
  1. По номеру серии  — пакетно, один .mka на серию.
  2. Все в один .mka   — фильм/OVA: все файлы папки → один .mka.

Порядок дорожек и их тайтлы настраиваются один раз (по «озвучкам»)
и применяются ко всем сериям.
"""

import os
import sys
import re
import json
import atexit
import signal
import subprocess
import datetime
import traceback
from pathlib import Path
from collections import defaultdict, OrderedDict

# ─── rich (интерфейс) ─────────────────────────────────────────
try:
    from rich.console import Console
    from rich.table import Table
    from rich.panel import Panel
    from rich.prompt import Prompt
    from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TimeRemainingColumn
    from rich.text import Text
    from rich import box
    RICH_AVAILABLE = True
except ImportError:
    RICH_AVAILABLE = False

# ─── anitopy (парсинг имён, опционально) ──────────────────────
try:
    import anitopy
    ANITOPY_AVAILABLE = True
except ImportError:
    ANITOPY_AVAILABLE = False


# ─── console (с фолбэком без rich) ────────────────────────────
if RICH_AVAILABLE:
    console = Console()
else:
    class _SimpleConsole:
        def print(self, *args, **kwargs):
            # грубо срезаем rich-разметку [tag]...[/]
            cleaned = []
            for a in args:
                s = str(a)
                s = re.sub(r'\[/?[^\]]*\]', '', s)
                cleaned.append(s)
            print(*cleaned)
    console = _SimpleConsole()


# ─── ffmpeg / ffprobe ─────────────────────────────────────────
FFMPEG_PATH = os.environ.get("FFMPEG_PATH", "ffmpeg")
FFPROBE_PATH = os.environ.get("FFPROBE_PATH", "ffprobe")

AUDIO_EXTENSIONS = [
    ".mka", ".mp3", ".ac3", ".dts", ".flac",
    ".aac", ".m4a", ".opus", ".wav", ".eac3", ".thd",
]

# Список озвучек для быстрого выбора тайтла (как в оригинале)
_VOICE_OPTIONS = [
    "AniLiberty (AniLibria)",
    "ТО Дубляжная",
    "Studio Band",
    "Оригинальная",
    "AniLibria.TV",
    "DEEP",
    "AniStar x DEEP",
    "AniLibria.TV x DEEP",
    "ТО Дубляжная x DEEP",
    "SHIZA Project",
    "AniDUB",
    "OnWave",
    "Reanimedia",
    "Dream Cast",
    "JAM",
    "AniPlague",
    "Animedia",
    "Shachiburi",
    "Ancord",
    "KANSAI Studio",
]


# ─── реестр процессов для чистого завершения по Ctrl+C ────────
ACTIVE_PROCESSES = set()

def register_process(p):
    try:
        ACTIVE_PROCESSES.add(p)
    except Exception:
        pass

def unregister_process(p):
    try:
        ACTIVE_PROCESSES.discard(p)
    except Exception:
        pass

def terminate_all_processes():
    for proc in list(ACTIVE_PROCESSES):
        try:
            if proc.poll() is None:
                proc.terminate()
                try:
                    proc.wait(timeout=3)
                except Exception:
                    proc.kill()
        except Exception:
            pass

atexit.register(terminate_all_processes)


# ─── логирование ──────────────────────────────────────────────
def log_error(error: Exception):
    try:
        ts = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        p = Path.cwd() / f"error_log_{ts}.txt"
        with p.open("w", encoding="utf-8") as f:
            f.write(f"Time: {datetime.datetime.now().isoformat()}\n")
            f.write(f"Error type: {type(error).__name__}\n")
            f.write(f"Message: {error}\n\nTraceback:\n")
            f.write(traceback.format_exc())
        return p
    except Exception:
        return None

def write_process_error_log(prefix: str, stderr_text: str):
    try:
        ts = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        safe = re.sub(r"[^\w.-]", "_", prefix)[:80] or "log"
        p = Path.cwd() / f"{safe}_ffmpeg_error_{ts}.log"
        with p.open("w", encoding="utf-8", errors="replace") as f:
            f.write(stderr_text or "")
        return p
    except Exception:
        return None


# ─── мелочи ───────────────────────────────────────────────────
def clear_screen():
    os.system('cls' if os.name == 'nt' else 'clear')

def _sanitize_name(name: str) -> str:
    """Убирает символы, недопустимые в именах файлов."""
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', name).strip().rstrip('.') or "output"


# ─── порядковые номера дорожек из anitools (keep_audio_only) ──
# anitools при многодорожечном извлечении даёт структуру
#   "N. Title/N. Show - 01.Title.mka", где N — номер дорожки в исходнике.
# Префикс "N. " убирается перед любым парсингом имени, а сам номер
# используется как порядок дорожек по умолчанию.
_TRACK_NUM_RE = re.compile(r'^(\d+)\.\s*')


def strip_track_num(name: str) -> str:
    """'2. Show - 01.Rus.mka' → 'Show - 01.Rus.mka'."""
    return _TRACK_NUM_RE.sub('', name, count=1)


def track_num(name: str):
    """Порядковый номер из префикса 'N. ' или None."""
    m = _TRACK_NUM_RE.match(name)
    return int(m.group(1)) if m else None


def _natural_key(name: str):
    """Сортировка по числовому префиксу (10 после 9), затем по имени."""
    n = track_num(name)
    return (n if n is not None else float('inf'), name.lower())


def voice_folder(path: str, root: str):
    """
    Папка озвучки: первая подпапка относительно корня ("1. AniDUB/…").
    Возвращает (label, num) — имя без префикса "N. " и сам номер,
    или (None, None), если файл лежит прямо в корне.
    """
    if not root:
        return None, None
    try:
        rel = os.path.relpath(path, root)
    except ValueError:
        return None, None
    parts = Path(rel).parts
    if len(parts) < 2:
        return None, None
    top = parts[0]
    label = strip_track_num(top).strip() or top
    return label, track_num(top)


def detect_lang(text: str):
    """
    Язык по тексту (метка/тайтл): «оригинальная / original / japan / JP» → jpn,
    «ENG / english / англ» → eng, иначе None.
    """
    low = (text or "").lower()
    if (any(k in low for k in ("ориг", "orig", "japan", "jpn", "яп"))
            or re.search(r'\bjp\b', low)):
        return "jpn"
    if (any(k in low for k in ("english", "англ"))
            or re.search(r'\beng?\b', low)):
        return "eng"
    return None


def ffprobe_title(path: str):
    """Возвращает title первой аудиодорожки файла или None."""
    try:
        cmd = [FFPROBE_PATH, "-v", "error", "-select_streams", "a:0",
               "-show_entries", "stream_tags=title",
               "-of", "default=noprint_wrappers=1:nokey=1", path]
        r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           encoding="utf-8", errors="replace")
        t = (r.stdout or "").strip()
        return t or None
    except Exception:
        return None


def audio_stream_count(path: str) -> int:
    """Сколько аудиопотоков в файле (обычно 1). При ошибке — 1."""
    try:
        cmd = [FFPROBE_PATH, "-v", "error", "-select_streams", "a",
               "-show_entries", "stream=index", "-of", "csv=p=0", path]
        r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           encoding="utf-8", errors="replace")
        n = len([ln for ln in (r.stdout or "").splitlines() if ln.strip()])
        return n if n > 0 else 1
    except Exception:
        return 1


# ─── парсинг серии / озвучки из имени файла ───────────────────
def parse_episode(filename: str):
    """
    Возвращает нормализованный номер серии (строкой, напр. '01') или None.
    Сначала anitopy, потом регэксп-фолбэк.
    """
    filename = strip_track_num(os.path.basename(filename))
    stem = Path(filename).stem

    if ANITOPY_AVAILABLE:
        try:
            p = anitopy.parse(filename)
            ep = p.get("episode_number")
            if isinstance(ep, list):
                ep = ep[0] if ep else None
            if ep is not None and str(ep).strip():
                s = str(ep).strip()
                return s.zfill(2) if s.isdigit() else s
        except Exception:
            pass

    # Фолбэк: ' - 01', '_01', '[01]', 'E01', 'ep01', ' 01 '
    patterns = [
        r'(?:^|[\s_\-\.])(?:e|ep|episode|серия|с)\s*(\d{1,4})(?=[\s_\-\.\[\]\(\)]|$)',
        r'[\s_\-]\s*(\d{1,4})(?=\s*(?:\[|\(|$|[\s_\-\.]))',
        r'\[(\d{1,4})\]',
    ]
    for pat in patterns:
        m = re.search(pat, stem, flags=re.IGNORECASE)
        if m:
            return m.group(1).zfill(2)
    return None


def episode_base(filename: str):
    """
    Пытается вытащить название тайтла для имени выходного файла.
    'Show Name - 01 [AniLibria]' → 'Show Name - 01'
    """
    filename = strip_track_num(os.path.basename(filename))
    stem = Path(filename).stem
    if ANITOPY_AVAILABLE:
        try:
            p = anitopy.parse(filename)
            title = p.get("anime_title")
            ep = p.get("episode_number")
            if isinstance(ep, list):
                ep = ep[0] if ep else None
            if title and ep is not None:
                return _sanitize_name(f"{title} - {str(ep).zfill(2) if str(ep).isdigit() else ep}")
            if title:
                return _sanitize_name(title)
        except Exception:
            pass
    # фолбэк: срезаем последний [..]/(..) тег
    s = re.sub(r'\s*[\[\(][^\]\)]*[\]\)]\s*$', '', stem).strip(' -_.')
    return _sanitize_name(s or stem)


def _strip_trailing_episode(label: str, ep) -> str:
    """
    Срезает хвостовой номер серии из метки, НО только если он совпадает
    с номером эпизода файла. Так "AniFilm 01" (серия 01) → "AniFilm",
    а легитимные цифры в названии ("Studio 2x2") не трогаются.
    """
    s = (label or "").strip()
    if ep is None:
        return s
    try:
        epn = int(str(ep).lstrip('0') or '0')
    except (ValueError, TypeError):
        return s
    m = re.search(r'[\s\-_.]+0*(\d{1,4})$', s)
    if m and int(m.group(1)) == epn:
        return s[:m.start()].strip(' -_.') or s
    return s


def voice_label(path: str, ep_base_guess: str = "", root: str = "") -> str:
    """
    «Метка озвучки» — стабильный идентификатор голоса между сериями.
    Приоритет: папка озвучки ("1. AniDUB/…") → встроенный title →
    тег в скобках → остаток имени без базы серии.
    Метка нормализуется: из неё убирается номер серии, иначе релизеры,
    зашивающие номер в title ("AniFilm 01"), и сырые файлы без title
    дают разную метку на каждую серию и группировка ломается.
    """
    folder_lbl, _ = voice_folder(path, root)
    if folder_lbl:
        return folder_lbl

    stem = strip_track_num(Path(path).stem)
    t = ffprobe_title(path)
    if t:
        candidate = t.strip()
    else:
        br = re.findall(r'[\[\(]([^\]\)]+)[\]\)]', stem)
        if br:
            candidate = br[-1].strip()
        else:
            s = stem
            if ep_base_guess and s.startswith(ep_base_guess):
                s = s[len(ep_base_guess):]
            candidate = s.strip(' -_.[]()') or stem

    ep = parse_episode(os.path.basename(path))
    candidate = _strip_trailing_episode(candidate, ep)
    return candidate or stem


# ─── ffmpeg-мукс одной группы в .mka ──────────────────────────
def mux_group(ordered_files, title_by_path, lang_by_path, output_path):
    """
    Собирает несколько аудиофайлов в один .mka.
    ordered_files    — список путей в нужном порядке дорожек.
    title_by_path    — {path: title|None}
    lang_by_path     — {path: 'rus'|'jpn'|None}
    Возвращает (ok: bool, stderr: str).
    """
    cmd = [FFMPEG_PATH, "-nostdin", "-y"]
    for p in ordered_files:
        cmd += ["-i", p]

    meta_opts = []
    disp_opts = []
    out_a = 0
    for in_idx, p in enumerate(ordered_files):
        n = audio_stream_count(p)
        for local in range(n):
            cmd += ["-map", f"{in_idx}:a:{local}?"]
            title = title_by_path.get(p)
            if title:
                meta_opts += [f"-metadata:s:a:{out_a}", f"title={title}"]
            lang = lang_by_path.get(p)
            if lang:
                meta_opts += [f"-metadata:s:a:{out_a}", f"language={lang}"]
            disp_opts += [f"-disposition:a:{out_a}", "default" if out_a == 0 else "none"]
            out_a += 1

    # только аудио, без перекодирования, без глав/видео
    cmd += ["-c:a", "copy", "-vn", "-map_chapters", "-1"]
    cmd += meta_opts + disp_opts
    cmd += [output_path]

    creationflags = 0
    if os.name == 'nt' and hasattr(subprocess, 'CREATE_NEW_PROCESS_GROUP'):
        creationflags = subprocess.CREATE_NEW_PROCESS_GROUP

    proc = subprocess.Popen(
        cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="replace", creationflags=creationflags,
    )
    register_process(proc)
    try:
        _, stderr = proc.communicate()
    finally:
        unregister_process(proc)
    return proc.returncode == 0, (stderr or "")


# ─── сбор и группировка файлов ────────────────────────────────
def scan_audio(input_folder):
    """Аудиофайлы в папке и её подпапках (структура anitools "N. Title/…"),
    относительными путями. Выходная папка MKA пропускается.
    Сортировка натуральная по префиксу "N. " папки и файла — так дорожки
    идут в порядке исходника, а не "1, 10, 11, 2"."""
    files = []
    for root, dirs, names in os.walk(input_folder):
        dirs[:] = sorted((d for d in dirs if d != "MKA" and not d.startswith('.')),
                         key=_natural_key)
        for f in sorted(names, key=_natural_key):
            if Path(f).suffix.lower() in AUDIO_EXTENSIONS:
                files.append(os.path.relpath(os.path.join(root, f), input_folder))
    return files


def group_by_episode(input_folder, files):
    """{episode_key: [full_path, ...]} с сохранением порядка сортировки."""
    groups = OrderedDict()
    for f in files:
        ep = parse_episode(f)
        key = ep if ep is not None else "no_ep"
        groups.setdefault(key, []).append(os.path.join(input_folder, f))
    return groups


# ─── конфигурирование порядка/тайтлов по озвучкам ─────────────
def build_label_config(all_paths, root=""):
    """
    Определяет набор различимых озвучек по всем файлам,
    спрашивает порядок и тайтлы один раз.
    Возвращает:
      order_labels   — список меток в нужном порядке,
      title_by_label — {label: title},
      lang_by_label  — {label: 'rus'|'jpn'|None}
    """
    # метка озвучки → пример пути (для дефолтного тайтла)
    label_example = OrderedDict()
    for p in all_paths:
        base = episode_base(os.path.basename(p))
        lbl = voice_label(p, base, root)
        if lbl not in label_example:
            label_example[lbl] = p

    # Порядок по умолчанию: озвучки с номером ("N. " у папки или файла) —
    # по номеру, файлы без префикса (лежат россыпью рядом с папками) — после них.
    def _num(p):
        _, n = voice_folder(p, root)
        return n if n is not None else track_num(os.path.basename(p))

    nums = {lbl: _num(p) for lbl, p in label_example.items()}
    if any(n is not None for n in nums.values()):
        label_example = OrderedDict(sorted(
            label_example.items(),
            key=lambda kv: (nums[kv[0]] if nums[kv[0]] is not None else float('inf'))
        ))

    labels = list(label_example.keys())

    # показать найденные озвучки
    t = Table(title="🎵 Найденные озвучки", box=box.ROUNDED,
              border_style="cyan", title_style="bold cyan")
    t.add_column("#", justify="center", style="magenta", no_wrap=True)
    t.add_column("Метка (озвучка)", style="bold white")
    for i, lbl in enumerate(labels, 1):
        t.add_row(str(i), lbl)
    console.print(t)

    order_labels = list(labels)

    # порядок
    if len(labels) > 1:
        ans = Prompt.ask("🔀 [bold magenta]Изменить порядок дорожек?[/] [1-Да/2-Нет]",
                         choices=["1", "2"], default="2")
        if ans == "1":
            while True:
                raw = Prompt.ask(f"  Введите номера через запятую (напр. [cyan]2,1,3[/])\n"
                                 f"  [dim]Доступны: 1–{len(labels)}[/]")
                try:
                    nums = [int(x.strip()) for x in raw.split(",")]
                    if len(nums) == len(labels) and sorted(nums) == list(range(1, len(labels) + 1)):
                        order_labels = [labels[n - 1] for n in nums]
                        break
                    console.print(f"  [red]Нужно ввести все {len(labels)} номера без повторов.[/]")
                except ValueError:
                    console.print("  [red]Введите числа через запятую.[/]")

    # тайтлы
    title_by_label = {lbl: lbl for lbl in order_labels}
    ans = Prompt.ask("✏ [bold magenta]Задать тайтлы дорожкам?[/] [1-Да/2-Нет]",
                     choices=["1", "2"], default="2")
    if ans == "1":
        vt = Table(title="🎙 Войс-лист", box=box.ROUNDED, border_style="cyan", title_style="bold cyan")
        vt.add_column("#", justify="center", style="magenta", no_wrap=True)
        vt.add_column("Озвучка", style="bold white")
        for vi, vname in enumerate(_VOICE_OPTIONS, 1):
            vt.add_row(str(vi), vname)
        vt.add_row("0", "[dim]Ввести вручную[/]")
        console.print(vt)

        for lbl in order_labels:
            console.print(f"\n  [magenta]Дорожка[/] [dim]({lbl})[/]")
            pick = Prompt.ask("  Выбери номер из списка или [cyan]0[/] для ручного ввода", default="0")
            if pick.isdigit() and 1 <= int(pick) <= len(_VOICE_OPTIONS):
                chosen = _VOICE_OPTIONS[int(pick) - 1]
            else:
                chosen = Prompt.ask("  Тайтл дорожки", default=lbl)
            title_by_label[lbl] = (chosen.strip() or lbl)

    # язык
    lang_by_label = {lbl: None for lbl in order_labels}
    ans = Prompt.ask("🌐 [bold magenta]Проставить язык дорожек?[/] [1-Да/2-Нет]",
                     choices=["1", "2"], default="1")
    if ans == "1":
        default_lang = Prompt.ask("  Язык по умолчанию (ISO 639-2, напр. [cyan]rus[/])", default="rus")
        for lbl in order_labels:
            lang_by_label[lbl] = (detect_lang(f"{lbl} {title_by_label.get(lbl, '')}")
                                  or default_lang.strip() or "rus")

    return order_labels, title_by_label, lang_by_label


# ─── основной процесс ─────────────────────────────────────────
def process(input_folder):
    clear_screen()
    files = scan_audio(input_folder)

    console.print(Panel(f"Найдено [bold magenta]{len(files)}[/] аудиофайлов "
                        f"([dim]{', '.join(AUDIO_EXTENSIONS)}[/])",
                        title="🎵 Информация", border_style="magenta", box=box.ROUNDED))
    if not files:
        console.print(Panel("Нет подходящих аудиофайлов в папке.",
                            title="❌ Ошибка", border_style="red", box=box.DOUBLE))
        return

    mode = Prompt.ask(
        "🧩 [bold magenta]Режим сборки[/]\n"
        "  [cyan]1[/] — по номеру серии (пакетно, один .mka на серию)\n"
        "  [cyan]2[/] — все файлы в один .mka (фильм/OVA)",
        choices=["1", "2"], default="1")

    output_folder = os.path.join(input_folder, "MKA")
    os.makedirs(output_folder, exist_ok=True)

    # ── строим группы {output_name: [paths]} ──
    plan = OrderedDict()
    if mode == "1":
        groups = group_by_episode(input_folder, files)
        for key, paths in groups.items():
            out_name = episode_base(os.path.basename(paths[0]))
            # если у нескольких серий совпало имя — добавим ключ серии
            if out_name in plan:
                out_name = f"{out_name} [{key}]"
            plan[out_name] = paths
    else:
        out_name = Prompt.ask("  Имя выходного файла (без .mka)",
                              default=episode_base(os.path.basename(files[0])))
        plan[_sanitize_name(out_name)] = [os.path.join(input_folder, f) for f in files]

    # ── предпросмотр плана ──
    clear_screen()
    prev = Table(title="🎬 План сборки", box=box.ROUNDED,
                 border_style="magenta", title_style="bold magenta")
    prev.add_column("#", justify="center", style="magenta", no_wrap=True)
    prev.add_column("Выходной .mka", style="bold white")
    prev.add_column("Дорожек", justify="center", style="green")
    prev.add_column("Файлы", style="dim")
    for i, (out_name, paths) in enumerate(plan.items(), 1):
        names = ", ".join(Path(p).name for p in paths)
        prev.add_row(str(i), out_name + ".mka", str(len(paths)), names[:60])
    console.print(prev)

    confirm = Prompt.ask("\n  ▶ [bold magenta]Продолжить?[/] [1-Да/2-Отмена]",
                         choices=["1", "2"], default="1")
    if confirm == "2":
        console.print(Panel("Отменено.", title="ℹ", border_style="yellow", box=box.ROUNDED))
        return

    # ── конфиг порядка/тайтлов/языка по всем файлам сразу ──
    all_paths = [p for paths in plan.values() for p in paths]
    order_labels, title_by_label, lang_by_label = build_label_config(all_paths, input_folder)
    label_rank = {lbl: i for i, lbl in enumerate(order_labels)}

    # ── сборка ──
    ok_count, fail_count, skip_count = 0, 0, 0
    with Progress(
        SpinnerColumn(style="magenta"),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn(),
        console=console,
    ) as progress:
        task = progress.add_task("🔗 [green]Сборка .mka...", total=len(plan))

        for out_name, paths in plan.items():
            output_path = os.path.join(output_folder, out_name + ".mka")
            progress.update(task, description=f"🔗 [green]Сборка:[/] [cyan]{out_name}.mka[/]")

            if os.path.exists(output_path) and os.path.getsize(output_path) > 0:
                skip_count += 1
                progress.update(task, advance=1)
                continue

            # упорядочиваем файлы группы по глобальному порядку озвучек
            def sort_key(p):
                base = episode_base(os.path.basename(p))
                lbl = voice_label(p, base, input_folder)
                return (label_rank.get(lbl, 10_000), _natural_key(Path(p).name))

            ordered = sorted(paths, key=sort_key)

            title_by_path, lang_by_path = {}, {}
            for p in ordered:
                base = episode_base(os.path.basename(p))
                lbl = voice_label(p, base, input_folder)
                title_by_path[p] = title_by_label.get(lbl, lbl)
                lang = lang_by_label.get(lbl)
                # тайтл в метаданных самого файла (eng / jp) важнее метки
                if lang:
                    lang = detect_lang(ffprobe_title(p) or "") or lang
                lang_by_path[p] = lang

            try:
                ok, stderr = mux_group(ordered, title_by_path, lang_by_path, output_path)
                if ok:
                    ok_count += 1
                else:
                    fail_count += 1
                    log_file = write_process_error_log(out_name, stderr)
                    details = f"Лог: {log_file}" if log_file else "лог не сохранён"
                    console.print(f"[bold red]❌ Ошибка:[/] [cyan]{out_name}.mka[/] ({details})")
            except Exception as e:
                fail_count += 1
                log_error(e)
                console.print(f"[bold red]❌ Непредвиденная ошибка:[/] [cyan]{out_name}.mka[/]")

            progress.update(task, advance=1)

    console.print(Panel(
        f"[green]Готово:[/] {ok_count}   "
        f"[yellow]Пропущено:[/] {skip_count}   "
        f"[red]Ошибок:[/] {fail_count}\n"
        f"[dim]Папка результата: {output_folder}[/]",
        title="✅ Сборка завершена", border_style="green", box=box.DOUBLE))


# ─── просмотр дорожек готового файла ──────────────────────────
def probe_tracks(path):
    """
    Возвращает список аудиодорожек файла:
    [{'i': вых_номер, 'title': str|None, 'lang': str|None,
      'codec': str|None, 'ch': str|None, 'default': bool}, ...]
    """
    cmd = [FFPROBE_PATH, "-v", "error", "-select_streams", "a",
           "-show_entries",
           "stream=index,codec_name,channels,channel_layout,disposition:stream_tags=title,language",
           "-of", "json", path]
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       encoding="utf-8", errors="replace")
    tracks = []
    try:
        data = json.loads(r.stdout or "{}")
    except Exception:
        return tracks
    for i, st in enumerate(data.get("streams", [])):
        tags = st.get("tags", {}) or {}
        disp = st.get("disposition", {}) or {}
        layout = st.get("channel_layout")
        ch = st.get("channels")
        ch_str = layout if layout else (f"{ch} ch" if ch else None)
        tracks.append({
            "i": i,
            "title": tags.get("title"),
            "lang": tags.get("language"),
            "codec": st.get("codec_name"),
            "ch": ch_str,
            "default": bool(disp.get("default")),
        })
    return tracks


def _esc(s):
    """Экранирует rich-разметку в произвольном тексте (тайтлы с '[' и т.п.)."""
    return s.replace('[', r'\[') if isinstance(s, str) else s


def print_tracks_table(filename, tracks):
    """Печатает таблицу дорожек одного файла (для просмотра/проверки)."""
    t = Table(title=f"🎧 {_esc(filename)}", box=box.ROUNDED,
              border_style="cyan", title_style="bold cyan")
    t.add_column("#", justify="center", style="magenta", no_wrap=True)
    t.add_column("Тайтл", style="bold white")
    t.add_column("Язык", justify="center", style="green")
    t.add_column("Кодек", justify="center", style="dim")
    t.add_column("Каналы", justify="center", style="dim")
    t.add_column("Def", justify="center", style="yellow")
    if not tracks:
        console.print(Panel(f"[yellow]Аудиодорожек не найдено:[/] {_esc(filename)}",
                            border_style="yellow", box=box.ROUNDED))
        return
    for tr in tracks:
        t.add_row(
            str(tr["i"] + 1),
            _esc(tr["title"]) if tr["title"] else "[dim]—[/]",
            _esc(tr["lang"]) if tr["lang"] else "[dim]—[/]",
            _esc(tr["codec"]) if tr["codec"] else "[dim]—[/]",
            _esc(tr["ch"]) if tr["ch"] else "[dim]—[/]",
            "★" if tr["default"] else "",
        )
    console.print(t)


def print_copy_block(tracks, fmt="1"):
    """
    Печатает plain-text список тайтлов, готовый к копированию в тгк.
    Без rich-рамок и разметки (обычный print), чтобы копировалось как есть.
    fmt: 1 — маркеры (• …), 2 — нумерация (1. …), 3 — просто текст.
    """
    titles = []
    for tr in tracks:
        # для копирования берём только тайтл; если его нет — понятная заглушка
        titles.append(tr["title"] or f"Дорожка {tr['i'] + 1}")
    if not titles:
        return

    print()  # пустая строка-разделитель перед блоком
    print("─── для копирования ───")
    for n, title in enumerate(titles, 1):
        if fmt == "2":
            print(f"{n}. {title}")
        elif fmt == "3":
            print(title)
        else:
            print(f"• {title}")
    print("───────────────────────")


def show_tracks(input_folder):
    """Отдельный пункт: вывод названий всех дорожек готового .mka."""
    clear_screen()
    files = [f for f in sorted(os.listdir(input_folder))
             if Path(f).suffix.lower() in (".mka", ".mkv", ".mp4", ".mov", ".m4a", ".webm")]

    if not files:
        console.print(Panel("В папке нет .mka (или иных медиа) файлов.",
                            title="ℹ Информация", border_style="yellow", box=box.ROUNDED))
        return

    # выбор файла
    target = files[0]
    show_all = False
    if len(files) > 1:
        lst = Table(title="📄 Файлы в папке", box=box.ROUNDED,
                    border_style="magenta", title_style="bold magenta")
        lst.add_column("#", justify="center", style="magenta", no_wrap=True)
        lst.add_column("Файл", style="bold white")
        for i, f in enumerate(files, 1):
            lst.add_row(str(i), _esc(f))
        lst.add_row("0", "[dim]Показать все файлы[/]")
        console.print(lst)

        pick = Prompt.ask("🎯 [bold magenta]Выбери номер файла[/] ([cyan]0[/] — все)",
                          default="1")
        if pick == "0":
            show_all = True
        elif pick.isdigit() and 1 <= int(pick) <= len(files):
            target = files[int(pick) - 1]
        else:
            target = files[0]

    # формат копи-блока
    fmt = Prompt.ask(
        "📋 [bold magenta]Формат списка для копирования[/]\n"
        "  [cyan]1[/] — маркеры (• …)   [cyan]2[/] — нумерация (1. …)   [cyan]3[/] — просто текст",
        choices=["1", "2", "3"], default="1")

    targets = files if show_all else [target]
    for f in targets:
        tracks = probe_tracks(os.path.join(input_folder, f))
        print_tracks_table(f, tracks)
        print_copy_block(tracks, fmt)


def main():
    if not RICH_AVAILABLE:
        print("❌ Нужна библиотека 'rich':  pip install rich")
        return

    clear_screen()
    console.print(Panel(
        Text.from_markup(
            "[bold magenta]▌[/] [bold white]MKA MUXER[/]   [dim]by shiguchi[/]\n"
            "[bold magenta]▌[/] [dim]несколько озвучек → один .mka[/]"),
        box=box.DOUBLE_EDGE, border_style="magenta", padding=(0, 2)))

    current_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd()
    console.print(Panel(str(current_dir), title="📂 Рабочая директория",
                        border_style="magenta", box=box.ROUNDED))

    if not ANITOPY_AVAILABLE:
        console.print("[dim]  (anitopy не установлен — номер серии парсится регэкспом; "
                      "для точности:  pip install anitopy)[/]")

    menu = Table(title="Меню действий", box=box.ROUNDED,
                 border_style="magenta", title_style="bold magenta")
    menu.add_column("  #", justify="center", style="bold magenta", no_wrap=True)
    menu.add_column("Действие", style="bold white")
    menu.add_column("Описание", style="dim")
    menu.add_row("1", "Собрать .mka", "несколько озвучек → один .mka")
    menu.add_row("2", "Показать дорожки", "список дорожек готового .mka")
    console.print(menu)

    choice = Prompt.ask("🎯 [bold magenta]Введите номер действия[/]",
                        choices=["1", "2"], default="1")

    if choice == "1":
        process(str(current_dir))
    else:
        show_tracks(str(current_dir))


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        terminate_all_processes()
    except Exception as e:
        log_file = log_error(e)
        msg = "Произошла непредвиденная ошибка."
        details = f"Лог: {log_file}" if log_file else "лог не сохранён."
        console.print(f"{msg} {details}")
    finally:
        terminate_all_processes()
