param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][long]$AssetId,
    [Parameter(Mandatory)][string]$FileName,
    [Parameter(Mandatory)][long]$ExpectedBytes,
    [Parameter(Mandatory)][string]$ExpectedSHA256,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,8)][int]$Concurrency = 4,
    [ValidateRange(10,1800)][int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '发行公共函数.ps1')
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $AssetId -le 0 -or
    $FileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,150}$' -or
    $ExpectedBytes -le 0 -or $ExpectedBytes -gt 2147483648 -or $ExpectedSHA256 -notmatch '^[a-fA-F0-9]{64}$') {
    throw 'GitHub 资产下载参数无效。'
}
$outputRoot = Assert-ReleasePlainPath $OutputDirectory
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$partRoot = Resolve-ReleaseChild $outputRoot ('分块-' + $AssetId)
New-Item -ItemType Directory -Path $partRoot -Force | Out-Null
# 下载目录也必须是普通路径，防止本地链接将资产写入其他位置。
[void](Resolve-ReleaseChild $partRoot $FileName)
@(Get-ReleasePlainFiles $partRoot) | Out-Null
# 凭据只保存在本进程内存中；下载脚本不记录令牌、重定向地址或异常原文。
if (-not ('ISayReleaseRangeDownload' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static class ISayReleaseRangeDownload {
    private const int ChunkBytes = 4 * 1024 * 1024;
    private static int CompleteChunks;
    private static long CompletedBytes;
    private static readonly object OutputLock = new object();

    public static async Task RunAsync(string token, string repository, long assetId, string fileName, long ExpectedBytes, string ExpectedHash, string directory, int concurrency, int seconds) {
        CompleteChunks = 0;
        CompletedBytes = 0;
        var started = DateTime.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try {
            Uri signedAddress;
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var api = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + repository + "/releases/assets/" + assetId)) {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                request.Headers.UserAgent.ParseAdd("iSay-installer-integrity-check/1.0");
                using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode != HttpStatusCode.Found && response.StatusCode != HttpStatusCode.TemporaryRedirect)
                    throw new InvalidOperationException("AssetRedirectStatus" + (int)response.StatusCode);
                signedAddress = response.Headers.Location;
                if (signedAddress == null || !signedAddress.IsAbsoluteUri || signedAddress.Scheme != "https" ||
                    signedAddress.UserInfo.Length != 0 || !signedAddress.IsDefaultPort ||
                    (signedAddress.Host != "release-assets.githubusercontent.com" && signedAddress.Host != "objects.githubusercontent.com"))
                    throw new InvalidOperationException("UnexpectedAssetHost");
            }
            token = null;

            // 分块请求不携带 GitHub 令牌，限制并发并逐个验证 Content-Range。
            using var downloadHandler = new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = concurrency };
            using var client = new HttpClient(downloadHandler) { Timeout = Timeout.InfiniteTimeSpan };
            using var slots = new SemaphoreSlim(concurrency);
            int chunkCount = (int)((ExpectedBytes + ChunkBytes - 1) / ChunkBytes);
            var tasks = Enumerable.Range(0, chunkCount).Select(async index => {
                await slots.WaitAsync(deadline.Token);
                try {
                    long first = (long)index * ChunkBytes;
                    long last = Math.Min(ExpectedBytes - 1, first + ChunkBytes - 1);
                    string chunkPath = Path.Combine(directory, index.ToString("D3") + ".part");
                    if (!File.Exists(chunkPath) || new FileInfo(chunkPath).Length != last - first + 1) {
                        for (int attempt = 1; attempt <= 3; attempt++) {
                            try {
                                using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                                perRequest.CancelAfter(TimeSpan.FromSeconds(90));
                                using var request = new HttpRequestMessage(HttpMethod.Get, signedAddress);
                                request.Headers.Range = new RangeHeaderValue(first, last);
                                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, perRequest.Token);
                                var range = response.Content.Headers.ContentRange;
                                if (response.StatusCode != HttpStatusCode.PartialContent || range == null || range.Unit != "bytes" ||
                                    range.From != first || range.To != last || range.Length != ExpectedBytes)
                                    throw new InvalidOperationException("InvalidContentRange");
                                if (response.Content.Headers.ContentLength != last - first + 1)
                                    throw new InvalidOperationException("InvalidChunkLengthHeader");
                                await using (var stream = await response.Content.ReadAsStreamAsync(perRequest.Token))
                                await using (var file = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true)) {
                                    await stream.CopyToAsync(file, 65536, perRequest.Token);
                                }
                                if (new FileInfo(chunkPath).Length != last - first + 1)
                                    throw new InvalidOperationException("IncompleteChunk");
                                break;
                            } catch (Exception error) when (attempt < 3 && !deadline.IsCancellationRequested) {
                                lock (OutputLock) Console.WriteLine(JsonSerializer.Serialize(new {status = "retry", chunk = index, attempt, errorType = error.GetType().Name}));
                            }
                        }
                    }
                    if (new FileInfo(chunkPath).Length != last - first + 1)
                        throw new InvalidOperationException("IncompleteChunk");
                    Interlocked.Increment(ref CompleteChunks);
                    Interlocked.Add(ref CompletedBytes, last - first + 1);
                    if (CompleteChunks % 8 == 0 || CompleteChunks == chunkCount)
                    lock (OutputLock) Console.WriteLine(JsonSerializer.Serialize(new {
                        status = "progress", chunks = CompleteChunks, totalChunks = chunkCount,
                        bytes = CompletedBytes, elapsedSeconds = (int)(DateTime.UtcNow - started).TotalSeconds
                    }));
                } finally { slots.Release(); }
            }).ToArray();
            await Task.WhenAll(tasks);

            // 只按分块顺序合并，最终长度与完整 SHA256 同时通过才视为成功。
            string destination = Path.Combine(directory, fileName);
            await using (var merged = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true)) {
                for (int index = 0; index < chunkCount; index++) {
                    await using var chunk = new FileStream(Path.Combine(directory, index.ToString("D3") + ".part"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    await chunk.CopyToAsync(merged, 65536, deadline.Token);
                }
            }
            long actualBytes = new FileInfo(destination).Length;
            using var mergedRead = File.OpenRead(destination);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(mergedRead, deadline.Token)).ToLowerInvariant();
            bool success = actualBytes == ExpectedBytes && actualHash == ExpectedHash;
            var result = new { status = success ? "verified" : "mismatch", bytes = actualBytes, sha256 = actualHash,
                expectedBytes = ExpectedBytes, expectedSha256 = ExpectedHash, elapsedSeconds = (int)(DateTime.UtcNow - started).TotalSeconds };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(directory, "下载核对结果.json"), json);
            Console.WriteLine(json);
            if (!success) throw new InvalidOperationException("AssetHashMismatch");
        } catch (Exception error) {
            // 不输出异常 Message/StackTrace，以免带出临时签名地址。
            var result = new { status = "failed", errorType = error.GetType().Name,
                chunks = CompleteChunks, bytes = CompletedBytes, elapsedSeconds = (int)(DateTime.UtcNow - started).TotalSeconds };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(directory, "下载核对结果.json"), json);
            Console.WriteLine(json);
            throw new InvalidOperationException("GitHubAssetDownloadFailed");
        }
    }
}
'@
}
$token = $null
try {
    $token = (& gh auth token 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) { throw '无法读取当前 GitHub 登录凭据。' }
    $null = [ISayReleaseRangeDownload]::RunAsync($token, $Repository, $AssetId, $FileName, $ExpectedBytes,
        $ExpectedSHA256.ToLowerInvariant(), $partRoot, $Concurrency, $TimeoutSeconds).GetAwaiter().GetResult()
    $received = Resolve-ReleaseChild $partRoot $FileName
    if ((Get-Item -LiteralPath $received).Length -ne $ExpectedBytes -or
        (Get-FileHash -LiteralPath $received -Algorithm SHA256).Hash -ine $ExpectedSHA256) { throw 'GitHub 下载资产完整校验失败。' }
    Copy-Item -LiteralPath $received -Destination (Resolve-ReleaseChild $outputRoot $FileName)
} catch {
    # 只报告类型，避免外层异常附带重定向地址或凭据。
    throw ('GitHub 分块下载失败：' + $_.Exception.GetType().Name)
} finally { $token = $null }
