using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using iSay.Updates;

var root = Path.Combine(AppContext.BaseDirectory, "../../..", ".runtime", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "布局"));
Directory.CreateDirectory(Path.Combine(root, "应用更新包"));
using var key = RSA.Create(2048);
var configuration = new UpdateConfiguration { ManifestUrls = ["https://updates.example.test/manifest.json"], PublicKeys = new() { ["test"] = key.ExportSubjectPublicKeyInfoPem() } };
var configurationPath = Path.Combine(root, "布局/release.json");
File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration, UpdateJson.Options));
var package = Path.Combine(root, "应用更新包/app.zip");
File.WriteAllBytes(package, [1, 2, 3]);
var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)));
var objectKey = "objects/" + hash.ToLowerInvariant() + "/app.zip";
var manifest = new ReleaseManifest
{
    Version = "1.0.0", Sequence = 100, PublishedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
    Components = [new() { Version = "1.0.0", Sha256 = hash, Size = 3, MaximumExpandedBytes = 3, PackageUrls = ["https://updates.example.test/" + objectKey],
        Files = [new() { Path = "iSay.dll", Size = 3, Sha256 = hash }] }]
};
var envelopePath = Path.Combine(root, "应用更新包/release.signed.json");
File.WriteAllText(envelopePath, ManifestCodec.Sign(manifest, key, "test"));
File.WriteAllText(Path.Combine(root, "应用更新包/发行产物.json"), JsonSerializer.Serialize(new { version = "1.0.0", packageName = "app.zip", objectKey, sha256 = hash, size = 3, signed = true }));
var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var tool = Path.Combine(projectRoot, "src/iSay.ReleaseTool/bin/Release/net10.0/iSay.ReleaseTool.dll");
var count = 0;
await CheckAsync(true, "完整签名与真实包核对通过");
File.WriteAllBytes(package, [3, 2, 1]);
await CheckAsync(false, "包内容更改阻断公开上传");
File.WriteAllBytes(package, [1, 2, 3]);
File.WriteAllText(envelopePath, "{}");
await CheckAsync(false, "伪造签名标志不能通过发布预检");
File.WriteAllText(envelopePath, ManifestCodec.Sign(manifest, key, "test"));
File.WriteAllText(configurationPath, JsonSerializer.Serialize(new UpdateConfiguration(), UpdateJson.Options));
await CheckAsync(false, "客户端缺少实际信任配置阻断发布");
Console.WriteLine($"发行发布预检：{count} 项通过；没有执行网络或上传。");

async Task CheckAsync(bool expected, string name)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(tool); start.ArgumentList.Add("--verify-publication"); start.ArgumentList.Add(Path.GetFullPath(root));
    using var process = Process.Start(start) ?? throw new IOException("无法启动预检工具。");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    await Task.WhenAll(output, error);
    if ((process.ExitCode == 0) != expected) throw new InvalidOperationException(name);
    count++; Console.WriteLine("通过：" + name);
}
