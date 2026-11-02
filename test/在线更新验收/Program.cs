using System.Text.Json;
using iSay.Updates;

// 只在测试目录下载与验签，不启动桌面、麦克风、安装器或真实用户进程。
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var configuration = UpdateConfiguration.Load(Path.Combine(root, "src/iSay.Desktop/release.json"));
var current = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/iSay.Desktop/iSay.Desktop.csproj"))
    .Descendants("Version").Single().Value;
var evidence = Path.Combine(root, "test/在线更新验收/证据", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(evidence);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var sources = new List<object>();
foreach (var source in configuration.ManifestUrls)
{
    var single = JsonSerializer.Deserialize<UpdateConfiguration>(JsonSerializer.Serialize(configuration, UpdateJson.Options), UpdateJson.Options)!;
    single.ManifestUrls = [source];
    using var client = new UpdateClient(single);
    var available = await client.CheckAsync("0.10.5", cancellation.Token);
    if (available.Status != UpdateCheckStatus.Available || available.Release?.Manifest.Version != current)
        throw new InvalidDataException("线上源没有向旧版本提供当前签名版本。");
    var latest = await client.CheckAsync(current, cancellation.Token);
    if (latest.Status != UpdateCheckStatus.UpToDate) throw new InvalidDataException("当前版本不应提示重复更新。");
    sources.Add(new { source, available = available.Release.Manifest.Version, currentUpToDate = true });
}
using (var client = new UpdateClient(configuration))
{
    var release = (await client.CheckAsync("0.10.5", cancellation.Token)).Release!;
    var prepared = await client.DownloadAsync(release, Path.Combine(evidence, "下载缓存"), cancellationToken: cancellation.Token);
    var component = release.Manifest.Components.Single();
    await UpdateSafety.VerifyFileAsync(prepared.PackagePaths["application"], component.Size, component.Sha256, cancellation.Token);
    File.WriteAllText(Path.Combine(evidence, "结果.json"), JsonSerializer.Serialize(new
    {
        version = current, sources, packageBytes = component.Size, packageSha256 = component.Sha256,
        signatureVerified = true, downloadedAndHashVerified = true, installed = false
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"真实线上更新验收通过：{sources.Count} 个签名清单源，旧版发现新版、当前版不重复提示、完整下载与 SHA256 校验通过；应用包 {component.Size} 字节。");
}
