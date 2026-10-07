@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ============================================
echo   Конвертер M2TS в MKV без перекодирования
echo ============================================
echo.

REM Проверяем наличие FFmpeg
where ffmpeg >nul 2>&1
if errorlevel 1 (
    echo ОШИБКА: FFmpeg не найден в системе!
    echo Пожалуйста, установите FFmpeg и добавьте его в PATH
    echo Скачать можно с: https://ffmpeg.org/download.html
    pause
    exit /b 1
)

REM Переменные для подсчета
set /a "total_files=0"
set /a "processed_files=0"
set /a "skipped_files=0"
set /a "error_files=0"

echo Поиск M2TS файлов в текущей папке...
echo.

REM Считаем общее количество M2TS файлов
for %%f in (*.m2ts *.M2TS) do (
    set /a "total_files+=1"
)

if %total_files%==0 (
    echo M2TS файлы не найдены в текущей папке!
    echo Поддерживаемые расширения: .m2ts, .M2TS
    pause
    exit /b 1
)

echo Найдено M2TS файлов: %total_files%
echo.

REM Обрабатываем каждый M2TS файл
for %%f in (*.m2ts *.M2TS) do (
    set "input_file=%%f"
    set "output_file=%%~nf.mkv"
    
    echo [!processed_files!/!total_files!] Обрабатываем: !input_file!
    
    REM Проверяем, не существует ли уже выходной файл
    if exist "!output_file!" (
        echo   Файл "!output_file!" уже существует - пропускаем
        set /a "skipped_files+=1"
        echo.
    ) else (
        REM Выполняем конвертацию без перекодирования (ремуксинг)
        REM Для PCM Blu-ray конвертируем аудио в FLAC (без потери качества)
        REM Копируем видео и субтитры, конвертируем аудио в FLAC
        echo   Запуск FFmpeg...
        ffmpeg -i "!input_file!" -map 0:v -map 0:a -map 0:s? -c:v copy -c:a flac -c:s copy "!output_file!" -y
        
        if errorlevel 1 (
            echo   ОШИБКА: Не удалось конвертировать !input_file!
            set /a "error_files+=1"
            if exist "!output_file!" (
                echo   Удаляем поврежденный файл...
                del "!output_file!"
            )
        ) else (
            REM Проверяем размер выходного файла
            for %%I in ("!output_file!") do set "output_size=%%~zI"
            if !output_size! LEQ 0 (
                echo   ОШИБКА: Создан пустой файл!
                set /a "error_files+=1"
                if exist "!output_file!" del "!output_file!"
            ) else (
                echo   Успешно: !output_file!
                set /a "processed_files+=1"
                
                REM Показываем размер файлов
                for %%I in ("!input_file!") do set "input_size=%%~zI"
                
                set /a "input_size_mb=!input_size!/1024/1024"
                set /a "output_size_mb=!output_size!/1024/1024"
                
                echo   Размер: !input_size_mb! MB ^-> !output_size_mb! MB
            )
        )
        echo.
    )
)

echo ============================================
echo   Обработка завершена!
echo ============================================
echo Всего файлов: %total_files%
echo Обработано: %processed_files%
echo Пропущено: %skipped_files%
echo Ошибок: %error_files%
echo.

pause
