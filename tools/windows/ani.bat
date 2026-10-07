@echo off
rem ani — открыть anitools в текущей папке (или в указанной: ani "D:\anime\X")
rem Ставится в C:\personal\Scripts (папка в PATH) вместо ani.bat Python-версии (её — в ani-py.bat), docs/PLAN.md §4.12
set "APP=C:\personal\Apps\anitools\Anitools.exe"
if "%~1"=="" (start "" "%APP%" "%CD%\.") else (start "" "%APP%" %*)
