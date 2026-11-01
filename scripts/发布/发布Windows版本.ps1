param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [string]$Bucket = 'isay-releases',
    [string]$DownloadBaseUrl = '',
    [string]$GitHubRepository = '',
    [switch]$IncludeFullInstaller,
    [switch]$PublicRedistributionApproved,
    [switch]$Publish,
    [switch]$Promote
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
$root=Assert-ReleasePlainPath $ArtifactDirectory
$metadata=Get-Content -LiteralPath (Join-Path $root '应用更新包/发行产物.json') -Raw | ConvertFrom-Json
$package=Join-Path $root ('应用更新包/' + $metadata.packageName)
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $metadata.sha256) { throw '发布包内容与构建结果不符。' }
$envelope=Join-Path $root '应用更新包/release.signed.json'
$trustedReady=[bool]($metadata.signed -and (Test-Path -LiteralPath $envelope))
if ($Bucket -notmatch '^[a-z0-9][a-z0-9-]{2,62}$') { throw 'R2 桶名称无效。' }
$installer=$null
if ($IncludeFullInstaller) {
    $build=Get-Content -LiteralPath (Join-Path $root '构建结果.json') -Raw | ConvertFrom-Json
    $installer=Assert-ReleasePlainPath $build.FullInstaller
    if (-not $installer.StartsWith($root.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $installer).Hash -ne $build.InstallerSHA256) { throw '完整安装包路径或哈希不符。' }
    $installerKey='objects/'+$build.InstallerSHA256.ToLowerInvariant()+'/'+[IO.Path]::GetFileName($installer)
}
$plan=[ordered]@{ Version=$metadata.version; UploadObject=($Bucket+'/'+$metadata.objectKey); FullInstallerObject=$(if($installer){$Bucket+'/'+$installerKey}else{$null}); ManifestTarget=($Bucket+'/channels/stable/win-x64.json'); GitHubDraft=$GitHubRepository; SignedReleaseReady=$trustedReady; PublishRequested=[bool]$Publish; PromoteRequested=[bool]$Promote }
$plan|ConvertTo-Json
if (-not $Publish) { return }
if ($IncludeFullInstaller -and -not $PublicRedistributionApproved) { throw '完整包含第三方组件；确认再分发范围后显式传入 -PublicRedistributionApproved。' }
# 上传前使用与客户端相同的验签实现，核对布局内公钥、清单和真实包，不只相信元数据中的 signed 标志。
& dotnet run --project (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'src/iSay.ReleaseTool/iSay.ReleaseTool.csproj') -c Release -- --verify-publication $root
if ($LASTEXITCODE -ne 0) { throw '正式发布预检失败，未上传或切换清单。' }
$verified = Get-Content -LiteralPath (Join-Path $root '应用更新包/发布核对.json') -Raw | ConvertFrom-Json
if (-not $trustedReady) { throw '缺少正式签名清单，不能发布或切换版本。请在受控发布环境提供签名私钥后重新构建。' }
if (-not $env:CLOUDFLARE_API_TOKEN) { throw '请通过环境变量提供 CLOUDFLARE_API_TOKEN。' }
if (-not $DownloadBaseUrl -or ([uri]$DownloadBaseUrl).Scheme -ne 'https') { throw '发布时必须指定实际 HTTPS 下载域名，供上传后验证。' }
& npx --no-install wrangler r2 object put ($Bucket+'/'+$metadata.objectKey) --file $package --remote
if ($LASTEXITCODE -ne 0) { throw 'R2 对象上传失败，未切换正式清单。' }
if ($installer) {
    & npx --no-install wrangler r2 object put ($Bucket+'/'+$installerKey) --file $installer --remote
    if ($LASTEXITCODE -ne 0) { throw '完整安装包上传失败，未切换正式清单。' }
}
$check=Join-Path $root ('远端验证-'+[guid]::NewGuid().ToString('N')+'.zip')
try {
    Invoke-WebRequest -Uri ($DownloadBaseUrl.TrimEnd('/')+'/'+$metadata.objectKey) -OutFile $check
    if ((Get-FileHash -LiteralPath $check -Algorithm SHA256).Hash -ne $metadata.sha256) { throw '远端对象哈希不符，未切换正式清单。' }
    if ($installer) {
        Invoke-WebRequest -Uri ($DownloadBaseUrl.TrimEnd('/')+'/'+$installerKey) -OutFile $check
        if ((Get-FileHash -LiteralPath $check).Hash -ne $build.InstallerSHA256) { throw '远端完整安装包哈希不符，未切换正式清单。' }
    }
} finally { if (Test-Path -LiteralPath $check -PathType Leaf) { Remove-Item -LiteralPath $check } }
if ($GitHubRepository) {
    if ($GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'GitHub 仓库格式无效。' }
    # 先创建草稿；人工核对附属说明和许可后再发布，私钥不进入命令行。
    $releaseFiles=@($package,$envelope)
    if ($installer) { $releaseFiles+=@($installer) }
    & gh release create ('v'+$metadata.version) @releaseFiles --repo $GitHubRepository --draft --title ('iSay '+$metadata.version) --notes-file (Join-Path $PSScriptRoot '第三方依赖声明.txt')
    if ($LASTEXITCODE -ne 0) { throw 'GitHub 草稿创建失败，未切换正式清单。' }
}
if ($Promote) {
    & npx --no-install wrangler r2 object put ($Bucket+'/channels/'+$verified.channel+'/'+$verified.architecture+'.json') --file $envelope --content-type 'application/json' --remote
    if ($LASTEXITCODE -ne 0) { throw '清单指针切换失败。' }
}
