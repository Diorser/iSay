$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scriptPath = Join-Path $root 'scripts/发布/发布完整安装包.ps1'
$fixture = Join-Path $PSScriptRoot ('.runtime/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$installer = Join-Path $fixture 'iSay-1.2.3-win-x64-full.exe'
[IO.File]::WriteAllBytes($installer, [byte[]](0x4d,0x5a,0,1,2,3))
[ordered]@{ Version='1.2.3'; FullInstaller=$installer; InstallerBytes=6; InstallerSHA256=(Get-FileHash -LiteralPath $installer).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture '构建结果.json') -Encoding utf8
$script:networkCalls = 0
function global:gh { $script:networkCalls++; throw '测试禁止网络上传。' }
function global:Get-AuthenticodeSignature { [pscustomobject]@{ Status='NotSigned' } }
$passed = 0
$originalPath = $env:PATH
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -like ('*' + $Message + '*')) { $script:passed++; return }; throw }
    throw ('未拒绝错误输入：' + $Message)
}
function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
try {
    $prepared = & $scriptPath -ArtifactDirectory $fixture
    if ($prepared.Uploaded -or $script:networkCalls -ne 0) { throw '准备模式不应上传。' }
    $passed++
    $metadata = Get-Content -LiteralPath $prepared.DownloadMetadata -Raw | ConvertFrom-Json
    if ($metadata.status -ne 'unpublished' -or $metadata.automaticUpdatesEnabled) { throw '安装包元数据不能冒充在线或启用自动更新。' }
    $passed++
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Publish } '再分发'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Draft } '源码提交'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -ReleaseTag '../bad' } '发行标签'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -GitHubRepository 'owner/repo;bad' } '仓库格式'
    [IO.File]::WriteAllBytes($installer, [byte[]](0x4d,0x5a,0,1,2,4))
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture } 'SHA256'
    if ($script:networkCalls -ne 0) { throw '校验失败前不得调用网络。' }
    Remove-Item Function:/gh
    [IO.File]::WriteAllBytes($installer, [byte[]](0x4d,0x5a,0,1,2,3))
    $shimDirectory = Join-Path $fixture '模拟命令'
    New-Item -ItemType Directory -Path $shimDirectory | Out-Null
    $shimSource = Join-Path $shimDirectory '模拟GitHub命令.cs'
    # 独立子进程模拟真实命令边界，不使用网络，也不依赖开发者的 GitHub 凭据。
    @'
using System;
using System.IO;
using System.Threading;
class SimulatedGitHub {
    static int Main(string[] args) {
        string root = Environment.GetEnvironmentVariable("ISAY_RELEASE_TEST_ROOT");
        string scenario = Environment.GetEnvironmentVariable("ISAY_RELEASE_TEST_CASE");
        string revision = new string('a', 40);
        File.AppendAllText(Path.Combine(root, "命令记录.txt"), args[0] + " " + (args.Length > 1 ? args[1] : "") + "\n");
        if (args[0] == "api") {
            if (scenario == "fail-api") return 1;
            string target = args[1].Substring(args[1].LastIndexOf('/') + 1);
            Console.WriteLine(target.StartsWith("v1.2.3") ? revision : target);
            return 0;
        }
        if (args[1] == "view") {
            bool isPublic = scenario.StartsWith("public") || File.Exists(Path.Combine(root, "已公开"));
            if (scenario == "missing" || (scenario == "fail-after-public" && isPublic)) return 1;
            string target = scenario == "wrong-target" ? new string('b', 40) : revision;
            Console.WriteLine("{\"isDraft\":" + (isPublic ? "false" : "true") + ",\"targetCommitish\":\"" + target + "\",\"url\":\"https://github.com/Diorser/iSay/releases/tag/" + (isPublic ? "v1.2.3-installer-preview" : "untagged-test") + "\",\"assets\":[{\"name\":\"iSay-1.2.3-win-x64-full.exe\"},{\"name\":\"SHA256SUMS.txt\"}]}");
            return 0;
        }
        if (args[1] == "edit") {
            if (Array.IndexOf(args, "--draft=false") >= 0) File.WriteAllText(Path.Combine(root, "已公开"), "true");
            return 0;
        }
        if (args[1] == "download") {
            if (scenario == "timeout") {
                File.WriteAllText(Path.Combine(root, "下载进程号.txt"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                Thread.Sleep(30000);
            }
            string destination = args[Array.IndexOf(args, "--dir") + 1];
            string name = "iSay-1.2.3-win-x64-full.exe";
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, name));
            if (scenario == "corrupt-installer") bytes[bytes.Length - 1] ^= 1;
            File.WriteAllBytes(Path.Combine(destination, name), bytes);
            File.Copy(Path.Combine(root, "独立安装包发布", "SHA256SUMS.txt"), Path.Combine(destination, "SHA256SUMS.txt"));
            if (scenario == "corrupt-checksum") File.AppendAllText(Path.Combine(destination, "SHA256SUMS.txt"), "changed");
            return 0;
        }
        return 9;
    }
}
'@ | Set-Content -LiteralPath $shimSource -Encoding utf8
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw '本地模拟回归需要 Windows 自带的 .NET Framework C# 编译器。' }
    & $compiler /nologo /target:exe ('/out:' + (Join-Path $shimDirectory 'gh.exe')) $shimSource
    if ($LASTEXITCODE -ne 0) { throw '模拟命令编译失败。' }
    $env:PATH = $shimDirectory + [IO.Path]::PathSeparator + $originalPath
    $env:ISAY_RELEASE_TEST_ROOT = $fixture
    $revision = 'a' * 40
    $metadataPath = Join-Path $fixture '独立安装包发布/官网下载元数据.json'
    $logPath = Join-Path $fixture '命令记录.txt'
    $env:ISAY_RELEASE_TEST_CASE = 'wrong-target'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Draft -SourceRevision $revision } '目标源码提交'
    Assert-Test (-not ((Get-Content -LiteralPath $logPath -Raw) -match 'release (edit|upload|create)')) '错误目标提交不得更改远端发行。'
    $env:ISAY_RELEASE_TEST_CASE = 'draft'
    $draftResult = & $scriptPath -ArtifactDirectory $fixture -Mode Draft -SourceRevision $revision
    $draftMetadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Test ($draftMetadata.status -eq 'draft' -and $draftMetadata.roundTripHashVerified -and $draftResult.RoundTripHashVerified) '成功草稿必须完成实际子进程回下载核验。'
    $previousHash = (Get-FileHash -LiteralPath $metadataPath).Hash
    & $scriptPath -ArtifactDirectory $fixture | Out-Null
    Assert-Test ((Get-FileHash -LiteralPath $metadataPath).Hash -eq $previousHash) 'Prepare 不得抹掉前次已核验的草稿状态。'
    Remove-Item -LiteralPath $logPath
    $env:ISAY_RELEASE_TEST_CASE = 'public'
    $verified = & $scriptPath -ArtifactDirectory $fixture -Mode Verify -SourceRevision $revision
    $publicMetadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Test ($verified.Public -and -not $verified.Uploaded -and $publicMetadata.status -eq 'available') 'Verify 应恢复公开发行的下载元数据。'
    Assert-Test (-not ((Get-Content -LiteralPath $logPath -Raw) -match 'release (edit|upload|create)')) 'Verify 只能读取与下载，不得更改资产。'
    $previousHash = (Get-FileHash -LiteralPath $metadataPath).Hash
    $env:ISAY_RELEASE_TEST_CASE = 'fail-api'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Verify -SourceRevision $revision } '不可访问'
    Assert-Test ((Get-FileHash -LiteralPath $metadataPath).Hash -eq $previousHash) '网络失败不得抹掉前次有效元数据。'
    $env:ISAY_RELEASE_TEST_CASE = 'corrupt-installer'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Verify -SourceRevision $revision } '回下载校验失败'
    Assert-Test ((Get-FileHash -LiteralPath $metadataPath).Hash -eq $previousHash) '回下载哈希错误不得抹掉前次有效元数据。'
    $env:ISAY_RELEASE_TEST_CASE = 'corrupt-checksum'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Verify -SourceRevision $revision } '校验和文件'
    $env:ISAY_RELEASE_TEST_CASE = 'timeout'
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Verify -SourceRevision $revision -DownloadTimeoutSeconds 10 } '回下载超时'
    $childId = [int](Get-Content -LiteralPath (Join-Path $fixture '下载进程号.txt') -Raw)
    Assert-Test ($elapsed.Elapsed.TotalSeconds -lt 20 -and $null -eq (Get-Process -Id $childId -ErrorAction SilentlyContinue)) '超时必须有界返回并终止本次下载子进程。'
    Assert-Test ((Get-FileHash -LiteralPath $metadataPath).Hash -eq $previousHash) '超时不得抹掉前次有效元数据。'
    $env:ISAY_RELEASE_TEST_CASE = 'public'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Draft -SourceRevision $revision } '已公开'
    $env:ISAY_RELEASE_TEST_CASE = 'fail-after-public'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Publish -SourceRevision $revision -PublicRedistributionApproved } '只读恢复'
    Assert-Test ((Get-FileHash -LiteralPath $metadataPath).Hash -eq $previousHash) '公开后的查询失败仍应保留前次有效元数据。'
    $env:ISAY_RELEASE_TEST_CASE = 'public'
    Remove-Item -LiteralPath $logPath
    $recovered = & $scriptPath -ArtifactDirectory $fixture -Mode Verify -SourceRevision $revision
    Assert-Test ($recovered.Public -and -not ((Get-Content -LiteralPath $logPath -Raw) -match 'release (edit|upload|create)')) '公开后可用 Verify 恢复，且不修改既有资产。'
    $env:ISAY_RELEASE_TEST_CASE = 'missing'
    Expect-Failure { & $scriptPath -ArtifactDirectory $fixture -Mode Verify -SourceRevision $revision } '不会创建'
    Remove-Item -LiteralPath (Join-Path $fixture '已公开') -ErrorAction SilentlyContinue
    $env:ISAY_RELEASE_TEST_CASE = 'draft'
    $acknowledged = & $scriptPath -ArtifactDirectory $fixture -Mode Publish -SourceRevision $revision -AcknowledgeUnverifiedThirdPartyRights
    $acknowledgedMetadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Test ($acknowledged.Public -and $acknowledgedMetadata.status -eq 'available') '维护者明确承认未核实范围后可公开预览。'
    Assert-Test (-not $acknowledgedMetadata.thirdPartyRightsVerified -and $acknowledgedMetadata.unverifiedThirdPartyRightsAcknowledged) '接受未核实范围不得被记为已取得第三方授权。'
    Assert-Test (@(Get-ChildItem -LiteralPath (Split-Path $metadataPath -Parent) -Filter '*.tmp').Count -eq 0) '原子写入后不应残留临时元数据文件。'
    Write-Host ('独立安装包发布回归通过：' + $passed + ' 项；仅使用本地命令模拟，未访问网络、未注册系统组件。')
} finally {
    $env:PATH = $originalPath
    Remove-Item Env:/ISAY_RELEASE_TEST_ROOT,Env:/ISAY_RELEASE_TEST_CASE -ErrorAction SilentlyContinue
    Remove-Item Function:/gh -ErrorAction SilentlyContinue
    Remove-Item Function:/Get-AuthenticodeSignature -ErrorAction SilentlyContinue
}
