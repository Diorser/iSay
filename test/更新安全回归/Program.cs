using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iSay.Updates;

if (args is ["--parent-fixture", var releaseFile])
{
    var until = DateTime.UtcNow.AddSeconds(30);
    while (!File.Exists(releaseFile) && DateTime.UtcNow < until) await Task.Delay(50);
    return 0;
}

var suite = new RegressionSuite();
await suite.RunAsync();
return suite.Failed == 0 ? 0 : 1;

sealed class RegressionSuite
{
    private readonly RSA key = RSA.Create(2048);
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.runtime", Guid.NewGuid().ToString("N")));
    private readonly List<object> results = [];
    public int Failed { get; private set; }
    private int sequence;

    public async Task RunAsync()
    {
        Directory.CreateDirectory(root);
        await Test("未配置时零网络请求", async () =>
        {
            var handler = new FakeHandler((_, _) => throw new Exception("不应请求网络"));
            using var client = new UpdateClient(new(), handler);
            Assert((await client.CheckAsync("1.0.0")).Status == UpdateCheckStatus.NotConfigured && handler.Count == 0);
        });
        await Test("签名与规范正文正确", () =>
        {
            var data = Create();
            Assert(ManifestCodec.Verify(data.Envelope, data.Configuration).Manifest.Version == "1.2.0");
            Assert(ManifestCodec.Plan(ManifestCodec.Verify(data.Envelope, data.Configuration), new Dictionary<string, string> { ["application"] = "1.2.0" }).Single().DownloadBytes == 0);
            return Task.CompletedTask;
        });
        await Test("篡改签名及未知公钥拒绝", async () =>
        {
            var data = Create();
            var envelope = JsonNode.Parse(data.Envelope)!;
            envelope["signatureBase64"] = Convert.ToBase64String(new byte[256]);
            await Reject(() => Task.FromResult(ManifestCodec.Verify(envelope.ToJsonString(), data.Configuration)));
            envelope = JsonNode.Parse(data.Envelope)!;
            envelope["keyId"] = "unknown";
            await Reject(() => Task.FromResult(ManifestCodec.Verify(envelope.ToJsonString(), data.Configuration)));
        });
        await Test("重复字段与非规范 JSON 拒绝", async () =>
        {
            var data = Create();
            await Reject(() => Task.FromResult(ManifestCodec.Verify("{\"keyId\":\"release\"," + data.Envelope[1..], data.Configuration)));
            var canonical = Encoding.UTF8.GetString(ManifestCodec.CanonicalPayload(data.Manifest));
            await Reject(() => Task.FromResult(ManifestCodec.Verify(SignRaw("{\"schemaVersion\":1," + canonical[1..]), data.Configuration)));
            await Reject(() => Task.FromResult(ManifestCodec.Verify(SignRaw(" " + canonical), data.Configuration)));
        });
        await Test("过期架构通道序号及版本限制", async () =>
        {
            foreach (var mutate in new Action<ReleaseManifest>[]
            {
                manifest => manifest.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                manifest => manifest.Architecture = "win-arm64",
                manifest => manifest.Channel = "preview",
                manifest => manifest.Sequence = 0,
                manifest => manifest.MinimumUpdaterVersion = "2.0.0",
                manifest => manifest.Components[0].Version = "1.1.0",
                manifest => manifest.Components = null!
            })
            {
                var data = Create(); mutate(data.Manifest);
                await Reject(() => Task.FromResult(ManifestCodec.Verify(ManifestCodec.Sign(data.Manifest, key, "release"), data.Configuration)));
            }
        });
        await Test("HTTP 与重定向降级拒绝", async () =>
        {
            var bad = Create(); bad.Configuration.ManifestUrls = ["http://invalid.example/release.json"];
            await Reject(() => Task.FromResult(new UpdateClient(bad.Configuration)));
            var data = Create();
            var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://invalid.example/release.json") } }));
            using var client = new UpdateClient(data.Configuration, handler);
            await Reject(() => client.CheckAsync("1.0.0"));
            Assert(handler.Count == 1);
        });
        await Test("清单镜像回退与数字版本比较", async () =>
        {
            var data = Create(); data.Configuration.ManifestUrls.Add("https://second.example.invalid/release.json");
            var attempts = 0;
            var handler = new FakeHandler((_, _) => Task.FromResult(++attempts == 1 ? new(HttpStatusCode.ServiceUnavailable) : Response(Encoding.UTF8.GetBytes(data.Envelope))));
            using var client = new UpdateClient(data.Configuration, handler);
            Assert((await client.CheckAsync("1.0.0")).Status == UpdateCheckStatus.Available && handler.Count == 2);
            Assert((await client.CheckAsync("1.2.0.0")).Status == UpdateCheckStatus.UpToDate);
        });
        await Test("下载镜像回退与可变正文隔离", async () =>
        {
            var data = Create(); data.Manifest.Components[0].PackageUrls.Add("https://second.example.invalid/application.zip"); data.Resign(key);
            var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.Host.StartsWith("packages") ? new(HttpStatusCode.BadGateway) : Response(data.Package)));
            using var client = new UpdateClient(data.Configuration, handler);
            var release = ManifestCodec.Verify(data.Envelope, data.Configuration);
            release.Manifest.Components[0].Sha256 = new string('0', 64);
            var prepared = await client.DownloadAsync(release, NewDirectory());
            Assert(handler.Count == 2 && File.ReadAllBytes(prepared.PackagePaths["application"]).SequenceEqual(data.Package));
        });
        await Test("长度哈希与超量流拒绝", async () =>
        {
            foreach (var mode in new[] { "length", "hash", "overflow" })
            {
                var data = Create();
                var handler = new FakeHandler((_, _) =>
                {
                    var bytes = data.Package.ToArray();
                    if (mode == "hash") bytes[0] ^= 1;
                    if (mode == "overflow") bytes = bytes.Concat(new byte[1]).ToArray();
                    var response = Response(bytes);
                    response.Content.Headers.ContentLength = mode == "overflow" ? null : bytes.Length + (mode == "length" ? 1 : 0);
                    return Task.FromResult(response);
                });
                using var client = new UpdateClient(data.Configuration, handler);
                await Reject(() => client.DownloadAsync(ManifestCodec.Verify(data.Envelope, data.Configuration), NewDirectory()));
            }
        });
        await Test("强 ETag 中断后续传", async () =>
        {
            var data = Create(); var cache = NewDirectory();
            var handler = new FakeHandler((request, _) => Task.FromResult(request.Headers.Range is null ? Interrupted(data.Package) : RangeResponse(data.Package, request)));
            using var client = new UpdateClient(data.Configuration, handler);
            var release = ManifestCodec.Verify(data.Envelope, data.Configuration);
            await Reject(() => client.DownloadAsync(release, cache));
            var prepared = await client.DownloadAsync(release, cache);
            Assert(File.ReadAllBytes(prepared.PackagePaths["application"]).SequenceEqual(data.Package) && handler.Count == 2);
        });
        await Test("取消保留可验证断点并可恢复", async () =>
        {
            var data = Create(new Dictionary<string, byte[]> { ["iSay.dll"] = new byte[200000] }); var cache = NewDirectory();
            var handler = new FakeHandler((request, _) => Task.FromResult(request.Headers.Range is null ? Response(data.Package, "\"entity-v1\"") : RangeResponse(data.Package, request)));
            using var client = new UpdateClient(data.Configuration, handler);
            var release = ManifestCodec.Verify(data.Envelope, data.Configuration);
            using var cancel = new CancellationTokenSource();
            await Reject(() => client.DownloadAsync(release, cache, new ImmediateProgress(_ => cancel.Cancel()), cancel.Token), typeof(OperationCanceledException));
            Assert(Directory.GetFiles(cache, "*.partial").Length == 1);
            var prepared = await client.DownloadAsync(release, cache);
            Assert(File.ReadAllBytes(prepared.PackagePaths["application"]).SequenceEqual(data.Package));
        });
        await Test("续传收到 200 安全重下", async () =>
        {
            var data = Create(); var cache = NewDirectory();
            var attempts = 0;
            var handler = new FakeHandler((_, _) => Task.FromResult(++attempts == 1 ? Interrupted(data.Package) : Response(data.Package, "\"entity-v2\"")));
            using var client = new UpdateClient(data.Configuration, handler);
            var release = ManifestCodec.Verify(data.Envelope, data.Configuration);
            await Reject(() => client.DownloadAsync(release, cache));
            var prepared = await client.DownloadAsync(release, cache);
            Assert(new FileInfo(prepared.PackagePaths["application"]).Length == data.Package.Length);
        });
        await Test("续传实体与错误范围拒绝", async () =>
        {
            foreach (var mode in new[] { "etag", "range" })
            {
                var data = Create(); var cache = NewDirectory();
                var handler = new FakeHandler((request, _) =>
                {
                    if (request.Headers.Range is null) return Task.FromResult(Interrupted(data.Package));
                    var response = RangeResponse(data.Package, request);
                    if (mode == "etag") response.Headers.ETag = new EntityTagHeaderValue("\"changed\"");
                    else response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, data.Package.Length - 1, data.Package.Length);
                    return Task.FromResult(response);
                });
                using var client = new UpdateClient(data.Configuration, handler);
                var release = ManifestCodec.Verify(data.Envelope, data.Configuration);
                await Reject(() => client.DownloadAsync(release, cache));
                await Reject(() => client.DownloadAsync(release, cache));
                Assert(Directory.GetFiles(cache, "*.partial").Length == 0);
            }
        });
        await Test("不同镜像不混用断点", async () =>
        {
            var data = Create(); data.Manifest.Components[0].PackageUrls.Add("https://other.example.invalid/application.zip"); data.Resign(key);
            var handler = new FakeHandler((request, _) =>
            {
                if (request.RequestUri!.Host.StartsWith("packages")) return Task.FromResult(Interrupted(data.Package));
                Assert(request.Headers.Range is null);
                return Task.FromResult(Response(data.Package));
            });
            using var client = new UpdateClient(data.Configuration, handler);
            await client.DownloadAsync(ManifestCodec.Verify(data.Envelope, data.Configuration), NewDirectory());
            Assert(handler.Count == 2);
        });
        await Test("正常压缩包精确展开", async () =>
        {
            var data = Create(new Dictionary<string, byte[]> { ["iSay.dll"] = Bytes("new"), ["assets/help.txt"] = Bytes("help") });
            var destination = NewDirectory();
            await SafeArchive.ExtractAsync(data.Manifest.Components[0], SavePackage(data), destination, data.Configuration);
            Assert(File.ReadAllText(Path.Combine(destination, "assets/help.txt")) == "help");
        });
        await Test("已完整断点无需重复联网", async () =>
        {
            var data = Create(); var cache = NewDirectory();
            var component = data.Manifest.Components[0];
            File.WriteAllBytes(Path.Combine(cache, $"application-{component.Sha256.ToLowerInvariant()}.zip.partial"), data.Package);
            var handler = new FakeHandler((_, _) => throw new Exception("完整且可验证的断点不应联网"));
            using var client = new UpdateClient(data.Configuration, handler);
            var prepared = await client.DownloadAsync(ManifestCodec.Verify(data.Envelope, data.Configuration), cache);
            Assert(handler.Count == 0 && File.ReadAllBytes(prepared.PackagePaths["application"]).SequenceEqual(data.Package));
        });
        await Test("目录穿越额外文件重复路径及链接拒绝", async () =>
        {
            foreach (var mode in new[] { "traversal", "extra", "duplicate", "symlink" })
            {
                var data = Create();
                var entries = new List<(string, byte[], int)> { ("iSay.dll", Bytes("new"), mode == "symlink" ? unchecked((int)0xa1ff0000) : 0) };
                if (mode == "traversal") entries.Add(("../outside.txt", Bytes("bad"), 0));
                if (mode == "extra") entries.Add(("extra.txt", Bytes("bad"), 0));
                if (mode == "duplicate") entries.Add(("ISAY.DLL", Bytes("new"), 0));
                data.ReplacePackage(Zip(entries));
                await Reject(() => SafeArchive.ExtractAsync(data.Manifest.Components[0], SavePackage(data), NewDirectory(), data.Configuration));
            }
        });
        await Test("展开长度哈希及中央目录数量拒绝", async () =>
        {
            foreach (var mode in new[] { "size", "hash", "entries", "false-count" })
            {
                var data = Create();
                if (mode == "size") data.Manifest.Components[0].Files[0].Size--;
                if (mode == "hash") data.Manifest.Components[0].Files[0].Sha256 = new string('0', 64);
                if (mode is "entries" or "false-count")
                {
                    data.Configuration.MaximumFiles = 1;
                    var bytes = Zip(Enumerable.Range(0, 8).Select(index => ($"unknown{index}.txt", Bytes("x"), 0)));
                    if (mode == "false-count")
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 1);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 1);
                    }
                    data.ReplacePackage(bytes);
                }
                await Reject(() => SafeArchive.ExtractAsync(data.Manifest.Components[0], SavePackage(data), NewDirectory(), data.Configuration));
            }
        });
        await Test("原生 TSF 信任配置及特殊路径保护", async () =>
        {
            foreach (var path in new[] { "runtime/file.dll", "composition/file.dll", "release.json", "iSay.Updater.exe", "iSay.Updates.dll", "iSay.TextService.dll", "shared/file.dll", "a/../x", "CON.txt", "COM¹.txt", "CON .txt", "CONIN$.txt", "DICTIO~1/x", "a:stream", "a\\b", "a./b", ".update-state/installed.json" })
                await Reject(() => { UpdateSafety.ValidateApplicationPath(path); return Task.CompletedTask; });
            var data = Create(new Dictionary<string, byte[]> { ["a"] = Bytes("a"), ["a/b"] = Bytes("b") });
            await Reject(() => Task.FromResult(ManifestCodec.Verify(data.Envelope, data.Configuration)));
        });
        await Test("事务成功备份与序号持久化", async () =>
        {
            var data = Create(new Dictionary<string, byte[]> { ["iSay.dll"] = Bytes("new"), ["assets/new.txt"] = Bytes("created") });
            var install = NewDirectory(); File.WriteAllText(Path.Combine(install, "iSay.dll"), "old");
            var result = await ApplicationUpdateInstaller.InstallAsync(data.Configuration, Request(data), install, "1.0.0");
            Assert(File.ReadAllText(Path.Combine(install, "iSay.dll")) == "new" && File.ReadAllText(Path.Combine(result.TransactionDirectory, "backup/iSay.dll")) == "old");
            Assert(ApplicationUpdateInstaller.ReadInstalledSequence(install) == 1);
            File.WriteAllText(Path.Combine(install, "release.json"), JsonSerializer.Serialize(data.Configuration, UpdateJson.Options));
            var loaded = UpdateConfiguration.Load(Path.Combine(install, "release.json"));
            Assert(loaded.MinimumSequence == 1);
            using var client = new UpdateClient(loaded, new FakeHandler((_, _) => Task.FromResult(Response(Encoding.UTF8.GetBytes(data.Envelope)))));
            Assert((await client.CheckAsync("1.2.0.0")).Status == UpdateCheckStatus.UpToDate);
            await Reject(() => ApplicationUpdateInstaller.InstallAsync(data.Configuration, Request(data), install, "1.0.0"));
            data.Manifest.Version = "1.3.0"; data.Manifest.Components[0].Version = "1.3.0"; data.Resign(key);
            await Reject(() => ApplicationUpdateInstaller.InstallAsync(data.Configuration, Request(data), install, "1.2.0"));
        });
        await Test("文件占用时中途失败逆序回滚", async () =>
        {
            var data = Create(new Dictionary<string, byte[]> { ["a.dll"] = Bytes("new-a"), ["b.dll"] = Bytes("new-b") });
            var install = NewDirectory(); File.WriteAllText(Path.Combine(install, "a.dll"), "old-a"); File.WriteAllText(Path.Combine(install, "b.dll"), "old-b");
            using var held = new FileStream(Path.Combine(install, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
            await Reject(() => ApplicationUpdateInstaller.InstallAsync(data.Configuration, Request(data), install, "1.0.0"));
            Assert(File.ReadAllText(Path.Combine(install, "a.dll")) == "old-a" && File.ReadAllText(Path.Combine(install, "b.dll")) == "old-b");
            Assert(ApplicationUpdateInstaller.ReadInstalledSequence(install) == 0);
            var journal = Directory.GetFiles(Path.Combine(install, ".update-state"), "journal.json", SearchOption.AllDirectories).Single();
            Assert(JsonNode.Parse(File.ReadAllText(journal))!["status"]!.GetValue<string>() == "RolledBack");
        });
        await Test("后续更新只保留上一份成功备份", async () =>
        {
            var data = Create(); var install = NewDirectory();
            await ApplicationUpdateInstaller.InstallAsync(data.Configuration, Request(data), install, "1.0.0");
            data.Manifest.Version = "1.3.0"; data.Manifest.Components[0].Version = "1.3.0"; data.Manifest.Sequence = 2; data.Resign(key);
            await ApplicationUpdateInstaller.InstallAsync(data.Configuration, Request(data), install, "1.2.0");
            Assert(Directory.GetDirectories(Path.Combine(install, ".update-state/transactions")).Length == 1 && ApplicationUpdateInstaller.ReadInstalledSequence(install) == 2);
        });
        await Test("崩溃事务恢复与提交边界", async () =>
        {
            foreach (var committed in new[] { false, true })
            {
                var install = NewDirectory(); var transaction = CreateCrashJournal(install, committed);
                await ApplicationUpdateInstaller.RecoverAsync(install);
                Assert(File.ReadAllText(Path.Combine(install, "iSay.dll")) == (committed ? "new" : "old"));
                Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(transaction, "journal.json")))!["status"]!.GetValue<string>() == (committed ? "Completed" : "RolledBack"));
            }
        });
        await Test("恢复冲突保留未知目标与备份", async () =>
        {
            var install = NewDirectory(); var transaction = CreateCrashJournal(install, false);
            File.WriteAllText(Path.Combine(install, "iSay.dll"), "external-change");
            await Reject(() => ApplicationUpdateInstaller.RecoverAsync(install));
            Assert(File.ReadAllText(Path.Combine(install, "iSay.dll")) == "external-change" && File.ReadAllText(Path.Combine(transaction, "backup/iSay.dll")) == "old");
        });
        await Test("重解析目录不能成为安装目标", async () =>
        {
            var parent = NewDirectory(); var outside = NewDirectory(); var junction = Path.Combine(parent, "link");
            var command = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            command.ArgumentList.Add("-NoProfile"); command.ArgumentList.Add("-Command");
            command.ArgumentList.Add($"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' | Out-Null");
            using var child = Process.Start(command)!; await child.WaitForExitAsync();
            Assert(child.ExitCode == 0);
            try { await Reject(() => { UpdateSafety.ResolveInside(junction, "iSay.dll"); return Task.CompletedTask; }); }
            finally { Directory.Delete(junction); }
            Assert(!File.Exists(Path.Combine(outside, "iSay.dll")));
        });
        await Test("回滚中断可重复恢复且校验备份", async () =>
        {
            var install = NewDirectory(); var transaction = CreateCrashJournal(install, true);
            var path = Path.Combine(transaction, "journal.json"); var journal = JsonNode.Parse(File.ReadAllText(path))!;
            journal["status"] = "RollingBack"; File.WriteAllText(path, journal.ToJsonString());
            File.Delete(Path.Combine(install, "iSay.dll")); File.Move(Path.Combine(transaction, "backup/iSay.dll"), Path.Combine(install, "iSay.dll"));
            await ApplicationUpdateInstaller.RecoverAsync(install);
            Assert(File.ReadAllText(Path.Combine(install, "iSay.dll")) == "old" && ApplicationUpdateInstaller.ReadInstalledSequence(install) == 0);
            install = NewDirectory(); transaction = CreateCrashJournal(install, false);
            File.WriteAllText(Path.Combine(transaction, "backup/iSay.dll"), "corrupted");
            await Reject(() => ApplicationUpdateInstaller.RecoverAsync(install));
            Assert(File.ReadAllText(Path.Combine(install, "iSay.dll")) == "new" && File.Exists(Path.Combine(transaction, "backup/iSay.dll")));
        });
        await Test("父进程身份启动时间及正常退出握手", ParentProcessTest);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = results.Count - Failed, failed = Failed, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        key.Dispose();
        // 测试产物保存在本测试目录内，便于审查失败证据，不接触实际安装目录。
    }

    private async Task ParentProcessTest()
    {
        var install = NewDirectory();
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".dll") || name.EndsWith(".json")) File.Copy(file, Path.Combine(install, name));
        }
        var sourceExe = Path.Combine(AppContext.BaseDirectory, "更新安全回归.exe");
        File.Copy(sourceExe, Path.Combine(install, "iSay.exe"));
        var release = Path.Combine(install, "exit.marker");
        var start = new ProcessStartInfo(Path.Combine(install, "iSay.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--parent-fixture"); start.ArgumentList.Add(release);
        using var child = Process.Start(start)!;
        try
        {
            var request = new UpdateInstallRequest { ParentProcessId = child.Id, ParentStartedUtcTicks = child.StartTime.ToUniversalTime().Ticks, EnvelopePath = Path.Combine(install, "manifest.json") };
            request.ReadyMarkerPath = Path.Combine(install, $"ready-{request.ParentProcessId}-{request.ParentStartedUtcTicks}.json");
            request.ParentStartedUtcTicks++;
            await Reject(() => UpdateParentGuard.WaitForExitAsync(request, install, TimeSpan.FromSeconds(5)));
            request.ParentStartedUtcTicks--;
            var wait = UpdateParentGuard.WaitForExitAsync(request, install, TimeSpan.FromSeconds(5));
            for (var attempt = 0; attempt < 50 && !File.Exists(request.ReadyMarkerPath); attempt++) await Task.Delay(20);
            Assert(File.Exists(request.ReadyMarkerPath) && !child.HasExited);
            File.WriteAllText(release, "exit");
            await wait;
            Assert(child.HasExited && child.ExitCode == 0);
        }
        finally { File.WriteAllText(release, "exit"); await child.WaitForExitAsync(); }
    }

    private string CreateCrashJournal(string install, bool committed)
    {
        var transaction = Path.Combine(install, ".update-state/transactions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(transaction, "backup"));
        File.WriteAllText(Path.Combine(install, "iSay.dll"), "new"); File.WriteAllText(Path.Combine(transaction, "backup/iSay.dll"), "old");
        File.WriteAllText(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(new
        {
            status = committed ? "Committing" : "Installing", version = "1.2.0", sequence = 1, previousState = (object?)null,
            files = new[] { new { path = "iSay.dll", sha256 = Hash(Bytes("new")), size = 3, hadOriginal = true, originalSize = 3, originalSha256 = Hash(Bytes("old")) } }
        }));
        if (committed) File.WriteAllText(Path.Combine(install, ".update-state/installed.json"), "{\"version\":\"1.2.0\",\"sequence\":1}");
        return transaction;
    }

    private TestData Create(Dictionary<string, byte[]>? files = null)
    {
        files ??= new() { ["iSay.dll"] = Bytes("new") };
        var package = Zip(files.Select(file => (file.Key, file.Value, 0)));
        var configuration = new UpdateConfiguration { Architecture = "win-x64", ManifestUrls = ["https://release.example.invalid/manifest.json"], PublicKeys = new() { ["release"] = key.ExportSubjectPublicKeyInfoPem() } };
        var manifest = new ReleaseManifest
        {
            Version = "1.2.0", Architecture = "win-x64", Sequence = 1, PublishedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7), ReleaseNotes = new() { ["zh-CN"] = "回归测试", ["en"] = "Regression" },
            Components = [new() { Version = "1.2.0", PackageUrls = ["https://packages.example.invalid/application.zip"], Sha256 = Hash(package), Size = package.Length, MaximumExpandedBytes = Math.Max(1, files.Values.Sum(bytes => bytes.LongLength)), Files = files.Select(file => new UpdateFile { Path = file.Key, Size = file.Value.Length, Sha256 = Hash(file.Value) }).ToList() }]
        };
        return new TestData(configuration, manifest, ManifestCodec.Sign(manifest, key, "release"), package);
    }

    private UpdateInstallRequest Request(TestData data)
    {
        var directory = NewDirectory(); var envelope = Path.Combine(directory, "manifest.json"); File.WriteAllText(envelope, data.Envelope);
        return new() { EnvelopePath = envelope, PackagePaths = new() { ["application"] = SavePackage(data) } };
    }

    private string SignRaw(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return JsonSerializer.Serialize(new SignedReleaseEnvelope { KeyId = "release", PayloadBase64 = Convert.ToBase64String(bytes), SignatureBase64 = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }, UpdateJson.Options);
    }
    private string SavePackage(TestData data) { var path = Path.Combine(NewDirectory(), "package.zip"); File.WriteAllBytes(path, data.Package); return path; }
    private string NewDirectory() { var path = Path.Combine(root, (++sequence).ToString()); Directory.CreateDirectory(path); return path; }
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("断言失败。"); }
    private async Task Test(string name, Func<Task> action)
    {
        try { await action(); results.Add(new { name, passed = true }); }
        catch (Exception error) { Failed++; results.Add(new { name, passed = false, error = error.ToString() }); }
    }
    private static async Task Reject(Func<Task> action, Type? expected = null)
    {
        try { await action(); }
        catch (Exception error) when (expected is null || expected.IsAssignableFrom(error.GetType())) { return; }
        throw new InvalidOperationException("预期拒绝操作，但操作成功。");
    }
    private static byte[] Zip(IEnumerable<(string Path, byte[] Bytes, int Attributes)> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var item in entries)
            { var entry = archive.CreateEntry(item.Path, CompressionLevel.NoCompression); if (item.Attributes != 0) entry.ExternalAttributes = item.Attributes; using var file = entry.Open(); file.Write(item.Bytes); }
        return output.ToArray();
    }
    private static HttpResponseMessage Response(byte[] bytes, string? tag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (tag is not null) response.Headers.ETag = new EntityTagHeaderValue(tag);
        return response;
    }
    private static HttpResponseMessage Interrupted(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptStream(bytes)) };
        response.Headers.ETag = new EntityTagHeaderValue("\"entity-v1\""); response.Content.Headers.ContentLength = bytes.Length;
        return response;
    }
    private static HttpResponseMessage RangeResponse(byte[] bytes, HttpRequestMessage request)
    {
        var offset = request.Headers.Range!.Ranges.Single().From!.Value;
        Assert(request.Headers.IfRange!.EntityTag!.Tag == "\"entity-v1\"");
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[(int)offset..]) };
        response.Headers.ETag = new EntityTagHeaderValue("\"entity-v1\""); response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return response;
    }
}

sealed class TestData(UpdateConfiguration configuration, ReleaseManifest manifest, string envelope, byte[] package)
{
    public UpdateConfiguration Configuration { get; } = configuration;
    public ReleaseManifest Manifest { get; } = manifest;
    public string Envelope { get; private set; } = envelope;
    public byte[] Package { get; private set; } = package;
    public void Resign(RSA key) => Envelope = ManifestCodec.Sign(Manifest, key, "release");
    public void ReplacePackage(byte[] bytes) { Package = bytes; Manifest.Components[0].Size = bytes.Length; Manifest.Components[0].Sha256 = Convert.ToHexString(SHA256.HashData(bytes)); }
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Count++; var response = await action(request, cancellationToken); response.RequestMessage ??= request; return response; }
}
sealed class ImmediateProgress(Action<UpdateProgress> action) : IProgress<UpdateProgress>
{ public void Report(UpdateProgress value) => action(value); }
sealed class InterruptStream(byte[] bytes) : Stream
{
    private bool delivered;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => bytes.Length;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delivered) throw new IOException("模拟连接中断。");
        delivered = true; var count = Math.Min(10, bytes.Length); bytes.AsMemory(0, count).CopyTo(buffer); return ValueTask.FromResult(count);
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
