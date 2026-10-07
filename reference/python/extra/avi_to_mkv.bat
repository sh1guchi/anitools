@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo Remux AVI to MKV
echo ==================

set count=0
set errors=0

for %%f in (*.avi) do (
    set /a count+=1
    set "input=%%f"
    set "output=%%~nf.mkv"

    echo [%%f] -^> [%%~nf.mkv]
    ffmpeg -fflags +genpts -i "%%f" -c copy -avoid_negative_ts make_zero "%%~nf.mkv" -y -loglevel warning

    if !errorlevel! neq 0 (
        echo   ERROR: %%f
        set /a errors+=1
    ) else (
        echo   Done.
    )
)

echo.
if !count! equ 0 (
    echo No .avi files found in current folder.
) else (
    echo Files processed: !count!
    if !errors! gtr 0 (
        echo Errors: !errors!
    ) else (
        echo All done!
    )
)

pause
