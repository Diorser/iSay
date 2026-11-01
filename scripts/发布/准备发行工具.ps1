param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
if (-not $OutputDirectory) { $OutputDirectory=Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/发行工具' }
$root=Assert-ReleasePlainPath $OutputDirectory
New-Item -ItemType Directory -Path $root -Force | Out-Null
$destination=Join-Path $root 'InnoSetup.6.7.3'
$compiler=Join-Path $destination 'tools/ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $archive=Join-Path $root ('InnoSetup.6.7.3-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg' -OutFile $archive
        Expand-Archive -LiteralPath $archive -DestinationPath $destination
    } finally { if (Test-Path -LiteralPath $archive -PathType Leaf) { Remove-Item -LiteralPath $archive } }
}
Assert-InnoCompilerSignature $compiler
[pscustomobject]@{ Compiler=$compiler; Version='6.7.3'; SignatureVerified=$true; InstallerExecuted=$false }
