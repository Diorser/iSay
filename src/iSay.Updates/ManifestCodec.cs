using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace iSay.Updates;

public static class ManifestCodec
{
    public const int MaximumEnvelopeBytes = 3 * 1024 * 1024;

    // 签名对象为递归按序排列属性名、无空白的 UTF8 JSON，数组顺序保持不变。
    public static byte[] CanonicalPayload(ReleaseManifest manifest)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJson.Options));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(document.RootElement, writer);
        return buffer.ToArray();
    }

    public static string Sign(ReleaseManifest manifest, RSA privateKey, string keyId)
    {
        if (privateKey.KeySize < 2048) throw new CryptographicException("更新签名密钥至少需要 2048 位。");
        var payload = CanonicalPayload(manifest);
        return JsonSerializer.Serialize(new SignedReleaseEnvelope
        {
            KeyId = keyId, PayloadBase64 = Convert.ToBase64String(payload),
            SignatureBase64 = Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        }, UpdateJson.Options);
    }

    public static VerifiedRelease Verify(string envelopeJson, UpdateConfiguration configuration, DateTimeOffset? now = null)
    {
        configuration.Validate();
        if (Encoding.UTF8.GetByteCount(envelopeJson) > MaximumEnvelopeBytes) throw new InvalidDataException("更新清单超出上限。");
        RejectDuplicates(envelopeJson);
        var envelope = JsonSerializer.Deserialize<SignedReleaseEnvelope>(envelopeJson, UpdateJson.Options) ?? throw new InvalidDataException("更新清单为空。");
        if (string.IsNullOrEmpty(envelope.KeyId) || string.IsNullOrEmpty(envelope.PayloadBase64) || string.IsNullOrEmpty(envelope.SignatureBase64) || envelope.EnvelopeVersion != 1 || envelope.Algorithm != "RSA-SHA256-PKCS1" ||
            !configuration.PublicKeys.TryGetValue(envelope.KeyId, out var pem)) throw new CryptographicException("更新签名来源不受信任。");
        var payload = Convert.FromBase64String(envelope.PayloadBase64);
        if (payload.Length > 2 * 1024 * 1024) throw new InvalidDataException("更新正文超出上限。");
        using var key = RSA.Create();
        key.ImportFromPem(pem);
        if (key.KeySize < 2048 || !key.VerifyData(payload, Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("更新签名验证失败。");
        RejectDuplicates(Encoding.UTF8.GetString(payload));
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(payload, UpdateJson.Options) ?? throw new InvalidDataException("更新正文为空。");
        if (!payload.AsSpan().SequenceEqual(CanonicalPayload(manifest))) throw new InvalidDataException("更新正文不是规范 JSON。");
        Validate(manifest, configuration, now ?? DateTimeOffset.UtcNow);
        return new VerifiedRelease(manifest, envelopeJson);
    }

    public static IReadOnlyList<ComponentPlan> Plan(VerifiedRelease release, IReadOnlyDictionary<string, string>? installed = null)
        => release.Manifest.Components.Select(component => new ComponentPlan(component.Id, component.Version,
            installed is null || !installed.TryGetValue(component.Id, out var version) || version != component.Version,
            installed is not null && installed.TryGetValue(component.Id, out var current) && current == component.Version ? 0 : component.Size)).ToArray();

    private static void Validate(ReleaseManifest manifest, UpdateConfiguration configuration, DateTimeOffset now)
    {
        if (manifest.SchemaVersion != 1 || manifest.ApplicationId != configuration.ApplicationId || manifest.Channel != configuration.Channel || manifest.Architecture != configuration.Architecture)
            throw new InvalidDataException("更新清单不匹配产品、通道或架构。");
        if (!System.Version.TryParse(manifest.Version, out _) || !System.Version.TryParse(manifest.MinimumUpdaterVersion, out var updater) || updater > new Version(1, 0, 0))
            throw new InvalidDataException("更新版本无效或需要先升级安装器。");
        if (manifest.Sequence < 1 || manifest.Sequence < configuration.MinimumSequence || manifest.PublishedAtUtc > now.AddMinutes(5) || manifest.ExpiresAtUtc <= now ||
            manifest.ExpiresAtUtc <= manifest.PublishedAtUtc || manifest.ExpiresAtUtc - manifest.PublishedAtUtc > TimeSpan.FromDays(180))
            throw new InvalidDataException("更新清单已过期、序号回退或时间范围无效。");
        if (manifest.Components is null || manifest.ReleaseNotes is null || manifest.Components.Count is < 1 or > 16 || manifest.ReleaseNotes.Count > 8 || manifest.ReleaseNotes.Values.Any(note => note is null || note.Length > 65536))
            throw new InvalidDataException("更新组件或说明超出限额。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in manifest.Components)
        {
            if (component is null || component.Files is null || component.PackageUrls is null) throw new InvalidDataException("更新组件存在空字段。");
            if (!ids.Add(component.Id) || component.Id != "application") throw new InvalidDataException("此更新器仅接受应用组件，原生或 TSF 组件请使用完整安装器。");
            if (!System.Version.TryParse(component.Version, out _) || component.Size < 1 || component.Size > configuration.MaximumPackageBytes ||
                component.MaximumExpandedBytes < 1 || component.MaximumExpandedBytes > configuration.MaximumExpandedBytes ||
                component.Files.Count is < 1 || component.Files.Count > configuration.MaximumFiles || component.PackageUrls.Count is < 1 or > 8)
                throw new InvalidDataException("更新组件限额无效。");
            if (UpdateVersions.Parse(component.Version) != UpdateVersions.Parse(manifest.Version)) throw new InvalidDataException("应用组件版本必须与发布版本一致。");
            UpdateSafety.RequireHash(component.Sha256);
            foreach (var url in component.PackageUrls) UpdateSafety.RequireHttps(url);
            long sum = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in component.Files)
            {
                if (file is null) throw new InvalidDataException("更新文件为空。");
                UpdateSafety.ValidateApplicationPath(file.Path);
                if (!paths.Add(file.Path.Replace('\\', '/')) || file.Size < 0) throw new InvalidDataException("更新文件重复或长度无效。");
                UpdateSafety.RequireHash(file.Sha256);
                sum = checked(sum + file.Size);
            }
            if (sum > component.MaximumExpandedBytes) throw new InvalidDataException("更新展开大小超出组件上限。");
            foreach (var path in paths)
            {
                var parent = path;
                while (parent.LastIndexOf('/') is var separator && separator >= 0)
                {
                    parent = parent[..separator];
                    if (paths.Contains(parent)) throw new InvalidDataException("更新文件与目录路径冲突。");
                }
            }
        }
    }

    private static void RejectDuplicates(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        Visit(document.RootElement);
        static void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in node.EnumerateObject())
                {
                    if (!names.Add(item.Name)) throw new InvalidDataException("更新 JSON 存在重复字段。");
                    Visit(item.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Visit(item);
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
}
