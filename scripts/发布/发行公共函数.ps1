$ErrorActionPreference = 'Stop'

function Assert-ReleasePlainPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '发行路径不能含重解析点。' }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

function Get-ReleasePlainFiles([string]$Directory) {
    $root = Assert-ReleasePlainPath $Directory
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw '发行源目录缺失。' }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '发行源树不能含链接。' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { $item }
        }
    }
}

function Resolve-ReleaseChild([string]$Root, [string]$Relative) {
    if ($Relative -match '[\\:<>"|?*\x00-\x1f]' -or $Relative.StartsWith('/') -or @($Relative.Split('/') | Where-Object { $_ -in @('','.','..') -or $_ -match '[. ]$' }).Count -gt 0) { throw '发行清单含非法路径。' }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $path = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $path.StartsWith($base + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw '发行路径超出根目录。' }
    return Assert-ReleasePlainPath $path
}

function Copy-FrozenReleaseRuntime([string]$Source, [string]$Destination, $Hashes) {
    $root = Assert-ReleasePlainPath $Source
    $actual = @(Get-ReleasePlainFiles $root)
    $expected = @($Hashes.PSObject.Properties)
    # 白名单之外的账户、日志、录音或新增程序一律不进入发行包。
    foreach ($entry in $expected) {
        $file = Resolve-ReleaseChild $root $entry.Name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or $entry.Value -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) { throw ('原生冻结文件校验失败：' + $entry.Name) }
    }
    foreach ($file in $actual) {
        $relative = $file.FullName.Substring($root.TrimEnd('\','/').Length + 1).Replace('\','/')
        if ($relative -notin $expected.Name -and $relative -ne '已验收清单.json') { throw ('原生目录存在未冻结文件，拒绝打包：' + $relative) }
    }
    foreach ($entry in $expected) {
        $target = Resolve-ReleaseChild $Destination $entry.Name
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath (Resolve-ReleaseChild $root $entry.Name) -Destination $target
    }
}

function Test-DesktopRuntimeVersion([string]$Version, [string]$Minimum = '10.0.8') {
    if ($Version -notmatch '^10\.0\.\d+$' -or $Minimum -notmatch '^10\.0\.\d+$') { return $false }
    return [version]$Version -ge [version]$Minimum
}

function Assert-MicrosoftReleaseSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') { throw '运行时安装器未通过微软 Authenticode 签名验证。' }
}

function Assert-InnoCompilerSignature([string]$Path) {
    $files=@(Get-Item -LiteralPath $Path)+@(Get-ChildItem -LiteralPath (Split-Path $Path -Parent) -File -Filter '*.dll')
    foreach ($file in $files) {
        $signature=Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch '(Pyrsys|Jordan Russell|Martijn Laan)') { throw 'Inno 编译器或其加载模块未通过发布者签名验证。' }
    }
}

function Test-ReleaseRuntimeHash([string]$Path, [string]$Sha512) {
    if ($Sha512 -notmatch '^[A-Fa-f0-9]{128}$' -or (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash -ne $Sha512) { throw '运行时安装器 SHA512 与微软元数据不一致。' }
}
