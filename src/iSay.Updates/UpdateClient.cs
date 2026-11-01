using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace iSay.Updates;

public sealed class UpdateClient : IDisposable
{
    private readonly UpdateConfiguration configuration;
    private readonly HttpClient http;

    public UpdateClient(UpdateConfiguration configuration, HttpMessageHandler? handler = null)
    {
        configuration.Validate();
        this.configuration = JsonSerializer.Deserialize<UpdateConfiguration>(JsonSerializer.Serialize(configuration, UpdateJson.Options), UpdateJson.Options)!;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
        { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("iSay-Updater/1.0");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!configuration.IsConfigured) return new(UpdateCheckStatus.NotConfigured);
        var current = UpdateVersions.Parse(currentVersion);
        foreach (var address in configuration.ManifestUrls)
        {
            try
            {
                using var response = await SendAsync(UpdateSafety.RequireHttps(address), null, null, cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = Encoding.UTF8.GetString(await ReadLimitedAsync(response.Content, ManifestCodec.MaximumEnvelopeBytes, cancellationToken));
                var release = ManifestCodec.Verify(json, configuration);
                if (UpdateVersions.Parse(release.Manifest.Version) > current && release.Manifest.Sequence <= configuration.MinimumSequence)
                    throw new InvalidDataException("新版本更新序号未递增。");
                return new(UpdateVersions.Parse(release.Manifest.Version) > current ? UpdateCheckStatus.Available : UpdateCheckStatus.UpToDate, release);
            }
            catch (Exception error) when (IsSourceFailure(error, cancellationToken)) { }
        }
        throw new IOException("所有更新源均不可用或清单校验失败。");
    }

    public async Task<PreparedUpdate> DownloadAsync(VerifiedRelease release, string cacheDirectory,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // 只使用再次验证后的快照，界面持有的可变对象不构成信任依据。
        var verified = ManifestCodec.Verify(release.EnvelopeJson, configuration);
        var root = UpdateSafety.PrepareDirectory(cacheDirectory);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in verified.Manifest.Components)
        {
            var path = Path.Combine(root, $"{component.Id}-{component.Sha256.ToLowerInvariant()}.zip");
            foreach (var candidate in new[] { path, path + ".lock", path + ".partial", path + ".partial.json" })
                UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, candidate);
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(path))
            {
                try { await UpdateSafety.VerifyFileAsync(path, component.Size, component.Sha256, cancellationToken); paths[component.Id] = path; continue; }
                catch (InvalidDataException) { File.Delete(path); }
            }
            await DownloadComponentAsync(component, path, progress, cancellationToken);
            paths.Add(component.Id, path);
        }
        var envelopePath = Path.Combine(root, $"manifest-{verified.Manifest.Sequence}.json");
        await UpdateSafety.WriteTextAtomicAsync(envelopePath, verified.EnvelopeJson, cancellationToken);
        return new PreparedUpdate(envelopePath, paths);
    }

    private async Task DownloadComponentAsync(UpdateComponent component, string destination, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var partial = destination + ".partial";
        var metadataPath = partial + ".json";
        if (File.Exists(partial) && new FileInfo(partial).Length == component.Size)
        {
            try
            {
                await UpdateSafety.VerifyFileAsync(partial, component.Size, component.Sha256, cancellationToken);
                File.Move(partial, destination, true);
                File.Delete(metadataPath);
                return;
            }
            catch (InvalidDataException) { File.Delete(partial); File.Delete(metadataPath); }
        }
        foreach (var address in component.PackageUrls)
        {
            try
            {
                var uri = UpdateSafety.RequireHttps(address);
                ResumeMetadata? metadata = null;
                if (File.Exists(partial) && File.Exists(metadataPath) && new FileInfo(metadataPath).Length < 8192)
                {
                    try { metadata = JsonSerializer.Deserialize<ResumeMetadata>(await File.ReadAllTextAsync(metadataPath, cancellationToken), UpdateJson.Options); }
                    catch (JsonException) { }
                }
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (metadata is null || metadata.Url != address || metadata.Sha256 != component.Sha256 || metadata.Size != component.Size ||
                    !IsStrongTag(metadata.ETag) || offset <= 0 || offset >= component.Size)
                { metadata = null; offset = 0; }

                using var firstResponse = await SendAsync(uri, offset > 0 ? offset : null, metadata?.ETag, cancellationToken);
                HttpResponseMessage response = firstResponse;
                HttpResponseMessage? replacement = null;
                try
                {
                    var entityUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? address;
                    if (offset > 0 && (entityUrl != metadata!.EntityUrl || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
                    {
                        replacement = await SendAsync(uri, null, null, cancellationToken);
                        response = replacement;
                        offset = 0;
                        entityUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? address;
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("更新包不接受内容编码转换。");
                    var tag = response.Headers.ETag?.ToString();
                    if (offset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (tag != metadata!.ETag || range is null || range.Unit != "bytes" || range.From != offset || range.To != component.Size - 1 || range.Length != component.Size)
                            throw new InvalidDataException("更新续传实体或范围不匹配。");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
                    else throw new InvalidDataException("更新源返回了无效的下载状态。");
                    if (response.Content.Headers.ContentLength is { } length && length != component.Size - offset)
                        throw new InvalidDataException("更新下载长度与签名清单不符。");
                    await UpdateSafety.WriteTextAtomicAsync(metadataPath, JsonSerializer.Serialize(new ResumeMetadata(address, entityUrl, tag, component.Sha256, component.Size), UpdateJson.Options), cancellationToken);
                    // 重新下载先移除旧目录项，续传先复制为独立文件，避免写穿已有硬链接。
                    if (offset == 0) File.Delete(partial);
                    else await UpdateSafety.DetachFileAsync(partial, cancellationToken);
                    await using (var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        if (output.Length != offset) throw new InvalidDataException("更新续传文件长度改变。");
                        output.Position = offset;
                        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                        var buffer = new byte[81920];
                        var total = offset;
                        while (true)
                        {
                            var read = await ReadWithTimeoutAsync(input, buffer, cancellationToken);
                            if (read == 0) break;
                            total = checked(total + read);
                            if (total > component.Size) throw new InvalidDataException("更新下载超出签名长度。");
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            progress?.Report(new UpdateProgress(component.Id, total, component.Size));
                        }
                        await output.FlushAsync(cancellationToken);
                        if (total != component.Size) throw new IOException("更新下载中断，可重新尝试续传。");
                    }
                    await UpdateSafety.VerifyFileAsync(partial, component.Size, component.Sha256, cancellationToken);
                    File.Move(partial, destination, true);
                    File.Delete(metadataPath);
                    return;
                }
                finally { replacement?.Dispose(); }
            }
            catch (InvalidDataException) { File.Delete(partial); File.Delete(metadataPath); }
            catch (Exception error) when (IsSourceFailure(error, cancellationToken)) { }
        }
        throw new IOException("所有更新包镜像均不可用或校验失败。");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, long? offset, string? tag, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset is { } start)
            {
                request.Headers.Range = new RangeHeaderValue(start, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(tag!));
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("更新重定向缺少地址。");
                uri = UpdateSafety.RequireHttps(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("更新重定向次数超出上限。");
    }

    private static bool IsStrongTag(string? tag) => EntityTagHeaderValue.TryParse(tag, out var value) && !value.IsWeak && value.Tag != "*";
    private static bool IsSourceFailure(Exception error, CancellationToken token) => !token.IsCancellationRequested && error is HttpRequestException or IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or FormatException or JsonException or OperationCanceledException;

    private static async ValueTask<int> ReadWithTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return await input.ReadAsync(buffer, timeout.Token);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("更新清单超出上限。");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var read = await ReadWithTimeoutAsync(input, buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("更新清单超出上限。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void Dispose() => http.Dispose();
    private sealed record ResumeMetadata(string Url, string EntityUrl, string? ETag, string Sha256, long Size);
}
