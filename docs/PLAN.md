# anitools → C# + Avalonia: план переноса

> Документ самодостаточный: облачная сессия работает **только по нему** и по копии
> оригинала в `reference/python/`. Доступа к компу пользователя у неё нет.
>
> Дата разбора: 2026-10-07. Оригинал: `C:\personal\Scripts\anitools.py` (5225 строк,
> sha256 `8920902b…49d50a`), скопирован в `reference/python/anitools.py` без изменений.
> Соседние скрипты, которые тоже переносим (§2.9), — в `reference/python/extra/`.
> Ответы пользователя на вопросы — в §8.

## 0. Как работать по этому плану

- **Источник истины по поведению** — `reference/python/anitools.py`. Его не править.
  Ниже описано *что* он делает и какие решения приняты при переносе; точные регулярки,
  списки ключевых слов и т.п. брать из оригинала (номера строк указаны как `py:NNN`).
- Общение с пользователем — по-русски, коротко и по-простому.
- Никаких реальных видео пользователя. Все тесты — на крошечных файлах, которые
  генерирует сам тест через ffmpeg (см. §5.3).
- Тяжёлые многоагентные аудиты не нужны: мелкие правки проверять тестами.
- Двигаться по этапам из §7, каждый этап — отдельный коммит/PR с зелёными тестами.
- Вопросы, которые остались открытыми, — в §8. Если упёрся в них — делать вариант
  «как в оригинале» и пометить в PR.

---

## 1. Цель и рамки

Перенести anitools (консольное меню на Python + rich) на C# с полноценным GUI на Avalonia:

- **ядро** (`Anitools.Core`) — вся логика, без UI, покрыто тестами, работает и на Linux;
- **GUI** (`Anitools.App`) — Avalonia, MVVM; всё, что сейчас спрашивается через
  `Prompt.ask`, становится полями формы с теми же значениями по умолчанию;
- выходные файлы и папки — **байт-в-байт та же структура и имена**, что у Python-версии
  (на них завязаны дальнейшие шаги пользователя: заливка, плееры).

В рамках: 7 пунктов меню anitools + служебное (RAM-диск, выключение ПК, логи)
**и** соседние скрипты из `C:\personal\Scripts` (§2.9) — отдельными страницами того же приложения.

---

## 2. Что умеет anitools сейчас

### 2.1 Запуск и окружение

| Что | Как |
|---|---|
| `anitools.bat` | `cd /d "%~dp0"` → `python anitools.py "%CD%"` → `pause` |
| `ani.bat` | `python "%~dp0anitools.py" %*` — лежит в `C:\personal\Scripts` (он в PATH), запускается из папки с сериями командой `ani` |
| Рабочая папка | `argv[1]`, иначе текущая директория. Все пункты работают только с файлами **верхнего уровня** этой папки (кроме поиска внешнего аудио в п.3 — рекурсивно) |
| Python-зависимости | только `rich` (при отсутствии предлагает `pip install rich`) |
| Переменные окружения | `FFMPEG_PATH` (деф. `ffmpeg`), `FFPROBE_PATH` (деф. `ffprobe`) — чтобы обойти шим Chocolatey; `ANITOOLS_WORK_DIR` (временная папка HLS, деф. пусто); `FFMPEG_HARD_KILL_ALL=1` — при выходе `taskkill /F /IM ffmpeg.exe` |
| Окружение пользователя | Windows 11, ffmpeg 9.0.2 (gyan essentials) через Chocolatey: шимы в `C:\ProgramData\chocolatey\bin`, настоящие exe в `C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin\` (в общем виде `...\lib\ffmpeg\tools\*\bin`). MKVToolNix в `C:\Program Files\MKVToolNix` (в PATH). ImDisk в `C:\Windows\System32\imdisk.exe`. NVIDIA (NVENC/NVDEC/CUDA). 7z = NanaZip (anitools его не использует). Браузер Vivaldi, Edge удалён. .NET: рантаймы 6/8/10, **SDK не установлен** |

### 2.2 Внешние программы

| Программа | Где | Аргументы |
|---|---|---|
| `ffmpeg -i FILE` (без выхода) | п.2, п.3: список аудиодорожек; фолбэк длительности | парсятся строки stderr с `Audio:` и `Stream #0:N`, `Duration: HH:MM:SS.cc` |
| `ffprobe` | везде | `-v error -select_streams a -show_entries stream=index:stream_tags=title -of json` (тайтлы аудио); `... stream=index:stream_tags=title,language` (п.7); `-show_entries format=duration -of default=noprint_wrappers=1:nokey=1` (длительность); `-select_streams v:0 -show_entries stream=width` ; `stream=codec_name,width,height -of json` (NVDEC-лимит); `stream=channels -of csv=p=0` (каналы аудио); `-select_streams v:0 -show_entries packet=pts_time,size -of csv=p=0` (битрейт по окнам) |
| `ffmpeg` (работа) | п.1,2,3,6,7 | см. §2.3 и §2.5 |
| `mkvmerge` | п.4 | `--version` (проверка), `-J FILE` (основной), `-i FILE` (фолбэк) |
| `mkvinfo` | п.4 | `FILE` (второй фолбэк) |
| `mkvextract` | п.4 | `tracks IN ID:OUT`; код 1 при непустом файле = предупреждение, не ошибка |
| `imdisk` | п.7 | `-l` (проверка, коды 0/1 = есть); `-a -t vm -s {N}G -m {L}: -p "/fs:ntfs /q /y"` (создать, нужны права админа, таймаут 120 с); `-D -m {L}:` (снять) |
| `taskkill` | выход | `/T /F /PID pid` (дерево), опц. `/F /IM ffmpeg.exe` |
| `shutdown` | п.7 | `/s /t 60` (отмена — `shutdown /a`) |
| HTTP Shikimori | п.5, п.7 | `https://shikimori.io/api/animes?search=Q&limit=15&order=popularity`, `.../animes/{id}`; заголовки `User-Agent: anitools/1.0`, `Accept: application/json`; таймаут 8 с; 3 попытки; 404 → нет; 429 → пауза `1.5×попытка` с; прочие ошибки → пауза 1 с. **Отличие:** диалог выбора ищет через GraphQL (`POST /api/graphql`, `animes(search, limit: 50, order: popularity)` — постер, описание, жанры, студии, оценка, статус одним запросом), при сбое — тот же REST с `limit=50` |

Все процессы: вывод читается как UTF-8 с заменой битых символов.

### 2.3 Пункты меню

Общие правила для п.1–4, 6:
- расширения сравниваются без учёта регистра; берутся только файлы верхнего уровня;
- «уже готово» = выходной файл существует и размер > 0 → пропуск, сообщение «N будут пропущены»;
- `process_output_filename` = имя с заменой `_` → пробел (п.1–4, **не** п.6);
- после успешной записи с выходного файла/папки снимается атрибут «только чтение»;
- при ненулевом коде ffmpeg/mkvextract — лог (§2.6) и переход к следующему файлу.

#### П.1 «Оставить только видео» (`keep_video_only`, py:1116)
- Вход: `.mkv .mp4 .hevc .avi .h264 .m2ts .ogm .mpg .mov`.
- Выход: `<папка>\Video only\<имя с _→пробел>` (то же расширение).
- Команда: `ffmpeg -nostdin -y -i IN -map 0:v:0 -c:v copy -an OUT` (субтитры/вложения/главы не берутся за счёт `-map`).

#### П.2 «Оставить только аудио» (`keep_audio_only`, py:1326)
- Вход: те же + `.mka`. Дорожки показываются по **первому** файлу: описание (кодек, каналы) + тайтл
  (ffprobe; для `.mov .qt .mp4 .m4a .m4v` — фолбэк на имена из атомов MOV, §2.4.6).
- Ввод дорожек: `1,3-5` → 0-based индексы среди аудио (`_parse_track_ids`, py:1195: диапазоны,
  обратный диапазон переворачивается, дубликаты убираются, порядок сохраняется). Дефолт `1`.
  Индекс за пределами числа дорожек первого файла → ошибка.
- Если выбрано > 1 дорожки — режим: **1 = отдельный файл на дорожку** (деф.) / **2 = все в один .mka на серию**.
- Режим «отдельные» и > 1 дорожки: вопрос «добавлять порядковый номер» (деф. да).
- Имя дорожки для папок: тайтл → иначе кодек из `Audio: <кодек>,` → иначе номер; `sanitize_folder_name`
  (`<>:"/\|?*` и \x00–\x1F → `_`, strip пробелов и точек, пусто → `audio_track`); повтор имени → `имя_{idx+1}`.
- Выход:
  - одна дорожка: `Audio only\<base>.mka`;
  - несколько: `Audio only\{N. }<Имя>\{N. }<base>.<Имя>.mka`, где `N` = номер дорожки в исходнике (1-based), `<base>` = stem с `_`→пробел.
  - команда на каждую дорожку: `ffmpeg -nostdin -y -i IN -map 0:a:{t} -c:a copy OUT`; пропуск — по каждой дорожке отдельно.
- Режим «один .mka» (`_extract_audio_single_mka`, py:1215):
  - тайтлы (деф. нет): для каждой дорожки — номер из войс-листа (§2.7) или ручной ввод (деф. реальный тайтл или `Track N`);
  - язык (деф. да): язык по умолчанию `rus`, для каждой дорожки `_detect_lang(тайтл + реальный тайтл) or default`;
  - выход `Audio only\<base>.mka`;
  - команда: `ffmpeg -nostdin -y -i IN -map 0:a:t1 -map 0:a:t2 … -c:a copy -vn -sn -dn -map_chapters -1`
    затем для каждой выходной дорожки i: `-metadata:s:a:i title=…` (если задан), `-metadata:s:a:i language=…` (если задан),
    `-disposition:a:i default` для i=0, иначе `none`; затем `OUT`.

#### П.3 «Обработка аудио» (`advanced_audio_processing`, py:1783)
Пересобирает видео с выбранными дорожками исходника + внешними аудиофайлами.
- Вход: видео `.mkv .mp4 .hevc .avi .h264 .m2ts .ogm .mpg .mov`; выход `Processed Audio\<имя с _→пробел>` (тот же контейнер).
- Дорожки исходника — по первому необработанному файлу. Ввод как в п.2, деф. `0` = не брать ничего из исходника.
- «Добавить внешние аудиофайлы?» (деф. нет). Нет ни дорожек, ни внешних → ошибка.
- Поиск внешних (`_find_external_audio`, py:1730): рекурсивно по рабочей папке, кроме выходной папки и папок на `.`;
  расширения `.mka .wav .mp3 .ac3 .dts .flac .aac .m4a`; файл относится к серии, если его имя (или имя без
  префикса `^\d+\.\s*`) начинается с base видео и следующий символ — не буква/цифра. Сортировка папок и файлов —
  «натуральная»: сначала по числу из префикса `N. `, без префикса — в конец, затем по имени без регистра.
- Каждая аудиодорожка каждого внешнего файла — отдельный «слот» (у многодорожечного .mka их несколько).
  Слоты по первой серии: сначала внутренние (в порядке ввода), потом внешние.
- Порядок слотов можно переставить (перестановка всех номеров, напр. `2,1,3`).
- Тайтлы (деф. нет): войс-лист / ручной ввод (деф. исходный тайтл слота).
- Язык (деф. да): default `rus`; `_detect_lang(метка слота + свой тайтл + реальный тайтл)`. Для внешних слотов
  в каждой серии язык ещё раз определяется по тайтлу дорожки **этой** серии, если получилось.
- Команда на серию:
  `ffmpeg -nostdin -y -i VIDEO [-i EXT1 -i EXT2 …] -map 0:v:0?` → для каждого слота `-map 0:a:{t}` или
  `-map {ext_idx+1}:a:{stream}` → `-disposition:a:i default|none` → тайтлы (`-metadata:s:a:i title=` свой
  или реальный, заглушки `Track N` не пишутся) → языки → `-c:v copy -c:a copy OUT`.
  Субтитры в выход **не** попадают.

#### П.4 «Извлечь субтитры» (`extract_subtitles`, py:2841)
- Вход: видео (список как в п.1). Требуется MKVToolNix.
- Дорожки субтитров первого файла: `mkvmerge -J` → `tracks[]` с `type == "subtitles"` (или `codec_id` содержит `S_TEXT`);
  имя = `properties.track_name` → тег `name/title` → `Язык: xxx` → `Субтитры N`; `language`, `language_ietf`, `codec_id`.
  (Фолбэки `mkvmerge -i` и `mkvinfo` — легаси, см. §2.8.)
- `codec_id → расширение`: ASS/SSA → `.ass`; UTF8/ASCII/UTF-8 → `.srt`; PGS/HDMV → `.sup`; VOBSUB → `.sub`; иначе `.ass`.
- Режим выбора дорожки во всех сериях: **1 по ID** (деф.) / **2 по тайтлу** / **3 по языку**.
  - по тайтлу: точное совпадение (casefold, trim), если оно единственное; иначе единственное вхождение подстроки; иначе пропуск серии;
  - по языку: языки дорожки = {language, language_ietf} в нижнем регистре без `und`; у эталонной дорожки язык обязателен;
    в серии берутся дорожки с пересекающимся языком; если их несколько — та, у которой совпал тайтл (если единственная),
    иначе та, что стоит на той же позиции среди дорожек этого языка, что и эталон в первом файле.
- Тип: **1 надписи** (деф.) → папка `надписи`, суффикс `.надписи`; **2 сабы** → папка `сабы`, суффикс `.сабы`.
- Выход: `<папка типа>\<base с _→пробел><суффикс><ext>`. Команда: `mkvextract tracks IN {trackId}:OUT`.

#### П.5 «Переименовать файлы» (`rename_files_by_pattern`, py:3199)
- Берёт **все файлы** верхнего уровня, кроме `.bat`, сортировка по коду символов.
- Базовое название: **1 Shikimori** (деф.) — подсказка = тайтл из первого видео (`.mkv .mp4 .avi .mov .m2ts .ts .webm`, иначе первый файл),
  интерактивный выбор (§2.4.3), берётся оригинальное `name` (ромадзи) → `_filename_safe_title`; **2 вручную**.
  `_filename_safe_title`: `\s*:\s+` → ` - `; `<>:"/\|?*` и управляющие → пробел; схлопнуть пробелы; strip ` .`.
- «С какого номера начинается исходная нумерация» (деф. 1): `серия = номер − (N−1)`, должно быть > 0, формат минимум 2 цифры.
- Суффикс (деф. нет; ведущая точка убирается).
- Новое имя: `{base} - {EP}.{suffix}{ext}` или `{base} - {EP}{ext}` (`ext` — только последнее расширение).
- Номер серии: `extract_episode_number_smart` (§2.4.2). Файлы без номера — в таблицу ошибок.
- Подтверждение: **1 авто** / **2 вручную** (для каждого файла: Enter = предложенный номер, пусто без предложения = пропуск) / **3 отмена**.
  Если номер не найден ни у одного файла — сразу ручной режим (или выход).
- Переименование на месте; если целевое имя занято другим файлом — ошибка по этому файлу.

#### П.6 «Конвертировать видео» (ремукс, `convert_mkv_to_mp4`, py:3520)
- Вход: `.mkv .mp4 .avi .mov .ts .m2ts .webm .flv .wmv .vob .m4v`. Формат: **MP4** (деф.) / **MKV**; для MKV «копировать субтитры» (деф. да).
- Выход: `converted_mp4\<stem>.<mp4|mkv>` (папка так называется и для MKV; `_` не заменяется).
- Команда: `ffmpeg -nostdin -i IN -map 0:v:0 -map 0:a [-map 0:s?] -c copy -y OUT`.

#### П.7 «HLS мульти-разрешение» — см. §2.5.

### 2.4 Общие алгоритмы

#### 2.4.1 Встроенный anitomy (`anitomy_parse`, py:41–434)
Порт anitomy.js: токенизация по скобкам (`([{「【『（〔` / `)]}」】』）〕`), разделителям ` _.,|`, длинным тире,
дефису только рядом с пробелом; словари ключевых слов (тип, аудио, видео, разрешение, источник, сабы, расширения,
язык, префиксы серии/сезона); шаги: группы в скобках (год, CRC32, разрешение, одиночное число — запасной номер серии,
технические теги, release group — первая нетехническая группа в начале, < 30 символов) → токены вне скобок с признаком
«после ` - `» → S01E02 / S01 / `2nd Season` / `Season 2` / `Ep01` / `Ep 01` / `01v2` / `01-12` / `第01話` / год /
разрешение / `v2` / число после последнего ` - ` (0..1999) / ключевые слова → запасной номер: последнее свободное
число не перед OVA/Movie → число в скобках → название = свободные токены до первого распознанного → название серии.
Возвращает словарь строк. **Портировать 1:1 и проверить golden-тестами (§5.2).**

#### 2.4.2 Номер серии (`extract_episode_number_smart`, py:3169)
1. `[._\- ]\d{1,2}_(\d{3,})` (HunterHunter.11_001 → 001);
2. anitomy `episode_number`;
3. `extract_episode_number_advanced` (наборы регулярок: сезон×серия, японские `第N話/N話/第N回/N回`, английские, спешлы с римскими, общие, любое число; только > 0);
4. `extract_episode_number` (старый список).
Результат — строка с минимум 2 цифрами (`f"{n:02d}"`). Из имени предварительно вырезается `.надписи`.

#### 2.4.3 Shikimori (py:438–661)
- `_title_season`: сезон из названия (`Season N`, `Nnd Season`, `… N` в конце, римские II–VI), предварительно отрезаются `: …`, `pt/part N`, `OVA/ONA/Movie/Special(s)`; иначе 1.
- `_clean_title_for_search`: убрать `pt N`, `part N`, `Season(s) N`, `Nnd season`, римские в конце, 1–2 цифры в конце.
- `_search_shikimori_smart`: запрос как есть → без знаков `!?:;,.'"~()[]` → первые 3 слова; первый непустой результат.
- `_rank_shikimori`: очки = 4 (нормализованный запрос входит в `name` или `russian`) + 2 (сезон по `name` совпал) + 1 (kind подходит; для групп OVA/ONA/Special/Movie — свой kind, иначе `tv`); при равенстве — исходный порядок (популярность). Показываются первые 8. **Отличие:** показываются все найденные (до 50) — порядок тот же.
- Диалог: номер → выбор; `0` → ввод ID вручную; текст → новый поиск; пусто/нет результатов → можно ввести ID или новый запрос, Enter = пропустить.
- Типы на русском: tv→TV, ova→OVA, ona→ONA, special/tv_special→Спешл, movie→Фильм, music→Клип, pv→PV, cm→CM.
- Папка тайтла в п.7: `_sanitize_folder(f"{id} - {название группы}")` (название — из имён файлов, **не** с Shikimori); при пропуске — `_sanitize_folder(название)`.

#### 2.4.4 Санитизация имён (три разные функции — не путать)
| Функция | Что заменяет | На что | Доп. |
|---|---|---|---|
| `sanitize_folder_name` (п.2) | `<>:"/\|?*`, \x00–\x1F | `_` | strip пробелов и `.`; пусто → `audio_track` |
| `_sanitize_folder` (п.7) | `\/:*?"<>|` | `_` | strip пробелов |
| `_filename_safe_title` (п.5) | см. §2.3 П.5 | пробел | `: ` → ` - ` |

#### 2.4.5 Язык по тексту (`_detect_lang`, py:1754)
нижний регистр; содержит `ориг|orig|japan|jpn|яп` или слово `jp` → `jpn`; содержит `english|англ` или слово `en`/`eng` → `eng`; иначе нет.

#### 2.4.6 Имена дорожек из MOV/MP4 (py:1583–1701)
Свой парсер боксов: `moov → trak → (mdia/hdlr: тип 'soun', имя обработчика) + (udta: name | ©nam | titl)`.
Размер бокса 32-бит, `1` = 64-бит, `0` = до конца. Имя udta: сначала формат `[size:2][lang:2][text]`, иначе сырой текст.
hdlr-имя: Pascal-строка или C-строка; стандартные имена (`soundhandler`, `core media audio`, `ffmpeg`, `lavf`, …) игнорируются.
Нужен для экспортов DaVinci Resolve, где тайтлы дорожек ffprobe не видит.

### 2.5 П.7 HLS мульти-разрешение (py:3665–5126)

**Вход:** `.mp4 .mkv .avi .m2ts .mov` верхнего уровня, сортировка по имени.

**Группировка по тайтлам:** ключ = `_parse_anime_title` (anitomy `anime_title`; фолбэк — stem без скобок до ` - NN` или до числа в конце) + ` OVA/ONA/Special/Movie`, если anitomy нашёл тип (`ova/oav/oad→OVA, ona→ONA, sp/special/specials→Special, movie/movies→Movie`). Пусто → `Без названия`. Можно перегруппировать вручную (создавать группы: название + номера файлов; остаток — в отдельную группу или пропустить).

**На каждую группу:** конвертировать/пропустить → Shikimori (папка тайтла, §2.4.3) → назначение озвучек.

**Озвучки:** дорожки файла через ffprobe (`title` → иначе `language` → иначе `Track N`; `lang` → иначе `und`).
«Раскладка» файла = список пар (title.strip().lower(), lang.lower()). Озвучки назначаются **один раз на каждую раскладку**
(у части серий может быть лишняя дорожка). Для каждой дорожки: номер из войс-листа / `0` или текст → ручной ввод
(деф. санитизированный тайтл) / `-` → не брать. Имя папки озвучки = `_sanitize_folder(имя)`, уникализация `_2`, `_3`…
Файл без аудио — пропуск. Раскладка без выбранных дорожек — все её файлы пропускаются.

**Папка серии:** `_sanitize_folder(stem)`; если уже занята (без регистра) — `_sanitize_folder(stem + "." + ext)`.

**План → подтверждение → «выключить ПК по завершении?» (деф. нет) → выбор временной папки:**
1. RAM-диск ImDisk (деф.): размер 10 / **14** (деф.) / 20 / 32 ГБ / свой (мин. 2); буква — первая свободная из `RZYXWVUTSQPONMLKJIHGFED`; ждать появления диска до 5 с; рабочая папка `L:\anitools_tmp`. Буква пишется в `%TEMP%\anitools_ramdisk.json` (JSON-массив букв); при следующем запуске п.7 оставшиеся диски снимаются. Снятие — в finally, atexit, по SIGTERM/SIGBREAK и при закрытии окна консоли.
2. Своя папка (деф. `D:\anitools_tmp`).
3. «Как раньше» — рядом с выходом.
Нет ImDisk / не удалось создать (обычно нет прав админа) → предложить папку или «как раньше».
**Отличие (C#):** права администратора не нужны заранее — диск создаёт помощник с правами (тот же exe с ключом
`--imdisk-helper`, запрос Windows один раз за запуск приложения), см. «Решения — пакет 2» в §7.

**Структура выхода:**
```
<рабочая папка>\hls_multi\
  <id> - <тайтл>\
    <серия>.zip            ← 360p/…/4K: seg000.ts… + master.m3u8 в каждой подпапке, ZIP_STORED
    <серия>.4K.zip         ← только если папка 4K > 7 ГиБ (7·1024³ байт); тогда в основном zip её нет
    audio\<озвучка>\<серия>.<озвучка>.mka
```
Корневой `master.m3u8` **не создаётся** (функция `_write_master_playlist` есть, но не вызывается — сохранить поведение).
Внутри zip пути вида `360p/seg000.ts`, `360p/master.m3u8`.

**Обработка серии (`_process_episode_multi_res`):**
0. Resume: если `<серия>.zip` есть и > 0 и все ожидаемые `.mka` есть и > 0 — пропуск («skip»).
1. `work_out` = `<временная папка>\<_sanitize_folder(папка тайтла)>\<серия>` или, при «как раньше», `hls_multi\<тайтл>\<серия>`.
2. Длительность (ffprobe → ffmpeg `Duration:` → 3600). Ширина источника (ffprobe) — только для сообщения: шире 3840 → «даунскейл до 4K на GPU».
3. Нужно ли CPU-декодирование: лимиты NVDEC по `max(w,h)`: h264 4096, mpeg2video/mpeg1video 4080, mpeg4 2048, vc1 2048, hevc/av1/vp9 8192, vp8 4096; неизвестный кодек — считаем, что GPU справится.
4. Качество: **сейчас постоянный CQ** (`_HLS_FIXED_CQ = 21.0`): на каждое разрешение `cq=21`, `maxrate = 4 × битрейт из лестницы`. Если `_HLS_FIXED_CQ = None` — подбор CQ (ниже). Приоритет пользователя: **качество > размер > скорость**.
5. Видео — одна команда ffmpeg на все 6 разрешений (ниже). Ошибка при GPU-декодировании → удалить `work_out`, повторить с CPU-декодированием. Ошибка снова → fail.
6. Вывести фактический Мбит/с по разрешениям (сумма `*.ts` × 8 / длительность).
7. Аудио — одна команда на все озвучки: `ffmpeg -nostdin -y -i IN -vn` + на каждую озвучку `-map 0:a:{idx} <кодек> <work_out>/audio/<озвучка>/<серия>.<озвучка>.mka`, где кодек = `-c:a aac -b:a 192k -ac 2`, если у дорожки 6 или 8 каналов, иначе `-c:a copy`.
8. Архив: основной zip (все качества, кроме верхнего, если оно идёт отдельно), каждая папка качества удаляется сразу после добавления; затем отдельный `.4K.zip` при необходимости. Ошибка → удалить недописанные zip и `work_out`, fail.
9. Перенос `.mka` в `<тайтл>\audio\<озвучка>\` (перезапись), удалить `work_out` и пустую папку серии.

Серии обрабатываются **строго по одной** (GPU загружен одной командой). В конце — сводка: начало/конец/время/готово/пропущено/ошибки. Затем `shutdown /s /t 60`, если просили.

**Лестница качеств** (`_HLS_RESOLUTIONS`): всегда все 6. Пользователь делает HLS только из 4K-исходников,
так что апскейла на практике нет — оставить как есть (§8):

| папка | ширина | высота (для BANDWIDTH/RESOLUTION) | битрейт, бит/с |
|---|---|---|---|
| 360p | 640 | 360 | 800 000 |
| 480p | 854 | 480 | 1 500 000 |
| 720p | 1280 | 720 | 3 000 000 |
| 1080p | 1920 | 1080 | 5 000 000 |
| 2K | 2560 | 1440 | 8 000 000 |
| 4K | 3840 | 2160 | 16 000 000 |

**Команда видео** (GPU-декодирование):
```
ffmpeg -nostdin -y -hwaccel cuda -hwaccel_output_format cuda -i IN
  -filter_complex "[0:v]split=6[v0][v1][v2][v3][v4][v5];[v0]scale_cuda=640:-2:format=nv12[v0out];…;[v5]scale_cuda=3840:-2:format=nv12[v5out]"
  # для каждого i:
  -map [v{i}out]
  -c:v h264_nvenc -preset p4 -tune hq -rc vbr -spatial-aq 1 -rc-lookahead 32 -forced-idr 1
  -force_key_frames expr:gte(t,n_forced*6)
  -cq 21.00 -b:v 0 -maxrate {M} -bufsize {2M}          # с rate control
  # (без rate control: -b:v {vbr} -maxrate {2vbr} -bufsize {4vbr})
  -f hls -hls_time 6 -hls_playlist_type vod -hls_flags independent_segments
  -hls_segment_filename {work}/{res}/seg%03d.ts {work}/{res}/master.m3u8
```
CPU-декодирование: вместо `-hwaccel …` — `-init_hw_device cuda=cu -filter_hw_device cu -i IN`, фильтр начинается с `[0:v]format=nv12,hwupload_cuda,split=6…`.
Пути сегментов — с прямыми слешами (`as_posix`). CQ форматируется `%.2f`.

**Подбор CQ** (при `_HLS_FIXED_CQ = None`; константы: окна 10, допуск 0.03, проходов ≤ 4, старт CQ 26, диапазон 14–40):
- битрейт исходника по окнам 6 с: ffprobe пакетов `pts_time,size`, окна от минимального pts, последнее (неполное) окно отбрасывается; нужно ≥ 3 окон и не все нули; `avg`, `p99 = sorted[min(n−1, int(0.99n))]`;
- `peak = clamp(p99/avg, 2, 3)`, `maxrate = int(target × peak)`;
- окна калибровки: индексы окон, отсортированные по битрейту, делятся на `count` страт, из каждой середина `order[int((k+0.5)/count·n)]`, уникальные, по возрастанию, × 6 с;
- проход: для каждого окна команда `ffmpeg -hide_banner -loglevel error -nostdin -y … -ss {start:.3f} -t 6.000 -i IN …` с выходами `-f mpegts _calibration/{res}.ts` только по «активным» разрешениям; битрейт = сумма размеров × 8 / (6 × число окон);
- следующий CQ: наклон по умолчанию `−ln2/6`; если есть 2 замера с разными CQ — наклон по ним, ограничен `[−0.25, −0.04]`; `cq = clamp(cq1 + (ln target − ln rate1)/наклон, 14, 40)`; разрешение выходит из активных, если ошибка ≤ 3 %, битрейт 0 или CQ не сдвинулся (> 0.05);
- упал ffmpeg на GPU → весь подбор повторить с CPU-декодированием; совсем не вышло → кодировать по среднему битрейту.

### 2.6 Служебное
- **Логи ошибок** (в текущую директорию процесса): `<stem>_ffmpeg_error_<YYYY-MM-DD_HH-MM-SS>.log` — время, команда, код, хвост вывода (последние 400 строк stderr для долгих процессов); общий `error_log_<ts>.txt` с трейсбеком при падении.
- **Прогресс ffmpeg** (`run_ffmpeg`): парсинг `speed=`, `time=`, `bitrate=` из stderr; процент = time/длительность.
- **Завершение**: реестр активных процессов; при выходе CTRL_BREAK → terminate → kill → `taskkill /T /F`; Ctrl+C даёт отработать finally (снятие RAM-диска).
- **Read-only**: после записи снимается атрибут «только чтение» с выходных файлов и папок.

### 2.7 Зашитые пути и константы

| Что | Значение |
|---|---|
| Выходные папки | `Video only`, `Audio only`, `Processed Audio`, `надписи` / `сабы`, `converted_mp4`, `hls_multi` |
| Временная папка HLS по умолчанию | `D:\anitools_tmp`; на RAM-диске `L:\anitools_tmp` |
| Состояние RAM-диска | `%TEMP%\anitools_ramdisk.json` |
| Shikimori | `https://shikimori.io/api/` |
| Порог отдельного 4K-архива | 7 ГиБ |
| HLS | сегмент 6 с, NVENC preset p4, CQ 21, потолок 4×, лестница из §2.5 |
| Войс-лист (`_VOICE_OPTIONS`, py:3670) | AniLiberty (AniLibria); ТО Дубляжная; Studio Band; Оригинальная; AniLibria.TV; DEEP; AniStar x DEEP; AniLibria.TV x DEEP; ТО Дубляжная x DEEP; SHIZA Project; AniDUB; OnWave; Reanimedia; Dream Cast; JAM; AniPlague; Animedia; Shachiburi; Ancord; KANSAI Studio |

### 2.8 Мёртвый код, странности и решения при переносе

| # | Что в оригинале | Решение |
|---|---|---|
| 1 | `_write_master_playlist`, `_get_audio_tracks_with_titles`, `get_track_id` нигде не вызываются | не переносить; корневой master.m3u8 не делать |
| 2 | Список аудиодорожек берётся парсингом stderr `ffmpeg -i` + отдельный ffprobe | заменить одним `ffprobe -show_streams -of json` (порядок `a:N` тот же) |
| 3 | П.4: фолбэки `mkvmerge -i` и `mkvinfo` | не переносить, только `mkvmerge -J` |
| 4 | П.4: mkvextract не работает с не-Matroska (mp4 с mov_text) | для не-MKV извлекать через `ffmpeg -map 0:s:N` (mov_text → `-c:s srt`), иначе `-c:s copy` |
| 5 | П.4: ветка ffmpeg использует `subtitle_id` вместо найденного в серии ID (мёртвая ветка) | п.4 решение выше закрывает |
| 6 | П.3: внешние файлы сопоставляются по **индексу** в списке; если у серии нет одного файла — дорожки съезжают молча | слот внешнего аудио идентифицировать по (относительная папка, хвост имени после base). Серии группируются по **набору слотов** (какие дорожки исходника и какие внешние файлы/дорожки у неё есть) — как «раскладки» в HLS (§2.5). Для каждого набора отдельно настраиваются порядок, тайтлы (войс-лист), язык и default-дорожка; набор, совпадающий с первой серией, — основной, остальные показываются отдельными блоками «у N серий нет …» с предзаполнением из основного (общие слоты — те же тайтл/язык, порядок — как в основном, недостающие выкинуты). Ничего не съезжает и не пропускается молча (подтверждено пользователем) |
| 7 | П.6: `-map 0:a` падает на файлах без звука | `-map 0:a?` |
| 8 | Логи пишутся в текущую директорию процесса | `%LOCALAPPDATA%\anitools\logs\` + кнопка «Открыть лог» у задачи (подтверждено пользователем) |
| 9 | Прогресс по stderr | `-progress pipe:1 -nostats` (out_time_us, speed, bitrate) + хвост stderr для логов |
| 10 | Шим Chocolatey: убийство шима не убивает ffmpeg | искать настоящий exe (§3.5) + Windows Job Object `KILL_ON_JOB_CLOSE` для всех дочерних процессов |
| 11 | Порядок файлов: `sorted(Path)` на Windows без учёта регистра | везде `OrdinalIgnoreCase` для списков файлов; для п.5 — как в оригинале (по коду символа) |
| 12 | Скрипт после любого пункта завершается | GUI остаётся открытым, задачи в очереди |

### 2.9 Соседние скрипты (anitools их не вызывает, но переносим тоже)
Оригиналы — в `reference/python/extra/`. Запускались через bat-обёртки (`ass.bat`/`sub.bat` → ass_fonts,
`font.bat` → extract_fonts, `mka.bat` → mka_muxer, `style.bat` → edit_styles), остальные — `python x.py` в папке.
Не переносим: `subs.py` (старый хардсаб+извлечение, заменён `hardsub.py` и п.4), `lordhls.py` (старая версия anitools),
`qBittorrent\*.py` (плагины поиска), `kodikdl.bat`, `lic.bat`, `res.bat`, `anivox-sheets-sync.gs`.
**Никогда не трогать и не копировать** `my_account.session` (сессия Telegram).

Общее для новых инструментов: работа с файлами верхнего уровня рабочей папки, «уже есть → пропуск», при ошибке
недописанный выход удаляется. Там, где оригинал гонял файлы параллельно (ProcessPool), — параллельность настраивается
(деф. как в оригинале), но это одна задача в очереди.

#### 2.9.1 Сборка озвучек в .mka (`mka_muxer.py`, режим 1)
- Вход: аудио `.mka .mp3 .ac3 .dts .flac .aac .m4a .opus .wav .eac3 .thd` **рекурсивно** (структура п.2 `N. Имя\N. Серия.Имя.mka`),
  кроме папки `MKA` и папок на `.`; натуральная сортировка папок и файлов по префиксу `N. `.
- Режим **1 по номеру серии** (деф.): номер = из имени без префикса `N. ` (в оригинале — пакет `anitopy`, при отсутствии
  регулярки; **в C# — наш Anitomy (§2.4.1)**, фолбэк — регулярки `parse_episode`), `zfill(2)`; без номера → группа `no_ep`.
  Имя выхода `episode_base`: `"{title} - {EP}"` или title, иначе stem без последнего `[..]`/`(..)`; санитизация
  `<>:"/\|?*`+управляющие → `_`, strip, rstrip `.`, пусто → `output`; совпало имя у разных серий → `"{имя} [{ключ}]"`.
  Режим **2 всё в один** (фильм/OVA): имя спрашивается (деф. `episode_base` первого файла).
- Выход: `MKA\<имя>.mka`; есть и > 0 → пропуск.
- «Метка озвучки» файла (одна на все серии): верхняя подпапка относительно корня без `N. ` → title первой аудиодорожки
  (ffprobe) → последний тег в скобках в имени → остаток имени после base серии; затем срезается хвостовой номер, если он
  равен номеру серии файла (`AniFilm 01` → `AniFilm`, `Studio 2x2` не трогается).
- Порядок меток по умолчанию — по числу `N. ` у папки/файла, без номера — в конец; можно переставить. Тайтлы (деф. нет;
  деф. значение = метка; войс-лист), язык (деф. да, `rus`; `detect_lang(метка + тайтл)`; у каждого файла язык ещё раз
  уточняется по его собственному title).
- Команда: `ffmpeg -nostdin -y -i f1 -i f2 …` → для каждого входа и каждого его аудиопотока `-map {i}:a:{k}?` →
  `-c:a copy -vn -map_chapters -1` → `-metadata:s:a:N title=…`, `language=…` → `-disposition:a:N default` (первая) / `none` → OUT.
  Файлы внутри серии упорядочены по рангу метки, затем натурально.

#### 2.9.2 Дорожки готового файла (`mka_muxer.py`, режим 2)
Файлы `.mka .mkv .mp4 .mov .m4a .webm` в папке (один или все). Таблица: № | тайтл | язык | кодек | каналы (`channel_layout`
или `N ch`) | default ★ (ffprobe `stream=index,codec_name,channels,channel_layout,disposition:stream_tags=title,language`).
Блок «для копирования» в Telegram: `• тайтл` / `1. тайтл` / `тайтл`; без тайтла — `Дорожка N`. В GUI — кнопка «Копировать».

#### 2.9.3 Шрифты для .ass → fonts.zip (`ass_fonts.py`)
- Вход: все `.ass` папки (или выбранные файлы). Выход: `fonts.zip` (Deflate, у записей `external_attr = 0x20`) рядом с первым .ass.
- Имена шрифтов: в `[V4+ Styles]`/`[V4 Styles]` — поле `Fontname` (индекс из строки `Format:`, деф. 1), в `[Events]` — теги
  `\fn…` в `Dialogue`; убрать ведущий `@`; ключ сравнения = схлопнутые пробелы + casefold. Чтение .ass: BOM UTF-16 → utf-16,
  иначе utf-8-sig, при ошибке cp1251.
- Имена из файла шрифта (без внешних библиотек): таблица `name` (sfnt `00010000`/`OTTO`/`true`, коллекции `ttcf`);
  основные ID 1, 4, 6; запасной ID 16 (только если по основным не нашлось); платформы 0/3 → UTF-16BE, 1/0 → MacRoman.
  Индекс папки (рекурсивно, `.ttf .otf .ttc .otc`): по основным именам → по запасным → по нормализованному имени файла
  (без пробелов, `-`, `_`, в нижнем регистре).
- Порядок поиска: **1)** своя папка `C:\personal\Apps\MPV by shiguchi\fonts` (env `CUSTOM_FONTS_DIR`, создаётся при отсутствии)
  → **2)** `%WINDIR%\Fonts` и `%LOCALAPPDATA%\Microsoft\Windows\Fonts` → **3)** скачивание:
  - Google Fonts: `https://fonts.googleapis.com/css?family=<имя>:100,…,900,100i,…,900i` **без** браузерного User-Agent
    (тогда отдаются .ttf); 400 → нет такого; файлы `{имя без спецсимволов}-{вес}[Italic].ttf`;
  - dafont: поиск `https://www.dafont.com/search.php?q=…` (браузерный UA), слаги = угаданный (`[^a-z0-9]+`→`_`) + из ссылок
    `dl.dafont.com/dl/?f=…`, до 4 попыток; из zip берутся только шрифты, чьё внутреннее имя совпало;
  - 1001fonts: `https://www.1001fonts.com/download/<слаг-через-дефис>.zip`, 404 → нет.
  Скачанное сохраняется и в свою папку (не затирать; одинаковый файл — пропуск; иначе `_2`, `_3`).
- Отбор: среди одноимённых — только с кириллицей (символ `Ж` U+0416 в `cmap`; в C# свой мини-парсер cmap форматов 4 и 12),
  если такие есть; дубли одного начертания (ключ = PostScript-имя ID 6, иначе полное ID 4) — один файл с рангом
  (кириллица, версия из ID 5, размер); одинаковое имя файла с другим содержимым → `_2`.
- Не нашлось нигде → список со ссылками `fontsquirrel.com/search?q=…` и `fontsgeek.com/search/?q=…`.

#### 2.9.4 Шрифты из видео → fonts.zip (`extract_fonts.py`)
Видео `.mkv .mp4 .webm .avi .mov .matroska`; `mkvmerge -J` → `attachments[]`; шрифт, если `content_type` содержит `font`
или расширение `.ttf .otf .woff .woff2 .eot`; `mkvextract FILE attachments {id}:{tmp}`; дубли по `file_name` пропускаются;
`fonts.zip` (Deflate) в папке.

#### 2.9.5 Чистка .ass по стилю/актёру (`edit_styles.py`)
Все `.ass` папки; поле `Dialogue` по наивному split по запятой: 3 = стиль, 4 = актёр (Name). Список уникальных значений
(+ пример файла, пустое = «(пусто)»). Режим: **оставить выбранные** / **удалить выбранные**. Файлы перезаписываются
**на месте** (utf-8-sig). При переносе: перед перезаписью копия оригиналов в `ass_backup_<YYYY-MM-DD_HH-MM-SS>\`
(правило пользователя «бэкап перед правкой»); сохранять исходные переводы строк.

#### 2.9.6 Сдвиг субтитров (`subtitle_delay+1s.py`)
`.srt .ass .ssa` → `subs_fixed\<то же имя>` (UTF-8 без BOM). Сдвиг, сек (деф. +1.0; в GUI можно и минус).
SRT: `(\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2},\d{3})`, мс += `int(shift×1000)`, не меньше 0.
ASS: только строки `Dialogue: (\d+,)(\d:\d{2}:\d{2}\.\d{2}),(\d:\d{2}:\d{2}\.\d{2}),(.*)`, сотые += `int(shift×100)`, не меньше 0.

#### 2.9.7 Сдвиг аудио (`delay+1s.py`, `delay-1s.py`)
`.mka .m4a .aac .mp3 .ac3 .dts .flac .wav .ogg .opus` → `audio_fixed\<stem>.mka`, AAC 256k, по 6 параллельно.
- `+N` сек (тишина в начало): `ffmpeg -hide_banner -nostdin -f lavfi -i aevalsrc=0:c=stereo:s=48000:d=N -i SRC -filter_complex "[0:a][1:a]concat=n=2:v=0:a=1[aout]" -map [aout] -c:a aac -b:a 256k -vn DST`
  (оригинал; в C# можно заменить на `-af adelay=N*1000:all=1` — не ломает 5.1 и 44.1 кГц; проверить тестом, что длительность +N);
- `−N` сек (обрезать начало): `ffmpeg -hide_banner -nostdin -i SRC -ss N -c:a aac -b:a 256k -vn DST`.

#### 2.9.8 Перекодирование аудио (`audio_decod.py`)
Много форматов (см. `EXTS` в оригинале) → `converted\<stem><ext>`, по 8 параллельно. Форматы: mka/m4a/aac (AAC LC),
mp3 (libmp3lame), opus, ogg (vorbis) — битрейт деф. 256k; flac, wav (pcm_s16le) — без потерь. Деф. mka.
Команда: `ffmpeg -hide_banner -nostdin -i SRC -map 0:a <кодек> [-af aresample=async=1] [-ac 2] -map_metadata 0
[-map 0:v? -c:v copy -disposition:v attached_pic — кроме mka] DST`. Каналы деф. 2, «чинить таймстампы» деф. да.

#### 2.9.9 Хардсаб (`hardsub.py`)
Пары «видео `.mkv .mp4 .avi .m2ts .ts` + `.ass` с тем же stem» → `Hardsub\<имя видео>` (существующий перезаписывается,
с предупреждением). Пишется в `Hardsub\<stem>.part<ext>`, потом переименовывается; при ошибке/отмене `.part` удаляется.
Шрифты: папка `Fonts` в рабочей папке, если есть (`:fontsdir=`). Команда:
`ffmpeg -hide_banner -y -v error -i V -map 0:v:0 -map 0:a? -vf subtitles=<ass>[:fontsdir=<Fonts>] -c:v hevc_nvenc -tune hq -multipass fullres -rc vbr -cq 17 -qmin 1 -qmax 51 -bufsize 80M -tier high -pix_fmt yuv420p10le -c:a copy -progress pipe:1 -nostats OUT`.
Экранирование пути для фильтра: `\`→`/`, затем `\ : ' [ ] , ;` экранируются `\`. В C# проще запускать ffmpeg с
`WorkingDirectory` = рабочая папка и относительными путями (`subtitles=имя.ass`) — без проблем с `C:`. Задача GPU — в общую очередь.

#### 2.9.10 Ремукс-пресеты из bat (`avi_to_mkv.bat`, `m2ts.bat`) — добавить в экран «Ремукс» как профили
- **AVI → MKV**: `ffmpeg -fflags +genpts -i X.avi -c copy -avoid_negative_ts make_zero X.mkv -y -loglevel warning`, выход рядом с исходником.
  *Решение после этапа 7:* отдельного профиля нет — AVI идёт обычным ремуксом (MP4 или MKV, папка `converted_mp4`),
  а к .avi добавляются те же `-fflags +genpts` и `-avoid_negative_ts make_zero`.
- **Blu-ray M2TS → MKV**: `ffmpeg -i X.m2ts -map 0:v -map 0:a -map 0:s? -c:v copy -c:a flac -c:s copy X.mkv -y` (PCM → FLAC),
  выход рядом; уже есть → пропуск; ошибка или пустой файл → удалить; показать размер до/после в МБ.

---

## 3. Архитектура C#

### 3.1 Стек
- **.NET 10** (LTS), C# последней версии, `Nullable=enable`, `TreatWarningsAsErrors=true`.
- **Avalonia** — последняя стабильная (11.3.x или 12.x, проверить на nuget.org в момент старта), тема Fluent, тёмная по умолчанию.
- **CommunityToolkit.Mvvm** — MVVM (ObservableProperty, RelayCommand).
- Иконки — пакет иконок для Avalonia (напр. `Material.Icons.Avalonia`) вместо эмодзи (эмодзи не рендерятся в headless Linux).
- Шрифт — встроенный Inter (`Avalonia.Fonts.Inter`), чтобы кириллица одинаково выглядела на скриншотах и на Windows.
- JSON — `System.Text.Json`; HTTP — `HttpClient`; zip — `System.IO.Compression` (`CompressionLevel.NoCompression`, тест проверяет, что записи именно Stored).
- Тесты — xUnit (+ `Avalonia.Headless.XUnit` для GUI). Версии пакетов — централизованно в `Directory.Packages.props`.
- Без лишних зависимостей: свой `ProcessRunner` (через `ProcessStartInfo.ArgumentList`, без ручного экранирования).

### 3.2 Структура репозитория
```
anitools/
  Anitools.slnx
  Directory.Build.props          # net10.0, nullable, warnings as errors
  Directory.Packages.props
  global.json                    # SDK 10.0.x, rollForward latestFeature
  src/
    Anitools.Core/
      Parsing/      Anitomy.cs, EpisodeNumber.cs, TitleText.cs (сезон, очистка, санитизация x3),
                    TrackIdList.cs, LanguageGuess.cs, NaturalSort.cs
      Media/        MediaProbe.cs (ffprobe/mkvmerge → модели), MovAtomReader.cs,
                    Models: MediaInfo, AudioStream, SubtitleTrack
      Processes/    IProcessRunner, ProcessRunner, ProcessResult, FfmpegProgressParser,
                    ToolLocator (ffmpeg/ffprobe/mkvmerge/mkvextract/imdisk), JobObject (Windows)
      Shikimori/    ShikimoriClient (ретраи, 429), ShikimoriRanker, ShikimoriAnime
      Operations/
        Common/     IOperation<TOptions>, OperationPlan, PlannedItem, ProgressEvent, OutputNames
        VideoOnly/  Options, Planner, Executor
        AudioExtract/
        AudioMux/   (п.3)
        Subtitles/
        Rename/
        Remux/      (+ профиль M2TS→MKV; AVI — обычным ремуксом с флагами меток времени)
        MkaMux/     (§2.9.1), TrackList/ (§2.9.2)
        Fonts/      AssFontParser, SfntNameReader, CmapReader, FontIndex, FontDownloaders
                    (GoogleFonts, Dafont, Fonts1001), AssFontsCollector, MkvFontExtractor
        AssEdit/    (чистка по стилю/актёру, с бэкапом), SubShift/ (srt/ass)
        AudioShift/, AudioConvert/, Hardsub/
        Hls/        HlsSettings, HlsLadder, HlsCommandBuilder, EncoderProfile (Nvenc | Software),
                    BitrateAnalyzer, CqCalibrator, HlsPackager (zip), HlsEpisodeProcessor, TitleGrouper,
                    AudioLayout, VoiceAssignment
      WorkDir/      IWorkDirProvider, FolderWorkDir, ImDiskRamDisk (Windows), RamDiskStateFile
      System/       IShutdownService (Windows: shutdown /s /t 60), ReadOnlyAttr
      Settings/     AppSettings, SettingsStore (%APPDATA%\anitools\settings.json; Linux ~/.config/anitools)
      Logging/      JobLog, ErrorLogWriter
      Jobs/         JobQueue (одна задача за раз), Job, JobState
    Anitools.App/                 # Avalonia
      App.axaml, Program.cs (аргумент командной строки = папка)
      ViewModels/   MainWindowVM, FolderVM, VideoOnlyVM, AudioExtractVM, AudioMuxVM, SubtitlesVM,
                    RenameVM, RemuxVM, HlsVM, JobsVM, SettingsVM, ShikimoriPickerVM, VoicePickerVM
      Views/        *.axaml к каждой VM
      Assets/       иконка, шрифт
  tests/
    Anitools.Core.Tests/
      Golden/       *.json — эталоны, сгенерированные из reference (см. §5.2)
      Parsing/, Operations/, Hls/, Shikimori/ (фикстуры JSON ответов API)
      Integration/  [Trait("Category","Integration")] — нужен ffmpeg/mkvtoolnix в PATH
      Fixtures/     MediaFactory — генерирует тестовые медиа в temp
    Anitools.App.Tests/           # Avalonia.Headless, скриншоты → docs/screenshots/
  tools/
    gen_golden.py                 # импортирует reference/python/anitools.py, пишет tests/.../Golden/*.json
  reference/python/               # оригинал, НЕ МЕНЯТЬ
  docs/PLAN.md, docs/screenshots/
```

### 3.3 Модель операции (ключевая идея)
Ядро никогда ничего не спрашивает. Каждый пункт меню = три шага:

```csharp
// эскиз, не окончательный код
public interface IOperation<TOptions>
{
    Task<SourceInfo> InspectAsync(string folder, CancellationToken ct);           // что есть в папке: файлы, дорожки первого файла…
    Task<OperationPlan> PlanAsync(string folder, TOptions options, CancellationToken ct); // чистый план: входы → выходы, статус (сделать/пропуск/ошибка + причина), команды
    Task<OperationResult> ExecuteAsync(OperationPlan plan, IProgress<ProgressEvent> progress, CancellationToken ct);
}
```
- `TOptions` — record с полями, соответствующими вопросам из §2.3 (те же дефолты).
- `PlanAsync` — основа и для превью в GUI, и для тестов (сравнение команд с эталонами).
- `ExecuteAsync` — запускает команды, шлёт `ProgressEvent` (файл i/N, стадия, %, скорость, битрейт, ETA), при отмене убивает дерево процессов и удаляет недописанный выход **текущего** файла.
- Интерактивные куски (Shikimori, озвучки, перегруппировка, ручные номера) — заполняются GUI до `PlanAsync` и приходят в Options.

### 3.4 Очередь задач
- `JobQueue`: задачи выполняются по одной (GPU и диск); можно поставить несколько в очередь.
- Задача = операция + план + лог + состояние (ожидает / идёт / готово / ошибка / отменена).
- При закрытии окна с идущей задачей — подтверждение; при выходе — отмена, kill дерева, снятие RAM-диска.

### 3.5 Поиск программ (`ToolLocator`)
Порядок: путь из настроек → переменные `FFMPEG_PATH`/`FFPROBE_PATH` → PATH.
Если найденный exe лежит в `...\chocolatey\bin\` (шим) — искать настоящий:
`%ChocolateyInstall%\lib\ffmpeg*\tools\*\bin\<имя>.exe` (сейчас `C:\ProgramData\chocolatey\lib\ffmpeg\tools\ffmpeg\bin\`).
mkvmerge/mkvextract — PATH, затем `C:\Program Files\MKVToolNix\`. imdisk — PATH/System32.
Плашки сверху справа показывают найденное (ffmpeg версия, mkvmerge, ImDisk, NVENC: проверка `ffmpeg -hide_banner -encoders` на `h264_nvenc` и `-filters` на `scale_cuda`).

### 3.6 Настройки (`settings.json`)
```json
{
  "tools": { "ffmpeg": "", "ffprobe": "", "mkvmerge": "", "mkvextract": "", "imdisk": "" },
  "voices": ["AniLiberty (AniLibria)", "ТО Дубляжная", "…"],
  "hls": {
    "segmentSeconds": 6, "nvencPreset": "p4", "fixedCq": 21.0, "fixedCqPeak": 4,
    "calibration": { "windows": 10, "tolerance": 0.03, "maxPasses": 4, "cqStart": 26.0, "cqMin": 14.0, "cqMax": 40.0 },
    "ladder": [ { "name": "360p", "width": 640, "height": 360, "bitrate": 800000 }, "…" ],
    "separateTopZipBytes": 7516192768
  },
  "workDir": { "mode": "RamDisk", "folder": "D:\\anitools_tmp", "ramDiskGb": 14 },
  "shikimoriBaseUrl": "https://shikimori.io/api/",
  "fonts": { "customDir": "C:\\personal\\Apps\\MPV by shiguchi\\fonts", "dafontMaxTries": 4 },
  "hardsub": { "encodeArgs": ["-c:v","hevc_nvenc","-tune","hq","-multipass","fullres","-rc","vbr","-cq","17","-qmin","1","-qmax","51","-bufsize","80M","-tier","high","-pix_fmt","yuv420p10le"], "fontsDirName": "Fonts" },
  "audioShift": { "seconds": 1.0, "bitrate": "256k", "workers": 6 },
  "audioConvert": { "format": "mka", "bitrate": "256k", "channels": 2, "fixTimestamps": true, "workers": 8 },
  "subShift": { "seconds": 1.0 },
  "recentFolders": []
}
```
`fixedCq: null` = режим подбора CQ. Дефолты = константы оригинала.

### 3.7 Что менять нельзя (совместимость выхода)
Имена выходных папок и файлов (§2.3, §2.5), структура zip (пути внутри, Stored), имена плейлистов `master.m3u8` в папках качеств, отсутствие корневого master, правило 5.1/7.1 → AAC 192k stereo, правило отдельного 4K-архива, резюм по наличию zip + всех mka, порядок/disposition/metadata аудиодорожек.

---

## 4. GUI

### 4.1 Принципы
- Один главный экран: слева меню по разделам и рабочая папка, сверху название страницы, папка, текущая задача
  и найденные программы; уведомления всплывают справа сверху.
- Каждый инструмент — одна страница сверху вниз: **файлы → настройки → превью результата → [Запустить]**. Никаких мастеров с «Далее», всё видно сразу; превью пересчитывается при изменении настроек.
- Рабочая папка: «Обзор», перетаскивание папки на окно, список недавних, аргумент командной строки (`Anitools.exe "D:\anime\X"`), команда `ani` в консоли — открывает приложение в текущей папке (§4.12).
- Тёмная тема и оформление как у Anime Uploader пользователя (его style.css): фиолетовый акцент, зелёный /
  жёлтый / красный / бирюзовый для состояний, карточки, плашки, полоса вариантов. Значок — фиолетовая плитка с «A».
  *(До редизайна после этапа 7 — пурпурная шапка `▌ ANITOOLS by shiguchi`, как рамки rich.)*
- Всё на русском.

### 4.2 Главное окно
```
┌──────────────────┬────────────────────────────────────────────────────────────────────┐
│ [A] anitools      │ Только видео  D:\anime\Sousou no Frieren   ⟳ (◌ HLS ▬▬ 41%) ✓ffmpeg ✓NVENC │
│     0.1.0 · by …  ├────────────────────────────────────────────────────────────────────┤
│ ВИДЕО             │ ┌ Файлы ────────────────────── 12 шагов · к запуску 9 ┐ ┌ уведомление ┐ │
│▌Только видео      │ │ ☑ Файл                     Размер  Что будет        │ └─────────────┘ │
│  Ремукс           │ │ ☑ Frieren - 01.mkv         1,4 ГБ  → Video only\…   │                 │
│  HLS, Хардсаб     │ │ ☐ Frieren - 02.mkv         1,4 ГБ  [пропуск] готово │                 │
│ АУДИО … ФАЙЛЫ     │ └──────────────────────────────────────────────────────┘                 │
│ ───────────────── ├────────────────────────────────────────────────────────────────────┤
│  Задачи   • 1     │ ⓘ что и куда пишется                                [▶ Запустить (9)]│
│  Настройки        │                                                                    │
│ ┌ ● Sousou no F… ┐│                                                                    │
│ └ D:\anime  🕘 📂 ┘│                                                                    │
└──────────────────┴────────────────────────────────────────────────────────────────────┘
```

### 4.3 «Только видео» / «Ремукс» (простые)
```
Файлы (12)                                                  [☑ все]
 ☑ Frieren - 01.mkv         1.4 ГБ   → Video only\Frieren - 01.mkv
 ☐ Frieren - 02.mkv         1.3 ГБ   уже готово — пропуск
 …
[Ремукс]  Формат: (•) MP4  ( ) MKV      ☑ копировать субтитры (только MKV)
                                                      [ Запустить (11) ]
```

### 4.4 «Только аудио» (п.2)
```
Файлы (12) …
Дорожки (по первому файлу: Frieren - 01.mkv)
 ☑ 1  AniLibria.TV      aac   2.0  rus
 ☐ 2  Оригинальная      flac  2.0  jpn
 ☑ 3  DEEP              ac3   5.1  rus
Сохранение: (•) отдельный файл на дорожку  ( ) все выбранные в один .mka
 ☑ номер дорожки в имени папки и файла
 ┌ только для «один .mka» ───────────────────────────────────────┐
 │ #  Исх.  Тайтл [▾ войс-лист / свой текст]   Язык [rus ▾]       │
 │ 1  1     AniLibria.TV                        rus               │
 │ 2  3     DEEP                                rus               │
 └───────────────────────────────────────────────────────────────┘
Результат: Audio only\1. AniLibria.TV\1. Frieren - 01.AniLibria.TV.mka (+23)
                                                      [ Запустить ]
```

### 4.5 «Сборка аудио» (п.3)
```
Дорожки исходника (по Frieren - 01.mkv):  ☑1 AniLibria.TV  ☐2 Оригинальная  ☑3 DEEP
☑ Добавить внешние аудио (найдено для 01: 2 файла, 3 дорожки)
Набор 1 — 11 серий (01–06, 08–12). Итоговые дорожки (перетаскивать мышью или ↑↓):
 #  Источник                          Тайтл [▾]          Язык   По умолч.
 1  внутр. 1: AniLibria.TV            AniLibria.TV       rus    ●
 2  внешн. Audio only\2. DEEP\…mka    DEEP               rus
 3  внутр. 3: DEEP                    …                  rus
Набор 2 — 1 серия (07): нет «Audio only\2. DEEP»          [взять из набора 1]
 #  Источник                          Тайтл [▾]          Язык   По умолч.
 1  внутр. 1: AniLibria.TV            AniLibria.TV       rus    ●
 2  внутр. 3: DEEP                    …                  rus
                                                      [ Запустить ]
```

### 4.6 «Субтитры» (п.4)
```
Дорожки субтитров (по Frieren - 01.mkv):
 (•) 1  Надписи   S_TEXT/ASS → .ass   rus
 ( ) 2  Полные    S_TEXT/ASS → .ass   rus
 ( ) 3  English   S_TEXT/UTF8 → .srt  eng
Искать в каждой серии: (•) по ID  ( ) по тайтлу  ( ) по языку
Тип: (•) надписи → папка «надписи», «.надписи»   ( ) сабы → «сабы», «.сабы»
Превью:  01 → надписи\Frieren - 01.надписи.ass  ✓
         05 → дорожка «Надписи» не найдена (есть: Signs, Full) — пропуск
                                                      [ Запустить ]
```

### 4.7 «Переименовать» (п.5)
```
Базовое название: [Sousou no Frieren          ] [Найти на Shikimori]
Нумерация файлов начинается с: [1]     Суффикс: [         ] (напр. «надписи»)
 Старое имя                                  Серия     Новое имя
 [SubsPlease] Sousou no Frieren - 01 (1080p)  [01]  →  Sousou no Frieren - 01.mkv
 [SubsPlease] Sousou no Frieren - 02 (1080p)  [02]  →  Sousou no Frieren - 02.mkv
 weird_name.mkv                               [  ] ⚠ номер не найден — пропуск
(номер в каждой строке можно поправить руками)
                                                [ Переименовать (2) ]
```
Улучшение: после переименования пишется `rename_<ts>.json` в логи → кнопка «Откатить последнее переименование».

### 4.8 «HLS» (п.7)
```
Тайтлы                                                  [Перегруппировать]
 ☑ Sousou no Frieren          12 файлов  Shikimori: 52991 - Sousou no Frieren [сменить]
 ☑ Sousou no Frieren OVA       1 файл    Shikimori: — (пропущено)       [выбрать]
 ☐ Something Else              3 файла   (не конвертировать)
Озвучки — Sousou no Frieren, набор дорожек 1 (11 файлов из 12):
 #  Дорожка в файле        Язык   Озвучка [▾ войс-лист / свой текст / — не брать]
 1  AniLibria.TV            rus    AniLibria.TV
 2  Japanese                jpn    Оригинальная
Набор дорожек 2 (1 файл: Frieren - 07.mkv): …
Качество: (•) постоянное CQ [21]  ( ) подбор под размер        Пресет NVENC [p4]
Временные файлы: (•) RAM-диск [14 ГБ ▾]  ( ) папка [D:\anitools_tmp] [..]  ( ) рядом с выходом
☐ Выключить компьютер по завершении
План: 13 серий × 6 качеств → hls_multi\52991 - Sousou no Frieren\ …  (2 уже готовы — пропуск)
                                                      [ Запустить ]
```

### 4.9 Диалоги
- **Shikimori**: строка поиска (предзаполнена очищенным названием), слева — все найденные тайтлы (постер, русское и оригинальное название, тип · серии · год, оценка), справа — карточка выбранного: постер, названия (ромадзи, английское), тип/статус/серии/длительность/год, оценка, жанры, студия, описание без BBCode, ссылка «открыть на Shikimori» (системный браузер); двойной клик = выбрать, поле «ID вручную», кнопка «Пропустить». Постеры (`main`, 225×318 webp) качаются по 4 после поиска; не скачался — заглушка.
- **Перегруппировка**: две панели — файлы и группы; перетаскивание файлов в группы, переименование группы, «новая группа», «остаток пропустить/в отдельную группу».
- **Подтверждение запуска** для долгих задач (HLS): сводка плана.

### 4.10 «Задачи» и «Настройки»
```
Задачи
 ● HLS · Sousou no Frieren          серия 3/12  видео 41%  x2.3  ETA 01:12:40  [Отмена] [Лог]
   └ 01 ✓ 23:41  02 ✓ 24:02  03 … 
 ✓ Субтитры · 12 файлов             12/12  0 ошибок                         [Папка] [Лог]
 ✗ Ремукс · 3 файла                 2/3    1 ошибка                          [Папка] [Лог]
```
Настройки: пути к программам (авто + «Обзор» + «Проверить»; по программам — FFmpeg, MKVToolNix, ImDisk — с кнопкой «Установить» / «Обновить»: последняя версия с сайта), войс-лист (редактируемый список), параметры HLS (лестница таблицей, CQ, потолок, пресет, сегмент, порог 4K-архива), временная папка/RAM-диск по умолчанию, URL Shikimori, папка своих шрифтов, параметры хардсаба/аудио.

### 4.11 Экраны доп. инструментов (§2.9)
```
Озвучки → .mka          Режим: (•) по сериям  ( ) всё в один [имя: Movie      ]
 Озвучки (найдено 3):   #  Метка          Тайтл [▾]        Язык   ↑↓
                        1  AniLibria.TV   AniLibria.TV     rus
                        2  DEEP           DEEP             rus
                        3  Original       Оригинальная     jpn
 План:  MKA\Frieren - 01.mka ← 3 файла · MKA\Frieren - 02.mka ← 3 файла · …

Шрифты для .ass         Своя папка: [C:\personal\Apps\MPV by shiguchi\fonts] [..]
 Шрифт               Где нашёлся                     Файл
 Arial               система                          arial.ttf
 Komika Axis         скачан (dafont) → в свою папку   KOMIKAX_.ttf
 Some Font           ✗ не найден  [fontsquirrel] [fontsgeek]
                                                  [ Собрать fonts.zip ]

Чистка стилей           Поле: (•) стиль  ( ) актёр     Режим: (•) оставить отмеченные  ( ) удалить отмеченные
 ☑ Default (ep01.ass)   ☐ Signs (ep01.ass)   ☑ Italics (ep03.ass) …
 Будет удалено строк: 412 в 12 файлах · бэкап → ass_backup_2026-10-07_20-15-00\

Сдвиг субтитров / Сдвиг аудио     Сдвиг: [+1.0] сек   файлы ☑…   → subs_fixed\ / audio_fixed\
Хардсаб                 Пары: ☑ 01.mkv + 01.ass  ☑ 02.mkv + 02.ass   Шрифты: Fonts\ ✓   Профиль: hevc_nvenc cq17 10bit
Дорожки файла           [файл ▾]  таблица дорожек   Формат копирования: (•) • ( ) 1. ( ) текст   [Копировать]
```

### 4.12 Запуск командой `ani`
Как сейчас с Python-версией: в любой консоли (cmd, PowerShell, Windows Terminal) в папке с сериями набрать `ani` —
открывается окно Anitools с этой папкой как рабочей. `ani "D:\anime\X"` — открыть указанную папку.
Консоль сразу свободна: не ждёт закрытия окна.

`C:\personal\Scripts\ani.bat` (папка в PATH), копия в репозитории — `tools/windows/ani.bat`:
```bat
@echo off
rem ani — открыть anitools в текущей папке (или в указанной: ani "D:\anime\X")
set "APP=C:\personal\Apps\anitools\Anitools.exe"
if "%~1"=="" (start "" "%APP%" "%CD%\.") else (start "" "%APP%" %*)
```
- `start ""` — окно запускается отдельно, консоль не ждёт.
- `"%CD%\."` вместо `"%CD%"`: в корне диска `%CD%` = `D:\`, и `"D:\"` ломает аргумент (обратная косая
  экранирует кавычку). Приложение приводит путь через `Path.GetFullPath` и срезает хвостовую `"` на всякий случай.
- Аргумент: относительный путь — от текущей папки; несуществующая папка → сообщение в окне, папку можно выбрать
  через «Обзор» / «Недавние».
- **Одна копия приложения.** Если Anitools уже открыт, новая копия не стартует, а передаёт папку открытому окну
  (именованный канал): окно выходит на передний план и переключается на эту папку. Задачи в очереди продолжают
  работать со своими папками, тяжёлые (GPU) по-прежнему идут строго по одной.
- Старый `ani.bat` (Python) переименовывается в `ani-py.bat` — это и бэкап, и запасной запуск прежней версии
  командой `ani-py`.

---

## 5. Тестирование

### 5.1 Уровни
1. **Golden-тесты чистых функций** — эталоны из оригинала (§5.2). Главная защита от расхождений.
2. **Юнит-тесты** — планировщики операций (на фейковом `IProcessRunner` и фейковой файловой системе/temp-папке), построители команд, парсер прогресса, MOV-парсер (байты собираются в тесте), Shikimori (фейковый `HttpMessageHandler`, 429/404/таймаут), калибратор CQ (фейковый раннер возвращает размеры), упаковщик zip (Stored, пути, отдельный 4K-архив по порогу — порог в тесте маленький), резюм, очистка при ошибке.
3. **Интеграционные** (`Category=Integration`, пропускаются, если нет ffmpeg/mkvmerge): реальные ffmpeg/ffprobe/mkvmerge/mkvextract на сгенерированных файлах (§5.3); HLS — с `EncoderProfile.Software` (libx264 + `scale`, без CUDA) и урезанной лестницей.
4. **GUI headless** — `Avalonia.Headless` + Skia: открыть каждый экран с демо-данными (VM на фейковом ядре), проверить ключевые состояния, сохранить PNG в `docs/screenshots/<экран>.png` (тёмная тема, 1280×800). Скриншоты коммитятся — пользователь смотрит их в PR.

### 5.2 Golden-эталоны из оригинала
`tools/gen_golden.py` (Python 3.11+, без rich — модуль импортируется и без него):
```python
import importlib.util, json, sys
spec = importlib.util.spec_from_file_location("anitools", "reference/python/anitools.py")
at = importlib.util.module_from_spec(spec); spec.loader.exec_module(at)
# для каждого набора входов → {"input": ..., "output": ...} в tests/Anitools.Core.Tests/Golden/<func>.json
```
Покрыть: `anitomy_parse`, `extract_episode_number_smart`, `_parse_anime_title`, `_parse_anime_group`,
`_title_season`, `_clean_title_for_search`, `_filename_safe_title`, `_sanitize_folder`, `sanitize_folder_name`,
`_parse_track_ids` (включая ошибки), `_detect_lang`, `_external_audio_matches`, сортировку `_natural_key`,
`codec_id_to_ext`, `_find_subtitle_track_by_title/_lang`, `_rank_shikimori` (на фикстурах), `_next_hls_cq`,
`_pick_calibration_windows`, `_hls_video_input_args`, `_hls_video_encode_args`, `_build_video_ffmpeg_cmd`
(с явным `resolutions`, во временной папке), `_build_audio_ffmpeg_cmd` (подменить `at._get_audio_channels`).
Входные имена файлов — набор из 150–300 реалистичных имён: SubsPlease/Erai-raws/`[Group] Title - 05 [1080p][ABCD1234].mkv`,
`S01E02`, `Title S2 - 05`, `2nd Season - 05`, `Title - 01v2`, `01-12`, `第01話`, OVA/ONA/Movie/Special (с номером и без),
`HunterHunter.11_001`, `Title.надписи.ass`, кириллица, японские скобки, годы, `- 00`, файлы без номера.
Различия Python и .NET, которые учесть: `casefold` vs `ToLowerInvariant`, `str.isalnum`/`isdigit` vs `char.IsLetterOrDigit`/`IsDigit`,
`int()` от не-ASCII цифр, `round`/форматирование `%.2f`/`%.3f` (InvariantCulture!), порядок сортировки.

### 5.3 Тестовые медиа (генерирует `MediaFactory` в temp, 2–3 с, 160×90)
Пример (Linux, ffmpeg + mkvtoolnix из apt):
```bash
ffmpeg -y -f lavfi -i testsrc2=size=160x90:rate=24:duration=3 \
  -f lavfi -i sine=f=440:d=3 -f lavfi -i sine=f=660:d=3 \
  -f lavfi -i anullsrc=channel_layout=5.1:sample_rate=48000 -t 3 \
  -i signs.ass -i full.srt \
  -map 0 -map 1 -map 2 -map 3 -map 4 -map 5 \
  -c:v libx264 -preset ultrafast -c:a:0 aac -c:a:1 flac -c:a:2 ac3 -c:s:0 ass -c:s:1 srt \
  -metadata:s:a:0 title="AniLibria.TV" -metadata:s:a:0 language=rus \
  -metadata:s:a:1 title="Оригинальная"  -metadata:s:a:1 language=jpn \
  -metadata:s:a:2 title="DEEP"          -metadata:s:a:2 language=rus \
  -metadata:s:s:0 title="Надписи" -metadata:s:s:0 language=rus \
  -metadata:s:s:1 title="Full"    -metadata:s:s:1 language=eng \
  "Test Show - 01.mkv"
```
Плюс: mp4/mov с `handler_name` у аудио (проверка MOV-фолбэка), файл без звука, файл с подчёркиваниями в имени,
внешние `.mka` в подпапках `Audio only\N. Имя\`, серия с другим набором дорожек (для раскладок в HLS),
read-only выходной файл.
Для доп. инструментов: .ass с разными стилями/актёрами/`\fn` и UTF-16/cp1251-кодировками; .srt; **синтетические шрифты** —
тест сам собирает минимальный TTF (таблицы `name` и `cmap`) с нужными именами, с кириллицей и без; mkv с шрифтами-вложениями
(`-attach font.ttf -metadata:s:t mimetype=font/ttf`); загрузчики шрифтов — только на фейковом HTTP с записанными ответами.

### 5.4 Что проверяется только на Windows
NVENC/scale_cuda/NVDEC и CPU-фолбэк на реальной видеокарте, ImDisk (права админа), Job Object и убийство дерева через шим,
`shutdown`, поиск exe Chocolatey, длинные пути и кириллица в путях, атрибут read-only, zip64 (> 4 ГБ), внешний вид на реальном
экране (DPI, шрифты), паритет с Python на **копиях** реальных серий (§6.2).

---

## 6. Облако vs локально

### 6.1 Облачная сессия (Linux, без экрана)
Подготовка окружения: .NET 10 SDK, `apt install ffmpeg mkvtoolnix python3`. Всё остальное — в репозитории.

| Делается в облаке | Как проверяется |
|---|---|
| Скелет решения, сборка, `global.json`, пакеты | `dotnet build` без предупреждений |
| Golden-генератор и эталоны | `python3 tools/gen_golden.py` → JSON в репозитории |
| Всё ядро: парсинг, Shikimori, операции 1–6, HLS (команды, калибровка, упаковка, резюм), очередь, настройки, логи | юнит + golden + интеграционные (Software-профиль для HLS) |
| `ToolLocator`, `JobObject`, `ImDiskRamDisk`, `ShutdownService` — код с абстракциями | только юнит-тесты на фейках (логика выбора буквы, файл состояния, построение аргументов) |
| Доп. инструменты §2.9 (шрифты, mka-муксер, хардсаб, сдвиги, чистка стилей, перекодирование) | юнит + интеграционные; хардсаб — с `libx265`/`libx264` вместо NVENC в тестах |
| GUI: все экраны и диалоги, VM, навигация | `Avalonia.Headless` тесты + скриншоты в `docs/screenshots/` |
| (опц.) GitHub Actions: `ubuntu-latest` (всё) + `windows-latest` (сборка + юнит) | зелёный CI |

### 6.2 Локально на Windows (с пользователем)
| Шаг | Детали |
|---|---|
| Установить .NET 10 SDK | сейчас есть только рантайм; через Chocolatey или winget (имя пакета проверить) |
| Поиск программ | шим `C:\ProgramData\chocolatey\bin\ffmpeg.exe` → настоящий `...\lib\ffmpeg\tools\ffmpeg\bin\ffmpeg.exe`; MKVToolNix; ImDisk |
| NVENC/CUDA | HLS на сгенерированном локально тест-клипе (4K, 10 с) + проверка CPU-фолбэка (H.264 шире 4096) |
| RAM-диск | создание/снятие ImDisk от админа и **без прав** (запрос Windows один раз, помощник снимает диск, если окно закрыли или процесс убили), файл состояния |
| Отмена/закрытие | kill дерева через шим, нет зависших ffmpeg.exe |
| Паритет с Python | на **копии** 1–2 серий в отдельной папке: прогнать п.1–7 Python-версией и C#-версией, сравнить: список файлов, `ffprobe` потоков (порядок, title, language, disposition), листинг zip, размеры ±. Оригиналы не трогать. |
| Внешний вид | реальное окно, DPI, шрифты, тёмная тема |
| Шрифты | реальные `C:\Windows\Fonts`, своя папка `C:\personal\Apps\MPV by shiguchi\fonts`, живое скачивание с Google Fonts/dafont/1001fonts |
| Хардсаб | hevc_nvenc на реальной карте, `Fonts\` рядом |
| Публикация | `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` → папку согласовать (напр. `C:\personal\Apps\anitools\`) |
| Команда `ani` | §4.12: старый `ani.bat` → `ani-py.bat` (бэкап и запасной запуск Python-версии), новый — из `tools/windows/ani.bat`; проверить: `ani` в cmd, PowerShell и Windows Terminal, в корне диска, в папке с кириллицей и пробелами, повторный `ani` при открытом окне |
| Интеграция | (опц.) пункт «Открыть в anitools» в контекстном меню папки (HKCU) — с экспортом ветки реестра перед изменением |

### 6.3 Репозиторий
Приватный `github.com/sh1guchi/anitools`, ветка `main`. Облачная сессия клонирует его и работает по этапам 1–7,
каждый этап — отдельная ветка/PR. Запуск: «Работай по `docs/PLAN.md`, начни с этапа 1».

---

## 7. Этапы и критерии готовности

**Этап 1 — скелет (облако)**
- [x] `Anitools.slnx`, проекты из §3.2, `Directory.Build.props/Packages.props`, `global.json`, `.editorconfig`.
- [x] Пустое окно Avalonia открывается в headless-тесте, скриншот сохраняется.
- [x] `tools/gen_golden.py` работает, эталоны закоммичены.

Решения этапа 1:
- Версии: Avalonia 12.1.3, CommunityToolkit.Mvvm 8.4.2, xunit.v3 3.2.2 (на ней собран Avalonia.Headless.XUnit 12.1.3),
  `global.json` — SDK 10.0.100+ (`latestFeature`). Иконки (Material.Icons.Avalonia) подключить на этапе 7.
- Скриншоты тесты пишут в `artifacts/screenshots/`; в `docs/screenshots/` — только с `ANITOOLS_UPDATE_SCREENSHOTS=1`,
  чтобы обычный прогон тестов на Windows не менял файлы в git.
- Эталоны (`tests/Anitools.Core.Tests/Golden/`, 39 файлов): шире списка §5.2 — ещё разбор вывода ffprobe/ffmpeg
  (длительность, каналы, дорожки, NVDEC, битрейт по окнам), полный подбор CQ с «проигрыванием» команд, запросы
  к Shikimori, константы. `source_sha256` считается по тексту с LF: CRLF-версия даёт `8920902b…49d50a` (как выше).
- CI (`.github/workflows/ci.yml`): Linux — `gen_golden.py --check`, сборка, тесты; Windows — сборка и тесты.

**Этап 2 — чистая логика (облако)**
- [x] Anitomy, номер серии, названия/сезоны/санитизация, track-id список, язык, натуральная сортировка, codec→ext, выбор субтитров по тайтлу/языку.
- [x] Все golden-тесты зелёные (расхождения с Python — только задокументированные в тесте с причиной).

Решения этапа 2:
- `Parsing/PyText` — строки и регулярки «как в Python»: `\w` = `[\p{L}\p{N}_]`, `\s` = `\s` + `\x1c–\x1f`,
  `\b` — через просмотр назад/вперёд, `strip()`, `lower()` (İ → i̇), `casefold()` (таблица из `tools/gen_casefold.py`),
  `int()` (цифры любого письма, `_`), `splitext`/`stem`, сравнение строк по кодовым точкам. Всё сверено с Python
  на всём диапазоне Unicode (эталон `str_casing`, тесты `PyTextTests`).
- Расхождений с оригиналом нет ни в одном эталоне. Перенесён и баг оригинала: в `extract_episode_number` римские
  номера OVA не срабатывают никогда (`int("II")` падает раньше словаря) — так и оставлено, с комментарием.
- Единственное осознанное отличие: `TrackIdList.Parse` отказывается раскрывать диапазон длиннее 10 000 дорожек
  (в оригинале «1-99999999» подвесил бы программу).

**Этап 3 — медиа и простые операции (облако)**
- [x] `ProcessRunner` (UTF-8, отмена, хвост stderr, `-progress pipe:1`), `MediaProbe` (ffprobe JSON, mkvmerge -J), `MovAtomReader`.
- [x] Операции 1, 2 (оба режима), 3, 4, 6: Options → Plan → Execute; интеграционные тесты на сгенерированных файлах сверяют выход через ffprobe (потоки, title, language, disposition) и имена файлов.
- [x] П.3: группировка серий по набору слотов, отдельные порядок/тайтлы/язык на набор; тест: у одной серии нет внешнего файла → её команда без этой дорожки, остальные не съехали.
- [x] Логи ошибок, снятие read-only.

Решения этапа 3 (часть 1 — процессы и медиа):
- `ProcessRunner`: аргументы через `ArgumentList`, вывод в UTF-8, stdin закрыт, отмена = `Kill(entireProcessTree)`,
  хвост stderr 400 строк, прогресс ffmpeg — `-progress pipe:1 -nostats` (`FfmpegProgressParser`).
- Найдено тестами: без UTF-8-локали (Linux, `LC_ALL=C`) mkvmerge выдаёт битый JSON с кириллицей, а mkvextract
  не понимает кириллицу в имени файла. Поэтому на Linux/macOS дочерним процессам ставится `LC_ALL=C.UTF-8`,
  а `mkvmerge -J` вызывается с `--output-charset UTF-8` (в оригинале без; на Windows страхует от кодовой страницы консоли).
- `ToolLocator`: настройки → `FFMPEG_PATH`/`FFPROBE_PATH` → PATH → `Program Files\MKVToolNix` / `System32`;
  шим Chocolatey заменяется настоящим exe из `chocolatey\lib\…\tools`. В CI на Windows ffmpeg ставится через
  Chocolatey, как у пользователя, и тест проверяет, что найден именно настоящий exe.
- `MediaProbe`: один `ffprobe -show_streams -show_format` вместо разбора `ffmpeg -i`; `CodecDescription` повторяет
  «Audio: …» из `ffmpeg -i` (сверено на всех фикстурах) — для имён папок дорожек без тайтла.
- Фикстуры реального вывода ffprobe/ffmpeg/mkvmerge — `tools/capture_media_fixtures.py` → `tools/golden_inputs/media/`;
  новые эталоны `mov_audio_titles`, `mkv_subtitle_tracks`, `list2cmdline`.
- Логи ошибок — формат оригинала; два лога одного файла в одну секунду не затирают друг друга (`…_2.log`).

Решения этапа 3 (часть 2 — операции):
- Модель: `OperationPlan` из `PlanItem` (одна команда над одним файлом: Run / Skip / Error с причиной, выходные файлы)
  → `PlanExecutor`. Inspect (что в папке) → настройки → Plan → Execute.
- Команды планов совпадают с оригиналом: сценарный эталон `operation_scenarios` — 17 сценариев, в которых оригинал
  запускается на временной папке со скриптом ответов на вопросы и выводом программ из фикстур. Единственное
  отличие — `-map 0:a?` в ремуксе (§2.8 #7), тест сверяет остальную команду.
- Исполнитель добавляет к ffmpeg только `-progress pipe:1 -nostats`, к mkvextract — `--output-charset UTF-8`.
  При ошибке — лог и удаление недописанного выхода (в оригинале недописанный файл оставался и при следующем
  запуске считался готовым). Пустой остаток прошлого запуска с «только чтение» перезаписывается.
- П.3: внешний файл — по ключу «папка | префикс «N. » | хвост имени после серии | дорожка», серии с одинаковым
  набором ключей — один набор со своими порядком/тайтлами/языком (`AudioSlotSetConfig.Default` / `FromMain`).
  ffprobe смотрит каждую серию (оригинал — только первую), иначе не узнать, где дорожек меньше.
  Серия, у которой не осталось ни одной дорожки, — ошибка в плане (оригинал собрал бы видео без звука).
- П.4: Matroska → mkvextract; MP4 и прочее → ffmpeg `-map 0:s:N`, mov_text → SRT. «Уже готово» проверяется по
  расширению дорожки этой серии (оригинал — по расширению эталонной дорожки первого файла).
- Порядок файлов — по имени без учёта регистра, как отдаёт NTFS (оригинал брал порядок `os.listdir`).

**Этап 4 — переименование и Shikimori (облако)**
- [x] `ShikimoriClient` (ретраи, 429, 404, таймаут) на фейковом HTTP; ранжирование по golden.
- [x] Операция 5 + журнал для отката.

Решения этапа 4:
- `ShikimoriClient`: те же запросы, что у оригинала (сверено эталонами: путь запроса, разбор ответа, запасные
  запросы, оригинальное название); `ShikimoriQuery` — тип/сезон/запрос из названия группы и ранжирование.
  Пауза между попытками подменяется в тестах, таймаут HttpClient — повод повторить, а не отмена.
- П.5: сценарный эталон `rename_scenarios` (6 сценариев: своё название, сдвиг нумерации с припиской, название
  с Shikimori, ручные номера, занятое имя, файлы без номеров) — C# переименовывает так же.
  В GUI ручной и автоматический режимы — одна таблица: номер предзаполнен найденным, его можно поправить.
- Отличия: два файла с одинаковым новым именем — конфликт у обоих (оригинал переименовывал первый);
  смена только регистра букв (`a.mkv` → `A.mkv`) — не конфликт (оригинал на Windows считал «файл уже существует»).
- Журнал `rename_<дата>.json` в папке логов → «Откатить последнее переименование» (`RenameJournal.Undo`).
  Если старое имя уже занято, файл не трогается и остаётся в журнале — после освобождения имени откат можно повторить.

**Этап 5 — HLS (облако)**
- [x] Группировка, раскладки и озвучки, имена папок серий.
- [x] Построитель команд (Nvenc и Software), совпадает с golden для Nvenc.
- [x] Анализ битрейта, калибратор (фейковый раннер), постоянный CQ.
- [x] Упаковщик zip (Stored, пути, 4K отдельно по порогу), перенос аудио, резюм, очистка при сбое, CPU-ретрай.
- [x] Интеграционный прогон Software-профиля на 2 сериях × 2 качества.
- [x] `IWorkDirProvider`: папка / рядом / RAM-диск (логика буквы и файла состояния — юнит-тесты).

Решения этапа 5:
- `Operations/Hls`: `HlsSettings` (константы оригинала по умолчанию, лестница — настройка), `HlsCommandBuilder`
  (видео, аудио, калибровочное окно; профиль Nvenc совпадает с эталонами до символа, в т.ч. смешанные слеши
  `as_posix`/`str` на Windows — сверяется на Linux через `PathStyle.Windows`), `SourceBitrate` (сумма Ноймайера и
  `//` как в CPython 3.12+), `CqCalibrator` (проигрывается по записанным вызовам эталона `hls_calibrate_cq`),
  `HlsOperation` (разбор папки и план), `HlsRunner`/`HlsEpisodeProcessor`, `HlsPackager` (zip Stored + перенос .mka).
- **Сценарный эталон `hls_scenarios`**: оригинальный п.7 прогоняется целиком (скрипт ответов, ffprobe из фикстур,
  фейковые ffmpeg и zip, `sorted` как на Windows): обычный прогон, разные раскладки дорожек + своя временная папка,
  резюм + повтор на CPU + ошибка аудио, перегруппировка с одинаковыми именами серий, отдельный 4K-архив + широкий
  исходник (CPU-декодирование), порядок имён со скобками. C# даёт те же команды, записи архивов, итоговые файлы и статусы.
- Профиль **Software** (`scale` + `libx264`, `-crf` вместо `-cq`) — для тестов на CI и компьютеров без NVIDIA;
  проверка NVDEC и повтор на CPU — только для Nvenc.
- Порядок файлов п.7 — как `sorted(Path)` оригинала на Windows: имена в нижнем регистре по кодам символов
  («[Group] …» раньше «Hellsing …»; у п.1–4 — порядок NTFS).
- Длительность и каналы — из одного JSON ffprobe (оригинал звал ffprobe отдельно на каждое поле; запасной
  `ffmpeg -i` не нужен — он читает ту же длительность контейнера). Неизвестна — прогресс без процентов, без отчёта
  о битрейте (оригинал подставлял 3600 с).
- В плане сразу видно «уже готово» (резюм оригинала); перед запуском серии проверяется ещё раз.
- Отличия: имена папок серий уникальны в пределах папки тайтла (в оригинале — в пределах группы: две группы с одним
  тайтлом перезаписывали бы друг друга), а если занято и имя с расширением — `_2`, `_3`…; имена папок озвучек
  уникальны без учёта регистра (на Windows «DEEP» и «deep» — одна папка); ошибка переноса .mka и отмена тоже чистят
  рабочую папку серии (оригинал оставлял её на RAM-диске); при отмене удаляются и недописанные архивы; старый
  `<серия>.4K.zip` удаляется, если верхнее качество теперь в основном архиве; пустая папка тайтла во временной папке убирается.
- Рабочая папка: `NearOutputWorkDir`, `FolderWorkDir`, `ImDiskRamDisk` (буква из `RZYXWVUTSQPONMLKJIHGFED`,
  `imdisk -a -t vm -s NG -m L: -p "/fs:ntfs /q /y"`, ожидание диска до 5 с, папка `L:\anitools_tmp`, снятие
  `imdisk -D -m L:`); файл состояния `%TEMP%\anitools_ramdisk.json` совместим с оригиналом; брошенные диски снимает
  `ImDiskRamDisk.CleanupOrphansAsync`; аренда снимает диск и при выходе процесса. Ошибки — `WorkDirException`
  с советом (нет ImDisk / нет прав администратора / нет свободной буквы) — GUI предложит папку или «рядом с выходом».
- `ShutdownService`: `shutdown /s /t 60`, отмена `shutdown /a` (только Windows).

**Этап 6 — доп. инструменты §2.9 (облако)**
- [x] Озвучки → .mka (§2.9.1), «Дорожки файла» (§2.9.2).
- [x] Шрифты для .ass: парсер .ass, чтение `name`/`cmap`, индекс, загрузчики (фейковый HTTP), сборка fonts.zip (§2.9.3); шрифты из видео (§2.9.4).
- [x] Чистка .ass с бэкапом, сдвиг субтитров, сдвиг и перекодирование аудио (§2.9.5–2.9.8).
- [x] Хардсаб (§2.9.9), профили ремукса AVI/M2TS (§2.9.10).
- [x] Golden-тесты для чистых функций из `reference/python/extra/` (сдвиг времени, `_strip_trailing_episode`, `voice_folder`, `font_key`, `slugify`, `escape_filter`…).

Решения этапа 6 (часть 1 — §2.9.2, 2.9.4–2.9.10):
- Эталоны соседних скриптов: `tools/gen_golden.py` грузит их из `reference/python/extra` (без запуска `main`),
  у каждого эталона свой `source` и хэш. Сняты: `sub_shift_times`, `sub_shift_text`, `ass_style_values`, `escape_filter`,
  `track_rows`, `font_attachments`, `audio_tool_commands` — C# совпадает (кроме исправленного `escape_filter`, ниже).
- `PlanExecutor`: параллельный запуск шагов (`maxParallel`; сдвиг аудио — 6, перекодирование — 8, как в оригинале),
  результаты — в порядке плана; `PlannedCommand.WorkingDirectory`; `PlanItem.RenameOnSuccess` (хардсаб пишет `.part`
  и переименовывается только после успеха, при ошибке `.part` удаляется, готовый файл не трогается).
- Сдвиг субтитров: всегда перезаписывает `subs_fixed` (сдвиг могли поменять), UTF-8 без BOM; переводы строк
  сохраняются как в файле (оригинал на Windows приводил всё к CRLF); файл с BOM UTF-16 читается правильно.
- Сдвиг аудио «+N»: `adelay=delays=N·1000:all=1` вместо склейки с тишиной стерео 48 кГц (concat приводил 5.1
  и 44,1 кГц к ней) — проверено тестом: 5.1 остаётся 5.1, длительность +1 с. «−N» и перекодирование — команды оригинала;
  добавлен только `-y` (пустой остаток прошлого запуска не мешает).
- Чистка .ass: перед правкой оригиналы копируются в `ass_backup_<дата>`; пишется UTF-8 с BOM, как в оригинале;
  переводы строк сохраняются; файл не в UTF-8 не трогается (оригинал падал целиком).
- **Исправлен баг хардсаба**: `escape_filter` экранировал один уровень, и файл с апострофом в имени («it's»)
  ffmpeg не открывал. Теперь два уровня — для разбора параметров фильтра и для графа фильтров (проверено на ffmpeg).
  ffmpeg запускается из рабочей папки с относительными путями, как в оригинале.
- Шрифты из видео: временные файлы — во временной папке системы, а не в рабочей; код 1 у mkvextract с файлом — не ошибка.
- Ремукс-профиль Blu-ray M2TS → MKV (PCM → FLAC): выход рядом с исходником, готовый пропускается. AVI (avi_to_mkv.bat)
  отдельным профилем был до редизайна; теперь — обычный вход ремукса, к .avi добавляются флаги меток времени из bat.
- «Дорожки файла»: таблица + список для копирования (• / 1. / просто текст), без тайтла — «Дорожка N».
- CI: Chocolatey иногда отвечает 504 — три попытки; поставились программы — интеграционные тесты обязательны
  (`ANITOOLS_REQUIRE_TOOLS=1`, нет программы — тест падает, а не пропускается), не поставились — предупреждение в проверке.

Решения этапа 6 (часть 2 — §2.9.1 сборка озвучек в .mka):
- `Operations/MkaMux`: `MkaNames` (префикс «N. », номер серии, имя выхода, метка озвучки, хвостовой номер в метке),
  `MkaMuxOperation` (рекурсивный обход без `MKA` и папок на «.», группы по серии / всё в один, метки и порядок
  по умолчанию, план с командами).
- Вместо пакета `anitopy` — наш Anitomy (§2.4.1), затем регулярки оригинала. Эталоны сняты так же: в `mka_muxer.py`
  на время снятия подставлен встроенный anitomy оригинала anitools.py, — эталоны `mka_names`, `mka_strip_trailing_episode`,
  `mka_voice_folder`, `mka_detect_lang` (та же логика, что `_detect_lang` п.3 — переиспользуется `LanguageGuess`).
- **Сценарный эталон `mka_scenarios`**: `process()` оригинала целиком (скрипт ответов, тайтлы и число дорожек из
  фикстур): папки п.2 «N. Озвучка» + тайтлы из войс-листа + язык (ориг. → jpn, «Eng» в тайтле файла → eng);
  россыпь файлов с [тегами] + перестановка + уже собранная серия + ошибка ffmpeg; «всё в один» с несколькими
  дорожками в файле. C# даёт те же команды.
- В GUI порядок шагов тот же: режим (по серии / всё в один, имя) → метки → порядок, тайтлы, язык → план.

Решения этапа 6 (часть 3 — §2.9.3 шрифты для .ass):
- `Operations/Fonts`: `FontText` (ключи имён, разбор .ass: UTF-16 по BOM / UTF-8 / cp1251), `SfntReader` (таблица name
  sfnt и коллекций .ttc с тем же поведением на обрыве данных, что у `struct.error` в оригинале; начертание и версия;
  кириллица в cmap), `FontIndex`, `FontDownloader` (Google Fonts без браузерного User-Agent — отдаёт .ttf, проверено;
  dafont до 4 архивов; 1001fonts), `AssFontsCollector` (своя папка → системные → скачивание, кириллица, дубли
  начертаний, `_2` при одинаковом имени, сохранение скачанного в свою папку, fonts.zip Deflate с атрибутом 0x20).
- Кириллица: оригинал без fontTools считал «кириллица есть» у любого шрифта (у пользователя fontTools, скорее всего,
  не стоит — отбор не работал). C# проверяет как fontTools: лучшая таблица символов (3,10)/(0,6)/(0,4)/(3,1)/…,
  форматы 0, 4, 6, 12; нет cmap/maxp, обрыв, неизвестный формат — «есть». Эталоны сняты с fontTools 4.60.1
  (генератор и CI ставят его сами): `sfnt_font_names` на синтетических шрифтах (MacRoman, нечётный UTF-16, непарный
  суррогат, обрыв таблицы, коллекции, cmap 4 и 12), `font_keys`, `ass_font_names`, `fonts_from_zip`, `font_downloads`
  (ответы серверов проигрываются по URL), **`ass_fonts_scenarios`** — `main()` целиком: C# собирает тот же fonts.zip,
  так же сохраняет скачанное в свою папку и так же сообщает, чего нет нигде.
- Отличия: при двух написаниях одного шрифта («Arial»/«arial») берётся первое по файлу (у оригинала — случайное:
  порядок множества); .ass ищутся без учёта регистра расширения (как glob на Windows).

**Этап 7 — GUI (облако)**
- [x] Главное окно, страницы §4.3–4.8 и §4.11, диалоги §4.9, «Задачи», «Настройки».
- [x] Очередь задач, прогресс, отмена, лог.
- [x] Запуск с папкой из командной строки и одна копия приложения: вторая передаёт папку первой (§4.12); тесты разбора аргумента.
- [x] Скриншоты всех экранов в `docs/screenshots/` (20 экранов и диалогов).

Решения этапа 7 (часть 1 — каркас):
- Ядро: `Settings/AppSettings` + `SettingsStore` — settings.json в `%APPDATA%\anitools` (Linux: `~/.config/anitools`),
  запись через временный файл; битый файл сохраняется рядом как `settings.json.bad`, работа идёт с умолчаниями;
  неверные и пустые значения заменяет `Normalized()`. Поля HLS — плоско (`hls.calibrationWindows`, `hls.cqMin`…),
  а не вложенным `calibration` из эскиза §3.6; перечисления — словами (`"mode": "ramDisk"`).
- `Jobs/JobQueue`: строго одна задача за раз; ждущую можно отменить до запуска; ошибка или исключение задачи не
  останавливает очередь (исключение целиком — в журнал задачи). `Job.Snapshot` — потокобезопасный снимок: доля,
  строка хода, вторая строка, итог, оставшееся время (по доле и прошедшему времени). `PlanJobs.Run` — план операции
  как задача: «файл 3/12 · имя · 41% · x2.3», журнал итогов по шагам, сводка «11 готово · 1 пропуск · 1 ошибка».
- `ToolStatusChecker`: версии ffmpeg и mkvmerge, NVENC = `h264_nvenc` в `-encoders` и `scale_cuda` в `-filters` (§3.5).
- Окно: шапка с рабочей папкой (клик — открыть в проводнике), «Обзор…», «Недавние» (10 последних, хранятся в
  настройках), перетаскивание папки или файла на окно; навигация по разделам §4.2; страница выбирается через
  `ViewLocator` (явный список, без рефлексии); внизу — программы ✓/✗ с подсказкой и текущая задача (клик — «Задачи»).
- Страница строится по папке, когда открыта; сменилась папка или в ней закончилась задача — перестраивается
  («уже готово» обновляется само). Превью плана — общий список с галочками: снятая галочка — шаг пропускается.
- «Ремукс» — профили MP4 / MKV (+ субтитры) / Blu-ray M2TS → MKV (AVI — обычный вход MP4 и MKV). «Настройки» — все поля §3.6
  с проверкой ввода, «По умолчанию», «Отменить изменения»; неверный путь к программе подсвечивается, даже если
  программа нашлась в PATH (она и будет использоваться, пока путь не исправят).
- Закрытие окна при идущей задаче — вопрос; «да» — отмена всех задач (процессы убиваются) и выход.
  На Windows каждая запущенная программа входит в Job Object с `KILL_ON_JOB_CLOSE` (§2.8 #10): если anitools
  упадёт или его закроют снаружи, ffmpeg завершится вместе с ним. Сам anitools в задание не входит — открытые им
  проводник и блокнот не закроются.
- §4.12: `StartupArguments` — относительный путь от текущей папки, файл → его папка, путь без кавычек из нескольких
  аргументов склеивается, `D:"` → `D:\`, нет папки — сообщение в окне. `SingleInstance` — именованный мьютекс
  `Local\anitools-<пользователь>` + именованный канал только для текущего пользователя: вторая копия передаёт уже
  вычисленный полный путь и выходит, первая открывает папку и выходит на передний план (отправитель вызывает
  `AllowSetForegroundWindow`). Первая копия не ответила за 3 с — вторая запускается сама.
- Иконки — Material.Icons.Avalonia 3.0.2 (собран под Avalonia 12).

Решения этапа 7 (часть 2 — п.2–п.5):
- Чтение серий (ffprobe, mkvmerge -J) идёт через `CachedMediaProbe` (кэш по пути, размеру и времени изменения) на
  время загрузки папки: смена галочек и режимов пересчитывает план без повторного чтения файлов; «Перечитать» — заново.
- «Только аудио»: дорожки по первому файлу, по умолчанию — первая (как «1» в оригинале). «Все в один .mka» — порядок
  стрелками, тайтл из войс-листа или свой, язык на каждую дорожку (`AudioExtractOptions.Languages`): по умолчанию
  угадывается по тайтлу (jpn/eng, иначе rus) и следует за тайтлом, пока язык не поменяли руками.
- «Сборка аудио»: по умолчанию взяты все дорожки исходника и внешние аудио (в оригинале по умолчанию — ни того, ни
  другого, что сразу давало ошибку). Наборы — «Набор N — K серий (01–06, 08–12)» с разницей от основного («нет: …»),
  у каждого порядок, тайтлы и язык, «взять из набора 1» (`AudioSlotSetConfig.FromMain`). Пустой тайтл — пишется
  настоящий, заглушки «Track N» и имена файлов не пишутся (как в оригинале).
- «Субтитры»: дорожка по первому файлу, поиск по ID / тайтлу / языку и вид (надписи / сабы) — план сразу.
- «Переименовать»: в поле названия — самое частое название среди видео (оригинал брал первое видео по алфавиту, и в
  папке с NCOP.mkv подсказка была «NCOP»); Shikimori — диалог §4.9 (8 результатов, ранжирование по сезону и типу из
  названия файлов, ID вручную, «Пропустить», ссылка на сайт), выбранное оригинальное название приводится к имени файла.
  Номер серии правится прямо в строке (ручной — жирным и берётся как есть). Переименование — сразу, а не задачей;
  если в папке идёт задача — вопрос. Кнопка отката показывает, что откатит: «N файлов в папке …».

Решения этапа 7 (часть 3 — HLS):
- Порядок решений тот же, что в оригинале, но всё на одном экране: тайтлы (галочка «конвертировать»,
  «Перегруппировать…», Shikimori на тайтл) → озвучки на каждый набор дорожек (по умолчанию название озвучки — тайтл
  дорожки, как Enter в оригинале; галочка «брать» вместо «-») → качество, кодирование, временные файлы, выключение
  (значения по умолчанию — из настроек, на экране меняются только для этого запуска) → план с галочками по сериям →
  сводка в диалоге → задача.
- Перегруппировка — у каждого файла название группы (выпадающий список уже введённых): одинаковые названия — один
  тайтл, пустое — файл не конвертировать, «Без названия → «Прочее»» — как «остаток в отдельную группу».
- Shikimori не обязателен: без него папка тайтла — его название (как «пропустить» в оригинале).
- `HlsJobs.Run`: временная папка берётся на время задачи и снимается (RAM-диск — через `WorkDirLease`); не получилось —
  задача заканчивается ошибкой с советом из `WorkDirException`. Ход — «серия 3/12 · имя · видео 41% · x2.3», доля
  серии по стадиям (видео — основная часть; анализ и подбор CQ — только при подборе), итоги готовых серий второй
  строкой «01 ✓ 23:41 · 02 ✗ 0:12», в журнале — CQ, битрейты по качествам, пояснения (даунскейл, CPU-декодирование,
  повтор, отдельный архив). Выключение — только если всё прошло без ошибок и отмены.
- `HlsRunner.ExecuteAsync` сообщает итог каждой серии сразу (`episodeDone`), а не только в конце.
- Экран предупреждает заранее: RAM-диск выбран, а ImDisk не найден; нет ffmpeg.

Решения этапа 7 (часть 4 — доп. инструменты §4.11):
- «Озвучки → .mka»: режим по сериям / всё в один (имя файла), озвучки стрелками, тайтл из войс-листа, язык по
  умолчанию (оригинальная и английская определяются сами, как в mka_muxer.py). Смена режима сохраняет порядок и тайтлы.
- «Сдвиг аудио», «Перекодировать»: параметры по умолчанию из настроек, на экране — для этого запуска; план сразу,
  выполнение по 6/8 файлов (`PlanJobs.Run` с `maxParallel`).
- «Дорожки файла»: файл из списка, таблица дорожек, список для копирования (• / 1. / текст) и кнопка «Копировать».
- «Сдвиг субтитров» и «Чистка стилей» работают сразу, без очереди (файлы маленькие). В «Чистке» ничего не отмечено —
  ничего не делается (иначе «оставить отмеченные» стёр бы все строки); перед правкой — вопрос со сводкой
  «Будет удалено строк: N в M файлах».
- «Шрифты для .ass»: до запуска — список шрифтов из .ass; сбор (с сетью) — задачей, её итог показывается на экране:
  архив, сохранённое в свою папку, чего нет нигде — с кнопками поиска на fontsquirrel/fontsgeek; где нашёлся каждый
  шрифт — в журнале задачи. «Шрифты из видео» — так же задачей с итогом на экране.
- «Хардсаб»: пары видео + .ass, есть ли папка Fonts, профиль кодирования из настроек; в превью — итоговое имя
  (а не временный `.part`) и пометка, если файл будет перезаписан.

Решения после этапа 7 — оформление и мелочи по просьбе пользователя (2026-10-08):
- Внешний вид — копия Anime Uploader пользователя: токены его style.css (цвета, радиусы, шрифт: Segoe UI на Windows,
  Inter — в остальных системах и для недостающих символов) в `App.axaml`; стандартные элементы Fluent перекрашены
  теми же токенами. Компоненты: кнопки (обычная, `primary`, `danger`, `ghost`, `sm`/`lg`, квадратная `icon`), поля
  с фиолетовой рамкой в фокусе, `.seg` (варианты полосой), карточки с заголовком (`CardHead`), плашки, `.note`,
  таблица с разделителями, вращающийся кружок, пульсирующая точка.
- Меню слева: плитка-значок, разделы, у выбранного — полоса акцента у края; «Задачи» и «Настройки» закреплены
  внизу (окно 1280×880 — без прокрутки меню); под ними карточка рабочей папки: клик — выбрать другую, рядом —
  недавние и «открыть в проводнике». Сверху: название страницы, рабочая папка, «перечитать папку», мини-ход текущей
  задачи (клик — «Задачи»), программы ✓/!. Нижней строки состояния больше нет.
- Страница: карточки настроек → карточка «Файлы» (таблица с галочками, плашки «пропуск» / «ошибка») → полоса
  внизу с пояснением «что и куда» и кнопкой запуска.
- Надписи «Запущено…», «Переименовано: 3.», «Сохранено…» — всплывающие уведомления справа сверху: исчезают сами
  (3,5 с; предупреждения 6 с; ошибки 9 с), ✕ — закрыть, одинаковые подряд не повторяются, не больше пяти. Итог
  каждой задачи — тоже уведомлением. Раньше надпись висела под заголовком, пока не сменишь папку.
- Ремукс: AVI — обычный вход (§2.9.10), отдельного профиля нет. На странице видны входящие форматы.

**Этап 8 — Windows (локально)** — всё из §6.2, включая команду `ani` (§4.12). Памятка для пользователя —
[docs/WINDOWS.md](WINDOWS.md): готовый `Anitools.exe` собирает CI (артефакт `anitools-win-x64` у каждого запуска на
Windows) или `tools/windows/publish.ps1` (один файл ≈150 МБ, .NET внутри). Сборка — ReadyToRun и без сжатия:
код уже скомпилирован, файлы читаются из exe напрямую; запуск ≈ втрое быстрее (замер: 1,9 → 0,6 с).

Подготовка к этапу 8 (облако):
- Перед каждым HLS, как `_setup_work_dir` оригинала, снимаются RAM-диски, оставшиеся от аварийно завершённого
  запуска (`ImDiskRamDisk.CleanupOrphansAsync` по файлу состояния), — в журнале задачи «Снят незакрытый RAM-диск R:».

Решения — пакет 2 (просьбы пользователя, 2026-10-08):
- «Сдвиг аудио» по умолчанию без перекодирования: `mkvmerge --sync -1:<мс>` сдвигает все дорожки без потерь, кодек
  и каналы те же (проверено на сгенерированных файлах: +1,5 с → старт 1,48 с; −1,5 с — начало срезается). Режим
  «в AAC, как в оригинале» остался (в настройках — по умолчанию для экрана).
- «Дорожки файла»: формат «через запятую» — русские озвучки, затем английская как «English», затем японская как
  «Original» (язык дорожки eng/jpn, без него — по тайтлу).
- «Субтитры»: у каждой дорожки отметки «надписи» и «сабы»; в каждой папке — одна дорожка, одна дорожка может идти в
  обе папки; всё за один запуск. Роли по умолчанию угадываются по тайтлам («надписи/signs», «полные/full/диалоги»).
- «Чистка стилей»: поиск по словам (через запятую) — показывает только подходящие значения, «Отметить найденные» /
  «Снять с найденных» (например, «демонобогский» → все «Имя (демонобогский)»).
- «Шрифты из видео»: архив всегда `fonts.zip`, даже для одной серии (как и в оригинале; добавлен тест).
- Shikimori: все найденные тайтлы (до 50) с постером, описанием, жанрами, студией, оценкой, статусом (§4.9).
- RAM-диск без запуска от администратора: без прав ImDisk создаёт устройство, но format.com его не форматирует —
  буква есть, файловой системы нет. Теперь диск создаёт и снимает помощник с правами администратора — тот же exe с
  ключом `--imdisk-helper <канал> <pid>`: Windows спрашивает разрешение один раз за запуск приложения (сразу по
  «Начать» в HLS), дальше без вопросов. Команды — по именованному каналу (create / remove / ping, буква A–Z, размер
  1–1024 ГБ), imdisk.exe — только из System32, PID проверяется с обеих сторон. Приложение закрылось или упало —
  помощник снимает свои диски и выходит. Остатки прошлых запусков снимаются, только если буква ещё есть в системе.
  Приложение запущено от администратора — как раньше, ImDisk напрямую.
- «Настройки» → «Программы»: по программам (FFmpeg — ffmpeg и ffprobe, MKVToolNix — mkvmerge и mkvextract, ImDisk)
  пути и кнопка «Установить» (что-то не найдено) / «Обновить» (всё есть — поставить последнюю), рядом «последняя: X»
  (узнаётся при первом открытии настроек). Ставится без прав администратора в `%LOCALAPPDATA%\anitools\tools\<программа>-<версия>`,
  пути сразу прописываются в настройки (остальные несохранённые поля не трогаются); ход скачивания и «Отмена» — в
  карточке. Источники: ffmpeg — gyan.dev `release-version` → `packages/ffmpeg-X-essentials_build.zip` + `.sha256`;
  MKVToolNix — `latest-release.xml` → `windows/releases/X/mkvtoolnix-64-bit-X.zip` + `sha256sums.txt` (zip, а не 7z:
  распаковка встроенная, без сторонней библиотеки). Архив сверяется по SHA-256, из него берутся только нужные exe
  (имена — из списка, не из архива), номер версии — только цифры с точками; прежняя версия удаляется, если её не
  держит идущая задача. ImDisk — драйвер: его подписанный установщик `imdiskinst.exe -y` запускается с правами
  администратора (Windows спросит; в конце установщик сам скажет, всё ли прошло), потом проверяется
  `System32\imdisk.exe`. Проверено на настоящих сайтах: MKVToolNix 102.0 — 5 с, ffmpeg 9.0.2 — 10 с.

**Этап 9 — запуск и установка на любом ПК** (просьбы пользователя, 2026-10-08):
1. **Консоль закрывается сама после запуска окна.** Сейчас `ani` освобождает консоль (`start`), но окно консоли
   остаётся. Варианты: в `ani.bat` — `exit` после `start` (закрывает cmd, где набрали `ani`, но не PowerShell: тот
   запускает батник отдельным cmd); со стороны приложения — подключиться к консоли родителя (`AttachConsole`) и
   «набрать» в ней `exit` (`WriteConsoleInput`) — оболочка (cmd или PowerShell) выходит сама, окно или вкладка
   Windows Terminal закрывается штатно. Не закрывать, если консоль открыта давно и в ней шла другая работа —
   галочка в настройках «Закрывать консоль после `ani`» (по умолчанию — да). Проверить: cmd, PowerShell, Windows
   Terminal, адресная строка проводника, Win+R.
2. **`ani` без батника, на любом ПК** (исследовать):
   - Приложение — GUI-программа: если `ani.exe` лежит в папке из PATH, cmd и PowerShell не ждут её, как со
     `start`, а текущая папка консоли достаётся программе — батник не нужен. Запущено под именем `ani` и без
     аргументов — открыть текущую папку.
   - Установщик (п.4) или кнопка в настройках «Включить команду ani»: папка программы в PATH пользователя
     (`HKCU\Environment`, без прав администратора, + `WM_SETTINGCHANGE`) и `ani.exe` — жёсткая ссылка на
     `Anitools.exe` (места не занимает; при обновлении пересоздаётся) или крошечный запускатель.
   - Дополнительно — `App Paths\ani.exe` в реестре пользователя: `ani` в Win+R и в адресной строке проводника
     (там текущей папкой должна стать открытая в проводнике — проверить).
   - Проверить и `%LOCALAPPDATA%\Microsoft\WindowsApps` (уже в PATH у всех): если обычный файл там работает, `ani`
     доступен сразу, даже в открытых консолях.
   - Позже — пакет winget (portable с командой `ani`: winget сам кладёт ссылку в `WinGet\Links` из PATH;
     проверить поля `Commands` / `PortableCommandAlias`).
3. **Субтитры: одна дорожка сразу в надписи и в сабы** — сделано в пакете 2 (см. выше).
4. **Релиз на GitHub и простая установка:**
   - Первый релиз — **версия 1.0.0** (просьба пользователя): тег `v1.0.0`, в окне и в свойствах exe — 1.0.0. До
     релиза сборки остаются 0.1.0 (`Directory.Build.props`).
   - Workflow на тег `v*`: сборка win-x64 (один файл, ReadyToRun, как в CI), версия — из тега, черновик релиза с
     `anitools-setup.exe`, `Anitools.exe` (без установки) и SHA-256; описание — из заголовков PR.
   - Установщик (Inno Setup, без прав администратора): `%LOCALAPPDATA%\Programs\anitools`, ярлык в «Пуске»,
     галочки «команда `ani`» (п.2) и «ярлык на рабочем столе», удаление через «Приложения». Настройки и логи
     пользователя при удалении не трогаются.
   - Первый запуск: чего нет из ffmpeg / MKVToolNix / ImDisk — плашка с кнопками «Установить» (пункт 9 пакета 2).
   - Обновления: раз в день — `releases/latest` на GitHub; новая версия — уведомление «Обновить» → скачать
     установщик и запустить тихо, приложение перезапустится.
   - Вопрос пользователю: подпись exe. Без неё Windows SmartScreen при первом запуске пишет «Windows защитила ваш
     компьютер» → «Подробнее» → «Выполнить в любом случае»; подпись платная (сертификат или Azure Trusted Signing).

---

## 8. Вопросы и ответы пользователя (2026-10-07)
1. **Апскейл в HLS** — HLS делается только из 4K-исходников, менять ничего не нужно: все 6 качеств, как в оригинале.
2. **Соседние скрипты** — переносим все из §2.9 (решение по объёму и порядку — за исполнителем; порядок — этап 6).
3. **Логи** — в `%LOCALAPPDATA%\anitools\logs\` (§2.8 #8).
4. **П.3, у части серий другой набор дорожек/внешних файлов** (§2.8 #6) — для такого набора отдельно свой список озвучек
   и выбор порядка (как раскладки в HLS), см. §4.5. Серии не пропускаются.
5. **Команда `ani`** — как сейчас: в консоли в папке с сериями открывает приложение с этой папкой (§4.12).
