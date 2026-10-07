# Сборка anitools в один Anitools.exe для Windows (docs/PLAN.md §6.2): .NET-рантайм внутри, ставить .NET не нужно.
# Запуск из любой папки (нужен .NET 10 SDK):
#   powershell -ExecutionPolicy Bypass -File tools\windows\publish.ps1
#   powershell -ExecutionPolicy Bypass -File tools\windows\publish.ps1 -Output D:\Apps\anitools
# Перед сборкой закройте anitools: открытый Anitools.exe не перезаписать.
param([string]$Output = "C:\personal\Apps\anitools")

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\..\src\Anitools.App"

dotnet publish $project -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Отладочные символы из пакетов Skia/HarfBuzz (≈100 МБ) запуску не нужны
Get-ChildItem $Output -Filter *.pdb | Remove-Item
Write-Host "Готово: $(Join-Path $Output 'Anitools.exe')"
