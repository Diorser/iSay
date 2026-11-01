using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace iSay.Voice;

public sealed record NativeRuntimeDiagnosticOptions(
    string AppDirectory,
    string DataDirectory,
    string? RecognitionDirectory = null,
    string? OrganizerDirectory = null)
{
    // 必须由调用方传入实际引擎选择，不能仅凭目录里出现候选就宣称已接线。
    public string RecognitionExecutableName { get; init; } = "ImeService.exe";
}

public enum NativeRuntimeDiagnosticState { Passed, Warning, Failed, NotVerified }

public sealed record NativeRuntimeDiagnosticItem(
    string Id,
    string Name,
    NativeRuntimeDiagnosticState State,
    string Description,
    string? Sha256 = null,
    string? FileVersion = null);

public sealed record NativeRuntimeCapabilityDiagnostic(
    string Name,
    bool LocalFilesValid,
    int RequiredFileCount,
    int ValidatedFileCount,
    string Description)
{
    // 本检查绝不启动SDK或访问网络，不能据此确认设备初始化和云端鉴权。
    public bool OnlineVerified => false;
}

public sealed record NativeRuntimeDiagnosticReport(
    string Summary,
    IReadOnlyList<NativeRuntimeDiagnosticItem> Items,
    NativeRuntimeCapabilityDiagnostic StandardRecognition,
    NativeRuntimeCapabilityDiagnostic VocabularyRecognition,
    NativeRuntimeCapabilityDiagnostic SmartFormatting)
{
    public string ToDisplayText()
    {
        var text = new StringBuilder(Summary);
        foreach (var capability in new[] { StandardRecognition, VocabularyRecognition, SmartFormatting })
            text.AppendLine().Append(capability.Name).Append('：')
                .Append(capability.LocalFilesValid ? "本地文件通过" : "本地文件未通过")
                .Append($"（{capability.ValidatedFileCount}/{capability.RequiredFileCount}），联网未验证。");
        foreach (var item in Items.Where(item => item.Sha256 is null || item.State != NativeRuntimeDiagnosticState.Passed))
            text.AppendLine().Append(item.Name).Append('：').Append(item.Description);
        return text.ToString();
    }
}

public static class NativeRuntimeDiagnostics
{
    private const string StandardAdapterSha256 = "C1ABE109F9150C3F112239E65CA4A69ADE98838AE6FC297DF71EC59AF79A0952";
    private const string VocabularyAdapterSha256 = "B239956CC60809870EB24F98C301AD189CAC9D62BAC5E6D300F1DFA8EAA3E7CC";
    private const string PersistentAdapterSha256 = Engines.PersistentNativeSpeechEngine.UpdatableAdapterSha256;

    // 这是本项目已验收发行包的依赖清单，不宣称它是SDK动态加载路径的最小集合。
    private static readonly IReadOnlyDictionary<string, string> RecognitionDependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["applogrs.dll"] = "4E3A27EB419DAFD76ED8D82267BE11AAF5BD7EFFD6F224B0348F874FBB5356F3",
        ["audioeffect-mt.dll"] = "16533A79FE707D5A217BBA89586B3A9F6C812C759AA32261D0DA2FE548468EA7",
        ["Dbghelp.dll"] = "B94EDADE15FFDADA1BDFA37FD21222C1C0D9630B1DF1E51952C11F0F1FA3110C",
        ["DoubaoIme.Settings.NativeRuntime.dll"] = "FD01727CF1D84E2AE0DA23CC51464DC24BE9F6024BC0812997E8979A3D6135A7",
        ["DoubaoIme.Settings.UI.dll"] = "4869FFFB484B9A08B7143C9127D0D2803B95D920FCB1F6F9E3145E331A05AE88",
        ["DoubaoIme.Settings.ViewModels.dll"] = "D82F892B47729AF59CD393585C006926F62A7225C5D33CB3287E1954E9D359CF",
        ["ime_net_sdk.dll"] = "55DFB2802B70A54DF6E98FEBEEE3F3260ECEA07401F00D33232D93D703172576",
        ["oime-config.dll"] = "40315A3C2613EAEB44000CB65FF846A876DFE989A97EBFA446CFF2D91305EFD7",
        ["onnxruntime.dll"] = "9D0ACA1EB848958CA7C0B9901CC4A102973013DB4CA1E6326720EDA0B150D399",
        ["parfait_wer.dll"] = "716A29AA7CCA2248BF68F7B88BBC75E4B2654C61D9FFB91CD98BAF4255422263",
        ["parfait.dll"] = "E571F84EE11D5C59630B390DB686D4BC24988F1B8303EC8F2DBD76FF1CF7F2C6",
        ["QRCoder.dll"] = "573FCD686F64020C123BD378B9225C63561E9BD71F5D12342FAD51B734CFCAFD",
        ["rpc.dll"] = "A3EAD1A55850257BAC01A878C899F42291A1F41BD2B834E0CAA5C5B66A674E02",
        ["SharpVectors.Converters.Wpf.dll"] = "13CCF851074E4D51F5D424D831724608696D6F3803BB7E2D1EE3EF8BAD3A50B9",
        ["SharpVectors.Core.dll"] = "6EA2E9D6ACB72EB059E44F2381935BE46957C18B35C83EC9A5103E9005F1F770",
        ["SharpVectors.Css.dll"] = "EFD3FDAF2614A00A5574C87AD41FB13AB993A8E88EFF10158514C11C12A54D04",
        ["SharpVectors.Dom.dll"] = "C27A57ABF7FBFB4C44014DF5B8C23BDC335C7B36E7A76B7DEA61701DE93914DD",
        ["SharpVectors.Model.dll"] = "C8B4D0CCE8ED0C8120B20236181F42E9747161F177BC7632A36825F5D4021F6A",
        ["SharpVectors.Rendering.Gdi.dll"] = "F94403F99887F85BD7D456535DF11EBD9245A4D2592BE0E871130E4421A6EEC8",
        ["SharpVectors.Rendering.Wpf.dll"] = "891F67FC4DC82132FC8F938AA81E6F385422D3D8872240D68A8421CA36B2B91E",
        ["SharpVectors.Runtime.Wpf.dll"] = "A9BC6EA80BF658535137E4D009D87A3AC3B7D17DEE33D6AA7DC194DAF0ED1DF8",
        ["sscronet.dll"] = "CEC57D137BE3DE8597A2AA3F4A0BF55E34D4ADEFEB8F75AB37B59E90D4736108",
        ["tsf-oime-core.dll"] = "77D58BFC5BBC9016EE58135967603FBAB19A30E79A1C338BCE6A2DF62CE60A83",
        ["TTNetDownloaderCrossPlatform.dll"] = "29721A937647715E46DCB95CEF0B23F20A7524418E76961E4CB07ED1CA9C1EF5",
        ["ui.dll"] = "63B74A0AAAB0CB3DC81ED9E4DCCA90CB55A2454184840E78CD05A2102240A8C1"
    };

    public static Task<NativeRuntimeDiagnosticReport> InspectAsync(
        NativeRuntimeDiagnosticOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        // 哈希和PE版本读取全部放到后台，避免设置窗口等待大量文件读取。
        return Task.Run(() => InspectCoreAsync(options, cancellationToken), cancellationToken);
    }

    private static async Task<NativeRuntimeDiagnosticReport> InspectCoreAsync(
        NativeRuntimeDiagnosticOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<NativeRuntimeDiagnosticItem>();
        var appDirectory = ResolveDirectory(options.AppDirectory, "application.path", "程序目录", items);
        var recognitionDirectory = ResolveDirectory(options.RecognitionDirectory ?? ChildPath(appDirectory, "runtime"), "recognition.path", "识别目录", items);
        var organizerDirectory = ResolveDirectory(options.OrganizerDirectory ?? ChildPath(appDirectory, "smart-runtime"), "organizer.path", "整理目录", items);
        var dataDirectory = ResolveDirectory(options.DataDirectory, "data.path", "iSay数据目录", items);
        AddEnvironmentItems(items);
        InspectDataDirectory(dataDirectory, items);
        var cache = new Dictionary<string, NativeRuntimeDiagnosticItem>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var persistent = string.Equals(options.RecognitionExecutableName, "ImeService.Persistent.exe", StringComparison.Ordinal);
        var vocabularyOnly = string.Equals(options.RecognitionExecutableName, "ImeService.Vocabulary.exe", StringComparison.Ordinal);
        var knownSelection = persistent || vocabularyOnly || string.Equals(options.RecognitionExecutableName, "ImeService.exe", StringComparison.Ordinal);
        if (!knownSelection)
        {
            items.Add(new("recognition.selection", "识别适配器选择", NativeRuntimeDiagnosticState.Failed,
                "调用方选择的适配器不在本项目诊断清单内；未猜测文件名或回退其他识别适配器。"));
            recognitionDirectory = null;
        }
        var standardFile = persistent ? "ImeService.Persistent.exe" : vocabularyOnly ? "ImeService.Vocabulary.exe" : "ImeService.exe";
        var standardHash = persistent ? PersistentAdapterSha256 : vocabularyOnly ? VocabularyAdapterSha256 : StandardAdapterSha256;
        var vocabularyFile = persistent ? "ImeService.Persistent.exe" : "ImeService.Vocabulary.exe";
        var standardHashes = new Dictionary<string, string>(RecognitionDependencies) { [standardFile] = standardHash };
        var vocabularyHashes = new Dictionary<string, string>(RecognitionDependencies) { [vocabularyFile] = persistent ? PersistentAdapterSha256 : VocabularyAdapterSha256 };
        var smartHashes = NativeSmartOrganizerOptions.Production(Path.GetTempPath()).RequiredHashes;
        var standard = await InspectCapabilityAsync("标准识别", "recognition.standard", recognitionDirectory, standardHashes,
            "核对项目当前标准适配器及发行依赖；不代表SDK初始化、设备身份或云端识别可用。", items, cache, cancellationToken).ConfigureAwait(false);
        var vocabulary = await InspectCapabilityAsync("热词识别", "recognition.vocabulary", recognitionDirectory, vocabularyHashes,
            "仅证明热词适配器和发行依赖匹配；未测试热词参数消费、词条收益或官方个人词库兼容。", items, cache, cancellationToken).ConfigureAwait(false);
        var smart = await InspectCapabilityAsync("智能整理", "organizer", organizerDirectory, smartHashes,
            "仅核对独立整理运行库的9个固定文件；不代表整理服务连通或设备初始化成功。", items, cache, cancellationToken).ConfigureAwait(false);
        if (!knownSelection)
            items.Add(new("recognition.cache", "识别隔离边界", NativeRuntimeDiagnosticState.NotVerified,
                "适配器选择未识别，本检查不推测该程序的数据隔离或联网行为。"));
        else if (persistent || vocabularyOnly)
            items.Add(new("recognition.cache", "识别隔离边界",
                standard.LocalFilesValid && vocabulary.LocalFilesValid ? NativeRuntimeDiagnosticState.NotVerified : NativeRuntimeDiagnosticState.Warning,
                standard.LocalFilesValid && vocabulary.LocalFilesValid
                    ? $"当前按调用方显式选择的{(persistent ? "持久" : "热词")}适配器校验；固定副本使用{(persistent ? "iSayAsr01" : "iSayHotWd")}独立技术资料后缀。未启动SDK，未确认实际运行进程或本机初始化结果。"
                    : "所选独立适配器及发行依赖尚未校验通过，不能确认其隔离边界；未启动SDK，也未回退到旧适配器。"));
        else
            items.Add(new("recognition.cache", "标准识别隔离边界", NativeRuntimeDiagnosticState.Warning,
                "当前标准适配器仍含原版技术缓存目录引用，未验证全新电脑可独立初始化；热词与整理使用各自隔离数据槽。"));
        items.Add(new("service.boundary", "云端与官方兼容", NativeRuntimeDiagnosticState.NotVerified,
            "未联网，未检查或读取设备身份、登录凭据、个人词正文；官方个人词上传合同及服务授权条件未验证。"));
        items.Add(new("proxy.boundary", "原生网络代理", NativeRuntimeDiagnosticState.NotVerified,
            "原生网络由SDK管理，应用的HTTP代理设置不能据此视为已控制原生识别或整理的流量。"));
        var filesValid = standard.LocalFilesValid && vocabulary.LocalFilesValid && smart.LocalFilesValid;
        var summary = filesValid ? "本地发行文件校验通过；联网能力尚未验证。" : "部分本地发行文件未通过，请查看具体缺项；联网能力尚未验证。";
        return new(summary, items.AsReadOnly(), standard, vocabulary, smart);
    }

    private static string? ChildPath(string? parent, string child) => parent is null ? null : Path.Combine(parent, child);

    private static string? ResolveDirectory(string? path, string id, string name, List<NativeRuntimeDiagnosticItem> items)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException();
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            items.Add(new(id, name, NativeRuntimeDiagnosticState.Failed, "目录路径无效，请在设置中重新选择。"));
            return null;
        }
    }

    private static void AddEnvironmentItems(List<NativeRuntimeDiagnosticItem> items)
    {
        var supported = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        items.Add(new("environment.platform", "运行平台", supported ? NativeRuntimeDiagnosticState.Passed : NativeRuntimeDiagnosticState.Failed,
            $"当前系统：{(OperatingSystem.IsWindows() ? "Windows" : "非Windows")}，进程架构：{RuntimeInformation.ProcessArchitecture}；当前原生发行包要求Windows x64。"));
        items.Add(new("environment.dotnet", ".NET宿主", Environment.Version.Major >= 10 ? NativeRuntimeDiagnosticState.Passed : NativeRuntimeDiagnosticState.Failed,
            $"当前进程运行于{RuntimeInformation.FrameworkDescription}；这只能证明本进程已经启动。"));
        items.Add(new("environment.desktop", "新电脑运行时", NativeRuntimeDiagnosticState.NotVerified,
            "当前发行不是自包含部署，全新电脑仍需.NET 10 Windows Desktop Runtime x64；本检查不执行安装或探测所有系统运行时。"));
        items.Add(new("environment.visual-cpp", "原生外部运行时", NativeRuntimeDiagnosticState.NotVerified,
            "识别发行包还静态依赖MSVCP140、VCRUNTIME140及VCRUNTIME140_1的x64运行时；它们不在本地包清单内，本检查未独立验证系统VC++运行时。"));
    }

    private static void InspectDataDirectory(string? directory, List<NativeRuntimeDiagnosticItem> items)
    {
        if (directory is null) return;
        try
        {
            // 只取根目录属性，不枚举文件、不创建探针，也不读取配置和个人词表。
            var attributes = File.GetAttributes(directory);
            if (!attributes.HasFlag(FileAttributes.Directory))
                items.Add(new("data.metadata", "iSay数据目录", NativeRuntimeDiagnosticState.Failed, "所选位置是文件，不能作为数据目录。"));
            else
                items.Add(new("data.metadata", "iSay数据目录", NativeRuntimeDiagnosticState.NotVerified,
                    "数据目录存在；仅检查目录属性，未读取资料内容，未验证写权限。"));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            items.Add(new("data.metadata", "iSay数据目录", NativeRuntimeDiagnosticState.NotVerified,
                "数据目录尚未创建；本检查没有创建目录，也没有验证写权限。"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            items.Add(new("data.metadata", "iSay数据目录", NativeRuntimeDiagnosticState.Failed,
                "无法读取数据目录属性，可能是访问权限或磁盘问题；未尝试修改权限。"));
        }
    }

    private static async Task<NativeRuntimeCapabilityDiagnostic> InspectCapabilityAsync(
        string name, string id, string? directory, IReadOnlyDictionary<string, string> hashes, string description,
        List<NativeRuntimeDiagnosticItem> items, Dictionary<string, NativeRuntimeDiagnosticItem> cache, CancellationToken cancellationToken)
    {
        var valid = 0;
        if (directory is not null)
        {
            foreach (var (fileName, expected) in hashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, fileName);
                // 用户可能把两类运行目录指向同处，不能复用另一版本的哈希判定。
                var cacheKey = path + '\0' + expected;
                if (!cache.TryGetValue(cacheKey, out var result))
                {
                    result = await InspectFileAsync(path, id + "." + fileName, fileName, expected, cancellationToken).ConfigureAwait(false);
                    cache[cacheKey] = result;
                    items.Add(result);
                }
                if (result.State == NativeRuntimeDiagnosticState.Passed && result.Sha256 == expected) valid++;
            }
        }
        return new(name, valid == hashes.Count, hashes.Count, valid, description);
    }

    private static async Task<NativeRuntimeDiagnosticItem> InspectFileAsync(
        string path, string id, string fileName, string expected, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.Ordinal))
                return new(id, fileName, NativeRuntimeDiagnosticState.Failed, "SHA256与本项目已验收发行包不同，不能据此确认兼容；请恢复匹配的运行库。", actual);
            string? version = null;
            try { version = FileVersionInfo.GetVersionInfo(path).FileVersion; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or System.ComponentModel.Win32Exception or System.Security.SecurityException) { }
            return new(id, fileName, NativeRuntimeDiagnosticState.Passed, "文件SHA256匹配；版本仅作元数据参考，不代表云端服务可用。", actual, version);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(id, fileName, NativeRuntimeDiagnosticState.Failed, "缺少发行文件，请恢复对应的独立运行库；本检查不会启动或下载替代程序。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(id, fileName, NativeRuntimeDiagnosticState.Failed, "文件无法读取，可能被占用或没有权限；未尝试结束进程或修改文件。");
        }
    }
}
