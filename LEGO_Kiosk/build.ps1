param(
    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'
$projectDir = (Resolve-Path $PSScriptRoot).Path
$workspaceDir = (Resolve-Path (Join-Path $projectDir '..')).Path
$appDir = Join-Path $projectDir 'app'
$distDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $projectDir $OutputDirectory
}
$buildDir = Join-Path $projectDir 'build'

$localPython = Join-Path $workspaceDir '.tools\python311\python.exe'
if (Test-Path $localPython) {
    $pythonExe = $localPython
    $pythonPrefix = @()
} elseif (Get-Command py -ErrorAction SilentlyContinue) {
    $pythonExe = (Get-Command py).Source
    $pythonPrefix = @('-3.11')
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
    $pythonExe = (Get-Command python).Source
    $pythonPrefix = @()
} else {
    throw 'Установите Python 3.11 x64, затем выполните pip install -r requirements-build.txt.'
}

New-Item -ItemType Directory -Force -Path $distDir, $buildDir | Out-Null
$common = @('-m', 'PyInstaller', '--clean', '--noconfirm', '--onefile', '--windowed',
    '--specpath', (Join-Path $buildDir 'specs'))

Push-Location $projectDir
try {
    & $pythonExe @pythonPrefix @common `
        '--name' 'ScaleSetup' `
        '--distpath' $distDir `
        '--workpath' (Join-Path $buildDir 'setup') `
        '--collect-all' 'bleak_winrt' `
        (Join-Path $appDir 'scale_setup.py')
    if ($LASTEXITCODE -ne 0) { throw "Сборка ScaleSetup завершилась с кодом $LASTEXITCODE." }

    & $pythonExe @pythonPrefix @common `
        '--name' 'LegoScaleKiosk' `
        '--distpath' $distDir `
        '--workpath' (Join-Path $buildDir 'kiosk') `
        '--collect-all' 'bleak_winrt' `
        '--add-data' "$appDir\data;data" `
        (Join-Path $appDir 'vesLego.py')
    if ($LASTEXITCODE -ne 0) { throw "Сборка LegoScaleKiosk завершилась с кодом $LASTEXITCODE." }
} finally {
    Pop-Location
}

Write-Host "Готово: $distDir"
