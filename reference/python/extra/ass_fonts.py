r"""
ass_fonts.py — собирает шрифты, используемые в .ass-субтитрах, в ZIP.

Где ищет (по порядку):
    1. папка с твоими шрифтами (CUSTOM_FONTS_DIR)
    2. системные шрифты: C:\Windows\Fonts и
       %LOCALAPPDATA%\Microsoft\Windows\Fonts (установленные «для себя»)
    3. скачивание: Google Fonts → dafont → 1001fonts
       (из архивов берутся только файлы, чьё внутреннее имя совпадает с искомым)

Шрифты сопоставляются по внутренним именам из таблицы name (семейство,
полное имя, PostScript-имя), а не по имени файла: «Times New Roman»
находится в times.ttf, а «Roboto» не тащит за собой RobotoMono.
Учитываются и стили, и теги \fn в строках Dialogue.

Дубли в архив не попадают: из копий и версий одного начертания (одинаковое
PostScript-имя: arial.ttf, Arial_0.ttf, «Arial (Regular).ttf») берётся один файл —
с кириллицей, самой новой версии, при равенстве самый полный. Одноимённые шрифты
без кириллицы (системный calligr0.ttf — тоже «Calligrapher») пропускаются, если
есть кириллический.

Использование:
    python ass_fonts.py                    все .ass в текущей папке
    python ass_fonts.py a.ass b.ass        конкретные файлы
    python ass_fonts.py "D:\anime\ep01"    все .ass в папке
    (можно просто перетащить файлы или папку на скрипт)

Настройки:
    CUSTOM_FONTS_DIR — папка, куда ты кладёшь шрифты вручную.
                       Задаётся ниже или через переменную окружения
                       CUSTOM_FONTS_DIR.
"""

import io
import os
import re
import struct
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path
from typing import BinaryIO


# ── Настройки ────────────────────────────────────────────────

# Папка с твоими шрифтами — измени путь под себя или задай через env
CUSTOM_FONTS_DIR = Path(os.environ.get("CUSTOM_FONTS_DIR", r"C:\personal\Apps\MPV by shiguchi\fonts"))

SYSTEM_FONTS_DIRS = [Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"]
if os.environ.get("LOCALAPPDATA"):
    # Шрифты, установленные «только для текущего пользователя»
    SYSTEM_FONTS_DIRS.append(Path(os.environ["LOCALAPPDATA"]) / "Microsoft" / "Windows" / "Fonts")

FONT_EXTS = (".ttf", ".otf", ".ttc", ".otc")

BROWSER_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36"

# Сколько архивов из выдачи dafont пробовать, пока не найдётся нужный шрифт
DAFONT_MAX_TRIES = 4


# ── Парсинг .ass ─────────────────────────────────────────────

FN_TAG = re.compile(r"\\fn([^\\}]*)")


def read_ass(path: Path) -> str:
    raw = path.read_bytes()
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        return raw.decode("utf-16", errors="replace")
    try:
        return raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        return raw.decode("cp1251", errors="replace")


def clean_font_name(name: str) -> str:
    # "@" в начале — вертикальный вариант того же шрифта
    return name.strip().lstrip("@").strip()


def parse_font_names(ass_path: Path) -> set[str]:
    """Имена шрифтов из стилей ([V4+ Styles]) и тегов \\fn в [Events]."""
    names = set()
    section = ""
    fontname_idx = 1  # стандартный Format: Name, Fontname, ...
    for line in read_ass(ass_path).splitlines():
        line = line.strip()
        if line.startswith("["):
            section = line.lower()
            continue
        key, sep, value = line.partition(":")
        if not sep:
            continue
        key = key.strip().lower()
        if section in ("[v4+ styles]", "[v4 styles]"):
            if key == "format":
                fields = [f.strip().lower() for f in value.split(",")]
                if "fontname" in fields:
                    fontname_idx = fields.index("fontname")
            elif key == "style":
                parts = value.split(",")
                if len(parts) > fontname_idx:
                    names.add(clean_font_name(parts[fontname_idx]))
        elif section == "[events]" and key == "dialogue":
            for m in FN_TAG.finditer(value):
                names.add(clean_font_name(m.group(1)))  # пустой \fn — сброс, отсеется ниже
    names.discard("")
    return names


# ── Чтение имён из файла шрифта ───────────────────────────────

# 1 — семейство, 4 — полное имя, 6 — PostScript, 16 — типографское семейство.
# ID 16 объединяет родственные начертания («Arial» для Arial Narrow и Arial Black),
# поэтому используется только если по 1/4/6 ничего не нашлось.
PRIMARY_NAME_IDS = {1, 4, 6}
FALLBACK_NAME_IDS = {16}
SFNT_MAGICS = (b"\x00\x01\x00\x00", b"OTTO", b"true")


def font_key(name: str) -> str:
    """Ключ для сравнения имён: libass сравнивает без учёта регистра."""
    return " ".join(name.split()).casefold()


def normalize(s: str) -> str:
    return re.sub(r"[\s\-_]", "", s).lower()


def _sfnt_names(fh: BinaryIO, offset: int, names: dict[int, set[str]]):
    fh.seek(offset)
    header = fh.read(12)
    if header[:4] not in SFNT_MAGICS:
        return
    num_tables = struct.unpack(">H", header[4:6])[0]
    directory = fh.read(16 * num_tables)
    for i in range(num_tables):
        tag, _, tbl_off, tbl_len = struct.unpack(">4sLLL", directory[16 * i:16 * i + 16])
        if tag == b"name":
            break
    else:
        return

    fh.seek(tbl_off)
    table = fh.read(tbl_len)
    _, count, str_off = struct.unpack(">HHH", table[:6])
    for i in range(count):
        platform, encoding, _, name_id, length, off = struct.unpack(">6H", table[6 + 12 * i:18 + 12 * i])
        if name_id not in names:
            continue
        raw = table[str_off + off:str_off + off + length]
        if platform in (0, 3):
            text = raw.decode("utf-16-be", errors="ignore")
        elif platform == 1 and encoding == 0:
            text = raw.decode("mac_roman", errors="ignore")
        else:
            continue
        if text.strip():
            names[name_id].add(font_key(text))


def _read_names(fh: BinaryIO, ids) -> dict[int, set[str]]:
    """Записи таблицы name с заданными ID по всем шрифтам файла (и коллекции .ttc)."""
    names = {i: set() for i in ids}
    try:
        fh.seek(0)
        if fh.read(4) == b"ttcf":
            fh.seek(8)
            num = struct.unpack(">L", fh.read(4))[0]
            offsets = struct.unpack(f">{num}L", fh.read(4 * num)) if num < 1000 else ()
        else:
            offsets = (0,)
        for off in offsets:
            _sfnt_names(fh, off, names)
    except (struct.error, OSError):
        pass
    return names


def read_font_names(fh: BinaryIO) -> tuple[set[str], set[str]]:
    """Имена, под которыми шрифт (или любой шрифт коллекции .ttc) можно указать
    в .ass: (основные, запасные)."""
    names = _read_names(fh, PRIMARY_NAME_IDS | FALLBACK_NAME_IDS)
    primary = set().union(*(names[i] for i in PRIMARY_NAME_IDS))
    fallback = set().union(*(names[i] for i in FALLBACK_NAME_IDS))
    return primary, fallback


def face_info(data: bytes) -> tuple[str, float]:
    """(начертание, версия): начертание — PostScript-имя (6), иначе полное имя (4).
    Файлы с одинаковым начертанием — копии/версии одного шрифта (arial.ttf, Arial_0.ttf,
    «Arial (Regular).ttf»), в архив нужен один."""
    names = _read_names(io.BytesIO(data), (4, 5, 6))
    key = "|".join(sorted(names[6] or names[4]))
    vers = [float(m.group(1)) for v in names[5] for m in [re.search(r"(\d+(?:\.\d+)?)", v)] if m]
    return key, max(vers, default=0.0)


def has_cyrillic(path: Path) -> bool:
    """Есть ли в шрифте кириллица (Ж). Без fontTools — считаем, что есть."""
    try:
        from fontTools.ttLib import TTFont, TTCollection
    except ImportError:
        return True
    try:
        fs = (TTCollection(str(path), lazy=True).fonts if path.suffix.lower() in (".ttc", ".otc")
              else [TTFont(str(path), lazy=True)])
        return any(0x0416 in (f.getBestCmap() or {}) for f in fs)
    except Exception:
        return True


class FontIndex:
    """Индекс «имя шрифта → файлы» по набору папок (рекурсивно)."""

    def __init__(self, dirs: list[Path]):
        self.by_name: dict[str, set[Path]] = {}
        self.by_family: dict[str, set[Path]] = {}
        self.by_stem: dict[str, set[Path]] = {}
        for d in dirs:
            if not d.is_dir():
                continue
            for f in d.rglob("*"):
                if f.suffix.lower() not in FONT_EXTS or not f.is_file():
                    continue
                try:
                    with f.open("rb") as fh:
                        primary, fallback = read_font_names(fh)
                except OSError:
                    primary, fallback = set(), set()
                for n in primary:
                    self.by_name.setdefault(n, set()).add(f)
                for n in fallback:
                    self.by_family.setdefault(n, set()).add(f)
                self.by_stem.setdefault(normalize(f.stem), set()).add(f)

    def find(self, font_name: str) -> list[Path]:
        key = font_key(font_name)
        # Имя файла — последний вариант, для шрифтов с нечитаемой таблицей name
        files = (self.by_name.get(key)
                 or self.by_family.get(key)
                 or self.by_stem.get(normalize(font_name), set()))
        return sorted(files)


# ── Скачивание ────────────────────────────────────────────────

def fetch(url: str, ua: str | None = None, timeout: int = 30) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": ua} if ua else {})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def safe_filename(s: str) -> str:
    return re.sub(r'[<>:"/\\|?*\s]+', "", s)


def slugify(name: str, sep: str) -> str:
    return re.sub(r"[^a-z0-9]+", sep, name.lower()).strip(sep)


def fonts_from_zip(data: bytes, font_name: str) -> dict[str, bytes]:
    """Шрифты из архива, чьё внутреннее имя совпадает с искомым."""
    key = font_key(font_name)
    try:
        zf = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        return {}
    result = {}
    with zf:
        for entry in zf.infolist():
            fname = Path(entry.filename).name
            if (entry.is_dir() or "__MACOSX" in entry.filename
                    or Path(fname).suffix.lower() not in FONT_EXTS):
                continue
            font_data = zf.read(entry)
            primary, fallback = read_font_names(io.BytesIO(font_data))
            if key in primary or key in fallback:
                result[fname] = font_data
    return result


GOOGLE_STYLES = ",".join(f"{w}{i}" for i in ("", "i") for w in range(100, 1000, 100))


def download_from_google_fonts(font_name: str) -> dict[str, bytes]:
    """Все начертания семейства. Пустой dict — такого семейства на Google Fonts нет."""
    url = ("https://fonts.googleapis.com/css?family="
           + urllib.parse.quote_plus(font_name) + ":" + GOOGLE_STYLES)
    try:
        # Без браузерного User-Agent Google отдаёт .ttf (с браузерным — woff2,
        # со старым IE — .eot)
        css = fetch(url).decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        if e.code == 400:  # семейство не найдено
            return {}
        raise

    result = {}
    seen = set()
    for block in re.findall(r"@font-face\s*\{([^}]*)\}", css):
        m = re.search(r"url\(\s*['\"]?([^)'\"]+)", block)
        if not m:
            continue
        font_url = m.group(1)
        ext = Path(urllib.parse.urlparse(font_url).path).suffix.lower()
        if ext not in (".ttf", ".otf") or font_url in seen:
            continue
        seen.add(font_url)
        weight = re.search(r"font-weight:\s*(\d+)", block)
        italic = re.search(r"font-style:\s*italic", block)
        fname = (f"{safe_filename(font_name)}-{weight.group(1) if weight else '400'}"
                 f"{'Italic' if italic else ''}{ext}")
        result[fname] = fetch(font_url)
    return result


def download_from_dafont(font_name: str) -> dict[str, bytes]:
    search = "https://www.dafont.com/search.php?q=" + urllib.parse.quote_plus(font_name)
    html = fetch(search, ua=BROWSER_UA).decode("utf-8", errors="replace")
    # Сначала угаданный слаг («Komika Axis» → komika_axis), потом выдача поиска
    slugs = [slugify(font_name, "_")] + re.findall(r"dl\.dafont\.com/dl/\?f=([a-z0-9_]+)", html)
    for slug in list(dict.fromkeys(slugs))[:DAFONT_MAX_TRIES]:
        # Для несуществующего слага dafont отдаёт пустой ответ — fonts_from_zip вернёт {}
        found = fonts_from_zip(fetch(f"https://dl.dafont.com/dl/?f={slug}", ua=BROWSER_UA), font_name)
        if found:
            return found
    return {}


def download_from_1001fonts(font_name: str) -> dict[str, bytes]:
    url = f"https://www.1001fonts.com/download/{slugify(font_name, '-')}.zip"
    try:
        data = fetch(url, ua=BROWSER_UA)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return {}
        raise
    return fonts_from_zip(data, font_name)


FONT_SOURCES = [
    ("Google Fonts", download_from_google_fonts),
    ("dafont", download_from_dafont),
    ("1001fonts", download_from_1001fonts),
]


# ── Main ──────────────────────────────────────────────────────

def collect_ass_files(args: list[str]) -> list[Path]:
    if not args:
        return sorted(Path.cwd().glob("*.ass"))
    files = []
    for a in args:
        p = Path(a)
        if p.is_dir():
            files.extend(sorted(p.glob("*.ass")))
        elif p.is_file() and p.suffix.lower() == ".ass":
            files.append(p)
        else:
            print(f"⚠  Пропускаю (не .ass и не папка): {a}")
    return list(dict.fromkeys(f.resolve() for f in files))


def ask(prompt: str) -> str:
    try:
        return input(prompt)
    except EOFError:
        return ""


def main():
    ass_files = collect_ass_files(sys.argv[1:])
    if not ass_files:
        where = "" if sys.argv[1:] else f" в папке:\n   {Path.cwd()}"
        print(f"❌ Не найдено .ass файлов{where}")
        ask("\nEnter для выхода...")
        return

    out_dir = ass_files[0].parent
    print(f"📂 Папка: {out_dir}")
    print(f"   Найдено .ass файлов: {len(ass_files)}")

    # ── Папка с ручными шрифтами ──────────────────────────────
    custom_dir = CUSTOM_FONTS_DIR
    print(f"\n📁 Папка с ручными шрифтами: {custom_dir}")
    custom_input = ask("   Изменить путь? (Enter — оставить текущий): ").strip().strip('"')
    if custom_input:
        custom_dir = Path(custom_input)

    if not custom_dir.exists():
        print(f"   ⚠  Папка не существует, создаю: {custom_dir}")
        custom_dir.mkdir(parents=True, exist_ok=True)
    else:
        print(f"   ✓ Используется: {custom_dir}")

    # ── Собираем уникальные имена шрифтов из всех файлов ──────
    unique: dict[str, str] = {}
    for ass_path in ass_files:
        try:
            for name in parse_font_names(ass_path):
                unique.setdefault(font_key(name), name)
        except OSError as e:
            print(f"   ⚠  Не читается {ass_path.name}: {e}")
    font_names = sorted(unique.values(), key=str.casefold)

    if not font_names:
        print("\nШрифты в файлах не найдены.")
        ask("\nEnter для выхода...")
        return

    src_label = ass_files[0].name if len(ass_files) == 1 else f"{len(ass_files)} файлов"
    print(f"\n🔍 Уникальных шрифтов из [{src_label}]: {len(font_names)}")
    for name in font_names:
        print(f"   • {name}")

    # {имя_файла_в_архиве: байты}
    fonts: dict[str, bytes] = {}
    packed: dict[str, str] = {}  # начертание -> файл в архиве

    def add(fname: str, data: bytes):
        face = face_info(data)[0] or f"file:{fname}"
        if face in packed:
            print(f"      ⏭  {fname}  (дубль {packed[face]}, уже в архиве)")
            return
        if fname in fonts:
            if fonts[fname] == data:
                print(f"      ⏭  {fname}  (уже в архиве)")
                return
            # Другой файл с тем же именем — не затираем
            stem, ext = os.path.splitext(fname)
            n = 2
            while f"{stem}_{n}{ext}" in fonts:
                n += 1
            fname = f"{stem}_{n}{ext}"
        fonts[fname] = data
        packed[face] = fname
        print(f"      ✓ {fname}")

    def save_to_custom(fname: str, data: bytes):
        """Кладёт скачанный шрифт в папку с ручными шрифтами, чтобы в следующий раз
        он нашёлся на шаге 1. Существующие файлы не затирает."""
        out = custom_dir / fname
        n = 2
        while out.exists():
            try:
                if out.read_bytes() == data:
                    return  # точно такой же файл уже лежит
            except OSError:
                pass
            out = custom_dir / f"{Path(fname).stem}_{n}{Path(fname).suffix}"
            n += 1
        try:
            out.write_bytes(data)
            print(f"      💾 {out.name} → в твою папку")
        except OSError as e:
            print(f"      ⚠  Не удалось сохранить {out.name} в твою папку: {e}")

    def take_local(index: FontIndex, names: list[str]) -> list[str]:
        missing = []
        for name in names:
            files = index.find(name)
            if not files:
                missing.append(name)
                print(f"   – {name}")
                continue
            print(f"   ✓ {name}")
            # Одноимённый шрифт без кириллицы (системный calligr0.ttf — тоже «Calligrapher»):
            # в архив только кириллические, иначе плеер может взять не тот
            cyr = [f for f in files if has_cyrillic(f)]
            if cyr and len(cyr) < len(files):
                for f in files:
                    if f not in cyr:
                        print(f"      ⏭  {f.name}  (то же имя, но без кириллицы)")
                files = cyr
            # Копии и версии одного начертания (arial.ttf, Arial_0.ttf, «Arial (Regular).ttf») —
            # в архив одна: с кириллицей, новее по версии, при равенстве — полнее (больше файл)
            best: dict[str, tuple] = {}
            for f in files:
                try:
                    data = f.read_bytes()
                except OSError as e:
                    print(f"      ⚠  {f.name}: ошибка чтения: {e}")
                    continue
                face, ver = face_info(data)
                rank = (has_cyrillic(f), ver, len(data))
                face = face or f"file:{f.name}"
                if face not in best or rank > best[face][0]:
                    if face in best:
                        print(f"      ⏭  {best[face][1].name}  (дубль, берётся {f.name})")
                    best[face] = (rank, f, data)
                else:
                    print(f"      ⏭  {f.name}  (дубль, берётся {best[face][1].name})")
            for _, f, data in best.values():
                add(f.name, data)
        return missing

    # ── Шаг 1: твоя папка (приоритет) ────────────────────────
    print(f"\n[1/3] Ищу в папке с ручными шрифтами ({custom_dir})...")
    missing = take_local(FontIndex([custom_dir]), font_names)

    # ── Шаг 2: системные шрифты ──────────────────────────────
    if missing:
        print(f"\n[2/3] Ищу в системе ({', '.join(map(str, SYSTEM_FONTS_DIRS))})...")
        missing = take_local(FontIndex(SYSTEM_FONTS_DIRS), missing)
    else:
        print("\n[2/3] Пропускаю — всё нашлось в твоей папке.")

    # ── Шаг 3: скачивание ────────────────────────────────────
    not_found: list[str] = []
    if missing:
        print(f"\n[3/3] Пробую скачать: {len(missing)} шрифт(ов)...")
        for name in missing:
            print(f"   ↓ {name}")
            for label, download in FONT_SOURCES:
                print(f"      {label}... ", end="", flush=True)
                try:
                    downloaded = download(name)
                except Exception as e:
                    print(f"⚠  ошибка: {e}")
                    continue
                if not downloaded:
                    print("нет")
                    continue
                print(f"{len(downloaded)} файл(ов)")
                for fname, data in downloaded.items():
                    add(fname, data)
                    save_to_custom(fname, data)
                break
            else:
                not_found.append(name)
    else:
        print("\n[3/3] Пропускаю — всё нашлось без автозагрузки.")

    # ── Пакуем в ZIP ──────────────────────────────────────────
    if not fonts:
        print("\nНечего паковать.")
    else:
        zip_path = out_dir / "fonts.zip"
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
            for fname, data in fonts.items():
                info = zipfile.ZipInfo(fname)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0x20  # Windows Archive bit — обычный rw файл
                zf.writestr(info, data)
        print(f"\n🗜  Архив создан: {zip_path}")
        print(f"   Файлов в архиве: {len(fonts)}")

    # ── Что не нашлось нигде ──────────────────────────────────
    if not_found:
        print(f"\n❌ Не найдено нигде ({len(not_found)}).")
        print(f"   Скачай вручную и положи в: {custom_dir}")
        for name in not_found:
            query = urllib.parse.quote_plus(name)
            print(f"\n   • {name}")
            print(f"     https://www.fontsquirrel.com/search?q={query}")
            print(f"     https://fontsgeek.com/search/?q={query}")

    ask("\nEnter для выхода...")


if __name__ == "__main__":
    main()
