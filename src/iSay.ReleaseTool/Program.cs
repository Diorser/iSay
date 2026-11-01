using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using iSay.Updates;

if (args.Length == 2 && args[0] == "--verify-publication")
{
    await VerifyPublicationAsync(args[1]);
    return;
}
if (args.Length != 1) throw new ArgumentException("用法：dotnet run --project src/iSay.ReleaseTool -- <发行请求.json>");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var request = JsonSerializer.Deserialize<ReleaseRequest>(File.ReadAllText(args[0]), options) ?? throw new InvalidDataException("发行请求为空。");
if (!Version.TryParse(request.Version, out _)) throw new InvalidDataException("发行版本无效。");
if (request.Channel is not ("stable" or "preview") || request.Sequence < 1) throw new InvalidDataException("发行通道或序号无效。");
var source = UpdateSafety.PrepareDirectory(request.LayoutDirectory);
var output = UpdateSafety.PrepareDirectory(request.OutputDirectory);
var filePaths = Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly)
    .Where(path => Path.GetExtension(path) is not (".pdb" or ".log") &&
        !Path.GetFileName(path).StartsWith("iSay.Updater", StringComparison.OrdinalIgnoreCase) &&
        !Path.GetFileName(path).Equals("iSay.Updates.dll", StringComparison.OrdinalIgnoreCase) &&
        !Path.GetFileName(path).Equals("release.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
if (!filePaths.Any(path => Path.GetFileName(path) == "iSay.exe") || !filePaths.Any(path => Path.GetFileName(path) == "iSay.dll")) throw new InvalidDataException("布局缺少 iSay 桌面程序。");
var files = new List<UpdateFile>();
var packageName = $"iSay-{request.Version}-win-x64-application.zip";
var packagePath = Path.Combine(output, packageName);
using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
{
    foreach (var path in filePaths)
    {
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(source)!, path);
        var name = Path.GetFileName(path);
        UpdateSafety.ValidateApplicationPath(name);
        files.Add(new UpdateFile { Path = name, Size = new FileInfo(path).Length, Sha256 = Hash(path) });
        zip.CreateEntryFromFile(path, name, CompressionLevel.SmallestSize);
    }
}
var hash = Hash(packagePath);
var immutableName = $"objects/{hash.ToLowerInvariant()}/{packageName}";
var urls = request.DownloadBaseUrls.Select(address => UpdateSafety.RequireHttps(address.TrimEnd('/') + "/" + immutableName).AbsoluteUri).ToList();
var manifest = new ReleaseManifest
{
    Version = request.Version, Channel = request.Channel, Architecture = "win-x64", Sequence = request.Sequence,
    PublishedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(90),
    ReleaseNotes = new Dictionary<string, string> { ["zh-CN"] = request.NotesZh, ["en-US"] = request.NotesEn },
    Components = [new UpdateComponent { Id = "application", Version = request.Version, PackageUrls = urls,
        Size = new FileInfo(packagePath).Length, Sha256 = hash, MaximumExpandedBytes = Math.Max(1, files.Sum(file => file.Size)), Files = files }]
};
File.WriteAllBytes(Path.Combine(output, "manifest.unsigned.json"), ManifestCodec.CanonicalPayload(manifest));
var keyPath = Environment.GetEnvironmentVariable("ISAY_RELEASE_SIGNING_KEY_PATH");
var signed = false;
if (!string.IsNullOrWhiteSpace(keyPath))
{
    if (urls.Count == 0 || string.IsNullOrWhiteSpace(request.KeyId)) throw new InvalidDataException("签名前必须提供真实下载地址和 keyId。");
    using var key = RSA.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    var envelope = ManifestCodec.Sign(manifest, key, request.KeyId);
    var configuration = new UpdateConfiguration { Channel = request.Channel, Architecture = "win-x64", PublicKeys = new() { [request.KeyId] = key.ExportSubjectPublicKeyInfoPem() } };
    ManifestCodec.Verify(envelope, configuration);
    File.WriteAllText(Path.Combine(output, "release.signed.json"), envelope);
    signed = true;
}
File.WriteAllText(Path.Combine(output, "发行产物.json"), JsonSerializer.Serialize(new { version = request.Version, packageName, objectKey = immutableName,
    sha256 = hash, size = new FileInfo(packagePath).Length, signed, published = false, fileCount = files.Count }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { packagePath, sha256 = hash, size = new FileInfo(packagePath).Length, signed, published = false }));

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static async Task VerifyPublicationAsync(string directory)
{
    var root = UpdateSafety.PrepareDirectory(directory);
    var metadataPath = UpdateSafety.ResolveInside(root, "应用更新包/发行产物.json");
    if (new FileInfo(metadataPath).Length > 65536) throw new InvalidDataException("发行产物元数据超出上限。");
    using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
    var data = metadata.RootElement;
    var configurationPath = Path.Combine(root, "布局", "release.json");
    UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(root)!, configurationPath);
    var configuration = UpdateConfiguration.Load(configurationPath);
    if (!configuration.IsConfigured) throw new InvalidDataException("公开发布前必须配置实际清单地址与客户端验签公钥。");
    var envelopePath = UpdateSafety.ResolveInside(root, "应用更新包/release.signed.json");
    if (new FileInfo(envelopePath).Length > ManifestCodec.MaximumEnvelopeBytes) throw new InvalidDataException("签名清单超出上限。");
    var release = ManifestCodec.Verify(File.ReadAllText(envelopePath), configuration);
    var component = release.Manifest.Components.Single();
    var packageName = data.GetProperty("packageName").GetString() ?? "";
    if (packageName.Contains('/') || packageName.Contains('\\')) throw new InvalidDataException("发布包名必须是单个文件名。");
    var package = UpdateSafety.ResolveInside(root, "应用更新包/" + packageName);
    if (!data.GetProperty("signed").GetBoolean() || release.Manifest.Version != data.GetProperty("version").GetString() ||
        component.Sha256 != data.GetProperty("sha256").GetString() || component.Size != data.GetProperty("size").GetInt64())
        throw new InvalidDataException("签名清单与发行产物描述不一致。");
    await UpdateSafety.VerifyFileAsync(package, component.Size, component.Sha256, CancellationToken.None);
    var objectKey = "objects/" + component.Sha256.ToLowerInvariant() + "/" + packageName;
    if (objectKey != data.GetProperty("objectKey").GetString() ||
        component.PackageUrls.Any(url => !UpdateSafety.RequireHttps(url).AbsolutePath.EndsWith("/" + objectKey, StringComparison.Ordinal)))
        throw new InvalidDataException("签名下载地址与不可变对象路径不一致。");
    File.WriteAllText(UpdateSafety.ResolveInside(root, "应用更新包/发布核对.json"), JsonSerializer.Serialize(new
    {
        version = release.Manifest.Version, channel = release.Manifest.Channel, architecture = release.Manifest.Architecture,
        sequence = release.Manifest.Sequence, envelopeSha256 = Hash(envelopePath), verified = true
    }));
    Console.WriteLine("签名、发布配置、包长度与哈希核对通过。");
}

sealed class ReleaseRequest
{
    public string LayoutDirectory { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public long Sequence { get; set; }
    public string KeyId { get; set; } = "";
    public List<string> DownloadBaseUrls { get; set; } = [];
    public string NotesZh { get; set; } = "";
    public string NotesEn { get; set; } = "";
}
