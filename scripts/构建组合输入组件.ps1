param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('x64', 'x86')][string]$Architecture = 'x64',
    [string]$OutputRoot = ''
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $taskRoot 'artifacts/组合输入组件' }
$taskOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$taskBuild = Join-Path $taskOutputRoot "构建-$Architecture"
$taskOutput = Join-Path $taskOutputRoot $Architecture
New-Item -ItemType Directory -Force -Path $taskBuild, $taskOutput | Out-Null
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) { $cmake = 'C:/Program Files/CMake/bin/cmake.exe' }
$taskPlatform = if ($Architecture -eq 'x86') { 'Win32' } else { 'x64' }
& $cmake -S (Join-Path $taskRoot 'src/iSay.TextService') -B $taskBuild -G 'Visual Studio 17 2022' -A $taskPlatform -DISAY_TSF_REF_TRACE=OFF -DISAY_TSF_TEST_DELAY_PROXY=OFF
if ($LASTEXITCODE -ne 0) { throw '组合输入组件配置失败。' }
& $cmake --build $taskBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw '组合输入组件构建失败。' }
Copy-Item -LiteralPath (Join-Path $taskBuild "$Configuration/iSay.TextService.dll") -Destination $taskOutput
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $taskOutput 'iSay.TextService.dll')
