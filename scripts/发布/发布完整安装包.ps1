param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [ValidateSet('Prepare','Draft','Publish','Verify')][string]$Mode = 'Prepare',
    [string]$GitHubRepository = 'Diorser/iSay',
    [string]$ReleaseTag = '',
    [string]$SourceRevision = '',
    [ValidateRange(10,1800)][int]$DownloadTimeoutSeconds = 600,
    [switch]$PublicRedistributionApproved,
    [switch]$AcknowledgeUnverifiedThirdPartyRights,
    [switch]$UseRangeDownload,
    [switch]$AutomaticUpdatesEnabled
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
function Write-ReleaseDownloadMetadata($Value, [string]$Path) {
    # 完整写入同目录临时文件后再替换，失败时保留前次核验结果。
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $Path, $true)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
function Get-InstallerRelease {
    $result = & gh release view $ReleaseTag --repo $GitHubRepository --json isDraft,targetCommitish,url,assets,databaseId 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($result | ConvertFrom-Json)
}
function Assert-InstallerReleaseTarget($Release) {
    # 已公开发行以不可变标签为准；草稿以准备创建标签的目标提交为准。
    $target = if ($Release.isDraft) { [string]$Release.targetCommitish } else { $ReleaseTag }
    $resolved = & gh api ('repos/' + $GitHubRepository + '/commits/' + [Uri]::EscapeDataString($target)) --jq '.sha'
    if ($LASTEXITCODE -ne 0 -or [string]$resolved -ine $SourceRevision) { throw '已有发行的目标源码提交与 SourceRevision 不符，未修改发行。' }
}
if ($GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'GitHub 仓库格式无效。' }
$root = Assert-ReleasePlainPath $ArtifactDirectory
$buildPath = Join-Path $root '构建结果.json'
if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) { throw '缺少构建结果，不能发布未知来源文件。' }
$build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
$version = [string]$build.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '安装包版本无效。' }
if (-not $ReleaseTag) { $ReleaseTag = 'v' + $version + '-installer-preview' }
if ($ReleaseTag -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$') { throw '发行标签无效。' }
$fileName = 'iSay-' + $version + '-win-x64-full.exe'
$installer = Resolve-ReleaseChild $root $fileName
if ([IO.Path]::GetFullPath([string]$build.FullInstaller) -ine $installer) { throw '安装包路径与构建结果不符。' }
$size = (Get-Item -LiteralPath $installer).Length
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($size -le 0 -or $size -ne $build.InstallerBytes -or $hash -ine [string]$build.InstallerSHA256) { throw '安装包长度或 SHA256 与构建结果不符，未执行上传。' }
$stream = [IO.File]::OpenRead($installer)
try {
    if ($stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) { throw '安装包缺少 Windows 可执行文件标记。' }
} finally { $stream.Dispose() }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -notin @('Valid','NotSigned')) { throw '安装包存在损坏或不可信的 Authenticode 签名。' }
$prepared = Resolve-ReleaseChild $root '独立安装包发布'
if ($AutomaticUpdatesEnabled) {
    # 只有布局内信任配置、正式签名和实际应用包一致，才记录为已启用更新。
    $releaseTool = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'src/iSay.ReleaseTool/iSay.ReleaseTool.csproj'
    & dotnet run --project $releaseTool -c Release -- --verify-publication $root
    if ($LASTEXITCODE -ne 0) { throw '应用更新签名预检失败，不能标记已启用自动更新。' }
}
New-Item -ItemType Directory -Path $prepared -Force | Out-Null
$checksum = Join-Path $prepared 'SHA256SUMS.txt'
($hash + '  ' + $fileName) | Set-Content -LiteralPath $checksum -Encoding ascii
$notesPath = Join-Path $prepared '安装包发行说明.md'
$updateNote = if ($AutomaticUpdatesEnabled) { '已启用官网签名更新源，默认自动检查；也可在设置最后的“关于”页手动检查、下载并安装应用更新。' } else { '当前客户端自动更新源未启用。手动下载安装与自动更新是两个独立流程。' }
$notes = @"
# iSay $version Windows 完整安装预览

本资产是可安装的 Windows x64 完整安装器，包含本次构建的原生组件与微软官方依赖安装器。安装时按需检测并安装 .NET Desktop 和 VC++ x64/x86。

- 文件：$fileName
- 长度：$size 字节
- SHA256：$hash
- Windows 发布者签名状态：$($signature.Status)
- 当前为安装预览；Windows 10 虚拟机新机安装和真实麦克风仍待验收。
- $updateNote

原生识别、整理、个性化和词典组件来自第三方软件，部分适配会生成修改后的程序副本。项目未取得单独再分发授权，相关修改、分发与服务使用范围仍未确认；这些二进制不属于 iSay 的 MIT 开源范围，也不代表获得原厂授权或背书。本版本由维护者选择作为完整安装预览发布。GitHub 草稿仅供维护者审阅。

This asset is a complete Windows x64 installer preview, separate from the automatic update channel. Clean Windows 10 VM installation and real microphone acceptance remain pending. Third-party native binaries are outside the iSay MIT license. Separate redistribution authorization has not been obtained; modification, redistribution and service-use rights remain unverified. This preview does not imply vendor authorization or endorsement.
"@
$notes | Set-Content -LiteralPath $notesPath -Encoding utf8
$download = [ordered]@{
    schema = 1; status = 'unpublished'; version = $version; channel = 'preview'; architecture = 'win-x64'
    fileName = $fileName; bytes = $size; sha256 = $hash; authenticode = [string]$signature.Status
    url = ('https://github.com/' + $GitHubRepository + '/releases/download/' + $ReleaseTag + '/' + $fileName)
    releaseUrl = ('https://github.com/' + $GitHubRepository + '/releases/tag/' + $ReleaseTag)
    automaticUpdatesEnabled = [bool]$AutomaticUpdatesEnabled
    thirdPartyRightsVerified = [bool]$PublicRedistributionApproved
    unverifiedThirdPartyRightsAcknowledged = [bool]$AcknowledgeUnverifiedThirdPartyRights
}
$downloadPath = Join-Path $prepared '官网下载元数据.json'
if ($Mode -eq 'Prepare') {
    $preservePrevious = $false
    if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
        try {
            $previous = Get-Content -LiteralPath $downloadPath -Raw | ConvertFrom-Json
            $preservePrevious = $previous.schema -eq 1 -and $previous.status -in @('draft','available') -and
                $previous.version -eq $version -and $previous.fileName -eq $fileName -and $previous.bytes -eq $size -and
                $previous.sha256 -ieq $hash -and $previous.url -ceq $download.url
        } catch { $preservePrevious = $false }
    }
    if (-not $preservePrevious) { Write-ReleaseDownloadMetadata $download $downloadPath }
    [pscustomobject]@{ Mode=$Mode; Uploaded=$false; Installer=$installer; Checksum=$checksum; Notes=$notesPath; DownloadMetadata=$downloadPath }
    return
}
if ($Mode -eq 'Publish' -and -not $PublicRedistributionApproved -and -not $AcknowledgeUnverifiedThirdPartyRights) { throw '完整包含第三方原生组件；公开发布前需确认再分发范围，或由维护者显式承认范围未确认；不得将风险接受记作已取得授权。未执行上传。' }
if ($SourceRevision -notmatch '^[a-fA-F0-9]{40}$') { throw '上传前需指定完整的目标源码提交 SHA。该参数只是发行标签目标，不代替可重复构建证明。' }
& gh api ('repos/' + $GitHubRepository + '/commits/' + $SourceRevision) --jq '.sha' | Out-Null
if ($LASTEXITCODE -ne 0) { throw '目标源码提交不可访问，未执行上传。' }
$existing = Get-InstallerRelease
if ($null -ne $existing) {
    Assert-InstallerReleaseTarget $existing
    if ($Mode -ne 'Verify' -and -not $existing.isDraft) { throw '同名发行已公开，拒绝替换既有安装器。可使用 Verify 只读核验并恢复下载元数据。' }
}
if ($Mode -eq 'Verify') {
    if ($null -eq $existing) { throw '未找到可核验的发行，Verify 不会创建或修改远端资产。' }
} elseif ($null -ne $existing) {
    & gh release edit $ReleaseTag --repo $GitHubRepository --notes-file $notesPath --prerelease
    if ($LASTEXITCODE -ne 0) { throw '草稿说明更新失败。' }
    # 重跑时不覆盖同名资产；先列出已有资产，防止旧文件被无意替换。
    $existingAssets = @($existing.assets | ForEach-Object { $_.name })
    $uploadFiles = @($installer,$checksum) | Where-Object { [IO.Path]::GetFileName($_) -notin $existingAssets }
    if ($uploadFiles.Count -gt 0) {
        & gh release upload $ReleaseTag @uploadFiles --repo $GitHubRepository
        if ($LASTEXITCODE -ne 0) { throw '草稿资产上传失败。' }
    }
} else {
    & gh release create $ReleaseTag $installer $checksum --repo $GitHubRepository --target $SourceRevision --draft --prerelease --title ('iSay ' + $version + ' Windows 安装预览') --notes-file $notesPath
    if ($LASTEXITCODE -ne 0) { throw '安装器草稿创建或上传失败。' }
}
# 从 GitHub 下载上传后的真实资产，再计算哈希，不能只相信上传命令返回成功。
$verification = Join-Path $prepared ('下载核对-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verification | Out-Null
if ($UseRangeDownload) {
    $releaseForDownload = Get-InstallerRelease
    if ($null -eq $releaseForDownload -or [long]$releaseForDownload.databaseId -le 0) { throw '无法定位发行资产，未执行分块下载。' }
    Assert-InstallerReleaseTarget $releaseForDownload
    $restReleaseText = & gh api ('repos/' + $GitHubRepository + '/releases/' + $releaseForDownload.databaseId)
    if ($LASTEXITCODE -ne 0) { throw '读取发行资产详情失败。' }
    $restRelease = $restReleaseText | ConvertFrom-Json
    foreach ($localFile in @($installer, $checksum)) {
        $assetName = [IO.Path]::GetFileName($localFile)
        $assets = @($restRelease.assets | Where-Object name -CEQ $assetName)
        $expectedHash = (Get-FileHash -LiteralPath $localFile -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedLength = (Get-Item -LiteralPath $localFile).Length
        if ($assets.Count -ne 1 -or $assets[0].size -ne $expectedLength -or
            $assets[0].digest -cne ('sha256:' + $expectedHash)) { throw 'GitHub 资产详情与本地产物不符，未执行下载或公开。' }
        & (Join-Path $PSScriptRoot '下载GitHub发行资产.ps1') -Repository $GitHubRepository -AssetId $assets[0].id `
            -FileName $assetName -ExpectedBytes $expectedLength -ExpectedSHA256 $expectedHash `
            -OutputDirectory $verification -TimeoutSeconds $DownloadTimeoutSeconds
    }
} else {
$downloadInfo = [Diagnostics.ProcessStartInfo]::new()
$downloadInfo.FileName = (Get-Command gh -CommandType Application | Select-Object -First 1).Source
$downloadInfo.UseShellExecute = $false
$downloadInfo.RedirectStandardOutput = $true
$downloadInfo.RedirectStandardError = $true
foreach ($argument in @('release','download',$ReleaseTag,'--repo',$GitHubRepository,'--pattern',$fileName,'--pattern','SHA256SUMS.txt','--dir',$verification)) { $downloadInfo.ArgumentList.Add($argument) }
$downloadProcess = [Diagnostics.Process]::new()
$downloadProcess.StartInfo = $downloadInfo
try {
    [void]$downloadProcess.Start()
    $downloadOutput = $downloadProcess.StandardOutput.ReadToEndAsync()
    $downloadError = $downloadProcess.StandardError.ReadToEndAsync()
    if (-not $downloadProcess.WaitForExit($DownloadTimeoutSeconds * 1000)) {
        $downloadProcess.Kill($true)
        [void]$downloadProcess.WaitForExit(5000)
        throw '安装器回下载超时；远端发布状态未改变，本地前次核验元数据保留。请检查 GitHub 资产下载网络后重试。'
    }
    if ($downloadProcess.ExitCode -ne 0) { throw '安装器回下载失败；远端发布状态未改变，本地前次核验元数据保留。' }
} finally { $downloadProcess.Dispose() }
}
$received = Join-Path $verification $fileName
if ((Get-Item -LiteralPath $received).Length -ne $size -or (Get-FileHash -LiteralPath $received).Hash -ine $hash) { throw 'GitHub 安装器回下载校验失败，未更改远端发布状态及前次核验元数据。' }
$receivedChecksum = Join-Path $verification 'SHA256SUMS.txt'
if ((Get-FileHash -LiteralPath $receivedChecksum).Hash -ne (Get-FileHash -LiteralPath $checksum).Hash) { throw 'GitHub 校验和文件与本地不同，未更改远端发布状态及前次核验元数据。' }
$verifiedRelease = Get-InstallerRelease
if ($null -eq $verifiedRelease) { throw '读取核验后的发行状态失败；前次下载元数据保留。' }
Assert-InstallerReleaseTarget $verifiedRelease
if ($Mode -ne 'Verify' -and -not $verifiedRelease.isDraft) { throw '发行在核验期间已被公开，未继续修改；请使用 Verify 恢复下载元数据。' }
if ($Mode -eq 'Publish') {
    & gh release edit $ReleaseTag --repo $GitHubRepository --draft=false --prerelease
    if ($LASTEXITCODE -ne 0) { throw '草稿未能公开，官网入口不得启用。' }
    $verifiedRelease = Get-InstallerRelease
    if ($null -eq $verifiedRelease -or $verifiedRelease.isDraft) { throw '公开操作后的发行状态未核实；前次元数据保留，可使用 Verify 只读恢复。' }
    Assert-InstallerReleaseTarget $verifiedRelease
}
$actualReleaseUrl = [string]$verifiedRelease.url
if (-not $actualReleaseUrl.StartsWith(('https://github.com/' + $GitHubRepository + '/releases/tag/'), [StringComparison]::Ordinal)) { throw '实际发行地址无效；前次下载元数据保留。' }
$download.status = if ($verifiedRelease.isDraft) { 'draft' } else { 'available' }
$download.releaseUrl = $actualReleaseUrl
$download.sourceRevision = $SourceRevision.ToLowerInvariant()
$download.roundTripHashVerified = $true
Write-ReleaseDownloadMetadata $download $downloadPath
[pscustomobject]@{ Mode=$Mode; Uploaded=($Mode -ne 'Verify'); Public=(-not $verifiedRelease.isDraft); ReleaseUrl=$download.releaseUrl; RoundTripHashVerified=$true; DownloadMetadata=$downloadPath }
