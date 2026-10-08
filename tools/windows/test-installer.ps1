# Проверка установщика на чистой Windows (CI): тихая установка → команда ani из cmd и из PowerShell → удаление.
# ani должен открыть окно anitools в текущей папке консоли (папка попадает в «недавние» в settings.json),
# а консоль — закрыться сама. Запуск: pwsh tools\windows\test-installer.ps1 -Setup artifacts\installer\anitools-setup.exe
param([Parameter(Mandatory)] [string]$Setup)

$ErrorActionPreference = "Stop"
$app = Join-Path $env:LOCALAPPDATA "Programs\anitools"
$settings = Join-Path $env:APPDATA "anitools\settings.json"

function Fail([string]$message) {
    Write-Host "::error::$message"
    exit 1
}

function Stop-Anitools {
    Get-Process ani, Anitools -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
}

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 500
    }
    Fail "Не дождались: $what"
}

# Консоль с командой ani в папке: ждём, что консоль закроется сама и окно откроется на этой папке
function Test-Ani([string]$shell, [string[]]$arguments, [string]$folder) {
    Stop-Anitools
    Remove-Item $settings -ErrorAction SilentlyContinue
    $console = Start-Process $shell -ArgumentList $arguments -WorkingDirectory $folder -PassThru
    Wait-Until {
        (Test-Path $settings) -and ((Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json).recentFolders | Select-Object -First 1) -eq $folder
    } 60 "$shell`: anitools открылся в папке «$folder»"
    if (-not $console.WaitForExit(30000)) {
        $console.Kill()
        if (Test-Path $env:ANITOOLS_ANI_TRACE) { Get-Content $env:ANITOOLS_ANI_TRACE | ForEach-Object { Write-Host "  ani: $_" } }
        Fail "$shell`: консоль после ani не закрылась"
    }
    if (-not (Get-Process ani -ErrorAction SilentlyContinue)) {
        Fail "$shell`: окно anitools закрылось вместе с консолью"
    }
    Write-Host "✓ ${shell}: ani открыл «$folder», консоль закрылась сама"
    Stop-Anitools
}

# 1. Тихая установка с командой ani
$install = Start-Process $Setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=anicommand", "/nolaunch=1" -Wait -PassThru
if ($install.ExitCode -ne 0) { Fail "Установщик вернул код $($install.ExitCode)" }
foreach ($file in "Anitools.exe", "ani.exe") {
    if (-not (Test-Path (Join-Path $app $file))) { Fail "После установки нет $app\$file" }
}
$links = fsutil hardlink list (Join-Path $app "ani.exe")
if (-not ($links -match "Anitools\.exe")) { Fail "ani.exe — не жёсткая ссылка на Anitools.exe: $links" }
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (-not (($userPath -split ";") -contains $app)) { Fail "Папки программы нет в PATH пользователя: $userPath" }
if (-not (Test-Path (Join-Path ([Environment]::GetFolderPath("Programs")) "anitools.lnk"))) { Fail "Нет ярлыка в «Пуске»" }
Write-Host "✓ установка: $app, ani.exe — ссылка на Anitools.exe, папка в PATH, ярлык в «Пуске»"

# 2. ani в папке с пробелами и кириллицей; эта консоль PATH из реестра ещё не видит — как новая консоль
$env:Path = "$app;$env:Path"
$env:ANITOOLS_ANI_TRACE = Join-Path $env:RUNNER_TEMP "ani-trace.txt"
$folder = (New-Item -ItemType Directory -Force (Join-Path $env:RUNNER_TEMP "ani тест папка")).FullName
Test-Ani "cmd.exe" @("/k", "ani") $folder
Test-Ani "powershell.exe" @("-NoExit", "-NoProfile", "-Command", "ani") $folder

# 3. Удаление: файлы и PATH чистые (удаляльщик перезапускает себя из TEMP — ждём, пока файлы исчезнут)
Start-Process (Join-Path $app "unins000.exe") -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait
Wait-Until { -not (Test-Path (Join-Path $app "Anitools.exe")) -and -not (Test-Path (Join-Path $app "ani.exe")) } 120 "удаление файлов"
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (($userPath -split ";") -contains $app) { Fail "После удаления папка осталась в PATH: $userPath" }
Write-Host "✓ удаление: файлов и записи в PATH нет"
