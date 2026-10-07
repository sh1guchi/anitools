# anitools

Перенос anitools (Python + rich, консоль) на C# с GUI на Avalonia.

- План и полное описание поведения: [docs/PLAN.md](docs/PLAN.md)
- Оригинал (не менять): [reference/python/](reference/python/)

## Структура

| Папка | Что там |
|---|---|
| `src/Anitools.Core` | вся логика, без UI (.NET 10) |
| `src/Anitools.App` | GUI на Avalonia, собирается в `Anitools.exe` |
| `tests/Anitools.Core.Tests` | тесты ядра + golden-эталоны из оригинала (`Golden/`) |
| `tests/Anitools.App.Tests` | headless-тесты окон и скриншоты |
| `tools/gen_golden.py` | снимает эталоны с `reference/python/anitools.py` |

## Сборка и тесты

Нужен .NET 10 SDK (и Python 3.11+ — только для перегенерации эталонов).

```bash
dotnet build
dotnet test
dotnet run --project src/Anitools.App -- "D:\anime\Папка с сериями"
```

Скриншоты экранов тесты пишут в `artifacts/screenshots/` (не в git). Обновить те, что лежат
в [docs/screenshots/](docs/screenshots/):

```bash
ANITOOLS_UPDATE_SCREENSHOTS=1 dotnet test tests/Anitools.App.Tests
```

Эталоны:

```bash
pip install fonttools==4.60.1        # нужен для эталонов шрифтов
python3 tools/gen_golden.py          # перегенерировать
python3 tools/gen_golden.py --check  # проверить, что актуальны
```

## Статус

- [x] Этап 1 — скелет решения, пустое окно, golden-эталоны
- [x] Этап 2 — чистая логика (anitomy, номера серий, названия…)
- [x] Этап 3 — запуск ffmpeg/MKVToolNix, разбор медиа, операции «Только видео», «Только аудио», «Обработка аудио», «Субтитры», «Ремукс»
- [x] Этап 4 — переименование по номеру серии, поиск названия на Shikimori, откат переименования
- [x] Этап 5 — HLS: 360p…4K в zip + озвучки, подбор качества, резюм, RAM-диск ImDisk
- [x] Этап 6 — доп. инструменты: сборка озвучек в .mka, шрифты для .ass и из видео, хардсаб, сдвиги, чистка .ass, перекодирование аудио
- [ ] Этапы 7–8 — GUI и проверка на Windows, см. [docs/PLAN.md §7](docs/PLAN.md)
