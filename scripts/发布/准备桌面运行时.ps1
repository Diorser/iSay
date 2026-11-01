param([string]$CacheDirectory = '', [string]$MinimumVersion = '10.0.8')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
if (-not $CacheDirectory) { $CacheDirectory = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/发行工具/运行时' }
$cache = Assert-ReleasePlainPath $CacheDirectory
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$metadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$metadata = Invoke-RestMethod -Uri $metadataUrl
$release = @($metadata.releases | Where-Object { Test-DesktopRuntimeVersion $_.'release-version' $MinimumVersion } | Sort-Object { [version]$_.'release-version' } -Descending)[0]
if ($null -eq $release) { throw '微软元数据中没有满足最低补丁要求的 .NET 10 稳定发行。' }
$candidate = @($release.windowsdesktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'windowsdesktop-runtime-win-x64.exe' })
if ($candidate.Count -ne 1) { throw '微软元数据没有唯一的 Windows Desktop x64 EXE。' }
$file = $candidate[0]
$url = [uri]$file.url
if ($url.Scheme -ne 'https' -or $url.Host -notin @('builds.dotnet.microsoft.com','download.visualstudio.microsoft.com')) { throw '运行时下载地址不属于微软官方发行源。' }
$destination = Join-Path $cache ('windowsdesktop-runtime-' + $release.'release-version' + '-win-x64.exe')
if (-not (Test-Path -LiteralPath $destination)) {
    $partial = $destination + '.partial-' + [guid]::NewGuid().ToString('N')
    try {
        Invoke-WebRequest -Uri $url.AbsoluteUri -OutFile $partial
        Test-ReleaseRuntimeHash $partial $file.hash
        Assert-MicrosoftReleaseSignature $partial
        [IO.File]::Move($partial,$destination)
    } finally { if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial } }
}
Test-ReleaseRuntimeHash $destination $file.hash
Assert-MicrosoftReleaseSignature $destination
$result = [ordered]@{ Version=$release.'release-version'; MinimumVersion=$MinimumVersion; Architecture='x64'; Framework='Microsoft.WindowsDesktop.App'; Path=$destination; SHA512=$file.hash; SHA256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash; MetadataUrl=$metadataUrl; SourceUrl=$url.AbsoluteUri; VerifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cache '运行时来源.json') -Encoding utf8
[pscustomobject]$result
