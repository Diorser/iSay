$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskBuild = Join-Path $taskRoot 'artifacts/组合输入安装器/构建'
$taskOutput = Join-Path $taskRoot 'artifacts/组合输入安装器'
$taskCmake = 'C:/Program Files/CMake/bin/cmake.exe'
& $taskCmake -S (Join-Path $taskRoot 'src/iSay.TextService.Setup') -B $taskBuild -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw '组合输入安装器配置失败。' }
& $taskCmake --build $taskBuild --config Release
if ($LASTEXITCODE -ne 0) { throw '组合输入安装器生成失败。' }
Copy-Item -LiteralPath (Join-Path $taskBuild 'Release/iSay.TextService.Setup.exe') -Destination $taskOutput
Get-FileHash -LiteralPath (Join-Path $taskOutput 'iSay.TextService.Setup.exe') -Algorithm SHA256
