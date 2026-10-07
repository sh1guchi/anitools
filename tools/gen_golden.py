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
import os
import platform
import random
import signal
import sys
import types
import unicodedata
from pathlib import Path, PurePath, PurePosixPath, PureWindowsPath

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


def source_sha256(rel: str = REF_REL) -> str:
    return hashlib.sha256((ROOT / rel).read_bytes().replace(b"\r\n", b"\n")).hexdigest()


EXTRA_REL = "reference/python/extra"


def load_extra(file_name: str):
    """Соседний скрипт из reference/python/extra (импорт без запуска main)."""
    path = ROOT / EXTRA_REL / file_name
    name = "extra_" + "".join(c if c.isalnum() else "_" for c in path.stem)
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    if hasattr(mod, "terminate_all_processes"):
        atexit.unregister(mod.terminate_all_processes)
    return mod


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
    missing = object()
    old = {k: getattr(at, k, missing) for k in attrs}
    for k, v in attrs.items():
        setattr(at, k, v)
    try:
        yield
    finally:
        for k, v in old.items():
            if v is missing:
                delattr(at, k)  # без rich этих имён в модуле нет
            else:
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

GOLDENS: list[tuple[str, str, object, str]] = []


def golden(name: str, description: str, source: str = REF_REL):
    def deco(fn):
        GOLDENS.append((name, description, fn, source))
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


# ─── Сценарии операций: какие команды запускает оригинал ─────────────────────

class _Quiet:
    """Заглушка объектов rich (Panel, Table, Progress, колонки, box): всё молча принимает."""

    def __init__(self, *args, **kwargs):
        pass

    def __getattr__(self, name):
        return lambda *args, **kwargs: 0

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False


class _ScriptedPrompt:
    """Prompt.ask по сценарию: ответы по очереди; None — Enter (значение по умолчанию)."""

    answers: list = []

    @classmethod
    def ask(cls, prompt, *args, choices=None, default=None, **kwargs):
        if not cls.answers:
            raise RuntimeError(f"сценарий: нет ответа на вопрос «{prompt}»")
        answer = cls.answers.pop(0)
        if answer is None:
            answer = default if default is not None else ""
        if choices is not None and answer not in choices:
            raise RuntimeError(f"сценарий: ответ {answer!r} не из {choices} на «{prompt}»")
        return answer


SCENARIO_OPERATIONS = {
    "video_only": lambda root: at.keep_video_only(root, os.path.join(root, "Video only")),
    "audio_extract": lambda root: at.keep_audio_only(root, os.path.join(root, "Audio only")),
    "audio_mux": lambda root: at.advanced_audio_processing(root, os.path.join(root, "Processed Audio")),
    "subtitles": lambda root: at.extract_subtitles(root, os.path.join(root, "надписи")),
    "remux": lambda root: at.convert_mkv_to_mp4(root, os.path.join(root, "converted_mp4")),
}

F1, F2, F3 = "Test Show - 01.mkv", "Test Show - 02.mkv", "Test_Show_-_03.mp4"
FMOV, FSIL, FMKA, FEXT = "Resolve Export.mov", "Silent Show - 01.mkv", "Test Show - 01.mka", "1. Test Show - 01.AniLibria.TV.mka"

# files — файлы папки (пустые), existing — уже готовые выходы (не пустые), media — какой фикстурой отвечать
# на ffmpeg -i / ffprobe / mkvmerge по файлу, answers — ответы на вопросы оригинала по порядку,
# options — те же решения в виде настроек C# (оригиналу не нужны, по ним строит план C#-тест).
SCENARIOS = [
    {"name": "video_only", "operation": "video_only",
     "files": ["Test Show - 01.mkv", "Test_Show_-_02.mkv", "Test Show - 03.MKV", "notes.txt", "cover.jpg"],
     "existing": ["Video only/Test Show - 01.mkv"], "media": {}, "answers": [], "options": {}},

    {"name": "audio_one_track", "operation": "audio_extract",
     "files": ["Test Show - 01.mkv", "Test Show - 02.mkv"], "media": {"Test Show - 01.mkv": F1, "Test Show - 02.mkv": F1},
     "answers": ["2"], "options": {"tracks": [1]}},

    {"name": "audio_separate_numbered", "operation": "audio_extract",
     "files": ["Test Show - 01.mkv", "Test Show - 02.mkv"], "media": {"Test Show - 01.mkv": F1, "Test Show - 02.mkv": F1},
     "existing": ["Audio only/3. DEEP/3. Test Show - 02.DEEP.mka"],
     "answers": ["1,3", "1", "1"], "options": {"tracks": [0, 2], "mode": "separate", "number": True}},

    {"name": "audio_separate_untitled", "operation": "audio_extract",
     "files": ["Test_Show_-_02.mkv"], "media": {"Test_Show_-_02.mkv": F2},
     "answers": ["1-2", "1", "2"], "options": {"tracks": [0, 1], "mode": "separate", "number": False}},

    {"name": "audio_mov_without_titles", "operation": "audio_extract",
     "files": ["Resolve Export.mov"], "media": {"Resolve Export.mov": FMOV},
     "answers": ["1,2", "1", "2"], "options": {"tracks": [0, 1], "mode": "separate", "number": False}},

    {"name": "audio_single_mka_titles", "operation": "audio_extract",
     "files": ["Test Show - 01.mkv", "Test Show - 02.mkv", "Test Show - 03.mkv"],
     "media": {"Test Show - 01.mkv": F1, "Test Show - 02.mkv": F1, "Test Show - 03.mkv": F1},
     "existing": ["Audio only/Test Show - 02.mka"],
     "answers": ["3,1", "2", "1", "6", "0", "Своя озвучка", "1", None],
     "options": {"tracks": [2, 0], "mode": "single", "titles": {"2": "DEEP", "0": "Своя озвучка"}, "language": "rus"}},

    {"name": "audio_single_mka_plain", "operation": "audio_extract",
     "files": ["Show_-_05.mka"], "media": {"Show_-_05.mka": FMKA},
     "answers": ["1-2", "2", "2", "2"], "options": {"tracks": [0, 1], "mode": "single", "titles": {}, "language": None}},

    {"name": "mux_internal_only", "operation": "audio_mux",
     "files": ["Test Show - 01.mkv", "Test_Show_-_02.mkv"], "media": {"Test Show - 01.mkv": F1, "Test_Show_-_02.mkv": F1},
     "existing": ["Processed Audio/Test Show - 03.mkv"],
     "answers": ["3,1", "2", "2", "2", "1", None],
     "options": {"tracks": [2, 0], "external": False, "order": None, "titles": None, "language": "rus"}},

    {"name": "mux_with_external", "operation": "audio_mux",
     "files": ["Test Show - 01.mkv", "Test Show - 02.mkv", "Test Show - 01.mka", "Test Show - 02.mka",
               "Audio only/1. AniLibria.TV/1. Test Show - 01.AniLibria.TV.mka",
               "Audio only/1. AniLibria.TV/1. Test Show - 02.AniLibria.TV.mka",
               "Audio only/1. AniLibria.TV/1. Test Show - 011.AniLibria.TV.mka",
               "Processed Audio/old/Test Show - 01.x.mka", ".hidden/Test Show - 01.y.mka"],
     "media": {"Test Show - 01.mkv": F1, "Test Show - 02.mkv": F1, "Test Show - 01.mka": FMKA, "Test Show - 02.mka": FMKA,
               "Audio only/1. AniLibria.TV/1. Test Show - 01.AniLibria.TV.mka": FEXT,
               "Audio only/1. AniLibria.TV/1. Test Show - 02.AniLibria.TV.mka": FEXT},
     "answers": ["1", "1", "1", "4,1,3,2", "1", "1", "0", None, "4", "0", "  ", "1", None],
     "options": {"tracks": [0], "external": True, "order": [3, 0, 2, 1],
                 "titles": ["AniLiberty (AniLibria)", "AniLibria.TV", "Оригинальная", "AniLibria.TV"], "language": "rus"}},

    {"name": "mux_external_only_no_lang", "operation": "audio_mux",
     "files": ["Test Show - 01.mkv", "Audio only/1. AniLibria.TV/1. Test Show - 01.AniLibria.TV.mka"],
     "media": {"Test Show - 01.mkv": F1, "Audio only/1. AniLibria.TV/1. Test Show - 01.AniLibria.TV.mka": FEXT},
     "answers": [None, "1", "2", "2"],
     "options": {"tracks": [], "external": True, "order": None, "titles": None, "language": None}},

    {"name": "subs_by_id", "operation": "subtitles",
     "files": ["Test Show - 01.mkv", "Test_Show_-_02.mkv"], "media": {"Test Show - 01.mkv": F1, "Test_Show_-_02.mkv": F2},
     "answers": ["1", "1", "1"], "options": {"mode": "id", "ref": 4, "kind": "signs"}},

    {"name": "subs_by_title", "operation": "subtitles",
     "files": ["Test Show - 01.mkv", "Test Show - 02.mkv"], "media": {"Test Show - 01.mkv": F1, "Test Show - 02.mkv": F2},
     "existing": ["сабы/Test Show - 03.сабы.srt"],
     "answers": ["2", "2", "2"], "options": {"mode": "title", "ref": 5, "kind": "subs"}},

    {"name": "subs_by_language", "operation": "subtitles",
     "files": ["Test Show - 01.mkv", "Test Show - 02.mkv"], "media": {"Test Show - 01.mkv": F1, "Test Show - 02.mkv": F2},
     "answers": ["3", "1", "1"], "options": {"mode": "lang", "ref": 4, "kind": "signs"}},

    {"name": "subs_track_missing", "operation": "subtitles",
     "files": ["Test Show - 01.mkv", "Test Show - 02.mkv"], "media": {"Test Show - 01.mkv": F1, "Test Show - 02.mkv": FSIL},
     "existing": ["надписи/Test Show - 01.надписи.ass"],
     "answers": ["2", "1", "1"], "options": {"mode": "title", "ref": 4, "kind": "signs"}},

    {"name": "remux_mp4", "operation": "remux",
     "files": ["Test Show - 01.mkv", "Test_Show_-_03.mp4", "clip.webm", "Silent Show - 01.mkv", "notes.txt"],
     "existing": ["converted_mp4/clip.mp4"], "media": {}, "answers": [None], "options": {"format": "mp4"}},

    {"name": "remux_mkv_with_subs", "operation": "remux",
     "files": ["Test Show - 01.mkv", "Test_Show_-_03.mp4"], "media": {}, "answers": ["2", None],
     "options": {"format": "mkv", "subtitles": True}},

    {"name": "remux_mkv_without_subs", "operation": "remux",
     "files": ["Test Show - 01.mkv"], "media": {}, "answers": ["2", "2"], "options": {"format": "mkv", "subtitles": False}},
]


def run_scenario(sc: dict) -> dict:
    import tempfile
    with tempfile.TemporaryDirectory(prefix="anitools-scenario-") as td:
        root = os.path.realpath(td)
        for rel in sc["files"]:
            p = Path(root, *rel.split("/"))
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"")
        for rel in sc.get("existing", []):
            p = Path(root, *rel.split("/"))
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"done")

        def fixture(path: str, suffix: str) -> str:
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            if rel not in sc["media"]:
                raise RuntimeError(f"сценарий {sc['name']}: оригинал читает {rel}, а фикстуры для него нет")
            return (MEDIA / f"{sc['media'][rel]}.{suffix}").read_text(encoding="utf-8")

        commands = []

        def handler(cmd):
            cmd = [str(c) for c in cmd]
            tool, rest = cmd[0], cmd[1:]
            if tool == "ffmpeg" and len(rest) == 2 and rest[0] == "-i":
                return "", fixture(rest[1], "ffmpeg_i.txt")
            if tool == "ffprobe":
                return fixture(rest[-1], "ffprobe_titles.json"), ""
            if tool == "mkvmerge" and rest[:1] == ["-J"]:
                return fixture(rest[1], "mkvmerge.json"), ""
            if tool in ("mkvmerge", "mkvextract") and rest == ["--version"]:
                return "", ""
            # запасные способы найти субтитры (mkvmerge -i, mkvinfo) — зондирование, а не работа
            if (tool == "mkvmerge" and rest[:1] == ["-i"]) or tool == "mkvinfo":
                return "", ""
            commands.append([c.replace(root, "{root}") for c in cmd])
            return "", ""

        real_listdir = os.listdir
        os.listdir = lambda p=".": sorted(real_listdir(p), key=str.upper)  # как на NTFS: без учёта регистра
        _ScriptedPrompt.answers = list(sc["answers"])
        try:
            with patched(subprocess=fake_subprocess(handler, []), Prompt=_ScriptedPrompt, Panel=_Quiet, Table=_Quiet,
                         Progress=_Quiet, SpinnerColumn=_Quiet, TextColumn=_Quiet, BarColumn=_Quiet,
                         TimeRemainingColumn=_Quiet, box=_Quiet(), clear_screen=lambda: None,
                         restart_script=lambda: None):
                SCENARIO_OPERATIONS[sc["operation"]](root)
        finally:
            os.listdir = real_listdir
        if _ScriptedPrompt.answers:
            raise RuntimeError(f"сценарий {sc['name']}: лишние ответы {_ScriptedPrompt.answers}")
        return {"commands": commands}


@golden("operation_scenarios", "Операции 1–4, 6: команды, которые запускает оригинал для папки и ответов на вопросы")
def g_operation_scenarios():
    cases = []
    for sc in SCENARIOS:
        inp = {k: sc.get(k, [] if k == "existing" else None) for k in ("name", "operation", "files", "existing", "media", "answers", "options")}
        cases.append({"input": inp, "output": run_scenario(sc)})
    return cases


# ─── Сценарии п.5 «Переименовать файлы» ──────────────────────────────────────

FRIEREN = [
    "[SubsPlease] Sousou no Frieren - 01 (1080p) [F02B9CB4].mkv",
    "[SubsPlease] Sousou no Frieren - 02 (1080p) [A1B2C3D4].mkv",
    "[SubsPlease] Sousou no Frieren - 10 (1080p) [ABCDEF12].mkv",
]

# answers — ответы оригиналу; options — то же для C#: base (базовое название, как ввели), start, suffix,
# manual — номера, введённые вручную (файл → текст; null — Enter, то есть предложенный номер)
RENAME_SCENARIOS = [
    {"name": "manual_base", "files": [*FRIEREN, "notes.txt", "run.bat", "Cover.jpg"],
     "answers": ["2", "Sousou no Frieren: Beyond Journey's End", None, "2", "1"],
     "options": {"base": "Sousou no Frieren: Beyond Journey's End", "start": 1, "suffix": None, "manual": None}},
    {"name": "offset_and_suffix", "files": ["Title - 12.надписи.ass", "Title - 13.надписи.ass", "Title - 11.надписи.ass"],
     "answers": ["2", "Overlord IV", "12", "1", ".надписи", "1"],
     "options": {"base": "Overlord IV", "start": 12, "suffix": ".надписи", "manual": None}},
    {"name": "shikimori_original_name", "shikimori": "frieren",
     "files": ["[Erai-raws] Sousou no Frieren - 01 [1080p][Multiple Subtitle][F1E2D3C4].mkv",
               "[Erai-raws] Sousou no Frieren - 02 [1080p][Multiple Subtitle][0A1B2C3D].mkv", "Fonts.zip"],
     "answers": ["1", "1", None, None, "2", "1"],
     "options": {"base": "Sousou no Frieren", "start": 1, "suffix": None, "manual": None}},
    {"name": "manual_numbers", "files": ["Ep 1.mkv", "Ep 2.mkv", "Bonus.mkv", "Ep 4.надписи.ass"],
     "answers": ["2", "Show", None, "2", "2", "5", None, "abc", None, "1"],
     "options": {"base": "Show", "start": 1, "suffix": None,
                 "manual": {"Bonus.mkv": "5", "Ep 1.mkv": None, "Ep 2.mkv": "abc", "Ep 4.надписи.ass": None}}},
    {"name": "target_exists", "files": ["A - 01.mkv", "Show - 01.mkv", "b - 02.mkv"],
     "answers": ["2", "Show", None, "2", "1"],
     "options": {"base": "Show", "start": 1, "suffix": None, "manual": None}},
    {"name": "no_numbers_manual", "files": ["Movie.mkv", "Extra.mkv"],
     "answers": ["2", "Film", None, "2", "1", "2", "1", "1"],
     "options": {"base": "Film", "start": 1, "suffix": None, "manual": {"Extra.mkv": "2", "Movie.mkv": "1"}}},
]


def run_rename_scenario(sc: dict) -> dict:
    import tempfile
    with tempfile.TemporaryDirectory(prefix="anitools-rename-") as td:
        root = os.path.realpath(td)
        for name in sc["files"]:
            Path(root, name).write_bytes(b"x")
        renames, paths = [], []
        real_rename = os.rename

        def rename(src, dst):
            pair = [os.path.relpath(src, root), os.path.relpath(dst, root)]
            if pair[0] != pair[1]:
                renames.append(pair)
            real_rename(src, dst)

        fixture = SHIKI_FIXTURES.get(sc.get("shikimori", ""), [])

        def shiki_get(path, tries=3):
            paths.append(path)
            if path.startswith("animes/"):
                return next((a for a in fixture if f"animes/{a['id']}" == path), None)
            return fixture

        os.rename = rename
        _ScriptedPrompt.answers = list(sc["answers"])
        try:
            with patched(Prompt=_ScriptedPrompt, Panel=_Quiet, Table=_Quiet, Progress=_Quiet, SpinnerColumn=_Quiet,
                         TextColumn=_Quiet, BarColumn=_Quiet, TimeRemainingColumn=_Quiet, box=_Quiet(),
                         clear_screen=lambda: None, restart_script=lambda: None, _shiki_get=shiki_get):
                at.rename_files_by_pattern(root)
        finally:
            os.rename = real_rename
        if _ScriptedPrompt.answers:
            raise RuntimeError(f"сценарий {sc['name']}: лишние ответы {_ScriptedPrompt.answers}")
        return {"renames": renames, "shikimori_paths": paths}


@golden("rename_scenarios", "П.5: какие файлы и как переименовывает оригинал для папки и ответов на вопросы")
def g_rename_scenarios():
    cases = []
    for sc in RENAME_SCENARIOS:
        inp = {k: sc.get(k) for k in ("name", "files", "shikimori", "answers", "options")}
        cases.append({"input": inp, "output": run_rename_scenario(sc)})
    return cases


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


# ─── Сценарии п.7 «HLS» ──────────────────────────────────────────────────────

def _probe_reply(full: dict, cmd: list[str]) -> str:
    """Ответ ffprobe на запрос п.7 — из полного JSON фикстуры (того же, что читает C#)."""
    streams = full.get("streams", [])
    audio = [s for s in streams if s.get("codec_type") == "audio"]
    video = [s for s in streams if s.get("codec_type") == "video"][:1]
    entries = cmd[cmd.index("-show_entries") + 1]
    if entries == "stream=index:stream_tags=title,language":
        out = []
        for s in audio:
            tags = {k: v for k, v in (s.get("tags") or {}).items() if k in ("title", "language")}
            out.append({"index": s["index"], **({"tags": tags} if tags else {})})
        return _ffjson(out)
    if entries == "stream=codec_name,width,height":
        return _ffjson([{k: s[k] for k in ("codec_name", "width", "height") if k in s} for s in video])
    if entries == "stream=width":
        return "".join(f"{s['width']}\n" for s in video)
    if entries == "format=duration":
        return f"{full['format']['duration']}\n"
    if entries == "stream=channels":
        return "".join(f"{s.get('channels', '')}\n" for s in audio)
    raise RuntimeError(f"неизвестный запрос ffprobe: {cmd}")


class _ZipRecorder:
    """zipfile.ZipFile для сценариев: запоминает имена записей, на диск кладёт маленький файл вместо архива."""

    root = ""
    zips: dict = {}

    def __init__(self, path, mode="r", compression=None):
        if mode != "w" or compression != 0:
            raise RuntimeError(f"неожиданный zip: mode={mode} compression={compression}")
        self.path, self.names = Path(path), []

    def write(self, filename, arcname=None):
        self.names.append(str(arcname).replace(os.sep, "/"))

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.path.write_bytes(b"zip")
        _ZipRecorder.zips[os.path.relpath(self.path, _ZipRecorder.root).replace(os.sep, "/")] = sorted(self.names)
        return False


def _windows_sorted(iterable, *, key=None, reverse=False):
    """sorted() оригинала на Windows: пути (PureWindowsPath) сравниваются по частям в нижнем регистре."""
    items = list(iterable)
    if key is None and items and all(isinstance(x, PurePath) for x in items):
        key = lambda p: [part.lower() for part in p.parts]  # noqa: E731
    return sorted(items, key=key, reverse=reverse)


FRIEREN_EP = "[SubsPlease] Sousou no Frieren - {} (1080p).mkv"

# files — файлы папки (пустые), existing — уже готовые выходы, media — фикстура ffprobe по файлу (video — подмена
# полей видеопотока), shikimori — ответ поиска, answers — ответы на вопросы оригинала ({root} — папка сценария),
# ffmpeg_fail — номера вызовов ffmpeg с кодом 1, top_bytes — размер сегмента верхнего качества (разреженный файл),
# options — те же решения для C#: группы (title, files при перегруппировке, shikimori_id, озвучки по раскладкам) и
# куда писать временные файлы (near — рядом с выходом, folder — {root}/work).
HLS_SCENARIOS = [
    {"name": "frieren_near",
     "files": [FRIEREN_EP.format("01"), FRIEREN_EP.format("02"), "notes.txt"],
     "media": {FRIEREN_EP.format("01"): F1, FRIEREN_EP.format("02"): F1}, "shikimori": "frieren",
     "answers": ["1", "1", "1", "5", "-", "6", "1", "2", "3"],
     "options": {"groups": [{"title": "Sousou no Frieren", "shikimori_id": 52991,
                             "layouts": [[[0, "AniLibria.TV"], [1, None], [2, "DEEP"]]]}], "work": "near"}},

    {"name": "layouts_and_work_folder",
     "files": ["Kaiju - 01.mkv", "Kaiju - 02.mkv", "Kaiju - 03.mp4", "Kaiju - 04.mkv", "Kaiju - 05.mkv"],
     "media": {"Kaiju - 01.mkv": F1, "Kaiju - 02.mkv": F2, "Kaiju - 03.mp4": F3, "Kaiju - 04.mkv": FSIL, "Kaiju - 05.mkv": F1},
     "shikimori": "empty",
     "answers": ["1", "1", "12345", "1", "0", "Оригинал / JP", "6", "1", "0", None, "-", "1", "2", "2", "{root}/work"],
     "options": {"groups": [{"title": "Kaiju", "shikimori_id": 12345,
                             "layouts": [[[0, "AniLiberty (AniLibria)"], [1, "Оригинал / JP"], [2, "DEEP"]],
                                         [[0, "AniLiberty (AniLibria)"], [1, "jpn"]],
                                         [[0, None]]]}], "work": "folder"}},

    {"name": "resume_retry_and_failure",
     "files": ["Kaiju - 01.mkv", "Kaiju - 02.mkv", "Kaiju - 03.mkv"],
     "existing": ["hls_multi/Kaiju/Kaiju - 01.zip", "hls_multi/Kaiju/audio/AniLibria.TV/Kaiju - 01.AniLibria.TV.mka"],
     "media": {"Kaiju - 01.mkv": F2, "Kaiju - 02.mkv": F2, "Kaiju - 03.mkv": F2}, "shikimori": "empty",
     "answers": ["1", "1", None, "5", "-", "1", "2", "3"], "ffmpeg_fail": [0, 4],
     "options": {"groups": [{"title": "Kaiju", "shikimori_id": None, "layouts": [[[0, "AniLibria.TV"], [1, None]]]}],
                 "work": "near"}},

    {"name": "regroup_and_same_names",
     "files": ["A - 01.mkv", "A - 01.mp4", "B - 01.mkv"],
     "media": {"A - 01.mkv": F3, "A - 01.mp4": F3, "B - 01.mkv": F3}, "shikimori": "rezero",
     "answers": ["2", "Re:Zero", "1,2", None, "1", None, "1", "0", "31240", "0", "Рус", "2", "1", "2", "3"],
     "options": {"groups": [{"title": "Re:Zero", "files": ["A - 01.mkv", "A - 01.mp4"], "shikimori_id": 31240,
                             "layouts": [[[0, "Рус"]]]}], "work": "near"}},

    {"name": "separate_top_zip_and_wide_source",
     "files": ["Big - 01.mkv"], "media": {"Big - 01.mkv": F1}, "video": {"Big - 01.mkv": {"width": 5120, "height": 2880}},
     "shikimori": "empty", "answers": ["1", "1", None, "5", "-", "-", "1", "2", "3"], "top_bytes": 7 * 1024 ** 3 + 1,
     "options": {"groups": [{"title": "Big", "shikimori_id": None, "layouts": [[[0, "AniLibria.TV"], [1, None], [2, None]]]}],
                 "work": "near", "separate_top_zip": True}},

    {"name": "bracket_names_order",
     "files": ["Hellsing Ultimate OVA 01.mkv", "[Group] Kaiju - 01.mkv", "_Kaiju - 02.mkv"],
     "media": {"Hellsing Ultimate OVA 01.mkv": F3, "[Group] Kaiju - 01.mkv": F3, "_Kaiju - 02.mkv": F3}, "shikimori": "empty",
     "answers": ["1", "1", None, "0", None, "1", None, "0", None, "1", "2", "3"],
     "options": {"groups": [{"title": "Kaiju", "shikimori_id": None, "layouts": [[[0, "rus"]]]},
                            {"title": "Hellsing Ultimate OVA", "shikimori_id": None, "layouts": [[[0, "rus"]]]}],
                 "work": "near"}},
]


def run_hls_scenario(sc: dict) -> dict:
    import tempfile
    with tempfile.TemporaryDirectory(prefix="anitools-hls-") as td:
        root = os.path.realpath(td)
        for rel in sc["files"]:
            Path(root, rel).write_bytes(b"")
        for rel in sc.get("existing", []):
            p = Path(root, *rel.split("/"))
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"done")

        def probe(cmd):
            cmd = [str(c) for c in cmd]
            rel = os.path.relpath(cmd[-1], root).replace(os.sep, "/")
            full = json.loads((MEDIA / f"{sc['media'][rel]}.ffprobe.json").read_text(encoding="utf-8"))
            for s in full["streams"]:
                if s.get("codec_type") == "video":
                    s.update(sc.get("video", {}).get(rel, {}))
            if cmd[0] != "ffprobe":
                raise RuntimeError(f"сценарий {sc['name']}: неожиданный запуск {cmd}")
            return _probe_reply(full, cmd), ""

        commands, statuses, fails = [], [], set(sc.get("ffmpeg_fail", []))

        def run_ffmpeg(cmd, *args, **kwargs):
            cmd = [str(c) for c in cmd]
            code = 1 if len(commands) in fails else 0
            commands.append([c.replace(root, "{root}") for c in cmd])
            for i, a in enumerate(cmd):
                if a == "-hls_segment_filename":
                    seg_dir = Path(cmd[i + 1]).parent
                    with open(seg_dir / "seg000.ts", "wb") as f:  # недописанный выход и при ошибке
                        f.truncate(sc["top_bytes"] if "top_bytes" in sc and seg_dir.name == "4K" else 100)
                    if code == 0:
                        (seg_dir / "seg001.ts").write_bytes(b"\0" * 100)
                        Path(cmd[i + 2]).write_text("#EXTM3U\n", encoding="utf-8")
                elif a.endswith(".mka") and code == 0:
                    Path(a).write_text("mka", encoding="utf-8")
            return code

        real_process = at._process_episode_multi_res

        def process(*args, **kwargs):
            status = real_process(*args, **kwargs)
            statuses.append(status)
            return status

        fixture = SHIKI_FIXTURES[sc["shikimori"]]
        _ZipRecorder.root, _ZipRecorder.zips = root, {}
        _ScriptedPrompt.answers = [a.replace("{root}", root) if isinstance(a, str) else a for a in sc["answers"]]
        try:
            with patched(subprocess=fake_subprocess(probe, []), Prompt=_ScriptedPrompt, Panel=_Quiet, Table=_Quiet,
                         Progress=_Quiet, SpinnerColumn=_Quiet, TextColumn=_Quiet, BarColumn=_Quiet,
                         TimeRemainingColumn=_Quiet, box=_Quiet(), clear_screen=lambda: None,
                         restart_script=lambda: None, run_ffmpeg=run_ffmpeg, _process_episode_multi_res=process,
                         _shiki_get=lambda path, tries=3: fixture, _cleanup_orphan_ramdisks=lambda: None,
                         zipfile=types.SimpleNamespace(ZipFile=_ZipRecorder, ZIP_STORED=0), sorted=_windows_sorted,
                         FFMPEG_PATH="ffmpeg", FFPROBE_PATH="ffprobe", ANITOOLS_WORK_DIR=""):
                at.convert_videos_multi_res(Path(root))
        finally:
            at.ANITOOLS_WORK_DIR = ""
        if _ScriptedPrompt.answers:
            raise RuntimeError(f"сценарий {sc['name']}: лишние ответы {_ScriptedPrompt.answers}")
        files = sorted(os.path.relpath(os.path.join(d, f), root).replace(os.sep, "/")
                       for d, _, names in os.walk(root) for f in names)
        return {"commands": commands, "statuses": statuses, "zips": _ZipRecorder.zips, "files": files}


@golden("hls_scenarios", "П.7: команды, архивы и итоговые файлы оригинала для папки и ответов на вопросы")
def g_hls_scenarios():
    cases = []
    for sc in HLS_SCENARIOS:
        inp = {k: sc.get(k) for k in ("name", "files", "existing", "media", "video", "shikimori", "answers",
                                      "ffmpeg_fail", "top_bytes", "options")}
        cases.append({"input": inp, "output": run_hls_scenario(sc)})
    return cases


# ─── Соседние скрипты (reference/python/extra), этап 6 ────────────────────────

SUBDELAY_REL = f"{EXTRA_REL}/subtitle_delay+1s.py"
STYLES_REL = f"{EXTRA_REL}/edit_styles.py"
HARDSUB_REL = f"{EXTRA_REL}/hardsub.py"
MKA_REL = f"{EXTRA_REL}/mka_muxer.py"
XFONTS_REL = f"{EXTRA_REL}/extract_fonts.py"
DECOD_REL = f"{EXTRA_REL}/audio_decod.py"
CUT_REL = f"{EXTRA_REL}/delay-1s.py"

subdelay = load_extra("subtitle_delay+1s.py")
styles = load_extra("edit_styles.py")
hardsub = load_extra("hardsub.py")
mka = load_extra("mka_muxer.py")
xfonts = load_extra("extract_fonts.py")
decod = load_extra("audio_decod.py")
cut = load_extra("delay-1s.py")


@golden("sub_shift_times", "Сдвиг одной метки времени SRT/ASS (subtitle_delay+1s.py: shift_time_srt/ass)", SUBDELAY_REL)
def g_sub_shift_times():
    srt = ["00:00:01,000", "00:00:00,500", "01:59:59,999", "99:59:59,999", "00:00:00,000", "00:00:01.000", "1:2:3,4"]
    ass = ["0:00:01.00", "0:00:00.50", "9:59:59.99", "1:2:3.4", "0:00:01,00"]
    shifts = [1.0, -1.0, -2.5, 0.0015, -0.0015, 0.015, -0.015, 0.009, 3600.0, 0.1 + 0.2]
    cases = []
    for kind, times, fn in (("srt", srt, subdelay.shift_time_srt), ("ass", ass, subdelay.shift_time_ass)):
        for t in times:
            for sh in shifts:
                cases.append({"input": {"kind": kind, "time": t, "shift": sh}, **call(fn, t, sh)})
    return cases


SRT_SAMPLE = ("1\n00:00:01,000 --> 00:00:02,500\nПривет\n\n2\n00:00:03,000-->00:00:04,000\nМир\n\n"
              "3\n00:00:00,200   -->   00:00:00,900\nРано\n\n4\n00:00:05,000 --> 00:00:06,000 X1:10 Y1:20\nКоорд\n")
ASS_SAMPLE = ("\ufeff[Script Info]\nScriptType: v4.00+\n\n[Events]\n"
              "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
              "Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,Текст, с, запятыми\n"
              "Comment: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Комментарий не сдвигается\n"
              "Dialogue: 10,0:00:00.20,0:00:00.90,Signs,,0,0,0,,{\\pos(10,20)}Надпись\n"
              "Dialogue: 0,10:00:00.00,10:00:01.00,Default,,0,0,0,,Десять часов не совпадают с шаблоном\n"
              "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Dialogue: 0,0:00:05.00,0:00:06.00,внутри\n")


@golden("sub_shift_text", "Сдвиг файла субтитров целиком (subtitle_delay+1s.py: process_srt/process_ass), вход с LF", SUBDELAY_REL)
def g_sub_shift_text():
    import tempfile
    cases = []
    for kind, text in (("srt", SRT_SAMPLE), ("ass", ASS_SAMPLE)):
        for sh in (1.0, -1.0, 0.25):
            with tempfile.TemporaryDirectory() as td:
                src, dst = Path(td, "in." + kind), Path(td, "out." + kind)
                src.write_text(text, encoding="utf-8")
                (subdelay.process_srt if kind == "srt" else subdelay.process_ass)(src, dst, sh)
                cases.append({"input": {"kind": kind, "text": text, "shift": sh}, "output": dst.read_bytes().decode("utf-8")})
    return cases


ASS_FILES = {
    "Ep 01.ass": "[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,Default,Хару,0,0,0,,Привет\n"
                 "Dialogue: 0,0:00:02.00,0:00:03.00,Signs,,0,0,0,,Надпись\n"
                 "Comment: 0,0:00:02.00,0:00:03.00,Signs,,0,0,0,,Комментарий\n"
                 "Dialogue: 0,0:00:03.00,0:00:04.00, Default ,Мико,0,0,0,,Пробелы вокруг стиля\n",
    "ep 02.ASS": "\ufeff[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,OP,,0,0,0,,Опенинг\n"
                 "Dialogue: 0,0:00:01.00,0:00:02.00,Default,Хару,0,0,0,,Снова\n"
                 "Dialogue: 0,0:00:01.00\n",
    "A.ass": "[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,ED,Хор,0,0,0,,Эндинг",
    "notes.txt": "Dialogue: 0,0:00:01.00,0:00:02.00,Txt,,0,0,0,,не .ass\n",
}


@golden("ass_style_values", "Чистка .ass (edit_styles.py): значения поля с примером файла и файлы после удаления строк", STYLES_REL)
def g_ass_style_values():
    import io
    import tempfile
    cases = []
    real_listdir = os.listdir
    for field, remove in ((3, ["Signs", "OP"]), (4, ["", "Хор"]), (3, [])):
        with tempfile.TemporaryDirectory() as td:
            for name, text in ASS_FILES.items():
                Path(td, name).write_text(text, encoding="utf-8")
            styles.os = types.SimpleNamespace(listdir=lambda p: sorted(real_listdir(p), key=str.upper), path=os.path)
            try:
                values, examples, files = styles.get_all_values(td, field)
                with contextlib.redirect_stdout(io.StringIO()):
                    styles.remove_lines(td, files, field, set(remove))
            finally:
                styles.os = os
            after = {name: Path(td, name).read_bytes().decode("utf-8") for name in files}
        cases.append({"input": {"files": ASS_FILES, "field": field, "remove": remove},
                      "output": {"values": values, "examples": examples, "files": after}})
    return cases


@golden("escape_filter", "Путь для фильтра subtitles в хардсабе (hardsub.py: escape_filter)", HARDSUB_REL)
def g_escape_filter():
    inputs = ["Ep 01.ass", "Fonts", r"C:\anime\[Group] Show - 01.ass", "a'b,c;d[e]f:g.ass", r"back\slash", "Тайтл, серия 1.ass", ""]
    return cases_for(hardsub.escape_filter, inputs)


def _audio_only_json(full: dict) -> str:
    keep = ("index", "codec_name", "channels", "channel_layout", "disposition", "tags")
    streams = [{k: s[k] for k in keep if k in s} for s in full.get("streams", []) if s.get("codec_type") == "audio"]
    return json.dumps({"programs": [], "streams": streams}, ensure_ascii=False, indent=4)


TRACK_JSON = {
    "Своя раскладка": {"streams": [
        {"index": 0, "codec_type": "audio", "codec_name": "aac", "channels": 2, "channel_layout": "stereo",
         "disposition": {"default": 1}, "tags": {"title": "AniLibria.TV", "language": "rus"}},
        {"index": 1, "codec_type": "audio", "codec_name": "ac3", "channels": 6, "disposition": {"default": 0},
         "tags": {"title": "", "language": "jpn"}},
        {"index": 2, "codec_type": "audio", "codec_name": "opus", "disposition": {}, "tags": {}},
    ]},
}


@golden("track_rows", "Дорожки файла (mka_muxer.py: probe_tracks) и списки для копирования (print_copy_block 1/2/3)", MKA_REL)
def g_track_rows():
    import io
    cases = []
    sources = {name: json.loads((MEDIA / f"{name}.ffprobe.json").read_text(encoding="utf-8"))
               for name in ("Test Show - 01.mkv", "Test Show - 02.mkv", "Resolve Export.mov", "Silent Show - 01.mkv")}
    sources.update(TRACK_JSON)
    for name, full in sources.items():
        reply = _audio_only_json(full)
        with patched_module(mka, subprocess=fake_subprocess(lambda cmd, r=reply: (r, ""), [])):
            tracks = mka.probe_tracks("x.mka")
        blocks = {}
        for fmt in ("1", "2", "3"):
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                mka.print_copy_block(tracks, fmt)
            blocks[fmt] = out.getvalue()
        cases.append({"input": {"name": name, "ffprobe": full}, "output": {"tracks": tracks, "copy": blocks}})
    return cases


@golden("font_attachments", "Шрифт ли вложение mkvmerge (extract_fonts.py: is_font)", XFONTS_REL)
def g_font_attachments():
    inputs = [
        {"file_name": "Arial.TTF", "content_type": "application/x-truetype-font"},
        {"file_name": "a.otf", "content_type": "application/vnd.ms-opentype"},
        {"file_name": "font.woff2", "content_type": "application/octet-stream"},
        {"file_name": "cover.jpg", "content_type": "image/jpeg"},
        {"file_name": "noext", "content_type": "font/ttf"},
        {"file_name": "x.EOT", "content_type": ""},
        {"file_name": "x.ttc", "content_type": "application/octet-stream"},
        {"file_name": "", "content_type": "FONT/SFNT"},
        {"content_type": "font/otf"},
        {"file_name": "a.woff"},
    ]
    return cases_for(xfonts.is_font, inputs)


@golden("audio_tool_commands", "Команды перекодирования аудио (audio_decod.py: все форматы) и обрезки начала (delay-1s.py)", DECOD_REL)
def g_audio_tool_commands():
    cases = []
    for fmt, (ext, codec) in decod.CODEC_MAP.items():
        calls = []
        with patched_module(decod, subprocess=fake_subprocess(lambda cmd: ("", ""), calls), OUTDIR=Path("converted")):
            decod.process_file(Path("Track 01.flac"), "ffmpeg", ext, codec)
        cases.append({"input": {"tool": "audio_decod", "format": fmt, "source": "Track 01.flac"}, "output": calls})
    calls = []
    with patched_module(cut, subprocess=fake_subprocess(lambda cmd: ("", ""), calls), OUTDIR=Path("audio_fixed")):
        cut.process_file(Path("Track 01.flac"), "ffmpeg")
    cases.append({"input": {"tool": "delay-1s", "source": "Track 01.flac", "seconds": cut.CUT_SECONDS}, "output": calls})
    return cases


# mka_muxer.py без пакета anitopy в C# опирается на наш Anitomy (docs/PLAN.md §2.9.1): эталоны снимаются так же —
# вместо anitopy подставляется встроенный anitomy оригинала anitools.py
mka.anitopy = types.SimpleNamespace(parse=at.anitomy_parse)
mka.ANITOPY_AVAILABLE = True

MKA_NAMES = [
    "1. Show - 01.AniLibria.TV.mka", "12. Show - 01.DEEP.mka", "Show - 01 [AniDUB].mka", "Show Name - 05 (JAM).mka",
    "[Group] Sousou no Frieren - 01 (1080p).mka", "Sousou no Frieren - 01.AniLiberty (AniLibria).mka", "Show_12_RUS.ac3",
    "Show E07.mp3", "Movie [AniDUB].mka", "Movie.flac", "1. Movie.flac", "2.Show - 02.x.mka", "Show - 1001.mka",
    "Show ep 3.opus", "Show - 01v2.mka", "Show [03].mka", "Show - 01 - 02.mka", "Re꞉Zero - 01.mka", "Show: Part 2 - 03.mka",
    "a<b>c - 01.mka", "Шоу - 01.Студия.mka", "   .mka", "10. 10.mka",
]


def _natural(name):
    num, low = mka._natural_key(name)
    return [None if num == float("inf") else num, low]


@golden("mka_names", "Имена в сборке .mka (mka_muxer.py): префикс «N. », номер серии, база имени выхода, ключ сортировки", MKA_REL)
def g_mka_names():
    cases = []
    for name in MKA_NAMES:
        out = {"strip_track_num": mka.strip_track_num(name), "track_num": mka.track_num(name),
               "parse_episode": mka.parse_episode(name), "episode_base": mka.episode_base(name), "natural_key": _natural(name)}
        cases.append({"input": name, "output": out})
    return cases


@golden("mka_strip_trailing_episode", "Хвостовой номер серии в метке озвучки (mka_muxer.py: _strip_trailing_episode)", MKA_REL)
def g_mka_strip_trailing():
    pairs = [("AniFilm 01", "01"), ("AniFilm 01", "02"), ("Studio 2x2", "02"), ("AniFilm - 1", "01"), ("AniFilm_007", "07"),
             ("AniFilm.01", "1"), ("01", "01"), ("AniFilm 01", None), ("AniFilm 01", "abc"), (" AniFilm 12 ", "12"),
             ("", "01"), (None, "01"), ("AniFilm 0", "00"), ("Dub 2024", "2024"), ("AniFilm 01 ", "1")]
    return [{"input": {"label": l, "ep": e}, **call(mka._strip_trailing_episode, l, e)} for l, e in pairs]


@golden("mka_voice_folder", "Папка озвучки относительно корня (mka_muxer.py: voice_folder): [метка, номер]", MKA_REL)
def g_mka_voice_folder():
    root = "/root/anime"
    rels = ["1. AniDUB/1. Show - 01.AniDUB.mka", "AniDUB/Show - 01.mka", "Show - 01.mka", "12.  DEEP /x.mka",
            "1. /x.mka", "Audio only/2. JAM/2. Show - 01.JAM.mka", "3.JAM/x.mka"]
    return [{"input": rel, **call(lambda r: list(mka.voice_folder(f"{root}/{r}", root)), rel)} for rel in rels]


@golden("mka_detect_lang", "Язык по метке/тайтлу в сборке .mka (mka_muxer.py: detect_lang)", MKA_REL)
def g_mka_detect_lang():
    return cases_for(mka.detect_lang, ["Оригинальная", "Original JP", "jp", "JPN", "Яп", "English Dub", "ENG", "en", "Англ.",
                                       "eng-sub", "AniLibria", "", None, "Japanese", "Jp.Dub", "Stereo"])


# file → {"title": тайтл первой аудиодорожки или null, "streams": число аудиодорожек}; answers — ответы process();
# ffmpeg_fail — номера вызовов ffmpeg с ошибкой; options — те же решения для C#
MKA_SCENARIOS = [
    {"name": "anitools_folders", "files": {
        "1. AniLibria.TV/1. Show - 01.AniLibria.TV.mka": {"title": "AniLibria.TV", "streams": 1},
        "1. AniLibria.TV/1. Show - 02.AniLibria.TV.mka": {"title": "AniLibria.TV", "streams": 1},
        "2. Оригинальная/2. Show - 01.Оригинальная.mka": {"title": "Оригинальная", "streams": 1},
        "2. Оригинальная/2. Show - 02.Оригинальная.mka": {"title": "Оригинальная", "streams": 1},
        "10. DEEP/10. Show - 01.DEEP.mka": {"title": "DEEP Eng", "streams": 2},
        "MKA/old.mka": {"title": None, "streams": 1}, ".hidden/1. Show - 01.x.mka": {"title": None, "streams": 1},
        "notes.txt": None},
     "answers": ["1", "1", "2", "1", "5", "0", "Оригинал", "0", None, "1", "rus"],
     "options": {"mode": "episodes", "order": None, "titles": {"AniLibria.TV": "AniLibria.TV", "Оригинальная": "Оригинал", "DEEP": "DEEP"},
                 "language": "rus"}},
    {"name": "loose_files_brackets_and_reorder", "files": {
        "Show - 01 [AniDUB].mka": {"title": None, "streams": 1}, "Show - 01 [AniLibria].mka": {"title": None, "streams": 1},
        "Show - 02 [AniDUB].mka": {"title": None, "streams": 1}, "Show - 02 [AniLibria].mka": {"title": "AniLibria 02", "streams": 1},
        "Extra [AniDUB].mka": {"title": None, "streams": 1}},
     "existing": ["MKA/Show - 01.mka"], "ffmpeg_fail": [0],
     "answers": ["1", "1", "1", "2,1", "2", "2"],
     "options": {"mode": "episodes", "order": [1, 0], "titles": None, "language": None}},
    {"name": "all_in_one_movie", "files": {
        "Movie [AniDUB].mka": {"title": "AniDUB", "streams": 1}, "Movie [JAM].mka": {"title": "Original", "streams": 1},
        "Movie.Comments.mka": {"title": None, "streams": 3}},
     "answers": ["2", "Фильм: Конец?", "1", "2", "2", "1", None],
     "options": {"mode": "single", "name": "Фильм: Конец?", "order": None, "titles": None, "language": "rus"}},
]


def run_mka_scenario(sc: dict) -> dict:
    import tempfile
    with tempfile.TemporaryDirectory(prefix="anitools-mka-") as td:
        root = os.path.realpath(td)
        for rel in list(sc["files"]) + sc.get("existing", []):
            p = Path(root, *rel.split("/"))
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(b"done" if rel in sc.get("existing", []) else b"")

        def probe(cmd):
            cmd = [str(c) for c in cmd]
            rel = os.path.relpath(cmd[-1], root).replace(os.sep, "/")
            info = sc["files"][rel]
            entries = cmd[cmd.index("-show_entries") + 1]
            if entries == "stream_tags=title":
                return (f"{info['title']}\n" if info["title"] else ""), ""
            if entries == "stream=index":
                return "".join(f"{i}\n" for i in range(info["streams"])), ""
            raise RuntimeError(f"неизвестный запрос ffprobe: {cmd}")

        commands, fails = [], set(sc.get("ffmpeg_fail", []))

        class FakePopen:
            def __init__(self, cmd, *args, **kwargs):
                self.cmd = [str(c).replace(root, "{root}") for c in cmd]
                self.returncode = 1 if len(commands) in fails else 0
                commands.append(self.cmd)

            def communicate(self):
                return "", "ошибка" if self.returncode else ""

            def poll(self):
                return self.returncode

        sub = fake_subprocess(probe, [])
        sub.Popen = FakePopen
        _ScriptedPrompt.answers = list(sc["answers"])
        with patched_module(mka, subprocess=sub, Prompt=_ScriptedPrompt, Panel=_Quiet, Table=_Quiet, Progress=_Quiet,
                            SpinnerColumn=_Quiet, TextColumn=_Quiet, BarColumn=_Quiet, TimeRemainingColumn=_Quiet,
                            box=_Quiet(), console=_Quiet(), clear_screen=lambda: None, FFMPEG_PATH="ffmpeg", FFPROBE_PATH="ffprobe",
                            write_process_error_log=lambda *a, **k: None, log_error=lambda *a, **k: None):
            real_walk = os.walk

            def ntfs_walk(top):
                # Порядок файловой системы — как у NTFS; списки те же, что у os.walk: оригинал правит dirs на месте
                for d, ds, fs in real_walk(top):
                    ds.sort(key=str.upper)
                    fs.sort(key=str.upper)
                    yield d, ds, fs

            mka.os = types.SimpleNamespace(**{k: getattr(os, k) for k in ("path", "sep", "name", "environ", "makedirs", "system")},
                                           walk=ntfs_walk)
            try:
                mka.process(root)
            finally:
                mka.os = os
        if _ScriptedPrompt.answers:
            raise RuntimeError(f"сценарий {sc['name']}: лишние ответы {_ScriptedPrompt.answers}")
        return {"commands": commands}


@golden("mka_scenarios", "Сборка озвучек в .mka (mka_muxer.py: process): команды ffmpeg для папки и ответов на вопросы", MKA_REL)
def g_mka_scenarios():
    cases = []
    for sc in MKA_SCENARIOS:
        inp = {k: sc.get(k) for k in ("name", "files", "existing", "ffmpeg_fail", "answers", "options")}
        cases.append({"input": inp, "output": run_mka_scenario(sc)})
    return cases


# ─── Шрифты для .ass (ass_fonts.py) ──────────────────────────────────────────

ASSFONTS_REL = f"{EXTRA_REL}/ass_fonts.py"
assfonts = load_extra("ass_fonts.py")


def _b64(data: bytes) -> str:
    import base64
    return base64.b64encode(data).decode("ascii")


def _utf16be(text: str) -> bytes:
    return text.encode("utf-16-be")


def _name_table(records) -> bytes:
    """Таблица name, формат 0: записи (platform, encoding, language, name_id, байты строки)."""
    import struct
    storage, recs = b"", b""
    for pid, eid, lid, nid, raw in records:
        recs += struct.pack(">6H", pid, eid, lid, nid, len(raw), len(storage))
        storage += raw
    return struct.pack(">HHH", 0, len(records), 6 + 12 * len(records)) + recs + storage


def _cmap_table(chars, fmt=4) -> bytes:
    """Таблица cmap с одной подтаблицей: формат 4 (3,1) или 12 (3,10); символ → глиф по порядку с 1."""
    import struct
    chars = sorted(set(chars))
    if fmt == 12:
        groups = b"".join(struct.pack(">3L", c, c, i + 1) for i, c in enumerate(chars))
        sub = struct.pack(">HHLLL", 12, 0, 16 + len(groups), 0, len(chars)) + groups
        platform, encoding = 3, 10
    else:
        segs = [(c, (i + 1 - c) % 0x10000) for i, c in enumerate(chars) if c < 0xFFFF] + [(0xFFFF, 1)]
        n = len(segs)
        entry = n.bit_length() - 1
        search = 2 * (1 << entry)
        body = (b"".join(struct.pack(">H", c) for c, _ in segs) + b"\x00\x00"
                + b"".join(struct.pack(">H", c) for c, _ in segs)
                + b"".join(struct.pack(">H", d) for _, d in segs) + b"\x00\x00" * n)
        sub = struct.pack(">7H", 4, 14 + len(body), 0, 2 * n, search, entry, 2 * n - search) + body
        platform, encoding = 3, 1
    return struct.pack(">HHHHL", 0, 1, platform, encoding, 12) + sub


def _sfnt(tables: dict, base: int = 0, magic: bytes = b"\x00\x01\x00\x00") -> bytes:
    """Шрифт sfnt из таблиц {тег: байты}; смещения таблиц — от начала файла (base — где шрифт лежит в коллекции)."""
    import struct
    tags = sorted(tables)
    n = len(tags)
    entry = max(n.bit_length() - 1, 0)
    head = magic + struct.pack(">4H", n, 16 * (1 << entry), entry, 16 * n - 16 * (1 << entry))
    offset = base + 12 + 16 * n
    directory, data = b"", b""
    for tag in tags:
        t = tables[tag] + b"\x00" * (-len(tables[tag]) % 4)
        directory += struct.pack(">4sLLL", tag.encode("ascii"), 0, offset + len(data), len(tables[tag]))
        data += t
    return head + directory + data


def _maxp(glyphs: int) -> bytes:
    """Таблица maxp версии 0.5 — fontTools без неё не строит таблицу символов."""
    import struct
    return struct.pack(">LH", 0x00005000, glyphs)


def _font(names, chars=(), fmt=4, magic=b"\x00\x01\x00\x00") -> bytes:
    tables = {"name": _name_table(names), "maxp": _maxp(len(set(chars)) + 1)}
    if chars:
        tables["cmap"] = _cmap_table(chars, fmt)
    return _sfnt(tables, magic=magic)


def _ttc(fonts_tables) -> bytes:
    """Коллекция .ttc: заголовок ttcf и шрифты подряд."""
    import struct
    count = len(fonts_tables)
    header_len = 12 + 4 * count
    blobs, offsets, pos = [], [], header_len
    for tables in fonts_tables:
        blob = _sfnt(tables, base=pos)
        offsets.append(pos)
        blobs.append(blob)
        pos += len(blob)
    return b"ttcf" + struct.pack(">LL", 0x00010000, count) + b"".join(struct.pack(">L", o) for o in offsets) + b"".join(blobs)


def _win(nid, text, eid=1):
    return (3, eid, 0x409, nid, _utf16be(text))


def _mac(nid, text):
    return (1, 0, 0, nid, text.encode("mac_roman"))


CYR = [ord("A"), ord("a"), 0x0416, 0x0436]
LATIN = [ord("A"), ord("a")]

FONT_SAMPLES = {
    "arial_cyr": (".ttf", _font([_win(1, "Arial"), _win(4, "Arial"), _win(6, "ArialMT"), _win(5, "Version 7.00"), _mac(1, "Arial")], CYR)),
    "arial_old_latin": (".ttf", _font([_win(1, "Arial"), _win(4, "Arial"), _win(6, "ArialMT"), _win(5, "Version 6.90")], LATIN)),
    "arial_bold": (".ttf", _font([_win(1, "Arial"), _win(2, "Bold"), _win(4, "Arial Bold"), _win(6, "Arial-BoldMT"), _win(5, "Version 7.00")], CYR)),
    "komika": (".otf", _font([_win(1, "Komika Axis Regular"), _win(16, "Komika Axis"), _win(6, "KomikaAxis")], CYR, fmt=12, magic=b"OTTO")),
    "family_only": (".ttf", _font([_win(16, "Arial"), _win(4, "Arial Narrow"), _win(6, "ArialNarrow")], LATIN)),
    "cambria_ttc": (".ttc", _ttc([{"name": _name_table([_win(1, "Cambria"), _win(6, "Cambria"), _win(5, "Version 6.98")]), "cmap": _cmap_table(CYR), "maxp": _maxp(5)},
                                  {"name": _name_table([_win(1, "Cambria Math"), _win(6, "CambriaMath")]), "cmap": _cmap_table(LATIN), "maxp": _maxp(3)}])),
    "latin_ttc": (".ttc", _ttc([{"name": _name_table([_win(1, "Duo A")]), "cmap": _cmap_table(LATIN), "maxp": _maxp(3)},
                                {"name": _name_table([_win(1, "Duo B")]), "cmap": _cmap_table(LATIN, 12), "maxp": _maxp(3)}])),
    "cyr_format12_only": (".ttf", _font([_win(1, "Twelve")], CYR, fmt=12)),
    "mac_roman": (".ttf", _font([_mac(1, "Caf\u00e9 Sans"), _mac(4, "Caf\u00e9 Sans Regular"), (1, 1, 0, 6, b"\x82\xa0")], LATIN)),
    "odd_utf16_and_space": (".ttf", _font([(3, 1, 0x409, 1, _utf16be("Odd") + b"\x00"), _win(4, "   "), (0, 3, 0, 6, _utf16be("Odd-PS")),
                                           (3, 1, 0x409, 4, b"\xd8\x00" + _utf16be("Lone"))], LATIN)),
    "true_magic": (".ttf", _font([_win(1, "Old  Mac   Font")], (), magic=b"true")),
    "woff_magic": (".ttf", b"wOFF" + b"\x00" * 60),
    "truncated_names": (".ttf", _font([_win(1, "Trunc One"), _win(4, "Trunc Full"), _win(6, "TruncPS")], LATIN)[:-30]),
    "no_cmap": (".ttf", _font([_win(1, "No Cmap")])),
    "empty": (".ttf", b""),
    "version_variants": (".ttf", _font([_win(1, "Ver"), _win(5, "Version 2.010;PS 2.000;hotconv 1.0.88"), _win(5, "1"), _win(5, "v.3"), _win(5, "no digits")], LATIN)),
}


def _has_cyrillic_bytes(ext: str, data: bytes) -> bool:
    import tempfile
    with tempfile.TemporaryDirectory() as td:
        path = Path(td) / f"font{ext}"
        path.write_bytes(data)
        return assfonts.has_cyrillic(path)


@golden("sfnt_font_names", "Имена и начертание из таблицы name, кириллица в cmap (ass_fonts.py: read_font_names, face_info, has_cyrillic через fontTools); вход — байты в base64", ASSFONTS_REL)
def g_sfnt_font_names():
    import io
    try:
        import fontTools  # noqa: F401
    except ImportError:
        raise SystemExit("Для эталона sfnt_font_names нужен fontTools: pip install fonttools==4.60.1")
    cases = []
    for name, (ext, data) in FONT_SAMPLES.items():
        primary, fallback = assfonts.read_font_names(io.BytesIO(data))
        face, ver = assfonts.face_info(data)
        cases.append({"input": {"name": name, "ext": ext, "base64": _b64(data)},
                      "output": {"primary": sorted(primary), "fallback": sorted(fallback), "face": face, "version": ver,
                                 "cyrillic": _has_cyrillic_bytes(ext, data)}})
    return cases


@golden("font_keys", "Ключи имён шрифтов (ass_fonts.py: font_key, normalize, slugify, safe_filename)", ASSFONTS_REL)
def g_font_keys():
    inputs = ["Arial", "  Times   New\tRoman ", "@MS Gothic", "Straße", "ARIAL", "Komika Axis", "Re:Zero / Font?", "Æon_Flux-Bold",
              "Шрифт Ж", "a\u00a0b", "", "İstanbul", "Ｆｕｌｌ", "x-y_z w"]
    return [{"input": x, "output": {"font_key": assfonts.font_key(x), "normalize": assfonts.normalize(x),
                                    "slug_": assfonts.slugify(x, "_"), "slug-": assfonts.slugify(x, "-"),
                                    "safe": assfonts.safe_filename(x), "clean": assfonts.clean_font_name(x)}} for x in inputs]


ASS_FONT_TEXTS = {
    "styles_and_fn": ("utf-8",
        "[Script Info]\nTitle: x\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Arial,20\n"
        "Style: Signs,@MS Gothic ,18\nStyle: Bad\n\n[Events]\nFormat: Layer, Start, End, Style, Text\n"
        "Dialogue: 0,0:00:01.00,0:00:02.00,Default,{\\fnKomika Axis}Текст{\\fn}сброс{\\b1\\fn Times New Roman \\i1}\n"
        "Comment: 0,0:00:01.00,0:00:02.00,Default,{\\fnNot Used}\n"),
    "custom_format_bom": ("utf-8-sig",
        "[V4 Styles]\nFormat: Name, Fontsize, Fontname\nStyle: A,20,Verdana\n[v4+ styles]\nStyle: B,18,Tahoma\n"
        "[Events]\nDIALOGUE: 0,0:00:01.00,0:00:02.00,A,{\\fnCourier New}x\n"),
    "utf16": ("utf-16", "[V4+ Styles]\nFormat: Name, Fontname\nStyle: Default,Шрифт Один\n[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,x,{\\fnШрифт Два}\n"),
    "cp1251": ("cp1251", "[V4+ Styles]\nFormat: Name, Fontname\nStyle: Default,Шрифт Три\n"),
    "no_fontname_in_format": ("utf-8", "[V4+ Styles]\nFormat: Name, Font\nStyle: Default,Second,Third\n\n[Fonts]\nStyle: X,Y\n"),
    "crlf_and_spaces": ("utf-8", "  [V4+ Styles]  \r\n  Format : Name , Fontname\r\n Style:Default,  Georgia  \r\n"),
}


@golden("ass_font_names", "Шрифты из .ass: стили и теги \\fn (ass_fonts.py: read_ass + parse_font_names); вход — байты в base64", ASSFONTS_REL)
def g_ass_font_names():
    import tempfile
    cases = []
    for name, (enc, text) in ASS_FONT_TEXTS.items():
        data = text.encode(enc)
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "x.ass"
            path.write_bytes(data)
            cases.append({"input": {"name": name, "base64": _b64(data)}, "output": sorted(assfonts.parse_font_names(path))})
    return cases


def _zip(entries) -> bytes:
    import io
    import zipfile
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, data in entries:
            # Дата записи фиксирована — иначе архив (и эталон) меняется от запуска к запуску
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            zf.writestr(info, b"" if name.endswith("/") else data)
    return buf.getvalue()


ZIP_SAMPLES = {
    "komika_pack": (_zip([("komika/", b""), ("komika/KomikaAxis.otf", FONT_SAMPLES["komika"][1]), ("komika/readme.txt", b"hi"),
                          ("__MACOSX/komika/._KomikaAxis.otf", FONT_SAMPLES["komika"][1]), ("Other.ttf", FONT_SAMPLES["arial_cyr"][1])]),
                    ["Komika Axis", "komika axis regular", "KomikaAxis", "Arial", "Missing"]),
    "dup_names": (_zip([("a/Arial.TTF", FONT_SAMPLES["arial_cyr"][1]), ("b/Arial.TTF", FONT_SAMPLES["arial_old_latin"][1])]), ["ArialMT"]),
    "not_zip": (b"PK\x03\x04broken", ["Arial"]),
    "empty_reply": (b"", ["Arial"]),
}


@golden("fonts_from_zip", "Шрифты из архива, чьё внутреннее имя совпадает с искомым (ass_fonts.py: fonts_from_zip)", ASSFONTS_REL)
def g_fonts_from_zip():
    import hashlib as h
    cases = []
    for name, (data, wanted) in ZIP_SAMPLES.items():
        for font in wanted:
            found = assfonts.fonts_from_zip(data, font)
            cases.append({"input": {"zip": name, "base64": _b64(data), "font": font},
                          "output": {k: h.sha256(v).hexdigest()[:16] for k, v in found.items()}})
    return cases


GOOGLE_CSS = """/* cyrillic */
@font-face {
  font-family: 'Roboto';
  font-style: italic;
  font-weight: 700;
  src: url(https://fonts.gstatic.com/s/roboto/v51/abc.ttf) format('truetype');
}
@font-face {
  font-family: 'Roboto';
  font-style: normal;
  src: url('https://fonts.gstatic.com/s/roboto/v51/def.otf?v=2') format('opentype');
}
@font-face { font-family: 'Roboto'; font-weight: 400; src: url(https://fonts.gstatic.com/s/roboto/v51/abc.ttf); }
@font-face { font-family: 'Roboto'; font-weight: 300; src: url(https://fonts.gstatic.com/s/roboto/v51/w.woff2) format('woff2'); }
@font-face { font-family: 'Roboto'; font-weight: 500; }
"""


@golden("font_downloads", "Скачивание шрифтов (ass_fonts.py: Google Fonts, dafont, 1001fonts): ответы серверов по URL ('*' — любой другой), запросы и итог", ASSFONTS_REL)
def g_font_downloads():
    import hashlib as h
    import urllib.error

    komika_zip = _b64(ZIP_SAMPLES["komika_pack"][0])
    gurl = "https://fonts.googleapis.com/css?family="
    variants = [
        ("google", "Roboto", {gurl + "Roboto:" + assfonts.GOOGLE_STYLES: {"text": GOOGLE_CSS}, "*": {"text": "FONTDATA"}}),
        ("google", "No Such Font", {"*": {"status": 400}}),
        ("google", "Roboto Slab", {"*": {"status": 500}}),
        ("dafont", "Komika Axis", {
            "https://www.dafont.com/search.php?q=Komika+Axis": {"text": "<a href='//dl.dafont.com/dl/?f=komika_axis'> dl.dafont.com/dl/?f=komika_pack dl.dafont.com/dl/?f=other"},
            "https://dl.dafont.com/dl/?f=komika_axis": {"text": ""}, "https://dl.dafont.com/dl/?f=komika_pack": {"base64": komika_zip}}),
        ("dafont", "Zzz Font!", {
            "https://www.dafont.com/search.php?q=Zzz+Font%21": {"text": "dl.dafont.com/dl/?f=a1 dl.dafont.com/dl/?f=b2 dl.dafont.com/dl/?f=c3 dl.dafont.com/dl/?f=d4"},
            "*": {"text": ""}}),
        ("dafont", "Шрифт", {"*": {"status": 503}}),
        ("1001fonts", "Komika Axis", {"https://www.1001fonts.com/download/komika-axis.zip": {"base64": komika_zip}}),
        ("1001fonts", "Nope", {"*": {"status": 404}}),
    ]
    functions = {"google": assfonts.download_from_google_fonts, "dafont": assfonts.download_from_dafont, "1001fonts": assfonts.download_from_1001fonts}
    cases = []
    for source, font, responses in variants:
        requests = []

        def fetch(url, ua=None, timeout=30, responses=responses, requests=requests):
            import base64
            requests.append([url, ua])
            reply = responses.get(url, responses.get("*"))
            if reply is None:
                raise urllib.error.URLError("нет ответа")
            if "status" in reply:
                raise urllib.error.HTTPError(url, reply["status"], "err", {}, None)
            return base64.b64decode(reply["base64"]) if "base64" in reply else reply["text"].encode("utf-8")

        with patched_module(assfonts, fetch=fetch):
            try:
                found = functions[source](font)
                out = {"result": {k: h.sha256(v).hexdigest()[:16] for k, v in found.items()}}
            except Exception as e:  # noqa: BLE001
                out = {"error": type(e).__name__}
        cases.append({"input": {"source": source, "font": font, "responses": responses}, "output": {**out, "requests": requests}})
    return cases


FONT_ROBOTO_OTHER = _font([_win(1, "Not Roboto"), _win(6, "NotRoboto")], LATIN)
FONT_ROBOTO = _font([_win(1, "Roboto"), _win(6, "Roboto-Regular"), _win(5, "Version 3.0")], CYR)
FONT_ARIAL_V69_CYR = _font([_win(1, "Arial"), _win(4, "Arial"), _win(6, "ArialMT"), _win(5, "Version 6.90")], CYR)

# files — {путь от корня: имя образца из FONT_SAMPLES / текст .ass}; downloads — {источник: {шрифт: {файл: образец}} | "error"};
# answers — ответы на вопросы (путь своей папки, Enter в конце)
FONT_SCENARIOS = [
    {"name": "local_steps_cyrillic_and_duplicates",
     "ass": {"work/Ep 01.ass": "[V4+ Styles]\nFormat: Name, Fontname\nStyle: Default,Arial\nStyle: Signs,Cambria\nStyle: B,Arial Bold\n"
                               "[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,x,{\\fnCafé Sans}{\\fnArial Narrow}\n",
             "work/ep 02.ass": "[V4+ Styles]\nFormat: Name, Fontname\nStyle: Default,Duo B\nStyle: X,Komika Axis\n"},
     "fonts": {"custom/arial.ttf": "arial_cyr", "custom/Arial_0.ttf": "arial_old_latin", "custom/sub/ArialV69.ttf": "arial_v69_cyr",
               "custom/mac/Cafe.ttf": "mac_roman", "sys1/arial.ttf": "arial_bold", "sys1/cambria.ttc": "cambria_ttc",
               "sys2/ArialN.ttf": "family_only", "sys2/duo.ttc": "latin_ttc", "sys2/KomikaAxis.otf": "komika"},
     "downloads": {}, "answers": ["", ""]},
    {"name": "downloads_and_not_found",
     "ass": {"work/a.ass": "[V4+ Styles]\nFormat: Name, Fontname\nStyle: Default,Roboto\nStyle: K,Komika Axis\nStyle: M,Missing Font\nStyle: A,Arial\n"},
     "fonts": {"custom/Roboto-400.ttf": "roboto_other", "custom/arial.ttf": "arial_cyr"},
     "downloads": {"google": {"Roboto": {"Roboto-400.ttf": "roboto", "Roboto-700.ttf": "arial_cyr"}},
                   "dafont": {"Komika Axis": "error", "Missing Font": {}},
                   "1001fonts": {"Komika Axis": {"KomikaAxis.otf": "komika"}}},
     "answers": ["", ""]},
    {"name": "no_fonts_in_ass", "ass": {"work/x.ass": "[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,x,{\\fn}\n"},
     "fonts": {}, "downloads": {}, "answers": ["", ""]},
]


def _font_sample(name: str) -> bytes:
    extra = {"roboto_other": FONT_ROBOTO_OTHER, "roboto": FONT_ROBOTO, "arial_v69_cyr": FONT_ARIAL_V69_CYR}
    return extra[name] if name in extra else FONT_SAMPLES[name][1]


def run_font_scenario(sc: dict) -> dict:
    import io
    import tempfile
    import zipfile
    import hashlib as h

    with tempfile.TemporaryDirectory(prefix="anitools-fonts-") as td:
        root = Path(os.path.realpath(td))
        for rel, text in sc["ass"].items():
            p = root.joinpath(*rel.split("/"))
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(text, encoding="utf-8")
        for rel, sample in sc["fonts"].items():
            p = root.joinpath(*rel.split("/"))
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(_font_sample(sample))

        def source(key):
            def download(name):
                reply = sc["downloads"].get(key, {}).get(name, {})
                if reply == "error":
                    raise OSError("сервер не ответил")
                return {fname: _font_sample(sample) for fname, sample in reply.items()}
            return download

        answers = list(sc["answers"])
        out = io.StringIO()
        with patched_module(assfonts, CUSTOM_FONTS_DIR=root / "custom", SYSTEM_FONTS_DIRS=[root / "sys1", root / "sys2"],
                            FONT_SOURCES=[("Google Fonts", source("google")), ("dafont", source("dafont")), ("1001fonts", source("1001fonts"))],
                            ask=lambda prompt: answers.pop(0), sorted=_windows_sorted):
            old_argv = sys.argv
            sys.argv = ["ass_fonts.py", str(root / "work")]
            try:
                with contextlib.redirect_stdout(out):
                    assfonts.main()
            finally:
                sys.argv = old_argv
        if answers:
            raise RuntimeError(f"сценарий {sc['name']}: лишние ответы {answers}")

        zip_path = root / "work" / "fonts.zip"
        entries = []
        if zip_path.exists():
            with zipfile.ZipFile(zip_path) as zf:
                for info in zf.infolist():
                    entries.append([info.filename, h.sha256(zf.read(info)).hexdigest()[:16], info.external_attr, info.compress_type])
        custom = sorted((p.relative_to(root / "custom").as_posix(), h.sha256(p.read_bytes()).hexdigest()[:16])
                        for p in (root / "custom").rglob("*") if p.is_file())
        lines = out.getvalue().split("\n")
        not_found = []
        if any("Не найдено нигде" in line for line in lines):
            start = next(i for i, line in enumerate(lines) if "Не найдено нигде" in line)
            not_found = [line.strip()[2:] for line in lines[start:] if line.strip().startswith("• ")]
        names = [line.strip()[2:] for line in lines if line.startswith("   • ")]
        return {"zip": entries, "custom": custom, "not_found": not_found, "font_names": names[:len(names) - len(not_found)]}


@golden("ass_fonts_scenarios", "Шрифты для .ass → fonts.zip (ass_fonts.py: main) — своя папка, системные, скачивание; эталон снят с fontTools", ASSFONTS_REL)
def g_ass_fonts_scenarios():
    try:
        import fontTools  # noqa: F401
    except ImportError:
        raise SystemExit("Для эталона ass_fonts_scenarios нужен fontTools: pip install fonttools==4.60.1")
    cases = []
    samples = {name: _b64(_font_sample(name)) for name in sorted({*FONT_SAMPLES, "roboto_other", "roboto", "arial_v69_cyr"})}
    for sc in FONT_SCENARIOS:
        inp = {k: sc[k] for k in ("name", "ass", "fonts", "downloads", "answers")}
        cases.append({"input": inp, "output": run_font_scenario(sc)})
    return cases, {"fixtures": samples}


@contextlib.contextmanager
def patched_module(mod, **attrs):
    """Временно подменить глобальные имена в модуле соседнего скрипта."""
    missing = object()
    old = {k: getattr(mod, k, missing) for k in attrs}
    for k, v in attrs.items():
        setattr(mod, k, v)
    try:
        yield
    finally:
        for k, v in old.items():
            if v is missing:
                delattr(mod, k)
            else:
                setattr(mod, k, v)


# ─── Запись ──────────────────────────────────────────────────────────────────

def render(name: str, description: str, source: str, cases: list, extra: dict) -> str:
    head = {"function": name, "description": description, "source": source, "source_sha256": source_sha256(source),
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
    files = {}
    for name, description, fn, source in GOLDENS:
        result = fn()
        cases, extra = result if isinstance(result, tuple) else (result, {})
        if not cases:
            raise SystemExit(f"{name}: нет ни одного случая")
        files[f"{name}.json"] = render(name, description, source, cases, extra)
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
