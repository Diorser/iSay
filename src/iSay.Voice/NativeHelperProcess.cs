using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace iSay.Voice;

// 已冻结的独立辅助程序不操作桌面，普通权限不需要原输入法的UIAccess声明。
// 只在Windows明确返回740时使用降权限副本，原文件和既有身份校验保持不变。
internal static class NativeHelperProcess
{
    private static readonly HashSet<string> SupportedImages = new(StringComparer.Ordinal)
    {
        "C1ABE109F9150C3F112239E65CA4A69ADE98838AE6FC297DF71EC59AF79A0952",
        "B239956CC60809870EB24F98C301AD189CAC9D62BAC5E6D300F1DFA8EAA3E7CC",
        "8424B0A9626F89F0395C5D941E1F13689005C612B5970ADA1D63F6E78A248E31",
        "BF373B2D2F522E232533708A49658561D7511C1336AD4A48C5FAF730DD140747",
        "393A4977FA0FD7B605A50044798AE44DB6208DBF8B5E55978908F26B5BC7F47A",
        "03F80C9B4220741BCF2D2B10273D89DCBB438E15E9C043C792C1E8337E027192"
    };
    private static readonly object CacheLock = new();

    public static bool Start(Process process)
    {
        try { return process.Start(); }
        catch (Win32Exception error) when (error.NativeErrorCode == 740 && !process.StartInfo.UseShellExecute)
        {
            var original = process.StartInfo.FileName;
            process.StartInfo.FileName = PrepareUserModeCopy(original);
            try { return process.Start(); }
            catch { process.StartInfo.FileName = original; throw; }
        }
    }

    internal static byte[] CreateUserModeImage(byte[] original)
    {
        if (!SupportedImages.Contains(Convert.ToHexString(SHA256.HashData(original))))
            throw new IOException("普通权限副本只允许已冻结的独立辅助程序。");
        var declaration = Encoding.UTF8.GetBytes("uiAccess=\"true\"");
        var position = original.AsSpan().IndexOf(declaration);
        if (position < 0 || original.AsSpan(position + declaration.Length).IndexOf(declaration) >= 0)
            throw new IOException("辅助程序权限声明与冻结版本不符。");
        var copy = (byte[])original.Clone();
        // 删除属性等同于默认uiAccess=false，等长空白保持全部节地址和其他字节不变。
        copy.AsSpan(position, declaration.Length).Fill((byte)' ');
        return copy;
    }

    private static string PrepareUserModeCopy(string executable)
    {
        var source = Path.GetFullPath(executable);
        var original = File.ReadAllBytes(source);
        var derived = CreateUserModeImage(original);
        var hash = Convert.ToHexString(SHA256.HashData(derived));
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iSay", "native-user-mode", hash);
        lock (CacheLock)
        {
            RejectReparsePoints(directory);
            Directory.CreateDirectory(directory);
            RejectReparsePoints(directory);
            // DLL必须与辅助程序同目录，不能依赖工作目录或改变全局DLL搜索路径。
            foreach (var dependency in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.dll"))
            {
                var target = Path.Combine(directory, Path.GetFileName(dependency));
                using var input = File.OpenRead(dependency);
                var expected = SHA256.HashData(input);
                if (Matches(target, expected)) continue;
                input.Position = 0;
                Publish(target, stream => input.CopyTo(stream));
                if (!Matches(target, expected)) throw new IOException("普通权限辅助程序依赖校验失败。");
            }
            var result = Path.Combine(directory, Path.GetFileName(source));
            var expectedImage = SHA256.HashData(derived);
            if (!Matches(result, expectedImage)) Publish(result, stream => stream.Write(derived));
            if (!Matches(result, expectedImage)) throw new IOException("普通权限辅助程序校验失败。");
            return result;
        }
    }

    private static bool Matches(string path, byte[] expected)
    {
        if (!File.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("普通权限辅助程序缓存不可使用重解析文件。");
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream).AsSpan().SequenceEqual(expected);
    }

    private static void Publish(string path, Action<Stream> write)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("普通权限辅助程序缓存不可使用重解析文件。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(stream);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectReparsePoints(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("普通权限辅助程序缓存不可位于重解析目录。");
    }
}
