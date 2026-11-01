using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace iSay.Updates;

public sealed class UpdateConfiguration
{
    public string ApplicationId { get; set; } = "iSay";
    public string Channel { get; set; } = "stable";
    public string Architecture { get; set; } = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
    public List<string> ManifestUrls { get; set; } = [];
    public Dictionary<string, string> PublicKeys { get; set; } = [];
    public long MinimumSequence { get; set; }
    public long MaximumPackageBytes { get; set; } = 512L * 1024 * 1024;
    public long MaximumExpandedBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaximumFiles { get; set; } = 5000;
    [JsonIgnore]
    public bool IsConfigured => ManifestUrls is { Count: > 0 } && PublicKeys is { Count: > 0 };

    public static UpdateConfiguration Load(string path)
    {
        UpdateSafety.RejectReparseAncestors(Path.GetPathRoot(Path.GetFullPath(path))!, path);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("更新配置超出上限。");
        var result = JsonSerializer.Deserialize<UpdateConfiguration>(File.ReadAllText(path), UpdateJson.Options) ?? throw new InvalidDataException("更新配置为空。");
        result.MinimumSequence = Math.Max(result.MinimumSequence, ApplicationUpdateInstaller.ReadInstalledSequence(Path.GetDirectoryName(Path.GetFullPath(path))!));
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (ManifestUrls is null || PublicKeys is null || PublicKeys.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new InvalidDataException("更新配置存在空字段。");
        if (string.IsNullOrWhiteSpace(ApplicationId) || Channel is not ("stable" or "preview") || Architecture is not ("win-x64" or "win-arm64"))
            throw new InvalidDataException("更新产品、通道或架构配置无效。");
        if (ManifestUrls.Count > 8 || PublicKeys.Count > 8 || MaximumPackageBytes is < 1 or > 4L * 1024 * 1024 * 1024 ||
            MaximumExpandedBytes is < 1 or > 8L * 1024 * 1024 * 1024 || MaximumFiles is < 1 or > 20000 || MinimumSequence < 0)
            throw new InvalidDataException("更新配置限额无效。");
        foreach (var url in ManifestUrls) UpdateSafety.RequireHttps(url);
    }
}

public sealed class ReleaseManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ApplicationId { get; set; } = "iSay";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public string Architecture { get; set; } = "win-x64";
    public long Sequence { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string MinimumUpdaterVersion { get; set; } = "1.0.0";
    public Dictionary<string, string> ReleaseNotes { get; set; } = [];
    public List<UpdateComponent> Components { get; set; } = [];
}

public sealed class UpdateComponent
{
    public string Id { get; set; } = "application";
    public string Version { get; set; } = "";
    public List<string> PackageUrls { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public long MaximumExpandedBytes { get; set; }
    public List<UpdateFile> Files { get; set; } = [];
}

public sealed class UpdateFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class SignedReleaseEnvelope
{
    public int EnvelopeVersion { get; set; } = 1;
    public string KeyId { get; set; } = "";
    public string Algorithm { get; set; } = "RSA-SHA256-PKCS1";
    public string PayloadBase64 { get; set; } = "";
    public string SignatureBase64 { get; set; } = "";
}

public sealed class VerifiedRelease
{
    public ReleaseManifest Manifest { get; }
    public string EnvelopeJson { get; }
    internal VerifiedRelease(ReleaseManifest manifest, string envelope) { Manifest = manifest; EnvelopeJson = envelope; }
}

public enum UpdateCheckStatus { NotConfigured, UpToDate, Available }
public sealed record UpdateCheckResult(UpdateCheckStatus Status, VerifiedRelease? Release = null);
public sealed record UpdateProgress(string ComponentId, long DownloadedBytes, long TotalBytes);
public sealed record PreparedUpdate(string EnvelopePath, IReadOnlyDictionary<string, string> PackagePaths);
public sealed record ComponentPlan(string Id, string Version, bool NeedsDownload, long DownloadBytes);

public sealed class UpdateInstallRequest
{
    public string EnvelopePath { get; set; } = "";
    public Dictionary<string, string> PackagePaths { get; set; } = [];
    public int ParentProcessId { get; set; }
    public long ParentStartedUtcTicks { get; set; }
    public string ReadyMarkerPath { get; set; } = "";
}

public static class UpdateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
}
