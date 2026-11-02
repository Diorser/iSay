param(
    [ValidateSet('Prepare','Build','Stage')][string]$Mode = 'Prepare',
    [string]$ArtifactDirectory = '',
    [string]$NotesZh = '启用自动检查更新、官网签名更新源与设置中的手动检查。',
    [string]$NotesEn = 'Enable automatic update checks, signed website releases and manual checks in Settings.',
    [switch]$LayoutOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$trustDirectory = Join-Path $projectRoot 'artifacts/更新信任'
$protectedKey = Join-Path $trustDirectory '发布签名密钥.dpapi'
$configurationPath = Join-Path $projectRoot 'src/iSay.Desktop/release.json'
$baseUrl = 'https://isay-voice.pages.dev'
New-Item -ItemType Directory -Path $trustDirectory -Force | Out-Null

function Open-ReleaseKey {
    if (-not (Test-Path -LiteralPath $protectedKey -PathType Leaf)) { throw '缺少本机受保护签名密钥，请先执行 Prepare。' }
    $secure = (Get-Content -LiteralPath $protectedKey -Raw).Trim() | ConvertTo-SecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secure.Dispose() }
}

if ($Mode -eq 'Prepare') {
    $key = [Security.Cryptography.RSA]::Create(3072)
    try {
        if (Test-Path -LiteralPath $protectedKey) { $key.ImportFromPem((Open-ReleaseKey)) }
        else {
            $existing = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
            if (@($existing.publicKeys.PSObject.Properties).Count -gt 0) { throw '当前源码已有信任公钥但本机没有对应私钥，拒绝自动更换信任根。' }
            # 密钥由 Windows 当前用户 DPAPI 加密，不保存明文，不写入 Git。
            $secure = ConvertTo-SecureString $key.ExportPkcs8PrivateKeyPem() -AsPlainText -Force
            try { $secure | ConvertFrom-SecureString | Set-Content -LiteralPath $protectedKey -Encoding ascii }
            finally { $secure.Dispose() }
        }
        $publicKey = $key.ExportSubjectPublicKeyInfoPem()
        $keyId = 'isay-' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($publicKey))).Substring(0,12).ToLowerInvariant()
        $config = [ordered]@{
            applicationId='iSay'; channel='preview'; architecture='win-x64'
            manifestUrls=@(($baseUrl+'/updates/preview/win-x64.json'), 'https://raw.githubusercontent.com/Diorser/iSay/main/website/dist/updates/preview/win-x64.json')
            publicKeys=@{ $keyId=$publicKey }; minimumSequence=0
            maximumPackageBytes=536870912; maximumExpandedBytes=2147483648; maximumFiles=5000
        }
        $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configurationPath -Encoding utf8
        Write-Host '已准备官网与 GitHub 备用清单地址和验签公钥；私钥仅保存在本机 DPAPI 加密文件中。'
    } finally { $key.Dispose() }
    return
}

$config = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
if ($config.channel -ne 'preview' -or @($config.manifestUrls).Count -ne 2 -or @($config.publicKeys.PSObject.Properties).Count -ne 1) { throw '请先准备完整官网更新配置。' }
if ($Mode -eq 'Build') {
    $keyId = @($config.publicKeys.PSObject.Properties)[0].Name
    $previousKey = $env:ISAY_RELEASE_SIGNING_KEY_PEM
    try {
        $env:ISAY_RELEASE_SIGNING_KEY_PEM = Open-ReleaseKey
        $buildArguments = @{ Channel='preview'; KeyId=$keyId; DownloadBaseUrls=@($baseUrl); NotesZh=$NotesZh; NotesEn=$NotesEn; LayoutOnly=$LayoutOnly }
        if ($ArtifactDirectory) { $buildArguments.OutputDirectory=$ArtifactDirectory }
        & (Join-Path $PSScriptRoot '构建Windows发行.ps1') @buildArguments
    } finally { $env:ISAY_RELEASE_SIGNING_KEY_PEM=$previousKey }
    return
}

if (-not $ArtifactDirectory) { throw 'Stage 必须指定已构建的 ArtifactDirectory。' }
$artifact = Assert-ReleasePlainPath $ArtifactDirectory
& dotnet run --project (Join-Path $projectRoot 'src/iSay.ReleaseTool/iSay.ReleaseTool.csproj') -c Release -- --verify-publication $artifact
if ($LASTEXITCODE -ne 0) { throw '发行内容预检失败，未改官网更新指针。' }
$metadata = Get-Content -LiteralPath (Join-Path $artifact '应用更新包/发行产物.json') -Raw | ConvertFrom-Json
$package = Join-Path $artifact ('应用更新包/'+$metadata.packageName)
if ((Get-Item -LiteralPath $package).Length -gt 24*1024*1024) { throw '应用包超过 Pages 安全文件预算，请改用对象存储，不要将完整安装器放入 Pages。' }
$dist = Join-Path $projectRoot 'website/dist'
$destination = Join-Path $dist $metadata.objectKey
New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
Copy-Item -LiteralPath $package -Destination $destination
$feed = Join-Path $dist 'updates/preview/win-x64.json'
New-Item -ItemType Directory -Path (Split-Path $feed -Parent) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $artifact '应用更新包/release.signed.json') -Destination $feed
Write-Host '官网应用包与签名清单已准备；部署 Pages 后必须验证实际网络检查和完整回下载，再启用安装包下载元数据。'
