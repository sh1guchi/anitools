#!/usr/bin/env python3
"""
Golden-эталоны для C#-порта (docs/PLAN.md §5.2).

Прогоняет чистые функции оригинала reference/python/anitools.py на наборе входов
и пишет ответы в tests/Anitools.Core.Tests/Golden/<имя>.json. C#-тесты сверяют с ними
свою реализацию.

    python3 tools/gen_golden.py          # перегенерировать эталоны
    python3 tools/gen_golden.py --check  # проверить, что эталоны актуальны (для CI)

Оригинал не меняется, rich не нужен. Внешние программы не запускаются:
где функция зовёт ffprobe/ffmpeg, subprocess подменяется записанным выводом;
пути — «чистые» (PureWindowsPath/PurePosixPath) без mkdir и удаления.

Формат файла: шапка (function, description, source, source_sha256, python) и
"cases": [{"input": …, "output": …} | {"input": …, "error": "ValueError"}, …],
по одному случаю в строке. source_sha256 считается по тексту оригинала с переводами
строк LF (на Windows git отдаёт CRLF — хэш от этого не меняется).
"""
from __future__ import annotations

import argparse
import atexit
import contextlib
import hashlib
import importlib.util
import json
import platform
import random
import signal
import sys
import types
import unicodedata
from pathlib import Path, PurePosixPath, PureWindowsPath

ROOT = Path(__file__).resolve().parent.parent
REF_REL = "reference/python/anitools.py"
REF = ROOT / REF_REL
INPUTS = ROOT / "tools" / "golden_inputs"
OUT = ROOT / "tests" / "Anitools.Core.Tests" / "Golden"


# ─── Загрузка оригинала ──────────────────────────────────────────────────────

def load_reference():
    spec = importlib.util.spec_from_file_location("anitools_reference", REF)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    # При импорте оригинал вешает atexit (с FFMPEG_HARD_KILL_ALL=1 на Windows — taskkill всех
    # ffmpeg.exe) и обработчики SIGTERM/SIGBREAK. Генератору это не нужно — снимаем.
    atexit.unregister(mod.terminate_all_processes)
    for name in ("SIGTERM", "SIGBREAK"):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), signal.SIG_DFL)
    # Пути к программам — как по умолчанию, независимо от переменных окружения
    mod.FFMPEG_PATH = "ffmpeg"
    mod.FFPROBE_PATH = "ffprobe"
    mod.ANITOOLS_WORK_DIR = ""
    # Оригинал печатает предупреждения в консоль — генератору они не нужны
    mod.console = types.SimpleNamespace(print=lambda *a, **k: None)
    return mod


at = load_reference()


def source_sha256() -> str:
    return hashlib.sha256(REF.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


# ─── Общие помощники ─────────────────────────────────────────────────────────

def read_list(name: str) -> list[str]:
    """Строки файла из tools/golden_inputs: без пустых и без комментариев '#'."""
    text = (INPUTS / name).read_text(encoding="utf-8")
    return [line for line in text.split("\n") if line.strip() and not line.startswith("#")]


def unique(items):
    seen, out = set(), []
    for x in items:
        key = json.dumps(x, ensure_ascii=False, sort_keys=True)
        if key not in seen:
            seen.add(key)
            out.append(x)
    return out


def call(fn, *args, **kwargs) -> dict:
    """{'output': результат} или {'error': тип исключения} — исключение тоже часть поведения."""
    try:
        return {"output": fn(*args, **kwargs)}
    except Exception as e:  # noqa: BLE001
        return {"error": type(e).__name__}


def cases_for(fn, inputs) -> list[dict]:
    return [{"input": x, **call(fn, x)} for x in inputs]


class _NoDisk:
    """Чистый путь, у которого mkdir — no-op, а stat берёт размер из FAKE_SIZES."""

    def mkdir(self, *args, **kwargs):
        pass

    def stat(self):
        return types.SimpleNamespace(st_size=FAKE_SIZES.get(str(self), 0))


class WinPath(_NoDisk, PureWindowsPath):
    pass


class PosixPath(_NoDisk, PurePosixPath):
    pass


PATH_TYPES = {"windows": WinPath, "posix": PosixPath}
FAKE_SIZES: dict[str, int] = {}


@contextlib.contextmanager
def patched(**attrs):
    """Временно подменить глобальные имена в модуле оригинала."""
    old = {k: getattr(at, k) for k in attrs}
    for k, v in attrs.items():
        setattr(at, k, v)
    try:
        yield
    finally:
        for k, v in old.items():
            setattr(at, k, v)


def fake_subprocess(handler, calls: list):
    """Подмена модуля subprocess: run(cmd) → handler(cmd) = (stdout, stderr)."""
    real = at.subprocess

    def run(cmd, *args, **kwargs):
        calls.append([str(c) for c in cmd])
        result = handler(cmd)
        if isinstance(result, Exception):
            raise result
        out, err = result
        return types.SimpleNamespace(stdout=out, stderr=err, returncode=0)

    return types.SimpleNamespace(run=run, PIPE=real.PIPE, DEVNULL=real.DEVNULL, STDOUT=real.STDOUT)


# ─── Реестр эталонов ─────────────────────────────────────────────────────────

GOLDENS: list[tuple[str, str, object]] = []


def golden(name: str, description: str):
    def deco(fn):
        GOLDENS.append((name, description, fn))
        return fn
    return deco


# ─── Входные данные ──────────────────────────────────────────────────────────

# Имена с пробелами/табами по краям и пустое — не в файле, чтобы их не «почистил» редактор
EXTRA_NAMES = ["   Title - 05.mkv   ", "\tTitle - 06.mkv", ""]


def filenames() -> list[str]:
    return unique(read_list("filenames.txt") + EXTRA_NAMES)


EXTRA_TITLES = [
    "", "  leading spaces", "trailing spaces  ", "Tab\tInside", "ctrl\x01char",
    "Title\u00a0NBSP", "Ñandú: Ácido", unicodedata.normalize("NFD", "Pokémon: Mezase"),
    "Title?.", " . ", "..Title..", "Title - ", "Title:", "a:b",
]


def anitomy_titles() -> list[str]:
    out = []
    for n in filenames():
        try:
            t = at.anitomy_parse(n).get("anime_title")
        except Exception:  # noqa: BLE001
            t = None
        if t:
            out.append(t)
    return out


def titles() -> list[str]:
    return unique(read_list("titles.txt") + EXTRA_TITLES + anitomy_titles())


def name_stems() -> list[str]:
    return unique(PureWindowsPath(n).stem for n in filenames() if n.strip())


# ─── Имена файлов: anitomy, номер серии, название группы ─────────────────────

@golden("anitomy_parse", "Встроенный anitomy (py:187): имя файла → словарь строк")
def g_anitomy():
    return cases_for(at.anitomy_parse, filenames())


@golden("extract_episode_number_smart", "Номер серии для п.5 (py:3169): строка ≥2 цифр или null")
def g_episode_smart():
    return cases_for(at.extract_episode_number_smart, filenames())


@golden("extract_episode_number_advanced", "Шаг 3 extract_episode_number_smart (py:3085)")
def g_episode_advanced():
    return cases_for(at.extract_episode_number_advanced, filenames())


@golden("extract_episode_number", "Шаг 4 extract_episode_number_smart, старый список (py:3034)")
def g_episode_basic():
    return cases_for(at.extract_episode_number, filenames())


@golden("parse_anime_title", "Название тайтла для группировки HLS (py:2396)")
def g_parse_title():
    return cases_for(at._parse_anime_title, filenames())


@golden("parse_anime_group", "Группа HLS: тайтл + OVA/ONA/Special/Movie (py:2425)")
def g_parse_group():
    return cases_for(at._parse_anime_group, filenames())


# ─── Названия и санитизация ──────────────────────────────────────────────────

@golden("title_season", "Номер сезона по названию (py:496)")
def g_title_season():
    return cases_for(at._title_season, titles())


@golden("clean_title_for_search", "Название без сезона/части для поиска на Shikimori (py:545)")
def g_clean_title():
    return cases_for(at._clean_title_for_search, titles())


@golden("norm_title", "Нормализация для сравнения названий при ранжировании (py:510)")
def g_norm_title():
    return cases_for(at._norm_title, titles())


@golden("filename_safe_title", "Название для имени файла в п.5 (py:657)")
def g_filename_safe_title():
    return cases_for(at._filename_safe_title, titles())


@golden("sanitize_folder", "Имя папки тайтла/серии/озвучки в п.7 (py:3779)")
def g_sanitize_folder():
    extra = [f"52991 - {t}" for t in ("Sousou no Frieren", "Re:Zero", "Fate/Zero", "What?")]
    return cases_for(at._sanitize_folder, unique(titles() + name_stems() + extra))


@golden("sanitize_folder_name", "Имя папки дорожки в п.2 (py:1188)")
def g_sanitize_folder_name():
    return cases_for(at.sanitize_folder_name, unique(titles() + name_stems()))


@golden("process_output_filename", "Имя выходного файла в п.1–4: '_' → пробел (py:1113)")
def g_process_output_filename():
    return cases_for(at.process_output_filename, [n for n in filenames() if n][:60] + ["a_b_c.mkv", "__", "_"])


# ─── Ввод дорожек, язык, внешнее аудио, сортировка ───────────────────────────

@golden("parse_track_ids", "Ввод дорожек '1,3-5' → 0-based индексы (py:1195)")
def g_parse_track_ids():
    inputs = [
        "1", "2", "1,3-5", "1, 3 - 5, 8", "5-3", "1,1,2,1", "3-5,4,1", "13-1", "1-1", " 7 ",
        "", " ", ",", "1,,2", "0", "0-2", "a", "1-", "-1", "1--3", "2-2-3", "+2",
        "1.5", "1;2", "10-12,1",
    ]
    return cases_for(at._parse_track_ids, inputs)


@golden("detect_lang", "Язык по метке/тайтлу дорожки: jpn/eng/null (py:1754)")
def g_detect_lang():
    inputs = [
        "Оригинальная", "Original", "ORIGINAL", "Japanese", "japanese", "Japan", "JP", "jp", "[JP]",
        "Jpn", "jpn", "Японская", "яп", "Яп.", "English", "ENG", "Eng", "en", "en-US", "Eng Sub",
        "English Dub", "Английская", "англ", "Русская", "rus", "ru", "und", "AniLibria.TV", "DEEP",
        "Studio Band", "Journey", "Ten", "Gen", "jpg", "Original + AniLibria", "Eng Original",
        "Оригинал (JP)", "JP_Original", "jp2", "eng1", "", None,
    ]
    return cases_for(at._detect_lang, inputs)


@golden("external_audio_matches", "Внешний аудиофайл относится к серии base (py:1707)")
def g_external_audio_matches():
    pairs = [
        ("Title - 05.AniLibria.TV.mka", "Title - 05"), ("Title - 05.mka", "Title - 05"),
        ("Title - 051.mka", "Title - 05"), ("Title - 05", "Title - 05"),
        ("1. Title - 05.AniLibria.TV.mka", "Title - 05"), ("10. Title - 05.DEEP.mka", "Title - 05"),
        ("01. Show.mka", "01. Show"), ("01. Show.mka", "Show"), ("Title - 05 [AniLibria].mka", "Title - 05"),
        ("Title - 05v2.mka", "Title - 05"), ("title - 05.mka", "Title - 05"), ("Title - 05Ж.mka", "Title - 05"),
        ("Title - 05_rus.mka", "Title - 05"), ("Title - 05 .mka", "Title - 05"),
        ("2. 2. Title - 05.mka", "Title - 05"), ("2.Title - 05.mka", "Title - 05"),
        ("2 . Title - 05.mka", "Title - 05"), ("Title - 05", ""), ("", "Title"),
        ("Title - 05.mka", "Title - 05.mka"), ("Title - 05（JP）.mka", "Title - 05"),
        ("Провожающая - 01.Студия.mka", "Провожающая - 01"), ("Провожающая - 012.mka", "Провожающая - 01"),
    ]
    return [{"input": {"filename": f, "base_name": b}, **call(at._external_audio_matches, f, b)} for f, b in pairs]


@golden("natural_sort", "Натуральная сортировка по префиксу 'N. ' (py:1723): ключ и порядок")
def g_natural_sort():
    lists = [
        ["10. DEEP", "2. AniLibria.TV", "1. Оригинальная", "Audio only", "audio", "3. Studio Band"],
        ["1. b", "1. B", "1. a", "01. c", "001. d", "1.e", "1 . f", "x", "X", "_", "Ж", "ж", "й", "ё", "Е"],
        ["Title - 10.mka", "Title - 9.mka", "Title - 1.mka", "title - 2.mka"],
        ["10. Ten", "2. Two", "02. Zero two", "Two"],
        [],
    ]
    cases = []
    for names in lists:
        keys = [[k[0] if k[0] != float("inf") else None, k[1]] for k in map(at._natural_key, names)]
        cases.append({"input": names, "output": {"sorted": sorted(names, key=at._natural_key), "keys": keys}})
    return cases


# ─── Субтитры ─────────────────────────────────────────────────────────────────

@golden("codec_id_to_ext", "Расширение файла субтитров по codec_id mkvmerge (py:2783)")
def g_codec_id_to_ext():
    inputs = [
        "S_TEXT/ASS", "S_TEXT/SSA", "S_ASS", "S_SSA", "S_TEXT/UTF8", "S_TEXT/ASCII", "S_TEXT/UTF-8",
        "S_HDMV/PGS", "S_HDMV/TEXTST", "S_VOBSUB", "S_TEXT/WEBVTT", "S_DVBSUB", "S_KATE", "S_TEXT/USF",
        "s_text/ass", "s_text/utf8", "", "A_AAC", "SubStationAlpha", "PGS",
    ]
    return cases_for(at.codec_id_to_ext, inputs)


def _track(tid, name, lang="", ietf="", codec="S_TEXT/ASS"):
    return [tid, {"id": tid, "name": name, "codec_id": codec, "language": lang, "language_ietf": ietf}]


SUB_FIXTURES = {
    "ru_signs_full_en": [
        _track(2, "Надписи", "rus", "ru"), _track(3, "Полные", "rus", "ru"),
        _track(4, "English", "eng", "en", "S_TEXT/UTF8"),
    ],
    "other_order": [
        _track(3, "Полные", "rus", "ru"), _track(4, "Надписи [Studio]", "rus", "ru"),
        _track(5, "English", "eng", "en", "S_HDMV/PGS"),
    ],
    "no_language": [_track(2, "Субтитры 3"), _track(3, "Язык: und", "und", "und")],
    "duplicates": [
        _track(2, "Full", "rus", "ru"), _track(3, "Full", "rus", "ru"), _track(4, "Full Signs", "rus", "ru"),
    ],
    "ietf_only": [_track(2, "RU", "", "ru"), _track(3, "EN", "", "en-US", "S_TEXT/UTF8")],
    "casefold": [_track(2, "Straße", "ger", "de"), _track(3, "STRASSE Signs", "ger", "de")],
    "spaces": [_track(2, "  Надписи  ", "rus", "ru"), _track(3, "надписи и песни", "rus", "ru")],
    "single": [_track(7, "Signs", "eng", "en", "S_VOBSUB")],
    "empty": [],
}


@golden("subtitle_track_by_title", "Дорожка субтитров серии по тайтлу эталона (py:2798) → [id, ext] | null")
def g_sub_by_title():
    queries = [
        ("ru_signs_full_en", "Надписи"), ("ru_signs_full_en", "надписи "), ("ru_signs_full_en", "Полн"),
        ("ru_signs_full_en", "e"), ("ru_signs_full_en", "Нет такой"), ("ru_signs_full_en", ""),
        ("other_order", "Надписи"), ("other_order", "Полные"), ("other_order", "english"),
        ("no_language", "Субтитры 3"), ("duplicates", "Full"), ("duplicates", "full signs"),
        ("duplicates", "Signs"), ("casefold", "STRASSE"), ("casefold", "straße"), ("spaces", "Надписи"),
        ("spaces", "надписи"), ("single", ""), ("single", "sig"), ("empty", "Надписи"),
    ]
    cases = []
    for fixture, ref in queries:
        tracks = [tuple(t) for t in SUB_FIXTURES[fixture]]
        cases.append({"input": {"tracks": fixture, "ref_title": ref},
                      **call(at._find_subtitle_track_by_title, tracks, ref)})
    return cases, {"fixtures": SUB_FIXTURES}


@golden("subtitle_track_by_lang", "Дорожка субтитров серии по языку эталона (py:2819) → [id, ext] | null")
def g_sub_by_lang():
    ru_signs = SUB_FIXTURES["ru_signs_full_en"][0][1]
    ru_full = SUB_FIXTURES["ru_signs_full_en"][1][1]
    en = SUB_FIXTURES["ru_signs_full_en"][2][1]
    queries = [
        ("ru_signs_full_en", ru_signs, 0), ("ru_signs_full_en", ru_full, 1), ("ru_signs_full_en", en, 0),
        ("other_order", ru_signs, 0), ("other_order", ru_full, 1), ("other_order", en, 0),
        ("other_order", ru_signs, 5), ("duplicates", ru_signs, 0), ("duplicates", ru_signs, 1),
        ("duplicates", ru_signs, 2), ("duplicates", ru_signs, 3),
        ("duplicates", {"name": "Full", "language": "rus", "language_ietf": ""}, 2),
        ("ietf_only", ru_signs, 0), ("ietf_only", {"name": "x", "language": "", "language_ietf": "en-US"}, 0),
        ("ietf_only", en, 0), ("no_language", ru_signs, 0), ("casefold", {"name": "strasse", "language": "GER", "language_ietf": ""}, 1),
        ("single", en, 0), ("empty", ru_signs, 0),
    ]
    cases = []
    for fixture, ref_info, ref_pos in queries:
        tracks = [tuple(t) for t in SUB_FIXTURES[fixture]]
        cases.append({"input": {"tracks": fixture, "ref_info": ref_info, "ref_pos": ref_pos},
                      **call(at._find_subtitle_track_by_lang, tracks, ref_info, ref_pos)})
    return cases, {"fixtures": SUB_FIXTURES}


@golden("sub_langs", "Языки дорожки субтитров: {language, language_ietf} без und (py:2814), отсортировано")
def g_sub_langs():
    infos = [
        {"language": "rus", "language_ietf": "ru"}, {"language": "RUS", "language_ietf": "ru-RU"},
        {"language": "und", "language_ietf": "und"}, {"language": "UND", "language_ietf": ""},
        {"language": " jpn ", "language_ietf": None}, {"language": "", "language_ietf": "en"},
        {"language": "ger", "language_ietf": "de"}, {},
    ]
    return [{"input": i, **call(lambda x: sorted(at._sub_langs(x)), i)} for i in infos]


# ─── Shikimori ───────────────────────────────────────────────────────────────

def _anime(id_, name, russian, kind, episodes, aired_on):
    return {"id": id_, "name": name, "russian": russian, "kind": kind, "episodes": episodes,
            "episodes_aired": episodes, "aired_on": aired_on, "score": "8.0", "status": "released",
            "url": f"/animes/{id_}", "image": {"original": f"/system/animes/original/{id_}.jpg"}}


# Синтетические ответы API /animes?search=… (структура как у Shikimori, данные условные)
SHIKI_FIXTURES = {
    "frieren": [
        _anime(52991, "Sousou no Frieren", "Провожающая в последний путь Фрирен", "tv", 28, "2023-09-29"),
        _anime(59978, "Sousou no Frieren 2nd Season", "Провожающая в последний путь Фрирен 2", "tv", 0, "2026-01-16"),
        _anime(56805, "Sousou no Frieren: ●● no Mahou", None, "special", 0, "2023-10-06"),
        _anime(57000, "Sousou no Frieren Recap", "", "tv_special", 1, None),
    ],
    "rezero": [
        _anime(31240, "Re:Zero kara Hajimeru Isekai Seikatsu", "Re:Zero. Жизнь с нуля в альтернативном мире", "tv", 25, "2016-04-04"),
        _anime(39587, "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season", "Re:Zero. Жизнь с нуля 2", "tv", 13, "2020-07-08"),
        _anime(42203, "Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season Part 2", "Re:Zero. Жизнь с нуля 2, часть 2", "tv", 12, "2021-01-06"),
        _anime(54857, "Re:Zero kara Hajimeru Isekai Seikatsu 3rd Season", "Re:Zero. Жизнь с нуля 3", "tv", 16, "2024-10-02"),
        _anime(36286, "Re:Zero kara Hajimeru Isekai Seikatsu: Memory Snow", "Re:Zero. Снежные воспоминания", "movie", 1, "2018-10-06"),
        _anime(38414, "Re:Zero kara Hajimeru Isekai Seikatsu: Hyouketsu no Kizuna", "Re:Zero. Узы льда", "movie", 1, "2019-11-08"),
        _anime(32263, "Re:Zero kara Hajimeru Break Time", "Re:Zero. Перерыв", "special", 11, "2016-04-08"),
    ],
    "overlord": [
        _anime(29803, "Overlord", "Повелитель", "tv", 13, "2015-07-07"),
        _anime(35073, "Overlord II", "Повелитель 2", "tv", 13, "2018-01-10"),
        _anime(37675, "Overlord III", "Повелитель 3", "tv", 13, "2018-07-11"),
        _anime(48895, "Overlord IV", "Повелитель 4", "tv", 13, "2022-07-06"),
        _anime(52634, "Overlord Movie 2: Sei Oukoku-hen", "Повелитель. Фильм 2", "movie", 1, "2024-09-20"),
        _anime(31138, "Overlord: Ple Ple Pleiades", "Повелитель: Плеяды", "special", 8, "2015-09-25"),
    ],
    "madeinabyss": [
        _anime(34599, "Made in Abyss", "Созданный в Бездне", "tv", 13, "2017-07-07"),
        _anime(41084, "Made in Abyss: Retsujitsu no Ougonkyou", "Созданный в Бездне: Золотой город", "tv", 12, "2022-07-06"),
        _anime(36862, "Made in Abyss Movie 3: Fukaki Tamashii no Reimei", "Созданный в Бездне. Фильм 3", "movie", 1, "2020-01-17"),
        _anime(37514, "Made in Abyss Movie 1: Tabidachi no Yoake", "Созданный в Бездне. Фильм 1", "movie", 1, "2019-01-04"),
        _anime(37515, "Made in Abyss Movie 2: Hourou Suru Tasogare", "Созданный в Бездне. Фильм 2", "movie", 1, "2019-01-18"),
    ],
    "hellsing": [
        _anime(777, "Hellsing Ultimate", "Хеллсинг: Война с нечистью OVA", "ova", 10, "2006-02-10"),
        _anime(270, "Hellsing", "Хеллсинг: Война с нечистью", "tv", 13, "2001-10-11"),
        _anime(3088, "Hellsing Ultimate: The Dawn", "Хеллсинг: Рассвет", "ova", 3, "2011-07-27"),
        _anime(2236, "Hellsing: Digest for Freaks", "", "special", 1, "2006-02-10"),
    ],
    "mushoku": [
        _anime(39535, "Mushoku Tensei: Isekai Ittara Honki Dasu", "Реинкарнация безработного", "tv", 11, "2021-01-11"),
        _anime(45576, "Mushoku Tensei: Isekai Ittara Honki Dasu Part 2", "Реинкарнация безработного, часть 2", "tv", 12, "2021-10-04"),
        _anime(51179, "Mushoku Tensei II: Isekai Ittara Honki Dasu", "Реинкарнация безработного 2", "tv", 12, "2023-07-03"),
        _anime(50360, "Mushoku Tensei: Isekai Ittara Honki Dasu - Eris no Goblin Toubatsu", "Реинкарнация безработного: Эрис", "special", 1, "2022-01-26"),
        _anime(55888, "Mushoku Tensei II: Isekai Ittara Honki Dasu Part 2", "Реинкарнация безработного 2, часть 2", "tv", 12, "2024-04-08"),
        _anime(59193, "Mushoku Tensei III: Isekai Ittara Honki Dasu", None, "tv", None, None),
    ],
    "aot": [
        _anime(16498, "Shingeki no Kyojin", "Атака титанов", "tv", 25, "2013-04-07"),
        _anime(25777, "Shingeki no Kyojin Season 2", "Атака титанов 2", "tv", 12, "2017-04-01"),
        _anime(35760, "Shingeki no Kyojin Season 3", "Атака титанов 3", "tv", 12, "2018-07-23"),
        _anime(38524, "Shingeki no Kyojin Season 3 Part 2", "Атака титанов 3, часть 2", "tv", 10, "2019-04-29"),
        _anime(40028, "Shingeki no Kyojin: The Final Season", "Атака титанов: Финал", "tv", 16, "2020-12-07"),
        _anime(48583, "Shingeki no Kyojin: The Final Season Part 2", "Атака титанов: Финал, часть 2", "tv", 12, "2022-01-10"),
        _anime(51535, "Shingeki no Kyojin: The Final Season - Kanketsu-hen", "Атака титанов: Финал. Заключение", "tv_special", 2, "2023-03-04"),
        _anime(18397, "Shingeki no Kyojin OVA", "Атака титанов OVA", "ova", 8, "2013-12-09"),
    ],
    "weird": [
        {"id": 1, "name": None, "russian": None, "kind": None, "episodes": None, "aired_on": None},
        {"name": "No id", "kind": "tv"},
        {"id": 0, "name": "Zero id", "kind": "tv"},
        {"id": 2, "name": "Name only", "kind": "music", "aired_on": "1999"},
        {"id": 3, "name": "Short date", "russian": "Короткая дата", "kind": "pv", "episodes": 2, "aired_on": "20"},
    ],
    "empty": [],
    "error_object": {"code": 404, "message": "Not found"},
}


@golden("shikimori_search", "Разбор ответа /animes?search (py:470): путь запроса и список результатов")
def g_shiki_search():
    cases = []
    queries = [("Sousou no Frieren", "frieren"), ("Re:Zero kara Hajimeru Isekai Seikatsu", "rezero"),
               ("Fate/stay night", "empty"), ("Weird", "weird"), ("Провожающая Фрирен", "empty"),
               ("Missing", "error_object"), ("Overlord", "overlord")]
    for query, fixture in queries:
        paths = []
        with patched(_shiki_get=lambda p, tries=3, f=fixture: (paths.append(p), SHIKI_FIXTURES[f])[1]):
            out = call(at._search_shikimori, query)
        cases.append({"input": {"query": query, "response": fixture}, "api_paths": paths, **out})
    return cases, {"fixtures": SHIKI_FIXTURES}


@golden("shikimori_search_variants", "Запасные запросы к Shikimori (py:529): пути API по порядку, если всё пусто")
def g_shiki_variants():
    inputs = [
        "Sousou no Frieren", "Re:Zero kara Hajimeru Isekai Seikatsu", "Kaguya-sama wa Kokurasetai: Ultra Romantic",
        "Steins;Gate 0", "Dr. Stone: New World", "Bocchi the Rock!", "K-On!!", "Kono Subarashii Sekai ni Shukufuku wo!",
        "Hunter x Hunter (2011)", "Fate/stay night [Unlimited Blade Works]", "Title  with   spaces",
        "Провожающая в последний путь Фрирен", "【推しの子】", "", "a", "One Two Three Four", "One Two Three",
        "'Quoted' \"name\"", "~Tilde~", "Gintama.", "100% Pascal-sensei", "Mob Psycho 100 & Co + #1",
    ]
    cases = []
    for q in inputs:
        paths = []
        with patched(_shiki_get=lambda p, tries=3: (paths.append(p), [])[1]):
            out = call(at._search_shikimori_smart, q)
        cases.append({"input": q, "api_paths": paths, **out})
    return cases


SHIKI_TITLES = [
    ("Sousou no Frieren", "frieren"), ("Sousou no Frieren 2nd Season", "frieren"), ("Sousou no Frieren Special", "frieren"),
    ("Re:Zero kara Hajimeru Isekai Seikatsu", "rezero"), ("Re:Zero kara Hajimeru Isekai Seikatsu 2nd Season", "rezero"),
    ("Re:Zero kara Hajimeru Isekai Seikatsu 3rd Season", "rezero"), ("Re:Zero kara Hajimeru Isekai Seikatsu Movie", "rezero"),
    ("Overlord", "overlord"), ("Overlord III", "overlord"), ("Overlord IV", "overlord"), ("Overlord Movie", "overlord"),
    ("Overlord Special", "overlord"), ("Made in Abyss", "madeinabyss"), ("Made in Abyss Movie", "madeinabyss"),
    ("Made in Abyss 2", "madeinabyss"), ("Hellsing Ultimate OVA", "hellsing"), ("Hellsing", "hellsing"),
    ("Mushoku Tensei", "mushoku"), ("Mushoku Tensei II", "mushoku"), ("Mushoku Tensei OVA", "mushoku"),
    ("Mushoku Tensei 3", "mushoku"), ("Shingeki no Kyojin", "aot"), ("Shingeki no Kyojin Season 3", "aot"),
    ("Shingeki no Kyojin The Final Season Part 2", "aot"), ("Shingeki no Kyojin OVA", "aot"),
    ("Shingeki no Kyojin ONA", "aot"), ("Unknown Title", "empty"), ("Weird", "weird"),
]


@golden("shikimori_rank", "Выбор тайтла (py:566–581): тип/сезон/запрос из названия группы и порядок первых 8 ID")
def g_shiki_rank():
    cases = []
    for title, fixture in SHIKI_TITLES:
        km = at._SPECIAL_SUFFIX_RE.search(title)
        kinds = at._SHIKI_KINDS[km.group(1)] if km else ("tv",)
        base_title = at._SPECIAL_SUFFIX_RE.sub("", title)
        season = at._title_season(base_title)
        query = at._clean_title_for_search(base_title) or base_title
        with patched(_shiki_get=lambda p, tries=3, f=fixture: SHIKI_FIXTURES[f]):
            results = at._search_shikimori(query)
        ranked = at._rank_shikimori(results, query, season, kinds)[:8]
        cases.append({"input": {"title": title, "response": fixture},
                      "output": {"kinds": list(kinds), "base_title": base_title, "season": season,
                                 "query": query, "ranked_ids": [r["id"] for r in ranked]}})
    return cases, {"fixtures": SHIKI_FIXTURES}


@golden("shikimori_original_name", "Оригинальное название по ID (py:651)")
def g_shiki_original_name():
    responses = {
        "52991": {"id": 52991, "name": "Sousou no Frieren", "russian": "Провожающая в последний путь Фрирен"},
        "1": {"id": 1, "name": "", "russian": "Только русское"},
        "2": {"id": 2, "russian": "Без name"},
        "404": None,
    }
    cases = []
    for sid, resp in responses.items():
        paths = []
        with patched(_shiki_get=lambda p, tries=3, r=resp: (paths.append(p), r)[1]):
            out = call(at._shiki_original_name, sid)
        cases.append({"input": {"id": sid, "response": resp}, "api_paths": paths, **out})
    return cases


# ─── Медиа: MOV и mkvmerge ───────────────────────────────────────────────────

MEDIA = INPUTS / "media"


def _box(kind: bytes, payload: bytes, *, size64: bool = False, size0: bool = False) -> bytes:
    if size64:
        return (1).to_bytes(4, "big") + kind + (16 + len(payload)).to_bytes(8, "big") + payload
    if size0:
        return (0).to_bytes(4, "big") + kind + payload
    return (8 + len(payload)).to_bytes(4, "big") + kind + payload


def _hdlr(handler: bytes, name: bytes) -> bytes:
    # version/flags(4) + pre_defined(4) + handler_type(4) + reserved(12) + name
    return _box(b"hdlr", b"\0" * 8 + handler + b"\0" * 12 + name)


def _trak(handler: bytes, hdlr_name: bytes = b"", udta: bytes = b"") -> bytes:
    body = _box(b"mdia", _box(b"mdhd", b"\0" * 24) + _hdlr(handler, hdlr_name))
    if udta:
        body += _box(b"udta", udta)
    return _box(b"trak", _box(b"tkhd", b"\0" * 84) + body)


def _intl_text(text: str) -> bytes:
    raw = text.encode("utf-8")
    return len(raw).to_bytes(2, "big") + b"\x55\xc4" + raw


MOV_SAMPLES = {
    "names_of_all_kinds": _box(b"ftyp", b"qt  \0\0\0\0qt  ") + _box(b"moov", b"".join([
        _box(b"mvhd", b"\0" * 100),
        _trak(b"vide", b"\x0cVideoHandler"),
        _trak(b"soun", b"\x0cSoundHandler", _box(b"name", "AniLibria.TV\0".encode())),
        _trak(b"soun", b"", _box(b"\xa9nam", _intl_text("Оригинальная"))),
        _trak(b"soun", b"\x04DEEP"),
        _trak(b"soun", b"\x0cSoundHandler"),
        _trak(b"soun", "Studio Band\0".encode()),
        _trak(b"soun", b"", _box(b"titl", b"  \0\0 Dream Cast \0")),
        _trak(b"soun", b"", _box(b"name", b"bad \xff\xfe utf8")),
        _trak(b"soun", b"core media audio\0"),
        _trak(b"text", b"", _box(b"name", b"Subtitle")),
    ])) + _box(b"mdat", b"\0" * 32),
    "moov_64bit_size": _box(b"moov", _trak(b"soun", b"", _box(b"name", b"Track A")), size64=True),
    "moov_to_end": _box(b"wide", b"") + _box(b"moov", _trak(b"soun", b"", _box(b"name", b"Last")), size0=True),
    "truncated_trak": _box(b"moov", _trak(b"soun", b"", _box(b"name", b"Ok"))
                           + (500).to_bytes(4, "big") + b"trak" + b"\0" * 20),
    "short_hdlr": _box(b"moov", _box(b"trak", _box(b"mdia", _box(b"hdlr", b"\0" * 8 + b"so")))),
    "no_moov": _box(b"ftyp", b"isom") + _box(b"mdat", b"\0" * 16),
    "garbage": b"not a quicktime file at all",
    "empty": b"",
}


@golden("mov_audio_titles", "Имена аудиодорожек из боксов QuickTime/MP4 (py:1589–1701); вход — байты файла в base64")
def g_mov_audio_titles():
    import base64
    import tempfile
    cases = []
    with tempfile.TemporaryDirectory() as td:
        for name, data in MOV_SAMPLES.items():
            path = Path(td) / f"{name}.mov"
            path.write_bytes(data)
            cases.append({"input": {"name": name, "base64": base64.b64encode(data).decode("ascii")},
                          **call(at._read_mov_audio_titles, str(path))})
        cases.append({"input": {"name": "missing_file", "base64": None},
                      **call(at._read_mov_audio_titles, str(Path(td) / "missing.mov"))})
    return cases


@golden("mkv_subtitle_tracks", "Дорожки субтитров из mkvmerge -J (py:2560): [id, {name, codec_id, language, language_ietf}]")
def g_mkv_subtitle_tracks():
    cases = []
    for fixture in sorted(MEDIA.glob("*.mkvmerge.json")):
        name = fixture.name.removesuffix(".mkvmerge.json")
        identify = fixture.read_text(encoding="utf-8")

        def handler(cmd, identify=identify):
            return (identify, "") if cmd[:2] == ["mkvmerge", "-J"] else ("", "")

        calls: list = []
        with patched(subprocess=fake_subprocess(handler, calls)):
            res = call(at.list_subtitle_tracks, str(MEDIA / name), quiet=True)
        if "output" in res:
            count, tracks, ok = res["output"]
            res["output"] = [[tid, {k: info[k] for k in ("name", "codec_id", "language", "language_ietf") if k in info}]
                             for tid, info in tracks]
        cases.append({"input": f"{name}.mkvmerge.json", **res})
    synthetic = {
        # имя из тегов, запасные имена, codec_id из названия кодека
        "synthetic": {"tracks": [
            {"id": 0, "type": "video", "codec": "AVC/H.264/MPEG-4p10", "properties": {"codec_id": "V_MPEG4/ISO/AVC"}},
            {"id": 1, "type": "subtitles", "codec": "SubStationAlpha", "properties": {"language": "rus"},
             "tags": {"simple": [{"name": "TITLE", "value": "Из тегов"}]}},
            {"id": 2, "type": "subtitles", "codec": "SubRip/SRT", "properties": {"language": "eng", "language_ietf": "en"}},
            {"id": 3, "type": "subtitles", "codec": "HDMV PGS", "properties": {}},
            {"id": 4, "type": "subtitles", "codec": "VobSub", "properties": {"track_name": "  "}},
            {"id": 5, "type": "subtitles", "codec": "Unknown", "codec_id": "", "properties": {"track_name": "Без кодека"}},
            {"id": 6, "type": "audio", "codec_id": "S_TEXT/ASS", "properties": {"track_name": "Аудио с S_TEXT"}},
        ]},
    }
    for name, data in synthetic.items():
        identify = json.dumps(data, ensure_ascii=False)
        with patched(subprocess=fake_subprocess(lambda cmd, j=identify: (j, "") if cmd[:2] == ["mkvmerge", "-J"] else ("", ""), [])):
            res = call(at.list_subtitle_tracks, name, quiet=True)
        if "output" in res:
            res["output"] = [[tid, {k: info[k] for k in ("name", "codec_id", "language", "language_ietf") if k in info}]
                             for tid, info in res["output"][1]]
        cases.append({"input": {"inline": data}, **res})
    return cases


@golden("list2cmdline", "Командная строка для логов ошибок (subprocess.list2cmdline, py:846)")
def g_list2cmdline():
    import subprocess as sp
    inputs = [
        ["ffmpeg", "-nostdin", "-y", "-i", r"D:\anime\Sousou no Frieren\Frieren - 01.mkv", "-map", "0:v:0"],
        ["mkvextract", "tracks", r"D:\a b\x.mkv", r"3:D:\a b\надписи\x.надписи.ass"],
        ["a", "", "b c", "d\te"], ["C:\\path with space\\"], ["C:\\no_space\\"], ['say "hi"'], ['a"b'],
        ['a\\"b'], ['a\\\\"b c'], ["\\\\server\\share\\x y\\"], ["title=AniLibria.TV x DEEP"], [],
    ]
    return [{"input": args, **call(sp.list2cmdline, args)} for args in inputs]


# ─── Строки Python ───────────────────────────────────────────────────────────

@golden("str_casing", "str.lower() и str.casefold() для всех символов, которые они меняют: код → [lower, casefold]")
def g_str_casing():
    cases = []
    for cp in range(sys.maxunicode + 1):
        if 0xD800 <= cp <= 0xDFFF:
            continue
        c = chr(cp)
        lower, folded = c.lower(), c.casefold()
        if lower != c or folded != c:
            cases.append({"input": cp, "output": [lower, folded]})
    return cases


# ─── Константы ───────────────────────────────────────────────────────────────

@golden("constants", "Константы оригинала, которые C# должен повторить один в один")
def g_constants():
    names = [
        "_AT_KEYWORDS", "_SHIKI_KIND_RU", "_ROMAN_SEASON", "_SHIKI_KINDS", "_SPECIAL_LABELS", "_VOICE_OPTIONS",
        "_HLS_RESOLUTIONS", "_HLS_SEGMENT_TIME", "_HLS_FIXED_CQ", "_HLS_FIXED_CQ_PEAK", "_HLS_NVENC_PRESET",
        "_HLS_CAL_WINDOWS", "_HLS_CAL_TOLERANCE", "_HLS_CAL_MAX_PASSES", "_HLS_CQ_START", "_HLS_CQ_RANGE",
        "_NVDEC_MAX_SIZE", "_MOV_GENERIC_HANDLERS",
    ]
    return [{"input": n, "output": getattr(at, n)} for n in names]


# ─── ffprobe/ffmpeg: разбор вывода ───────────────────────────────────────────

FILE = r"D:\anime\Sousou no Frieren\[SubsPlease] Sousou no Frieren - 01 (1080p).mkv"


def probe_cases(fn, outputs, *, both=False):
    """Каждый вариант вывода → вызов fn(FILE) с подменённым subprocess; записываются и команды."""
    cases = []
    for out in outputs:
        calls: list = []
        if both:  # (stdout ffprobe, stderr ffmpeg) — для длительности
            handler = lambda cmd, o=out: (o[0], "") if cmd[0] == at.FFPROBE_PATH else ("", o[1])
        else:
            handler = lambda cmd, o=out: o if isinstance(o, Exception) else (o, "")
        with patched(subprocess=fake_subprocess(handler, calls)):
            res = call(fn, FILE)
        inp = {"stdout": out[0], "stderr": out[1]} if both else ({"raise": True} if isinstance(out, Exception) else out)
        cases.append({"input": inp, "commands": calls, **res})
    return cases


def _ffjson(streams):
    return json.dumps({"programs": [], "streams": streams}, ensure_ascii=False, indent=4)


@golden("video_duration", "Длительность (py:2235): ffprobe format=duration → ffmpeg 'Duration:' → 3600")
def g_video_duration():
    outputs = [
        ("1425.024000\n", ""), ("  23.976  \n", ""), ("N/A\n", "  Duration: 00:23:40.02, start: 0.000000, bitrate: 5000 kb/s\n"),
        ("", "  Duration: 01:02:03.45, start: 0.0\n"), ("", "  Duration: N/A, bitrate: N/A\n"),
        ("", ""), ("abc", "Duration: 00:00:05.5"), ("1e3", ""), ("-5", ""),
    ]
    return probe_cases(at.get_video_duration, outputs, both=True)


@golden("audio_channels", "Каналы аудиодорожек (py:4194): ffprobe stream=channels -of csv=p=0")
def g_audio_channels():
    outputs = ["2\n6\n", "2,\n6,\n8,\n", "", "2\n\n6\n", "N/A\n2\n", " 8 \n", "6\r\n2\r\n", "1\n", OSError("no ffprobe")]
    return probe_cases(at._get_audio_channels, outputs)


@golden("audio_tracks_for_episode", "Аудиодорожки для озвучек HLS (py:3754) и их раскладка (py:3784)")
def g_audio_tracks_for_episode():
    outputs = [
        _ffjson([{"index": 1, "tags": {"title": "AniLibria.TV", "language": "rus"}},
                 {"index": 2, "tags": {"title": "Оригинальная", "language": "jpn"}},
                 {"index": 3, "tags": {"title": " DEEP ", "language": "RUS"}}]),
        _ffjson([{"index": 1, "tags": {"language": "jpn"}}, {"index": 2}, {"index": 3, "tags": {"title": "", "language": ""}}]),
        _ffjson([]), "{}", "not json", "",
    ]
    cases = probe_cases(at._get_audio_track_ids_for_episode, outputs)
    for c in cases:
        if "output" in c:
            c["layout"] = at._audio_layout(c["output"])
    return cases


@golden("ffprobe_audio_titles", "Тайтлы аудиодорожек (py:1769): '' если нет title")
def g_ffprobe_audio_titles():
    outputs = [
        _ffjson([{"index": 1, "tags": {"title": "AniLibria.TV"}}, {"index": 2, "tags": {"title": "  Оригинальная  "}},
                 {"index": 3}, {"index": 4, "tags": None}, {"index": 5, "tags": {"title": None}}]),
        _ffjson([]), "", "{}", "garbage",
    ]
    return probe_cases(at._ffprobe_audio_titles, outputs)


@golden("needs_cpu_decode", "Причина декодировать на CPU — лимиты NVDEC (py:3914) или null")
def g_needs_cpu_decode():
    def s(codec, w, h):
        return _ffjson([{"codec_name": codec, "width": w, "height": h}])
    outputs = [
        s("h264", 3840, 2160), s("h264", 4096, 2160), s("h264", 5120, 2880), s("h264", 2160, 4097),
        s("hevc", 7680, 4320), s("hevc", 8192, 4320), s("hevc", 8193, 4320), s("av1", 3840, 2160),
        s("vp9", 8200, 4000), s("vp8", 4097, 2000), s("mpeg2video", 4080, 2160), s("mpeg2video", 4096, 2160),
        s("mpeg1video", 5000, 100), s("mpeg4", 1920, 1080), s("mpeg4", 2560, 1440), s("vc1", 2049, 1000),
        s("prores", 5000, 3000), _ffjson([{"codec_name": "h264"}]), _ffjson([]), "{}", "", "broken",
    ]
    return probe_cases(at._needs_cpu_decode, outputs)


def _packets_csv(rng, fps, seconds, *, start=0.0, noise=()):
    lines, n = [], int(fps * seconds)
    for i in range(n):
        pts = start + i / fps
        # «сцены»: сложность меняется каждые ~7 с
        size = int(rng.uniform(2_000, 9_000) * (1 + 3 * ((int(pts) // 7) % 3 == 1)))
        lines.append(f"{pts:.6f},{size}")
    for pos, line in noise:
        lines.insert(pos, line)
    return "\n".join(lines) + "\n"


@golden("source_bitrate", "Битрейт исходника по окнам 6 с (py:4036): ffprobe packet=pts_time,size")
def g_source_bitrate():
    rng = random.Random(4036)
    outputs = [
        _packets_csv(rng, 23.976, 40),
        _packets_csv(rng, 24, 30, start=-0.042, noise=[(5, "N/A,1234"), (10, ""), (20, "1.001000,N/A"), (30, "3.5,100,")]),
        _packets_csv(rng, 30, 20),
        _packets_csv(rng, 24, 13),  # 2 полных окна → мало
        "0.0,0\n6.0,0\n12.0,0\n18.0,0\n24.0,0\n",  # все нули
        "", "garbage\n",
        "0.000000,1000\n12.500000,3000\n3.000000,2000\n30.000000,500\n",  # пропуск окон и порядок B-кадров
    ]
    return probe_cases(at._analyze_source_bitrate, outputs)


# ─── HLS: команды и подбор качества ──────────────────────────────────────────

LADDER = [list(r) for r in at._HLS_RESOLUTIONS]
SMALL_LADDER = [["360p", 640, 360, 800_000], ["720p", 1280, 720, 3_000_000]]
FIXED_RC = [{"cq": at._HLS_FIXED_CQ, "maxrate": at._HLS_FIXED_CQ_PEAK * r[3]} for r in LADDER]
SMALL_FIXED_RC = [{"cq": at._HLS_FIXED_CQ, "maxrate": at._HLS_FIXED_CQ_PEAK * r[3]} for r in SMALL_LADDER]
CAL_RC = [{"cq": 23.456789, "maxrate": 2_000_000}, {"cq": 19.995, "maxrate": 3_750_000},
          {"cq": 14.005, "maxrate": 7_500_000}, {"cq": 40.0, "maxrate": 12_500_000},
          {"cq": 26.125, "maxrate": 20_000_000}, {"cq": 21.0049999, "maxrate": 40_000_000}]

WIN_IN = r"D:\anime\Sousou no Frieren\[SubsPlease] Sousou no Frieren - 01 (1080p).mkv"
WIN_RAM = r"L:\anitools_tmp\52991 - Sousou no Frieren\[SubsPlease] Sousou no Frieren - 01 (1080p)"
WIN_NEAR = r"D:\anime\Sousou no Frieren\hls_multi\52991 - Sousou no Frieren\[SubsPlease] Sousou no Frieren - 01 (1080p)"
POSIX_IN = "/mnt/anime/Провожающая/Серия 01.mkv"
POSIX_OUT = "/nonexistent/anitools_tmp/Провожающая/Серия 01"
CHOCO_FFMPEG = r"C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin\ffmpeg.exe"


@golden("hls_video_input_args", "Начало команды видео HLS: декодирование + split/scale_cuda (py:3932)")
def g_hls_input_args():
    widths = [r[1] for r in LADDER]
    variants = [
        ("windows", WIN_IN, "ffmpeg", widths, False, None), ("windows", WIN_IN, "ffmpeg", widths, True, None),
        ("windows", WIN_IN, CHOCO_FFMPEG, [640, 1280], False, [12.0, 6]),
        ("posix", POSIX_IN, "ffmpeg", [1920], True, [0.5, 6.0004]),
        ("posix", POSIX_IN, "ffmpeg", widths, False, [1234.5675, 6.0005]),
        ("windows", WIN_IN, "ffmpeg", [], False, None),
    ]
    cases = []
    for plat, path, ff, ws, cpu, seek in variants:
        p = PATH_TYPES[plat](path)
        cases.append({"input": {"platform": plat, "input_path": path, "ffmpeg_path": ff, "widths": ws,
                                "cpu_decode": cpu, "seek": seek},
                      **call(at._hls_video_input_args, p, ff, ws, cpu, tuple(seek) if seek else None)})
    return cases


@golden("hls_video_encode_args", "Параметры h264_nvenc для одного качества (py:3972)")
def g_hls_encode_args():
    variants = [(800_000, None), (16_000_000, None), (3_000_000, {"cq": 21.0, "maxrate": 12_000_000})]
    variants += [(1_500_000, rc) for rc in CAL_RC]
    variants += [(1, {"cq": 0.004999, "maxrate": 1}), (5_000_000, {"cq": 99.999, "maxrate": 0})]
    return [{"input": {"vbr": v, "rate_control": rc}, **call(at._hls_video_encode_args, v, rc)} for v, rc in variants]


@golden("hls_video_cmd", "Полная команда видео HLS на все качества (py:3999)")
def g_hls_video_cmd():
    variants = [
        ("windows", WIN_IN, WIN_RAM, "ffmpeg", LADDER, False, FIXED_RC),
        ("windows", WIN_IN, WIN_RAM, "ffmpeg", LADDER, True, FIXED_RC),
        ("windows", WIN_IN, WIN_NEAR, CHOCO_FFMPEG, LADDER, False, FIXED_RC),
        ("windows", WIN_IN, WIN_RAM, "ffmpeg", LADDER, False, CAL_RC),
        ("windows", WIN_IN, WIN_RAM, "ffmpeg", LADDER, False, None),
        ("posix", POSIX_IN, POSIX_OUT, "ffmpeg", SMALL_LADDER, False, SMALL_FIXED_RC),
        ("posix", POSIX_IN, POSIX_OUT, "/usr/bin/ffmpeg", SMALL_LADDER, True, None),
    ]
    cases = []
    for plat, inp, out, ff, ladder, cpu, rc in variants:
        P = PATH_TYPES[plat]
        cases.append({"input": {"platform": plat, "input_path": inp, "episode_out": out, "ffmpeg_path": ff,
                                "resolutions": ladder, "cpu_decode": cpu, "rate_control": rc},
                      **call(at._build_video_ffmpeg_cmd, P(inp), P(out), ff, [tuple(r) for r in ladder], cpu, rc)})
    return cases


@golden("hls_audio_cmd", "Команда аудио HLS: все озвучки одним вызовом, 5.1/7.1 → AAC 192k stereo (py:4208)")
def g_hls_audio_cmd():
    voices3 = [{"folder": "AniLibria.TV", "track_index": 0}, {"folder": "Оригинальная", "track_index": 1},
               {"folder": "DEEP", "track_index": 2}]
    variants = [
        ("windows", WIN_IN, WIN_RAM, voices3, [2, 6, 8], "[SubsPlease] Sousou no Frieren - 01 (1080p)"),
        ("windows", WIN_IN, WIN_NEAR, voices3, [], "Frieren - 01"),
        ("windows", WIN_IN, WIN_RAM, [{"folder": "DEEP", "track_index": 5}], [2, 6], "Ep"),
        ("windows", WIN_IN, WIN_RAM, [{"folder": "AniLibria.TV", "track_index": 1}, {"folder": "AniLibria.TV_2", "track_index": 0}],
         [8, 1], "Серия 01"),
        ("posix", POSIX_IN, POSIX_OUT, voices3, [0, 3, 6], "Серия 01"),
        ("posix", POSIX_IN, POSIX_OUT, [], [2], "Серия 01"),
    ]
    cases = []
    for plat, inp, out, voices, channels, ep in variants:
        P = PATH_TYPES[plat]
        with patched(_get_audio_channels=lambda _p, c=channels: list(c)):
            res = call(at._build_audio_ffmpeg_cmd, P(inp), P(out), voices, "ffmpeg", ep)
        cases.append({"input": {"platform": plat, "input_path": inp, "episode_out": out, "voices": voices,
                                "channels": channels, "ep_name": ep}, **res})
    return cases


@golden("hls_pick_calibration_windows", "Начала калибровочных окон, сек (py:4077)")
def g_pick_windows():
    rng = random.Random(4077)
    cases = []
    for n, count in [(1, 10), (2, 10), (3, 10), (9, 10), (10, 10), (11, 10), (25, 10), (240, 10), (240, 3), (7, 1)]:
        rates = [round(rng.uniform(1e6, 2e7), 3) for _ in range(n)]
        cases.append({"input": {"rates": rates, "count": count}, **call(at._pick_calibration_windows, rates, count)})
    ties = [5.0, 1.0, 5.0, 1.0, 3.0, 3.0, 5.0, 1.0, 3.0, 3.0, 5.0, 1.0]
    cases.append({"input": {"rates": ties, "count": 4}, **call(at._pick_calibration_windows, ties, 4)})
    cases.append({"input": {"rates": [0.0] * 12, "count": 10}, **call(at._pick_calibration_windows, [0.0] * 12, 10)})
    return cases


@golden("hls_next_cq", "Следующий CQ по замерам (py:4089); сравнивать с допуском 1e-9")
def g_next_cq():
    variants = [
        ([[26.0, 5_000_000.0]], 3_000_000), ([[26.0, 3_000_000.0]], 3_000_000), ([[26.0, 1_000_000.0]], 3_000_000),
        ([[26.0, 5e6], [29.0, 3.2e6]], 3_000_000), ([[26.0, 5e6], [26.05, 4.9e6]], 3_000_000),
        ([[26.0, 5e6], [28.0, 5e6]], 3_000_000), ([[26.0, 0.0], [28.0, 5e6]], 3_000_000),
        ([[26.0, 5e6], [27.0, 1e6]], 3_000_000), ([[26.0, 5e6], [36.0, 4.9e6]], 3_000_000),
        ([[20.0, 1e5]], 16_000_000), ([[30.0, 1e9]], 800_000), ([[14.0, 2e6], [14.0, 2e6]], 3e6),
        ([[26.0, 4e6], [24.0, 6e6], [25.0, 5.1e6]], 5_000_000), ([[26.0, 0.0]], 3_000_000),
    ]
    cases = []
    for history, target in variants:
        cases.append({"input": {"history": history, "target": target},
                      **call(at._next_hls_cq, [tuple(h) for h in history], target)})
    return cases


def _fake_encoder(model):
    """run_ffmpeg для калибровки: по команде считает размеры выходных .ts и кладёт их в FAKE_SIZES."""
    calls = []

    def run(cmd, *args, **kwargs):
        cmd = [str(c) for c in cmd]
        rc = model.returncode(len(calls))
        sizes = {}
        if rc == 0:
            start = float(cmd[cmd.index("-ss") + 1])
            cq = None
            for i, a in enumerate(cmd):
                if a == "-cq":
                    cq = float(cmd[i + 1])
                if a == "-f" and cmd[i + 1] == "mpegts":
                    out = cmd[i + 2]
                    res = PureWindowsPath(out).stem if "\\" in out else PurePosixPath(out).stem
                    sizes[out] = model(res, start, cq)
            FAKE_SIZES.update(sizes)
        calls.append({"cmd": cmd, "returncode": rc, "sizes": sizes})
        return rc

    return run, calls


class EncoderModel:
    """Детерминированная «видеокарта»: битрейт падает вдвое на каждые +6 CQ, сцены разной сложности."""

    BASE = {"360p": 950_000, "480p": 1_700_000, "720p": 3_300_000, "1080p": 5_200_000, "2K": 8_100_000,
            "4K": 15_000_000, "5K": 20_000_000}

    def __init__(self, scale=1.0, zero=(), fail_on=None):
        self.scale, self.zero, self.fail_on = scale, set(zero), fail_on

    def __call__(self, res, start, cq):
        if res in self.zero:
            return 0
        complexity = 0.55 + ((int(start) // 6) * 37 % 10) / 10
        bps = self.BASE[res] * self.scale * complexity * 2 ** ((26.0 - cq) / 6)
        return int(bps * at._HLS_SEGMENT_TIME / 8)

    def returncode(self, call_index):
        return 1 if self.fail_on is not None and call_index >= self.fail_on else 0


def _source(rng, n, lo=2e6, hi=1.2e7):
    rates = [round(rng.uniform(lo, hi), 1) for _ in range(n)]
    srt = sorted(rates)
    return {"rates": rates, "avg": sum(rates) / len(rates), "p99": srt[min(len(srt) - 1, int(0.99 * len(srt)))]}


@golden("hls_calibrate_cq", "Подбор CQ под серию (py:4101–4191): команды проходов с размерами и итог")
def g_calibrate_cq():
    rng = random.Random(4139)
    variants = [
        ("windows", WIN_IN, WIN_RAM, LADDER, False, _source(rng, 240), EncoderModel()),
        ("posix", POSIX_IN, POSIX_OUT, SMALL_LADDER, True, _source(rng, 60), EncoderModel(scale=1.7)),
        ("windows", WIN_IN, WIN_RAM, SMALL_LADDER, False, _source(rng, 5), EncoderModel(scale=0.01)),
        ("windows", WIN_IN, WIN_RAM, SMALL_LADDER, False, _source(rng, 30), EncoderModel(scale=60.0)),
        ("posix", POSIX_IN, POSIX_OUT, SMALL_LADDER, False, _source(rng, 30), EncoderModel(zero=["720p"])),
        ("windows", WIN_IN, WIN_RAM, LADDER, False, _source(rng, 100, 1e6, 1.5e6), EncoderModel(fail_on=0)),
        ("windows", WIN_IN, WIN_RAM, SMALL_LADDER, True, _source(rng, 50), EncoderModel(fail_on=12)),
    ]
    cases = []
    for plat, inp, out, ladder, cpu, source, model in variants:
        P = PATH_TYPES[plat]
        FAKE_SIZES.clear()
        run, calls = _fake_encoder(model)
        with patched(run_ffmpeg=run, shutil=types.SimpleNamespace(rmtree=lambda *a, **k: None)):
            res = call(at._calibrate_hls_cq, P(inp), "ffmpeg", [tuple(r) for r in ladder], source, cpu, P(out),
                       lambda text: None)
        cases.append({"input": {"platform": plat, "input_path": inp, "work_out": out, "ffmpeg_path": "ffmpeg",
                                "resolutions": ladder, "source": source, "cpu_decode": cpu},
                      "calls": calls, **res})
    FAKE_SIZES.clear()
    return cases


# ─── Запись ──────────────────────────────────────────────────────────────────

def render(name: str, description: str, sha: str, cases: list, extra: dict) -> str:
    head = {"function": name, "description": description, "source": REF_REL, "source_sha256": sha,
            "python": platform.python_version(), **extra}
    dumps = lambda v: json.dumps(v, ensure_ascii=False, allow_nan=False)  # noqa: E731
    lines = ["{"]
    for k, v in head.items():
        if isinstance(v, dict):  # фикстуры — по строке на ключ, чтобы читались в диффе
            lines.append(f"  {dumps(k)}: {{")
            lines.append(",\n".join(f"    {dumps(fk)}: {dumps(fv)}" for fk, fv in v.items()))
            lines.append("  },")
        else:
            lines.append(f"  {dumps(k)}: {dumps(v)},")
    lines.append('  "cases": [')
    lines.append(",\n".join("    " + dumps(c) for c in cases))
    lines.append("  ]")
    lines.append("}")
    return "\n".join(lines) + "\n"


def build() -> dict[str, str]:
    sha = source_sha256()
    files = {}
    for name, description, fn in GOLDENS:
        result = fn()
        cases, extra = result if isinstance(result, tuple) else (result, {})
        if not cases:
            raise SystemExit(f"{name}: нет ни одного случая")
        files[f"{name}.json"] = render(name, description, sha, cases, extra)
    return files


def strip_python_version(text: str) -> str:
    return "\n".join(line for line in text.split("\n") if not line.startswith('  "python": '))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true", help="только проверить, что эталоны актуальны")
    args = ap.parse_args()

    files = build()
    existing = {p.name for p in OUT.glob("*.json")} if OUT.exists() else set()

    if args.check:
        # Версия Python в шапке не считается расхождением — важны только ответы
        changed = [n for n, text in files.items()
                   if not (OUT / n).exists()
                   or strip_python_version((OUT / n).read_text(encoding="utf-8")) != strip_python_version(text)]
        stale = sorted(existing - files.keys())
        for n in changed:
            print(f"устарел: {n}")
        for n in stale:
            print(f"лишний: {n}")
        if changed or stale:
            print("Эталоны не совпадают с оригиналом — запусти: python3 tools/gen_golden.py")
            return 1
        print(f"Эталоны актуальны ({len(files)} файлов)")
        return 0

    OUT.mkdir(parents=True, exist_ok=True)
    for n, text in files.items():
        (OUT / n).write_text(text, encoding="utf-8", newline="\n")
    for n in existing - files.keys():
        (OUT / n).unlink()
    total = sum(text.count("\n    {") for text in files.values())
    print(f"Записано {len(files)} файлов, {total} случаев → {OUT.relative_to(ROOT).as_posix()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
