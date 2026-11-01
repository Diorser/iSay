param([string]$WhitelistPath = '', [string]$RepositoryRoot = '', [switch]$BuildNative)
$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not $WhitelistPath) {
    $latest = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '检查产物') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { throw '请先运行公开源码检查，或显式提供白名单路径。' }
    $WhitelistPath = Join-Path $latest.FullName '公开候选白名单.json'
}
$files = @(Get-Content -LiteralPath $WhitelistPath -Raw | ConvertFrom-Json)
$hashPath = Join-Path (Split-Path ([IO.Path]::GetFullPath($WhitelistPath)) -Parent) '公开候选哈希.json'
if (-not (Test-Path -LiteralPath $hashPath)) { throw '白名单缺少对应的哈希记录，请重新运行公开源码检查。' }
$hashes = Get-Content -LiteralPath $hashPath -Raw | ConvertFrom-Json
$evidence = Join-Path $PSScriptRoot ('编译验证/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$staging = Join-Path $evidence '源码'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
foreach ($file in $files) {
    if ($file -match '(?:^|/)(?:external|artifacts|bin|obj)(?:/|$)' -or $file -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($file)) {
        throw '白名单包含越界或非源码文件。'
    }
    $source = [IO.Path]::GetFullPath((Join-Path $repository $file))
    if (-not $source.StartsWith($repository.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '白名单源文件越界。' }
    if (-not $hashes.$file -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hashes.$file) { throw ('审查后源码已改变，请重新检查：' + $file) }
    $target = Join-Path $staging $file
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$steps = [Collections.Generic.List[object]]::new()
function Invoke-CompileStep([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $log = Join-Path $evidence ($Name + '.log')
    & $Executable @Arguments *> $log
    $exit = $LASTEXITCODE
    $steps.Add([ordered]@{ Step=$Name; ExitCode=$exit; Passed=($exit -eq 0) })
    if ($exit -ne 0) { throw ('独立源码验证失败：' + $Name + '；请查看本地构建日志。') }
    Write-Host ('通过：' + $Name)
}
Push-Location $staging
try {
    Invoke-CompileStep '桌面源码构建' 'dotnet' @('build','src/iSay.Desktop/iSay.Desktop.csproj','-c','Release','--nologo')
    Invoke-CompileStep '发行工具源码构建' 'dotnet' @('build','src/iSay.ReleaseTool/iSay.ReleaseTool.csproj','-c','Release','--nologo')
    Invoke-CompileStep '更新器源码构建' 'dotnet' @('build','src/iSay.Updater/iSay.Updater.csproj','-c','Release','--nologo')
    foreach ($project in @('更新安全回归','发行发布预检回归','客户端核心测试','配置恢复回归测试')) {
        Invoke-CompileStep $project 'dotnet' @('run','--project',('test/' + $project + '/' + $project + '.csproj'),'-c','Release')
    }
    Invoke-CompileStep 'Worker本地回归' 'node' @('test/发行打包回归/Worker回归.mjs')
    if ($BuildNative) {
        $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
        if (-not $cmake) { $cmake = 'C:/Program Files/CMake/bin/cmake.exe' }
        foreach ($architecture in @('x64','Win32')) {
            $build = Join-Path $evidence ('原生-' + $architecture)
            Invoke-CompileStep ('TSF配置-' + $architecture) $cmake @('-S','src/iSay.TextService','-B',$build,'-G','Visual Studio 17 2022','-A',$architecture)
            Invoke-CompileStep ('TSF构建-' + $architecture) $cmake @('--build',$build,'--config','Release')
        }
        $build = Join-Path $evidence '原生维护程序'
        Invoke-CompileStep '维护程序配置' $cmake @('-S','src/iSay.TextService.Setup','-B',$build,'-G','Visual Studio 17 2022','-A','x64')
        Invoke-CompileStep '维护程序构建' $cmake @('--build',$build,'--config','Release')
    }
} finally {
    Pop-Location
    [ordered]@{ CopiedSourceFiles=$files.Count; HasExternalDirectory=(Test-Path -LiteralPath (Join-Path $staging 'external'));
        HasArtifactsDirectory=(Test-Path -LiteralPath (Join-Path $staging 'artifacts')); Steps=$steps.ToArray();
        NativeExecutionPerformed=$false; ActualInstallationPerformed=$false; ActualPublicationPerformed=$false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence '结果.json') -Encoding utf8
}
Write-Host ('独立源码编译与纯本地回归完成，证据：' + $evidence)
