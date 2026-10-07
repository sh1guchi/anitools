import os
import sys
import subprocess
import re
from collections import deque
import datetime  # Добавляем модуль datetime в глобальные импорты
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import json
import math
import traceback
import signal
import atexit
import time
import zipfile
import shutil
import tempfile
import stat

# Импорты rich с обработкой ошибок
try:
    from rich.console import Console
    from rich.table import Table
    from rich.panel import Panel
    from rich.prompt import Prompt
    from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TimeRemainingColumn
    from rich.layout import Layout
    from rich.text import Text
    from rich.live import Live
    from rich import box
    RICH_AVAILABLE = True
except ImportError:
    RICH_AVAILABLE = False

# ═══════════════════════════════════════════════════════════
#   ANITOMY — встроенный парсер имён аниме-файлов (порт anitomy.js)
#   anitomy_parse('[Group] Title - 05 [1080p].mkv')
#   → {'anime_title': 'Title', 'episode_number': '5', 'release_group': 'Group', ...}
# ═══════════════════════════════════════════════════════════

_AT_KEYWORDS = {
    # Тип аниме
    "anime_type": [
        "OVA", "ONA", "OAD", "OAV", "SP", "SPECIAL", "SPECIALS",
        "Movie", "Movies", "MOVIE", "MOVIES",
        "TV",
        "NCED", "NCOP", "OP", "ED", "CM", "PV", "Preview",
        "Gekijouban", "Tokubetsu", "Special Episode",
    ],
    "audio_term": [
        "2CH", "2.0CH", "5.1", "5.1CH", "7.1", "7.1CH",
        "DTS", "DTS-MA", "DTS-ES", "DTS-HD", "DTS:X",
        "FLAC", "FLAC5.1", "FLAC7.1",
        "AAC", "AAC2.0", "AAC5.1",
        "AC3", "DD2.0", "DD5.1", "DD7.1", "Dolby", "TrueHD",
        "MP3", "OGG", "VORBIS",
        "HE-AAC", "LC-AAC", "EAC3",
        "Dual Audio", "Dual-Audio", "DualAudio",
        "Multi-Audio", "MultiAudio",
        "Commentary",
    ],
    "video_term": [
        "8bit", "8-bit", "10bit", "10-bit", "10bits", "Hi10",
        "Hi444", "Hi444P", "Hi444PP",
        "H264", "H.264", "x264", "AVC",
        "H265", "H.265", "x265", "HEVC",
        "AV1", "VP8", "VP9",
        "Xvid", "DivX",
        "HDR", "HDR10", "HDR10+", "HLG", "DV", "Dolby Vision",
        "BT.709", "BT.2020",
        "60FPS", "120FPS", "24FPS",
    ],
    "video_resolution": [
        "480p", "480i", "576p", "576i",
        "720p", "720i", "1080p", "1080i",
        "2160p", "4K", "UHD", "FHD", "HD", "SD",
        "1280x720", "1920x1080", "3840x2160",
    ],
    "source": [
        "BD", "Blu-ray", "BluRay", "BLURAY",
        "DVD", "DVDRIP", "DVDRip",
        "HDTV", "HDTVRip",
        "WEB", "WEB-DL", "WEBDL", "WEBRip", "WEBRIP",
        "Crunchyroll", "Funimation", "Amazon", "Netflix",
        "VHSRip", "VHS",
        "LaserDisc", "LD",
    ],
    "subtitles": [
        "ASS", "SSA", "SRT", "PGS", "SUB", "VOBSUB",
        "Soft Subs", "SoftSubs", "Softsubs",
        "Hard Subs", "HardSubs", "Hardsubs",
        "Multi-Subs", "MultiSubs",
        "Dubbed", "Subbed",
    ],
    "file_extension": [
        "mkv", "mk3d", "mka", "mks",
        "mp4", "m4v", "m4a",
        "avi", "divx",
        "flv", "f4v",
        "mov", "qt",
        "wmv", "asf",
        "ogv", "ogg",
        "ts", "m2ts", "m2t", "mts",
        "rmvb", "rm", "3gp",
    ],
    "language": [
        "RUS", "ENG", "JPN", "CHI", "KOR", "SPA", "POR", "FRA",
        "GER", "ITA", "ARA", "THA", "VIE",
        "Russian", "English", "Japanese", "Chinese",
        "RAW",
    ],
    # Префиксы серии. Vol/Part/Ch сюда НЕ входят: "Final Season Part 2 - 05" — серия 5, а не 2
    "episode_prefix": [
        "Ep", "Ep.", "EP", "EP.", "Episode", "Episodes",
        "E", "EPS", "Eps",
        "SP", "OVA", "ONA", "Movie",
    ],
    "season_prefix": ["S", "Season", "Seasons", "Saison"],
}
_AT_LOWER = {k: {w.lower() for w in v} for k, v in _AT_KEYWORDS.items()}
_AT_ALL_KEYWORDS = set().union(*(_AT_LOWER[k] for k in (
    "anime_type", "audio_term", "video_term", "video_resolution",
    "source", "subtitles", "file_extension", "language")))

_AT_OPEN, _AT_CLOSE, _AT_DELIM, _AT_UNKNOWN = "open", "close", "delim", "unknown"


def _at_tokenize(filename: str) -> list[dict]:
    """Разбивает строку на токены по разделителям и скобкам."""
    tokens, buf, in_bracket = [], "", False

    def flush():
        nonlocal buf
        if buf:
            tokens.append({"type": _AT_UNKNOWN, "value": buf, "enclosed": in_bracket})
            buf = ""

    for i, ch in enumerate(filename):
        if ch in "([{「【『（〔":
            flush()
            tokens.append({"type": _AT_OPEN, "value": ch, "enclosed": False})
            in_bracket = True
        elif ch in ")]}」】』）〕":
            flush()
            tokens.append({"type": _AT_CLOSE, "value": ch, "enclosed": False})
            in_bracket = False
        elif ch in " _.,|":
            flush()
            tokens.append({"type": _AT_DELIM, "value": ch, "enclosed": in_bracket})
        elif ch in "–—":
            # Длинное тире — всегда разделитель, нормализуем к "-"
            flush()
            tokens.append({"type": _AT_DELIM, "value": "-", "enclosed": in_bracket})
        elif ch == "-":
            # Дефис — разделитель, если рядом пробел
            prev = filename[i - 1] if i > 0 else ""
            nxt = filename[i + 1] if i + 1 < len(filename) else ""
            if prev == " " or nxt == " ":
                flush()
                tokens.append({"type": _AT_DELIM, "value": "-", "enclosed": in_bracket})
            else:
                buf += ch
        else:
            buf += ch
    flush()
    return tokens


def _at_is_numeric(s: str) -> bool:
    return re.fullmatch(r"[0-9]+", s) is not None


def _at_looks_like_year(s: str) -> bool:
    return _at_is_numeric(s) and len(s) == 4 and 1950 <= int(s) <= 2050


def _at_looks_like_resolution(s: str) -> bool:
    return (re.fullmatch(r"[0-9]{3,4}[pi]", s, re.I) is not None
            or re.fullmatch(r"[0-9]{3,4}x[0-9]{3,4}", s, re.I) is not None
            or s.upper() in ("4K", "UHD", "FHD", "HD", "SD"))


def _at_is_keyword(s: str) -> bool:
    return s.lower() in _AT_ALL_KEYWORDS


def anitomy_parse(input_name: str) -> dict:
    """Парсит имя аниме-файла. Все значения — строки (номера без ведущих нулей)."""
    if not input_name or not isinstance(input_name, str):
        return {}

    filename = input_name.strip()
    file_extension = None
    m = re.search(r"\.([a-zA-Z0-9]{2,4})$", filename)
    if m and m.group(1).lower() in _AT_LOWER["file_extension"]:
        file_extension = m.group(1).lower()
        filename = filename[:-len(m.group(0))]

    tokens = _at_tokenize(filename)
    found = {
        "release_group": None, "anime_title": None, "anime_year": None,
        "anime_type": None, "anime_season": None, "episode_number": None,
        "episode_number_alt": None, "episode_title": None, "release_version": None,
        "video_resolution": None, "video_term": [], "audio_term": [],
        "source": None, "subtitles": None, "language": None, "file_checksum": None,
    }

    # ── Шаг 1: токены в скобках (release group, технические теги) ──
    bracket_groups = []
    bracket_ep_candidate = None  # одиночное число в скобках — только запасной вариант
    starts_with_bracket = bool(tokens) and tokens[0]["type"] == _AT_OPEN
    cur = None
    for tok in tokens:
        if tok["type"] == _AT_OPEN:
            cur = {"toks": [], "is_first": not bracket_groups}
        elif tok["type"] == _AT_CLOSE:
            if cur is not None:
                bracket_groups.append(cur)
                cur = None
        elif cur is not None and tok["type"] == _AT_UNKNOWN:
            cur["toks"].append(tok["value"])

    for grp in bracket_groups:
        words = grp["toks"]
        combined = " ".join(words)

        if len(words) == 1 and _at_looks_like_year(words[0]):
            found["anime_year"] = words[0]
            continue
        if len(words) == 1 and re.fullmatch(r"[0-9A-Fa-f]{8}", words[0]):
            found["file_checksum"] = words[0]
            continue
        if len(words) == 1 and _at_looks_like_resolution(words[0]):
            found["video_resolution"] = words[0]
            continue
        if len(words) == 1 and _at_is_numeric(words[0]) and not bracket_ep_candidate:
            if 0 < int(words[0]) < 2000:
                bracket_ep_candidate = words[0]
                continue

        is_technical = False
        for w_raw in words:
            # "1080p-FLAC" → проверяем и части, склеенные дефисом
            parts = (w_raw.split("-") if "-" in w_raw and not _at_is_keyword(w_raw)
                     and not _at_looks_like_resolution(w_raw) else [w_raw])
            for w in parts:
                wl = w.lower()
                if not found["video_resolution"] and _at_looks_like_resolution(w) and not _at_is_numeric(w):
                    found["video_resolution"] = w; is_technical = True
                if wl in _AT_LOWER["video_term"]:
                    found["video_term"].append(w); is_technical = True
                if wl in _AT_LOWER["audio_term"]:
                    found["audio_term"].append(w); is_technical = True
                if wl in _AT_LOWER["source"]:
                    found["source"] = w; is_technical = True
                if wl in _AT_LOWER["subtitles"]:
                    found["subtitles"] = w; is_technical = True
                if wl in _AT_LOWER["language"]:
                    found["language"] = w; is_technical = True
                if wl in _AT_LOWER["anime_type"]:
                    found["anime_type"] = w; is_technical = True

        # Первая скобочная группа в самом начале имени и не техническая — release group
        if (grp["is_first"] and starts_with_bracket and not is_technical
                and not found["release_group"] and len(combined) < 30):
            found["release_group"] = combined

    # ── Шаг 2: токены вне скобок; afterDash — перед токеном стоит " - " ──
    flat_open = []
    enclosed = 0
    for ti, t in enumerate(tokens):
        if t["type"] == _AT_OPEN:
            enclosed += 1; continue
        if t["type"] == _AT_CLOSE:
            enclosed = max(0, enclosed - 1); continue
        if enclosed > 0 or t["type"] != _AT_UNKNOWN:
            continue
        after_dash = False
        pj = ti - 1
        while pj >= 0 and tokens[pj]["type"] == _AT_DELIM:
            if tokens[pj]["value"] == "-":
                after_dash = True
                break
            pj -= 1
        flat_open.append({"value": t["value"], "after_dash": after_dash})

    # ── Шаг 3: номер серии и сезона ──
    used = set()
    ep_idx = -1
    # Если чисел после " - " несколько ("Title - 2 - 05") — серия последнее из них
    last_dash_num = -1
    for j, t in enumerate(flat_open):
        if t["after_dash"] and _at_is_numeric(t["value"]):
            last_dash_num = j
    type_idx = -1  # позиция OVA/Movie/... — число перед ним не серия ("Title 2 - OVA")

    i = 0
    while i < len(flat_open):
        v, after_dash = flat_open[i]["value"], flat_open[i]["after_dash"]
        vl = v.lower()
        nxt = flat_open[i + 1] if i + 1 < len(flat_open) else None

        # S01E02
        m = re.fullmatch(r"[Ss]([0-9]{1,2})[Ee]([0-9]{1,3})", v)
        if m:
            found["anime_season"] = str(int(m.group(1)))
            found["episode_number"] = str(int(m.group(2)))
            ep_idx = i; used.add(i); i += 1; continue

        # S01
        m = re.fullmatch(r"[Ss]([0-9]{1,2})", v)
        if m:
            found["anime_season"] = str(int(m.group(1)))
            used.add(i); i += 1; continue

        # "2nd Season"
        m = re.fullmatch(r"([0-9]{1,2})(?:st|nd|rd|th)", v, re.I)
        if m and nxt and nxt["value"].lower() == "season" and not found["anime_season"]:
            found["anime_season"] = str(int(m.group(1)))
            used.update((i, i + 1)); i += 2; continue

        # Season 2 (номер после " - " — это уже серия: "2nd Season - 05")
        if vl in _AT_LOWER["season_prefix"] and nxt and _at_is_numeric(nxt["value"]) and not nxt["after_dash"]:
            found["anime_season"] = str(int(nxt["value"]))
            used.update((i, i + 1)); i += 2; continue

        # Ep01, E05, Episode5
        m = re.fullmatch(r"(?:e|ep\.?|eps\.?|episode)([0-9]+)", v, re.I)
        if m and not found["episode_number"]:
            found["episode_number"] = str(int(m.group(1)))
            ep_idx = i; used.add(i); i += 1; continue
        # Ep 01, Episode 5
        if vl in _AT_LOWER["episode_prefix"] and not found["episode_number"] and nxt and _at_is_numeric(nxt["value"]):
            found["episode_number"] = str(int(nxt["value"]))
            # "OVA 2", "Movie 3" — это ещё и тип релиза
            if not found["anime_type"] and vl in _AT_LOWER["anime_type"]:
                found["anime_type"] = v
            ep_idx = i + 1; used.update((i, i + 1)); i += 2; continue

        # 01v2
        m = re.fullmatch(r"([0-9]+)v([0-9])", v, re.I)
        if m and not found["episode_number"]:
            found["episode_number"] = str(int(m.group(1)))
            found["release_version"] = "v" + m.group(2)
            ep_idx = i; used.add(i); i += 1; continue

        # 01-12, 01~12
        m = re.fullmatch(r"([0-9]+)[-~]([0-9]+)", v)
        if m and not found["episode_number"]:
            found["episode_number"] = str(int(m.group(1)))
            found["episode_number_alt"] = str(int(m.group(2)))
            ep_idx = i; used.add(i); i += 1; continue

        # 第01話
        m = re.fullmatch(r"第([0-9]{1,3})話", v)
        if m and not found["episode_number"]:
            found["episode_number"] = str(int(m.group(1)))
            ep_idx = i; used.add(i); i += 1; continue

        if _at_looks_like_year(v) and not found["anime_year"]:
            found["anime_year"] = v
            used.add(i); i += 1; continue

        if _at_looks_like_resolution(v) and not found["video_resolution"]:
            found["video_resolution"] = v
            used.add(i); i += 1; continue

        # Отдельная версия релиза: "Title - 01 v2"
        if re.fullmatch(r"v[0-9]", v, re.I) and found["episode_number"] and not found["release_version"]:
            found["release_version"] = vl
            used.add(i); i += 1; continue

        # Число после " - " — серия (главный паттерн). "- 00" тоже серия (пролог)
        if after_dash and _at_is_numeric(v) and i == last_dash_num and not found["episode_number"]:
            if 0 <= int(v) < 2000:
                found["episode_number"] = str(int(v))
                ep_idx = i; used.add(i); i += 1; continue

        # Техническое ключевое слово
        if _at_is_keyword(v):
            if not found["anime_type"] and vl in _AT_LOWER["anime_type"]:
                found["anime_type"] = v
                type_idx = i
            used.add(i); i += 1; continue

        i += 1

    # Серия не найдена — последнее изолированное число (но не перед OVA/Movie)
    if not found["episode_number"]:
        for i in range(len(flat_open) - 1, type_idx, -1):
            v = flat_open[i]["value"]
            if _at_is_numeric(v) and i not in used and 0 < int(v) < 2000:
                found["episode_number"] = str(int(v))
                ep_idx = i; used.add(i)
                break

    if not found["episode_number"] and bracket_ep_candidate:
        found["episode_number"] = str(int(bracket_ep_candidate))

    # ── Шаг 4: название — всё, что до первого распознанного токена ──
    first_used = min(used) if used else len(flat_open)
    title = " ".join(flat_open[i]["value"] for i in range(first_used) if i not in used).strip()
    title = re.sub(r"[\s_]+$", "", title)
    title = re.sub(r"[-–]+$", "", title).strip()
    if " " not in title and "_" in title:
        title = title.replace("_", " ")
    if title:
        found["anime_title"] = title

    # ── Шаг 5: название серии — токены после номера серии ──
    if found["episode_number"]:
        last_used = ep_idx if ep_idx >= 0 else max(used, default=-1)
        after = [flat_open[i]["value"] for i in range(last_used + 1, len(flat_open))
                 if i not in used and not _at_is_keyword(flat_open[i]["value"])]
        ep_title = re.sub(r"^[-–\s]+", "", " ".join(after).strip()).strip()
        if len(ep_title) > 1:
            found["episode_title"] = ep_title

    out = {"file_name": filename}
    if file_extension:
        out["file_extension"] = file_extension
    for key in ("release_group", "anime_title", "anime_year", "anime_type", "anime_season",
                "episode_number", "episode_number_alt", "episode_title", "release_version",
                "video_resolution"):
        if found[key]:
            out[key] = found[key]
    if found["video_term"]:
        out["video_term"] = ", ".join(found["video_term"])
    if found["audio_term"]:
        out["audio_term"] = ", ".join(found["audio_term"])
    for key in ("source", "subtitles", "language", "file_checksum"):
        if found[key]:
            out[key] = found[key]
    return out

# ═══════════════════════════════════════════════════════════

# ─── Шикимори ────────────────────────────────────────────────
import urllib.request
import urllib.parse
import urllib.error

def _shiki_get(api_path: str, tries: int = 3):
    """
    GET к API Shikimori. На 429 (лимит 5 запросов/с) и сетевые сбои — повтор с паузой.
    Возвращает распарсенный JSON или None.
    """
    for attempt in range(1, tries + 1):
        status = 0
        try:
            req = urllib.request.Request(f"https://shikimori.io/api/{api_path}", headers={
                "User-Agent": "anitools/1.0",
                "Accept":     "application/json",
            })
            with urllib.request.urlopen(req, timeout=8) as resp:
                return json.loads(resp.read().decode())
        except urllib.error.HTTPError as e:
            status = e.code
            if status == 404:
                return None
        except Exception:
            pass
        if attempt < tries:
            if status == 429:
                console.print("  [yellow]Shikimori: слишком много запросов — жду и повторяю...[/]")
            time.sleep(1.5 * attempt if status == 429 else 1.0)
    return None


def _search_shikimori(query: str, limit: int = 15) -> list[dict]:
    """
    Ищет аниме на Shikimori по названию.
    Возвращает список {'id', 'name', 'russian', 'year', 'kind', 'episodes'}.
    """
    data = _shiki_get(f"animes?search={urllib.parse.quote(query)}&limit={limit}&order=popularity")
    if not isinstance(data, list):
        return []
    return [
        {
            "id":       r["id"],
            "name":     r.get("name", "") or "",
            "russian":  r.get("russian", "") or r.get("name", "") or "",
            "year":     (r.get("aired_on") or "????")[:4],
            "kind":     r.get("kind", "") or "",
            "episodes": r.get("episodes") or 0,
        }
        for r in data if r.get("id")
    ]


_SHIKI_KIND_RU = {"tv": "TV", "ova": "OVA", "ona": "ONA", "special": "Спешл", "tv_special": "Спешл",
                  "movie": "Фильм", "music": "Клип", "pv": "PV", "cm": "CM"}
_ROMAN_SEASON = {"II": 2, "III": 3, "IV": 4, "V": 5, "VI": 6}


def _title_season(name: str) -> int:
    """Номер сезона по названию: 'X 2', 'X 2: Subtitle', 'X Season 3', 'X 2nd Season', 'X III', 'X 3 pt 1'."""
    n = (name or "").split(": ")[0]
    n = re.sub(r'\s+(?:pt|part)\s*\d+\s*$', '', n, flags=re.IGNORECASE)
    n = re.sub(r'\s+(?:OVA|ONA|Movie|Specials?)\s*$', '', n, flags=re.IGNORECASE).strip()
    m = (re.search(r'\bSeasons?\s*(\d{1,2})\b', n, re.IGNORECASE)
         or re.search(r'\b(\d{1,2})(?:st|nd|rd|th)\s+Season\b', n, re.IGNORECASE)
         or re.search(r'\s(\d{1,2})$', n))
    if m:
        return int(m.group(1))
    m = re.search(r'\s(II|III|IV|V|VI)$', n)
    return _ROMAN_SEASON[m.group(1)] if m else 1


def _norm_title(s: str) -> str:
    return re.sub(r'[\W_]+', ' ', (s or "").lower()).strip()


# Метка группы (OVA/ONA/Special/Movie) → kind на Shikimori
_SHIKI_KINDS = {"OVA": ("ova",), "ONA": ("ona",), "Special": ("special", "tv_special"), "Movie": ("movie",)}


def _rank_shikimori(results: list[dict], query: str, season: int, kinds: tuple = ("tv",)) -> list[dict]:
    """Название содержит запрос > совпал сезон > совпал тип; дальше — по популярности (исходный порядок)."""
    q = _norm_title(query)

    def score(r):
        related = bool(q) and (q in _norm_title(r["name"]) or q in _norm_title(r["russian"]))
        return (4 if related else 0) + (2 if _title_season(r["name"]) == season else 0) + (1 if r["kind"] in kinds else 0)

    return [r for _, r in sorted(enumerate(results), key=lambda x: (-score(x[1]), x[0]))]


def _search_shikimori_smart(query: str) -> list[dict]:
    """Поиск с запасными запросами: без знаков препинания, затем по первым трём словам."""
    variants = [query]
    plain = re.sub(r'\s+', ' ', re.sub(r'[!?:;,.\'"~()\[\]]', ' ', query)).strip()
    if plain != query:
        variants.append(plain)
    words = plain.split(" ")
    if len(words) > 3:
        variants.append(" ".join(words[:3]))
    for q in variants:
        res = _search_shikimori(q)
        if res:
            return res
    return []


def _clean_title_for_search(title: str) -> str:
    """
    Убирает из названия суффиксы сезона/части перед поиском на Шикимори.
    'Enen no Shouboutai 3 pt 1' → 'Enen no Shouboutai'
    'Attack on Titan Season 4' → 'Attack on Titan'
    'Overlord III'             → 'Overlord'
    """
    # Убираем " pt N" / " part N"
    title = re.sub(r'\s+pt\s*\d+\s*$', '', title, flags=re.IGNORECASE).strip()
    title = re.sub(r'\s+part\s*\d+\s*$', '', title, flags=re.IGNORECASE).strip()
    # Убираем " Season N" / " S N"
    title = re.sub(r'\s+seasons?\s*\d+\s*$', '', title, flags=re.IGNORECASE).strip()
    # " 2nd Season"
    title = re.sub(r'\s+\d{1,2}(?:st|nd|rd|th)\s+season\s*$', '', title, flags=re.IGNORECASE).strip()
    # Убираем римские цифры в конце (I II III IV V ...)
    title = re.sub(r'\s+(?:I{1,3}|IV|VI{0,3}|IX|XI{0,3})\s*$', '', title).strip()
    # Убираем арабскую цифру сезона в конце (если она одна/две цифры)
    title = re.sub(r'\s+\d{1,2}\s*$', '', title).strip()
    return title


def _choose_shikimori(title: str) -> tuple[str, object]:
    """
    Интерактивный поиск на Шикимори по названию из файла.
    Возвращает ("pick", запись поиска) — выбран результат, ("id", "12345") — ID введён вручную,
    ("skip", None) — пропущено.
    """
    # Ищем по чистому названию (без сезона/части/OVA), а сезон и тип учитываем при сортировке
    km = _SPECIAL_SUFFIX_RE.search(title)
    kinds = _SHIKI_KINDS[km.group(1)] if km else ("tv",)
    base_title = _SPECIAL_SUFFIX_RE.sub("", title)
    season = _title_season(base_title)
    search_query = _clean_title_for_search(base_title) or base_title

    while True:
        console.print(f"\n  [dim]Ищу [bold]{search_query}[/bold] на Shikimori...[/]")
        results = _rank_shikimori(_search_shikimori_smart(search_query), search_query, season, kinds)[:8]

        if not results:
            console.print("  [yellow]Shikimori не ответил или ничего не нашёл.[/]")
            answer = Prompt.ask(
                "  [magenta]Shikimori ID или другой запрос[/] (Enter — пропустить)",
                default=""
            ).strip()
            if answer.isdigit():
                return "id", answer
            if answer:
                search_query = answer
                continue
            return "skip", None

        # Показываем результаты
        st = Table(box=box.ROUNDED, border_style="purple", title_style="bold purple",
                   title="🔍 Результаты Shikimori")
        st.add_column("#", justify="center", style="magenta")
        st.add_column("ID", justify="right", style="dim")
        st.add_column("Оригинал", style="bold white")
        st.add_column("Тип", justify="center", style="cyan")
        st.add_column("Эп.", justify="right", style="dim")
        st.add_column("Год", justify="center", style="cyan")
        for i, r in enumerate(results, 1):
            st.add_row(str(i), str(r["id"]), r["name"][:55],
                       _SHIKI_KIND_RU.get(r["kind"], r["kind"] or "?"),
                       str(r["episodes"] or ""), r["year"])
        st.add_row("0", "—", "[dim]Ввести ID вручную / пропустить[/]", "", "", "")
        console.print(st)
        console.print("  [dim]номер — выбрать · 0 — вручную · текст — искать заново[/]")

        while True:
            pick = Prompt.ask("  [magenta]Выбери номер[/]", default="1").strip()
            if not pick.isdigit() or int(pick) <= len(results):
                break
            console.print(f"  [yellow]Нет варианта {pick}.[/]")
        if pick.isdigit() or not pick:
            break
        search_query = pick

    if pick.isdigit() and int(pick) == 0:
        shiki_id_raw = Prompt.ask(
            "  [magenta]Shikimori ID[/] (Enter — пропустить)",
            default=""
        ).strip()
        return ("id", shiki_id_raw) if shiki_id_raw.isdigit() else ("skip", None)

    if pick.isdigit() and 1 <= int(pick) <= len(results):
        return "pick", results[int(pick) - 1]

    return "skip", None


def _pick_shikimori(title: str) -> str:
    """
    Показывает результаты поиска Шикимори и возвращает folder_name.
    folder_name = '{id} - {title}' или просто title если пропущено/не найдено.
    Результат санируется от символов, недопустимых в именах папок Windows
    (важно для названий, введённых вручную при перегруппировке, напр. "Re:Zero").
    """
    kind, val = _choose_shikimori(title)
    if kind == "skip":
        return _sanitize_folder(title)
    sid = val if kind == "id" else val["id"]
    folder = _sanitize_folder(f"{sid} - {title}")
    console.print(f"  [green]✓[/] Папка тайтла: [bold white]{folder}[/]")
    return folder


def _shiki_original_name(shiki_id) -> str | None:
    """Оригинальное (ромадзи) название тайтла по ID Шикимори."""
    data = _shiki_get(f"animes/{shiki_id}")
    return (data or {}).get("name") or None


def _filename_safe_title(name: str) -> str:
    """Название для имени файла Windows: 'X 2: Subtitle' → 'X 2 - Subtitle', 'Re:Zero' → 'Re Zero'."""
    name = re.sub(r'\s*:\s+', ' - ', name)
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', ' ', name)
    return re.sub(r'\s+', ' ', name).strip(' .')

# ─────────────────────────────────────────────────────────────

def _install_reflow_safe_live() -> None:
    """
    Прогресс-бары rich перерисовываются на месте: курсор поднимается на столько строк,
    сколько занимал прошлый кадр. Если окно сузить, терминал переносит уже нарисованные
    строки на несколько — rich об этом не знает, стирает меньше, и сверху остаётся мусор.
    Подменяем LiveRender: запоминаем ширину каждой строки кадра и перед перерисовкой
    считаем, сколько строк экрана она занимает при ТЕКУЩЕЙ ширине окна.
    """
    try:
        import math
        import rich.live
        from rich.live_render import LiveRender
        from rich.segment import Segment
        from rich.control import Control, ControlType
        from rich.text import Text as _Text
    except Exception:
        return

    class _ReflowLiveRender(LiveRender):
        _line_widths: list = []

        def __rich_console__(self, console_, options):
            # Та же отрисовка, что в rich.live_render.LiveRender, плюс запоминаем ширину строк кадра
            style = console_.get_style(self.style)
            lines = console_.render_lines(self.renderable, options, style=style, pad=False)
            if len(lines) > options.size.height:
                if self.vertical_overflow == "crop":
                    lines = lines[: options.size.height]
                elif self.vertical_overflow == "ellipsis":
                    lines = lines[: options.size.height - 1]
                    lines.append(list(console_.render(_Text("...", overflow="crop", justify="center",
                                                            end="", style="live.ellipsis"))))
            self._shape = Segment.get_shape(lines)
            self._line_widths = [Segment.get_line_length(l) for l in lines]
            new_line = Segment.line()
            for i, line in enumerate(lines):
                yield from line
                if i < len(lines) - 1:
                    yield new_line

        def _screen_rows(self) -> int:
            cols = max(1, shutil.get_terminal_size((120, 30)).columns)
            if not self._line_widths:
                return self._shape[1] if self._shape else 0
            return sum(max(1, math.ceil(w / cols)) for w in self._line_widths)

        def position_cursor(self):
            if self._shape is None:
                return Control()
            rows = self._screen_rows()
            return Control(
                ControlType.CARRIAGE_RETURN, (ControlType.ERASE_IN_LINE, 2),
                *(((ControlType.CURSOR_UP, 1), (ControlType.ERASE_IN_LINE, 2)) * (rows - 1))
            )

        def restore_cursor(self):
            if self._shape is None:
                return Control()
            rows = self._screen_rows()
            return Control(
                ControlType.CARRIAGE_RETURN,
                *((ControlType.CURSOR_UP, 1), (ControlType.ERASE_IN_LINE, 2)) * rows
            )

    rich.live.LiveRender = _ReflowLiveRender


# Инициализация console с проверкой доступности rich
if RICH_AVAILABLE:
    _install_reflow_safe_live()
    console = Console()
else:
    # Fallback для случая, когда rich недоступен
    class SimpleConsole:
        def print(self, *args, **kwargs):
            print(*args, **kwargs)
        def ask(self, *args, **kwargs):
            return input(*args)
    
    console = SimpleConsole()

# Пути к ffmpeg/ffprobe можно переопределить, чтобы обойти шим Chocolatey
FFMPEG_PATH = os.environ.get("FFMPEG_PATH", "ffmpeg")
FFPROBE_PATH = os.environ.get("FFPROBE_PATH", "ffprobe")

# Рабочая папка для ПРОМЕЖУТОЧНЫХ файлов HLS-конвертации (сегменты, mka).
# Для пункта меню 7 рабочая папка выбирается интерактивно (RAM-диск / папка / по умолчанию).
# Эта переменная задаёт значение по умолчанию до выбора; '' = писать рядом с выходной папкой.
ANITOOLS_WORK_DIR = os.environ.get("ANITOOLS_WORK_DIR", "")

# Реестр активных подпроцессов (ffmpeg и др.) для корректного завершения при выходе
ACTIVE_PROCESSES = set()

# Реестр cleanup-колбэков (отмонтирование RAM-диска и т.п.), которые нужно
# выполнить при аварийном завершении по SIGTERM/SIGBREAK, где atexit/finally
# не отработают из-за os._exit.
EXIT_CLEANUP_CALLBACKS = []

def register_exit_cleanup(fn) -> None:
    try:
        EXIT_CLEANUP_CALLBACKS.append(fn)
    except Exception:
        pass

def run_exit_cleanups() -> None:
    for fn in list(EXIT_CLEANUP_CALLBACKS):
        try:
            fn()
        except Exception:
            pass

def register_process(process: subprocess.Popen) -> None:
    try:
        ACTIVE_PROCESSES.add(process)
    except Exception:
        pass

def unregister_process(process: subprocess.Popen) -> None:
    try:
        ACTIVE_PROCESSES.discard(process)
    except Exception:
        pass

def terminate_all_processes() -> None:
    for proc in list(ACTIVE_PROCESSES):
        try:
            if proc.poll() is None:
                if os.name == 'nt':
                    # Сначала посылаем CTRL+BREAK для всей группы процесса
                    try:
                        proc.send_signal(getattr(signal, 'CTRL_BREAK_EVENT', signal.SIGTERM))
                        time.sleep(0.5)
                    except Exception:
                        pass
                # Пробуем мягко завершить
                try:
                    proc.terminate()
                except Exception:
                    pass
                # Ждём и при необходимости убиваем
                try:
                    proc.wait(timeout=2)
                except Exception:
                    try:
                        proc.kill()
                    except Exception:
                        pass
                # Жёстко убиваем дерево процессов, если всё ещё жив
                if os.name == 'nt' and proc.poll() is None:
                    try:
                        subprocess.run([
                            "taskkill", "/T", "/F", "/PID", str(proc.pid)
                        ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    except Exception:
                        pass
        finally:
            unregister_process(proc)
    # Глобальный аварийный сброс: по запросу пользователя
    if os.environ.get("FFMPEG_HARD_KILL_ALL", "0") == "1" and os.name == 'nt':
        try:
            subprocess.run(["taskkill", "/F", "/IM", "ffmpeg.exe"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except Exception:
            pass


def log_error(error: Exception) -> Path | None:
    """Пишет информацию об ошибке в лог-файл в текущей рабочей директории."""
    try:
        timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        log_path = Path.cwd() / f"error_log_{timestamp}.txt"
        with log_path.open("w", encoding="utf-8") as f:
            f.write(f"Time: {datetime.datetime.now().isoformat()}\n")
            f.write(f"Error type: {type(error).__name__}\n")
            f.write(f"Message: {error}\n\n")
            f.write("Traceback:\n")
            f.write(traceback.format_exc())
        return log_path
    except Exception:
        return None


def write_process_error_log(prefix: str, stderr_text: str, cmd=None, returncode=None) -> Path | None:
    """Сохраняет вывод внешнего процесса (хвост) в лог в текущей рабочей директории.
    В лог всегда пишется заголовок с командой и кодом возврата — пустой файл
    невозможно диагностировать (раньше такое случалось, если инструмент
    писал ошибку в stdout, а мы перехватывали только stderr)."""
    try:
        timestamp = datetime.datetime.now().strftime("%Y-%m-%d_%H-%M-%S")
        safe_prefix = re.sub(r"[^\w.-]", "_", prefix)[:80] or "log"
        log_path = Path.cwd() / f"{safe_prefix}_ffmpeg_error_{timestamp}.log"
        with log_path.open("w", encoding="utf-8", errors="replace") as f:
            f.write(f"Time: {datetime.datetime.now().isoformat()}\n")
            if cmd is not None:
                f.write("Command: " + subprocess.list2cmdline([str(c) for c in cmd]) + "\n")
            if returncode is not None:
                f.write(f"Return code: {returncode}\n")
            f.write("\n--- output (stdout+stderr, tail) ---\n")
            f.write(stderr_text.strip() + "\n" if stderr_text and stderr_text.strip()
                    else "(процесс не вывел ничего — возможно, был прерван снаружи)\n")
        return log_path
    except Exception:
        return None

def _graceful_exit_handler(signum, frame):
    # ВАЖНО: os._exit пропускает atexit и все finally-блоки, поэтому
    # здесь вручную гасим процессы И выполняем cleanup-колбэки (RAM-диск).
    try:
        terminate_all_processes()
        run_exit_cleanups()
    finally:
        os._exit(1)

# Регистрируем обработчики завершения.
# SIGINT (Ctrl+C) НЕ перехватываем: стандартный KeyboardInterrupt даёт
# отработать finally-блокам (отмонтирование RAM-диска) и atexit,
# а сам ffmpeg добивается в __main__ через terminate_all_processes().
atexit.register(terminate_all_processes)
for _sig in ('SIGTERM',):
    if hasattr(signal, _sig):
        try:
            signal.signal(getattr(signal, _sig), _graceful_exit_handler)
        except Exception:
            pass
if hasattr(signal, 'SIGBREAK'):
    try:
        signal.signal(signal.SIGBREAK, _graceful_exit_handler)
    except Exception:
        pass

def check_and_install_dependencies():
    """Проверяет и устанавливает необходимые зависимости"""
    required_packages = {
        'rich': 'rich'
    }
    
    missing_packages = []
    
    # Проверяем какие пакеты отсутствуют
    for package_name, pip_name in required_packages.items():
        try:
            __import__(package_name)
            # Не показываем сообщение если всё в порядке
        except ImportError:
            missing_packages.append((package_name, pip_name))
    
    # Если есть отсутствующие пакеты, показываем информацию и предлагаем установить
    if missing_packages:
        if RICH_AVAILABLE:
            console.print(f"\n[bold yellow]⚠ Обнаружены отсутствующие зависимости:[/]")
            for package_name, pip_name in missing_packages:
                console.print(f"  ❌ [red]{package_name}[/red] - не установлен")
            
            install_choice = Prompt.ask(
                "\n🔧 [bold magenta]Установить отсутствующие зависимости?[/] [1-Да/2-Нет]", 
                choices=["1", "2"], 
                default="1"
            )
        else:
            print(f"\n⚠ Обнаружены отсутствующие зависимости:")
            for package_name, pip_name in missing_packages:
                print(f"  ❌ {package_name} - не установлен")
            
            install_choice = input("\n🔧 Установить отсутствующие зависимости? [1-Да/2-Нет] (по умолчанию 1): ").strip()
            if not install_choice:
                install_choice = "1"
        
        if install_choice == "1":
            if RICH_AVAILABLE:
                console.print("\n[bold magenta]📦 Установка зависимостей...[/]")
            else:
                print("\n📦 Установка зависимостей...")
            
            for package_name, pip_name in missing_packages:
                try:
                    if RICH_AVAILABLE:
                        console.print(f"  Устанавливаю [cyan]{package_name}[/cyan]...")
                    else:
                        print(f"  Устанавливаю {package_name}...")
                    
                    subprocess.check_call([
                        sys.executable, "-m", "pip", "install", pip_name
                    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    
                    if RICH_AVAILABLE:
                        console.print(f"  ✅ [green]{package_name}[/green] установлен успешно")
                    else:
                        print(f"  ✅ {package_name} установлен успешно")
                except subprocess.CalledProcessError:
                    if RICH_AVAILABLE:
                        console.print(f"  ❌ [red]Ошибка при установке {package_name}[/red]")
                        console.print(f"  Попробуйте установить вручную: [cyan]pip install {pip_name}[/cyan]")
                    else:
                        print(f"  ❌ Ошибка при установке {package_name}")
                        print(f"  Попробуйте установить вручную: pip install {pip_name}")
                    return False
            
            if RICH_AVAILABLE:
                console.print("\n[bold green]🎉 Все зависимости установлены! Перезапустите скрипт.[/]")
            else:
                print("\n🎉 Все зависимости установлены! Перезапустите скрипт.")
            return False
        else:
            if RICH_AVAILABLE:
                console.print("\n[bold yellow]⚠ Некоторые функции могут работать некорректно без установленных зависимостей.[/]")
            else:
                print("\n⚠ Некоторые функции могут работать некорректно без установленных зависимостей.")
    
    # Проверяем системные зависимости
    # Проверяем именно те бинарники, которые реально будут использоваться
    # (FFMPEG_PATH/FFPROBE_PATH можно переопределить через переменные окружения)
    system_deps = {
        'ffmpeg': FFMPEG_PATH,
        'ffprobe': FFPROBE_PATH
    }
    
    missing_system = []
    for dep_name, command in system_deps.items():
        try:
            subprocess.run([command, "-version"], 
                         stdout=subprocess.DEVNULL, 
                         stderr=subprocess.DEVNULL, 
                         check=True)
            # Не показываем сообщение если всё в порядке
        except (subprocess.CalledProcessError, FileNotFoundError):
            missing_system.append(dep_name)
    
    # Показываем информацию только если есть проблемы
    if missing_system:
        if RICH_AVAILABLE:
            console.print(f"\n[bold yellow]⚠ Отсутствуют системные зависимости:[/]")
            for dep in missing_system:
                console.print(f"  ❌ [red]{dep}[/red] - не найден")
            console.print("\n[bold magenta]📥 Скачайте и установите FFmpeg с сайта:[/] [cyan]https://ffmpeg.org/download.html[/cyan]")
            console.print("   Или используйте менеджер пакетов вашей системы.")
        else:
            print(f"\n⚠ Отсутствуют системные зависимости:")
            for dep in missing_system:
                print(f"  ❌ {dep} - не найден")
            print("\n📥 Скачайте и установите FFmpeg с сайта: https://ffmpeg.org/download.html")
            print("   Или используйте менеджер пакетов вашей системы.")
    
    # Показываем сообщение об успешной проверке только если были проблемы
    if missing_packages or missing_system:
        if RICH_AVAILABLE:
            console.print(f"\n[bold green]✅ Проверка зависимостей завершена[/]")
        else:
            print(f"\n✅ Проверка зависимостей завершена")
    
    return True

def check_rich_availability():
    """Проверяет доступность rich и перепривязывает модульные имена если нужно"""
    global RICH_AVAILABLE, console
    global Console, Table, Panel, Prompt, Progress, SpinnerColumn, TextColumn
    global BarColumn, TimeRemainingColumn, Layout, Text, Live, box
    try:
        from rich.console import Console
        from rich.table import Table
        from rich.panel import Panel
        from rich.prompt import Prompt
        from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TimeRemainingColumn
        from rich.layout import Layout
        from rich.text import Text
        from rich.live import Live
        from rich import box
        
        # Если rich стал доступен, обновляем console
        console = Console()
        RICH_AVAILABLE = True
        return True
    except ImportError:
        RICH_AVAILABLE = False
        return False

def clear_screen():
    os.system('cls' if os.name == 'nt' else 'clear')

def restart_script():
    """Исторически называется restart, но на деле просто завершает скрипт."""
    sys.exit(0)


def clear_readonly(path):
    """
    Снимает атрибут «только для чтения» с файла или папки.
    На Windows os.chmod(..., S_IWRITE) сбрасывает read-only-атрибут;
    на POSIX добавляет владельцу право на запись. Ошибки глушатся.
    """
    try:
        if os.name == 'nt':
            os.chmod(path, stat.S_IWRITE)
        else:
            mode = os.stat(path).st_mode
            os.chmod(path, mode | stat.S_IWUSR)
    except Exception:
        pass


def clear_readonly_tree(path):
    """Снимает read-only со всей папки рекурсивно (сама папка + все файлы/подпапки)."""
    try:
        clear_readonly(path)
        for root, dirs, filenames in os.walk(path):
            for d in dirs:
                clear_readonly(os.path.join(root, d))
            for fn in filenames:
                clear_readonly(os.path.join(root, fn))
    except Exception:
        pass

# =====================
# Функции первого скрипта (обработка аудио/видео)
# =====================

def check_files(input_folder, extensions):
    # Сравниваем расширения регистронезависимо (.MKV, .Mp4 и т.п.)
    files = [f for f in os.listdir(input_folder) if Path(f).suffix.lower() in extensions]
    if not files:
        console.print(Panel("Нет подходящих файлов в папке.", 
                           title="❌ Ошибка", 
                           border_style="red", 
                           box=box.DOUBLE))
        sys.exit(1)
    return files

def list_audio_tracks(file):
    command = [FFMPEG_PATH, "-i", file]
    result = subprocess.run(command, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
    lines = result.stderr.split('\n')
    audio_tracks = [line for line in lines if "Audio:" in line]
    
    table = Table(title=f"🎵 Аудиодорожки в файле [bold green]{file}[/bold green]", box=box.ROUNDED)
    table.add_column("ID", justify="center", style="cyan", no_wrap=True)
    table.add_column("Описание", style="magenta")
    
    for idx, track in enumerate(audio_tracks, start=1):
        table.add_row(str(idx), track.strip())
    
    console.print(table)
    return len(audio_tracks)

def get_track_id():
    while True:
        track_id = Prompt.ask("🔊 [bold magenta]Введите ID аудиодорожки[/]")
        if track_id.isdigit() and int(track_id) > 0:
            return int(track_id) - 1
        console.print("[bold red]❌ Ошибка:[/] Введите корректный числовой ID (начиная с 1).")

def process_output_filename(file):
    return file.replace('_', ' ')

def keep_video_only(input_folder, output_folder):
    clear_screen()
    os.makedirs(output_folder, exist_ok=True)
    clear_readonly(output_folder)
    extensions = [".mkv", ".mp4", ".hevc", ".avi", ".h264", ".m2ts", ".ogm", ".mpg", ".mov"]
    files = check_files(input_folder, extensions)
    
    console.print(Panel(f"Найдено [bold magenta]{len(files)}[/] видеофайлов", 
                       title="🎬 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))

    # Проверка уже существующих обработанных файлов
    skipped_files = []
    files_to_process = []
    
    for file in files:
        output_path = os.path.join(output_folder, process_output_filename(file))
        if os.path.exists(output_path) and os.path.getsize(output_path) > 0:
            skipped_files.append(file)
        else:
            files_to_process.append(file)
    
    if skipped_files:
        console.print(Panel(f"Найдено [bold yellow]{len(skipped_files)}[/] уже обработанных файлов, они будут пропущены", 
                           title="⏩ Пропуск", 
                           border_style="yellow",
                           box=box.ROUNDED))
    
    if not files_to_process:
        console.print(Panel("Все файлы уже обработаны.", 
                           title="✅ Информация", 
                           border_style="green",
                           box=box.DOUBLE))
        restart_script()
        return

    console.print(Panel(f"Будет обработано [bold magenta]{len(files_to_process)}[/] файлов", 
                       title="🔄 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn()
    ) as progress:
        task = progress.add_task("🎬 [green]Обработка видео...", total=len(files_to_process))
        for idx, file in enumerate(files_to_process, 1):
            # Обновляем описание задачи с текущим файлом и прогрессом
            progress.update(task, description=f"🎬 [green]Обработка: [cyan]{file}[/cyan] ([bold magenta]{idx}/{len(files_to_process)}[/bold magenta])")
            
            input_path = os.path.join(input_folder, file)
            output_path = os.path.join(output_folder, process_output_filename(file))
            # Модифицируем команду, чтобы исключить субтитры, шрифты и прикрепленные картинки
            command = [FFMPEG_PATH, "-nostdin", "-y", "-i", input_path, "-map", "0:v:0", "-c:v", "copy", "-an", output_path]
            result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", check=False)
            if result.returncode != 0:
                log_file = write_process_error_log(Path(file).stem, result.stdout, command, result.returncode)
                details = f"Лог: {log_file}" if log_file else "Не удалось сохранить лог."
                console.print(f"[bold red]❌ Ошибка при обработке[/] [cyan]{file}[/cyan]\n{details}")
            elif os.path.exists(output_path):
                clear_readonly(output_path)
            progress.update(task, advance=1)
    
    console.print(Panel(f"Видео без аудиодорожек сохранено в [green]{output_folder}[/green]", 
                       title="✅ Успех", 
                       border_style="green",
                       box=box.DOUBLE))
    restart_script()

def sanitize_folder_name(name: str) -> str:
    """Очищает имя для использования в качестве имени папки"""
    clean = re.sub(r'[<>:"/\\|?*\x00-\x1F]', '_', name)
    clean = clean.strip().strip('.')
    return clean or "audio_track"


def _parse_track_ids(text: str) -> list[int]:
    """Разбирает ввод вида "1,3-5,8" в список 0-based ID дорожек.
    Диапазон "1-13" раскрывается в 1..13. Порядок сохраняется,
    дубликаты убираются. ValueError при некорректном вводе."""
    ids: list[int] = []
    for part in text.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            lo, hi = (int(x.strip()) for x in part.split("-", 1))
            if lo > hi:
                lo, hi = hi, lo
            ids.extend(range(lo - 1, hi))
        else:
            ids.append(int(part) - 1)
    seen: set[int] = set()
    return [i for i in ids if not (i in seen or seen.add(i))]


def _extract_audio_single_mka(input_folder, output_folder, files, track_ids, real_titles):
    """
    Режим пункта 2 «все дорожки в один .mka»: на каждую серию собирает
    один .mka с выбранными дорожками (порядок — как введён), без
    перекодирования. Тайтлы/язык настраиваются один раз, как в mka_muxer.
    """
    # --- тайтлы ---
    titles = {tid: real_titles.get(tid) for tid in track_ids}
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

        for out_i, tid in enumerate(track_ids, 1):
            default_t = real_titles.get(tid) or f"Track {tid + 1}"
            console.print(f"\n  [magenta]Дорожка {out_i}[/] [dim](исходная {tid + 1}: {default_t})[/]")
            pick = Prompt.ask("  Выбери номер из списка или [cyan]0[/] для ручного ввода", default="0")
            if pick.isdigit() and 1 <= int(pick) <= len(_VOICE_OPTIONS):
                chosen = _VOICE_OPTIONS[int(pick) - 1]
            else:
                chosen = Prompt.ask("  Тайтл дорожки", default=default_t)
            titles[tid] = chosen.strip() or default_t

    # --- язык ---
    langs = {}
    ans = Prompt.ask("🌐 [bold magenta]Проставить язык дорожек?[/] [1-Да/2-Нет]",
                     choices=["1", "2"], default="1")
    if ans == "1":
        default_lang = Prompt.ask("  Язык по умолчанию (ISO 639-2, напр. [cyan]rus[/])",
                                  default="rus").strip() or "rus"
        for tid in track_ids:
            text = " ".join(filter(None, (titles.get(tid), real_titles.get(tid))))
            langs[tid] = _detect_lang(text) or default_lang

    # --- план ---
    def _out_path(file):
        return os.path.join(output_folder, process_output_filename(file.rsplit('.', 1)[0]) + ".mka")

    files_to_process = [f for f in files
                        if not (os.path.exists(_out_path(f)) and os.path.getsize(_out_path(f)) > 0)]
    skipped = len(files) - len(files_to_process)
    if skipped:
        console.print(Panel(f"Найдено [bold yellow]{skipped}[/] уже обработанных файлов, они будут пропущены",
                            title="⏩ Пропуск", border_style="yellow", box=box.ROUNDED))
    if not files_to_process:
        console.print(Panel("Все файлы уже обработаны.", title="✅ Информация",
                            border_style="green", box=box.DOUBLE))
        return

    res_t = Table(title="✅ Дорожки в .mka", box=box.ROUNDED, border_style="green", title_style="bold green")
    res_t.add_column("Вых. #", justify="center", style="magenta")
    res_t.add_column("Исх. #", justify="center", style="cyan")
    res_t.add_column("Тайтл", style="bold white")
    res_t.add_column("Язык", justify="center", style="green")
    for out_i, tid in enumerate(track_ids, 1):
        res_t.add_row(str(out_i), str(tid + 1), titles.get(tid) or "[dim]—[/]", langs.get(tid) or "[dim]как в исходнике[/]")
    console.print(res_t)

    # --- сборка ---
    os.makedirs(output_folder, exist_ok=True)
    fail_count = 0
    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn()
    ) as progress:
        task = progress.add_task("📦 [green]Сборка .mka...", total=len(files_to_process))
        for idx, file in enumerate(files_to_process, 1):
            progress.update(task, description=f"📦 [green]{file}[/] ({idx}/{len(files_to_process)})")
            input_path = os.path.join(input_folder, file)
            output_path = _out_path(file)

            command = [FFMPEG_PATH, "-nostdin", "-y", "-i", input_path]
            for tid in track_ids:
                command += ["-map", f"0:a:{tid}"]
            command += ["-c:a", "copy", "-vn", "-sn", "-dn", "-map_chapters", "-1"]
            for out_i, tid in enumerate(track_ids):
                if titles.get(tid):
                    command += [f"-metadata:s:a:{out_i}", f"title={titles[tid]}"]
                if langs.get(tid):
                    command += [f"-metadata:s:a:{out_i}", f"language={langs[tid]}"]
                command += [f"-disposition:a:{out_i}", "default" if out_i == 0 else "none"]
            command.append(output_path)

            result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, encoding="utf-8", errors="replace", check=False)
            if result.returncode != 0:
                fail_count += 1
                log_file = write_process_error_log(Path(file).stem, result.stdout, command, result.returncode)
                details = f"Лог: {log_file}" if log_file else "Не удалось сохранить лог."
                console.print(f"[bold red]❌ Ошибка при сборке .mka[/] [cyan]{file}[/cyan]\n{details}")
            elif os.path.exists(output_path):
                clear_readonly(output_path)
            progress.update(task, advance=1)

    if fail_count:
        console.print(Panel(f"Собрано: {len(files_to_process) - fail_count}, [red]ошибок: {fail_count}[/]\n"
                            f"[dim]{output_folder}[/]",
                            title="⚠ Завершено с ошибками", border_style="yellow", box=box.DOUBLE))
    else:
        console.print(Panel(f"Дорожки собраны в .mka по сериям в [green]{output_folder}[/green]",
                            title="✅ Успех", border_style="green", box=box.DOUBLE))


def keep_audio_only(input_folder, output_folder):
    clear_screen()
    os.makedirs(output_folder, exist_ok=True)
    clear_readonly(output_folder)
    # .mka добавлен, чтобы можно было доставать нужные дорожки
    # из уже упакованных mka с несколькими аудиопотоками.
    extensions = [".mkv", ".mp4", ".hevc", ".avi", ".h264", ".m2ts", ".ogm", ".mpg", ".mov", ".mka"]
    files = check_files(input_folder, extensions)
    
    console.print(Panel(f"Найдено [bold magenta]{len(files)}[/] файлов", 
                       title="🎵 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    # Показываем аудиодорожки только для первого файла
    if files:
        first_file = os.path.join(input_folder, files[0])
        console.print(Panel(f"Показываем аудиодорожки первого файла: [bold cyan]{files[0]}[/]", 
                           border_style="magenta",
                           box=box.ROUNDED))
        list_audio_tracks(first_file)
        # --- Вывод title дорожек (как в 3 действии) ---
        command = [FFMPEG_PATH, "-i", first_file]
        result = subprocess.run(command, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
        lines = result.stderr.split('\n')
        audio_tracks = [line for line in lines if "Audio:" in line]
        ffprobe_titles = []
        try:
            ffprobe_cmd = [
                FFPROBE_PATH, "-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_tags=title", "-of", "json", first_file
            ]
            ffprobe_res = subprocess.run(ffprobe_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
            ffprobe_json = json.loads(ffprobe_res.stdout)
            if 'streams' in ffprobe_json:
                for stream in ffprobe_json['streams']:
                    idx = stream.get('index', None)
                    title = stream.get('tags', {}).get('title', None)
                    ffprobe_titles.append((idx, title))
        except Exception:
            ffprobe_titles = []
        # Фолбэк для QuickTime/MOV: если ffprobe не дал title, читаем имена
        # дорожек прямо из контейнера (udta/name) и подставляем по порядку.
        if not ffprobe_titles or any(not t for _, t in ffprobe_titles):
            if Path(first_file).suffix.lower() in ('.mov', '.qt', '.mp4', '.m4a', '.m4v'):
                mov_names = _read_mov_audio_titles(first_file)
                if mov_names:
                    if not ffprobe_titles:
                        # ffprobe ничего не дал — берём индексы потоков из вывода ffmpeg
                        ffprobe_titles = [
                            (int(m.group(1)), None) for m in
                            (re.search(r'Stream #0:(\d+)', d) for d in audio_tracks) if m
                        ]
                    ffprobe_titles = [
                        (idx, (t if t else (mov_names[pos] if pos < len(mov_names) else None)))
                        for pos, (idx, t) in enumerate(ffprobe_titles)
                    ]
        if audio_tracks:
            all_tracks = []
            for idx, desc in enumerate(audio_tracks, start=1):
                title = None
                stream_idx_match = re.search(r'Stream #0:(\d+)', desc)
                if stream_idx_match:
                    stream_idx = int(stream_idx_match.group(1))
                    for ff_idx, ff_title in ffprobe_titles:
                        if ff_idx == stream_idx and ff_title:
                            title = ff_title
                            break
                if title:
                    all_tracks.append(f"[cyan]{idx}[/]: {title}")
            if all_tracks:
                console.print(Panel("\n".join(all_tracks), title="Title всех аудиодорожек", border_style="magenta", box=box.ROUNDED))
            else:
                console.print(Panel("Нет названий (title) ни у одной аудиодорожки", title="Title всех аудиодорожек", border_style="magenta", box=box.ROUNDED))
        # --- конец вывода title дорожек ---
    
    # Получаем список выбранных дорожек
    track_ids_input = Prompt.ask(
        "🔊 [bold magenta]Выберите аудиодорожки (через запятую, диапазон: 1-13)[/]", default="1"
    )
    try:
        track_ids = _parse_track_ids(track_ids_input)
    except ValueError:
        console.print("[bold red]❌ Ошибка:[/] Введите корректные числовые ID через запятую (или диапазон вида 1-13).")
        restart_script()
        return

    if not track_ids:
        console.print("[bold red]❌ Ошибка:[/] Нужно выбрать хотя бы одну аудиодорожку.")
        restart_script()
        return

    multi_track_mode = len(track_ids) > 1

    # Все выбранные дорожки → один .mka на серию (как mka_muxer)
    single_mka = False
    if multi_track_mode:
        single_mka = Prompt.ask(
            "📦 [bold magenta]Как сохранить дорожки?[/]\n"
            "  [cyan]1[/] — отдельный файл на каждую дорожку\n"
            "  [cyan]2[/] — все выбранные дорожки в один .mka на серию",
            choices=["1", "2"], default="1"
        ) == "2"

    # Нумерация папок/файлов по порядковому номеру дорожки в исходнике —
    # нужна, чтобы потом перенести дорожки в другой файл, сохранив порядок.
    number_tracks = False
    if multi_track_mode and not single_mka:
        number_tracks = Prompt.ask(
            "🔢 [bold magenta]Добавлять порядковый номер дорожки к папкам и файлам?[/] [1-Да/2-Нет]",
            choices=["1", "2"], default="1"
        ) == "1"

    # Проверяем, что выбранные ID существуют
    invalid_ids = [tid for tid in track_ids if tid < 0 or tid >= len(audio_tracks)]
    if invalid_ids:
        console.print("[bold red]❌ Ошибка:[/] Выбран ID, отсутствующий в файле.")
        restart_script()
        return

    # Сопоставляем ID дорожек с названиями (title)
    track_titles = {}
    real_titles = {}  # настоящий title дорожки или None (без фолбэка на кодек)
    used_names = set()
    for idx in track_ids:
        title = None
        if 0 <= idx < len(audio_tracks):
            desc = audio_tracks[idx]
            stream_idx_match = re.search(r'Stream #0:(\d+)', desc)
            if stream_idx_match:
                stream_idx = int(stream_idx_match.group(1))
                for ff_idx, ff_title in ffprobe_titles:
                    if ff_idx == stream_idx and ff_title:
                        title = ff_title
                        break
        real_titles[idx] = title
        if not title:
            match = re.search(r'Audio: ([^,]+)', audio_tracks[idx]) if 0 <= idx < len(audio_tracks) else None
            title = match.group(1) if match else str(idx + 1)
        sanitized = sanitize_folder_name(title)
        if sanitized in used_names:
            sanitized = f"{sanitized}_{idx + 1}"
        used_names.add(sanitized)
        track_titles[idx] = sanitized

    if single_mka:
        _extract_audio_single_mka(input_folder, output_folder, files, track_ids, real_titles)
        restart_script()
        return

    # В многодорожечном режиме папка и файл (опционально) получают префикс
    # с порядковым номером дорожки в исходнике: "2. Title/2. Ep01.Title.mka".
    # Тайтл дорожки в конце имени файла сохраняется всегда.
    def _track_output(file, track_id):
        base_name = process_output_filename(file.rsplit('.', 1)[0])
        if not multi_track_mode:
            return output_folder, f"{base_name}.mka"
        num = f"{track_id + 1}. " if number_tracks else ""
        return (
            os.path.join(output_folder, f"{num}{track_titles[track_id]}"),
            f"{num}{base_name}.{track_titles[track_id]}.mka",
        )

    description_lines = [
        f"[cyan]{i+1}[/] → [green]{f'{i+1}. ' if number_tracks else ''}{track_titles[i]}[/]" for i in track_ids
    ]
    if not multi_track_mode:
        description_lines.append("[yellow]Файлы будут сохранены прямо в папке Audio only[/]")

    console.print(Panel(
        "Будут извлечены дорожки:\n" + "\n".join(description_lines),
        title="✅ Выбор подтвержден",
        border_style="green",
        box=box.ROUNDED
    ))

    # Проверка уже существующих обработанных файлов
    skipped_files = []
    files_to_process = []
    processing_plan = []
    
    for file in files:
        missing_tracks = []
        for track_id in track_ids:
            track_folder, output_filename = _track_output(file, track_id)
            output_path = os.path.join(track_folder, output_filename)
            if not (os.path.exists(output_path) and os.path.getsize(output_path) > 0):
                missing_tracks.append(track_id)
        if missing_tracks:
            files_to_process.append(file)
            processing_plan.append((file, missing_tracks))
        else:
            skipped_files.append(file)
    
    if skipped_files:
        console.print(Panel(f"Найдено [bold yellow]{len(skipped_files)}[/] уже обработанных файлов, они будут пропущены", 
                           title="⏩ Пропуск", 
                           border_style="yellow",
                           box=box.ROUNDED))
    
    if not processing_plan:
        console.print(Panel("Все файлы уже обработаны.", 
                           title="✅ Информация", 
                           border_style="green",
                           box=box.DOUBLE))
        restart_script()
        return

    console.print(Panel(f"Будет обработано [bold magenta]{len(processing_plan)}[/] файлов", 
                       title="🔄 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    total_tasks = sum(len(tracks) for _, tracks in processing_plan)
    
    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn()
    ) as progress:
        task = progress.add_task("🎵 [green]Извлечение аудио...", total=total_tasks)
        processed_files = 0
        for file, tracks in processing_plan:
            processed_files += 1
            input_path = os.path.join(input_folder, file)
            for idx, track_id in enumerate(tracks, 1):
                track_folder, output_filename = _track_output(file, track_id)
                os.makedirs(track_folder, exist_ok=True)
                clear_readonly(track_folder)
                output_path = os.path.join(track_folder, output_filename)
                
                progress.update(
                    task,
                    description=(
                        f"🎵 [green]{file}[/] → [cyan]{track_titles[track_id]}[/] "
                        f"({processed_files}/{len(processing_plan)})"
                    )
                )
                
                command = [FFMPEG_PATH, "-nostdin", "-y", "-i", input_path, "-map", f"0:a:{track_id}", "-c:a", "copy", output_path]
                result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", check=False)
                if result.returncode != 0:
                    log_file = write_process_error_log(Path(file).stem, result.stdout, command, result.returncode)
                    details = f"Лог: {log_file}" if log_file else "Не удалось сохранить лог."
                    console.print(f"[bold red]❌ Ошибка при извлечении аудио[/] [cyan]{file}[/cyan]\n{details}")
                elif os.path.exists(output_path):
                    clear_readonly(output_path)
                
                progress.update(task, advance=1)
    
    console.print(Panel(f"Аудиофайлы сохранены по дорожкам в [green]{output_folder}[/green]", 
                       title="✅ Успех", 
                       border_style="green",
                       box=box.DOUBLE))
    restart_script()


# ─── Чтение имён дорожек из QuickTime/MOV напрямую ────────────
# Экспорты DaVinci Resolve (и другие QuickTime-мувы) пишут имена
# аудиодорожек в атом moov→trak→udta→name (или ©nam), который
# ffmpeg/ffprobe НЕ отдаёт как stream title. MediaInfo читает именно его.
# Здесь — минимальный парсер боксов, без внешних зависимостей.

def _mov_boxes(f, start, end):
    """Список боксов [(type, data_start, data_end), ...] в диапазоне [start, end)."""
    boxes = []
    pos = start
    while pos < end:
        f.seek(pos)
        header = f.read(8)
        if len(header) < 8:
            break
        size = int.from_bytes(header[0:4], 'big')
        btype = header[4:8].decode('latin-1', 'replace')
        hdr = 8
        if size == 1:                      # 64-битный размер
            ext = f.read(8)
            if len(ext) < 8:
                break
            size = int.from_bytes(ext, 'big')
            hdr = 16
        elif size == 0:                    # до конца контейнера
            size = end - pos
        if size < hdr or pos + size > end: # усечённый/битый бокс
            break
        boxes.append((btype, pos + hdr, pos + size))
        pos += size
    return boxes


def _mov_decode_name(data):
    """Декодирует полезную нагрузку name/©nam атома в строку или None."""
    # Международный текст-атом (©-стиль): [size:2][lang:2][text]
    if len(data) >= 4:
        sz = int.from_bytes(data[0:2], 'big')
        if 0 < sz <= len(data) - 4:
            txt = data[4:4 + sz].decode('utf-8', 'replace').strip('\x00').strip()
            if txt:
                return txt
    # Обычный 'name'-атом: сырой текст (возможно с завершающим \x00)
    txt = data.decode('utf-8', 'replace').strip('\x00').strip()
    return txt or None


# Стандартные имена обработчиков из hdlr — это не пользовательские тайтлы.
_MOV_GENERIC_HANDLERS = (
    'soundhandler', 'sound handler', 'apple sound media handler', 'core media audio',
    'core media sound', 'mainconcept', 'audio', 'sound', 'isomediahandler', 'ffmpeg',
    'lavf', 'lavc', 'handler',
)


def _mov_decode_hdlr_name(data):
    """Имя обработчика из hdlr: в QuickTime это Pascal-строка (первый байт —
    длина), в ISO BMFF — C-строка с завершающим \\x00. Пробуем оба варианта."""
    if not data:
        return None
    if data[0] == len(data) - 1 or (data[0] < len(data) and data[-1] != 0):
        txt = data[1:1 + data[0]].decode('utf-8', 'replace').strip()
        if txt:
            return txt
    return data.decode('utf-8', 'replace').split('\x00', 1)[0].strip() or None


def _mov_track_name(f, trak_start, trak_end):
    """Возвращает (is_audio, name|None) для одного trak.
    Источники имени по приоритету:
      1. trak/udta/name (QuickTime Player, DaVinci, Premiere) или ©nam / titl;
      2. имя обработчика из mdia/hdlr, если оно не стандартное
         (некоторые экспортёры кладут имя дорожки именно туда)."""
    is_audio = False
    name = None
    hdlr_name = None
    for btype, ds, de in _mov_boxes(f, trak_start, trak_end):
        if btype == 'mdia':
            for b2, ds2, de2 in _mov_boxes(f, ds, de):
                if b2 == 'hdlr':
                    f.seek(ds2)
                    payload = f.read(de2 - ds2)
                    # version/flags(4) + predefined(4) + handler_type(4) + reserved(12) + name
                    if len(payload) >= 12 and payload[8:12] == b'soun':
                        is_audio = True
                    if len(payload) > 24:
                        hdlr_name = _mov_decode_hdlr_name(payload[24:])
        elif btype == 'udta':
            for b2, ds2, de2 in _mov_boxes(f, ds, de):
                if b2 in ('name', '\xa9nam', 'titl'):
                    f.seek(ds2)
                    nm = _mov_decode_name(f.read(de2 - ds2))
                    if nm:
                        name = nm
    if not name and hdlr_name and hdlr_name.lower() not in _MOV_GENERIC_HANDLERS:
        name = hdlr_name
    return is_audio, name


def _read_mov_audio_titles(path):
    """
    Имена аудиодорожек из QuickTime/MOV по порядку (a:0, a:1, ...).
    Возвращает [str|None, ...]; при любой ошибке — [].
    """
    try:
        with open(path, 'rb') as f:
            f.seek(0, 2)
            file_end = f.tell()
            names = []
            for btype, ds, de in _mov_boxes(f, 0, file_end):
                if btype == 'moov':
                    for b2, ds2, de2 in _mov_boxes(f, ds, de):
                        if b2 == 'trak':
                            is_audio, nm = _mov_track_name(f, ds2, de2)
                            if is_audio:
                                names.append(nm)
            return names
    except Exception:
        return []


_TRACK_NUM_PREFIX_RE = re.compile(r'^\d+\.\s*')


def _external_audio_matches(filename: str, base_name: str) -> bool:
    """Аудиофайл считается относящимся к видео base_name, если его имя
    начинается с base_name и дальше идёт разделитель (не буква/цифра) —
    иначе "Show - 01" ошибочно цепляет аудио от "Show - 011".
    Дополнительно принимается префикс "N. " из keep_audio_only
    (многодорожечное извлечение): "2. Show - 01.Title.mka" → "Show - 01".
    Сравнение делается и с исходным, и с обрезанным именем, чтобы
    видео вида "01. Show.mkv" продолжало матчиться без префикса."""
    for cand in (filename, _TRACK_NUM_PREFIX_RE.sub('', filename, count=1)):
        if (cand.startswith(base_name)
                and len(cand) > len(base_name)
                and not cand[len(base_name)].isalnum()):
            return True
    return False


def _natural_key(name: str):
    """Ключ сортировки: сначала по числовому префиксу "N. " (10 после 9,
    а не после 1), затем по имени без учёта регистра."""
    m = _TRACK_NUM_PREFIX_RE.match(name)
    return (int(m.group(0).split('.')[0]) if m else float('inf'), name.lower())


def _find_external_audio(input_folder: str, base_name: str, audio_extensions: list,
                         exclude_dirs: tuple = ()) -> list[str]:
    """Рекурсивно ищет внешние аудио для base_name в input_folder и всех
    его подпапках — включая структуру keep_audio_only без переноса:
    "Audio only/N. Title/N. Ep.Title.mka". Папки из exclude_dirs
    (например, выходная "Processed Audio") пропускаются.
    Порядок детерминирован (натуральная сортировка), поэтому один и тот же
    индекс в списке соответствует одной и той же дорожке для всех серий."""
    excluded = {os.path.normcase(os.path.abspath(d)) for d in exclude_dirs}
    found = []
    for root, dirs, files in os.walk(input_folder):
        dirs[:] = sorted(
            (d for d in dirs
             if not d.startswith('.')
             and os.path.normcase(os.path.abspath(os.path.join(root, d))) not in excluded),
            key=_natural_key,
        )
        for fn in sorted(files, key=_natural_key):
            if (any(fn.lower().endswith(ext) for ext in audio_extensions)
                    and _external_audio_matches(fn, base_name)):
                found.append(os.path.join(root, fn))
    return found


def _detect_lang(text: str):
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


def _ffprobe_audio_titles(path: str) -> list[str]:
    """Title каждой аудиодорожки файла по порядку ('' — если title нет).
    Длина списка = число аудиодорожек (пусто при ошибке)."""
    try:
        cmd = [FFPROBE_PATH, "-v", "error", "-select_streams", "a",
               "-show_entries", "stream=index:stream_tags=title", "-of", "json", path]
        r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           encoding='utf-8', errors='replace')
        streams = json.loads(r.stdout or "{}").get("streams", [])
        return [((s.get("tags") or {}).get("title") or "").strip() for s in streams]
    except Exception:
        return []


def advanced_audio_processing(input_folder, output_folder):
    """Универсальная функция для обработки аудиодорожек в видео: 
    позволяет выбрать несколько аудиодорожек из исходника и добавить внешние аудиофайлы"""
    
    clear_screen()
    os.makedirs(output_folder, exist_ok=True)
    clear_readonly(output_folder)
    
    video_extensions = [".mkv", ".mp4", ".hevc", ".avi", ".h264", ".m2ts", ".ogm", ".mpg", ".mov"]
    audio_extensions = [".mka", ".wav", ".mp3", ".ac3", ".dts", ".flac", ".aac", ".m4a"]
    
    video_files = check_files(input_folder, video_extensions)
    
    console.print(Panel(f"Найдено [bold magenta]{len(video_files)}[/] видеофайлов", 
                       title="🎬 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    # Проверка уже существующих обработанных файлов
    skipped_files = []
    files_to_process = []
    
    for video_file in video_files:
        # То же имя, под которым файл сохраняется ниже (с заменой "_" на пробелы),
        # иначе файлы с подчёркиваниями никогда не считались готовыми
        output_path = os.path.join(output_folder, process_output_filename(video_file))
        if os.path.exists(output_path) and os.path.getsize(output_path) > 0:
            skipped_files.append(video_file)
        else:
            files_to_process.append(video_file)
    
    if skipped_files:
        console.print(Panel(f"Найдено [bold yellow]{len(skipped_files)}[/] уже обработанных файлов, они будут пропущены", 
                           title="⏩ Пропуск", 
                           border_style="yellow",
                           box=box.ROUNDED))
    
    if not files_to_process:
        console.print(Panel("Все файлы уже обработаны.", 
                           title="✅ Информация", 
                           border_style="green",
                           box=box.DOUBLE))
        restart_script()
        return
    
    console.print(Panel(f"Будет обработано [bold magenta]{len(files_to_process)}[/] файлов", 
                       title="🔄 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    # Показываем аудиодорожки в первом видеофайле для выбора
    audio_tracks = []
    if files_to_process:
        first_file_path = os.path.join(input_folder, files_to_process[0])
        num_tracks = list_audio_tracks(first_file_path)
        # Выводим список всех названий/описаний дорожек
        command = [FFMPEG_PATH, "-i", first_file_path]
        result = subprocess.run(command, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
        lines = result.stderr.split('\n')
        audio_tracks = [line for line in lines if "Audio:" in line]
        # Попробуем получить title через ffprobe
        ffprobe_titles = []
        try:
            ffprobe_cmd = [
                FFPROBE_PATH, "-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_tags=title", "-of", "json", first_file_path
            ]
            ffprobe_res = subprocess.run(ffprobe_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
            ffprobe_json = json.loads(ffprobe_res.stdout)
            if 'streams' in ffprobe_json:
                for stream in ffprobe_json['streams']:
                    idx = stream.get('index', None)
                    title = stream.get('tags', {}).get('title', None)
                    ffprobe_titles.append((idx, title))
        except Exception:
            ffprobe_titles = []
        # Фолбэк для QuickTime/MOV: если ffprobe не дал title, читаем имена
        # дорожек прямо из контейнера (udta/name) и подставляем по порядку.
        if not ffprobe_titles or any(not t for _, t in ffprobe_titles):
            if Path(first_file_path).suffix.lower() in ('.mov', '.qt', '.mp4', '.m4a', '.m4v'):
                mov_names = _read_mov_audio_titles(first_file_path)
                if mov_names:
                    if not ffprobe_titles:
                        # ffprobe ничего не дал — берём индексы потоков из вывода ffmpeg
                        ffprobe_titles = [
                            (int(m.group(1)), None) for m in
                            (re.search(r'Stream #0:(\d+)', d) for d in audio_tracks) if m
                        ]
                    ffprobe_titles = [
                        (idx, (t if t else (mov_names[pos] if pos < len(mov_names) else None)))
                        for pos, (idx, t) in enumerate(ffprobe_titles)
                    ]
        if audio_tracks:
            all_tracks = []
            for idx, desc in enumerate(audio_tracks, start=1):
                # Пытаемся найти title по индексу дорожки
                title = None
                # Вытаскиваем индекс дорожки из строки типа Stream #0:1
                stream_idx_match = re.search(r'Stream #0:(\d+)', desc)
                if stream_idx_match:
                    stream_idx = int(stream_idx_match.group(1))
                    for ff_idx, ff_title in ffprobe_titles:
                        if ff_idx == stream_idx and ff_title:
                            title = ff_title
                            break
                if title:
                    all_tracks.append(f"[cyan]{idx}[/]: {title}")
            if all_tracks:
                console.print(Panel("\n".join(all_tracks), title="Title всех аудиодорожек", border_style="magenta", box=box.ROUNDED))
            else:
                console.print(Panel("Нет названий (title) ни у одной аудиодорожки", title="Title всех аудиодорожек", border_style="magenta", box=box.ROUNDED))
    
    # Выбор аудиодорожек из исходного видео
    console.print(Panel("[bold yellow]Выберите аудиодорожки из исходного видео:[/]\n" +
                        "- Введите ID через запятую или диапазон (например: 1,3,4 или 1-13)\n" +
                        "- [bold green]Первая введенная дорожка автоматически станет основной[/]\n" +
                        "- Введите 0, чтобы не сохранять аудио из исходника", 
                       border_style="yellow",
                       box=box.ROUNDED))
    
    track_ids_input = Prompt.ask("🔊 [bold magenta]Выберите аудиодорожки из исходника[/] [dim](напр. 1,3-5)[/]", default="0")
    
    # Парсим ввод для получения списка ID дорожек (поддерживаются диапазоны 1-13)
    track_ids = []
    if track_ids_input.strip() != "0":
        try:
            track_ids = _parse_track_ids(track_ids_input)
            if any(track_id < 0 for track_id in track_ids):
                raise ValueError("ID аудиодорожки должен быть положительным числом")
        except ValueError:
            console.print("[bold red]❌ Ошибка:[/] Введите корректные ID аудиодорожек через запятую (или диапазон 1-13), либо 0")
            restart_script()
            return
        # Верхняя граница: раньше ID вне диапазона приводил к падению ffmpeg на каждом файле
        if audio_tracks and any(tid >= len(audio_tracks) for tid in track_ids):
            console.print(f"[bold red]❌ Ошибка:[/] В файле только {len(audio_tracks)} аудиодорожек — выбран несуществующий ID.")
            restart_script()
            return
    # --- Новый порядок: сначала спрашиваем про внешние озвучки ---
    use_external_audio = Prompt.ask(
        "🎵 [bold magenta]Добавить внешние аудиофайлы?[/] [1-Да/2-Нет]", 
        choices=["1", "2"], 
        default="2"
    )
    # Проверка наличия аудиодорожек (выбранных или внешних)
    if not track_ids and use_external_audio != "1":
        console.print("[bold red]❌ Ошибка:[/] Не выбрано ни одной аудиодорожки и отключено добавление внешних файлов")
        restart_script()
        return
    
    # --- Конец блока выбора основной дорожки ---
    
   
    
    # Показываем информацию о выбранном режиме
    if track_ids:
        console.print(Panel(
            f"Выбрано [bold magenta]{len(track_ids)}[/] аудиодорожек из исходника: [green]{', '.join(str(idx+1) for idx in track_ids)}[/]", 
            border_style="green",
            box=box.ROUNDED
        ))
    else:
        console.print(Panel(
            "Аудиодорожки из исходника [bold red]не выбраны[/]", 
            border_style="yellow",
            box=box.ROUNDED
        ))
    
    # --- Собираем все аудио-слоты (внутренние + внешние) ---
    # Каждый слот: {'type': 'internal'|'external', 'track_id': int|None,
    #               'ext_idx': int|None, 'label': str, 'default_title': str}
    all_audio_slots = []

    for tid in track_ids:
        cur_title = None
        if 0 <= tid < len(audio_tracks):
            desc = audio_tracks[tid]
            m = re.search(r'Stream #0:(\d+)', desc)
            if m:
                si = int(m.group(1))
                for fi, ft in ffprobe_titles:
                    if fi == si and ft:
                        cur_title = ft
                        break
        default_t = cur_title or f"Track {tid + 1}"
        all_audio_slots.append({
            'type': 'internal', 'track_id': tid, 'ext_idx': None,
            'label': f"Внутр. дорожка {tid + 1}: {default_t}",
            'default_title': default_t,
            'has_title': bool(cur_title),  # реальный тайтл, а не заглушка "Track N"
        })

    if use_external_audio == "1" and files_to_process:
        first_base = files_to_process[0].rsplit('.', 1)[0]
        ext_scan_idx = 0
        for audio_path_preview in _find_external_audio(input_folder, first_base, audio_extensions, (output_folder,)):
            filename = os.path.relpath(audio_path_preview, input_folder)
            # Файл может содержать несколько дорожек (напр. .mka со всеми
            # озвучками) — каждая дорожка становится отдельным слотом,
            # иначе тайтлы/язык/порядок съезжают.
            ext_titles = _ffprobe_audio_titles(audio_path_preview) or [""]
            multi = len(ext_titles) > 1
            for stream_k, ext_def in enumerate(ext_titles):
                has_ext_title = bool(ext_def)
                if not ext_def:
                    ext_def = os.path.basename(audio_path_preview)
                    if multi:
                        ext_def += f" #{stream_k + 1}"
                label = f"Внешний файл: {filename}"
                if multi:
                    label += f" [дорожка {stream_k + 1}: {ext_def}]"
                all_audio_slots.append({
                    'type': 'external', 'track_id': None, 'ext_idx': ext_scan_idx,
                    'ext_stream': stream_k,
                    'label': label,
                    'default_title': ext_def,
                    'has_title': has_ext_title,
                })
            ext_scan_idx += 1

    # --- Выбор порядка дорожек ---
    ordered_audio_slots = list(all_audio_slots)  # по умолчанию — исходный порядок

    if len(all_audio_slots) > 1:
        order_t = Table(
            title="🎵 Все аудиодорожки",
            box=box.ROUNDED, border_style="cyan", title_style="bold cyan"
        )
        order_t.add_column("#", justify="center", style="magenta", no_wrap=True)
        order_t.add_column("Дорожка", style="bold white")
        for i, slot in enumerate(all_audio_slots, 1):
            order_t.add_row(str(i), slot['label'])
        console.print(order_t)

        reorder_ans = Prompt.ask(
            "🔀 [bold magenta]Изменить порядок дорожек?[/] [1-Да/2-Нет]",
            choices=["1", "2"], default="2"
        )
        if reorder_ans == "1":
            while True:
                order_input = Prompt.ask(
                    f"  Введите номера через запятую (напр. [cyan]2,1,3[/])\n  "
                    f"[dim]Доступны: 1–{len(all_audio_slots)}[/]"
                )
                try:
                    order_nums = [int(x.strip()) for x in order_input.split(",")]
                    if (len(order_nums) == len(all_audio_slots)
                            and sorted(order_nums) == list(range(1, len(all_audio_slots) + 1))):
                        ordered_audio_slots = [all_audio_slots[n - 1] for n in order_nums]
                        break
                    else:
                        console.print(
                            f"  [red]Нужно ввести все {len(all_audio_slots)} номера без повторов.[/]"
                        )
                except ValueError:
                    console.print("  [red]Введите числа через запятую.[/]")

        # Показываем итоговый порядок
        res_t = Table(
            title="✅ Итоговый порядок дорожек",
            box=box.ROUNDED, border_style="green", title_style="bold green"
        )
        res_t.add_column("Вых. #", justify="center", style="magenta")
        res_t.add_column("Дорожка", style="bold white")
        for i, slot in enumerate(ordered_audio_slots, 1):
            res_t.add_row(str(i), slot['label'])
        console.print(res_t)

    # --- Задать тайтлы аудиодорожкам ---
    audio_title_overrides = {}  # output_audio_index → title (по ordered_audio_slots)

    rename_answer = Prompt.ask(
        "✏ [bold magenta]Задать тайтлы аудиодорожкам?[/] [1-Да/2-Нет]",
        choices=["1", "2"], default="2"
    )
    if rename_answer == "1":
        # Показываем войс-лист один раз перед циклом
        vt = Table(title="🎙 Войс-лист", box=box.ROUNDED, border_style="cyan", title_style="bold cyan")
        vt.add_column("#", justify="center", style="magenta", no_wrap=True)
        vt.add_column("Озвучка", style="bold white")
        for vi, vname in enumerate(_VOICE_OPTIONS, 1):
            vt.add_row(str(vi), vname)
        vt.add_row("0", "[dim]Ввести вручную[/]")
        console.print(vt)

        for out_idx, slot in enumerate(ordered_audio_slots):
            console.print(f"\n  [magenta]Дорожка {out_idx + 1}[/] [dim]({slot['label']})[/]")
            pick_v = Prompt.ask(
                "  Выбери номер из списка или [cyan]0[/] для ручного ввода",
                default="0"
            )
            if pick_v.isdigit() and 1 <= int(pick_v) <= len(_VOICE_OPTIONS):
                chosen_title = _VOICE_OPTIONS[int(pick_v) - 1]
            else:
                chosen_title = Prompt.ask(
                    f"  Тайтл для дорожки {out_idx + 1}",
                    default=slot['default_title']
                )
            audio_title_overrides[out_idx] = chosen_title.strip() or slot['default_title']

    # --- Язык аудиодорожек ---
    lang_by_slot = {}  # output_audio_index → 'rus'|'eng'|'jpn'|…
    lang_answer = Prompt.ask(
        "🌐 [bold magenta]Проставить язык дорожек?[/] [1-Да/2-Нет]",
        choices=["1", "2"], default="1"
    )
    if lang_answer == "1":
        default_lang = Prompt.ask(
            "  Язык по умолчанию (ISO 639-2, напр. [cyan]rus[/])", default="rus"
        ).strip() or "rus"
        for out_idx, slot in enumerate(ordered_audio_slots):
            text = " ".join(filter(None, (
                slot['label'],
                audio_title_overrides.get(out_idx),
                slot['default_title'] if slot.get('has_title') else None,
            )))
            lang_by_slot[out_idx] = _detect_lang(text) or default_lang

    # --- ЛОГИРОВАНИЕ по каждому файлу ---
    # (убираю старое логирование до цикла)
    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn()
    ) as progress:
        task = progress.add_task("\U0001F501 [green]Обработка видео...", total=len(files_to_process))
        for idx, video_file in enumerate(files_to_process, 1):
            base_name = video_file.rsplit('.', 1)[0]
            video_path = os.path.join(input_folder, video_file)
            output_path = os.path.join(output_folder, process_output_filename(video_file))
            # Находим внешние аудиофайлы (sorted — порядок соответствует ext_idx)
            ext_paths_sorted = []
            if use_external_audio == "1":
                ext_paths_sorted = _find_external_audio(input_folder, base_name, audio_extensions, (output_folder,))

            # Для обратной совместимости: audio_files нужен только для счётчика в описании
            audio_files = ext_paths_sorted
            # Формируем список выбранных дорожек с названиями для этого файла
            selected_tracks_log = []
            if track_ids and audio_tracks:
                for tid in track_ids:
                    if 0 <= tid < len(audio_tracks):
                        desc = audio_tracks[tid]
                        stream_idx_match = re.search(r'Stream #0:(\d+)', desc)
                        title = None
                        if stream_idx_match:
                            stream_idx = int(stream_idx_match.group(1))
                            for ff_idx, ff_title in ffprobe_titles:
                                if ff_idx == stream_idx and ff_title:
                                    title = ff_title
                                    break
                        if title:
                            name = title
                        else:
                            match = re.search(r'Audio: ([^,]+)', desc)
                            name = match.group(1) if match else desc.strip()
                        selected_tracks_log.append(name)
            # --- КОНЕЦ ЛОГИРОВАНИЯ ---
            
            # Обновляем описание задачи
            audio_count_text = f"[yellow]{len(audio_files)}[/] внешних аудио" if audio_files else "[yellow]без внешних аудио[/]"
            track_count_text = f"[green]{len(track_ids)}[/] исходных дорожек" if track_ids else "[red]без исходных дорожек[/]"
            progress.update(task, description=f"\U0001F501 [green]Обработка: [cyan]{video_file}[/cyan] ({track_count_text}, {audio_count_text}) ([bold magenta]{idx}/{len(files_to_process)}[/bold magenta])")
            
            # Сложный случай: есть внешние аудиофайлы
            # Создаем команду с добавлением всех аудиодорожек
            command = [FFMPEG_PATH, "-nostdin", "-y", "-i", video_path]

            # Добавляем все внешние файлы как входящие потоки (в sorted-порядке,
            # чтобы ext_idx из ordered_audio_slots соответствовал индексу входа)
            for ext_path in ext_paths_sorted:
                command.extend(["-i", ext_path])

            # Добавляем основной видеопоток, если он есть.
            # Трейлинг '?' делает map необязательным: для аудио-only источников
            # (например многодорожечный .mov/.mka-экспорт из DaVinci без видеоряда)
            # ffmpeg иначе падает с "Stream map '' matches no streams" на 0:v:0.
            command.extend(["-map", "0:v:0?"])

            # Маппинг аудио в порядке ordered_audio_slots
            for slot in ordered_audio_slots:
                if slot['type'] == 'internal':
                    command.extend(["-map", f"0:a:{slot['track_id']}"])
                else:
                    # ext_idx + 1, потому что вход 0 — исходный файл;
                    # ровно одна дорожка на слот, чтобы индексы не съезжали
                    command.extend(["-map", f"{slot['ext_idx'] + 1}:a:{slot['ext_stream']}"])

            # Disposition: первый слот — default, остальные — none
            for i in range(len(ordered_audio_slots)):
                flag = "default" if i == 0 else "none"
                command.extend([f"-disposition:a:{i}", flag])

            # Тайтлы аудиодорожек. Если пользователь не задавал свои — явно
            # переносим существующие: у MOV тайтлы лежат в trak/udta/name,
            # ffmpeg при ремуксе их не подхватывает и после смены порядка
            # они терялись. Ставим только реальные тайтлы, не заглушки.
            for out_i, slot in enumerate(ordered_audio_slots):
                if out_i in audio_title_overrides:
                    command += [f"-metadata:s:a:{out_i}", f"title={audio_title_overrides[out_i]}"]
                elif slot.get('has_title'):
                    command += [f"-metadata:s:a:{out_i}", f"title={slot['default_title']}"]

            # Язык аудиодорожек. У внешних файлов тайтл в метаданных
            # самой дорожки этой серии (eng / jp) важнее общей настройки слота.
            ext_titles_cache = {}
            for out_i, slot in enumerate(ordered_audio_slots):
                lang = lang_by_slot.get(out_i)
                if not lang:
                    continue
                if slot['type'] == 'external' and slot['ext_idx'] < len(ext_paths_sorted):
                    ei = slot['ext_idx']
                    if ei not in ext_titles_cache:
                        ext_titles_cache[ei] = _ffprobe_audio_titles(ext_paths_sorted[ei])
                    ts = ext_titles_cache[ei]
                    k = slot['ext_stream']
                    lang = _detect_lang(ts[k] if k < len(ts) else "") or lang
                command += [f"-metadata:s:a:{out_i}", f"language={lang}"]

            # Копируем видео и аудио без перекодирования
            command.extend(["-c:v", "copy", "-c:a", "copy", output_path])
            
            # Выполняем команду
            try:
                result = subprocess.run(
                    command,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    check=False,
                )
                if result.returncode != 0:
                    log_file = write_process_error_log(Path(video_file).stem, result.stdout, command, result.returncode)
                    details = f"Лог: {log_file}" if log_file else "Не удалось сохранить лог."
                    console.print(f"[bold red]❌ Ошибка при обработке[/] [cyan]{video_file}[/cyan]\n{details}")
                elif os.path.exists(output_path):
                    clear_readonly(output_path)
            except Exception as e:
                log_error(e)
                console.print(f"[bold red]❌ Непредвиденная ошибка при обработке[/] [cyan]{video_file}[/cyan]")
            
            progress.update(task, advance=1)
    
    console.print(Panel("Видео успешно обработано с выбранными аудиодорожками.", 
                       title="✅ Успех", 
                       border_style="green",
                       box=box.DOUBLE))
    
    restart_script()

def get_video_duration(file_path):
    """Получает длительность видео в секундах с помощью ffprobe"""
    try:
        # Используем ffprobe для получения длительности
        cmd = [FFPROBE_PATH, "-v", "error", "-show_entries", "format=duration", 
               "-of", "default=noprint_wrappers=1:nokey=1", str(file_path)]
        result = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        
        # Пробуем получить длительность из вывода ffprobe
        if result.stdout.strip():
            try:
                return float(result.stdout.strip())
            except ValueError:
                pass
        
        # Если ffprobe не вернул результат, используем вывод ffmpeg
        cmd = [FFMPEG_PATH, "-i", str(file_path)]
        result = subprocess.run(
            cmd,
            stderr=subprocess.PIPE,
            stdout=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        
        # Ищем строку с длительностью (Duration)
        duration_match = re.search(r"Duration: (\d{2}):(\d{2}):(\d{2})\.(\d{2})", result.stderr)
        if duration_match:
            hours, minutes, seconds, centiseconds = map(int, duration_match.groups())
            total_seconds = hours * 3600 + minutes * 60 + seconds + centiseconds / 100
            return total_seconds
        
        # Если ничего не помогло, возвращаем примерное значение в 1 час
        return 3600
    except Exception as e:
        console.print(f"[bold yellow]⚠ Ошибка при получении длительности видео:[/] {str(e)}")
        return 3600  # Возвращаем примерное значение в 1 час

def run_ffmpeg(cmd, progress=None, task_id=None, file_name=None, duration=0, file_index=None, total_files=None, is_hls=False):
    """Запускает FFmpeg и анализирует вывод для получения информации о скорости и прогрессе"""
    creationflags = 0
    if os.name == 'nt' and hasattr(subprocess, 'CREATE_NEW_PROCESS_GROUP'):
        creationflags = subprocess.CREATE_NEW_PROCESS_GROUP
    process = subprocess.Popen(
        cmd,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
        encoding="utf-8",
        errors="replace",
        creationflags=creationflags,
    )
    register_process(process)
    
    # Текущее время начала обработки файла
    current_time = datetime.datetime.now().strftime("%H:%M:%S")
    
    # Начальное сообщение о конвертации отображаем только при передаче task_id
    if progress and task_id is not None and file_name:
        # Информация о прогрессе по файлам
        file_progress = f" ([bold magenta]{file_index}/{total_files}[/bold magenta])" if file_index and total_files else ""
        # Начальное сообщение о конвертации
        progress.update(task_id, description=f"[{current_time}] 🔄 Конвертация: [cyan]{file_name}[/]{file_progress}")
    
    # Ищем информацию о скорости, времени и битрейте в выводе ffmpeg
    speed_pattern = re.compile(r'speed=\s*(\d+\.?\d*)x')
    time_pattern = re.compile(r'time=(\d+:\d+:\d+\.\d+)')
    bitrate_pattern = re.compile(r'bitrate=\s*(\d+\.?\d*\w*\/s)')
    
    # Создаем отдельную задачу для отслеживания прогресса конкретного файла
    file_task_id = None
    if progress and duration > 0:
        # Добавляем информацию о файле в заголовок задачи если доступно
        file_info = f"[cyan]{file_name}[/]"
        if file_index and total_files:
            file_info += f" [bold magenta]({file_index}/{total_files})[/bold magenta]"
        file_task_description = f"{file_info} • 0%"
        file_task_id = progress.add_task(file_task_description, total=duration)
    
    # Храним только хвост stderr: полный лог прогресса ffmpeg при 6 выходах
    # разрастается до десятков МБ, а для диагностики нужны последние строки.
    stderr_lines = deque(maxlen=400)
    for line in process.stderr:
        stderr_lines.append(line)
        speed_match = speed_pattern.search(line)
        time_match = time_pattern.search(line)
        bitrate_match = bitrate_pattern.search(line)
        
        if progress and (speed_match or time_match or bitrate_match):
            speed = speed_match.group(1) if speed_match else "N/A"
            current_time_pos = time_match.group(1) if time_match else "00:00:00.00"
            bitrate = bitrate_match.group(1) if bitrate_match else "N/A"
            
            # Преобразуем время в секунды
            time_parts = current_time_pos.split(':')
            seconds = 0
            if len(time_parts) == 3:
                h, m, s = time_parts
                # Учитываем, что s может содержать миллисекунды
                s = s.split('.')[0]
                seconds = int(h) * 3600 + int(m) * 60 + int(s)
            
            # Обновляем информацию о файле
            current_time = datetime.datetime.now().strftime("%H:%M:%S")
            
            # Обновляем описание основной задачи (только если задан task_id)
            if task_id is not None:
                file_progress = f" ([bold magenta]{file_index}/{total_files}[/bold magenta])" if file_index and total_files else ""
                if is_hls:
                    progress.update(task_id, description=f"[{current_time}] 🔄 [cyan]{file_name}[/] [yellow]скорость: x{speed}[/]{file_progress}")
                else:
                    progress.update(task_id, description=f"[{current_time}] 🔄 [cyan]{file_name}[/] [yellow]скорость: x{speed}[/] • [blue]битрейт: {bitrate}[/]{file_progress}")
            
            # Обновляем прогресс файла, если известна длительность
            if file_task_id is not None and duration > 0:
                percent = int((seconds / duration) * 100) if duration > 0 else 0
                file_progress = f" • [bold magenta]Файл {file_index}/{total_files}[/bold magenta]" if file_index and total_files else ""
                
                # Разное отображение для HLS и других форматов
                if is_hls:
                    progress.update(
                        file_task_id, 
                        completed=seconds, 
                        description=f"[cyan]{file_name}[/] • [yellow]{seconds}/{int(duration)} сек[/] ([magenta]{percent}%[/]){file_progress}"
                    )
                else:
                    progress.update(
                        file_task_id, 
                        completed=seconds, 
                        description=f"[cyan]{file_name}[/] • [yellow]{seconds}/{int(duration)} сек[/] ([magenta]{percent}%[/]) • [blue]{bitrate}[/]{file_progress}"
                    )
    
    process.wait()
    
    # Завершаем задачу прогресса файла
    if file_task_id is not None:
        progress.update(file_task_id, completed=duration, visible=False)
    
    unregister_process(process)

    if process.returncode != 0:
        log_file = write_process_error_log(
            Path(file_name).stem if file_name else "ffmpeg",
            "".join(stderr_lines), cmd, process.returncode
        )
        if log_file:
            console.print(f"  [dim]Лог ffmpeg ошибки: {log_file}[/]")

    return process.returncode



def _parse_anime_title(filename: str) -> str:
    """
    Извлекает название тайтла из имени файла через anitomy.
    Fallback — всё до первого ' - NN' паттерна, или само имя файла без расширения.
    """
    try:
        title = anitomy_parse(filename).get('anime_title', '')
        if title:
            return title.strip()
    except Exception:
        pass
    stem = Path(filename).stem
    stem = re.sub(r'[\[\(][^\]\)]*[\]\)]', '', stem).strip()
    m = re.match(r'^(.*?)\s*[-–]\s*\d{1,4}', stem)
    if m:
        return m.group(1).strip()
    m = re.match(r'^(.*?)\s+\d{1,4}\s*$', stem)
    if m:
        return m.group(1).strip()
    return stem.strip()


# Тип релиза из anitomy → метка группы. OVA/ONA/спешлы/фильмы идут отдельным тайтлом.
_SPECIAL_LABELS = {"ova": "OVA", "oav": "OVA", "oad": "OVA", "ona": "ONA",
                   "sp": "Special", "special": "Special", "specials": "Special",
                   "movie": "Movie", "movies": "Movie"}
_SPECIAL_SUFFIX_RE = re.compile(r'\s+(OVA|ONA|Special|Movie)$')


def _parse_anime_group(filename: str) -> str:
    """Название группы для пункта 7: тайтл + ' OVA' / ' Movie' / …, если файл — спешл."""
    title = _parse_anime_title(filename)
    try:
        kind = _SPECIAL_LABELS.get((anitomy_parse(filename).get("anime_type") or "").lower())
    except Exception:
        kind = None
    return f"{title} {kind}" if kind and title else title


def _get_audio_tracks_with_titles(file_path: str) -> list:
    """
    Возвращает список аудиодорожек с индексами и title-метками.
    Каждый элемент: {'index': int, 'title': str, 'description': str}
    """
    tracks = []
    try:
        result = subprocess.run(
            [FFMPEG_PATH, "-i", file_path],
            stderr=subprocess.PIPE, stdout=subprocess.DEVNULL,
            encoding='utf-8', errors='replace'
        )
        audio_lines = [l for l in result.stderr.split('\n') if 'Audio:' in l]
        ffprobe_titles = {}
        try:
            fp = subprocess.run(
                [FFPROBE_PATH, "-v", "error", "-select_streams", "a",
                 "-show_entries", "stream=index:stream_tags=title",
                 "-of", "json", file_path],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                encoding='utf-8', errors='replace'
            )
            for s in json.loads(fp.stdout).get('streams', []):
                ffprobe_titles[s['index']] = s.get('tags', {}).get('title', '')
        except Exception:
            pass
        for i, desc in enumerate(audio_lines):
            m = re.search(r'Stream #0:(\d+)', desc)
            stream_idx = int(m.group(1)) if m else i
            title = ffprobe_titles.get(stream_idx, '')
            if not title:
                cm = re.search(r'Audio: ([^,]+)', desc)
                title = cm.group(1).strip() if cm else f"Дорожка {i + 1}"
            tracks.append({'index': i, 'title': title, 'description': desc.strip()})
    except Exception:
        pass
    return tracks



def _show_groups_table(groups: dict) -> None:
    """Выводит таблицу найденных групп."""
    t = Table(
        title="📂 Найденные тайтлы",
        box=box.ROUNDED, border_style="magenta", title_style="bold magenta"
    )
    t.add_column("#", justify="center", style="magenta", no_wrap=True)
    t.add_column("Тайтл", style="bold white")
    t.add_column("Файлов", justify="center", style="cyan")
    t.add_column("Первый файл", style="dim")
    for i, (title, files) in enumerate(groups.items(), 1):
        t.add_row(str(i), title, str(len(files)), files[0].name[:60])
    console.print(t)




def _regroup_manually(all_files_nested, current_dir):
    """Ручная перегруппировка файлов по тайтлам."""
    all_files = [f for group in all_files_nested for f in group]
    ft = Table(title="📋 Все файлы", box=box.ROUNDED, border_style="purple", title_style="bold purple")
    ft.add_column("#", justify="center", style="magenta")
    ft.add_column("Файл", style="cyan")
    for i, f in enumerate(all_files, 1):
        ft.add_row(str(i), f.name[:70])
    console.print(ft)

    groups = {}
    used = set()
    console.print(
        "\n[dim]  Создавай группы: введи название тайтла, затем номера файлов через запятую.\n"
        "  Пустое название — завершить.[/]\n"
    )
    while True:
        title = Prompt.ask("  [magenta]Название тайтла[/] (Enter — закончить)", default="")
        if not title:
            break
        nums_raw = Prompt.ask(f"  [magenta]Номера файлов для «{title}»[/] (через запятую)")
        try:
            nums = [int(x.strip()) for x in nums_raw.split(',') if x.strip()]
        except ValueError:
            console.print("  [red]Некорректный ввод.[/]")
            continue
        valid = [n for n in nums if 1 <= n <= len(all_files)]
        if not valid:
            console.print("  [red]Нет подходящих номеров.[/]")
            continue
        repeated = [n for n in valid if n in used]
        if repeated:
            console.print(
                f"  [yellow]⚠ Файлы {', '.join(map(str, repeated))} уже назначены другой группе — "
                f"они будут сконвертированы дважды.[/]"
            )
        selected = [all_files[n - 1] for n in valid]
        used.update(valid)
        groups[title] = selected
        console.print(f"  [green]✓ Группа «{title}»: {len(selected)} файл(ов)[/]")

    leftover = [all_files[i - 1] for i in range(1, len(all_files) + 1) if i not in used]
    if leftover:
        console.print(f"\n  [yellow]Нераспределено: {len(leftover)} файл(ов)[/]")
        action = Prompt.ask(
            "  ▶ [magenta]Что делать с ними?[/] [1-В отдельную группу/2-Пропустить]",
            choices=["1", "2"], default="2"
        )
        if action == "1":
            extra_title = Prompt.ask("  [magenta]Название группы для остатка[/]", default="Прочее")
            groups[extra_title] = leftover

    if not groups:
        console.print(Panel("Нет групп — отмена.", title="ℹ", border_style="yellow", box=box.ROUNDED))
        return {}
    _show_groups_table(groups)
    return groups


def check_mkv_tools():
    """Проверяет доступность инструментов MKVToolNix"""
    try:
        subprocess.run(["mkvmerge", "--version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        subprocess.run(["mkvextract", "--version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        return True
    except (FileNotFoundError, subprocess.SubprocessError):
        return False

def list_subtitle_tracks(file, quiet=False):
    """quiet=True — только вернуть список дорожек, без таблицы и сообщений
    (используется при поиске субтитров по тайтлу в каждой серии)."""
    # Проверяем доступность MKVToolNix
    use_mkv_tools = check_mkv_tools()
    
    if not use_mkv_tools:
        console.print(Panel(
            "Для работы с субтитрами необходим MKVToolNix. Установите его с сайта https://mkvtoolnix.download/",
            title="❌ Ошибка",
            border_style="red",
            box=box.DOUBLE
        ))
        return 0, [], False
    
    # Используем непосредственно mkvmerge для получения информации о треках
    try:
        # Используем опцию -J для вывода данных в формате JSON
        command = ["mkvmerge", "-J", file]
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
        
        try:
            info = json.loads(result.stdout)
            subtitle_tracks = []
            
            if "tracks" in info:
                for track in info["tracks"]:
                    # Ищем дорожки субтитров - они могут обозначаться по-разному
                    if track["type"] == "subtitles" or "S_TEXT" in track.get("codec_id", ""):
                        track_id = track["id"]
                        properties = track.get("properties", {})
                        
                        # Извлекаем имя трека
                        track_name = properties.get("track_name", "")
                        
                        # Если имя не найдено в properties, ищем в тегах
                        if not track_name and "tags" in track:
                            tags = track["tags"]
                            if "simple" in tags:
                                for tag in tags["simple"]:
                                    if tag["name"].lower() in ["name", "title"]:
                                        track_name = tag["value"]
                                        break
                        
                        # Получаем язык и кодек (language — ISO 639-2 "rus", language_ietf — "ru")
                        language = properties.get("language", "")
                        language_ietf = properties.get("language_ietf", "")
                        codec_id = track.get("codec_id", "") or properties.get("codec_id", "")
                        # Дополнительный fallback: определяем codec по codec_name если codec_id пустой
                        if not codec_id:
                            codec_name = track.get("codec", "") or properties.get("codec", "")
                            if "ass" in codec_name.lower() or "ssa" in codec_name.lower():
                                codec_id = "S_TEXT/ASS"
                            elif "subrip" in codec_name.lower() or "srt" in codec_name.lower():
                                codec_id = "S_TEXT/UTF8"
                            elif "pgs" in codec_name.lower() or "hdmv" in codec_name.lower():
                                codec_id = "S_HDMV/PGS"
                            elif "vobsub" in codec_name.lower():
                                codec_id = "S_VOBSUB"
                        
                        # Формируем информацию о дорожке
                        if not track_name:
                            if language:
                                track_name = f"Язык: {language}"
                            else:
                                track_name = f"Субтитры {track_id + 1}"
                                
                        description = f"Track {track_id}: {codec_id} ({language})"
                        
                        subtitle_tracks.append((
                            track_id, 
                            {"id": track_id, "name": track_name, "description": description, "codec_id": codec_id,
                             "language": language, "language_ietf": language_ietf}
                        ))

            # Если JSON-метод не нашел субтитры, попробуем через стандартный вывод
            if not subtitle_tracks:
                console.print("[yellow]⚠ JSON не содержит информации о субтитрах, используем стандартный вывод[/yellow]")
                command = ["mkvmerge", "-i", file]
                result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
                lines = result.stdout.split('\n')
                
                current_track_id = None
                
                for line in lines:
                    # Поиск строк с ID трека
                    track_id_match = re.search(r'Track ID (\d+)', line)
                    if track_id_match:
                        current_track_id = int(track_id_match.group(1))
                    
                    # Проверяем, содержит ли строка информацию о субтитрах
                    # Включаем различные форматы: S_TEXT, S_HDMV/PGS, S_VOBSUB и т.д.
                    if current_track_id is not None and any(substr in line for substr in ["subtitles", "S_TEXT", "S_HDMV", "S_VOBSUB"]):
                        # Ищем название и язык
                        name_match = re.search(r'name:([^,]+)', line, re.IGNORECASE)
                        lang_match = re.search(r'language:([^,]+)', line, re.IGNORECASE)
                        
                        track_name = ""
                        if name_match and name_match.group(1).strip():
                            track_name = name_match.group(1).strip()
                        elif lang_match and lang_match.group(1).strip():
                            track_name = f"Язык: {lang_match.group(1).strip()}"
                        else:
                            track_name = f"Субтитры {current_track_id + 1}"
                        
                        # Извлекаем codec_id из строки
                        codec_match = re.search(r'(S_TEXT/\w+|S_HDMV/\w+|S_VOBSUB\w*|S_ASS|S_SSA)', line)
                        fallback_codec = codec_match.group(1) if codec_match else ""
                        subtitle_tracks.append((
                            current_track_id, 
                            {"id": current_track_id, "name": track_name, "description": line.strip(), "codec_id": fallback_codec,
                             "language": lang_match.group(1).strip() if lang_match else ""}
                        ))
                
            # Если всё еще не нашли, попробуем mkvinfo
            if not subtitle_tracks:
                console.print("[yellow]⚠ mkvmerge не обнаружил субтитры, используем mkvinfo[/yellow]")
                command = ["mkvinfo", file]
                result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
                lines = result.stdout.split('\n')
                
                current_track = None
                track_id = None
                track_type = None
                track_name = None
                track_lang = None
                track_codec = None
                
                for line in lines:
                    line = line.strip()
                    
                    # Начало нового трека
                    if "|+ Track" in line:
                        # Сохраняем предыдущий трек (тип/кодек могут отсутствовать — None)
                        if track_id is not None and (track_type == "subtitles" or "S_TEXT" in (track_codec or "") or "subtitle" in (track_type or "").lower()):
                            name = track_name if track_name else (f"Язык: {track_lang}" if track_lang else f"Субтитры {track_id + 1}")
                            subtitle_tracks.append((
                                track_id, 
                                {"id": track_id, "name": name, "description": f"Track {track_id}: {track_codec}", "codec_id": track_codec or "", "language": track_lang or ""}
                            ))
                        
                        # Сбрасываем переменные
                        track_id = None
                        track_type = None
                        track_name = None
                        track_lang = None
                        track_codec = None
                    
                    # Номер трека
                    if "| + Track number:" in line:
                        track_num_match = re.search(r'(\d+)', line.split(":")[-1].strip())
                        if track_num_match:
                            track_id = int(track_num_match.group(1)) - 1
                    
                    # Тип трека
                    if "| + Track type:" in line:
                        track_type = line.split(":")[-1].strip()
                    
                    # Кодек
                    if "| + Codec ID:" in line:
                        track_codec = line.split(":")[-1].strip()
                    
                    # Название трека
                    if "| + Name:" in line:
                        track_name = line.split(":")[-1].strip()
                    
                    # Язык трека
                    if "| + Language:" in line:
                        track_lang = line.split(":")[-1].strip()
                
                # Проверяем последний трек
                if track_id is not None and (track_type == "subtitles" or "S_TEXT" in (track_codec or "") or "subtitle" in (track_type or "").lower()):
                    name = track_name if track_name else (f"Язык: {track_lang}" if track_lang else f"Субтитры {track_id + 1}")
                    subtitle_tracks.append((
                        track_id, 
                        {"id": track_id, "name": name, "description": f"Track {track_id}: {track_codec}", "codec_id": track_codec or "", "language": track_lang or ""}
                    ))
            
            if quiet:
                return len(subtitle_tracks), subtitle_tracks, True

            # Выводим результат
            table = Table(title=f"📝 Субтитры в файле [bold green]{file}[/bold green]", box=box.ROUNDED)
            table.add_column("ID", justify="center", style="cyan", no_wrap=True)
            table.add_column("Название", style="yellow")
            table.add_column("Описание", style="magenta")
            table.add_column("Формат", style="dim")
            
            for idx, (track_id, info) in enumerate(subtitle_tracks, start=1):
                codec = info.get("codec_id", "")
                ext = codec_id_to_ext(codec)
                table.add_row(str(idx), info["name"], info["description"], f"{codec or '?'} → {ext}")
            
            console.print(table)
            
            if not subtitle_tracks:
                console.print(f"[bold yellow]⚠ Субтитры не найдены в файле: {file}[/]")
                
            return len(subtitle_tracks), subtitle_tracks, True
            
        except json.JSONDecodeError as e:
            console.print(f"[bold red]❌ Ошибка декодирования JSON: {str(e)}[/]")
            return 0, [], False
        
    except Exception as e:
        console.print(f"[bold red]❌ Ошибка при поиске субтитров: {str(e)}[/]")
        return 0, [], False

def get_subtitle_id(subtitle_tracks, use_mkv_tools):
    while True:
        subtitle_id = Prompt.ask("📝 [bold magenta]Введите ID субтитров[/]")
        if subtitle_id.isdigit() and int(subtitle_id) > 0 and int(subtitle_id) <= len(subtitle_tracks):
            chosen = subtitle_tracks[int(subtitle_id) - 1]
            codec_id = chosen[1].get("codec_id", "")
            ext = codec_id_to_ext(codec_id)
            if use_mkv_tools:
                # Возвращаем реальный ID дорожки из mkvmerge
                return chosen[0], ext
            else:
                # Возвращаем индекс для ffmpeg (с нуля)
                return int(subtitle_id) - 1, ext
        console.print("[bold red]❌ Ошибка:[/] Введите корректный числовой ID (начиная с 1).")

def codec_id_to_ext(codec_id: str) -> str:
    """Определяет расширение файла субтитров по codec_id из mkvmerge."""
    c = codec_id.upper()
    if "ASS" in c or "SSA" in c:
        return ".ass"
    elif "UTF8" in c or "ASCII" in c or "UTF-8" in c:
        return ".srt"
    elif "PGS" in c or "HDMV" in c:
        return ".sup"
    elif "VOBSUB" in c:
        return ".sub"
    else:
        return ".ass"  # fallback


def _find_subtitle_track_by_title(subtitle_tracks, ref_title: str):
    """Ищет дорожку субтитров по тайтлу. Сначала точное совпадение
    (без учёта регистра и пробелов по краям), затем — единственное
    вхождение подстроки. Возвращает (track_id, ext) или None."""
    ref = ref_title.strip().casefold()
    exact = [t for t in subtitle_tracks if t[1]["name"].strip().casefold() == ref]
    if len(exact) == 1:
        hit = exact[0]
    else:
        partial = [t for t in subtitle_tracks if ref in t[1]["name"].casefold()]
        if len(partial) != 1:
            return None
        hit = partial[0]
    return hit[0], codec_id_to_ext(hit[1].get("codec_id", ""))


def _sub_langs(info: dict) -> set:
    """Все обозначения языка дорожки в нижнем регистре: {'rus', 'ru'}; пусто, если язык не указан."""
    return {l.strip().casefold() for l in (info.get("language"), info.get("language_ietf")) if l and l.strip() and l.strip().lower() != "und"}


def _find_subtitle_track_by_lang(subtitle_tracks, ref_info: dict, ref_pos: int):
    """
    Ищет в серии дорожку субтитров с тем же языком, что выбрана в первом файле.
    Если таких несколько (например, русские надписи и полные) — сначала та, у которой совпадает
    и тайтл, иначе та, что стоит на том же месте среди дорожек этого языка (ref_pos).
    Возвращает (track_id, ext) или None.
    """
    langs = _sub_langs(ref_info)
    same = [t for t in subtitle_tracks if _sub_langs(t[1]) & langs]
    if not same:
        return None
    if len(same) > 1:
        ref_name = ref_info["name"].strip().casefold()
        by_name = [t for t in same if t[1]["name"].strip().casefold() == ref_name]
        if len(by_name) == 1:
            same = by_name
        elif ref_pos < len(same):
            same = [same[ref_pos]]
    hit = same[0]
    return hit[0], codec_id_to_ext(hit[1].get("codec_id", ""))


def extract_subtitles(input_folder, output_folder):
    clear_screen()
    # Папка создаётся только после выбора типа (надписи/сабы) — см. ниже.
    extensions = [".mkv", ".mp4", ".hevc", ".avi", ".h264", ".m2ts", ".ogm", ".mpg", ".mov"]
    files = check_files(input_folder, extensions)
    
    console.print(Panel(f"Найдено [bold magenta]{len(files)}[/] файлов", 
                       title="📝 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    use_mkv_tools = False
    
    # Обрабатываем только первый файл для отображения информации о субтитрах
    if files:
        first_file = os.path.join(input_folder, files[0])
        console.print(Panel(f"Показываем субтитры первого файла: [bold cyan]{files[0]}[/]", 
                           border_style="magenta",
                           box=box.ROUNDED))
        count, tracks, is_mkv_tools = list_subtitle_tracks(first_file)
        use_mkv_tools = is_mkv_tools
        
        if not tracks:
            console.print(Panel("Субтитры не найдены.", 
                               title="❌ Информация", 
                               border_style="red",
                               box=box.DOUBLE))
            restart_script()
            return
            
        # Режим выбора: по ID дорожки (как раньше), по тайтлу или по языку —
        # у русских сабов в разных сериях ID может отличаться, а тайтлы бывают одинаковые (у CR все «CR»).
        sub_mode = "id"
        if use_mkv_tools:
            mode_table = Table(box=box.ROUNDED, border_style="magenta", title="🎯 Способ выбора дорожки", title_style="bold magenta")
            mode_table.add_column("#", justify="center", style="bold magenta", no_wrap=True)
            mode_table.add_column("Режим", style="bold white")
            mode_table.add_column("Описание", style="dim")
            mode_table.add_row("1", "По ID", "один и тот же ID дорожки во всех файлах")
            mode_table.add_row("2", "По тайтлу", "в каждой серии ищется дорожка с таким же названием")
            mode_table.add_row("3", "По языку", "в каждой серии ищется дорожка с таким же языком (rus, eng…)")
            console.print(mode_table)
            sub_mode = {"1": "id", "2": "title", "3": "lang"}[
                Prompt.ask("  [magenta]Выбери режим[/]", choices=["1", "2", "3"], default="1")]

        while True:
            subtitle_id, sub_ext = get_subtitle_id(tracks, use_mkv_tools)
            ref_info = next(info for tid, info in tracks if tid == subtitle_id)
            if sub_mode == "lang" and not _sub_langs(ref_info):
                console.print("  [yellow]У этой дорожки не указан язык — выбери другую или режим «По ID»/«По тайтлу».[/]")
                continue
            break
        ref_title = ref_info["name"]
        # Какая это по счёту дорожка своего языка — чтобы различать, например, русские надписи и полные
        same_lang_ids = [tid for tid, info in tracks if _sub_langs(info) & _sub_langs(ref_info)]
        ref_lang_pos = same_lang_ids.index(subtitle_id) if sub_mode == "lang" else 0
        if sub_mode == "title":
            console.print(Panel(f"Во всех сериях будет искаться дорожка с тайтлом: [bold cyan]{ref_title}[/]",
                               border_style="magenta", box=box.ROUNDED))
        elif sub_mode == "lang":
            lang_str = ref_info.get("language") or ref_info.get("language_ietf")
            extra = (f"\n[dim]в файле {len(same_lang_ids)} дорожки на этом языке — в каждой серии берётся "
                     f"«{ref_title}», а если тайтлы не различаются, то {ref_lang_pos + 1}-я по счёту[/]"
                     if len(same_lang_ids) > 1 else "")
            console.print(Panel(f"Во всех сериях будет искаться дорожка на языке: [bold cyan]{lang_str}[/]{extra}",
                               border_style="magenta", box=box.ROUNDED))
    else:
        console.print(Panel("Файлы не найдены.", 
                           title="❌ Информация", 
                           border_style="red",
                           box=box.DOUBLE))
        restart_script()
        return

    # Выбор приписки и названия папки
    suffix_table = Table(box=box.ROUNDED, border_style="magenta", title="📂 Тип субтитров", title_style="bold magenta")
    suffix_table.add_column("#", justify="center", style="bold magenta", no_wrap=True)
    suffix_table.add_column("Приписка", style="bold white")
    suffix_table.add_column("Папка", style="cyan")
    suffix_table.add_row("1", ".надписи", "надписи")
    suffix_table.add_row("2", ".сабы",    "сабы")
    console.print(suffix_table)

    suffix_choice = Prompt.ask(
        "  [magenta]Выбери тип[/]",
        choices=["1", "2"],
        default="1"
    )
    if suffix_choice == "2":
        sub_suffix = ".сабы"
        output_folder = os.path.join(os.path.dirname(output_folder), "сабы")
    else:
        sub_suffix = ".надписи"
        # output_folder уже задан как «надписи»

    os.makedirs(output_folder, exist_ok=True)
    clear_readonly(output_folder)

    # Проверка уже существующих обработанных файлов
    skipped_files = []
    files_to_process = []
    
    for file in files:
        output_filename = process_output_filename(file.rsplit('.', 1)[0]) + sub_suffix + sub_ext
        output_path = os.path.join(output_folder, output_filename)
        if os.path.exists(output_path) and os.path.getsize(output_path) > 0:
            skipped_files.append(file)
        else:
            files_to_process.append(file)
    
    if skipped_files:
        console.print(Panel(f"Найдено [bold yellow]{len(skipped_files)}[/] уже обработанных файлов, они будут пропущены", 
                           title="⏩ Пропуск", 
                           border_style="yellow",
                           box=box.ROUNDED))
    
    if not files_to_process:
        console.print(Panel("Все файлы уже обработаны.", 
                           title="✅ Информация", 
                           border_style="green",
                           box=box.DOUBLE))
        restart_script()
        return

    console.print(Panel(f"Будет обработано [bold magenta]{len(files_to_process)}[/] файлов", 
                       title="🔄 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn()
    ) as progress:
        task = progress.add_task("📝 [green]Извлечение субтитров...", total=len(files_to_process))
        for idx, file in enumerate(files_to_process, 1):
            # Обновляем описание задачи с текущим файлом и прогрессом
            progress.update(task, description=f"📝 [green]Извлечение субтитров из: [cyan]{file}[/cyan] ([bold magenta]{idx}/{len(files_to_process)}[/bold magenta])")
            
            input_path = os.path.join(input_folder, file)
            file_sub_id, file_sub_ext = subtitle_id, sub_ext
            if sub_mode in ("title", "lang"):
                _, file_tracks, _ = list_subtitle_tracks(input_path, quiet=True)
                if sub_mode == "title":
                    hit = _find_subtitle_track_by_title(file_tracks, ref_title)
                    what = f"с тайтлом [bold]{ref_title}[/]"
                    have = ", ".join(info["name"] for _, info in file_tracks)
                else:
                    hit = _find_subtitle_track_by_lang(file_tracks, ref_info, ref_lang_pos)
                    what = f"на языке [bold]{ref_info.get('language') or ref_info.get('language_ietf')}[/]"
                    have = ", ".join(info.get("language") or "?" for _, info in file_tracks)
                if hit is None:
                    console.print(f"[bold yellow]⚠ Пропуск[/] [cyan]{file}[/cyan]: дорожка {what} не найдена ({have or 'нет субтитров'})")
                    progress.update(task, advance=1)
                    continue
                file_sub_id, file_sub_ext = hit
            output_filename = process_output_filename(file.rsplit('.', 1)[0]) + sub_suffix + file_sub_ext
            output_path = os.path.join(output_folder, output_filename)
            
            try:
                if use_mkv_tools:
                    # Извлекаем субтитры с помощью mkvextract
                    command = ["mkvextract", "tracks", input_path, f"{file_sub_id}:{output_path}"]
                    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", check=False)
                else:
                    # Извлекаем субтитры с помощью ffmpeg
                    command = [FFMPEG_PATH, "-nostdin", "-y", "-i", input_path, "-map", f"0:s:{subtitle_id}", "-c:s", "copy", output_path]
                    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", check=False)
                # mkvextract: 0 — ок, 1 — предупреждения (файл извлечён), 2 — ошибка
                if use_mkv_tools and result.returncode == 1 and os.path.exists(output_path) and os.path.getsize(output_path) > 0:
                    console.print(f"[yellow]⚠ mkvextract вернул предупреждения для[/] [cyan]{file}[/cyan] (файл извлечён)")
                    clear_readonly(output_path)
                elif result.returncode != 0:
                    log_file = write_process_error_log(Path(file).stem, result.stdout, command, result.returncode)
                    details = f"Лог: {log_file}" if log_file else "Не удалось сохранить лог."
                    console.print(f"[bold red]❌ Ошибка при извлечении субтитров из[/] [cyan]{file}[/cyan]\n{details}")
                elif os.path.exists(output_path):
                    # Снимаем read-only, если его навесил инструмент/среда извлечения.
                    clear_readonly(output_path)
            except Exception as e:
                console.print(f"[bold red]❌ Ошибка при извлечении субтитров из[/] [cyan]{file}[/cyan]: {e}")
            
            progress.update(task, advance=1)
    
    console.print(Panel(f"Субтитры сохранены в [green]{output_folder}[/green]", 
                       title="✅ Успех", 
                       border_style="green",
                       box=box.DOUBLE))
    
    restart_script()

# Функция для извлечения номера серии из имени файла
def extract_episode_number(filename):
    basename = os.path.splitext(filename)[0]
    basename = basename.replace(".надписи", "")

    # Сопоставление римских цифр для OVA/SP
    roman_map = {
        'I': 1, 'II': 2, 'III': 3, 'IV': 4, 'V': 5, 'VI': 6, 'VII': 7, 'VIII': 8, 'IX': 9, 'X': 10
    }

    patterns = [
        # Новый паттерн: число после дефиса и перед [ (например, - 0530 [1080p])
        (r'-\s*(\d{2,4})\s*\[', lambda m: int(m.group(1))),
        # S01E02, s1e2
        (r'[Ss](\d{1,2})[Ee](\d{1,2})', lambda m: int(m.group(2))),
        # 01x02, 1x2
        (r'(\d{1,2})[xX](\d{1,2})', lambda m: int(m.group(2))),
        # [01], (01)
        (r'[\[\(](\d{1,3})[\]\)]', lambda m: int(m.group(1))),
        # ep01, episode01, эпизод01, ep-01, ep_01, ep 01
        (r'(?:ep|episode|эпизод)[\s\-_]*(\d{1,3})', lambda m: int(m.group(1))),
        # E01, e01
        (r'[Ee](\d{1,3})', lambda m: int(m.group(1))),
        # OVA 01, OVA01, OVA-I, OVA II, SP01, SPECIAL01
        (r'(?:OVA|SP|SPECIAL)[\s\-_]*(\d{1,3}|[IVX]+)', lambda m: roman_map.get(m.group(1).upper(), int(m.group(1))) if not m.group(1).isdigit() else int(m.group(1))),
        # Part 1, part1
        (r'part[\s\-_]*(\d{1,3})', lambda m: int(m.group(1))),
        # 01, 001, 1 (в конце строки или после пробела/дефиса/подчеркивания)
        (r'[-_\s](\d{1,3})$', lambda m: int(m.group(1))),
        # Просто число в конце
        (r'(\d{1,3})$', lambda m: int(m.group(1))),
        # Японский стиль: 第01話
        (r'第(\d{1,3})話', lambda m: int(m.group(1))),
    ]

    for pattern, handler in patterns:
        match = re.search(pattern, basename, re.IGNORECASE)
        if match:
            try:
                ep_num = handler(match)
                if ep_num is not None:
                    return f"{int(ep_num):02d}"
            except Exception:
                continue

    # Fallback: первое число в имени файла
    match = re.search(r'(\d{1,3})', basename)
    if match:
        return f"{int(match.group(1)):02d}"

    return None

def extract_episode_number_advanced(filename):
    """
    Улучшенная функция для извлечения номера серии с использованием anitomy
    и расширенной конфигурации паттернов
    """
    basename = os.path.splitext(filename)[0]
    basename = basename.replace(".надписи", "")

    # Попытка 1: встроенный anitomy
    try:
        ep_num = anitomy_parse(filename).get('episode_number')
        if ep_num:
            return f"{int(ep_num):02d}"
    except Exception:
        pass  # Если anitomy не сработал, переходим к fallback
    
    # Попытка 2: Расширенная конфигурация паттернов
    # Конфигурация вынесена в отдельный словарь для легкого редактирования
    episode_patterns = {
        # Основные паттерны аниме
        'season_episode': [
            (r'[Ss](\d{1,2})[Ee](\d{1,2})', lambda m: int(m.group(2))),
            (r'(\d{1,2})[xX](\d{1,2})', lambda m: int(m.group(2))),
        ],
        
        # Японские паттерны
        'japanese': [
            (r'第(\d{1,3})話', lambda m: int(m.group(1))),
            (r'(\d{1,3})話', lambda m: int(m.group(1))),
            (r'第(\d{1,3})回', lambda m: int(m.group(1))),
            (r'(\d{1,3})回', lambda m: int(m.group(1))),
        ],
        
        # Английские паттерны
        'english': [
            (r'(?:ep|episode|эпизод)[\s\-_]*(\d{1,3})', lambda m: int(m.group(1))),
            (r'[Ee](\d{1,3})', lambda m: int(m.group(1))),
            (r'[Pp]art[\s\-_]*(\d{1,3})', lambda m: int(m.group(1))),
        ],
        
        # Специальные выпуски
        'special': [
            (r'(?:OVA|SP|SPECIAL|ONA|MOVIE)[\s\-_]*(\d{1,3}|[IVX]+)', 
             lambda m: roman_to_int(m.group(1)) if not m.group(1).isdigit() else int(m.group(1))),
        ],
        
        # Общие паттерны
        'general': [
            (r'[\[\(](\d{1,3})[\]\)]', lambda m: int(m.group(1))),
            (r'-\s*(\d{2,4})\s*\[', lambda m: int(m.group(1))),
            (r'[-_\s](\d{1,3})$', lambda m: int(m.group(1))),
            (r'(\d{1,3})$', lambda m: int(m.group(1))),
        ],
        
        # Fallback - любое число в имени
        'fallback': [
            (r'(\d{1,3})', lambda m: int(m.group(1))),
        ]
    }
    
    # Функция для конвертации римских цифр
    def roman_to_int(roman):
        roman_map = {
            'I': 1, 'II': 2, 'III': 3, 'IV': 4, 'V': 5, 
            'VI': 6, 'VII': 7, 'VIII': 8, 'IX': 9, 'X': 10,
            'XI': 11, 'XII': 12, 'XIII': 13, 'XIV': 14, 'XV': 15,
            'XVI': 16, 'XVII': 17, 'XVIII': 18, 'XIX': 19, 'XX': 20
        }
        return roman_map.get(roman.upper(), None)
    
    # Проходим по всем категориям паттернов
    for category, patterns in episode_patterns.items():
        for pattern, handler in patterns:
            match = re.search(pattern, basename, re.IGNORECASE)
            if match:
                try:
                    ep_num = handler(match)
                    if ep_num is not None and ep_num > 0:
                        return f"{ep_num:02d}"
                except Exception:
                    continue
    
    return None

def extract_episode_number_smart(filename):
    """
    Умная функция, которая комбинирует несколько методов
    и возвращает наиболее вероятный номер серии
    """
    basename = os.path.splitext(filename)[0]

    # Приоритет 0: паттерн Title.NN_MMM (сезон/арк + серия) — например HunterHunter.11_001
    # NN — 1-2 цифры (номер сезона/арки), MMM — 3+ цифры (номер серии)
    # Без этого anitomy ошибочно парсит NN как номер серии
    m = re.search(r'[._\- ]\d{1,2}_(\d{3,})', basename)
    if m:
        return f"{int(m.group(1)):02d}"

    # Метод 1: встроенный anitomy
    try:
        ep_num = anitomy_parse(filename).get('episode_number')
        if ep_num:
            return f"{int(ep_num):02d}"
    except Exception:
        pass
    
    # Метод 2: Улучшенная функция
    result = extract_episode_number_advanced(filename)
    if result:
        return result
    
    # Метод 3: Оригинальная функция как fallback
    return extract_episode_number(filename)

def rename_files_by_pattern(input_folder):
    """Переименовывает файлы по шаблону, извлекая номер серии из исходного имени"""
    clear_screen()
    
    # Получаем список всех файлов в директории
    all_files = os.listdir(input_folder)
    
    # Фильтруем только файлы (исключаем папки) и исключаем .bat файлы
    files = [f for f in all_files if os.path.isfile(os.path.join(input_folder, f)) and not f.lower().endswith('.bat')]
    
    if not files:
        console.print(Panel("Не найдено подходящих файлов в директории.", 
                           title="❌ Ошибка", 
                           border_style="red",
                           box=box.DOUBLE))
        restart_script()
        return
    
    console.print(Panel(f"Найдено [bold magenta]{len(files)}[/] файлов", 
                       title="📝 Информация", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    # Сортируем файлы для наглядности
    files.sort()
    
    # Показываем первый файл как пример
    console.print(Panel(f"Пример файла: [bold cyan]{files[0]}[/]", 
                       border_style="magenta",
                       box=box.ROUNDED))
    
    # Базовое название: оригинальное (ромадзи) с Шикимори или вручную
    name_source = Prompt.ask(
        "📝 [bold magenta]Базовое название:[/] [1-Найти на Shikimori (оригинальное)/2-Ввести вручную]",
        choices=["1", "2"], default="1"
    )
    suggested = ""
    if name_source == "1":
        # Поиск по названию из первого видеофайла (если видео нет — из первого файла)
        videos = [f for f in files if Path(f).suffix.lower() in (".mkv", ".mp4", ".avi", ".mov", ".m2ts", ".ts", ".webm")]
        hint = _parse_anime_title((videos or files)[0]) or Path((videos or files)[0]).stem
        kind, val = _choose_shikimori(hint)
        original = val["name"] if kind == "pick" else (_shiki_original_name(val) if kind == "id" else None)
        if original:
            suggested = _filename_safe_title(original)
            console.print(f"  [green]✓[/] Оригинальное название: [bold white]{original}[/]")
        elif kind != "skip":
            console.print("  [yellow]Не удалось получить название с Shikimori — введи вручную.[/]")

    prompt_text = "📝 [bold magenta]Базовое название аниме[/]" + (" [dim](Enter — как предложено)[/]" if suggested else "")
    base_name = Prompt.ask(prompt_text, default=suggested) if suggested else Prompt.ask(prompt_text)
    # Символы, недопустимые в именах файлов Windows (двоеточие и т.п.), убираем и из ручного ввода
    base_name = _filename_safe_title(base_name)

    if not base_name:
        console.print(Panel("Базовое название не может быть пустым.", 
                           title="❌ Ошибка", 
                           border_style="red",
                           box=box.DOUBLE))
        restart_script()
        return
    
    # Запрашиваем начальный номер в исходной нумерации
    while True:
        numbering_start_input = Prompt.ask(
            "📌 [bold magenta]С какого номера начинается исходная нумерация файлов?[/] (например, 12 если файлы 12,13,...)",
            default="1"
        )
        try:
            numbering_start_int = int(numbering_start_input)
            if numbering_start_int < 1:
                raise ValueError
            break
        except ValueError:
            console.print("[bold red]❌ Ошибка:[/] Введите положительное целое число.")
    
    def adjust_episode_number(raw_value: str):
        try:
            episode_int = int(raw_value)
        except (TypeError, ValueError):
            return None
        adjusted = episode_int - (numbering_start_int - 1)
        if adjusted <= 0:
            return None
        return f"{adjusted:02d}"
    
    # Спрашиваем, нужен ли дополнительный суффикс
    add_suffix = Prompt.ask(
        "📝 [bold magenta]Добавить дополнительный суффикс в конце названия?[/] [1-Да/2-Нет]", 
        choices=["1", "2"], 
        default="2"
    )
    
    suffix = ""
    if add_suffix == "1":
        suffix = Prompt.ask("📝 [bold magenta]Введите дополнительный суффикс[/] (будет добавлен через точку)")
        # Убираем точку в начале, если пользователь ввел с точкой
        if suffix.startswith('.'):
            suffix = suffix[1:]
    
    # Информация о файлах для переименования
    files_to_rename = []
    files_with_errors = []
    force_manual = False  # True — сразу перейти в ручной режим без повторного вопроса
    
    # Формируем информацию о переименовании
    for filename in files:
        episode_number = extract_episode_number_smart(filename)
        # Сохраняем исходное расширение файла
        extension = os.path.splitext(filename)[1]
        
        if episode_number:
            adjusted_episode_number = adjust_episode_number(episode_number)
            if not adjusted_episode_number:
                files_with_errors.append(filename)
                continue
            # Формируем новое имя с учетом суффикса, если он есть
            if suffix:
                new_name = f"{base_name} - {adjusted_episode_number}.{suffix}{extension}"
            else:
                new_name = f"{base_name} - {adjusted_episode_number}{extension}"
            files_to_rename.append((filename, new_name))
        else:
            files_with_errors.append(filename)
    
    # Если есть файлы, для которых не удалось определить номер серии
    if files_with_errors:
        error_table = Table(title="❌ Не удалось определить номер серии", box=box.ROUNDED, border_style="red")
        error_table.add_column("#", justify="center", style="cyan", no_wrap=True)
        error_table.add_column("Имя файла", style="red")
        for idx, filename in enumerate(files_with_errors, 1):
            error_table.add_row(str(idx), filename)
        console.print(error_table)
        
        console.print("[yellow]Эти файлы будут пропущены при автоматическом переименовании.[/]")
        console.print("[green]Выберите режим ручного ввода, чтобы задать все номера вручную.[/]")
        
        if not files_to_rename:
            console.print(Panel("Не удалось определить номер серии ни в одном файле.", 
                               title="❌ Ошибка", 
                               border_style="red",
                               box=box.DOUBLE))
            
            # Предлагаем выбор между закрытием скрипта и ручным режимом
            manual_choice = Prompt.ask(
                "📝 [bold magenta]Что делать?[/] [1-Перейти в ручной режим/2-Закрыть скрипт]", 
                choices=["1", "2"], 
                default="1"
            )
            
            if manual_choice == "2":
                restart_script()
                return
            else:
                # Переходим в ручной режим
                console.print(Panel("Переход в режим ручного ввода", 
                              title="✎ Ручной ввод", 
                              border_style="magenta",
                              box=box.ROUNDED))
                force_manual = True  # Сразу в ручной режим, без повторного вопроса
    
    # Показываем предварительный просмотр переименования (если есть что показывать)
    if files_to_rename:
        preview_table = Table(title="Предварительный просмотр переименования", box=box.ROUNDED, border_style="magenta")
        preview_table.add_column("Старое имя", style="cyan")
        preview_table.add_column("→", justify="center", style="yellow")
        preview_table.add_column("Новое имя", style="green")
        
        for old_name, new_name in files_to_rename:
            preview_table.add_row(old_name, "→", new_name)
        
        console.print(preview_table)
    
    # Запрашиваем подтверждение переименования
    # (если выше выбран ручной режим — не спрашиваем повторно,
    #  раньше здесь Prompt затирал confirm="2" и переход не срабатывал)
    if force_manual:
        confirm = "2"
    else:
        confirm = Prompt.ask(
            "🔄 [bold magenta]Переименовать файлы?[/] [1-Да автоматически/2-Ввести номера вручную/3-Отмена]", 
            choices=["1", "2", "3"], 
            default="1"
        )
    
    if confirm == "3":
        console.print(Panel("Переименование отменено.", 
                           title="ℹ Информация", 
                           border_style="yellow",
                           box=box.DOUBLE))
        restart_script()
        return
    
    # Ввод номеров вручную
    if confirm == "2":
        console.print(Panel("Режим ручного ввода номеров серий", 
                           title="✎ Ручной ввод", 
                           border_style="magenta",
                           box=box.ROUNDED))
        
        # Сбрасываем список файлов для переименования
        files_to_rename = []
        
        # Для каждого файла запрашиваем номер серии
        for idx, filename in enumerate(files, 1):
            console.print(f"[cyan]{idx}/{len(files)}[/] [bold magenta]{filename}[/]")
            
            # Убираем стандартное расширение .надписи.ass для анализа
            basename = filename.replace(".надписи.ass", "")
            
            # Показываем предположительный номер, если его удалось найти
            ep_number = extract_episode_number_smart(basename)
            default_value = adjust_episode_number(ep_number) if ep_number else ""
            
            # Запрашиваем номер серии
            if default_value:
                console.print(f"[green]Предполагаемый номер:[/] [magenta]{default_value}[/]")
            
            episode_number = Prompt.ask(
                "📝 [bold magenta]Введите номер серии[/] (или Enter для пропуска)",
                default="", 
                show_default=False
            )
            
            # Если ничего не введено и нет предполагаемого номера, пропускаем файл
            if not episode_number and not default_value:
                continue
                
            # Если ничего не введено, используем предполагаемый номер
            if not episode_number:
                episode_number = default_value
            else:
                # Иначе форматируем введенный номер с ведущим нулем
                try:
                    episode_number = f"{int(episode_number):02d}"
                except ValueError:
                    console.print("[bold red]❌ Некорректный номер, файл будет пропущен[/]")
                    continue
            
            # Формируем новое имя
            extension = os.path.splitext(filename)[1]
            if suffix:
                new_name = f"{base_name} - {episode_number}.{suffix}{extension}"
            else:
                new_name = f"{base_name} - {episode_number}{extension}"
                
            files_to_rename.append((filename, new_name))
        
        # Показываем предварительный просмотр после ручного ввода
        if files_to_rename:
            console.print("\n[bold green]Предварительный просмотр после ручного ввода:[/]")
            preview_table = Table(box=box.ROUNDED, border_style="magenta")
            preview_table.add_column("#", justify="center", style="cyan", no_wrap=True)
            preview_table.add_column("Старое имя", style="cyan")
            preview_table.add_column("→", justify="center", style="yellow")
            preview_table.add_column("Новое имя", style="green")
            
            for idx, (old_name, new_name) in enumerate(files_to_rename, 1):
                preview_table.add_row(str(idx), old_name, "→", new_name)
            
            console.print(preview_table)
            
            # Запрашиваем окончательное подтверждение после ручного ввода
            final_confirm = Prompt.ask(
                "🔄 [bold magenta]Подтвердить переименование?[/] [1-Да/2-Нет]", 
                choices=["1", "2"], 
                default="1"
            )
            
            if final_confirm == "2":
                console.print(Panel("Переименование отменено.", 
                               title="ℹ Информация", 
                               border_style="yellow",
                               box=box.DOUBLE))
                restart_script()
                return
    
    # Процесс переименования
    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn()
    ) as progress:
        task = progress.add_task("🔄 [green]Переименование файлов...", total=len(files_to_rename))
        
        renamed_count = 0
        errors_count = 0
        
        for old_name, new_name in files_to_rename:
            old_path = os.path.join(input_folder, old_name)
            new_path = os.path.join(input_folder, new_name)
            
            # Проверка на существование файла с таким именем
            if os.path.exists(new_path) and old_path != new_path:
                console.print(f"[bold red]❌ Ошибка:[/] Файл [cyan]{new_name}[/cyan] уже существует")
                errors_count += 1
            else:
                try:
                    os.rename(old_path, new_path)
                    renamed_count += 1
                except Exception as e:
                    console.print(f"[bold red]❌ Ошибка при переименовании [cyan]{old_name}[/cyan]: {str(e)}[/]")
                    errors_count += 1
            
            progress.update(task, advance=1, description=f"🔄 [green]Переименовано: [cyan]{renamed_count}[/cyan] файлов")
    
    # Сообщение об успешном завершении
    if errors_count == 0:
        console.print(Panel(f"Успешно переименовано [bold green]{renamed_count}[/] файлов", 
                           title="✅ Успех", 
                           border_style="green",
                           box=box.DOUBLE))
    else:
        console.print(Panel(f"Переименовано [bold green]{renamed_count}[/] файлов\nОшибки при переименовании [bold red]{errors_count}[/] файлов", 
                           title="⚠ Завершено с ошибками", 
                           border_style="yellow",
                           box=box.DOUBLE))
    
    restart_script()

def convert_mkv_to_mp4(input_folder, output_folder):
    """
    Конвертирует видеофайлы в MP4 или MKV, копируя видео и аудио потоки без перекодирования.
    Поддерживаемые входящие форматы: MKV, MP4, AVI, MOV, TS, M2TS, WEBM, FLV, WMV, VOB, M4V.
    """
    clear_screen()

    INPUT_EXTENSIONS = [".mkv", ".mp4", ".avi", ".mov", ".ts", ".m2ts", ".webm", ".flv", ".wmv", ".vob", ".m4v"]

    # Выбор выходного формата
    fmt_table = Table(box=box.ROUNDED, border_style="magenta", title_style="bold magenta", title="📦 Выходной формат")
    fmt_table.add_column("  #", justify="center", style="bold magenta")
    fmt_table.add_column("Формат", style="bold white")
    fmt_table.add_column("Примечание", style="dim")
    fmt_table.add_row("1", "MP4", "широкая совместимость")
    fmt_table.add_row("2", "MKV", "сохраняет все дорожки и субтитры")
    console.print(fmt_table)

    fmt_choice = Prompt.ask("  ▶ [magenta]Выходной формат[/]", choices=["1", "2"], default="1")
    out_ext = ".mp4" if fmt_choice == "1" else ".mkv"
    out_fmt_name = "MP4" if fmt_choice == "1" else "MKV"

    # Для MKV выхода — спрашиваем копировать ли субтитры
    copy_subs = False
    if out_ext == ".mkv":
        subs_choice = Prompt.ask(
            "  ▶ [magenta]Копировать субтитры?[/] [1-Да/2-Нет]",
            choices=["1", "2"], default="1"
        )
        copy_subs = subs_choice == "1"

    # Сканируем файлы
    all_files = [
        f for f in os.listdir(input_folder)
        if Path(f).suffix.lower() in INPUT_EXTENSIONS
    ]

    if not all_files:
        exts_str = "  ".join(INPUT_EXTENSIONS)
        console.print(Panel(
            f"Не найдено видеофайлов.\n[dim]Поддерживаются: {exts_str}[/]",
            title="❌ Ошибка", border_style="red", box=box.DOUBLE
        ))
        restart_script()
        return

    # Показываем найденные файлы по форматам
    counts: dict[str, int] = {}
    for f in all_files:
        ext = Path(f).suffix.lower()
        counts[ext] = counts.get(ext, 0) + 1

    info_table = Table(box=box.ROUNDED, border_style="magenta", title_style="bold magenta", title="🎬 Найдено файлов")
    info_table.add_column("Формат", style="cyan")
    info_table.add_column("Количество", justify="center", style="bold white")
    for ext, cnt in sorted(counts.items()):
        info_table.add_row(ext, str(cnt))
    info_table.add_row("[bold]Итого[/]", f"[bold]{len(all_files)}[/]")
    console.print(info_table)

    os.makedirs(output_folder, exist_ok=True)
    clear_readonly(output_folder)

    # Проверяем уже готовые
    skipped, files_to_process = [], []
    for f in all_files:
        base_name = Path(f).stem
        out_path = os.path.join(output_folder, f"{base_name}{out_ext}")
        if os.path.exists(out_path) and os.path.getsize(out_path) > 0:
            skipped.append(f)
        else:
            files_to_process.append(f)

    if skipped:
        console.print(Panel(
            f"[bold yellow]{len(skipped)}[/] файл(ов) уже конвертированы — пропускаем",
            title="⏩ Пропуск", border_style="yellow", box=box.ROUNDED
        ))

    if not files_to_process:
        console.print(Panel("Все файлы уже обработаны.", title="✅ Готово",
                            border_style="green", box=box.DOUBLE))
        restart_script()
        return

    console.print(Panel(
        f"Конвертируем [bold magenta]{len(files_to_process)}[/] файл(ов) → [bold white]{out_fmt_name}[/]",
        title="🔄 Старт", border_style="magenta", box=box.ROUNDED
    ))

    with Progress(
        SpinnerColumn(style="magenta"),
        TextColumn("[progress.description]{task.description}"),
        BarColumn(complete_style="magenta"),
        TimeRemainingColumn(),
        console=console
    ) as progress:
        task = progress.add_task("", total=len(files_to_process))
        for idx, file in enumerate(files_to_process, 1):
            progress.update(task, description=(
                f"[magenta]{idx}/{len(files_to_process)}[/]  [cyan]{file[:60]}[/]"
            ))

            input_path  = os.path.join(input_folder, file)
            output_path = os.path.join(output_folder, f"{Path(file).stem}{out_ext}")

            # Строим map-аргументы в зависимости от формата и опций
            map_args = ["-map", "0:v:0", "-map", "0:a"]
            if copy_subs:
                map_args += ["-map", "0:s?"]  # субтитры если есть

            command = [
                FFMPEG_PATH, "-nostdin", "-i", input_path,
                *map_args,
                "-c", "copy",
                "-y",
                output_path
            ]

            try:
                result = subprocess.run(
                    command,
                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                    text=True, encoding="utf-8", errors="replace", check=False
                )
                if result.returncode != 0:
                    log_file = write_process_error_log(Path(file).stem, result.stdout, command, result.returncode)
                    details = f"Лог: {log_file}" if log_file else "Не удалось сохранить лог."
                    console.print(f"\n[bold red]❌[/] [cyan]{file}[/]\n[dim]{details}[/]")
                elif os.path.exists(output_path):
                    clear_readonly(output_path)
            except Exception as e:
                log_error(e)
                console.print(f"\n[bold red]❌[/] [cyan]{file}[/]: {e}")

            progress.update(task, advance=1)

    console.print(Panel(
        f"Файлы сохранены в [cyan]{output_folder}[/]",
        title=f"✅ Конвертация в {out_fmt_name} завершена",
        border_style="green", box=box.DOUBLE
    ))
    restart_script()


# =====================
# HLS мульти-разрешение
# =====================

# Список предустановленных озвучек (используется в ф-ях 3 и 7)
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

# Параметры разрешений: (папка, ширина, высота, опорный битрейт видео).
# При постоянном качестве (_HLS_FIXED_CQ) от него считается потолок битрейта,
# при подборе CQ (_calibrate_hls_cq) это средний битрейт серии, под который подгоняется CQ.
# Последний слот ("4K") подменяется на "5K" динамически если источник шире 3840px.
_HLS_RESOLUTIONS = [
    ("360p",  640,   360,    800_000),
    ("480p",  854,   480,  1_500_000),
    ("720p",  1280,  720,  3_000_000),
    ("1080p", 1920,  1080, 5_000_000),
    ("2K",    2560,  1440, 8_000_000),
    ("4K",    3840,  2160, 16_000_000),  # может заменяться на 5K при wide-source
]


def _get_source_width(file_path: Path) -> int:
    """Возвращает ширину видеопотока источника (0 при ошибке)."""
    try:
        r = subprocess.run(
            [FFPROBE_PATH, "-v", "error", "-select_streams", "v:0",
             "-show_entries", "stream=width",
             "-of", "default=noprint_wrappers=1:nokey=1", str(file_path)],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            encoding="utf-8", errors="replace",
        )
        return int(r.stdout.strip())
    except Exception:
        return 0


def _get_resolutions_for(file_path: Path) -> list[tuple]:
    """
    Возвращает список разрешений для данного файла.
    Если исходник шире 3840px — верхний слот остаётся 4K (3840x2160),
    scale_cuda сделает даунскейл на GPU.
    """
    src_w = _get_source_width(file_path)
    resolutions = list(_HLS_RESOLUTIONS)
    if src_w > 3840:
        console.print(
            f"  [cyan]i[/] Источник [bold]{src_w}px[/] → даунскейл до [bold magenta]3840px (4K)[/] на GPU"
        )
    return resolutions

_HLS_SEGMENT_TIME  = 6

# Видео кодируется с постоянным качеством (-cq) и потолком битрейта.
#   _HLS_FIXED_CQ = число — этот CQ на все разрешения, потолок _HLS_FIXED_CQ_PEAK × битрейт
#     из _HLS_RESOLUTIONS. Качество одинаковое во всех сериях, размер зависит от сложности.
#   _HLS_FIXED_CQ = None  — CQ подбирается под каждую серию: исходник делится на окна по
#     _HLS_SEGMENT_TIME сек, окна разной сложности пробно кодируются, и CQ подгоняется, пока
#     средний битрейт не совпадёт с битрейтом из _HLS_RESOLUTIONS (±_HLS_CAL_TOLERANCE).
_HLS_FIXED_CQ       = 21.0
_HLS_FIXED_CQ_PEAK  = 4
_HLS_NVENC_PRESET   = "p4"         # p5–p7 по VMAF в пределах погрешности, но в 1.7× медленнее (NVENC загружен на 99%)
_HLS_CAL_WINDOWS    = 10           # калибровочных окон (по одному из каждой страты сложности)
_HLS_CAL_TOLERANCE  = 0.03
_HLS_CAL_MAX_PASSES = 4
_HLS_CQ_START       = 26.0
_HLS_CQ_RANGE       = (14.0, 40.0)


def _get_audio_track_ids_for_episode(file_path: Path) -> list[dict]:
    """
    Возвращает список аудиодорожек файла.
    [{'index': 0, 'title': 'Русский', 'lang': 'rus'}, ...]
    """
    tracks = []
    try:
        fp = subprocess.run(
            [FFPROBE_PATH, "-v", "error", "-select_streams", "a",
             "-show_entries", "stream=index:stream_tags=title,language",
             "-of", "json", str(file_path)],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            encoding="utf-8", errors="replace"
        )
        streams = json.loads(fp.stdout).get("streams", [])
        for i, s in enumerate(streams):
            tags  = s.get("tags", {})
            title = tags.get("title", "") or tags.get("language", "") or f"Track {i + 1}"
            lang  = tags.get("language", "und")
            tracks.append({"index": i, "title": title, "lang": lang})
    except Exception:
        pass
    return tracks


def _sanitize_folder(name: str) -> str:
    """Убирает символы, недопустимые в именах папок."""
    return re.sub(r'[\\/:*?"<>|]', "_", name).strip()


def _audio_layout(tracks: list[dict]) -> tuple:
    """Подпись набора аудиодорожек файла: файлы с одинаковой подписью делят одно назначение озвучек."""
    return tuple((t["title"].strip().lower(), t["lang"].lower()) for t in tracks)


def _select_audio_voices_multi_res(file_path: Path, tracks: list[dict] | None = None) -> list[dict]:
    """
    Автоматически определяет все аудиодорожки файла.
    Для каждой дорожки предлагает выбрать название озвучки из списка, ввести вручную
    или пропустить дорожку (например, комментарии).
    Возвращает список: [{'track_index': int, 'audio_id': str, 'folder': str, 'lang': str}, ...]
    Папки называются именем озвучки: audio/{voice_name}/
    Файлы: {ep_name}.{voice_name}.mka
    """
    if tracks is None:
        tracks = _get_audio_track_ids_for_episode(file_path)
    if not tracks:
        console.print(f"[bold red]❌ Не удалось определить аудиодорожки:[/] {file_path.name}")
        return []

    # Таблица дорожек файла
    tt = Table(
        title=f"🎵 Аудиодорожки · {file_path.name[:55]}",
        box=box.ROUNDED, border_style="purple", title_style="bold purple"
    )
    tt.add_column("#", justify="center", style="magenta")
    tt.add_column("Авто-название", style="bold white")
    tt.add_column("Язык", style="cyan")
    for t in tracks:
        tt.add_row(str(t["index"] + 1), t["title"], t["lang"])
    console.print(tt)

    # Таблица доступных озвучек
    vt = Table(
        title="🎙 Доступные озвучки",
        box=box.ROUNDED, border_style="green", title_style="bold green"
    )
    vt.add_column("#", justify="center", style="cyan", no_wrap=True)
    vt.add_column("Название", style="yellow")
    for i, v in enumerate(_VOICE_OPTIONS, 1):
        vt.add_row(str(i), v)
    vt.add_row("0", "[dim]Ввести вручную[/]")
    console.print(vt)

    console.print(
        "\n  [dim]Для каждой дорожки: выберите номер озвучки из списка "
        "или [cyan]0[/] для ручного ввода, [cyan]-[/] — не брать дорожку.[/]\n"
    )

    voices = []
    used_names: set[str] = set()

    for t in tracks:
        num = str(t["index"] + 1)
        auto_name = _sanitize_folder(t["title"]) or f"Track{num}"

        raw_name = None
        while True:
            pick = Prompt.ask(
                f"  [magenta]Дорожка #{num}[/] [dim]({t['title']} / {t['lang']})[/] — номер озвучки",
                default="0",
            ).strip()

            if pick == "-":
                console.print(f"  [dim]Дорожка #{num} пропущена.[/]")
                break
            if pick.isdigit() and 1 <= int(pick) <= len(_VOICE_OPTIONS):
                raw_name = _VOICE_OPTIONS[int(pick) - 1]
                break
            elif pick == "0" or not pick.isdigit():
                raw_name = Prompt.ask(
                    f"  [magenta]Дорожка #{num}[/] — введите название озвучки",
                    default=auto_name,
                )
                if raw_name.strip():
                    break
                console.print("  [red]Название не может быть пустым.[/]")
            else:
                console.print(f"  [red]Введите число от 0 до {len(_VOICE_OPTIONS)}.[/]")

        if raw_name is None:
            continue

        sanitized = _sanitize_folder(raw_name.strip()) or f"Track{num}"
        # Гарантируем уникальность
        unique = sanitized
        suffix = 2
        while unique in used_names:
            unique = f"{sanitized}_{suffix}"
            suffix += 1
        used_names.add(unique)

        voices.append({
            "track_index": t["index"],
            "audio_id":    num,
            "folder":      unique,
            "lang":        t["lang"],
        })

    if not voices:
        return []

    # Итоговая таблица подтверждения
    confirm_t = Table(
        title="✅ Назначенные озвучки",
        box=box.ROUNDED, border_style="green", title_style="bold green"
    )
    confirm_t.add_column("#", justify="center", style="magenta")
    confirm_t.add_column("Озвучка", style="bold white")
    confirm_t.add_column("Папка", style="cyan")
    confirm_t.add_column("Шаблон файла", style="dim")
    for v in voices:
        confirm_t.add_row(
            v["audio_id"],
            v["folder"],
            f"audio/{v['folder']}/",
            f"{{серия}}.{v['folder']}.mka",
        )
    console.print(confirm_t)

    return voices


# Максимальный размер кадра (ширина и высота), который декодирует NVDEC.
# H.264 — только до 4096 (даже на RTX 40), HEVC/AV1/VP9 — до 8192.
# Кодек не из списка считаем поддерживаемым: если NVDEC всё же упадёт, сработает повтор на CPU.
_NVDEC_MAX_SIZE = {"h264": 4096, "mpeg2video": 4080, "mpeg1video": 4080, "mpeg4": 2048,
                   "vc1": 2048, "hevc": 8192, "av1": 8192, "vp9": 8192, "vp8": 4096}


def _needs_cpu_decode(file_path: Path) -> str | None:
    """Причина декодировать исходник на CPU (NVDEC не потянет) или None, если GPU справится."""
    try:
        r = subprocess.run(
            [FFPROBE_PATH, "-v", "error", "-select_streams", "v:0",
             "-show_entries", "stream=codec_name,width,height", "-of", "json", str(file_path)],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, encoding="utf-8", errors="replace",
        )
        s = (json.loads(r.stdout or "{}").get("streams") or [{}])[0]
    except Exception:
        return None
    codec, w, h = s.get("codec_name", ""), int(s.get("width") or 0), int(s.get("height") or 0)
    limit = _NVDEC_MAX_SIZE.get(codec)
    if limit and max(w, h) > limit:
        return f"{codec.upper()} {w}×{h} — видеокарта декодирует такой кодек только до {limit}px"
    return None


def _hls_video_input_args(
    input_path: Path,
    ffmpeg_path: str,
    widths: list[int],
    cpu_decode: bool,
    seek: tuple[float, float] | None = None,
) -> list[str]:
    """
    Начало ffmpeg-команды для видео: декодирование исходника и split → scale_cuda
    на каждую ширину из widths. Выходы фильтра — [v0out], [v1out], … в том же порядке.
    seek=(начало, длина) — взять только отрезок (для калибровки).
    """
    # Метки по ИНДЕКСУ (а не по высоте), чтобы избежать коллизий при одинаковой высоте.
    # Высота -2 в scale_cuda: авторасчёт с сохранением aspect ratio (выравнивание до чётного).
    # format=nv12 конвертирует 10-bit -> 8-bit прямо на GPU (нужно для h264_nvenc).
    # Все слоты <= 3840px, libx264/hwdownload не нужны -- всё остаётся на GPU.
    split_labels = "".join("[v%d]" % i for i in range(len(widths)))
    scales = ";".join("[v%d]scale_cuda=%d:-2:format=nv12[v%dout]" % (i, w, i) for i, w in enumerate(widths))
    seek_args = ["-ss", "%.3f" % seek[0], "-t", "%.3f" % seek[1]] if seek else []
    if cpu_decode:
        # NVDEC не берёт исходник (напр. H.264 шире 4096px): декодируем на CPU,
        # переводим в 8-bit nv12 и загружаем кадры на GPU — масштаб и NVENC остаются на видеокарте
        return [
            ffmpeg_path,
            "-nostdin", "-y",
            "-init_hw_device", "cuda=cu",
            "-filter_hw_device", "cu",
            *seek_args, "-i", str(input_path),
            "-filter_complex", "[0:v]format=nv12,hwupload_cuda,split=%d%s;%s" % (len(widths), split_labels, scales),
        ]
    return [
        ffmpeg_path,
        "-nostdin", "-y",
        "-hwaccel", "cuda",
        "-hwaccel_output_format", "cuda",
        *seek_args, "-i", str(input_path),
        "-filter_complex", "[0:v]split=%d%s;%s" % (len(widths), split_labels, scales),
    ]


def _hls_video_encode_args(vbr: int, rate_control: dict | None) -> list[str]:
    """
    Параметры h264_nvenc для одного качества.
    rate_control = {'cq', 'maxrate'} из _calibrate_hls_cq → постоянное качество с потолком битрейта.
    Без него (калибровка не удалась) — средний битрейт vbr с потолком 2×.
    """
    # force_key_frames: ставим IDR-кадр ровно каждые _HLS_SEGMENT_TIME секунд,
    # независимо от fps. Без этого NVENC ставит keyframe раз в 250 кадров (~10 с),
    # и HLS не может резать чаще, чем стоят ключевые кадры → сегменты по 10 с.
    # Одинаковое выражение для всех качеств = выровненные границы сегментов (важно для ABR).
    args = [
        "-c:v",              "h264_nvenc",
        "-preset",           _HLS_NVENC_PRESET,
        "-tune",             "hq",
        "-rc",               "vbr",
        "-spatial-aq",       "1",       # биты из пёстрых участков в плоские (меньше бандинга)
        "-rc-lookahead",     "32",
        "-forced-idr",       "1",       # форсированные keyframe = настоящие IDR
        "-force_key_frames", "expr:gte(t,n_forced*%d)" % _HLS_SEGMENT_TIME,
    ]
    if rate_control:
        maxrate = rate_control["maxrate"]
        return args + ["-cq", "%.2f" % rate_control["cq"], "-b:v", "0",
                       "-maxrate", str(maxrate), "-bufsize", str(2 * maxrate)]
    return args + ["-b:v", str(vbr), "-maxrate", str(2 * vbr), "-bufsize", str(4 * vbr)]


def _build_video_ffmpeg_cmd(
    input_path: Path,
    episode_out: Path,
    ffmpeg_path: str,
    resolutions: list | None = None,
    cpu_decode: bool = False,
    rate_control: list[dict] | None = None,
) -> list[str]:
    """
    Строит ffmpeg-команду для видео (N разрешений, без аудио).
    Верхний слот автоматически адаптируется под ширину источника (4K или 5K).
    Структура выхода: episode_out/{360p,480p,...}/seg%03d.ts + master.m3u8
    resolutions передаётся из _process_episode_multi_res чтобы не дублировать ffprobe.
    rate_control — результат _calibrate_hls_cq (по элементу на разрешение) или None.
    """
    if resolutions is None:
        resolutions = _get_resolutions_for(input_path)

    cmd = _hls_video_input_args(input_path, ffmpeg_path, [w for _, w, _, _ in resolutions], cpu_decode)
    for i, (res, w, h, vbr) in enumerate(resolutions):
        out_dir = episode_out / res
        out_dir.mkdir(parents=True, exist_ok=True)
        seg_path = out_dir.as_posix() + "/seg%03d.ts"
        cmd += [
            "-map", "[v%dout]" % i,
            *_hls_video_encode_args(vbr, rate_control[i] if rate_control else None),
            "-f",                    "hls",
            "-hls_time",             str(_HLS_SEGMENT_TIME),
            "-hls_playlist_type",    "vod",
            "-hls_flags",            "independent_segments",  # каждый сегмент самодостаточен
            "-hls_segment_filename", seg_path,
            str(out_dir / "master.m3u8"),
        ]

    return cmd


def _analyze_source_bitrate(file_path: Path) -> dict | None:
    """
    Битрейт видео исходника по окнам длиной в HLS-сегмент — по размерам пакетов, без декодирования.
    Возвращает {'rates': [бит/с по окнам], 'avg': …, 'p99': …} или None, если ffprobe ничего не дал.
    """
    try:
        r = subprocess.run(
            [FFPROBE_PATH, "-v", "error", "-select_streams", "v:0",
             "-show_entries", "packet=pts_time,size", "-of", "csv=p=0", str(file_path)],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            encoding="utf-8", errors="replace",
        )
    except Exception:
        return None
    packets = []
    for line in r.stdout.splitlines():
        parts = line.split(",")
        try:
            packets.append((float(parts[0]), int(parts[1])))
        except (ValueError, IndexError):
            continue
    if not packets:
        return None

    # Окна отсчитываются от начала файла — как и -ss при калибровке
    start = min(pts for pts, _ in packets)
    sizes: dict[int, int] = {}
    for pts, size in packets:
        w = int((pts - start) // _HLS_SEGMENT_TIME)
        sizes[w] = sizes.get(w, 0) + size
    rates = [sizes.get(w, 0) * 8 / _HLS_SEGMENT_TIME for w in range(max(sizes))]  # последнее окно неполное
    if len(rates) < 3 or not any(rates):
        return None
    srt = sorted(rates)
    return {
        "rates": rates,
        "avg":   sum(rates) / len(rates),
        "p99":   srt[min(len(srt) - 1, int(0.99 * len(srt)))],
    }


def _pick_calibration_windows(rates: list[float], count: int) -> list[float]:
    """
    Начала калибровочных окон (сек). Окна сортируются по битрейту исходника и делятся
    на count равных страт, из каждой берётся середина: выборка покрывает и тихие сцены,
    и экшен, а простое среднее по окнам оценивает среднее по всей серии.
    """
    order = sorted(range(len(rates)), key=rates.__getitem__)
    count = min(count, len(order))
    picks = {order[int((k + 0.5) / count * len(order))] for k in range(count)}
    return [w * _HLS_SEGMENT_TIME for w in sorted(picks)]


def _next_hls_cq(history: list[tuple[float, float]], target: float) -> float:
    """Следующий CQ по замерам [(cq, битрейт), …]: линейная интерполяция по логарифму битрейта."""
    cq1, rate1 = history[-1]
    slope = -math.log(2) / 6  # у H.264 +6 к QP ≈ битрейт /2
    if len(history) > 1:
        cq0, rate0 = history[-2]
        if abs(cq1 - cq0) > 0.1 and rate0 > 0 and rate1 != rate0:
            slope = min(-0.04, max(-0.25, (math.log(rate1) - math.log(rate0)) / (cq1 - cq0)))
    lo, hi = _HLS_CQ_RANGE
    return min(hi, max(lo, cq1 + (math.log(target) - math.log(rate1)) / slope))


def _hls_calibration_pass(
    file_path: Path,
    ffmpeg_path: str,
    resolutions: list,
    rungs: list[int],
    rate_control: list[dict],
    windows: list[float],
    cpu_decode: bool,
    cal_dir: Path,
    on_window,
) -> list[float] | None:
    """
    Кодирует калибровочные окна для разрешений rungs (индексы в resolutions)
    и возвращает оценку среднего битрейта серии по каждому или None, если ffmpeg упал.
    Окна пишутся в .ts — как настоящие сегменты, вместе с накладными расходами контейнера.
    """
    totals = [0] * len(rungs)
    for wi, start in enumerate(windows, 1):
        on_window(wi, len(windows))
        cmd = _hls_video_input_args(
            file_path, ffmpeg_path, [resolutions[i][1] for i in rungs], cpu_decode,
            seek=(start, _HLS_SEGMENT_TIME),
        )
        cmd[1:1] = ["-hide_banner", "-loglevel", "error"]
        outs = []
        for k, i in enumerate(rungs):
            out = cal_dir / f"{resolutions[i][0]}.ts"
            outs.append(out)
            cmd += ["-map", "[v%dout]" % k,
                    *_hls_video_encode_args(resolutions[i][3], rate_control[i]),
                    "-f", "mpegts", str(out)]
        if run_ffmpeg(cmd, file_name=file_path.name) != 0:
            return None
        for k, out in enumerate(outs):
            totals[k] += out.stat().st_size
    return [b * 8 / (_HLS_SEGMENT_TIME * len(windows)) for b in totals]


def _calibrate_hls_cq(
    file_path: Path,
    ffmpeg_path: str,
    resolutions: list,
    source: dict,
    cpu_decode: bool,
    work_out: Path,
    on_status,
) -> list[dict] | None:
    """
    Подбирает каждому разрешению CQ так, чтобы средний битрейт серии совпал с битрейтом
    из resolutions: сложные сцены получают больше, простые — меньше, а размер серии прежний.

    source — результат _analyze_source_bitrate. По нему выбираются _HLS_CAL_WINDOWS окон
    разной сложности; они кодируются с пробным CQ, по замеру CQ поправляется
    (до _HLS_CAL_MAX_PASSES проходов, пока ошибка больше _HLS_CAL_TOLERANCE).
    Повторные проходы кодируют только разрешения, которые ещё не попали в цель.

    Возвращает [{'cq': …, 'maxrate': …}, …] по порядку resolutions или None, если ffmpeg упал.
    """
    targets = [vbr for *_, vbr in resolutions]
    # Потолок битрейта: насколько пики сложности исходника выше среднего (в пределах 2–3×)
    peak = min(3.0, max(2.0, source["p99"] / source["avg"]))
    rate_control = [{"cq": _HLS_CQ_START, "maxrate": int(t * peak)} for t in targets]
    windows = _pick_calibration_windows(source["rates"], _HLS_CAL_WINDOWS)
    history: list[list[tuple[float, float]]] = [[] for _ in resolutions]
    active = list(range(len(resolutions)))

    cal_dir = work_out / "_calibration"
    cal_dir.mkdir(parents=True, exist_ok=True)
    try:
        for n in range(1, _HLS_CAL_MAX_PASSES + 1):
            rates = _hls_calibration_pass(
                file_path, ffmpeg_path, resolutions, active, rate_control, windows, cpu_decode, cal_dir,
                lambda w, total: on_status(f"подбор качества: проход {n}, окно {w}/{total}"),
            )
            if rates is None:
                return None
            still = []
            for i, rate in zip(active, rates):
                history[i].append((rate_control[i]["cq"], rate))
                if rate <= 0 or abs(rate / targets[i] - 1) <= _HLS_CAL_TOLERANCE:
                    continue
                cq = _next_hls_cq(history[i], targets[i])
                if abs(cq - rate_control[i]["cq"]) < 0.05:  # упёрлись в границу _HLS_CQ_RANGE
                    continue
                rate_control[i]["cq"] = cq
                still.append(i)
            active = still
            if not active:
                break
    finally:
        shutil.rmtree(cal_dir, ignore_errors=True)
    return rate_control

def _get_audio_channels(file_path: Path) -> list[int]:
    """Число каналов каждой аудиодорожки файла по порядку дорожек (0 — не удалось узнать)."""
    try:
        r = subprocess.run(
            [FFPROBE_PATH, "-v", "error", "-select_streams", "a",
             "-show_entries", "stream=channels", "-of", "csv=p=0", str(file_path)],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            encoding="utf-8", errors="replace",
        )
    except Exception:
        return []
    return [int(x) if x.isdigit() else 0 for x in (l.strip().strip(",") for l in r.stdout.splitlines())]


def _build_audio_ffmpeg_cmd(
    input_path: Path,
    episode_out: Path,
    voices: list[dict],
    ffmpeg_path: str,
    ep_name: str,
) -> list[str]:
    """
    Строит ОДНУ ffmpeg-команду для всех аудиодорожек сразу.
    Дорожки копируются без перекодирования; только 5.1 и 7.1 (6 и 8 каналов)
    сводятся в стерео AAC 192k. Каждая сохраняется:
      audio/{voice_name}/{ep_name}.{voice_name}.mka
    """
    cmd = [ffmpeg_path, "-nostdin", "-y", "-i", str(input_path), "-vn"]
    channels = _get_audio_channels(input_path)

    for v in voices:
        track_dir = episode_out / "audio" / v["folder"]
        track_dir.mkdir(parents=True, exist_ok=True)
        out_filename = f"{ep_name}.{v['folder']}.mka"
        out_file = (track_dir / out_filename).as_posix()
        idx = v["track_index"]
        if idx < len(channels) and channels[idx] in (6, 8):
            codec = ["-c:a", "aac", "-b:a", "192k", "-ac", "2"]
        else:
            codec = ["-c:a", "copy"]
        cmd += ["-map", f"0:a:{idx}", *codec, out_file]

    return cmd


def _write_master_playlist(episode_out: Path, voices: list[dict], resolutions: list | None = None) -> None:
    """
    Генерирует master.m3u8 в корне папки серии.
    Аудио вынесено в отдельные .mka файлы, поэтому в плейлисте только видеопотоки.
    EXT-X-STREAM-INF — по одной записи на каждое разрешение.
    resolutions — список из _get_resolutions_for(); если None, берётся _HLS_RESOLUTIONS.
    """
    if resolutions is None:
        resolutions = _HLS_RESOLUTIONS
    lines = ["#EXTM3U", "#EXT-X-VERSION:3", "#EXT-X-INDEPENDENT-SEGMENTS", ""]

    # Видеопотоки (без аудиогрупп — аудио в .mka)
    for res_name, w, h, vbr in resolutions:
        # Все качества кодируются h264_nvenc (H.264 High), поэтому CODECS одинаковый
        codecs = "avc1.640028"
        lines.append(
            f'#EXT-X-STREAM-INF:BANDWIDTH={vbr},'
            f'RESOLUTION={w}x{h},'
            f'CODECS="{codecs}"'
        )
        lines.append(f"{res_name}/master.m3u8")

    lines.append("")

    master_path = episode_out / "master.m3u8"
    master_path.write_text("\n".join(lines), encoding="utf-8")
    console.print(f"  [green]✓[/] master.m3u8 → [dim]{master_path}[/]")


def _process_episode_multi_res(
    file_path: Path,
    episode_out: Path,
    voices: list[dict],
    ffmpeg_path: str,
    progress,
    task_id,
    file_index: int,
    total_files: int,
) -> str:
    """
    Возвращает "ok", "skip" (уже готово) или "fail".
    Конвертирует одну серию:
      0) анализ битрейта исходника и подбор CQ под серию (_calibrate_hls_cq)
      1) видео → 6 разрешений (одна ffmpeg-команда, split)
      2) каждая аудиодорожка → отдельный прогон ffmpeg
      3) упаковывает качества в zip сразу в корень тайтла

    Экономия ресурса SSD:
      Если задан ANITOOLS_WORK_DIR (RAM-диск или HDD), все промежуточные
      файлы (HLS-сегменты, mka) пишутся туда, а на SSD попадает только
      финальный zip + mka — т.е. каждый байт пишется на SSD ровно один раз
      вместо двух (сегменты + zip).
    """
    title_out = episode_out.parent  # корневая папка тайтла (финальное место)
    title_out.mkdir(parents=True, exist_ok=True)

    # ── Resume: пропускаем уже сконвертированные серии ─────────
    # Порядок записи результатов: zip → (zip 4K) → mka. Значит если основной
    # zip и все ожидаемые mka на месте — серия полностью готова
    # (отдельный 4K-zip, если он был нужен, пишется до переноса аудио).
    ep_name_check = episode_out.name
    zip_check = title_out / f"{episode_out.name}.zip"
    audio_done = all(
        (p := title_out / "audio" / v["folder"] / f"{ep_name_check}.{v['folder']}.mka").exists()
        and p.stat().st_size > 0
        for v in voices
    )
    if zip_check.exists() and zip_check.stat().st_size > 0 and audio_done:
        ts = datetime.datetime.now().strftime("%H:%M:%S")
        if progress and task_id is not None:
            progress.update(
                task_id,
                description=(
                    f"[{ts}] ⏩ [dim]Пропуск (уже готово):[/] [cyan]{file_path.name}[/] "
                    f"([bold magenta]{file_index}/{total_files}[/])"
                )
            )
        console.print(f"  [yellow]⏩[/] [dim]{episode_out.name} — zip и аудио уже на месте, пропускаю[/]")
        return "skip"

    # Рабочая папка: RAM-диск/HDD если задан ANITOOLS_WORK_DIR, иначе как раньше
    if ANITOOLS_WORK_DIR:
        work_out = Path(ANITOOLS_WORK_DIR) / _sanitize_folder(title_out.name) / episode_out.name
    else:
        work_out = episode_out
    work_out.mkdir(parents=True, exist_ok=True)

    duration = get_video_duration(file_path)
    resolutions = _get_resolutions_for(file_path)
    cpu_reason = _needs_cpu_decode(file_path)
    if cpu_reason:
        console.print(f"  [cyan]i[/] {cpu_reason} → декодирую на CPU, масштаб и кодирование на GPU")
    cpu_decode = bool(cpu_reason)

    # ── Качество: постоянный CQ или подбор CQ под серию ───────
    def _status(text: str) -> None:
        if progress and task_id is not None:
            ts = datetime.datetime.now().strftime("%H:%M:%S")
            progress.update(
                task_id,
                description=(
                    f"[{ts}] 🎯 [cyan]{file_path.name}[/] "
                    f"— {text} ([bold magenta]{file_index}/{total_files}[/])"
                )
            )

    if _HLS_FIXED_CQ is not None:
        rate_control = [{"cq": _HLS_FIXED_CQ, "maxrate": _HLS_FIXED_CQ_PEAK * vbr} for *_, vbr in resolutions]
        console.print(
            f"  [cyan]i[/] Постоянное качество: CQ [bold]{_HLS_FIXED_CQ:g}[/], "
            f"потолок {_HLS_FIXED_CQ_PEAK}× битрейта из таблицы"
        )
    else:
        _status("анализ битрейта")
        cal_start = time.monotonic()
        rate_control = None
        source = _analyze_source_bitrate(file_path)
        if source:
            rate_control = _calibrate_hls_cq(file_path, ffmpeg_path, resolutions, source, cpu_decode, work_out, _status)
            if rate_control is None and not cpu_decode:
                console.print(f"  [yellow]⚠[/] Подбор качества не прошёл на видеокарте — повторяю с декодированием на CPU")
                cpu_decode = True
                rate_control = _calibrate_hls_cq(file_path, ffmpeg_path, resolutions, source, cpu_decode, work_out, _status)
        if rate_control:
            cal_time = int(time.monotonic() - cal_start)
            console.print(
                f"  [green]✓[/] CQ под серию [dim]({cal_time // 60}:{cal_time % 60:02d})[/]: "
                + " · ".join(f"{res} [bold]{rc['cq']:.1f}[/]" for (res, *_), rc in zip(resolutions, rate_control))
            )
        else:
            console.print(f"  [yellow]⚠[/] Не удалось подобрать качество — кодирую со средним битрейтом из таблицы")

    # ── Видео ──────────────────────────────────────────────────
    ts = datetime.datetime.now().strftime("%H:%M:%S")
    if progress and task_id is not None:
        progress.update(
            task_id,
            description=(
                f"[{ts}] 🎬 [cyan]{file_path.name}[/] "
                f"— видео ([bold magenta]{file_index}/{total_files}[/])"
            )
        )

    video_cmd = _build_video_ffmpeg_cmd(file_path, work_out, ffmpeg_path, resolutions, cpu_decode, rate_control)
    ret = run_ffmpeg(
        video_cmd, progress, task_id,
        file_path.name, duration,
        file_index, total_files, is_hls=True
    )
    if ret != 0 and not cpu_decode:
        # NVDEC мог не справиться с чем-то, чего нет в таблице лимитов, — пробуем ещё раз с CPU-декодированием
        console.print(f"  [yellow]⚠[/] Видеокарта не смогла декодировать {file_path.name} — повторяю с декодированием на CPU")
        shutil.rmtree(work_out, ignore_errors=True)
        work_out.mkdir(parents=True, exist_ok=True)
        video_cmd = _build_video_ffmpeg_cmd(file_path, work_out, ffmpeg_path, resolutions, True, rate_control)
        ret = run_ffmpeg(
            video_cmd, progress, task_id,
            file_path.name, duration,
            file_index, total_files, is_hls=True
        )
    if ret != 0:
        console.print(f"[bold red]❌ Ошибка видео:[/] {file_path.name}")
        shutil.rmtree(work_out, ignore_errors=True)
        return "fail"

    if duration > 0:
        with_target = _HLS_FIXED_CQ is None
        console.print(
            ("  [dim]Мбит/с (факт / цель): " if with_target else "  [dim]Мбит/с: ") + " · ".join(
                f"{res} {sum(f.stat().st_size for f in (work_out / res).glob('*.ts')) * 8 / duration / 1e6:.2f}"
                + (f" / {vbr / 1e6:g}" if with_target else "")
                for res, _, _, vbr in resolutions
            ) + "[/]"
        )

    # ── Аудио (все дорожки одним вызовом) ────────────────────
    ts = datetime.datetime.now().strftime("%H:%M:%S")
    if progress and task_id is not None:
        voices_str = ", ".join(v["folder"] for v in voices)
        progress.update(
            task_id,
            description=(
                f"[{ts}] 🎵 [cyan]{file_path.name}[/] "
                f"— аудио [{voices_str}] "
                f"([bold magenta]{file_index}/{total_files}[/])"
            )
        )
    ep_name = episode_out.name
    audio_cmd = _build_audio_ffmpeg_cmd(file_path, work_out, voices, ffmpeg_path, ep_name)
    ret = run_ffmpeg(
        audio_cmd, progress, task_id,
        file_path.name, duration,
        file_index, total_files, is_hls=True
    )
    if ret != 0:
        console.print(f"[bold red]❌ Ошибка аудио:[/] {file_path.name}")
        shutil.rmtree(work_out, ignore_errors=True)
        return "fail"

    # ── Архивируем папки с качествами СРАЗУ в корень тайтла ───
    # (читаем из work_out, пишем в title_out — без промежуточного zip и move)
    ts = datetime.datetime.now().strftime("%H:%M:%S")
    if progress and task_id is not None:
        progress.update(
            task_id,
            description=(
                f"[{ts}] 🗜 [cyan]{file_path.name}[/] "
                f"— архивирование "
                f"([bold magenta]{file_index}/{total_files}[/])"
            )
        )

    quality_folders = [r[0] for r in resolutions]  # ["360p", "480p", ..., "4K"/"5K"]
    zip_path = title_out / f"{episode_out.name}.zip"

    _4K_SIZE_THRESHOLD = 7 * 1024 ** 3  # 7 ГБ в байтах

    # Проверяем размер папки верхнего качества (4K или 5K)
    top_quality = resolutions[-1][0]  # "4K" или "5K"
    _4k_dir = work_out / top_quality
    _4k_size = 0
    if _4k_dir.exists():
        _4k_size = sum(f.stat().st_size for f in _4k_dir.rglob("*") if f.is_file())

    _4k_separate = _4k_dir.exists() and _4k_size > _4K_SIZE_THRESHOLD

    if _4k_separate:
        _4k_gb = _4k_size / 1024 ** 3
        console.print(
            f"  [yellow]⚠[/] Папка {top_quality} весит [bold]{_4k_gb:.2f} ГБ[/] — "
            f"будет упакована в отдельный архив"
        )

    try:
        # Основной архив — все качества кроме 4K (если 4K идёт отдельно)
        if zip_path.exists():
            zip_path.unlink()
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_STORED) as zf:
            for q in quality_folders:
                if _4k_separate and q == top_quality:
                    continue  # верхнее качество пойдёт в отдельный архив
                q_dir = work_out / q
                if not q_dir.exists():
                    continue
                for file in q_dir.rglob("*"):
                    if file.is_file():
                        zf.write(file, arcname=file.relative_to(work_out))
                # Удаляем папку сразу после добавления в архив — экономим место
                shutil.rmtree(q_dir)
        console.print(f"  [green]✓[/] архив → [dim]{zip_path}[/]")
        clear_readonly(zip_path)

        # Отдельный архив для верхнего качества (4K / 5K)
        if _4k_separate:
            zip_4k_path = title_out / f"{episode_out.name}.{top_quality}.zip"
            if zip_4k_path.exists():
                zip_4k_path.unlink()
            with zipfile.ZipFile(zip_4k_path, "w", zipfile.ZIP_STORED) as zf:
                for file in _4k_dir.rglob("*"):
                    if file.is_file():
                        zf.write(file, arcname=file.relative_to(work_out))
            # Удаляем сразу после упаковки
            shutil.rmtree(_4k_dir)
            console.print(f"  [green]✓[/] архив 4K → [dim]{zip_4k_path}[/]")
            clear_readonly(zip_4k_path)

        console.print(f"  [green]✓[/] папки качеств удалены")

    except Exception as e:
        console.print(f"  [bold red]❌ Ошибка архивирования:[/] {e}")
        # Недописанные архивы и рабочую папку убираем: иначе сегменты (~6–7 ГБ)
        # остаются на RAM-диске и следующие серии падают из-за нехватки места
        for zp in (zip_path, title_out / f"{episode_out.name}.{top_quality}.zip"):
            try:
                zp.unlink(missing_ok=True)
            except Exception:
                pass
        shutil.rmtree(work_out, ignore_errors=True)
        return "fail"

    # ── Аудио → title/audio/{voice}/ ──────────────────────────
    ts = datetime.datetime.now().strftime("%H:%M:%S")
    if progress and task_id is not None:
        progress.update(
            task_id,
            description=(
                f"[{ts}] 📦 [cyan]{file_path.name}[/] "
                f"— перенос в тайтл "
                f"([bold magenta]{file_index}/{total_files}[/])"
            )
        )

    try:
        ep_audio_dir = work_out / "audio"
        if ep_audio_dir.exists():
            for voice_dir in ep_audio_dir.iterdir():
                if not voice_dir.is_dir():
                    continue
                dest_voice_dir = title_out / "audio" / voice_dir.name
                dest_voice_dir.mkdir(parents=True, exist_ok=True)
                clear_readonly(dest_voice_dir)
                for mka in voice_dir.iterdir():
                    if mka.is_file():
                        dest_mka = dest_voice_dir / mka.name
                        if dest_mka.exists():
                            dest_mka.unlink()
                        # cross-device move = copy → единственная запись mka на SSD
                        shutil.move(str(mka), str(dest_mka))
                        clear_readonly(dest_mka)
            console.print(f"  [green]✓[/] аудио → [dim]{title_out / 'audio'}[/]")

        # Чистим рабочую папку (RAM-диск/HDD) и пустую папку серии на SSD
        if work_out.exists():
            shutil.rmtree(work_out, ignore_errors=True)
        if work_out != episode_out and episode_out.exists():
            shutil.rmtree(episode_out, ignore_errors=True)
        console.print(f"  [green]✓[/] рабочая папка очищена")

    except Exception as e:
        console.print(f"  [bold red]❌ Ошибка переноса:[/] {e}")
        return "fail"
    return "ok"


def _imdisk_available() -> bool:
    """Проверяет, установлен ли ImDisk (команда imdisk в PATH)."""
    if os.name != "nt":
        return False
    try:
        r = subprocess.run(
            ["imdisk", "-l"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            timeout=5,
        )
        return r.returncode in (0, 1)  # 1 = нет смонтированных дисков, но imdisk есть
    except Exception:
        return False


def _find_free_drive_letter() -> str | None:
    """Возвращает свободную букву диска (например 'R'), ищет с R, затем Z→D."""
    if os.name != "nt":
        return None
    used = set()
    try:
        import string
        import ctypes
        bitmask = ctypes.windll.kernel32.GetLogicalDrives()
        for i, letter in enumerate(string.ascii_uppercase):
            if bitmask & (1 << i):
                used.add(letter)
    except Exception:
        import string
        for letter in string.ascii_uppercase:
            if os.path.exists(f"{letter}:\\"):
                used.add(letter)
    for letter in "RZYXWVUTSQPONMLKJIHGFED":
        if letter not in used:
            return letter
    return None


def _create_ramdisk(size_gb: int, letter: str) -> bool:
    """
    Создаёт RAM-диск через ImDisk (-t vm) и форматирует его в NTFS.
    Требует прав администратора. Возвращает True при успехе.
    """
    try:
        r = subprocess.run(
            ["imdisk", "-a", "-t", "vm", "-s", f"{size_gb}G",
             "-m", f"{letter}:", "-p", "/fs:ntfs /q /y"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            encoding="utf-8", errors="replace", timeout=120,
        )
        if r.returncode != 0:
            console.print(f"  [red]ImDisk:[/] {(r.stdout or '').strip()[:300]}")
            return False
        for _ in range(20):  # ждём появления диска
            if os.path.exists(f"{letter}:\\"):
                return True
            time.sleep(0.25)
        return os.path.exists(f"{letter}:\\")
    except Exception as e:
        console.print(f"  [bold red]❌ Не удалось создать RAM-диск:[/] {e}")
        return False


def _remove_ramdisk(letter: str) -> None:
    """Отмонтирует RAM-диск (принудительно)."""
    try:
        subprocess.run(
            ["imdisk", "-D", "-m", f"{letter}:"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30,
        )
    except Exception:
        pass


# ── Состояние RAM-диска (для очистки после жёсткого убийства скрипта) ──────
# Если скрипт прибили крестиком окна / taskkill — atexit и finally не отработают,
# и диск останется висеть. Поэтому пишем букву созданного диска в файл состояния,
# а при следующем запуске пункта 7 принудительно отмонтируем всё, что осталось.

def _ramdisk_state_file() -> Path:
    return Path(tempfile.gettempdir()) / "anitools_ramdisk.json"


def _save_ramdisk_state(letter: str) -> None:
    """Добавляет букву диска в файл состояния."""
    try:
        sf = _ramdisk_state_file()
        letters = []
        if sf.exists():
            try:
                letters = json.loads(sf.read_text(encoding="utf-8"))
            except Exception:
                letters = []
        if letter not in letters:
            letters.append(letter)
        sf.write_text(json.dumps(letters), encoding="utf-8")
    except Exception:
        pass


def _clear_ramdisk_state(letter: str) -> None:
    """Убирает букву диска из файла состояния (после успешного отмонтирования)."""
    try:
        sf = _ramdisk_state_file()
        if not sf.exists():
            return
        try:
            letters = json.loads(sf.read_text(encoding="utf-8"))
        except Exception:
            letters = []
        letters = [x for x in letters if x != letter]
        if letters:
            sf.write_text(json.dumps(letters), encoding="utf-8")
        else:
            sf.unlink(missing_ok=True)
    except Exception:
        pass


def _cleanup_orphan_ramdisks() -> None:
    """
    Отмонтирует RAM-диски, оставшиеся от прошлого аварийно завершённого запуска.
    imdisk -D работает только со своими виртуальными дисками, реальные диски
    с той же буквой не трогает, так что вызывать безопасно.
    """
    sf = _ramdisk_state_file()
    if not sf.exists():
        return
    try:
        letters = json.loads(sf.read_text(encoding="utf-8"))
    except Exception:
        letters = []
    if not letters:
        sf.unlink(missing_ok=True)
        return
    if _imdisk_available():
        for letter in letters:
            console.print(f"  [yellow]⚠[/] Найден незакрытый RAM-диск [bold]{letter}:[/] от прошлого запуска — отмонтирую.")
            _remove_ramdisk(letter)
    try:
        sf.unlink(missing_ok=True)
    except Exception:
        pass


# Ссылка на колбэк обработчика консоли (чтобы его не съел сборщик мусора)
_CONSOLE_HANDLER_REF = None


def _install_console_ctrl_handler(cleanup_fn) -> None:
    """
    Ставит обработчик закрытия окна консоли (крестик / logoff / shutdown),
    чтобы успеть отмонтировать RAM-диск до того, как Windows убьёт процесс.
    Ctrl+C/Ctrl+Break не перехватываем — их обрабатывает обычный KeyboardInterrupt.
    """
    global _CONSOLE_HANDLER_REF
    if os.name != "nt":
        return
    try:
        import ctypes
        # CTRL_C=0, CTRL_BREAK=1, CTRL_CLOSE=2, CTRL_LOGOFF=5, CTRL_SHUTDOWN=6
        HANDLER = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_uint)

        def _handler(ctrl_type):
            if ctrl_type in (2, 5, 6):  # окно закрыто / logoff / shutdown
                try:
                    cleanup_fn()
                except Exception:
                    pass
            return False  # отдаём управление дальше (процесс завершится штатно)

        _CONSOLE_HANDLER_REF = HANDLER(_handler)
        ctypes.windll.kernel32.SetConsoleCtrlHandler(_CONSOLE_HANDLER_REF, True)
    except Exception:
        pass


def _setup_work_dir() -> tuple[str, "callable"]:
    """
    Интерактивно выбирает, куда писать промежуточные файлы HLS-конвертации.
    Возвращает (work_dir, cleanup): путь рабочей папки ('' = рядом с выходом)
    и функцию очистки (отмонтирование RAM-диска по завершении).
    """
    noop = lambda: None

    # Сначала подчищаем диски, оставшиеся от прошлого аварийного завершения
    _cleanup_orphan_ramdisks()

    console.print(Panel(
        "[bold white]Куда писать временные файлы конвертации?[/]\n"
        "[dim]Промежуточные сегменты/аудио весят ~6–7 ГБ на серию. "
        "RAM-диск полностью снимает нагрузку с SSD.[/]",
        title="💾 Рабочая папка", border_style="magenta", box=box.ROUNDED
    ))

    wt = Table(box=box.ROUNDED, border_style="magenta")
    wt.add_column("#", justify="center", style="bold magenta")
    wt.add_column("Вариант", style="bold white")
    wt.add_column("Описание", style="dim")
    wt.add_row("1", "RAM-диск (ImDisk)", "не изнашивает SSD, авто-создание и закрытие")
    wt.add_row("2", "Указать папку (HDD/SSD)", "обычная папка на диске")
    wt.add_row("3", "Как раньше", "рядом с выходной папкой (на SSD)")
    console.print(wt)

    choice = Prompt.ask("  ▶ [magenta]Выбор[/]", choices=["1", "2", "3"], default="1")

    # ── Вариант 3: поведение по умолчанию ──────────────────────
    if choice == "3":
        return "", noop

    # ── Вариант 2: обычная папка ───────────────────────────────
    if choice == "2":
        path = Prompt.ask("  [magenta]Путь к папке для временных файлов[/]",
                          default="D:\\anitools_tmp")
        try:
            Path(path).mkdir(parents=True, exist_ok=True)
        except Exception as e:
            console.print(f"  [red]Не удалось создать папку:[/] {e} — использую поведение по умолчанию.")
            return "", noop
        console.print(f"  [green]✓[/] Временные файлы → [bold]{path}[/]")
        return path, noop

    # ── Вариант 1: RAM-диск ────────────────────────────────────
    if os.name != "nt":
        console.print("  [yellow]RAM-диск через ImDisk доступен только на Windows. Использую поведение по умолчанию.[/]")
        return "", noop

    if not _imdisk_available():
        console.print(Panel(
            "ImDisk не найден в PATH.\n"
            "Скачай бесплатный [bold]ImDisk Toolkit[/] и установи "
            "(после установки imdisk появляется в System32).\n"
            "[dim]https://sourceforge.net/projects/imdisk-toolkit/[/]",
            title="⚠ ImDisk не установлен", border_style="yellow", box=box.ROUNDED
        ))
        fallback = Prompt.ask(
            "  ▶ [magenta]Указать обычную папку вместо RAM-диска?[/] [1-Да/2-Как раньше]",
            choices=["1", "2"], default="1"
        )
        if fallback == "1":
            path = Prompt.ask("  [magenta]Путь к папке[/]", default="D:\\anitools_tmp")
            try:
                Path(path).mkdir(parents=True, exist_ok=True)
                return path, noop
            except Exception:
                return "", noop
        return "", noop

    # Размер RAM-диска
    st = Table(box=box.ROUNDED, border_style="cyan")
    st.add_column("#", justify="center", style="bold cyan")
    st.add_column("Размер", style="bold white")
    st.add_column("Под что", style="dim")
    st.add_row("1", "10 ГБ", "обычные серии ~24 мин")
    st.add_row("2", "14 ГБ", "с запасом (рекоменд.)")
    st.add_row("3", "20 ГБ", "длинные серии")
    st.add_row("4", "32 ГБ", "фильмы / спешлы")
    st.add_row("0", "Свой размер", "ввести вручную")
    console.print(st)

    size_map = {"1": 10, "2": 14, "3": 20, "4": 32}
    size_pick = Prompt.ask("  ▶ [magenta]Размер RAM-диска[/]",
                           choices=["1", "2", "3", "4", "0"], default="2")
    if size_pick == "0":
        raw = Prompt.ask("  [magenta]Размер в ГБ[/]", default="14")
        try:
            size_gb = max(2, int(raw))
        except ValueError:
            size_gb = 14
    else:
        size_gb = size_map[size_pick]

    letter = _find_free_drive_letter()
    if not letter:
        console.print("  [red]Нет свободной буквы диска. Использую поведение по умолчанию.[/]")
        return "", noop

    console.print(f"  [dim]Создаю RAM-диск [bold]{letter}:[/] на [bold]{size_gb} ГБ[/]…[/]")
    if not _create_ramdisk(size_gb, letter):
        console.print(Panel(
            "Не удалось создать RAM-диск.\n"
            "Самая частая причина — [bold]нет прав администратора[/]. "
            "Запусти скрипт от имени администратора.",
            title="❌ RAM-диск", border_style="red", box=box.ROUNDED
        ))
        fallback = Prompt.ask(
            "  ▶ [magenta]Продолжить с обычной папкой?[/] [1-Да/2-Как раньше]",
            choices=["1", "2"], default="2"
        )
        if fallback == "1":
            path = Prompt.ask("  [magenta]Путь к папке[/]", default="D:\\anitools_tmp")
            try:
                Path(path).mkdir(parents=True, exist_ok=True)
                return path, noop
            except Exception:
                return "", noop
        return "", noop

    work_dir = f"{letter}:\\anitools_tmp"
    try:
        Path(work_dir).mkdir(parents=True, exist_ok=True)
    except Exception:
        work_dir = f"{letter}:\\"

    console.print(f"  [green]✓[/] RAM-диск [bold]{letter}:[/] ({size_gb} ГБ) создан → [bold]{work_dir}[/]")

    # Запоминаем диск, чтобы подчистить его при следующем запуске, если скрипт убьют
    _save_ramdisk_state(letter)

    cleaned = {"done": False}
    def cleanup():
        if cleaned["done"]:
            return
        cleaned["done"] = True
        console.print(f"  [dim]Отмонтирую RAM-диск {letter}:…[/]")
        _remove_ramdisk(letter)
        _clear_ramdisk_state(letter)
        console.print(f"  [green]✓[/] RAM-диск {letter}: закрыт")

    # Страховка: закрыть диск даже при аварийном выходе
    atexit.register(cleanup)
    # SIGTERM/taskkill обрабатывается через os._exit (atexit не сработает) —
    # регистрируем cleanup в реестре, который дергает _graceful_exit_handler
    register_exit_cleanup(cleanup)
    # Перехват закрытия окна консоли (крестик / logoff / shutdown)
    _install_console_ctrl_handler(cleanup)
    return work_dir, cleanup


def convert_videos_multi_res(current_dir: Path) -> None:
    """
    Пункт меню 7 — конвертация в HLS с мультиразрешением.

    Структура выхода:
      hls_multi/
        {shikimori_id} - {тайтл}/
          {episode_folder}.zip            ← 360p…2K (+4K если <7ГБ): seg%03d.ts + master.m3u8
          {episode_folder}.4K.zip         ← отдельно, если папка 4K тяжелее 7 ГБ
          audio/
            {voice_folder}/
              {episode}.{voice}.mka       ← дорожка как в исходнике; 5.1/7.1 → AAC 192k stereo
    """
    clear_screen()

    # Сканируем файлы
    extensions = [".mp4", ".mkv", ".avi", ".m2ts", ".mov"]
    files = sorted([
        f for f in current_dir.iterdir()
        if f.is_file() and f.suffix.lower() in extensions
    ])
    if not files:
        console.print(Panel(
            "Нет видеофайлов в текущей папке.",
            title="❌ Ошибка", border_style="red", box=box.DOUBLE
        ))
        restart_script()
        return

    # Группируем по тайтлу
    groups = {}
    for f in files:
        title = _parse_anime_group(f.name) or "Без названия"
        groups.setdefault(title, []).append(f)

    _show_groups_table(groups)

    regroup = Prompt.ask(
        "\n  ▶ [magenta]Группировка верна?[/] [1-Да/2-Перегруппировать вручную]",
        choices=["1", "2"], default="1"
    )
    if regroup == "2":
        groups = _regroup_manually(list(groups.values()), current_dir)
        if not groups:
            return

    output_base = current_dir / "hls_multi"
    output_base.mkdir(parents=True, exist_ok=True)

    # Для каждого тайтла — Шикимори + выбор озвучек (один раз на тайтл)
    plan: list[dict] = []
    group_items = list(groups.items())

    for gi, (title, group_files) in enumerate(group_items, 1):
        clear_screen()

        console.print(Panel(
            f"[bold white]{title}[/]\n[dim]{len(group_files)} файл(ов)[/]",
            title=f"[magenta]Тайтл {gi}/{len(group_items)}[/]",
            border_style="magenta", box=box.ROUNDED
        ))

        skip = Prompt.ask(
            "  ▶ [magenta]Конвертировать этот тайтл?[/] [1-Да/2-Пропустить]",
            choices=["1", "2"], default="1"
        )
        if skip == "2":
            console.print("[dim]  Пропущено.[/]")
            continue

        folder_name = _pick_shikimori(title)

        # Набор дорожек у файлов тайтла может отличаться (в части серий есть комментарии,
        # mp4 без звука рядом с mkv и т.п.), поэтому озвучки назначаются не по первому файлу,
        # а один раз на каждый встретившийся набор дорожек
        file_tracks = [(f, _get_audio_track_ids_for_episode(f)) for f in group_files]
        layout_files: dict[tuple, list[Path]] = {}
        for f, tracks in file_tracks:
            layout_files.setdefault(_audio_layout(tracks), []).append(f)

        layout_voices: dict[tuple, list[dict]] = {}
        used_ep_folders: set[str] = set()
        for f, tracks in file_tracks:
            if not tracks:
                console.print(f"[yellow]  ⚠ Нет аудиодорожек — файл пропущен:[/] {f.name}")
                continue
            layout = _audio_layout(tracks)
            if layout not in layout_voices:
                same = layout_files[layout]
                if len(layout_files) > 1:
                    console.print(
                        f"\n  [cyan]i[/] Набор дорожек ниже — у [bold]{len(same)}[/] из {len(group_files)} файл(ов): "
                        f"[dim]{', '.join(x.name for x in same[:3])}{' …' if len(same) > 3 else ''}[/]"
                    )
                layout_voices[layout] = _select_audio_voices_multi_res(f, tracks)
                if not layout_voices[layout]:
                    console.print(f"[yellow]  ⚠ Не выбрано ни одной дорожки — файлов пропущено: {len(same)}[/]")
            voices = layout_voices[layout]
            if not voices:
                continue

            # Имя папки серии = имя файла без расширения, очищенное.
            # Одноимённые файлы с разным расширением (01.mkv + 01.mp4) разводим по расширению
            ep_folder = _sanitize_folder(f.stem)
            if ep_folder.lower() in used_ep_folders:
                ep_folder = _sanitize_folder(f"{f.stem}.{f.suffix.lstrip('.')}")
            used_ep_folders.add(ep_folder.lower())
            episode_out = output_base / folder_name / ep_folder
            plan.append({
                "file":        f,
                "episode_out": episode_out,
                "voices":      voices,
                "folder_name": folder_name,
            })

    if not plan:
        console.print(Panel(
            "Нет задач для конвертации.",
            title="ℹ Информация", border_style="yellow", box=box.ROUNDED
        ))
        restart_script()
        return

    # Предпросмотр плана
    clear_screen()
    preview = Table(
        title="🎬 План конвертации (мульти-разрешение)",
        box=box.ROUNDED, border_style="magenta", title_style="bold magenta"
    )
    preview.add_column("#",        justify="center", style="magenta", no_wrap=True)
    preview.add_column("Файл",     style="cyan")
    preview.add_column("Тайтл",    style="bold white")
    preview.add_column("Озвучки",  style="green")
    preview.add_column("Папка",    style="dim")
    for i, task in enumerate(plan, 1):
        voices_str = ", ".join(v["folder"] for v in task["voices"])
        preview.add_row(
            str(i),
            task["file"].name[:40],
            task["folder_name"][:30],
            voices_str[:35],
            str(task["episode_out"])[:45],
        )
    console.print(preview)
    console.print(f"\n  [dim]Всего серий: [bold]{len(plan)}[/]  "
                  f"Разрешений на серию: [bold]{len(_HLS_RESOLUTIONS)}[/][/]")

    confirm = Prompt.ask(
        "\n  ▶ [magenta]Начать конвертацию?[/] [1-Да/2-Отмена]",
        choices=["1", "2"], default="1"
    )
    if confirm == "2":
        console.print(Panel("Конвертация отменена.", title="ℹ", border_style="yellow", box=box.ROUNDED))
        restart_script()
        return

    shutdown_after = Prompt.ask(
        "  ⏻ [magenta]Выключить компьютер по завершении?[/] [1-Да/2-Нет]",
        choices=["1", "2"], default="2"
    ) == "1"

    # Выбор рабочей папки для временных файлов (RAM-диск / папка / по умолчанию).
    # Делаем это после подтверждения плана, чтобы не создавать RAM-диск зря.
    global ANITOOLS_WORK_DIR
    work_dir, _ramdisk_cleanup = _setup_work_dir()
    ANITOOLS_WORK_DIR = work_dir

    # Конвертация (однопоточно — ffmpeg сам грузит GPU)
    failed: list[str] = []
    skipped_count = 0
    start_time = datetime.datetime.now()
    console.print(Panel(
        f"[bold magenta]Начало:[/] {start_time.strftime('%H:%M:%S')}  "
        f"[bold magenta]Серий:[/] {len(plan)}",
        title="🚀 Конвертация запущена", border_style="magenta", box=box.ROUNDED
    ))

    try:
        with Progress(
            SpinnerColumn(style="magenta"),
            TextColumn("[progress.description]{task.description}"),
            BarColumn(complete_style="magenta"),
            TimeRemainingColumn(),
            console=console,
        ) as progress:
            task_id = progress.add_task("🔄 Подготовка...", total=len(plan))

            for idx, item in enumerate(plan, 1):
                status = _process_episode_multi_res(
                    file_path    = item["file"],
                    episode_out  = item["episode_out"],
                    voices       = item["voices"],
                    ffmpeg_path  = FFMPEG_PATH,
                    progress     = progress,
                    task_id      = task_id,
                    file_index   = idx,
                    total_files  = len(plan),
                )
                if status == "fail":
                    failed.append(item["file"].name)
                elif status == "skip":
                    skipped_count += 1
                progress.update(task_id, advance=1)
    finally:
        # Гарантированно закрываем RAM-диск (даже при ошибке или Ctrl+C)
        _ramdisk_cleanup()

    end_time = datetime.datetime.now()
    elapsed  = str(end_time - start_time).split(".")[0]
    done_count = len(plan) - len(failed) - skipped_count
    summary = (
        f"[bold magenta]Начало:[/]    {start_time.strftime('%H:%M:%S')}\n"
        f"[bold magenta]Окончание:[/] {end_time.strftime('%H:%M:%S')}\n"
        f"[bold magenta]Время:[/]     {elapsed}\n"
        f"[bold magenta]Готово:[/]    {done_count} из {len(plan)}"
        + (f"  [dim](уже были готовы: {skipped_count})[/]" if skipped_count else "")
    )
    if failed:
        summary += f"\n[bold red]Ошибки:[/]    {len(failed)}\n" + "\n".join(f"  [red]✗[/] {n}" for n in failed)
        summary += "\n[dim]при повторном запуске готовые серии пропустятся, переделаются только эти[/]"
    console.print(Panel(
        summary,
        title="⚠ Конвертация завершена с ошибками" if failed else "✅ Конвертация завершена",
        border_style="yellow" if failed else "green", box=box.DOUBLE
    ))

    if shutdown_after:
        # 60 секунд на отмену: shutdown /a
        subprocess.run(["shutdown", "/s", "/t", "60"])
        console.print(Panel(
            "Компьютер выключится через [bold]60 сек[/].\n"
            "[dim]Отменить: [cyan]shutdown /a[/][/]",
            title="⏻ Выключение", border_style="yellow", box=box.ROUNDED
        ))
    restart_script()


def main():
    clear_screen()
    
    # Проверяем и устанавливаем зависимости
    if not check_and_install_dependencies():
        return
    
    # Проверяем доступность rich после возможной установки.
    # Без rich работать нельзя: весь интерфейс построен на Panel/Table/Prompt,
    # и дальше был бы NameError. Честно выходим с подсказкой.
    if not check_rich_availability():
        print("\n❌ Библиотека 'rich' не установлена — интерфейс скрипта без неё не работает.")
        print("   Установите вручную:  pip install rich  — и запустите скрипт снова.")
        return
    
    current_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd()
    
    # Создаем шапку
    header = Panel(
        Text.from_markup(
            "[bold magenta]▌[/] [bold white]ANITOOLS[/]   [dim]by shiguchi[/]\n"
            "[bold magenta]▌[/] [dim]обработка · конвертация · переименование[/]",
            justify="left"
        ),
        box=box.DOUBLE_EDGE,
        border_style="magenta",
        padding=(0, 2)
    )
    console.print(header)
    
    # Информация о директории
    console.print(Panel(
        str(current_dir),
        title="📂 Рабочая директория",
        border_style="magenta",
        box=box.ROUNDED
    ))

    # Таблица меню
    menu = Table(title="Меню действий", box=box.ROUNDED, border_style="magenta", title_style="bold magenta")
    menu.add_column("  #", justify="center", style="bold magenta", no_wrap=True)
    menu.add_column("Действие", style="bold white")
    menu.add_column("Описание", style="dim")

    menu.add_row("1", "Оставить только видео", "удалить аудио из видео")
    menu.add_row("2", "Оставить только аудио", "извлечь аудиодорожку")
    menu.add_row("3", "Обработка аудио", "выбрать и добавить аудиодорожки")
    menu.add_row("4", "Извлечь субтитры", "сохранить субтитры в ASS")
    menu.add_row("5", "Переименовать файлы", "переименовать с номером серии")
    menu.add_row("6", "Конвертировать видео", "MKV/MP4/AVI/TS/… → MP4 или MKV")
    menu.add_row("7", "HLS мульти-разрешение", "6 качеств + раздельные аудиодорожки по id")
    console.print(menu)

    choice = Prompt.ask("🎯 [bold magenta]Введите номер действия", choices=["1","2","3","4","5","6","7"], default="1")

    if choice == "1":
        keep_video_only(str(current_dir), os.path.join(str(current_dir), "Video only"))
    elif choice == "2":
        keep_audio_only(str(current_dir), os.path.join(str(current_dir), "Audio only"))
    elif choice == "3":
        advanced_audio_processing(str(current_dir), os.path.join(str(current_dir), "Processed Audio"))
    elif choice == "4":
        extract_subtitles(str(current_dir), os.path.join(str(current_dir), "надписи"))
    elif choice == "5":
        rename_files_by_pattern(str(current_dir))
    elif choice == "6":
        convert_mkv_to_mp4(str(current_dir), os.path.join(str(current_dir), "converted_mp4"))
    elif choice == "7":
        convert_videos_multi_res(current_dir)
    else:
        console.print(Panel("Неверный выбор действия", 
                           title="❌ Ошибка", 
                           border_style="red",
                           box=box.DOUBLE))
        restart_script()

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        # Гарантированно останавливаем все запущенные внешние процессы при Ctrl+C
        try:
            terminate_all_processes()
        finally:
            pass
    except Exception as e:
        log_file = log_error(e)
        error_msg = "Произошла непредвиденная ошибка."
        if RICH_AVAILABLE:
            details = f"Лог сохранен в: {log_file}" if log_file else "Не удалось сохранить лог."
            console.print(Panel(f"{error_msg}\n{details}", title="❌ Ошибка", border_style="red", box=box.DOUBLE))
        else:
            print(error_msg)
            if log_file:
                print(f"Лог сохранен в: {log_file}")
    finally:
        # На всякий случай чистим процессы и при обычном завершении
        terminate_all_processes()