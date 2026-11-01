using System.Collections.ObjectModel;
using System.Text.Json;

namespace iSay.Voice;

public static class NativeDictionaryRuntime
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Hashes = new(LoadHashes);

    public static NativeVocabularyDictionaryOptions Production(string appDirectory, string ownDataDirectory) => new()
    {
        RuntimeDirectory = Path.Combine(appDirectory, "dictionary-runtime"),
        SystemDictionaryDirectory = Path.Combine(appDirectory, "dictionary-runtime", "dict"),
        DataDirectory = ownDataDirectory,
        ExecutableName = "iSay.NativeDictionary.exe",
        RequiredHashes = Hashes.Value
    };

    private static IReadOnlyDictionary<string, string> LoadHashes()
    {
        // 冻结清单编入程序集，不能用安装目录中的可变清单替代信任依据。
        using var source = typeof(NativeDictionaryRuntime).Assembly.GetManifestResourceStream("iSay.NativeDictionary.Manifest")
            ?? throw new InvalidDataException("缺少原生词典冻结清单。");
        using var json = JsonDocument.Parse(source);
        var root = json.RootElement;
        if (root.GetProperty("Version").GetInt32() != 1 || root.GetProperty("ExecutableName").GetString() != "iSay.NativeDictionary.exe"
            || root.GetProperty("SystemDictionaryDirectory").GetString() != "dict")
            throw new InvalidDataException("原生词典冻结版本无效。");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.GetProperty("Hashes").EnumerateObject())
        {
            var hash = entry.Value.GetString();
            if (Path.IsPathRooted(entry.Name) || entry.Name.Split('/','\\').Any(part => part is "" or "." or "..")
                || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !hashes.TryAdd(entry.Name, hash))
                throw new InvalidDataException("原生词典冻结文件无效。");
        }
        if (hashes.Count != 30 || !hashes.ContainsKey("iSay.NativeDictionary.exe"))
            throw new InvalidDataException("原生词典冻结文件不完整。");
        return new ReadOnlyDictionary<string, string>(hashes);
    }
}
