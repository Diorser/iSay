param(
    [string]$OutputDirectory = '',
    [string]$IsccPath = '',
    [string]$RuntimeInstaller = '',
    [string]$ReleaseConfiguration = '',
    [string]$MinimumRuntimeVersion = '10.0.8',
    [string[]]$DownloadBaseUrls = @(),
    [long]$Sequence = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(),
    [string]$KeyId = '',
    [string]$NotesZh = 'Windows 安装、更新与中英文界面。',
    [string]$NotesEn = 'Windows installer, updates and bilingual interface.',
    [switch]$LayoutOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/iSay.Desktop/iSay.Desktop.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$compositionVersion = [string]$project.Project.PropertyGroup.CompositionVersion
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $compositionVersion -notmatch '^\d+\.\d+\.\d+$') { throw '源码产品或组件版本无效。' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/Windows发行/' + $version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = Assert-ReleasePlainPath $OutputDirectory
if (Test-Path -LiteralPath $output) { throw '发行输出目录已存在，请使用新的目录以保留已有产物。' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$layout = Join-Path $output '布局'
$updates = Join-Path $output '应用更新包'
New-Item -ItemType Directory -Path $layout,$updates -Force | Out-Null
$publishArguments = @('-c','Release','-r','win-x64','--self-contained','false','-p:DebugType=None','-p:DebugSymbols=false','--nologo')
& dotnet publish (Join-Path $projectRoot 'src/iSay.Desktop/iSay.Desktop.csproj') @publishArguments -o $layout
if ($LASTEXITCODE -ne 0) { throw '桌面程序发行构建失败。' }
& dotnet publish (Join-Path $projectRoot 'src/iSay.Updater/iSay.Updater.csproj') @publishArguments -o $layout
if ($LASTEXITCODE -ne 0) { throw '更新器发行构建失败。' }
if ($ReleaseConfiguration) {
    $configuration = Get-Content -LiteralPath $ReleaseConfiguration -Raw | ConvertFrom-Json
    if (@($configuration.manifestUrls).Count -eq 0 -or @($configuration.publicKeys.PSObject.Properties).Count -eq 0) { throw '正式发布配置必须同时包含清单地址与公钥。' }
    Copy-Item -LiteralPath $ReleaseConfiguration -Destination (Join-Path $layout 'release.json') -Force
}
$frozen = Get-Content -LiteralPath (Join-Path $PSScriptRoot '原生发行冻结清单.json') -Raw -Encoding utf8 | ConvertFrom-Json
$mapping = [ordered]@{ runtime='native'; 'smart-runtime'='smart-runtime'; 'personalization-runtime'='personalization-runtime'; 'dictionary-runtime'='dictionary-runtime' }
foreach ($pair in $mapping.GetEnumerator()) {
    Copy-FrozenReleaseRuntime (Join-Path $projectRoot ('external/' + $pair.Value)) (Join-Path $layout $pair.Key) $frozen.($pair.Key)
}
$sourceDictionary = Join-Path $projectRoot 'external/dictionary-runtime.冻结清单.json'
$embeddedDictionary = Join-Path $projectRoot 'src/iSay.Voice/Assets/原生词典冻结清单.json'
if ((Get-FileHash -LiteralPath $sourceDictionary).Hash -ne (Get-FileHash -LiteralPath $embeddedDictionary).Hash) { throw '词典源冻结清单与程序集嵌入清单不同。' }
$composition = Join-Path $layout ('composition/' + $compositionVersion)
foreach ($architecture in @('x64','x86')) {
    $target = Join-Path $composition $architecture
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot ('artifacts/组合输入组件/' + $architecture + '/iSay.TextService.dll')) -Destination $target
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/iSay.Desktop/Assets/iSay.ico') -Destination (Join-Path $composition 'iSay.ico')
Copy-Item -LiteralPath (Join-Path $projectRoot 'artifacts/组合输入安装器/iSay.TextService.Setup.exe') -Destination (Join-Path $layout 'composition/iSay.TextService.Setup.exe')
$totalBytes = (@(Get-ReleasePlainFiles $layout) | Measure-Object Length -Sum).Sum
[ordered]@{ application='iSay'; version=$version; compositionVersion=$compositionVersion; architecture='win-x64'; minimumDesktopRuntime=$MinimumRuntimeVersion; expandedBytes=$totalBytes; containsPersonalData=$false; publicRedistributionApproved=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $layout 'product.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '第三方依赖声明.txt') -Destination $layout
$releaseRequest = [ordered]@{ LayoutDirectory=$layout; OutputDirectory=$updates; Version=$version; Sequence=$Sequence; KeyId=$KeyId; DownloadBaseUrls=@($DownloadBaseUrls); NotesZh=$NotesZh; NotesEn=$NotesEn }
$requestPath = Join-Path $output '发行请求.json'
$releaseRequest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $requestPath -Encoding utf8
& dotnet run --project (Join-Path $projectRoot 'src/iSay.ReleaseTool/iSay.ReleaseTool.csproj') -c Release -- $requestPath
if ($LASTEXITCODE -ne 0) { throw '应用更新包生成失败。' }
$report = [ordered]@{ Version=$version; CompositionVersion=$compositionVersion; OutputDirectory=$output; Layout=$layout; LayoutBytes=(@(Get-ReleasePlainFiles $layout)|Measure-Object Length -Sum).Sum; UpdateDirectory=$updates; FullInstaller=$null; Runtime=$null; Published=$false }
if (-not $LayoutOnly) {
    if (-not $RuntimeInstaller) { $runtime = & (Join-Path $PSScriptRoot '准备桌面运行时.ps1') -MinimumVersion $MinimumRuntimeVersion; $RuntimeInstaller=$runtime.Path }
    else {
        # 离线指定路径仍要求来自已校验的微软元数据缓存，不能绕过哈希与签名。
        $cache = Split-Path ([IO.Path]::GetFullPath($RuntimeInstaller)) -Parent
        $runtime = Get-Content -LiteralPath (Join-Path $cache '运行时来源.json') -Raw | ConvertFrom-Json
        if ([IO.Path]::GetFullPath($runtime.Path) -ine [IO.Path]::GetFullPath($RuntimeInstaller)) { throw '离线运行时路径与来源记录不符。' }
        Test-ReleaseRuntimeHash $RuntimeInstaller $runtime.SHA512
        Assert-MicrosoftReleaseSignature $RuntimeInstaller
    }
    if (-not $IsccPath) { $IsccPath = Join-Path $projectRoot 'artifacts/发行工具/InnoSetup.6.7.3/tools/ISCC.exe' }
    if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw '未找到 Inno Setup 编译器。运行 准备发行工具.ps1，或指定 -IsccPath。布局与更新包已保留。' }
    Assert-InnoCompilerSignature $IsccPath
    $chinese = Join-Path $PSScriptRoot '简体中文安装语言.isl'
    $vcRuntimes=@(& (Join-Path $PSScriptRoot '准备VC运行时.ps1'))
    $vcX64=@($vcRuntimes|Where-Object Architecture -eq 'x64')[0]
    $vcX86=@($vcRuntimes|Where-Object Architecture -eq 'x86')[0]
    & $IsccPath '/Qp' ('/DLayout=' + $layout) ('/DProductVersion=' + $version) ('/DCompositionVersion=' + $compositionVersion) ('/DOutputDirectory=' + $output) ('/DRuntimeInstaller=' + $RuntimeInstaller) ('/DRuntimeSHA256=' + $runtime.SHA256.ToLowerInvariant()) ('/DMinimumRuntimeVersion=' + $MinimumRuntimeVersion) ('/DTsfX64SHA256=' + (Get-FileHash -LiteralPath (Join-Path $composition 'x64/iSay.TextService.dll')).Hash.ToLowerInvariant()) ('/DTsfX86SHA256=' + (Get-FileHash -LiteralPath (Join-Path $composition 'x86/iSay.TextService.dll')).Hash.ToLowerInvariant()) ('/DChineseLanguageFile=' + $chinese) ('/DVcX64Installer='+$vcX64.Path) ('/DVcX86Installer='+$vcX86.Path) ('/DVcX64Version='+$vcX64.Version) ('/DVcX86Version='+$vcX86.Version) ('/DVcX64SHA256='+$vcX64.SHA256.ToLowerInvariant()) ('/DVcX86SHA256='+$vcX86.SHA256.ToLowerInvariant()) (Join-Path $projectRoot 'scripts/安装/iSay安装包.iss')
    if ($LASTEXITCODE -ne 0) { throw '完整安装包编译失败，布局与日志已保留。' }
    $report.FullInstaller = Join-Path $output ('iSay-' + $version + '-win-x64-full.exe')
    $report.Runtime = $runtime.Version
    $report.VcRuntimes = @($vcRuntimes|Select-Object Architecture,Version)
    $report.InstallerBytes = (Get-Item -LiteralPath $report.FullInstaller).Length
    $report.InstallerSHA256 = (Get-FileHash -LiteralPath $report.FullInstaller).Hash
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output '构建结果.json') -Encoding utf8
[pscustomobject]$report
