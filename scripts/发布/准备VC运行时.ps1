param([string]$CacheDirectory = '')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
if (-not $CacheDirectory) { $CacheDirectory=Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/发行工具/VC运行时' }
$cache=Assert-ReleasePlainPath $CacheDirectory
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
foreach ($architecture in @('x64','x86')) {
    $url='https://aka.ms/vc14/vc_redist.'+$architecture+'.exe'
    $destination=Join-Path $cache ('vc_redist.'+$architecture+'.exe')
    if (-not (Test-Path -LiteralPath $destination)) {
        $partial=$destination+'.partial-'+[guid]::NewGuid().ToString('N')
        try {
            Invoke-WebRequest -Uri $url -OutFile $partial
            Assert-MicrosoftReleaseSignature $partial
            [IO.File]::Move($partial,$destination)
        } finally { if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial } }
    }
    Assert-MicrosoftReleaseSignature $destination
    $version=(Get-Item -LiteralPath $destination).VersionInfo.ProductVersion
    if ($version -notmatch '^14\.\d+\.\d+\.\d+$') { throw 'VC 运行时版本无效。' }
    $results.Add([pscustomobject]@{ Architecture=$architecture; Version=$version; Path=$destination; SHA256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash; SourceUrl=$url; SignatureVerified=$true })
}
$results|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $cache 'VC运行时来源.json') -Encoding utf8
$results.ToArray()
