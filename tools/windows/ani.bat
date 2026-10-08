@echo off
rem ani — открыть anitools в текущей папке (или в указанной: ani "D:\anime\X")
rem Для anitools без установщика: положить в папку из PATH, путь к Anitools.exe — ниже (docs/PLAN.md §4.12)
set "APP=C:\personal\Apps\anitools\Anitools.exe"
if "%~1"=="" (start "" "%APP%" "%CD%\.") else (start "" "%APP%" %*)
