using System.Security.Cryptography;

namespace iSay.Updates;

public static class UpdateSafety
{
    private static readonly string[] ProtectedDirectories = ["runtime", "smart-runtime", "dictionary-runtime", "personalization-runtime", "composition", "prerequisites", "dotnet", "shared", "host", "packs", ".update-state"];

    public static Uri RequireHttps(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("更新仅允许无凭据的 HTTPS 地址。");
        return uri;
    }

    public static void RequireHash(string hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("更新 SHA256 无效。");
    }

    public static void ValidateApplicationPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 220 || relative.Contains('\\') || relative.Any(char.IsControl) ||
            relative.Contains(':') || relative.StartsWith('/') || Path.IsPathRooted(relative)) throw new InvalidDataException("更新路径无效。");
        var pieces = relative.Split('/');
        if (pieces.Any(piece => piece.Length == 0 || piece is "." or ".." || piece.EndsWith('.') || piece.EndsWith(' ') || piece.IndexOfAny(['*', '?', '<', '>', '|', '"', '~']) >= 0 ||
                IsDeviceName(piece)) || pieces.Any(piece => piece.StartsWith(".update-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("更新路径包含保留或越界名称。");
        if (ProtectedDirectories.Contains(pieces[0], StringComparer.OrdinalIgnoreCase) ||
            pieces[^1].Equals("release.json", StringComparison.OrdinalIgnoreCase) || pieces[^1] is "设置.json" or "历史.json" ||
            pieces[^1].StartsWith("iSay.Updater", StringComparison.OrdinalIgnoreCase) || pieces[^1].Equals("iSay.Updates.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("应用更新不能覆盖引擎、TSF、信任配置、更新器或个人资料。");
        if (pieces[^1].StartsWith("iSay.TextService", StringComparison.OrdinalIgnoreCase) || pieces[^1].StartsWith("ImeService", StringComparison.OrdinalIgnoreCase) ||
            pieces[^1].StartsWith("iSay.Native", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("原生组件需要完整安装器维护。");
    }

    private static bool IsDeviceName(string piece)
    {
        var stem = piece.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    public static string ResolveInside(string root, string relative)
    {
        ValidateApplicationPath(relative);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RejectReparseAncestors(Path.GetPathRoot(fullRoot)!, fullRoot);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径超出安装目录。");
        RejectReparseAncestors(fullRoot, full);
        return full;
    }

    public static void RejectReparseAncestors(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(path);
        while (current.Length >= fullRoot.Length)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新不允许经过重解析目录或链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
            current = Path.GetDirectoryName(current) ?? "";
        }
        throw new InvalidDataException("更新目标不在指定根目录内。");
    }

    public static string PrepareDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        RejectReparseAncestors(Path.GetPathRoot(full)!, full);
        Directory.CreateDirectory(full);
        return full;
    }

    internal static async Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        RejectReparseAncestors(Path.GetPathRoot(Path.GetFullPath(path))!, path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                await file.WriteAsync(bytes, cancellationToken);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task DetachFileAsync(string path, CancellationToken cancellationToken)
    {
        RejectReparseAncestors(Path.GetPathRoot(Path.GetFullPath(path))!, path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await VerifyStreamAsync(file, size, hash, cancellationToken);
    }

    internal static async Task VerifyStreamAsync(Stream file, long size, string hash, CancellationToken cancellationToken)
    {
        if (file.Length != size) throw new InvalidDataException("更新文件长度不符。");
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件 SHA256 不符。");
    }
}
