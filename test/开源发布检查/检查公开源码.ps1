param([string]$RepositoryRoot = '', [string]$ReportDirectory = '')
$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $PSScriptRoot ('检查产物/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$sourceDirectories = @('src/iSay.Desktop', 'src/iSay.Voice', 'src/iSay.Updates', 'src/iSay.Updater',
    'src/iSay.ReleaseTool', 'src/iSay.TextService', 'src/iSay.TextService.Setup')
$testDirectories = @('test/更新安全回归', 'test/发行发布预检回归', 'test/客户端核心测试', 'test/配置恢复回归测试')
$scriptFiles = @('scripts/构建组合输入组件.ps1', 'scripts/构建组合输入安装器.ps1',
    'scripts/安装/iSay安装包.iss', 'scripts/发布/构建Windows发行.ps1', 'scripts/发布/发行公共函数.ps1',
    'scripts/发布/原生发行冻结清单.json', 'scripts/发布/第三方依赖声明.txt', 'scripts/发布/简体中文安装语言.isl',
    'scripts/发布/准备发行工具.ps1', 'scripts/发布/准备桌面运行时.ps1', 'scripts/发布/准备VC运行时.ps1',
    'scripts/发布/发布Windows版本.ps1', 'scripts/发布/发布完整安装包.ps1', 'scripts/发布/下载GitHub发行资产.ps1', 'scripts/发布/Cloudflare/worker.mjs',
    'scripts/发布/Cloudflare/wrangler.jsonc', 'scripts/发布/Cloudflare/使用说明.md',
    'test/发行打包回归/Worker回归.mjs', 'test/开源发布检查/检查公开源码.ps1', 'test/开源发布检查/验证独立编译.ps1',
    'test/安装包下载发布回归/安装包下载发布回归.ps1', 'test/官网下载入口回归/官网入口回归.mjs')
$sourceExtensions = @('.cs','.cpp','.h','.rc','.def','.csproj','.xaml','.manifest','.md','.json','.jsonc',
    '.ps1','.py','.mjs','.js','.iss','.isl','.txt','.toml','.svg','.png','.ico')
$publicFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Get-RelativeSourceFiles([string]$Directory) {
    $absolute = Join-Path $repository $Directory
    if (-not (Test-Path -LiteralPath $absolute -PathType Container)) { return }
    & rg --files $absolute -g '!**/bin/**' -g '!**/obj/**' -g '!**/__pycache__/**' |
        ForEach-Object {
            if ([IO.Path]::GetExtension($_) -in $sourceExtensions -or [IO.Path]::GetFileName($_) -eq 'CMakeLists.txt') {
                [IO.Path]::GetRelativePath($repository, $_).Replace('\','/')
            }
        }
}
foreach ($directory in $sourceDirectories) {
    foreach ($file in @(Get-RelativeSourceFiles $directory)) { [void]$publicFiles.Add($file) }
}
foreach ($directory in $testDirectories) {
    # 只纳入直接源码，不递归带入测试生成的账号、录音、日志、密钥和证据。
    Get-ChildItem -LiteralPath (Join-Path $repository $directory) -File |
        Where-Object Extension -in @('.cs','.csproj') |
        ForEach-Object { [void]$publicFiles.Add([IO.Path]::GetRelativePath($repository, $_.FullName).Replace('\','/')) }
}
foreach ($file in $scriptFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository $file) -PathType Leaf)) { throw ('公开候选缺少必要文件：' + $file) }
    [void]$publicFiles.Add($file)
}
$patterns = [ordered]@{
    '疑似凭据或私钥格式' = '(?i)(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{24,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)'
    '个人绝对路径' = '(?i)(?:[A-Z]:[\\/]+Users[\\/]+(?!Public(?:[\\/]|$))[^\\/\s"'']+|/home/(?!runner/)[A-Za-z0-9._-]+(?:/|\b))'
    '疑似凭据字面量' = '(?im)["'']?(?:ApiKey|ApiToken|AccessToken|AccessKey|SecretKey|ClientSecret|Password|Passwd)["'']?\s*[:=]\s*["''][A-Za-z0-9_+/=-]{24,}["'']'
    '带身份URL待人工复核' = '(?i)https?://[^\s/"'']+:[^\s/@"'']+@|https?://[^\s"'']+[?&](?:token|api_key|secret|password|access_token)=[^&\s"'']+'
    '联系信息待许可核对' = '(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\.(?:com|org|net|cn|io|dev)\b'
}
$riskFiles = [Collections.Generic.List[object]]::new()
$scanFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($directory in @('src','scripts','docs')) {
    foreach ($file in @(Get-RelativeSourceFiles $directory)) { [void]$scanFiles.Add($file) }
}
foreach ($directory in @((Join-Path $repository 'test')) + @(Get-ChildItem -LiteralPath (Join-Path $repository 'test') -Directory | ForEach-Object FullName)) {
    Get-ChildItem -LiteralPath $directory -File | Where-Object Extension -in @('.cs','.cpp','.h','.csproj','.ps1','.py','.mjs','.js','.md') |
        ForEach-Object { [void]$scanFiles.Add([IO.Path]::GetRelativePath($repository, $_.FullName).Replace('\','/')) }
}
foreach ($file in $scanFiles) {
    if ([IO.Path]::GetExtension($file) -in @('.ico','.png')) { continue }
    $content = [IO.File]::ReadAllText((Join-Path $repository $file))
    foreach ($pattern in $patterns.GetEnumerator()) {
        if ([regex]::IsMatch($content, $pattern.Value)) {
            # 只保留路径和风险类型，不保存命中行、疑似密钥或用户内容。
            $riskFiles.Add([ordered]@{ Path = $file; RiskType = $pattern.Key })
        }
    }
}
$whitelist = @($publicFiles | Sort-Object)
$riskFiles | Sort-Object Path,RiskType | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $ReportDirectory '风险路径.json') -Encoding utf8
$whitelist | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory '公开候选白名单.json') -Encoding utf8
$hashes = [ordered]@{}
foreach ($file in $whitelist) { $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $repository $file) -Algorithm SHA256).Hash }
$hashes | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $ReportDirectory '公开候选哈希.json') -Encoding utf8
$blockingRisks = @($riskFiles | Where-Object { $publicFiles.Contains($_.Path) -and $_.RiskType -ne '联系信息待许可核对' })
$summary = [ordered]@{
    ScannedSourceFiles = $scanFiles.Count; PublicCandidateFiles = $whitelist.Count; BlockingCandidateRisks = $blockingRisks.Count
    Excluded = @('external/**','artifacts/**','**/bin/**','**/obj/**','test/**/运行产物/**','test/**/.runtime/**',
        'test/**/证据/**','test/**/检查产物/**','test/**/编译验证/**','docs/**','scripts/识别适配器/**',
        'scripts/发布/LiteMonitor发布脚本去敏参考.ps1.txt','scripts/发布/复制发布脚本参考.ps1','src/iSay.App/**','src/iSay.Core/**')
    SourcePreviewOnly = $true; NativeRedistributionApproved = $false; ActualPublicationPerformed = $false
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory '检查汇总.json') -Encoding utf8
if ($blockingRisks.Count -gt 0) { throw '公开候选仍有风险，请只查看风险路径报告并人工处理，不要打印命中内容。' }
Write-Host ('公开源码边界检查通过；候选 ' + $whitelist.Count + ' 个文件，扫描 ' + $scanFiles.Count + ' 个源码文件。')
Write-Host ('报告目录：' + $ReportDirectory)
